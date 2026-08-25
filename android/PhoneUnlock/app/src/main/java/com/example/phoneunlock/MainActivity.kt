package com.example.phoneunlock

import android.Manifest
import android.app.NotificationManager
import android.content.ActivityNotFoundException
import android.content.ClipboardManager
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.view.View
import android.widget.LinearLayout
import android.widget.RadioGroup
import android.widget.TextView
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.biometric.BiometricManager
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import com.example.phoneunlock.network.ConnectionService
import com.example.phoneunlock.network.PairingClient
import com.example.phoneunlock.storage.PairedComputer
import com.example.phoneunlock.storage.PairingPayload
import com.example.phoneunlock.storage.SecurePairingStore
import com.google.android.material.button.MaterialButton
import com.google.android.material.materialswitch.MaterialSwitch
import com.google.android.material.radiobutton.MaterialRadioButton
import com.google.android.material.textfield.TextInputEditText
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import kotlinx.coroutines.launch
import org.json.JSONException
import java.util.UUID

class MainActivity : AppCompatActivity() {
    private lateinit var computerNameText: TextView
    private lateinit var networkStatusText: TextView
    private lateinit var resultText: TextView
    private lateinit var pairingControls: LinearLayout
    private lateinit var manualCodeGroup: LinearLayout
    private lateinit var pairingInput: TextInputEditText
    private lateinit var scanQrButton: MaterialButton
    private lateinit var pasteCodeButton: MaterialButton
    private lateinit var pairButton: MaterialButton
    private lateinit var disconnectButton: MaterialButton
    private lateinit var computerListGroup: RadioGroup
    private lateinit var updateButton: MaterialButton
    private lateinit var autoPromptSwitch: MaterialSwitch
    private lateinit var autoPromptStatusText: TextView
    private lateinit var fullScreenPermissionButton: MaterialButton
    private lateinit var deviceCredentialSwitch: MaterialSwitch
    private lateinit var weakFaceSwitch: MaterialSwitch
    private lateinit var authMethodStatusText: TextView
    private lateinit var diagnosticsButton: MaterialButton
    private lateinit var diagnosticsText: TextView
    private lateinit var notificationSettingsButton: MaterialButton
    private lateinit var batterySettingsButton: MaterialButton
    private lateinit var keystoreSigner: KeystoreSigner
    private lateinit var pairingStore: SecurePairingStore
    private val pairingClient = PairingClient()
    private val releaseUpdateChecker = ReleaseUpdateChecker()
    private lateinit var phoneId: String
    private var availableUpdate: AndroidRelease? = null

    private val qrScanner = registerForActivityResult(ScanContract()) { result ->
        val code = result.contents
        if (code.isNullOrBlank()) {
            showResult("QR 스캔을 취소했습니다.", success = false)
        } else {
            connectWithCode(code)
        }
    }

