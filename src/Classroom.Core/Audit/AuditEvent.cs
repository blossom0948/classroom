namespace Blossom.Classroom.Core.Audit;

public sealed record AuditEvent(
    Guid EventId,
    DateTimeOffset TimestampUtc,
    Guid? SchoolId,
    Guid? ClassId,
    Guid? SessionId,
    Guid? TeacherId,
    Guid? TeacherDeviceId,
    Guid? StudentId,
    Guid? StudentDeviceId,
    string Action,
    string Result,
    string? Reason,
    Guid? RequestId)
{
    public static AuditEvent Create(
        string action,
        string result,
        string? reason = null,
        Guid? schoolId = null,
        Guid? classId = null,
        Guid? sessionId = null,
        Guid? teacherId = null,
        Guid? teacherDeviceId = null,
        Guid? studentId = null,
        Guid? studentDeviceId = null,
        Guid? requestId = null,
        DateTimeOffset? timestampUtc = null) =>
        new(
            Guid.NewGuid(),
            (timestampUtc ?? DateTimeOffset.UtcNow).ToUniversalTime(),
            schoolId,
            classId,
            sessionId,
            teacherId,
            teacherDeviceId,
            studentId,
            studentDeviceId,
            RequireText(action, nameof(action)),
            RequireText(result, nameof(result)),
            reason,
            requestId);

    private static string RequireText(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128)
        {
            throw new ArgumentException("Audit text is too long.", name);
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("Audit text cannot contain control characters.", name);
        }

        return value;
    }
}

