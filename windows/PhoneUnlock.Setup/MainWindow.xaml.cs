using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private bool updatingAppearance;

    public MainWindow()
    {
        var appearance = SetupAppearance.Load();
        SetupAppearance.Apply(Application.Current, appearance);
        InitializeComponent();
        updatingAppearance = true;
        ThemeModeComboBox.SelectedItem = ThemeModeComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), appearance, StringComparison.Ordinal));
        updatingAppearance = false;
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

    private void SidebarNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        var destination = button.Tag?.ToString();
        if (destination == "network")
        {
            RemoteConnection_Click(sender, e);
            return;
        }
        if (destination == "update")
        {
            Update_Click(sender, e);
            return;
        }
        if (destination is "home" or "devices")
        {
            ShowHomePanel();
            if (destination == "devices")
            {
                Dispatcher.BeginInvoke(new Action(HomePhoneList.BringIntoView));
            }
            return;
        }

        var phone = currentStatus?.Phones
            .OrderByDescending(candidate => candidate.PhoneId == currentStatus.PreferredPhoneId)
            .FirstOrDefault();
        if (phone is null)
        {
            ShowHomePanel();
            SetOperation("먼저 휴대폰을 연결하세요.", success: false);
            return;
        }

        SettingsHeaderText.Text = phone.PhoneName;
        PhoneSelectorComboBox.SelectedItem = PhoneSelectorComboBox.Items
            .OfType<PhoneSelectionItem>()
            .FirstOrDefault(candidate => candidate.PhoneId == phone.PhoneId);
        ShowSettingsPanel();
        FrameworkElement target = destination switch
        {
            "login" => LoginSection,
            "automation" => AutomationSection,
            "remote" => RemoteControlSection,
            "security" => SecuritySection,
            "activity" => AuditSection,
            "diagnostics" => DiagnosticsSection,
            _ => SettingsPanel,
        };
        Dispatcher.BeginInvoke(new Action(target.BringIntoView));
    }

    private void ThemeMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingAppearance || ThemeModeComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var mode = item.Tag?.ToString() ?? SetupAppearance.System;
        SetupAppearance.Apply(Application.Current, mode);
        SetupAppearance.Save(mode);
        SetOperation("화면 테마를 적용했습니다.", success: true);
        AnimatePanel(SettingsPanel.Visibility == Visibility.Visible ? SettingsPanel : HomePanel);
    }

    private void RemoteConnection_Click(object sender, RoutedEventArgs e)
    {
        var executable = FindTailscaleExecutable();
        if (executable is null)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://tailscale.com/download/windows") { UseShellExecute = true });
                SetOperation("Tailscale 설치 페이지를 열었습니다. 설치 후 이 버튼을 다시 누르면 원격 연결을 준비합니다.", success: true);
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                SetOperation($"원격 연결 페이지를 열지 못했습니다: {exception.Message}", success: false);
            }
            return;
        }

        try
        {
            var guiPath = Path.Combine(Path.GetDirectoryName(executable)!, "tailscale-ipn.exe");
            if (File.Exists(guiPath))
            {
                Process.Start(new ProcessStartInfo(guiPath) { UseShellExecute = true });
            }

            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            startInfo.ArgumentList.Add("up");
            _ = Process.Start(startInfo);
            SetOperation("Tailscale 원격 연결을 시작했습니다. 처음 한 번만 로그인과 Windows/VPN 확인을 완료하면 이후 주소를 자동 갱신합니다.", success: true);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetOperation($"Tailscale 원격 연결을 시작하지 못했습니다: {exception.Message}", success: false);
        }
    }

    private void HomePhoneList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingControls || HomePhoneList.SelectedItem is not ListBoxItem item
            || item.Tag is not string phoneId || currentStatus is null)
        {
            return;
        }

        var phone = currentStatus.Phones.FirstOrDefault(candidate => candidate.PhoneId == phoneId);
        if (phone is null) return;
        PhoneSelectorComboBox.SelectedItem = PhoneSelectorComboBox.Items
            .OfType<PhoneSelectionItem>()
            .FirstOrDefault(candidate => candidate.PhoneId == phone.PhoneId);
        SettingsHeaderText.Text = phone.PhoneName;
        ShowSettingsPanel();
    }

    private void BackToHome_Click(object sender, RoutedEventArgs e)
    {
        ShowHomePanel();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var isBack = e.Key == Key.Escape
            || (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt);
        if (!isBack || SettingsPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        ShowHomePanel();
        e.Handled = true;
    }

    private void ShowHomePanel()
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        HomePanel.Visibility = Visibility.Visible;
        AnimatePanel(HomePanel);
        if (currentStatus is not null)
        {
            updatingControls = true;
            HomePhoneList.SelectedItem = null;
            updatingControls = false;
        }
    }

    private void ShowSettingsPanel()
    {
        HomePanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Visible;
        AnimatePanel(SettingsPanel);
    }

    private static void AnimatePanel(UIElement panel)
    {
        panel.Opacity = 0;
        var transforms = new TransformGroup();
        var scale = new ScaleTransform(0.985, 0.985);
        var translate = new TranslateTransform(0, 14);
        transforms.Children.Add(scale);
        transforms.Children.Add(translate);
        panel.RenderTransform = transforms;
        panel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
        translate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation
            {
                From = 14,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        scale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        scale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
    }

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
            var vpnAddresses = diagnostics.LocalAddresses
                .Where(address => address.StartsWith("100.", StringComparison.Ordinal))
                .ToArray();
            var route = vpnAddresses.Length > 0
                ? $"원격 자동 연결 준비됨 · VPN 주소 {vpnAddresses.Length}개"
                : "LAN 주소 준비됨 · 원격 연결을 한 번 켜면 자동 갱신";
            var wake = diagnostics.WakeOnLanTargets.Count > 0
                ? $"WOL {diagnostics.WakeOnLanTargets.Count}개"
                : "WOL 대상 없음";
            DiagnosticsSummaryText.Text = connected == 0
                ? $"PC 주소 {string.Join(", ", diagnostics.LocalAddresses)} · 연결된 휴대폰 없음 · {route} · {wake} · 자동잠금 에이전트 {(diagnostics.InteractiveAgentConnected ? "연결됨" : "없음")}"
                : $"PC 주소 {string.Join(", ", diagnostics.LocalAddresses)} · 휴대폰 {connected}대 연결됨 · 포트 {diagnostics.ListeningPort} · {route} · {wake} · 자동잠금 에이전트 {(diagnostics.InteractiveAgentConnected ? "연결됨" : "없음")}";
            SetOperation("Windows 연결 진단을 완료했습니다. 알림·배터리 상태는 휴대폰 앱 진단에서 확인하세요.", success: true);
        });
    }

    private async void AuditRefresh_Click(object sender, RoutedEventArgs e) => await RefreshAuditAsync(silent: false);

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Phone Unlock 진단 저장",
            Filter = "JSON 파일|*.json|모든 파일|*.*",
            FileName = $"PhoneUnlock-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            AddExtension = true,
            DefaultExt = ".json",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

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
            var safeExport = new
            {
                exportedAt = DateTimeOffset.UtcNow,
                diagnostics.ServiceVersion,
                diagnostics.ListeningPort,
                diagnostics.LocalAddresses,
                diagnostics.CertificateFingerprint,
                diagnostics.Phones,
                diagnostics.RecentAudit,
                diagnostics.ProximityLockEnabled,
                diagnostics.ProximityUnlockEnabled,
                diagnostics.ProximityGraceSeconds,
                diagnostics.AutoLockProfile,
                diagnostics.BluetoothRssiEnabled,
                diagnostics.BluetoothRssiThreshold,
                diagnostics.RemoteUnlockEnabled,
                diagnostics.PresenceSensorEnabled,
                diagnostics.PresenceSensorProtocol,
                diagnostics.PresenceSensorBaseUrl,
                diagnostics.PresenceSensorEntityId,
                diagnostics.InteractiveAgentConnected,
                diagnostics.WakeOnLanTargets
            };
            await File.WriteAllTextAsync(
                dialog.FileName,
                ProtocolJson.Serialize(safeExport),
                Encoding.UTF8);
            SetOperation($"민감한 토큰·비밀번호를 제외한 진단 정보를 저장했습니다: {dialog.FileName}", success: true);
        });
    }

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

    private async void SmartArrival_Click(object sender, RoutedEventArgs e)
    {
        if (updatingControls) return;
        var enabled = SmartArrivalCheckBox.IsChecked == true;
        await RunOperationAsync(async () =>
        {
            var response = await client.SendAsync(
                new SetupRequest(SetupCommands.SetSmartArrival, Enabled: enabled),
                TimeSpan.FromSeconds(10));
            SetOperation(response.Message, response.Success);
            if (response.Success) await RefreshStatusAsync();
        });
    }

    private async void RemoteUnlock_Click(object sender, RoutedEventArgs e)
    {
        if (updatingControls) return;
        var enabled = RemoteUnlockCheckBox.IsChecked == true;
        await RunOperationAsync(async () =>
        {
            var response = await client.SendAsync(
                new SetupRequest(SetupCommands.SetRemoteUnlock, Enabled: enabled),
                TimeSpan.FromSeconds(10));
            SetOperation(response.Message, response.Success);
            if (response.Success) await RefreshStatusAsync();
        });
    }

    private async void RemotePower_Click(object sender, RoutedEventArgs e)
    {
        if (updatingControls) return;
        var enabled = RemotePowerCheckBox.IsChecked == true;
        await RunOperationAsync(async () =>
        {
            var response = await client.SendAsync(
                new SetupRequest(SetupCommands.SetRemotePower, Enabled: enabled),
                TimeSpan.FromSeconds(10));
            SetOperation(response.Message, response.Success);
            if (response.Success) await RefreshStatusAsync();
        });
    }

    private async void PauseHour_Click(object sender, RoutedEventArgs e) => await SetPauseAsync(60);

    private async void PauseToday_Click(object sender, RoutedEventArgs e) => await SetPauseAsync(1_440);

    private async void ResumeAutomation_Click(object sender, RoutedEventArgs e) => await SetPauseAsync(0);

    private async Task SetPauseAsync(int minutes)
    {
        await RunOperationAsync(async () =>
        {
            var response = await client.SendAsync(
                new SetupRequest(SetupCommands.SetPause, Enabled: minutes > 0, PauseMinutes: minutes > 0 ? minutes : null),
                TimeSpan.FromSeconds(10));
            SetOperation(response.Message, response.Success);
            if (response.Success) await RefreshStatusAsync();
        });
    }

    private async void RevokePhone_Click(object sender, RoutedEventArgs e)
    {
        if (PhoneSelectorComboBox.SelectedItem is not PhoneSelectionItem phone)
        {
            SetOperation("차단할 휴대폰을 먼저 선택하세요.", success: false);
            return;
        }

        if (MessageBox.Show(
                $"{phone.DisplayName}을(를) 즉시 차단할까요? 다시 사용하려면 새로 페어링해야 합니다.",
                "휴대폰 차단",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunOperationAsync(async () =>
        {
            var response = await client.SendAsync(
                new SetupRequest(SetupCommands.RevokePhone, PhoneId: phone.PhoneId),
                TimeSpan.FromSeconds(10));
            SetOperation(response.Message, response.Success);
            if (response.Success) await RefreshStatusAsync();
        });
    }

    private async void RevokeAllPhones_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "등록된 모든 휴대폰의 로그인을 즉시 차단할까요? Windows 기본 로그인은 유지됩니다.",
                "모든 휴대폰 차단",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunOperationAsync(async () =>
        {
            var response = await client.SendAsync(
                new SetupRequest(SetupCommands.RevokeAllPhones),
                TimeSpan.FromSeconds(10));
            SetOperation(response.Message, response.Success);
            if (response.Success) await RefreshStatusAsync();
        });
    }

    private async void SecurityCheckup_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(async () =>
        {
            SecurityCheckupButton.IsEnabled = false;
            var response = await client.SendAsync(
                new SetupRequest(SetupCommands.SecurityCheckup),
                TimeSpan.FromSeconds(10));
            if (!response.Success || string.IsNullOrWhiteSpace(response.Data))
            {
                SetOperation(response.Message, success: false);
                return;
            }

            var checks = ProtocolJson.Deserialize<SecurityCheckItem[]>(response.Data);
            SecurityCheckupList.Items.Clear();
            foreach (var check in checks)
            {
                SecurityCheckupList.Items.Add(new ListBoxItem
                {
                    Content = $"{(check.Passed ? "✓" : "!")} {check.Title} · {check.Detail}",
                    Foreground = BrushFrom(check.Passed ? "#8FE0B0" : "#FFD18A"),
                    Padding = new Thickness(8, 4, 8, 4)
                });
            }

            var warnings = checks.Count(check => !check.Passed);
            SecurityCheckupSummaryText.Text = warnings == 0
                ? "✓ 보안 상태가 정상입니다."
                : $"! 주의할 항목 {warnings}개가 있습니다.";
            SetOperation(response.Message, warnings == 0);
        });
        SecurityCheckupButton.IsEnabled = currentStatus is not null;
    }

    private async void AutoLockProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingControls || !IsLoaded || currentStatus is null) return;
        await SaveAutoLockProfileAsync();
    }

    private async void BluetoothRssi_Click(object sender, RoutedEventArgs e)
    {
        if (updatingControls) return;
        await SaveBluetoothRssiAsync();
    }

    private async void BluetoothRssiThreshold_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingControls || !IsLoaded || currentStatus is null || BluetoothRssiCheckBox.IsChecked != true) return;
        await SaveBluetoothRssiAsync();
    }

    private async void PresenceSensor_Click(object sender, RoutedEventArgs e)
    {
        if (updatingControls) return;
        await SavePresenceSensorAsync();
    }

    private async void TestPresenceSensor_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(async () =>
        {
            TestPresenceSensorButton.IsEnabled = false;
            var response = await client.SendAsync(
                new SetupRequest(SetupCommands.TestPresenceSensor),
                TimeSpan.FromSeconds(10));
            PresenceSensorStateText.Text = response.Message;
            PresenceSensorStateText.Foreground = BrushFrom(response.Success ? "#8FE0B0" : "#FFB4AB");
            SetOperation(response.Message, response.Success);
        });
        TestPresenceSensorButton.IsEnabled = currentStatus is not null;
    }

    private async void PresenceSensorProtocol_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingControls || !IsLoaded) return;
        ApplyPresenceSensorProtocolUi(updateDefaults: true);
        if (currentStatus is not null
            && PresenceSensorCheckBox.IsChecked == true
            && !string.Equals(SelectedPresenceSensorProtocol(), "smartthings", StringComparison.OrdinalIgnoreCase))
        {
            await SavePresenceSensorAsync();
        }
    }

    private async void SmartThingsQuickConnect_Click(object sender, RoutedEventArgs e)
    {
        await LoadSmartThingsSensorsAsync(autoSaveWhenSingle: true);
    }

    private async void SmartThingsUseSensor_Click(object sender, RoutedEventArgs e)
    {
        if (SmartThingsSensorComboBox.SelectedItem is not SmartThingsSensorOption sensor)
        {
            SetOperation("먼저 SmartThings 센서를 선택하세요.", success: false);
            return;
        }

        ApplySmartThingsSensor(sensor);
        PresenceSensorCheckBox.IsChecked = true;
        await SavePresenceSensorAsync();
    }

    private async void FindSmartThingsSensors_Click(object sender, RoutedEventArgs e)
    {
        await LoadSmartThingsSensorsAsync(autoSaveWhenSingle: false);
    }

    private void SmartThingsSensor_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingControls || SmartThingsSensorComboBox.SelectedItem is not SmartThingsSensorOption sensor)
        {
            return;
        }

        ApplySmartThingsSensor(sensor);
        SmartThingsUseSensorButton.Visibility = Visibility.Visible;
    }

    private async Task LoadSmartThingsSensorsAsync(bool autoSaveWhenSingle)
    {
        if (!string.Equals(SelectedPresenceSensorProtocol(), "smartthings", StringComparison.OrdinalIgnoreCase))
        {
            SetOperation("연결 방식에서 SmartThings Station을 먼저 선택하세요.", success: false);
            return;
        }

        ApplyPresenceSensorProtocolUi(updateDefaults: true);
        SmartThingsQuickStatusText.Text = "SmartThings에서 센서를 찾는 중…";
        await RunOperationAsync(async () =>
        {
            var response = await client.SendAsync(
                new SetupRequest(
                    SetupCommands.ListSmartThingsSensors,
                    Url: PresenceSensorUrlInput.Text,
                    Token: PresenceSensorTokenInput.Password,
                    SensorProtocol: "smartthings"),
                TimeSpan.FromSeconds(15));
            if (!response.Success || string.IsNullOrWhiteSpace(response.Data))
            {
                SmartThingsQuickStatusText.Text = response.Message;
                if (response.Message.Contains("토큰", StringComparison.OrdinalIgnoreCase))
                {
                    PresenceConnectionExpander.IsExpanded = true;
                    PresenceSensorTokenInput.Focus();
                    SetOperation("SmartThings 토큰을 처음 한 번만 입력한 뒤 다시 누르세요.", success: false);
                }
                else
                {
                    SetOperation(response.Message, success: false);
                }
                return;
            }

            var sensors = ProtocolJson.Deserialize<SmartThingsSensorOption[]>(response.Data);
            updatingControls = true;
            SmartThingsSensorComboBox.Items.Clear();
            foreach (var sensor in sensors)
            {
                SmartThingsSensorComboBox.Items.Add(sensor);
            }
            SmartThingsSensorComboBox.Visibility = sensors.Length == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
            SmartThingsUseSensorButton.Visibility = sensors.Length == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
            if (sensors.Length == 1)
            {
                SmartThingsSensorComboBox.SelectedIndex = 0;
            }
            updatingControls = false;

            if (sensors.Length == 0)
            {
                SmartThingsQuickStatusText.Text = "재실·동작 센서를 찾지 못했습니다.";
                SetOperation("SmartThings에서 occupancy·presence·motion 센서를 찾지 못했습니다.", success: false);
                return;
            }

            if (sensors.Length == 1 && autoSaveWhenSingle)
            {
                ApplySmartThingsSensor(sensors[0]);
                PresenceSensorCheckBox.IsChecked = true;
                await SavePresenceSensorAsync();
                SmartThingsQuickStatusText.Text = $"{sensors[0].Label} 연결됨";
                return;
            }

            SmartThingsQuickStatusText.Text = $"센서 {sensors.Length}개를 찾았습니다. 사용할 센서만 선택하세요.";
            SetOperation(response.Message, success: true);
        });
    }

    private void ApplySmartThingsSensor(SmartThingsSensorOption sensor)
    {
        updatingControls = true;
        PresenceSensorEntityInput.Text = sensor.DeviceId;
        PresenceSensorComponentInput.Text = sensor.ComponentId;
        PresenceSensorCapabilityInput.Text = sensor.CapabilityId;
        PresenceSensorAttributeInput.Text = sensor.AttributeName;
        updatingControls = false;
    }

    private async void PresenceSensorGrace_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingControls || !IsLoaded || currentStatus is null || PresenceSensorCheckBox.IsChecked != true) return;
        await SavePresenceSensorAsync();
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

    private async Task SaveAutoLockProfileAsync()
    {
        var profile = SelectedAutoLockProfile();
        await RunOperationAsync(async () =>
        {
            var response = await client.SendAsync(
                new SetupRequest(SetupCommands.SetAutoLockProfile, Profile: profile),
                TimeSpan.FromSeconds(10));
            SetOperation(response.Message, response.Success);
            if (response.Success) await RefreshStatusAsync();
        });
    }

    private async Task SaveBluetoothRssiAsync()
    {
        var enabled = BluetoothRssiCheckBox.IsChecked == true;
        var threshold = SelectedBluetoothRssiThreshold();
        await RunOperationAsync(async () =>
        {
            var response = await client.SendAsync(
                new SetupRequest(
                    SetupCommands.SetBluetoothRssi,
                    Enabled: enabled,
                    RssiThreshold: threshold),
                TimeSpan.FromSeconds(10));
            SetOperation(response.Message, response.Success);
            if (response.Success) await RefreshStatusAsync();
        });
    }

    private async Task SavePresenceSensorAsync()
    {
        var enabled = PresenceSensorCheckBox.IsChecked == true;
        var grace = SelectedPresenceGraceSeconds();
        var protocol = SelectedPresenceSensorProtocol();
        await RunOperationAsync(async () =>
        {
            var response = await client.SendAsync(
                new SetupRequest(
                    SetupCommands.SetPresenceSensor,
                    Enabled: enabled,
                    GraceSeconds: grace,
                    Url: PresenceSensorUrlInput.Text,
                    EntityId: PresenceSensorEntityInput.Text,
                    Token: PresenceSensorTokenInput.Password,
                    SensorProtocol: protocol,
                    ComponentId: PresenceSensorComponentInput.Text,
                    CapabilityId: PresenceSensorCapabilityInput.Text,
                    AttributeName: PresenceSensorAttributeInput.Text),
                TimeSpan.FromSeconds(15));
            PresenceSensorTokenInput.Clear();
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
            CredentialExpander.IsExpanded = true;
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
            CredentialExpander.IsExpanded = true;
            PasswordInput.Focus();
            return;
        }
        if (!currentStatus.Phones.Any(phone => phone.Enabled && phone.Connected))
        {
            SetOperation("연결된 휴대폰이 없습니다. 휴대폰 앱을 열고 QR로 연결하세요.", success: false);
            return;
        }

        TestResultText.Text = "휴대폰에서 설정한 인증을 완료해 주세요…";
        TestResultText.Foreground = BrushFrom("#FFD18A");
        await RunOperationAsync(async () =>
        {
            var response = await client.SendAsync(
                new SetupRequest(SetupCommands.TestAuthentication),
                TimeSpan.FromSeconds(40));
            if (!response.Success)
            {
                TestResultText.Text = $"휴대폰 인증 테스트 실패: {response.Message}";
                TestResultText.Foreground = BrushFrom("#FFB4AB");
                SetOperation(response.Message, success: false);
                return;
            }

            TestResultText.Text = "✓ 휴대폰 인증 확인 성공";
            TestResultText.Foreground = BrushFrom("#8FE0B0");
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
            ServiceStatusText.Foreground = BrushFrom("#8FE0B0");
            SidebarServiceStatusText.Text = "● 실행 중";
            SidebarServiceStatusText.Foreground = BrushFrom("#8FE0B0");
            ComputerText.Text = currentStatus.ComputerName;
            CredentialStateText.Text = currentStatus.CredentialConfigured
                ? "✓ 현재 계정 암호가 안전하게 저장되어 있습니다."
                : "아직 암호 확인이 필요합니다.";
            PhoneStateText.Text = currentStatus.Phones.Count == 0
                ? "연결된 휴대폰이 없습니다. 위 버튼으로 QR을 만드세요."
                : string.Join(Environment.NewLine, currentStatus.Phones.Select(phone =>
                    $"{(phone.Connected ? "●" : "○")} {phone.PhoneName} · {(phone.Connected ? "연결됨" : "오프라인")}"));

            updatingControls = true;
            HomePhoneList.Items.Clear();
            foreach (var phone in currentStatus.Phones)
            {
                HomePhoneList.Items.Add(new ListBoxItem
                {
                    Tag = phone.PhoneId,
                    Content = $"{(phone.Connected ? "●" : "○")}  {phone.PhoneName}    {(phone.Connected ? "연결됨" : "오프라인")}",
                    Padding = new Thickness(10, 8, 10, 8),
                    Foreground = BrushFrom(phone.Connected ? "#8FE0B0" : "#9999A2")
                });
            }
            PhoneSelectorComboBox.Items.Clear();
            foreach (var phone in currentStatus.Phones)
            {
                PhoneSelectorComboBox.Items.Add(new PhoneSelectionItem(
                    phone.PhoneId,
                    $"{phone.PhoneName} · {(phone.Connected ? "연결됨" : "오프라인")}"));
            }
            RevokePhoneButton.IsEnabled = PhoneSelectorComboBox.Items.Count > 0;
            PhoneSelectorComboBox.SelectedItem = currentStatus.PreferredPhoneId is null
                ? null
                : PhoneSelectorComboBox.Items.OfType<PhoneSelectionItem>()
                    .FirstOrDefault(item => item.PhoneId == currentStatus.PreferredPhoneId);
            UseSelectedPhoneButton.IsEnabled = PhoneSelectorComboBox.Items.Count > 0;
            RemoteUnlockCheckBox.IsChecked = currentStatus.RemoteUnlockEnabled;
            RemotePowerCheckBox.IsChecked = currentStatus.RemotePowerEnabled;
            PauseStateText.Text = currentStatus.PauseIndefinitely
                ? "자동 기능 일시 중지 중 · 다시 켜기를 누르세요."
                : currentStatus.PauseUntil is { } pauseUntil && pauseUntil > DateTimeOffset.UtcNow
                    ? $"자동 기능 일시 중지 중 · {pauseUntil.ToLocalTime():MM-dd HH:mm}까지"
                    : "자동 기능 일시 중지 안 함";
            AutoLockProfileComboBox.SelectedItem = AutoLockProfileComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), currentStatus.AutoLockProfile, StringComparison.OrdinalIgnoreCase))
                ?? AutoLockProfileComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == "standard");
            ProximityLockCheckBox.IsChecked = currentStatus.ProximityLockEnabled;
            ProximityUnlockCheckBox.IsChecked = currentStatus.ProximityUnlockEnabled;
            SmartArrivalCheckBox.IsChecked = currentStatus.SmartArrivalEnabled;
            BluetoothRssiCheckBox.IsChecked = currentStatus.BluetoothRssiEnabled;
            BluetoothRssiThresholdComboBox.SelectedItem = BluetoothRssiThresholdComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), currentStatus.BluetoothRssiThreshold.ToString(), StringComparison.Ordinal))
                ?? BluetoothRssiThresholdComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == "-75");
            ProximityAgentStatusText.Text = currentStatus.InteractiveAgentConnected
                ? "✓ 자동잠금 에이전트 연결됨 · 휴대폰 연결 상태를 감시 중입니다."
                : currentStatus.ProximityLockEnabled
                    ? "○ 자동잠금 에이전트 연결 안 됨 · 버튼을 누르거나 Windows에 다시 로그인하세요."
                    : "✓ 자동 잠금 해제 감시는 서비스에서 실행됩니다. 자동 잠금도 사용하려면 에이전트를 시작하세요.";
            ProximityAgentStatusText.Foreground = BrushFrom(currentStatus.InteractiveAgentConnected ? "#8FE0B0" : "#FFD18A");
            ProximityGraceComboBox.SelectedItem = ProximityGraceComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), currentStatus.ProximityGraceSeconds.ToString(), StringComparison.Ordinal))
                ?? ProximityGraceComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == "30");
            PresenceSensorCheckBox.IsChecked = currentStatus.PresenceSensorEnabled;
            var visiblePresenceProtocol = currentStatus.PresenceSensorEnabled
                ? currentStatus.PresenceSensorProtocol
                : "windows";
            PresenceSensorProtocolComboBox.SelectedItem = PresenceSensorProtocolComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), visiblePresenceProtocol, StringComparison.OrdinalIgnoreCase))
                ?? PresenceSensorProtocolComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
            PresenceSensorUrlInput.Text = currentStatus.PresenceSensorBaseUrl ?? string.Empty;
            PresenceSensorEntityInput.Text = currentStatus.PresenceSensorEntityId ?? string.Empty;
            PresenceSensorComponentInput.Text = currentStatus.PresenceSensorComponentId;
            PresenceSensorCapabilityInput.Text = currentStatus.PresenceSensorCapabilityId;
            PresenceSensorAttributeInput.Text = currentStatus.PresenceSensorAttributeName;
            ApplyPresenceSensorProtocolUi(updateDefaults: false);
            SmartThingsSensorComboBox.Items.Clear();
            SmartThingsSensorComboBox.Visibility = Visibility.Collapsed;
            SmartThingsUseSensorButton.Visibility = Visibility.Collapsed;
            PresenceSensorTokenInput.Clear();
            PresenceSensorStateText.Text = currentStatus.PresenceSensorEnabled
                ? string.Equals(currentStatus.PresenceSensorProtocol, "windows", StringComparison.OrdinalIgnoreCase)
                    ? "이 PC 재실 센서 사용 중 · 추가 로그인·토큰 없음"
                    : $"{PresenceSensorProtocolLabel(currentStatus.PresenceSensorProtocol)} 센서 사용 중 · 토큰 저장됨"
                : "사용 안 함 · 이 PC 재실 센서는 추가 연결 없이 바로 켤 수 있습니다.";
            SmartThingsQuickStatusText.Text = currentStatus.PresenceSensorEnabled
                && string.Equals(currentStatus.PresenceSensorProtocol, "smartthings", StringComparison.OrdinalIgnoreCase)
                ? "현재 센서가 연결되어 있습니다. 다른 센서로 바꾸려면 자동 연결을 누르세요."
                : "센서 이름을 자동으로 불러옵니다.";
            PresenceSensorGraceComboBox.SelectedItem = PresenceSensorGraceComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), currentStatus.PresenceSensorGraceSeconds.ToString(), StringComparison.Ordinal))
                ?? PresenceSensorGraceComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == "10");
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
            ReadyBadge.Background = BrushFrom(usable ? "#293B31" : "#242427");
            await RefreshAuditAsync(silent: true);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or JsonException or InvalidOperationException)
        {
            currentStatus = null;
            SetServiceControls(enabled: false);
            HomePanel.Visibility = Visibility.Visible;
            SettingsPanel.Visibility = Visibility.Collapsed;
            InstallRequiredCard.Visibility = Visibility.Visible;
            InstallButton.IsEnabled = true;
            ServiceStatusText.Text = "● 설치되지 않음";
            ServiceStatusText.Foreground = BrushFrom("#FFB4AB");
            SidebarServiceStatusText.Text = "● 확인 필요";
            SidebarServiceStatusText.Foreground = BrushFrom("#FFB4AB");
            ComputerText.Text = "설정 앱만 실행된 상태입니다.";
            CredentialStateText.Text = "서비스 설치 후 자동으로 현재 계정을 사용합니다.";
            PhoneStateText.Text = "서비스가 없어 연결 QR을 만들 수 없습니다.";
            LoginStateText.Text = "Windows 로그인 연동이 꺼져 있습니다.";
            ReadyBadgeText.Text = "설치 필요";
            ReadyBadge.Background = BrushFrom("#41292A");
            SetOperation("'설치 프로그램 받기'를 누르세요. ZIP이나 PowerShell 작업 없이 복구됩니다.", success: false);
        }
    }

    private void SetServiceControls(bool enabled)
    {
        CreatePairingButton.IsEnabled = enabled;
        StoreCredentialButton.IsEnabled = enabled;
        PasswordInput.IsEnabled = enabled;
        EnableLoginButton.IsEnabled = enabled;
        HomePhoneList.IsEnabled = enabled;
        PhoneSelectorComboBox.IsEnabled = enabled;
        UseSelectedPhoneButton.IsEnabled = enabled && PhoneSelectorComboBox.Items.Count > 0;
        DiagnosticsButton.IsEnabled = enabled;
        ExportDiagnosticsButton.IsEnabled = enabled;
        AuditList.IsEnabled = enabled;
        ProximityLockCheckBox.IsEnabled = enabled;
        ProximityUnlockCheckBox.IsEnabled = enabled;
        SmartArrivalCheckBox.IsEnabled = enabled;
        ProximityGraceComboBox.IsEnabled = enabled;
        RemoteUnlockCheckBox.IsEnabled = enabled;
        RemotePowerCheckBox.IsEnabled = enabled;
        AutoLockProfileComboBox.IsEnabled = enabled;
        PresenceSensorCheckBox.IsEnabled = enabled;
        TestPresenceSensorButton.IsEnabled = enabled;
        PresenceSensorProtocolComboBox.IsEnabled = enabled;
        PresenceSensorUrlInput.IsEnabled = enabled;
        PresenceSensorEntityInput.IsEnabled = enabled;
        PresenceSensorComponentInput.IsEnabled = enabled;
        PresenceSensorCapabilityInput.IsEnabled = enabled;
        PresenceSensorAttributeInput.IsEnabled = enabled;
        PresenceSensorTokenInput.IsEnabled = enabled;
        PresenceSensorGraceComboBox.IsEnabled = enabled;
        BluetoothRssiCheckBox.IsEnabled = enabled;
        BluetoothRssiThresholdComboBox.IsEnabled = enabled;
        FindSmartThingsSensorsButton.IsEnabled = enabled;
        SmartThingsQuickConnectButton.IsEnabled = enabled;
        SmartThingsSensorComboBox.IsEnabled = enabled;
        SmartThingsUseSensorButton.IsEnabled = enabled;
        SecurityCheckupButton.IsEnabled = enabled;
        RevokePhoneButton.IsEnabled = enabled && PhoneSelectorComboBox.Items.Count > 0;
        RevokeAllPhonesButton.IsEnabled = enabled;
        PauseHourButton.IsEnabled = enabled;
        PauseTodayButton.IsEnabled = enabled;
        ResumeAutomationButton.IsEnabled = enabled;
        StartAgentButton.IsEnabled = enabled;
        if (!enabled)
        {
            DisableLoginButton.Visibility = Visibility.Collapsed;
            PairingPanel.Visibility = Visibility.Collapsed;
            HomePhoneList.Items.Clear();
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
                    Foreground = BrushFrom(entry.Suspicious ? "#FFB4AB" : entry.Outcome == "SUCCESS" ? "#8FE0B0" : "#9999A2"),
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

    private int SelectedPresenceGraceSeconds() =>
        int.TryParse((PresenceSensorGraceComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var seconds)
            ? seconds
            : 10;

    private string SelectedAutoLockProfile() =>
        (AutoLockProfileComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "standard";

    private int SelectedBluetoothRssiThreshold() =>
        int.TryParse((BluetoothRssiThresholdComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var threshold)
            ? threshold
            : -75;

    private string SelectedPresenceSensorProtocol() =>
        (PresenceSensorProtocolComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "windows";

    private void ApplyPresenceSensorProtocolUi(bool updateDefaults)
    {
        var protocol = SelectedPresenceSensorProtocol();
        var smartThings = string.Equals(protocol, "smartthings", StringComparison.OrdinalIgnoreCase);
        var windowsPresence = string.Equals(protocol, "windows", StringComparison.OrdinalIgnoreCase);
        SmartThingsQuickSetupPanel.Visibility = smartThings ? Visibility.Visible : Visibility.Collapsed;
        SmartThingsFieldsPanel.Visibility = smartThings ? Visibility.Visible : Visibility.Collapsed;
        FindSmartThingsSensorsButton.Visibility = smartThings ? Visibility.Visible : Visibility.Collapsed;
        PresenceConnectionExpander.Visibility = windowsPresence ? Visibility.Collapsed : Visibility.Visible;
        if (!smartThings)
        {
            SmartThingsSensorComboBox.Visibility = Visibility.Collapsed;
            SmartThingsUseSensorButton.Visibility = Visibility.Collapsed;
            SmartThingsQuickStatusText.Text = "센서 이름을 자동으로 불러옵니다.";
        }
        PresenceSensorTargetHint.Text = windowsPresence
            ? "Windows 11 지원 PC의 사람 감지 센서를 자동으로 사용합니다."
            : smartThings
            ? "SmartThings device ID · capability와 attribute는 아래에 입력"
            : "Home Assistant entity_id · Zigbee/Matter 센서는 HA에 추가되어 있어야 합니다.";
        PresenceSensorUrlInput.ToolTip = smartThings
            ? "SmartThings API 주소 (기본값: https://api.smartthings.com/v1)"
            : "Home Assistant 주소";
        PresenceSensorEntityInput.ToolTip = smartThings
            ? "SmartThings device ID"
            : "sensor 또는 binary_sensor entity_id";
        PresenceSensorTokenInput.ToolTip = smartThings
            ? "SmartThings Personal Access Token"
            : "Home Assistant 장기 액세스 토큰";

        if (!updateDefaults || windowsPresence) return;
        if (smartThings)
        {
            if (string.IsNullOrWhiteSpace(PresenceSensorUrlInput.Text)
                || PresenceSensorUrlInput.Text.Contains("homeassistant.local", StringComparison.OrdinalIgnoreCase))
            {
                PresenceSensorUrlInput.Text = "https://api.smartthings.com/v1";
            }
            if (string.IsNullOrWhiteSpace(PresenceSensorCapabilityInput.Text))
            {
                PresenceSensorCapabilityInput.Text = "occupancySensor";
            }
            if (string.IsNullOrWhiteSpace(PresenceSensorAttributeInput.Text))
            {
                PresenceSensorAttributeInput.Text = "occupancy";
            }
            if (string.IsNullOrWhiteSpace(PresenceSensorComponentInput.Text))
            {
                PresenceSensorComponentInput.Text = "main";
            }
        }
        else if (PresenceSensorUrlInput.Text.Contains("api.smartthings.com", StringComparison.OrdinalIgnoreCase))
        {
            PresenceSensorUrlInput.Text = "http://homeassistant.local:8123";
        }
    }

    private static string PresenceSensorProtocolLabel(string protocol) =>
        protocol.ToLowerInvariant() switch
        {
            "windows" => "Windows 내장 재실",
            "smartthings" => "SmartThings Station",
            "matter" => "Matter",
            _ => "Zigbee"
        };

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

    private static string? FindTailscaleExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tailscale", "tailscale.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
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
        OperationStatusText.Foreground = BrushFrom(success ? "#8FE0B0" : "#FFB4AB");
    }

    private static SolidColorBrush BrushFrom(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));
}
