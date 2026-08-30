using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Blossom.Classroom.Student.Setup;

/// <summary>
/// Performs the privileged part of student installation without invoking
/// PowerShell. The setup UI starts this same executable with UAC elevation.
/// This is important on managed school PCs where PowerShell scripts may be
/// blocked before their first instruction is executed.
/// </summary>
internal static class ElevatedStudentInstaller
{
    private const string ServiceName = "ClassroomStudentService";
    private const string ServiceDisplayName = "Blossom Classroom Student Service";
    private const string ServiceRegistryPath =
        @"SYSTEM\CurrentControlSet\Services\ClassroomStudentService";
    private const string ConfigFormat = "BLOSSOM-CLASSROOM-DEVICE-V1";
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const int ServiceDoesNotExist = 1060;

    public static bool IsInstallInvocation(string[] args) =>
        args.Any(argument => string.Equals(argument, "--install-package", StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        var logPath = FindArgument(args, "--log-path");
        try
        {
            Log(logPath, "설치 도우미 시작");
            EnsureAdministrator();

            var packageRoot = RequiredArgument(args, "--package-root");
            var configPath = RequiredArgument(args, "--device-config-file");
            var agentVersion = RequiredArgument(args, "--agent-version");
            var installRoot = FindArgument(args, "--install-root")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Blossom Classroom Student");

            Install(packageRoot, configPath, installRoot, agentVersion, logPath);
            Log(logPath, "설치 도우미 완료");
            return 0;
        }
        catch (Exception exception)
        {
            Log(logPath, $"설치 실패: {exception}");
            return 1;
        }
    }

    private static void Install(
        string packageRoot,
        string configPath,
        string installRoot,
        string agentVersion,
        string? logPath)
    {
        var resolvedPackageRoot = Path.GetFullPath(packageRoot);
        if (!Directory.Exists(resolvedPackageRoot))
        {
            throw new DirectoryNotFoundException($"학생용 패키지 폴더를 찾지 못했습니다: {resolvedPackageRoot}");
        }

        var config = ReadDeviceConfig(configPath);
        var serviceSource = FindPayloadFile(
            resolvedPackageRoot,
            "Classroom.Student.Service.exe",
            Path.Combine("student-service", "Classroom.Student.Service.exe"),
            Path.Combine("student", "service", "Classroom.Student.Service.exe"));
        var desktopSource = FindPayloadFile(
            resolvedPackageRoot,
            "Classroom.Student.Desktop.exe",
            Path.Combine("student-desktop", "Classroom.Student.Desktop.exe"),
            Path.Combine("student", "desktop", "Classroom.Student.Desktop.exe"));

        Log(logPath, $"패키지 확인 완료: {resolvedPackageRoot}");
        Log(logPath, $"장치 설정 확인 완료: {config.DeviceId}");

        var serviceInstallRoot = Path.Combine(installRoot, "service");
        var desktopInstallRoot = Path.Combine(installRoot, "desktop");
        var installedService = Path.Combine(serviceInstallRoot, "Classroom.Student.Service.exe");
        var installedDesktop = Path.Combine(desktopInstallRoot, "Classroom.Student.Desktop.exe");

        StopExistingService(installedService, logPath);
        StopExistingDesktop(desktopInstallRoot, logPath);

        CopyDirectory(Path.GetDirectoryName(serviceSource)!, serviceInstallRoot);
        CopyDirectory(Path.GetDirectoryName(desktopSource)!, desktopInstallRoot);
        CopyOptionalFile(
            Path.Combine(resolvedPackageRoot, "Uninstall-ClassroomStudent.ps1"),
            Path.Combine(installRoot, "Uninstall-ClassroomStudent.ps1"));
        RemoveDownloadMark(installRoot);
        Log(logPath, "학생용 파일 복사 완료");

        if (!File.Exists(installedService) || !File.Exists(installedDesktop))
        {
            throw new InvalidOperationException("학생용 파일을 설치 위치에 복사하지 못했습니다.");
        }

        ConfigureService(installedService, config, agentVersion, logPath);
        StartService(logPath);

        // The UI process is deliberately started by the unelevated parent after
        // it restores the interactive user's HKCU environment. This also works
        // when a different local administrator approves the UAC prompt.
        Log(logPath, "Windows 서비스 실행 상태 확인 완료");
    }

    private static DeviceConfig ReadDeviceConfig(string configPath)
    {
        var resolvedPath = Path.GetFullPath(configPath);
        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException("장치 설정 파일을 찾지 못했습니다.", resolvedPath);
        }

        var config = JsonSerializer.Deserialize<DeviceConfig>(
            File.ReadAllText(resolvedPath, Encoding.UTF8),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true
            });
        if (config is null || !string.Equals(config.Format, ConfigFormat, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("지원하지 않는 장치 설정 파일입니다.");
        }

        if (!Uri.TryCreate(config.ServerUrl, UriKind.Absolute, out var serverUri)
            || serverUri.Scheme is not ("ws" or "wss"))
        {
            throw new InvalidOperationException("학생 에이전트 서버 URL이 올바르지 않습니다.");
        }

        if (config.DeviceId == Guid.Empty || string.IsNullOrWhiteSpace(config.DeviceToken))
        {
            throw new InvalidOperationException("장치 등록 정보가 완전하지 않습니다.");
        }

        return config with
        {
            IpcToken = string.IsNullOrWhiteSpace(config.IpcToken)
                ? CreateToken()
                : config.IpcToken
        };
    }

