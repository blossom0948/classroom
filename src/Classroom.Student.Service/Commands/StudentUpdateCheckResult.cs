namespace Blossom.Classroom.Student.Service.Commands;

public sealed record StudentUpdateCheckResult(
    bool Success,
    string Code,
    string Message,
    string CurrentVersion,
    string? AvailableVersion,
    bool RestartRequired);
