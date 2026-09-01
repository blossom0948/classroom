using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using Blossom.Classroom.Protocol;
using Blossom.Classroom.Protocol.Models;

namespace Blossom.Classroom.Student.Desktop.Status;

public sealed record DesktopStatusData(
    ActivitySnapshot? Activity,
    int? BatteryPercent,
    string? NetworkStatus,
    bool PolicyApplied,
    ScreenFrame? ScreenFrame = null,
    bool ScreenSharingEnabled = false,
    bool NeedsHelp = false,
    int ScreenShareIntervalMilliseconds = ProtocolConstants.ScreenShareStandardIntervalMilliseconds);

public sealed class WindowsStudentStatusProvider
{
    private int policyApplied;
    private int screenSharingEnabled;
    private int helpRequested;
    private int screenShareIntervalMilliseconds = ProtocolConstants.ScreenShareStandardIntervalMilliseconds;

    public void SetPolicyApplied(bool applied) =>
        Interlocked.Exchange(ref policyApplied, applied ? 1 : 0);

    public void SetScreenSharing(bool enabled, int? intervalMilliseconds = null)
    {
        var effectiveInterval = enabled && intervalMilliseconds is >= ProtocolConstants.ScreenShareMinimumIntervalMilliseconds
            and <= ProtocolConstants.ScreenShareMaximumIntervalMilliseconds
            ? intervalMilliseconds.Value
            : ProtocolConstants.ScreenShareStandardIntervalMilliseconds;
        Interlocked.Exchange(ref screenShareIntervalMilliseconds, effectiveInterval);
        Interlocked.Exchange(ref screenSharingEnabled, enabled ? 1 : 0);
    }

    public void SetHelpRequested(bool requested) =>
        Interlocked.Exchange(ref helpRequested, requested ? 1 : 0);

    public DesktopStatusData GetCurrent()
    {
        var sharing = Volatile.Read(ref screenSharingEnabled) == 1;
        return new DesktopStatusData(
            GetForegroundActivity(),
            GetBatteryPercent(),
            GetNetworkStatus(),
            Volatile.Read(ref policyApplied) == 1,
            sharing ? CapturePrimaryScreen() : null,
            sharing,
            Volatile.Read(ref helpRequested) == 1,
            Volatile.Read(ref screenShareIntervalMilliseconds));
    }

    private static ScreenFrame? CapturePrimaryScreen()
    {
        try
        {
            var bounds = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return null;
            }

            using var source = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(source))
            {
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            }

            var attemptedSizes = new HashSet<(int Width, int Height)>();
            foreach (var (targetWidth, qualities) in new[]
            {
                (ProtocolConstants.MaxScreenFrameWidth, new long[] { 58, 50, 42 }),
                (1_024, new long[] { 62, 54, 46 }),
                (800, new long[] { 64, 56, 48 }),
                (640, new long[] { 66, 58, 50 })
            })
            {
                var width = Math.Min(targetWidth, source.Width);
                var height = Math.Max(1, (int)Math.Round(source.Height * (width / (double)source.Width)));
                if (height > ProtocolConstants.MaxScreenFrameHeight)
                {
                    height = ProtocolConstants.MaxScreenFrameHeight;
                    width = Math.Max(1, (int)Math.Round(source.Width * (height / (double)source.Height)));
                }

                if (!attemptedSizes.Add((width, height)))
                {
                    continue;
                }

                using var thumbnail = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                using (var graphics = Graphics.FromImage(thumbnail))
                {
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.SmoothingMode = SmoothingMode.HighQuality;
                    graphics.DrawImage(source, new Rectangle(0, 0, width, height));
                }

                foreach (var quality in qualities)
                {
                    var bytes = EncodeJpeg(thumbnail, quality);
                    if (bytes.Length <= ProtocolConstants.MaxScreenFrameBytes)
                    {
                        return new ScreenFrame(
                            "image/jpeg",
                            Convert.ToBase64String(bytes),
                            width,
                            height,
                            DateTimeOffset.UtcNow);
                    }
                }
            }
        }
        catch (Exception exception) when (
            exception is ExternalException
            or ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            // Secure desktop, display transitions, and disconnected sessions
            // can temporarily make CopyFromScreen unavailable.
        }

        return null;
    }

    private static byte[] EncodeJpeg(Image image, long quality)
    {
        var codec = ImageCodecInfo.GetImageEncoders()
            .First(item => item.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        using var stream = new MemoryStream();
        image.Save(stream, codec, parameters);
        return stream.ToArray();
    }

    private static ActivitySnapshot GetForegroundActivity()
    {
        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero)
            {
                return UnknownActivity();
            }

            GetWindowThreadProcessId(window, out var processId);
            if (processId == 0)
            {
                return UnknownActivity();
            }

            using var process = Process.GetProcessById((int)processId);
            var processName = process.ProcessName;
            var displayName = IsBrowser(processName)
                ? ToBrowserDisplayName(processName)
                : processName;
            return new ActivitySnapshot(
                displayName,
                processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? processName
                    : $"{processName}.exe",
                BrowserDomain: null,
                WindowTitle: GetWindowTitle(window),
                DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return UnknownActivity();
        }
    }

    private static int? GetBatteryPercent()
    {
        if (!GetSystemPowerStatus(out var status) || status.BatteryLifePercent == 255)
        {
            return null;
        }

        return Math.Clamp((int)status.BatteryLifePercent, 0, 100);
    }

    private static string GetNetworkStatus()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up)
                .ToArray();
            if (interfaces.Length == 0)
            {
                return "offline";
            }

            if (interfaces.Any(networkInterface => networkInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
            {
                return "wifi";
            }

            if (interfaces.Any(networkInterface => networkInterface.NetworkInterfaceType == NetworkInterfaceType.Ethernet))
            {
                return "ethernet";
            }

            return "connected";
        }
        catch (NetworkInformationException)
        {
            return "unknown";
        }
    }

    private static ActivitySnapshot UnknownActivity() =>
        new("알 수 없음", "unknown.exe", null, null, DateTimeOffset.UtcNow);

    private static string? GetWindowTitle(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0)
        {
            return null;
        }

        var buffer = new StringBuilder(length + 1);
        return GetWindowText(window, buffer, buffer.Capacity) > 0
            ? buffer.ToString().Trim()
            : null;
    }

    private static bool IsBrowser(string processName) =>
        processName.Equals("chrome", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("msedge", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("firefox", StringComparison.OrdinalIgnoreCase);

    private static string ToBrowserDisplayName(string processName) => processName.ToLowerInvariant() switch
    {
        "chrome" => "Chrome",
        "msedge" => "Edge",
        "firefox" => "Firefox",
        _ => processName
    };

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte Reserved1;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }
}
