using Blossom.Classroom.Core.Security;
using Blossom.Classroom.Protocol.Models;
using Blossom.Classroom.Server.Configuration;
using Blossom.Classroom.Server.Models;
using Blossom.Classroom.Server.Security;
using Blossom.Classroom.Server.Storage;

var tests = new (string Name, Action Run)[]
{
    ("enrollment binds a server-issued device to a student", EnrollmentBindsIdentity),
    ("student join code reuses one device until regenerated", StudentJoinCodeRemainsValidUntilRegenerated),
    ("device token authentication rejects wrong tokens", DeviceAuthenticationIsBound),
    ("devices follow the server session without reinstalling", HeartbeatUpdatesStatus),
    ("commands are queued and ACK/result are audited", CommandsAreTracked),
    ("ending a session queues focus mode cleanup", SessionEndQueuesCleanup),
    ("revoked devices can no longer authenticate", RevokedDevicesAreRejected),
    ("teachers cannot access unassigned classes", TeacherScopeIsEnforced),
    ("sqlite restores sessions and enrollment state", SqliteRestoresState),
    ("teacher session tokens can be revoked", TeacherSessionsCanBeRevoked),
    ("password rotation revokes other teacher sessions", PasswordRotationRevokesOtherSessions),
    ("firebase identities create and reuse a teacher", FirebaseIdentityCreatesTeacher),
    ("administrators can grant school-wide access", AdministratorsCanGrantSchoolWideAccess),
    ("student app exit PINs are administrator-managed and server-verified", StudentExitPinsAreServerVerified),
    ("guest passwords create school-scoped read-only sessions", GuestPasswordsAreSchoolScoped)
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

static void StudentJoinCodeRemainsValidUntilRegenerated()
{
    var fixture = CreateFixture();
    var ticket = fixture.Store.CreateEnrollmentTicket(
        fixture.TeacherId,
        fixture.ClassId,
        fixture.StudentId,
        "코드 학생");

    var formattedCode = $"{ticket.JoinCode[..4]}-{ticket.JoinCode[4..]}";
    var result = fixture.Store.EnrollByJoinCode(new JoinCodeEnrollmentRequest(
        formattedCode,
        "STUDENT-CODE-01",
        "0.3.0"));

    Assert(result.Succeeded && result.Value is not null, "Join code enrollment did not succeed.");
    Assert(result.Value!.DeviceId != Guid.Empty, "Join code enrollment did not issue a device identity.");
    Assert(result.Value.StudentId == fixture.StudentId, "Join code enrollment changed the student identity.");

    var reused = fixture.Store.EnrollByJoinCode(new JoinCodeEnrollmentRequest(
        ticket.JoinCode,
        "STUDENT-CODE-02",
        "0.3.0"));
    Assert(reused.Succeeded && reused.Value is not null, "Persistent join code could not renew the installed device.");
    Assert(reused.Value!.DeviceId == result.Value.DeviceId, "Persistent join code created a duplicate device identity.");
    Assert(fixture.Store.GetClassStatuses(fixture.TeacherId, fixture.ClassId).Count == 1, "The same student appeared more than once after re-enrollment.");
    Assert(!fixture.Store.TryAuthenticateDevice(result.Value!.DeviceId, result.Value.DeviceToken, out _), "The previous device token remained active after renewal.");

    var regenerated = fixture.Store.CreateEnrollmentTicket(
        fixture.TeacherId,
        fixture.ClassId,
        fixture.StudentId,
        "코드 학생");
    Assert(regenerated.DeviceId == ticket.DeviceId, "Regeneration should update the student's existing code record.");
    Assert(regenerated.JoinCode != ticket.JoinCode, "Regeneration did not create a new code.");

    var oldCode = fixture.Store.EnrollByJoinCode(new JoinCodeEnrollmentRequest(
        ticket.JoinCode,
        "STUDENT-CODE-03",
        "0.3.0"));
    Assert(oldCode.Code == "ENROLLMENT_INVALID", "The previous code remained valid after regeneration.");

    var newCode = fixture.Store.EnrollByJoinCode(new JoinCodeEnrollmentRequest(
        regenerated.JoinCode,
        "STUDENT-CODE-03",
        "0.3.0"));
    Assert(newCode.Succeeded, "The regenerated code could not be used.");
}

static void HeartbeatUpdatesStatus()
{
    var fixture = CreateEnrolledFixture();
    Assert(fixture.Store.TryAuthenticateDevice(
        fixture.DeviceId,
        fixture.DeviceToken,
        out var identity) && identity is not null, "Device authentication failed.");
    Assert(fixture.Store.TryOpenConnection(
        identity!,
        Guid.Empty,
        out var acceptedSessionId,
        out var code,
        out var message),
        $"{code}: {message}");
    Assert(acceptedSessionId == Guid.Empty, "Device should be allowed to wait without an active session.");

    var session = fixture.Store.StartSession(fixture.TeacherId, fixture.ClassId, "정보");

    var heartbeat = new DeviceHeartbeat(
        fixture.DeviceId,
        Guid.Empty,
        "0.1.0-dev",
        DateTimeOffset.UtcNow,
        new ActivitySnapshot("Chrome", "chrome.exe", "classroom.google.com", null, DateTimeOffset.UtcNow),
        72,
        "wifi",
        true,
        new ScreenFrame(
            "image/jpeg",
            Convert.ToBase64String([0xff, 0xd8, 0xff, 0xd9]),
            480,
            270,
            DateTimeOffset.UtcNow),
        true,
        true);
    var result = fixture.Store.RecordHeartbeat(identity!, heartbeat);
    Assert(result.Succeeded, $"{result.Code}: {result.Message}");
    Assert(result.Value == session.SessionId, "Server did not move the device into the active session.");

    var status = fixture.Store.GetClassStatuses(fixture.TeacherId, fixture.ClassId).Single();
    Assert(status.Online, "Heartbeat did not make the device online.");
    Assert(status.StudentId == fixture.StudentId, "Status exposed a client-claimed student identity.");
    Assert(status.ScreenSharingAvailable, "Student status did not advertise screen sharing support.");
    Assert(status.NeedsHelp, "Student help request was not retained in class status.");
    var screens = fixture.Store.GetClassScreenFrames(fixture.TeacherId, fixture.ClassId);
    Assert(screens.Count == 1 && screens[0].DeviceId == fixture.DeviceId, "Current screen frame was not available to the teacher.");
    Assert(status.Activity?.BrowserDomain == "classroom.google.com", "Activity domain was not retained.");

    fixture.Store.EndSession(fixture.TeacherId, fixture.ClassId, session.SessionId);
    var endedStatus = fixture.Store.GetClassStatuses(fixture.TeacherId, fixture.ClassId).Single();
    Assert(!endedStatus.NeedsHelp, "A completed class retained a stale student help request.");
    var nextSession = fixture.Store.StartSession(fixture.TeacherId, fixture.ClassId, "수학");
    var nextHeartbeat = fixture.Store.RecordHeartbeat(identity!, heartbeat with { SessionId = session.SessionId });
    Assert(nextHeartbeat.Value == nextSession.SessionId, "Device stayed pinned to an ended session.");
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
    var status = fixture.Store.GetCommandStatus(fixture.TeacherId, fixture.ClassId, command.RequestId);
    Assert(status.Finished && status.CompletedCount == 1 && status.FailedCount == 0,
        "Teacher command status did not expose the completed result.");
}

static void SessionEndQueuesCleanup()
{
    var fixture = CreateEnrolledFixture();
    var session = fixture.Store.StartSession(fixture.TeacherId, fixture.ClassId, "정보");
    fixture.Store.EndSession(fixture.TeacherId, fixture.ClassId, session.SessionId);

    var cleanup = fixture.Store.WaitForCommandAsync(fixture.DeviceId, CancellationToken.None)
        .AsTask()
        .GetAwaiter()
        .GetResult();
    Assert(cleanup.Kind == ClassroomCommandKind.FocusMode && cleanup.FocusEnabled is false,
        "Session end did not queue a visible focus overlay cleanup.");
}

static void RevokedDevicesAreRejected()
{
    var fixture = CreateEnrolledFixture();
    var response = fixture.Store.RevokeDevice(fixture.TeacherId, fixture.ClassId, fixture.DeviceId);
    Assert(response.Status == "revoked", "Device revoke did not succeed.");
    Assert(!fixture.Store.TryAuthenticateDevice(fixture.DeviceId, fixture.DeviceToken, out _),
        "Revoked device token was still accepted.");
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

static void FirebaseIdentityCreatesTeacher()
{
    var rootPath = Path.Combine(Path.GetTempPath(), $"classroom-firebase-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(rootPath);
    try
    {
        var options = new ServerOptions(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            TokenSecurity.HashToken("teacher-token"),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(10),
            Path.Combine(rootPath, "classroom.db"));
        using var database = new ClassroomDatabase(options.DatabasePath);
        database.Initialize(options);
        var identity = new FirebaseIdentity(
            "firebase-subject-001",
            "teacher@example.edu",
            "새 교사",
            true,
            "google.com");

        var first = database.CreateOrGetFirebaseTeacher(identity, "수학 선생님", "수학");
        var second = database.CreateOrGetFirebaseTeacher(identity);
        Assert(first.Id == second.Id, "Firebase identity created duplicate teachers.");
        Assert(first.LoginName == "teacher@example.edu", "Firebase email was not used as the login name.");
        Assert(first.DisplayName == "수학 선생님", "Firebase signup display name was not saved.");
        Assert(database.GetClassesForTeacher(first.Id).Count == 1, "Firebase teacher did not receive a starter class.");
        Assert(database.GetClassesForTeacher(first.Id).Single().DefaultSubject == "수학", "Firebase signup subject was not saved.");
    }
    finally
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }
}

static void AdministratorsCanGrantSchoolWideAccess()
{
    var rootPath = Path.Combine(Path.GetTempPath(), $"classroom-admin-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(rootPath);
    try
    {
        var options = new ServerOptions(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            TokenSecurity.HashToken("teacher-token"),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(10),
            Path.Combine(rootPath, "classroom.db"));
        using var database = new ClassroomDatabase(options.DatabasePath);
        database.Initialize(options);
        Assert(database.IsTeacherAdmin(options.DevelopmentTeacherId), "Bootstrap teacher was not made an administrator.");

        var identity = new FirebaseIdentity(
            "firebase-admin-target-001",
            "teacher2@example.edu",
            "두 번째 선생님",
            true,
            "google.com");
        var target = database.CreateOrGetFirebaseTeacher(identity, "두 번째 선생님", "영어");
        Assert(target.SchoolId == options.DevelopmentSchoolId, "Firebase teachers were not placed in the school organization.");
        Assert(!database.IsTeacherAdmin(target.Id), "A normal teacher was made an administrator automatically.");

        Assert(database.SetTeacherAdmin(options.DevelopmentTeacherId, "teacher2@example.edu", true), "Existing teacher was not found by Google email.");
        Assert(database.IsTeacherAdmin(target.Id), "Administrator grant did not update the existing teacher.");
        Assert(database.SetTeacherAdmin(options.DevelopmentTeacherId, "pending.teacher", true) is false, "Pending grant should report no existing teacher.");
        Assert(database.GetActiveAdministratorGrants(options.DevelopmentSchoolId).Any(grant => grant.Identifier == "pending.teacher"), "Pending administrator grant was not persisted.");

        Assert(database.SetTeacherAdmin(options.DevelopmentTeacherId, "teacher2@example.edu", false), "Existing administrator was not found for removal.");
        Assert(!database.IsTeacherAdmin(target.Id), "Administrator removal did not update the existing teacher.");
    }
    finally
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }
}

static void StudentExitPinsAreServerVerified()
{
    var fixture = CreatePersistentFixture();
    try
    {
        using var database = new ClassroomDatabase(fixture.DatabasePath);
        database.Initialize(fixture.Options);
        var store = new ClassroomStore(fixture.Options, database);
        var ticket = store.CreateEnrollmentTicket(
            fixture.TeacherId,
            fixture.ClassId,
            fixture.StudentId,
            "종료 비밀번호 학생");
        var enrollment = store.Enroll(new DeviceEnrollmentRequest(
            ticket.DeviceId,
            "EXIT-PIN-01",
            "0.4.1",
            ticket.EnrollmentToken));
        Assert(enrollment.Succeeded && enrollment.Value is not null, "Exit PIN fixture enrollment failed.");
        Assert(store.TryAuthenticateDevice(ticket.DeviceId, enrollment.Value!.DeviceToken, out var device)
            && device is not null, "Exit PIN fixture authentication failed.");

        var beforeSetup = store.VerifyStudentExitPin(device!, "school-exit-2026");
        Assert(beforeSetup.Code == "EXIT_PIN_NOT_CONFIGURED", "An unset exit PIN was accepted.");

        database.SetStudentExitPin(fixture.TeacherId, "school-exit-2026");
        var status = database.GetStudentExitPinStatus(fixture.Options.DevelopmentSchoolId);
        Assert(status.Configured && status.UpdatedAtUtc is not null, "Exit PIN setup state was not persisted.");

        var rejected = store.VerifyStudentExitPin(device!, "wrong-exit-pin");
        Assert(rejected.Code == "EXIT_PIN_REJECTED", "Wrong exit PIN was accepted.");
        var approved = store.VerifyStudentExitPin(device!, "school-exit-2026");
        Assert(approved.Succeeded && approved.Value is true, "Correct exit PIN was not approved.");

        var nonAdmin = database.CreateOrGetFirebaseTeacher(
            new FirebaseIdentity("exit-pin-non-admin", "nonadmin@example.edu", "일반 교사", true, "google.com"),
            "일반 교사",
            "국어");
        AssertThrows<InvalidOperationException>(() => database.SetStudentExitPin(nonAdmin.Id, "different-exit-pin"));
    }
    finally
    {
        if (Directory.Exists(fixture.RootPath))
        {
            Directory.Delete(fixture.RootPath, recursive: true);
        }
    }
}

static void GuestPasswordsAreSchoolScoped()
{
    var fixture = CreatePersistentFixture();
    try
    {
        using var database = new ClassroomDatabase(fixture.DatabasePath);
        database.Initialize(fixture.Options);
        database.SetGuestPassword(fixture.TeacherId, "guest-pass");

        var status = database.GetGuestPasswordStatus(fixture.Options.DevelopmentSchoolId);
        Assert(status.Configured, "Guest password status did not report the configured password.");
        Assert(database.VerifyGuestPassword(fixture.Options.DevelopmentSchoolId, "guest-pass"), "The configured guest password was rejected.");
        Assert(!database.VerifyGuestPassword(fixture.Options.DevelopmentSchoolId, "wrong-pass"), "An incorrect guest password was accepted.");
        Assert(!database.VerifyGuestPassword(Guid.NewGuid(), "guest-pass"), "A guest password crossed the school boundary.");
        AssertThrows<InvalidOperationException>(() => database.SetGuestPassword(Guid.NewGuid(), "other-pass"));

        var token = database.CreateGuestSession(fixture.Options.DevelopmentSchoolId, TimeSpan.FromMinutes(5));
        Assert(database.TryValidateGuestSession(token, out var schoolId) && schoolId == fixture.Options.DevelopmentSchoolId,
            "Guest session was not bound to its school.");
        database.RevokeGuestSession(token);
        Assert(!database.TryValidateGuestSession(token, out _), "Revoked guest session token was accepted.");
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
