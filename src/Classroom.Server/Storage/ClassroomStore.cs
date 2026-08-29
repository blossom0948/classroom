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
    private readonly Queue<AuditEvent> auditEvents = [];
    private const int MaximumAuditEvents = 10_000;

    public ClassroomStore(ServerOptions options, ClassroomDatabase? database = null)
    {
        this.options = options;
        this.database = database;
        if (database is not null)
        {
            database.Initialize(options);
            LoadPersistedState();
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
        var schoolId = GetClassSchoolId(teacherId, classId);
        RequireGuid(studentId, nameof(studentId));
        RequireText(studentDisplayName, nameof(studentDisplayName), 128);

        var deviceId = Guid.NewGuid();
        var token = TokenSecurity.CreateToken();
        var expiresAt = DateTimeOffset.UtcNow.Add(options.EnrollmentLifetime);
        lock (gate)
        {
            enrollmentTickets[deviceId] = new EnrollmentTicketState(
                deviceId,
                schoolId,
                classId,
                studentId,
                studentDisplayName,
                TokenSecurity.HashToken(token),
                expiresAt,
                teacherId);
            database?.SaveEnrollmentTicket(enrollmentTickets[deviceId].ToPersisted());
            AddAuditLocked(AuditEvent.Create(
                "DEVICE_ENROLLMENT_TICKET",
                "ISSUED",
                schoolId: schoolId,
                classId: classId,
                teacherId: teacherId,
                studentId: studentId));
        }

        return new DeviceEnrollmentTicket(
            deviceId,
            schoolId,
            classId,
            studentId,
            expiresAt,
            token);
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

            ticket.Consumed = true;
            database?.SaveEnrollmentTicket(ticket.ToPersisted());
            var issuedAt = DateTimeOffset.UtcNow;
            var deviceToken = TokenSecurity.CreateToken();
            var device = new StudentDeviceState(
                request.DeviceId,
                ticket.SchoolId,
                ticket.ClassId,
                ticket.StudentId,
                ticket.StudentDisplayName,
                request.DeviceName,
                request.AgentVersion,
                TokenSecurity.HashToken(deviceToken),
                issuedAt);
            devices[request.DeviceId] = device;
            database?.SaveDevice(device.ToPersisted());

            AddAuditLocked(AuditEvent.Create(
                "DEVICE_ENROLLMENT",
                "SUCCESS",
                schoolId: ticket.SchoolId,
                classId: ticket.ClassId,
                studentId: ticket.StudentId,
                studentDeviceId: request.DeviceId));

            return StoreResult<DeviceEnrollmentResponse>.Success(
                new DeviceEnrollmentResponse(
                    request.DeviceId,
                    ticket.SchoolId,
                    ticket.ClassId,
                    ticket.StudentId,
                    deviceToken,
                    issuedAt));
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
        Guid sessionId,
        out string code,
        out string message)
    {
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
            if (active is null || active.SessionId != sessionId)
            {
                code = "SESSION_NOT_ACTIVE";
                message = "The requested class session is not active.";
                return false;
            }

            device.ConnectionActive = true;
            device.SessionId = sessionId;
            device.LastHeartbeatUtc = DateTimeOffset.UtcNow;
            RestorePendingCommandsLocked(device, sessionId);
            database?.SaveDevice(device.ToPersisted());
            AddAuditLocked(AuditEvent.Create(
                "DEVICE_CONNECTION",
                "CONNECTED",
                schoolId: device.SchoolId,
                classId: device.ClassId,
                sessionId: sessionId,
                studentId: device.StudentId,
                studentDeviceId: device.DeviceId));
            return true;
        }
    }

    public void CloseConnection(Guid deviceId, Guid sessionId)
    {
        lock (gate)
        {
            if (!devices.TryGetValue(deviceId, out var device)
                || device.SessionId != sessionId)
            {
                return;
            }

            device.ConnectionActive = false;
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

    public StoreResult<bool> RecordHeartbeat(
        AuthenticatedDevice identity,
        DeviceHeartbeat heartbeat)
    {
        try
        {
            ProtocolValidation.ValidateHeartbeat(heartbeat);
        }
        catch (ProtocolValidationException exception)
        {
            return StoreResult<bool>.Failure("INVALID_HEARTBEAT", exception.Message);
        }

        lock (gate)
        {
            if (heartbeat.DeviceId != identity.DeviceId)
            {
                return StoreResult<bool>.Failure(
                    "DEVICE_MISMATCH",
                    "Heartbeat device ID does not match the authenticated device.");
            }

            if (!devices.TryGetValue(identity.DeviceId, out var device) || device.Revoked)
            {
                return StoreResult<bool>.Failure("DEVICE_REVOKED", "The student device is not enrolled.");
            }

            var active = FindActiveSessionLocked(device.ClassId);
            if (active is null || active.SessionId != heartbeat.SessionId)
            {
                return StoreResult<bool>.Failure(
                    "SESSION_NOT_ACTIVE",
                    "Heartbeat does not belong to the active class session.");
            }

            device.ConnectionActive = true;
            device.SessionId = heartbeat.SessionId;
            device.AgentVersion = heartbeat.AgentVersion;
            device.LatestHeartbeat = heartbeat;
            device.LastHeartbeatUtc = DateTimeOffset.UtcNow;
            database?.SaveDevice(device.ToPersisted());
            return StoreResult<bool>.Success(true);
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

            session.EndedAtUtc = DateTimeOffset.UtcNow;
            foreach (var device in devices.Values.Where(device => device.ClassId == classId))
            {
                device.ConnectionActive = false;
                device.SessionId = null;
                device.LatestHeartbeat = null;
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

    public ClassSessionSnapshot? GetActiveSession(Guid teacherId, Guid classId)
    {
        EnsureTeacherAccess(teacherId, classId);
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

    private void EnsureTeacherAccess(Guid teacherId, Guid classId)
    {
        _ = GetClassSchoolId(teacherId, classId);
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
            if (pair.Key.DeviceId != device.DeviceId
                || pair.Value.Command.SessionId != sessionId
                || pair.Value.Command.SessionId == Guid.Empty
                || pair.Value.State is "COMPLETED" or "FAILED")
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
        DateTimeOffset expiresAtUtc,
        Guid createdByTeacherId)
    {
        public Guid DeviceId { get; } = deviceId;
        public Guid SchoolId { get; } = schoolId;
        public Guid ClassId { get; } = classId;
        public Guid StudentId { get; } = studentId;
        public string StudentDisplayName { get; } = studentDisplayName;
        public string TokenHash { get; } = tokenHash;
        public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;
        public Guid CreatedByTeacherId { get; } = createdByTeacherId;
        public bool Consumed { get; set; }

        public PersistedEnrollmentTicket ToPersisted() =>
            new(
                DeviceId,
                SchoolId,
                ClassId,
                StudentId,
                StudentDisplayName,
                TokenHash,
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

        public PersistedDevice ToPersisted() =>
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
                LatestHeartbeat,
                ConnectionActive,
                SessionId,
                Revoked);

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
                false);
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
