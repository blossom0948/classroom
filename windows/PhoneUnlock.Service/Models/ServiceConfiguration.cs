namespace PhoneUnlock.Service.Models;

public sealed record ServiceConfiguration
{
    public Guid ComputerId { get; init; } = Guid.NewGuid();
    public string ComputerName { get; init; } = Environment.MachineName;
    public List<PairedPhoneRecord> Phones { get; init; } = [];
    public string? ConfiguredAccountSid { get; init; }
    public string? ConfiguredQualifiedUsername { get; init; }
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
