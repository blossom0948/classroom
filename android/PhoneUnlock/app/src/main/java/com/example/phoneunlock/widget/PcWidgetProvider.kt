package com.example.phoneunlock.widget

import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.widget.RemoteViews
import com.example.phoneunlock.MainActivity
import com.example.phoneunlock.PhoneUnlockProtocol
import com.example.phoneunlock.R
import com.example.phoneunlock.network.ConnectionService
import com.example.phoneunlock.storage.SecurePairingStore
import java.util.UUID

class PcWidgetProvider : AppWidgetProvider() {
    override fun onUpdate(
        context: Context,
        appWidgetManager: AppWidgetManager,
        appWidgetIds: IntArray,
    ) {
        appWidgetIds.forEach { appWidgetId ->
            updateAppWidget(context, appWidgetManager, appWidgetId)
        }
    }

    companion object {
        private const val LOCK_PENDING_INTENT_REQUEST_CODE = 41_001
        private const val UNLOCK_PENDING_INTENT_REQUEST_CODE = 41_002

        fun refresh(context: Context) {
            val manager = AppWidgetManager.getInstance(context)
            val component = ComponentName(context, PcWidgetProvider::class.java)
            val ids = manager.getAppWidgetIds(component)
            ids.forEach { updateAppWidget(context, manager, it) }
        }

        private fun updateAppWidget(
            context: Context,
            manager: AppWidgetManager,
            appWidgetId: Int,
        ) {
            val computer = SecurePairingStore(context).load()
            val views = RemoteViews(context.packageName, R.layout.widget_pc_controls)
            if (computer == null) {
                views.setTextViewText(R.id.widgetTitle, "Phone Unlock")
                views.setTextViewText(R.id.widgetStatus, "PC 연결 필요")
            } else {
                views.setTextViewText(R.id.widgetTitle, computer.computerName)
                views.setTextViewText(
                    R.id.widgetStatus,
                    if (ConnectionService.isConnected(computer.computerId)) "온라인" else "오프라인 · 연결 대기",
                )
            }

            val lockIntent = Intent(context, WidgetActionReceiver::class.java)
                .setAction(WidgetActionReceiver.ACTION_LOCK)
            val lockPendingIntent = PendingIntent.getBroadcast(
                context,
                LOCK_PENDING_INTENT_REQUEST_CODE,
                lockIntent,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
            )
            views.setOnClickPendingIntent(R.id.widgetLockButton, lockPendingIntent)

            val unlockIntent = Intent(context, MainActivity::class.java)
                .setAction(MainActivity.ACTION_WIDGET_UNLOCK)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
            val unlockPendingIntent = PendingIntent.getActivity(
                context,
                UNLOCK_PENDING_INTENT_REQUEST_CODE,
                unlockIntent,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
            )
            views.setOnClickPendingIntent(R.id.widgetUnlockButton, unlockPendingIntent)
            manager.updateAppWidget(appWidgetId, views)
        }
    }
}

class WidgetActionReceiver : android.content.BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent?) {
        if (intent?.action != ACTION_LOCK) return

        val computer = SecurePairingStore(context).load() ?: return
        val request = com.example.phoneunlock.RemoteLockRequest(
            requestId = UUID.randomUUID(),
            computerId = computer.computerId,
            expiresAt = java.time.Instant.now().epochSecond + 30,
            phoneId = computer.phoneId,
        )
        ConnectionService.sendRemoteLockRequest(
            context,
            PhoneUnlockProtocol.remoteLockResponse(request),
        )
        PcWidgetProvider.refresh(context)
    }

    companion object {
        const val ACTION_LOCK = "com.example.phoneunlock.WIDGET_LOCK"
    }
}
