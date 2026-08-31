namespace Blossom.Classroom.Protocol.Models;

public sealed record DeviceEnrollmentTicket(
    Guid DeviceId,
    Guid SchoolId,
    Guid ClassId,
    Guid StudentId,
    DateTimeOffset ExpiresAtUtc,
    string EnrollmentToken,
    string JoinCode = "");

public sealed record DeviceEnrollmentRequest(
    Guid DeviceId,
    string DeviceName,
    string AgentVersion,
    string EnrollmentToken);

public sealed record DeviceEnrollmentResponse(
    Guid DeviceId,
    Guid SchoolId,
    Guid ClassId,
    Guid StudentId,
    string DeviceToken,
    DateTimeOffset IssuedAtUtc);

public sealed record DeviceSessionAccepted(
    Guid DeviceId,
    Guid SessionId,
    DateTimeOffset AcceptedAtUtc);

public sealed record DeviceHello(
    Guid DeviceId,
    Guid SessionId,
    string AgentVersion);

public sealed record DeviceHeartbeat(
    Guid DeviceId,
    Guid SessionId,
    string AgentVersion,
    DateTimeOffset ObservedAtUtc,
    ActivitySnapshot? Activity,
    int? BatteryPercent,
    string? NetworkStatus,
    bool PolicyApplied,
    ScreenFrame? ScreenFrame = null,
    bool ScreenSharingEnabled = false);

/// <summary>
/// A visible Student Desktop asks its already-authenticated local service to
/// check this value with the school server before an ordinary app exit. The
/// value is never persisted or sent to a teacher browser.
/// </summary>
public sealed record DeviceExitPinVerificationRequest(
    Guid RequestId,
    string Pin);

public sealed record DeviceExitPinVerificationResponse(
    Guid RequestId,
    bool Approved,
    string Code,
    string Message);

public sealed record ScreenFrame(
    string MimeType,
    string Base64Data,
    int Width,
    int Height,
    DateTimeOffset CapturedAtUtc);

public sealed record DeviceScreenFrameStatus(
    Guid DeviceId,
    string StudentDisplayName,
    ScreenFrame ScreenFrame,
    DateTimeOffset ReceivedAtUtc);

public sealed record DeviceStatus(
    Guid DeviceId,
    Guid StudentId,
    Guid ClassId,
    Guid SessionId,
    string StudentDisplayName,
    string ComputerName,
    bool Online,
    DateTimeOffset LastHeartbeatUtc,
    string AgentVersion,
    ActivitySnapshot? Activity,
    int? BatteryPercent,
    string? NetworkStatus,
    bool PolicyApplied,
    bool ScreenSharingAvailable);
