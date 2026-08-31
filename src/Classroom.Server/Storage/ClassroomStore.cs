using System.Threading.Channels;
using Blossom.Classroom.Core.Audit;
using Blossom.Classroom.Core.Security;
using Blossom.Classroom.Protocol.Models;
using Blossom.Classroom.Protocol.Validation;
using Blossom.Classroom.Server.Configuration;
using Blossom.Classroom.Server.Models;

namespace Blossom.Classroom.Server.Storage;

public sealed class ClassroomStore
{
    private readonly ServerOptions options;
    private readonly ClassroomDatabase? database;
    private readonly object gate = new();
    private readonly Dictionary<Guid, EnrollmentTicketState> enrollmentTickets = [];
    private readonly Dictionary<Guid, StudentDeviceState> devices = [];
    private readonly Dictionary<Guid, SessionState> sessions = [];
    private readonly Dictionary<CommandKey, CommandRecord> commands = [];
    private readonly Dictionary<Guid, ExitPinAttemptState> exitPinAttempts = [];
    private readonly Queue<AuditEvent> auditEvents = [];
    private const int MaximumAuditEvents = 10_000;
    private const int JoinCodeLength = 8;
    private const string JoinCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int MaximumExitPinAttempts = 5;
    private static readonly TimeSpan ExitPinCooldown = TimeSpan.FromMinutes(5);

    public ClassroomStore(ServerOptions options, ClassroomDatabase? database = null)
    {
        this.options = options;
        this.database = database;
        if (database is not null)
        {
            database.Initialize(options);
            LoadPersistedState();
            DeduplicateActiveDevices();
        }
    }

    private void LoadPersistedState()
    {
        foreach (var ticket in database!.LoadEnrollmentTickets())
        {
            enrollmentTickets[ticket.DeviceId] = new EnrollmentTicketState(
                ticket.DeviceId,
                ticket.SchoolId,
                ticket.ClassId,
                ticket.StudentId,
                ticket.StudentDisplayName,
                ticket.TokenHash,
                ticket.JoinCode,
                ticket.JoinCodeHash,
                ticket.JoinCodeCreatedAtUtc,
                ticket.JoinCodeLastUsedAtUtc,
                ticket.ExpiresAtUtc,
                ticket.CreatedByTeacherId)
            {
                Consumed = ticket.Consumed
            };
        }

        foreach (var device in database.LoadDevices())
        {
            devices[device.DeviceId] = new StudentDeviceState(
                device.DeviceId,
                device.SchoolId,
                device.ClassId,
                device.StudentId,
                device.StudentDisplayName,
                device.DeviceName,
                device.AgentVersion,
                device.DeviceTokenHash,
                device.EnrolledAtUtc)
            {
                LastHeartbeatUtc = device.LastHeartbeatUtc,
                LatestHeartbeat = device.LatestHeartbeat,
                ConnectionActive = false,
                SessionId = device.SessionId,
                Revoked = device.Revoked
            };
        }

        foreach (var session in database.LoadSessions())
        {
            sessions[session.SessionId] = new SessionState(
                session.SessionId,
                session.SchoolId,
                session.ClassId,
                session.Subject,
                session.StartedAtUtc)
            {
                EndedAtUtc = session.EndedAtUtc
            };
        }

        foreach (var command in database.LoadCommands())
        {
            commands[new CommandKey(command.RequestId, command.DeviceId)] =
                new CommandRecord(command.Command, command.TeacherId, command.CreatedAtUtc)
                {
                    State = command.State
                };
        }
    }

    public DeviceEnrollmentTicket CreateEnrollmentTicket(
        Guid teacherId,
        Guid classId,
        Guid studentId,
        string studentDisplayName)
    {
        var schoolId = GetEnrollmentClassSchoolId(teacherId, classId);
        RequireText(studentDisplayName, nameof(studentDisplayName), 128);

        lock (gate)
        {
            var existingTicket = studentId == Guid.Empty
                ? null
                : enrollmentTickets.Values
                    .Where(ticket => ticket.SchoolId == schoolId
                        && ticket.ClassId == classId
                        && ticket.StudentId == studentId)
                    .OrderByDescending(ticket => ticket.JoinCodeCreatedAtUtc ?? DateTimeOffset.MinValue)
                    .FirstOrDefault();
            var deviceId = existingTicket?.DeviceId ?? Guid.NewGuid();
            var resolvedStudentId = existingTicket?.StudentId ?? (studentId == Guid.Empty ? Guid.NewGuid() : studentId);
            var token = TokenSecurity.CreateToken();
            var expiresAt = DateTimeOffset.UtcNow.Add(options.EnrollmentLifetime);
            var joinCode = CreateUniqueJoinCodeLocked();
            var joinCodeHash = TokenSecurity.HashToken(joinCode);
            EnrollmentTicketState ticket;
            if (existingTicket is null)
            {
                ticket = new EnrollmentTicketState(
                    deviceId,
                    schoolId,
                    classId,
                    resolvedStudentId,
                    studentDisplayName,
                    TokenSecurity.HashToken(token),
                    joinCode,
                    joinCodeHash,
                    DateTimeOffset.UtcNow,
                    null,
                    expiresAt,
                    teacherId);
                enrollmentTickets[deviceId] = ticket;
            }
            else
            {
                existingTicket.StudentDisplayName = studentDisplayName;
                existingTicket.TokenHash = TokenSecurity.HashToken(token);
                existingTicket.JoinCode = joinCode;
                existingTicket.JoinCodeHash = joinCodeHash;
                existingTicket.JoinCodeCreatedAtUtc = DateTimeOffset.UtcNow;
                existingTicket.JoinCodeLastUsedAtUtc = null;
                existingTicket.ExpiresAtUtc = expiresAt;
                existingTicket.CreatedByTeacherId = teacherId;
                existingTicket.Consumed = false;
                ticket = existingTicket;
            }

            database?.SaveEnrollmentTicket(ticket.ToPersisted());
            AddAuditLocked(AuditEvent.Create(
                "DEVICE_ENROLLMENT_TICKET",
                existingTicket is null ? "ISSUED" : "REISSUED",
                schoolId: schoolId,
                classId: classId,
                teacherId: teacherId,
                studentId: resolvedStudentId));

            return new DeviceEnrollmentTicket(
                ticket.DeviceId,
                ticket.SchoolId,
                ticket.ClassId,
                ticket.StudentId,
                ticket.ExpiresAtUtc,
                token,
                ticket.JoinCode!);
        }
    }

