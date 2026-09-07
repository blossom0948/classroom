using System.Text;

namespace Blossom.Classroom.Student.Desktop;

/// <summary>
/// The student desktop is normally a WinExe with no console, so a crash or
/// transient background exception is otherwise invisible to the school user.
/// Keep a small text log so the watchdog and support team can tell whether the
/// process was restarted.
/// </summary>
internal static class StudentDesktopDiagnostics
{
    private static readonly object Gate = new();

    public static void Log(string message, Exception? exception = null)
    {
        var line = $"[{DateTimeOffset.Now:O}] {message}"
            + (exception is null ? string.Empty : $"{Environment.NewLine}{exception}")
            + Environment.NewLine;

        try
        {
            lock (Gate)
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Blossom Classroom Student",
                    "logs");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "student-desktop.log");
                File.AppendAllText(path, line, Encoding.UTF8);

                var fileInfo = new FileInfo(path);
                if (fileInfo.Length > 1_000_000)
                {
                    var rotatedPath = path + ".1";
                    try
                    {
                        File.Delete(rotatedPath);
                        File.Move(path, rotatedPath);
                    }
                    catch (IOException)
                    {
                        // Logging must never take down the student process.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Logging must never take down the student process.
                    }
                }
            }
        }
        catch (Exception)
        {
            // The app must keep reconnecting even when the log directory is
            // unavailable (for example during a profile transition).
        }

        try
        {
            Console.Error.WriteLine(line);
        }
        catch (Exception)
        {
            // WinExe processes commonly have no console attached.
        }
    }
}
