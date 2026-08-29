namespace Blossom.Classroom.Protocol.Models;

public sealed record ProtocolEnvelope<TPayload>(
    int Version,
    string Type,
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    TPayload Payload)
{
    public static ProtocolEnvelope<TPayload> Create(
        string type,
        TPayload payload,
        DateTimeOffset? timestampUtc = null) =>
        new(
            ProtocolConstants.Version,
            type,
            Guid.NewGuid(),
            (timestampUtc ?? DateTimeOffset.UtcNow).ToUniversalTime(),
            payload);
}

