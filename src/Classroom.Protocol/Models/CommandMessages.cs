namespace Blossom.Classroom.Protocol.Models;

public enum ClassroomCommandKind
{
    Message,
    OpenUrl,
    FocusMode,
    LaunchApprovedApp,
    ScreenShare
}

public sealed record CommandRequest(
    Guid RequestId,
    Guid SessionId,
    IReadOnlyList<Guid> TargetDeviceIds,
    ClassroomCommandKind Kind,
    string? Message = null,
    string? Url = null,
    string? ApprovedAppId = null,
    int? DisplaySeconds = null,
    bool RequiresAcknowledgement = true,
    bool? FocusEnabled = null,
    bool? ScreenShareEnabled = null,
    int? ScreenShareIntervalMilliseconds = null);

public sealed record CommandAck(
    Guid RequestId,
    Guid DeviceId,
    bool Accepted,
    string? Reason,
    DateTimeOffset ReceivedAtUtc);

public sealed record CommandResult(
    Guid RequestId,
    Guid DeviceId,
    bool Success,
    string Code,
    string Message,
    DateTimeOffset AppliedAtUtc);

public sealed record ErrorMessage(
    string Code,
    string Message,
    Guid? RequestId = null);
