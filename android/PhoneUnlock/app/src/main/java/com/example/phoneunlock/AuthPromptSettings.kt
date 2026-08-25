package com.example.phoneunlock

import android.app.NotificationManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings

object AuthPromptSettings {
    private const val PREFERENCES = "phone_unlock"
    private const val AUTO_OPEN_KEY = "auto_open_biometric_prompt"
    private const val DEVICE_CREDENTIAL_KEY = "allow_device_credential"
    private const val WEAK_FACE_KEY = "allow_weak_face_compatibility"

    fun isAutoOpenEnabled(context: Context): Boolean =
        context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
            .getBoolean(AUTO_OPEN_KEY, true)

    fun setAutoOpenEnabled(context: Context, enabled: Boolean) {
        context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
            .edit()
            .putBoolean(AUTO_OPEN_KEY, enabled)
            .apply()
    }

    fun isDeviceCredentialEnabled(context: Context): Boolean =
        context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
            .getBoolean(DEVICE_CREDENTIAL_KEY, false)

    fun setDeviceCredentialEnabled(context: Context, enabled: Boolean) {
        context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
            .edit()
            .putBoolean(DEVICE_CREDENTIAL_KEY, enabled)
            .apply()
    }

    fun isWeakFaceEnabled(context: Context): Boolean =
        context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
            .getBoolean(WEAK_FACE_KEY, false)

    fun setWeakFaceEnabled(context: Context, enabled: Boolean) {
        context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
            .edit()
            .putBoolean(WEAK_FACE_KEY, enabled)
            .apply()
    }

    fun currentAuthMode(context: Context): String =
        when {
            isWeakFaceEnabled(context) -> "weak_face"
            isDeviceCredentialEnabled(context) -> "credential"
            else -> "biometric"
        }

    fun canUseFullScreenIntent(context: Context): Boolean =
        Build.VERSION.SDK_INT < Build.VERSION_CODES.UPSIDE_DOWN_CAKE ||
            context.getSystemService(NotificationManager::class.java).canUseFullScreenIntent()

    fun permissionIntent(context: Context): Intent? {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            return null
        }
        return Intent(
            Settings.ACTION_MANAGE_APP_USE_FULL_SCREEN_INTENT,
            Uri.parse("package:${context.packageName}"),
        )
    }
}
