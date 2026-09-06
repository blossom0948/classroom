using System.Drawing;
using Blossom.Classroom.Protocol;

namespace Blossom.Classroom.Student.Desktop.Ui;

internal sealed class RemoteAssistConsentForm : Form
{
    private readonly DateTimeOffset deadlineUtc;
    private readonly Label countdown = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1_000 };
    private bool decisionMade;

    public RemoteAssistConsentForm(string teacherDisplayName, int durationSeconds)
    {
        deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(ProtocolConstants.RemoteAssistConsentTimeoutSeconds);
        Text = "Classroom 원격 지원 요청";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        ClientSize = new Size(500, 320);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

        var accent = new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Color.FromArgb(218, 104, 42) };
        var title = new Label
        {
            Text = "원격 지원 요청",
            Location = new Point(30, 31),
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 20F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(35, 44, 58)
        };
        var copy = new Label
        {
            Text = $"{teacherDisplayName} 선생님이 이 컴퓨터의 화면을 보고\n마우스와 키보드를 조작하려고 합니다.",
            Location = new Point(32, 83),
            AutoSize = true,
            Font = new Font("Segoe UI", 11F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(69, 80, 99)
        };
        var privacy = new Label
        {
            Text = $"허용하면 최대 {Math.Max(1, durationSeconds / 60)}분 동안만 지원이 이어지며,\n화면 위에 ‘원격 지원 중’ 표시와 즉시 종료 버튼이 계속 나타납니다.",
            Location = new Point(32, 143),
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(101, 112, 130)
        };
        countdown.Location = new Point(32, 207);
        countdown.AutoSize = true;
        countdown.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point);
        countdown.ForeColor = Color.FromArgb(177, 79, 38);

        var decline = new Button
        {
            Text = "거절",
            DialogResult = DialogResult.Cancel,
            Location = new Point(272, 252),
            Size = new Size(92, 38),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(247, 249, 252),
            ForeColor = Color.FromArgb(72, 83, 101)
        };
        decline.FlatAppearance.BorderColor = Color.FromArgb(207, 216, 230);
        decline.Click += (_, _) =>
        {
            decisionMade = true;
            DialogResult = DialogResult.Cancel;
            Close();
        };
        var approve = new Button
        {
            Text = "허용",
            Location = new Point(374, 252),
            Size = new Size(92, 38),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(218, 104, 42),
            ForeColor = Color.White
        };
        approve.FlatAppearance.BorderSize = 0;
        approve.Click += (_, _) =>
        {
            decisionMade = true;
            DialogResult = DialogResult.OK;
            Close();
        };

        Controls.AddRange([accent, title, copy, privacy, countdown, decline, approve]);
        AcceptButton = approve;
        CancelButton = decline;
        Shown += (_, _) =>
        {
            UpdateCountdown();
            timer.Start();
            decline.Focus();
        };
        timer.Tick += (_, _) =>
        {
            if (DateTimeOffset.UtcNow >= deadlineUtc)
            {
                timer.Stop();
                decisionMade = true;
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }

            UpdateCountdown();
        };
        FormClosing += (_, _) =>
        {
            if (!decisionMade) DialogResult = DialogResult.Cancel;
        };
        FormClosed += (_, _) => timer.Dispose();
    }

    private void UpdateCountdown()
    {
        var seconds = Math.Max(0, (int)Math.Ceiling((deadlineUtc - DateTimeOffset.UtcNow).TotalSeconds));
        countdown.Text = $"이 요청은 {seconds}초 후 자동으로 거절됩니다.";
    }
}

internal sealed class RemoteAssistOverlayForm : Form
{
    private readonly Label countdown = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1_000 };
    private readonly DateTimeOffset expiresAtUtc;
    private bool allowClose;

    public RemoteAssistOverlayForm(string teacherDisplayName, DateTimeOffset expiresAtUtc)
    {
        this.expiresAtUtc = expiresAtUtc;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.FromArgb(184, 57, 68);
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

        var title = new Label
        {
            Text = "● 원격 지원 중",
            Location = new Point(22, 13),
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 14F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.White
        };
        var copy = new Label
        {
            Text = $"{teacherDisplayName} 선생님이 마우스와 키보드를 조작할 수 있습니다.",
            Location = new Point(24, 40),
            AutoSize = true,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(255, 229, 232)
        };
        countdown.Location = new Point(430, 14);
        countdown.AutoSize = true;
        countdown.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold, GraphicsUnit.Point);
        countdown.ForeColor = Color.White;
        var stop = new Button
        {
            Text = "지금 종료",
            Location = new Point(0, 0),
            Size = new Size(104, 44),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(158, 43, 54),
            Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point)
        };
        stop.FlatAppearance.BorderSize = 0;
        stop.Click += (_, _) => StopRequested?.Invoke(this, EventArgs.Empty);
        Controls.AddRange([title, copy, countdown, stop]);

        Shown += (_, _) =>
        {
            UpdateCountdown();
            PositionForPrimaryDisplay(stop);
            timer.Start();
        };
        timer.Tick += (_, _) =>
        {
            if (DateTimeOffset.UtcNow >= this.expiresAtUtc)
            {
                timer.Stop();
                StopRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            UpdateCountdown();
        };
        FormClosing += (_, eventArgs) =>
        {
            if (!allowClose && eventArgs.CloseReason == CloseReason.UserClosing)
            {
                eventArgs.Cancel = true;
                BringToFront();
            }
        };
        FormClosed += (_, _) => timer.Dispose();
    }

    public event EventHandler? StopRequested;

    public void Dismiss()
    {
        allowClose = true;
        Close();
    }

    private void PositionForPrimaryDisplay(Control stop)
    {
        var bounds = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
        Bounds = new Rectangle(bounds.Left, bounds.Top, Math.Max(640, bounds.Width), 72);
        stop.Left = ClientSize.Width - stop.Width - 20;
        countdown.Left = Math.Max(280, stop.Left - countdown.Width - 24);
    }

    private void UpdateCountdown()
    {
        var remaining = Math.Max(0, (int)Math.Ceiling((expiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds));
        countdown.Text = $"자동 종료까지 {remaining / 60:D2}:{remaining % 60:D2}";
    }
}
