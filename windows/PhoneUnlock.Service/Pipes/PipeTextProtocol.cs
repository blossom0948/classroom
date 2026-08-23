using System.Text;
using PhoneUnlock.Service.Configuration;

namespace PhoneUnlock.Service.Pipes;

public static class PipeTextProtocol
{
    public static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(256);
        var buffer = new byte[1];
        while (bytes.Count <= ServiceConstants.MaxPipeLineLength)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return bytes.Count == 0 ? null : Encoding.UTF8.GetString([.. bytes]);
            }

            if (buffer[0] == (byte)'\n')
            {
                if (bytes.Count > 0 && bytes[^1] == (byte)'\r')
                {
                    bytes.RemoveAt(bytes.Count - 1);
                }

                return Encoding.UTF8.GetString([.. bytes]);
            }

            bytes.Add(buffer[0]);
        }

        throw new InvalidDataException("Named pipe request exceeded the size limit.");
    }

    public static async Task WriteLineAsync(Stream stream, string value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\n");
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
