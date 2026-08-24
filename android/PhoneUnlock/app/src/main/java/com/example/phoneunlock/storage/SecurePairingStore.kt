package com.example.phoneunlock.storage

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import org.json.JSONArray
import org.json.JSONObject
import java.nio.ByteBuffer
import java.security.KeyStore
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

class SecurePairingStore(context: Context) {
    private val preferences = context.getSharedPreferences(PREFERENCES_NAME, Context.MODE_PRIVATE)
    private val keyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }

    fun save(computer: PairedComputer, select: Boolean = true) {
        val current = loadAll().toMutableList()
        val index = current.indexOfFirst { it.computerId == computer.computerId }
        if (index >= 0) current[index] = computer else current += computer
        val selected = if (select) computer.computerId else selectedId()
        writeState(current, selected)
    }

    fun load(): PairedComputer? {
        val computers = loadAll()
        val selected = selectedId()?.let { id -> computers.firstOrNull { it.computerId == id } }
        return selected ?: computers.firstOrNull()
    }

    fun loadAll(): List<PairedComputer> = try {
        val encoded = preferences.getString(PAIRED_COMPUTERS_KEY, null)
        if (!encoded.isNullOrBlank()) {
            val state = decrypt(encoded)
            val array = state.optJSONArray("computers") ?: JSONArray()
            (0 until array.length()).mapNotNull { index ->
                runCatching { PairedComputer.fromJson(array.getString(index)) }.getOrNull()
            }
        } else {
            val legacy = preferences.getString(LEGACY_PAIRED_COMPUTER_KEY, null)
            if (legacy.isNullOrBlank()) emptyList()
            else listOf(PairedComputer.fromJson(decrypt(legacy).toString()))
        }
    } catch (_: Exception) {
        emptyList()
    }

    fun select(computerId: UUID): Boolean {
        if (loadAll().none { it.computerId == computerId }) return false
        preferences.edit().putString(SELECTED_COMPUTER_KEY, computerId.toString()).apply()
        return true
    }

    fun remove(computerId: UUID): Boolean {
        val current = loadAll()
        if (current.none { it.computerId == computerId }) return false
        val remaining = current.filterNot { it.computerId == computerId }
        val selected = selectedId().takeUnless { it == computerId } ?: remaining.firstOrNull()?.computerId
        writeState(remaining, selected)
        return true
    }

    fun clear() {
        preferences.edit()
            .remove(PAIRED_COMPUTERS_KEY)
            .remove(LEGACY_PAIRED_COMPUTER_KEY)
            .remove(SELECTED_COMPUTER_KEY)
            .apply()
    }

    private fun selectedId(): UUID? = preferences.getString(SELECTED_COMPUTER_KEY, null)
        ?.let { runCatching { UUID.fromString(it) }.getOrNull() }

    private fun writeState(computers: List<PairedComputer>, selected: UUID?) {
        val array = JSONArray()
        computers.forEach { array.put(it.toJson()) }
        val state = JSONObject().put("computers", array)
        preferences.edit()
            .putString(PAIRED_COMPUTERS_KEY, encrypt(state.toString()))
            .apply {
                if (selected == null) remove(SELECTED_COMPUTER_KEY)
                else putString(SELECTED_COMPUTER_KEY, selected.toString())
            }
            .apply()
    }

    private fun encrypt(value: String): String {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, getOrCreateKey())
        val encrypted = cipher.doFinal(value.toByteArray(Charsets.UTF_8))
        val packed = ByteBuffer.allocate(2 + cipher.iv.size + encrypted.size)
            .put(FORMAT_VERSION)
            .put(cipher.iv.size.toByte())
            .put(cipher.iv)
            .put(encrypted)
            .array()
        return Base64.encodeToString(packed, Base64.NO_WRAP)
    }

    private fun decrypt(encoded: String): JSONObject {
        val packed = ByteBuffer.wrap(Base64.decode(encoded, Base64.DEFAULT))
        require(packed.get() == FORMAT_VERSION) { "지원하지 않는 보안 저장 형식입니다." }
        val ivLength = packed.get().toInt() and 0xFF
        require(ivLength in 12..16 && packed.remaining() > ivLength) { "보안 저장 데이터가 손상되었습니다." }
        val iv = ByteArray(ivLength).also { packed.get(it) }
        val encrypted = ByteArray(packed.remaining()).also { packed.get(it) }
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.DECRYPT_MODE, getOrCreateKey(), GCMParameterSpec(128, iv))
        return JSONObject(String(cipher.doFinal(encrypted), Charsets.UTF_8))
    }

    private fun getOrCreateKey(): SecretKey {
        val existing = keyStore.getKey(KEY_ALIAS, null) as? SecretKey
        if (existing != null) return existing

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
        const val PAIRED_COMPUTERS_KEY = "paired_computers"
        const val LEGACY_PAIRED_COMPUTER_KEY = "paired_computer"
        const val SELECTED_COMPUTER_KEY = "selected_computer"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val FORMAT_VERSION: Byte = 1
    }
}
