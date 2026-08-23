namespace PhoneUnlock.Core.Models;

public sealed record ProtocolEnvelope<TPayload>(
    int Version,
    string Type,
    Guid MessageId,
    long Timestamp,
    TPayload Payload);
