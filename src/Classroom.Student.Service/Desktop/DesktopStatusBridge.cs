using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Blossom.Classroom.Core.Desktop;
using Blossom.Classroom.Core.Security;
using Blossom.Classroom.Core.Serialization;
using Blossom.Classroom.Protocol.Models;
using Blossom.Classroom.Protocol.Validation;
using Blossom.Classroom.Student.Service.Commands;
using Blossom.Classroom.Student.Service.Configuration;
using Blossom.Classroom.Student.Service.Status;

namespace Blossom.Classroom.Student.Service.Desktop;

public sealed class DesktopStatusBridge(
    StudentAgentOptions options,
    ILogger<DesktopStatusBridge> logger) : IStudentStatusSource, IStudentCommandSink, IAsyncDisposable
{
    public const int MaxIpcMessageBytes = StudentDesktopIpc.MaxMessageBytes;

    private readonly object gate = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<DesktopCommandResult>> pending = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly string ipcTokenHash = string.IsNullOrWhiteSpace(options.IpcToken)
        ? string.Empty
        : TokenSecurity.HashToken(options.IpcToken);
    private StudentStatusData latest = StudentStatusData.Empty;
    private Task? acceptTask;
    private NamedPipeServerStream? connectedPipe;
    private StreamWriter? connectedWriter;
    private bool serverConnected;
    private Guid serverSessionId;
    private int disposed;

    public ValueTask<StudentStatusData> GetAsync(CancellationToken cancellationToken)
    {
        EnsureStarted();
        lock (gate)
        {
            // The service heartbeat can continue while the visible desktop
            // process is gone. Never report the last desktop activity as if
            // it were current; the server will mark this device as needing
            // attention until the desktop reconnects.
            var desktopConnected = connectedPipe is not null
                && connectedWriter is not null
                && connectedPipe.IsConnected;
            return ValueTask.FromResult(desktopConnected ? latest : StudentStatusData.Empty);
        }
    }

    public async Task<CommandApplyResult> ApplyAsync(
        CommandRequest command,
        CancellationToken cancellationToken)
    {
        EnsureStarted();
        ProtocolValidation.ValidateCommand(command);

        lock (gate)
        {
            if (connectedPipe is null || connectedWriter is null || !connectedPipe.IsConnected)
            {
                return DesktopDisconnectedCommandSink.NotConnectedResult;
            }
        }

        var completion = new TaskCompletionSource<DesktopCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(command.RequestId, completion))
        {
            return new CommandApplyResult(false, "DUPLICATE_REQUEST", "The command is already being applied.");
        }

        try
        {
            var sent = await SendAsync(
                new DesktopCommandMessage("command", command.RequestId, command),
                cancellationToken);
            if (!sent)
            {
                return DesktopDisconnectedCommandSink.NotConnectedResult;
            }

            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            return new CommandApplyResult(result.Success, result.Code, result.Message);
        }
        catch (TimeoutException)
        {
            return new CommandApplyResult(false, "DESKTOP_COMMAND_TIMEOUT", "Student Desktop did not respond in time.");
        }
        catch (IOException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new CommandApplyResult(false, "DESKTOP_DISCONNECTED", exception.Message);
        }
        finally
        {
            pending.TryRemove(command.RequestId, out _);
        }
    }

    public async Task UpdateServerConnectionAsync(
        bool connected,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            serverConnected = connected;
            serverSessionId = connected ? sessionId : Guid.Empty;
        }

        try
        {
            await SendAsync(
                new DesktopServerStatusMessage(
                    "server-status",
                    connected,
                    connected ? sessionId : Guid.Empty),
                cancellationToken);
        }
        catch (IOException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Could not publish server state to Student Desktop: {Message}", exception.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lifetime.Cancel();
        lock (gate)
        {
            connectedPipe?.Dispose();
            connectedPipe = null;
            connectedWriter = null;
        }

        if (acceptTask is not null)
        {
            try
            {
                await acceptTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        FailPending(new IOException("Student Desktop bridge stopped."));
        writeGate.Dispose();
        lifetime.Dispose();
    }

    private void EnsureStarted()
    {
        if (string.IsNullOrWhiteSpace(options.IpcToken))
        {
            return;
        }

        lock (gate)
        {
            acceptTask ??= Task.Run(() => AcceptLoopAsync(lifetime.Token));
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var pipeName = StudentDesktopIpc.GetPipeName(options.DeviceId);
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe(pipeName);
                await pipe.WaitForConnectionAsync(cancellationToken);
                lock (gate)
                {
                    connectedPipe = pipe;
                }

                await RunConnectionAsync(pipe, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or JsonException
                or ProtocolValidationException)
            {
                logger.LogWarning("Student Desktop IPC connection ended: {Message}", exception.Message);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
            finally
            {
                lock (gate)
                {
                    if (ReferenceEquals(connectedPipe, pipe))
                    {
                        connectedPipe = null;
                        connectedWriter = null;
                    }
                }

                FailPending(new IOException("Student Desktop IPC connection ended."));
                pipe?.Dispose();
            }
        }
    }

    private static NamedPipeServerStream CreatePipe(string pipeName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                MaxIpcMessageBytes,
                MaxIpcMessageBytes);
        }

        // The service runs as LocalSystem while the visible student desktop
        // runs as the logged-in student. Grant the authenticated desktop user
        // access to this pipe; the IPC token handshake remains mandatory.
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            MaxIpcMessageBytes,
            MaxIpcMessageBytes,
            security);
    }

    private async Task RunConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
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

        var helloJson = await ReadLineAsync(reader, cancellationToken);
        if (helloJson is null)
        {
            return;
        }

        var hello = ClassroomJson.Deserialize<DesktopHello>(helloJson);
        if (!string.Equals(hello.Kind, "hello", StringComparison.Ordinal)
            || !TokenSecurity.VerifyToken(hello.Token, ipcTokenHash))
        {
            await WriteAsync(writer, new DesktopReply("hello-rejected", "Invalid desktop IPC credentials."));
            throw new InvalidDataException("Student Desktop IPC authentication failed.");
        }

        lock (gate)
        {
            connectedWriter = writer;
        }
        await WriteAsync(writer, new DesktopReply("hello-accepted", "Student Desktop connected."));
        bool currentServerConnected;
        Guid currentServerSessionId;
        lock (gate)
        {
            currentServerConnected = serverConnected;
            currentServerSessionId = serverSessionId;
        }
        await WriteAsync(
            writer,
            new DesktopServerStatusMessage(
                "server-status",
                currentServerConnected,
                currentServerSessionId));

        while (!cancellationToken.IsCancellationRequested)
        {
            var json = await ReadLineAsync(reader, cancellationToken);
            if (json is null)
            {
                return;
            }

            using var document = JsonDocument.Parse(json);
            var kind = document.RootElement.TryGetProperty("kind", out var kindElement)
                ? kindElement.GetString()
                : null;
            switch (kind)
            {
                case "status":
                    var status = ClassroomJson.Deserialize<DesktopStatusMessage>(json);
                    if (status.Activity is not null)
                    {
                        ProtocolValidation.ValidateActivity(status.Activity);
                    }

                    if (status.BatteryPercent is < 0 or > 100)
                    {
                        throw new ProtocolValidationException("Desktop battery percent is invalid.");
                    }

                    if (status.ScreenFrame is not null)
                    {
                        ProtocolValidation.ValidateScreenFrame(status.ScreenFrame);
                    }

                    lock (gate)
                    {
                        latest = new StudentStatusData(
                            status.Activity,
                            status.BatteryPercent,
                            status.NetworkStatus,
                            status.PolicyApplied,
                            status.ScreenFrame,
                            status.ScreenSharingEnabled);
                    }

                    break;
                case "command-result":
                    var result = ClassroomJson.Deserialize<DesktopCommandResult>(json);
                    if (pending.TryGetValue(result.RequestId, out var completion))
                    {
                        completion.TrySetResult(result);
                    }

                    break;
                default:
                    throw new ProtocolValidationException("Unknown Student Desktop IPC message kind.");
            }
        }
    }

    private async Task<bool> SendAsync(object message, CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            StreamWriter? writer;
            lock (gate)
            {
                writer = connectedWriter;
            }

            if (writer is null)
            {
                return false;
            }

            await WriteAsync(writer, message);
            return true;
        }
        catch (ObjectDisposedException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            writeGate.Release();
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (var completion in pending.Values)
        {
            completion.TrySetException(exception);
        }
    }

    private static async Task WriteAsync(StreamWriter writer, object message)
    {
        var json = ClassroomJson.Serialize(message);
        if (Encoding.UTF8.GetByteCount(json) > MaxIpcMessageBytes)
        {
            throw new ProtocolValidationException("Student Desktop IPC message exceeded the size limit.");
        }

        await writer.WriteLineAsync(json);
        await writer.FlushAsync();
    }

    private static async Task<string?> ReadLineAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken);
        if (line is not null && Encoding.UTF8.GetByteCount(line) > MaxIpcMessageBytes)
        {
            throw new ProtocolValidationException("Student Desktop IPC message exceeded the size limit.");
        }

        return line;
    }

    private sealed record DesktopHello(string Kind, string Token);

    private sealed record DesktopReply(string Kind, string Message);

    private sealed record DesktopCommandMessage(
        string Kind,
        Guid RequestId,
        CommandRequest Command);

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

    private sealed record DesktopCommandResult(
        string Kind,
        Guid RequestId,
        bool Success,
        string Code,
        string Message);
}
