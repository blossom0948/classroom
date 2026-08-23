namespace PhoneUnlock.Core.Models;

public sealed record AuthDeniedPayload(
    Guid RequestId,
    Guid ComputerId,
    string Reason);
