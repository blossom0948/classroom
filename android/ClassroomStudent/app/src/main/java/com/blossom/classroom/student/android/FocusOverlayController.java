package com.blossom.classroom.student.android;

import android.content.Context;
import android.graphics.Color;
import android.graphics.PixelFormat;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.View;
import android.view.WindowManager;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;

import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

/** Shows teacher focus guidance above other apps when the board grants overlay access. */
final class FocusOverlayController {
    private final Context context;
    private final WindowManager windowManager;
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private View overlayView;

    FocusOverlayController(Context context) {
        this.context = context.getApplicationContext();
        this.windowManager = (WindowManager) this.context.getSystemService(Context.WINDOW_SERVICE);
    }

    static boolean hasPermission(Context context) {
        return Settings.canDrawOverlays(context);
    }

    boolean show(String mode, String message) {
        if (Looper.myLooper() != Looper.getMainLooper()) {
            AtomicBoolean applied = new AtomicBoolean(false);
            CountDownLatch completed = new CountDownLatch(1);
            if (!mainHandler.post(() -> {
                applied.set(showOnMainThread(mode, message));
                completed.countDown();
            })) return false;
            try {
                return completed.await(3, TimeUnit.SECONDS) && applied.get();
            } catch (InterruptedException exception) {
                Thread.currentThread().interrupt();
                return false;
            }
        }
        return showOnMainThread(mode, message);
    }

    private boolean showOnMainThread(String mode, String message) {
        if (windowManager == null || !hasPermission(context)) return false;
        remove();

        FrameLayout root = new FrameLayout(context);
        root.setClickable(true);
        root.setFocusable(true);
        root.setFocusableInTouchMode(true);
        root.setOnKeyListener((view, keyCode, event) -> keyCode == KeyEvent.KEYCODE_BACK);

        if ("blackScreen".equals(mode)) {
            root.setBackgroundColor(Color.BLACK);
        } else {
            root.setBackground(new GradientDrawable(
                    GradientDrawable.Orientation.TL_BR,
                    new int[]{0xFF17152B, 0xFF282350, 0xFF423B86}));
            LinearLayout content = new LinearLayout(context);
            content.setOrientation(LinearLayout.VERTICAL);
            content.setGravity(Gravity.CENTER);
            content.setPadding(dp(32), dp(32), dp(32), dp(32));

            TextView label = text("CLASSROOM  ·  집중 모드", 14, 0xFFC8C2FF, true);
            label.setLetterSpacing(0.09f);
            content.addView(label);
            space(content, 22);

            TextView title = text("수업에 집중해 주세요", 34, Color.WHITE, true);
            content.addView(title);
            space(content, 18);

            TextView body = text(message == null || message.isBlank() ? "선생님의 안내를 확인해 주세요." : message,
                    21, 0xFFE8E6F8, false);
            body.setLineSpacing(dp(5), 1.05f);
            content.addView(body);

            root.addView(content, new FrameLayout.LayoutParams(-1, -1));
        }

        WindowManager.LayoutParams layout = new WindowManager.LayoutParams(
                WindowManager.LayoutParams.MATCH_PARENT,
                WindowManager.LayoutParams.MATCH_PARENT,
                WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN
                        | WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS
                        | WindowManager.LayoutParams.FLAG_FULLSCREEN
                        | WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON,
                PixelFormat.OPAQUE);
        layout.gravity = Gravity.TOP | Gravity.START;
        layout.setTitle("Classroom 집중 모드");
        try {
            windowManager.addView(root, layout);
            overlayView = root;
            root.requestFocus();
            return true;
        } catch (SecurityException | WindowManager.BadTokenException exception) {
            overlayView = null;
            return false;
        }
    }

    void remove() {
        if (Looper.myLooper() != Looper.getMainLooper()) {
            mainHandler.post(this::removeOnMainThread);
            return;
        }
        removeOnMainThread();
    }

    private void removeOnMainThread() {
        View current = overlayView;
        overlayView = null;
        if (current == null || windowManager == null) return;
        try {
            windowManager.removeViewImmediate(current);
        } catch (IllegalArgumentException | WindowManager.BadTokenException ignored) {
            // The system may remove an overlay after the permission changes.
        }
    }

    private TextView text(String value, int sizeSp, int color, boolean bold) {
        TextView view = new TextView(context);
        view.setText(value);
        view.setTextSize(sizeSp);
        view.setTextColor(color);
        view.setGravity(Gravity.CENTER);
        view.setTypeface(Typeface.create("sans-serif", bold ? Typeface.BOLD : Typeface.NORMAL));
        return view;
    }

    private void space(LinearLayout layout, int heightDp) {
        layout.addView(new View(context), new LinearLayout.LayoutParams(1, dp(heightDp)));
    }

    private int dp(float value) {
        return Math.round(value * context.getResources().getDisplayMetrics().density);
    }
}
