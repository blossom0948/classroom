using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Blossom.Classroom.Protocol;
using Blossom.Classroom.Protocol.Models;
using Blossom.Classroom.Protocol.Serialization;
using Blossom.Classroom.Protocol.Validation;
using Blossom.Classroom.Student.Service.Commands;
using Blossom.Classroom.Student.Service.Configuration;
using Blossom.Classroom.Student.Service.Status;

namespace Blossom.Classroom.Student.Service.Networking;

public sealed class ClassroomServerClient(
    StudentAgentOptions options,
    IStudentStatusSource statusSource,
    IStudentCommandSink commandSink,
    ILogger<ClassroomServerClient> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(cancellationToken);
                retryDelay = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is WebSocketException or IOException or ProtocolValidationException)
            {
                logger.LogWarning(
                    "Classroom server connection ended: {Message}. Retrying in {RetrySeconds}s.",
                    exception.Message,
                    retryDelay.TotalSeconds);
            }

            await Task.Delay(retryDelay, cancellationToken);
            retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
        }
    }

    private async Task RunConnectionAsync(CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {options.DeviceToken}");
        var endpoint = BuildStudentEndpoint(options.ServerUri, options.DeviceId);
        logger.LogInformation("Connecting student device {DeviceId} to {Endpoint}.", options.DeviceId, endpoint);
        await socket.ConnectAsync(endpoint, cancellationToken);

        using var sendGate = new SemaphoreSlim(1, 1);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await SendEnvelopeAsync(
            socket,
            sendGate,
            ProtocolConstants.DeviceHello,
            new DeviceHello(options.DeviceId, options.SessionId, options.AgentVersion),
            lifetime.Token);

        var heartbeatTask = SendHeartbeatLoopAsync(socket, sendGate, lifetime.Token);
        try
        {
            await ReceiveLoopAsync(socket, sendGate, lifetime.Token);
        }
        finally
        {
            lifetime.Cancel();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task SendHeartbeatLoopAsync(
        ClientWebSocket socket,
        SemaphoreSlim sendGate,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var status = await statusSource.GetAsync(cancellationToken);
            await SendEnvelopeAsync(
                socket,
                sendGate,
                ProtocolConstants.DeviceHeartbeat,
                new DeviceHeartbeat(
                    options.DeviceId,
                    options.SessionId,
                    options.AgentVersion,
                    DateTimeOffset.UtcNow,
                    status.Activity,
                    status.BatteryPercent,
                    status.NetworkStatus,
                    status.PolicyApplied),
                cancellationToken);
            await Task.Delay(options.HeartbeatInterval, cancellationToken);
        }
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        SemaphoreSlim sendGate,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var json = await ReceiveTextAsync(socket, cancellationToken);
            if (json is null)
            {
                throw new IOException("Classroom server closed the student connection.");
            }

            var type = ReadMessageType(json);
            if (type == ProtocolConstants.CommandRequest)
            {
                var commandEnvelope = ProtocolCodec.Deserialize<CommandRequest>(json);
                ProtocolValidation.ValidateCommand(commandEnvelope.Payload);
                await ApplyCommandAsync(socket, sendGate, commandEnvelope.Payload, cancellationToken);
                continue;
            }

            if (type == ProtocolConstants.Error)
            {
                var error = ProtocolCodec.Deserialize<ErrorMessage>(json);
                logger.LogWarning("Classroom server rejected a student message: {Code} {Message}.", error.Payload.Code, error.Payload.Message);
                continue;
            }

            if (type == ProtocolConstants.DeviceSessionAccepted)
            {
                continue;
            }

            logger.LogWarning("Ignoring unexpected server message type {Type}.", type);
        }
    }

    private async Task ApplyCommandAsync(
        ClientWebSocket socket,
        SemaphoreSlim sendGate,
        CommandRequest command,
        CancellationToken cancellationToken)
    {
        CommandApplyResult applied;
        try
        {
            applied = await commandSink.ApplyAsync(command, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            applied = new CommandApplyResult(false, "COMMAND_APPLY_FAILED", exception.Message);
        }

        await SendEnvelopeAsync(
            socket,
            sendGate,
            ProtocolConstants.CommandAck,
            new CommandAck(
                command.RequestId,
                options.DeviceId,
                applied.Success,
                applied.Success ? null : applied.Message,
                DateTimeOffset.UtcNow),
            cancellationToken);
        await SendEnvelopeAsync(
            socket,
            sendGate,
            ProtocolConstants.CommandResult,
            new CommandResult(
                command.RequestId,
                options.DeviceId,
                applied.Success,
                applied.Code,
                applied.Message,
                DateTimeOffset.UtcNow),
            cancellationToken);
    }

    private static Uri BuildStudentEndpoint(Uri serverUri, Guid deviceId)
    {
        var builder = new UriBuilder(serverUri)
        {
            Scheme = serverUri.Scheme is "http" or "https"
                ? serverUri.Scheme switch
                {
                    "http" => "ws",
                    _ => "wss"
                }
                : serverUri.Scheme,
            Path = CombinePath(serverUri.AbsolutePath, "/ws/student"),
            Query = $"deviceId={Uri.EscapeDataString(deviceId.ToString("D"))}"
        };
        return builder.Uri;
    }

    private static string CombinePath(string basePath, string suffix) =>
        $"{basePath.TrimEnd('/')}/{suffix.TrimStart('/')}";

    private static string ReadMessageType(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("type", out var type)
            ? type.GetString() ?? string.Empty
            : string.Empty;
    }

    private static async Task<string?> ReceiveTextAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var buffer = new byte[8 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new ProtocolValidationException("Only UTF-8 text websocket messages are supported.");
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > ProtocolConstants.MaxMessageBytes)
            {
                throw new ProtocolValidationException("Protocol message exceeded the size limit.");
            }

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.ToArray());
            }
        }
    }

    private static async Task SendEnvelopeAsync<TPayload>(
        ClientWebSocket socket,
        SemaphoreSlim sendGate,
        string type,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        var envelope = ProtocolEnvelope<TPayload>.Create(type, payload);
        var json = ProtocolCodec.Serialize(envelope);
        var bytes = Encoding.UTF8.GetBytes(json);
        await sendGate.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            sendGate.Release();
        }
    }
}

