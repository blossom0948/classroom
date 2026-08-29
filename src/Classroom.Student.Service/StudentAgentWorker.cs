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
        await client.RunAsync(stoppingToken);
    }
}

