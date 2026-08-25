using System.IO.Pipes;
using System.Text;
using PhoneUnlock.Service.Configuration;
using PhoneUnlock.Service.Networking;
using PhoneUnlock.Service.Storage;

namespace PhoneUnlock.Service.Pipes;

public sealed class AgentPipeService(
    ConfigurationStore configurationStore,
    PhoneConnectionRegistry connectionRegistry,
    AgentConnectionState agentConnectionState,
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
        var readTask = reader.ReadLineAsync(agentLifetime.Token).AsTask();
        var monitorTask = MonitorPresenceAsync(writer, agentLifetime.Token);
        await Task.WhenAny(readTask, monitorTask);
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

    private async Task MonitorPresenceAsync(StreamWriter writer, CancellationToken cancellationToken)
    {
        var armed = false;
        var lastPresentAt = DateTimeOffset.UtcNow;
        while (!cancellationToken.IsCancellationRequested)
        {
            var configuration = await configurationStore.GetAsync(cancellationToken);
            if (!configuration.ProximityLockEnabled)
            {
                armed = false;
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                continue;
            }

            var statuses = connectionRegistry.GetStatuses(configuration.Phones);
            var selected = configuration.PreferredPhoneId is null
                ? statuses.FirstOrDefault(phone => phone.Enabled)
                : statuses.FirstOrDefault(phone => phone.PhoneId == configuration.PreferredPhoneId);
            var present = selected?.Connected == true
                && selected.LastHeartbeat is { } heartbeat
                && DateTimeOffset.UtcNow - heartbeat <= TimeSpan.FromSeconds(20);
            if (present)
            {
                armed = true;
                lastPresentAt = DateTimeOffset.UtcNow;
            }
            else
            {
                if (armed && DateTimeOffset.UtcNow - lastPresentAt >= TimeSpan.FromSeconds(configuration.ProximityGraceSeconds))
                {
                    await writer.WriteLineAsync("LOCK");
                    logger.LogInformation("Requested workstation lock after phone presence was lost.");
                    armed = false;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }
}
