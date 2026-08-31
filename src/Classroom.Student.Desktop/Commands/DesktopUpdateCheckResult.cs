namespace Blossom.Classroom.Student.Desktop.Commands;

public sealed record DesktopUpdateCheckResult(
    bool Success,
    string Code,
    string Message,
    string CurrentVersion,
    string? AvailableVersion,
    bool RestartRequired);
