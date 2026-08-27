namespace PhoneUnlock.Core.Models;

public sealed record DeckActionRequestPayload(
    Guid RequestId,
    Guid ComputerId,
    long ExpiresAt,
    string PhoneId,
    string Action);
