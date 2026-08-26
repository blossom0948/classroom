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
    public bool SmartArrivalEnabled { get; init; }
    public int ProximityGraceSeconds { get; init; } = 30;
    public string AutoLockProfile { get; init; } = "standard";
    public bool BluetoothRssiEnabled { get; init; }
    public int BluetoothRssiThreshold { get; init; } = -75;
    public bool RemoteUnlockEnabled { get; init; }
    public bool RemotePowerEnabled { get; init; }
    public DateTimeOffset? PauseUntil { get; init; }
    public bool PauseIndefinitely { get; init; }
    public bool PresenceSensorEnabled { get; init; }
    public string PresenceSensorProtocol { get; init; } = "windows";
    public string? PresenceSensorBaseUrl { get; init; }
    public string? PresenceSensorEntityId { get; init; }
    public string PresenceSensorComponentId { get; init; } = "main";
    public string PresenceSensorCapabilityId { get; init; } = "occupancySensor";
    public string PresenceSensorAttributeName { get; init; } = "occupancy";
    public int PresenceSensorGraceSeconds { get; init; } = 10;
    public DateTimeOffset? LastSuccessfulPhoneAuth { get; init; }

    public bool IsPaused(DateTimeOffset? now = null) => PauseIndefinitely
        || PauseUntil is { } until && until > (now ?? DateTimeOffset.UtcNow);
}

public sealed record PairedPhoneRecord(
    string PhoneId,
    string PhoneName,
    string PublicKey,
    string DeviceTokenHash,
    DateTimeOffset PairedAt,
    DateTimeOffset? LastSeen,
    bool Enabled);
