namespace Blossom.Classroom.Student.Desktop.Commands;

public sealed record DesktopCommandApplyResult(
    bool Success,
    string Code,
    string Message);
