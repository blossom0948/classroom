package com.example.phoneunlock.network

import okhttp3.OkHttpClient
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager

object PinnedHttpClient {
    fun create(expectedFingerprint: String): OkHttpClient {
        val normalized = expectedFingerprint.replace(":", "").uppercase()
        require(normalized.matches(Regex("^[0-9A-F]{64}$"))) { "인증서 지문이 올바르지 않습니다." }

        val trustManager = object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                throw CertificateException("Client certificates are not accepted.")
            }

            override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                val leaf = chain?.firstOrNull() ?: throw CertificateException("Server certificate is missing.")
                leaf.checkValidity()
                val actual = MessageDigest.getInstance("SHA-256").digest(leaf.encoded)
                val expected = normalized.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
                if (!MessageDigest.isEqual(actual, expected)) {
                    throw CertificateException("Phone Unlock certificate fingerprint did not match pairing.")
                }
            }

            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
        }

        val context = SSLContext.getInstance("TLS")
        context.init(null, arrayOf<TrustManager>(trustManager), SecureRandom())
        return OkHttpClient.Builder()
            .sslSocketFactory(context.socketFactory, trustManager)
            // The exact self-signed leaf certificate is pinned above. Hostname validation
            // would reject a private VPN address that was not present when the PC certificate
            // was first created; the pinned fingerprint remains the trust boundary.
            .hostnameVerifier { _, _ -> true }
            .pingInterval(java.time.Duration.ofSeconds(15))
            .retryOnConnectionFailure(true)
            .build()
    }
}
