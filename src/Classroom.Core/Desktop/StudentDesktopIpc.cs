namespace Blossom.Classroom.Core.Desktop;

public static class StudentDesktopIpc
{
    // Must remain aligned with ProtocolConstants.MaxMessageBytes: a 720p
    // adaptive JPEG is base64-encoded before it crosses this pipe.
    public const int MaxMessageBytes = 128 * 1024;

    public static string GetPipeName(Guid deviceId) =>
        $"classroom-student-desktop-{deviceId:N}";
}
