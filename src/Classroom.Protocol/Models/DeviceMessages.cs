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
    bool PolicyApplied);

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
