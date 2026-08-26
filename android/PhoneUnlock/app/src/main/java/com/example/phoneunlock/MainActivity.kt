package com.example.phoneunlock

import android.Manifest
import android.app.NotificationManager
import android.app.Dialog
import android.content.BroadcastReceiver
import android.content.ActivityNotFoundException
import android.content.ClipboardManager
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.drawable.ColorDrawable
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import android.security.keystore.KeyPermanentlyInvalidatedException
import android.util.Base64
import android.view.View
import android.view.ViewGroup
import android.view.Window
import android.widget.LinearLayout
import android.widget.RadioGroup
import android.widget.TextView
import androidx.activity.OnBackPressedCallback
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.biometric.BiometricManager
import androidx.biometric.BiometricPrompt
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import com.example.phoneunlock.network.ConnectionService
import com.example.phoneunlock.network.PairingClient
import com.example.phoneunlock.network.WakeOnLanSender
import com.example.phoneunlock.storage.ActivityLogStore
import com.example.phoneunlock.storage.PairedComputer
import com.example.phoneunlock.storage.PairingPayload
import com.example.phoneunlock.storage.SecurePairingStore
import com.example.phoneunlock.widget.PcWidgetProvider
import com.example.phoneunlock.widget.WidgetAppearanceSettings
import com.google.android.material.button.MaterialButton
import com.google.android.material.button.MaterialButtonToggleGroup
import com.google.android.material.materialswitch.MaterialSwitch
import com.google.android.material.bottomnavigation.BottomNavigationView
import com.google.android.material.radiobutton.MaterialRadioButton
import com.google.android.material.textfield.TextInputEditText
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import kotlinx.coroutines.launch
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONException
import java.security.SecureRandom
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale
import java.util.UUID

