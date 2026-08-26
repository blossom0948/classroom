package com.example.phoneunlock.network

import com.example.phoneunlock.storage.WakeOnLanTarget
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress

object WakeOnLanSender {
    fun send(targets: List<WakeOnLanTarget>): Int {
        if (targets.isEmpty()) return 0
        val packets = targets.flatMap { target ->
            val mac = parseMac(target.macAddress) ?: return@flatMap emptyList()
            val magic = ByteArray(6) { 0xFF.toByte() } + ByteArray(16 * 6) { index -> mac[index % 6] }
            val addresses = listOf(target.broadcastAddress, "255.255.255.255")
                .distinct()
                .mapNotNull { address -> runCatching { InetAddress.getByName(address) }.getOrNull() }
            addresses.flatMap { address ->
                listOf(7, 9).map { port ->
                    DatagramPacket(magic, magic.size, address, port)
                }
            }
        }
        if (packets.isEmpty()) return 0

        DatagramSocket().use { socket ->
            socket.broadcast = true
            // Some NICs or routers drop the first broadcast after a phone wakes
            // its Wi-Fi radio. A few short repeats make the action reliable
            // without turning it into a long-running background operation.
            repeat(3) { attempt ->
                packets.forEach(socket::send)
                if (attempt < 2) Thread.sleep(80)
            }
        }
        return packets.size * 3
    }

    private fun parseMac(value: String): ByteArray? {
        val normalized = value.replace(":", "").replace("-", "").trim()
        if (!normalized.matches(Regex("^[0-9A-Fa-f]{12}$"))) return null
        return ByteArray(6) { index -> normalized.substring(index * 2, index * 2 + 2).toInt(16).toByte() }
    }
}