    public StoreResult<DeviceEnrollmentResponse> EnrollByJoinCode(
        JoinCodeEnrollmentRequest request)
    {
        var joinCode = NormalizeJoinCode(request.JoinCode);
        if (joinCode.Length != JoinCodeLength
            || joinCode.Any(character => !JoinCodeAlphabet.Contains(character)))
        {
            return StoreResult<DeviceEnrollmentResponse>.Failure(
                "ENROLLMENT_INVALID",
                "학생 코드는 8자리 영문 대문자와 숫자로 입력해야 합니다.");
        }

        try
        {
            RequireText(request.DeviceName, nameof(request.DeviceName), 128);
            RequireText(request.AgentVersion, nameof(request.AgentVersion), 64);
        }
        catch (ClassroomStoreException exception)
        {
            return StoreResult<DeviceEnrollmentResponse>.Failure(exception.Code, exception.Message);
        }

        lock (gate)
        {
            var ticket = enrollmentTickets.Values.FirstOrDefault(candidate =>
                candidate.JoinCodeHash is not null
                && TokenSecurity.VerifyToken(joinCode, candidate.JoinCodeHash));
            if (ticket is null)
            {
                return StoreResult<DeviceEnrollmentResponse>.Failure(
                    "ENROLLMENT_INVALID",
                    "학생 코드가 올바르지 않거나 재발급되어 사용할 수 없습니다. 선생님에게 코드를 확인하세요.");
            }

            var existingDeviceId = devices.Values
                .Where(device => device.ClassId == ticket.ClassId
                    && device.StudentId == ticket.StudentId
                    && !device.Revoked)
                .OrderByDescending(device => device.LastHeartbeatUtc ?? DateTimeOffset.MinValue)
                .ThenByDescending(device => device.EnrolledAtUtc)
                .Select(device => device.DeviceId)
                .FirstOrDefault();

            return CompleteEnrollmentLocked(
                ticket,
                existingDeviceId == Guid.Empty ? Guid.NewGuid() : existingDeviceId,
                request.DeviceName,
                request.AgentVersion,
                consumeTicket: false);
        }
    }

    public StoreResult<DeviceEnrollmentResponse> Enroll(DeviceEnrollmentRequest request)
    {
        try
        {
            ProtocolValidation.ValidateEnrollmentRequest(request);
        }
        catch (ProtocolValidationException exception)
        {
            return StoreResult<DeviceEnrollmentResponse>.Failure("INVALID_REQUEST", exception.Message);
        }

        lock (gate)
        {
            if (!enrollmentTickets.TryGetValue(request.DeviceId, out var ticket))
            {
                return StoreResult<DeviceEnrollmentResponse>.Failure(
                    "ENROLLMENT_NOT_FOUND",
                    "The enrollment ticket was not found.");
            }

            if (ticket.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                enrollmentTickets.Remove(request.DeviceId);
                return StoreResult<DeviceEnrollmentResponse>.Failure(
                    "ENROLLMENT_EXPIRED",
                    "The enrollment ticket has expired.");
            }

            if (ticket.Consumed)
            {
                return StoreResult<DeviceEnrollmentResponse>.Failure(
                    "ENROLLMENT_USED",
                    "The enrollment ticket has already been used.");
            }

            if (!TokenSecurity.VerifyToken(request.EnrollmentToken, ticket.TokenHash))
            {
                AddAuditLocked(AuditEvent.Create(
                    "DEVICE_ENROLLMENT",
                    "REJECTED",
                    reason: "Invalid enrollment token.",
                    schoolId: ticket.SchoolId,
                    classId: ticket.ClassId,
                    studentId: ticket.StudentId,
                    studentDeviceId: request.DeviceId));
                return StoreResult<DeviceEnrollmentResponse>.Failure(
                    "ENROLLMENT_INVALID",
                    "The enrollment token is invalid.");
            }

            return CompleteEnrollmentLocked(
                ticket,
                request.DeviceId,
                request.DeviceName,
                request.AgentVersion,
                consumeTicket: true);
        }
    }

    public bool TryAuthenticateDevice(
        Guid deviceId,
        string? token,
        out AuthenticatedDevice? identity)
    {
        lock (gate)
        {
            if (token is not null
                && devices.TryGetValue(deviceId, out var device)
                && !device.Revoked
                && TokenSecurity.VerifyToken(token, device.DeviceTokenHash))
            {
                identity = device.ToIdentity();
                return true;
            }
        }

        identity = null;
        return false;
    }

    public bool TryOpenConnection(
        AuthenticatedDevice identity,
        Guid requestedSessionId,
        out Guid acceptedSessionId,
        out string code,
        out string message)
    {
        acceptedSessionId = Guid.Empty;
        code = "OK";
        message = "Connection accepted.";
        lock (gate)
        {
            if (!devices.TryGetValue(identity.DeviceId, out var device) || device.Revoked)
            {
                code = "DEVICE_REVOKED";
                message = "The student device is not enrolled.";
                return false;
            }

            var active = FindActiveSessionLocked(device.ClassId);
            acceptedSessionId = active?.SessionId ?? Guid.Empty;

            device.ConnectionActive = true;
            device.SessionId = active?.SessionId;
            RestorePendingCommandsLocked(device, acceptedSessionId);
            database?.SaveDevice(device.ToPersisted());
            AddAuditLocked(AuditEvent.Create(
                "DEVICE_CONNECTION",
                "CONNECTED",
                reason: requestedSessionId == acceptedSessionId
                    ? null
                    : "Server selected the current class session.",
                schoolId: device.SchoolId,
                classId: device.ClassId,
                sessionId: acceptedSessionId == Guid.Empty ? null : acceptedSessionId,
                studentId: device.StudentId,
                studentDeviceId: device.DeviceId));
            return true;
        }
    }

