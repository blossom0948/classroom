using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Blossom.Classroom.Student.Service;

/// <summary>
/// Applies a validated student package from outside the running service
/// process. This is an update helper only: it does not monitor, hide, or block
/// other Windows processes. The helper is launched from the new staged service
/// executable so the installed executable can be replaced after the old
/// service has stopped.
/// </summary>
internal static class StudentUpdateHelper
{
    private const string ServiceName = "ClassroomStudentService";
    private const int ServiceAlreadyStopped = 1062;
    private const int ServiceAlreadyRunning = 1056;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint StartfUseShowWindow = 0x00000001;
    private const ushort ShowNormal = 1;

    public static async Task<int> RunAsync(string[] args)
    {
        var installRoot = Argument(args, "--install-root");
        var payloadRoot = Argument(args, "--payload-root");
        var version = Argument(args, "--version");
        var parentPidText = Argument(args, "--parent-pid");
        var logPath = TryGetLogPath(installRoot, version);

        try
        {
            ValidateArguments(installRoot, payloadRoot, version, parentPidText);
            using var log = new UpdateLog(logPath);
            log.Write($"학생 앱 업데이트 적용 시작: v{version}");
            await Task.Delay(750);

            var parentPid = int.Parse(parentPidText!, System.Globalization.CultureInfo.InvariantCulture);
            StopService(log);
            if (!WaitForProcessExit(parentPid, TimeSpan.FromSeconds(45), log))
            {
                throw new TimeoutException("기존 학생 서비스가 종료될 때까지 기다리지 못했습니다.");
            }

            StopInstalledDesktopProcesses(Path.Combine(installRoot!, "desktop"), log);
            CopyDirectory(
                Path.Combine(payloadRoot!, "student-service"),
                Path.Combine(installRoot!, "service"),
                log);
            CopyDirectory(
                Path.Combine(payloadRoot!, "student-desktop"),
                Path.Combine(installRoot!, "desktop"),
                log);

            StartService(log);
            TryStartDesktopInActiveSession(
                Path.Combine(installRoot!, "desktop", "Classroom.Student.Desktop.exe"),
                log);
            log.Write("학생 앱 업데이트 적용 완료: 재부팅 없이 서비스와 화면을 다시 시작했습니다.");
            return 0;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or ArgumentException
            or FormatException
            or IOException
            or UnauthorizedAccessException
            or TimeoutException
            or System.ComponentModel.Win32Exception)
        {
            TryAppendLog(logPath, $"학생 앱 업데이트 적용 실패: {exception}");
            return 1;
        }
    }

    private static void ValidateArguments(
        string? installRoot,
        string? payloadRoot,
        string? version,
        string? parentPidText)
    {
        if (string.IsNullOrWhiteSpace(installRoot)
            || string.IsNullOrWhiteSpace(payloadRoot)
            || string.IsNullOrWhiteSpace(version)
            || !int.TryParse(parentPidText, out var parentPid)
            || parentPid <= 0
            || !Version.TryParse(version, out _))
        {
            throw new ArgumentException("학생 앱 업데이트 도우미 인자가 올바르지 않습니다.");
        }

        var resolvedInstallRoot = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
        var programFiles = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
            .TrimEnd(Path.DirectorySeparatorChar);
        var installPrefix = programFiles + Path.DirectorySeparatorChar;
        if (!resolvedInstallRoot.StartsWith(installPrefix, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetFileName(resolvedInstallRoot),
                "Blossom Classroom Student",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("학생 앱 설치 위치가 허용된 경로가 아닙니다.");
        }

        var updatesRoot = Path.Combine(resolvedInstallRoot, ".updates") + Path.DirectorySeparatorChar;
        var resolvedPayloadRoot = Path.GetFullPath(payloadRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!resolvedPayloadRoot.StartsWith(updatesRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("학생 앱 업데이트 임시 위치가 허용된 경로가 아닙니다.");
        }

        var servicePayload = Path.Combine(resolvedPayloadRoot, "student-service", "Classroom.Student.Service.exe");
        var desktopPayload = Path.Combine(resolvedPayloadRoot, "student-desktop", "Classroom.Student.Desktop.exe");
        if (!File.Exists(servicePayload) || !File.Exists(desktopPayload))
        {
            throw new FileNotFoundException("검증된 학생 앱 업데이트 파일을 찾지 못했습니다.");
        }
    }

    private static void StopService(UpdateLog log)
    {
        var result = RunSc("stop", ServiceName);
        if (result.ExitCode != 0 && result.ExitCode != ServiceAlreadyStopped)
        {
            log.Write($"학생 서비스 중지 명령 결과: {result.ExitCode} {result.Output}");
            throw new InvalidOperationException("기존 학생 서비스를 중지하지 못했습니다.");
        }

        log.Write("기존 학생 서비스 중지 요청 완료");
    }

    private static void StartService(UpdateLog log)
    {
        var result = RunSc("start", ServiceName);
        if (result.ExitCode != 0 && result.ExitCode != ServiceAlreadyRunning)
        {
            log.Write($"학생 서비스 시작 명령 결과: {result.ExitCode} {result.Output}");
            throw new InvalidOperationException("업데이트 후 학생 서비스를 시작하지 못했습니다.");
        }

        log.Write("업데이트 후 학생 서비스 시작 완료");
    }

    private static bool WaitForProcessExit(int processId, TimeSpan timeout, UpdateLog log)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    log.Write($"기존 학생 서비스 프로세스 종료 확인: PID {processId}");
                    return true;
                }
            }
            catch (ArgumentException)
            {
                log.Write($"기존 학생 서비스 프로세스 종료 확인: PID {processId}");
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }

