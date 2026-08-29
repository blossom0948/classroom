using System.Security.Cryptography;
using System.Text;

namespace Blossom.Classroom.Core.Security;

public static class TokenSecurity
{
    public static string CreateToken(int byteLength = 32)
    {
        if (byteLength < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(byteLength), "Tokens must contain at least 128 bits.");
        }

        return Base64Url.Encode(RandomNumberGenerator.GetBytes(byteLength));
    }

    public static string HashToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    public static bool VerifyToken(string token, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }

        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