class MainActivity : AppCompatActivity() {
    private lateinit var homePanel: LinearLayout
    private lateinit var settingsPanel: LinearLayout
    private lateinit var homeControlsCard: View
    private lateinit var historyPanel: LinearLayout
    private lateinit var historyList: LinearLayout
    private lateinit var historyEmptyText: TextView
    private lateinit var bottomNavigation: BottomNavigationView
    private lateinit var quickPairButton: MaterialButton
    private lateinit var settingsBackButton: MaterialButton
    private lateinit var settingsTitleText: TextView
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
    private lateinit var remoteRouteButton: MaterialButton
    private lateinit var diagnosticsText: TextView
    private lateinit var remoteUnlockButton: MaterialButton
    private lateinit var remoteLockButton: MaterialButton
    private lateinit var sleepButton: MaterialButton
    private lateinit var hibernateButton: MaterialButton
    private lateinit var restartButton: MaterialButton
    private lateinit var shutdownButton: MaterialButton
    private lateinit var wakeButton: MaterialButton
    private lateinit var themeToggleGroup: MaterialButtonToggleGroup
    private lateinit var widgetThemeButton: MaterialButton
    private lateinit var widgetTransparencySwitch: MaterialSwitch
    private lateinit var keystoreSigner: KeystoreSigner
    private lateinit var pairingStore: SecurePairingStore
    private lateinit var activityLogStore: ActivityLogStore
    private val pairingClient = PairingClient()
    private val releaseUpdateChecker = ReleaseUpdateChecker()
    private lateinit var phoneId: String
    private var availableUpdate: AndroidRelease? = null
    private var changingTab = false
    private var updatingAppearance = false
    private val remoteResultReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: android.content.Context, intent: Intent) {
            if (intent.action != ConnectionService.ACTION_REMOTE_ACTION_RESULT) return
            val action = intent.getStringExtra(ConnectionService.EXTRA_ACTION).orEmpty()
            val success = intent.getBooleanExtra(ConnectionService.EXTRA_ACTION_SUCCESS, false)
            val message = intent.getStringExtra(ConnectionService.EXTRA_ACTION_MESSAGE).orEmpty()
            showResult(
                message.ifBlank { "PC ${remoteActionLabel(action)} ${if (success) "완료" else "실패"}" },
                success,
            )
            PcWidgetProvider.refresh(this@MainActivity)
        }
    }

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

    private val bluetoothPermissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { result ->
            if (result.values.any { !it }) {
                showResult("Bluetooth RSSI 거리 측정을 사용하려면 Bluetooth 권한을 허용하세요.", success = false)
            }
        }

    override fun onCreate(savedInstanceState: Bundle?) {
        AppearanceSettings.apply(this)
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        homePanel = findViewById(R.id.homePanel)
        settingsPanel = findViewById(R.id.settingsPanel)
        homeControlsCard = findViewById(R.id.homeControlsCard)
        historyPanel = findViewById(R.id.historyPanel)
        historyList = findViewById(R.id.historyList)
        historyEmptyText = findViewById(R.id.historyEmptyText)
        bottomNavigation = findViewById(R.id.bottomNavigation)
        quickPairButton = findViewById(R.id.quickPairButton)
        settingsBackButton = findViewById(R.id.settingsBackButton)
        settingsTitleText = findViewById(R.id.settingsTitleText)
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
        remoteRouteButton = findViewById(R.id.remoteRouteButton)
        diagnosticsText = findViewById(R.id.diagnosticsText)
        remoteUnlockButton = findViewById(R.id.remoteUnlockButton)
        remoteLockButton = findViewById(R.id.remoteLockButton)
        sleepButton = findViewById(R.id.sleepButton)
        hibernateButton = findViewById(R.id.hibernateButton)
        restartButton = findViewById(R.id.restartButton)
        shutdownButton = findViewById(R.id.shutdownButton)
        wakeButton = findViewById(R.id.wakeButton)
        themeToggleGroup = findViewById(R.id.themeToggleGroup)
        widgetThemeButton = findViewById(R.id.widgetThemeButton)
        widgetTransparencySwitch = findViewById(R.id.widgetTransparencySwitch)
        keystoreSigner = KeystoreSigner(this)
        pairingStore = SecurePairingStore(this)
        activityLogStore = ActivityLogStore(this)
        phoneId = loadOrCreatePhoneId()

        scanQrButton.setOnClickListener { scanPairingQr() }
        quickPairButton.setOnClickListener { scanPairingQr() }
        pasteCodeButton.setOnClickListener { connectFromClipboard() }
        pairButton.setOnClickListener { connectWithCode(pairingInput.text?.toString().orEmpty()) }
        disconnectButton.setOnClickListener { disconnectComputer() }
        settingsBackButton.setOnClickListener { showHome() }
        remoteUnlockButton.setOnClickListener { requestRemoteUnlock() }
        remoteLockButton.setOnClickListener { requestRemoteLock() }
        sleepButton.setOnClickListener { requestRemotePower("SLEEP") }
        hibernateButton.setOnClickListener { requestRemotePower("HIBERNATE") }
        restartButton.setOnClickListener { requestRemotePower("RESTART") }
        shutdownButton.setOnClickListener { requestRemotePower("SHUTDOWN") }
        wakeButton.setOnClickListener { requestWakeComputer() }
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
        remoteRouteButton.setOnClickListener { openRemoteConnectionSetup() }
        themeToggleGroup.addOnButtonCheckedListener { _, checkedId, isChecked ->
            if (!isChecked || updatingAppearance) return@addOnButtonCheckedListener
            val mode = when (checkedId) {
                R.id.themeLightButton -> AppearanceSettings.LIGHT
                R.id.themeDarkButton -> AppearanceSettings.DARK
                else -> AppearanceSettings.SYSTEM
            }
            if (AppearanceSettings.current(this) != mode) {
                AppearanceSettings.set(this, mode)
            }
        }
        widgetThemeButton.setOnClickListener {
            WidgetAppearanceSettings.nextTheme(this)
            updateAppearanceControls()
            PcWidgetProvider.refresh(this)
        }
        widgetTransparencySwitch.setOnCheckedChangeListener { _, enabled ->
            if (!updatingAppearance) {
                WidgetAppearanceSettings.setTransparent(this, enabled)
                PcWidgetProvider.refresh(this)
            }
        }
        bottomNavigation.setOnItemSelectedListener { item ->
            if (changingTab) {
                true
            } else {
                when (item.itemId) {
                    R.id.nav_pc -> {
                        showHome()
                        true
                    }
                    R.id.nav_automation -> {
                        pairingStore.load()?.let { showSettings(it) } ?: showHome()
                        true
                    }
                    R.id.nav_history -> {
                        showHistory()
                        true
                    }
                    else -> false
                }
            }
        }
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                when {
                    settingsPanel.visibility == View.VISIBLE || historyPanel.visibility == View.VISIBLE -> showHome()
                    manualCodeGroup.visibility == View.VISIBLE -> manualCodeGroup.visibility = View.GONE
                    else -> finish()
                }
            }
        })
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
        updateAppearanceControls()

        pairingStore.load()?.let {
            displayPairedComputer(it)
            requestNotificationPermission()
            requestBluetoothPermission()
            ConnectionService.connect(this)
            showHome()
        } ?: displayNoPairedComputer()

        checkForUpdate(silent = true)
        if (savedInstanceState == null) {
            handleLaunchAction(intent)
        }
    }

    override fun onNewIntent(intent: Intent?) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleLaunchAction(intent)
    }

    override fun onResume() {
        super.onResume()
        if (::autoPromptSwitch.isInitialized) {
            updateAutoPromptControls()
            pairingStore.load()?.let { refreshComputerChoices(it) }
            pairingStore.load()?.let { ConnectionService.refreshConnectionRoute(this) }
            PcWidgetProvider.refresh(this)
        }
    }

    override fun onStart() {
        super.onStart()
        ContextCompat.registerReceiver(
            this,
            remoteResultReceiver,
            IntentFilter(ConnectionService.ACTION_REMOTE_ACTION_RESULT),
            ContextCompat.RECEIVER_NOT_EXPORTED,
        )
    }

    override fun onStop() {
        unregisterReceiver(remoteResultReceiver)
        super.onStop()
    }

    private fun updateAutoPromptControls() {
        val enabled = AuthPromptSettings.isAutoOpenEnabled(this)
        val permissionGranted = AuthPromptSettings.canUseFullScreenIntent(this)
        val methods = authenticationMethodsText()
        autoPromptStatusText.text = when {
            !enabled -> "알림을 눌러 인증합니다"
            !permissionGranted -> "전체 화면 알림 권한이 필요합니다"
            else -> "$methods 인증창을 바로 표시합니다"
        }
        fullScreenPermissionButton.visibility =
            if (enabled && !permissionGranted) View.VISIBLE else View.GONE
    }

    private fun updateAuthMethodControls() {
        val enabled = AuthPromptSettings.isDeviceCredentialEnabled(this)
        val weakFace = AuthPromptSettings.isWeakFaceEnabled(this)
        authMethodStatusText.text = if (weakFace) {
            "지문·얼굴인식·${if (enabled) "PIN" else "강한 생체인식"}"
        } else if (enabled) {
            "지문·강한 얼굴인식·PIN"
        } else {
            "지문·강한 얼굴인식"
        }
        updateAutoPromptControls()
    }

    private fun updateAppearanceControls() {
        updatingAppearance = true
        val checkedId = when (AppearanceSettings.current(this)) {
            AppearanceSettings.LIGHT -> R.id.themeLightButton
            AppearanceSettings.DARK -> R.id.themeDarkButton
            else -> R.id.themeSystemButton
        }
        themeToggleGroup.check(checkedId)
        widgetThemeButton.text = "위젯 · ${WidgetAppearanceSettings.label(this)}"
        widgetTransparencySwitch.isChecked = WidgetAppearanceSettings.isTransparent(this)
        updatingAppearance = false
    }

    private fun handleLaunchAction(launchIntent: Intent?) {
        val action = launchIntent?.action ?: return
        window.decorView.post {
            when (action) {
                ACTION_WIDGET_UNLOCK, ACTION_SMART_ARRIVAL -> requestRemoteUnlock()
                ACTION_WIDGET_POWER -> launchIntent.getStringExtra(EXTRA_WIDGET_POWER_COMMAND)
                    ?.takeIf { it in setOf("SLEEP", "HIBERNATE", "RESTART", "SHUTDOWN") }
                    ?.let(::requestRemotePower)
            }
        }
    }

    private fun openRemoteConnectionSetup() {
        if (pairingStore.load() == null) {
            showResult("먼저 PC를 연결하세요", success = false)
            return
        }

        val tailscaleIntent = packageManager.getLaunchIntentForPackage(TAILSCALE_PACKAGE)
            ?: Intent(Intent.ACTION_VIEW, Uri.parse("market://details?id=$TAILSCALE_PACKAGE"))
        try {
            startActivity(tailscaleIntent)
            showResult(
                "Tailscale에서 로그인과 VPN 허용을 한 번만 완료하세요. 이후 Phone Unlock이 LAN·원격 주소를 자동 갱신합니다.",
                success = true,
            )
        } catch (_: ActivityNotFoundException) {
            startActivity(Intent(Intent.ACTION_VIEW, Uri.parse("https://play.google.com/store/apps/details?id=$TAILSCALE_PACKAGE")))
            showResult("Tailscale 설치 화면을 열었습니다. 설치 후 한 번만 로그인하세요.", success = true)
        }
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
                activityLogStore.append("PC 연결", paired.computerName)
                PcWidgetProvider.refresh(this@MainActivity)
                pairingInput.text?.clear()
                displayPairedComputer(paired)
                showHome()
                requestNotificationPermission()
                ConnectionService.connect(this@MainActivity)
                showResult("연결 완료", success = true)
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
        activityLogStore.append("PC 연결 해제", paired.computerName)
        pairingStore.remove(paired.computerId)
        keystoreSigner.delete(paired.computerId)
        PcWidgetProvider.refresh(this)
        pairingInput.text?.clear()
        val next = pairingStore.load()
        if (next == null) {
            displayNoPairedComputer()
            showResult("등록된 PC가 없습니다.", success = true)
        } else {
            displayPairedComputer(next)
            showHome()
            ConnectionService.connect(this)
            showResult("선택한 PC 연결을 삭제했습니다. 다른 등록 PC를 선택했습니다.", success = true)
        }
    }

    private fun displayPairedComputer(computer: PairedComputer) {
        computerNameText.text = computer.computerName
        settingsTitleText.text = computer.computerName
        val online = ConnectionService.isConnected(computer.computerId)
        networkStatusText.text = if (online) "● 온라인" else "○ 오프라인 · 연결 대기 중"
        networkStatusText.setTextColor(ContextCompat.getColor(
            this,
            if (online) R.color.brand_success else R.color.brand_muted,
        ))
        refreshComputerChoices(computer)
        homeControlsCard.visibility = View.VISIBLE
        pairingControls.visibility = View.VISIBLE
        disconnectButton.visibility = View.VISIBLE
    }

    private fun displayNoPairedComputer() {
        networkStatusText.text = "등록된 PC가 없습니다"
        networkStatusText.setTextColor(ContextCompat.getColor(this, R.color.brand_muted))
        homeControlsCard.visibility = View.GONE
        pairingControls.visibility = View.VISIBLE
        computerListGroup.removeAllViews()
        manualCodeGroup.visibility = View.GONE
        disconnectButton.visibility = View.GONE
        showHome()
    }

    private fun showHome() {
        showPanel(homePanel, R.id.nav_pc)
        pairingStore.load()?.let { refreshComputerChoices(it) }
    }

    private fun showSettings(computer: PairedComputer) {
        showPanel(settingsPanel, R.id.nav_automation)
        computerNameText.text = computer.computerName
        settingsTitleText.text = computer.computerName
        updateAutoPromptControls()
        updateAuthMethodControls()
    }

    private fun showHistory() {
        showPanel(historyPanel, R.id.nav_history)
        historyList.removeAllViews()
        val events = activityLogStore.load()
        if (events.isEmpty()) {
            historyList.addView(historyEmptyText)
            return
        }

        events.forEachIndexed { index, event ->
            val row = TextView(this).apply {
                val time = HISTORY_TIME_FORMATTER.format(event.occurredAt)
                text = "$time\n${event.title} · ${event.detail}"
                setTextColor(ContextCompat.getColor(this@MainActivity, R.color.brand_on_surface))
                textSize = 14f
                setPadding(0, 12.dp, 0, 12.dp)
            }
            historyList.addView(row)
            if (index < events.lastIndex) {
                val divider = View(this).apply {
                    setBackgroundColor(ContextCompat.getColor(this@MainActivity, R.color.brand_stroke))
                    layoutParams = LinearLayout.LayoutParams(
                        LinearLayout.LayoutParams.MATCH_PARENT,
                        1.dp,
                    )
                }
                historyList.addView(divider)
            }
        }
    }

    private fun showPanel(panel: View, tabId: Int) {
        homePanel.visibility = if (panel === homePanel) View.VISIBLE else View.GONE
        settingsPanel.visibility = if (panel === settingsPanel) View.VISIBLE else View.GONE
        historyPanel.visibility = if (panel === historyPanel) View.VISIBLE else View.GONE
        selectTab(tabId)
        panel.alpha = 0f
        panel.translationY = 16.dp.toFloat()
        panel.animate()
            .alpha(1f)
            .translationY(0f)
            .setDuration(180L)
            .start()
    }

    private fun selectTab(tabId: Int) {
        if (bottomNavigation.selectedItemId == tabId) return
        changingTab = true
        bottomNavigation.selectedItemId = tabId
        changingTab = false
    }

    private fun refreshComputerChoices(selected: PairedComputer) {
        computerListGroup.removeAllViews()
        pairingStore.loadAll().forEach { computer ->
            val radio = MaterialRadioButton(this).apply {
                id = View.generateViewId()
                val online = ConnectionService.isConnected(computer.computerId)
                text = "${computer.computerName}\n${if (online) "온라인" else "오프라인"} · LAN/VPN 주소 자동 선택"
                textSize = 14f
                isChecked = computer.computerId == selected.computerId
                setPadding(0, 8.dp, 0, 8.dp)
                setOnClickListener {
                    if (pairingStore.select(computer.computerId)) {
                        ConnectionService.disconnect(this@MainActivity)
                        ConnectionService.connect(this@MainActivity)
                        displayPairedComputer(computer)
                        showHome()
                        PcWidgetProvider.refresh(this@MainActivity)
                        showResult("${computer.computerName} 선택됨", success = true)
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

    private fun requestBluetoothPermission() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.S) return
        val permissions = arrayOf(
            Manifest.permission.BLUETOOTH_ADVERTISE,
            Manifest.permission.BLUETOOTH_CONNECT,
            Manifest.permission.BLUETOOTH_SCAN,
        )
        val missing = permissions.filter {
            ContextCompat.checkSelfPermission(this, it) != PackageManager.PERMISSION_GRANTED
        }
        if (missing.isNotEmpty()) {
            bluetoothPermissionLauncher.launch(missing.toTypedArray())
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
                        "✓ PC 연결: ${computer.computerName} 응답 확인 · 주소 후보 ${connectionHosts(computer).size}개"
                    } else {
                        "✕ PC 연결: 응답 없음 · 서비스/방화벽/LAN 또는 VPN을 확인하세요"
                    }
                    lines += if (computer.wakeOnLanTargets.isNotEmpty()) {
                        "✓ PC 켜기: Wake-on-LAN 정보 준비됨 · ${computer.wakeOnLanTargets.size}개 대상"
                    } else {
                        "△ PC 켜기: WOL 정보 없음 · PC에서 새 연결 QR을 다시 만드세요"
                    }
                }

                val connectivity = getSystemService(ConnectivityManager::class.java)
                val vpnActive = connectivity.activeNetwork?.let { network ->
                    connectivity.getNetworkCapabilities(network)
                        ?.hasTransport(NetworkCapabilities.TRANSPORT_VPN) == true
                } == true
                lines += if (vpnActive) {
                    "✓ 원격 경로: VPN 연결됨 · LAN/VPN 주소를 자동 갱신·재연결합니다"
                } else {
                    "△ 원격 경로: 원격 연결 켜기에서 VPN을 한 번만 설정하면 이후 자동 재연결합니다"
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
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                    val bluetoothReady = listOf(
                        Manifest.permission.BLUETOOTH_ADVERTISE,
                        Manifest.permission.BLUETOOTH_CONNECT,
                        Manifest.permission.BLUETOOTH_SCAN,
                    ).all {
                        ContextCompat.checkSelfPermission(this@MainActivity, it) == PackageManager.PERMISSION_GRANTED
                    }
                    lines += if (bluetoothReady) {
                        "✓ Bluetooth RSSI: 권한 준비됨"
                    } else {
                        "△ Bluetooth RSSI: Bluetooth 권한 필요"
                    }
                }
                diagnosticsText.text = lines.joinToString("\n")
            } catch (exception: Exception) {
                diagnosticsText.text = "진단을 완료하지 못했습니다: ${exception.message ?: "Android 설정을 확인하세요."}"
            } finally {
                diagnosticsButton.isEnabled = true
            }
        }
    }

    private fun requestRemoteUnlock() {
        val computer = pairingStore.load()
        if (computer == null) {
            showResult("먼저 PC를 연결하세요", success = false)
            return
        }
        showConfirmationDialog(
            title = "${computer.computerName} 잠금 해제",
            message = "PC를 잠금 해제하시겠습니까?",
            actionText = "생체인증",
        ) {
            authenticateRemoteUnlock()
        }
    }

    private fun authenticateRemoteUnlock() {
        val computer = pairingStore.load()
        if (computer == null) {
            showResult("먼저 PC를 연결하세요", success = false)
            return
        }

        val authStatus = BiometricManager.from(this).canAuthenticate(allowedAuthenticators())
        if (authStatus != BiometricManager.BIOMETRIC_SUCCESS) {
            showResult("휴대폰 생체인식을 설정하세요", success = false)
            return
        }

        val request = RemoteUnlockRequest(
            requestId = UUID.randomUUID(),
            computerId = computer.computerId,
            challenge = Base64.encodeToString(ByteArray(32).also { SecureRandom().nextBytes(it) }, Base64.NO_WRAP),
            expiresAt = Instant.now().epochSecond + 30,
            phoneId = computer.phoneId,
        )
        val weakFaceCompatibility = AuthPromptSettings.isWeakFaceEnabled(this)
        val allowDeviceCredential = AuthPromptSettings.isDeviceCredentialEnabled(this)
        val material = try {
            if (weakFaceCompatibility) {
                keystoreSigner.getOrCreateCompatibility(computer.computerId)
            } else {
                keystoreSigner.getOrCreate(computer.computerId, allowDeviceCredential)
            }
        } catch (exception: Exception) {
            showResult(exception.message ?: "보안 키를 준비하지 못했습니다", success = false)
            return
        }
        if (computer.publicKey.isNotBlank() && computer.publicKey != material.publicKeyBase64) {
            showResult("인증 방식이 바뀌었습니다. 이 PC를 다시 연결하세요", success = false)
            return
        }

        val signature = if (weakFaceCompatibility) {
            null
        } else {
            try {
                keystoreSigner.createSignature(material.privateKey)
            } catch (_: KeyPermanentlyInvalidatedException) {
                keystoreSigner.delete(computer.computerId)
                showResult("생체정보 변경으로 키가 무효화되었습니다. PC를 다시 연결하세요", success = false)
                return
            }
        }

        remoteUnlockButton.isEnabled = false
        val prompt = BiometricPrompt(
            this,
            ContextCompat.getMainExecutor(this),
            object : BiometricPrompt.AuthenticationCallback() {
                override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) {
                    val authorized = if (weakFaceCompatibility) {
                        runCatching { keystoreSigner.createSignature(material.privateKey) }.getOrNull()
                    } else {
                        result.cryptoObject?.signature
                    }
                    if (authorized == null) {
                        remoteUnlockButton.isEnabled = true
                        showResult("인증 서명을 만들지 못했습니다", success = false)
                        return
                    }
                    try {
                        authorized.update(PhoneUnlockProtocol.canonicalRemoteUnlockPayload(request))
                        ConnectionService.sendRemoteUnlockRequest(
                            this@MainActivity,
                            PhoneUnlockProtocol.remoteUnlockResponse(request, authorized.sign()),
                        )
                        activityLogStore.append("PC 잠금 해제", computer.computerName)
                        showResult("PC에 잠금 해제를 요청했습니다", success = true)
                    } catch (exception: Exception) {
                        showResult(exception.message ?: "잠금 해제 요청을 보내지 못했습니다", success = false)
                    } finally {
                        remoteUnlockButton.isEnabled = true
                    }
                }

                override fun onAuthenticationError(errorCode: Int, errString: CharSequence) {
                    remoteUnlockButton.isEnabled = true
                    showResult(errString.toString(), success = false)
                }

                override fun onAuthenticationFailed() {
                    showResult("생체인식에 실패했습니다. 다시 시도하세요", success = false)
                }
            },
        )
        val promptBuilder = BiometricPrompt.PromptInfo.Builder()
            .setTitle("${computer.computerName} 잠금 해제")
            .setSubtitle("휴대폰에서 인증하면 PC가 열립니다")
            .setAllowedAuthenticators(allowedAuthenticators())
        if (!allowDeviceCredential) {
            promptBuilder.setNegativeButtonText(getString(R.string.biometric_cancel))
        }
        if (weakFaceCompatibility) {
            prompt.authenticate(promptBuilder.build())
        } else {
            prompt.authenticate(promptBuilder.build(), BiometricPrompt.CryptoObject(signature!!))
        }
    }

    private fun requestRemoteLock() {
        val computer = pairingStore.load()
        if (computer == null) {
            showResult("먼저 PC를 연결하세요", success = false)
            return
        }
        showConfirmationDialog(
            title = "${computer.computerName} 잠금",
            message = "PC를 잠그시겠습니까?",
            actionText = "잠금",
        ) {
            sendRemoteLock()
        }
    }

    private fun sendRemoteLock() {
        val computer = pairingStore.load()
        if (computer == null) {
            showResult("먼저 PC를 연결하세요", success = false)
            return
        }

        val request = RemoteLockRequest(
            requestId = UUID.randomUUID(),
            computerId = computer.computerId,
            expiresAt = Instant.now().epochSecond + 30,
            phoneId = computer.phoneId,
        )
        remoteLockButton.isEnabled = false
        try {
            ConnectionService.sendRemoteLockRequest(
                this,
                PhoneUnlockProtocol.remoteLockResponse(request),
            )
            activityLogStore.append("PC 잠금", computer.computerName)
            showResult("PC에 잠금을 요청했습니다", success = true)
        } catch (exception: Exception) {
            showResult(exception.message ?: "잠금 요청을 보내지 못했습니다", success = false)
        } finally {
            remoteLockButton.isEnabled = true
        }
    }

    private fun requestRemotePower(command: String) {
        val computer = pairingStore.load()
        if (computer == null) {
            showResult("먼저 PC를 연결하세요", success = false)
            return
        }

        val title = when (command) {
            "SLEEP" -> "PC를 절전 모드로 전환할까요?"
            "HIBERNATE" -> "PC를 최대 절전 모드로 전환할까요?"
            "RESTART" -> "PC를 재시작할까요?"
            else -> "PC를 종료할까요?"
        }
        val message = if (command == "RESTART" || command == "SHUTDOWN") {
            "저장하지 않은 작업이 사라질 수 있습니다. 휴대폰 생체인증 후 명령을 보냅니다."
        } else {
            "휴대폰 생체인증 후 ${computer.computerName}에 명령을 보냅니다."
        }
        showConfirmationDialog(
            title = title,
            message = message,
            actionText = "생체인증",
        ) {
            authenticateAndSendRemotePower(computer, command)
        }
    }

    private fun requestWakeComputer() {
        val computer = pairingStore.load()
        if (computer == null) {
            showResult("먼저 PC를 연결하세요", success = false)
            return
        }
        showConfirmationDialog(
            title = "${computer.computerName} 켜기",
            message = "PC 켜기 신호를 보내시겠습니까?",
            actionText = "켜기",
        ) {
            wakeComputer()
        }
    }

    private fun wakeComputer() {
        val computer = pairingStore.load()
        if (computer == null) {
            showResult("먼저 PC를 연결하세요", success = false)
            return
        }
        if (computer.wakeOnLanTargets.isEmpty()) {
            showResult("이 PC의 Wake-on-LAN 정보가 없습니다. PC에서 새 연결 QR을 만든 뒤 다시 연결하세요.", success = false)
            return
        }

        wakeButton.isEnabled = false
        lifecycleScope.launch {
            try {
                val sent = withContext(Dispatchers.IO) {
                    WakeOnLanSender.send(computer.wakeOnLanTargets)
                }
                if (sent > 0) {
                    activityLogStore.append("PC 켜기 신호", computer.computerName)
                }
                showResult(
                    if (sent > 0) "PC 켜기 신호를 보냈습니다. WOL 설정이 켜져 있어야 합니다."
                    else "Wake-on-LAN 신호를 보낼 수 없습니다.",
                    success = sent > 0,
                )
            } catch (exception: Exception) {
                showResult(exception.message ?: "Wake-on-LAN 신호를 보내지 못했습니다.", success = false)
            } finally {
                wakeButton.isEnabled = true
            }
        }
    }

    private fun authenticateAndSendRemotePower(computer: PairedComputer, command: String) {
        val authStatus = BiometricManager.from(this).canAuthenticate(allowedAuthenticators())
        if (authStatus != BiometricManager.BIOMETRIC_SUCCESS) {
            showResult("휴대폰 생체인식을 설정하세요", success = false)
            return
        }

        val request = RemotePowerRequest(
            requestId = UUID.randomUUID(),
            computerId = computer.computerId,
            command = command,
            challenge = Base64.encodeToString(ByteArray(32).also { SecureRandom().nextBytes(it) }, Base64.NO_WRAP),
            expiresAt = Instant.now().epochSecond + 30,
            phoneId = computer.phoneId,
        )
        val weakFaceCompatibility = AuthPromptSettings.isWeakFaceEnabled(this)
        val allowDeviceCredential = AuthPromptSettings.isDeviceCredentialEnabled(this)
        val material = try {
            if (weakFaceCompatibility) {
                keystoreSigner.getOrCreateCompatibility(computer.computerId)
            } else {
                keystoreSigner.getOrCreate(computer.computerId, allowDeviceCredential)
            }
        } catch (exception: Exception) {
            showResult(exception.message ?: "보안 키를 준비하지 못했습니다", success = false)
            return
        }
        if (computer.publicKey.isNotBlank() && computer.publicKey != material.publicKeyBase64) {
            showResult("인증 방식이 바뀌었습니다. 이 PC를 다시 연결하세요", success = false)
            return
        }

        val signature = if (weakFaceCompatibility) {
            null
        } else {
            try {
                keystoreSigner.createSignature(material.privateKey)
            } catch (_: KeyPermanentlyInvalidatedException) {
                keystoreSigner.delete(computer.computerId)
                showResult("생체정보 변경으로 키가 무효화되었습니다. PC와 다시 연결하세요", success = false)
                return
            }
        }

        setPowerButtonsEnabled(false)
        val prompt = BiometricPrompt(
            this,
            ContextCompat.getMainExecutor(this),
            object : BiometricPrompt.AuthenticationCallback() {
                override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) {
                    val authorized = if (weakFaceCompatibility) {
                        runCatching { keystoreSigner.createSignature(material.privateKey) }.getOrNull()
                    } else {
                        result.cryptoObject?.signature
                    }
                    if (authorized == null) {
                        setPowerButtonsEnabled(true)
                        showResult("인증 서명을 만들지 못했습니다", success = false)
                        return
                    }
                    try {
                        authorized.update(PhoneUnlockProtocol.canonicalRemotePowerPayload(request))
                        ConnectionService.sendRemotePowerRequest(
                            this@MainActivity,
                            PhoneUnlockProtocol.remotePowerResponse(request, authorized.sign()),
                        )
                        activityLogStore.append("${commandLabel(command)} 요청", computer.computerName)
                        showResult("PC에 ${commandLabel(command)} 명령을 요청했습니다", success = true)
                    } catch (exception: Exception) {
                        showResult(exception.message ?: "원격 전원 요청을 보내지 못했습니다", success = false)
                    } finally {
                        setPowerButtonsEnabled(true)
                    }
                }

                override fun onAuthenticationError(errorCode: Int, errString: CharSequence) {
                    setPowerButtonsEnabled(true)
                    showResult(errString.toString(), success = false)
                }

                override fun onAuthenticationFailed() {
                    showResult("생체인식에 실패했습니다. 다시 시도하세요", success = false)
                }
            },
        )
        val promptBuilder = BiometricPrompt.PromptInfo.Builder()
            .setTitle("${computer.computerName} ${commandLabel(command)}")
            .setSubtitle("휴대폰에서 인증하면 PC에 명령을 보냅니다")
            .setAllowedAuthenticators(allowedAuthenticators())
        if (!allowDeviceCredential) {
            promptBuilder.setNegativeButtonText(getString(R.string.biometric_cancel))
        }
        if (weakFaceCompatibility) {
            prompt.authenticate(promptBuilder.build())
        } else {
            prompt.authenticate(promptBuilder.build(), BiometricPrompt.CryptoObject(signature!!))
        }
    }

    private fun setPowerButtonsEnabled(enabled: Boolean) {
        sleepButton.isEnabled = enabled
        hibernateButton.isEnabled = enabled
        restartButton.isEnabled = enabled
        shutdownButton.isEnabled = enabled
    }

    private fun commandLabel(command: String): String = when (command) {
        "SLEEP" -> "절전"
        "HIBERNATE" -> "최대 절전"
        "RESTART" -> "재시작"
        else -> "종료"
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

    private fun showConfirmationDialog(
        title: String,
        message: String,
        actionText: String,
        onConfirm: () -> Unit,
    ) {
        val dialog = Dialog(this)
        dialog.requestWindowFeature(Window.FEATURE_NO_TITLE)
        val content = layoutInflater.inflate(R.layout.dialog_confirmation, null)
        content.findViewById<TextView>(R.id.confirmationTitle).text = title
        content.findViewById<TextView>(R.id.confirmationMessage).text = message
        content.findViewById<MaterialButton>(R.id.confirmationActionButton).text = actionText
        content.findViewById<MaterialButton>(R.id.confirmationCancelButton).setOnClickListener { dialog.dismiss() }
        content.findViewById<MaterialButton>(R.id.confirmationActionButton).setOnClickListener {
            dialog.dismiss()
            onConfirm()
        }
        dialog.setContentView(content)
        dialog.setCanceledOnTouchOutside(true)
        dialog.setOnShowListener {
            content.alpha = 0f
            content.scaleX = 0.92f
            content.scaleY = 0.92f
            content.animate()
                .alpha(1f)
                .scaleX(1f)
                .scaleY(1f)
                .setDuration(115L)
                .start()
        }
        dialog.show()
        dialog.window?.apply {
            setBackgroundDrawable(ColorDrawable(Color.TRANSPARENT))
            setLayout(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
        }
    }

    private fun showResult(message: String, success: Boolean) {
        resultText.text = message
        resultText.setTextColor(ContextCompat.getColor(
            this,
            if (success) R.color.brand_success else R.color.brand_error,
        ))
        resultText.setBackgroundResource(
            if (success) R.drawable.result_success_background else R.drawable.result_error_background,
        )
        resultText.setPadding(16.dp, 14.dp, 16.dp, 14.dp)
        if (homePanel.visibility == View.VISIBLE) {
            networkStatusText.text = message
            networkStatusText.setTextColor(ContextCompat.getColor(
                this,
                if (success) R.color.brand_success else R.color.brand_error,
            ))
        }
    }

    private fun remoteActionLabel(action: String): String = when (action.uppercase()) {
        "UNLOCK" -> "잠금 해제"
        "LOCK" -> "잠금"
        "SLEEP" -> "절전"
        "HIBERNATE" -> "최대 절전"
        "RESTART" -> "재시작"
        "SHUTDOWN" -> "종료"
        else -> "작업"
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

    private fun connectionHosts(computer: PairedComputer): List<String> =
        (listOf(computer.host) + computer.hosts)
            .map { it.trim() }
            .filter { it.isNotEmpty() }
            .distinct()

    private val Int.dp: Int
        get() = (this * resources.displayMetrics.density).toInt()

    companion object {
        private val HISTORY_TIME_FORMATTER: DateTimeFormatter =
            DateTimeFormatter.ofPattern("MM.dd  HH:mm", Locale.KOREA)
                .withZone(ZoneId.systemDefault())
        const val ACTION_WIDGET_UNLOCK = "com.example.phoneunlock.WIDGET_UNLOCK"
        const val ACTION_SMART_ARRIVAL = "com.example.phoneunlock.SMART_ARRIVAL"
        const val ACTION_WIDGET_POWER = "com.example.phoneunlock.WIDGET_POWER"
        const val EXTRA_WIDGET_POWER_COMMAND = "widget_power_command"
        private const val TAILSCALE_PACKAGE = "com.tailscale.ipn"
    }
}
