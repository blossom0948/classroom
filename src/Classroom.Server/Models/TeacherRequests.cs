using Blossom.Classroom.Server.Storage;

namespace Blossom.Classroom.Server.Models;

public sealed record CreateEnrollmentTicketRequest(
    Guid? StudentId,
    string StudentDisplayName);

public sealed record JoinCodeEnrollmentRequest(
    string JoinCode,
    string DeviceName,
    string AgentVersion);

public sealed record AdministratorRequest(
    string Identifier,
    bool IsAdmin);

public sealed record StudentExitPinUpdateRequest(string Pin);

public sealed record StudentExitPinStatus(
    bool Configured,
    DateTimeOffset? UpdatedAtUtc);

public sealed record StudentCodeView(
    Guid DeviceId,
    Guid SchoolId,
    Guid ClassId,
    string ClassName,
    string Subject,
    Guid StudentId,
    string StudentDisplayName,
    string JoinCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastUsedAtUtc,
    string CreatedByDisplayName);

public sealed record AdministratorGrantView(
    string Identifier,
    DateTimeOffset CreatedAtUtc);

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

public sealed record GuestLoginRequest(
    string SchoolId,
    string Password);

public sealed record GuestPasswordUpdateRequest(
    string Password);

public sealed record GuestPasswordStatus(
    bool Configured,
    DateTimeOffset? UpdatedAtUtc);

public sealed record FirebaseLoginRequest(
    string IdToken,
    string? DisplayName = null,
    string? Subject = null);

public sealed record TeacherLoginResponse(
    string AccessToken,
    DateTimeOffset ExpiresAtUtc,
    Guid TeacherId,
    string DisplayName,
    IReadOnlyList<TeacherClass> Classes,
    bool IsAdmin = false);

public sealed record TeacherSessionResponse(
    Guid TeacherId,
    string DisplayName,
    IReadOnlyList<TeacherClass> Classes,
    bool IsAdmin = false);

public sealed record ChangePasswordRequest(
    string CurrentPassword,
    string NewPassword);
