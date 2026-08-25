using PhoneUnlock.Service.Interop;
using PhoneUnlock.Service.Models;
using PhoneUnlock.Service.Networking;
using PhoneUnlock.Service.Storage;

namespace PhoneUnlock.Service.Pipes;

public sealed class ProximityPresenceService(
    ConfigurationStore configurationStore,
    PhoneConnectionRegistry connectionRegistry,
    PresenceSensorClient presenceSensorClient,
    ProximityUnlockSignal proximityUnlockSignal,
    ILogger<ProximityPresenceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wasPresent = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var configuration = await configurationStore.GetAsync(stoppingToken);
            if (!configuration.ProximityUnlockEnabled)
            {
                wasPresent = false;
                proximityUnlockSignal.Reset();
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }

            var phonePresent = IsSelectedPhonePresent(configuration);
            var sensorPresent = configuration.PresenceSensorEnabled
                ? await presenceSensorClient.ReadPresenceAsync(configuration, stoppingToken)
                : null;
            var present = phonePresent || sensorPresent == true;
            if (present && !wasPresent)
            {
                proximityUnlockSignal.Signal();
                logger.LogInformation("Signaled automatic unlock after {Source} presence was detected.",
                    phonePresent ? "trusted phone" : "room sensor");
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

    private bool IsSelectedPhonePresent(ServiceConfiguration configuration)
    {
        var statuses = connectionRegistry.GetStatuses(configuration.Phones);
        var selected = configuration.PreferredPhoneId is null
            ? statuses.FirstOrDefault(phone => phone.Enabled)
            : statuses.FirstOrDefault(phone => phone.PhoneId == configuration.PreferredPhoneId);
        return selected?.Enabled == true
            && selected.Connected
            && selected.LastHeartbeat is { } heartbeat
            && DateTimeOffset.UtcNow - heartbeat <= TimeSpan.FromSeconds(20);
    }
}
