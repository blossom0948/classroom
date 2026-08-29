namespace Blossom.Classroom.Protocol.Validation;

public sealed class ProtocolValidationException(string message) : ArgumentException(message);

