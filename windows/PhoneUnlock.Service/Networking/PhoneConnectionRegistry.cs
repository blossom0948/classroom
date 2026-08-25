using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using PhoneUnlock.Service.Models;
using PhoneUnlock.Service.Security;
using PhoneUnlock.Service.Storage;

namespace PhoneUnlock.Service.Networking;

public sealed class PhoneConnectionRegistry(
    ConfigurationStore configurationStore,
    AuditLogStore auditLog,
    ILoggerFactory loggerFactory)
{
    private readonly ConcurrentDictionary<string, PhoneConnection> connections = new(StringComparer.Ordinal);
    private readonly Channel<RemoteUnlockRequest> remoteUnlockRequests = Channel.CreateUnbounded<RemoteUnlockRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Channel<RemoteUnlockRequest> remoteLockRequests = Channel.CreateUnbounded<RemoteUnlockRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public ChannelReader<RemoteUnlockRequest> RemoteUnlockRequests => remoteUnlockRequests.Reader;
    public ChannelReader<RemoteUnlockRequest> RemoteLockRequests => remoteLockRequests.Reader;

    public async Task<PairedPhoneRecord?> AuthenticateDeviceAsync(
        string? phoneId,
        string? authorizationHeader,
        string? remoteIp,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(phoneId)
            || string.IsNullOrWhiteSpace(authorizationHeader)
            || !authorizationHeader.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return null;
        }

        var token = authorizationHeader["Bearer ".Length..].Trim();
        var configuration = await configurationStore.GetAsync(cancellationToken);
        var matched = configuration.Phones.FirstOrDefault(phone =>
            phone.Enabled
            && string.Equals(phone.PhoneId, phoneId, StringComparison.Ordinal)
            && TokenSecurity.VerifyToken(token, phone.DeviceTokenHash));
        if (matched is null)
        {
            await auditLog.AppendAsync(new AuditEntry(
                DateTimeOffset.UtcNow,
                "CONNECTION_ATTEMPT",
                "REJECTED",
                phoneId,
                null,
                remoteIp,
                null,
                "알 수 없거나 비활성화된 휴대폰 연결 요청",
                Suspicious: true), cancellationToken);
        }

        return matched;
    }

    public async Task AcceptAsync(
        PairedPhoneRecord phone,
        WebSocket socket,
        string? remoteIp,
        CancellationToken cancellationToken)
    {
        var connection = new PhoneConnection(
            phone,
            socket,
            remoteIp,
            loggerFactory.CreateLogger<PhoneConnection>(),
            json => remoteUnlockRequests.Writer.TryWrite(new RemoteUnlockRequest(phone.PhoneId, remoteIp, json)),
            json => remoteLockRequests.Writer.TryWrite(new RemoteUnlockRequest(phone.PhoneId, remoteIp, json)));
        if (connections.TryGetValue(phone.PhoneId, out var previous))
        {
            await previous.DisposeAsync();
        }

        connections[phone.PhoneId] = connection;
        await UpdateLastSeenAsync(phone.PhoneId, cancellationToken);
        try
        {
            await connection.RunReceiveLoopAsync(cancellationToken);
        }
        finally
        {
            connections.TryRemove(new KeyValuePair<string, PhoneConnection>(phone.PhoneId, connection));
            await connection.DisposeAsync();
            await UpdateLastSeenAsync(phone.PhoneId, CancellationToken.None);
        }
    }

    public async Task<(PairedPhoneRecord Phone, PhoneConnection Connection)?> GetConnectedPhoneAsync(
        string? preferredPhoneId,
        CancellationToken cancellationToken)
    {
        var configuration = await configurationStore.GetAsync(cancellationToken);
        var phones = configuration.Phones
            .Where(phone => phone.Enabled)
            .OrderByDescending(phone => string.Equals(phone.PhoneId, preferredPhoneId, StringComparison.Ordinal));
        foreach (var phone in phones)
        {
            if (connections.TryGetValue(phone.PhoneId, out var connection) && connection.IsOpen)
            {
                return (phone, connection);
            }
        }

        return null;
    }

    public bool IsConnected(string phoneId) =>
        connections.TryGetValue(phoneId, out var connection) && connection.IsOpen;

    public IReadOnlyList<PhoneConnectionStatus> GetStatuses(IReadOnlyList<PairedPhoneRecord> phones) =>
        phones.Select(phone =>
        {
            connections.TryGetValue(phone.PhoneId, out var connection);
            return new PhoneConnectionStatus(
                phone.PhoneId,
                phone.PhoneName,
                phone.Enabled,
                connection?.IsOpen == true,
                phone.LastSeen,
                connection?.LastHeartbeat,
                connection?.RemoteIp);
        }).ToArray();

    public bool HasRecentHeartbeat(string phoneId, TimeSpan threshold)
    {
        return connections.TryGetValue(phoneId, out var connection)
            && connection.IsOpen
            && connection.LastHeartbeat is { } heartbeat
            && DateTimeOffset.UtcNow - heartbeat <= threshold;
    }

    private Task UpdateLastSeenAsync(string phoneId, CancellationToken cancellationToken) =>
        configurationStore.UpdateAsync(configuration => configuration with
        {
            Phones = configuration.Phones.Select(phone =>
                string.Equals(phone.PhoneId, phoneId, StringComparison.Ordinal)
                    ? phone with { LastSeen = DateTimeOffset.UtcNow }
                    : phone).ToList()
        }, cancellationToken);
}
