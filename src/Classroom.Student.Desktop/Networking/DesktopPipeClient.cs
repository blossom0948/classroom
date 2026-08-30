using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Blossom.Classroom.Core.Desktop;
using Blossom.Classroom.Core.Serialization;
using Blossom.Classroom.Protocol.Models;
using Blossom.Classroom.Student.Desktop.Commands;
using Blossom.Classroom.Student.Desktop.Configuration;
using Blossom.Classroom.Student.Desktop.Status;

namespace Blossom.Classroom.Student.Desktop.Networking;

public sealed class DesktopPipeClient(
    StudentDesktopOptions options,
    WindowsStudentStatusProvider statusProvider,
    Action<string> log)
{
    private const int MaxMessageBytes = StudentDesktopIpc.MaxMessageBytes;

    public async Task RunAsync(
        Func<CommandRequest, Task<DesktopCommandApplyResult>> commandHandler,
        Action<DesktopStatusData> statusHandler,
        Action<bool> connectionHandler,
        Action<bool, Guid> serverConnectionHandler,
        CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(
                    commandHandler,
                    statusHandler,
                    connectionHandler,
                    serverConnectionHandler,
                    cancellationToken);
                retryDelay = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or TimeoutException
                or InvalidDataException
                or JsonException)
            {
                log($"Student Service IPC connection ended: {exception.Message}");
            }

            connectionHandler(false);
            serverConnectionHandler(false, Guid.Empty);
            await Task.Delay(retryDelay, cancellationToken);
            retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 15));
        }
    }

    private async Task RunConnectionAsync(
        Func<CommandRequest, Task<DesktopCommandApplyResult>> commandHandler,
        Action<DesktopStatusData> statusHandler,
        Action<bool> connectionHandler,
        Action<bool, Guid> serverConnectionHandler,
        CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            StudentDesktopIpc.GetPipeName(options.DeviceId),
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5_000, cancellationToken);
        using var reader = new StreamReader(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 8 * 1024,
            leaveOpen: true);
        using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 8 * 1024,
            leaveOpen: true)
        {
            AutoFlush = true
        };
        using var writeGate = new SemaphoreSlim(1, 1);

        await WriteAsync(
            writer,
            writeGate,
            new DesktopHello("hello", options.IpcToken));
        var replyJson = await ReadLineAsync(reader, cancellationToken);
        if (replyJson is null)
        {
            throw new IOException("Student Service closed the IPC connection during handshake.");
        }

        var reply = ClassroomJson.Deserialize<DesktopReply>(replyJson);
        if (!string.Equals(reply.Kind, "hello-accepted", StringComparison.Ordinal))
        {
            throw new InvalidDataException(reply.Message);
        }

        connectionHandler(true);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var statusTask = SendStatusLoopAsync(writer, writeGate, statusHandler, lifetime.Token);
        try
        {
            await ReceiveLoopAsync(
                reader,
                writer,
                writeGate,
                commandHandler,
                serverConnectionHandler,
                lifetime.Token);
        }
        finally
        {
            lifetime.Cancel();
            try
            {
                await statusTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task SendStatusLoopAsync(
        StreamWriter writer,
        SemaphoreSlim writeGate,
        Action<DesktopStatusData> statusHandler,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var status = statusProvider.GetCurrent();
            statusHandler(status);
            await WriteAsync(
                writer,
                writeGate,
                new DesktopStatusMessage(
                    "status",
                    status.Activity,
                    status.BatteryPercent,
                    status.NetworkStatus,
                    status.PolicyApplied,
                    status.ScreenFrame,
                    status.ScreenSharingEnabled));
            await Task.Delay(options.StatusInterval, cancellationToken);
        }
    }

    private static async Task ReceiveLoopAsync(
        StreamReader reader,
        StreamWriter writer,
        SemaphoreSlim writeGate,
        Func<CommandRequest, Task<DesktopCommandApplyResult>> commandHandler,
        Action<bool, Guid> serverConnectionHandler,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var json = await ReadLineAsync(reader, cancellationToken);
            if (json is null)
            {
                throw new IOException("Student Service closed the IPC connection.");
            }

            using var document = JsonDocument.Parse(json);
            var kind = document.RootElement.TryGetProperty("kind", out var kindElement)
                ? kindElement.GetString()
                : null;
            if (string.Equals(kind, "server-status", StringComparison.Ordinal))
            {
                var serverStatus = ClassroomJson.Deserialize<DesktopServerStatusMessage>(json);
                serverConnectionHandler(serverStatus.Connected, serverStatus.SessionId);
                continue;
            }

            if (!string.Equals(kind, "command", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Unexpected Student Service IPC message.");
            }

            var message = ClassroomJson.Deserialize<DesktopCommandMessage>(json);
            var result = await commandHandler(message.Command);
            await WriteAsync(
                writer,
                writeGate,
                new DesktopCommandResult(
                    "command-result",
                    message.RequestId,
                    result.Success,
                    result.Code,
                    result.Message));
        }
    }

    private static async Task WriteAsync(
        StreamWriter writer,
        SemaphoreSlim writeGate,
        object message)
    {
        var json = ClassroomJson.Serialize(message);
        if (Encoding.UTF8.GetByteCount(json) > MaxMessageBytes)
        {
            throw new InvalidDataException("Student Desktop IPC message exceeded the size limit.");
        }

        await writeGate.WaitAsync();
        try
        {
            await writer.WriteLineAsync(json);
            await writer.FlushAsync();
        }
        finally
        {
            writeGate.Release();
        }
    }

    private static async Task<string?> ReadLineAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken);
        if (line is not null && Encoding.UTF8.GetByteCount(line) > MaxMessageBytes)
        {
            throw new InvalidDataException("Student Desktop IPC message exceeded the size limit.");
        }

        return line;
    }

    private sealed record DesktopHello(string Kind, string Token);

    private sealed record DesktopReply(string Kind, string Message);

    private sealed record DesktopStatusMessage(
        string Kind,
        ActivitySnapshot? Activity,
        int? BatteryPercent,
        string? NetworkStatus,
        bool PolicyApplied,
        ScreenFrame? ScreenFrame,
        bool ScreenSharingEnabled);

    private sealed record DesktopServerStatusMessage(
        string Kind,
        bool Connected,
        Guid SessionId);

    private sealed record DesktopCommandMessage(
        string Kind,
        Guid RequestId,
        CommandRequest Command);

    private sealed record DesktopCommandResult(
        string Kind,
        Guid RequestId,
        bool Success,
        string Code,
        string Message);
}
