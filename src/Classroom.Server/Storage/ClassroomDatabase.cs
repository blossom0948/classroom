using System.Globalization;
using Microsoft.Data.Sqlite;
using Blossom.Classroom.Core.Audit;
using Blossom.Classroom.Core.Security;
using Blossom.Classroom.Core.Serialization;
using Blossom.Classroom.Protocol.Models;
using Blossom.Classroom.Protocol.Serialization;
using Blossom.Classroom.Server.Configuration;
using Blossom.Classroom.Server.Models;
using Blossom.Classroom.Server.Security;

namespace Blossom.Classroom.Server.Storage;

public sealed class ClassroomDatabase : IDisposable
{
    public const int CurrentSchemaVersion = 5;

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
        else if (version < CurrentSchemaVersion)
        {
            ExecuteNonQuery(
                connection,
                transaction,
                "UPDATE SchemaInfo SET Version = @version WHERE Id = 1;",
                ("@version", CurrentSchemaVersion));
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
                IsAdmin INTEGER NOT NULL DEFAULT 0,
                CreatedAtUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS TeacherIdentities (
                Provider TEXT NOT NULL,
                Subject TEXT NOT NULL,
                TeacherId TEXT NOT NULL,
                Email TEXT NULL,
                CreatedAtUtc TEXT NOT NULL,
                PRIMARY KEY (Provider, Subject)
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
                JoinCode TEXT NULL,
                JoinCodeHash TEXT NULL,
                JoinCodeCreatedAtUtc TEXT NULL,
                JoinCodeLastUsedAtUtc TEXT NULL,
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
            CREATE TABLE IF NOT EXISTS AdminGrants (
                Identifier TEXT NOT NULL,
                SchoolId TEXT NOT NULL,
                CreatedByTeacherId TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                IsActive INTEGER NOT NULL DEFAULT 1,
                PRIMARY KEY (Identifier, SchoolId)
            );
            CREATE TABLE IF NOT EXISTS StudentExitPins (
                SchoolId TEXT PRIMARY KEY,
                PinHash TEXT NOT NULL,
                UpdatedByTeacherId TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Classes_TeacherId ON Classes (TeacherId);
            CREATE INDEX IF NOT EXISTS IX_Devices_ClassId ON Devices (ClassId);
            CREATE INDEX IF NOT EXISTS IX_AuditEvents_ClassId_TimestampUtc ON AuditEvents (ClassId, TimestampUtc);
            CREATE INDEX IF NOT EXISTS IX_Commands_DeviceId_State ON Commands (DeviceId, State);
            """);

        // Keep migrations idempotent so an existing pilot database upgrades in place.
        if (!HasColumn(connection, transaction, "Users", "IsAdmin"))
        {
            ExecuteNonQuery(
                connection,
                transaction,
                "ALTER TABLE Users ADD COLUMN IsAdmin INTEGER NOT NULL DEFAULT 0;");
        }
        // Keep this idempotent so an existing pilot database upgrades in place.
        if (!HasColumn(connection, transaction, "EnrollmentTickets", "JoinCodeHash"))
        {
            ExecuteNonQuery(
                connection,
                transaction,
                "ALTER TABLE EnrollmentTickets ADD COLUMN JoinCodeHash TEXT NULL;");
        }
        if (!HasColumn(connection, transaction, "EnrollmentTickets", "JoinCode"))
        {
            ExecuteNonQuery(
                connection,
                transaction,
                "ALTER TABLE EnrollmentTickets ADD COLUMN JoinCode TEXT NULL;");
        }
        if (!HasColumn(connection, transaction, "EnrollmentTickets", "JoinCodeCreatedAtUtc"))
        {
            ExecuteNonQuery(
                connection,
                transaction,
                "ALTER TABLE EnrollmentTickets ADD COLUMN JoinCodeCreatedAtUtc TEXT NULL;");
        }
        if (!HasColumn(connection, transaction, "EnrollmentTickets", "JoinCodeLastUsedAtUtc"))
        {
            ExecuteNonQuery(
                connection,
                transaction,
                "ALTER TABLE EnrollmentTickets ADD COLUMN JoinCodeLastUsedAtUtc TEXT NULL;");
        }

        ExecuteNonQuery(
            connection,
            transaction,
            "UPDATE Users SET LoginName = @loginName, IsAdmin = 1 WHERE Id = @teacherId AND Role = 'Teacher';",
            ("@loginName", options.BootstrapTeacherLogin),
            ("@teacherId", ToDb(options.DevelopmentTeacherId)));
        ExecuteNonQuery(
            connection,
            transaction,
            "UPDATE Users SET IsAdmin = 1 WHERE LoginName = 'blossom0948';");

        ExecuteNonQuery(
            connection,
            transaction,
            """
            INSERT OR IGNORE INTO Users
                (Id, SchoolId, Role, LoginName, DisplayName, PasswordHash, IsActive, IsAdmin, CreatedAtUtc)
            VALUES
                (@id, @schoolId, 'Teacher', @loginName, @displayName, @passwordHash, 1, 1, @createdAtUtc);
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
            INSERT OR IGNORE INTO AdminGrants
                (Identifier, SchoolId, CreatedByTeacherId, CreatedAtUtc, IsActive)
            VALUES
                ('blossom0948@gmail.com', @schoolId, @teacherId, @createdAtUtc, 1);
            """,
            ("@schoolId", ToDb(options.DevelopmentSchoolId)),
            ("@teacherId", ToDb(options.DevelopmentTeacherId)),
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

    public bool IsReady()
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }
        catch (Exception exception) when (
            exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
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

    public TeacherAccount CreateOrGetFirebaseTeacher(
        FirebaseIdentity identity,
        string? requestedDisplayName = null,
        string? requestedSubject = null)
    {
        if (string.IsNullOrWhiteSpace(identity.Subject))
        {
            throw new ArgumentException("A Firebase subject is required.", nameof(identity));
        }

        var provider = string.IsNullOrWhiteSpace(identity.Provider)
            ? "firebase"
            : identity.Provider.Trim();
        var email = identity.Email.Trim().ToLowerInvariant();
        var displayName = string.IsNullOrWhiteSpace(requestedDisplayName)
            ? identity.DisplayName.Trim()
            : requestedDisplayName.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = email;
        }
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = "새 교사";
        }

        if (!string.IsNullOrWhiteSpace(requestedDisplayName))
        {
            displayName = requestedDisplayName.Trim();
        }
        if (displayName.Length > 80 || displayName.Any(char.IsControl))
        {
            throw new ArgumentException("Teacher display name is invalid.", nameof(requestedDisplayName));
        }

        var subject = string.IsNullOrWhiteSpace(requestedSubject)
            ? "정보"
            : requestedSubject.Trim();
        if (subject.Length > 128 || subject.Any(char.IsControl))
        {
            throw new ArgumentException("Teacher subject is invalid.", nameof(requestedSubject));
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var organizationSchoolId = FindOrganizationSchoolId(
            connection,
            transaction,
            "blossom0948");
        var isAdmin = IsAdministratorIdentifier(email)
            || (organizationSchoolId is Guid organizationId
                && HasActiveAdminGrant(connection, transaction, email, organizationId));
        var account = FindTeacherByIdentity(connection, transaction, provider, identity.Subject.Trim());
        if (account is null && identity.EmailVerified && !string.IsNullOrWhiteSpace(email))
        {
            account = FindTeacherByLogin(connection, transaction, email);
        }

        if (account is not null)
        {
            var targetSchoolId = organizationSchoolId ?? account.SchoolId;
            if (account.SchoolId != targetSchoolId)
            {
                ExecuteNonQuery(
                    connection,
                    transaction,
                    "UPDATE Users SET SchoolId = @schoolId WHERE Id = @teacherId AND Role = 'Teacher';",
                    ("@schoolId", ToDb(targetSchoolId)),
                    ("@teacherId", ToDb(account.Id)));
                ExecuteNonQuery(
                    connection,
                    transaction,
                    "UPDATE Classes SET SchoolId = @schoolId WHERE TeacherId = @teacherId;",
                    ("@schoolId", ToDb(targetSchoolId)),
                    ("@teacherId", ToDb(account.Id)));
                account = account with { SchoolId = targetSchoolId };
            }

            if (isAdmin)
            {
                ExecuteNonQuery(
                    connection,
                    transaction,
                    "UPDATE Users SET IsAdmin = 1 WHERE Id = @teacherId AND Role = 'Teacher';",
                    ("@teacherId", ToDb(account.Id)));
            }

            if (!string.IsNullOrWhiteSpace(requestedDisplayName))
            {
                ExecuteNonQuery(
                    connection,
                    transaction,
                    "UPDATE Users SET DisplayName = @displayName WHERE Id = @teacherId AND Role = 'Teacher';",
                    ("@displayName", displayName),
                    ("@teacherId", ToDb(account.Id)));
                account = account with { DisplayName = displayName };
            }

            if (!string.IsNullOrWhiteSpace(requestedSubject))
            {
                ExecuteNonQuery(
                    connection,
                    transaction,
                    "UPDATE Classes SET DefaultSubject = @subject WHERE TeacherId = @teacherId;",
                    ("@subject", subject),
                    ("@teacherId", ToDb(account.Id)));
            }

            transaction.Commit();
            return account;
        }

        if (account is null)
        {
            var loginName = CreateFirebaseLoginName(
                connection,
                transaction,
                email,
                identity.Subject.Trim());
            var teacherId = Guid.NewGuid();
            var schoolId = organizationSchoolId ?? Guid.NewGuid();
            var classId = Guid.NewGuid();
            account = new TeacherAccount(
                teacherId,
                schoolId,
                "Teacher",
                loginName,
                displayName,
                PasswordSecurity.HashPassword(TokenSecurity.CreateToken()));

            ExecuteNonQuery(
                connection,
                transaction,
                """
                INSERT INTO Users
                    (Id, SchoolId, Role, LoginName, DisplayName, PasswordHash, IsActive, IsAdmin, CreatedAtUtc)
                VALUES
                    (@id, @schoolId, 'Teacher', @loginName, @displayName, @passwordHash, 1, @isAdmin, @createdAtUtc);
                """,
                ("@id", ToDb(account.Id)),
                ("@schoolId", ToDb(account.SchoolId)),
                ("@loginName", account.LoginName),
                ("@displayName", account.DisplayName),
                ("@passwordHash", account.PasswordHash),
                ("@isAdmin", isAdmin ? 1 : 0),
                ("@createdAtUtc", ToDb(DateTimeOffset.UtcNow)));
            ExecuteNonQuery(
                connection,
                transaction,
                """
                INSERT INTO Classes (Id, SchoolId, TeacherId, Name, DefaultSubject)
                VALUES (@id, @schoolId, @teacherId, @name, @subject);
                """,
                ("@id", ToDb(classId)),
                ("@schoolId", ToDb(schoolId)),
                ("@teacherId", ToDb(teacherId)),
                ("@name", "내 학급"),
                ("@subject", subject));
        }

        ExecuteNonQuery(
            connection,
            transaction,
            """
            INSERT OR IGNORE INTO TeacherIdentities
                (Provider, Subject, TeacherId, Email, CreatedAtUtc)
            VALUES
                (@provider, @subject, @teacherId, @email, @createdAtUtc);
            """,
            ("@provider", provider),
            ("@subject", identity.Subject.Trim()),
            ("@teacherId", ToDb(account.Id)),
            ("@email", string.IsNullOrWhiteSpace(email) ? null : email),
            ("@createdAtUtc", ToDb(DateTimeOffset.UtcNow)));

        transaction.Commit();
        return account;
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

    public bool TryGetClassSchoolId(Guid classId, out Guid schoolId)
    {
        schoolId = Guid.Empty;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SchoolId FROM Classes WHERE Id = @classId;";
        command.Parameters.AddWithValue("@classId", ToDb(classId));
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

    public bool GetTeacherSchoolId(Guid teacherId, out Guid schoolId)
    {
        schoolId = Guid.Empty;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SchoolId FROM Users WHERE Id = @teacherId AND Role = 'Teacher' AND IsActive = 1;";
        command.Parameters.AddWithValue("@teacherId", ToDb(teacherId));
        var value = command.ExecuteScalar();
        if (value is not string schoolValue || !Guid.TryParse(schoolValue, out schoolId))
        {
            schoolId = Guid.Empty;
            return false;
        }

        return true;
    }

    public bool IsTeacherAdmin(Guid teacherId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT IsAdmin FROM Users WHERE Id = @teacherId AND Role = 'Teacher' AND IsActive = 1;";
        command.Parameters.AddWithValue("@teacherId", ToDb(teacherId));
        return Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture) != 0;
    }

    public StudentExitPinStatus GetStudentExitPinStatus(Guid schoolId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT UpdatedAtUtc FROM StudentExitPins WHERE SchoolId = @schoolId;";
        command.Parameters.AddWithValue("@schoolId", ToDb(schoolId));
        var value = command.ExecuteScalar();
        return value is string updatedAtUtc
            ? new StudentExitPinStatus(true, ParseDate(updatedAtUtc))
            : new StudentExitPinStatus(false, null);
    }

    public void SetStudentExitPin(Guid requesterId, string? pin)
    {
        ValidateStudentExitPin(pin);
        var hash = PasswordSecurity.HashPassword(pin!);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        Guid schoolId;
        using (var requesterCommand = connection.CreateCommand())
        {
            requesterCommand.Transaction = transaction;
            requesterCommand.CommandText = """
                SELECT SchoolId
                FROM Users
                WHERE Id = @teacherId
                  AND Role = 'Teacher'
                  AND IsActive = 1
                  AND IsAdmin = 1;
                """;
            requesterCommand.Parameters.AddWithValue("@teacherId", ToDb(requesterId));
            var value = requesterCommand.ExecuteScalar();
            if (value is not string schoolValue || !Guid.TryParse(schoolValue, out schoolId))
            {
                throw new InvalidOperationException("관리자 계정을 확인할 수 없습니다.");
            }
        }

        ExecuteNonQuery(
            connection,
            transaction,
            """
            INSERT INTO StudentExitPins (SchoolId, PinHash, UpdatedByTeacherId, UpdatedAtUtc)
            VALUES (@schoolId, @pinHash, @teacherId, @updatedAtUtc)
            ON CONFLICT(SchoolId) DO UPDATE SET
                PinHash = excluded.PinHash,
                UpdatedByTeacherId = excluded.UpdatedByTeacherId,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """,
            ("@schoolId", ToDb(schoolId)),
            ("@pinHash", hash),
            ("@teacherId", ToDb(requesterId)),
            ("@updatedAtUtc", ToDb(DateTimeOffset.UtcNow)));
        transaction.Commit();
    }

    public StudentExitPinVerification VerifyStudentExitPin(Guid schoolId, string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin)
            || pin.Length is < 6 or > 64
            || pin.Any(char.IsControl))
        {
            return new StudentExitPinVerification(false, false);
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT PinHash FROM StudentExitPins WHERE SchoolId = @schoolId;";
        command.Parameters.AddWithValue("@schoolId", ToDb(schoolId));
        var value = command.ExecuteScalar();
        if (value is not string hash || string.IsNullOrWhiteSpace(hash))
        {
            return new StudentExitPinVerification(false, false);
        }

        return new StudentExitPinVerification(true, PasswordSecurity.VerifyPassword(pin, hash));
    }

    public IReadOnlyList<StudentCodeView> GetStudentCodes(Guid schoolId)
    {
        var result = new List<StudentCodeView>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tickets.DeviceId,
                   tickets.SchoolId,
                   tickets.ClassId,
                   classes.Name,
                   classes.DefaultSubject,
                   tickets.StudentId,
                   tickets.StudentDisplayName,
                   tickets.JoinCode,
                   COALESCE(tickets.JoinCodeCreatedAtUtc, tickets.ExpiresAtUtc),
                   tickets.JoinCodeLastUsedAtUtc,
                   COALESCE(users.DisplayName, '관리자')
            FROM EnrollmentTickets AS tickets
            INNER JOIN Classes AS classes ON classes.Id = tickets.ClassId
            LEFT JOIN Users AS users ON users.Id = tickets.CreatedByTeacherId
            WHERE tickets.SchoolId = @schoolId
              AND tickets.JoinCode IS NOT NULL
              AND tickets.JoinCode <> ''
              AND tickets.JoinCodeHash IS NOT NULL
            ORDER BY classes.Name, tickets.StudentDisplayName;
            """;
        command.Parameters.AddWithValue("@schoolId", ToDb(schoolId));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new StudentCodeView(
                ParseGuid(reader.GetString(0)),
                ParseGuid(reader.GetString(1)),
                ParseGuid(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                ParseGuid(reader.GetString(5)),
                reader.GetString(6),
                reader.GetString(7),
                ParseDate(reader.GetString(8)),
                reader.IsDBNull(9) ? null : ParseDate(reader.GetString(9)),
                reader.GetString(10)));
        }

        return result;
    }

    public IReadOnlyList<TeacherDirectoryEntry> GetTeacherDirectory(Guid schoolId)
    {
        var result = new List<TeacherDirectoryEntry>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT users.Id,
                   users.LoginName,
                   users.DisplayName,
                   COALESCE((
                       SELECT identities.Email
                       FROM TeacherIdentities AS identities
                       WHERE identities.TeacherId = users.Id
                         AND identities.Email IS NOT NULL
                         AND identities.Email <> ''
                       ORDER BY identities.CreatedAtUtc
                       LIMIT 1), ''),
                   users.IsAdmin
            FROM Users AS users
            WHERE users.SchoolId = @schoolId
              AND users.Role = 'Teacher'
              AND users.IsActive = 1
            ORDER BY users.DisplayName, users.LoginName;
            """;
        command.Parameters.AddWithValue("@schoolId", ToDb(schoolId));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new TeacherDirectoryEntry(
                ParseGuid(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4) != 0));
        }

        return result;
    }

    public IReadOnlyList<AdministratorGrantView> GetActiveAdministratorGrants(Guid schoolId)
    {
        var result = new List<AdministratorGrantView>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Identifier, CreatedAtUtc
            FROM AdminGrants
            WHERE SchoolId = @schoolId AND IsActive = 1
            ORDER BY Identifier;
            """;
        command.Parameters.AddWithValue("@schoolId", ToDb(schoolId));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new AdministratorGrantView(
                reader.GetString(0),
                ParseDate(reader.GetString(1))));
        }

