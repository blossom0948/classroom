namespace PhoneUnlock.Service.Configuration;

public static class ServiceConstants
{
    public const string ServiceName = "PhoneUnlockService";
    public const string AuthPipeName = "PhoneUnlock.Auth";
    public const string SetupPipeName = "PhoneUnlock.Setup";
    public const string CredentialTarget = "PhoneUnlock/WindowsLogon";
    public const int Port = 48231;
    public const int PairingLifetimeSeconds = 120;
    public const int AuthenticationTimeoutSeconds = 30;
    public const int MaxPipeLineLength = 64 * 1024;
}
