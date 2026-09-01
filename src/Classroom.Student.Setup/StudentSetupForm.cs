using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Blossom.Classroom.Student.Setup;

internal sealed class StudentSetupForm : Form
{
    private const string AgentVersion = "0.5.28";
    private const int JoinCodeLength = 8;
    private const string StudentPackageUrl = "https://github.com/blossom0948/classroom/releases/latest/download/Classroom-Windows-x64.zip";
    private const string InstallRootName = "Blossom Classroom Student";
    private readonly Uri serverOrigin;
    private readonly TextBox codeInput;
    private readonly Button enrollButton;
    private readonly Label statusLabel;
    private bool isBusy;

    public StudentSetupForm(Uri serverOrigin)
    {
        this.serverOrigin = serverOrigin;
        Text = "Classroom 학생 등록";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(480, 360);
        BackColor = Color.FromArgb(247, 249, 253);
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8,
            Padding = new Padding(34, 28, 34, 24),
            BackColor = Color.White
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        var title = new Label
        {
            AutoSize = true,
            Text = "Classroom 학생 등록",
            Font = new Font("Segoe UI", 19F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(29, 41, 57),
            Margin = new Padding(0, 0, 0, 6)
        };
        layout.Controls.Add(title, 0, 0);

        var description = new Label
        {
            AutoSize = true,
            Text = "선생님에게 받은 8자리 학생 코드를 입력하면\n이 컴퓨터가 자동으로 등록되고 Classroom이 설치됩니다.",
            ForeColor = Color.FromArgb(102, 112, 133),
            Margin = new Padding(0, 0, 0, 0)
        };
        layout.Controls.Add(description, 0, 1);

        var spacer = new Panel { Dock = DockStyle.Fill };
        layout.Controls.Add(spacer, 0, 2);

        var codeLabel = new Label
        {
            AutoSize = true,
            Text = "학생 코드",
            Font = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(52, 64, 84),
            Margin = new Padding(0, 0, 0, 4)
        };
        layout.Controls.Add(codeLabel, 0, 3);

        codeInput = new TextBox
        {
            Dock = DockStyle.Fill,
            MaxLength = JoinCodeLength,
            CharacterCasing = CharacterCasing.Upper,
            TextAlign = HorizontalAlignment.Center,
            Font = new Font("Segoe UI Semibold", 23F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(48, 85, 197),
            BackColor = Color.FromArgb(247, 249, 253),
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "예: AB7K2M9Q",
            Margin = new Padding(0, 0, 0, 8)
        };
        codeInput.TextChanged += (_, _) => NormalizeInput();
        codeInput.KeyDown += CodeInputKeyDown;
        layout.Controls.Add(codeInput, 0, 4);

        enrollButton = new Button
        {
            Dock = DockStyle.Fill,
            Text = "등록하고 설치하기",
            BackColor = Color.FromArgb(61, 100, 232),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 0, 8)
        };
        enrollButton.FlatAppearance.BorderSize = 0;
        enrollButton.Click += async (_, _) => await EnrollAndInstallAsync();
        layout.Controls.Add(enrollButton, 0, 5);

        statusLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = "학생 코드는 관리자가 새로 발급하기 전까지 계속 사용할 수 있습니다.",
            ForeColor = Color.FromArgb(102, 112, 133),
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(0, 0, 0, 0)
        };
        layout.Controls.Add(statusLabel, 0, 6);

        var serverLabel = new Label
        {
            AutoSize = true,
            Text = $"서버: {serverOrigin.Host}",
            ForeColor = Color.FromArgb(150, 162, 180),
            TextAlign = ContentAlignment.BottomLeft,
            Margin = new Padding(0, 2, 0, 0)
        };
        layout.Controls.Add(serverLabel, 0, 7);

        AcceptButton = enrollButton;
        Shown += (_, _) => codeInput.Focus();
    }

