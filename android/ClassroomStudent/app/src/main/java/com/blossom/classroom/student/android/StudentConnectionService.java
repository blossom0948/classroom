package com.blossom.classroom.student.android;

import android.app.ActivityManager;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.ServiceInfo;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.os.BatteryManager;
import android.os.Build;
import android.os.IBinder;
import android.util.Log;

import androidx.annotation.Nullable;
import androidx.core.app.NotificationCompat;
import androidx.core.app.ServiceCompat;
import androidx.core.content.ContextCompat;

import org.json.JSONObject;

import java.time.Instant;
import java.util.Locale;
import java.util.UUID;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;

import okhttp3.HttpUrl;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;
import okhttp3.WebSocket;
import okhttp3.WebSocketListener;

/** Visible, reconnecting Android client for a school-managed board. */
public final class StudentConnectionService extends Service {
    public static final String ACTION_START = "com.blossom.classroom.student.android.START";
    public static final String ACTION_VERIFY_EXIT_PIN = "com.blossom.classroom.student.android.VERIFY_EXIT_PIN";
    public static final String ACTION_STATUS = "com.blossom.classroom.student.android.STATUS";
    public static final String ACTION_FOCUS = "com.blossom.classroom.student.android.FOCUS";
    public static final String ACTION_NOTICE = "com.blossom.classroom.student.android.NOTICE";
    public static final String ACTION_PIN_RESULT = "com.blossom.classroom.student.android.PIN_RESULT";
    public static final String ACTION_SCREEN_SHARE_REQUEST = "com.blossom.classroom.student.android.SCREEN_SHARE_REQUEST";
    public static final String ACTION_SCREEN_SHARE_RESPONSE = "com.blossom.classroom.student.android.SCREEN_SHARE_RESPONSE";
    public static final String ACTION_STOP_SCREEN_SHARE = "com.blossom.classroom.student.android.STOP_SCREEN_SHARE";
    public static final String ACTION_HIDE_FOCUS_OVERLAY = "com.blossom.classroom.student.android.HIDE_FOCUS_OVERLAY";
    public static final String ACTION_SHOW_FOCUS_OVERLAY = "com.blossom.classroom.student.android.SHOW_FOCUS_OVERLAY";
    public static final String ACTION_REMOTE_COMMAND = "com.blossom.classroom.student.android.REMOTE_COMMAND";
    public static final String ACTION_REMOTE_ACTION_RESULT = "com.blossom.classroom.student.android.REMOTE_ACTION_RESULT";
    public static final String EXTRA_CONNECTED = "connected";
    public static final String EXTRA_STATUS = "status";
    public static final String EXTRA_FOCUS_ENABLED = "focusEnabled";
    public static final String EXTRA_FOCUS_MODE = "focusMode";
    public static final String EXTRA_FOCUS_MESSAGE = "focusMessage";
    public static final String EXTRA_NOTICE_TEXT = "noticeText";
    public static final String EXTRA_NOTICE_SECONDS = "noticeSeconds";
    public static final String EXTRA_REQUEST_ID = "requestId";
    public static final String EXTRA_PIN = "pin";
    public static final String EXTRA_APPROVED = "approved";
    public static final String EXTRA_CODE = "code";
    public static final String EXTRA_SCREEN_SHARE_REQUEST_ID = "screenShareRequestId";
    public static final String EXTRA_SCREEN_SHARE_APPROVED = "screenShareApproved";
    public static final String EXTRA_SCREEN_SHARE_INTERVAL_MS = "screenShareIntervalMilliseconds";
    public static final String EXTRA_SCREEN_SHARE_RESULT_CODE = "screenShareResultCode";
    public static final String EXTRA_SCREEN_SHARE_RESULT_DATA = "screenShareResultData";
    public static final String EXTRA_REMOTE_ACTION = "remoteAction";
    public static final String EXTRA_REMOTE_VALUE = "remoteValue";
    public static final String EXTRA_REMOTE_SUCCESS = "remoteSuccess";
    public static final String EXTRA_REMOTE_CODE = "remoteCode";
    public static final String EXTRA_REMOTE_MESSAGE = "remoteMessage";

    private static final String TAG = "ClassroomStudent";
    private static final String CHANNEL_ID = "classroom_connection";
    private static final int NOTIFICATION_ID = 7041;
    private static final int PROTOCOL_VERSION = 1;
    private static final String ZERO_SESSION_ID = "00000000-0000-0000-0000-000000000000";
    private static final String VERSION = BuildConfig.VERSION_NAME;
    private static final String PREFS = "classroom_app_state";
    private static final String PREF_FOCUS_ENABLED = "focus_enabled";
    private static final String PREF_FOCUS_MODE = "focus_mode";
    private static final String PREF_FOCUS_MESSAGE = "focus_message";
    private static final String PREF_NOTICE = "notice_text";
    private static final String PREF_CONNECTED = "server_connected";
    private static final String PREF_STATUS = "server_status";
    private static final String PREF_PENDING_SCREEN_SHARE_REQUEST = "pending_screen_share_request";
    private static final String PREF_SCREEN_SHARE_INTERVAL = "screen_share_interval_ms";
    private static final String PREF_PENDING_REMOTE_ACTION = "pending_remote_action";

    private final AtomicBoolean running = new AtomicBoolean(false);
    private final AtomicBoolean focusEnabled = new AtomicBoolean(false);
    private final AtomicBoolean focusOverlayApplied = new AtomicBoolean(false);
    private volatile String focusMode = "message";
    private volatile String focusMessage = "수업에 집중해 주세요.";
    private volatile boolean focusOverlaySuppressed;
    private volatile String sessionId = ZERO_SESSION_ID;
    private volatile String currentStatus = "Classroom 서버 연결을 준비하고 있습니다.";
    private volatile WebSocket currentSocket;
    private volatile boolean screenSharingEnabled;
    private volatile JSONObject latestScreenFrame;
    private AndroidScreenCapture screenCapture;
    private ScheduledFuture<?> screenShareConsentTimeout;
    private ScheduledFuture<?> remoteActionTimeout;
    private ScheduledExecutorService executor;
    private ScheduledFuture<?> heartbeatTask;
    private ScheduledFuture<?> noticeExpiryTask;
    private OkHttpClient httpClient;
    private SecureDeviceStore.Enrollment enrollment;
    private FocusOverlayController focusOverlayController;

