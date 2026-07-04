using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Snapfield.Core.Net;

namespace Snapfield.Platform.Net;

/// <summary>
/// A secure TCP connection between two Snapfield peers.
///
/// On connect the peers run an ECDH (nistP256) key exchange and derive per-
/// direction AES-256-GCM keys, then authenticate the exchange with an HMAC over
/// the pairing code (PIN). A wrong PIN — or a man-in-the-middle who doesn't know
/// it — fails the handshake, so the code both pairs AND protects the channel.
/// Every message after that is encrypted and integrity-checked; a monotonic
/// counter per direction prevents replay.
///
/// Sends are queued and written by a dedicated thread so callers (the low-level
/// input-hook threads among them) never block on the network. <see cref="Disconnected"/>
/// fires at most once per link, whichever side (reader, writer, connector) failed first.
/// </summary>
public sealed class PeerLink : IDisposable
{
    // ~8s of cursor moves at 125 Hz. A full queue means the peer stopped
    // draining (hung app / dead network) — drop the link rather than block.
    private const int SendQueueCapacity = 1024;

    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private BlockingCollection<NetMessage>? _sendQueue;
    private volatile bool _closed;
    private int _downFired;

    private AesGcm? _send, _recv;
    private ulong _sendCtr, _recvCtr; // _sendCtr: writer thread only; _recvCtr: reader thread only

    public event Action? Connected;
    public event Action<NetMessage>? MessageReceived;
    public event Action<string>? Disconnected;

    public bool IsConnected => _send is not null && !_closed;

    public void Listen(int port, string pin)
    {
        // Bind on the worker thread: a port squatted by another process must
        // surface as Disconnected (and be retried), not throw at the caller.
        StartThread("Snapfield.Listen", () =>
        {
            if (_closed) return;
            var listener = new TcpListener(IPAddress.Any, port);
            _listener = listener;
            listener.Start();
            if (_closed) { listener.Stop(); return; } // disposed while binding
            var c = listener.AcceptTcpClient();
            Attach(c, initiator: false, pin);
        });
    }

    public void Connect(string host, int port, string pin)
    {
        StartThread("Snapfield.Connect", () =>
        {
            var c = new TcpClient();
            c.Connect(host, port);
            Attach(c, initiator: true, pin);
        });
    }

