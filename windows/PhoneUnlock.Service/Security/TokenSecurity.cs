using System.Security.Cryptography;
using System.Text;

namespace PhoneUnlock.Service.Security;

public static class TokenSecurity
{
    public static string CreateToken() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string HashToken(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool VerifyToken(string token, string expectedHash)
    {
        try
        {
            var actual = Convert.FromBase64String(HashToken(token));
            var expected = Convert.FromBase64String(expectedHash);
            return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
