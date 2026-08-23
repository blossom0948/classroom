using System.Security.Cryptography;
using PhoneUnlock.Core.Models;

namespace PhoneUnlock.Core.Security;

public sealed class SignatureVerifier
{
    public bool Verify(AuthApprovedPayload response, string publicKeyBase64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyBase64);

        try
        {
            var publicKeyBytes = Convert.FromBase64String(publicKeyBase64.Trim());
            var signatureBytes = Convert.FromBase64String(response.Signature);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out var bytesRead);
            if (bytesRead != publicKeyBytes.Length || ecdsa.KeySize != 256)
            {
                return false;
            }

            return ecdsa.VerifyData(
                CanonicalPayload.GetBytes(response),
                signatureBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}
