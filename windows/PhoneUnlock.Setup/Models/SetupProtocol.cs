namespace PhoneUnlock.Setup.Models;

public static class SetupCommands
{
    public const string Status = "STATUS";
    public const string CreatePairing = "CREATE_PAIRING";
    public const string StoreCredential = "STORE_CREDENTIAL";
    public const string DeleteCredential = "DELETE_CREDENTIAL";
    public const string RemovePhone = "REMOVE_PHONE";
    public const string TestAuthentication = "TEST_AUTH";
}

public sealed record SetupRequest(
    string Command,
    string? QualifiedUsername = null,
    string? Password = null,
    string? PhoneId = null);

public sealed record SetupResponse(bool Success, string Code, string Message, string? Data);

public sealed record SetupStatus(
    Guid ComputerId,
    string ComputerName,
    bool CredentialConfigured,
    string? ConfiguredAccountSid,
    string? ConfiguredQualifiedUsername,
    IReadOnlyList<PhoneStatus> Phones,
    DateTimeOffset? LastSuccessfulPhoneAuth,
    bool ReadyToEnableCredentialProvider);

public sealed record PhoneStatus(
    string PhoneId,
    string PhoneName,
    bool Enabled,
    bool Connected,
    DateTimeOffset? LastSeen);
