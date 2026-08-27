package com.example.phoneunlock.storage

import android.content.Context
import org.json.JSONObject
import java.time.Instant
import java.util.UUID

data class PcRuntimeState(
    val powerState: String,
    val sessionState: String,
    val route: String,
    val observedAt: Long,
) {
    val isOnline: Boolean get() = powerState == "ON"
    val isLocked: Boolean get() = sessionState == "LOCKED"
}

class PcStateStore(context: Context) {
    private val preferences = context.getSharedPreferences("phone_unlock_pc_state", Context.MODE_PRIVATE)

    fun load(computerId: UUID): PcRuntimeState? = runCatching {
        val raw = preferences.getString(computerId.toString(), null) ?: return null
        val value = JSONObject(raw)
        PcRuntimeState(
            powerState = value.optString("powerState", "OFF"),
            sessionState = value.optString("sessionState", "UNKNOWN"),
            route = value.optString("route", ""),
            observedAt = value.optLong("observedAt", 0L),
        )
    }.getOrNull()

    fun save(computerId: UUID, state: PcRuntimeState) {
        val value = JSONObject()
            .put("powerState", state.powerState)
            .put("sessionState", state.sessionState)
            .put("route", state.route)
            .put("observedAt", state.observedAt)
        preferences.edit().putString(computerId.toString(), value.toString()).apply()
    }

    fun markOnline(computerId: UUID, route: String) {
        val previous = load(computerId)
        save(computerId, PcRuntimeState(
            powerState = "ON",
            sessionState = previous?.sessionState ?: "UNKNOWN",
            route = route,
            observedAt = Instant.now().epochSecond,
        ))
    }

    fun markOffline(computerId: UUID) {
        val previous = load(computerId)
        save(computerId, PcRuntimeState(
            powerState = "OFF",
            sessionState = "UNKNOWN",
            route = previous?.route.orEmpty(),
            observedAt = Instant.now().epochSecond,
        ))
    }
}
