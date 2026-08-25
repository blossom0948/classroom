namespace PhoneUnlock.Service.Networking;

public sealed record RemoteUnlockRequest(
    string PhoneId,
    string? RemoteIp,
    string Json);