    private void CodeInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            _ = EnrollAndInstallAsync();
        }
    }

    private void NormalizeInput()
    {
        var normalized = NormalizeCode(codeInput.Text);
        if (codeInput.Text == normalized)
        {
            return;
        }

        var selection = codeInput.SelectionStart;
        codeInput.Text = normalized;
        codeInput.SelectionStart = Math.Min(selection, codeInput.TextLength);
    }

    private async Task EnrollAndInstallAsync()
    {
        if (isBusy)
        {
            return;
        }

        var joinCode = NormalizeCode(codeInput.Text);
        if (joinCode.Length != JoinCodeLength)
        {
            SetStatus("학생 코드는 8자리로 입력해 주세요.", isError: true);
            codeInput.Focus();
            return;
        }

        isBusy = true;
        enrollButton.Enabled = false;
        codeInput.Enabled = false;
        try
        {
            SetStatus("학생 코드를 확인하는 중...", isError: false);
            var enrollment = await EnrollDeviceAsync(joinCode);
            var configPath = Path.Combine(
                Path.GetTempPath(),
                $"classroom-device-{Guid.NewGuid():N}.json");
            try
            {
                var deviceConfig = new DeviceConfig(
                    "BLOSSOM-CLASSROOM-DEVICE-V1",
                    ToWebSocketOrigin(serverOrigin).AbsoluteUri,
                    enrollment.DeviceId,
                    enrollment.DeviceToken,
                    CreateIpcToken());
                await File.WriteAllTextAsync(
                    configPath,
                    JsonSerializer.Serialize(
                        deviceConfig,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)
                        {
                            WriteIndented = true
                        }));

                SetStatus("등록 완료. 관리자 권한으로 Classroom을 설치하는 중...", isError: false);
                await LaunchInstallerAsync(configPath, deviceConfig);
            }
            finally
            {
                TryDelete(configPath);
            }

            SetStatus("설치가 완료되었습니다. Classroom이 실행됩니다.", isError: false);
            MessageBox.Show(
                this,
                "학생 PC 등록과 설치가 완료되었습니다.\n이 창을 닫아도 Classroom 서비스는 계속 실행됩니다.",
                "Classroom 설치 완료",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            Close();
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            SetStatus("관리자 권한 승인이 취소되었습니다. 같은 코드를 다시 입력해 설치를 재시도할 수 있습니다.", isError: true);
        }
        catch (Exception exception)
        {
            var message = $"설치에 실패했습니다. {exception.Message}\n같은 코드를 다시 입력해 재시도하거나, 코드가 노출된 경우 관리자에게 재발급을 요청하세요.";
            SetStatus(message, isError: true);
            if (exception.Message.StartsWith("학생 서비스 설치가 완료되지 않았습니다.", StringComparison.Ordinal))
            {
                MessageBox.Show(this, exception.Message, "Classroom 설치 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            isBusy = false;
            enrollButton.Enabled = true;
            codeInput.Enabled = true;
        }
    }

    private async Task<DeviceEnrollmentResponse> EnrollDeviceAsync(string joinCode)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        using var response = await client.PostAsJsonAsync(
            new Uri(serverOrigin, "/api/devices/enroll-code"),
            new
            {
                joinCode,
                deviceName = Environment.MachineName,
                agentVersion = AgentVersion
            });
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ReadServerError(body, response.StatusCode));
        }

        var responseOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
        var enrollment = JsonSerializer.Deserialize<DeviceEnrollmentResponse>(body, responseOptions);
        if (enrollment is null
            || enrollment.DeviceId == Guid.Empty
            || string.IsNullOrWhiteSpace(enrollment.DeviceToken))
        {
            throw new InvalidOperationException("서버가 올바른 장치 등록 응답을 반환하지 않았습니다.");
        }

        return enrollment;
    }

    private async Task LaunchInstallerAsync(string configPath, DeviceConfig deviceConfig)
    {
        var packageRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string? downloadedPackageRoot = null;
        if (!HasStudentPayload(packageRoot))
        {
            SetStatus("학생용 구성 요소를 내려받는 중입니다. 잠시만 기다려 주세요...", isError: false);
            downloadedPackageRoot = await DownloadStudentPackageAsync();
            packageRoot = downloadedPackageRoot;
        }

        var logPath = Path.Combine(
            Path.GetTempPath(),
            $"install-{Guid.NewGuid():N}.log");
        var installSucceeded = false;
        try
        {
            // Create the file before UAC elevation so the original user can still
            // read it when a different local administrator approves the prompt.
            await File.WriteAllTextAsync(logPath, string.Empty);
            var executablePath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("학생용 설치 도우미 경로를 확인하지 못했습니다.");
            var installRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                InstallRootName);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = string.Join(
                    " ",
                    "--install-package",
                    "--package-root",
                    Quote(packageRoot),
                    "--device-config-file",
                    Quote(configPath),
                    "--agent-version",
                    Quote(AgentVersion),
                    "--install-root",
                    Quote(installRoot),
                    "--log-path",
                    Quote(logPath)),
                WorkingDirectory = packageRoot,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            }) ?? throw new InvalidOperationException("Windows 설치 프로세스를 시작하지 못했습니다.");

            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                var detail = ReadInstallLog(logPath);
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(detail)
                        ? $"학생 서비스 설치가 완료되지 않았습니다. (코드 {process.ExitCode})\n설치 도우미가 로그를 남기기 전에 종료되었습니다. Windows 보안 정책 또는 관리자 권한을 확인해 주세요.\n로그: {logPath}"
                        : $"학생 서비스 설치가 완료되지 않았습니다.\n{detail}\n로그: {logPath}");
            }

            ConfigureUserSession(deviceConfig, installRoot);
            var desktopStarted = TryStartInstalledDesktop(installRoot);
            installSucceeded = true;
            SetStatus(
                desktopStarted
                    ? "설치가 완료되었습니다. Classroom이 실행됩니다."
                    : "설치가 완료되었습니다. 바탕화면의 Classroom을 실행해 주세요.",
                isError: false);
        }
        finally
        {
            if (installSucceeded)
            {
                TryDelete(logPath);
            }
            if (downloadedPackageRoot is not null)
            {
                TryDeleteDirectory(downloadedPackageRoot);
            }
        }
    }

    private static void ConfigureUserSession(DeviceConfig config, string installRoot)
    {
        // Keep the values in this process too, because the desktop app is
        // started immediately after installation and inherits this environment.
        Environment.SetEnvironmentVariable("CLASSROOM_DEVICE_ID", config.DeviceId.ToString());
        Environment.SetEnvironmentVariable("CLASSROOM_IPC_TOKEN", config.IpcToken);
        Environment.SetEnvironmentVariable("CLASSROOM_AGENT_VERSION", AgentVersion);
        Environment.SetEnvironmentVariable(
            "CLASSROOM_DEVICE_ID",
            config.DeviceId.ToString(),
            EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(
            "CLASSROOM_IPC_TOKEN",
            config.IpcToken,
            EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(
            "CLASSROOM_AGENT_VERSION",
            AgentVersion,
            EnvironmentVariableTarget.User);

        var desktopPath = Path.Combine(installRoot, "desktop", "Classroom.Student.Desktop.exe");
        using var runKey = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            writable: true);
        runKey?.SetValue(
            "BlossomClassroomStudent",
            $"{Quote(desktopPath)} --classroom-watchdog",
            RegistryValueKind.String);
        CreateStartMenuShortcut(desktopPath);
    }

    private static void CreateStartMenuShortcut(string desktopPath)
    {
        try
        {
            var programsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs");
            Directory.CreateDirectory(programsDirectory);
            var shortcutPath = Path.Combine(programsDirectory, "Classroom Student.lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return;
            }

            object? shell = null;
            object? shortcut = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                if (shell is null)
                {
                    return;
                }

                shortcut = shellType.InvokeMember(
                    "CreateShortcut",
                    BindingFlags.InvokeMethod,
                    binder: null,
                    target: shell,
                    args: [shortcutPath]);
                if (shortcut is null)
                {
                    return;
                }

                var shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [desktopPath]);
                shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, ["--classroom-watchdog"]);
                shortcutType.InvokeMember(
                    "WorkingDirectory",
                    BindingFlags.SetProperty,
                    null,
                    shortcut,
                    [Path.GetDirectoryName(desktopPath) ?? string.Empty]);
                shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, ["Classroom 학교 학생 상태 앱"]);
                shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, [$"{desktopPath},0"]);
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally
            {
                if (shortcut is not null && Marshal.IsComObject(shortcut))
                {
                    Marshal.FinalReleaseComObject(shortcut);
                }

                if (shell is not null && Marshal.IsComObject(shell))
                {
                    Marshal.FinalReleaseComObject(shell);
                }
            }
        }
        catch (Exception)
        {
            // The Run key remains the authoritative startup mechanism. A
            // managed Windows image may disable the WScript shortcut COM
            // object, so a shortcut failure must not make enrollment fail.
        }
    }

    private static bool TryStartInstalledDesktop(string installRoot)
    {
        var desktopPath = Path.Combine(installRoot, "desktop", "Classroom.Student.Desktop.exe");
        if (!File.Exists(desktopPath))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = desktopPath,
                Arguments = "--classroom-watchdog",
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(desktopPath)!
            });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static bool HasStudentPayload(string packageRoot) =>
        (File.Exists(Path.Combine(packageRoot, "student-service", "Classroom.Student.Service.exe"))
            || File.Exists(Path.Combine(packageRoot, "Classroom.Student.Service.exe")))
        && (File.Exists(Path.Combine(packageRoot, "student-desktop", "Classroom.Student.Desktop.exe"))
            || File.Exists(Path.Combine(packageRoot, "Classroom.Student.Desktop.exe")));

    private static async Task<string> DownloadStudentPackageAsync()
    {
        var packageRoot = Path.Combine(
            Path.GetTempPath(),
            $"classroom-student-package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(packageRoot);
        var zipPath = Path.Combine(packageRoot, "Classroom-Windows-x64.zip");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var response = await client.GetAsync(
                StudentPackageUrl,
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (var output = File.Create(zipPath))
            {
                await response.Content.CopyToAsync(output);
            }

            ZipFile.ExtractToDirectory(zipPath, packageRoot);
            TryDelete(zipPath);
            if (!HasStudentPayload(packageRoot))
            {
                throw new InvalidOperationException("학생용 설치 구성 요소를 내려받았지만 패키지가 올바르지 않습니다.");
            }

            return packageRoot;
        }
        catch
        {
            TryDeleteDirectory(packageRoot);
            throw new InvalidOperationException(
                "학생용 구성 요소를 내려받지 못했습니다. 인터넷 연결 또는 학교 네트워크 정책을 확인해 주세요.");
        }
    }

    private static string ReadInstallLog(string path)
    {
        try
        {
            if (!File.Exists(path)) return string.Empty;
            var text = File.ReadAllText(path).Trim();
            return text.Length > 2400 ? text[^2400..] : text;
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private void SetStatus(string message, bool isError)
    {
        statusLabel.Text = message;
        statusLabel.ForeColor = isError
            ? Color.FromArgb(196, 68, 68)
            : Color.FromArgb(102, 112, 133);
    }

    private static string NormalizeCode(string value) =>
        new string(value
            .Where(character => !char.IsWhiteSpace(character) && character != '-')
            .ToArray())
        .ToUpperInvariant();

    private static Uri ToWebSocketOrigin(Uri origin)
    {
        var builder = new UriBuilder(origin)
        {
            Scheme = origin.Scheme == "https" ? "wss" : "ws",
            Port = origin.IsDefaultPort ? -1 : origin.Port,
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
    }

    private static string CreateIpcToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string Quote(string value) =>
        $"\"{value.Replace("\"", "\\\"")}\"";

    private static string ReadServerError(string body, System.Net.HttpStatusCode statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(message.GetString()))
            {
                return message.GetString()!;
            }
        }
        catch (JsonException)
        {
            // Fall back to a status-specific message below.
        }

        return statusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => "학생 코드가 올바르지 않거나 관리자에 의해 재발급되었습니다.",
            System.Net.HttpStatusCode.Conflict => "학생 코드 처리에 실패했습니다. 잠시 후 다시 시도하세요.",
            System.Net.HttpStatusCode.TooManyRequests => "잠시 후 다시 시도해 주세요.",
            _ => $"서버 요청에 실패했습니다. ({(int)statusCode})"
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The temporary config contains a short-lived device token. The installer
            // normally releases it before this point; a locked file is harmless.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A failed cleanup does not invalidate an otherwise successful install.
        }
        catch (UnauthorizedAccessException)
        {
            // A failed cleanup does not invalidate an otherwise successful install.
        }
    }

    private sealed record DeviceConfig(
        string Format,
        string ServerUrl,
        Guid DeviceId,
        string DeviceToken,
        string IpcToken);

    private sealed record DeviceEnrollmentResponse(
        [property: JsonPropertyName("deviceId")] Guid DeviceId,
        [property: JsonPropertyName("deviceToken")] string DeviceToken);
}
