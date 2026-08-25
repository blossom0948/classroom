namespace PhoneUnlock.Core.Models;

public sealed record RemoteUnlockRequestPayload(
    Guid RequestId,
    Guid ComputerId,
    string Challenge,
    long ExpiresAt,
    string PhoneId,
    string Signature);
