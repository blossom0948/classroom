package com.example.phoneunlock.widget

import android.content.Intent
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import com.example.phoneunlock.MainActivity
import com.example.phoneunlock.network.ConnectionService
import com.example.phoneunlock.storage.SecurePairingStore

class PcUnlockTileService : TileService() {
    override fun onStartListening() {
        super.onStartListening()
        updateTile()
    }

    override fun onClick() {
        super.onClick()
        val computer = SecurePairingStore(this).load()
        if (computer == null) {
            startActivityAndCollapse(Intent(this, MainActivity::class.java))
            return
        }

        val intent = Intent(this, MainActivity::class.java)
            .setAction(MainActivity.ACTION_WIDGET_UNLOCK)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        startActivityAndCollapse(intent)
        qsTile?.state = Tile.STATE_ACTIVE
        qsTile?.updateTile()
    }

    private fun updateTile() {
        val tile = qsTile ?: return
        val computer = SecurePairingStore(this).load()
        tile.label = if (computer == null) "PC 연결" else "PC 잠금 해제"
        tile.state = if (computer != null && ConnectionService.isConnected(computer.computerId)) {
            Tile.STATE_ACTIVE
        } else {
            Tile.STATE_INACTIVE
        }
        tile.updateTile()
    }
}
