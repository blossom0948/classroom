namespace PhoneUnlock.Service.Security;

public sealed class RemoteUnlockGrantStore
{
    private readonly object gate = new();
    private GrantRecord? grant;

    public void Grant(string phoneId, string sid, DateTimeOffset expiresAt)
    {
        lock (gate)
        {
            grant = new GrantRecord(phoneId, sid, expiresAt);
        }
    }

    public bool TryConsume(string sid, out string? phoneId)
    {
        lock (gate)
        {
            if (grant is null || grant.ExpiresAt < DateTimeOffset.UtcNow
                || !string.Equals(grant.Sid, sid, StringComparison.OrdinalIgnoreCase))
            {
                phoneId = null;
                if (grant is { ExpiresAt: var expiresAt } && expiresAt < DateTimeOffset.UtcNow)
                {
                    grant = null;
                }
                return false;
            }

            phoneId = grant.PhoneId;
            grant = null;
            return true;
        }
    }

    private sealed record GrantRecord(string PhoneId, string Sid, DateTimeOffset ExpiresAt);
}
