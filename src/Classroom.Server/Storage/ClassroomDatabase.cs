using System.Globalization;
using Microsoft.Data.Sqlite;
using Blossom.Classroom.Core.Audit;
using Blossom.Classroom.Core.Security;
using Blossom.Classroom.Core.Serialization;
using Blossom.Classroom.Protocol.Models;
using Blossom.Classroom.Protocol.Serialization;
using Blossom.Classroom.Server.Configuration;
using Blossom.Classroom.Server.Security;

namespace Blossom.Classroom.Server.Storage;

public sealed class ClassroomDatabase : IDisposable
{
    public const int CurrentSchemaVersion = 1;

    private readonly string connectionString;

    public ClassroomDatabase(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("A SQLite database path is required.", nameof(databasePath));
        }

        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public void Initialize(ServerOptions options)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        ExecuteNonQuery(connection, transaction, """
            CREATE TABLE IF NOT EXISTS SchemaInfo (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                Version INTEGER NOT NULL
            );
            """);

        var version = ExecuteScalar<long?>(
            connection,
            transaction,
            "SELECT Version FROM SchemaInfo WHERE Id = 1;");
        if (version is null)
        {
            ExecuteNonQuery(
                connection,
                transaction,
                "INSERT INTO SchemaInfo (Id, Version) VALUES (1, @version);",
                ("@version", CurrentSchemaVersion));
        }
        else if (version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Classroom database schema {version} is newer than supported schema {CurrentSchemaVersion}.");
        }

        ExecuteNonQuery(connection, transaction, """
            CREATE TABLE IF NOT EXISTS Users (
                Id TEXT PRIMARY KEY,
                SchoolId TEXT NOT NULL,
                Role TEXT NOT NULL,
                LoginName TEXT NOT NULL COLLATE NOCASE UNIQUE,
                DisplayName TEXT NOT NULL,
                PasswordHash TEXT NOT NULL,
                IsActive INTEGER NOT NULL DEFAULT 1,
                CreatedAtUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Classes (
                Id TEXT PRIMARY KEY,
                SchoolId TEXT NOT NULL,
                TeacherId TEXT NOT NULL,
                Name TEXT NOT NULL,
                DefaultSubject TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Students (
                Id TEXT PRIMARY KEY,
                SchoolId TEXT NOT NULL,
                DisplayName TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS EnrollmentTickets (
                DeviceId TEXT PRIMARY KEY,
                SchoolId TEXT NOT NULL,
                ClassId TEXT NOT NULL,
                StudentId TEXT NOT NULL,
                StudentDisplayName TEXT NOT NULL,
                TokenHash TEXT NOT NULL,
                ExpiresAtUtc TEXT NOT NULL,
                CreatedByTeacherId TEXT NOT NULL,
                Consumed INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS Devices (
                DeviceId TEXT PRIMARY KEY,
                SchoolId TEXT NOT NULL,
                ClassId TEXT NOT NULL,
                StudentId TEXT NOT NULL,
                StudentDisplayName TEXT NOT NULL,
                DeviceName TEXT NOT NULL,
                AgentVersion TEXT NOT NULL,
                DeviceTokenHash TEXT NOT NULL,
                EnrolledAtUtc TEXT NOT NULL,
                LastHeartbeatUtc TEXT NULL,
                LatestHeartbeatJson TEXT NULL,
                ConnectionActive INTEGER NOT NULL DEFAULT 0,
                SessionId TEXT NULL,
                Revoked INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS Sessions (
                SessionId TEXT PRIMARY KEY,
                SchoolId TEXT NOT NULL,
                ClassId TEXT NOT NULL,
                Subject TEXT NOT NULL,
                StartedAtUtc TEXT NOT NULL,
                EndedAtUtc TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS Commands (
                RequestId TEXT NOT NULL,
                DeviceId TEXT NOT NULL,
                TeacherId TEXT NOT NULL,
                ClassId TEXT NOT NULL,
                SessionId TEXT NOT NULL,
                CommandJson TEXT NOT NULL,
                State TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                PRIMARY KEY (RequestId, DeviceId)
            );
            CREATE TABLE IF NOT EXISTS AuditEvents (
                EventId TEXT PRIMARY KEY,
                TimestampUtc TEXT NOT NULL,
                SchoolId TEXT NULL,
                ClassId TEXT NULL,
                SessionId TEXT NULL,
                TeacherId TEXT NULL,
                TeacherDeviceId TEXT NULL,
                StudentId TEXT NULL,
                StudentDeviceId TEXT NULL,
                Action TEXT NOT NULL,
                Result TEXT NOT NULL,
                Reason TEXT NULL,
                RequestId TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS TeacherSessions (
                TokenHash TEXT PRIMARY KEY,
                TeacherId TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                ExpiresAtUtc TEXT NOT NULL,
                Revoked INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS IX_Classes_TeacherId ON Classes (TeacherId);
            CREATE INDEX IF NOT EXISTS IX_Devices_ClassId ON Devices (ClassId);
            CREATE INDEX IF NOT EXISTS IX_AuditEvents_ClassId_TimestampUtc ON AuditEvents (ClassId, TimestampUtc);
            CREATE INDEX IF NOT EXISTS IX_Commands_DeviceId_State ON Commands (DeviceId, State);
            """);

