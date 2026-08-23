using System.Security.Cryptography;
using PhoneUnlock.Core.Models;
using PhoneUnlock.Core.Protocol;

namespace PhoneUnlock.Core.Security;

public sealed class ChallengeGenerator
{
    public ProtocolEnvelope<AuthRequestPayload> Create(
        Guid computerId,
        string computerName,
        DateTimeOffset? now = null,
        TimeSpan? lifetime = null)
    {
        if (computerId == Guid.Empty)
        {
            throw new ArgumentException("Computer ID cannot be empty.", nameof(computerId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(computerName);

        var effectiveNow = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var effectiveLifetime = lifetime ?? TimeSpan.FromSeconds(ProtocolConstants.DefaultAuthTimeoutSeconds);
        if (effectiveLifetime <= TimeSpan.Zero || effectiveLifetime > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Challenge lifetime must be between 1 second and 5 minutes.");
        }

        var createdAt = effectiveNow.ToUnixTimeSeconds();
        var expiresAt = effectiveNow.Add(effectiveLifetime).ToUnixTimeSeconds();
        var payload = new AuthRequestPayload(
            Guid.NewGuid(),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(ProtocolConstants.ChallengeSizeBytes)),
            createdAt,
            expiresAt,
            computerId,
            computerName.Trim());

        return new ProtocolEnvelope<AuthRequestPayload>(
            ProtocolConstants.Version,
            ProtocolConstants.AuthRequest,
            Guid.NewGuid(),
            createdAt,
            payload);
    }
}