    private static void ConfigureService(
        string serviceExecutable,
        DeviceConfig config,
        string agentVersion,
        string? logPath)
    {
        var existing = QueryService();
        var operation = existing.Exists ? "config" : "create";
        var result = RunSc(
            new[]
            {
                operation,
                ServiceName,
                "binPath=",
                "\"" + serviceExecutable + "\"",
                "start=",
                "auto",
                "DisplayName=",
                ServiceDisplayName
            });
        EnsureScSuccess(operation, result);

        RunScOptional(
            new[]
            {
                "description",
                ServiceName,
                "Classroom 학생 기기 연결 및 상태 제공 서비스"
            },
            logPath);
        RunScOptional(
            new[]
            {
                "failure",
                ServiceName,
                "reset=",
                "86400",
                "actions=",
                "restart/5000/restart/15000/restart/60000"
            },
            logPath);

        using var serviceKey = Registry.LocalMachine.CreateSubKey(ServiceRegistryPath, writable: true)
            ?? throw new InvalidOperationException("Windows 서비스 레지스트리 키를 열지 못했습니다.");
        serviceKey.SetValue(
            "Environment",
            new[]
            {
                $"CLASSROOM_SERVER_URL={config.ServerUrl}",
                $"CLASSROOM_DEVICE_ID={config.DeviceId}",
                $"CLASSROOM_DEVICE_TOKEN={config.DeviceToken}",
                $"CLASSROOM_IPC_TOKEN={config.IpcToken}",
                $"CLASSROOM_AGENT_VERSION={agentVersion}"
            },
            RegistryValueKind.MultiString);
        Log(logPath, "Windows 서비스 등록 및 장치 설정 저장 완료");
    }

    private static void StartService(string? logPath)
    {
        var state = QueryService();
        if (!state.Exists)
        {
            throw new InvalidOperationException("Windows 서비스가 생성되지 않았습니다.");
        }

        if (state.Code != 4)
        {
            var start = RunSc(new[] { "start", ServiceName });
            // 1056 means the service is already running; it is safe to continue.
            if (start.ExitCode != 0 && start.ExitCode != 1056)
            {
                throw new InvalidOperationException($"Windows 서비스 시작에 실패했습니다: {FormatScResult(start)}");
            }
        }

        WaitForServiceState(4, logPath);
    }

    private static void StopExistingService(string expectedExecutable, string? logPath)
    {
        var state = QueryService();
        if (!state.Exists || state.Code == 1)
        {
            return;
        }

        var previousProcessId = state.ProcessId;
        Log(logPath, "기존 Classroom 서비스를 중지하는 중");
        var stop = RunSc(new[] { "stop", ServiceName });
        if (stop.ExitCode != 0 && stop.ExitCode != 1062)
        {
            throw new InvalidOperationException($"기존 Windows 서비스 중지에 실패했습니다: {FormatScResult(stop)}");
        }

        WaitForServiceState(1, logPath);
        WaitForProcessExit(previousProcessId, expectedExecutable, logPath);
    }

    private static void WaitForProcessExit(int processId, string expectedExecutable, string? logPath)
    {
        if (processId <= 0)
        {
            return;
        }

        var expectedPath = Path.GetFullPath(expectedExecutable);
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }

                var processPath = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(processPath)
                    && !string.Equals(
                        Path.GetFullPath(processPath),
                        expectedPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    // The PID was reused by an unrelated process. Do not wait
                    // for or terminate a process that is not Classroom.
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // The service process can briefly disappear while Windows closes
                // its handles. Treat that as successful termination.
                return;
            }

