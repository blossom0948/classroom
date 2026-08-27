package com.example.phoneunlock

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.ActivityInfo
import android.os.Bundle
import android.view.HapticFeedbackConstants
import android.view.View
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import com.example.phoneunlock.network.ConnectionService
import com.example.phoneunlock.storage.SecurePairingStore
import com.google.android.material.button.MaterialButton
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import com.google.android.material.snackbar.Snackbar

class StreamDeckActivity : AppCompatActivity() {
    private lateinit var root: View
    private lateinit var pairingStore: SecurePairingStore
    private val slots = mutableListOf<MaterialButton>()
    private val actionResults = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            if (intent.action != ConnectionService.ACTION_REMOTE_ACTION_RESULT) return
            val message = intent.getStringExtra(ConnectionService.EXTRA_ACTION_MESSAGE).orEmpty()
            val success = intent.getBooleanExtra(ConnectionService.EXTRA_ACTION_SUCCESS, false)
            Snackbar.make(root, message.ifBlank { if (success) "실행했습니다" else "실행하지 못했습니다" }, Snackbar.LENGTH_SHORT)
                .setBackgroundTint(ContextCompat.getColor(this@StreamDeckActivity,
                    if (success) R.color.brand_success else R.color.brand_error))
                .show()
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        AppearanceSettings.apply(this)
        super.onCreate(savedInstanceState)
        requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
        setContentView(R.layout.activity_stream_deck)
        root = findViewById(R.id.deckRoot)
        pairingStore = SecurePairingStore(this)
        findViewById<MaterialButton>(R.id.deckBackButton).setOnClickListener { finish() }
        findViewById<TextView>(R.id.deckComputerName).text =
            pairingStore.load()?.computerName ?: "연결된 PC 없음"

        listOf(
            R.id.deckSlot1, R.id.deckSlot2, R.id.deckSlot3, R.id.deckSlot4,
            R.id.deckSlot5, R.id.deckSlot6, R.id.deckSlot7, R.id.deckSlot8,
            R.id.deckSlot9, R.id.deckSlot10, R.id.deckSlot11, R.id.deckSlot12,
        ).forEachIndexed { index, id ->
            val button = findViewById<MaterialButton>(id)
            slots += button
            button.setOnClickListener {
                it.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                executeSlot(index)
            }
            button.setOnLongClickListener {
                showActionPicker(index)
                true
            }
        }
        refreshSlots()
    }

    override fun onStart() {
        super.onStart()
        ContextCompat.registerReceiver(this, actionResults,
            IntentFilter(ConnectionService.ACTION_REMOTE_ACTION_RESULT),
            ContextCompat.RECEIVER_NOT_EXPORTED)
    }

    override fun onStop() {
        unregisterReceiver(actionResults)
        super.onStop()
    }

    private fun executeSlot(index: Int) {
        val computer = pairingStore.load()
        if (computer == null) {
            Snackbar.make(root, "먼저 PC를 연결하세요", Snackbar.LENGTH_SHORT).show()
            return
        }
        if (!ConnectionService.isConnected(computer.computerId)) {
            Snackbar.make(root, "PC가 오프라인입니다", Snackbar.LENGTH_SHORT).show()
            return
        }
        val action = actionFor(index)
        ConnectionService.sendDeckAction(this,
            PhoneUnlockProtocol.deckAction(computer.computerId, computer.phoneId, action.id))
        Snackbar.make(root, "${action.label} 실행 중…", Snackbar.LENGTH_SHORT).show()
    }

    private fun showActionPicker(index: Int) {
        val labels = Actions.map { "${it.icon}  ${it.label}" }.toTypedArray()
        MaterialAlertDialogBuilder(this)
            .setTitle("버튼 ${index + 1} 동작")
            .setSingleChoiceItems(labels, Actions.indexOf(actionFor(index))) { dialog, selected ->
                preferences().edit().putString("slot_$index", Actions[selected].id).apply()
                refreshSlots()
                dialog.dismiss()
            }
            .setNegativeButton("취소", null)
            .show()
    }

    private fun refreshSlots() {
        slots.forEachIndexed { index, button ->
            val action = actionFor(index)
            button.text = "${action.icon}\n${action.label}"
            button.contentDescription = "${action.label}. 길게 눌러 변경"
        }
    }

    private fun actionFor(index: Int): DeckAction {
        val fallback = Actions[index % Actions.size]
        val stored = preferences().getString("slot_$index", fallback.id)
        return Actions.firstOrNull { it.id == stored } ?: fallback
    }

    private fun preferences() = getSharedPreferences("phone_unlock_deck", MODE_PRIVATE)

    private data class DeckAction(val id: String, val label: String, val icon: String)

    private companion object {
        val Actions = listOf(
            DeckAction("MEDIA_PLAY_PAUSE", "재생 / 일시정지", "▶"),
            DeckAction("MEDIA_PREVIOUS", "이전 곡", "◀"),
            DeckAction("MEDIA_NEXT", "다음 곡", "▶▶"),
            DeckAction("VOLUME_DOWN", "볼륨 낮춤", "−"),
            DeckAction("VOLUME_UP", "볼륨 높임", "+"),
            DeckAction("VOLUME_MUTE", "음소거", "◩"),
            DeckAction("OPEN_BROWSER", "브라우저", "◎"),
            DeckAction("OPEN_EXPLORER", "파일 탐색기", "▰"),
            DeckAction("OPEN_SPOTIFY", "Spotify", "●"),
            DeckAction("OPEN_STEAM", "Steam", "◉"),
            DeckAction("SHOW_DESKTOP", "바탕 화면", "▣"),
            DeckAction("SCREENSHOT", "화면 캡처", "⌗"),
        )
    }
}
