namespace PhoneUnlock.Core.Models;

public sealed record RemotePowerRequestPayload(
    Guid RequestId,
    Guid ComputerId,
    string Command,
    string Challenge,
    long ExpiresAt,
    string PhoneId,
    string Signature);
