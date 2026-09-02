using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Forms;
using Blossom.Classroom.Core.Desktop;

namespace Blossom.Classroom.Student.Setup;

internal static class Program
{
    // Cloudflare Worker endpoint: unlike the former local Tunnel, this remains
    // available when the teacher's computer is turned off.
    private const string DefaultServerOrigin = "https://classroom-api.blossom0948.cloud";

    [STAThread]
    private static void Main(string[] args)
    {
        if (ElevatedStudentInstaller.IsInstallInvocation(args))
        {
            Environment.ExitCode = ElevatedStudentInstaller.Run(args);
            return;
        }

        if (TryStartExistingInstallation())
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new StudentSetupForm(ResolveServerOrigin(args)));
    }

    private static bool TryStartExistingInstallation()
    {
        if (!StudentDesktopConfigurationStore.TryLoad(out _))
        {
            return false;
        }

        var desktopPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Blossom Classroom Student",
            "desktop",
            "Classroom.Student.Desktop.exe");
        if (!File.Exists(desktopPath))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = desktopPath,
                Arguments = "--classroom-watchdog",
                WorkingDirectory = Path.GetDirectoryName(desktopPath)!,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static Uri ResolveServerOrigin(string[] args)
    {
        var configured = Environment.GetEnvironmentVariable("CLASSROOM_STUDENT_SERVER_URL");
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--server-url", StringComparison.OrdinalIgnoreCase))
            {
                configured = args[index + 1];
                break;
            }
        }

        if (!Uri.TryCreate(configured, UriKind.Absolute, out var candidate)
            || candidate.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(candidate.Host))
        {
            candidate = new Uri(DefaultServerOrigin);
        }

        var builder = new UriBuilder(candidate)
        {
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
    }
}
