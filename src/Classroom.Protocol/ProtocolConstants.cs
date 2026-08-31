namespace Blossom.Classroom.Protocol;

public static class ProtocolConstants
{
    public const int Version = 1;
    public const int MaxMessageBytes = 64 * 1024;
    public const int MaxTargetDevices = 30;
    public const int MaxTextLength = 2_000;
    public const int MaxDisplaySeconds = 3_600;
    public const int MaxScreenFrameBytes = 36 * 1024;
    public const int MaxScreenFrameWidth = 640;
    public const int MaxScreenFrameHeight = 480;
    public const int HeartbeatIntervalSeconds = 10;

    public const string DeviceEnrollmentRequest = "DEVICE_ENROLLMENT_REQUEST";
    public const string DeviceEnrollmentResponse = "DEVICE_ENROLLMENT_RESPONSE";
    public const string DeviceSessionAccepted = "DEVICE_SESSION_ACCEPTED";
    public const string DeviceHello = "DEVICE_HELLO";
    public const string DeviceHeartbeat = "DEVICE_HEARTBEAT";
    public const string DeviceStatus = "DEVICE_STATUS";
    public const string DeviceExitPinVerificationRequest = "DEVICE_EXIT_PIN_VERIFICATION_REQUEST";
    public const string DeviceExitPinVerificationResponse = "DEVICE_EXIT_PIN_VERIFICATION_RESPONSE";
    public const string CommandRequest = "COMMAND_REQUEST";
    public const string CommandAck = "COMMAND_ACK";
    public const string CommandResult = "COMMAND_RESULT";
    public const string Error = "ERROR";

    public static bool IsKnownMessageType(string type) => type is
        DeviceEnrollmentRequest
        or DeviceEnrollmentResponse
        or DeviceSessionAccepted
        or DeviceHello
        or DeviceHeartbeat
        or DeviceStatus
        or DeviceExitPinVerificationRequest
        or DeviceExitPinVerificationResponse
        or CommandRequest
        or CommandAck
        or CommandResult
        or Error;
}
