namespace Blossom.Classroom.Student.Desktop.Commands;

public sealed record DesktopExitPinVerificationResult(
    bool Approved,
    string Code,
    string Message);
