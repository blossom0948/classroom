package com.example.phoneunlock.network

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
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
import com.example.phoneunlock.storage.PairedComputer
import com.example.phoneunlock.storage.SecurePairingStore
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.json.JSONObject
import java.time.Instant
import kotlin.math.min

class ConnectionService : Service() {
    private lateinit var pairingStore: SecurePairingStore
    private val handler = Handler(Looper.getMainLooper())
    private var webSocket: WebSocket? = null
    private var reconnectAttempt = 0
    private var stopping = false

    override fun onCreate() {
        super.onCreate()
        pairingStore = SecurePairingStore(this)
        createNotificationChannels()
        startForeground(CONNECTION_NOTIFICATION_ID, connectionNotification("PC 연결을 준비하는 중…"))
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action ?: ACTION_CONNECT) {
            ACTION_CONNECT -> connect()
            ACTION_SEND_RESPONSE -> {
                val response = intent?.getStringExtra(EXTRA_RESPONSE)
                if (!response.isNullOrBlank()) {
                    webSocket?.send(response)
                }
            }
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
        updateConnectionNotification("${computer.computerName}에 연결 중…")
        val client = PinnedHttpClient.create(computer.certificateFingerprint)
        val request = Request.Builder()
            .url("wss://${computer.host}:${computer.port}/ws?phoneId=${computer.phoneId}")
            .header("Authorization", "Bearer ${computer.deviceToken}")
            .build()
        webSocket = client.newWebSocket(request, listener(computer))
    }

    private fun listener(computer: PairedComputer) = object : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) {
            reconnectAttempt = 0
            updateConnectionNotification("${computer.computerName} · 연결됨")
            val hello = JSONObject()
                .put("version", 1)
                .put("type", "DEVICE_HELLO")
                .put("messageId", java.util.UUID.randomUUID().toString())
                .put("timestamp", Instant.now().epochSecond)
                .put("payload", JSONObject().put("phoneId", computer.phoneId))
            webSocket.send(hello.toString())
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            try {
                val root = JSONObject(text)
                if (root.getInt("version") != 1 || root.getString("type") != "AUTH_REQUEST") {
                    return
                }
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
            scheduleReconnect(computer)
        }

        override fun onFailure(webSocket: WebSocket, throwable: Throwable, response: Response?) {
            this@ConnectionService.webSocket = null
            updateConnectionNotification("${computer.computerName} · 오프라인, 재연결 대기")
            scheduleReconnect(computer)
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
        val intent = Intent(this, AuthApprovalActivity::class.java)
            .putExtra(EXTRA_AUTH_REQUEST, requestJson)
            .putExtra(EXTRA_AUTH_NOTIFICATION_ID, requestCode)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        val pendingIntent = PendingIntent.getActivity(
            this,
            requestCode,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val notification = Notification.Builder(this, AUTH_CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_phone_unlock)
            .setContentTitle("Windows 로그인 요청")
            .setContentText("$computerName 잠금 해제를 생체인증으로 승인")
            .setCategory(Notification.CATEGORY_REMINDER)
            .setPriority(Notification.PRIORITY_HIGH)
            .setAutoCancel(true)
            .setTimeoutAfter(((expiresAt - Instant.now().epochSecond).coerceAtLeast(1)) * 1_000L)
            .setContentIntent(pendingIntent)
            .addAction(R.drawable.ic_phone_unlock, "지문 인증", pendingIntent)
            .setVisibility(Notification.VISIBILITY_PUBLIC)
            .apply {
                if (AuthPromptSettings.isAutoOpenEnabled(this@ConnectionService)) {
                    setFullScreenIntent(pendingIntent, true)
                }
            }
            .build()
        getSystemService(NotificationManager::class.java).notify(requestCode, notification)
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
    }

    override fun onDestroy() {
        stopping = true
        handler.removeCallbacksAndMessages(null)
        webSocket?.cancel()
        webSocket = null
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    companion object {
        const val EXTRA_AUTH_REQUEST = "auth_request"
        const val EXTRA_AUTH_NOTIFICATION_ID = "auth_notification_id"
        private const val EXTRA_RESPONSE = "auth_response"
        private const val ACTION_CONNECT = "com.example.phoneunlock.CONNECT"
        private const val ACTION_SEND_RESPONSE = "com.example.phoneunlock.SEND_RESPONSE"
        private const val ACTION_DISCONNECT = "com.example.phoneunlock.DISCONNECT"
        private const val CONNECTION_CHANNEL_ID = "phone_unlock_connection"
        private const val AUTH_CHANNEL_ID = "phone_unlock_auth"
        private const val CONNECTION_NOTIFICATION_ID = 48231

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

        fun disconnect(context: Context) {
            context.startService(
                Intent(context, ConnectionService::class.java).setAction(ACTION_DISCONNECT),
            )
        }
    }
}
