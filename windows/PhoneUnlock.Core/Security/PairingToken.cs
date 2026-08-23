using System.Security.Cryptography;
using System.Text;
using PhoneUnlock.Core.Protocol;

namespace PhoneUnlock.Core.Security;

public sealed record PairingToken(string Value, long ExpiresAt);

public static class PairingTokenService
{
    public static PairingToken Create(DateTimeOffset? now = null)
    {
        var effectiveNow = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        return new PairingToken(
            Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
            effectiveNow.AddSeconds(ProtocolConstants.DefaultPairingTimeoutSeconds).ToUnixTimeSeconds());
    }

    public static bool Validate(PairingToken expected, string candidate, DateTimeOffset? now = null)
    {
        var unixNow = (now ?? DateTimeOffset.UtcNow).ToUniversalTime().ToUnixTimeSeconds();
        if (unixNow > expected.ExpiresAt)
        {
            return false;
        }

        var expectedBytes = Encoding.ASCII.GetBytes(expected.Value);
        var candidateBytes = Encoding.ASCII.GetBytes(candidate);
        return expectedBytes.Length == candidateBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, candidateBytes);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
