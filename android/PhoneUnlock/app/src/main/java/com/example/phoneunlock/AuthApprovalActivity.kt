package com.example.phoneunlock

import android.app.NotificationManager
import android.os.Bundle
import android.os.CountDownTimer
import android.security.keystore.KeyPermanentlyInvalidatedException
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.biometric.BiometricManager
import androidx.biometric.BiometricPrompt
import androidx.core.content.ContextCompat
import com.example.phoneunlock.network.ConnectionService
import com.example.phoneunlock.storage.SecurePairingStore
import com.google.android.material.button.MaterialButton
import java.security.Signature
import java.time.Instant

class AuthApprovalActivity : AppCompatActivity() {
    private lateinit var request: AuthRequest
    private lateinit var pairedComputer: com.example.phoneunlock.storage.PairedComputer
    private lateinit var statusText: TextView
    private lateinit var countdownText: TextView
    private lateinit var approveButton: MaterialButton
    private lateinit var denyButton: MaterialButton
    private lateinit var signer: KeystoreSigner
    private var countdown: CountDownTimer? = null
    private var responseSent = false
    private var authenticationStarted = false
    private var notificationId = 0

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_auth_approval)
        statusText = findViewById(R.id.authStatusText)
        countdownText = findViewById(R.id.authCountdownText)
        approveButton = findViewById(R.id.approveButton)
        denyButton = findViewById(R.id.denyButton)
        signer = KeystoreSigner(this)
        notificationId = intent.getIntExtra(ConnectionService.EXTRA_AUTH_NOTIFICATION_ID, 0)

        request = try {
            PhoneUnlockProtocol.parseAuthRequest(
                intent.getStringExtra(ConnectionService.EXTRA_AUTH_REQUEST).orEmpty(),
            ).also { parsed ->
                pairedComputer = SecurePairingStore(this).loadAll()
                    .firstOrNull { it.computerId == parsed.computerId }
                    ?: error("등록되지 않은 PC의 요청입니다.")
            }
        } catch (exception: Exception) {
            statusText.text = exception.message ?: "요청이 올바르지 않습니다."
            approveButton.isEnabled = false
            denyButton.isEnabled = false
            return
        }

        findViewById<TextView>(R.id.authComputerNameText).text =
            "${request.computerName}에서 로그인을 요청했습니다."
        approveButton.setOnClickListener { authenticate() }
        denyButton.setOnClickListener {
            sendAndFinish(PhoneUnlockProtocol.deniedResponse(request, "USER_DENIED"))
        }
        startCountdown()

        if (savedInstanceState == null) {
            statusText.text = "인증 창을 여는 중…"
            window.decorView.post { authenticate() }
        }
    }

    override fun onDestroy() {
        countdown?.cancel()
        countdown = null
        super.onDestroy()
    }

    private fun startCountdown() {
        val remainingMillis = ((request.expiresAt - Instant.now().epochSecond) * 1_000L)
            .coerceAtLeast(0L)
        countdown?.cancel()
        countdown = object : CountDownTimer(remainingMillis, 250L) {
            override fun onTick(millisUntilFinished: Long) {
                val seconds = ((millisUntilFinished + 999L) / 1_000L).coerceAtLeast(1L)
                countdownText.text = "${seconds}초 안에 승인하세요"
                if (seconds <= 10L) {
                    countdownText.setTextColor(ContextCompat.getColor(
                        this@AuthApprovalActivity,
                        R.color.brand_error,
                    ))
                }
            }

            override fun onFinish() {
                if (responseSent) return
                approveButton.isEnabled = false
                denyButton.isEnabled = false
                countdownText.text = "요청이 만료되었습니다"
                statusText.text = "PC에서 다시 요청하세요."
                sendAndFinish(PhoneUnlockProtocol.expiredResponse(request))
            }
        }.start()
    }

    private fun authenticate() {
        if (authenticationStarted || responseSent) return
        authenticationStarted = true
        if (PhoneUnlockProtocol.hasExpired(request)) {
            sendAndFinish(PhoneUnlockProtocol.expiredResponse(request))
            return
        }

        val allowDeviceCredential = AuthPromptSettings.isDeviceCredentialEnabled(this)
        val weakFaceCompatibility = AuthPromptSettings.isWeakFaceEnabled(this)
        val currentAuthMode = AuthPromptSettings.currentAuthMode(this)
        if ((pairedComputer.authMode.isBlank() && currentAuthMode != "biometric") ||
            (pairedComputer.authMode.isNotBlank() && pairedComputer.authMode != currentAuthMode)
        ) {
            statusText.text = "인증 방식이 바뀌었습니다. 이 PC를 휴대폰 앱에서 다시 연결하세요."
            authenticationStarted = false
            return
        }
        val biometricAuthenticator = if (weakFaceCompatibility) {
            BiometricManager.Authenticators.BIOMETRIC_WEAK
        } else {
            BiometricManager.Authenticators.BIOMETRIC_STRONG
        }
        val allowedAuthenticators = biometricAuthenticator or
            if (allowDeviceCredential) BiometricManager.Authenticators.DEVICE_CREDENTIAL else 0
        if (BiometricManager.from(this).canAuthenticate(allowedAuthenticators)
            != BiometricManager.BIOMETRIC_SUCCESS
        ) {
            statusText.text = if (weakFaceCompatibility) {
                if (allowDeviceCredential) "얼굴인식 호환 모드 또는 휴대폰 PIN을 사용할 수 없습니다."
                else "얼굴인식 호환 모드를 사용할 수 없습니다."
            } else if (allowDeviceCredential) {
                "강한 생체인증 또는 휴대폰 PIN을 사용할 수 없습니다."
            } else {
                "강한 생체인증을 사용할 수 없습니다."
            }
            authenticationStarted = false
            return
        }

        val material = if (weakFaceCompatibility) {
            signer.getOrCreateCompatibility(request.computerId)
        } else {
            signer.getOrCreate(request.computerId, allowDeviceCredential)
        }
        if (pairedComputer.publicKey.isNotBlank() && pairedComputer.publicKey != material.publicKeyBase64) {
            statusText.text = "인증 방식이 바뀌어 PC 보안 키가 변경되었습니다. 이 PC를 다시 연결하세요."
            authenticationStarted = false
            return
        }
        val signature = if (weakFaceCompatibility) {
            null
        } else {
            try {
                signer.createSignature(material.privateKey)
            } catch (_: KeyPermanentlyInvalidatedException) {
                signer.delete(request.computerId)
                statusText.text = "생체정보 변경으로 키가 무효화되었습니다. PC와 다시 연결하세요."
                authenticationStarted = false
                return
            }
        }
        val prompt = BiometricPrompt(
            this,
            ContextCompat.getMainExecutor(this),
            object : BiometricPrompt.AuthenticationCallback() {
                override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) {
                    if (weakFaceCompatibility) {
                        val authorized = try {
                            signer.createSignature(material.privateKey)
                        } catch (_: Exception) {
                            statusText.text = "얼굴인식은 성공했지만 서명 키를 사용할 수 없습니다. PC를 다시 연결하세요."
                            authenticationStarted = false
                            return
                        }
                        sendApproved(authorized)
                    } else {
                        val authorized = result.cryptoObject?.signature
                        if (authorized == null) {
                            sendAndFinish(PhoneUnlockProtocol.expiredResponse(request))
                            return
                        }
                        sendApproved(authorized)
                    }
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
        val promptBuilder = BiometricPrompt.PromptInfo.Builder()
            .setTitle(getString(R.string.biometric_title))
            .setSubtitle("${request.computerName} 로그인을 승인합니다")
            .setAllowedAuthenticators(allowedAuthenticators)
        if (!allowDeviceCredential) {
            promptBuilder.setNegativeButtonText(getString(R.string.biometric_cancel))
        }
        val info = promptBuilder.build()
        statusText.text = if (weakFaceCompatibility && allowDeviceCredential) {
            "지문·얼굴인식 호환 모드 또는 휴대폰 PIN을 기다리고 있습니다…"
        } else if (weakFaceCompatibility) {
            "지문 또는 얼굴인식 호환 모드를 기다리고 있습니다…"
        } else if (allowDeviceCredential) {
            "지문·강한 얼굴인식 또는 휴대폰 PIN을 기다리고 있습니다…"
        } else {
            "지문 또는 강한 얼굴인식을 기다리고 있습니다…"
        }
        if (weakFaceCompatibility) {
            prompt.authenticate(info)
        } else {
            prompt.authenticate(info, BiometricPrompt.CryptoObject(signature!!))
        }
    }

    private fun sendApproved(signature: Signature) {
        if (PhoneUnlockProtocol.hasExpired(request)) {
            sendAndFinish(PhoneUnlockProtocol.expiredResponse(request))
            return
        }
        try {
            signature.update(PhoneUnlockProtocol.canonicalPayload(request))
            sendAndFinish(PhoneUnlockProtocol.approvedResponse(request, loadPhoneId(), signature.sign()))
        } catch (_: Exception) {
            statusText.text = "인증은 성공했지만 서명을 만들지 못했습니다. PC를 다시 연결하세요."
            authenticationStarted = false
        }
    }

    private fun sendAndFinish(response: String) {
        if (responseSent) return
        responseSent = true
        countdown?.cancel()
        if (notificationId != 0) {
            getSystemService(NotificationManager::class.java).cancel(notificationId)
        }
        ConnectionService.sendResponse(this, response)
        finishAndRemoveTask()
    }

    private fun loadPhoneId(): String =
        getSharedPreferences("phone_unlock", MODE_PRIVATE).getString("phone_id", null)
            ?: error("phoneId is not initialized.")
}
