package com.blossom.classroom.student.android;

import android.content.Context;
import android.content.Intent;
import android.graphics.Bitmap;
import android.graphics.PixelFormat;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.Image;
import android.media.ImageReader;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.SystemClock;
import android.util.Base64;
import android.util.DisplayMetrics;
import android.view.Display;
import android.view.WindowManager;

import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.nio.ByteBuffer;
import java.time.Instant;
import java.util.concurrent.atomic.AtomicBoolean;

/** Transparent, consent-gated screen capture for the teacher's live screen view. */
final class AndroidScreenCapture {
    interface Listener {
        void onFrame(JSONObject frame);
        void onStopped();
    }

    private static final int MAX_WIDTH = 1_280;
    private static final int MAX_HEIGHT = 720;
    private static final int MAX_JPEG_BYTES = 72 * 1024;

    private final Context context;
    private final Listener listener;
    private final long intervalMs;
    private final AtomicBoolean stopped = new AtomicBoolean(false);
    private HandlerThread captureThread;
    private Handler captureHandler;
    private MediaProjection projection;
    private MediaProjection.Callback projectionCallback;
    private ImageReader imageReader;
    private VirtualDisplay virtualDisplay;
    private long lastFrameElapsedMs;
    private int captureWidth;
    private int captureHeight;

    AndroidScreenCapture(Context context, int resultCode, Intent resultData, long intervalMs, Listener listener) {
        this.context = context.getApplicationContext();
        this.listener = listener;
        this.intervalMs = Math.max(1_000L, Math.min(10_000L, intervalMs));
        start(resultCode, resultData);
    }

    private void start(int resultCode, Intent resultData) {
        try {
            startInternal(resultCode, resultData);
        } catch (RuntimeException | Error exception) {
            releaseResources(true);
            throw exception;
        }
    }

    private void startInternal(int resultCode, Intent resultData) {
        if (resultCode != android.app.Activity.RESULT_OK || resultData == null) {
            throw new IllegalArgumentException("화면 공유 권한 승인이 필요합니다.");
        }
        MediaProjectionManager manager = (MediaProjectionManager) context.getSystemService(Context.MEDIA_PROJECTION_SERVICE);
        if (manager == null) throw new IllegalStateException("화면 공유 서비스를 사용할 수 없습니다.");
        projection = manager.getMediaProjection(resultCode, resultData);
        if (projection == null) throw new IllegalStateException("화면 공유 권한을 확인하지 못했습니다.");

        DisplayMetrics metrics = new DisplayMetrics();
        WindowManager windowManager = (WindowManager) context.getSystemService(Context.WINDOW_SERVICE);
        if (windowManager == null) throw new IllegalStateException("화면 크기를 확인하지 못했습니다.");
        Display display = windowManager.getDefaultDisplay();
        display.getRealMetrics(metrics);
        float scale = Math.min(1f, Math.min(MAX_WIDTH / (float) metrics.widthPixels, MAX_HEIGHT / (float) metrics.heightPixels));
        captureWidth = Math.max(1, Math.round(metrics.widthPixels * scale));
        captureHeight = Math.max(1, Math.round(metrics.heightPixels * scale));

        captureThread = new HandlerThread("ClassroomScreenCapture");
        captureThread.start();
        captureHandler = new Handler(captureThread.getLooper());
        imageReader = ImageReader.newInstance(captureWidth, captureHeight, PixelFormat.RGBA_8888, 2);
        imageReader.setOnImageAvailableListener(this::captureLatestImage, captureHandler);

        projectionCallback = new MediaProjection.Callback() {
            @Override
            public void onStop() {
                releaseResources(false);
                listener.onStopped();
            }
        };
        projection.registerCallback(projectionCallback, captureHandler);
        int densityDpi = metrics.densityDpi > 0 ? metrics.densityDpi : 160;
        virtualDisplay = projection.createVirtualDisplay(
                "Classroom teacher-approved screen share",
                captureWidth,
                captureHeight,
                densityDpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
                imageReader.getSurface(),
                null,
                captureHandler);
        if (virtualDisplay == null) {
            releaseResources(true);
            throw new IllegalStateException("화면 공유를 시작하지 못했습니다.");
        }
    }

    private void captureLatestImage(ImageReader reader) {
        if (stopped.get()) return;
        Image image = null;
        Bitmap paddedBitmap = null;
        Bitmap bitmap = null;
        try {
            image = reader.acquireLatestImage();
            if (image == null) return;
            long now = SystemClock.elapsedRealtime();
            if (now - lastFrameElapsedMs < intervalMs) return;

            Image.Plane[] planes = image.getPlanes();
            if (planes.length == 0) return;
            Image.Plane plane = planes[0];
            ByteBuffer buffer = plane.getBuffer();
            int pixelStride = plane.getPixelStride();
            int rowStride = plane.getRowStride();
            if (pixelStride <= 0 || rowStride <= 0) return;
            int paddedWidth = captureWidth + Math.max(0, (rowStride - pixelStride * captureWidth) / pixelStride);
            paddedBitmap = Bitmap.createBitmap(paddedWidth, captureHeight, Bitmap.Config.ARGB_8888);
            buffer.rewind();
            paddedBitmap.copyPixelsFromBuffer(buffer);
            bitmap = paddedWidth == captureWidth
                    ? paddedBitmap
                    : Bitmap.createBitmap(paddedBitmap, 0, 0, captureWidth, captureHeight);

            byte[] jpeg = compressWithinLimit(bitmap);
            if (jpeg == null) return;
            String base64 = Base64.encodeToString(jpeg, Base64.NO_WRAP);
            JSONObject frame = new JSONObject()
                    .put("mimeType", "image/jpeg")
                    .put("base64Data", base64)
                    .put("width", captureWidth)
                    .put("height", captureHeight)
                    .put("capturedAtUtc", Instant.now().toString());
            lastFrameElapsedMs = now;
            listener.onFrame(frame);
        } catch (Exception ignored) {
            // A single bad capture must not terminate the managed device connection.
        } finally {
            if (image != null) image.close();
            if (bitmap != null && bitmap != paddedBitmap && !bitmap.isRecycled()) bitmap.recycle();
            if (paddedBitmap != null && !paddedBitmap.isRecycled()) paddedBitmap.recycle();
        }
    }

    private static byte[] compressWithinLimit(Bitmap bitmap) {
        for (int quality = 58; quality >= 26; quality -= 8) {
            ByteArrayOutputStream output = new ByteArrayOutputStream(24 * 1024);
            if (!bitmap.compress(Bitmap.CompressFormat.JPEG, quality, output)) return null;
            byte[] bytes = output.toByteArray();
            if (bytes.length <= MAX_JPEG_BYTES) return bytes;
        }
        return null;
    }

    void stop() {
        releaseResources(true);
    }

    private void releaseResources(boolean stopProjection) {
        if (!stopped.compareAndSet(false, true)) return;
        if (imageReader != null) {
            imageReader.setOnImageAvailableListener(null, null);
            imageReader.close();
            imageReader = null;
        }
        if (virtualDisplay != null) {
            virtualDisplay.release();
            virtualDisplay = null;
        }
        if (projection != null) {
            if (projectionCallback != null) projection.unregisterCallback(projectionCallback);
            if (stopProjection) projection.stop();
            projection = null;
        }
        if (captureThread != null) {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.JELLY_BEAN_MR2) captureThread.quitSafely();
            else captureThread.quit();
            captureThread = null;
            captureHandler = null;
        }
    }
}
