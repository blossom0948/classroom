package com.example.phoneunlock.network

import android.Manifest
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothManager
import android.bluetooth.le.AdvertiseCallback
import android.bluetooth.le.AdvertiseData
import android.bluetooth.le.AdvertiseSettings
import android.bluetooth.le.BluetoothLeAdvertiser
import android.content.Context
import android.content.pm.PackageManager
import android.os.ParcelUuid
import androidx.core.content.ContextCompat
import java.util.UUID

class BleBeaconAdvertiser(private val context: Context) {
    private var advertiser: BluetoothLeAdvertiser? = null
    private var callback: AdvertiseCallback? = null

    fun start(phoneId: String) {
        if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.S &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.BLUETOOTH_ADVERTISE) != PackageManager.PERMISSION_GRANTED
        ) {
            throw SecurityException("Bluetooth 광고 권한이 없습니다.")
        }

        val manager = context.getSystemService(BluetoothManager::class.java)
        val adapter: BluetoothAdapter = manager?.adapter ?: throw IllegalStateException("Bluetooth 어댑터가 없습니다.")
        if (!adapter.isEnabled) {
            throw IllegalStateException("Bluetooth가 꺼져 있습니다.")
        }

        stop()
        val service = ParcelUuid(BEACON_SERVICE_UUID)
        val settings = AdvertiseSettings.Builder()
            .setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_LOW_POWER)
            .setTxPowerLevel(AdvertiseSettings.ADVERTISE_TX_POWER_MEDIUM)
            .setConnectable(false)
            .setTimeout(0)
            .build()
        val data = AdvertiseData.Builder()
            .setIncludeDeviceName(false)
            .addServiceUuid(service)
            .addServiceData(service, beaconId(phoneId))
            .build()
        val nextCallback = object : AdvertiseCallback() {}
        advertiser = adapter.bluetoothLeAdvertiser
        callback = nextCallback
        advertiser?.startAdvertising(settings, data, nextCallback)
    }

    fun stop() {
        val currentAdvertiser = advertiser
        val currentCallback = callback
        if (currentAdvertiser != null && currentCallback != null) {
            runCatching { currentAdvertiser.stopAdvertising(currentCallback) }
        }
        advertiser = null
        callback = null
    }

    private fun beaconId(phoneId: String): ByteArray {
        val compact = UUID.fromString(phoneId).toString().replace("-", "")
        return ByteArray(16) { index -> compact.substring(index * 2, index * 2 + 2).toInt(16).toByte() }
    }

    companion object {
        val BEACON_SERVICE_UUID: UUID = UUID.fromString("0000f2a0-0000-1000-8000-00805f9b34fb")
    }
}
