using System.Text;
using PhoneUnlock.Core.Models;

namespace PhoneUnlock.Core.Security;

public static class CanonicalPayload
{
    public static string Create(AuthRequestPayload request) => Create(
        request.RequestId,
        request.ComputerId,
        request.Challenge,
        request.ExpiresAt);

    public static string Create(AuthApprovedPayload response) => Create(
        response.RequestId,
        response.ComputerId,
        response.Challenge,
        response.ExpiresAt);

    public static string Create(RemoteUnlockRequestPayload request) => Create(
        request.RequestId,
        request.ComputerId,
        request.Challenge,
        request.ExpiresAt);

    public static byte[] GetBytes(AuthRequestPayload request) =>
        Encoding.UTF8.GetBytes(Create(request));

    public static byte[] GetBytes(AuthApprovedPayload response) =>
        Encoding.UTF8.GetBytes(Create(response));

    public static byte[] GetBytes(RemoteUnlockRequestPayload request) =>
        Encoding.UTF8.GetBytes(Create(request));

    public static string Create(RemoteLockRequestPayload request) => string.Join(
        '\n',
        "PHONE-UNLOCK-V1-LOCK",
        $"requestId={request.RequestId.ToString("D").ToLowerInvariant()}",
        $"computerId={request.ComputerId.ToString("D").ToLowerInvariant()}",
        $"expiresAt={request.ExpiresAt}",
        $"phoneId={request.PhoneId}");

    public static string Create(RemotePowerRequestPayload request)
    {
        ValidateCommand(request.Command);
        ValidateChallenge(request.Challenge);
        return string.Join(
            '\n',
            "PHONE-UNLOCK-V1-POWER",
            $"requestId={request.RequestId.ToString("D").ToLowerInvariant()}",
            $"computerId={request.ComputerId.ToString("D").ToLowerInvariant()}",
            $"command={request.Command.Trim().ToUpperInvariant()}",
            $"challenge={request.Challenge}",
            $"expiresAt={request.ExpiresAt}",
            $"phoneId={request.PhoneId}");
    }

    public static byte[] GetBytes(RemoteLockRequestPayload request) =>
        Encoding.UTF8.GetBytes(Create(request));

    public static byte[] GetBytes(RemotePowerRequestPayload request) =>
        Encoding.UTF8.GetBytes(Create(request));

    public static void ValidateCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)
            || command.Trim().ToUpperInvariant() is not ("SLEEP" or "HIBERNATE" or "RESTART" or "SHUTDOWN"))
        {
            throw new ArgumentException("Unsupported remote power command.", nameof(command));
        }
    }

    private static string Create(Guid requestId, Guid computerId, string challenge, long expiresAt)
    {
        ValidateChallenge(challenge);

        return string.Join(
            '\n',
            "PHONE-UNLOCK-V1",
            $"requestId={requestId.ToString("D").ToLowerInvariant()}",
            $"computerId={computerId.ToString("D").ToLowerInvariant()}",
            $"challenge={challenge}",
            $"expiresAt={expiresAt}");
    }

    public static void ValidateChallenge(string challenge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challenge);

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(challenge);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Challenge must be standard Base64.", nameof(challenge), exception);
        }

        if (bytes.Length != Protocol.ProtocolConstants.ChallengeSizeBytes)
        {
            throw new ArgumentException(
                $"Challenge must decode to {Protocol.ProtocolConstants.ChallengeSizeBytes} bytes.",
                nameof(challenge));
        }

        if (!string.Equals(Convert.ToBase64String(bytes), challenge, StringComparison.Ordinal))
        {
            throw new ArgumentException("Challenge must use canonical Base64 with padding.", nameof(challenge));
        }
    }
}
