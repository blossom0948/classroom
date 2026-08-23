package com.example.phoneunlock.storage

import org.json.JSONObject
import java.time.Instant
import java.util.UUID

data class PairingPayload(
    val version: Int,
    val computerId: UUID,
    val computerName: String,
    val pairingToken: String,
    val host: String,
    val port: Int,
    val expiresAt: Long,
    val certificateFingerprint: String,
) {
    companion object {
        fun parse(json: String): PairingPayload {
            val value = JSONObject(json)
            val payload = PairingPayload(
                version = value.getInt("version"),
                computerId = UUID.fromString(value.getString("computerId")),
                computerName = value.getString("computerName").trim(),
                pairingToken = value.getString("pairingToken"),
                host = value.getString("host").trim(),
                port = value.getInt("port"),
                expiresAt = value.getLong("expiresAt"),
                certificateFingerprint = normalizeFingerprint(value.getString("certificateFingerprint")),
            )
            require(payload.version == 1) { "지원하지 않는 페어링 버전입니다." }
            require(payload.computerName.isNotEmpty()) { "PC 이름이 비어 있습니다." }
            require(payload.pairingToken.length >= 43) { "페어링 토큰이 올바르지 않습니다." }
            require(payload.host.matches(Regex("^[A-Za-z0-9.-]+$"))) { "PC 주소가 올바르지 않습니다." }
            require(payload.port in 1..65535) { "포트가 올바르지 않습니다." }
            require(payload.expiresAt >= Instant.now().epochSecond) { "페어링 정보가 만료되었습니다." }
            require(payload.certificateFingerprint.matches(Regex("^[0-9A-F]{64}$"))) { "인증서 지문이 올바르지 않습니다." }
            return payload
        }

        private fun normalizeFingerprint(value: String): String =
            value.replace(":", "").uppercase()
    }
}

data class PairedComputer(
    val version: Int,
    val computerId: UUID,
    val computerName: String,
    val host: String,
    val port: Int,
    val certificateFingerprint: String,
    val phoneId: String,
    val deviceToken: String,
) {
    fun toJson(): String = JSONObject()
        .put("version", version)
        .put("computerId", computerId.toString())
        .put("computerName", computerName)
        .put("host", host)
        .put("port", port)
        .put("certificateFingerprint", certificateFingerprint)
        .put("phoneId", phoneId)
        .put("deviceToken", deviceToken)
        .toString()

    companion object {
        fun fromJson(json: String): PairedComputer {
            val value = JSONObject(json)
            return PairedComputer(
                version = value.getInt("version"),
                computerId = UUID.fromString(value.getString("computerId")),
                computerName = value.getString("computerName"),
                host = value.getString("host"),
                port = value.getInt("port"),
                certificateFingerprint = value.getString("certificateFingerprint"),
                phoneId = value.getString("phoneId"),
                deviceToken = value.getString("deviceToken"),
            )
        }
    }
}
