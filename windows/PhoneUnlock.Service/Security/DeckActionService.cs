using System.Collections.Concurrent;
using System.Text.Json;
using PhoneUnlock.Core.Models;
using PhoneUnlock.Core.Protocol;
using PhoneUnlock.Service.Models;
using PhoneUnlock.Service.Networking;
using PhoneUnlock.Service.Pipes;
using PhoneUnlock.Service.Storage;

namespace PhoneUnlock.Service.Security;

public sealed class DeckActionService(
    PhoneConnectionRegistry connectionRegistry,
    ConfigurationStore configurationStore,
    AgentCommandQueue commandQueue,
    AuditLogStore auditLog,
    ILogger<DeckActionService> logger) : BackgroundService
{
    private static readonly HashSet<string> AllowedActions = new(StringComparer.Ordinal)
    {
        "MEDIA_PLAY_PAUSE", "MEDIA_NEXT", "MEDIA_PREVIOUS",
        "VOLUME_UP", "VOLUME_DOWN", "VOLUME_MUTE",
        "SCREENSHOT", "SHOW_DESKTOP", "OPEN_EXPLORER",
        "OPEN_BROWSER", "OPEN_SPOTIFY", "OPEN_STEAM",
    };
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> seen = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in connectionRegistry.DeckActionRequests.ReadAllAsync(stoppingToken))
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
            phone = configuration.Phones.FirstOrDefault(candidate => candidate.Enabled
                && string.Equals(candidate.PhoneId, request.PhoneId, StringComparison.Ordinal));
            var envelope = ProtocolJson.Deserialize<ProtocolEnvelope<DeckActionRequestPayload>>(request.Json);
            var payload = envelope.Payload ?? throw new JsonException("Deck action payload is missing.");
            var now = DateTimeOffset.UtcNow;
            var valid = phone is not null
                && envelope.Version == ProtocolConstants.Version
                && envelope.Type == ProtocolConstants.DeckAction
                && payload.PhoneId == request.PhoneId
                && payload.ComputerId == configuration.ComputerId
                && payload.ExpiresAt >= now.ToUnixTimeSeconds()
                && payload.ExpiresAt <= now.AddSeconds(45).ToUnixTimeSeconds()
                && AllowedActions.Contains(payload.Action)
                && seen.TryAdd(payload.RequestId, now.AddMinutes(2));
            if (!valid)
            {
                await RecordAsync(request, phone, "REJECTED", "허용되지 않거나 만료된 Deck 동작", true, cancellationToken);
                return;
            }

            var queued = commandQueue.Publish(payload.Action);
            await RecordAsync(request, phone, queued ? "SUCCESS" : "BUSY", $"Deck 동작: {payload.Action}", false, cancellationToken);
            await connectionRegistry.TrySendActionResultAsync(
                request.PhoneId, payload.Action, queued,
                queued ? "PC에서 동작을 실행했습니다." : "PC Agent가 사용 중입니다.", cancellationToken);
            Cleanup(now);
            logger.LogInformation("DECK_ACTION phone={PhoneId} action={Action}", request.PhoneId, payload.Action);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            await RecordAsync(request, phone, "REJECTED", "Deck 동작 요청 형식 오류", true, cancellationToken);
        }
    }

    private Task RecordAsync(RemoteUnlockRequest request, PairedPhoneRecord? phone, string outcome,
        string message, bool suspicious, CancellationToken cancellationToken) => auditLog.AppendAsync(new AuditEntry(
            DateTimeOffset.UtcNow, "DECK_ACTION", outcome, request.PhoneId, phone?.PhoneName,
            request.RemoteIp, null, message, suspicious), cancellationToken);

    private void Cleanup(DateTimeOffset now)
    {
        foreach (var item in seen)
        {
            if (item.Value < now) seen.TryRemove(item.Key, out _);
        }
    }
}
