using System.Collections.Concurrent;

namespace Blossom.Classroom.Server.Security;

public sealed class TeacherLoginRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const int MaximumAttemptsPerWindow = 10;
    private readonly ConcurrentDictionary<string, AttemptWindow> windows = new(StringComparer.Ordinal);

    public bool TryAcquire(string key)
    {
        var now = DateTimeOffset.UtcNow;
        while (true)
        {
            var current = windows.GetOrAdd(key, _ => new AttemptWindow(now, 0));
            if (now - current.StartedAtUtc >= Window)
            {
                var replacement = new AttemptWindow(now, 1);
                if (windows.TryUpdate(key, replacement, current))
                {
                    return true;
                }

                continue;
            }

            if (current.Attempts >= MaximumAttemptsPerWindow)
            {
                return false;
            }

            var updated = current with { Attempts = current.Attempts + 1 };
            if (windows.TryUpdate(key, updated, current))
            {
                return true;
            }
        }
    }

    public void Reset(string key) => windows.TryRemove(key, out _);

    private sealed record AttemptWindow(DateTimeOffset StartedAtUtc, int Attempts);
}
