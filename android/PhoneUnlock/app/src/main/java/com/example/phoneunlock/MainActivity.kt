package com.example.phoneunlock

import android.Manifest
import android.content.ClipboardManager
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.view.View
import android.widget.LinearLayout
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
    private lateinit var updateButton: MaterialButton
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
        updateButton = findViewById(R.id.updateButton)
        keystoreSigner = KeystoreSigner(this)
        pairingStore = SecurePairingStore(this)
        phoneId = loadOrCreatePhoneId()

        scanQrButton.setOnClickListener { scanPairingQr() }
        pasteCodeButton.setOnClickListener { connectFromClipboard() }
        pairButton.setOnClickListener { connectWithCode(pairingInput.text?.toString().orEmpty()) }
        disconnectButton.setOnClickListener { disconnectComputer() }
        updateButton.setOnClickListener { handleUpdateClick() }

        pairingStore.load()?.let {
            displayPairedComputer(it)
            requestNotificationPermission()
            ConnectionService.connect(this)
        } ?: displayNoPairedComputer()

        checkForUpdate(silent = true)
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
            .canAuthenticate(BiometricManager.Authenticators.BIOMETRIC_STRONG)
        if (biometricStatus != BiometricManager.BIOMETRIC_SUCCESS) {
            showResult("먼저 Android 설정에서 지문 또는 강한 생체인증을 등록하세요.", success = false)
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
                val key = keystoreSigner.getOrCreate(payload.computerId)
                val paired = pairingClient.pair(payload, phoneId, key.publicKeyBase64)
                val previous = pairingStore.load()
                pairingStore.save(paired)
                if (previous != null && previous.computerId != paired.computerId) {
                    keystoreSigner.delete(previous.computerId)
                }
                pairingInput.text?.clear()
                displayPairedComputer(paired)
                requestNotificationPermission()
                ConnectionService.connect(this@MainActivity)
                showResult("연결 완료. 이제 Windows가 잠기면 알림을 누르고 지문만 인증하세요.", success = true)
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
        ConnectionService.disconnect(this)
        pairingStore.clear()
        paired?.let { keystoreSigner.delete(it.computerId) }
        pairingInput.text?.clear()
        displayNoPairedComputer()
        showResult("이 PC와의 연결을 삭제했습니다.", success = true)
    }

    private fun displayPairedComputer(computer: PairedComputer) {
        computerNameText.text = computer.computerName
        networkStatusText.text = "● 연결 준비 완료 · 로그인 요청 대기 중"
        networkStatusText.setTextColor(Color.parseColor("#217346"))
        pairingControls.visibility = View.GONE
        disconnectButton.visibility = View.VISIBLE
    }

    private fun displayNoPairedComputer() {
        computerNameText.text = "연결된 PC 없음"
        networkStatusText.text = "Windows 앱에서 QR 코드를 만들어 주세요."
        networkStatusText.setTextColor(Color.parseColor("#737780"))
        pairingControls.visibility = View.VISIBLE
        manualCodeGroup.visibility = View.GONE
        disconnectButton.visibility = View.GONE
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
