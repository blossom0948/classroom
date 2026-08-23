namespace PhoneUnlock.Core.Models;

public sealed record AuthApprovedPayload(
    Guid RequestId,
    Guid ComputerId,
    string Challenge,
    long ExpiresAt,
    string PhoneId,
    string Signature);