    public void CloseConnection(Guid deviceId)
    {
        lock (gate)
        {
            if (!devices.TryGetValue(deviceId, out var device))
            {
                return;
            }

            var sessionId = device.SessionId;
            device.ConnectionActive = false;
            device.SessionId = null;
            database?.SaveDevice(device.ToPersisted());
            AddAuditLocked(AuditEvent.Create(
                "DEVICE_CONNECTION",
                "DISCONNECTED",
                schoolId: device.SchoolId,
                classId: device.ClassId,
                sessionId: sessionId,
                studentId: device.StudentId,
                studentDeviceId: device.DeviceId));
        }
    }

    public StoreResult<Guid> RecordHeartbeat(
        AuthenticatedDevice identity,
        DeviceHeartbeat heartbeat)
    {
        try
        {
            ProtocolValidation.ValidateHeartbeat(heartbeat);
        }
        catch (ProtocolValidationException exception)
        {
            return StoreResult<Guid>.Failure("INVALID_HEARTBEAT", exception.Message);
        }

        lock (gate)
        {
            if (heartbeat.DeviceId != identity.DeviceId)
            {
                return StoreResult<Guid>.Failure(
                    "DEVICE_MISMATCH",
                    "Heartbeat device ID does not match the authenticated device.");
            }

            if (!devices.TryGetValue(identity.DeviceId, out var device) || device.Revoked)
            {
                return StoreResult<Guid>.Failure("DEVICE_REVOKED", "The student device is not enrolled.");
            }

            var active = FindActiveSessionLocked(device.ClassId);
            var acceptedSessionId = active?.SessionId ?? Guid.Empty;
            var normalizedHeartbeat = heartbeat with { SessionId = acceptedSessionId };

            device.ConnectionActive = true;
            device.SessionId = active?.SessionId;
            device.AgentVersion = heartbeat.AgentVersion;
            device.LatestHeartbeat = normalizedHeartbeat;
            device.LastHeartbeatUtc = DateTimeOffset.UtcNow;
            RestorePendingCommandsLocked(device, acceptedSessionId);
            database?.SaveDevice(device.ToPersisted());
            return StoreResult<Guid>.Success(acceptedSessionId);
        }
    }

    public StoreResult<CommandDispatchSummary> QueueCommand(
        Guid teacherId,
        Guid classId,
        CommandRequest command)
    {
        try
        {
            ProtocolValidation.ValidateCommand(command);
        }
        catch (ProtocolValidationException exception)
        {
            return StoreResult<CommandDispatchSummary>.Failure("INVALID_COMMAND", exception.Message);
        }

        lock (gate)
        {
            EnsureTeacherAccess(teacherId, classId);
            var active = FindActiveSessionLocked(classId);
            if (active is null || active.SessionId != command.SessionId)
            {
                return StoreResult<CommandDispatchSummary>.Failure(
                    "SESSION_NOT_ACTIVE",
                    "Commands require an active class session.");
            }

            foreach (var deviceId in command.TargetDeviceIds)
            {
                if (!devices.TryGetValue(deviceId, out var device)
                    || device.Revoked
                    || device.ClassId != classId)
                {
                    return StoreResult<CommandDispatchSummary>.Failure(
                        "TARGET_FORBIDDEN",
                        "Every target device must belong to the assigned class.");
                }

                if (commands.ContainsKey(new CommandKey(command.RequestId, deviceId)))
                {
                    return StoreResult<CommandDispatchSummary>.Failure(
                        "DUPLICATE_REQUEST",
                        "The command request ID has already been used for a target device.");
                }
            }

            var queued = new List<Guid>();
            var rejected = new List<Guid>();
            foreach (var deviceId in command.TargetDeviceIds)
            {
                var device = devices[deviceId];
                if (device.Commands.Writer.TryWrite(command))
                {
                    queued.Add(deviceId);
                    var key = new CommandKey(command.RequestId, deviceId);
                    commands[key] = new CommandRecord(command, teacherId, DateTimeOffset.UtcNow);
                    device.QueuedCommandKeys.Add(key);
                    database?.SaveCommand(new PersistedCommand(
                        command.RequestId,
                        deviceId,
                        teacherId,
                        classId,
                        command,
                        "QUEUED",
                        DateTimeOffset.UtcNow));
                    AddAuditLocked(AuditEvent.Create(
                        "COMMAND_REQUEST",
                        "QUEUED",
                        reason: command.Kind.ToString(),
                        schoolId: device.SchoolId,
                        classId: device.ClassId,
                        sessionId: command.SessionId,
                        teacherId: teacherId,
                        studentId: device.StudentId,
                        studentDeviceId: device.DeviceId,
                        requestId: command.RequestId));
                }
                else
                {
                    rejected.Add(deviceId);
                    AddAuditLocked(AuditEvent.Create(
                        "COMMAND_REQUEST",
                        "QUEUE_FULL",
                        reason: command.Kind.ToString(),
                        schoolId: device.SchoolId,
                        classId: device.ClassId,
                        sessionId: command.SessionId,
                        teacherId: teacherId,
                        studentId: device.StudentId,
                        studentDeviceId: device.DeviceId,
                        requestId: command.RequestId));
                }
            }

            return StoreResult<CommandDispatchSummary>.Success(
                new CommandDispatchSummary(
                    command.RequestId,
                    command.TargetDeviceIds.Count,
                    queued.Count,
                    queued,
                    rejected));
        }
    }

