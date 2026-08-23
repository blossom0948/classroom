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
            "PC에 연결할 수 없습니다. PC는 이더넷이어도 됩니다. 휴대폰 Wi-Fi가 PC와 같은 공유기/LAN인지, 게스트 Wi-Fi가 아닌지 확인하세요. 시도한 PC 주소: ${payload.hosts.joinToString()}",
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
            )
            require(paired.version == 1) { "지원하지 않는 PC 프로토콜 버전입니다." }
            require(paired.computerId == payload.computerId) { "PC 식별자가 페어링 정보와 다릅니다." }
            require(paired.phoneId == phoneId) { "PC가 다른 휴대폰 식별자를 반환했습니다." }
            require(paired.deviceToken.length >= 43) { "PC 장치 토큰이 올바르지 않습니다." }
            paired
        }
    }
}