            Thread.Sleep(500);
        }

        Log(logPath, $"기존 서비스 프로세스가 종료되지 않음: PID {processId}");
        throw new TimeoutException("기존 Classroom 서비스가 파일을 해제할 때까지 기다리지 못했습니다.");
    }

    private static void StopExistingDesktop(string desktopInstallRoot, string? logPath)
    {
        var resolvedRoot = Path.GetFullPath(desktopInstallRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path)
                    && Path.GetFullPath(path).StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10_000);
                    Log(logPath, "기존 학생 화면 프로세스를 종료했습니다.");
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited between enumeration and inspection.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Protected/system processes are not relevant to the install.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void WaitForServiceState(int expectedCode, string? logPath)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        ServiceQuery state;
        do
        {
            state = QueryService();
            if (state.Exists && state.Code == expectedCode)
            {
                return;
            }

            Thread.Sleep(500);
        }
        while (DateTime.UtcNow < deadline);

        Log(logPath, $"서비스 상태 확인 실패: {FormatServiceQuery(state)}");
        throw new InvalidOperationException($"학생 서비스 상태가 예상과 다릅니다: {FormatServiceQuery(state)}");
    }

    private static ServiceQuery QueryService()
    {
        var manager = OpenScManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows 서비스 관리자에 연결하지 못했습니다.");
        }

        try
        {
            var service = OpenService(manager, ServiceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ServiceDoesNotExist)
                {
                    return new ServiceQuery(false, 0, 0);
                }

                throw new Win32Exception(error, "Windows Classroom 서비스에 연결하지 못했습니다.");
            }

            try
            {
                var size = Marshal.SizeOf<ServiceStatusProcess>();
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!QueryServiceStatusEx(
                            service,
                            ScStatusProcessInfo,
                            buffer,
                            size,
                            out _))
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "Windows Classroom 서비스 상태를 읽지 못했습니다.");
                    }

                    var status = Marshal.PtrToStructure<ServiceStatusProcess>(buffer);
                    return new ServiceQuery(
                        true,
                        checked((int)status.CurrentState),
                        checked((int)status.ProcessId));
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static ScResult RunSc(IReadOnlyList<string> arguments)
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
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("Windows 서비스 제어 도구를 시작하지 못했습니다.");
        }

        if (!process.WaitForExit(30_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited while the timeout was being handled.
            }

            throw new TimeoutException("Windows 서비스 제어 작업이 시간 초과되었습니다.");
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        return new ScResult(process.ExitCode, (output + Environment.NewLine + error).Trim());
    }

    private static void RunScOptional(IReadOnlyList<string> arguments, string? logPath)
    {
        var result = RunSc(arguments);
        if (result.ExitCode != 0)
        {
            Log(logPath, $"선택적 Windows 서비스 작업을 건너뜀: {FormatScResult(result)}");
        }
    }

    private static void EnsureScSuccess(string operation, ScResult result)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Windows 서비스 {operation} 작업에 실패했습니다: {FormatScResult(result)}");
        }
    }

    private static string FormatScResult(ScResult result) =>
        $"종료 코드 {result.ExitCode}{(string.IsNullOrWhiteSpace(result.Output) ? string.Empty : $": {result.Output}")}";

    private static string FormatServiceQuery(ServiceQuery query) =>
        query.Exists ? $"코드 {query.Code}" : "서비스 없음";

    private static string FindPayloadFile(string root, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var path = Path.Combine(root, candidate);
            if (File.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }

        throw new FileNotFoundException(
            $"학생용 구성 요소를 찾지 못했습니다: {string.Join(", ", candidates)}");
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"학생용 구성 요소 폴더를 찾지 못했습니다: {sourceDirectory}");
        }

        Directory.CreateDirectory(destinationDirectory);
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
            var destinationFile = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            RemoveDownloadMark(sourceFile);
            CopyFileWithRetry(sourceFile, destinationFile);
        }
    }

    private static void CopyFileWithRetry(string sourcePath, string destinationPath)
    {
        IOException? lastException = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        do
        {
            try
            {
                File.Copy(sourcePath, destinationPath, overwrite: true);
                return;
            }
            catch (IOException exception)
            {
                lastException = exception;
                Thread.Sleep(500);
            }
        }
        while (DateTime.UtcNow < deadline);

        throw new IOException(
            $"학생용 파일을 복사하지 못했습니다. 다른 프로세스가 파일을 사용 중일 수 있습니다: {destinationPath}",
            lastException);
    }

    private static void CopyOptionalFile(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        RemoveDownloadMark(sourcePath);
        File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    private static void RemoveDownloadMark(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path + ":Zone.Identifier");
            }
        }
        catch (IOException)
        {
            // Removing the mark is best effort; it is not required for copying.
        }
        catch (UnauthorizedAccessException)
        {
            // Removing the mark is best effort; it is not required for copying.
        }
    }

    private static void EnsureAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new InvalidOperationException("학생용 Classroom 설치는 관리자 권한이 필요합니다.");
        }
    }

    private static string RequiredArgument(string[] args, string name) =>
        FindArgument(args, name)
        ?? throw new InvalidOperationException($"설치 인자 {name}이(가) 없습니다.");

    private static string? FindArgument(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static string CreateToken()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static void Log(string? path, string message)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(
                path,
                $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // The parent setup app still reports the process exit code if logging
            // is blocked by a machine policy.
        }
    }

    private sealed record DeviceConfig(
        string Format,
        string ServerUrl,
        Guid DeviceId,
        string DeviceToken,
        string? IpcToken);

    private sealed record ScResult(int ExitCode, string Output);

    private sealed record ServiceQuery(bool Exists, int Code, int ProcessId);

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenScManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(
        IntPtr serviceControlManager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(
        IntPtr service,
        int infoLevel,
        IntPtr buffer,
        int bufferSize,
        out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }
}
