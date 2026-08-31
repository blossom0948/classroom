namespace Blossom.Classroom.Student.Service.Commands;

public sealed record StudentExitPinVerificationResult(
    bool Approved,
    string Code,
    string Message);
