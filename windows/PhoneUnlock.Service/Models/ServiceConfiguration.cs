namespace PhoneUnlock.Service.Models;

public sealed record ServiceConfiguration
{
    public Guid ComputerId { get; init; } = Guid.NewGuid();
    public string ComputerName { get; init; } = Environment.MachineName;
    public List<PairedPhoneRecord> Phones { get; init; } = [];
    public string? ConfiguredAccountSid { get; init; }
    public string? ConfiguredQualifiedUsername { get; init; }
    public string? PreferredPhoneId { get; init; }
    public bool ProximityLockEnabled { get; init; }
    public bool ProximityUnlockEnabled { get; init; }
    public int ProximityGraceSeconds { get; init; } = 30;
    public DateTimeOffset? LastSuccessfulPhoneAuth { get; init; }
}

public sealed record PairedPhoneRecord(
    string PhoneId,
    string PhoneName,
    string PublicKey,
    string DeviceTokenHash,
    DateTimeOffset PairedAt,
    DateTimeOffset? LastSeen,
    bool Enabled);
