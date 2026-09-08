using Microsoft.Extensions.Hosting;

namespace Blossom.Classroom.Student.Service;

/// <summary>
/// Heals the interactive student tray process independently of the user's
/// Run-key policy. The service starts at boot, then keeps checking for an
/// active user session so a PC that was powered off or logged out eventually
/// reconnects without another manual installation.
/// </summary>
public sealed class StudentDesktopRecoveryWorker(
    ILogger<StudentDesktopRecoveryWorker> logger) : BackgroundService
{
    // Keep the first launch quick after logon and recover a force-closed
    // desktop within a few seconds. The service itself remains the durable
    // component; this worker only starts the visible per-user watchdog.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows()
            || string.Equals(Environment.GetEnvironmentVariable("CLASSROOM_DISABLE_DESKTOP_AUTOSTART"), "1", StringComparison.Ordinal)) return;

        await DelaySafeAsync(InitialDelay, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var desktopPath = InstalledDesktopPath();
                if (desktopPath is not null)
                {
                    StudentDesktopSessionLauncher.EnsureRunning(
                        desktopPath,
                        message => logger.LogInformation("{Message}", message));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "학생 화면 자동 복구 검사 중 오류가 발생했습니다.");
            }

            await DelaySafeAsync(RetryInterval, stoppingToken);
        }
    }

    private static string? InstalledDesktopPath()
    {
        var serviceDirectory = Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var installRoot = Directory.GetParent(serviceDirectory)?.FullName;
        if (string.IsNullOrWhiteSpace(installRoot))
        {
            return null;
        }

        var desktopPath = Path.Combine(installRoot, "desktop", "Classroom.Student.Desktop.exe");
        return File.Exists(desktopPath) ? desktopPath : null;
    }

    private static async Task DelaySafeAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown is expected.
        }
    }
}