            Thread.Sleep(250);
        }

        return false;
    }

    private static void StopInstalledDesktopProcesses(string desktopRoot, UpdateLog log)
    {
        var resolvedRoot = Path.GetFullPath(desktopRoot).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcessesByName("Classroom.Student.Desktop"))
        {
            try
            {
                var processPath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(processPath)
                    || !Path.GetFullPath(processPath).StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
                log.Write("업데이트를 위해 설치된 학생 화면 프로세스를 종료했습니다.");
            }
            catch (InvalidOperationException)
            {
                // The process exited between enumeration and inspection.
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                log.Write($"학생 화면 종료를 기다리는 중 Windows 오류: {exception.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void CopyDirectory(string sourceRoot, string destinationRoot, UpdateLog log)
    {
        var sourcePrefix = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var destinationPrefix = Path.GetFullPath(destinationRoot).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(destinationRoot);
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourcePrefix, source);
            var destination = Path.GetFullPath(Path.Combine(destinationRoot, relative));
            if (!destination.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("학생 앱 업데이트에 허용되지 않은 파일 경로가 있습니다.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            CopyWithRetry(source, destination);
        }

        log.Write($"업데이트 파일 복사 완료: {destinationRoot}");
    }

    private static void CopyWithRetry(string source, string destination)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                File.Copy(source, destination, overwrite: true);
                return;
            }
            catch (IOException exception)
            {
                lastException = exception;
                Thread.Sleep(250);
            }
            catch (UnauthorizedAccessException exception)
            {
                lastException = exception;
                Thread.Sleep(250);
            }
        }

        throw new IOException($"학생 앱 파일을 교체하지 못했습니다: {destination}", lastException);
    }

    private static bool TryStartDesktopInActiveSession(string desktopPath, UpdateLog log)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(desktopPath))
        {
            return false;
        }

        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == uint.MaxValue || !WTSQueryUserToken(sessionId, out var userToken))
        {
            log.Write("현재 로그인한 대화형 사용자가 없어 학생 화면은 다음 로그인 때 자동으로 시작됩니다.");
            return false;
        }

        try
        {
            var environment = IntPtr.Zero;
            try
            {
                if (!CreateEnvironmentBlock(out environment, userToken, false))
                {
                    log.Write($"학생 화면 환경 변수를 만들지 못했습니다: {Marshal.GetLastWin32Error()}");
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
                    log.Write($"학생 화면을 다시 시작하지 못했습니다: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                CloseHandle(processInformation.hProcess);
                CloseHandle(processInformation.hThread);
                log.Write("현재 로그인한 사용자 세션에서 학생 화면을 다시 시작했습니다.");
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

    private static ScResult RunSc(params string[] arguments)
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var executable = Path.Combine(windowsDirectory, "System32", "sc.exe");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new InvalidOperationException("Windows 서비스 제어 도구를 시작하지 못했습니다.");
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("Windows 서비스 제어 작업이 시간 초과되었습니다.");
        }

        return new ScResult(
            process.ExitCode,
            (process.StandardOutput.ReadToEnd() + Environment.NewLine + process.StandardError.ReadToEnd()).Trim());
    }

    private static string? Argument(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        }

        return null;
    }

    private static string? TryGetLogPath(string? installRoot, string? version)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || string.IsNullOrWhiteSpace(version)) return null;
        try { return Path.Combine(Path.GetFullPath(installRoot), ".updates", version, "apply.log"); }
        catch (ArgumentException) { return null; }
    }

    private static void TryAppendLog(string? path, string message)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private sealed record ScResult(int ExitCode, string Output);

    private sealed class UpdateLog : IDisposable
    {
        private readonly StreamWriter writer;

        public UpdateLog(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("업데이트 로그 위치가 없습니다.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            writer = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = true };
        }

        public void Write(string message) => writer.WriteLine($"[{DateTimeOffset.Now:O}] {message}");

        public void Dispose() => writer.Dispose();
    }

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
