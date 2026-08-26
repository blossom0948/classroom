using System.Collections.Concurrent;
using System.Text.Json;
using PhoneUnlock.Core.Models;
using PhoneUnlock.Core.Protocol;
using PhoneUnlock.Core.Security;
using PhoneUnlock.Service.Interop;
using PhoneUnlock.Service.Models;
using PhoneUnlock.Service.Networking;
using PhoneUnlock.Service.Pipes;
using PhoneUnlock.Service.Storage;

namespace PhoneUnlock.Service.Security;

public sealed class RemoteUnlockService(
    PhoneConnectionRegistry connectionRegistry,
    ConfigurationStore configurationStore,
    WindowsCredentialStore credentialStore,
    RemoteUnlockGrantStore grantStore,
    ProximityUnlockSignal proximityUnlockSignal,
    AgentNotificationQueue notificationQueue,
    AuditLogStore auditLog,
    ILogger<RemoteUnlockService> logger) : BackgroundService
{
    private readonly SignatureVerifier signatureVerifier = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> seenRequests = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in connectionRegistry.RemoteUnlockRequests.ReadAllAsync(stoppingToken))
        {
            await HandleAsync(request, stoppingToken);
        }
    }

    private async Task HandleAsync(RemoteUnlockRequest request, CancellationToken cancellationToken)
    {
        PairedPhoneRecord? phone = null;
        try
        {
            var configuration = await configurationStore.GetAsync(cancellationToken);
            phone = configuration.Phones.FirstOrDefault(candidate =>
                candidate.Enabled && string.Equals(candidate.PhoneId, request.PhoneId, StringComparison.Ordinal));
            if (phone is null)
            {
                await RecordAsync(request, null, "REJECTED", "등록되지 않은 휴대폰의 원격 잠금 해제 요청", suspicious: true, cancellationToken);
                return;
            }

            if (!configuration.RemoteUnlockEnabled)
            {
                await RecordAsync(request, phone, "DISABLED", "원격 잠금 해제가 설정에서 꺼져 있음", suspicious: false, cancellationToken);
                return;
            }

            if (!credentialStore.Exists() || string.IsNullOrWhiteSpace(configuration.ConfiguredAccountSid))
            {
                await RecordAsync(request, phone, "NOT_CONFIGURED", "Windows 계정 자격 증명이 설정되지 않음", suspicious: false, cancellationToken);
                return;
            }

            var envelope = ProtocolJson.Deserialize<ProtocolEnvelope<RemoteUnlockRequestPayload>>(request.Json);
            var payload = envelope.Payload ?? throw new JsonException("원격 잠금 해제 payload가 없습니다.");
            var now = DateTimeOffset.UtcNow;
            if (envelope.Version != ProtocolConstants.Version
                || envelope.Type != ProtocolConstants.RemoteUnlockRequest
                || payload.PhoneId != request.PhoneId
                || payload.ComputerId != configuration.ComputerId
                || payload.ExpiresAt < now.ToUnixTimeSeconds()
                || payload.ExpiresAt > now.AddSeconds(45).ToUnixTimeSeconds()
                || Math.Abs(envelope.Timestamp - now.ToUnixTimeSeconds()) > 60)
            {
                await RecordAsync(request, phone, "REJECTED", "원격 잠금 해제 요청의 시간·PC·재사용 검증 실패", suspicious: true, cancellationToken);
                return;
            }

            if (!signatureVerifier.Verify(payload, phone.PublicKey))
            {
                await RecordAsync(request, phone, "REJECTED", "원격 잠금 해제 서명이 일치하지 않음", suspicious: true, cancellationToken);
                return;
            }

            if (!seenRequests.TryAdd(payload.RequestId, now.AddMinutes(2)))
            {
                await RecordAsync(request, phone, "REJECTED", "원격 잠금 해제 요청이 이미 사용됨", suspicious: true, cancellationToken);
                return;
            }

            grantStore.Grant(phone.PhoneId, configuration.ConfiguredAccountSid, now.AddSeconds(30));
            proximityUnlockSignal.Signal();
            await RecordAsync(request, phone, "SUCCESS", "휴대폰 생체인증으로 1회성 원격 잠금 해제 승인", suspicious: false, cancellationToken);
            notificationQueue.Publish("Phone Unlock", "휴대폰에서 생체인식으로 잠금 해제");
            await connectionRegistry.TrySendActionResultAsync(
                phone.PhoneId,
                "UNLOCK",
                success: true,
                "생체인식으로 PC 잠금 해제 승인",
                cancellationToken);
            CleanupSeenRequests(now);
            logger.LogInformation("REMOTE_UNLOCK_SUCCESS phone={PhoneId} request={RequestId}", phone.PhoneId, payload.RequestId);
        }
        catch (JsonException exception)
        {
            await RecordAsync(request, phone, "REJECTED", $"원격 잠금 해제 JSON이 올바르지 않음: {exception.Message}", suspicious: true, cancellationToken);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException)
        {
            await RecordAsync(request, phone, "REJECTED", $"원격 잠금 해제 요청을 처리하지 못함: {exception.Message}", suspicious: true, cancellationToken);
        }
    }

    private Task RecordAsync(
        RemoteUnlockRequest request,
        PairedPhoneRecord? phone,
        string outcome,
        string message,
        bool suspicious,
        CancellationToken cancellationToken) => auditLog.AppendAsync(new AuditEntry(
        DateTimeOffset.UtcNow,
        "REMOTE_UNLOCK",
        outcome,
        phone?.PhoneId ?? request.PhoneId,
        phone?.PhoneName,
        request.RemoteIp,
        TryGetRequestId(request.Json),
        message,
        suspicious), cancellationToken);

    private void CleanupSeenRequests(DateTimeOffset now)
    {
        foreach (var entry in seenRequests)
        {
            if (entry.Value < now)
            {
                seenRequests.TryRemove(entry.Key, out _);
            }
        }
    }

    private static Guid? TryGetRequestId(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.GetProperty("payload").GetProperty("requestId").GetGuid();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
