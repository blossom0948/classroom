using System.Diagnostics;
using Blossom.Classroom.Core.Desktop;
using Blossom.Classroom.Student.Desktop.Configuration;

namespace Blossom.Classroom.Student.Desktop;

internal static class StudentDesktopWatchdog
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public static async Task RunAsync()
    {
        StudentDesktopOptions options;
        while (true)
        {
            try
            {
                options = StudentDesktopOptions.FromEnvironment();
                break;
            }
            catch (Exception exception)
            {
                // Enrollment can be restored a moment after the Windows user
                // session starts. Keep retrying instead of letting a missing
                // environment value permanently disable the watchdog.
                StudentDesktopDiagnostics.Log("Student Desktop configuration is not ready; retrying.", exception);
                await DelayBeforeRetryAsync(PollInterval);
            }
        }

        using var mutex = new Mutex(
            initiallyOwned: true,
            $"Local\\BlossomClassroomStudentWatchdog-{options.DeviceId:N}",
            out var ownsMutex);
        if (!ownsMutex)
        {
            return;
        }

        var executablePath = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("학생 화면 실행 파일 경로를 확인하지 못했습니다.");
        var workingDirectory = Path.GetDirectoryName(executablePath);
        Process? studentProcess = null;
        try
        {
            while (true)
            {
                try
                {
                    if (StudentDesktopExitAuthorization.IsGrantedForCurrentBoot(options.DeviceId))
                    {
                        return;
                    }

                    if (studentProcess is null || studentProcess.HasExited)
                    {
                        studentProcess?.Dispose();
                        studentProcess = StartStudentDesktop(executablePath, workingDirectory);
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception)
                {
                    // A transient Windows startup failure must not disable the
                    // watchdog itself. Retry the visible student window.
                    StudentDesktopDiagnostics.Log("Student Desktop restart attempt failed.", exception);
                    studentProcess?.Dispose();
                    studentProcess = null;
                }
                catch (Exception exception)
                {
                    // Process handles, profile transitions, and security
                    // products can throw exceptions that are not predictable
                    // at install time. Keep the watchdog alive for all of
                    // them instead of losing the background connection.
                    StudentDesktopDiagnostics.Log("Student Desktop watchdog recovered from an unexpected error.", exception);
                    studentProcess?.Dispose();
                    studentProcess = null;
                }

                await DelayBeforeRetryAsync(PollInterval);
            }
        }
        finally
        {
            studentProcess?.Dispose();
        }
    }

    private static Process StartStudentDesktop(string executablePath, string? workingDirectory) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = "--classroom-background",
            WorkingDirectory = workingDirectory ?? string.Empty,
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("학생 화면을 시작하지 못했습니다.");

    private static async Task DelayBeforeRetryAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay);
        }
        catch (Exception exception)
        {
            // A delay failure should not end the watchdog. Yield once and
            // immediately continue the supervision loop.
            StudentDesktopDiagnostics.Log("Student Desktop watchdog delay failed; continuing.", exception);
            await Task.Yield();
        }
    }
}
