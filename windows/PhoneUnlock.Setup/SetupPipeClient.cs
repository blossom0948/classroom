using System.IO;
using System.IO.Pipes;
using System.Text;
using PhoneUnlock.Core.Protocol;
using PhoneUnlock.Setup.Models;

namespace PhoneUnlock.Setup;

public sealed class SetupPipeClient
{
    private const string PipeName = "PhoneUnlock.Setup";

    public async Task<SetupResponse> SendAsync(SetupRequest request, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        await using var pipe = new NamedPipeClientStream(
            ".",
            PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        try
        {
            await pipe.ConnectAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Phone Unlock 서비스를 찾을 수 없습니다.");
        }

        var requestBytes = Encoding.UTF8.GetBytes(ProtocolJson.SerializeCompact(request) + "\n");
        await pipe.WriteAsync(requestBytes, cancellation.Token);
        await pipe.FlushAsync(cancellation.Token);

        using var reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
        var line = await reader.ReadLineAsync(cancellation.Token)
            ?? throw new IOException("Phone Unlock 서비스가 응답 없이 연결을 닫았습니다.");
        return ProtocolJson.Deserialize<SetupResponse>(line);
    }
}
