using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
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

    public MainWindow()
    {
        InitializeComponent();
        currentQualifiedUsername = WindowsIdentity.GetCurrent().Name
            ?? $"{Environment.UserDomainName}\\{Environment.UserName}";
        DetectedAccountText.Text = currentQualifiedUsername;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshStatusAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshStatusAsync();

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        var script = FindNearbyFile("Install-PhoneUnlock.ps1");
        if (script is null)
        {
            SetOperation("설치 파일을 찾을 수 없습니다. 릴리스 ZIP 전체를 푼 뒤 'Phone Unlock 설치.cmd'를 실행하세요.", success: false);
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(script)!
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(script);
            Process.Start(startInfo);
            Close();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SetOperation($"설치를 시작하지 못했습니다: {exception.Message}", success: false);
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

        TestResultText.Text = "휴대폰에서 지문을 인증해 주세요…";
        TestResultText.Foreground = BrushFrom("#A15C00");
        await RunOperationAsync(async () =>
        {
            var response = await client.SendAsync(
                new SetupRequest(SetupCommands.TestAuthentication),
                TimeSpan.FromSeconds(40));
            if (!response.Success)
            {
                TestResultText.Text = $"지문 테스트 실패: {response.Message}";
                TestResultText.Foreground = BrushFrom("#A4262C");
                SetOperation(response.Message, success: false);
                return;
            }

            TestResultText.Text = "✓ 휴대폰 지문 확인 성공";
            TestResultText.Foreground = BrushFrom("#217346");
            var scriptResult = await RunNearbyScriptAsync("Enable-CredentialProvider.ps1");
            if (!scriptResult.Success)
            {
                SetOperation($"지문 테스트는 성공했지만 Windows 로그인을 켜지 못했습니다. {scriptResult.Message}", success: false);
                return;
            }

            SetOperation("설정 완료. 이제 잠금화면이 열리면 휴대폰에 지문 요청이 자동으로 갑니다.", success: true);
            await RefreshStatusAsync();
        });
    }

    private async void DisableLogin_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(async () =>
        {
            var result = await RunNearbyScriptAsync("Disable-CredentialProvider.ps1");
            SetOperation(result.Success
                ? "지문 로그인을 껐습니다. 기존 PIN과 비밀번호 로그인은 그대로입니다."
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

            var providerEnabled = IsCredentialProviderRegistered();
            var providerDefault = IsDefaultCredentialProvider();
            LoginStateText.Text = providerEnabled
                ? providerDefault ? "잠금화면 기본 로그인: Phone Unlock" : "Phone Unlock 로그인 옵션이 켜져 있습니다."
                : "아직 Windows 잠금화면에 연결되지 않았습니다.";
            EnableLoginButton.Content = providerEnabled ? "지문 로그인 다시 테스트" : "지문 로그인 켜기";
            DisableLoginButton.Visibility = providerEnabled ? Visibility.Visible : Visibility.Collapsed;

            var usable = providerEnabled
                && currentStatus.CredentialConfigured
                && currentStatus.Phones.Any(phone => phone.Enabled);
            ReadyBadgeText.Text = usable ? "사용 가능" : "설정 필요";
            ReadyBadge.Background = BrushFrom(usable ? "#EAF7EF" : "#ECEEF2");
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
            SetOperation("먼저 '이 PC에 설치'를 누르세요. 설치 후 설정 앱이 다시 열립니다.", success: false);
        }
    }

    private void SetServiceControls(bool enabled)
    {
        CreatePairingButton.IsEnabled = enabled;
        StoreCredentialButton.IsEnabled = enabled;
        PasswordInput.IsEnabled = enabled;
        EnableLoginButton.IsEnabled = enabled;
        if (!enabled)
        {
            DisableLoginButton.Visibility = Visibility.Collapsed;
            PairingPanel.Visibility = Visibility.Collapsed;
        }
    }

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
