using System.Security.Cryptography;
using PhoneUnlock.Core.Models;

namespace PhoneUnlock.Core.Security;

public sealed class SignatureVerifier
{
    public bool Verify(AuthApprovedPayload response, string publicKeyBase64)
    {
        return Verify(CanonicalPayload.GetBytes(response), response.Signature, publicKeyBase64);
    }

    public bool Verify(RemoteUnlockRequestPayload request, string publicKeyBase64)
    {
        return Verify(CanonicalPayload.GetBytes(request), request.Signature, publicKeyBase64);
    }

    public bool Verify(RemotePowerRequestPayload request, string publicKeyBase64)
    {
        return Verify(CanonicalPayload.GetBytes(request), request.Signature, publicKeyBase64);
    }

    private static bool Verify(byte[] data, string signatureBase64, string publicKeyBase64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyBase64);

        try
        {
            var publicKeyBytes = Convert.FromBase64String(publicKeyBase64.Trim());
            var signatureBytes = Convert.FromBase64String(signatureBase64);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out var bytesRead);
            if (bytesRead != publicKeyBytes.Length || ecdsa.KeySize != 256)
            {
                return false;
            }

            return ecdsa.VerifyData(
                data,
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
