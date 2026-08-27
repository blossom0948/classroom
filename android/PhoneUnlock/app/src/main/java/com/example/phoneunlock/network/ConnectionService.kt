package com.example.phoneunlock.network

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import androidx.core.content.ContextCompat
import com.example.phoneunlock.AuthApprovalActivity
import com.example.phoneunlock.AuthPromptSettings
import com.example.phoneunlock.PhoneUnlockProtocol
import com.example.phoneunlock.R
import com.example.phoneunlock.storage.ActivityLogStore
import com.example.phoneunlock.storage.PairedComputer
import com.example.phoneunlock.storage.PcRuntimeState
import com.example.phoneunlock.storage.PcStateStore
import com.example.phoneunlock.storage.SecurePairingStore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.json.JSONObject
import java.time.Instant
import java.util.UUID
import kotlin.math.min

class ConnectionService : Service() {
    private lateinit var pairingStore: SecurePairingStore
    private lateinit var pcStateStore: PcStateStore
    private val pairingClient = PairingClient()
    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val handler = Handler(Looper.getMainLooper())
    private var webSocket: WebSocket? = null
    private var reconnectAttempt = 0
    private var hostIndex = 0
    private var stopping = false
    private var pendingMessage: String? = null
    private var beaconAdvertiser: BleBeaconAdvertiser? = null
    private val heartbeat = object : Runnable {
        override fun run() {
            val socket = webSocket
            if (socket?.send(deviceHeartbeat()) == true) {
                handler.postDelayed(this, HEARTBEAT_INTERVAL_MS)
            }
        }
    }

