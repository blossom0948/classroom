namespace PhoneUnlock.Core.Models;

public sealed record AuthRequestPayload(
    Guid RequestId,
    string Challenge,
    long CreatedAt,
    long ExpiresAt,
    Guid ComputerId,
    string ComputerName);