    public StoreResult<bool> VerifyStudentExitPin(
        AuthenticatedDevice identity,
        string pin)
    {
        lock (gate)
        {
            if (!devices.TryGetValue(identity.DeviceId, out var device)
                || device.Revoked
                || device.SchoolId != identity.SchoolId
                || device.ClassId != identity.ClassId
                || device.StudentId != identity.StudentId)
            {
                return StoreResult<bool>.Failure(
                    "DEVICE_REVOKED",
                    "학생 장치 등록을 확인할 수 없습니다.");
            }

            var now = DateTimeOffset.UtcNow;
            if (exitPinAttempts.TryGetValue(identity.DeviceId, out var attempts)
                && attempts.BlockedUntilUtc > now)
            {
                return StoreResult<bool>.Failure(
                    "EXIT_PIN_RATE_LIMITED",
                    "종료 비밀번호 확인 시도가 많습니다. 잠시 후 다시 시도해 주세요.");
            }

            var verification = database?.VerifyStudentExitPin(device.SchoolId, pin)
                ?? new StudentExitPinVerification(false, false);
            if (!verification.Configured)
            {
                return StoreResult<bool>.Failure(
                    "EXIT_PIN_NOT_CONFIGURED",
                    "관리자가 학생 앱 종료 비밀번호를 아직 설정하지 않았습니다.");
            }

            if (!verification.Approved)
            {
                attempts ??= new ExitPinAttemptState();
                attempts.FailedAttempts += 1;
                var rateLimited = attempts.FailedAttempts >= MaximumExitPinAttempts;
                if (rateLimited)
                {
                    attempts.FailedAttempts = 0;
                    attempts.BlockedUntilUtc = now.Add(ExitPinCooldown);
                }

                exitPinAttempts[identity.DeviceId] = attempts;
                AddAuditLocked(AuditEvent.Create(
                    "STUDENT_APP_EXIT_PIN",
                    rateLimited ? "RATE_LIMITED" : "REJECTED",
                    schoolId: device.SchoolId,
                    classId: device.ClassId,
                    studentId: device.StudentId,
                    studentDeviceId: device.DeviceId));
                return StoreResult<bool>.Failure(
                    rateLimited ? "EXIT_PIN_RATE_LIMITED" : "EXIT_PIN_REJECTED",
                    rateLimited
                        ? "종료 비밀번호 확인 시도가 많습니다. 잠시 후 다시 시도해 주세요."
                        : "종료 비밀번호가 올바르지 않습니다.");
            }

            exitPinAttempts.Remove(identity.DeviceId);
            AddAuditLocked(AuditEvent.Create(
                "STUDENT_APP_EXIT_PIN",
                "APPROVED",
                schoolId: device.SchoolId,
                classId: device.ClassId,
                studentId: device.StudentId,
                studentDeviceId: device.DeviceId));
            return StoreResult<bool>.Success(true);
        }
    }

    public ValueTask<CommandRequest> WaitForCommandAsync(
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!devices.TryGetValue(deviceId, out var device) || device.Revoked)
            {
                return ValueTask.FromException<CommandRequest>(
                    new InvalidOperationException("Student device is not enrolled."));
            }

