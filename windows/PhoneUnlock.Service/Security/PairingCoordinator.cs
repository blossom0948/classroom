using System.Collections.Concurrent;
using System.Security.Cryptography;
using PhoneUnlock.Core.Security;
using PhoneUnlock.Service.Configuration;
using PhoneUnlock.Service.Models;
using PhoneUnlock.Service.Storage;

namespace PhoneUnlock.Service.Security;

public sealed class PairingCoordinator(
    ConfigurationStore configurationStore,
    CertificateManager certificateManager,
    AuditLogStore auditLog)
{
    private readonly ConcurrentDictionary<string, PairingSession> sessions = new(StringComparer.Ordinal);

    public async Task<PairingPayload> CreateAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await configurationStore.GetAsync(cancellationToken);
        var token = TokenSecurity.CreateToken();
        var hash = TokenSecurity.HashToken(token);
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(ServiceConstants.PairingLifetimeSeconds);
        sessions[hash] = new PairingSession(hash, token, expiresAt);
        RemoveExpired();

        var hosts = CertificateManager.GetLocalAddresses().Select(address => address.ToString()).ToArray();
        if (hosts.Length == 0)
        {
            throw new InvalidOperationException("휴대폰과 연결할 로컬 네트워크 주소를 찾지 못했습니다. Wi-Fi 또는 이더넷 연결을 확인하세요.");
        }
        var certificate = certificateManager.LoadOrCreate();
        return new PairingPayload(
            1,
            configuration.ComputerId,
            configuration.ComputerName,
            token,
            hosts[0],
            hosts,
            ServiceConstants.Port,
            expiresAt.ToUnixTimeSeconds(),
            CertificateManager.GetSha256Fingerprint(certificate));
    }

    public async Task<PairResponse?> PairAsync(
        string pairingToken,
        PairRequest request,
        string? remoteIp = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePairRequest(request);
        var hash = TokenSecurity.HashToken(pairingToken);
        if (!sessions.TryGetValue(hash, out var session)
            || session.ExpiresAt < DateTimeOffset.UtcNow
            || Interlocked.CompareExchange(ref session.Consumed, 1, 0) != 0)
        {
            return null;
        }

        sessions.TryRemove(hash, out _);
        var deviceToken = TokenSecurity.CreateToken();
        var deviceTokenHash = TokenSecurity.HashToken(deviceToken);
        var updated = await configurationStore.UpdateAsync(configuration =>
        {
            var phones = configuration.Phones
                .Where(phone => !string.Equals(phone.PhoneId, request.PhoneId, StringComparison.Ordinal))
                .ToList();
            phones.Add(new PairedPhoneRecord(
                request.PhoneId,
                request.PhoneName.Trim(),
                request.PublicKey,
                deviceTokenHash,
                DateTimeOffset.UtcNow,
                null,
                true));
            return configuration with { Phones = phones };
        }, cancellationToken);

        var certificate = certificateManager.LoadOrCreate();
        await auditLog.AppendAsync(new AuditEntry(
            DateTimeOffset.UtcNow,
            "PAIRING",
            "SUCCESS",
            request.PhoneId,
            request.PhoneName.Trim(),
            remoteIp,
            null,
            "휴대폰 페어링 완료",
            Suspicious: false), cancellationToken);
        return new PairResponse(
            1,
            updated.ComputerId,
            updated.ComputerName,
            request.PhoneId,
            deviceToken,
            ServiceConstants.Port,
            CertificateManager.GetSha256Fingerprint(certificate));
    }

    private static void ValidatePairRequest(PairRequest request)
    {
        if (!Guid.TryParse(request.PhoneId, out _))
        {
            throw new ArgumentException("phoneId must be a UUID.");
        }

        if (string.IsNullOrWhiteSpace(request.PhoneName) || request.PhoneName.Length > 100)
        {
            throw new ArgumentException("phoneName is invalid.");
        }

        byte[] publicKey;
        try
        {
            publicKey = Convert.FromBase64String(request.PublicKey);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            if (bytesRead != publicKey.Length || ecdsa.KeySize != 256)
            {
                throw new ArgumentException("publicKey must be an EC P-256 SubjectPublicKeyInfo key.");
            }
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("publicKey must be Base64.", exception);
        }
        catch (CryptographicException exception)
        {
            throw new ArgumentException("publicKey is not valid.", exception);
        }
    }

    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in sessions)
        {
            if (pair.Value.ExpiresAt < now)
            {
                sessions.TryRemove(pair.Key, out _);
            }
        }
    }
}
