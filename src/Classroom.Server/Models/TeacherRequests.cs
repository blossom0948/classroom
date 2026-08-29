using Blossom.Classroom.Server.Storage;

namespace Blossom.Classroom.Server.Models;

public sealed record CreateEnrollmentTicketRequest(
    Guid StudentId,
    string StudentDisplayName);

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

public sealed record TeacherLoginRequest(
    string LoginName,
    string Password);

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
