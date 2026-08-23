package com.example.phoneunlock

import android.content.ClipData
import android.content.ClipboardManager
import android.graphics.Color
import android.os.Bundle
import android.security.keystore.KeyPermanentlyInvalidatedException
import android.text.method.ScrollingMovementMethod
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.biometric.BiometricManager
import androidx.biometric.BiometricPrompt
import androidx.core.content.ContextCompat
import com.google.android.material.button.MaterialButton
import com.google.android.material.textfield.TextInputEditText
import org.json.JSONException
import java.security.Signature
import java.time.Instant
import java.util.UUID

class MainActivity : AppCompatActivity() {
    private lateinit var requestInput: TextInputEditText
    private lateinit var computerNameText: TextView
    private lateinit var requestMetaText: TextView
    private lateinit var resultText: TextView
    private lateinit var publicKeyText: TextView
    private lateinit var responseText: TextView
    private lateinit var keystoreSigner: KeystoreSigner
    private lateinit var phoneId: String
    private var pendingRequest: AuthRequest? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        requestInput = findViewById(R.id.requestInput)
        computerNameText = findViewById(R.id.computerNameText)
        requestMetaText = findViewById(R.id.requestMetaText)
        resultText = findViewById(R.id.resultText)
        publicKeyText = findViewById(R.id.publicKeyText)
        responseText = findViewById(R.id.responseText)
        publicKeyText.movementMethod = ScrollingMovementMethod()
        responseText.movementMethod = ScrollingMovementMethod()
        keystoreSigner = KeystoreSigner(this)
        phoneId = loadOrCreatePhoneId()

