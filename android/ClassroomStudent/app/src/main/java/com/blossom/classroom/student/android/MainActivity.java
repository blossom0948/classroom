package com.blossom.classroom.student.android;

import android.Manifest;
import android.animation.ValueAnimator;
import android.app.ActivityManager;
import android.app.admin.DevicePolicyManager;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.media.projection.MediaProjectionManager;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.text.InputFilter;
import android.text.InputType;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.View;
import android.view.Window;
import android.view.WindowManager;
import android.view.animation.DecelerateInterpolator;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import androidx.annotation.Nullable;
import androidx.appcompat.app.AppCompatActivity;
import androidx.appcompat.app.AlertDialog;
import androidx.core.app.ActivityCompat;
import androidx.core.content.ContextCompat;
import androidx.core.content.FileProvider;
import androidx.core.graphics.Insets;
import androidx.core.view.ViewCompat;
import androidx.core.view.WindowInsetsCompat;
import androidx.dynamicanimation.animation.DynamicAnimation;
import androidx.dynamicanimation.animation.SpringAnimation;
import androidx.dynamicanimation.animation.SpringForce;

import com.google.android.material.button.MaterialButton;
import com.google.android.material.card.MaterialCardView;
import com.google.android.material.dialog.MaterialAlertDialogBuilder;
import com.google.android.material.textfield.TextInputEditText;
import com.google.android.material.textfield.TextInputLayout;

import org.json.JSONObject;

import java.util.Locale;
import java.util.UUID;
import java.io.File;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;

import okhttp3.HttpUrl;
import okhttp3.MediaType;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.RequestBody;
import okhttp3.Response;

/** Touch-first onboarding and visible focus-mode surface for an Android classroom board. */
public final class MainActivity extends AppCompatActivity {
    private static final int COLOR_BG = Color.rgb(246, 245, 251);
    private static final int COLOR_INK = Color.rgb(27, 34, 52);
    private static final int COLOR_MUTED = Color.rgb(102, 112, 137);
    private static final int COLOR_PURPLE = Color.rgb(101, 88, 211);
    private static final String DEFAULT_SERVER = "https://classroom-api.blossom0948.cloud";
    private static final String PREFS = "classroom_app_state";
    private static final String PREF_FOCUS_ENABLED = "focus_enabled";
    private static final String PREF_FOCUS_MODE = "focus_mode";
    private static final String PREF_FOCUS_MESSAGE = "focus_message";
    private static final String PREF_NOTICE = "notice_text";
    private static final String PREF_CONNECTED = "server_connected";
    private static final String PREF_STATUS = "server_status";
    private static final String PREF_PENDING_SCREEN_SHARE_REQUEST = "pending_screen_share_request";
    private static final String PREF_PENDING_REMOTE_ACTION = "pending_remote_action";
    private static final String PREF_LAST_UPDATE_CHECK = "last_update_check_utc_ms";
    private static final long UPDATE_CHECK_INTERVAL_MS = TimeUnit.HOURS.toMillis(12);
    private static final int REQUEST_INSTALL_PERMISSION = 106;
    private static final int REQUEST_SCREEN_CAPTURE_PERMISSION = 107;

    private final ExecutorService enrollmentExecutor = Executors.newSingleThreadExecutor();
    private final OkHttpClient enrollmentClient = new OkHttpClient.Builder()
            .connectTimeout(20, java.util.concurrent.TimeUnit.SECONDS)
            .readTimeout(20, java.util.concurrent.TimeUnit.SECONDS)
            .build();
    private final OkHttpClient updateClient = new OkHttpClient.Builder()
            .connectTimeout(30, TimeUnit.SECONDS)
            .readTimeout(90, TimeUnit.SECONDS)
            .build();
    private final AndroidUpdateManager updateManager = new AndroidUpdateManager(updateClient);
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private final Runnable periodicUpdateCheck = new Runnable() {
        @Override
        public void run() {
            checkForUpdatesIfDue(false);
            mainHandler.postDelayed(this, UPDATE_CHECK_INTERVAL_MS);
        }
    };

    private SecureDeviceStore.Enrollment enrollment;
    private FrameLayout root;
    private MaterialButton enrollButton;
    private MaterialButton updateButton;
    private TextInputEditText joinCodeInput;
    private TextView connectionBadge;
    private TextView registeredStatus;
    private TextView noticeCard;
    private boolean connected;
    private boolean receiverRegistered;
    private String currentStatus = "연결 준비 중";
    private String pendingPinRequestId;
    private AlertDialog pendingExitDialog;
    private AlertDialog updateDialog;
    private AlertDialog updateProgressDialog;
    private AlertDialog screenShareConsentDialog;
    private AndroidUpdateManager.Update pendingUpdate;
    private File pendingUpdateApk;
    private String activeScreenShareRequestId;
    private boolean updateCheckInFlight;
    private boolean updateDialogVisible;
    private boolean installPermissionSettingsOpen;
    private boolean screenShareConsentDialogVisible;
    private boolean screenCapturePermissionInFlight;
    private String remoteActionInFlight;
    private boolean compactLayout;

    private final BroadcastReceiver serviceReceiver = new BroadcastReceiver() {
        @Override
        public void onReceive(Context context, Intent intent) {
            String action = intent.getAction();
            if (StudentConnectionService.ACTION_STATUS.equals(action)) {
                connected = intent.getBooleanExtra(StudentConnectionService.EXTRA_CONNECTED, false);
                currentStatus = intent.getStringExtra(StudentConnectionService.EXTRA_STATUS);
                updateStatusViews();
            } else if (StudentConnectionService.ACTION_FOCUS.equals(action)) {
                renderFromSavedFocusState();
            } else if (StudentConnectionService.ACTION_NOTICE.equals(action)) {
                showNotice(intent.getStringExtra(StudentConnectionService.EXTRA_NOTICE_TEXT));
            } else if (StudentConnectionService.ACTION_PIN_RESULT.equals(action)) {
                onPinResult(intent);
            } else if (StudentConnectionService.ACTION_SCREEN_SHARE_REQUEST.equals(action)) {
                showPendingScreenShareRequest();
            } else if (StudentConnectionService.ACTION_REMOTE_COMMAND.equals(action)) {
                executePendingRemoteAction();
            }
        }
    };

    @Override
    protected void onCreate(@Nullable Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        enrollment = SecureDeviceStore.load(this);
        askForNotificationsIfNeeded();
        if (enrollment != null) startConnectionService();
        renderFromSavedFocusState();
        checkForUpdatesIfDue(false);
    }

    @Override
    protected void onStart() {
        super.onStart();
        registerServiceReceiver();
    }

    @Override
    protected void onResume() {
        super.onResume();
        renderFromSavedFocusState();
        if (enrollment != null && isFocusEnabled()) startConnectionService();
        if (pendingUpdateApk != null && !installPermissionSettingsOpen) {
            continueUpdateInstall();
        }
        if (pendingUpdate != null) {
            showAvailableUpdate();
        }
        showPendingScreenShareRequest();
        executePendingRemoteAction();
        checkForUpdatesIfDue(false);
        mainHandler.removeCallbacks(periodicUpdateCheck);
        mainHandler.postDelayed(periodicUpdateCheck, UPDATE_CHECK_INTERVAL_MS);
    }

    @Override
    protected void onPause() {
        mainHandler.removeCallbacks(periodicUpdateCheck);
        super.onPause();
    }

    @Override
    protected void onStop() {
        if (receiverRegistered) {
            try {
                unregisterReceiver(serviceReceiver);
            } catch (IllegalArgumentException ignored) {
            }
            receiverRegistered = false;
        }
        super.onStop();
    }

