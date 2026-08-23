using PhoneUnlock.Core.Models;
using PhoneUnlock.Core.Protocol;

namespace PhoneUnlock.Core.Security;

public sealed class AuthValidationService(
    ChallengeStore challengeStore,
    SignatureVerifier signatureVerifier)
{
    public AuthValidationStatus Verify(
        ProtocolEnvelope<AuthApprovedPayload> envelope,
        string publicKeyBase64,
        DateTimeOffset? now = null)
    {
        if (envelope.Version != ProtocolConstants.Version)
        {
            return AuthValidationStatus.UnsupportedProtocol;
        }

        if (!string.Equals(envelope.Type, ProtocolConstants.AuthApproved, StringComparison.Ordinal))
        {
            return AuthValidationStatus.WrongMessageType;
        }

        var lookup = challengeStore.Lookup(envelope.Payload, now);
        if (lookup.Status != AuthValidationStatus.Success)
        {
            return lookup.Status;
        }

        if (!signatureVerifier.Verify(envelope.Payload, publicKeyBase64))
        {
            return AuthValidationStatus.InvalidPublicKeyOrSignature;
        }

        return challengeStore.TryConsume(envelope.Payload.RequestId)
            ? AuthValidationStatus.Success
            : AuthValidationStatus.Replayed;
    }
}
