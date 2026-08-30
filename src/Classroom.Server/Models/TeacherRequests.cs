using Blossom.Classroom.Server.Storage;

namespace Blossom.Classroom.Server.Models;

public sealed record CreateEnrollmentTicketRequest(
    Guid? StudentId,
    string StudentDisplayName);

public sealed record DeviceActionResponse(
    Guid DeviceId,
    string Status,
    DateTimeOffset UpdatedAtUtc);

public sealed record StartClassSessionRequest(string Subject);

public sealed record ClassSessionSnapshot(
    Guid SessionId,
    Guid SchoolId,
    Guid ClassId,
    string Subject,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc);

public sealed record CommandDispatchSummary(
    Guid RequestId,
    int RequestedCount,
    int QueuedCount,
    IReadOnlyList<Guid> QueuedDeviceIds,
    IReadOnlyList<Guid> RejectedDeviceIds);

public sealed record DeviceCommandStatus(
    Guid DeviceId,
    string State);

public sealed record CommandStatusResponse(
    Guid RequestId,
    int TotalCount,
    int CompletedCount,
    int FailedCount,
    bool Finished,
    IReadOnlyList<DeviceCommandStatus> Devices);

public sealed record TeacherLoginRequest(
    string LoginName,
    string Password);

public sealed record FirebaseLoginRequest(
    string IdToken);

public sealed record TeacherLoginResponse(
    string AccessToken,
    DateTimeOffset ExpiresAtUtc,
    Guid TeacherId,
    string DisplayName,
    IReadOnlyList<TeacherClass> Classes);

public sealed record TeacherSessionResponse(
    Guid TeacherId,
    string DisplayName,
    IReadOnlyList<TeacherClass> Classes);

public sealed record ChangePasswordRequest(
    string CurrentPassword,
    string NewPassword);
