using System.Drawing;
using System.Runtime.InteropServices;
using Blossom.Classroom.Protocol;
using Blossom.Classroom.Protocol.Models;

namespace Blossom.Classroom.Student.Desktop.Ui;

/// <summary>
/// Keeps the focus surface visible on the active Windows virtual desktop.
/// Windows assigns a normal top-level window to one virtual desktop, so a
/// single <see cref="Form"/> cannot cover a desktop the user switches to.
/// We therefore replace the surface on the active desktop when the documented
/// virtual-desktop query reports that the previous surface is elsewhere.
/// </summary>
internal sealed class FocusOverlayController : IDisposable
{
    private const int DesktopPollIntervalMilliseconds = 150;

    private readonly System.Windows.Forms.Timer desktopTimer = new()
    {
        Interval = DesktopPollIntervalMilliseconds
    };
    private FocusOverlaySurface? surface;
    private FocusDisplayMode displayMode = FocusDisplayMode.Message;
    private string message = "수업에 집중해 주세요.";
    private bool disposed;
    private bool replacing;

    public FocusOverlayController()
    {
        desktopTimer.Tick += (_, _) => EnsureCurrentDesktopCoverage();
    }

    public void SetDisplay(FocusDisplayMode mode, string? text)
    {
        if (disposed)
        {
            return;
        }

        displayMode = mode;
        message = string.IsNullOrWhiteSpace(text) ? "수업에 집중해 주세요." : text.Trim();
        if (surface is null || surface.IsDisposed)
        {
            CreateSurface();
        }
        else
        {
            surface.SetDisplay(displayMode, message);
            surface.ReassertTopMost(activate: false);
        }

        desktopTimer.Start();
    }

    public void Show()
    {
        if (disposed)
        {
            return;
        }

        if (surface is null || surface.IsDisposed)
        {
            CreateSurface();
        }
        else
        {
            surface.ShowForCurrentDesktop(activate: true);
        }

        desktopTimer.Start();
    }

    public void Dismiss()
    {
        if (disposed)
        {
            return;
        }

        desktopTimer.Stop();
        surface?.Dismiss();
        surface = null;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        desktopTimer.Stop();
        desktopTimer.Dispose();
        surface?.Dismiss();
        surface = null;
    }

    private void EnsureCurrentDesktopCoverage()
    {
        if (disposed || replacing)
        {
            return;
        }

        if (surface is null || surface.IsDisposed)
        {
            CreateSurface();
            return;
        }

        if (!surface.IsOnCurrentVirtualDesktop())
        {
            CreateSurface();
            return;
        }

        surface.ReassertTopMost(activate: false);
    }

    private void CreateSurface()
    {
        if (disposed || replacing)
        {
            return;
        }

        replacing = true;
        try
        {
            var previous = surface;
            var next = new FocusOverlaySurface(displayMode, message);
            surface = next;
            next.ShowForCurrentDesktop(activate: true);
            // Show the replacement first. This avoids a visible gap when a
            // student changes virtual desktops while focus mode is active.
            previous?.Dismiss();
        }
        finally
        {
            replacing = false;
        }
    }
}

internal sealed class FocusOverlaySurface : Form
{
    private const int WsExToolWindow = 0x00000080;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly Color MessageBackground = Color.FromArgb(24, 36, 58);

