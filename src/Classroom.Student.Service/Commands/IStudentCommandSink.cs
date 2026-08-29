using Blossom.Classroom.Protocol.Models;

namespace Blossom.Classroom.Student.Service.Commands;

public sealed record CommandApplyResult(bool Success, string Code, string Message);

public interface IStudentCommandSink
{
    Task<CommandApplyResult> ApplyAsync(
        CommandRequest command,
        CancellationToken cancellationToken);
}

public sealed class DesktopDisconnectedCommandSink(
    ILogger<DesktopDisconnectedCommandSink> logger) : IStudentCommandSink
{
    public static CommandApplyResult NotConnectedResult { get; } = new(
        false,
        "STUDENT_DESKTOP_OFFLINE",
        "The visible Student Desktop is not connected, so the command was not applied.");

    public Task<CommandApplyResult> ApplyAsync(
        CommandRequest command,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Command {RequestId} ({Kind}) received; visible Student Desktop is not connected.",
            command.RequestId,
            command.Kind);
        return Task.FromResult(NotConnectedResult);
    }
}
