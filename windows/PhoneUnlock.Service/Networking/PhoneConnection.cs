using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PhoneUnlock.Core.Protocol;
using PhoneUnlock.Service.Models;

namespace PhoneUnlock.Service.Networking;

public sealed class PhoneConnection(
    PairedPhoneRecord phone,
    WebSocket socket,
    ILogger<PhoneConnection> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<string>> pending = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);

    public string PhoneId => phone.PhoneId;
    public bool IsOpen => socket.State == WebSocketState.Open;

    public async Task<string> SendAuthRequestAsync(
        Guid requestId,
        string requestJson,
        CancellationToken cancellationToken)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("Phone WebSocket is not open.");
        }

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(requestId, completion))
        {
            throw new InvalidOperationException("This request is already pending.");
        }

        try
        {
            await SendTextAsync(requestJson, cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            pending.TryRemove(requestId, out _);
        }
    }

    public async Task RunReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (!cancellationToken.IsCancellationRequested && IsOpen)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closed", CancellationToken.None);
                        return;
                    }

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        await CloseAsync(WebSocketCloseStatus.InvalidMessageType, "Text messages only", CancellationToken.None);
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (message.Length > 64 * 1024)
                    {
                        await CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large", CancellationToken.None);
                        return;
                    }
                }
                while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
                RouteResponse(json);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException exception)
        {
            logger.LogInformation("Phone {PhoneId} disconnected: {Message}", PhoneId, exception.Message);
        }
        finally
        {
            foreach (var completion in pending.Values)
            {
                completion.TrySetException(new IOException("Phone disconnected before responding."));
            }

            pending.Clear();
        }
    }

    private void RouteResponse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("version").GetInt32() != ProtocolConstants.Version)
            {
                return;
            }

            var type = root.GetProperty("type").GetString();
            if (type is not (ProtocolConstants.AuthApproved or ProtocolConstants.AuthDenied or ProtocolConstants.AuthExpired))
            {
                return;
            }

            var requestId = root.GetProperty("payload").GetProperty("requestId").GetGuid();
            if (pending.TryGetValue(requestId, out var completion))
            {
                completion.TrySetResult(json);
            }
        }
        catch (JsonException)
        {
            logger.LogWarning("Rejected malformed message from phone {PhoneId}", PhoneId);
        }
    }

    private async Task SendTextAsync(string value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
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

    private async Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken cancellationToken)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await socket.CloseAsync(status, description, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await CloseAsync(WebSocketCloseStatus.NormalClosure, "Connection replaced", CancellationToken.None);
        }
        catch (WebSocketException)
        {
        }

        socket.Dispose();
        sendGate.Dispose();
    }
}
