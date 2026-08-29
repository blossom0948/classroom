using Blossom.Classroom.Core.Security;
using Blossom.Classroom.Protocol.Models;
using Blossom.Classroom.Server.Configuration;
using Blossom.Classroom.Server.Models;
using Blossom.Classroom.Server.Storage;

var tests = new (string Name, Action Run)[]
{
    ("enrollment binds a server-issued device to a student", EnrollmentBindsIdentity),
    ("device token authentication rejects wrong tokens", DeviceAuthenticationIsBound),
    ("session and heartbeat create an online status", HeartbeatUpdatesStatus),
    ("commands are queued and ACK/result are audited", CommandsAreTracked),
    ("teachers cannot access unassigned classes", TeacherScopeIsEnforced),
    ("sqlite restores sessions and enrollment state", SqliteRestoresState),
    ("teacher session tokens can be revoked", TeacherSessionsCanBeRevoked),
    ("password rotation revokes other teacher sessions", PasswordRotationRevokesOtherSessions)
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

static void EnrollmentBindsIdentity()
{
    var fixture = CreateFixture();
    var ticket = fixture.Store.CreateEnrollmentTicket(
        fixture.TeacherId,
        fixture.ClassId,
        fixture.StudentId,
        "김민수");
    var result = fixture.Store.Enroll(new DeviceEnrollmentRequest(
        ticket.DeviceId,
        "STUDENT-01",
        "0.1.0-dev",
        ticket.EnrollmentToken));

    Assert(result.Succeeded && result.Value is not null, "Enrollment did not succeed.");
    Assert(result.Value!.DeviceId == ticket.DeviceId, "Server changed the ticket device ID.");
    Assert(result.Value.StudentId == fixture.StudentId, "Student identity was not bound by the server.");
    Assert(fixture.Store.Enroll(new DeviceEnrollmentRequest(
        ticket.DeviceId,
        "STUDENT-01",
        "0.1.0-dev",
        ticket.EnrollmentToken)).Code == "ENROLLMENT_USED", "Enrollment ticket was reusable.");
}

static void DeviceAuthenticationIsBound()
{
    var fixture = CreateFixture();
    var ticket = fixture.Store.CreateEnrollmentTicket(
        fixture.TeacherId,
        fixture.ClassId,
        fixture.StudentId,
        "김민수");
    var enrollment = fixture.Store.Enroll(new DeviceEnrollmentRequest(
        ticket.DeviceId,
        "STUDENT-01",
        "0.1.0-dev",
        ticket.EnrollmentToken));
    Assert(enrollment.Value is not null, "Enrollment response was empty.");

    Assert(!fixture.Store.TryAuthenticateDevice(
        ticket.DeviceId,
        "wrong-token",
        out _), "Wrong device token was accepted.");
    Assert(fixture.Store.TryAuthenticateDevice(
        ticket.DeviceId,
        enrollment.Value!.DeviceToken,
        out var identity), "Issued device token was rejected.");
    Assert(identity is not null && identity.StudentId == fixture.StudentId, "Authenticated identity was not server-bound.");
}

static void HeartbeatUpdatesStatus()
{
    var fixture = CreateEnrolledFixture();
    var session = fixture.Store.StartSession(fixture.TeacherId, fixture.ClassId, "정보");
    Assert(fixture.Store.TryAuthenticateDevice(
        fixture.DeviceId,
        fixture.DeviceToken,
        out var identity) && identity is not null, "Device authentication failed.");
    Assert(fixture.Store.TryOpenConnection(identity!, session.SessionId, out var code, out var message),
        $"{code}: {message}");

    var heartbeat = new DeviceHeartbeat(
        fixture.DeviceId,
        session.SessionId,
        "0.1.0-dev",
        DateTimeOffset.UtcNow,
        new ActivitySnapshot("Chrome", "chrome.exe", "classroom.google.com", null, DateTimeOffset.UtcNow),
        72,
        "wifi",
        true);
    var result = fixture.Store.RecordHeartbeat(identity!, heartbeat);
    Assert(result.Succeeded, $"{result.Code}: {result.Message}");

    var status = fixture.Store.GetClassStatuses(fixture.TeacherId, fixture.ClassId).Single();
    Assert(status.Online, "Heartbeat did not make the device online.");
    Assert(status.StudentId == fixture.StudentId, "Status exposed a client-claimed student identity.");
    Assert(status.Activity?.BrowserDomain == "classroom.google.com", "Activity domain was not retained.");
}

static void CommandsAreTracked()
{
    var fixture = CreateEnrolledFixture();
    var session = fixture.Store.StartSession(fixture.TeacherId, fixture.ClassId, "정보");
    var command = new CommandRequest(
        Guid.NewGuid(),
        session.SessionId,
        new[] { fixture.DeviceId },
        ClassroomCommandKind.Message,
        "10분 뒤 과제를 제출해주세요.",
        DisplaySeconds: 10);

    var dispatch = fixture.Store.QueueCommand(fixture.TeacherId, fixture.ClassId, command);
    Assert(dispatch.Succeeded && dispatch.Value?.QueuedCount == 1, "Command was not queued.");
    var queued = fixture.Store.WaitForCommandAsync(fixture.DeviceId, CancellationToken.None)
        .AsTask()
        .GetAwaiter()
        .GetResult();
    Assert(queued.RequestId == command.RequestId, "Queued command was changed.");

    Assert(fixture.Store.TryAuthenticateDevice(
        fixture.DeviceId,
        fixture.DeviceToken,
        out var identity) && identity is not null, "Device authentication failed.");
    var acknowledgment = new CommandAck(
        command.RequestId,
        fixture.DeviceId,
        true,
        null,
        DateTimeOffset.UtcNow);
    Assert(fixture.Store.RecordCommandAck(identity!, acknowledgment).Succeeded, "Command ACK was not tracked.");

    var result = new CommandResult(
        command.RequestId,
        fixture.DeviceId,
        true,
        "APPLIED",
        "Message displayed.",
        DateTimeOffset.UtcNow);
    Assert(fixture.Store.RecordCommandResult(identity!, result).Succeeded, "Command result was not tracked.");

    var audit = fixture.Store.GetAuditEvents(fixture.TeacherId, fixture.ClassId);
    Assert(audit.Any(entry => entry.Action == "COMMAND_REQUEST" && entry.Result == "QUEUED"), "Queue audit is missing.");
    Assert(audit.Any(entry => entry.Action == "COMMAND_ACK"
        && entry.Result == "ACCEPTED"
        && entry.TeacherId == fixture.TeacherId), "ACK audit is missing the teacher identity.");
    Assert(audit.Any(entry => entry.Action == "COMMAND_RESULT"
        && entry.Result == "SUCCESS"
        && entry.TeacherId == fixture.TeacherId), "Result audit is missing the teacher identity.");
}

static void TeacherScopeIsEnforced()
{
    var fixture = CreateFixture();
    AssertThrows<ClassroomStoreException>(() =>
        fixture.Store.GetClassStatuses(fixture.TeacherId, Guid.NewGuid()));
}

static void SqliteRestoresState()
{
    var fixture = CreatePersistentFixture();
    var path = fixture.DatabasePath;
    try
    {
        ClassSessionSnapshot session;
        DeviceEnrollmentTicket ticket;
        using (var firstDatabase = new ClassroomDatabase(path))
        {
            var firstStore = new ClassroomStore(fixture.Options, firstDatabase);
            ticket = firstStore.CreateEnrollmentTicket(
                fixture.TeacherId,
                fixture.ClassId,
                fixture.StudentId,
                "영속 학생");
            session = firstStore.StartSession(fixture.TeacherId, fixture.ClassId, "영속 수업");
        }

        using (var restoredDatabase = new ClassroomDatabase(path))
        {
            var restoredStore = new ClassroomStore(fixture.Options, restoredDatabase);
            var restoredSession = restoredStore.GetActiveSession(fixture.TeacherId, fixture.ClassId);
            Assert(restoredSession?.SessionId == session.SessionId, "Active session was not restored from SQLite.");

            var enrollment = restoredStore.Enroll(new DeviceEnrollmentRequest(
                ticket.DeviceId,
                "PERSISTED-01",
                "0.1.0-dev",
                ticket.EnrollmentToken));
            Assert(enrollment.Succeeded && enrollment.Value is not null, "Persisted enrollment ticket was not usable after restart.");
            Assert(restoredStore.TryAuthenticateDevice(
                ticket.DeviceId,
                enrollment.Value!.DeviceToken,
                out _), "Persisted device token was not restored.");
        }
    }
    finally
    {
        if (Directory.Exists(fixture.RootPath))
        {
            Directory.Delete(fixture.RootPath, recursive: true);
        }
    }
}

static void TeacherSessionsCanBeRevoked()
{
    var fixture = CreatePersistentFixture();
    try
    {
        using (var database = new ClassroomDatabase(fixture.DatabasePath))
        {
            database.Initialize(fixture.Options);
            var token = database.CreateTeacherSession(fixture.TeacherId, TimeSpan.FromMinutes(5));
            Assert(database.TryValidateTeacherSession(token, out var teacherId) && teacherId == fixture.TeacherId,
                "Teacher session token was not accepted.");
            database.RevokeTeacherSession(token);
            Assert(!database.TryValidateTeacherSession(token, out _), "Revoked teacher session token was accepted.");
        }
    }
    finally
    {
        if (Directory.Exists(fixture.RootPath))
        {
            Directory.Delete(fixture.RootPath, recursive: true);
        }
    }
}

static void PasswordRotationRevokesOtherSessions()
{
    var fixture = CreatePersistentFixture();
    try
    {
        using var database = new ClassroomDatabase(fixture.DatabasePath);
        database.Initialize(fixture.Options);
        var currentToken = database.CreateTeacherSession(fixture.TeacherId, TimeSpan.FromMinutes(5));
        var otherToken = database.CreateTeacherSession(fixture.TeacherId, TimeSpan.FromMinutes(5));
        database.RevokeOtherTeacherSessions(fixture.TeacherId, currentToken);

        Assert(database.TryValidateTeacherSession(currentToken, out _), "Current teacher session was revoked during rotation.");
        Assert(!database.TryValidateTeacherSession(otherToken, out _), "An old teacher session survived password rotation.");
    }
    finally
    {
        if (Directory.Exists(fixture.RootPath))
        {
            Directory.Delete(fixture.RootPath, recursive: true);
        }
    }
}

static Fixture CreateEnrolledFixture()
{
    var fixture = CreateFixture();
    var ticket = fixture.Store.CreateEnrollmentTicket(
        fixture.TeacherId,
        fixture.ClassId,
        fixture.StudentId,
        "김민수");
    var enrollment = fixture.Store.Enroll(new DeviceEnrollmentRequest(
        ticket.DeviceId,
        "STUDENT-01",
        "0.1.0-dev",
        ticket.EnrollmentToken));
    Assert(enrollment.Succeeded && enrollment.Value is not null, "Fixture enrollment failed.");
    return fixture with
    {
        DeviceId = ticket.DeviceId,
        DeviceToken = enrollment.Value!.DeviceToken
    };
}

static Fixture CreateFixture()
{
    var schoolId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    var classId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    var teacherId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    var studentId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    var options = new ServerOptions(
        schoolId,
        classId,
        teacherId,
        TokenSecurity.HashToken("teacher-token"),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(10));
    return new Fixture(new ClassroomStore(options), teacherId, classId, studentId, Guid.Empty, string.Empty);
}

static PersistentFixture CreatePersistentFixture()
{
    var rootPath = Path.Combine(Path.GetTempPath(), $"classroom-server-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(rootPath);
    var schoolId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    var classId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    var teacherId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    var studentId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    var options = new ServerOptions(
        schoolId,
        classId,
        teacherId,
        TokenSecurity.HashToken("teacher-token"),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(10),
        Path.Combine(rootPath, "classroom.db"));
    return new PersistentFixture(rootPath, options, teacherId, classId, studentId, options.DatabasePath);
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

internal sealed record Fixture(
    ClassroomStore Store,
    Guid TeacherId,
    Guid ClassId,
    Guid StudentId,
    Guid DeviceId,
    string DeviceToken);

internal sealed record PersistentFixture(
    string RootPath,
    ServerOptions Options,
    Guid TeacherId,
    Guid ClassId,
    Guid StudentId,
    string DatabasePath);
