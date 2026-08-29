using System.Security.Cryptography;

namespace Blossom.Classroom.Server.Security;

public static class PasswordSecurity
{
    private const string Algorithm = "pbkdf2-sha256";
    private const int Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string HashPassword(string password)
    {
        ValidatePassword(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashBytes);
        return string.Join(
            '$',
            Algorithm,
            Iterations,
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    public static bool VerifyPassword(string password, string encodedHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(encodedHash))
        {
            return false;
        }

        var parts = encodedHash.Split('$');
        if (parts.Length != 4
            || !string.Equals(parts[0], Algorithm, StringComparison.Ordinal)
            || !int.TryParse(parts[1], out var iterations)
            || iterations < 100_000
            || !TryDecode(parts[2], SaltBytes, out var salt)
            || !TryDecode(parts[3], HashBytes, out var expected))
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool TryDecode(string value, int expectedLength, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(value);
            return bytes.Length == expectedLength;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password)
            || password.Length is < 12 or > 256
            || password.Any(char.IsControl))
        {
            throw new ArgumentException("Password must be 12 to 256 printable characters.", nameof(password));
        }
    }
}
