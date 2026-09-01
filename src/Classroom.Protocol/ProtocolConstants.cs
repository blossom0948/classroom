namespace Blossom.Classroom.Protocol;

public static class ProtocolConstants
{
    public const int Version = 1;
    // Screen frames travel through both the desktop IPC channel and the
    // student WebSocket. Keep one shared ceiling so a valid adaptive 720p
    // frame cannot be accepted by one hop and rejected by the next.
    public const int MaxMessageBytes = 128 * 1024;
    public const int MaxTargetDevices = 30;
    public const int MaxTextLength = 2_000;
    public const int MaxDisplaySeconds = 3_600;
    public const int MaxScreenFrameBytes = 72 * 1024;
    public const int MaxScreenFrameWidth = 1_280;
    public const int MaxScreenFrameHeight = 720;
    public const int ScreenShareMinimumIntervalMilliseconds = 750;
    public const int ScreenShareStandardIntervalMilliseconds = 1_000;
    public const int ScreenShareMaximumIntervalMilliseconds = 3_000;
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