    private readonly Label label = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.White,
        BackColor = MessageBackground,
        Font = new Font("Segoe UI Semibold", 28F, FontStyle.Bold, GraphicsUnit.Point)
    };
    private bool allowClose;

    public FocusOverlaySurface(FocusDisplayMode displayMode, string message)
    {
        Text = "Classroom 집중 모드";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        WindowState = FormWindowState.Normal;
        ShowInTaskbar = false;
        TopMost = true;
        ControlBox = false;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = MessageBackground;
        AutoScaleMode = AutoScaleMode.Dpi;
        SetDisplay(displayMode, message);
        Controls.Add(label);

        FormClosing += (_, eventArgs) =>
        {
            if (!allowClose && eventArgs.CloseReason == CloseReason.UserClosing)
            {
                eventArgs.Cancel = true;
                ReassertTopMost(activate: true);
            }
        };
        Deactivate += (_, _) =>
        {
            if (!allowClose && IsHandleCreated)
            {
                try
                {
                    BeginInvoke(new Action(() => ReassertTopMost(activate: true)));
                }
                catch (InvalidOperationException)
                {
                    // The form may be closing at the same time as Windows
                    // sends the deactivation event.
                }
            }
        };
    }

    public void SetDisplay(FocusDisplayMode displayMode, string message)
    {
        var blackScreen = displayMode is FocusDisplayMode.BlackScreen;
        var background = blackScreen ? Color.Black : MessageBackground;
        BackColor = background;
        label.BackColor = background;
        if (blackScreen)
        {
            // A black screen is intentionally content-free: no title,
            // message, accessible overlay text, or other visual hint is
            // painted on top of the black surface.
            label.Text = string.Empty;
            label.Visible = false;
            return;
        }

        label.ForeColor = Color.White;
        label.Text = $"집중 모드\n\n{message}";
        label.Visible = true;
    }

    public void ShowForCurrentDesktop(bool activate)
    {
        if (IsDisposed)
        {
            return;
        }

        SetBoundsToVirtualScreen();
        if (!Visible)
        {
            Show();
        }

        ReassertTopMost(activate);
    }

    public void ReassertTopMost(bool activate)
    {
        if (IsDisposed)
        {
            return;
        }

        SetBoundsToVirtualScreen();
        TopMost = true;
        if (IsHandleCreated && OperatingSystem.IsWindows())
        {
            SetWindowPos(
                Handle,
                HwndTopmost,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        }

        BringToFront();
        if (activate)
        {
            Activate();
        }
    }

    public bool IsOnCurrentVirtualDesktop()
    {
        if (!OperatingSystem.IsWindows() || !IsHandleCreated)
        {
            return true;
        }

        return VirtualDesktopApi.IsWindowOnCurrentDesktop(Handle);
    }

    public void Dismiss()
    {
        if (IsDisposed)
        {
            return;
        }

        allowClose = true;
        Close();
    }

    private void SetBoundsToVirtualScreen()
    {
        if (WindowState != FormWindowState.Normal)
        {
            WindowState = FormWindowState.Normal;
        }

        var bounds = SystemInformation.VirtualScreen;
        if (bounds.Width > 0 && bounds.Height > 0 && Bounds != bounds)
        {
            Bounds = bounds;
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow;
            return parameters;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    private static class VirtualDesktopApi
    {
        private static readonly Lazy<IVirtualDesktopManager?> Manager = new(CreateManager);

        public static bool IsWindowOnCurrentDesktop(IntPtr handle)
        {
            try
            {
                var manager = Manager.Value;
                if (manager is null)
                {
                    return true;
                }

                var result = manager.IsWindowOnCurrentVirtualDesktop(handle, out var onCurrentDesktop);
                // A non-zero HRESULT means this Windows build does not expose
                // the query. Keep the current surface rather than repeatedly
                // replacing it in a tight loop.
                return result == 0 ? onCurrentDesktop : true;
            }
            catch (COMException)
            {
                return true;
            }
            catch (InvalidCastException)
            {
                return true;
            }
            catch (Exception)
            {
                // Virtual-desktop COM support differs between Windows builds;
                // an unavailable query should never tear down focus mode.
                return true;
            }
        }

        private static IVirtualDesktopManager? CreateManager()
        {
            try
            {
                return (IVirtualDesktopManager)new VirtualDesktopManagerClass();
            }
            catch (COMException)
            {
                return null;
            }
            catch (InvalidCastException)
            {
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        [ComImport]
        [Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class VirtualDesktopManagerClass
        {
        }

        [ComImport]
        [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IVirtualDesktopManager
        {
            int IsWindowOnCurrentVirtualDesktop(
                IntPtr topLevelWindow,
                [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop);

            int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);

            int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
        }
    }
}
