using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using Blossom.Classroom.Protocol;
using Blossom.Classroom.Protocol.Models;
using Blossom.Classroom.Student.Desktop.Commands;
using Blossom.Classroom.Student.Desktop.Configuration;
using Blossom.Classroom.Student.Desktop.Status;

namespace Blossom.Classroom.Student.Desktop.Ui;

public sealed class StudentDesktopForm : Form
{
    private readonly StudentDesktopOptions options;
    private readonly WindowsStudentStatusProvider statusProvider;
    private readonly Func<string, CancellationToken, Task<DesktopExitPinVerificationResult>> exitPinVerifier;
    private readonly Func<CancellationToken, Task<DesktopUpdateCheckResult>> updateChecker;
    private readonly bool startInBackground;
    private readonly Label connectionLabel = CreateLabel("● 서비스 연결 대기 중", 11, Color.DarkOrange);
    private readonly Label serverLabel = CreateLabel("● Classroom 서버 재연결 중", 11, Color.DarkOrange);
    private readonly Label screenSharingLabel = CreateLabel("● Windows 시작 시 자동 연결 · 화면 공유 대기", 10, Color.FromArgb(62, 92, 165));
    private readonly RoundedButton updateButton = new()
    {
        Text = "업데이트 확인",
        FillColor = Color.FromArgb(55, 96, 230),
        HoverColor = Color.FromArgb(43, 79, 204),
        TextColor = Color.White
    };
    private readonly RoundedButton exitButton = new()
    {
        Text = "프로그램 종료",
        FillColor = Color.FromArgb(255, 255, 255),
        HoverColor = Color.FromArgb(255, 242, 242),
        TextColor = Color.FromArgb(184, 57, 68),
        BorderColor = Color.FromArgb(241, 178, 184)
    };
    private readonly RoundedButton helpButton = new()
    {
        Text = "도움 요청",
        FillColor = Color.FromArgb(235, 137, 52),
        HoverColor = Color.FromArgb(205, 107, 31),
        TextColor = Color.White,
        BorderColor = Color.FromArgb(235, 137, 52),
        Enabled = false
    };
    private readonly Label updateLabel = CreateLabel("자동 업데이트 · 시작 후와 15분마다 확인", 9, Color.FromArgb(74, 91, 117));
    private readonly Label helpStatusLabel = CreateLabel("수업이 시작되면 도움을 요청할 수 있습니다.", 9, Color.FromArgb(88, 106, 137));
    private readonly Label deviceLabel;
    private readonly NotifyIcon trayIcon = new();
    private readonly System.Windows.Forms.Timer disconnectFailsafeTimer = new() { Interval = 60_000 };
    private bool screenSharingActive;
    private bool approvedExit;
    private bool exitPromptOpen;
    private bool initialVisibilityHandled;
    private bool backgroundNoticeShown;
    private bool helpRequestAvailable;
    private bool helpRequested;
    private FocusOverlayForm? focusOverlay;

    public StudentDesktopForm(
        StudentDesktopOptions options,
        WindowsStudentStatusProvider statusProvider,
        Func<string, CancellationToken, Task<DesktopExitPinVerificationResult>> exitPinVerifier,
        Func<CancellationToken, Task<DesktopUpdateCheckResult>> updateChecker,
        bool startInBackground = false)
    {
        this.options = options;
        this.statusProvider = statusProvider;
        this.exitPinVerifier = exitPinVerifier;
        this.updateChecker = updateChecker;
        this.startInBackground = startInBackground;
        deviceLabel = CreateLabel($"장치 ID  {options.DeviceId:D}", 9, Color.FromArgb(104, 121, 147));
        Text = "Classroom Student";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = !startInBackground;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(560, 480);
        MinimumSize = new Size(560, 480);
        BackColor = Color.FromArgb(241, 245, 252);
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

        var header = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(ClientSize.Width, 102),
            BackColor = Color.FromArgb(20, 38, 70),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        var title = CreateLabel("Classroom Student", 22, Color.White);
        title.Font = new Font("Segoe UI Semibold", 22F, FontStyle.Bold, GraphicsUnit.Point);
        title.Location = new Point(28, 18);
        title.AutoSize = true;
        var version = CreateLabel($"학생 화면  ·  v{options.AgentVersion}", 10, Color.FromArgb(183, 205, 255));
        version.Location = new Point(30, 61);
        version.AutoSize = true;
        header.Controls.AddRange([title, version]);

        var connectionCard = new RoundedPanel
        {
            Location = new Point(24, 120),
            Size = new Size(512, 108),
            BackColor = Color.White,
            BorderColor = Color.FromArgb(217, 226, 240),
            CornerRadius = 16
        };
        var statusCaption = CreateLabel("연결 상태", 9, Color.FromArgb(104, 121, 147));
        statusCaption.Location = new Point(16, 12);
        statusCaption.AutoSize = true;
        connectionLabel.Location = new Point(16, 31);
        connectionLabel.AutoSize = true;
        serverLabel.Location = new Point(16, 53);
        serverLabel.AutoSize = true;
        screenSharingLabel.Location = new Point(16, 76);
        screenSharingLabel.AutoSize = false;
        screenSharingLabel.AutoEllipsis = true;
        screenSharingLabel.Size = new Size(480, 20);
        screenSharingLabel.Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold, GraphicsUnit.Point);
        connectionCard.Controls.AddRange([statusCaption, connectionLabel, serverLabel, screenSharingLabel]);