    private void StartThread(string name, Action body)
    {
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { FireDisconnected(ex.Message); }
        }) { IsBackground = true, Name = name };
        t.Start();
    }

    private void Attach(TcpClient client, bool initiator, string pin)
    {
        if (_closed) { try { client.Close(); } catch { } return; }
        _client = client;
        _client.NoDelay = true;
        EnableTcpKeepAlive(_client.Client);
        _stream = _client.GetStream();

        var reader = new Thread(() => Run(initiator, pin)) { IsBackground = true, Name = "Snapfield.Reader" };
        reader.Start();
    }

    private void Run(bool initiator, string pin)
    {
        try
        {
            Handshake(initiator, pin);
        }
        catch (AuthenticationFailure)
        {
            FireDisconnected("AUTH: 연결 코드가 일치하지 않습니다.");
            return;
        }
        catch (Exception ex)
        {
            FireDisconnected(ex.Message);
            return;
        }

        var queue = new BlockingCollection<NetMessage>(SendQueueCapacity);
        _sendQueue = queue;
        StartThread("Snapfield.Writer", () => WriteLoop(queue));
        Connected?.Invoke();
        ReadLoop();
    }

    // ── handshake ─────────────────────────────────────────────────────────────
    private sealed class AuthenticationFailure : Exception { }

    private void Handshake(bool initiator, string pin)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var myPub = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        var myNonce = RandomNumberGenerator.GetBytes(16);

        // Exchange {pubkey, nonce}.
        WritePlain(Concat(Len16(myPub), myPub, myNonce));
        var hello = ReadPlain();
        var pubLen = BinaryPrimitives.ReadUInt16BigEndian(hello);
        var peerPub = hello[2..(2 + pubLen)];
        var peerNonce = hello[(2 + pubLen)..(2 + pubLen + 16)];

        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerPub, out _);
        var secret = ecdh.DeriveRawSecretAgreement(peer.PublicKey);

        // Order inputs by role so both sides derive identical keys.
        var (pubI, pubR, nonceI, nonceR) = initiator
            ? (myPub, peerPub, myNonce, peerNonce)
            : (peerPub, myPub, peerNonce, myNonce);
        var salt = SHA256.HashData(Concat(pubI, pubR, nonceI, nonceR));
        var km = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, outputLength: 96, salt: salt, info: Encoding.ASCII.GetBytes("snapfield-v1"));
        var kI2R = km[..32]; var kR2I = km[32..64]; var kAuth = km[64..96];

        // Authenticate the exchange with the PIN.
        var myTag = HMACSHA256.HashData(kAuth, Encoding.UTF8.GetBytes(pin ?? ""));
        WritePlain(myTag);
        var peerTag = ReadPlain();
        if (peerTag.Length != myTag.Length || !CryptographicOperations.FixedTimeEquals(peerTag, myTag))
            throw new AuthenticationFailure();

        _send = new AesGcm(initiator ? kI2R : kR2I, 16);
        _recv = new AesGcm(initiator ? kR2I : kI2R, 16);
    }

    // ── encrypted messaging ─────────────────────────────────────────────────
    public void Send(NetMessage message)
    {
        var queue = _sendQueue;
        if (queue is null || _closed) return;
        try
        {
            if (!queue.TryAdd(message))
                FireDisconnected("송신 적체 — 상대 기기가 응답하지 않습니다.");
        }
        catch (InvalidOperationException) { /* queue completed during shutdown */ }
    }

    private void WriteLoop(BlockingCollection<NetMessage> queue)
    {
        var stream = _stream!;
        var gcm = _send!;
        try
        {
            foreach (var message in queue.GetConsumingEnumerable())
            {
                var plain = message.ToJson();
                var ct = new byte[plain.Length];
                var tag = new byte[16];
                var ctr = _sendCtr++;
                gcm.Encrypt(NonceFor(ctr), plain, ct, tag);
                var frame = new byte[4 + 8 + 16 + ct.Length];
                BinaryPrimitives.WriteInt32LittleEndian(frame, 8 + 16 + ct.Length);
                BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(4), ctr);
                tag.CopyTo(frame, 12);
                ct.CopyTo(frame, 28);
                stream.Write(frame, 0, frame.Length);
            }
        }
        catch (Exception ex) { FireDisconnected(ex.Message); }
    }

    private void ReadLoop()
    {
        var stream = _stream!;
        var lenBuf = new byte[4];
        try
        {
            while (!_closed)
            {
                if (!ReadExact(stream, lenBuf, 4)) break;
                var len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
                if (len < 24 || len > 96 * 1024 * 1024) break;
                var buf = new byte[len];
                if (!ReadExact(stream, buf, len)) break;

                var ctr = BinaryPrimitives.ReadUInt64BigEndian(buf);
                if (ctr < _recvCtr) break;           // replay / reorder — drop the link
                _recvCtr = ctr + 1;
                var tag = buf.AsSpan(8, 16);
                var ct = buf.AsSpan(24);
                var plain = new byte[ct.Length];
                _recv!.Decrypt(NonceFor(ctr), ct, tag, plain);

                var msg = NetMessage.FromBody(plain);
                if (msg is not null) MessageReceived?.Invoke(msg);
            }
        }
        catch (Exception ex) { FireDisconnected(ex.Message); return; }
        FireDisconnected("peer closed the connection");
    }

    private void FireDisconnected(string reason)
    {
        if (_closed) return;
        if (Interlocked.Exchange(ref _downFired, 1) == 0) Disconnected?.Invoke(reason);
    }

    private static byte[] NonceFor(ulong counter)
    {
        var n = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(n.AsSpan(4), counter);
        return n;
    }

    // ── raw framed I/O for the plaintext handshake (single-threaded) ─────────
    private void WritePlain(byte[] payload)
    {
        var s = _stream!;
        var head = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(head, payload.Length);
        s.Write(head, 0, 4);
        s.Write(payload, 0, payload.Length);
        s.Flush();
    }

    private byte[] ReadPlain()
    {
        var s = _stream!;
        var head = new byte[4];
        if (!ReadExact(s, head, 4)) throw new IOException("handshake closed");
        var len = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (len is < 0 or > 8192) throw new IOException("bad handshake frame");
        var buf = new byte[len];
        if (!ReadExact(s, buf, len)) throw new IOException("handshake closed");
        return buf;
    }

    private static bool ReadExact(NetworkStream s, byte[] buffer, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = s.Read(buffer, read, count - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private static byte[] Len16(byte[] b)
    {
        var h = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(h, (ushort)b.Length);
        return h;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var r = new byte[total];
        var o = 0;
        foreach (var p in parts) { p.CopyTo(r, o); o += p.Length; }
        return r;
    }

    private static void EnableTcpKeepAlive(Socket s)
    {
        try
        {
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 2);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch { /* older Windows: best-effort */ }
    }

    public void Dispose()
    {
        _closed = true;
        try { _sendQueue?.CompleteAdding(); } catch { }
        try { _stream?.Close(); } catch { }
        try { _client?.Close(); } catch { }
        try { _listener?.Stop(); } catch { }
        _send?.Dispose();
        _recv?.Dispose();
    }
}