            return ReadCommandAsync(device, cancellationToken);
        }
    }

    private async ValueTask<CommandRequest> ReadCommandAsync(
        StudentDeviceState device,
        CancellationToken cancellationToken)
    {
        var command = await device.Commands.Reader.ReadAsync(cancellationToken);
        lock (gate)
        {
            var key = new CommandKey(command.RequestId, device.DeviceId);
            device.QueuedCommandKeys.Remove(key);
            if (commands.TryGetValue(key, out var commandRecord))
            {
                commandRecord.State = "DISPATCHED";
            }
            database?.UpdateCommandState(command.RequestId, device.DeviceId, "DISPATCHED");
        }

        return command;
    }

    public StoreResult<bool> RecordCommandAck(
        AuthenticatedDevice identity,
        CommandAck acknowledgment)
    {
        try
        {
            ProtocolValidation.ValidateAck(acknowledgment);
        }
        catch (ProtocolValidationException exception)
        {
            return StoreResult<bool>.Failure("INVALID_ACK", exception.Message);
        }

        lock (gate)
        {
            if (acknowledgment.DeviceId != identity.DeviceId)
            {
                return StoreResult<bool>.Failure("DEVICE_MISMATCH", "ACK device ID does not match the connection.");
            }

            if (!commands.TryGetValue(new CommandKey(acknowledgment.RequestId, identity.DeviceId), out var commandRecord))
            {
                return StoreResult<bool>.Failure("UNKNOWN_COMMAND", "ACK does not match a queued command.");
            }

            var device = devices[identity.DeviceId];
            AddAuditLocked(AuditEvent.Create(
                "COMMAND_ACK",
                acknowledgment.Accepted ? "ACCEPTED" : "REJECTED",
                acknowledgment.Reason,
                schoolId: device.SchoolId,
                classId: device.ClassId,
                sessionId: commandRecord.Command.SessionId,
                teacherId: commandRecord.TeacherId,
                studentId: device.StudentId,
                studentDeviceId: device.DeviceId,
                requestId: acknowledgment.RequestId));
            database?.UpdateCommandState(
                acknowledgment.RequestId,
                identity.DeviceId,
                acknowledgment.Accepted ? "ACKED" : "ACK_REJECTED");
            commandRecord.State = acknowledgment.Accepted ? "ACKED" : "ACK_REJECTED";
            return StoreResult<bool>.Success(true);
        }
    }

    public StoreResult<bool> RecordCommandResult(
        AuthenticatedDevice identity,
        CommandResult result)
    {
        try
        {
            ProtocolValidation.ValidateResult(result);
        }
        catch (ProtocolValidationException exception)
        {
            return StoreResult<bool>.Failure("INVALID_RESULT", exception.Message);
        }

        lock (gate)
        {
            if (result.DeviceId != identity.DeviceId)
            {
                return StoreResult<bool>.Failure("DEVICE_MISMATCH", "Result device ID does not match the connection.");
            }

            if (!commands.TryGetValue(new CommandKey(result.RequestId, identity.DeviceId), out var commandRecord))
            {
                return StoreResult<bool>.Failure("UNKNOWN_COMMAND", "Result does not match a queued command.");
            }

            var device = devices[identity.DeviceId];
            AddAuditLocked(AuditEvent.Create(
                "COMMAND_RESULT",
                result.Success ? "SUCCESS" : "FAILED",
                result.Code,
                schoolId: device.SchoolId,
                classId: device.ClassId,
                sessionId: commandRecord.Command.SessionId,
                teacherId: commandRecord.TeacherId,
                studentId: device.StudentId,
                studentDeviceId: device.DeviceId,
                requestId: result.RequestId));
            database?.UpdateCommandState(
                result.RequestId,
                identity.DeviceId,
                result.Success ? "COMPLETED" : "FAILED");
            commandRecord.State = result.Success ? "COMPLETED" : "FAILED";
            return StoreResult<bool>.Success(true);
        }
    }

    public ClassSessionSnapshot StartSession(
        Guid teacherId,
        Guid classId,
        string subject)
    {
        var schoolId = GetClassSchoolId(teacherId, classId);
        RequireText(subject, nameof(subject), 128);
        lock (gate)
        {
            if (FindActiveSessionLocked(classId) is not null)
            {
                throw new ClassroomStoreException("SESSION_ALREADY_ACTIVE", "The class already has an active session.");
            }

            var session = new SessionState(
                Guid.NewGuid(),
                schoolId,
                classId,
                subject,
                DateTimeOffset.UtcNow);
            sessions[session.SessionId] = session;
            database?.SaveSession(session.ToPersisted());
            AddAuditLocked(AuditEvent.Create(
                "CLASS_SESSION",
                "STARTED",
                reason: subject,
                schoolId: session.SchoolId,
                classId: session.ClassId,
                sessionId: session.SessionId,
                teacherId: teacherId));
            return session.ToSnapshot();
        }
    }

    public ClassSessionSnapshot EndSession(
        Guid teacherId,
        Guid classId,
        Guid sessionId)
    {
        EnsureTeacherAccess(teacherId, classId);
        lock (gate)
        {
            if (!sessions.TryGetValue(sessionId, out var session)
                || session.ClassId != classId
                || session.EndedAtUtc is not null)
            {
                throw new ClassroomStoreException("SESSION_NOT_FOUND", "The class session was not found.");
            }

            var targets = devices.Values
                .Where(device => device.ClassId == classId && !device.Revoked)
                .Select(device => device.DeviceId)
                .ToArray();
            if (targets.Length > 0)
            {
                var cleanup = new CommandRequest(
                    Guid.NewGuid(),
                    session.SessionId,
                    targets,
                    ClassroomCommandKind.FocusMode,
                    FocusEnabled: false);
                var cleanupResult = QueueCommand(teacherId, classId, cleanup);
                if (!cleanupResult.Succeeded)
                {
                    AddAuditLocked(AuditEvent.Create(
                        "SESSION_POLICY_CLEANUP",
                        "FAILED",
                        cleanupResult.Code,
                        schoolId: session.SchoolId,
                        classId: session.ClassId,
                        sessionId: session.SessionId,
                        teacherId: teacherId,
                        requestId: cleanup.RequestId));
                }
            }

            session.EndedAtUtc = DateTimeOffset.UtcNow;
            foreach (var device in devices.Values.Where(device => device.ClassId == classId))
            {
                device.SessionId = null;
                if (device.LatestHeartbeat is { } latestHeartbeat)
                {
                    device.LatestHeartbeat = latestHeartbeat with
                    {
                        NeedsHelp = false,
                        PolicyApplied = false,
                        ScreenFrame = null,
                        ScreenSharingEnabled = false
                    };
                }
                database?.SaveDevice(device.ToPersisted());
            }

            database?.SaveSession(session.ToPersisted());

            AddAuditLocked(AuditEvent.Create(
                "CLASS_SESSION",
                "ENDED",
                schoolId: session.SchoolId,
                classId: session.ClassId,
                sessionId: session.SessionId,
                teacherId: teacherId));
            return session.ToSnapshot();
        }
    }

    public IReadOnlyList<DeviceStatus> GetClassStatuses(Guid teacherId, Guid classId)
    {
        EnsureTeacherAccess(teacherId, classId);
        return GetClassStatusesCore(classId);
    }

    public IReadOnlyList<DeviceStatus> GetClassStatusesForSchool(Guid schoolId, Guid classId)
    {
        EnsureSchoolClassAccess(schoolId, classId);
        return GetClassStatusesCore(classId);
    }

    private IReadOnlyList<DeviceStatus> GetClassStatusesCore(Guid classId)
    {
        lock (gate)
        {
            var active = FindActiveSessionLocked(classId);
            var now = DateTimeOffset.UtcNow;
            return devices.Values
                .Where(device => device.ClassId == classId && !device.Revoked)
                .OrderBy(device => device.StudentDisplayName, StringComparer.Ordinal)
                .Select(device => device.ToStatus(active?.SessionId ?? Guid.Empty, now, options.HeartbeatTimeout))
                .ToArray();
        }
    }

    public IReadOnlyList<DeviceScreenFrameStatus> GetClassScreenFrames(Guid teacherId, Guid classId)
    {
        EnsureTeacherAccess(teacherId, classId);
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            return devices.Values
                .Where(device => device.ClassId == classId
                    && !device.Revoked
                    && device.ConnectionActive
                    && device.LatestHeartbeat?.ScreenSharingEnabled == true
                    && device.LatestHeartbeat.ScreenFrame is not null
                    && device.LastHeartbeatUtc is not null
                    && now - device.LastHeartbeatUtc <= TimeSpan.FromSeconds(15))
                .OrderBy(device => device.StudentDisplayName, StringComparer.Ordinal)
                .Select(device => new DeviceScreenFrameStatus(
                    device.DeviceId,
                    device.StudentDisplayName,
                    device.LatestHeartbeat!.ScreenFrame!,
                    device.LastHeartbeatUtc!.Value))
                .ToArray();
        }
    }

    public DeviceActionResponse RevokeDevice(
        Guid teacherId,
        Guid classId,
        Guid deviceId)
    {
        EnsureTeacherAccess(teacherId, classId);
        lock (gate)
        {
            if (!devices.TryGetValue(deviceId, out var device)
                || device.ClassId != classId
                || device.Revoked)
            {
                throw new ClassroomStoreException("DEVICE_NOT_FOUND", "The student device was not found.");
            }

            device.Revoked = true;
            device.ConnectionActive = false;
            device.SessionId = null;
            database?.SaveDevice(device.ToPersisted());
            AddAuditLocked(AuditEvent.Create(
                "DEVICE_ACCESS",
                "REVOKED",
                schoolId: device.SchoolId,
                classId: device.ClassId,
                teacherId: teacherId,
                studentId: device.StudentId,
                studentDeviceId: device.DeviceId));
            return new DeviceActionResponse(device.DeviceId, "revoked", DateTimeOffset.UtcNow);
        }
    }

    public IReadOnlyList<TeacherClass> GetClassesForTeacher(Guid teacherId)
    {
        if (database is not null)
        {
            return database.GetClassesForTeacher(teacherId);
        }

        if (teacherId != options.DevelopmentTeacherId)
        {
            return [];
        }

        return [new TeacherClass(
            options.DevelopmentClassId,
            options.DevelopmentSchoolId,
            options.BootstrapClassName,
            options.BootstrapClassSubject)];
    }

    public IReadOnlyList<StudentCodeView> GetStudentCodes(Guid teacherId)
    {
        if (database is not null)
        {
            return database.GetTeacherSchoolId(teacherId, out var schoolId)
                ? database.GetStudentCodes(schoolId)
                : [];
        }

        if (teacherId != options.DevelopmentTeacherId)
        {
            return [];
        }

        lock (gate)
        {
            return enrollmentTickets.Values
                .Where(ticket => ticket.JoinCode is not null && ticket.JoinCodeHash is not null)
                .OrderBy(ticket => ticket.ClassId)
                .ThenBy(ticket => ticket.StudentDisplayName, StringComparer.Ordinal)
                .Select(ticket => new StudentCodeView(
                    ticket.DeviceId,
                    ticket.SchoolId,
                    ticket.ClassId,
                    options.BootstrapClassName,
                    options.BootstrapClassSubject,
                    ticket.StudentId,
                    ticket.StudentDisplayName,
                    ticket.JoinCode!,
                    ticket.JoinCodeCreatedAtUtc ?? DateTimeOffset.UtcNow,
                    ticket.JoinCodeLastUsedAtUtc,
                    options.BootstrapTeacherDisplayName))
                .ToArray();
        }
    }

    public ClassSessionSnapshot? GetActiveSession(Guid teacherId, Guid classId)
    {
        EnsureTeacherAccess(teacherId, classId);
        return GetActiveSessionCore(classId);
    }

    public ClassSessionSnapshot? GetActiveSessionForSchool(Guid schoolId, Guid classId)
    {
        EnsureSchoolClassAccess(schoolId, classId);
        return GetActiveSessionCore(classId);
    }

    private ClassSessionSnapshot? GetActiveSessionCore(Guid classId)
    {
        lock (gate)
        {
            return FindActiveSessionLocked(classId)?.ToSnapshot();
        }
    }

    public IReadOnlyList<AuditEvent> GetAuditEvents(Guid teacherId, Guid classId, int limit = 100)
    {
        EnsureTeacherAccess(teacherId, classId);
        if (database is not null)
        {
            return database.GetAuditEvents(classId, limit);
        }

        lock (gate)
        {
            return auditEvents
                .Where(entry => entry.ClassId == classId)
                .Reverse()
                .Take(Math.Clamp(limit, 1, 1_000))
                .ToArray();
        }
    }

    public CommandStatusResponse GetCommandStatus(
        Guid teacherId,
        Guid classId,
        Guid requestId)
    {
        EnsureTeacherAccess(teacherId, classId);
        lock (gate)
        {
            var statuses = commands
                .Where(pair => pair.Key.RequestId == requestId
                    && pair.Value.TeacherId == teacherId
                    && devices.TryGetValue(pair.Key.DeviceId, out var device)
                    && device.ClassId == classId)
                .Select(pair => new DeviceCommandStatus(pair.Key.DeviceId, pair.Value.State))
                .OrderBy(status => status.DeviceId)
                .ToArray();
            if (statuses.Length == 0)
            {
                throw new ClassroomStoreException("COMMAND_NOT_FOUND", "The command was not found.");
            }

            var completed = statuses.Count(status => status.State == "COMPLETED");
            var failed = statuses.Count(status => status.State is "FAILED" or "ACK_REJECTED");
            return new CommandStatusResponse(
                requestId,
                statuses.Length,
                completed,
                failed,
                completed + failed == statuses.Length,
                statuses);
        }
    }

    private SessionState? FindActiveSessionLocked(Guid classId) =>
        sessions.Values.FirstOrDefault(session =>
            session.ClassId == classId && session.EndedAtUtc is null);

    private Guid GetClassSchoolId(Guid teacherId, Guid classId)
    {
        if (database is not null)
        {
            if (database.TeacherHasClass(teacherId, classId, out var schoolId))
            {
                return schoolId;
            }

            throw new ClassroomStoreException(
                "FORBIDDEN",
                "Teacher is not assigned to this class.");
        }

        if (options.CanTeacherAccess(teacherId, classId))
        {
            return options.DevelopmentSchoolId;
        }

        throw new ClassroomStoreException(
            "FORBIDDEN",
            "Teacher is not assigned to this class.");
    }

    private Guid GetEnrollmentClassSchoolId(Guid teacherId, Guid classId)
    {
        if (database is not null
            && database.IsTeacherAdmin(teacherId)
            && database.TryGetClassSchoolId(classId, out var adminSchoolId)
            && database.GetTeacherSchoolId(teacherId, out var teacherSchoolId)
            && adminSchoolId == teacherSchoolId)
        {
            return adminSchoolId;
        }

        return GetClassSchoolId(teacherId, classId);
    }

    private void EnsureTeacherAccess(Guid teacherId, Guid classId)
    {
        _ = GetClassSchoolId(teacherId, classId);
    }

    private void EnsureSchoolClassAccess(Guid schoolId, Guid classId)
    {
        if (database is not null
            && database.TryGetClassSchoolId(classId, out var classSchoolId)
            && classSchoolId == schoolId)
        {
            return;
        }

        throw new ClassroomStoreException(
            "FORBIDDEN",
            "The selected class is outside the guest school.");
    }

    private void AddAuditLocked(AuditEvent entry)
    {
        auditEvents.Enqueue(entry);
        while (auditEvents.Count > MaximumAuditEvents)
        {
            auditEvents.Dequeue();
        }

        database?.AddAudit(entry);
    }

    private void RestorePendingCommandsLocked(StudentDeviceState device, Guid sessionId)
    {
        foreach (var pair in commands)
        {
            var isSessionCleanup = pair.Value.Command.Kind == ClassroomCommandKind.FocusMode
                && pair.Value.Command.FocusEnabled is false;
            if (pair.Key.DeviceId != device.DeviceId
                || (!isSessionCleanup && pair.Value.Command.SessionId != sessionId)
                || (!isSessionCleanup && pair.Value.Command.SessionId == Guid.Empty)
                || pair.Value.State is "COMPLETED" or "FAILED" or "ACK_REJECTED")
            {
                continue;
            }

            if (device.QueuedCommandKeys.Add(pair.Key))
            {
                if (!device.Commands.Writer.TryWrite(pair.Value.Command))
                {
                    device.QueuedCommandKeys.Remove(pair.Key);
                }
            }
        }
    }

    private StoreResult<DeviceEnrollmentResponse> CompleteEnrollmentLocked(
        EnrollmentTicketState ticket,
        Guid deviceId,
        string deviceName,
        string agentVersion,
        bool consumeTicket)
    {
        if (consumeTicket && ticket.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            enrollmentTickets.Remove(ticket.DeviceId);
            return StoreResult<DeviceEnrollmentResponse>.Failure(
                "ENROLLMENT_EXPIRED",
                "The enrollment ticket has expired.");
        }

        if (consumeTicket && ticket.Consumed)
        {
            return StoreResult<DeviceEnrollmentResponse>.Failure(
                "ENROLLMENT_USED",
                "The enrollment ticket has already been used.");
        }

        if (consumeTicket)
        {
            ticket.Consumed = true;
        }
        else
        {
            ticket.JoinCodeLastUsedAtUtc = DateTimeOffset.UtcNow;
        }

        foreach (var existingDevice in devices.Values.Where(device =>
                     device.ClassId == ticket.ClassId
                     && device.StudentId == ticket.StudentId
                     && !device.Revoked
                     && device.DeviceId != deviceId))
        {
            existingDevice.Revoked = true;
            existingDevice.ConnectionActive = false;
            existingDevice.SessionId = null;
            database?.SaveDevice(existingDevice.ToPersisted());
        }

        database?.SaveEnrollmentTicket(ticket.ToPersisted());
        var issuedAt = DateTimeOffset.UtcNow;
        var deviceToken = TokenSecurity.CreateToken();
        var device = new StudentDeviceState(
            deviceId,
            ticket.SchoolId,
            ticket.ClassId,
            ticket.StudentId,
            ticket.StudentDisplayName,
            deviceName,
            agentVersion,
            TokenSecurity.HashToken(deviceToken),
            issuedAt);
        devices[deviceId] = device;
        database?.SaveDevice(device.ToPersisted());

        AddAuditLocked(AuditEvent.Create(
            "DEVICE_ENROLLMENT",
            "SUCCESS",
            schoolId: ticket.SchoolId,
            classId: ticket.ClassId,
            studentId: ticket.StudentId,
            studentDeviceId: deviceId));

        return StoreResult<DeviceEnrollmentResponse>.Success(
            new DeviceEnrollmentResponse(
                deviceId,
                ticket.SchoolId,
                ticket.ClassId,
                ticket.StudentId,
                deviceToken,
                issuedAt));
    }

    private void DeduplicateActiveDevices()
    {
        foreach (var group in devices.Values
                     .Where(device => !device.Revoked)
                     .GroupBy(device => (device.ClassId, device.StudentId)))
        {
            var keep = group
                .OrderByDescending(device => device.LastHeartbeatUtc ?? DateTimeOffset.MinValue)
                .ThenByDescending(device => device.EnrolledAtUtc)
                .First();
            foreach (var duplicate in group.Where(device => device.DeviceId != keep.DeviceId))
            {
                duplicate.Revoked = true;
                duplicate.ConnectionActive = false;
                duplicate.SessionId = null;
                database?.SaveDevice(duplicate.ToPersisted());
            }
        }
    }

    private static string CreateJoinCode()
    {
        var characters = new char[JoinCodeLength];
        for (var index = 0; index < characters.Length; index++)
        {
            characters[index] = JoinCodeAlphabet[
                System.Security.Cryptography.RandomNumberGenerator.GetInt32(JoinCodeAlphabet.Length)];
        }

        return new string(characters);
    }

    private string CreateUniqueJoinCodeLocked()
    {
        string joinCode;
        do
        {
            joinCode = CreateJoinCode();
        }
        while (enrollmentTickets.Values.Any(ticket =>
            ticket.JoinCodeHash is not null
            && TokenSecurity.VerifyToken(joinCode, ticket.JoinCodeHash)));

        return joinCode;
    }

    private static string NormalizeJoinCode(string? value) =>
        new string((value ?? string.Empty)
            .Where(character => !char.IsWhiteSpace(character) && character != '-')
            .ToArray())
        .ToUpperInvariant();

    private static void RequireGuid(Guid value, string name)
    {
        if (value == Guid.Empty)
        {
            throw new ClassroomStoreException("INVALID_REQUEST", $"{name} must not be empty.");
        }
    }

    private static void RequireText(string? value, string name, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximum
            || value.Any(char.IsControl))
        {
            throw new ClassroomStoreException("INVALID_REQUEST", $"{name} is missing or invalid.");
        }
    }

    private sealed class EnrollmentTicketState(
        Guid deviceId,
        Guid schoolId,
        Guid classId,
        Guid studentId,
        string studentDisplayName,
        string tokenHash,
        string? joinCode,
        string? joinCodeHash,
        DateTimeOffset? joinCodeCreatedAtUtc,
        DateTimeOffset? joinCodeLastUsedAtUtc,
        DateTimeOffset expiresAtUtc,
        Guid createdByTeacherId)
    {
        public Guid DeviceId { get; } = deviceId;
        public Guid SchoolId { get; } = schoolId;
        public Guid ClassId { get; } = classId;
        public Guid StudentId { get; } = studentId;
        public string StudentDisplayName { get; set; } = studentDisplayName;
        public string TokenHash { get; set; } = tokenHash;
        public string? JoinCode { get; set; } = joinCode;
        public string? JoinCodeHash { get; set; } = joinCodeHash;
        public DateTimeOffset? JoinCodeCreatedAtUtc { get; set; } = joinCodeCreatedAtUtc;
        public DateTimeOffset? JoinCodeLastUsedAtUtc { get; set; } = joinCodeLastUsedAtUtc;
        public DateTimeOffset ExpiresAtUtc { get; set; } = expiresAtUtc;
        public Guid CreatedByTeacherId { get; set; } = createdByTeacherId;
        public bool Consumed { get; set; }

        public PersistedEnrollmentTicket ToPersisted() =>
            new(
                DeviceId,
                SchoolId,
                ClassId,
                StudentId,
                StudentDisplayName,
                TokenHash,
                JoinCode,
                JoinCodeHash,
                JoinCodeCreatedAtUtc,
                JoinCodeLastUsedAtUtc,
                ExpiresAtUtc,
                CreatedByTeacherId,
                Consumed);
    }

    private sealed class StudentDeviceState(
        Guid deviceId,
        Guid schoolId,
        Guid classId,
        Guid studentId,
        string studentDisplayName,
        string deviceName,
        string agentVersion,
        string deviceTokenHash,
        DateTimeOffset enrolledAtUtc)
    {
        public Guid DeviceId { get; } = deviceId;
        public Guid SchoolId { get; } = schoolId;
        public Guid ClassId { get; } = classId;
        public Guid StudentId { get; } = studentId;
        public string StudentDisplayName { get; } = studentDisplayName;
        public string DeviceName { get; } = deviceName;
        public string AgentVersion { get; set; } = agentVersion;
        public string DeviceTokenHash { get; } = deviceTokenHash;
        public DateTimeOffset EnrolledAtUtc { get; } = enrolledAtUtc;
        public DateTimeOffset? LastHeartbeatUtc { get; set; }
        public DeviceHeartbeat? LatestHeartbeat { get; set; }
        public bool ConnectionActive { get; set; }
        public Guid? SessionId { get; set; }
        public bool Revoked { get; set; }
        public Channel<CommandRequest> Commands { get; } =
            Channel.CreateBounded<CommandRequest>(new BoundedChannelOptions(64)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        public HashSet<CommandKey> QueuedCommandKeys { get; } = [];

        public AuthenticatedDevice ToIdentity() =>
            new(DeviceId, SchoolId, ClassId, StudentId, StudentDisplayName, DeviceName);

        public PersistedDevice ToPersisted()
        {
            // Screen frames are intentionally memory-only and expire quickly.
            // Persisting the latest heartbeat must never write a classroom
            // screenshot to SQLite.
            var persistedHeartbeat = LatestHeartbeat is null
                ? null
                : LatestHeartbeat with { ScreenFrame = null, ScreenSharingEnabled = false };
            return
            new(
                DeviceId,
                SchoolId,
                ClassId,
                StudentId,
                StudentDisplayName,
                DeviceName,
                AgentVersion,
                DeviceTokenHash,
                EnrolledAtUtc,
                LastHeartbeatUtc,
                persistedHeartbeat,
                ConnectionActive,
                SessionId,
                Revoked);
        }

        public DeviceStatus ToStatus(
            Guid activeSessionId,
            DateTimeOffset now,
            TimeSpan heartbeatTimeout)
        {
            var latest = LatestHeartbeat;
            var lastHeartbeat = LastHeartbeatUtc ?? EnrolledAtUtc;
            var online = ConnectionActive
                && LastHeartbeatUtc is not null
                && now - LastHeartbeatUtc <= heartbeatTimeout;
            return new DeviceStatus(
                DeviceId,
                StudentId,
                ClassId,
                SessionId ?? activeSessionId,
                StudentDisplayName,
                DeviceName,
                online,
                lastHeartbeat,
                AgentVersion,
                latest?.Activity,
                latest?.BatteryPercent,
                latest?.NetworkStatus,
                latest?.PolicyApplied ?? false,
                true,
                latest?.NeedsHelp ?? false);
        }
    }

    private sealed class SessionState(
        Guid sessionId,
        Guid schoolId,
        Guid classId,
        string subject,
        DateTimeOffset startedAtUtc)
    {
        public Guid SessionId { get; } = sessionId;
        public Guid SchoolId { get; } = schoolId;
        public Guid ClassId { get; } = classId;
        public string Subject { get; } = subject;
        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
        public DateTimeOffset? EndedAtUtc { get; set; }

        public ClassSessionSnapshot ToSnapshot() =>
            new(SessionId, SchoolId, ClassId, Subject, StartedAtUtc, EndedAtUtc);

        public PersistedSession ToPersisted() =>
            new(SessionId, SchoolId, ClassId, Subject, StartedAtUtc, EndedAtUtc);
    }

    private sealed record CommandRecord(
        CommandRequest Command,
        Guid TeacherId,
        DateTimeOffset CreatedAtUtc)
    {
        public string State { get; set; } = "QUEUED";
    }

    private sealed class ExitPinAttemptState
    {
        public int FailedAttempts { get; set; }

        public DateTimeOffset BlockedUntilUtc { get; set; }
    }

    private readonly record struct CommandKey(Guid RequestId, Guid DeviceId);
}

public sealed record AuthenticatedDevice(
    Guid DeviceId,
    Guid SchoolId,
    Guid ClassId,
    Guid StudentId,
    string StudentDisplayName,
    string DeviceName);

public sealed record StoreResult<T>(
    bool Succeeded,
    string Code,
    string Message,
    T? Value)
{
    public static StoreResult<T> Success(T value) =>
        new(true, "OK", "Operation succeeded.", value);

    public static StoreResult<T> Failure(string code, string message) =>
        new(false, code, message, default);
}

public sealed class ClassroomStoreException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
