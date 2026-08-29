namespace Blossom.Classroom.Core.Security;

public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public static byte[] Decode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
        try
        {
            return Convert.FromBase64String(normalized);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Value is not valid Base64URL.", nameof(value), exception);
        }
    }
}

