package com.snapfield.receiver.net

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.NetworkInterface

/**
 * UDP presence beacon — same wire format as the desktop receiver:
 * "SNAPFIELD1\tname\tport" to port 45655, every ~2s, on the global broadcast
 * address plus each interface's subnet-directed broadcast.
 */
class Beacon(private val name: String, private val tcpPort: Int) {
    @Volatile private var stopped = false

    fun start() {
        Thread({
            val payload = "SNAPFIELD1\t$name\t$tcpPort".toByteArray(Charsets.UTF_8)
            try {
                DatagramSocket().use { udp ->
                    udp.broadcast = true
                    var targets = broadcastTargets()
                    var iteration = 0
                    while (!stopped) {
                        if (iteration++ % 15 == 0) targets = broadcastTargets()
                        for (t in targets) {
                            try { udp.send(DatagramPacket(payload, payload.size, t, 45655)) } catch (_: Exception) {}
                        }
                        var i = 0
                        while (i < 20 && !stopped) { Thread.sleep(100); i++ }
                    }
                }
            } catch (_: Exception) { /* broadcast unavailable — discovery just won't show us */ }
        }, "Snapfield.Beacon").start()
    }

    private fun broadcastTargets(): List<InetAddress> {
        val targets = mutableListOf(InetAddress.getByName("255.255.255.255"))
        try {
            for (ni in NetworkInterface.getNetworkInterfaces()) {
                if (!ni.isUp || ni.isLoopback) continue
                for (ia in ni.interfaceAddresses) {
                    val bcast = ia.broadcast ?: continue
                    if (targets.none { it == bcast }) targets.add(bcast)
                }
            }
        } catch (_: Exception) {}
        return targets
    }

    fun stop() { stopped = true }
}
