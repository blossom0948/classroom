using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PhoneUnlock.Core.Models;
using PhoneUnlock.Core.Protocol;
using PhoneUnlock.Core.Security;

namespace PhoneUnlock.Desktop;

public partial class MainWindow : Window
{
    private readonly ChallengeGenerator challengeGenerator = new();
    private readonly ChallengeStore challengeStore = new();
    private readonly AuthValidationService validationService;
    private readonly DesktopIdentity identity;
    private ProtocolEnvelope<AuthRequestPayload>? activeRequest;

    public MainWindow()
    {
        InitializeComponent();
        validationService = new AuthValidationService(challengeStore, new SignatureVerifier());
        identity = DesktopIdentity.LoadOrCreate();
        ComputerNameText.Text = identity.ComputerName;
        ComputerIdText.Text = identity.ComputerId.ToString("D");
        AddLog("Phase 1 테스트 앱 시작 — Windows 로그인 변경 없음");
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            Keyboard.ClearFocus();
            MainScrollViewer.ScrollToTop();
        }, DispatcherPriority.ContextIdle);
    }

    private void GenerateChallenge_Click(object sender, RoutedEventArgs e) => GenerateChallenge();

    private void GenerateChallenge()
    {
        activeRequest = challengeGenerator.Create(identity.ComputerId, identity.ComputerName);
        challengeStore.Register(activeRequest.Payload);
        RequestJsonBox.Text = ProtocolJson.Serialize(activeRequest);
        CanonicalPayloadBox.Text = CanonicalPayload.Create(activeRequest.Payload);
        PublicKeyBox.Clear();
        ResponseJsonBox.Clear();

        AddLog($"AUTH_REQUEST 생성 request={ShortId(activeRequest.Payload.RequestId)}");
        SetResult(
            "휴대폰 승인을 기다립니다",
            $"{activeRequest.Payload.ExpiresAt - activeRequest.Payload.CreatedAt}초 안에 Android에서 서명하세요.",
            ResultKind.Pending);
    }

    private void CopyRequest_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RequestJsonBox.Text))
        {
            SetResult("복사할 요청이 없습니다", "먼저 테스트 challenge를 생성하세요.", ResultKind.Error);
            return;
        }

        Clipboard.SetText(RequestJsonBox.Text);
        AddLog("AUTH_REQUEST JSON을 클립보드에 복사");
    }

    private void VerifyResponse_Click(object sender, RoutedEventArgs e) => VerifyCurrentResponse();

    private void VerifyCurrentResponse()
    {
        if (activeRequest is null)
        {
            SetResult("대기 중인 요청이 없습니다", "먼저 테스트 challenge를 생성하세요.", ResultKind.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(PublicKeyBox.Text) || string.IsNullOrWhiteSpace(ResponseJsonBox.Text))
        {
            SetResult("Android 응답이 필요합니다", "공개키와 AUTH_APPROVED JSON을 모두 붙여 넣으세요.", ResultKind.Error);
            return;
        }

        try
        {
            var response = ProtocolJson.Deserialize<ProtocolEnvelope<AuthApprovedPayload>>(ResponseJsonBox.Text);
            AddLog($"AUTH_RESPONSE 수신 request={ShortId(response.Payload.RequestId)}");
            var status = validationService.Verify(response, PublicKeyBox.Text.Trim());
            ShowValidationStatus(status);
        }
        catch (JsonException exception)
        {
            AddLog("AUTH_RESPONSE JSON 파싱 실패");
            SetResult("응답 JSON이 올바르지 않습니다", exception.Message, ResultKind.Error);
        }
    }

    private void RunLocalDemo_Click(object sender, RoutedEventArgs e)
    {
        GenerateChallenge();
        if (activeRequest is null)
        {
            return;
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = activeRequest.Payload;
        var unsigned = new AuthApprovedPayload(
            request.RequestId,
            request.ComputerId,
            request.Challenge,
            request.ExpiresAt,
            "local-demo-only",
            string.Empty);
        var signature = key.SignData(
            CanonicalPayload.GetBytes(unsigned),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        var signed = unsigned with { Signature = Convert.ToBase64String(signature) };
        var response = new ProtocolEnvelope<AuthApprovedPayload>(
            ProtocolConstants.Version,
            ProtocolConstants.AuthApproved,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            signed);

        PublicKeyBox.Text = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        ResponseJsonBox.Text = ProtocolJson.Serialize(response);
        AddLog("로컬 P-256 테스트 키로 DER 서명 생성");
        VerifyCurrentResponse();
    }

    private void ShowValidationStatus(AuthValidationStatus status)
    {
        var (title, detail, kind) = status switch
        {
            AuthValidationStatus.Success => ("✓ 암호 인증 성공", "서명이 유효하며 challenge를 1회 소비했습니다. 실제 Windows 잠금은 해제하지 않았습니다.", ResultKind.Success),
            AuthValidationStatus.Replayed => ("재전송 응답 거부", "이미 성공 처리된 request ID입니다.", ResultKind.Error),
            AuthValidationStatus.Expired => ("인증 요청 만료", "새 challenge를 생성해 다시 시도하세요.", ResultKind.Error),
            AuthValidationStatus.RequestMismatch => ("요청 내용 불일치", "request ID, PC ID, challenge 또는 만료시간이 원 요청과 다릅니다.", ResultKind.Error),
            AuthValidationStatus.InvalidPublicKeyOrSignature => ("서명 검증 실패", "공개키가 다르거나 응답이 변경되었습니다.", ResultKind.Error),
            AuthValidationStatus.UnknownRequest => ("알 수 없는 요청", "현재 PC가 만든 pending request가 아닙니다.", ResultKind.Error),
            AuthValidationStatus.UnsupportedProtocol => ("지원하지 않는 프로토콜", "version 1 응답만 받을 수 있습니다.", ResultKind.Error),
            AuthValidationStatus.WrongMessageType => ("잘못된 메시지 종류", "AUTH_APPROVED 응답이 필요합니다.", ResultKind.Error),
            _ => ("인증 실패", status.ToString(), ResultKind.Error)
        };

        AddLog(status == AuthValidationStatus.Success ? "ECDSA 서명 유효 — AUTH_SUCCESS" : $"응답 거부 — {status}");
        SetResult(title, detail, kind);
    }

    private void SetResult(string title, string detail, ResultKind kind)
    {
        ResultTitleText.Text = title;
        ResultDetailText.Text = detail;
        ResultCard.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(kind switch
        {
            ResultKind.Success => "#EAF7EF",
            ResultKind.Error => "#FFF0F0",
            ResultKind.Pending => "#EEF3FF",
            _ => "#F0F1F3"
        }));
    }

    private void AddLog(string message)
    {
        LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (LogList.Items.Count > 50)
        {
            LogList.Items.RemoveAt(LogList.Items.Count - 1);
        }
    }

    private static string ShortId(Guid value) => value.ToString("N")[..8];

    private enum ResultKind
    {
        Neutral,
        Pending,
        Success,
        Error
    }
}

internal sealed record DesktopIdentity(Guid ComputerId, string ComputerName)
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhoneUnlock",
        "desktop-identity.json");

    public static DesktopIdentity LoadOrCreate()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                var stored = JsonSerializer.Deserialize<DesktopIdentity>(File.ReadAllText(StatePath));
                if (stored is null || stored.ComputerId == Guid.Empty)
                {
                    throw new JsonException("Stored computer ID is empty.");
                }

                return stored;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged development identity is safely replaced; it contains no credential.
        }

        var identity = new DesktopIdentity(Guid.NewGuid(), Environment.MachineName);
        var directory = Path.GetDirectoryName(StatePath)
            ?? throw new InvalidOperationException("Could not determine local state directory.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(identity, ProtocolJson.Options));
        return identity;
    }
}