        var helpCard = new RoundedPanel
        {
            Location = new Point(24, 244),
            Size = new Size(512, 84),
            BackColor = Color.FromArgb(255, 250, 243),
            BorderColor = Color.FromArgb(244, 207, 166),
            CornerRadius = 16
        };
        var helpCaption = CreateLabel("수업 중 도움이 필요하면", 9, Color.FromArgb(141, 91, 37));
        helpCaption.Location = new Point(16, 11);
        helpCaption.AutoSize = true;
        var helpPrompt = CreateLabel("선생님께 바로 알려주세요", 12, Color.FromArgb(91, 55, 22));
        helpPrompt.Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold, GraphicsUnit.Point);
        helpPrompt.Location = new Point(16, 28);
        helpPrompt.AutoSize = true;
        helpStatusLabel.Location = new Point(16, 51);
        helpStatusLabel.AutoSize = false;
        helpStatusLabel.AutoEllipsis = true;
        helpStatusLabel.Size = new Size(318, 20);
        helpButton.Location = new Point(356, 19);
        helpButton.Size = new Size(140, 47);
        helpButton.Click += (_, _) => ToggleHelpRequest();
        helpCard.Controls.AddRange([helpCaption, helpPrompt, helpStatusLabel, helpButton]);

        updateLabel.Location = new Point(40, 344);
        updateLabel.AutoSize = false;
        updateLabel.Size = new Size(480, 23);
        updateLabel.TextAlign = ContentAlignment.MiddleLeft;

        updateButton.Location = new Point(40, 370);
        updateButton.Size = new Size(228, 48);
        updateButton.BorderColor = Color.FromArgb(55, 96, 230);
        updateButton.Click += async (_, _) => await CheckForUpdatesAsync();

        exitButton.Location = new Point(292, 370);
        exitButton.Size = new Size(228, 48);
        exitButton.Click += (_, _) => BeginInvoke(new Action(() => _ = RequestApprovedExitAsync()));

        deviceLabel.Location = new Point(40, 440);
        deviceLabel.AutoSize = false;
        deviceLabel.Size = new Size(480, 20);
        deviceLabel.AutoEllipsis = true;

        Controls.AddRange([header, connectionCard, helpCard, updateLabel, updateButton, exitButton, deviceLabel]);

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("상태 열기", null, (_, _) => ShowMainWindow());
        trayMenu.Items.Add("프로그램 종료", null, (_, _) => ShowMainWindowAndRequestExit());
        trayIcon.Icon = SystemIcons.Information;
        trayIcon.Text = "Classroom Student · 학교 관리 활성화";
        trayIcon.ContextMenuStrip = trayMenu;
        trayIcon.Visible = true;
        trayIcon.DoubleClick += (_, _) => ShowMainWindow();

        disconnectFailsafeTimer.Tick += (_, _) =>
        {
            disconnectFailsafeTimer.Stop();
            ClearFocusMode();
        };
        FormClosing += (_, eventArgs) =>
        {
            if (eventArgs.CloseReason == CloseReason.UserClosing && !approvedExit)
            {
                eventArgs.Cancel = true;
                HideToTray();
            }
        };
        FormClosed += (_, _) =>
        {
            disconnectFailsafeTimer.Dispose();
            trayIcon.Visible = false;
            trayIcon.Dispose();
            trayMenu.Dispose();
        };
    }

    protected override void SetVisibleCore(bool value)
    {
        if (startInBackground && value && !initialVisibilityHandled)
        {
            initialVisibilityHandled = true;
            base.SetVisibleCore(false);
            return;
        }

        base.SetVisibleCore(value);
    }

    public void SetConnectionState(bool connected)
    {
        RunOnUiThread(() =>
        {
            connectionLabel.Text = connected
                ? "● Classroom 서비스 연결됨"
                : "● Classroom 서비스 연결 대기 중";
            connectionLabel.ForeColor = connected ? Color.SeaGreen : Color.DarkOrange;
        });
    }

    public void SetServerConnectionState(bool connected, Guid sessionId)
    {
        RunOnUiThread(() =>
        {
            if (connected)
            {
                if (sessionId == Guid.Empty)
                {
                    ApplyScreenSharingState(false);
                    SetHelpRequestAvailability(false, clearRequest: true);
                }
                else
                {
                    SetHelpRequestAvailability(true, clearRequest: false);
                }
                serverLabel.Text = sessionId == Guid.Empty
                    ? "● Classroom 서버 연결됨 · 수업 대기 중"
                    : $"● Classroom 서버 연결됨 · 수업 참여 {sessionId.ToString("N")[..8]}";
                serverLabel.ForeColor = Color.SeaGreen;
                trayIcon.Text = sessionId == Guid.Empty
                    ? "Classroom Student · 수업 대기"
                    : screenSharingActive
                        ? "Classroom Student · 화면 공유 중"
                        : "Classroom Student · 수업 참여 중";
                disconnectFailsafeTimer.Stop();
            }
            else
            {
                ApplyScreenSharingState(false);
                SetHelpRequestAvailability(false, clearRequest: false);
                serverLabel.Text = "● Classroom 서버 재연결 중";
                serverLabel.ForeColor = Color.DarkOrange;
                trayIcon.Text = "Classroom Student · 서버 재연결 중";
                if (focusOverlay is not null)
                {
                    disconnectFailsafeTimer.Stop();
                    disconnectFailsafeTimer.Start();
                }
            }
        });
    }

    public void ShowStatus(DesktopStatusData status)
    {
        // Detailed activity belongs in the teacher console. The student window
        // intentionally exposes only connection, version, and sharing state.
    }

    private void ToggleHelpRequest()
    {
        if (!helpRequestAvailable)
        {
            return;
        }

        helpRequested = !helpRequested;
        statusProvider.SetHelpRequested(helpRequested);
        RefreshHelpRequestControls();
        if (helpRequested)
        {
            trayIcon.ShowBalloonTip(
                2_500,
                "Classroom Student",
                "선생님께 도움 요청을 보냈습니다. 취소하려면 학생 창에서 ‘요청 취소’를 누르세요.",
                ToolTipIcon.Info);
        }
    }

    private void SetHelpRequestAvailability(bool available, bool clearRequest)
    {
        helpRequestAvailable = available;
        if (clearRequest && helpRequested)
        {
            helpRequested = false;
            statusProvider.SetHelpRequested(false);
        }

        RefreshHelpRequestControls();
    }

    private void RefreshHelpRequestControls()
    {
        helpButton.Enabled = helpRequestAvailable;
        if (helpRequested)
        {
            helpButton.Text = helpRequestAvailable ? "요청 취소" : "요청 전달 중";
            helpButton.FillColor = Color.FromArgb(69, 112, 210);
            helpButton.HoverColor = Color.FromArgb(50, 88, 180);
            helpButton.BorderColor = Color.FromArgb(69, 112, 210);
            helpStatusLabel.ForeColor = Color.FromArgb(40, 92, 167);
            helpStatusLabel.Text = helpRequestAvailable
                ? "● 선생님께 도움이 필요하다고 알렸습니다."
                : "● 서버에 다시 연결되면 요청을 이어서 전달합니다.";
            return;
        }

        helpButton.Text = "도움 요청";
        helpButton.FillColor = Color.FromArgb(235, 137, 52);
        helpButton.HoverColor = Color.FromArgb(205, 107, 31);
        helpButton.BorderColor = Color.FromArgb(235, 137, 52);
        helpStatusLabel.ForeColor = Color.FromArgb(88, 106, 137);
        helpStatusLabel.Text = helpRequestAvailable
            ? "질문이 있거나 선생님의 도움이 필요할 때 눌러주세요."
            : "수업이 시작되면 도움을 요청할 수 있습니다.";
    }

    public Task<DesktopCommandApplyResult> ApplyCommandAsync(CommandRequest command)
    {
        if (IsDisposed)
        {
            return Task.FromResult(new DesktopCommandApplyResult(false, "DESKTOP_CLOSED", "Student Desktop is closed."));
        }

        if (!InvokeRequired)
        {
            return ApplyCommandOnUiAsync(command);
        }

        var completion = new TaskCompletionSource<DesktopCommandApplyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(new Action(async () =>
        {
            try
            {
                completion.SetResult(await ApplyCommandOnUiAsync(command));
            }
            catch (Exception exception)
            {
                completion.SetResult(new DesktopCommandApplyResult(false, "DESKTOP_COMMAND_FAILED", exception.Message));
            }
        }));
        return completion.Task;
    }

    private Task<DesktopCommandApplyResult> ApplyCommandOnUiAsync(CommandRequest command)
    {
        try
        {
            return command.Kind switch
            {
                ClassroomCommandKind.Message =>
                    Task.FromResult(ShowMessage(command)),
                ClassroomCommandKind.OpenUrl =>
                    Task.FromResult(OpenUrl(command)),
                ClassroomCommandKind.FocusMode =>
                    Task.FromResult(SetFocusMode(command)),
                ClassroomCommandKind.LaunchApprovedApp =>
                    Task.FromResult(LaunchApprovedApp(command)),
                ClassroomCommandKind.ScreenShare =>
                    Task.FromResult(SetScreenSharing(command)),
                _ => Task.FromResult(new DesktopCommandApplyResult(false, "COMMAND_UNSUPPORTED", "Unsupported command."))
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult(new DesktopCommandApplyResult(false, "COMMAND_APPLY_FAILED", exception.Message));
        }
    }

    private DesktopCommandApplyResult ShowMessage(CommandRequest command)
    {
        var message = new TeacherMessageForm(command.Message ?? "선생님 메시지", command.DisplaySeconds ?? 10);
        message.Show(this);
        return new DesktopCommandApplyResult(true, "MESSAGE_DISPLAYED", "Teacher message displayed.");
    }

    private static DesktopCommandApplyResult OpenUrl(CommandRequest command)
    {
        if (command.Url is null)
        {
            return new DesktopCommandApplyResult(false, "URL_MISSING", "The HTTPS URL is missing.");
        }

        Process.Start(new ProcessStartInfo(command.Url)
        {
            UseShellExecute = true
        });
        return new DesktopCommandApplyResult(true, "URL_OPENED", "The HTTPS URL was opened.");
    }

    private DesktopCommandApplyResult SetFocusMode(CommandRequest command)
    {
        var enabled = command.FocusEnabled is not false;
        statusProvider.SetPolicyApplied(enabled);
        if (enabled)
        {
            focusOverlay ??= new FocusOverlayForm();
            focusOverlay.SetDisplay(
                command.FocusDisplayMode ?? FocusDisplayMode.Message,
                command.Message ?? "수업에 집중해 주세요.");
            focusOverlay.Show();
            focusOverlay.BringToFront();
            return new DesktopCommandApplyResult(true, "FOCUS_MODE_ENABLED", "Focus mode enabled.");
        }

        ClearFocusMode();
        return new DesktopCommandApplyResult(true, "FOCUS_MODE_DISABLED", "Focus mode disabled.");
    }

    private void ClearFocusMode()
    {
        disconnectFailsafeTimer.Stop();
        focusOverlay?.Dismiss();
        focusOverlay = null;
        statusProvider.SetPolicyApplied(false);
    }

    private DesktopCommandApplyResult SetScreenSharing(CommandRequest command)
    {
        var enabled = command.ScreenShareEnabled is true;
        var intervalMilliseconds = command.ScreenShareIntervalMilliseconds
            ?? ProtocolConstants.ScreenShareStandardIntervalMilliseconds;
        ApplyScreenSharingState(enabled, intervalMilliseconds);
        if (enabled)
        {
            return new DesktopCommandApplyResult(
                true,
                "SCREEN_SHARE_ENABLED",
                "Adaptive screen sharing enabled (up to 720p).");
        }

        return new DesktopCommandApplyResult(true, "SCREEN_SHARE_DISABLED", "Screen sharing disabled.");
    }

    private void ApplyScreenSharingState(bool enabled, int? intervalMilliseconds = null)
    {
        screenSharingActive = enabled;
        statusProvider.SetScreenSharing(enabled, intervalMilliseconds);
        screenSharingLabel.Visible = true;
        screenSharingLabel.Text = enabled
            ? "● 화면 공유 중 · 최대 720p로 자동 조정됩니다"
            : "● Windows 시작 시 자동 연결 · 화면 공유 대기";
        screenSharingLabel.ForeColor = enabled
            ? Color.FromArgb(188, 42, 52)
            : Color.FromArgb(62, 92, 165);
        trayIcon.Text = enabled
            ? "Classroom Student · 화면 공유 중"
            : "Classroom Student · 학교 관리 활성화";
    }

    private void ShowMainWindow()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void HideToTray()
    {
        if (IsDisposed)
        {
            return;
        }

        // Closing the visible status window must never interrupt the desktop
        // pipe, screen sharing, or the automatic Windows-start watchdog. The
        // explicit tray/menu exit path below is the only route that requests
        // the administrator-managed PIN and actually closes the process.
        ShowInTaskbar = false;
        Hide();
        trayIcon.Visible = true;
        if (!backgroundNoticeShown)
        {
            trayIcon.ShowBalloonTip(
                4_000,
                "Classroom Student",
                "창만 닫혔습니다. Classroom은 백그라운드에서 연결을 유지합니다. 종료하려면 알림 영역 Classroom 아이콘의 ‘프로그램 종료’를 선택하세요.",
                ToolTipIcon.Info);
            backgroundNoticeShown = true;
        }
    }

    private void ShowMainWindowAndRequestExit()
    {
        ShowMainWindow();
        BeginInvoke(new Action(() => _ = RequestApprovedExitAsync()));
    }

    private async Task RequestApprovedExitAsync()
    {
        if (exitPromptOpen || IsDisposed)
        {
            return;
        }

        exitPromptOpen = true;
        try
        {
            using var dialog = new StudentExitPinForm();
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var result = await exitPinVerifier(dialog.Pin, timeout.Token);
            if (!result.Approved)
            {
                MessageBox.Show(
                    this,
                    string.IsNullOrWhiteSpace(result.Message)
                        ? "종료 비밀번호를 확인하지 못했습니다."
                        : result.Message,
                    "Classroom 종료 확인",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            approvedExit = true;
            trayIcon.Visible = false;
            Close();
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show(
                this,
                "종료 비밀번호 확인 시간이 초과되었습니다. 잠시 후 다시 시도해 주세요.",
                "Classroom 종료 확인",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            exitPromptOpen = false;
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        if (updateButton.Enabled is false)
        {
            return;
        }

        updateButton.Enabled = false;
        updateLabel.ForeColor = Color.FromArgb(92, 102, 118);
        updateLabel.Text = "업데이트를 확인하는 중입니다…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            var result = await updateChecker(timeout.Token);
            updateLabel.Text = result.Message;
            updateLabel.ForeColor = !result.Success
                ? Color.FromArgb(188, 42, 52)
                : result.Code == "UPDATE_APPLYING"
                    ? Color.FromArgb(55, 96, 230)
                    : Color.SeaGreen;
            if (result.Success && result.Code == "UPDATE_APPLYING")
            {
                trayIcon.ShowBalloonTip(2_500, "Classroom Student", result.Message, ToolTipIcon.Info);
            }
        }
        catch (OperationCanceledException)
        {
            updateLabel.ForeColor = Color.FromArgb(188, 42, 52);
            updateLabel.Text = "업데이트 확인 시간이 초과되었습니다. 잠시 후 다시 시도해 주세요.";
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            updateLabel.ForeColor = Color.FromArgb(188, 42, 52);
            updateLabel.Text = exception.Message;
        }
        finally
        {
            updateButton.Enabled = true;
        }
    }

    private DesktopCommandApplyResult LaunchApprovedApp(CommandRequest command)
    {
        if (command.ApprovedAppId is null
            || !options.ApprovedApplications.TryGetValue(command.ApprovedAppId, out var executable))
        {
            return new DesktopCommandApplyResult(false, "APP_NOT_APPROVED", "The requested app is not approved on this device.");
        }

        Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = true
        });
        return new DesktopCommandApplyResult(true, "APP_LAUNCHED", "The approved app was launched.");
    }

    private void RunOnUiThread(Action action)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(action);
        }
        else
        {
            action();
        }
    }

    private sealed class RoundedPanel : Panel
    {
        private int cornerRadius = 16;
        private Color borderColor = Color.Transparent;

        public int CornerRadius
        {
            get => cornerRadius;
            set { cornerRadius = Math.Max(0, value); Invalidate(); }
        }

        public Color BorderColor
        {
            get => borderColor;
            set { borderColor = value; Invalidate(); }
        }

        public RoundedPanel()
        {
            SetStyle(
                ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer,
                true);
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            base.OnPaint(eventArgs);
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = ClientRectangle;
            bounds.Width--;
            bounds.Height--;
            using var path = CreatePath(bounds, CornerRadius);
            using var fill = new SolidBrush(BackColor);
            eventArgs.Graphics.FillPath(fill, path);
            if (BorderColor != Color.Transparent)
            {
                using var border = new Pen(BorderColor, 1F);
                eventArgs.Graphics.DrawPath(border, path);
            }
        }
    }

    private sealed class RoundedButton : Button
    {
        private Color fillColor = Color.FromArgb(55, 96, 230);
        private Color hoverColor = Color.FromArgb(43, 79, 204);
        private Color textColor = Color.White;
        private Color borderColor = Color.Transparent;
        private bool hovered;

        public Color FillColor
        {
            get => fillColor;
            set { fillColor = value; Invalidate(); }
        }

        public Color HoverColor
        {
            get => hoverColor;
            set { hoverColor = value; Invalidate(); }
        }

        public Color TextColor
        {
            get => textColor;
            set { textColor = value; Invalidate(); }
        }

        public Color BorderColor
        {
            get => borderColor;
            set { borderColor = value; Invalidate(); }
        }

        public RoundedButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            TabStop = true;
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold, GraphicsUnit.Point);
            SetStyle(
                ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer,
                true);
        }

        protected override void OnMouseEnter(EventArgs eventArgs)
        {
            hovered = true;
            Invalidate();
            base.OnMouseEnter(eventArgs);
        }

        protected override void OnMouseLeave(EventArgs eventArgs)
        {
            hovered = false;
            Invalidate();
            base.OnMouseLeave(eventArgs);
        }

        protected override void OnEnabledChanged(EventArgs eventArgs)
        {
            Invalidate();
            base.OnEnabledChanged(eventArgs);
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            base.OnPaint(eventArgs);
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = ClientRectangle;
            bounds.Inflate(-1, -1);
            using var path = CreatePath(bounds, 14);
            var background = Enabled
                ? hovered ? HoverColor : FillColor
                : Color.FromArgb(225, 231, 241);
            using var fill = new SolidBrush(background);
            eventArgs.Graphics.FillPath(fill, path);
            if (BorderColor != Color.Transparent)
            {
                using var border = new Pen(Enabled ? BorderColor : Color.FromArgb(205, 214, 229), 1F);
                eventArgs.Graphics.DrawPath(border, path);
            }

            var color = Enabled ? TextColor : Color.FromArgb(142, 155, 175);
            TextRenderer.DrawText(
                eventArgs.Graphics,
                Text,
                Font,
                ClientRectangle,
                color,
                TextFormatFlags.HorizontalCenter
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPrefix
                | TextFormatFlags.EndEllipsis);
        }
    }

    private static GraphicsPath CreatePath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Label CreateLabel(string text, float size, Color color) => new()
    {
        Text = text,
        Font = new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Point),
        ForeColor = color,
        AutoSize = true
    };

    private static Font CreateTeacherMessageFont(float size)
    {
        try
        {
            return new Font("Gungsuh", size, FontStyle.Regular, GraphicsUnit.Point);
        }
        catch (ArgumentException)
        {
            return new Font("Batang", size, FontStyle.Regular, GraphicsUnit.Point);
        }
    }

    private sealed class TeacherMessageForm : Form
    {
        public TeacherMessageForm(string message, int seconds)
        {
            Text = "선생님 공지";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(720, 360);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            TopMost = true;
            BackColor = Color.FromArgb(196, 42, 52);
            var label = new Label
            {
                Text = message,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(34),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(196, 42, 52),
                Font = CreateTeacherMessageFont(28F),
                AutoEllipsis = false
            };
            Controls.Add(label);
            var timer = new System.Windows.Forms.Timer { Interval = Math.Clamp(seconds, 1, 3_600) * 1_000 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Close();
            };
            Shown += (_, _) => timer.Start();
            FormClosed += (_, _) => timer.Dispose();
        }
    }

    private sealed class StudentExitPinForm : Form
    {
        private readonly TextBox pinInput = new()
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 13F, FontStyle.Regular, GraphicsUnit.Point),
            MaxLength = 64,
            UseSystemPasswordChar = true
        };
        private readonly Label errorLabel = new()
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(188, 42, 52),
            Visible = false
        };

        public StudentExitPinForm()
        {
            Text = "Classroom 종료 확인";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(420, 248);
            BackColor = Color.White;
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

            var title = new Label
            {
                Text = "학생 앱 종료",
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 19F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(27, 42, 68),
                Location = new Point(28, 24)
            };
            var copy = new Label
            {
                Text = "관리자가 설정한 종료 비밀번호를 입력하면\n학생 앱을 종료합니다.",
                AutoSize = true,
                ForeColor = Color.FromArgb(92, 102, 118),
                Location = new Point(30, 66)
            };
            var fieldLabel = new Label
            {
                Text = "종료 비밀번호",
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(35, 44, 58),
                Location = new Point(30, 119)
            };
            pinInput.Location = new Point(30, 143);
            pinInput.Size = new Size(360, 33);
            errorLabel.Location = new Point(30, 181);
            var cancel = new Button
            {
                Text = "취소",
                DialogResult = DialogResult.Cancel,
                Location = new Point(213, 205),
                Size = new Size(82, 31)
            };
            var submit = new Button
            {
                Text = "종료하기",
                Location = new Point(303, 205),
                Size = new Size(87, 31),
                BackColor = Color.FromArgb(56, 91, 223),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            submit.FlatAppearance.BorderSize = 0;
            submit.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(pinInput.Text) || pinInput.Text.Length is < 6 or > 64)
                {
                    errorLabel.Text = "종료 비밀번호는 6~64자로 입력해 주세요.";
                    errorLabel.Visible = true;
                    pinInput.Focus();
                    return;
                }

                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.AddRange([title, copy, fieldLabel, pinInput, errorLabel, cancel, submit]);
            AcceptButton = submit;
            CancelButton = cancel;
            Shown += (_, _) => pinInput.Focus();
        }

        public string Pin => pinInput.Text;
    }

    private sealed class FocusOverlayForm : Form
    {
        private static readonly Color MessageBackground = Color.FromArgb(24, 36, 58);
        private bool allowClose;
        private readonly Label label = new()
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White,
            BackColor = MessageBackground,
            Font = new Font("Segoe UI Semibold", 28F, FontStyle.Bold, GraphicsUnit.Point)
        };

        public FocusOverlayForm()
        {
            BackColor = MessageBackground;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            TopMost = true;
            ShowInTaskbar = false;
            Controls.Add(label);
            FormClosing += (_, eventArgs) =>
            {
                if (!allowClose && eventArgs.CloseReason == CloseReason.UserClosing)
                {
                    eventArgs.Cancel = true;
                    BringToFront();
                }
            };
        }

        public void SetDisplay(FocusDisplayMode displayMode, string message)
        {
            var blackScreen = displayMode is FocusDisplayMode.BlackScreen;
            var background = blackScreen ? Color.Black : MessageBackground;
            BackColor = background;
            label.BackColor = background;
            label.Visible = !blackScreen;
            label.Text = blackScreen ? string.Empty : $"집중 모드\n\n{message}";
        }

        public void Dismiss()
        {
            allowClose = true;
            Close();
        }
    }
}
