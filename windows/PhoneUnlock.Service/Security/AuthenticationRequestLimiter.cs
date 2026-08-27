using System.Collections.Concurrent;

namespace PhoneUnlock.Service.Security;

public sealed class AuthenticationRequestLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EscalatedCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ViolationWindow = TimeSpan.FromMinutes(10);
    private const int MaximumRequests = 3;
    private const int EscalationThreshold = 3;
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);

    public RequestLimitDecision TryAcquire(string key, DateTimeOffset now)
    {
        var entry = entries.GetOrAdd(key, static _ => new Entry());
        lock (entry.Gate)
        {
            Trim(entry.Requests, now - Window);
            Trim(entry.Violations, now - ViolationWindow);
            if (entry.CooldownUntil > now)
            {
                return new RequestLimitDecision(false, entry.CooldownUntil - now);
            }

            if (entry.Requests.Count >= MaximumRequests)
            {
                entry.Violations.Enqueue(now);
                var cooldown = entry.Violations.Count >= EscalationThreshold
                    ? EscalatedCooldown
                    : Cooldown;
                entry.CooldownUntil = now + cooldown;
                return new RequestLimitDecision(false, cooldown);
            }

            entry.Requests.Enqueue(now);
            return new RequestLimitDecision(true, TimeSpan.Zero);
        }
    }

    private static void Trim(Queue<DateTimeOffset> values, DateTimeOffset threshold)
    {
        while (values.TryPeek(out var value) && value <= threshold)
        {
            values.Dequeue();
        }
    }

    private sealed class Entry
    {
        public object Gate { get; } = new();
        public Queue<DateTimeOffset> Requests { get; } = new();
        public Queue<DateTimeOffset> Violations { get; } = new();
        public DateTimeOffset CooldownUntil { get; set; }
    }
}

public readonly record struct RequestLimitDecision(bool Allowed, TimeSpan RetryAfter);
