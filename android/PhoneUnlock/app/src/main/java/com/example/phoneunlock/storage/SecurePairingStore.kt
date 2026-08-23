package com.example.phoneunlock.storage

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.nio.ByteBuffer
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

class SecurePairingStore(context: Context) {
    private val preferences = context.getSharedPreferences(PREFERENCES_NAME, Context.MODE_PRIVATE)
    private val keyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }

    fun save(computer: PairedComputer) {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, getOrCreateKey())
        val encrypted = cipher.doFinal(computer.toJson().toByteArray(Charsets.UTF_8))
        val packed = ByteBuffer.allocate(2 + cipher.iv.size + encrypted.size)
            .put(FORMAT_VERSION)
            .put(cipher.iv.size.toByte())
            .put(cipher.iv)
            .put(encrypted)
            .array()
        preferences.edit().putString(PAIRED_COMPUTER_KEY, Base64.encodeToString(packed, Base64.NO_WRAP)).apply()
    }

    fun load(): PairedComputer? {
        val encoded = preferences.getString(PAIRED_COMPUTER_KEY, null) ?: return null
        return try {
            val packed = ByteBuffer.wrap(Base64.decode(encoded, Base64.DEFAULT))
            require(packed.get() == FORMAT_VERSION) { "지원하지 않는 보안 저장 형식입니다." }
            val ivLength = packed.get().toInt() and 0xFF
            require(ivLength in 12..16 && packed.remaining() > ivLength) { "보안 저장 데이터가 손상되었습니다." }
            val iv = ByteArray(ivLength).also { packed.get(it) }
            val encrypted = ByteArray(packed.remaining()).also { packed.get(it) }
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.DECRYPT_MODE, getOrCreateKey(), GCMParameterSpec(128, iv))
            PairedComputer.fromJson(String(cipher.doFinal(encrypted), Charsets.UTF_8))
        } catch (_: Exception) {
            null
        }
    }

    fun clear() {
        preferences.edit().remove(PAIRED_COMPUTER_KEY).apply()
    }

    private fun getOrCreateKey(): SecretKey {
        val existing = keyStore.getKey(KEY_ALIAS, null) as? SecretKey
        if (existing != null) {
            return existing
        }

        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE).run {
            init(
                KeyGenParameterSpec.Builder(
                    KEY_ALIAS,
                    KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
                )
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .setKeySize(256)
                    .setRandomizedEncryptionRequired(true)
                    .build(),
            )
            generateKey()
        }
    }

    private companion object {
        const val ANDROID_KEYSTORE = "AndroidKeyStore"
        const val KEY_ALIAS = "phone_unlock_pairing_storage"
        const val PREFERENCES_NAME = "phone_unlock_secure"
        const val PAIRED_COMPUTER_KEY = "paired_computer"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val FORMAT_VERSION: Byte = 1
    }
}
