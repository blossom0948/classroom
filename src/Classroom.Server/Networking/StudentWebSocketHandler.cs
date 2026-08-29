using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Blossom.Classroom.Protocol;
using Blossom.Classroom.Protocol.Models;
using Blossom.Classroom.Protocol.Security;
using Blossom.Classroom.Protocol.Serialization;
using Blossom.Classroom.Protocol.Validation;
using Blossom.Classroom.Server.Storage;

namespace Blossom.Classroom.Server.Networking;

public sealed class StudentWebSocketHandler(
    ClassroomStore store,
    ILogger<StudentWebSocketHandler> logger)
{
    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!Guid.TryParse(context.Request.Query["deviceId"], out var deviceId)
            || !store.TryAuthenticateDevice(
                deviceId,
                GetBearerToken(context.Request),
                out var identity)
            || identity is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var sendGate = new SemaphoreSlim(1, 1);
        Guid sessionId = Guid.Empty;
        try
        {
            var helloJson = await ReceiveTextAsync(socket, context.RequestAborted);
            if (helloJson is null)
            {
                return;
            }

            var hello = ProtocolCodec.Deserialize<DeviceHello>(helloJson);
            if (!string.Equals(hello.Type, ProtocolConstants.DeviceHello, StringComparison.Ordinal))
            {
                await SendErrorAsync(socket, sendGate, "HELLO_REQUIRED", "The first message must be DEVICE_HELLO.", context.RequestAborted);
                return;
            }

            ProtocolValidation.ValidateHello(hello.Payload);
            if (hello.Payload.DeviceId != deviceId)
            {
                await SendErrorAsync(socket, sendGate, "DEVICE_MISMATCH", "Hello device ID does not match the authenticated device.", context.RequestAborted);
                return;
            }

            sessionId = hello.Payload.SessionId;
            if (!store.TryOpenConnection(identity, sessionId, out var code, out var message))
            {
                await SendErrorAsync(socket, sendGate, code, message, context.RequestAborted);
                return;
            }

            await SendEnvelopeAsync(
                socket,
                sendGate,
                ProtocolConstants.DeviceSessionAccepted,
                new DeviceSessionAccepted(
                    identity.DeviceId,
                    sessionId,
                    DateTimeOffset.UtcNow),
                context.RequestAborted);

            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var receiveTask = ReceiveLoopAsync(socket, identity, lifetime.Token, sendGate);
            var commandTask = SendCommandLoopAsync(socket, identity.DeviceId, lifetime.Token, sendGate);
            await Task.WhenAny(receiveTask, commandTask);
            lifetime.Cancel();
            await IgnoreCancellationAsync(receiveTask);
            await IgnoreCancellationAsync(commandTask);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (WebSocketException exception)
        {
            logger.LogInformation(exception, "Student device {DeviceId} websocket closed.", deviceId);
        }
        catch (ProtocolValidationException exception)
        {
            logger.LogWarning("Student device {DeviceId} sent invalid protocol data: {Message}", deviceId, exception.Message);
            if (socket.State == WebSocketState.Open)
            {
                await SendErrorAsync(socket, sendGate, "INVALID_PROTOCOL", exception.Message, CancellationToken.None);
            }
        }
        catch (JsonException exception)
        {
            logger.LogWarning("Student device {DeviceId} sent malformed JSON: {Message}", deviceId, exception.Message);
        }
        finally
        {
            if (sessionId != Guid.Empty)
            {
                store.CloseConnection(deviceId, sessionId);
            }

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Connection closed",
                        CancellationToken.None);
                }
                catch (WebSocketException)
                {
                }
            }
        }
    }

    private async Task ReceiveLoopAsync(
        WebSocket socket,
        AuthenticatedDevice identity,
        CancellationToken cancellationToken,
        SemaphoreSlim sendGate)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var json = await ReceiveTextAsync(socket, cancellationToken);
            if (json is null)
            {
                return;
            }

            var type = ReadMessageType(json);
            switch (type)
            {
                case ProtocolConstants.DeviceHeartbeat:
                {
                    var heartbeat = ProtocolCodec.Deserialize<DeviceHeartbeat>(json);
                    var result = store.RecordHeartbeat(identity, heartbeat.Payload);
                    if (!result.Succeeded)
                    {
                        await SendErrorAsync(socket, sendGate, result.Code, result.Message, cancellationToken);
                    }

                    break;
                }
                case ProtocolConstants.CommandAck:
                {
                    var acknowledgment = ProtocolCodec.Deserialize<CommandAck>(json);
                    var result = store.RecordCommandAck(identity, acknowledgment.Payload);
                    if (!result.Succeeded)
                    {
                        await SendErrorAsync(socket, sendGate, result.Code, result.Message, cancellationToken);
                    }

                    break;
                }
                case ProtocolConstants.CommandResult:
                {
                    var resultEnvelope = ProtocolCodec.Deserialize<CommandResult>(json);
                    var result = store.RecordCommandResult(identity, resultEnvelope.Payload);
                    if (!result.Succeeded)
                    {
                        await SendErrorAsync(socket, sendGate, result.Code, result.Message, cancellationToken);
                    }

                    break;
                }
                default:
                    await SendErrorAsync(
                        socket,
                        sendGate,
                        "MESSAGE_NOT_ALLOWED",
                        "This message type is not allowed from a student device.",
                        cancellationToken);
                    break;
            }
        }
    }

    private async Task SendCommandLoopAsync(
        WebSocket socket,
        Guid deviceId,
        CancellationToken cancellationToken,
        SemaphoreSlim sendGate)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var command = await store.WaitForCommandAsync(deviceId, cancellationToken);
            await SendEnvelopeAsync(
                socket,
                sendGate,
                ProtocolConstants.CommandRequest,
                command,
                cancellationToken);
        }
    }

    private static string? GetBearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.Ordinal)
            ? header["Bearer ".Length..].Trim()
            : null;
    }

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
        WebSocket socket,
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

    private static Task SendErrorAsync(
        WebSocket socket,
        SemaphoreSlim sendGate,
        string code,
        string message,
        CancellationToken cancellationToken) =>
        SendEnvelopeAsync(
            socket,
            sendGate,
            ProtocolConstants.Error,
            new ErrorMessage(code, message),
            cancellationToken);

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
    }
}
