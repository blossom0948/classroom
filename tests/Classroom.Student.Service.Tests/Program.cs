using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Blossom.Classroom.Core.Desktop;
using Blossom.Classroom.Core.Serialization;
using Blossom.Classroom.Protocol.Models;
using Blossom.Classroom.Student.Service;
using Blossom.Classroom.Student.Service.Commands;
using Blossom.Classroom.Student.Service.Configuration;
using Blossom.Classroom.Student.Service.Desktop;

var deviceId = Guid.NewGuid();
var sessionId = Guid.NewGuid();
var options = new StudentAgentOptions(
    new Uri("ws://127.0.0.1:48240"),
    deviceId,
    sessionId,
    "device-token",
    "desktop-ipc-token-123456",
    "0.1.0-dev",
    TimeSpan.FromSeconds(5));

using var updateWorker = new StudentUpdateWorker(
    options,
    Microsoft.Extensions.Logging.Abstractions.NullLogger<StudentUpdateWorker>.Instance);
await using var bridge = new DesktopStatusBridge(
    options,
    Microsoft.Extensions.Logging.Abstractions.NullLogger<DesktopStatusBridge>.Instance,
    updateWorker);
_ = await bridge.GetAsync(CancellationToken.None);

using var pipe = new NamedPipeClientStream(
    ".",
    StudentDesktopIpc.GetPipeName(deviceId),
    PipeDirection.InOut,
    PipeOptions.Asynchronous);
await pipe.ConnectAsync(3_000);
using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 8 * 1024, true);
using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 8 * 1024, true) { AutoFlush = true };

await WriteAsync(writer, new { kind = "hello", token = options.IpcToken });
var accepted = await reader.ReadLineAsync() ?? throw new InvalidOperationException("IPC handshake response was empty.");
Assert(accepted.Contains("hello-accepted", StringComparison.Ordinal), "Student Desktop handshake was rejected.");
var initialServerState = await reader.ReadLineAsync()
    ?? throw new InvalidOperationException("Initial server state was empty.");
Assert(initialServerState.Contains("server-status", StringComparison.Ordinal),
    "Student Desktop did not receive the initial server state.");

await bridge.UpdateServerConnectionAsync(true, sessionId, CancellationToken.None);
var connectedServerState = await reader.ReadLineAsync()
    ?? throw new InvalidOperationException("Connected server state was empty.");
Assert(connectedServerState.Contains(sessionId.ToString(), StringComparison.OrdinalIgnoreCase),
    "Student Desktop did not receive the active class session.");

var activity = new ActivitySnapshot("Chrome", "chrome.exe", "classroom.google.com", null, DateTimeOffset.UtcNow);
await WriteAsync(writer, new
{
    kind = "status",
    activity,
    batteryPercent = 84,
    networkStatus = "wifi",
    policyApplied = false,
    needsHelp = true
});
await Task.Delay(100);
var status = await bridge.GetAsync(CancellationToken.None);
Assert(status.Activity?.BrowserDomain == "classroom.google.com", "Desktop activity was not forwarded to the service.");
Assert(status.BatteryPercent == 84 && status.NetworkStatus == "wifi", "Desktop status values were not forwarded.");
Assert(status.NeedsHelp, "Desktop help request was not forwarded to the service.");

var updateRequestId = Guid.NewGuid();
await WriteAsync(writer, new
{
    kind = "update-check",
    requestId = updateRequestId
});
var updateJson = await reader.ReadLineAsync()
    ?? throw new InvalidOperationException("Student update response was not sent.");
using var updateDocument = JsonDocument.Parse(updateJson);
Assert(updateDocument.RootElement.GetProperty("kind").GetString() == "update-result",
    "IPC update response type was invalid.");
Assert(updateDocument.RootElement.GetProperty("requestId").GetGuid() == updateRequestId,
    "IPC update response was not correlated.");
Assert(updateDocument.RootElement.GetProperty("code").GetString() == "INSTALL_ROOT_NOT_FOUND",
    "Student update worker did not report its test installation boundary.");

var command = new CommandRequest(
    Guid.NewGuid(),
    sessionId,
    new[] { deviceId },
    ClassroomCommandKind.Message,
    "과제를 확인해 주세요.",
    DisplaySeconds: 5);
var applyTask = bridge.ApplyAsync(command, CancellationToken.None);
var commandJson = await reader.ReadLineAsync() ?? throw new InvalidOperationException("Desktop command was not sent.");
using var commandDocument = JsonDocument.Parse(commandJson);
Assert(commandDocument.RootElement.GetProperty("kind").GetString() == "command", "IPC command type was invalid.");
var requestId = commandDocument.RootElement.GetProperty("requestId").GetGuid();
await WriteAsync(writer, new
{
    kind = "command-result",
    requestId,
    success = true,
    code = "MESSAGE_DISPLAYED",
    message = "Message displayed."
});
var applied = await applyTask;
Assert(applied.Success && applied.Code == "MESSAGE_DISPLAYED", "Desktop command result was not correlated.");

Console.WriteLine("PASS named-pipe desktop status and command bridge");
return 0;

static async Task WriteAsync(StreamWriter writer, object value)
{
    await writer.WriteLineAsync(ClassroomJson.Serialize(value));
    await writer.FlushAsync();
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
