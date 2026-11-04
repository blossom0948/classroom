package com.blossom.classroom.student.android;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import androidx.core.content.ContextCompat;

/** Reconnects after boot; kiosk auto-launch itself must be configured by the school's DPC/EMM. */
public final class BootReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        if (!Intent.ACTION_BOOT_COMPLETED.equals(intent.getAction())
                || !SecureDeviceStore.hasEnrollment(context)) return;
        Intent service = new Intent(context, StudentConnectionService.class)
                .setAction(StudentConnectionService.ACTION_START);
        try {
            ContextCompat.startForegroundService(context, service);
        } catch (IllegalStateException ignored) {
            // Android or the device manager may block background service starts; the app reconnects
            // when a teacher or the device policy launches the visible app.
        }
    }
}
