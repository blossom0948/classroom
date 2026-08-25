using System.Threading.Channels;

namespace PhoneUnlock.Service.Security;

public sealed class WorkstationLockSignal
{
    private readonly Channel<bool> requests = Channel.CreateUnbounded<bool>();

    public void Request() => requests.Writer.TryWrite(true);

    public ValueTask<bool> WaitAsync(CancellationToken cancellationToken) => requests.Reader.ReadAsync(cancellationToken);
}
