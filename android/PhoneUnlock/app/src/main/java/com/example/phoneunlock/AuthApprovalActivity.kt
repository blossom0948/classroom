package com.example.phoneunlock

import android.os.Bundle
import android.security.keystore.KeyPermanentlyInvalidatedException
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.biometric.BiometricManager
import androidx.biometric.BiometricPrompt
import androidx.core.content.ContextCompat
import com.example.phoneunlock.network.ConnectionService
import com.example.phoneunlock.storage.SecurePairingStore
import com.google.android.material.button.MaterialButton
import java.time.Instant

class AuthApprovalActivity : AppCompatActivity() {
    private lateinit var request: AuthRequest
    private lateinit var statusText: TextView
    private lateinit var signer: KeystoreSigner
    private var responseSent = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_auth_approval)
        statusText = findViewById(R.id.authStatusText)
        signer = KeystoreSigner(this)

        request = try {
            PhoneUnlockProtocol.parseAuthRequest(
                intent.getStringExtra(ConnectionService.EXTRA_AUTH_REQUEST).orEmpty(),
            ).also { parsed ->
                val paired = SecurePairingStore(this).load()
                    ?: error("연결된 PC가 없습니다.")
                require(parsed.computerId == paired.computerId) { "등록되지 않은 PC의 요청입니다." }
            }
        } catch (exception: Exception) {
            statusText.text = exception.message ?: "요청이 올바르지 않습니다."
            findViewById<MaterialButton>(R.id.approveButton).isEnabled = false
            return
        }

        findViewById<TextView>(R.id.authComputerNameText).text =
            "${request.computerName}에서 로그인을 요청했습니다."
        findViewById<TextView>(R.id.authCountdownText).text =
            "${request.expiresAt - Instant.now().epochSecond}초 안에 승인하세요"
        findViewById<MaterialButton>(R.id.approveButton).setOnClickListener { authenticate() }
        findViewById<MaterialButton>(R.id.denyButton).setOnClickListener {
            sendAndFinish(PhoneUnlockProtocol.deniedResponse(request, "USER_DENIED"))
        }
    }

    private fun authenticate() {
        if (PhoneUnlockProtocol.hasExpired(request)) {
            sendAndFinish(PhoneUnlockProtocol.expiredResponse(request))
            return
        }

        if (BiometricManager.from(this).canAuthenticate(BiometricManager.Authenticators.BIOMETRIC_STRONG)
            != BiometricManager.BIOMETRIC_SUCCESS
        ) {
            statusText.text = "강한 생체인증을 사용할 수 없습니다."
            return
        }

        val material = signer.getOrCreate(request.computerId)
        val signature = try {
            signer.createSignature(material.privateKey)
        } catch (_: KeyPermanentlyInvalidatedException) {
            signer.delete(request.computerId)
            statusText.text = "생체정보 변경으로 키가 무효화되었습니다. PC와 다시 연결하세요."
            return
        }
        val prompt = BiometricPrompt(
            this,
            ContextCompat.getMainExecutor(this),
            object : BiometricPrompt.AuthenticationCallback() {
                override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) {
                    val authorized = result.cryptoObject?.signature
                    if (authorized == null || PhoneUnlockProtocol.hasExpired(request)) {
                        sendAndFinish(PhoneUnlockProtocol.expiredResponse(request))
                        return
                    }
                    authorized.update(PhoneUnlockProtocol.canonicalPayload(request))
                    sendAndFinish(PhoneUnlockProtocol.approvedResponse(request, loadPhoneId(), authorized.sign()))
                }

                override fun onAuthenticationError(errorCode: Int, errString: CharSequence) {
                    if (!responseSent) {
                        sendAndFinish(PhoneUnlockProtocol.deniedResponse(request, "BIOMETRIC_CANCELLED"))
                    }
                }

                override fun onAuthenticationFailed() {
                    statusText.text = "생체인증에 실패했습니다. 다시 시도하세요."
                }
            },
        )
        val info = BiometricPrompt.PromptInfo.Builder()
            .setTitle(getString(R.string.biometric_title))
            .setSubtitle("${request.computerName} 로그인을 승인합니다")
            .setNegativeButtonText(getString(R.string.biometric_cancel))
            .setAllowedAuthenticators(BiometricManager.Authenticators.BIOMETRIC_STRONG)
            .build()
        statusText.text = "생체인증을 기다리고 있습니다…"
        prompt.authenticate(info, BiometricPrompt.CryptoObject(signature))
    }

    private fun sendAndFinish(response: String) {
        if (responseSent) return
        responseSent = true
        ConnectionService.sendResponse(this, response)
        finishAndRemoveTask()
    }

    private fun loadPhoneId(): String =
        getSharedPreferences("phone_unlock", MODE_PRIVATE).getString("phone_id", null)
            ?: error("phoneId is not initialized.")
}
