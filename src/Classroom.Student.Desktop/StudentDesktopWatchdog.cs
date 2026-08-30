using System.Diagnostics;
using Blossom.Classroom.Student.Desktop.Configuration;

namespace Blossom.Classroom.Student.Desktop;

internal static class StudentDesktopWatchdog
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public static async Task RunAsync()
    {
        var options = StudentDesktopOptions.FromEnvironment();
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
                    studentProcess?.Dispose();
                    studentProcess = null;
                }

                await Task.Delay(PollInterval);
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
            Arguments = "--classroom-student-ui",
            WorkingDirectory = workingDirectory ?? string.Empty,
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("학생 화면을 시작하지 못했습니다.");
}
