package com.example.phoneunlock.network

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import com.example.phoneunlock.storage.SecurePairingStore

class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent?) {
        if (intent?.action == Intent.ACTION_BOOT_COMPLETED && SecurePairingStore(context).load() != null) {
            ConnectionService.connect(context)
        }
    }
}
