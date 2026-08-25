using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PhoneUnlock.Core.Protocol;
using PhoneUnlock.Setup.Models;
using QRCoder;

namespace PhoneUnlock.Setup;

public partial class MainWindow : Window
{
    private const string ProviderGuid = "{8C12D44B-04D3-41D4-980B-80DF3D8DD324}";
    private const string ProviderRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\" + ProviderGuid;
    private const string LogonPolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\System";
    private const string DefaultProviderValue = "DefaultCredentialProvider";

    private readonly SetupPipeClient client = new();
    private readonly string currentQualifiedUsername;
    private SetupStatus? currentStatus;
    private bool updatingControls;

    public MainWindow()
    {
        InitializeComponent();
        currentQualifiedUsername = WindowsIdentity.GetCurrent().Name
            ?? $"{Environment.UserDomainName}\\{Environment.UserName}";
        DetectedAccountText.Text = currentQualifiedUsername;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshStatusAsync();
        await RefreshAuditAsync(silent: true);
        await CheckForUpdateAsync(silent: true);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshStatusAsync();

    private async void UseSelectedPhone_Click(object sender, RoutedEventArgs e)
    {
        if (PhoneSelectorComboBox.SelectedItem is not PhoneSelectionItem phone)
        {
            SetOperation("먼저 로그인에 사용할 휴대폰을 선택하세요.", success: false);
            return;
        }

        await RunOperationAsync(async () =>
        {
            var response = await client.SendAsync(
                new SetupRequest(SetupCommands.SetPreferredPhone, PhoneId: phone.PhoneId),
                TimeSpan.FromSeconds(10));
            SetOperation(response.Message, response.Success);
            if (response.Success) await RefreshStatusAsync();
        });
    }

    private async void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(async () =>
        {
            var response = await client.SendAsync(
                new SetupRequest(SetupCommands.Diagnostics),
                TimeSpan.FromSeconds(10));
            if (!response.Success || string.IsNullOrWhiteSpace(response.Data))
            {
                SetOperation(response.Message, success: false);
                return;
            }

            var diagnostics = ProtocolJson.Deserialize<SetupDiagnostics>(response.Data);
            var connected = diagnostics.Phones.Count(phone => phone.Connected);
            DiagnosticsSummaryText.Text = connected == 0
                ? $"PC 주소 {string.Join(", ", diagnostics.LocalAddresses)} · 연결된 휴대폰 없음 · 자동잠금 에이전트 {(diagnostics.InteractiveAgentConnected ? "연결됨" : "없음")}"
                : $"PC 주소 {string.Join(", ", diagnostics.LocalAddresses)} · 휴대폰 {connected}대 연결됨 · 포트 {diagnostics.ListeningPort} · 자동잠금 에이전트 {(diagnostics.InteractiveAgentConnected ? "연결됨" : "없음")}";
            SetOperation("Windows 연결 진단을 완료했습니다. 알림·배터리 상태는 휴대폰 앱 진단에서 확인하세요.", success: true);
        });
    }

    private async void AuditRefresh_Click(object sender, RoutedEventArgs e) => await RefreshAuditAsync(silent: false);

    private async void ProximityLock_Click(object sender, RoutedEventArgs e)
    {
        if (updatingControls) return;
        await SaveProximityLockAsync();
    }

    private async void ProximityUnlock_Click(object sender, RoutedEventArgs e)
    {
        if (updatingControls) return;
        await SaveProximityUnlockAsync();
    }

