using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PhoneUnlock.Service.Configuration;

namespace PhoneUnlock.Service.Security;

public sealed class CertificateManager(ServicePaths paths)
{
    private const string LegacyPfxPassword = "PhoneUnlock-Local-Service-Certificate";

    public X509Certificate2 LoadOrCreate()
    {
        if (File.Exists(paths.CertificateFile))
        {
            try
            {
                return LoadCertificate(password: null);
            }
            catch (CryptographicException)
            {
                // Migrate certificates created by development builds. The file ACL, not a
                // password embedded in the application, is the actual private-key boundary.
                using var legacy = LoadCertificate(LegacyPfxPassword);
                File.WriteAllBytes(paths.CertificateFile, legacy.Export(X509ContentType.Pfx));
                RestrictFile();
                return LoadCertificate(password: null);
            }
        }

        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(
            $"CN=Phone Unlock {Environment.MachineName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },
            critical: true));

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddDnsName(Environment.MachineName);
        names.AddIpAddress(IPAddress.Loopback);
        foreach (var address in GetLocalAddresses())
        {
            names.AddIpAddress(address);
        }

        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(5));
        File.WriteAllBytes(paths.CertificateFile, generated.Export(X509ContentType.Pfx));
        RestrictFile();
        return LoadCertificate(password: null);
    }

    private X509Certificate2 LoadCertificate(string? password) => new(
        paths.CertificateFile,
        password,
        X509KeyStorageFlags.DefaultKeySet);

    private void RestrictFile()
    {
        if (paths.RestrictPermissions)
        {
            SecureFilePermissions.RestrictFile(paths.CertificateFile);
        }
    }

    public static string GetSha256Fingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData));

    public static IReadOnlyList<IPAddress> GetLocalAddresses() => NetworkInterface
        .GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
        .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
        .Select(address => address.Address)
        .Where(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        .Where(address => !IPAddress.IsLoopback(address) && !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
        .Distinct()
        .ToArray();
}
