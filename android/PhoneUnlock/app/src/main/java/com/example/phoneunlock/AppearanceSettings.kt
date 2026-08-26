package com.example.phoneunlock

import android.content.Context
import androidx.appcompat.app.AppCompatDelegate

object AppearanceSettings {
    const val SYSTEM = "system"
    const val LIGHT = "light"
    const val DARK = "dark"
    private const val PREFERENCES = "phone_unlock"
    private const val KEY = "appearance"

    fun current(context: Context): String = context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
        .getString(KEY, SYSTEM)
        ?.takeIf { it in setOf(SYSTEM, LIGHT, DARK) }
        ?: SYSTEM

    fun set(context: Context, mode: String) {
        require(mode in setOf(SYSTEM, LIGHT, DARK))
        context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
            .edit()
            .putString(KEY, mode)
            .apply()
        apply(mode)
    }

    fun apply(context: Context) = apply(current(context))

    private fun apply(mode: String) {
        AppCompatDelegate.setDefaultNightMode(
            when (mode) {
                LIGHT -> AppCompatDelegate.MODE_NIGHT_NO
                DARK -> AppCompatDelegate.MODE_NIGHT_YES
                else -> AppCompatDelegate.MODE_NIGHT_FOLLOW_SYSTEM
            },
        )
    }
}
