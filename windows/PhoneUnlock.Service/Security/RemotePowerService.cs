using System.Collections.Concurrent;
using System.Text.Json;
using PhoneUnlock.Core.Models;
using PhoneUnlock.Core.Protocol;
using PhoneUnlock.Core.Security;
using PhoneUnlock.Service.Interop;
using PhoneUnlock.Service.Models;
using PhoneUnlock.Service.Networking;
using PhoneUnlock.Service.Storage;

namespace PhoneUnlock.Service.Security;

public sealed class RemotePowerService(
    PhoneConnectionRegistry connectionRegistry,
    ConfigurationStore configurationStore,
    RemotePowerController powerController,
    AuditLogStore auditLog,
    ILogger<RemotePowerService> logger) : BackgroundService
{
    private readonly SignatureVerifier signatureVerifier = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> seenRequests = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in connectionRegistry.RemotePowerRequests.ReadAllAsync(stoppingToken))
        {
            await HandleAsync(request, stoppingToken);
        }
    }

    private async Task HandleAsync(RemoteUnlockRequest request, CancellationToken cancellationToken)
    {
        PairedPhoneRecord? phone = null;
        var command = "POWER";
        try
        {
            var configuration = await configurationStore.GetAsync(cancellationToken);
            phone = configuration.Phones.FirstOrDefault(candidate =>
                candidate.Enabled && string.Equals(candidate.PhoneId, request.PhoneId, StringComparison.Ordinal));
            if (phone is null)
            {
                await RecordAsync(request, null, command, "REJECTED", "등록되지 않은 휴대폰의 원격 전원 요청", true, cancellationToken);
                return;
            }

            var envelope = ProtocolJson.Deserialize<ProtocolEnvelope<RemotePowerRequestPayload>>(request.Json);
            var payload = envelope.Payload ?? throw new JsonException("원격 전원 payload가 없습니다.");
            command = payload.Command?.Trim().ToUpperInvariant() ?? "POWER";
            var now = DateTimeOffset.UtcNow;
            if (!configuration.RemotePowerEnabled)
            {
                await RecordAsync(request, phone, command, "DISABLED", "원격 전원 제어가 설정에서 꺼져 있음", false, cancellationToken);
                return;
            }

            CanonicalPayload.ValidateCommand(command);
            if (envelope.Version != ProtocolConstants.Version
                || envelope.Type != ProtocolConstants.RemotePowerRequest
                || payload.PhoneId != request.PhoneId
                || payload.ComputerId != configuration.ComputerId
                || payload.ExpiresAt < now.ToUnixTimeSeconds()
                || payload.ExpiresAt > now.AddSeconds(45).ToUnixTimeSeconds()
                || Math.Abs(envelope.Timestamp - now.ToUnixTimeSeconds()) > 60)
            {
                await RecordAsync(request, phone, command, "REJECTED", "원격 전원 요청의 서명·시간·PC·재사용 검증 실패", true, cancellationToken);
                return;
            }

            if (!signatureVerifier.Verify(payload, phone.PublicKey))
            {
                await RecordAsync(request, phone, command, "REJECTED", "원격 전원 서명이 일치하지 않음", true, cancellationToken);
                return;
            }

            if (!seenRequests.TryAdd(payload.RequestId, now.AddMinutes(2)))
            {
                await RecordAsync(request, phone, command, "REJECTED", "원격 전원 요청이 이미 사용됨", true, cancellationToken);
                return;
            }

            if (!powerController.TryExecute(command, out var error))
            {
                await RecordAsync(request, phone, command, "FAILED", $"원격 {command} 실행 실패: {error}", true, cancellationToken);
                return;
            }

            await RecordAsync(request, phone, command, "SUCCESS", $"휴대폰 생체인증으로 원격 {command} 실행", false, cancellationToken);
            CleanupSeenRequests(now);
            logger.LogInformation("REMOTE_POWER_SUCCESS phone={PhoneId} command={Command}", phone.PhoneId, command);
        }
        catch (JsonException exception)
        {
            await RecordAsync(request, phone, command, "REJECTED", $"원격 전원 JSON이 올바르지 않음: {exception.Message}", true, cancellationToken);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException)
        {
            await RecordAsync(request, phone, command, "REJECTED", $"원격 전원 요청을 처리하지 못함: {exception.Message}", true, cancellationToken);
        }
    }

    private Task RecordAsync(
        RemoteUnlockRequest request,
        PairedPhoneRecord? phone,
        string command,
        string outcome,
        string message,
        bool suspicious,
        CancellationToken cancellationToken) => auditLog.AppendAsync(new AuditEntry(
        DateTimeOffset.UtcNow,
        $"REMOTE_{command}",
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
