package com.example.phoneunlock.storage

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.time.Instant

data class LocalActivity(
    val occurredAt: Instant,
    val title: String,
    val detail: String,
)

class ActivityLogStore(context: Context) {
    private val preferences = context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)

    fun append(title: String, detail: String) {
        val current = load().toMutableList()
        current.add(0, LocalActivity(Instant.now(), title, detail))
        val array = JSONArray()
        current.take(MAX_ENTRIES).forEach { item ->
            array.put(JSONObject()
                .put("occurredAt", item.occurredAt.toString())
                .put("title", item.title)
                .put("detail", item.detail))
        }
        preferences.edit().putString(KEY, array.toString()).apply()
    }

    fun load(): List<LocalActivity> = try {
        val array = JSONArray(preferences.getString(KEY, "[]") ?: "[]")
        (0 until array.length()).mapNotNull { index ->
            runCatching {
                val item = array.getJSONObject(index)
                LocalActivity(
                    Instant.parse(item.getString("occurredAt")),
                    item.getString("title"),
                    item.getString("detail"),
                )
            }.getOrNull()
        }
    } catch (_: Exception) {
        emptyList()
    }

    fun clear() {
        preferences.edit().remove(KEY).apply()
    }

    private companion object {
        const val PREFERENCES = "phone_unlock_activity"
        const val KEY = "events"
        const val MAX_ENTRIES = 60
    }
}
