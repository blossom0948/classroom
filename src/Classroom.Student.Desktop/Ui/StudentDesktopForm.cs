using System.Diagnostics;
using System.Drawing;
using Blossom.Classroom.Protocol.Models;
using Blossom.Classroom.Student.Desktop.Commands;
using Blossom.Classroom.Student.Desktop.Configuration;
using Blossom.Classroom.Student.Desktop.Status;

namespace Blossom.Classroom.Student.Desktop.Ui;

public sealed class StudentDesktopForm : Form
{
    private readonly StudentDesktopOptions options;
    private readonly WindowsStudentStatusProvider statusProvider;
    private readonly Label connectionLabel = CreateLabel("● 서비스 연결 대기 중", 11, Color.DarkOrange);
    private readonly Label serverLabel = CreateLabel("● Classroom 서버 재연결 중", 11, Color.DarkOrange);
    private readonly Label activityLabel = CreateLabel("현재 앱: 확인 중", 11, Color.FromArgb(35, 44, 58));
    private readonly Label deviceLabel;
    private readonly NotifyIcon trayIcon = new();
    private readonly System.Windows.Forms.Timer disconnectFailsafeTimer = new() { Interval = 60_000 };
    private FocusOverlayForm? focusOverlay;
    private bool explicitExit;

    public StudentDesktopForm(
        StudentDesktopOptions options,
        WindowsStudentStatusProvider statusProvider)
    {
        this.options = options;
        this.statusProvider = statusProvider;
        deviceLabel = CreateLabel($"장치: {options.DeviceId:D}", 9, Color.DimGray);
        Text = "Classroom Student";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 335);
        MinimumSize = new Size(520, 335);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

        var title = CreateLabel("Classroom Student", 20, Color.FromArgb(27, 42, 68));
        title.Font = new Font("Segoe UI Semibold", 20F, FontStyle.Bold, GraphicsUnit.Point);
        title.Location = new Point(28, 22);
        title.AutoSize = true;

        connectionLabel.Location = new Point(30, 72);
        connectionLabel.AutoSize = true;
        serverLabel.Location = new Point(30, 101);
        serverLabel.AutoSize = true;
        activityLabel.Location = new Point(30, 151);
        activityLabel.AutoSize = true;

        var transparency = CreateLabel(
            "현재 앱·창 제목·연결 상태만 이 화면과 학교 콘솔에 표시됩니다.\n화면 캡처·키 입력·임의 원격 셸은 사용하지 않습니다.",
            10,
            Color.FromArgb(92, 102, 118));
        transparency.Location = new Point(30, 200);
        transparency.AutoSize = true;

        deviceLabel.Location = new Point(30, 285);
        deviceLabel.AutoSize = true;

        Controls.AddRange([title, connectionLabel, serverLabel, activityLabel, transparency, deviceLabel]);

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("상태 열기", null, (_, _) => ShowMainWindow());
        trayMenu.Items.Add("종료", null, (_, _) =>
        {
            explicitExit = true;
            Close();
        });
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
            if (!explicitExit && eventArgs.CloseReason == CloseReason.UserClosing)
            {
                eventArgs.Cancel = true;
                Hide();
                trayIcon.ShowBalloonTip(
                    2_000,
                    "Classroom Student",
                    "학교 관리 상태는 알림 영역에서 계속 확인할 수 있습니다.",
                    ToolTipIcon.Info);
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
                serverLabel.Text = sessionId == Guid.Empty
                    ? "● Classroom 서버 연결됨 · 수업 대기 중"
                    : $"● Classroom 서버 연결됨 · 수업 참여 {sessionId.ToString("N")[..8]}";
                serverLabel.ForeColor = Color.SeaGreen;
                trayIcon.Text = sessionId == Guid.Empty
                    ? "Classroom Student · 수업 대기"
                    : "Classroom Student · 수업 참여 중";
                disconnectFailsafeTimer.Stop();
            }
            else
            {
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
        RunOnUiThread(() =>
        {
            var activity = status.Activity;
            var battery = status.BatteryPercent is int value ? $"배터리 {value}%" : "배터리 확인 필요";
            var network = status.NetworkStatus ?? "unknown";
            var windowTitle = string.IsNullOrWhiteSpace(activity?.WindowTitle)
                ? "현재 창 확인 필요"
                : activity.WindowTitle;
            activityLabel.Text = $"현재 앱: {activity?.ApplicationDisplayName ?? "확인 필요"} · 창: {windowTitle} · {battery} · 네트워크 {network}";
        });
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
            focusOverlay.SetMessage(command.Message ?? "수업에 집중해 주세요.");
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
        focusOverlay?.Close();
        focusOverlay = null;
        statusProvider.SetPolicyApplied(false);
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
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

    private static Label CreateLabel(string text, float size, Color color) => new()
    {
        Text = text,
        Font = new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Point),
        ForeColor = color,
        AutoSize = true
    };

    private sealed class TeacherMessageForm : Form
    {
        public TeacherMessageForm(string message, int seconds)
        {
            Text = "선생님 메시지";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(480, 220);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            TopMost = true;
            var label = new Label
            {
                Text = message,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(20),
                Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold, GraphicsUnit.Point)
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

    private sealed class FocusOverlayForm : Form
    {
        private readonly Label label = new()
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(24, 36, 58),
            Font = new Font("Segoe UI Semibold", 28F, FontStyle.Bold, GraphicsUnit.Point)
        };

        public FocusOverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            TopMost = true;
            ShowInTaskbar = false;
            Controls.Add(label);
        }

        public void SetMessage(string message) => label.Text = $"집중 모드\n\n{message}";
    }
}
