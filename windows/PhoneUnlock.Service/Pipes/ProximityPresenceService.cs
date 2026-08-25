using PhoneUnlock.Service.Interop;
using PhoneUnlock.Service.Models;
using PhoneUnlock.Service.Networking;
using PhoneUnlock.Service.Storage;

namespace PhoneUnlock.Service.Pipes;

public sealed class ProximityPresenceService(
    ConfigurationStore configurationStore,
    PhoneConnectionRegistry connectionRegistry,
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

            var present = IsSelectedPhonePresent(configuration);
            if (present && !wasPresent)
            {
                proximityUnlockSignal.Signal();
                logger.LogInformation("Signaled trusted-phone proximity unlock.");
            }
            wasPresent = present;
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
