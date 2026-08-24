namespace PhoneUnlock.Service.Models;

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
}

public sealed record SetupRequest(
    string Command,
    string? QualifiedUsername = null,
    string? Password = null,
    string? PhoneId = null,
    bool? Enabled = null,
    int? GraceSeconds = null,
    int? Limit = null);

public sealed record SetupResponse(
    bool Success,
    string Code,
    string Message,
    string? Data = null);

public sealed record SetupStatus(
    Guid ComputerId,
    string ComputerName,
    bool CredentialConfigured,
    string? ConfiguredAccountSid,
    string? ConfiguredQualifiedUsername,
    IReadOnlyList<PhoneStatus> Phones,
    string? PreferredPhoneId,
    bool ProximityLockEnabled,
    int ProximityGraceSeconds,
    DateTimeOffset? LastSuccessfulPhoneAuth,
    bool ReadyToEnableCredentialProvider);

public sealed record PhoneStatus(
    string PhoneId,
    string PhoneName,
    bool Enabled,
    bool Connected,
    DateTimeOffset? LastSeen);

public sealed record SetupDiagnostics(
    string ServiceVersion,
    int ListeningPort,
    IReadOnlyList<string> LocalAddresses,
    string CertificateFingerprint,
    IReadOnlyList<PhoneConnectionStatus> Phones,
    IReadOnlyList<AuditEntry> RecentAudit);
