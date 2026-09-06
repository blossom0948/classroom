namespace Blossom.Classroom.Protocol.Models;

/// <summary>
/// A bounded input event for a student-approved remote-assistance session.
/// It can only describe pointer and non-privileged keyboard input; it never
/// represents a shell command, file operation, or privileged Windows action.
/// </summary>
public enum RemoteAssistInputKind
{
    PointerMove,
    PointerButton,
    PointerWheel,
    Key
}

public enum RemoteMouseButton
{
    Left,
    Middle,
    Right
}

public sealed record RemoteAssistInput(
    Guid RemoteAssistSessionId,
    long Sequence,
    RemoteAssistInputKind Kind,
    double? X = null,
    double? Y = null,
    RemoteMouseButton? Button = null,
    int? WheelDelta = null,
    string? KeyCode = null,
    bool? IsDown = null);

public sealed record RemoteAssistStatus(
    Guid RemoteAssistSessionId,
    string State,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string TeacherDisplayName,
    string? EndReason = null);
