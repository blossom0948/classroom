using System.Collections.Concurrent;

namespace Blossom.Classroom.Core.Security;

public sealed class ReplayGuard
{
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public bool TryAccept(
        string key,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var expiry = expiresAtUtc.ToUniversalTime();
        if (expiry <= now)
        {
            return false;
        }

        RemoveExpired(now);
        return entries.TryAdd(key, new Entry(expiry));
    }

    public bool Contains(string key, DateTimeOffset? nowUtc = null)
    {
        if (!entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        if (entry.ExpiresAtUtc <= now)
        {
            entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            return false;
        }

        return true;
    }

    public int RemoveExpired(DateTimeOffset? nowUtc = null)
    {
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var removed = 0;
        foreach (var pair in entries)
        {
            if (pair.Value.ExpiresAtUtc <= now
                && entries.TryRemove(pair.Key, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    private sealed record Entry(DateTimeOffset ExpiresAtUtc);
}