        return result;
    }

    public bool SetTeacherAdmin(Guid requesterId, string? identifier, bool isAdmin)
    {
        var normalizedIdentifier = NormalizeAdministratorIdentifier(identifier);
        if (!isAdmin && IsBootstrapAdministrator(normalizedIdentifier))
        {
            throw new InvalidOperationException("기본 관리자 권한은 해제할 수 없습니다.");
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        Guid schoolId;
        var requesterIsAdmin = false;
        using (var requesterCommand = connection.CreateCommand())
        {
            requesterCommand.Transaction = transaction;
            requesterCommand.CommandText = """
                SELECT SchoolId, IsAdmin
                FROM Users
                WHERE Id = @teacherId AND Role = 'Teacher' AND IsActive = 1;
                """;
            requesterCommand.Parameters.AddWithValue("@teacherId", ToDb(requesterId));
            using var requesterReader = requesterCommand.ExecuteReader();
            if (!requesterReader.Read()
                || !Guid.TryParse(requesterReader.GetString(0), out schoolId))
            {
                throw new InvalidOperationException("관리자 계정을 확인할 수 없습니다.");
            }

            requesterIsAdmin = requesterReader.GetInt32(1) != 0;
        }

        if (!requesterIsAdmin)
        {
            throw new InvalidOperationException("관리자만 다른 관리자 권한을 변경할 수 있습니다.");
        }

        Guid? targetTeacherId = null;
        using (var targetCommand = connection.CreateCommand())
        {
            targetCommand.Transaction = transaction;
            targetCommand.CommandText = """
                SELECT users.Id
                FROM Users AS users
                WHERE users.SchoolId = @schoolId
                  AND users.Role = 'Teacher'
                  AND users.IsActive = 1
                  AND (
                      lower(users.LoginName) = @identifier
                      OR EXISTS (
                          SELECT 1
                          FROM TeacherIdentities AS identities
                          WHERE identities.TeacherId = users.Id
                            AND lower(COALESCE(identities.Email, '')) = @identifier))
                LIMIT 1;
                """;
            AddParameters(
                targetCommand,
                ("@schoolId", ToDb(schoolId)),
                ("@identifier", normalizedIdentifier));
            var targetValue = targetCommand.ExecuteScalar();
            if (targetValue is string targetValueText && Guid.TryParse(targetValueText, out var parsedTargetId))
            {
                targetTeacherId = parsedTargetId;
            }
        }

        if (!isAdmin && targetTeacherId == requesterId)
        {
            throw new InvalidOperationException("현재 로그인한 관리자 권한은 이 화면에서 해제할 수 없습니다.");
        }

        var now = DateTimeOffset.UtcNow;
        ExecuteNonQuery(
            connection,
            transaction,
            """
            INSERT INTO AdminGrants (Identifier, SchoolId, CreatedByTeacherId, CreatedAtUtc, IsActive)
            VALUES (@identifier, @schoolId, @createdByTeacherId, @createdAtUtc, @isActive)
            ON CONFLICT(Identifier, SchoolId) DO UPDATE SET
                CreatedByTeacherId = excluded.CreatedByTeacherId,
                CreatedAtUtc = excluded.CreatedAtUtc,
                IsActive = excluded.IsActive;
            """,
            ("@identifier", normalizedIdentifier),
            ("@schoolId", ToDb(schoolId)),
            ("@createdByTeacherId", ToDb(requesterId)),
            ("@createdAtUtc", ToDb(now)),
            ("@isActive", isAdmin ? 1 : 0));
        if (targetTeacherId is Guid resolvedTargetId)
        {
            ExecuteNonQuery(
                connection,
                transaction,
                "UPDATE Users SET IsAdmin = @isAdmin WHERE Id = @teacherId AND SchoolId = @schoolId;",
                ("@isAdmin", isAdmin ? 1 : 0),
                ("@teacherId", ToDb(resolvedTargetId)),
                ("@schoolId", ToDb(schoolId)));
        }

        transaction.Commit();
        return targetTeacherId is not null;
    }

    public void SaveEnrollmentTicket(PersistedEnrollmentTicket ticket)
    {
        ExecuteNonQuery(
            """
            INSERT INTO EnrollmentTickets
                (DeviceId, SchoolId, ClassId, StudentId, StudentDisplayName, TokenHash, JoinCode, JoinCodeHash, JoinCodeCreatedAtUtc, JoinCodeLastUsedAtUtc, ExpiresAtUtc, CreatedByTeacherId, Consumed)
            VALUES
                (@deviceId, @schoolId, @classId, @studentId, @studentDisplayName, @tokenHash, @joinCode, @joinCodeHash, @joinCodeCreatedAtUtc, @joinCodeLastUsedAtUtc, @expiresAtUtc, @createdByTeacherId, @consumed)
            ON CONFLICT(DeviceId) DO UPDATE SET
                SchoolId = excluded.SchoolId,
                ClassId = excluded.ClassId,
                StudentId = excluded.StudentId,
                StudentDisplayName = excluded.StudentDisplayName,
                TokenHash = excluded.TokenHash,
                JoinCode = excluded.JoinCode,
                JoinCodeHash = excluded.JoinCodeHash,
                JoinCodeCreatedAtUtc = excluded.JoinCodeCreatedAtUtc,
                JoinCodeLastUsedAtUtc = excluded.JoinCodeLastUsedAtUtc,
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
            ("@joinCode", ticket.JoinCode),
            ("@joinCodeHash", ticket.JoinCodeHash),
            ("@joinCodeCreatedAtUtc", ToDbNullable(ticket.JoinCodeCreatedAtUtc)),
            ("@joinCodeLastUsedAtUtc", ToDbNullable(ticket.JoinCodeLastUsedAtUtc)),
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
        command.CommandText = "SELECT DeviceId, SchoolId, ClassId, StudentId, StudentDisplayName, TokenHash, JoinCode, JoinCodeHash, JoinCodeCreatedAtUtc, JoinCodeLastUsedAtUtc, ExpiresAtUtc, CreatedByTeacherId, Consumed FROM EnrollmentTickets;";
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
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : ParseDate(reader.GetString(8)),
                reader.IsDBNull(9) ? null : ParseDate(reader.GetString(9)),
                ParseDate(reader.GetString(10)),
                ParseGuid(reader.GetString(11)),
                reader.GetInt32(12) != 0));
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

    private static bool HasColumn(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

    private static Guid? FindOrganizationSchoolId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string bootstrapLogin)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT SchoolId
            FROM Users
            WHERE Role = 'Teacher' AND IsActive = 1
            ORDER BY CASE
                WHEN lower(LoginName) = lower(@bootstrapLogin) THEN 0
                WHEN lower(LoginName) = 'blossom0948' THEN 1
                WHEN lower(LoginName) = 'teacher' THEN 2
                ELSE 3
            END,
            CreatedAtUtc
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@bootstrapLogin", bootstrapLogin);
        var value = command.ExecuteScalar();
        return value is string schoolValue && Guid.TryParse(schoolValue, out var schoolId)
            ? schoolId
            : null;
    }

    private static bool HasActiveAdminGrant(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string identifier,
        Guid schoolId)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM AdminGrants
            WHERE Identifier = @identifier AND SchoolId = @schoolId AND IsActive = 1
            LIMIT 1;
            """;
        AddParameters(
            command,
            ("@identifier", identifier.Trim().ToLowerInvariant()),
            ("@schoolId", ToDb(schoolId)));
        return command.ExecuteScalar() is not null;
    }

    private static bool IsAdministratorIdentifier(string? identifier) =>
        string.Equals(identifier?.Trim(), "blossom0948@gmail.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsBootstrapAdministrator(string identifier) =>
        string.Equals(identifier, "blossom0948", StringComparison.OrdinalIgnoreCase)
        || string.Equals(identifier, "blossom0948@gmail.com", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeAdministratorIdentifier(string? identifier)
    {
        var normalized = identifier?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Length > 254
            || normalized.Any(char.IsControl)
            || normalized.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("관리자 계정은 이메일 또는 아이디 형식으로 입력하세요.", nameof(identifier));
        }

        if (normalized.Contains('@'))
        {
            if (normalized.Count(character => character == '@') != 1
                || normalized.StartsWith('@')
                || normalized.EndsWith('@'))
            {
                throw new ArgumentException("관리자 이메일 형식이 올바르지 않습니다.", nameof(identifier));
            }
        }
        else if (normalized.Length > 64
            || normalized.Any(character => !char.IsLetterOrDigit(character) && character is not ('.' or '_' or '-')))
        {
            throw new ArgumentException("관리자 아이디 형식이 올바르지 않습니다.", nameof(identifier));
        }

        return normalized;
    }

    private static void ValidateStudentExitPin(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin)
            || pin.Length is < 6 or > 64
            || pin.Any(char.IsControl))
        {
            throw new ArgumentException("학생 앱 종료 비밀번호는 6~64자로 입력하세요.", nameof(pin));
        }
    }

    private static TeacherAccount? FindTeacherByIdentity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string provider,
        string subject)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT users.Id, users.SchoolId, users.Role, users.LoginName, users.DisplayName, users.PasswordHash
            FROM TeacherIdentities AS identities
            INNER JOIN Users AS users ON users.Id = identities.TeacherId
            WHERE identities.Provider = @provider
              AND identities.Subject = @subject
              AND users.Role = 'Teacher'
              AND users.IsActive = 1;
            """;
        AddParameters(
            command,
            ("@provider", provider),
            ("@subject", subject));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTeacher(reader) : null;
    }

    private static TeacherAccount? FindTeacherByLogin(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string loginName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT Id, SchoolId, Role, LoginName, DisplayName, PasswordHash
            FROM Users
            WHERE LoginName = @loginName AND Role = 'Teacher' AND IsActive = 1;
            """;
        AddParameters(command, ("@loginName", loginName));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTeacher(reader) : null;
    }

    private static string CreateFirebaseLoginName(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string email,
        string subject)
    {
        var baseName = !string.IsNullOrWhiteSpace(email) && email.Length <= 64
            ? email
            : $"firebase-{subject[..Math.Min(subject.Length, 24)]}";
        if (FindTeacherByLogin(connection, transaction, baseName) is null)
        {
            return baseName;
        }

        var suffix = $"-{subject[..Math.Min(subject.Length, 12)]}";
        var availableLength = Math.Max(3, 64 - suffix.Length);
        return $"{baseName[..Math.Min(baseName.Length, availableLength)]}{suffix}";
    }

    private static TeacherAccount ReadTeacher(SqliteDataReader reader) =>
        new(
            ParseGuid(reader.GetString(0)),
            ParseGuid(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5));
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
    string? JoinCode,
    string? JoinCodeHash,
    DateTimeOffset? JoinCodeCreatedAtUtc,
    DateTimeOffset? JoinCodeLastUsedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    Guid CreatedByTeacherId,
    bool Consumed);

public sealed record TeacherDirectoryEntry(
    Guid TeacherId,
    string LoginName,
    string DisplayName,
    string Email,
    bool IsAdmin);

public sealed record StudentExitPinVerification(
    bool Configured,
    bool Approved);

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
