using Blossom.Classroom.Student.Desktop.Configuration;
using Blossom.Classroom.Student.Desktop.Networking;
using Blossom.Classroom.Student.Desktop.Status;
using Blossom.Classroom.Student.Desktop.Ui;
using Blossom.Classroom.Student.Desktop;

if (args.Any(argument => string.Equals(argument, "--classroom-watchdog", StringComparison.OrdinalIgnoreCase)))
{
    while (true)
    {
        try
        {
            await StudentDesktopWatchdog.RunAsync();
            break;
        }
        catch (Exception exception)
        {
            StudentDesktopDiagnostics.Log("Student Desktop watchdog stopped unexpectedly; restarting supervision.", exception);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            catch (Exception delayException)
            {
                StudentDesktopDiagnostics.Log("Student Desktop watchdog retry delay failed.", delayException);
            }
        }
    }

    return;
}

ApplicationConfiguration.Initialize();
Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
Application.ThreadException += (_, eventArgs) =>
    StudentDesktopDiagnostics.Log("Student Desktop UI exception was contained.", eventArgs.Exception);
AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
    StudentDesktopDiagnostics.Log(
        "Student Desktop unhandled exception.",
        eventArgs.ExceptionObject as Exception);
TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
{
    StudentDesktopDiagnostics.Log("Student Desktop unobserved task exception.", eventArgs.Exception);
    eventArgs.SetObserved();
};

var startInBackground = args.Any(argument => string.Equals(argument, "--classroom-background", StringComparison.OrdinalIgnoreCase));
var options = StudentDesktopOptions.FromEnvironment();
using var singleInstance = new Mutex(
    initiallyOwned: true,
    $"Local\\BlossomClassroomStudent-{options.DeviceId:N}",
    out var ownsSingleInstance);
if (!ownsSingleInstance)
{
    return;
}

var statusProvider = new WindowsStudentStatusProvider();
using var cancellation = new CancellationTokenSource();
var client = new DesktopPipeClient(options, statusProvider, message => StudentDesktopDiagnostics.Log(message));
using var form = new StudentDesktopForm(
    options,
    statusProvider,
    client.VerifyExitPinAsync,
    client.CheckForUpdateAsync,
    startInBackground);

form.FormClosed += (_, _) => cancellation.Cancel();
var connectionTask = client.RunAsync(
    form.ApplyCommandAsync,
    form.ApplyRemoteAssistInputAsync,
    form.ShowStatus,
    form.SetConnectionState,
    form.SetServerConnectionState,
    cancellation.Token);
Application.Run(form);
cancellation.Cancel();
try
{
    connectionTask.GetAwaiter().GetResult();
}
catch (OperationCanceledException)
{
}
catch (Exception exception)
{
    // The desktop window has already closed at this point. The watchdog will
    // relaunch it unless an administrator-authorized exit marker is present.
    StudentDesktopDiagnostics.Log("Student Desktop connection loop stopped.", exception);
}
