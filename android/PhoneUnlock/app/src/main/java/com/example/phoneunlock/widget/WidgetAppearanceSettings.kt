package com.example.phoneunlock.widget

import android.content.Context
import android.content.res.Configuration
import com.example.phoneunlock.R

object WidgetAppearanceSettings {
    const val SYSTEM = "system"
    const val LIGHT = "light"
    const val DARK = "dark"
    private const val PREFERENCES = "phone_unlock_widget"
    private const val THEME_KEY = "theme"
    private const val TRANSPARENT_KEY = "transparent"

    fun theme(context: Context): String = context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
        .getString(THEME_KEY, SYSTEM)
        ?.takeIf { it in setOf(SYSTEM, LIGHT, DARK) }
        ?: SYSTEM

    fun setTheme(context: Context, value: String) {
        context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
            .edit().putString(THEME_KEY, value).apply()
    }

    fun isTransparent(context: Context): Boolean = context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
        .getBoolean(TRANSPARENT_KEY, false)

    fun setTransparent(context: Context, enabled: Boolean) {
        context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
            .edit().putBoolean(TRANSPARENT_KEY, enabled).apply()
    }

    fun nextTheme(context: Context): String = when (theme(context)) {
        SYSTEM -> LIGHT
        LIGHT -> DARK
        else -> SYSTEM
    }.also { setTheme(context, it) }

    fun label(context: Context): String = when (theme(context)) {
        LIGHT -> "밝게"
        DARK -> "어둡게"
        else -> "시스템"
    }

    fun palette(context: Context): Palette {
        val dark = when (theme(context)) {
            DARK -> true
            LIGHT -> false
            else -> context.resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK == Configuration.UI_MODE_NIGHT_YES
        }
        val transparent = isTransparent(context)
        return when {
            transparent && dark -> Palette(
                R.drawable.widget_background_transparent_dark,
                R.drawable.widget_primary_button_dark,
                R.drawable.widget_secondary_button_dark,
                0xFFFFFFFF.toInt(),
                0xFFC4C6CF.toInt(),
                0xFFFFFFFF.toInt(),
                0xFFFFFFFF.toInt(),
            )
            transparent -> Palette(
                R.drawable.widget_background_transparent_light,
                R.drawable.widget_primary_button_light,
                R.drawable.widget_secondary_button_light,
                0xFF17171A.toInt(),
                0xFF5D6068.toInt(),
                0xFFFFFFFF.toInt(),
                0xFF2E5CD6.toInt(),
            )
            dark -> Palette(
                R.drawable.widget_background_dark,
                R.drawable.widget_primary_button_dark,
                R.drawable.widget_secondary_button_dark,
                0xFFF7F7FA.toInt(),
                0xFFC4C6CF.toInt(),
                0xFFFFFFFF.toInt(),
                0xFFF7F7FA.toInt(),
            )
            else -> Palette(
                R.drawable.widget_background_light,
                R.drawable.widget_primary_button_light,
                R.drawable.widget_secondary_button_light,
                0xFF17171A.toInt(),
                0xFF5D6068.toInt(),
                0xFFFFFFFF.toInt(),
                0xFF2E5CD6.toInt(),
            )
        }
    }

    data class Palette(
        val background: Int,
        val primaryButton: Int,
        val secondaryButton: Int,
        val titleColor: Int,
        val statusColor: Int,
        val primaryTextColor: Int,
        val secondaryTextColor: Int,
    )
}
