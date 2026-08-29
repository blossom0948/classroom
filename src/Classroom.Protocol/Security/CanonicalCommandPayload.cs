using System.Text;
using Blossom.Classroom.Core.Security;
using Blossom.Classroom.Protocol.Models;
using Blossom.Classroom.Protocol.Validation;

namespace Blossom.Classroom.Protocol.Security;

public static class CanonicalCommandPayload
{
    public static string Create(CommandRequest command)
    {
        ProtocolValidation.ValidateCommand(command);
        return string.Join(
            '\n',
            "CLASSROOM-COMMAND-V1",
            $"requestId={command.RequestId.ToString("D").ToLowerInvariant()}",
            $"sessionId={command.SessionId.ToString("D").ToLowerInvariant()}",
            $"targets={string.Join(',', command.TargetDeviceIds.OrderBy(id => id).Select(id => id.ToString("D").ToLowerInvariant()))}",
            $"kind={command.Kind.ToString().ToUpperInvariant()}",
            $"message={Encode(command.Message)}",
            $"url={Encode(command.Url)}",
            $"approvedAppId={Encode(command.ApprovedAppId)}",
            $"displaySeconds={command.DisplaySeconds?.ToString() ?? "-"}",
            $"requiresAcknowledgement={command.RequiresAcknowledgement.ToString().ToLowerInvariant()}",
            $"focusEnabled={command.FocusEnabled?.ToString().ToLowerInvariant() ?? "-"}");
    }

    private static string Encode(string? value) =>
        value is null ? "-" : Base64Url.Encode(Encoding.UTF8.GetBytes(value));
}