    @Override
    protected void onDestroy() {
        mainHandler.removeCallbacksAndMessages(null);
        enrollmentExecutor.shutdownNow();
        updateClient.dispatcher().cancelAll();
        super.onDestroy();
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, @Nullable Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode == REQUEST_SCREEN_CAPTURE_PERMISSION) {
            screenCapturePermissionInFlight = false;
            boolean granted = resultCode == RESULT_OK && data != null;
            completeScreenShareRequest(granted, resultCode, data);
            return;
        }
        if (requestCode != REQUEST_INSTALL_PERMISSION || pendingUpdateApk == null) return;
        installPermissionSettingsOpen = false;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && getPackageManager().canRequestPackageInstalls()) {
            continueUpdateInstall();
        } else {
            pendingUpdateApk = null;
            new MaterialAlertDialogBuilder(this)
                    .setTitle("설치 권한이 필요합니다")
                    .setMessage("자동 다운로드는 완료됐지만 Android 설치 허용이 꺼져 있어 업데이트를 적용하지 못했습니다.")
                    .setPositiveButton("확인", null)
                    .show();
        }
    }

    private void registerServiceReceiver() {
        if (receiverRegistered) return;
        IntentFilter filter = new IntentFilter();
        filter.addAction(StudentConnectionService.ACTION_STATUS);
        filter.addAction(StudentConnectionService.ACTION_FOCUS);
        filter.addAction(StudentConnectionService.ACTION_NOTICE);
        filter.addAction(StudentConnectionService.ACTION_PIN_RESULT);
        filter.addAction(StudentConnectionService.ACTION_SCREEN_SHARE_REQUEST);
        filter.addAction(StudentConnectionService.ACTION_REMOTE_COMMAND);
        ContextCompat.registerReceiver(this, serviceReceiver, filter, ContextCompat.RECEIVER_NOT_EXPORTED);
        receiverRegistered = true;
    }

    private void askForNotificationsIfNeeded() {
        if (Build.VERSION.SDK_INT >= 33
                && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            ActivityCompat.requestPermissions(this, new String[]{Manifest.permission.POST_NOTIFICATIONS}, 105);
        }
    }

    private boolean isFocusEnabled() {
        return getSharedPreferences(PREFS, MODE_PRIVATE).getBoolean(PREF_FOCUS_ENABLED, false);
    }

    private void renderFromSavedFocusState() {
        if (isFinishing()) return;
        android.content.SharedPreferences preferences = getSharedPreferences(PREFS, MODE_PRIVATE);
        connected = preferences.getBoolean(PREF_CONNECTED, false);
        currentStatus = preferences.getString(PREF_STATUS, "연결 준비 중");
        if (isFocusEnabled()) {
            renderFocusScreen();
        } else {
            enrollment = SecureDeviceStore.load(this);
            renderMainScreen();
            if (pendingUpdate != null) showAvailableUpdate();
        }
    }

    private void renderMainScreen() {
        restoreSystemBars();
        getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        root = new FrameLayout(this);
        root.setBackground(new GradientDrawable(
                GradientDrawable.Orientation.TL_BR,
                new int[]{0xFFF8F7FC, 0xFFEDEBF7}));

        int widthDp = (int) (getResources().getDisplayMetrics().widthPixels
                / getResources().getDisplayMetrics().density);
        compactLayout = widthDp < 430;
        boolean wide = widthDp >= 720
                && getResources().getConfiguration().orientation
                == android.content.res.Configuration.ORIENTATION_LANDSCAPE;
        if (wide) applyImmersiveSystemBars();
        else restoreSystemBars();
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setClipToPadding(false);
        int outerInset = compactLayout ? 16 : 22;
        scroll.setPadding(dp(outerInset), dp(outerInset), dp(outerInset), dp(outerInset));
        if (Build.VERSION.SDK_INT >= 35 && !wide) {
            int baseInset = dp(outerInset);
            ViewCompat.setOnApplyWindowInsetsListener(scroll, (view, insets) -> {
                Insets bars = insets.getInsets(WindowInsetsCompat.Type.systemBars());
                view.setPadding(baseInset + bars.left, baseInset + bars.top,
                        baseInset + bars.right, baseInset + bars.bottom);
                return insets;
            });
            ViewCompat.requestApplyInsets(scroll);
        }
        LinearLayout split = new LinearLayout(this);
        split.setOrientation(wide ? LinearLayout.HORIZONTAL : LinearLayout.VERTICAL);
        split.setGravity(wide ? Gravity.CENTER : Gravity.TOP | Gravity.CENTER_HORIZONTAL);
        split.setClipChildren(false);
        split.setClipToPadding(false);

        View hero = buildHeroPanel();
        View connection = buildConnectionPanel();
        if (wide) {
            LinearLayout.LayoutParams heroParams = new LinearLayout.LayoutParams(0, -1, 0.92f);
            heroParams.setMarginEnd(dp(18));
            split.addView(hero, heroParams);
            split.addView(connection, new LinearLayout.LayoutParams(0, -1, 1.08f));
        } else {
            LinearLayout.LayoutParams heroParams = new LinearLayout.LayoutParams(-1, dp(compactLayout ? 292 : 350));
            heroParams.bottomMargin = dp(14);
            split.addView(hero, heroParams);
            split.addView(connection, new LinearLayout.LayoutParams(-1, -2));
        }

        scroll.addView(split, new ScrollView.LayoutParams(-1, wide ? -1 : -2));
        root.addView(scroll, new FrameLayout.LayoutParams(-1, -1));
        setContentView(root);
        animateScreenEntrance(scroll);
        showSavedNoticeIfPresent();
        updateStatusViews();
    }

    private View buildHeroPanel() {
        FrameLayout panel = new FrameLayout(this);
        GradientDrawable gradient = new GradientDrawable(
                GradientDrawable.Orientation.TL_BR,
                new int[]{0xFF28234F, 0xFF6558D3, 0xFF8074E6});
        gradient.setCornerRadius(dp(32));
        panel.setBackground(gradient);
        panel.setElevation(0);

        View orb = new View(this);
        GradientDrawable orbShape = new GradientDrawable();
        orbShape.setShape(GradientDrawable.OVAL);
        orbShape.setColor(0x20FFFFFF);
        orb.setBackground(orbShape);
        FrameLayout.LayoutParams orbParams = new FrameLayout.LayoutParams(dp(230), dp(230), Gravity.TOP | Gravity.END);
        orbParams.setMargins(0, -dp(68), -dp(52), 0);
        panel.addView(orb, orbParams);

        View orbSmall = new View(this);
        GradientDrawable orbSmallShape = new GradientDrawable();
        orbSmallShape.setShape(GradientDrawable.OVAL);
        orbSmallShape.setColor(0x12FFFFFF);
        orbSmall.setBackground(orbSmallShape);
        FrameLayout.LayoutParams smallParams = new FrameLayout.LayoutParams(dp(112), dp(112), Gravity.BOTTOM | Gravity.END);
        smallParams.setMargins(0, 0, dp(28), dp(34));
        panel.addView(orbSmall, smallParams);

        LinearLayout content = new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);
        content.setGravity(Gravity.CENTER_VERTICAL);
        int horizontalInset = compactLayout ? 24 : 34;
        content.setPadding(dp(horizontalInset), dp(compactLayout ? 22 : 32), dp(horizontalInset), dp(compactLayout ? 22 : 32));
        content.setClipToPadding(false);

        TextView brand = text("CLASSROOM  ·  ANDROID", 13, 0xFFDCD8FF, true);
        brand.setLetterSpacing(0.12f);
        content.addView(brand);
        addGap(content, compactLayout ? 16 : 24);

        TextView title = text("전자칠판을\n수업 흐름에 연결", compactLayout ? 29 : 34, Color.WHITE, true);
        title.setLineSpacing(dp(4), 1.0f);
        content.addView(title);
        addGap(content, compactLayout ? 10 : 16);

        TextView description = text(
                "학교 코드로 등록하고, 선생님의 안내와 집중 화면을 전자칠판에서 확인합니다.",
                compactLayout ? 15 : 17,
                0xFFEAE8FF,
                false);
        description.setLineSpacing(dp(5), 1.08f);
        content.addView(description);
        addGap(content, compactLayout ? 18 : 28);

        LinearLayout chips = new LinearLayout(this);
        chips.setOrientation(LinearLayout.HORIZONTAL);
        chips.addView(featureChip("실시간 연결"));
        View chipGap = new View(this);
        chips.addView(chipGap, new LinearLayout.LayoutParams(dp(9), 1));
        chips.addView(featureChip("집중 화면"));
        content.addView(chips);

        FrameLayout.LayoutParams contentParams = new FrameLayout.LayoutParams(-1, -1);
        panel.addView(content, contentParams);
        return panel;
    }

    private TextView featureChip(String label) {
        TextView chip = text(label, 13, Color.WHITE, true);
        chip.setGravity(Gravity.CENTER);
        chip.setPadding(dp(compactLayout ? 11 : 14), dp(compactLayout ? 7 : 9), dp(compactLayout ? 11 : 14), dp(compactLayout ? 7 : 9));
        GradientDrawable background = new GradientDrawable();
        background.setColor(0x22FFFFFF);
        background.setCornerRadius(dp(18));
        background.setStroke(dp(1), 0x36FFFFFF);
        chip.setBackground(background);
        return chip;
    }

    private View buildConnectionPanel() {
        MaterialCardView card = createCard();
        LinearLayout content = new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);
        int contentInset = compactLayout ? 20 : 30;
        content.setPadding(dp(contentInset), dp(compactLayout ? 20 : 28), dp(contentInset), dp(compactLayout ? 20 : 28));

        LinearLayout topRow = new LinearLayout(this);
        topRow.setGravity(Gravity.CENTER_VERTICAL);
        TextView eyebrow = text(enrollment == null ? "기기 등록" : "기기 관리", 13, COLOR_MUTED, true);
        eyebrow.setLetterSpacing(0.06f);
        topRow.addView(eyebrow, new LinearLayout.LayoutParams(0, -2, 1));
        connectionBadge = text(connected ? "연결됨" : "연결 대기", 13, connected ? 0xFF177A5A : 0xFF8B6500, true);
        connectionBadge.setGravity(Gravity.CENTER);
        connectionBadge.setPadding(dp(13), dp(8), dp(13), dp(8));
        connectionBadge.setBackground(pillBackground(connected ? 0xFFE7F5EF : 0xFFFFF4D9));
        topRow.addView(connectionBadge);
        content.addView(topRow);
        addGap(content, compactLayout ? 16 : 24);

        if (enrollment == null) {
            buildEnrollmentForm(content);
        } else {
            buildRegisteredDevice(content);
        }

        card.addView(content);
        return card;
    }

    private void buildEnrollmentForm(LinearLayout content) {
        content.addView(text("전자칠판 등록", compactLayout ? 24 : 27, COLOR_INK, true));
        addGap(content, 6);
        TextView description = text(
                "관리자 화면에서 발급한 8자리 학생 코드를 입력하세요.",
                15,
                COLOR_MUTED,
                false);
        description.setLineSpacing(dp(3), 1.0f);
        content.addView(description);
        addGap(content, compactLayout ? 14 : 22);

        TextInputLayout codeLayout = new TextInputLayout(this);
        codeLayout.setHint("학생 코드");
        codeLayout.setBoxBackgroundMode(TextInputLayout.BOX_BACKGROUND_OUTLINE);
        codeLayout.setBoxCornerRadii(dp(15), dp(15), dp(15), dp(15));
        codeLayout.setBoxStrokeColor(0xFFDAD8E6);
        joinCodeInput = new TextInputEditText(codeLayout.getContext());
        joinCodeInput.setSingleLine(true);
        joinCodeInput.setTextSize(22);
        joinCodeInput.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        joinCodeInput.setLetterSpacing(0.17f);
        joinCodeInput.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_CAP_CHARACTERS);
        joinCodeInput.setFilters(new InputFilter[]{new InputFilter.LengthFilter(8), this::filterJoinCode});
        codeLayout.addView(joinCodeInput, new TextInputLayout.LayoutParams(-1, dp(compactLayout ? 56 : 62)));
        content.addView(codeLayout, new LinearLayout.LayoutParams(-1, -2));
        addGap(content, compactLayout ? 12 : 18);

        enrollButton = primaryButton("기기 등록 및 연결");
        enrollButton.setOnClickListener(view -> enrollDevice());
        addSpringPress(enrollButton);
        content.addView(enrollButton, new LinearLayout.LayoutParams(-1, dp(compactLayout ? 52 : 58)));
        addGap(content, compactLayout ? 10 : 16);

        TextView note = text(
                "등록 정보는 이 기기에 안전하게 저장되며, 다시 열어도 연결을 이어갑니다.",
                compactLayout ? 12 : 13,
                COLOR_MUTED,
                false);
        note.setLineSpacing(dp(3), 1.0f);
        content.addView(note);
        addGap(content, compactLayout ? 10 : 14);
        TextView status = text(currentStatus, 14, connected ? 0xFF177A5A : COLOR_MUTED, true);
        status.setTag("enrollment-status");
        content.addView(status);
    }

    private CharSequence filterJoinCode(CharSequence source, int start, int end, android.text.Spanned dest, int dstart, int dend) {
        String raw = source.subSequence(start, end).toString().toUpperCase(Locale.ROOT);
        String filtered = raw.replaceAll("[^ABCDEFGHJKLMNPQRSTUVWXYZ23456789]", "");
        return filtered.equals(raw) ? null : filtered;
    }

    private void buildRegisteredDevice(LinearLayout content) {
        content.addView(text("서버 연결", 27, COLOR_INK, true));
        addGap(content, 8);
        TextView description = text("이 전자칠판은 학교의 Classroom 기기로 등록되어 있습니다.", 15, COLOR_MUTED, false);
        content.addView(description);
        addGap(content, 22);

        MaterialCardView stateCard = createCard();
        stateCard.setCardBackgroundColor(0xFFF6F7FB);
        stateCard.setCardElevation(0);
        LinearLayout stateContent = new LinearLayout(this);
        stateContent.setOrientation(LinearLayout.VERTICAL);
        stateContent.setPadding(dp(18), dp(17), dp(18), dp(17));
        stateContent.addView(text("연결 상태", 13, COLOR_MUTED, true));
        addGap(stateContent, 6);
        registeredStatus = text(currentStatus, 17, connected ? 0xFF177A5A : COLOR_INK, true);
        stateContent.addView(registeredStatus);
        stateCard.addView(stateContent);
        content.addView(stateCard, new LinearLayout.LayoutParams(-1, -2));
        addGap(content, 18);

        MaterialButton reconnect = primaryButton("서버 연결 다시 확인");
        reconnect.setOnClickListener(view -> startConnectionService());
        addSpringPress(reconnect);
        content.addView(reconnect, new LinearLayout.LayoutParams(-1, dp(56)));
        addGap(content, 12);

        addFocusOverlayPermissionControl(content);

        DevicePolicyManager policyManager = getSystemService(DevicePolicyManager.class);
        if (policyManager != null && policyManager.isLockTaskPermitted(getPackageName())) {
            MaterialButton kioskButton = secondaryButton("전자칠판 전용 모드 시작");
            kioskButton.setOnClickListener(view -> confirmKioskMode());
            addSpringPress(kioskButton);
            content.addView(kioskButton, new LinearLayout.LayoutParams(-1, dp(52)));
            addGap(content, 12);
        }

        addUpdateControls(content);
        addGap(content, 16);

        MaterialButton exitButton = secondaryButton("관리자 PIN으로 앱 종료");
        exitButton.setOnClickListener(view -> promptAdministratorExit());
        addSpringPress(exitButton);
        content.addView(exitButton, new LinearLayout.LayoutParams(-1, dp(52)));
        addGap(content, 14);

        noticeCard = text("", 15, COLOR_INK, true);
        noticeCard.setPadding(dp(16), dp(13), dp(16), dp(13));
        noticeCard.setBackground(pillBackground(0xFFECE9FF));
        noticeCard.setVisibility(View.GONE);
        content.addView(noticeCard);
        showSavedNoticeIfPresent();
    }

    private void addFocusOverlayPermissionControl(LinearLayout content) {
        boolean allowed = FocusOverlayController.hasPermission(this);
        content.addView(text("앱 밖에서도 집중 화면 적용", 16, COLOR_INK, true));
        addGap(content, 5);
        TextView explanation = text(
                allowed
                        ? "허용됨 · 다른 앱을 사용 중이어도 선생님 집중 화면을 표시합니다."
                        : "다른 앱 위에 집중 화면을 띄우는 권한입니다. 앱 등록과 서버 연결에는 필요하지 않습니다.",
                13,
                COLOR_MUTED,
                false);
        explanation.setLineSpacing(dp(3), 1.0f);
        content.addView(explanation);
        addGap(content, 9);

        MaterialButton permissionButton = secondaryButton(allowed ? "집중 화면 권한 관리" : "다른 앱 위에 표시 허용");
        permissionButton.setOnClickListener(view -> openFocusOverlaySettings());
        addSpringPress(permissionButton);
        content.addView(permissionButton, new LinearLayout.LayoutParams(-1, dp(50)));
        addGap(content, 7);

        TextView kioskNote = text("앱 전환 자체를 제한하려면 학교 MDM 전용 모드를 사용하세요.", 12, COLOR_MUTED, false);
        content.addView(kioskNote);
        addGap(content, 14);
    }

    private void openFocusOverlaySettings() {
        Intent settingsIntent = new Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                Uri.parse("package:" + getPackageName()));
        try {
            startActivity(settingsIntent);
        } catch (RuntimeException exception) {
            new MaterialAlertDialogBuilder(this)
                    .setTitle("집중 화면 권한 설정을 열지 못했습니다")
                    .setMessage("전자칠판 설정의 앱 권한에서 Classroom Student의 ‘다른 앱 위에 표시’를 허용해 주세요.")
                    .setPositiveButton("확인", null)
                    .show();
        }
    }

    private void addUpdateControls(LinearLayout content) {
        updateButton = secondaryButton("업데이트 확인");
        updateButton.setOnClickListener(view -> checkForUpdatesIfDue(true));
        addSpringPress(updateButton);
        content.addView(updateButton, new LinearLayout.LayoutParams(-1, dp(50)));
        addGap(content, 7);
        TextView note = text(
                "새 버전은 자동으로 확인·다운로드합니다. 설치는 Android 확인이 필요합니다.",
                12,
                COLOR_MUTED,
                false);
        note.setLineSpacing(dp(2), 1.0f);
        content.addView(note);
    }

    private void renderFocusScreen() {
        String mode = getSharedPreferences(PREFS, MODE_PRIVATE).getString(PREF_FOCUS_MODE, "message");
        String message = getSharedPreferences(PREFS, MODE_PRIVATE)
                .getString(PREF_FOCUS_MESSAGE, "수업에 집중해 주세요.");
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        applyImmersiveSystemBars();

        FrameLayout focusRoot = new FrameLayout(this);
        if ("blackScreen".equals(mode)) {
            focusRoot.setBackgroundColor(Color.BLACK);
            setContentView(focusRoot);
            return;
        }

        focusRoot.setBackground(new GradientDrawable(
                GradientDrawable.Orientation.TL_BR,
                new int[]{0xFF17152B, 0xFF282350, 0xFF423B86}));
        LinearLayout content = new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);
        content.setGravity(Gravity.CENTER);
        content.setPadding(dp(32), dp(32), dp(32), dp(32));
        TextView label = text("CLASSROOM  ·  집중 모드", 14, 0xFFC8C2FF, true);
        label.setLetterSpacing(0.09f);
        content.addView(label);
        addGap(content, 22);
        TextView title = text("수업에 집중해 주세요", 34, Color.WHITE, true);
        title.setGravity(Gravity.CENTER);
        content.addView(title);
        addGap(content, 18);
        TextView body = text(message, 21, 0xFFE8E6F8, false);
        body.setGravity(Gravity.CENTER);
        body.setLineSpacing(dp(5), 1.05f);
        LinearLayout.LayoutParams bodyParams = new LinearLayout.LayoutParams(-1, -2);
        bodyParams.leftMargin = dp(28);
        bodyParams.rightMargin = dp(28);
        content.addView(body, bodyParams);
        FrameLayout.LayoutParams contentParams = new FrameLayout.LayoutParams(-1, -1);
        focusRoot.addView(content, contentParams);
        setContentView(focusRoot);
    }

    private void restoreSystemBars() {
        getWindow().clearFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN);
        getWindow().setStatusBarColor(COLOR_BG);
        getWindow().setNavigationBarColor(COLOR_BG);
        getWindow().getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR
                | View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR);
    }

    private void applyImmersiveSystemBars() {
        Window window = getWindow();
        window.setStatusBarColor(Color.BLACK);
        window.setNavigationBarColor(Color.BLACK);
        window.addFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN);
        window.getDecorView().setSystemUiVisibility(
                View.SYSTEM_UI_FLAG_FULLSCREEN
                        | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                        | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                        | View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                        | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                        | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN);
    }

    private void enrollDevice() {
        if (enrollButton == null || joinCodeInput == null) return;
        String origin = DEFAULT_SERVER;
        String joinCode = joinCodeInput.getText() == null
                ? ""
                : joinCodeInput.getText().toString().trim().toUpperCase(Locale.ROOT);
        if (joinCode.length() != 8) {
            joinCodeInput.setError("8자리 코드를 입력해 주세요.");
            joinCodeInput.requestFocus();
            return;
        }

        enrollButton.setEnabled(false);
        enrollButton.setText("기기를 등록하고 있습니다…");
        enrollmentExecutor.execute(() -> {
            try {
                checkServerReady(origin);
                HttpUrl originUrl = HttpUrl.parse(origin);
                if (originUrl == null) throw new IllegalArgumentException("서버 주소가 올바르지 않습니다.");
                HttpUrl endpoint = originUrl.newBuilder()
                        .encodedPath("/api/devices/enroll-code")
                        .query(null)
                        .build();
                JSONObject requestJson = new JSONObject()
                        .put("joinCode", joinCode)
                        .put("deviceName", android.os.Build.MODEL == null ? "Android Board" : android.os.Build.MODEL)
                        .put("agentVersion", BuildConfig.VERSION_NAME);
                RequestBody body = RequestBody.create(
                        MediaType.parse("application/json; charset=utf-8"),
                        requestJson.toString());
                Request request = new Request.Builder().url(endpoint).post(body).build();
                try (Response response = enrollmentClient.newCall(request).execute()) {
                    String responseText = response.body() == null ? "{}" : response.body().string();
                    JSONObject responseJson = new JSONObject(responseText);
                    if (!response.isSuccessful()) {
                        String message = enrollmentResponseError(response.code(), responseJson);
                        runOnUiThread(() -> showEnrollmentError(message));
                        return;
                    }
                    String deviceId = responseJson.optString("deviceId", "");
                    String deviceToken = responseJson.optString("deviceToken", "");
                    if (deviceId.isBlank() || deviceToken.isBlank()) {
                        throw new IllegalStateException("서버 등록 응답이 올바르지 않습니다.");
                    }
                    SecureDeviceStore.Enrollment enrolled = new SecureDeviceStore.Enrollment(
                            origin,
                            deviceId,
                            deviceToken,
                            responseJson.optString("schoolId", ""),
                            responseJson.optString("classId", ""),
                            responseJson.optString("studentId", ""),
                            responseJson.optString("issuedAtUtc", ""));
                    SecureDeviceStore.save(this, enrolled);
                    runOnUiThread(() -> {
                        enrollment = enrolled;
                        connected = false;
                        currentStatus = "등록 완료 · 서버 연결 중";
                        renderMainScreen();
                        startConnectionService();
                    });
                }
            } catch (Exception exception) {
                String message = enrollmentNetworkError(exception);
                runOnUiThread(() -> showEnrollmentError(message));
            }
        });
    }

    private void checkServerReady(String origin) throws Exception {
        HttpUrl base = HttpUrl.parse(origin);
        if (base == null) throw new IllegalArgumentException("서버 주소가 올바르지 않습니다.");
        HttpUrl healthUrl = base.newBuilder().encodedPath("/health/ready").query(null).build();
        Request healthRequest = new Request.Builder().url(healthUrl).get().build();
        try (Response response = enrollmentClient.newCall(healthRequest).execute()) {
            if (!response.isSuccessful() || response.body() == null) {
                throw new java.io.IOException("Classroom 서버 상태 확인 실패 (HTTP " + response.code() + ")");
            }
            JSONObject health = new JSONObject(response.body().string());
            if (!"ready".equalsIgnoreCase(health.optString("status", ""))) {
                throw new java.io.IOException("Classroom 서버가 아직 준비되지 않았습니다.");
            }
        }
    }

    private String enrollmentResponseError(int statusCode, JSONObject response) {
        String code = response.optString("code", "");
        if (statusCode == 401 || "ENROLLMENT_NOT_FOUND".equals(code)) {
            return "학생 코드를 찾을 수 없습니다. 관리자 화면에서 코드를 다시 확인해 주세요.";
        }
        if (statusCode == 429) return "등록 요청이 많습니다. 잠시 기다린 뒤 다시 시도해 주세요.";
        if (statusCode >= 500) return "Classroom 서버가 일시적으로 응답하지 않습니다. 잠시 후 다시 시도해 주세요.";
        return response.optString("message", "학생 등록을 완료하지 못했습니다. 코드를 확인해 주세요.");
    }

    private String enrollmentNetworkError(Exception exception) {
        if (hasCause(exception, java.net.UnknownHostException.class)) {
            return "서버 주소를 찾지 못했습니다. 전자칠판의 Wi-Fi와 인터넷 연결을 확인해 주세요. 기기관리자 설정 문제는 아닙니다.";
        }
        if (hasCause(exception, javax.net.ssl.SSLHandshakeException.class)) {
            return "보안 연결을 확인하지 못했습니다. 전자칠판의 날짜·시간과 네트워크 보안 설정을 확인해 주세요.";
        }
        if (hasCause(exception, java.net.SocketTimeoutException.class)
                || hasCause(exception, java.net.ConnectException.class)) {
            return "Classroom 서버에 연결되지 않습니다. 인터넷 연결 또는 학교 방화벽의 HTTPS(443) 허용을 확인해 주세요. MDM은 등록에 필요하지 않습니다.";
        }
        if (exception instanceof java.io.IOException) {
            return "Classroom 서버 상태를 확인하지 못했습니다. 전자칠판 인터넷 연결을 확인하고 다시 시도해 주세요.";
        }
        return "학생 등록을 완료하지 못했습니다. 코드를 확인하거나 잠시 후 다시 시도해 주세요.";
    }

    private boolean hasCause(Throwable error, Class<? extends Throwable> type) {
        Throwable current = error;
        for (int depth = 0; current != null && depth < 10; depth++, current = current.getCause()) {
            if (type.isInstance(current)) return true;
        }
        return false;
    }

    private void showEnrollmentError(String message) {
        if (enrollButton != null) {
            enrollButton.setEnabled(true);
            enrollButton.setText("기기 등록 및 연결");
        }
        TextView status = findViewWithTag(root, "enrollment-status");
        if (status != null) {
            status.setText(message);
            status.setTextColor(0xFFB3394D);
        } else {
            new MaterialAlertDialogBuilder(this)
                    .setTitle("등록할 수 없습니다")
                    .setMessage(message)
                    .setPositiveButton("확인", null)
                    .show();
        }
    }

    private TextView findViewWithTag(View parent, Object tag) {
        if (parent == null) return null;
        if (tag.equals(parent.getTag()) && parent instanceof TextView textView) return textView;
        if (parent instanceof android.view.ViewGroup group) {
            for (int index = 0; index < group.getChildCount(); index++) {
                TextView found = findViewWithTag(group.getChildAt(index), tag);
                if (found != null) return found;
            }
        }
        return null;
    }

    private void startConnectionService() {
        if (enrollment == null) enrollment = SecureDeviceStore.load(this);
        if (enrollment == null) return;
        Intent service = new Intent(this, StudentConnectionService.class)
                .setAction(StudentConnectionService.ACTION_START);
        try {
            ContextCompat.startForegroundService(this, service);
        } catch (IllegalStateException exception) {
            currentStatus = "앱을 열어 서버 연결을 다시 시작해 주세요.";
            updateStatusViews();
        } catch (SecurityException exception) {
            currentStatus = "Android 연결 권한을 시작하지 못했습니다. 앱을 업데이트하고 다시 시도해 주세요.";
            updateStatusViews();
        }
    }

    private void setFocusOverlayVisible(boolean visible) {
        if (enrollment == null) return;
        Intent service = new Intent(this, StudentConnectionService.class)
                .setAction(visible
                        ? StudentConnectionService.ACTION_SHOW_FOCUS_OVERLAY
                        : StudentConnectionService.ACTION_HIDE_FOCUS_OVERLAY);
        try {
            ContextCompat.startForegroundService(this, service);
        } catch (IllegalStateException | SecurityException ignored) {
            // The visible activity remains the focus surface if the service cannot be reached.
        }
    }

    private void updateStatusViews() {
        if (connectionBadge != null) {
            connectionBadge.setText(connected ? "연결됨" : "연결 대기");
            connectionBadge.setTextColor(connected ? 0xFF177A5A : 0xFF8B6500);
            connectionBadge.setBackground(pillBackground(connected ? 0xFFE7F5EF : 0xFFFFF4D9));
        }
        if (registeredStatus != null) {
            registeredStatus.setText(currentStatus);
            registeredStatus.setTextColor(connected ? 0xFF177A5A : COLOR_INK);
        }
        TextView enrollmentStatus = findViewWithTag(root, "enrollment-status");
        if (enrollmentStatus != null) {
            enrollmentStatus.setText(currentStatus);
            enrollmentStatus.setTextColor(connected ? 0xFF177A5A : COLOR_MUTED);
        }
    }

    private void checkForUpdatesIfDue(boolean force) {
        if (updateCheckInFlight || isFinishing()) return;
        long now = System.currentTimeMillis();
        long lastCheck = getSharedPreferences(PREFS, MODE_PRIVATE).getLong(PREF_LAST_UPDATE_CHECK, 0L);
        if (!force && now - lastCheck < UPDATE_CHECK_INTERVAL_MS) return;

        updateCheckInFlight = true;
        if (updateButton != null) {
            updateButton.setEnabled(false);
            updateButton.setText("업데이트 확인 중…");
        }
        updateManager.checkForUpdate(BuildConfig.VERSION_CODE, new AndroidUpdateManager.CheckCallback() {
            @Override
            public void onSuccess(AndroidUpdateManager.Update update) {
                mainHandler.post(() -> {
                    updateCheckInFlight = false;
                    getSharedPreferences(PREFS, MODE_PRIVATE).edit()
                            .putLong(PREF_LAST_UPDATE_CHECK, System.currentTimeMillis())
                            .apply();
                    restoreUpdateButton();
                    pendingUpdate = update;
                    if (update != null) {
                        showAvailableUpdate();
                    } else if (force && !isFinishing()) {
                        new MaterialAlertDialogBuilder(MainActivity.this)
                                .setTitle("최신 버전입니다")
                                .setMessage("현재 Classroom Student " + BuildConfig.VERSION_NAME + "을 사용 중입니다.")
                                .setPositiveButton("확인", null)
                                .show();
                    }
                });
            }

            @Override
            public void onFailure(String reason) {
                mainHandler.post(() -> {
                    updateCheckInFlight = false;
                    restoreUpdateButton();
                    if (force && !isFinishing()) {
                        new MaterialAlertDialogBuilder(MainActivity.this)
                                .setTitle("업데이트를 확인하지 못했습니다")
                                .setMessage(reason + " 네트워크가 연결되면 다시 시도해 주세요.")
                                .setPositiveButton("확인", null)
                                .show();
                    }
                });
            }
        });
    }

    private void restoreUpdateButton() {
        if (updateButton == null) return;
        updateButton.setEnabled(true);
        updateButton.setText("업데이트 확인");
    }

    private void showAvailableUpdate() {
        if (pendingUpdate == null || updateDialogVisible || isFinishing() || isFocusEnabled()) return;
        updateDialogVisible = true;
        AndroidUpdateManager.Update update = pendingUpdate;
        AlertDialog dialog = new MaterialAlertDialogBuilder(this)
                .setTitle("새 업데이트가 있습니다")
                .setMessage("Classroom Student " + update.versionName
                        + "을 내려받을 수 있습니다. 다운로드 후 Android가 설치를 확인합니다.")
                .setNegativeButton("나중에", (ignored, which) -> pendingUpdate = null)
                .setPositiveButton("다운로드", (ignored, which) -> downloadAndInstallUpdate(update))
                .create();
        updateDialog = dialog;
        dialog.setOnDismissListener(ignored -> {
            updateDialogVisible = false;
            updateDialog = null;
        });
        dialog.show();
    }

    private void downloadAndInstallUpdate(AndroidUpdateManager.Update update) {
        if (update == null || isFinishing()) return;
        if (updateButton != null) {
            updateButton.setEnabled(false);
            updateButton.setText("업데이트 다운로드 중…");
        }
        updateProgressDialog = new MaterialAlertDialogBuilder(this)
                .setTitle("업데이트 준비 중")
                .setMessage("APK를 내려받고 무결성을 확인하고 있습니다.")
                .setCancelable(false)
                .create();
        updateProgressDialog.show();

        File destination = new File(new File(getCacheDir(), "updates"), "Classroom-Student-Android.apk");
        updateManager.download(update, destination, new AndroidUpdateManager.DownloadCallback() {
            @Override
            public void onSuccess(File apk) {
                mainHandler.post(() -> {
                    dismissUpdateProgress();
                    restoreUpdateButton();
                    pendingUpdate = null;
                    pendingUpdateApk = apk;
                    continueUpdateInstall();
                });
            }

            @Override
            public void onFailure(String reason) {
                mainHandler.post(() -> {
                    dismissUpdateProgress();
                    restoreUpdateButton();
                    new MaterialAlertDialogBuilder(MainActivity.this)
                            .setTitle("업데이트를 준비하지 못했습니다")
                            .setMessage(reason)
                            .setPositiveButton("확인", null)
                            .show();
                });
            }
        });
    }

    private void dismissUpdateProgress() {
        if (updateProgressDialog != null) {
            updateProgressDialog.dismiss();
            updateProgressDialog = null;
        }
    }

    private void continueUpdateInstall() {
        if (pendingUpdateApk == null || isFinishing()) return;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && !getPackageManager().canRequestPackageInstalls()) {
            if (installPermissionSettingsOpen) return;
            installPermissionSettingsOpen = true;
            new MaterialAlertDialogBuilder(this)
                    .setTitle("업데이트 설치 허용")
                    .setMessage("Android 설정에서 Classroom의 앱 설치를 허용해 주세요. 설치 파일은 공식 Classroom 업데이트에서 내려받고 SHA-256으로 확인했습니다.")
                    .setNegativeButton("취소", (ignored, which) -> {
                        installPermissionSettingsOpen = false;
                        pendingUpdateApk = null;
                    })
                    .setPositiveButton("설정 열기", (ignored, which) -> {
                        Intent settings = new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                                Uri.parse("package:" + getPackageName()));
                        try {
                            startActivityForResult(settings, REQUEST_INSTALL_PERMISSION);
                        } catch (RuntimeException exception) {
                            installPermissionSettingsOpen = false;
                            pendingUpdateApk = null;
                            showUpdateInstallError("이 기기에서 앱 설치 설정을 열지 못했습니다.");
                        }
                    })
                    .setOnCancelListener(ignored -> {
                        installPermissionSettingsOpen = false;
                        pendingUpdateApk = null;
                    })
                    .show();
            return;
        }

        File apk = pendingUpdateApk;
        try {
            Uri apkUri = FileProvider.getUriForFile(this, getPackageName() + ".updates", apk);
            Intent install = new Intent(Intent.ACTION_VIEW)
                    .setDataAndType(apkUri, "application/vnd.android.package-archive")
                    .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            startActivity(install);
            pendingUpdateApk = null;
        } catch (Exception exception) {
            pendingUpdateApk = null;
            showUpdateInstallError("Android 설치 화면을 열지 못했습니다. 저장 공간과 설치 권한을 확인해 주세요.");
        }
    }

    private void showUpdateInstallError(String message) {
        if (isFinishing()) return;
        new MaterialAlertDialogBuilder(this)
                .setTitle("업데이트를 설치하지 못했습니다")
                .setMessage(message)
                .setPositiveButton("확인", null)
                .show();
    }

    private void showPendingScreenShareRequest() {
        if (screenCapturePermissionInFlight) return;
        String requestId = getSharedPreferences(PREFS, MODE_PRIVATE)
                .getString(PREF_PENDING_SCREEN_SHARE_REQUEST, "");
        if (requestId.isBlank() || isFinishing() || screenShareConsentDialogVisible) return;
        activeScreenShareRequestId = requestId;

        setFocusOverlayVisible(false);
        if (updateDialogVisible && updateDialog != null) updateDialog.dismiss();
        AlertDialog dialog = new MaterialAlertDialogBuilder(this)
                .setTitle("화면 공유 요청")
                .setMessage("선생님이 전자칠판의 화면을 확인하려 합니다. 허용하면 화면이 Classroom 교사 화면으로 전송됩니다. Android는 공유를 시작할 때마다 별도로 권한을 확인합니다.")
                .setNegativeButton("거절", (ignored, which) -> completeScreenShareRequest(false, 0, null))
                .setPositiveButton("공유 권한 확인", (ignored, which) -> requestMediaProjectionConsent())
                .create();
        screenShareConsentDialog = dialog;
        screenShareConsentDialogVisible = true;
        dialog.setOnCancelListener(ignored -> completeScreenShareRequest(false, 0, null));
        dialog.setOnDismissListener(ignored -> {
            screenShareConsentDialogVisible = false;
            screenShareConsentDialog = null;
            setFocusOverlayVisible(true);
        });
        dialog.show();
    }

    private void executePendingRemoteAction() {
        if (isFinishing()) return;
        String serialized = getSharedPreferences(PREFS, MODE_PRIVATE)
                .getString(PREF_PENDING_REMOTE_ACTION, "");
        if (serialized.isBlank()) return;

        String requestId = "";
        boolean success = false;
        String code = "REMOTE_ACTION_FAILED";
        String message = "요청한 작업을 전자칠판에서 실행하지 못했습니다.";
        try {
            JSONObject pending = new JSONObject(serialized);
            requestId = pending.optString("requestId", "");
            if (requestId.isBlank() || requestId.equals(remoteActionInFlight)) return;
            remoteActionInFlight = requestId;
            String action = pending.optString("action", "");
            String value = pending.optString("value", "").trim();
            if ("openUrl".equals(action)) {
                HttpUrl parsed = HttpUrl.parse(value);
                if (parsed == null || !"https".equalsIgnoreCase(parsed.scheme())
                        || !parsed.username().isEmpty() || !parsed.password().isEmpty()) {
                    code = "ANDROID_URL_INVALID";
                    message = "HTTPS 웹 주소만 열 수 있습니다.";
                } else {
                    Intent view = new Intent(Intent.ACTION_VIEW, Uri.parse(parsed.toString()));
                    startActivity(view);
                    success = true;
                    code = "URL_OPENED";
                    message = "전자칠판에서 웹 주소를 열었습니다.";
                }
            } else if ("launchApprovedApp".equals(action)) {
                Intent launch = findApprovedAndroidApp(value);
                if (launch == null) {
                    code = "ANDROID_APP_NOT_INSTALLED";
                    message = "이 전자칠판에 해당 앱이 설치되어 있지 않습니다.";
                } else {
                    launch.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                    startActivity(launch);
                    success = true;
                    code = "APP_LAUNCHED";
                    message = "허용 앱을 열었습니다.";
                }
            } else {
                code = "REMOTE_ACTION_INVALID";
                message = "지원하지 않는 전자칠판 작업입니다.";
            }
        } catch (Exception exception) {
            if ("openUrl".equals(getPendingRemoteActionType(serialized))) {
                code = "ANDROID_BROWSER_UNAVAILABLE";
                message = "전자칠판에서 웹 브라우저를 열지 못했습니다.";
            } else {
                code = "ANDROID_APP_LAUNCH_FAILED";
                message = "허용 앱을 실행하지 못했습니다.";
            }
        }
        if (requestId.isBlank()) return;
        Intent result = new Intent(this, StudentConnectionService.class)
                .setAction(StudentConnectionService.ACTION_REMOTE_ACTION_RESULT)
                .putExtra(StudentConnectionService.EXTRA_REQUEST_ID, requestId)
                .putExtra(StudentConnectionService.EXTRA_REMOTE_SUCCESS, success)
                .putExtra(StudentConnectionService.EXTRA_REMOTE_CODE, code)
                .putExtra(StudentConnectionService.EXTRA_REMOTE_MESSAGE, message);
        try {
            startService(result);
        } catch (RuntimeException ignored) {
            remoteActionInFlight = null;
        }
    }

    private Intent findApprovedAndroidApp(String appId) {
        String normalized = appId == null ? "" : appId.trim();
        String[] candidates;
        if ("calculator".equalsIgnoreCase(normalized)) {
            candidates = new String[]{
                    "com.google.android.calculator",
                    "com.android.calculator2",
                    "com.android.calculator",
                    "com.sec.android.app.popupcalculator"
            };
        } else if ("notepad".equalsIgnoreCase(normalized)) {
            candidates = new String[]{
                    "com.samsung.android.app.notes",
                    "com.google.android.keep",
                    "com.microsoft.office.onenote"
            };
        } else if (normalized.matches("[A-Za-z][A-Za-z0-9_]*(\\.[A-Za-z0-9_]+)+")) {
            candidates = new String[]{normalized};
        } else {
            return null;
        }

        for (String packageName : candidates) {
            Intent launch = getPackageManager().getLaunchIntentForPackage(packageName);
            if (launch != null) return launch;
        }
        return null;
    }

    private String getPendingRemoteActionType(String serialized) {
        try {
            return new JSONObject(serialized).optString("action", "");
        } catch (Exception ignored) {
            return "";
        }
    }

    private void requestMediaProjectionConsent() {
        if (activeScreenShareRequestId == null || activeScreenShareRequestId.isBlank()) return;
        MediaProjectionManager manager = getSystemService(MediaProjectionManager.class);
        if (manager == null) {
            completeScreenShareRequest(false, 0, null);
            new MaterialAlertDialogBuilder(this)
                    .setTitle("화면 공유를 시작할 수 없습니다")
                    .setMessage("이 전자칠판에서 화면 공유 서비스를 사용할 수 없습니다.")
                    .setPositiveButton("확인", null)
                    .show();
            return;
        }
        try {
            screenCapturePermissionInFlight = true;
            startActivityForResult(manager.createScreenCaptureIntent(), REQUEST_SCREEN_CAPTURE_PERMISSION);
        } catch (RuntimeException exception) {
            screenCapturePermissionInFlight = false;
            completeScreenShareRequest(false, 0, null);
            new MaterialAlertDialogBuilder(this)
                    .setTitle("화면 공유를 시작할 수 없습니다")
                    .setMessage("전자칠판 제조사 또는 학교 기기 관리 정책이 화면 공유를 제한할 수 있습니다.")
                    .setPositiveButton("확인", null)
                    .show();
        }
    }

    private void completeScreenShareRequest(boolean approved, int resultCode, @Nullable Intent resultData) {
        String requestId = activeScreenShareRequestId;
        activeScreenShareRequestId = null;
        if (requestId == null || requestId.isBlank()) return;

        String pending = getSharedPreferences(PREFS, MODE_PRIVATE)
                .getString(PREF_PENDING_SCREEN_SHARE_REQUEST, "");
        if (!requestId.equals(pending)) {
            if (approved && !isFinishing()) {
                new MaterialAlertDialogBuilder(this)
                        .setTitle("화면 공유 요청이 만료되었습니다")
                        .setMessage("선생님이 요청을 취소했거나 승인 시간이 지났습니다. 다시 요청해 주세요.")
                        .setPositiveButton("확인", null)
                        .show();
            }
            return;
        }

        Intent response = new Intent(this, StudentConnectionService.class)
                .setAction(StudentConnectionService.ACTION_SCREEN_SHARE_RESPONSE)
                .putExtra(StudentConnectionService.EXTRA_SCREEN_SHARE_REQUEST_ID, requestId)
                .putExtra(StudentConnectionService.EXTRA_SCREEN_SHARE_APPROVED, approved);
        if (approved && resultData != null) {
            response.putExtra(StudentConnectionService.EXTRA_SCREEN_SHARE_RESULT_CODE, resultCode);
            response.putExtra(StudentConnectionService.EXTRA_SCREEN_SHARE_RESULT_DATA, resultData);
        }
        try {
            ContextCompat.startForegroundService(this, response);
        } catch (IllegalStateException exception) {
            new MaterialAlertDialogBuilder(this)
                    .setTitle("화면 공유를 시작하지 못했습니다")
                    .setMessage("Classroom 연결 서비스를 다시 열고 재시도해 주세요.")
                    .setPositiveButton("확인", null)
                    .show();
        }
    }

    private void showNotice(String text) {
        if (noticeCard == null) return;
        if (text == null || text.isBlank()) {
            noticeCard.setVisibility(View.GONE);
            return;
        }
        noticeCard.setText(getString(R.string.teacher_notice, text));
        noticeCard.setVisibility(View.VISIBLE);
    }

    private void showSavedNoticeIfPresent() {
        String saved = getSharedPreferences(PREFS, MODE_PRIVATE).getString(PREF_NOTICE, "");
        showNotice(saved);
    }

    private void confirmKioskMode() {
        DevicePolicyManager policyManager = getSystemService(DevicePolicyManager.class);
        if (policyManager == null || !policyManager.isLockTaskPermitted(getPackageName())) {
            new MaterialAlertDialogBuilder(this)
                    .setTitle("전용 모드를 사용할 수 없습니다")
                    .setMessage("전용 모드는 학교 기기관리자 설정이 있는 경우에만 사용할 수 있습니다. 이 설정은 서버 연결이나 학생 등록과는 무관합니다.")
                    .setPositiveButton("확인", null)
                    .show();
            return;
        }
        new MaterialAlertDialogBuilder(this)
                .setTitle("전자칠판 전용 모드를 시작할까요?")
                .setMessage("선택 기능입니다. 서버 연결과 학생 등록에는 필요하지 않습니다. 학교 관리자와 복구 방법을 확인한 뒤 시작하세요.")
                .setNegativeButton("취소", null)
                .setPositiveButton("전용 모드 시작", (dialog, which) -> {
                    try {
                        startLockTask();
                    } catch (RuntimeException exception) {
                        new MaterialAlertDialogBuilder(this)
                                .setMessage("기기에서 전용 모드를 시작하지 못했습니다. MDM 키오스크 정책을 확인해 주세요.")
                                .setPositiveButton("확인", null)
                                .show();
                    }
                })
                .show();
    }

    private void promptAdministratorExit() {
        EditText pinInput = new EditText(this);
        pinInput.setSingleLine(true);
        pinInput.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        pinInput.setHint("관리자 종료 PIN");
        pinInput.setFilters(new InputFilter[]{new InputFilter.LengthFilter(64)});
        int padding = dp(18);
        pinInput.setPadding(padding, padding, padding, padding);
        LinearLayout container = new LinearLayout(this);
        container.setPadding(dp(24), dp(8), dp(24), 0);
        container.addView(pinInput, new LinearLayout.LayoutParams(-1, dp(58)));
        String requestId = UUID.randomUUID().toString();
        pendingPinRequestId = requestId;

        AlertDialog dialog = new MaterialAlertDialogBuilder(this)
                .setTitle("관리자 확인")
                .setMessage("학교에서 설정한 종료 PIN을 입력해 주세요.")
                .setView(container)
                .setNegativeButton("취소", null)
                .setPositiveButton("확인", null)
                .create();
        pendingExitDialog = dialog;
        dialog.setOnShowListener(ignored -> dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(view -> {
            String pin = pinInput.getText() == null ? "" : pinInput.getText().toString();
            if (pin.length() < 6) {
                pinInput.setError("PIN은 6자 이상이어야 합니다.");
                return;
            }
            Intent service = new Intent(this, StudentConnectionService.class)
                    .setAction(StudentConnectionService.ACTION_VERIFY_EXIT_PIN)
                    .putExtra(StudentConnectionService.EXTRA_REQUEST_ID, requestId)
                    .putExtra(StudentConnectionService.EXTRA_PIN, pin);
            try {
                ContextCompat.startForegroundService(this, service);
                dialog.getButton(AlertDialog.BUTTON_POSITIVE).setEnabled(false);
                dialog.getButton(AlertDialog.BUTTON_POSITIVE).setText("확인 중…");
            } catch (IllegalStateException exception) {
                pinInput.setError("앱을 다시 열고 연결한 뒤 시도해 주세요.");
            }
        }));
        dialog.show();
    }

    private void onPinResult(Intent intent) {
        String requestId = intent.getStringExtra(StudentConnectionService.EXTRA_REQUEST_ID);
        if (pendingPinRequestId == null || !pendingPinRequestId.equals(requestId)) return;
        pendingPinRequestId = null;
        boolean approved = intent.getBooleanExtra(StudentConnectionService.EXTRA_APPROVED, false);
        if (!approved) {
            if (pendingExitDialog != null) {
                pendingExitDialog.dismiss();
                pendingExitDialog = null;
            }
            new MaterialAlertDialogBuilder(this)
                    .setTitle("종료할 수 없습니다")
                    .setMessage("PIN을 확인하지 못했습니다. 서버 연결 상태와 입력한 PIN을 확인해 주세요.")
                    .setPositiveButton("확인", null)
                    .show();
            return;
        }
        if (pendingExitDialog != null) {
            pendingExitDialog.dismiss();
            pendingExitDialog = null;
        }
        ActivityManager activityManager = getSystemService(ActivityManager.class);
        if (activityManager != null
                && activityManager.getLockTaskModeState() == ActivityManager.LOCK_TASK_MODE_LOCKED) {
            try {
                stopLockTask();
            } catch (RuntimeException ignored) {
            }
        }
        stopService(new Intent(this, StudentConnectionService.class));
        finish();
    }

    private void addSpringPress(View view) {
        view.setOnTouchListener((target, event) -> {
            if (event.getAction() == MotionEvent.ACTION_DOWN) {
                target.setPressed(true);
                animateScale(target, ValueAnimator.areAnimatorsEnabled() ? 0.96f : 1f);
                return true;
            } else if (event.getAction() == MotionEvent.ACTION_UP
                    || event.getAction() == MotionEvent.ACTION_CANCEL) {
                target.setPressed(false);
                animateScale(target, 1f);
                if (event.getAction() == MotionEvent.ACTION_UP) target.performClick();
                return true;
            }
            return true;
        });
    }

    private void animateScale(View view, float scale) {
        SpringForce spring = new SpringForce(scale)
                .setStiffness(360f)
                .setDampingRatio(0.62f);
        new SpringAnimation(view, DynamicAnimation.SCALE_X).setSpring(spring).start();
        new SpringAnimation(view, DynamicAnimation.SCALE_Y)
                .setSpring(new SpringForce(scale).setStiffness(360f).setDampingRatio(0.62f))
                .start();
    }

    private void animateScreenEntrance(View view) {
        if (!ValueAnimator.areAnimatorsEnabled()) return;
        view.setAlpha(0f);
        view.setScaleX(0.985f);
        view.setScaleY(0.985f);
        view.setTranslationY(dp(12));
        view.animate().alpha(1f).setDuration(220).setInterpolator(new DecelerateInterpolator(1.5f)).start();
        new SpringAnimation(view, DynamicAnimation.TRANSLATION_Y, 0f)
                .setSpring(new SpringForce(0f).setStiffness(320f).setDampingRatio(0.82f))
                .start();
        new SpringAnimation(view, DynamicAnimation.SCALE_X, 1f)
                .setSpring(new SpringForce(1f).setStiffness(320f).setDampingRatio(0.82f))
                .start();
        new SpringAnimation(view, DynamicAnimation.SCALE_Y, 1f)
                .setSpring(new SpringForce(1f).setStiffness(320f).setDampingRatio(0.82f))
                .start();
    }

    private MaterialCardView createCard() {
        MaterialCardView card = new MaterialCardView(this);
        card.setRadius(dp(compactLayout ? 22 : 26));
        card.setCardBackgroundColor(Color.WHITE);
        card.setStrokeColor(0xFFE7E4F0);
        card.setStrokeWidth(dp(1));
        card.setCardElevation(0);
        card.setUseCompatPadding(false);
        return card;
    }

    private MaterialButton primaryButton(String label) {
        MaterialButton button = new MaterialButton(this);
        button.setText(label);
        button.setTextSize(16);
        button.setTextColor(Color.WHITE);
        button.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        button.setCornerRadius(dp(18));
        button.setBackgroundTintList(android.content.res.ColorStateList.valueOf(COLOR_PURPLE));
        button.setInsetTop(0);
        button.setInsetBottom(0);
        return button;
    }

    private MaterialButton secondaryButton(String label) {
        MaterialButton button = new MaterialButton(this);
        button.setText(label);
        button.setTextSize(15);
        button.setTextColor(COLOR_INK);
        button.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        button.setCornerRadius(dp(18));
        button.setStrokeColor(android.content.res.ColorStateList.valueOf(0xFFDAD8E6));
        button.setStrokeWidth(dp(1));
        button.setBackgroundTintList(android.content.res.ColorStateList.valueOf(0xFFF8F7FC));
        button.setInsetTop(0);
        button.setInsetBottom(0);
        return button;
    }

    private TextView text(String value, int sp, int color, boolean bold) {
        TextView view = new TextView(this);
        view.setText(value);
        view.setTextSize(sp);
        view.setTextColor(color);
        view.setTypeface(Typeface.create(bold ? "sans-serif-medium" : "sans-serif", bold ? Typeface.NORMAL : Typeface.NORMAL));
        view.setIncludeFontPadding(false);
        return view;
    }

    private GradientDrawable pillBackground(int color) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(color);
        drawable.setCornerRadius(dp(100));
        return drawable;
    }

    private void addGap(LinearLayout parent, int heightDp) {
        View spacer = new View(this);
        parent.addView(spacer, new LinearLayout.LayoutParams(1, dp(heightDp)));
    }

    private int dp(float value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }
}
