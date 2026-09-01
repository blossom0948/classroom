using Blossom.Classroom.Protocol;
using Blossom.Classroom.Protocol.Models;

namespace Blossom.Classroom.Student.Service.Status;

public sealed record StudentStatusData(
    ActivitySnapshot? Activity,
    int? BatteryPercent,
    string? NetworkStatus,
    bool PolicyApplied,
    ScreenFrame? ScreenFrame = null,
    bool ScreenSharingEnabled = false,
    bool NeedsHelp = false,
    int ScreenShareIntervalMilliseconds = ProtocolConstants.ScreenShareStandardIntervalMilliseconds)
{
    public static StudentStatusData Empty { get; } = new(null, null, "unknown", false);
}

public interface IStudentStatusSource
{
    ValueTask<StudentStatusData> GetAsync(CancellationToken cancellationToken);
}

public sealed class EmptyStudentStatusSource : IStudentStatusSource
{
    public ValueTask<StudentStatusData> GetAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(StudentStatusData.Empty);
}
