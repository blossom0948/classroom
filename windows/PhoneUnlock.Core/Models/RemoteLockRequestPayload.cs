namespace PhoneUnlock.Core.Models;

public sealed record RemoteLockRequestPayload(
    Guid RequestId,
    Guid ComputerId,
    long ExpiresAt,
    string PhoneId);