    private val notificationPermissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
            if (!granted) {
                showResult("로그인 요청을 받으려면 Android 설정에서 Phone Unlock 알림을 허용하세요.", success = false)
            }
        }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        computerNameText = findViewById(R.id.computerNameText)
        networkStatusText = findViewById(R.id.networkStatusText)
        resultText = findViewById(R.id.resultText)
        pairingControls = findViewById(R.id.pairingControls)
        manualCodeGroup = findViewById(R.id.manualCodeGroup)
        pairingInput = findViewById(R.id.pairingInput)
        scanQrButton = findViewById(R.id.scanQrButton)
        pasteCodeButton = findViewById(R.id.pasteCodeButton)
        pairButton = findViewById(R.id.pairButton)
        disconnectButton = findViewById(R.id.disconnectButton)
        computerListGroup = findViewById(R.id.computerListGroup)
        updateButton = findViewById(R.id.updateButton)
        autoPromptSwitch = findViewById(R.id.autoPromptSwitch)
        autoPromptStatusText = findViewById(R.id.autoPromptStatusText)
        fullScreenPermissionButton = findViewById(R.id.fullScreenPermissionButton)
        deviceCredentialSwitch = findViewById(R.id.deviceCredentialSwitch)
        weakFaceSwitch = findViewById(R.id.weakFaceSwitch)
        authMethodStatusText = findViewById(R.id.authMethodStatusText)
        diagnosticsButton = findViewById(R.id.diagnosticsButton)
        diagnosticsText = findViewById(R.id.diagnosticsText)
        notificationSettingsButton = findViewById(R.id.notificationSettingsButton)
        batterySettingsButton = findViewById(R.id.batterySettingsButton)
        keystoreSigner = KeystoreSigner(this)
        pairingStore = SecurePairingStore(this)
        phoneId = loadOrCreatePhoneId()

        scanQrButton.setOnClickListener { scanPairingQr() }
        pasteCodeButton.setOnClickListener { connectFromClipboard() }
        pairButton.setOnClickListener { connectWithCode(pairingInput.text?.toString().orEmpty()) }
        disconnectButton.setOnClickListener { disconnectComputer() }
        updateButton.setOnClickListener { handleUpdateClick() }
        autoPromptSwitch.isChecked = AuthPromptSettings.isAutoOpenEnabled(this)
        autoPromptSwitch.setOnCheckedChangeListener { _, enabled ->
            AuthPromptSettings.setAutoOpenEnabled(this, enabled)
            updateAutoPromptControls()
        }
        deviceCredentialSwitch.isChecked = AuthPromptSettings.isDeviceCredentialEnabled(this)
        deviceCredentialSwitch.setOnCheckedChangeListener { _, enabled ->
            AuthPromptSettings.setDeviceCredentialEnabled(this, enabled)
            updateAuthMethodControls()
            showResult(
                if (enabled) {
                    "지문·강한 얼굴인식·휴대폰 PIN을 허용했습니다. 이미 연결한 PC는 이 PC 연결을 한 번 다시 해 주세요."
                } else {
                    "강한 생체인증만 허용하도록 바꿨습니다. 이미 연결한 PC는 이 PC 연결을 한 번 다시 해 주세요."
                },
                success = true,
            )
        }
        weakFaceSwitch.isChecked = AuthPromptSettings.isWeakFaceEnabled(this)
        weakFaceSwitch.setOnCheckedChangeListener { _, enabled ->
            AuthPromptSettings.setWeakFaceEnabled(this, enabled)
            updateAuthMethodControls()
            showResult(
                if (enabled) {
                    "약한 얼굴인식 호환 모드입니다. 보안 수준이 낮아지며, 이미 연결한 PC는 이 PC 연결을 한 번 다시 해 주세요."
                } else {
                    "강한 생체인증 모드로 돌아왔습니다. 이미 연결한 PC는 이 PC 연결을 한 번 다시 해 주세요."
                },
                success = true,
            )
        }
        diagnosticsButton.setOnClickListener { runDiagnostics() }
        notificationSettingsButton.setOnClickListener { openNotificationSettings() }
        batterySettingsButton.setOnClickListener { openBatterySettings() }
        fullScreenPermissionButton.setOnClickListener {
            val permissionIntent = AuthPromptSettings.permissionIntent(this)
                ?: return@setOnClickListener
            try {
                startActivity(permissionIntent)
            } catch (_: ActivityNotFoundException) {
                showResult("이 기기에서는 자동 팝업 권한 설정 화면을 열 수 없습니다.", success = false)
            }
        }
        updateAutoPromptControls()
        updateAuthMethodControls()

        pairingStore.load()?.let {
            displayPairedComputer(it)
            requestNotificationPermission()
            ConnectionService.connect(this)
        } ?: displayNoPairedComputer()

        checkForUpdate(silent = true)
    }

    override fun onResume() {
        super.onResume()
        if (::autoPromptSwitch.isInitialized) {
            updateAutoPromptControls()
        }
    }

    private fun updateAutoPromptControls() {
        val enabled = AuthPromptSettings.isAutoOpenEnabled(this)
        val permissionGranted = AuthPromptSettings.canUseFullScreenIntent(this)
        val methods = authenticationMethodsText()
        autoPromptStatusText.text = when {
            !enabled -> "알림만 표시합니다. 알림을 누르면 $methods 인증창이 열립니다."
            !permissionGranted -> "Android 설정에서 전체 화면 알림을 허용하면 잠금화면 위로 $methods 인증창을 바로 열 수 있습니다."
            else -> "Windows 요청이 오면 $methods 인증창을 바로 엽니다."
        }
        fullScreenPermissionButton.visibility =
            if (enabled && !permissionGranted) View.VISIBLE else View.GONE
    }

    private fun updateAuthMethodControls() {
        val enabled = AuthPromptSettings.isDeviceCredentialEnabled(this)
        val weakFace = AuthPromptSettings.isWeakFaceEnabled(this)
        authMethodStatusText.text = if (weakFace) {
            "제조사가 약한 얼굴인식으로 분류한 얼굴도 사용할 수 있습니다. 대신 생체인식과 서명 키가 암호학적으로 연결되지 않아 보안 수준이 낮습니다. 집에서만 사용하고, 연결한 PC는 다시 연결하세요."
        } else if (enabled) {
            "지문·강한 얼굴인식 또는 휴대폰 PIN/패턴/비밀번호를 사용할 수 있습니다. PIN은 휴대폰 밖으로 전송되지 않습니다."
        } else {
            "강한 지문 또는 강한 얼굴인식만 사용합니다. 제조사가 약한 얼굴인식으로 분류한 얼굴인식은 보안 키 인증에 사용할 수 없습니다."
        }
        updateAutoPromptControls()
    }

    private fun authenticationMethodsText(): String = if (AuthPromptSettings.isDeviceCredentialEnabled(this)) {
        if (AuthPromptSettings.isWeakFaceEnabled(this)) "지문·얼굴인식·휴대폰 PIN" else "지문·강한 얼굴인식·휴대폰 PIN"
    } else {
        if (AuthPromptSettings.isWeakFaceEnabled(this)) "지문·얼굴인식" else "지문·강한 얼굴인식"
    }

    private fun handleUpdateClick() {
        val update = availableUpdate
        if (update == null) {
            checkForUpdate(silent = false)
            return
        }

        startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(update.downloadUrl)))
        showResult("APK 다운로드가 열렸습니다. Android 설치 확인만 한 번 눌러 주세요.", success = true)
    }

    private fun checkForUpdate(silent: Boolean) {
        updateButton.isEnabled = false
        updateButton.text = "업데이트 확인 중…"
        lifecycleScope.launch {
            try {
                availableUpdate = releaseUpdateChecker.findUpdate(BuildConfig.VERSION_NAME)
                val update = availableUpdate
                updateButton.text = if (update == null) {
                    "버전 ${BuildConfig.VERSION_NAME} · 최신"
                } else {
                    "새 버전 ${update.tag} 받기"
                }
                if (!silent) {
                    showResult(
                        if (update == null) "현재 최신 버전입니다." else "${update.tag}을 바로 설치할 수 있습니다.",
                        success = true
                    )
                }
            } catch (exception: Exception) {
                updateButton.text = "업데이트 다시 확인"
                if (!silent) {
                    showResult(exception.message ?: "업데이트 확인에 실패했습니다.", success = false)
                }
            } finally {
                updateButton.isEnabled = true
            }
        }
    }

    private fun scanPairingQr() {
        val options = ScanOptions()
            .setDesiredBarcodeFormats(ScanOptions.QR_CODE)
            .setPrompt("Windows에 표시된 Phone Unlock QR을 스캔하세요")
            .setBeepEnabled(false)
            .setOrientationLocked(false)
            .setBarcodeImageEnabled(false)
        qrScanner.launch(options)
    }

    private fun connectFromClipboard() {
        val clipboard = getSystemService(ClipboardManager::class.java)
        val code = clipboard.primaryClip
            ?.takeIf { it.itemCount > 0 }
            ?.getItemAt(0)
            ?.coerceToText(this)
            ?.toString()
            .orEmpty()
        if (code.contains("pairingToken") && code.contains("computerId")) {
            connectWithCode(code)
            return
        }

        manualCodeGroup.visibility = View.VISIBLE
        showResult("클립보드에서 연결 코드를 찾지 못했습니다. Windows에서 코드를 다시 복사해 붙여 넣으세요.", success = false)
        pairingInput.requestFocus()
    }

    private fun connectWithCode(code: String) {
        val biometricStatus = BiometricManager.from(this)
            .canAuthenticate(allowedAuthenticators())
        if (biometricStatus != BiometricManager.BIOMETRIC_SUCCESS) {
            showResult(
                if (AuthPromptSettings.isDeviceCredentialEnabled(this)) {
                    "먼저 Android 설정에서 지문·강한 얼굴인식 또는 휴대폰 PIN을 사용할 수 있게 설정하세요."
                } else {
                    "먼저 Android 설정에서 지문 또는 강한 얼굴인식을 등록하세요."
                },
                success = false,
            )
            return
        }

        val payload = try {
            PairingPayload.parse(code.trim())
        } catch (exception: Exception) {
            showRequestError(exception)
            return
        }

        setPairingButtonsEnabled(false)
        networkStatusText.text = "${payload.computerName}에 안전하게 연결하는 중…"
        showResult("PC 연결을 확인하고 있습니다…", success = true)
        lifecycleScope.launch {
            try {
                val key = if (AuthPromptSettings.isWeakFaceEnabled(this@MainActivity)) {
                    keystoreSigner.getOrCreateCompatibility(payload.computerId)
                } else {
                    keystoreSigner.getOrCreate(
                        payload.computerId,
                        AuthPromptSettings.isDeviceCredentialEnabled(this@MainActivity),
                    )
                }
                val paired = pairingClient.pair(payload, phoneId, key.publicKeyBase64).copy(
                    authMode = AuthPromptSettings.currentAuthMode(this@MainActivity),
                )
                pairingStore.save(paired, select = true)
                pairingInput.text?.clear()
                displayPairedComputer(paired)
                requestNotificationPermission()
                ConnectionService.connect(this@MainActivity)
                showResult("연결 완료. 이제 Windows 요청이 오면 휴대폰 인증창이 자동으로 열립니다.", success = true)
            } catch (exception: Exception) {
                networkStatusText.text = "연결 실패 · QR을 새로 만들어 다시 시도하세요."
                showRequestError(exception)
            } finally {
                setPairingButtonsEnabled(true)
            }
        }
    }

    private fun disconnectComputer() {
        val paired = pairingStore.load()
        if (paired == null) return
        ConnectionService.disconnect(this)
        pairingStore.remove(paired.computerId)
        keystoreSigner.delete(paired.computerId)
        pairingInput.text?.clear()
        val next = pairingStore.load()
        if (next == null) {
            displayNoPairedComputer()
            showResult("등록된 PC가 없습니다.", success = true)
        } else {
            displayPairedComputer(next)
            ConnectionService.connect(this)
            showResult("선택한 PC 연결을 삭제했습니다. 다른 등록 PC를 선택했습니다.", success = true)
        }
    }

    private fun displayPairedComputer(computer: PairedComputer) {
        computerNameText.text = computer.computerName
        networkStatusText.text = "● 연결 준비 완료 · 로그인 요청 대기 중"
        networkStatusText.setTextColor(Color.parseColor("#217346"))
        refreshComputerChoices(computer)
        pairingControls.visibility = View.VISIBLE
        disconnectButton.visibility = View.VISIBLE
    }

    private fun displayNoPairedComputer() {
        computerNameText.text = "연결된 PC 없음"
        networkStatusText.text = "Windows 앱에서 QR 코드를 만들어 주세요."
        networkStatusText.setTextColor(Color.parseColor("#737780"))
        pairingControls.visibility = View.VISIBLE
        computerListGroup.removeAllViews()
        manualCodeGroup.visibility = View.GONE
        disconnectButton.visibility = View.GONE
    }

    private fun refreshComputerChoices(selected: PairedComputer) {
        computerListGroup.removeAllViews()
        pairingStore.loadAll().forEach { computer ->
            val radio = MaterialRadioButton(this).apply {
                id = View.generateViewId()
                text = "${computer.computerName}\n${computer.host}"
                textSize = 14f
                isChecked = computer.computerId == selected.computerId
                setPadding(0, 8.dp, 0, 8.dp)
                setOnClickListener {
                    if (pairingStore.select(computer.computerId)) {
                        ConnectionService.disconnect(this@MainActivity)
                        ConnectionService.connect(this@MainActivity)
                        displayPairedComputer(computer)
                        showResult("${computer.computerName}을(를) 현재 로그인 PC로 선택했습니다.", success = true)
                    }
                }
            }
            computerListGroup.addView(radio)
        }
    }

    private fun setPairingButtonsEnabled(enabled: Boolean) {
        scanQrButton.isEnabled = enabled
        pasteCodeButton.isEnabled = enabled
        pairButton.isEnabled = enabled
    }

    private fun requestNotificationPermission() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            notificationPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
    }

    private fun runDiagnostics() {
        diagnosticsButton.isEnabled = false
        diagnosticsText.text = "연결·알림·배터리·인증 상태를 점검하는 중…"
        lifecycleScope.launch {
            try {
                val lines = mutableListOf<String>()
                val computer = pairingStore.load()
                if (computer == null) {
                    lines += "✕ PC 연결: 등록된 PC가 없습니다."
                } else {
                    val reachable = runCatching { pairingClient.checkHealth(computer) }.getOrDefault(false)
                    lines += if (reachable) {
                        "✓ PC 연결: ${computer.computerName} 응답 확인"
                    } else {
                        "✕ PC 연결: 응답 없음 · PC 서비스/방화벽/LAN을 확인하세요"
                    }
                }

                val notificationsEnabled = getSystemService(NotificationManager::class.java)
                    .areNotificationsEnabled()
                lines += if (notificationsEnabled) {
                    "✓ 알림: 허용됨"
                } else {
                    "✕ 알림: 차단됨 · 아래 알림 설정을 여세요"
                }

                val powerManager = getSystemService(PowerManager::class.java)
                val batteryExemption = powerManager.isIgnoringBatteryOptimizations(packageName)
                lines += if (batteryExemption) {
                    "✓ 배터리: 백그라운드 제한 없음"
                } else {
                    "△ 배터리: 최적화 예외 필요 · 아래 배터리 설정을 여세요"
                }

                val authStatus = BiometricManager.from(this@MainActivity)
                    .canAuthenticate(allowedAuthenticators())
                lines += if (authStatus == BiometricManager.BIOMETRIC_SUCCESS) {
                    "✓ 인증: ${authenticationMethodsText()} 준비됨"
                } else {
                    "✕ 인증: Android 설정에서 ${authenticationMethodsText()}을(를) 사용할 수 있게 하세요"
                }

                val fullScreenReady = !AuthPromptSettings.isAutoOpenEnabled(this@MainActivity) ||
                    AuthPromptSettings.canUseFullScreenIntent(this@MainActivity)
                lines += if (fullScreenReady) {
                    "✓ 잠금화면 팝업: 준비됨"
                } else {
                    "△ 잠금화면 팝업: 전체 화면 알림 허용 필요"
                }
                diagnosticsText.text = lines.joinToString("\n")
            } catch (exception: Exception) {
                diagnosticsText.text = "진단을 완료하지 못했습니다: ${exception.message ?: "Android 설정을 확인하세요."}"
            } finally {
                diagnosticsButton.isEnabled = true
            }
        }
    }

    private fun openNotificationSettings() {
        val intent = Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS)
            .putExtra(Settings.EXTRA_APP_PACKAGE, packageName)
        startActivity(intent)
    }

    private fun openBatterySettings() {
        val requestIntent = Intent(
            Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS,
            Uri.parse("package:$packageName"),
        )
        try {
            startActivity(requestIntent)
        } catch (_: ActivityNotFoundException) {
            startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS))
        }
    }

    private fun allowedAuthenticators(): Int =
        (if (AuthPromptSettings.isWeakFaceEnabled(this)) {
            BiometricManager.Authenticators.BIOMETRIC_WEAK
        } else {
            BiometricManager.Authenticators.BIOMETRIC_STRONG
        }) or
            if (AuthPromptSettings.isDeviceCredentialEnabled(this)) {
                BiometricManager.Authenticators.DEVICE_CREDENTIAL
            } else {
                0
            }

    private fun showRequestError(exception: Exception) {
        val message = when (exception) {
            is JSONException, is IllegalArgumentException -> exception.message ?: "연결 코드가 올바르지 않습니다."
            else -> exception.message ?: "연결 중 오류가 발생했습니다."
        }
        showResult(message, success = false)
    }

    private fun showResult(message: String, success: Boolean) {
        resultText.text = message
        resultText.setTextColor(Color.parseColor(if (success) "#24503A" else "#8A2424"))
        resultText.setBackgroundColor(Color.parseColor(if (success) "#EAF7EF" else "#FFF0F0"))
        resultText.setPadding(16.dp, 14.dp, 16.dp, 14.dp)
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