        ExecuteNonQuery(
            connection,
            transaction,
            """
            INSERT OR IGNORE INTO Users
                (Id, SchoolId, Role, LoginName, DisplayName, PasswordHash, IsActive, CreatedAtUtc)
            VALUES
                (@id, @schoolId, 'Teacher', @loginName, @displayName, @passwordHash, 1, @createdAtUtc);
            """,
            ("@id", ToDb(options.DevelopmentTeacherId)),
            ("@schoolId", ToDb(options.DevelopmentSchoolId)),
            ("@loginName", options.BootstrapTeacherLogin),
            ("@displayName", options.BootstrapTeacherDisplayName),
            ("@passwordHash", PasswordSecurity.HashPassword(options.BootstrapTeacherPassword)),
            ("@createdAtUtc", ToDb(DateTimeOffset.UtcNow)));
        ExecuteNonQuery(
            connection,
            transaction,
            """
            INSERT OR IGNORE INTO Classes (Id, SchoolId, TeacherId, Name, DefaultSubject)
            VALUES (@id, @schoolId, @teacherId, @name, @subject);
            """,
            ("@id", ToDb(options.DevelopmentClassId)),
            ("@schoolId", ToDb(options.DevelopmentSchoolId)),
            ("@teacherId", ToDb(options.DevelopmentTeacherId)),
            ("@name", options.BootstrapClassName),
            ("@subject", options.BootstrapClassSubject));

