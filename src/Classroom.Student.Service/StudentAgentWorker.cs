using Microsoft.Extensions.Hosting;
using Blossom.Classroom.Student.Service.Networking;

namespace Blossom.Classroom.Student.Service;

public sealed class StudentAgentWorker(
    ClassroomServerClient client,
    ILogger<StudentAgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Classroom Student Service started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await client.RunAsync(stoppingToken);
                if (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Classroom server loop returned unexpectedly; restarting it.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Classroom Student Service recovered from an unexpected connection failure.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