    private void StartAgent_Click(object sender, RoutedEventArgs e)
    {
        var agentPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "agent", "PhoneUnlock.Agent.exe"));
        if (!File.Exists(agentPath))
        {
            SetOperation("자동잠금 에이전트가 없습니다. 최신 PhoneUnlock-Setup.exe로 Windows를 업데이트하세요.", success: false);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(agentPath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(agentPath) ?? AppContext.BaseDirectory
            });
            SetOperation("자동잠금 감시를 시작했습니다. 이 창을 닫아도 백그라운드에서 계속 실행됩니다.", success: true);
        }
        catch (Exception exception)
        {
            SetOperation($"자동잠금 감시를 시작하지 못했습니다: {exception.Message}", success: false);
        }
    }

    private async void ProximityGrace_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingControls || !IsLoaded || currentStatus is null || ProximityLockCheckBox.IsChecked != true) return;
        await SaveProximityLockAsync();
    }

    private async Task SaveProximityLockAsync()
    {
        var enabled = ProximityLockCheckBox.IsChecked == true;
        var grace = SelectedGraceSeconds();
        await RunOperationAsync(async () =>
        {
            var response = await client.SendAsync(
                new SetupRequest(SetupCommands.SetProximityLock, Enabled: enabled, GraceSeconds: grace),
                TimeSpan.FromSeconds(10));
            SetOperation(response.Message, response.Success);
            if (response.Success) await RefreshStatusAsync();
        });
    }

    private async Task SaveProximityUnlockAsync()
    {
        var enabled = ProximityUnlockCheckBox.IsChecked == true;
        await RunOperationAsync(async () =>
        {
            var response = await client.SendAsync(
                new SetupRequest(SetupCommands.SetProximityUnlock, Enabled: enabled),
                TimeSpan.FromSeconds(10));
            SetOperation(response.Message, response.Success);
            if (response.Success) await RefreshStatusAsync();
        });
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        await DownloadAndLaunchInstallerAsync();
    }

    private async void Update_Click(object sender, RoutedEventArgs e) => await CheckForUpdateAsync(silent: false);

    private async Task CheckForUpdateAsync(bool silent)
    {
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "확인 중…";
        try
        {
            var release = await ReleaseUpdateService.GetLatestInstallerAsync();
            if (ReleaseUpdateService.IsNewerThanCurrent(release.Tag))
            {
                UpdateButton.Content = $"{release.Tag} 설치";
                UpdateButton.Tag = release;
                UpdateButton.Click -= Update_Click;
                UpdateButton.Click += InstallKnownUpdate_Click;
                if (!silent)
                {
                    SetOperation($"새 버전 {release.Tag}이 있습니다. 위 버튼으로 바로 설치할 수 있습니다.", success: true);
                }
                return;
            }

            UpdateButton.Content = "최신 버전";
            if (!silent)
            {
                SetOperation($"현재 {ReleaseUpdateService.CurrentVersion}이 최신 버전입니다.", success: true);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            UpdateButton.Content = "업데이트 재시도";
            if (!silent)
            {
                SetOperation($"업데이트 확인 실패: {exception.Message}", success: false);
            }
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    private async void InstallKnownUpdate_Click(object sender, RoutedEventArgs e)
    {
        await DownloadAndLaunchInstallerAsync(UpdateButton.Tag as InstallerRelease);
    }

    private async Task DownloadAndLaunchInstallerAsync(InstallerRelease? knownRelease = null)
    {
        InstallButton.IsEnabled = false;
        UpdateButton.IsEnabled = false;
        try
        {
            var release = knownRelease ?? await ReleaseUpdateService.GetLatestInstallerAsync();
            var progress = new Progress<int>(percent =>
            {
                InstallButton.Content = $"다운로드 {percent}%";
                UpdateButton.Content = $"다운로드 {percent}%";
            });
            SetOperation($"{release.Tag} 설치 프로그램을 안전하게 내려받는 중입니다…", success: true);
            var installer = await ReleaseUpdateService.DownloadInstallerAsync(release, progress);
            SetOperation("Windows 관리자 확인 창에서 '예'를 누르면 설치가 계속됩니다.", success: true);
            var process = ReleaseUpdateService.LaunchInstaller(installer);
            if (process is null)
            {
                throw new InvalidOperationException("설치 프로그램을 시작하지 못했습니다.");
            }
            Close();
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            SetOperation("관리자 확인이 취소되어 설치하지 않았습니다.", success: false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            SetOperation($"설치 프로그램을 시작하지 못했습니다: {exception.Message}", success: false);
        }
        finally
        {
            InstallButton.Content = "설치 프로그램 받기";
            InstallButton.IsEnabled = true;
            UpdateButton.IsEnabled = true;
        }
    }

    private async void StoreCredential_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(PasswordInput.Password))
        {
            SetOperation("PIN이 아닌 현재 Windows 계정 암호를 입력하세요.", success: false);
            PasswordInput.Focus();
            return;
        }

        await RunOperationAsync(async () =>
        {
            var response = await client.SendAsync(
                new SetupRequest(SetupCommands.StoreCredential, currentQualifiedUsername, PasswordInput.Password),
                TimeSpan.FromSeconds(20));
            PasswordInput.Clear();
            SetOperation(response.Success
                ? "현재 Windows 계정의 암호를 확인하고 안전하게 저장했습니다."
                : ExplainCredentialError(response.Message), response.Success);
            await RefreshStatusAsync();
        });
    }

    private async void CreatePairing_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(async () =>
        {
            var response = await client.SendAsync(
                new SetupRequest(SetupCommands.CreatePairing),
                TimeSpan.FromSeconds(5));
            if (!response.Success || string.IsNullOrWhiteSpace(response.Data))
            {
                PairingPanel.Visibility = Visibility.Collapsed;
                SetOperation($"연결 QR을 만들지 못했습니다: {response.Message}", success: false);
                return;
            }

            PairingJsonBox.Text = response.Data;
            PairingQrImage.Source = CreateQrImage(response.Data);
            PairingPanel.Visibility = Visibility.Visible;

            using var document = JsonDocument.Parse(response.Data);
            var expiresAt = document.RootElement.GetProperty("expiresAt").GetInt64();
            var localExpiry = DateTimeOffset.FromUnixTimeSeconds(expiresAt).ToLocalTime();
            PairingExpiryText.Text = $"{localExpiry:HH:mm:ss}까지 유효합니다.";
            SetOperation("QR이 준비됐습니다. 휴대폰 Phone Unlock 앱에서 스캔하세요.", success: true);
        });
    }

    private void CopyPairing_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PairingJsonBox.Text))
        {
            SetOperation("먼저 연결 QR 코드를 만드세요.", success: false);
            return;
        }

        Clipboard.SetText(PairingJsonBox.Text);
        SetOperation("연결 코드를 복사했습니다. 휴대폰 앱의 '코드 붙여넣기'를 누르세요.", success: true);
    }

    private async void TestAndEnable_Click(object sender, RoutedEventArgs e)
    {
        if (currentStatus is null)
        {
            SetOperation("먼저 서비스를 설치하고 상태를 새로 고침하세요.", success: false);
            return;
        }
        if (!currentStatus.CredentialConfigured)
        {
            SetOperation("먼저 현재 Windows 계정 암호를 확인해 주세요.", success: false);
            PasswordInput.Focus();
            return;
        }
        if (!currentStatus.Phones.Any(phone => phone.Enabled && phone.Connected))
        {
            SetOperation("연결된 휴대폰이 없습니다. 휴대폰 앱을 열고 QR로 연결하세요.", success: false);
            return;
        }

        TestResultText.Text = "휴대폰에서 설정한 인증을 완료해 주세요…";
        TestResultText.Foreground = BrushFrom("#A15C00");
        await RunOperationAsync(async () =>
        {
            var response = await client.SendAsync(
                new SetupRequest(SetupCommands.TestAuthentication),
                TimeSpan.FromSeconds(40));
            if (!response.Success)
            {
                TestResultText.Text = $"휴대폰 인증 테스트 실패: {response.Message}";
                TestResultText.Foreground = BrushFrom("#A4262C");
                SetOperation(response.Message, success: false);
                return;
            }

            TestResultText.Text = "✓ 휴대폰 인증 확인 성공";
            TestResultText.Foreground = BrushFrom("#217346");
            var scriptResult = await RunNearbyScriptAsync("Enable-CredentialProvider.ps1");
            if (!scriptResult.Success)
            {
                SetOperation($"지문 테스트는 성공했지만 Windows 로그인을 켜지 못했습니다. {scriptResult.Message}", success: false);
                return;
            }

            SetOperation("설정 완료. 이제 잠금화면이 열리면 휴대폰에 인증 요청이 자동으로 갑니다.", success: true);
            await RefreshStatusAsync();
        });
    }

    private async void DisableLogin_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(async () =>
        {
            var result = await RunNearbyScriptAsync("Disable-CredentialProvider.ps1");
            SetOperation(result.Success
                ? "휴대폰 인증 로그인을 껐습니다. 기존 PIN과 비밀번호 로그인은 그대로입니다."
                : result.Message, result.Success);
            await RefreshStatusAsync();
        });
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            var response = await client.SendAsync(new SetupRequest(SetupCommands.Status), TimeSpan.FromSeconds(4));
            if (!response.Success || string.IsNullOrWhiteSpace(response.Data))
            {
                throw new InvalidOperationException(response.Message);
            }

            currentStatus = ProtocolJson.Deserialize<SetupStatus>(response.Data);
            SetServiceControls(enabled: true);
            InstallRequiredCard.Visibility = Visibility.Collapsed;
            ServiceStatusText.Text = "● 서비스 실행 중";
            ServiceStatusText.Foreground = BrushFrom("#217346");
            ComputerText.Text = currentStatus.ComputerName;
            CredentialStateText.Text = currentStatus.CredentialConfigured
                ? "✓ 현재 계정 암호가 안전하게 저장되어 있습니다."
                : "아직 암호 확인이 필요합니다.";
            PhoneStateText.Text = currentStatus.Phones.Count == 0
                ? "연결된 휴대폰이 없습니다. 위 버튼으로 QR을 만드세요."
                : string.Join(Environment.NewLine, currentStatus.Phones.Select(phone =>
                    $"{(phone.Connected ? "●" : "○")} {phone.PhoneName} · {(phone.Connected ? "연결됨" : "오프라인")}"));

            updatingControls = true;
            PhoneSelectorComboBox.Items.Clear();
            foreach (var phone in currentStatus.Phones)
            {
                PhoneSelectorComboBox.Items.Add(new PhoneSelectionItem(
                    phone.PhoneId,
                    $"{phone.PhoneName} · {(phone.Connected ? "연결됨" : "오프라인")}"));
            }
            PhoneSelectorComboBox.SelectedItem = currentStatus.PreferredPhoneId is null
                ? null
                : PhoneSelectorComboBox.Items.OfType<PhoneSelectionItem>()
                    .FirstOrDefault(item => item.PhoneId == currentStatus.PreferredPhoneId);
            UseSelectedPhoneButton.IsEnabled = PhoneSelectorComboBox.Items.Count > 0;
            ProximityLockCheckBox.IsChecked = currentStatus.ProximityLockEnabled;
            ProximityUnlockCheckBox.IsChecked = currentStatus.ProximityUnlockEnabled;
            ProximityAgentStatusText.Text = currentStatus.InteractiveAgentConnected
                ? "✓ 자동잠금 에이전트 연결됨 · 휴대폰 연결 상태를 감시 중입니다."
                : currentStatus.ProximityLockEnabled
                    ? "○ 자동잠금 에이전트 연결 안 됨 · 버튼을 누르거나 Windows에 다시 로그인하세요."
                    : "✓ 자동 잠금 해제 감시는 서비스에서 실행됩니다. 자동 잠금도 사용하려면 에이전트를 시작하세요.";
            ProximityAgentStatusText.Foreground = BrushFrom(currentStatus.InteractiveAgentConnected ? "#217346" : "#A15C00");
            ProximityGraceComboBox.SelectedItem = ProximityGraceComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), currentStatus.ProximityGraceSeconds.ToString(), StringComparison.Ordinal))
                ?? ProximityGraceComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == "30");
            updatingControls = false;

            var providerEnabled = IsCredentialProviderRegistered();
            var providerDefault = IsDefaultCredentialProvider();
            LoginStateText.Text = providerEnabled
                ? providerDefault ? "잠금화면 기본 로그인: Phone Unlock" : "Phone Unlock 로그인 옵션이 켜져 있습니다."
                : "아직 Windows 잠금화면에 연결되지 않았습니다.";
            EnableLoginButton.Content = providerEnabled ? "휴대폰 인증 다시 테스트" : "휴대폰 인증 로그인 켜기";
            DisableLoginButton.Visibility = providerEnabled ? Visibility.Visible : Visibility.Collapsed;

            var usable = providerEnabled
                && currentStatus.CredentialConfigured
                && currentStatus.Phones.Any(phone => phone.Enabled);
            ReadyBadgeText.Text = usable ? "사용 가능" : "설정 필요";
            ReadyBadge.Background = BrushFrom(usable ? "#EAF7EF" : "#ECEEF2");
            await RefreshAuditAsync(silent: true);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or JsonException or InvalidOperationException)
        {
            currentStatus = null;
            SetServiceControls(enabled: false);
            InstallRequiredCard.Visibility = Visibility.Visible;
            InstallButton.IsEnabled = true;
            ServiceStatusText.Text = "● 설치되지 않음";
            ServiceStatusText.Foreground = BrushFrom("#A4262C");
            ComputerText.Text = "설정 앱만 실행된 상태입니다.";
            CredentialStateText.Text = "서비스 설치 후 자동으로 현재 계정을 사용합니다.";
            PhoneStateText.Text = "서비스가 없어 연결 QR을 만들 수 없습니다.";
            LoginStateText.Text = "Windows 로그인 연동이 꺼져 있습니다.";
            ReadyBadgeText.Text = "설치 필요";
            ReadyBadge.Background = BrushFrom("#FFF0F0");
            SetOperation("'설치 프로그램 받기'를 누르세요. ZIP이나 PowerShell 작업 없이 복구됩니다.", success: false);
        }
    }

    private void SetServiceControls(bool enabled)
    {
        CreatePairingButton.IsEnabled = enabled;
        StoreCredentialButton.IsEnabled = enabled;
        PasswordInput.IsEnabled = enabled;
        EnableLoginButton.IsEnabled = enabled;
        PhoneSelectorComboBox.IsEnabled = enabled;
        UseSelectedPhoneButton.IsEnabled = enabled && PhoneSelectorComboBox.Items.Count > 0;
        DiagnosticsButton.IsEnabled = enabled;
        AuditList.IsEnabled = enabled;
        ProximityLockCheckBox.IsEnabled = enabled;
        ProximityUnlockCheckBox.IsEnabled = enabled;
        ProximityGraceComboBox.IsEnabled = enabled;
        StartAgentButton.IsEnabled = enabled;
        if (!enabled)
        {
            DisableLoginButton.Visibility = Visibility.Collapsed;
            PairingPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async Task RefreshAuditAsync(bool silent)
    {
        try
        {
            var response = await client.SendAsync(
                new SetupRequest(SetupCommands.GetAuditLog, Limit: 100),
                TimeSpan.FromSeconds(10));
            if (!response.Success || string.IsNullOrWhiteSpace(response.Data))
            {
                if (!silent) SetOperation(response.Message, success: false);
                return;
            }

            var entries = ProtocolJson.Deserialize<AuditEntry[]>(response.Data);
            AuditList.Items.Clear();
            foreach (var entry in entries)
            {
                var prefix = entry.Suspicious ? "⚠ 의심 " : entry.Outcome == "SUCCESS" ? "✓ " : "• ";
                var phone = string.IsNullOrWhiteSpace(entry.PhoneName) ? "알 수 없는 휴대폰" : entry.PhoneName;
                var ip = string.IsNullOrWhiteSpace(entry.RemoteIp) ? "IP 미확인" : entry.RemoteIp;
                AuditList.Items.Add(new ListBoxItem
                {
                    Content = $"{prefix}{entry.OccurredAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {phone} · {ip}\n{entry.Message}",
                    Foreground = BrushFrom(entry.Suspicious ? "#A4262C" : entry.Outcome == "SUCCESS" ? "#217346" : "#555A63"),
                    Padding = new Thickness(8, 5, 8, 5)
                });
            }
            if (!silent) SetOperation("보안 기록을 새로 고쳤습니다.", success: true);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or JsonException or InvalidOperationException)
        {
            if (!silent) SetOperation($"보안 기록을 불러오지 못했습니다: {exception.Message}", success: false);
        }
    }

    private int SelectedGraceSeconds() =>
        int.TryParse((ProximityGraceComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var seconds)
            ? seconds
            : 30;

    private async Task RunOperationAsync(Func<Task> operation)
    {
        IsEnabled = false;
        try
        {
            await operation();
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or JsonException or InvalidOperationException)
        {
            SetOperation(exception.Message, success: false);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private static BitmapImage CreateQrImage(string value)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.M);
        using var qrCode = new PngByteQRCode(data);
        var bytes = qrCode.GetGraphic(7);
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static string ExplainCredentialError(string message) =>
        message.Contains("rejected", StringComparison.OrdinalIgnoreCase)
            ? "Windows가 암호를 확인하지 못했습니다. PIN이 아니라 이 계정의 실제 암호를 입력했는지 확인하세요."
            : message;

    private static string? FindNearbyFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 4 && directory is not null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static async Task<(bool Success, string Message)> RunNearbyScriptAsync(string fileName)
    {
        var script = FindNearbyFile(fileName);
        if (script is null)
        {
            return (false, $"{fileName} 파일을 찾을 수 없습니다.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(script)!
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (false, exception.Message);
        }
        using (process)
        {
            if (process is null)
            {
                return (false, "Windows 설정 스크립트를 시작하지 못했습니다.");
            }
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            return process.ExitCode == 0
                ? (true, output)
                : (false, string.IsNullOrWhiteSpace(error) ? output : error);
        }
    }

    private static bool IsCredentialProviderRegistered()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ProviderRegistryPath);
        return key is not null;
    }

    private static bool IsDefaultCredentialProvider()
    {
        using var key = Registry.LocalMachine.OpenSubKey(LogonPolicyPath);
        return string.Equals(key?.GetValue(DefaultProviderValue)?.ToString(), ProviderGuid, StringComparison.OrdinalIgnoreCase);
    }

    private void SetOperation(string message, bool success)
    {
        OperationStatusText.Text = message;
        OperationStatusText.Foreground = BrushFrom(success ? "#217346" : "#A4262C");
    }

    private static SolidColorBrush BrushFrom(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));
}
