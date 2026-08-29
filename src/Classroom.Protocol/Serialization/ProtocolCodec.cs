using System.Text.Json;
using System.Text;
using Blossom.Classroom.Core.Serialization;
using Blossom.Classroom.Protocol.Models;
using Blossom.Classroom.Protocol.Validation;

namespace Blossom.Classroom.Protocol.Serialization;

public static class ProtocolCodec
{
    public static string Serialize<TPayload>(ProtocolEnvelope<TPayload> envelope)
    {
        ValidateEnvelope(envelope);
        var json = ClassroomJson.Serialize(envelope);
        EnsureSize(json);
        return json;
    }

    public static ProtocolEnvelope<TPayload> Deserialize<TPayload>(string json)
    {
        EnsureSize(json);
        ProtocolEnvelope<TPayload> envelope;
        try
        {
            envelope = ClassroomJson.Deserialize<ProtocolEnvelope<TPayload>>(json);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new ProtocolValidationException("Protocol JSON is malformed.");
        }

        ValidateEnvelope(envelope);
        return envelope;
    }

    private static void ValidateEnvelope<TPayload>(ProtocolEnvelope<TPayload> envelope)
    {
        if (envelope is null)
        {
            throw new ProtocolValidationException("Protocol envelope is required.");
        }

        if (envelope.Version != ProtocolConstants.Version)
        {
            throw new ProtocolValidationException("Unsupported protocol version.");
        }

        if (envelope.MessageId == Guid.Empty)
        {
            throw new ProtocolValidationException("Protocol message ID must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(envelope.Type)
            || !ProtocolConstants.IsKnownMessageType(envelope.Type))
        {
            throw new ProtocolValidationException("Unsupported protocol message type.");
        }

        if (envelope.Payload is null)
        {
            throw new ProtocolValidationException("Protocol payload is required.");
        }
    }

    private static void EnsureSize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)
            || Encoding.UTF8.GetByteCount(json) > ProtocolConstants.MaxMessageBytes)
        {
            throw new ProtocolValidationException(
                $"Protocol messages must be 1 to {ProtocolConstants.MaxMessageBytes} UTF-8 bytes.");
        }
    }
}
