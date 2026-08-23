using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using PhoneUnlock.Core.Models;

namespace PhoneUnlock.Core.Security;

public sealed class ChallengeStore
{
    private readonly ConcurrentDictionary<Guid, ChallengeEntry> entries = new();

    public void Register(AuthRequestPayload request)
    {
        if (!entries.TryAdd(request.RequestId, new ChallengeEntry(request)))
        {
            throw new InvalidOperationException("A challenge with this request ID is already registered.");
        }
    }

    public ChallengeLookup Lookup(AuthApprovedPayload response, DateTimeOffset? now = null)
    {
        if (!entries.TryGetValue(response.RequestId, out var entry))
        {
            return ChallengeLookup.Fail(AuthValidationStatus.UnknownRequest);
        }

        if (entry.IsConsumed)
        {
            return ChallengeLookup.Fail(AuthValidationStatus.Replayed);
        }

        var request = entry.Request;
        var unixNow = (now ?? DateTimeOffset.UtcNow).ToUniversalTime().ToUnixTimeSeconds();
        if (unixNow > request.ExpiresAt)
        {
            return ChallengeLookup.Fail(AuthValidationStatus.Expired);
        }

        if (response.ComputerId != request.ComputerId || response.ExpiresAt != request.ExpiresAt)
        {
            return ChallengeLookup.Fail(AuthValidationStatus.RequestMismatch);
        }

        if (!FixedTimeEquals(response.Challenge, request.Challenge))
        {
            return ChallengeLookup.Fail(AuthValidationStatus.RequestMismatch);
        }

        return ChallengeLookup.Success(request);
    }

    public bool TryConsume(Guid requestId)
    {
        if (!entries.TryGetValue(requestId, out var entry))
        {
            return false;
        }

        return Interlocked.CompareExchange(ref entry.Consumed, 1, 0) == 0;
    }

    public int RemoveExpired(DateTimeOffset? now = null)
    {
        var unixNow = (now ?? DateTimeOffset.UtcNow).ToUniversalTime().ToUnixTimeSeconds();
        var removed = 0;
        foreach (var pair in entries)
        {
            if (pair.Value.Request.ExpiresAt < unixNow && entries.TryRemove(pair.Key, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private sealed class ChallengeEntry(AuthRequestPayload request)
    {
        public AuthRequestPayload Request { get; } = request;
        public int Consumed;
        public bool IsConsumed => Volatile.Read(ref Consumed) == 1;
    }
}

public sealed record ChallengeLookup(
    AuthValidationStatus Status,
    AuthRequestPayload? Request)
{
    public static ChallengeLookup Success(AuthRequestPayload request) =>
        new(AuthValidationStatus.Success, request);

    public static ChallengeLookup Fail(AuthValidationStatus status) =>
        new(status, null);
}
