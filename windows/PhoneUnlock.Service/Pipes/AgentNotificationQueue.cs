using System.Threading.Channels;

namespace PhoneUnlock.Service.Pipes;

/// <summary>
/// Delivers user-session notifications from the service to the tray agent. The
/// service cannot display UI in the signed-in Windows session directly.
/// </summary>
public sealed class AgentNotificationQueue
{
    private readonly Channel<AgentNotification> notices = Channel.CreateUnbounded<AgentNotification>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public void Publish(string title, string message) =>
        notices.Writer.TryWrite(new AgentNotification(title, message));

    public ValueTask<AgentNotification> WaitAsync(CancellationToken cancellationToken) =>
        notices.Reader.ReadAsync(cancellationToken);
}

public sealed record AgentNotification(string Title, string Message);
