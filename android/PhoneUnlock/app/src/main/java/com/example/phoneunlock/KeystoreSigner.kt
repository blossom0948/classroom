package com.example.phoneunlock

import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyInfo
import android.security.keystore.KeyProperties
import android.security.keystore.StrongBoxUnavailableException
import android.util.Base64
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.PrivateKey
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.util.UUID

data class KeyMaterial(
    val privateKey: PrivateKey,
    val publicKeyBase64: String,
    val protection: String,
)

class KeystoreSigner(private val context: Context) {
    private val keyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }

    fun getOrCreate(computerId: UUID): KeyMaterial {
        val alias = aliasFor(computerId)
        if (!keyStore.containsAlias(alias)) {
            createKey(alias)
        }

        val privateKey = keyStore.getKey(alias, null) as? PrivateKey
            ?: error("Android Keystore 개인키를 읽을 수 없습니다.")
        val publicKey = keyStore.getCertificate(alias)?.publicKey
            ?: error("Android Keystore 공개키를 읽을 수 없습니다.")

        return KeyMaterial(
            privateKey = privateKey,
            publicKeyBase64 = Base64.encodeToString(publicKey.encoded, Base64.NO_WRAP),
            protection = describeProtection(privateKey),
        )
    }

    fun createSignature(privateKey: PrivateKey): Signature =
        Signature.getInstance("SHA256withECDSA").apply { initSign(privateKey) }

    fun delete(computerId: UUID) {
        keyStore.deleteEntry(aliasFor(computerId))
    }

    private fun createKey(alias: String) {
        val supportsStrongBox = context.packageManager.hasSystemFeature(PackageManager.FEATURE_STRONGBOX_KEYSTORE)
        if (supportsStrongBox) {
            try {
                generateKey(alias, useStrongBox = true)
                return
            } catch (_: StrongBoxUnavailableException) {
                // The feature can be present while capacity is temporarily unavailable.
            }
        }

        generateKey(alias, useStrongBox = false)
    }

    private fun generateKey(alias: String, useStrongBox: Boolean) {
        val specBuilder = KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_SIGN)
            .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
            .setDigests(KeyProperties.DIGEST_SHA256)
            .setUserAuthenticationRequired(true)
            .setUserAuthenticationParameters(0, KeyProperties.AUTH_BIOMETRIC_STRONG)
            .setInvalidatedByBiometricEnrollment(true)

        if (useStrongBox) {
            specBuilder.setIsStrongBoxBacked(true)
        }

        KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, ANDROID_KEYSTORE).apply {
            initialize(specBuilder.build())
            generateKeyPair()
        }
    }

    @Suppress("DEPRECATION")
    private fun describeProtection(privateKey: PrivateKey): String {
        val factory = KeyFactory.getInstance(privateKey.algorithm, ANDROID_KEYSTORE)
        val keyInfo = factory.getKeySpec(privateKey, KeyInfo::class.java)
        return when {
            Build.VERSION.SDK_INT >= Build.VERSION_CODES.S && keyInfo.securityLevel == KeyProperties.SECURITY_LEVEL_STRONGBOX -> "StrongBox hardware-backed"
            keyInfo.isInsideSecureHardware -> "Hardware-backed"
            else -> "Software-backed Keystore"
        }
    }

    private fun aliasFor(computerId: UUID): String = "phone_unlock_${computerId.toString().lowercase()}"

    private companion object {
        const val ANDROID_KEYSTORE = "AndroidKeyStore"
    }
}