    private final BroadcastReceiver localControlReceiver = new BroadcastReceiver() {
        @Override
        public void onReceive(Context context, Intent intent) {
            if (ACTION_VERIFY_EXIT_PIN.equals(intent.getAction())) {
                verifyExitPin(
                        intent.getStringExtra(EXTRA_REQUEST_ID),
                        intent.getStringExtra(EXTRA_PIN));
            }
        }
    };

    @Override
    public void onCreate() {
        super.onCreate();
        createNotificationChannel();
        executor = Executors.newScheduledThreadPool(2);
        httpClient = new OkHttpClient.Builder()
                .pingInterval(20, TimeUnit.SECONDS)
                .connectTimeout(15, TimeUnit.SECONDS)
                .readTimeout(0, TimeUnit.MILLISECONDS)
                .build();
        focusOverlayController = new FocusOverlayController(this);
        loadFocusState();
        IntentFilter filter = new IntentFilter(ACTION_VERIFY_EXIT_PIN);
        ContextCompat.registerReceiver(this, localControlReceiver, filter, ContextCompat.RECEIVER_NOT_EXPORTED);
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        boolean startingScreenShare = intent != null
                && ACTION_SCREEN_SHARE_RESPONSE.equals(intent.getAction())
                && intent.getBooleanExtra(EXTRA_SCREEN_SHARE_APPROVED, false);
        try {
            if (startingScreenShare && Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                int foregroundType = ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION;
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
                    foregroundType |= ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE;
                }
                ServiceCompat.startForeground(this, NOTIFICATION_ID,
                        buildNotification("선생님과 화면을 공유하고 있습니다."), foregroundType);
            } else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
                // Android 14+ must not activate the declared mediaProjection type
                // until the system's screen-capture consent has been granted.
                ServiceCompat.startForeground(this, NOTIFICATION_ID,
                        buildNotification("Classroom 서버에 연결되어 있습니다."),
                        ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE);
            } else {
                startForeground(NOTIFICATION_ID, buildNotification("Classroom 서버에 연결되어 있습니다."));
            }
        } catch (SecurityException exception) {
            Log.e(TAG, "Android denied the requested foreground-service type.", exception);
            running.set(false);
            publishStatus(false, startingScreenShare
                    ? "화면 공유 권한을 시작하지 못했습니다. 화면 공유를 다시 요청해 주세요."
                    : "Android가 백그라운드 연결 권한을 거부했습니다. 앱 권한과 업데이트를 확인해 주세요.");
            stopSelf(startId);
            return START_NOT_STICKY;
        }
        if (running.compareAndSet(false, true)) {
            enrollment = SecureDeviceStore.load(this);
            if (enrollment == null) {
                publishStatus(false, "등록 정보가 없습니다. 앱에서 기기를 등록해 주세요.");
                stopSelf(startId);
                return START_NOT_STICKY;
            }
            executor.execute(this::connectionLoop);
        }
        if (intent != null && ACTION_VERIFY_EXIT_PIN.equals(intent.getAction())) {
            verifyExitPin(intent.getStringExtra(EXTRA_REQUEST_ID), intent.getStringExtra(EXTRA_PIN));
        } else if (intent != null && ACTION_SCREEN_SHARE_RESPONSE.equals(intent.getAction())) {
            handleScreenShareResponse(intent);
        } else if (intent != null && ACTION_STOP_SCREEN_SHARE.equals(intent.getAction())) {
            stopScreenCapture(false);
        } else if (intent != null && ACTION_REMOTE_ACTION_RESULT.equals(intent.getAction())) {
            handleRemoteActionResult(intent);
        }
        if (intent != null && ACTION_HIDE_FOCUS_OVERLAY.equals(intent.getAction())) {
            focusOverlaySuppressed = true;
            focusOverlayController.remove();
            focusOverlayApplied.set(false);
        } else if (intent != null && ACTION_SHOW_FOCUS_OVERLAY.equals(intent.getAction())) {
            focusOverlaySuppressed = false;
            syncFocusOverlay();
        } else {
            syncFocusOverlay();
        }
        return START_STICKY;
    }

    @Nullable
    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    @Override
    public void onDestroy() {
        running.set(false);
        if (focusOverlayController != null) focusOverlayController.remove();
        focusOverlayApplied.set(false);
        stopScreenCapture(false);
        cancelHeartbeat();
        ScheduledFuture<?> expiry = noticeExpiryTask;
        noticeExpiryTask = null;
        if (expiry != null) expiry.cancel(true);
        ScheduledFuture<?> consentTimeout = screenShareConsentTimeout;
        screenShareConsentTimeout = null;
        if (consentTimeout != null) consentTimeout.cancel(true);
        ScheduledFuture<?> actionTimeout = remoteActionTimeout;
        remoteActionTimeout = null;
        if (actionTimeout != null) actionTimeout.cancel(true);
        WebSocket socket = currentSocket;
        if (socket != null) socket.close(1000, "Student app stopped by administrator");
        try {
            unregisterReceiver(localControlReceiver);
        } catch (IllegalArgumentException ignored) {
        }
        if (executor != null) executor.shutdownNow();
        if (httpClient != null) httpClient.dispatcher().cancelAll();
        super.onDestroy();
    }

    private void connectionLoop() {
        AtomicInteger retryDelaySeconds = new AtomicInteger(1);
        while (running.get() && !Thread.currentThread().isInterrupted()) {
            CountDownLatch connectionEnded = new CountDownLatch(1);
            AtomicBoolean endedOnce = new AtomicBoolean(false);
            try {
                HttpUrl base = HttpUrl.parse(enrollment.serverOrigin);
                if (base == null || !"https".equalsIgnoreCase(base.scheme())) {
                    publishStatus(false, "보안 연결 주소가 올바르지 않습니다. 앱에서 HTTPS 서버 주소를 확인해 주세요.");
                    return;
                }
                HttpUrl endpoint = base.newBuilder()
                        // HttpUrl supports only http/https. newWebSocket() adds the
                        // WebSocket upgrade while keeping the secure HTTPS transport.
                        .scheme("https")
                        .encodedPath("/ws/student")
                        .query(null)
                        .addQueryParameter("deviceId", enrollment.deviceId)
                        .build();
                Request request = new Request.Builder()
                        .url(endpoint)
                        .header("Authorization", "Bearer " + enrollment.deviceToken)
                        .build();

                publishStatus(false, "Classroom 서버에 연결하고 있습니다.");
                currentSocket = httpClient.newWebSocket(request, new WebSocketListener() {
                    @Override
                    public void onOpen(WebSocket webSocket, Response response) {
                        currentSocket = webSocket;
                        sessionId = ZERO_SESSION_ID;
                        try {
                            JSONObject hello = new JSONObject()
                                    .put("deviceId", enrollment.deviceId)
                                    .put("sessionId", ZERO_SESSION_ID)
                                    .put("agentVersion", VERSION);
                            sendEnvelope(webSocket, "DEVICE_HELLO", hello);
                        } catch (Exception exception) {
                            webSocket.close(1002, "Could not send device hello");
                            return;
                        }
                        publishStatus(true, "서버에 연결됨 · 수업 대기 중");
                        startHeartbeat(webSocket);
                        retryDelaySeconds.set(1);
                    }

                    @Override
                    public void onMessage(WebSocket webSocket, String text) {
                        handleServerMessage(webSocket, text);
                    }

                    @Override
                    public void onClosed(WebSocket webSocket, int code, String reason) {
                        cancelHeartbeat();
                        currentSocket = null;
                        publishStatus(false, "연결이 종료되어 다시 시도하고 있습니다.");
                        if (endedOnce.compareAndSet(false, true)) connectionEnded.countDown();
                    }

                    @Override
                    public void onFailure(WebSocket webSocket, Throwable error, Response response) {
                        cancelHeartbeat();
                        currentSocket = null;
                        publishStatus(false, connectionFailureMessage(error, response));
                        Log.i(TAG, "Student connection ended; retrying.");
                        if (endedOnce.compareAndSet(false, true)) connectionEnded.countDown();
                    }
                });
                connectionEnded.await();
                if (!running.get()) break;
                Thread.sleep(TimeUnit.SECONDS.toMillis(retryDelaySeconds.get()));
                retryDelaySeconds.updateAndGet(value -> Math.min(value * 2, 30));
            } catch (InterruptedException exception) {
                Thread.currentThread().interrupt();
                break;
            } catch (Exception exception) {
                publishStatus(false, connectionFailureMessage(exception, null));
                Log.i(TAG, "Student connection attempt failed; retrying.");
                try {
                    Thread.sleep(TimeUnit.SECONDS.toMillis(retryDelaySeconds.get()));
                } catch (InterruptedException interruptedException) {
                    Thread.currentThread().interrupt();
                    break;
                }
                retryDelaySeconds.updateAndGet(value -> Math.min(value * 2, 30));
            }
        }
    }

    private String connectionFailureMessage(Throwable error, Response response) {
        if (response != null) {
            int status = response.code();
            if (status == 401 || status == 403) {
                return "기기 인증이 거부되었습니다. 관리자 화면에서 학생 코드를 새로 발급해 다시 등록해 주세요.";
            }
            if (status == 404 || status == 426) {
                return "서버 연결 경로를 확인하지 못했습니다. 앱 업데이트 후 다시 등록해 주세요.";
            }
            if (status >= 500) return "Classroom 서버가 일시적으로 응답하지 않습니다. 자동으로 다시 연결합니다.";
            return "Classroom 서버가 연결을 거부했습니다 (" + status + "). 자동으로 다시 시도합니다.";
        }
        if (hasCauseType(error, java.net.UnknownHostException.class)) {
            return "서버 주소를 찾지 못했습니다. 전자칠판의 Wi-Fi·인터넷·DNS를 확인하세요. MDM 설정은 서버 연결과 무관합니다.";
        }
        if (hasCauseType(error, javax.net.ssl.SSLHandshakeException.class)) {
            return "보안 연결을 만들지 못했습니다. 전자칠판의 날짜·시간과 HTTPS 보안 설정을 확인하세요.";
        }
        if (hasCauseType(error, java.net.SocketTimeoutException.class)
                || hasCauseType(error, java.net.ConnectException.class)) {
            return "서버 응답이 없습니다. 인터넷 또는 학교 방화벽의 HTTPS(443) 허용을 확인하세요. 자동 재연결 중입니다.";
        }
        return "Classroom 서버에 연결되지 않습니다. 네트워크를 확인하세요. MDM은 키오스크 전용 모드에만 필요합니다.";
    }

    private boolean hasCauseType(Throwable error, Class<? extends Throwable> type) {
        Throwable current = error;
        for (int depth = 0; current != null && depth < 10; depth++, current = current.getCause()) {
            if (type.isInstance(current)) return true;
        }
        return false;
    }

    private void startHeartbeat(WebSocket socket) {
        cancelHeartbeat();
        long periodSeconds = screenSharingEnabled ? 2L : 10L;
        heartbeatTask = executor.scheduleWithFixedDelay(() -> {
            if (!running.get() || currentSocket != socket) return;
            try {
                Integer batteryPercent = readBatteryPercent();
                JSONObject payload = new JSONObject()
                        .put("deviceId", enrollment.deviceId)
                        .put("sessionId", sessionId)
                        .put("agentVersion", VERSION)
                        .put("observedAtUtc", Instant.now().toString())
                        .put("activity", JSONObject.NULL)
                        .put("batteryPercent", batteryPercent == null ? JSONObject.NULL : batteryPercent)
                        .put("networkStatus", readNetworkStatus())
                        .put("policyApplied", focusOverlayApplied.get())
                        .put("screenFrame", screenSharingEnabled && latestScreenFrame != null
                                ? latestScreenFrame : JSONObject.NULL)
                        .put("screenSharingEnabled", screenSharingEnabled)
                        .put("needsHelp", false);
                sendEnvelope(socket, "DEVICE_HEARTBEAT", payload);
            } catch (Exception exception) {
                Log.i(TAG, "Heartbeat could not be composed.");
            }
        }, 0, periodSeconds, TimeUnit.SECONDS);
    }

    private void cancelHeartbeat() {
        ScheduledFuture<?> task = heartbeatTask;
        heartbeatTask = null;
        if (task != null) task.cancel(true);
    }

    private void handleServerMessage(WebSocket socket, String text) {
        try {
            JSONObject envelope = new JSONObject(text);
            if (envelope.optInt("version", -1) != PROTOCOL_VERSION) return;
            String type = envelope.optString("type", "");
            JSONObject payload = envelope.optJSONObject("payload");
            if (payload == null) return;
            switch (type) {
                case "DEVICE_SESSION_ACCEPTED":
                    if (!enrollment.deviceId.equals(payload.optString("deviceId"))) return;
                    sessionId = payload.optString("sessionId", ZERO_SESSION_ID);
                    publishStatus(true, sessionId.isBlank() || ZERO_SESSION_ID.equals(sessionId)
                            ? "서버에 연결됨 · 수업 대기 중"
                            : "서버에 연결됨 · 수업에 참여 중");
                    break;
                case "COMMAND_REQUEST":
                    applyCommand(socket, payload);
                    break;
                case "DEVICE_EXIT_PIN_VERIFICATION_RESPONSE":
                    publishPinResult(payload);
                    break;
                case "ERROR":
                    if ("DEVICE_REVOKED".equals(payload.optString("code"))) {
                        publishStatus(false, "이 기기의 등록이 해제되었습니다. 학교 관리자에게 문의해 주세요.");
                    }
                    break;
                default:
                    break;
            }
        } catch (Exception exception) {
            Log.i(TAG, "Ignored an unreadable server message.");
        }
    }

    private void applyCommand(WebSocket socket, JSONObject command) {
        String requestId = command.optString("requestId", "");
        String kind = command.optString("kind", "");
        boolean success = false;
        boolean resultPending = false;
        String code = "COMMAND_UNSUPPORTED";
        String message = "이 Android 기기에서 아직 지원하지 않는 명령입니다.";

        try {
            switch (kind.toLowerCase(Locale.ROOT)) {
                case "message": {
                    String text = command.optString("message", "선생님 안내");
                    int seconds = Math.max(1, Math.min(3600, command.optInt("displaySeconds", 10)));
                    saveNotice(text, seconds);
                    Intent notice = new Intent(ACTION_NOTICE)
                            .setPackage(getPackageName())
                            .putExtra(EXTRA_NOTICE_TEXT, text)
                            .putExtra(EXTRA_NOTICE_SECONDS, seconds);
                    sendBroadcast(notice);
                    showMessageNotification(text);
                    success = true;
                    code = "MESSAGE_DISPLAYED";
                    message = "선생님 안내를 전달했습니다.";
                    break;
                }
                case "focusmode": {
                    boolean enabled = command.optBoolean("focusEnabled", true);
                    String displayMode = command.optString("focusDisplayMode", "message");
                    String focusText = command.optString("message", "수업에 집중해 주세요.");
                    if ("blackscreen".equalsIgnoreCase(displayMode)) displayMode = "blackScreen";
                    if (!"blackScreen".equals(displayMode)) displayMode = "message";
                    saveFocusState(enabled, displayMode, focusText);
                    boolean overlayApplied = syncFocusOverlay();
                    Intent focus = new Intent(ACTION_FOCUS)
                            .setPackage(getPackageName())
                            .putExtra(EXTRA_FOCUS_ENABLED, enabled)
                            .putExtra(EXTRA_FOCUS_MODE, displayMode)
                            .putExtra(EXTRA_FOCUS_MESSAGE, focusText);
                    sendBroadcast(focus);
                    if (enabled) {
                        showStatusNotification(overlayApplied
                                ? displayMode.equals("blackScreen")
                                        ? "선생님이 집중 모드를 시작했습니다."
                                        : "선생님 안내를 확인해 주세요."
                                : "집중 화면을 다른 앱 위에 표시하려면 권한을 설정해 주세요.");
                    } else {
                        showStatusNotification("집중 모드가 종료되었습니다.");
                    }
                    success = !enabled || overlayApplied;
                    code = !enabled
                            ? "FOCUS_MODE_DISABLED"
                            : overlayApplied ? "FOCUS_MODE_ENABLED" : "FOCUS_OVERLAY_PERMISSION_REQUIRED";
                    message = !enabled
                            ? "집중 모드를 종료했습니다."
                            : overlayApplied
                                    ? "다른 앱 위에 집중 화면을 적용했습니다."
                                    : "다른 앱 위 집중 화면 권한이 꺼져 있습니다. 전자칠판에서 ‘다른 앱 위에 표시’를 허용해 주세요.";
                    break;
                }
                case "screenshare": {
                    boolean enabled = command.optBoolean("screenShareEnabled", false);
                    if (!enabled) {
                        stopScreenCapture(true);
                        success = true;
                        code = "SCREEN_SHARE_STOPPED";
                        message = "화면 공유를 종료했습니다.";
                    } else if (screenSharingEnabled) {
                        success = true;
                        code = "SCREEN_SHARE_ALREADY_ACTIVE";
                        message = "화면 공유가 이미 진행 중입니다.";
                    } else {
                        requestScreenShareConsent(requestId,
                                command.optLong("screenShareIntervalMilliseconds", 2_000L));
                        success = true;
                        resultPending = true;
                        code = "SCREEN_SHARE_CONSENT_PENDING";
                        message = "전자칠판에서 화면 공유 승인을 기다리고 있습니다.";
                    }
                    break;
                }
                case "openurl": {
                    String url = command.optString("url", "").trim();
                    if (!isSafeHttpsUrl(url)) {
                        code = "ANDROID_URL_INVALID";
                        message = "HTTPS 웹 주소만 열 수 있습니다.";
                    } else if (queueRemoteAction(requestId, "openUrl", url)) {
                        success = true;
                        resultPending = true;
                        code = "OPEN_URL_PENDING";
                        message = "전자칠판에서 웹 주소를 여는 중입니다.";
                    } else {
                        code = "REMOTE_ACTION_BUSY";
                        message = "이 기기에서 다른 실행 요청을 처리하고 있습니다.";
                    }
                    break;
                }
                case "launchapprovedapp": {
                    String packageName = command.optString("approvedAppId", "").trim();
                    if (!isAndroidPackageName(packageName)) {
                        code = "ANDROID_APP_ID_INVALID";
                        message = "허용 앱의 Android 패키지 이름이 올바르지 않습니다.";
                    } else if (queueRemoteAction(requestId, "launchApprovedApp", packageName)) {
                        success = true;
                        resultPending = true;
                        code = "APP_LAUNCH_PENDING";
                        message = "전자칠판에서 허용 앱을 여는 중입니다.";
                    } else {
                        code = "REMOTE_ACTION_BUSY";
                        message = "이 기기에서 다른 실행 요청을 처리하고 있습니다.";
                    }
                    break;
                }
                default:
                    code = "COMMAND_UNSUPPORTED";
                    message = "알 수 없는 Classroom 명령입니다.";
                    break;
            }
        } catch (Exception exception) {
            success = false;
            code = "COMMAND_APPLY_FAILED";
            message = "명령을 적용하지 못했습니다. 앱을 다시 열어 주세요.";
        }

        if (requestId.isBlank()) return;
        try {
            JSONObject ack = new JSONObject()
                    .put("requestId", requestId)
                    .put("deviceId", enrollment.deviceId)
                    .put("accepted", success)
                    .put("reason", success ? JSONObject.NULL : message)
                    .put("receivedAtUtc", Instant.now().toString());
            JSONObject result = new JSONObject()
                    .put("requestId", requestId)
                    .put("deviceId", enrollment.deviceId)
                    .put("success", success)
                    .put("code", code)
                    .put("message", message)
                    .put("appliedAtUtc", Instant.now().toString());
            sendEnvelope(socket, "COMMAND_ACK", ack);
            if (!resultPending) sendEnvelope(socket, "COMMAND_RESULT", result);
        } catch (Exception ignored) {
        }
    }

    private boolean queueRemoteAction(String requestId, String action, String value) {
        if (requestId == null || requestId.isBlank() || executor == null || executor.isShutdown()) return false;
        synchronized (PREF_PENDING_REMOTE_ACTION.intern()) {
            android.content.SharedPreferences preferences = getSharedPreferences(PREFS, MODE_PRIVATE);
            String existing = preferences.getString(PREF_PENDING_REMOTE_ACTION, "");
            if (!existing.isBlank()) return false;
            try {
                String pending = new JSONObject()
                        .put("requestId", requestId)
                        .put("action", action)
                        .put("value", value)
                        .toString();
                preferences.edit().putString(PREF_PENDING_REMOTE_ACTION, pending).apply();
                sendBroadcast(new Intent(ACTION_REMOTE_COMMAND)
                        .setPackage(getPackageName())
                        .putExtra(EXTRA_REQUEST_ID, requestId)
                        .putExtra(EXTRA_REMOTE_ACTION, action)
                        .putExtra(EXTRA_REMOTE_VALUE, value));
                showStatusNotification("선생님 요청을 처리하려면 전자칠판 앱을 열어 주세요.");

                ScheduledFuture<?> previous = remoteActionTimeout;
                if (previous != null) previous.cancel(false);
                remoteActionTimeout = executor.schedule(() -> {
                    android.content.SharedPreferences state = getSharedPreferences(PREFS, MODE_PRIVATE);
                    String serialized = state.getString(PREF_PENDING_REMOTE_ACTION, "");
                    try {
                        JSONObject pendingAction = new JSONObject(serialized);
                        if (!requestId.equals(pendingAction.optString("requestId", ""))) return;
                        state.edit().remove(PREF_PENDING_REMOTE_ACTION).apply();
                        remoteActionTimeout = null;
                        completeCommand(requestId, false, "REMOTE_ACTION_TIMEOUT",
                                "전자칠판 앱이 실행되지 않아 요청 시간이 초과되었습니다.");
                        showStatusNotification("선생님 요청 시간이 지났습니다. 앱을 열어 연결을 확인해 주세요.");
                    } catch (Exception ignored) {
                        // A completed action or a newer request replaced this timeout.
                    }
                }, 90, TimeUnit.SECONDS);
                return true;
            } catch (Exception exception) {
                return false;
            }
        }
    }

    private void handleRemoteActionResult(Intent intent) {
        String requestId = intent.getStringExtra(EXTRA_REQUEST_ID);
        if (requestId == null || requestId.isBlank()) return;
        android.content.SharedPreferences preferences = getSharedPreferences(PREFS, MODE_PRIVATE);
        String serialized = preferences.getString(PREF_PENDING_REMOTE_ACTION, "");
        try {
            JSONObject pending = new JSONObject(serialized);
            if (!requestId.equals(pending.optString("requestId", ""))) return;
        } catch (Exception exception) {
            return;
        }
        preferences.edit().remove(PREF_PENDING_REMOTE_ACTION).apply();
        ScheduledFuture<?> timeout = remoteActionTimeout;
        remoteActionTimeout = null;
        if (timeout != null) timeout.cancel(false);
        completeCommand(requestId,
                intent.getBooleanExtra(EXTRA_REMOTE_SUCCESS, false),
                intent.getStringExtra(EXTRA_REMOTE_CODE),
                intent.getStringExtra(EXTRA_REMOTE_MESSAGE));
    }

    private static boolean isSafeHttpsUrl(String value) {
        HttpUrl parsed = HttpUrl.parse(value);
        return parsed != null && "https".equalsIgnoreCase(parsed.scheme())
                && parsed.username().isEmpty() && parsed.password().isEmpty();
    }

    private static boolean isAndroidPackageName(String value) {
        return value != null && value.length() <= 128
                && value.matches("[A-Za-z][A-Za-z0-9_]*(\\.[A-Za-z0-9_]+)+");
    }

    private void requestScreenShareConsent(String requestId, long intervalMs) {
        if (requestId == null || requestId.isBlank()) return;
        android.content.SharedPreferences preferences = getSharedPreferences(PREFS, MODE_PRIVATE);
        String previousRequest = preferences.getString(PREF_PENDING_SCREEN_SHARE_REQUEST, "");
        if (!previousRequest.isBlank() && !previousRequest.equals(requestId)) {
            completeCommand(previousRequest, false, "SCREEN_SHARE_REPLACED", "새 화면 공유 요청으로 대체되었습니다.");
        }
        long safeInterval = Math.max(1_000L, Math.min(10_000L, intervalMs));
        preferences.edit()
                .putString(PREF_PENDING_SCREEN_SHARE_REQUEST, requestId)
                .putLong(PREF_SCREEN_SHARE_INTERVAL, safeInterval)
                .apply();

        sendBroadcast(new Intent(ACTION_SCREEN_SHARE_REQUEST)
                .setPackage(getPackageName())
                .putExtra(EXTRA_SCREEN_SHARE_REQUEST_ID, requestId)
                .putExtra(EXTRA_SCREEN_SHARE_INTERVAL_MS, safeInterval));
        showStatusNotification("선생님이 화면 공유를 요청했습니다. 앱을 열어 승인해 주세요.");

        ScheduledFuture<?> previousTimeout = screenShareConsentTimeout;
        if (previousTimeout != null) previousTimeout.cancel(false);
        screenShareConsentTimeout = executor.schedule(() -> {
            String pending = getSharedPreferences(PREFS, MODE_PRIVATE)
                    .getString(PREF_PENDING_SCREEN_SHARE_REQUEST, "");
            if (!requestId.equals(pending)) return;
            clearPendingScreenShareRequest();
            completeCommand(requestId, false, "SCREEN_SHARE_CONSENT_TIMEOUT", "화면 공유 승인 시간이 지나 요청을 취소했습니다.");
            showStatusNotification("화면 공유 요청 시간이 지났습니다.");
        }, 90, TimeUnit.SECONDS);
    }

    private void handleScreenShareResponse(Intent intent) {
        String requestId = intent.getStringExtra(EXTRA_SCREEN_SHARE_REQUEST_ID);
        String pending = getSharedPreferences(PREFS, MODE_PRIVATE)
                .getString(PREF_PENDING_SCREEN_SHARE_REQUEST, "");
        if (requestId == null || requestId.isBlank() || !requestId.equals(pending)) return;

        cancelScreenShareConsentTimeout();
        clearPendingScreenShareRequest();
        if (!intent.getBooleanExtra(EXTRA_SCREEN_SHARE_APPROVED, false)) {
            completeCommand(requestId, false, "SCREEN_SHARE_DECLINED", "전자칠판에서 화면 공유를 승인하지 않았습니다.");
            showStatusNotification("화면 공유 요청을 승인하지 않았습니다.");
            return;
        }

        int resultCode = intent.getIntExtra(EXTRA_SCREEN_SHARE_RESULT_CODE, 0);
        Intent resultData;
        if (Build.VERSION.SDK_INT >= 33) {
            resultData = intent.getParcelableExtra(EXTRA_SCREEN_SHARE_RESULT_DATA, Intent.class);
        } else {
            resultData = (Intent) intent.getParcelableExtra(EXTRA_SCREEN_SHARE_RESULT_DATA);
        }
        if (resultData == null || resultCode != android.app.Activity.RESULT_OK) {
            completeCommand(requestId, false, "SCREEN_SHARE_PERMISSION_DENIED", "Android 화면 공유 권한을 받지 못했습니다.");
            showStatusNotification("Android 화면 공유 권한이 허용되지 않았습니다.");
            return;
        }

        long interval = getSharedPreferences(PREFS, MODE_PRIVATE).getLong(PREF_SCREEN_SHARE_INTERVAL, 2_000L);
        try {
            screenCapture = new AndroidScreenCapture(this, resultCode, resultData, interval,
                    new AndroidScreenCapture.Listener() {
                        @Override
                        public void onFrame(JSONObject frame) {
                            latestScreenFrame = frame;
                        }

                        @Override
                        public void onStopped() {
                            screenCapture = null;
                            screenSharingEnabled = false;
                            latestScreenFrame = null;
                            WebSocket socket = currentSocket;
                            if (socket != null && running.get()) startHeartbeat(socket);
                            publishStatus(true, "화면 공유가 종료되었습니다. 서버 연결은 유지 중입니다.");
                        }
                    });
            screenSharingEnabled = true;
            WebSocket socket = currentSocket;
            if (socket != null) startHeartbeat(socket);
            publishStatus(true, "화면 공유 중 · 전자칠판 화면을 선생님이 확인하고 있습니다.");
            showStatusNotification("화면 공유 중 · Classroom 화면 전송을 누르면 언제든 종료할 수 있습니다.");
            completeCommand(requestId, true, "SCREEN_SHARE_STARTED", "승인된 화면 공유를 시작했습니다.");
        } catch (Exception exception) {
            screenCapture = null;
            screenSharingEnabled = false;
            latestScreenFrame = null;
            WebSocket socket = currentSocket;
            if (socket != null) startHeartbeat(socket);
            completeCommand(requestId, false, "SCREEN_SHARE_START_FAILED", "화면 공유를 시작하지 못했습니다. 다시 시도해 주세요.");
            showStatusNotification("화면 공유를 시작하지 못했습니다.");
        }
    }

    private void stopScreenCapture(boolean teacherRequested) {
        String pending = getSharedPreferences(PREFS, MODE_PRIVATE)
                .getString(PREF_PENDING_SCREEN_SHARE_REQUEST, "");
        if (!pending.isBlank()) {
            clearPendingScreenShareRequest();
            cancelScreenShareConsentTimeout();
            if (teacherRequested) {
                completeCommand(pending, false, "SCREEN_SHARE_CANCELLED", "선생님이 화면 공유 요청을 취소했습니다.");
            }
        }

        AndroidScreenCapture capture = screenCapture;
        screenCapture = null;
        boolean hadCapture = screenSharingEnabled || capture != null;
        screenSharingEnabled = false;
        latestScreenFrame = null;
        if (capture != null) capture.stop();

        WebSocket socket = currentSocket;
        if (socket != null && running.get()) startHeartbeat(socket);
        if (hadCapture) {
            publishStatus(true, "화면 공유가 종료되었습니다. 서버 연결은 유지 중입니다.");
        }
    }

    private void clearPendingScreenShareRequest() {
        getSharedPreferences(PREFS, MODE_PRIVATE).edit()
                .remove(PREF_PENDING_SCREEN_SHARE_REQUEST)
                .remove(PREF_SCREEN_SHARE_INTERVAL)
                .apply();
    }

    private void cancelScreenShareConsentTimeout() {
        ScheduledFuture<?> timeout = screenShareConsentTimeout;
        screenShareConsentTimeout = null;
        if (timeout != null) timeout.cancel(false);
    }

    private void completeCommand(String requestId, boolean success, String code, String message) {
        WebSocket socket = currentSocket;
        if (socket == null || enrollment == null || requestId == null || requestId.isBlank()) return;
        try {
            sendEnvelope(socket, "COMMAND_RESULT", new JSONObject()
                    .put("requestId", requestId)
                    .put("deviceId", enrollment.deviceId)
                    .put("success", success)
                    .put("code", code)
                    .put("message", message)
                    .put("appliedAtUtc", Instant.now().toString()));
        } catch (Exception ignored) {
        }
    }

    private void verifyExitPin(String requestId, String pin) {
        WebSocket socket = currentSocket;
        if (requestId == null || requestId.isBlank() || pin == null || socket == null) {
            Intent result = new Intent(ACTION_PIN_RESULT)
                    .setPackage(getPackageName())
                    .putExtra(EXTRA_REQUEST_ID, requestId)
                    .putExtra(EXTRA_APPROVED, false)
                    .putExtra(EXTRA_CODE, "SERVER_OFFLINE");
            sendBroadcast(result);
            return;
        }
        if (pin.length() < 6 || pin.length() > 64) {
            Intent result = new Intent(ACTION_PIN_RESULT)
                    .setPackage(getPackageName())
                    .putExtra(EXTRA_REQUEST_ID, requestId)
                    .putExtra(EXTRA_APPROVED, false)
                    .putExtra(EXTRA_CODE, "EXIT_PIN_INVALID");
            sendBroadcast(result);
            return;
        }
        try {
            JSONObject payload = new JSONObject()
                    .put("requestId", requestId)
                    .put("pin", pin);
            sendEnvelope(socket, "DEVICE_EXIT_PIN_VERIFICATION_REQUEST", payload);
        } catch (Exception exception) {
            Intent result = new Intent(ACTION_PIN_RESULT)
                    .setPackage(getPackageName())
                    .putExtra(EXTRA_REQUEST_ID, requestId)
                    .putExtra(EXTRA_APPROVED, false)
                    .putExtra(EXTRA_CODE, "SERVER_OFFLINE");
            sendBroadcast(result);
        }
    }

    private void publishPinResult(JSONObject payload) {
        Intent result = new Intent(ACTION_PIN_RESULT)
                .setPackage(getPackageName())
                .putExtra(EXTRA_REQUEST_ID, payload.optString("requestId", ""))
                .putExtra(EXTRA_APPROVED, payload.optBoolean("approved", false))
                .putExtra(EXTRA_CODE, payload.optString("code", "EXIT_PIN_FAILED"));
        sendBroadcast(result);
    }

    private void sendEnvelope(WebSocket socket, String type, JSONObject payload) {
        try {
            JSONObject envelope = new JSONObject()
                    .put("version", PROTOCOL_VERSION)
                    .put("type", type)
                    .put("messageId", UUID.randomUUID().toString())
                    .put("timestampUtc", Instant.now().toString())
                    .put("payload", payload);
            socket.send(envelope.toString());
        } catch (Exception exception) {
            Log.i(TAG, "Protocol message could not be composed.");
        }
    }

    private Integer readBatteryPercent() {
        BatteryManager battery = (BatteryManager) getSystemService(BATTERY_SERVICE);
        if (battery == null) return null;
        int value = battery.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY);
        return value < 0 ? null : Math.min(100, value);
    }

    private String readNetworkStatus() {
        ConnectivityManager manager = (ConnectivityManager) getSystemService(CONNECTIVITY_SERVICE);
        if (manager == null) return "unknown";
        Network network = manager.getActiveNetwork();
        NetworkCapabilities capabilities = network == null ? null : manager.getNetworkCapabilities(network);
        if (capabilities == null) return "offline";
        if (capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) return "wifi";
        if (capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)) return "ethernet";
        if (capabilities.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR)) return "cellular";
        return "online";
    }

    private boolean isFocusEnabled() {
        return getSharedPreferences(PREFS, MODE_PRIVATE).getBoolean(PREF_FOCUS_ENABLED, false);
    }

    private void loadFocusState() {
        android.content.SharedPreferences preferences = getSharedPreferences(PREFS, MODE_PRIVATE);
        focusEnabled.set(preferences.getBoolean(PREF_FOCUS_ENABLED, false));
        focusMode = preferences.getString(PREF_FOCUS_MODE, "message");
        focusMessage = preferences.getString(PREF_FOCUS_MESSAGE, "수업에 집중해 주세요.");
    }

    private boolean syncFocusOverlay() {
        if (!focusEnabled.get()) {
            if (focusOverlayController != null) focusOverlayController.remove();
            focusOverlayApplied.set(false);
            return true;
        }
        if (focusOverlaySuppressed) {
            focusOverlayApplied.set(false);
            return FocusOverlayController.hasPermission(this);
        }
        boolean applied = focusOverlayController != null
                && focusOverlayController.show(focusMode, focusMessage);
        focusOverlayApplied.set(applied);
        return applied;
    }

    private void saveFocusState(boolean enabled, String mode, String message) {
        focusEnabled.set(enabled);
        focusMode = mode;
        focusMessage = message;
        getSharedPreferences(PREFS, MODE_PRIVATE).edit()
                .putBoolean(PREF_FOCUS_ENABLED, enabled)
                .putString(PREF_FOCUS_MODE, mode)
                .putString(PREF_FOCUS_MESSAGE, message)
                .apply();
    }

    private void saveNotice(String text, int displaySeconds) {
        getSharedPreferences(PREFS, MODE_PRIVATE).edit().putString(PREF_NOTICE, text).apply();
        ScheduledFuture<?> previous = noticeExpiryTask;
        if (previous != null) previous.cancel(false);
        noticeExpiryTask = executor.schedule(() -> {
            getSharedPreferences(PREFS, MODE_PRIVATE).edit().remove(PREF_NOTICE).apply();
            sendBroadcast(new Intent(ACTION_NOTICE)
                    .setPackage(getPackageName())
                    .putExtra(EXTRA_NOTICE_TEXT, "")
                    .putExtra(EXTRA_NOTICE_SECONDS, 0));
            updateNotification(currentStatus);
        }, Math.max(1, displaySeconds), TimeUnit.SECONDS);
    }

    private void publishStatus(boolean connected, String status) {
        currentStatus = status;
        getSharedPreferences(PREFS, MODE_PRIVATE).edit()
                .putBoolean(PREF_CONNECTED, connected)
                .putString(PREF_STATUS, status)
                .apply();
        Intent update = new Intent(ACTION_STATUS)
                .setPackage(getPackageName())
                .putExtra(EXTRA_CONNECTED, connected)
                .putExtra(EXTRA_STATUS, status);
        sendBroadcast(update);
        updateNotification(status);
    }

    private void showMessageNotification(String text) {
        Intent openApp = new Intent(this, MainActivity.class)
                .addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP | Intent.FLAG_ACTIVITY_CLEAR_TOP);
        PendingIntent pending = PendingIntent.getActivity(
                this, 0, openApp, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        Notification notification = new NotificationCompat.Builder(this, CHANNEL_ID)
                .setSmallIcon(android.R.drawable.ic_dialog_info)
                .setContentTitle("선생님 안내")
                .setContentText(text)
                .setStyle(new NotificationCompat.BigTextStyle().bigText(text))
                .setContentIntent(pending)
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .setCategory(NotificationCompat.CATEGORY_SERVICE)
                .build();
        NotificationManager manager = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        manager.notify(NOTIFICATION_ID, notification);
    }

    private void showStatusNotification(String text) {
        updateNotification(text);
    }

    private Notification buildNotification(String text) {
        Intent openApp = new Intent(this, MainActivity.class)
                .addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP | Intent.FLAG_ACTIVITY_CLEAR_TOP);
        PendingIntent pending = PendingIntent.getActivity(
                this, 0, openApp, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        NotificationCompat.Builder builder = new NotificationCompat.Builder(this, CHANNEL_ID)
                .setSmallIcon(android.R.drawable.ic_lock_idle_lock)
                .setContentTitle("Classroom 기기 연결")
                .setContentText(text)
                .setContentIntent(pending)
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .setCategory(NotificationCompat.CATEGORY_SERVICE);
        if (screenSharingEnabled) {
            Intent stopShare = new Intent(this, StudentConnectionService.class).setAction(ACTION_STOP_SCREEN_SHARE);
            PendingIntent stopPending = PendingIntent.getService(
                    this, 1, stopShare, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
            builder.addAction(android.R.drawable.ic_media_pause, "화면 공유 종료", stopPending);
        }
        return builder.build();
    }

    private void updateNotification(String text) {
        NotificationManager manager = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        if (manager != null) manager.notify(NOTIFICATION_ID, buildNotification(text));
    }

    private void createNotificationChannel() {
        NotificationChannel channel = new NotificationChannel(
                CHANNEL_ID,
                "Classroom 기기 연결",
                NotificationManager.IMPORTANCE_LOW);
        channel.setDescription("학교 서버 연결 상태와 선생님 안내를 표시합니다.");
        channel.setShowBadge(false);
        NotificationManager manager = getSystemService(NotificationManager.class);
        if (manager != null) manager.createNotificationChannel(channel);
    }
}
