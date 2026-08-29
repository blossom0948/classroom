using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Blossom.Classroom.Protocol.Models;

namespace Blossom.Classroom.Student.Desktop.Status;

public sealed record DesktopStatusData(
    ActivitySnapshot? Activity,
    int? BatteryPercent,
    string? NetworkStatus,
    bool PolicyApplied);

public sealed class WindowsStudentStatusProvider
{
    private int policyApplied;

    public void SetPolicyApplied(bool applied) =>
        Interlocked.Exchange(ref policyApplied, applied ? 1 : 0);

    public DesktopStatusData GetCurrent()
    {
        return new DesktopStatusData(
            GetForegroundActivity(),
            GetBatteryPercent(),
            GetNetworkStatus(),
            Volatile.Read(ref policyApplied) == 1);
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
                WindowTitle: null,
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
