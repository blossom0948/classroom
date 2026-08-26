using System.Collections.Concurrent;
using System.Text.Json;
using PhoneUnlock.Core.Models;
using PhoneUnlock.Core.Protocol;
using PhoneUnlock.Service.Models;
using PhoneUnlock.Service.Networking;
using PhoneUnlock.Service.Storage;

namespace PhoneUnlock.Service.Security;

public sealed class RemoteLockService(
    PhoneConnectionRegistry connectionRegistry,
    ConfigurationStore configurationStore,
    WorkstationLockSignal lockSignal,
    AuditLogStore auditLog,
    ILogger<RemoteLockService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> seenRequests = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in connectionRegistry.RemoteLockRequests.ReadAllAsync(stoppingToken))
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
                await RecordAsync(request, null, "REJECTED", "등록되지 않은 휴대폰의 원격 잠금 요청", suspicious: true, cancellationToken);
                return;
            }

            var envelope = ProtocolJson.Deserialize<ProtocolEnvelope<RemoteLockRequestPayload>>(request.Json);
            var payload = envelope.Payload ?? throw new JsonException("원격 잠금 payload가 없습니다.");
            var now = DateTimeOffset.UtcNow;
            if (envelope.Version != ProtocolConstants.Version
                || envelope.Type != ProtocolConstants.RemoteLockRequest
                || payload.PhoneId != request.PhoneId
                || payload.ComputerId != configuration.ComputerId
                || payload.ExpiresAt < now.ToUnixTimeSeconds()
                || payload.ExpiresAt > now.AddSeconds(45).ToUnixTimeSeconds()
                || Math.Abs(envelope.Timestamp - now.ToUnixTimeSeconds()) > 60
                || !seenRequests.TryAdd(payload.RequestId, now.AddMinutes(2)))
            {
                await RecordAsync(request, phone, "REJECTED", "원격 잠금 요청의 시간·PC·재사용 검증 실패", suspicious: true, cancellationToken);
                return;
            }

            lockSignal.Request();
            await RecordAsync(request, phone, "SUCCESS", "휴대폰에서 원격 잠금 요청", suspicious: false, cancellationToken);
            await connectionRegistry.TrySendActionResultAsync(
                phone.PhoneId,
                "LOCK",
                success: true,
                "PC 잠금 요청을 처리했습니다.",
                cancellationToken);
            CleanupSeenRequests(now);
            logger.LogInformation("REMOTE_LOCK_SUCCESS phone={PhoneId} request={RequestId}", phone.PhoneId, payload.RequestId);
        }
        catch (JsonException exception)
        {
            await RecordAsync(request, phone, "REJECTED", $"원격 잠금 JSON이 올바르지 않음: {exception.Message}", suspicious: true, cancellationToken);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException)
        {
            await RecordAsync(request, phone, "REJECTED", $"원격 잠금 요청을 처리하지 못함: {exception.Message}", suspicious: true, cancellationToken);
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
        "REMOTE_LOCK",
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
