namespace PhoneUnlock.Service.Models;

public sealed record AuditEntry(
    DateTimeOffset OccurredAt,
    string EventType,
    string Outcome,
    string? PhoneId,
    string? PhoneName,
    string? RemoteIp,
    Guid? RequestId,
    string Message,
    bool Suspicious);

public sealed record PhoneConnectionStatus(
    string PhoneId,
    string PhoneName,
    bool Enabled,
    bool Connected,
    DateTimeOffset? LastSeen,
    DateTimeOffset? LastHeartbeat,
    string? RemoteIp);

