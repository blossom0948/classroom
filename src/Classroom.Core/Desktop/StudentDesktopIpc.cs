namespace Blossom.Classroom.Core.Desktop;

public static class StudentDesktopIpc
{
    public const int MaxMessageBytes = 64 * 1024;

    public static string GetPipeName(Guid deviceId) =>
        $"classroom-student-desktop-{deviceId:N}";
}
