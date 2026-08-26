using System.Net.WebSockets;
using PhoneUnlock.Service.Interop;
using PhoneUnlock.Service.Models;
using PhoneUnlock.Service.Networking;
using PhoneUnlock.Service.Storage;

namespace PhoneUnlock.Service.Pipes;

public sealed class ProximityPresenceService(
    ConfigurationStore configurationStore,
    PhoneConnectionRegistry connectionRegistry,
    PresenceSensorClient presenceSensorClient,
    AgentConnectionState agentConnectionState,
    AgentNotificationQueue notificationQueue,
    ProximityUnlockSignal proximityUnlockSignal,
    ProximityUnlockResultSignal proximityUnlockResultSignal,
    ILogger<ProximityPresenceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wasPresent = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var configuration = await configurationStore.GetAsync(stoppingToken);
            await PublishUnlockSuccessIfAnyAsync(configuration, stoppingToken);
            if ((!configuration.ProximityUnlockEnabled && !configuration.SmartArrivalEnabled)
                || configuration.IsPaused())
            {
                wasPresent = false;
                proximityUnlockSignal.Reset();
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }

            var selectedConnection = await GetSelectedPhoneAsync(configuration, stoppingToken);
            var phonePresent = selectedConnection is not null;
            var sensorPresent = await ReadSensorPresenceAsync(configuration, stoppingToken);
            var present = phonePresent || sensorPresent == true;
            if (present && !wasPresent)
            {
                if (configuration.SmartArrivalEnabled && configuration.RemoteUnlockEnabled && phonePresent)
                {
                    if (selectedConnection is not null)
                    {
                        try
                        {
                            await selectedConnection.SendSmartArrivalAsync(
                                configuration.ComputerId,
                                configuration.ComputerName,
                                stoppingToken);
                            logger.LogInformation("Sent Smart Arrival biometric prompt after trusted phone presence was detected.");
                        }
                        catch (Exception exception) when (exception is IOException or WebSocketException)
                        {
                            logger.LogInformation("Could not send Smart Arrival prompt: {Message}", exception.Message);
                        }
                    }
                }
                if (configuration.ProximityUnlockEnabled)
                {
                    proximityUnlockSignal.Signal(
                        phonePresent ? ProximityUnlockSource.TrustedPhone : ProximityUnlockSource.RoomSensor);
                    logger.LogInformation("Signaled experimental automatic unlock after {Source} presence was detected.",
                        phonePresent ? "trusted phone" : "room sensor");
                }
            }

            // Do not treat a temporary API outage as an absence transition. A
            // confirmed absence is required before the next arrival can unlock.
            if (!phonePresent && (!configuration.PresenceSensorEnabled || sensorPresent == false))
            {
                wasPresent = false;
            }
            else if (present)
            {
                wasPresent = true;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task PublishUnlockSuccessIfAnyAsync(
        ServiceConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!proximityUnlockResultSignal.TryConsume(out var source))
        {
            return;
        }

        var (title, message, sourceName) = source == ProximityUnlockSource.RoomSensor
            ? ("Phone Unlock", "재실 센서 감지로 PC 잠금 해제 완료", "room_sensor")
            : ("Phone Unlock", "인증된 휴대폰 감지로 PC 잠금 해제 완료", "trusted_phone");
        notificationQueue.Publish(title, message);

        var selectedConnection = await GetSelectedPhoneAsync(configuration, cancellationToken);
        if (selectedConnection is not null)
        {
            try
            {
                await selectedConnection.SendAutomationNoticeAsync(message, sourceName, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or WebSocketException)
            {
                logger.LogInformation("Could not send automatic-unlock success notice: {Message}", exception.Message);
            }
        }

        logger.LogInformation("Automatic unlock succeeded through {Source}.", sourceName);
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

    private async Task<PhoneConnection?> GetSelectedPhoneAsync(
        ServiceConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var connected = await connectionRegistry.GetConnectedPhoneAsync(
            configuration.PreferredPhoneId,
            cancellationToken);
        if (connected is null
            || !connectionRegistry.HasRecentHeartbeat(connected.Value.Phone.PhoneId, TimeSpan.FromSeconds(20)))
        {
            return null;
        }

        return connected.Value.Connection;
    }
}
