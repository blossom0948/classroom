using System.IO.Pipes;
using System.Globalization;
using System.Text;
using PhoneUnlock.Service.Configuration;
using PhoneUnlock.Service.Models;
using PhoneUnlock.Service.Networking;
using PhoneUnlock.Service.Security;
using PhoneUnlock.Service.Storage;

namespace PhoneUnlock.Service.Pipes;

public sealed class AgentPipeService(
    ConfigurationStore configurationStore,
    PhoneConnectionRegistry connectionRegistry,
    PresenceSensorClient presenceSensorClient,
    AgentConnectionState agentConnectionState,
    AgentNotificationQueue notificationQueue,
    WorkstationLockSignal lockSignal,
    ILogger<AgentPipeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var configuration = await configurationStore.GetAsync(stoppingToken);
            await using var pipe = SecureNamedPipe.Create(ServiceConstants.AgentPipeName, configuration.ConfiguredAccountSid);
            try
            {
                using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                connectionTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                await pipe.WaitForConnectionAsync(connectionTimeout.Token);
                agentConnectionState.SetConnected(true);
                await HandleAgentAsync(pipe, stoppingToken);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Recreate the pipe periodically so a newly configured Windows user receives access.
            }
            catch (IOException exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Interactive agent pipe ended: {Message}", exception.Message);
            }
            finally
            {
                agentConnectionState.SetConnected(false);
            }
        }
    }

    private async Task HandleAgentAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
        var ready = await reader.ReadLineAsync(cancellationToken);
        if (!string.Equals(ready, "READY", StringComparison.Ordinal))
        {
            return;
        }

        using var agentLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var readTask = ReadAgentMessagesAsync(reader, agentLifetime.Token);
        var monitorTask = MonitorPresenceAsync(writer, agentLifetime.Token);
        var noticeTask = notificationQueue.WaitAsync(agentLifetime.Token).AsTask();
        var lockTask = lockSignal.WaitAsync(agentLifetime.Token).AsTask();
        while (!agentLifetime.IsCancellationRequested)
        {
            var completed = await Task.WhenAny(readTask, monitorTask, lockTask, noticeTask);
            if (completed == lockTask)
            {
                await writer.WriteLineAsync("LOCK");
                lockTask = lockSignal.WaitAsync(agentLifetime.Token).AsTask();
                continue;
            }

            if (completed == noticeTask)
            {
                var notice = await noticeTask;
                var title = Convert.ToBase64String(Encoding.UTF8.GetBytes(notice.Title));
                var message = Convert.ToBase64String(Encoding.UTF8.GetBytes(notice.Message));
                await writer.WriteLineAsync($"NOTICE|{title}|{message}");
                noticeTask = notificationQueue.WaitAsync(agentLifetime.Token).AsTask();
                continue;
            }

            break;
        }

        agentLifetime.Cancel();
        try
        {
            await monitorTask;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Automatic lock monitor stopped for the interactive agent connection.");
        }
    }

    private async Task ReadAgentMessagesAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return;
            }

            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length == 3
                && string.Equals(parts[0], "RSSI", StringComparison.Ordinal)
                && Guid.TryParse(parts[1], out _)
                && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rssi))
            {
                agentConnectionState.SetRssi(parts[1], rssi);
                continue;
            }

            if (parts.Length == 2
                && string.Equals(parts[0], "PRESENCE", StringComparison.Ordinal)
                && (string.Equals(parts[1], "PRESENT", StringComparison.Ordinal)
                    || string.Equals(parts[1], "ABSENT", StringComparison.Ordinal)))
            {
                agentConnectionState.SetHumanPresence(string.Equals(parts[1], "PRESENT", StringComparison.Ordinal));
            }
        }
    }

    private async Task MonitorPresenceAsync(StreamWriter writer, CancellationToken cancellationToken)
    {
        var phoneArmed = false;
        var sensorArmed = false;
        var lastPhonePresentAt = DateTimeOffset.UtcNow;
        var lastSensorPresentAt = DateTimeOffset.UtcNow;
        while (!cancellationToken.IsCancellationRequested)
        {
            var configuration = await configurationStore.GetAsync(cancellationToken);
            if (configuration.IsPaused())
            {
                phoneArmed = false;
                sensorArmed = false;
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                continue;
            }
            if (!configuration.ProximityLockEnabled
                && !configuration.BluetoothRssiEnabled
                && !configuration.PresenceSensorEnabled)
            {
                phoneArmed = false;
                sensorArmed = false;
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                continue;
            }

            var statuses = connectionRegistry.GetStatuses(configuration.Phones);
            var selected = configuration.PreferredPhoneId is null
                ? statuses.FirstOrDefault(phone => phone.Enabled)
                : statuses.FirstOrDefault(phone => phone.PhoneId == configuration.PreferredPhoneId);
            var heartbeatPresent = selected?.Connected == true
                && selected.LastHeartbeat is { } heartbeat
                && DateTimeOffset.UtcNow - heartbeat <= TimeSpan.FromSeconds(20);
            var rssiPresent = selected is not null
                && agentConnectionState.TryGetRecentRssi(
                    selected.PhoneId,
                    TimeSpan.FromSeconds(20),
                    out var rssi)
                && rssi >= configuration.BluetoothRssiThreshold;
            var present = heartbeatPresent && (!configuration.BluetoothRssiEnabled || rssiPresent);
            var sensorPresent = await ReadSensorPresenceAsync(configuration, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            if (present)
            {
                phoneArmed = true;
                lastPhonePresentAt = now;
            }
            if (sensorPresent == true)
            {
                sensorArmed = true;
                lastSensorPresentAt = now;
            }

            var phoneLockActive = configuration.ProximityLockEnabled || configuration.BluetoothRssiEnabled;
            var phoneExpired = phoneLockActive
                && phoneArmed
                && !present
                && now - lastPhonePresentAt >= TimeSpan.FromSeconds(configuration.ProximityGraceSeconds);
            var sensorExpired = configuration.PresenceSensorEnabled
                && sensorArmed
                && sensorPresent == false
                && now - lastSensorPresentAt >= TimeSpan.FromSeconds(configuration.PresenceSensorGraceSeconds);
            if (phoneExpired || sensorExpired)
            {
                await writer.WriteLineAsync("LOCK");
                logger.LogInformation("Requested workstation lock after {Reason} presence was lost.",
                    sensorExpired ? "room sensor" : configuration.BluetoothRssiEnabled ? "phone Bluetooth RSSI" : "phone");
                phoneArmed = false;
                sensorArmed = false;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private async Task<bool?> ReadSensorPresenceAsync(
        ServiceConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!configuration.PresenceSensorEnabled)
        {
            return null;
        }

        if (string.Equals(configuration.PresenceSensorProtocol, "windows", StringComparison.OrdinalIgnoreCase))
        {
            return agentConnectionState.TryGetRecentHumanPresence(TimeSpan.FromSeconds(12), out var present)
                ? present
                : null;
        }

        return await presenceSensorClient.ReadPresenceAsync(configuration, cancellationToken);
    }
}
