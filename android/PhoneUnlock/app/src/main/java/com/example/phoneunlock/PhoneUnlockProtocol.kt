package com.example.phoneunlock

import android.util.Base64
import org.json.JSONObject
import java.time.Instant
import java.util.UUID

data class AuthRequest(
    val requestId: UUID,
    val computerId: UUID,
    val computerName: String,
    val challenge: String,
    val createdAt: Long,
    val expiresAt: Long,
)

data class RemoteUnlockRequest(
    val requestId: UUID,
    val computerId: UUID,
    val challenge: String,
    val expiresAt: Long,
    val phoneId: String,
)

data class RemoteLockRequest(
    val requestId: UUID,
    val computerId: UUID,
    val expiresAt: Long,
    val phoneId: String,
)

object PhoneUnlockProtocol {
    const val VERSION = 1
    const val MAX_AUTH_LIFETIME_SECONDS = 30L
    const val ALLOWED_CLOCK_SKEW_SECONDS = 30L

    fun parseAuthRequest(json: String, now: Long = Instant.now().epochSecond): AuthRequest {
        require(json.isNotBlank()) { "Windows 요청 JSON을 붙여 넣어 주세요." }

        val envelope = JSONObject(json)
        require(envelope.getInt("version") == VERSION) { "지원하지 않는 protocol version입니다." }
        require(envelope.getString("type") == "AUTH_REQUEST") { "AUTH_REQUEST 메시지가 아닙니다." }
        UUID.fromString(envelope.getString("messageId"))

        val payload = envelope.getJSONObject("payload")
        val request = AuthRequest(
            requestId = UUID.fromString(payload.getString("requestId")),
            computerId = UUID.fromString(payload.getString("computerId")),
            computerName = payload.getString("computerName").trim(),
            challenge = payload.getString("challenge"),
            createdAt = payload.getLong("createdAt"),
            expiresAt = payload.getLong("expiresAt"),
        )

        require(request.computerName.isNotEmpty()) { "PC 이름이 비어 있습니다." }
        require(request.createdAt <= now + ALLOWED_CLOCK_SKEW_SECONDS) { "휴대폰 시간보다 지나치게 미래에 생성된 요청입니다." }
        require(request.expiresAt + ALLOWED_CLOCK_SKEW_SECONDS >= now) { "인증 요청이 만료되었습니다." }
        require(request.expiresAt - request.createdAt in 1..MAX_AUTH_LIFETIME_SECONDS) {
            "허용된 30초보다 긴 요청입니다."
        }
        validateChallenge(request.challenge)
        return request
    }

    fun hasExpired(request: AuthRequest, now: Long = Instant.now().epochSecond): Boolean =
        request.expiresAt + ALLOWED_CLOCK_SKEW_SECONDS < now

    fun canonicalPayload(request: AuthRequest): ByteArray {
        validateChallenge(request.challenge)
        val value = listOf(
            "PHONE-UNLOCK-V1",
            "requestId=${request.requestId.toString().lowercase()}",
            "computerId=${request.computerId.toString().lowercase()}",
            "challenge=${request.challenge}",
            "expiresAt=${request.expiresAt}",
        ).joinToString("\n")
        return value.toByteArray(Charsets.UTF_8)
    }

    fun approvedResponse(request: AuthRequest, phoneId: String, signature: ByteArray): String {
        val payload = JSONObject()
            .put("requestId", request.requestId.toString().lowercase())
            .put("computerId", request.computerId.toString().lowercase())
            .put("challenge", request.challenge)
            .put("expiresAt", request.expiresAt)
            .put("phoneId", phoneId)
            .put("signature", Base64.encodeToString(signature, Base64.NO_WRAP))

        return envelope("AUTH_APPROVED", payload).toString(2)
    }

    fun deniedResponse(request: AuthRequest, reason: String): String {
        val payload = JSONObject()
            .put("requestId", request.requestId.toString().lowercase())
            .put("computerId", request.computerId.toString().lowercase())
            .put("reason", reason)
        return envelope("AUTH_DENIED", payload).toString(2)
    }

    fun expiredResponse(request: AuthRequest): String {
        val payload = JSONObject()
            .put("requestId", request.requestId.toString().lowercase())
            .put("computerId", request.computerId.toString().lowercase())
            .put("reason", "REQUEST_EXPIRED")
        return envelope("AUTH_EXPIRED", payload).toString(2)
    }

    fun canonicalRemoteUnlockPayload(request: RemoteUnlockRequest): ByteArray {
        validateChallenge(request.challenge)
        return listOf(
            "PHONE-UNLOCK-V1",
            "requestId=${request.requestId.toString().lowercase()}",
            "computerId=${request.computerId.toString().lowercase()}",
            "challenge=${request.challenge}",
            "expiresAt=${request.expiresAt}",
        ).joinToString("\n").toByteArray(Charsets.UTF_8)
    }

    fun remoteUnlockResponse(request: RemoteUnlockRequest, signature: ByteArray): String {
        val payload = JSONObject()
            .put("requestId", request.requestId.toString().lowercase())
            .put("computerId", request.computerId.toString().lowercase())
            .put("challenge", request.challenge)
            .put("expiresAt", request.expiresAt)
            .put("phoneId", request.phoneId)
            .put("signature", Base64.encodeToString(signature, Base64.NO_WRAP))
        return envelope("REMOTE_UNLOCK_REQUEST", payload).toString(2)
    }

    fun remoteLockResponse(request: RemoteLockRequest): String {
        val payload = JSONObject()
            .put("requestId", request.requestId.toString().lowercase())
            .put("computerId", request.computerId.toString().lowercase())
            .put("expiresAt", request.expiresAt)
            .put("phoneId", request.phoneId)
        return envelope("REMOTE_LOCK_REQUEST", payload).toString(2)
    }

    private fun envelope(type: String, payload: JSONObject): JSONObject = JSONObject()
        .put("version", VERSION)
        .put("type", type)
        .put("messageId", UUID.randomUUID().toString())
        .put("timestamp", Instant.now().epochSecond)
        .put("payload", payload)

    private fun validateChallenge(challenge: String) {
        val bytes = try {
            Base64.decode(challenge, Base64.DEFAULT)
        } catch (exception: IllegalArgumentException) {
            throw IllegalArgumentException("challenge가 Base64 형식이 아닙니다.", exception)
        }

        require(bytes.size == 32) { "challenge는 정확히 32바이트여야 합니다." }
        require(Base64.encodeToString(bytes, Base64.NO_WRAP) == challenge) {
            "challenge는 padding을 포함한 표준 Base64여야 합니다."
        }
    }
}
