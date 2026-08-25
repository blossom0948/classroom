namespace PhoneUnlock.Core.Protocol;

public static class ProtocolConstants
{
    public const int Version = 1;
    public const int ChallengeSizeBytes = 32;
    public const int DefaultAuthTimeoutSeconds = 30;
    public const int DefaultPairingTimeoutSeconds = 120;
    public const int Port = 48231;

    public const string AuthRequest = "AUTH_REQUEST";
    public const string AuthApproved = "AUTH_APPROVED";
    public const string AuthDenied = "AUTH_DENIED";
    public const string AuthExpired = "AUTH_EXPIRED";
    public const string DeviceHello = "DEVICE_HELLO";
    public const string DeviceHeartbeat = "DEVICE_HEARTBEAT";
    public const string RemoteUnlockRequest = "REMOTE_UNLOCK_REQUEST";
    public const string SecurityAlert = "SECURITY_ALERT";
}
