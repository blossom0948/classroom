namespace PhoneUnlock.Core.Models;

public sealed record PairedDevice(
    string PhoneId,
    string PhoneName,
    string PublicKey,
    DateTimeOffset PairedAt,
    DateTimeOffset? LastSeen,
    bool Enabled);
