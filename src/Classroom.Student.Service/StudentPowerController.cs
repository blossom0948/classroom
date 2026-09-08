using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Blossom.Classroom.Protocol.Models;
using Blossom.Classroom.Student.Service.Commands;

namespace Blossom.Classroom.Student.Service;

/// <summary>
/// Executes the small, explicit set of power actions exposed by Classroom.
/// The service is the durable component, so shutdown and restart still work
/// when the visible tray process is restarting. A powered-off device cannot
/// receive a WebSocket command; Wake is therefore reported as requiring a
/// separately configured school-LAN WOL relay.
/// </summary>
public sealed class StudentPowerController(
    ILogger<StudentPowerController> logger)
{
    private const uint InvalidSessionId = 0xFFFFFFFF;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint StartfUseShowWindow = 0x00000001;
    private const ushort ShowHidden = 0;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const uint ExitWindowsShutdown = 0x00000001;
    private const uint ExitWindowsReboot = 0x00000002;
    private const uint ExitWindowsForceIfHung = 0x00000010;
    private readonly ConcurrentDictionary<Guid, byte> pending = [];

    public CommandApplyResult Schedule(CommandRequest command)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new CommandApplyResult(
                false,
                "WINDOWS_REQUIRED",
                "전원·잠금 제어는 Windows 학생 앱에서만 사용할 수 있습니다.");
        }

        if (command.PowerAction is null)
        {
            return new CommandApplyResult(false, "POWER_ACTION_MISSING", "전원 동작이 지정되지 않았습니다.");
        }

        if (command.PowerAction == PowerAction.Wake)
        {
            return new CommandApplyResult(
                false,
                "POWER_ON_REQUIRES_WOL_RELAY",
                "완전히 꺼진 PC를 켜려면 학교망 WOL 중계기가 필요합니다.");
        }

        if (!pending.TryAdd(command.RequestId, 0))
        {
            return new CommandApplyResult(false, "DUPLICATE_REQUEST", "전원 동작 요청이 이미 처리 중입니다.");
        }

        _ = ExecuteAfterAcknowledgementAsync(command.RequestId, command.PowerAction.Value);
        var label = command.PowerAction.Value switch
        {
            PowerAction.Lock => "잠금",
            PowerAction.Shutdown => "종료",
            PowerAction.Restart => "재시작",
            _ => "전원 동작"
        };
        return new CommandApplyResult(true, "POWER_ACTION_SCHEDULED", $"{label} 명령을 실행하도록 준비했습니다.");
    }

    private async Task ExecuteAfterAcknowledgementAsync(Guid requestId, PowerAction action)
    {
        try
        {
            // Give ClassroomServerClient enough time to send ACK and RESULT
            // before Windows tears down the service or locks the session.
            await Task.Delay(TimeSpan.FromMilliseconds(1_500));
            var result = action switch
            {
                PowerAction.Lock => LockActiveSession(),
                PowerAction.Shutdown => ShutdownOrRestart(restart: false),
                PowerAction.Restart => ShutdownOrRestart(restart: true),
                _ => new PowerExecutionResult(false, "POWER_ACTION_UNSUPPORTED", "지원하지 않는 전원 동작입니다.")
            };
            if (!result.Success)
            {
                logger.LogWarning(
                    "Student power action {Action} ({RequestId}) could not be executed: {Code} {Message}.",
                    action,
                    requestId,
                    result.Code,
                    result.Message);
            }
            else
            {
                logger.LogInformation("Student power action {Action} ({RequestId}) was executed.", action, requestId);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(exception, "Student power action {Action} ({RequestId}) failed.", action, requestId);
        }
        finally
        {
            pending.TryRemove(requestId, out _);
        }
    }

    private static PowerExecutionResult ShutdownOrRestart(bool restart)
    {
        if (!EnableShutdownPrivilege())
        {
            return new PowerExecutionResult(
                false,
                "POWER_PRIVILEGE_UNAVAILABLE",
                "Windows 전원 권한을 활성화하지 못했습니다.");
        }

        var flags = (restart ? ExitWindowsReboot : ExitWindowsShutdown) | ExitWindowsForceIfHung;
        if (!ExitWindowsEx(flags, 0))
        {
            return new PowerExecutionResult(
                false,
                "POWER_ACTION_FAILED",
                $"Windows 전원 동작을 실행하지 못했습니다. 오류 코드 {Marshal.GetLastWin32Error()}.");
        }

        return new PowerExecutionResult(true, "POWER_ACTION_EXECUTED", "Windows 전원 동작을 실행했습니다.");
    }

    private static PowerExecutionResult LockActiveSession()
    {
        if (!TryLaunchInActiveSession(out var errorCode))
        {
            return new PowerExecutionResult(
                false,
                errorCode == 0 ? "NO_INTERACTIVE_SESSION" : "LOCK_ACTION_FAILED",
                errorCode == 0
                    ? "로그인한 Windows 사용자 세션을 찾지 못했습니다."
                    : $"Windows 잠금 화면을 실행하지 못했습니다. 오류 코드 {errorCode}.");
        }

        return new PowerExecutionResult(true, "LOCK_ACTION_EXECUTED", "Windows 잠금 화면을 실행했습니다.");
    }

    private static bool TryLaunchInActiveSession(out int errorCode)
    {
        errorCode = 0;
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == InvalidSessionId || !WTSQueryUserToken(sessionId, out var userToken))
        {
            errorCode = Marshal.GetLastWin32Error();
            return false;
        }

        try
        {
            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var rundll32 = Path.Combine(systemDirectory, "rundll32.exe");
            if (!File.Exists(rundll32))
            {
                errorCode = 2;
                return false;
            }

            var environment = IntPtr.Zero;
            try
            {
                if (!CreateEnvironmentBlock(out environment, userToken, false))
                {
                    errorCode = Marshal.GetLastWin32Error();
                    return false;
                }

                var startup = new STARTUPINFO
                {
                    cb = (uint)Marshal.SizeOf<STARTUPINFO>(),
                    lpDesktop = "winsta0\\default",
                    dwFlags = StartfUseShowWindow,
                    wShowWindow = ShowHidden
                };
                var commandLine = new StringBuilder($"{Quote(rundll32)} user32.dll,LockWorkStation");
                if (!CreateProcessAsUser(
                        userToken,
                        rundll32,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        CreateUnicodeEnvironment,
                        environment,
                        systemDirectory,
                        ref startup,
                        out var processInformation))
                {
                    errorCode = Marshal.GetLastWin32Error();
                    return false;
                }

                CloseHandle(processInformation.hProcess);
                CloseHandle(processInformation.hThread);
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

    private static bool EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid))
            {
                return false;
            }

            var privileges = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SePrivilegeEnabled }
            };
            return AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)
                && Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private sealed record PowerExecutionResult(bool Success, string Code, string Message);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
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

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out IntPtr tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(
        string? systemName,
        string name,
        out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
