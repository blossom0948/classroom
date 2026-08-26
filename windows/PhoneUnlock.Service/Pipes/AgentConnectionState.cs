using System.Collections.Concurrent;

namespace PhoneUnlock.Service.Pipes;

public sealed class AgentConnectionState
{
    private int connected;
    private int humanPresence = -1;
    private long humanPresenceTicks;
    private readonly ConcurrentDictionary<string, RssiSample> latestRssi = new(StringComparer.Ordinal);

    public bool IsConnected => Volatile.Read(ref connected) == 1;

    public void SetConnected(bool value)
    {
        Volatile.Write(ref connected, value ? 1 : 0);
        if (!value)
        {
            latestRssi.Clear();
            Interlocked.Exchange(ref humanPresence, -1);
            Interlocked.Exchange(ref humanPresenceTicks, 0);
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

    public void SetHumanPresence(bool present)
    {
        Interlocked.Exchange(ref humanPresence, present ? 1 : 0);
        Interlocked.Exchange(ref humanPresenceTicks, DateTimeOffset.UtcNow.Ticks);
    }

    public bool TryGetRecentHumanPresence(TimeSpan maxAge, out bool present)
    {
        var state = Volatile.Read(ref humanPresence);
        var ticks = Interlocked.Read(ref humanPresenceTicks);
        if (state >= 0 && ticks != 0 && DateTimeOffset.UtcNow - new DateTimeOffset(ticks, TimeSpan.Zero) <= maxAge)
        {
            present = state == 1;
            return true;
        }

        present = false;
        return false;
    }

    private sealed record RssiSample(int Rssi, DateTimeOffset ObservedAt);
}
