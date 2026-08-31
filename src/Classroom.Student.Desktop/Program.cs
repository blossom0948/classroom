using Blossom.Classroom.Student.Desktop.Configuration;
using Blossom.Classroom.Student.Desktop.Networking;
using Blossom.Classroom.Student.Desktop.Status;
using Blossom.Classroom.Student.Desktop.Ui;
using Blossom.Classroom.Student.Desktop;

if (args.Any(argument => string.Equals(argument, "--classroom-watchdog", StringComparison.OrdinalIgnoreCase)))
{
    await StudentDesktopWatchdog.RunAsync();
    return;
}

ApplicationConfiguration.Initialize();
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
var client = new DesktopPipeClient(options, statusProvider, message => Console.Error.WriteLine(message));
using var form = new StudentDesktopForm(options, statusProvider, client.VerifyExitPinAsync);

form.FormClosed += (_, _) => cancellation.Cancel();
var connectionTask = client.RunAsync(
    form.ApplyCommandAsync,
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
