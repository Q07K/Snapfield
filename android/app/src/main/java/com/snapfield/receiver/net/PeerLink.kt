package com.snapfield.receiver.net

import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.net.ServerSocket
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.spec.ECGenParameterSpec
import java.security.spec.X509EncodedKeySpec
import java.util.concurrent.atomic.AtomicBoolean
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * Receiver-side port of the desktop `PeerLink`: accepts one controller, runs the
 * ECDH(nistP256) → HKDF-SHA256 → per-direction AES-256-GCM handshake
 * authenticated by an HMAC over the pairing code, then reads encrypted frames.
 *
 * Wire format (identical to the desktop):
 *   plaintext handshake frame: [len:4 LE][payload]
 *     hello payload:           [pubLen:2 BE][SPKI DER pubkey][nonce:16]
 *     auth payload:            HMAC-SHA256(kAuth, UTF8(pin))
 *   encrypted frame:           [len:4 LE = 8+16+ct][ctr:8 BE][tag:16][ciphertext]
 *     nonce = 4 zero bytes + ctr as 8 BE bytes
 */
class PeerLink(
    private val port: Int,
    private val pinProvider: () -> String,
    private val onConnected: () -> Unit,
    private val onMessage: (NetMessage) -> Unit,
    private val onDisconnected: (String) -> Unit,
) {
    private val closed = AtomicBoolean(false)
    private val downFired = AtomicBoolean(false)
    @Volatile private var server: ServerSocket? = null
    @Volatile private var socket: Socket? = null

    private var sendKey: SecretKeySpec? = null
    private var recvKey: SecretKeySpec? = null
    private var sendCtr = 0L
    private var recvCtr = 0L
    private val sendLock = Any()

    class AuthenticationFailure : Exception()

    fun listen() {
        Thread({
            try {
                val server = ServerSocket(port)
                this.server = server
                if (closed.get()) { server.close(); return@Thread }
                val s = server.accept()
                s.tcpNoDelay = true
                s.keepAlive = true
                socket = s
                run(s)
            } catch (e: AuthenticationFailure) {
                fireDisconnected("AUTH: 연결 코드가 일치하지 않습니다.")
            } catch (e: Exception) {
                fireDisconnected(e.message ?: "socket error")
            }
        }, "Snapfield.Listen").start()
    }

    private fun run(s: Socket) {
        val input = DataInputStream(s.getInputStream().buffered())
        val output = DataOutputStream(s.getOutputStream())
        handshake(input, output)
        onConnected()
        readLoop(input)
    }

    // ── handshake (responder role) ────────────────────────────────────────────
    private fun handshake(input: DataInputStream, output: DataOutputStream) {
        val gen = KeyPairGenerator.getInstance("EC")
        gen.initialize(ECGenParameterSpec("secp256r1"))
        val pair = gen.generateKeyPair()
        val myPub = pair.public.encoded // X.509 SubjectPublicKeyInfo — same as C# export
        val myNonce = ByteArray(16).also { SecureRandom().nextBytes(it) }

        writePlain(output, len16(myPub) + myPub + myNonce)
        val hello = readPlain(input)
        val pubLen = ((hello[0].toInt() and 0xFF) shl 8) or (hello[1].toInt() and 0xFF)
        val peerPub = hello.copyOfRange(2, 2 + pubLen)
        val peerNonce = hello.copyOfRange(2 + pubLen, 2 + pubLen + 16)

        val kf = KeyFactory.getInstance("EC")
        val peerKey = kf.generatePublic(X509EncodedKeySpec(peerPub))
        val agree = KeyAgreement.getInstance("ECDH")
        agree.init(pair.private)
        agree.doPhase(peerKey, true)
        val secret = agree.generateSecret() // raw X coordinate — matches DeriveRawSecretAgreement

        // We are the responder: the controller (initiator) ordered first.
        val salt = MessageDigest.getInstance("SHA-256").digest(peerPub + myPub + peerNonce + myNonce)
        val km = hkdfSha256(secret, salt, "snapfield-v1".toByteArray(Charsets.US_ASCII), 96)
        val kI2R = km.copyOfRange(0, 32)
        val kR2I = km.copyOfRange(32, 64)
        val kAuth = km.copyOfRange(64, 96)

        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(kAuth, "HmacSHA256"))
        val myTag = mac.doFinal(pinProvider().toByteArray(Charsets.UTF_8))
        writePlain(output, myTag)
        val peerTag = readPlain(input)
        if (!MessageDigest.isEqual(peerTag, myTag)) throw AuthenticationFailure()

        sendKey = SecretKeySpec(kR2I, "AES") // responder→initiator
        recvKey = SecretKeySpec(kI2R, "AES")
    }

    // ── encrypted messaging ───────────────────────────────────────────────────
    fun send(message: NetMessage) {
        val key = sendKey ?: return
        if (closed.get()) return
        try {
            synchronized(sendLock) {
                val plain = message.toJsonBytes()
                val ctr = sendCtr++
                val cipher = Cipher.getInstance("AES/GCM/NoPadding")
                cipher.init(Cipher.ENCRYPT_MODE, key, GCMParameterSpec(128, nonceFor(ctr)))
                val out = cipher.doFinal(plain) // javax appends the tag: [ct][tag16]
                val ct = out.copyOfRange(0, out.size - 16)
                val tag = out.copyOfRange(out.size - 16, out.size)

                val frame = ByteBuffer.allocate(4 + 8 + 16 + ct.size).order(ByteOrder.LITTLE_ENDIAN)
                frame.putInt(8 + 16 + ct.size)
                frame.order(ByteOrder.BIG_ENDIAN).putLong(ctr)
                frame.put(tag).put(ct)
                val stream = socket?.getOutputStream() ?: return
                stream.write(frame.array())
                stream.flush()
            }
        } catch (e: Exception) {
            fireDisconnected(e.message ?: "send failed")
        }
    }

    private fun readLoop(input: DataInputStream) {
        val key = recvKey!!
        try {
            while (!closed.get()) {
                val len = readIntLe(input)
                if (len < 24 || len > 96 * 1024 * 1024) break
                val buf = ByteArray(len)
                input.readFully(buf)

                val ctr = ByteBuffer.wrap(buf, 0, 8).order(ByteOrder.BIG_ENDIAN).long
                if (ctr < recvCtr) break // replay / reorder — drop the link
                recvCtr = ctr + 1

                val cipher = Cipher.getInstance("AES/GCM/NoPadding")
                cipher.init(Cipher.DECRYPT_MODE, key, GCMParameterSpec(128, nonceFor(ctr)))
                // javax wants [ciphertext][tag16] as one logical input; GCM decrypt
                // may buffer in update(), so keep both outputs.
                val part = cipher.update(buf, 24, len - 24) ?: ByteArray(0)
                val rest = cipher.doFinal(buf, 8, 16)
                val plain = if (part.isEmpty()) rest else part + rest

                NetMessage.fromBody(plain, plain.size)?.let(onMessage)
            }
        } catch (e: Exception) {
            fireDisconnected(e.message ?: "read failed")
            return
        }
        fireDisconnected("peer closed the connection")
    }

    private fun fireDisconnected(reason: String) {
        if (closed.get()) return
        if (downFired.compareAndSet(false, true)) onDisconnected(reason)
    }

    // ── plumbing ──────────────────────────────────────────────────────────────
    private fun nonceFor(ctr: Long): ByteArray =
        ByteBuffer.allocate(12).order(ByteOrder.BIG_ENDIAN).putInt(0).putLong(ctr).array()

    private fun writePlain(output: DataOutputStream, payload: ByteArray) {
        val head = ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(payload.size).array()
        output.write(head)
        output.write(payload)
        output.flush()
    }

    private fun readPlain(input: DataInputStream): ByteArray {
        val len = readIntLe(input)
        if (len < 0 || len > 8192) throw IOException("bad handshake frame")
        val buf = ByteArray(len)
        input.readFully(buf)
        return buf
    }

    private fun readIntLe(input: DataInputStream): Int {
        val b = ByteArray(4)
        input.readFully(b)
        return ByteBuffer.wrap(b).order(ByteOrder.LITTLE_ENDIAN).int
    }

    private fun len16(b: ByteArray) =
        byteArrayOf(((b.size shr 8) and 0xFF).toByte(), (b.size and 0xFF).toByte())

    /** RFC 5869 HKDF with SHA-256 — matches .NET's HKDF.DeriveKey. */
    private fun hkdfSha256(ikm: ByteArray, salt: ByteArray, info: ByteArray, length: Int): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(salt, "HmacSHA256"))
        val prk = mac.doFinal(ikm)

        val out = ByteArray(length)
        var previous = ByteArray(0)
        var generated = 0
        var counter = 1
        while (generated < length) {
            mac.init(SecretKeySpec(prk, "HmacSHA256"))
            mac.update(previous)
            mac.update(info)
            mac.update(counter.toByte())
            previous = mac.doFinal()
            val n = minOf(previous.size, length - generated)
            System.arraycopy(previous, 0, out, generated, n)
            generated += n
            counter++
        }
        return out
    }

    fun close() {
        closed.set(true)
        try { socket?.close() } catch (_: Exception) {}
        try { server?.close() } catch (_: Exception) {}
    }
}
