using Blossom.Classroom.Protocol;
using Blossom.Classroom.Protocol.Models;
using Blossom.Classroom.Protocol.Security;
using Blossom.Classroom.Protocol.Serialization;
using Blossom.Classroom.Protocol.Validation;

var tests = new (string Name, Action Run)[]
{
    ("envelope serializes with version and camelCase fields", EnvelopeSerializationWorks),
    ("malformed and unsupported envelopes are rejected", InvalidEnvelopeIsRejected),
    ("hello and heartbeat can wait for a server-selected session", HeartbeatValidationWorks),
    ("activity keeps browser data to a hostname", ActivityValidationWorks),
    ("screen frames are bounded and require visible sharing", ScreenFrameValidationWorks),
    ("message command validates and canonicalizes", MessageCommandWorks),
    ("open URL rejects non-HTTPS and arbitrary targets", OpenUrlIsConstrained),
    ("approved app command has no shell field", ApprovedAppIsConstrained),
    ("screen sharing command requires an explicit state", ScreenShareCommandWorks),
    ("commands reject duplicate or oversized targets", TargetLimitsAreEnforced),
    ("student exit PIN verification messages are strictly validated", StudentExitPinVerificationWorks),
    ("protocol codec rejects oversized JSON", OversizedMessageIsRejected)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static void EnvelopeSerializationWorks()
{
    var deviceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    var sessionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    var envelope = ProtocolEnvelope<DeviceHeartbeat>.Create(
        ProtocolConstants.DeviceHeartbeat,
        new DeviceHeartbeat(
            deviceId,
            sessionId,
            "0.1.0-dev",
            DateTimeOffset.Parse("2026-08-28T00:00:00Z"),
            null,
            72,
            "wifi",
            true,
            NeedsHelp: true),
        DateTimeOffset.Parse("2026-08-28T00:00:00Z"));

    var json = ProtocolCodec.Serialize(envelope);
    Assert(json.Contains("\"version\":1"), "Protocol version was not serialized.");
    Assert(json.Contains("\"type\":\"DEVICE_HEARTBEAT\""), "Message type was not serialized.");
    Assert(json.Contains("\"deviceId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\""), "Device identity was not serialized.");
    Assert(!json.Contains("password", StringComparison.OrdinalIgnoreCase), "Credential field leaked into protocol.");
    var parsed = ProtocolCodec.Deserialize<DeviceHeartbeat>(json);
    Assert(parsed.Payload.DeviceId == deviceId && parsed.Payload.BatteryPercent == 72 && parsed.Payload.NeedsHelp, "Heartbeat did not round trip.");
}

static void InvalidEnvelopeIsRejected()
{
    AssertThrows<ProtocolValidationException>(() =>
        ProtocolCodec.Deserialize<DeviceHeartbeat>("{\"version\":99,\"type\":\"DEVICE_HEARTBEAT\",\"messageId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"timestampUtc\":\"2026-08-28T00:00:00Z\",\"payload\":{}}"));
    AssertThrows<ProtocolValidationException>(() =>
        ProtocolCodec.Deserialize<DeviceHeartbeat>("{\"version\":1,\"type\":\"UNKNOWN\",\"messageId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"timestampUtc\":\"2026-08-28T00:00:00Z\",\"payload\":{}}"));
}

static void HeartbeatValidationWorks()
{
    var heartbeat = new DeviceHeartbeat(
        Guid.NewGuid(),
        Guid.Empty,
        "0.1.0-dev",
        DateTimeOffset.UtcNow,
        null,
        null,
        "wifi",
        false);
    ProtocolValidation.ValidateHeartbeat(heartbeat);
    ProtocolValidation.ValidateHello(new DeviceHello(
        heartbeat.DeviceId,
        Guid.Empty,
        heartbeat.AgentVersion));
}

static void ActivityValidationWorks()
{
    ProtocolValidation.ValidateActivity(new ActivitySnapshot(
        "Chrome",
        "chrome.exe",
        "classroom.google.com",
        null,
        DateTimeOffset.UtcNow));

    AssertThrows<ProtocolValidationException>(() =>
        ProtocolValidation.ValidateActivity(new ActivitySnapshot(
            "Chrome",
            "chrome.exe",
            "https://example.com/private?q=secret",
            null,
            DateTimeOffset.UtcNow)));
}

static void ScreenFrameValidationWorks()
{
    var frame = new ScreenFrame(
        "image/jpeg",
        Convert.ToBase64String([0xff, 0xd8, 0xff, 0xd9]),
        480,
        270,
        DateTimeOffset.UtcNow);
    ProtocolValidation.ValidateScreenFrame(frame);
    var heartbeat = new DeviceHeartbeat(
        Guid.NewGuid(),
        Guid.Empty,
        "0.4.0",
        DateTimeOffset.UtcNow,
        null,
        80,
        "wifi",
        false,
        frame,
        true);
    ProtocolValidation.ValidateHeartbeat(heartbeat);
    ProtocolValidation.ValidateScreenFrame(frame with
    {
        Width = ProtocolConstants.MaxScreenFrameWidth,
        Height = ProtocolConstants.MaxScreenFrameHeight
    });
    AssertThrows<ProtocolValidationException>(() =>
        ProtocolValidation.ValidateHeartbeat(heartbeat with { ScreenSharingEnabled = false }));
    AssertThrows<ProtocolValidationException>(() =>
        ProtocolValidation.ValidateScreenFrame(frame with { Width = ProtocolConstants.MaxScreenFrameWidth + 1 }));
}

static void MessageCommandWorks()
{
    var command = new CommandRequest(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
        new[] { Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc") },
        ClassroomCommandKind.Message,
        "10분 뒤 과제를 제출해주세요.",
        DisplaySeconds: 10);

    ProtocolValidation.ValidateCommand(command);
    var canonical = CanonicalCommandPayload.Create(command);
    Assert(canonical.StartsWith("CLASSROOM-COMMAND-V1\n", StringComparison.Ordinal), "Canonical prefix changed.");
    Assert(canonical.Contains("kind=MESSAGE"), "Command kind was not canonicalized.");
    Assert(canonical.Contains("screenShareIntervalMilliseconds=-"), "Command timing must be canonicalized.");
    Assert(!canonical.EndsWith('\n'), "Canonical payload has a trailing newline.");
}

static void OpenUrlIsConstrained()
{
    var baseCommand = new CommandRequest(
        Guid.NewGuid(),
        Guid.NewGuid(),
        new[] { Guid.NewGuid() },
        ClassroomCommandKind.OpenUrl,
        Url: "https://classroom.google.com");

    ProtocolValidation.ValidateCommand(baseCommand);
    AssertThrows<ProtocolValidationException>(() =>
        ProtocolValidation.ValidateCommand(baseCommand with { Url = "http://insecure.example" }));
    AssertThrows<ProtocolValidationException>(() =>
        ProtocolValidation.ValidateCommand(baseCommand with { Url = "powershell://run?command=whoami" }));
    AssertThrows<ProtocolValidationException>(() =>
        ProtocolValidation.ValidateCommand(baseCommand with { Url = "https://user:pass@example.com" }));
}

static void ApprovedAppIsConstrained()
{
    var command = new CommandRequest(
        Guid.NewGuid(),
        Guid.NewGuid(),
        new[] { Guid.NewGuid() },
        ClassroomCommandKind.LaunchApprovedApp,
        ApprovedAppId: "vscode");

    ProtocolValidation.ValidateCommand(command);
    AssertThrows<ProtocolValidationException>(() =>
        ProtocolValidation.ValidateCommand(command with { ApprovedAppId = "powershell -Command whoami" }));
}

static void ScreenShareCommandWorks()
{
    var command = new CommandRequest(
        Guid.NewGuid(),
        Guid.NewGuid(),
        new[] { Guid.NewGuid() },
        ClassroomCommandKind.ScreenShare,
        ScreenShareEnabled: true);
    ProtocolValidation.ValidateCommand(command);
    AssertThrows<ProtocolValidationException>(() =>
        ProtocolValidation.ValidateCommand(command with { ScreenShareEnabled = null }));
    ProtocolValidation.ValidateCommand(command with
    {
        ScreenShareIntervalMilliseconds = ProtocolConstants.ScreenShareMinimumIntervalMilliseconds
    });
    AssertThrows<ProtocolValidationException>(() =>
        ProtocolValidation.ValidateCommand(command with
        {
            ScreenShareIntervalMilliseconds = ProtocolConstants.ScreenShareMinimumIntervalMilliseconds - 1
        }));
}

static void TargetLimitsAreEnforced()
{
    var targets = Enumerable.Range(0, ProtocolConstants.MaxTargetDevices + 1)
        .Select(_ => Guid.NewGuid())
        .ToArray();
    var command = new CommandRequest(
        Guid.NewGuid(),
        Guid.NewGuid(),
        targets,
        ClassroomCommandKind.Message,
        "hello");

    AssertThrows<ProtocolValidationException>(() => ProtocolValidation.ValidateCommand(command));
    AssertThrows<ProtocolValidationException>(() =>
        ProtocolValidation.ValidateCommand(command with
        {
            TargetDeviceIds = new[] { targets[0], targets[0] }
        }));
}

static void StudentExitPinVerificationWorks()
{
    var request = new DeviceExitPinVerificationRequest(
        Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
        "school-exit-2026");
    ProtocolValidation.ValidateExitPinVerification(request);

    var envelope = ProtocolEnvelope<DeviceExitPinVerificationRequest>.Create(
        ProtocolConstants.DeviceExitPinVerificationRequest,
        request);
    var json = ProtocolCodec.Serialize(envelope);
    var parsed = ProtocolCodec.Deserialize<DeviceExitPinVerificationRequest>(json);
    Assert(parsed.Type == ProtocolConstants.DeviceExitPinVerificationRequest,
        "Exit PIN request type was not retained.");
    Assert(parsed.Payload.RequestId == request.RequestId && parsed.Payload.Pin == request.Pin,
        "Exit PIN request did not round trip.");

    ProtocolValidation.ValidateExitPinVerificationResponse(
        new DeviceExitPinVerificationResponse(request.RequestId, false, "EXIT_PIN_REJECTED", "Not approved."));
    AssertThrows<ProtocolValidationException>(() =>
        ProtocolValidation.ValidateExitPinVerification(request with { Pin = "12345" }));
    AssertThrows<ProtocolValidationException>(() =>
        ProtocolValidation.ValidateExitPinVerification(request with { Pin = "valid\nline" }));
}

static void OversizedMessageIsRejected()
{
    var payload = new ErrorMessage("TEST", new string('x', ProtocolConstants.MaxMessageBytes));
    var envelope = ProtocolEnvelope<ErrorMessage>.Create(ProtocolConstants.Error, payload);
    AssertThrows<ProtocolValidationException>(() => ProtocolCodec.Serialize(envelope));
}

static void AssertThrows<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
