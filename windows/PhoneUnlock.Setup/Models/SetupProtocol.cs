namespace PhoneUnlock.Setup.Models;

public static class SetupCommands
{
    public const string Status = "STATUS";
    public const string CreatePairing = "CREATE_PAIRING";
    public const string StoreCredential = "STORE_CREDENTIAL";
    public const string DeleteCredential = "DELETE_CREDENTIAL";
    public const string RemovePhone = "REMOVE_PHONE";
    public const string TestAuthentication = "TEST_AUTH";
    public const string SetPreferredPhone = "SET_PREFERRED_PHONE";
    public const string GetAuditLog = "GET_AUDIT_LOG";
    public const string Diagnostics = "DIAGNOSTICS";
    public const string SetProximityLock = "SET_PROXIMITY_LOCK";
    public const string SetProximityUnlock = "SET_PROXIMITY_UNLOCK";
    public const string SetAutoLockProfile = "SET_AUTO_LOCK_PROFILE";
    public const string SetBluetoothRssi = "SET_BLUETOOTH_RSSI";
    public const string SetRemoteUnlock = "SET_REMOTE_UNLOCK";
    public const string SetPresenceSensor = "SET_PRESENCE_SENSOR";
    public const string ListSmartThingsSensors = "LIST_SMARTTHINGS_SENSORS";
}

public sealed record SetupRequest(
    string Command,
    string? QualifiedUsername = null,
    string? Password = null,
    string? PhoneId = null,
    bool? Enabled = null,
    int? GraceSeconds = null,
    int? Limit = null,
    string? Url = null,
    string? EntityId = null,
    string? Token = null,
    string? Profile = null,
    int? RssiThreshold = null,
    string? SensorProtocol = null,
    string? ComponentId = null,
    string? CapabilityId = null,
    string? AttributeName = null);

public sealed record SetupResponse(bool Success, string Code, string Message, string? Data);

public sealed record SetupStatus(
    Guid ComputerId,
    string ComputerName,
    bool CredentialConfigured,
    string? ConfiguredAccountSid,
    string? ConfiguredQualifiedUsername,
    IReadOnlyList<PhoneStatus> Phones,
    string? PreferredPhoneId,
    bool ProximityLockEnabled,
    bool ProximityUnlockEnabled,
    int ProximityGraceSeconds,
    string AutoLockProfile,
    bool BluetoothRssiEnabled,
    int BluetoothRssiThreshold,
    bool RemoteUnlockEnabled,
    bool PresenceSensorEnabled,
    string PresenceSensorProtocol,
    string? PresenceSensorBaseUrl,
    string? PresenceSensorEntityId,
    string PresenceSensorComponentId,
    string PresenceSensorCapabilityId,
    string PresenceSensorAttributeName,
    int PresenceSensorGraceSeconds,
    DateTimeOffset? LastSuccessfulPhoneAuth,
    bool ReadyToEnableCredentialProvider,
    bool InteractiveAgentConnected);

public sealed record PhoneStatus(
    string PhoneId,
    string PhoneName,
    bool Enabled,
    bool Connected,
    DateTimeOffset? LastSeen);

public sealed record SmartThingsSensorOption(
    string DeviceId,
    string Label,
    string ComponentId,
    string CapabilityId,
    string AttributeName,
    string? CurrentState)
{
    public string DisplayName => $"{Label} · {CapabilityId}/{AttributeName} · {CurrentState ?? "상태 미확인"}";
}

public sealed record AuditEntry(
    DateTimeOffset OccurredAt,
    string EventType,
    string Outcome,
    string? PhoneId,
    string? PhoneName,
    string? RemoteIp,
    Guid? RequestId,
    string Message,
    bool Suspicious);

public sealed record PhoneConnectionStatus(
    string PhoneId,
    string PhoneName,
    bool Enabled,
    bool Connected,
    DateTimeOffset? LastSeen,
    DateTimeOffset? LastHeartbeat,
    string? RemoteIp);

public sealed record SetupDiagnostics(
    string ServiceVersion,
    int ListeningPort,
    IReadOnlyList<string> LocalAddresses,
    string CertificateFingerprint,
    IReadOnlyList<PhoneConnectionStatus> Phones,
    IReadOnlyList<AuditEntry> RecentAudit,
    bool ProximityLockEnabled,
    bool ProximityUnlockEnabled,
    int ProximityGraceSeconds,
    string AutoLockProfile,
    bool BluetoothRssiEnabled,
    int BluetoothRssiThreshold,
    bool RemoteUnlockEnabled,
    bool PresenceSensorEnabled,
    string PresenceSensorProtocol,
    string? PresenceSensorBaseUrl,
    string? PresenceSensorEntityId,
    string PresenceSensorComponentId,
    string PresenceSensorCapabilityId,
    string PresenceSensorAttributeName,
    int PresenceSensorGraceSeconds,
    bool InteractiveAgentConnected);

public sealed record PhoneSelectionItem(string PhoneId, string DisplayName);
