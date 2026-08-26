using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace PhoneUnlock.Agent;

internal sealed class AgentTrayContext : ApplicationContext
{
    private readonly NotifyIcon notifyIcon;
    private readonly CancellationTokenSource stopSource = new();
    private readonly SynchronizationContext uiContext;

    public CancellationToken StoppingToken => stopSource.Token;

    public AgentTrayContext()
    {
        uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripLabel("Phone Unlock · 자동 잠금 감시"));
        menu.Items.Add(new ToolStripSeparator());

        var settingsItem = new ToolStripMenuItem("Phone Unlock 설정");
        settingsItem.Click += (_, _) => OpenSettings();
        menu.Items.Add(settingsItem);

        var lockItem = new ToolStripMenuItem("PC 잠금");
        lockItem.Click += (_, _) => LockWorkstation();
        menu.Items.Add(lockItem);

        menu.Items.Add(new ToolStripSeparator());
        var exitItem = new ToolStripMenuItem("자동 잠금 감시 종료");
        exitItem.Click += (_, _) => ExitAgent();
        menu.Items.Add(exitItem);

        notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "Phone Unlock · 자동 잠금 감시 중",
            ContextMenuStrip = menu,
            Visible = true
        };
        notifyIcon.DoubleClick += (_, _) => OpenSettings();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            stopSource.Cancel();
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
        }

        stopSource.Dispose();
        base.Dispose(disposing);
    }

    private static void OpenSettings()
    {
        var setupPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "setup",
            "PhoneUnlock.Setup.exe"));
        if (!File.Exists(setupPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(setupPath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(setupPath) ?? AppContext.BaseDirectory
        });
    }

    private void ExitAgent()
    {
        stopSource.Cancel();
        ExitThread();
    }

    private static void LockWorkstation()
    {
        _ = NativeMethods.LockWorkStation();
    }

    public void ShowNotice(string title, string message)
    {
        if (stopSource.IsCancellationRequested)
        {
            return;
        }

        uiContext.Post(_ =>
        {
            if (stopSource.IsCancellationRequested)
            {
                return;
            }

            notifyIcon.BalloonTipTitle = title;
            notifyIcon.BalloonTipText = message;
            notifyIcon.ShowBalloonTip(4_000);
        }, null);
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool LockWorkStation();
    }
}