        transaction.Commit();
    }

    public bool TryGetTeacher(string loginName, out TeacherAccount? account)
    {
        account = null;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, SchoolId, Role, LoginName, DisplayName, PasswordHash
            FROM Users
            WHERE LoginName = @loginName AND Role = 'Teacher' AND IsActive = 1;
            """;
        command.Parameters.AddWithValue("@loginName", loginName);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }

        account = new TeacherAccount(
            ParseGuid(reader.GetString(0)),
            ParseGuid(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5));
        return true;
    }

    public bool TryGetTeacher(Guid teacherId, out TeacherAccount? account)
    {
        account = null;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, SchoolId, Role, LoginName, DisplayName, PasswordHash
            FROM Users
            WHERE Id = @teacherId AND Role = 'Teacher' AND IsActive = 1;
            """;
        command.Parameters.AddWithValue("@teacherId", ToDb(teacherId));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }

        account = new TeacherAccount(
            ParseGuid(reader.GetString(0)),
            ParseGuid(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5));
        return true;
    }

    public bool UpdateTeacherPassword(Guid teacherId, string passwordHash)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Users SET PasswordHash = @passwordHash WHERE Id = @teacherId AND IsActive = 1;";
        command.Parameters.AddWithValue("@passwordHash", passwordHash);
        command.Parameters.AddWithValue("@teacherId", ToDb(teacherId));
        return command.ExecuteNonQuery() == 1;
    }

    public bool TeacherHasClass(Guid teacherId, Guid classId, out Guid schoolId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SchoolId FROM Classes WHERE Id = @classId AND TeacherId = @teacherId;";
        command.Parameters.AddWithValue("@classId", ToDb(classId));
        command.Parameters.AddWithValue("@teacherId", ToDb(teacherId));
        var value = command.ExecuteScalar();
        if (value is not string schoolValue || !Guid.TryParse(schoolValue, out schoolId))
        {
            schoolId = Guid.Empty;
            return false;
        }

        return true;
    }

    public IReadOnlyList<TeacherClass> GetClassesForTeacher(Guid teacherId)
    {
        var result = new List<TeacherClass>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, SchoolId, Name, DefaultSubject
            FROM Classes
            WHERE TeacherId = @teacherId
            ORDER BY Name;
            """;
        command.Parameters.AddWithValue("@teacherId", ToDb(teacherId));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new TeacherClass(
                ParseGuid(reader.GetString(0)),
                ParseGuid(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return result;
    }

    public void SaveEnrollmentTicket(PersistedEnrollmentTicket ticket)
    {
        ExecuteNonQuery(
            """
            INSERT INTO EnrollmentTickets
                (DeviceId, SchoolId, ClassId, StudentId, StudentDisplayName, TokenHash, ExpiresAtUtc, CreatedByTeacherId, Consumed)
            VALUES
                (@deviceId, @schoolId, @classId, @studentId, @studentDisplayName, @tokenHash, @expiresAtUtc, @createdByTeacherId, @consumed)
            ON CONFLICT(DeviceId) DO UPDATE SET
                SchoolId = excluded.SchoolId,
                ClassId = excluded.ClassId,
                StudentId = excluded.StudentId,
                StudentDisplayName = excluded.StudentDisplayName,
                TokenHash = excluded.TokenHash,
                ExpiresAtUtc = excluded.ExpiresAtUtc,
                CreatedByTeacherId = excluded.CreatedByTeacherId,
                Consumed = excluded.Consumed;
            """,
            ("@deviceId", ToDb(ticket.DeviceId)),
            ("@schoolId", ToDb(ticket.SchoolId)),
            ("@classId", ToDb(ticket.ClassId)),
            ("@studentId", ToDb(ticket.StudentId)),
            ("@studentDisplayName", ticket.StudentDisplayName),
            ("@tokenHash", ticket.TokenHash),
            ("@expiresAtUtc", ToDb(ticket.ExpiresAtUtc)),
            ("@createdByTeacherId", ToDb(ticket.CreatedByTeacherId)),
            ("@consumed", ticket.Consumed ? 1 : 0));
        ExecuteNonQuery(
            """
            INSERT INTO Students (Id, SchoolId, DisplayName, CreatedAtUtc)
            VALUES (@id, @schoolId, @displayName, @createdAtUtc)
            ON CONFLICT(Id) DO UPDATE SET DisplayName = excluded.DisplayName;
            """,
            ("@id", ToDb(ticket.StudentId)),
            ("@schoolId", ToDb(ticket.SchoolId)),
            ("@displayName", ticket.StudentDisplayName),
            ("@createdAtUtc", ToDb(DateTimeOffset.UtcNow)));
    }

    public IReadOnlyList<PersistedEnrollmentTicket> LoadEnrollmentTickets()
    {
        var result = new List<PersistedEnrollmentTicket>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DeviceId, SchoolId, ClassId, StudentId, StudentDisplayName, TokenHash, ExpiresAtUtc, CreatedByTeacherId, Consumed FROM EnrollmentTickets;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new PersistedEnrollmentTicket(
                ParseGuid(reader.GetString(0)),
                ParseGuid(reader.GetString(1)),
                ParseGuid(reader.GetString(2)),
                ParseGuid(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                ParseDate(reader.GetString(6)),
                ParseGuid(reader.GetString(7)),
                reader.GetInt32(8) != 0));
        }

        return result;
    }

    public void SaveDevice(PersistedDevice device)
    {
        ExecuteNonQuery(
            """
            INSERT INTO Devices
                (DeviceId, SchoolId, ClassId, StudentId, StudentDisplayName, DeviceName, AgentVersion,
                 DeviceTokenHash, EnrolledAtUtc, LastHeartbeatUtc, LatestHeartbeatJson, ConnectionActive, SessionId, Revoked)
            VALUES
                (@deviceId, @schoolId, @classId, @studentId, @studentDisplayName, @deviceName, @agentVersion,
                 @deviceTokenHash, @enrolledAtUtc, @lastHeartbeatUtc, @latestHeartbeatJson, @connectionActive, @sessionId, @revoked)
            ON CONFLICT(DeviceId) DO UPDATE SET
                SchoolId = excluded.SchoolId,
                ClassId = excluded.ClassId,
                StudentId = excluded.StudentId,
                StudentDisplayName = excluded.StudentDisplayName,
                DeviceName = excluded.DeviceName,
                AgentVersion = excluded.AgentVersion,
                DeviceTokenHash = excluded.DeviceTokenHash,
                EnrolledAtUtc = excluded.EnrolledAtUtc,
                LastHeartbeatUtc = excluded.LastHeartbeatUtc,
                LatestHeartbeatJson = excluded.LatestHeartbeatJson,
                ConnectionActive = excluded.ConnectionActive,
                SessionId = excluded.SessionId,
                Revoked = excluded.Revoked;
            """,
            ("@deviceId", ToDb(device.DeviceId)),
            ("@schoolId", ToDb(device.SchoolId)),
            ("@classId", ToDb(device.ClassId)),
            ("@studentId", ToDb(device.StudentId)),
            ("@studentDisplayName", device.StudentDisplayName),
            ("@deviceName", device.DeviceName),
            ("@agentVersion", device.AgentVersion),
            ("@deviceTokenHash", device.DeviceTokenHash),
            ("@enrolledAtUtc", ToDb(device.EnrolledAtUtc)),
            ("@lastHeartbeatUtc", ToDbNullable(device.LastHeartbeatUtc)),
            ("@latestHeartbeatJson", device.LatestHeartbeat is null ? null : ClassroomJson.Serialize(device.LatestHeartbeat)),
            ("@connectionActive", device.ConnectionActive ? 1 : 0),
            ("@sessionId", device.SessionId is null ? null : ToDb(device.SessionId.Value)),
            ("@revoked", device.Revoked ? 1 : 0));
    }

    public IReadOnlyList<PersistedDevice> LoadDevices()
    {
        var result = new List<PersistedDevice>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DeviceId, SchoolId, ClassId, StudentId, StudentDisplayName, DeviceName, AgentVersion,
                   DeviceTokenHash, EnrolledAtUtc, LastHeartbeatUtc, LatestHeartbeatJson, ConnectionActive, SessionId, Revoked
            FROM Devices;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var heartbeatJson = reader.IsDBNull(10) ? null : reader.GetString(10);
            result.Add(new PersistedDevice(
                ParseGuid(reader.GetString(0)),
                ParseGuid(reader.GetString(1)),
                ParseGuid(reader.GetString(2)),
                ParseGuid(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                ParseDate(reader.GetString(8)),
                reader.IsDBNull(9) ? null : ParseDate(reader.GetString(9)),
                heartbeatJson is null ? null : ClassroomJson.Deserialize<DeviceHeartbeat>(heartbeatJson),
                false,
                reader.IsDBNull(12) ? null : ParseGuid(reader.GetString(12)),
                reader.GetInt32(13) != 0));
        }

        return result;
    }

    public void SaveSession(PersistedSession session)
    {
        ExecuteNonQuery(
            """
            INSERT INTO Sessions (SessionId, SchoolId, ClassId, Subject, StartedAtUtc, EndedAtUtc)
            VALUES (@sessionId, @schoolId, @classId, @subject, @startedAtUtc, @endedAtUtc)
            ON CONFLICT(SessionId) DO UPDATE SET
                EndedAtUtc = excluded.EndedAtUtc;
            """,
            ("@sessionId", ToDb(session.SessionId)),
            ("@schoolId", ToDb(session.SchoolId)),
            ("@classId", ToDb(session.ClassId)),
            ("@subject", session.Subject),
            ("@startedAtUtc", ToDb(session.StartedAtUtc)),
            ("@endedAtUtc", ToDbNullable(session.EndedAtUtc)));
    }

    public IReadOnlyList<PersistedSession> LoadSessions()
    {
        var result = new List<PersistedSession>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SessionId, SchoolId, ClassId, Subject, StartedAtUtc, EndedAtUtc FROM Sessions;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new PersistedSession(
                ParseGuid(reader.GetString(0)),
                ParseGuid(reader.GetString(1)),
                ParseGuid(reader.GetString(2)),
                reader.GetString(3),
                ParseDate(reader.GetString(4)),
                reader.IsDBNull(5) ? null : ParseDate(reader.GetString(5))));
        }

        return result;
    }

    public void SaveCommand(PersistedCommand command)
    {
        ExecuteNonQuery(
            """
            INSERT INTO Commands
                (RequestId, DeviceId, TeacherId, ClassId, SessionId, CommandJson, State, CreatedAtUtc)
            VALUES
                (@requestId, @deviceId, @teacherId, @classId, @sessionId, @commandJson, @state, @createdAtUtc)
            ON CONFLICT(RequestId, DeviceId) DO UPDATE SET State = excluded.State;
            """,
            ("@requestId", ToDb(command.RequestId)),
            ("@deviceId", ToDb(command.DeviceId)),
            ("@teacherId", ToDb(command.TeacherId)),
            ("@classId", ToDb(command.ClassId)),
            ("@sessionId", ToDb(command.Command.SessionId)),
            ("@commandJson", ClassroomJson.Serialize(command.Command)),
            ("@state", command.State),
            ("@createdAtUtc", ToDb(command.CreatedAtUtc)));
    }

    public IReadOnlyList<PersistedCommand> LoadCommands()
    {
        var result = new List<PersistedCommand>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT RequestId, DeviceId, TeacherId, ClassId, CommandJson, State, CreatedAtUtc FROM Commands WHERE State NOT IN ('COMPLETED', 'FAILED');";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new PersistedCommand(
                ParseGuid(reader.GetString(0)),
                ParseGuid(reader.GetString(1)),
                ParseGuid(reader.GetString(2)),
                ParseGuid(reader.GetString(3)),
                ClassroomJson.Deserialize<CommandRequest>(reader.GetString(4)),
                reader.GetString(5),
                ParseDate(reader.GetString(6))));
        }

        return result;
    }

    public void UpdateCommandState(Guid requestId, Guid deviceId, string state)
    {
        ExecuteNonQuery(
            "UPDATE Commands SET State = @state WHERE RequestId = @requestId AND DeviceId = @deviceId;",
            ("@state", state),
            ("@requestId", ToDb(requestId)),
            ("@deviceId", ToDb(deviceId)));
    }

    public void AddAudit(AuditEvent entry)
    {
        ExecuteNonQuery(
            """
            INSERT OR IGNORE INTO AuditEvents
                (EventId, TimestampUtc, SchoolId, ClassId, SessionId, TeacherId, TeacherDeviceId,
                 StudentId, StudentDeviceId, Action, Result, Reason, RequestId)
            VALUES
                (@eventId, @timestampUtc, @schoolId, @classId, @sessionId, @teacherId, @teacherDeviceId,
                 @studentId, @studentDeviceId, @action, @result, @reason, @requestId);
            """,
            ("@eventId", ToDb(entry.EventId)),
            ("@timestampUtc", ToDb(entry.TimestampUtc)),
            ("@schoolId", ToDbNullable(entry.SchoolId)),
            ("@classId", ToDbNullable(entry.ClassId)),
            ("@sessionId", ToDbNullable(entry.SessionId)),
            ("@teacherId", ToDbNullable(entry.TeacherId)),
            ("@teacherDeviceId", ToDbNullable(entry.TeacherDeviceId)),
            ("@studentId", ToDbNullable(entry.StudentId)),
            ("@studentDeviceId", ToDbNullable(entry.StudentDeviceId)),
            ("@action", entry.Action),
            ("@result", entry.Result),
            ("@reason", entry.Reason),
            ("@requestId", ToDbNullable(entry.RequestId)));
    }

    public IReadOnlyList<AuditEvent> GetAuditEvents(Guid classId, int limit)
    {
        var result = new List<AuditEvent>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EventId, TimestampUtc, SchoolId, ClassId, SessionId, TeacherId, TeacherDeviceId,
                   StudentId, StudentDeviceId, Action, Result, Reason, RequestId
            FROM AuditEvents
            WHERE ClassId = @classId
            ORDER BY TimestampUtc DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@classId", ToDb(classId));
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 1_000));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new AuditEvent(
                ParseGuid(reader.GetString(0)),
                ParseDate(reader.GetString(1)),
                ParseNullableGuid(reader, 2),
                ParseNullableGuid(reader, 3),
                ParseNullableGuid(reader, 4),
                ParseNullableGuid(reader, 5),
                ParseNullableGuid(reader, 6),
                ParseNullableGuid(reader, 7),
                ParseNullableGuid(reader, 8),
                reader.GetString(9),
                reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                ParseNullableGuid(reader, 12)));
        }

        return result;
    }

    public string CreateTeacherSession(Guid teacherId, TimeSpan lifetime)
    {
        var token = TokenSecurity.CreateToken();
        ExecuteNonQuery(
            "INSERT INTO TeacherSessions (TokenHash, TeacherId, CreatedAtUtc, ExpiresAtUtc, Revoked) VALUES (@tokenHash, @teacherId, @createdAtUtc, @expiresAtUtc, 0);",
            ("@tokenHash", TokenSecurity.HashToken(token)),
            ("@teacherId", ToDb(teacherId)),
            ("@createdAtUtc", ToDb(DateTimeOffset.UtcNow)),
            ("@expiresAtUtc", ToDb(DateTimeOffset.UtcNow.Add(lifetime))));
        return token;
    }

    public bool TryValidateTeacherSession(string token, out Guid teacherId)
    {
        teacherId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sessions.TeacherId, sessions.ExpiresAtUtc
            FROM TeacherSessions AS sessions
            INNER JOIN Users AS users ON users.Id = sessions.TeacherId
            WHERE sessions.TokenHash = @tokenHash
              AND sessions.Revoked = 0
              AND users.Role = 'Teacher'
              AND users.IsActive = 1;
            """;
        command.Parameters.AddWithValue("@tokenHash", TokenSecurity.HashToken(token));
        using var reader = command.ExecuteReader();
        if (!reader.Read()
            || !Guid.TryParse(reader.GetString(0), out teacherId)
            || ParseDate(reader.GetString(1)) <= DateTimeOffset.UtcNow)
        {
            teacherId = Guid.Empty;
            return false;
        }

        return true;
    }

    public void RevokeTeacherSession(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        ExecuteNonQuery(
            "UPDATE TeacherSessions SET Revoked = 1 WHERE TokenHash = @tokenHash;",
            ("@tokenHash", TokenSecurity.HashToken(token)));
    }

    public void RevokeOtherTeacherSessions(Guid teacherId, string currentToken)
    {
        ExecuteNonQuery(
            "UPDATE TeacherSessions SET Revoked = 1 WHERE TeacherId = @teacherId AND TokenHash <> @currentTokenHash;",
            ("@teacherId", ToDb(teacherId)),
            ("@currentTokenHash", TokenSecurity.HashToken(currentToken)));
    }

    public void Dispose() => SqliteConnection.ClearAllPools();

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
        command.ExecuteNonQuery();
        return connection;
    }

    private void ExecuteNonQuery(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = OpenConnection();
        ExecuteNonQuery(connection, null, sql, parameters);
    }

    private static void ExecuteNonQuery(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddParameters(command, parameters);
        command.ExecuteNonQuery();
    }

    private static T? ExecuteScalar<T>(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddParameters(command, parameters);
        var value = command.ExecuteScalar();
        if (value is null or DBNull)
        {
            return default;
        }

        return (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
    }

    private static void AddParameters(SqliteCommand command, params (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    private static string ToDb(Guid value) => value.ToString("D");

    private static string ToDb(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static object ToDbNullable(Guid? value) => value is null ? DBNull.Value : ToDb(value.Value);

    private static object ToDbNullable(DateTimeOffset? value) => value is null ? DBNull.Value : ToDb(value.Value);

    private static Guid ParseGuid(string value) =>
        Guid.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidDataException($"Invalid GUID in Classroom database: {value}");

    private static Guid? ParseNullableGuid(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : ParseGuid(reader.GetString(index));

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

public sealed record TeacherAccount(
    Guid Id,
    Guid SchoolId,
    string Role,
    string LoginName,
    string DisplayName,
    string PasswordHash);

public sealed record TeacherClass(
    Guid Id,
    Guid SchoolId,
    string Name,
    string DefaultSubject);

public sealed record PersistedEnrollmentTicket(
    Guid DeviceId,
    Guid SchoolId,
    Guid ClassId,
    Guid StudentId,
    string StudentDisplayName,
    string TokenHash,
    DateTimeOffset ExpiresAtUtc,
    Guid CreatedByTeacherId,
    bool Consumed);

public sealed record PersistedDevice(
    Guid DeviceId,
    Guid SchoolId,
    Guid ClassId,
    Guid StudentId,
    string StudentDisplayName,
    string DeviceName,
    string AgentVersion,
    string DeviceTokenHash,
    DateTimeOffset EnrolledAtUtc,
    DateTimeOffset? LastHeartbeatUtc,
    DeviceHeartbeat? LatestHeartbeat,
    bool ConnectionActive,
    Guid? SessionId,
    bool Revoked);

public sealed record PersistedSession(
    Guid SessionId,
    Guid SchoolId,
    Guid ClassId,
    string Subject,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc);

public sealed record PersistedCommand(
    Guid RequestId,
    Guid DeviceId,
    Guid TeacherId,
    Guid ClassId,
    CommandRequest Command,
    string State,
    DateTimeOffset CreatedAtUtc);
