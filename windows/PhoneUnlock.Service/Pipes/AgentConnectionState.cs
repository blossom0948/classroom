using System.Collections.Concurrent;

namespace PhoneUnlock.Service.Pipes;

public sealed class AgentConnectionState
{
    private int connected;
    private readonly ConcurrentDictionary<string, RssiSample> latestRssi = new(StringComparer.Ordinal);

    public bool IsConnected => Volatile.Read(ref connected) == 1;

    public void SetConnected(bool value)
    {
        Volatile.Write(ref connected, value ? 1 : 0);
        if (!value)
        {
            latestRssi.Clear();
        }
    }

    public void SetRssi(string phoneId, int rssi) =>
        latestRssi[phoneId] = new RssiSample(rssi, DateTimeOffset.UtcNow);

    public bool TryGetRecentRssi(string phoneId, TimeSpan maxAge, out int rssi)
    {
        if (latestRssi.TryGetValue(phoneId, out var sample)
            && DateTimeOffset.UtcNow - sample.ObservedAt <= maxAge)
        {
            rssi = sample.Rssi;
            return true;
        }

        rssi = 0;
        return false;
    }

    private sealed record RssiSample(int Rssi, DateTimeOffset ObservedAt);
}
