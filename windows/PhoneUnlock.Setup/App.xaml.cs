using System.IO;
using System.Windows;

namespace PhoneUnlock.Setup;

public partial class App : Application
{
    private static readonly string StartupLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhoneUnlock",
        "setup-startup.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            LogStartupFailure(exception);
            MessageBox.Show(
                $"Phone Unlock 설정창을 열지 못했습니다.\n\n{exception.Message}\n\n로그: {StartupLogPath}",
                "Phone Unlock",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void LogStartupFailure(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StartupLogPath)!);
            File.AppendAllText(
                StartupLogPath,
                $"[{DateTimeOffset.Now:O}] setup startup failure{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            // A diagnostic failure must not mask the original startup error.
        }
    }
}
