using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using PhoneUnlock.Core.Protocol;
using PhoneUnlock.Setup.Models;

namespace PhoneUnlock.Setup;

public partial class MainWindow : Window
{
    private readonly SetupPipeClient client = new();

    public MainWindow()
    {
        InitializeComponent();
        UsernameBox.Text = $"{Environment.UserDomainName}\\{Environment.UserName}";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshStatusAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshStatusAsync();

    private async void StoreCredential_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UsernameBox.Text) || string.IsNullOrEmpty(PasswordInput.Password))
        {
            SetOperation("계정과 Windows 비밀번호를 모두 입력하세요.", success: false);
            return;
        }

        await RunOperationAsync(async () =>
        {
            var response = await client.SendAsync(
                new SetupRequest(SetupCommands.StoreCredential, UsernameBox.Text.Trim(), PasswordInput.Password),
                TimeSpan.FromSeconds(20));
            PasswordInput.Clear();
            SetOperation(response.Message, response.Success);
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
                SetOperation(response.Message, success: false);
                return;
            }

            PairingJsonBox.Text = response.Data;
            SetOperation("2분 안에 Android 앱에 페어링 정보를 붙여 넣으세요.", success: true);
        });
    }

    private void CopyPairing_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PairingJsonBox.Text))
        {
            SetOperation("먼저 페어링 정보를 생성하세요.", success: false);
            return;
        }

        Clipboard.SetText(PairingJsonBox.Text);
        SetOperation("페어링 정보를 복사했습니다.", success: true);
    }

    private async void TestAuthentication_Click(object sender, RoutedEventArgs e)
    {
        TestResultText.Text = "휴대폰 응답을 기다리는 중…";
        await RunOperationAsync(async () =>
        {
            var response = await client.SendAsync(
                new SetupRequest(SetupCommands.TestAuthentication),
                TimeSpan.FromSeconds(40));
            TestResultText.Text = response.Success ? "✓ 휴대폰 생체인증 테스트 성공" : $"실패: {response.Message}";
            TestResultText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(response.Success ? "#217346" : "#A4262C"));
            SetOperation(response.Message, response.Success);
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

            var status = ProtocolJson.Deserialize<SetupStatus>(response.Data);
            ServiceStatusText.Text = "● 실행 중";
            ServiceStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#217346"));
            ComputerText.Text = $"{status.ComputerName} · {status.ComputerId:D}";
            CredentialStateText.Text = status.CredentialConfigured
                ? $"저장됨: {status.ConfiguredQualifiedUsername}"
                : "Windows 자격 증명이 아직 없습니다.";
            PhoneStateText.Text = status.Phones.Count == 0
                ? "연결된 휴대폰이 없습니다."
                : string.Join(Environment.NewLine, status.Phones.Select(phone =>
                    $"{phone.PhoneName} · {(phone.Connected ? "연결됨" : "오프라인")}"));
            ReadyBadgeText.Text = status.ReadyToEnableCredentialProvider ? "활성화 준비 완료" : "준비 안 됨";
            ReadyBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                status.ReadyToEnableCredentialProvider ? "#EAF7EF" : "#ECEEF2"));
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or JsonException or InvalidOperationException)
        {
            ServiceStatusText.Text = "● 서비스 오프라인";
            ServiceStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A4262C"));
            ComputerText.Text = "PhoneUnlockService를 먼저 설치하고 시작하세요.";
            SetOperation(exception.Message, success: false);
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

    private void SetOperation(string message, bool success)
    {
        OperationStatusText.Text = message;
        OperationStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(success ? "#217346" : "#A4262C"));
    }
}