        findViewById<MaterialButton>(R.id.inspectButton).setOnClickListener { inspectRequest() }
        findViewById<MaterialButton>(R.id.signButton).setOnClickListener { beginBiometricSigning() }
        findViewById<MaterialButton>(R.id.copyPublicKeyButton).setOnClickListener {
            copyText("Phone Unlock public key", publicKeyText.text.toString())
        }
        findViewById<MaterialButton>(R.id.copyResponseButton).setOnClickListener {
            copyText("Phone Unlock response", responseText.text.toString())
        }
    }

    private fun inspectRequest(): AuthRequest? {
        return try {
            val request = PhoneUnlockProtocol.parseAuthRequest(requestInput.text?.toString().orEmpty())
            val material = keystoreSigner.getOrCreate(request.computerId)
            pendingRequest = request
            computerNameText.text = request.computerName
            requestMetaText.text = "● 요청 확인됨 · ${request.expiresAt - Instant.now().epochSecond}초 남음"
            publicKeyText.text = material.publicKeyBase64
            showResult("요청이 안전한 형식입니다.\n키 보호: ${material.protection}", success = true)
            request
        } catch (exception: Exception) {
            showRequestError(exception)
            null
        }
    }

    private fun beginBiometricSigning() {
        val request = inspectRequest() ?: return
        val biometricStatus = BiometricManager.from(this).canAuthenticate(BiometricManager.Authenticators.BIOMETRIC_STRONG)
        if (biometricStatus != BiometricManager.BIOMETRIC_SUCCESS) {
            showResult("강한 생체인증을 사용할 수 없습니다. 기기 잠금과 지문/생체정보를 확인하세요.", success = false)
            return
        }

        val material = try {
            keystoreSigner.getOrCreate(request.computerId)
        } catch (exception: Exception) {
            showRequestError(exception)
            return
        }

        val signature = try {
            keystoreSigner.createSignature(material.privateKey)
        } catch (_: KeyPermanentlyInvalidatedException) {
            keystoreSigner.delete(request.computerId)
            showResult("생체정보 변경으로 키가 무효화되었습니다. 다시 눌러 새 키를 만든 뒤 Windows 공개키를 교체하세요.", success = false)
            return
        }

        val prompt = BiometricPrompt(
            this,
            ContextCompat.getMainExecutor(this),
            biometricCallback(request),
        )
        val promptInfo = BiometricPrompt.PromptInfo.Builder()
            .setTitle(getString(R.string.biometric_title))
            .setSubtitle("${request.computerName}에서 보낸 로그인을 승인합니다")
            .setNegativeButtonText(getString(R.string.biometric_cancel))
            .setAllowedAuthenticators(BiometricManager.Authenticators.BIOMETRIC_STRONG)
            .build()

        showResult("생체인증을 기다리고 있습니다…", success = true)
        prompt.authenticate(promptInfo, BiometricPrompt.CryptoObject(signature))
    }

    private fun biometricCallback(request: AuthRequest) = object : BiometricPrompt.AuthenticationCallback() {
        override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) {
            super.onAuthenticationSucceeded(result)
            val signature = result.cryptoObject?.signature
            if (signature == null) {
                showResult("인증 결과에 서명 객체가 없어 요청을 거부했습니다.", success = false)
                return
            }

            if (Instant.now().epochSecond > request.expiresAt) {
                responseText.text = PhoneUnlockProtocol.expiredResponse(request)
                showResult("생체인증 중 요청이 만료되었습니다. Windows에서 새 요청을 만드세요.", success = false)
                return
            }

            try {
                signature.update(PhoneUnlockProtocol.canonicalPayload(request))
                val signedBytes = signature.sign()
                responseText.text = PhoneUnlockProtocol.approvedResponse(request, phoneId, signedBytes)
                showResult("생체인증 성공 · challenge 서명이 완료되었습니다.\n응답을 Windows 앱에 전달하세요.", success = true)
            } catch (exception: Exception) {
                showRequestError(exception)
            }
        }

        override fun onAuthenticationError(errorCode: Int, errString: CharSequence) {
            super.onAuthenticationError(errorCode, errString)
            responseText.text = PhoneUnlockProtocol.deniedResponse(request, "BIOMETRIC_CANCELLED")
            showResult("인증이 취소되었습니다: $errString", success = false)
        }

        override fun onAuthenticationFailed() {
            super.onAuthenticationFailed()
            showResult("생체인증에 실패했습니다. 다시 시도하세요.", success = false)
        }
    }

    private fun showRequestError(exception: Exception) {
        val message = when (exception) {
            is JSONException, is IllegalArgumentException -> exception.message ?: "요청 형식이 올바르지 않습니다."
            else -> "처리 중 오류가 발생했습니다: ${exception.message ?: exception.javaClass.simpleName}"
        }
        showResult(message, success = false)
    }

    private fun showResult(message: String, success: Boolean) {
        resultText.text = message
        resultText.setTextColor(Color.parseColor(if (success) "#24503A" else "#8A2424"))
        resultText.setBackgroundColor(Color.parseColor(if (success) "#EAF7EF" else "#FFF0F0"))
        resultText.setPadding(16.dp, 14.dp, 16.dp, 14.dp)
    }

    private fun copyText(label: String, value: String) {
        if (value.isBlank() || value.startsWith("아직") || value.startsWith("생체인증 후")) {
            showResult("먼저 요청을 확인하고 생체인증을 완료하세요.", success = false)
            return
        }

        val clipboard = getSystemService(ClipboardManager::class.java)
        clipboard.setPrimaryClip(ClipData.newPlainText(label, value))
        showResult("클립보드에 복사했습니다.", success = true)
    }

    private fun loadOrCreatePhoneId(): String {
        val preferences = getSharedPreferences("phone_unlock", MODE_PRIVATE)
        val stored = preferences.getString("phone_id", null)
        if (!stored.isNullOrBlank()) {
            return stored
        }

        val created = UUID.randomUUID().toString()
        preferences.edit().putString("phone_id", created).apply()
        return created
    }

    private val Int.dp: Int
        get() = (this * resources.displayMetrics.density).toInt()
}
