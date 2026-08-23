using System.Collections.Concurrent;
using System.Net.WebSockets;
using PhoneUnlock.Service.Models;
using PhoneUnlock.Service.Security;
using PhoneUnlock.Service.Storage;

namespace PhoneUnlock.Service.Networking;

public sealed class PhoneConnectionRegistry(
    ConfigurationStore configurationStore,
    ILoggerFactory loggerFactory)
{
    private readonly ConcurrentDictionary<string, PhoneConnection> connections = new(StringComparer.Ordinal);

    public async Task<PairedPhoneRecord?> AuthenticateDeviceAsync(
        string? phoneId,
        string? authorizationHeader,
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
        return configuration.Phones.FirstOrDefault(phone =>
            phone.Enabled
            && string.Equals(phone.PhoneId, phoneId, StringComparison.Ordinal)
            && TokenSecurity.VerifyToken(token, phone.DeviceTokenHash));
    }

    public async Task AcceptAsync(
        PairedPhoneRecord phone,
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var connection = new PhoneConnection(
            phone,
            socket,
            loggerFactory.CreateLogger<PhoneConnection>());
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
        CancellationToken cancellationToken)
    {
        var configuration = await configurationStore.GetAsync(cancellationToken);
        foreach (var phone in configuration.Phones.Where(phone => phone.Enabled))
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

    private Task UpdateLastSeenAsync(string phoneId, CancellationToken cancellationToken) =>
        configurationStore.UpdateAsync(configuration => configuration with
        {
            Phones = configuration.Phones.Select(phone =>
                string.Equals(phone.PhoneId, phoneId, StringComparison.Ordinal)
                    ? phone with { LastSeen = DateTimeOffset.UtcNow }
                    : phone).ToList()
        }, cancellationToken);
}
