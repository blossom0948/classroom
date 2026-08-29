using Blossom.Classroom.Student.Desktop.Configuration;
using Blossom.Classroom.Student.Desktop.Networking;
using Blossom.Classroom.Student.Desktop.Status;
using Blossom.Classroom.Student.Desktop.Ui;

ApplicationConfiguration.Initialize();
var options = StudentDesktopOptions.FromEnvironment();
var statusProvider = new WindowsStudentStatusProvider();
using var cancellation = new CancellationTokenSource();
var client = new DesktopPipeClient(options, statusProvider, message => Console.Error.WriteLine(message));
using var form = new StudentDesktopForm(options, statusProvider);

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