    override fun onCreate() {
        super.onCreate()
        pairingStore = SecurePairingStore(this)
        pcStateStore = PcStateStore(this)
        createNotificationChannels()
        startForeground(CONNECTION_NOTIFICATION_ID, connectionNotification("PC 연결을 준비하는 중…"))
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action ?: ACTION_CONNECT) {
            ACTION_CONNECT -> connect()
            ACTION_SEND_RESPONSE -> {
                val response = intent?.getStringExtra(EXTRA_RESPONSE)
                if (!response.isNullOrBlank()) {
                    sendOrQueue(response)
                }
            }
            ACTION_DENY_AUTH -> {
                val requestJson = intent?.getStringExtra(EXTRA_AUTH_REQUEST)
                val notificationId = intent?.getIntExtra(EXTRA_AUTH_NOTIFICATION_ID, 0) ?: 0
                val response = runCatching {
                    PhoneUnlockProtocol.deniedResponse(
                        PhoneUnlockProtocol.parseAuthRequest(requestJson.orEmpty()),
                        "NOTIFICATION_DENIED",
                    )
                }.getOrNull()
                if (response != null) sendOrQueue(response)
                if (notificationId != 0) {
                    getSystemService(NotificationManager::class.java).cancel(notificationId)
                }
            }
            ACTION_SEND_REMOTE_UNLOCK -> {
                val request = intent?.getStringExtra(EXTRA_REMOTE_UNLOCK)
                if (!request.isNullOrBlank()) {
                    sendOrQueue(request)
                }
            }
            ACTION_SEND_REMOTE_LOCK -> {
                val request = intent?.getStringExtra(EXTRA_REMOTE_LOCK)
                if (!request.isNullOrBlank()) {
                    sendOrQueue(request)
                }
            }
            ACTION_SEND_REMOTE_POWER -> {
                val request = intent?.getStringExtra(EXTRA_REMOTE_POWER)
                if (!request.isNullOrBlank()) {
                    sendOrQueue(request)
                }
            }
            ACTION_SEND_DECK_ACTION -> {
                val request = intent?.getStringExtra(EXTRA_DECK_ACTION)
                if (!request.isNullOrBlank()) {
                    sendOrQueue(request)
                }
            }
            ACTION_REFRESH_ROUTE -> pairingStore.load()?.let { refreshStoredRoute(it) }
            ACTION_DISCONNECT -> {
                stopping = true
                handler.removeCallbacksAndMessages(null)
                webSocket?.close(1000, "User disconnected")
                webSocket = null
                stopForeground(STOP_FOREGROUND_REMOVE)
                stopSelf()
            }
        }
        return START_STICKY
    }

    private fun connect() {
        val computer = pairingStore.load()
        if (computer == null) {
            updateConnectionNotification("연결된 PC가 없습니다")
            stopSelf()
            return
        }

        if (webSocket != null) {
            return
        }

        stopping = false
        val hosts = connectionHosts(computer)
        if (hosts.isEmpty()) {
            updateConnectionNotification("${computer.computerName}에 연결할 주소가 없습니다")
            stopSelf()
            return
        }
        val host = hosts[hostIndex % hosts.size]
        updateConnectionNotification("${computer.computerName}에 연결 중…")
        val client = PinnedHttpClient.create(computer.certificateFingerprint)
        val request = Request.Builder()
            .url("wss://$host:${computer.port}/ws?phoneId=${computer.phoneId}")
            .header("Authorization", "Bearer ${computer.deviceToken}")
            .build()
        webSocket = client.newWebSocket(request, listener(computer, host))
    }

    private fun listener(computer: PairedComputer, host: String) = object : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) {
            reconnectAttempt = 0
            hostIndex = connectionHosts(computer).indexOf(host).coerceAtLeast(0)
            updateConnectionNotification("${computer.computerName} · 연결됨")
            val hello = JSONObject()
                .put("version", 1)
                .put("type", "DEVICE_HELLO")
                .put("messageId", java.util.UUID.randomUUID().toString())
                .put("timestamp", Instant.now().epochSecond)
                .put("payload", JSONObject().put("phoneId", computer.phoneId))
            webSocket.send(hello.toString())
            startBleBeacon(computer.phoneId)
            activeComputerId = computer.computerId
            activeConnection = true
            pcStateStore.markOnline(computer.computerId, routeLabel(host))
            publishPcStateChanged(computer.computerId)
            refreshStoredRoute(computer)
            pendingMessage?.let { queued ->
                if (webSocket.send(queued)) {
                    pendingMessage = null
                }
            }
            handler.removeCallbacks(heartbeat)
            handler.postDelayed(heartbeat, HEARTBEAT_INTERVAL_MS)
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            try {
                val root = JSONObject(text)
                if (root.getInt("version") != 1) {
                    return
                }
                if (root.getString("type") == "SECURITY_ALERT") {
                    showSecurityAlertNotification(root.optJSONObject("payload")?.optString("message").orEmpty())
                    return
                }
                if (root.getString("type") == "SMART_ARRIVAL") {
                    showSmartArrivalNotification(
                        root.optJSONObject("payload")?.optString("computerName").orEmpty(),
                    )
                    return
                }
                if (root.getString("type") == "AUTOMATION_NOTICE") {
                    val payload = root.optJSONObject("payload")
                    showAutomationNotification(
                        payload?.optString("message").orEmpty(),
                        payload?.optString("source").orEmpty(),
                    )
                    return
                }
                if (root.getString("type") == "ACTION_RESULT") {
                    val payload = root.optJSONObject("payload")
                    val action = payload?.optString("action").orEmpty()
                    val success = payload?.optBoolean("success", false) == true
                    val message = payload?.optString("message").orEmpty()
                    ActivityLogStore(this@ConnectionService).append(
                        "PC ${actionLabel(action)} ${if (success) "완료" else "실패"}",
                        message.ifBlank { computer.computerName },
                    )
                    sendBroadcast(Intent(ACTION_REMOTE_ACTION_RESULT)
                        .setPackage(packageName)
                        .putExtra(EXTRA_ACTION, action)
                        .putExtra(EXTRA_ACTION_SUCCESS, success)
                        .putExtra(EXTRA_ACTION_MESSAGE, message))
                    return
                }
                if (root.getString("type") == "PC_STATE") {
                    val payload = root.optJSONObject("payload") ?: return
                    val computerId = runCatching { UUID.fromString(payload.optString("computerId")) }
                        .getOrNull() ?: return
                    if (computerId != computer.computerId) return
                    pcStateStore.save(computerId, PcRuntimeState(
                        powerState = payload.optString("powerState", "ON"),
                        sessionState = payload.optString("sessionState", "UNKNOWN"),
                        route = routeLabel(host),
                        observedAt = payload.optLong("observedAt", Instant.now().epochSecond),
                    ))
                    publishPcStateChanged(computerId)
                    return
                }
                if (root.getString("type") != "AUTH_REQUEST") return
                val request = PhoneUnlockProtocol.parseAuthRequest(text)
                showAuthenticationNotification(
                    text,
                    request.computerName,
                    request.requestId.hashCode(),
                    request.expiresAt,
                )
            } catch (_: Exception) {
                // Untrusted network messages are ignored without exposing details in notifications.
            }
        }

        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
            webSocket.close(code, reason)
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
            this@ConnectionService.webSocket = null
            stopBleBeacon()
            if (activeComputerId == computer.computerId) {
                activeConnection = false
            }
            pcStateStore.markOffline(computer.computerId)
            publishPcStateChanged(computer.computerId)
            advanceHost(computer)
            scheduleReconnect(computer)
        }

        override fun onFailure(webSocket: WebSocket, throwable: Throwable, response: Response?) {
            this@ConnectionService.webSocket = null
            stopBleBeacon()
            if (activeComputerId == computer.computerId) {
                activeConnection = false
            }
            pcStateStore.markOffline(computer.computerId)
            publishPcStateChanged(computer.computerId)
            advanceHost(computer)
            updateConnectionNotification("${computer.computerName} · 오프라인, 다른 연결 경로 재시도")
            scheduleReconnect(computer)
        }
    }

    private fun connectionHosts(computer: PairedComputer): List<String> =
        (listOf(computer.host) + computer.hosts)
            .map { it.trim() }
            .filter { it.isNotEmpty() }
            .distinct()

    private fun routeLabel(host: String): String = when {
        host.startsWith("100.") || host.startsWith("fd7a:", ignoreCase = true) -> "Tailscale"
        host.startsWith("10.") || host.startsWith("192.168.") ||
            Regex("^172\\.(1[6-9]|2[0-9]|3[01])\\.").containsMatchIn(host) -> "로컬 네트워크"
        else -> "사설 VPN"
    }

    private fun publishPcStateChanged(computerId: UUID) {
        sendBroadcast(Intent(ACTION_PC_STATE_CHANGED)
            .setPackage(packageName)
            .putExtra(EXTRA_COMPUTER_ID, computerId.toString()))
    }

    private fun advanceHost(computer: PairedComputer) {
        val count = connectionHosts(computer).size
        if (count > 0) {
            hostIndex = (hostIndex + 1) % count
        }
    }

    private fun scheduleReconnect(computer: PairedComputer) {
        if (stopping || pairingStore.load()?.computerId != computer.computerId) {
            return
        }

        reconnectAttempt++
        val delay = min(60_000L, 2_000L * (1L shl min(reconnectAttempt, 5)))
        handler.postDelayed({ connect() }, delay)
    }

    private fun showAuthenticationNotification(
        requestJson: String,
        computerName: String,
        requestCode: Int,
        expiresAt: Long,
    ) {
        val intent = authIntent(requestJson, requestCode)
        if (AuthPromptSettings.isAutoOpenEnabled(this)
            && AuthPromptSettings.canUseFullScreenIntent(this)) {
            try {
                startActivity(intent)
                return
            } catch (_: SecurityException) {
                // Android background-activity rules can reject this path. Use the notification fallback.
            } catch (_: ActivityNotFoundException) {
                // Use the notification fallback.
            }
        }

        val pendingIntent = PendingIntent.getActivity(
            this,
            requestCode,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val denyIntent = PendingIntent.getForegroundService(
            this,
            requestCode xor 0x40000000,
            Intent(this, ConnectionService::class.java)
                .setAction(ACTION_DENY_AUTH)
                .putExtra(EXTRA_AUTH_REQUEST, requestJson)
                .putExtra(EXTRA_AUTH_NOTIFICATION_ID, requestCode),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val notification = Notification.Builder(this, AUTH_CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_phone_unlock)
            .setContentTitle("Windows 로그인 요청")
            .setContentText("$computerName · 휴대폰 생체인식으로 승인")
            .setCategory(Notification.CATEGORY_REMINDER)
            .setPriority(Notification.PRIORITY_HIGH)
            .setAutoCancel(true)
            .setTimeoutAfter(((expiresAt - Instant.now().epochSecond).coerceAtLeast(1)) * 1_000L)
            .setContentIntent(pendingIntent)
            .addAction(R.drawable.ic_phone_unlock, "거절", denyIntent)
            .addAction(R.drawable.ic_phone_unlock, "생체인식", pendingIntent)
            .setVisibility(Notification.VISIBILITY_PUBLIC)
            .apply {
                if (AuthPromptSettings.isAutoOpenEnabled(this@ConnectionService)) {
                    setFullScreenIntent(pendingIntent, true)
                }
            }
            .build()
        getSystemService(NotificationManager::class.java).notify(requestCode, notification)
    }

    private fun refreshStoredRoute(computer: PairedComputer) {
        serviceScope.launch {
            runCatching { pairingClient.refreshConnectionInfo(computer) }
                .getOrNull()
                ?.let { refreshed ->
                    if (refreshed != computer) {
                        pairingStore.save(refreshed, select = false)
                    }
                }
        }
    }

    private fun showSecurityAlertNotification(message: String) {
        val notification = Notification.Builder(this, AUTH_CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_phone_unlock)
            .setContentTitle("의심스러운 연결 차단")
            .setContentText(message.ifBlank { "PC에서 의심스러운 요청을 차단했습니다." })
            .setCategory(Notification.CATEGORY_ERROR)
            .setPriority(Notification.PRIORITY_HIGH)
            .setAutoCancel(true)
            .setVisibility(Notification.VISIBILITY_PUBLIC)
            .build()
        getSystemService(NotificationManager::class.java).notify(SECURITY_NOTIFICATION_ID, notification)
    }

    private fun showSmartArrivalNotification(computerName: String) {
        val intent = Intent(this, com.example.phoneunlock.MainActivity::class.java)
            .setAction(com.example.phoneunlock.MainActivity.ACTION_SMART_ARRIVAL)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        val pendingIntent = PendingIntent.getActivity(
            this,
            SMART_ARRIVAL_NOTIFICATION_ID,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        if (AuthPromptSettings.isAutoOpenEnabled(this)
            && AuthPromptSettings.canUseFullScreenIntent(this)) {
            try {
                startActivity(intent)
                return
            } catch (_: SecurityException) {
                // Fall back to an actionable notification when Android blocks the background launch.
            } catch (_: ActivityNotFoundException) {
                // Fall back to the notification below.
            }
        }

        val notification = Notification.Builder(this, AUTH_CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_phone_unlock)
            .setContentTitle("${computerName}에 돌아왔습니다")
            .setContentText("생체인증으로 PC 잠금을 해제할 수 있습니다")
            .setCategory(Notification.CATEGORY_REMINDER)
            .setPriority(Notification.PRIORITY_HIGH)
            .setAutoCancel(true)
            .setContentIntent(pendingIntent)
            .addAction(R.drawable.ic_phone_unlock, "생체인식으로 해제", pendingIntent)
            .setVisibility(Notification.VISIBILITY_PUBLIC)
            .build()
        getSystemService(NotificationManager::class.java)
            .notify(SMART_ARRIVAL_NOTIFICATION_ID, notification)
    }

    private fun showAutomationNotification(message: String, source: String) {
        val title: String
        val fallbackMessage: String
        val notificationId: Int
        when (source) {
            "room_sensor" -> {
                title = "재실 센서로 PC 잠금 해제"
                fallbackMessage = "재실 센서 감지로 PC 잠금 해제가 완료되었습니다."
                notificationId = SENSOR_AUTOMATION_NOTIFICATION_ID
            }
            "phone_biometric" -> {
                title = "휴대폰 생체인식으로 PC 잠금 해제"
                fallbackMessage = "휴대폰 생체인식으로 PC 잠금 해제가 완료되었습니다."
                notificationId = BIOMETRIC_AUTOMATION_NOTIFICATION_ID
            }
            else -> {
                title = "인증된 휴대폰으로 PC 잠금 해제"
                fallbackMessage = "인증된 휴대폰 근접 감지로 PC 잠금 해제가 완료되었습니다."
                notificationId = AUTOMATION_NOTIFICATION_ID
            }
        }
        val notification = Notification.Builder(this, AUTOMATION_CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_phone_unlock)
            .setContentTitle(title)
            .setContentText(message.ifBlank { fallbackMessage })
            .setCategory(Notification.CATEGORY_STATUS)
            .setPriority(Notification.PRIORITY_DEFAULT)
            .setAutoCancel(true)
            .setVisibility(Notification.VISIBILITY_PRIVATE)
            .build()
        getSystemService(NotificationManager::class.java).notify(notificationId, notification)
    }

    private fun actionLabel(action: String): String = when (action.uppercase()) {
        "UNLOCK" -> "잠금 해제"
        "LOCK" -> "잠금"
        "SLEEP" -> "절전"
        "HIBERNATE" -> "최대 절전"
        "RESTART" -> "재시작"
        "SHUTDOWN" -> "종료"
        else -> "작업"
    }

    private fun authIntent(requestJson: String, requestCode: Int): Intent = Intent(this, AuthApprovalActivity::class.java)
            .putExtra(EXTRA_AUTH_REQUEST, requestJson)
            .putExtra(EXTRA_AUTH_NOTIFICATION_ID, requestCode)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)

    private fun sendOrQueue(message: String) {
        if (webSocket?.send(message) == true) {
            return
        }
        pendingMessage = message
        stopping = false
        connect()
    }

    private fun startBleBeacon(phoneId: String) {
        try {
            beaconAdvertiser = BleBeaconAdvertiser(this).also { it.start(phoneId) }
        } catch (_: Exception) {
            // Bluetooth RSSI is optional; the WebSocket heartbeat remains the source of truth.
            beaconAdvertiser = null
        }
    }

    private fun stopBleBeacon() {
        beaconAdvertiser?.stop()
        beaconAdvertiser = null
    }

    private fun connectionNotification(message: String): Notification =
        Notification.Builder(this, CONNECTION_CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_phone_unlock)
            .setContentTitle("Phone Unlock")
            .setContentText(message)
            .setCategory(Notification.CATEGORY_SERVICE)
            .setOngoing(true)
            .build()

    private fun updateConnectionNotification(message: String) {
        getSystemService(NotificationManager::class.java)
            .notify(CONNECTION_NOTIFICATION_ID, connectionNotification(message))
    }

    private fun createNotificationChannels() {
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(NotificationChannel(
            CONNECTION_CHANNEL_ID,
            "PC 연결 상태",
            NotificationManager.IMPORTANCE_LOW,
        ))
        manager.createNotificationChannel(NotificationChannel(
            AUTH_CHANNEL_ID,
            "Windows 로그인 요청",
            NotificationManager.IMPORTANCE_HIGH,
        ).apply {
            description = "휴대폰 생체인증이 필요한 Windows 로그인 요청"
            lockscreenVisibility = Notification.VISIBILITY_PUBLIC
        })
        manager.createNotificationChannel(NotificationChannel(
            AUTOMATION_CHANNEL_ID,
            "자동 잠금 해제 알림",
            NotificationManager.IMPORTANCE_DEFAULT,
        ).apply {
            description = "인증된 휴대폰 또는 재실 센서로 실행한 자동 잠금 해제 알림"
        })
    }

    override fun onDestroy() {
        stopping = true
        handler.removeCallbacksAndMessages(null)
        webSocket?.cancel()
        webSocket = null
        stopBleBeacon()
        activeConnection = false
        serviceScope.cancel()
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun deviceHeartbeat(): String = JSONObject()
        .put("version", PhoneUnlockProtocol.VERSION)
        .put("type", "DEVICE_HEARTBEAT")
        .put("messageId", java.util.UUID.randomUUID().toString())
        .put("timestamp", Instant.now().epochSecond)
        .put("payload", JSONObject().put("phoneId", pairingStore.load()?.phoneId))
        .toString()

    companion object {
        const val EXTRA_AUTH_REQUEST = "auth_request"
        const val EXTRA_AUTH_NOTIFICATION_ID = "auth_notification_id"
        private const val EXTRA_RESPONSE = "auth_response"
        private const val EXTRA_REMOTE_UNLOCK = "remote_unlock_request"
        private const val EXTRA_REMOTE_LOCK = "remote_lock_request"
        private const val EXTRA_REMOTE_POWER = "remote_power_request"
        private const val EXTRA_DECK_ACTION = "deck_action_request"
        private const val ACTION_CONNECT = "com.example.phoneunlock.CONNECT"
        private const val ACTION_SEND_RESPONSE = "com.example.phoneunlock.SEND_RESPONSE"
        private const val ACTION_DENY_AUTH = "com.example.phoneunlock.DENY_AUTH"
        private const val ACTION_SEND_REMOTE_UNLOCK = "com.example.phoneunlock.SEND_REMOTE_UNLOCK"
        private const val ACTION_SEND_REMOTE_LOCK = "com.example.phoneunlock.SEND_REMOTE_LOCK"
        private const val ACTION_SEND_REMOTE_POWER = "com.example.phoneunlock.SEND_REMOTE_POWER"
        private const val ACTION_SEND_DECK_ACTION = "com.example.phoneunlock.SEND_DECK_ACTION"
        private const val ACTION_REFRESH_ROUTE = "com.example.phoneunlock.REFRESH_ROUTE"
        private const val ACTION_DISCONNECT = "com.example.phoneunlock.DISCONNECT"
        const val ACTION_REMOTE_ACTION_RESULT = "com.example.phoneunlock.REMOTE_ACTION_RESULT"
        const val ACTION_PC_STATE_CHANGED = "com.example.phoneunlock.PC_STATE_CHANGED"
        const val EXTRA_COMPUTER_ID = "computer_id"
        const val EXTRA_ACTION = "remote_action"
        const val EXTRA_ACTION_SUCCESS = "remote_action_success"
        const val EXTRA_ACTION_MESSAGE = "remote_action_message"
        private const val CONNECTION_CHANNEL_ID = "phone_unlock_connection"
        private const val AUTH_CHANNEL_ID = "phone_unlock_auth"
        private const val AUTOMATION_CHANNEL_ID = "phone_unlock_automation"
        private const val CONNECTION_NOTIFICATION_ID = 48231
        private const val SECURITY_NOTIFICATION_ID = 48232
        private const val AUTOMATION_NOTIFICATION_ID = 48233
        private const val SMART_ARRIVAL_NOTIFICATION_ID = 48234
        private const val SENSOR_AUTOMATION_NOTIFICATION_ID = 48235
        private const val BIOMETRIC_AUTOMATION_NOTIFICATION_ID = 48236
        private const val HEARTBEAT_INTERVAL_MS = 10_000L
        @Volatile private var activeComputerId: UUID? = null
        @Volatile private var activeConnection: Boolean = false

        fun isConnected(computerId: UUID): Boolean = activeConnection && activeComputerId == computerId

        fun connect(context: Context) {
            ContextCompat.startForegroundService(
                context,
                Intent(context, ConnectionService::class.java).setAction(ACTION_CONNECT),
            )
        }

        fun sendResponse(context: Context, response: String) {
            context.startService(
                Intent(context, ConnectionService::class.java)
                    .setAction(ACTION_SEND_RESPONSE)
                    .putExtra(EXTRA_RESPONSE, response),
            )
        }

        fun sendRemoteUnlockRequest(context: Context, request: String) {
            ContextCompat.startForegroundService(
                context,
                Intent(context, ConnectionService::class.java)
                    .setAction(ACTION_SEND_REMOTE_UNLOCK)
                    .putExtra(EXTRA_REMOTE_UNLOCK, request),
            )
        }

        fun sendRemoteLockRequest(context: Context, request: String) {
            ContextCompat.startForegroundService(
                context,
                Intent(context, ConnectionService::class.java)
                    .setAction(ACTION_SEND_REMOTE_LOCK)
                    .putExtra(EXTRA_REMOTE_LOCK, request),
            )
        }

        fun sendRemotePowerRequest(context: Context, request: String) {
            ContextCompat.startForegroundService(
                context,
                Intent(context, ConnectionService::class.java)
                    .setAction(ACTION_SEND_REMOTE_POWER)
                    .putExtra(EXTRA_REMOTE_POWER, request),
            )
        }

        fun sendDeckAction(context: Context, request: String) {
            ContextCompat.startForegroundService(
                context,
                Intent(context, ConnectionService::class.java)
                    .setAction(ACTION_SEND_DECK_ACTION)
                    .putExtra(EXTRA_DECK_ACTION, request),
            )
        }

        fun refreshConnectionRoute(context: Context) {
            ContextCompat.startForegroundService(
                context,
                Intent(context, ConnectionService::class.java).setAction(ACTION_REFRESH_ROUTE),
            )
        }

        fun disconnect(context: Context) {
            context.startService(
                Intent(context, ConnectionService::class.java).setAction(ACTION_DISCONNECT),
            )
        }
    }
}
