using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Blossom.Classroom.Core.Desktop;

namespace Blossom.Classroom.Student.Service;

/// <summary>
/// Starts the tray/desktop process in the currently active Windows user
/// session. A Windows service runs in session 0, so Process.Start alone would
/// create an invisible process that cannot show the student's status UI or
/// own the desktop IPC channel.
/// </summary>
internal static class StudentDesktopSessionLauncher
{
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint StartfUseShowWindow = 0x00000001;
    private const ushort ShowNormal = 1;

    public static bool EnsureRunning(string desktopPath, Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(desktopPath)) return false;
        var deviceIdValue = Environment.GetEnvironmentVariable("CLASSROOM_DEVICE_ID");
        if (Guid.TryParse(deviceIdValue, out var deviceId)
            && deviceId != Guid.Empty
            && StudentDesktopExitAuthorization.IsGrantedForCurrentBoot(deviceId))
        {
            return false;
        }
        if (IsRunning(desktopPath)) return true;

        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == uint.MaxValue || !WTSQueryUserToken(sessionId, out var userToken))
        {
            log?.Invoke("현재 로그인한 Windows 사용자 세션이 없어 학생 화면을 다음 로그인으로 미룹니다.");
            return false;
        }

        try
        {
            var environment = IntPtr.Zero;
            try
            {
                if (!CreateEnvironmentBlock(out environment, userToken, false))
                {
                    log?.Invoke($"학생 화면 환경 변수를 만들지 못했습니다: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                var startup = new STARTUPINFO
                {
                    cb = (uint)Marshal.SizeOf<STARTUPINFO>(),
                    lpDesktop = "winsta0\\default",
                    dwFlags = StartfUseShowWindow,
                    wShowWindow = ShowNormal
                };
                var commandLine = new StringBuilder($"{Quote(desktopPath)} --classroom-watchdog");
                if (!CreateProcessAsUser(
                        userToken,
                        desktopPath,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        CreateUnicodeEnvironment,
                        environment,
                        Path.GetDirectoryName(desktopPath),
                        ref startup,
                        out var processInformation))
                {
                    log?.Invoke($"학생 화면 자동 복구에 실패했습니다: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                CloseHandle(processInformation.hProcess);
                CloseHandle(processInformation.hThread);
                log?.Invoke("활성 Windows 사용자 세션에서 학생 화면 감시를 자동 복구했습니다.");
                return true;
            }
            finally
            {
                if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            }
        }
        finally
        {
            CloseHandle(userToken);
        }
    }

    private static bool IsRunning(string desktopPath)
    {
        var expected = Path.GetFullPath(desktopPath);
        foreach (var process in Process.GetProcessesByName("Classroom.Student.Desktop"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path)
                    && string.Equals(Path.GetFullPath(path), expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public uint cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        IntPtr token,
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
