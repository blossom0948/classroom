namespace PhoneUnlock.Service.Models;

public sealed record PairingPayload(
    int Version,
    Guid ComputerId,
    string ComputerName,
    string PairingToken,
    string Host,
    IReadOnlyList<string> Hosts,
    int Port,
    long ExpiresAt,
    string CertificateFingerprint,
    IReadOnlyList<WakeOnLanTarget> WakeOnLanTargets);

public sealed record WakeOnLanTarget(string MacAddress, string BroadcastAddress);

public sealed record PairRequest(
    string PhoneId,
    string PhoneName,
    string PublicKey);

public sealed record PairResponse(
    int Version,
    Guid ComputerId,
    string ComputerName,
    string PhoneId,
    string DeviceToken,
    int Port,
    string CertificateFingerprint);

public sealed class PairingSession(string tokenHash, string rawToken, DateTimeOffset expiresAt)
{
    public string TokenHash { get; } = tokenHash;
    public string RawToken { get; } = rawToken;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public int Consumed;
}
