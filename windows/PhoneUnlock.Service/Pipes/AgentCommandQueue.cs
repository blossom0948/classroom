using System.Threading.Channels;

namespace PhoneUnlock.Service.Pipes;

public sealed class AgentCommandQueue
{
    private readonly Channel<string> commands = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest,
    });

    public bool Publish(string command) => commands.Writer.TryWrite(command);

    public ValueTask<string> WaitAsync(CancellationToken cancellationToken) =>
        commands.Reader.ReadAsync(cancellationToken);
}
