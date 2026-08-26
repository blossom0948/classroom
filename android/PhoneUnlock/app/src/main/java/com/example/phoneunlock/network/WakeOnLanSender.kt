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
            listOf(
                DatagramPacket(
                    magic,
                    magic.size,
                    InetAddress.getByName(target.broadcastAddress),
                    9,
                ),
            )
        }
        if (packets.isEmpty()) return 0

        DatagramSocket().use { socket ->
            socket.broadcast = true
            packets.forEach(socket::send)
        }
        return packets.size
    }

    private fun parseMac(value: String): ByteArray? {
        val normalized = value.replace(":", "").replace("-", "").trim()
        if (!normalized.matches(Regex("^[0-9A-Fa-f]{12}$"))) return null
        return ByteArray(6) { index -> normalized.substring(index * 2, index * 2 + 2).toInt(16).toByte() }
    }
}
