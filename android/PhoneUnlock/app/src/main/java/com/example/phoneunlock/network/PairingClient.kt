package com.example.phoneunlock.network

import android.os.Build
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONObject
import com.example.phoneunlock.storage.PairedComputer
import com.example.phoneunlock.storage.PairingPayload
import java.io.IOException

class PairingClient {
    suspend fun pair(
        payload: PairingPayload,
        phoneId: String,
        publicKey: String,
    ): PairedComputer = withContext(Dispatchers.IO) {
        var lastNetworkError: IOException? = null
        for (host in payload.hosts) {
            try {
                return@withContext pairUsingHost(payload, host, phoneId, publicKey)
            } catch (exception: IOException) {
                lastNetworkError = exception
            }
        }
        throw IOException(
            "PC에 연결할 수 없습니다. PC는 이더넷이어도 됩니다. 같은 LAN이 아니면 양쪽에 Tailscale/WireGuard VPN을 연결하세요. 게스트 Wi-Fi가 아닌지도 확인하세요. 시도한 PC 주소: ${payload.hosts.joinToString()}",
            lastNetworkError,
        )
    }

    private fun pairUsingHost(
        payload: PairingPayload,
        host: String,
        phoneId: String,
        publicKey: String,
    ): PairedComputer {
        val client = PinnedHttpClient.create(payload.certificateFingerprint)
        val body = JSONObject()
            .put("phoneId", phoneId)
            .put("phoneName", "${Build.MANUFACTURER} ${Build.MODEL}".trim())
            .put("publicKey", publicKey)
            .toString()
            .toRequestBody("application/json; charset=utf-8".toMediaType())
        val request = Request.Builder()
            .url("https://$host:${payload.port}/pair")
            .header("X-Pairing-Token", payload.pairingToken)
            .post(body)
            .build()

        return client.newCall(request).execute().use { response ->
            require(response.isSuccessful) { "PC가 페어링 요청을 거부했습니다 (${response.code})." }
            val value = JSONObject(response.body?.string() ?: error("PC 응답이 비어 있습니다."))
            val fingerprint = value.getString("certificateFingerprint").uppercase()
            require(fingerprint == payload.certificateFingerprint) { "PC 인증서 지문이 응답에서 변경되었습니다." }
            val paired = PairedComputer(
                version = value.getInt("version"),
                computerId = java.util.UUID.fromString(value.getString("computerId")),
                computerName = value.getString("computerName"),
                host = host,
                port = value.getInt("port"),
                certificateFingerprint = fingerprint,
                phoneId = value.getString("phoneId"),
                deviceToken = value.getString("deviceToken"),
                publicKey = publicKey,
                hosts = payload.hosts,
                wakeOnLanTargets = payload.wakeOnLanTargets,
            )
            require(paired.version == 1) { "지원하지 않는 PC 프로토콜 버전입니다." }
            require(paired.computerId == payload.computerId) { "PC 식별자가 페어링 정보와 다릅니다." }
            require(paired.phoneId == phoneId) { "PC가 다른 휴대폰 식별자를 반환했습니다." }
            require(paired.deviceToken.length >= 43) { "PC 장치 토큰이 올바르지 않습니다." }
            paired
        }
    }

    suspend fun checkHealth(computer: PairedComputer): Boolean = withContext(Dispatchers.IO) {
        val client = PinnedHttpClient.create(computer.certificateFingerprint)
        val hosts = (listOf(computer.host) + computer.hosts)
            .filter { it.isNotBlank() }
            .distinct()
        hosts.any { host ->
            runCatching {
                val request = Request.Builder()
                    .url("https://$host:${computer.port}/health")
                    .get()
                    .build()
                client.newCall(request).execute().use { response -> response.isSuccessful }
            }.getOrDefault(false)
        }
    }

    /**
     * Refreshes the addresses advertised by a paired PC without exposing a
     * pairing token again. This lets a PC add its VPN address while the phone
     * is still on the home LAN, then keep working away from home.
     */
    suspend fun refreshConnectionInfo(computer: PairedComputer): PairedComputer = withContext(Dispatchers.IO) {
        var lastNetworkError: IOException? = null
        val client = PinnedHttpClient.create(computer.certificateFingerprint)
        val candidates = (listOf(computer.host) + computer.hosts)
            .map { it.trim() }
            .filter { it.isNotEmpty() }
            .distinct()
        for (host in candidates) {
            try {
                val request = Request.Builder()
                    .url("https://$host:${computer.port}/connection-info?phoneId=${computer.phoneId}")
                    .header("Authorization", "Bearer ${computer.deviceToken}")
                    .get()
                    .build()
                return@withContext client.newCall(request).execute().use { response ->
                    if (!response.isSuccessful) {
                        throw IOException("PC 연결 정보 요청이 거부되었습니다 (${response.code}).")
                    }
                    val value = JSONObject(response.body?.string() ?: error("PC 연결 정보가 비어 있습니다."))
                    require(value.getInt("version") == 1) { "지원하지 않는 PC 프로토콜 버전입니다." }
                    require(java.util.UUID.fromString(value.getString("computerId")) == computer.computerId) {
                        "다른 PC의 연결 정보를 받았습니다."
                    }
                    val refreshedHosts = buildList {
                        add(host)
                        value.optJSONArray("hosts")?.let { hosts ->
                            for (index in 0 until hosts.length()) {
                                val candidate = hosts.optString(index).trim()
                                if (candidate.isNotEmpty()) add(candidate)
                            }
                        }
                    }.distinct()
                    require(refreshedHosts.isNotEmpty() && refreshedHosts.all {
                        it.matches(Regex("^[A-Za-z0-9.-]+$"))
                    }) { "PC 주소가 올바르지 않습니다." }
                    computer.copy(
                        computerName = value.optString("computerName", computer.computerName).trim()
                            .ifBlank { computer.computerName },
                        host = host,
                        port = value.optInt("port", computer.port).takeIf { it in 1..65535 } ?: computer.port,
                        hosts = refreshedHosts,
                        wakeOnLanTargets = value.optJSONArray("wakeOnLanTargets")?.let { targets ->
                            buildList {
                                for (index in 0 until targets.length()) {
                                    val target = targets.optJSONObject(index) ?: continue
                                    val mac = target.optString("macAddress").trim()
                                    val broadcast = target.optString("broadcastAddress").trim()
                                    if (mac.isNotEmpty() && broadcast.isNotEmpty()) {
                                        add(com.example.phoneunlock.storage.WakeOnLanTarget(mac, broadcast))
                                    }
                                }
                            }.distinctBy { "${it.macAddress}|${it.broadcastAddress}" }
                        } ?: computer.wakeOnLanTargets,
                    )
                }
            } catch (exception: IOException) {
                lastNetworkError = exception
            }
        }
        throw IOException("PC 연결 정보를 갱신할 수 없습니다.", lastNetworkError)
    }
}
