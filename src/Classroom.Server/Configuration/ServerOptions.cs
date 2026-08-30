using Blossom.Classroom.Core.Security;

namespace Blossom.Classroom.Server.Configuration;

public sealed record ServerOptions(
    Guid DevelopmentSchoolId,
    Guid DevelopmentClassId,
    Guid DevelopmentTeacherId,
    string DevelopmentTeacherTokenHash,
    TimeSpan HeartbeatTimeout,
    TimeSpan EnrollmentLifetime,
    string DatabasePath = "",
    bool DevelopmentMode = true,
    string BootstrapTeacherLogin = "blossom0948",
    string BootstrapTeacherPassword = "ChangeMe!Classroom123",
    string BootstrapTeacherDisplayName = "담임 교사",
    string BootstrapClassName = "2학년 3반",
    string BootstrapClassSubject = "정보",
    TimeSpan? TeacherSessionLifetime = null,
    string FirebaseProjectId = "",
    string FirebaseWebApiKey = "")
{
    public static readonly Guid DefaultSchoolId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static readonly Guid DefaultClassId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static readonly Guid DefaultTeacherId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static ServerOptions FromConfiguration(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var token = configuration["Classroom:DevTeacherToken"]
            ?? Environment.GetEnvironmentVariable("CLASSROOM_DEV_TEACHER_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            token = environment.IsDevelopment()
                ? "dev-only-classroom-teacher-token"
                : Guid.NewGuid().ToString("N");
        }

        var databaseValue = configuration["Classroom:DatabasePath"]
            ?? Environment.GetEnvironmentVariable("CLASSROOM_DATABASE_PATH")
            ?? Path.Combine("data", "classroom.db");
        var databasePath = Path.GetFullPath(
            Path.IsPathRooted(databaseValue)
                ? databaseValue
                : Path.Combine(environment.ContentRootPath, databaseValue));

        var bootstrapPassword = configuration["Classroom:BootstrapTeacherPassword"]
            ?? Environment.GetEnvironmentVariable("CLASSROOM_BOOTSTRAP_TEACHER_PASSWORD");
        if (string.IsNullOrWhiteSpace(bootstrapPassword))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "CLASSROOM_BOOTSTRAP_TEACHER_PASSWORD must be configured outside Development.");
            }

            bootstrapPassword = "ChangeMe!Classroom123";
        }

        var bootstrapLogin = configuration["Classroom:BootstrapTeacherLogin"]
            ?? Environment.GetEnvironmentVariable("CLASSROOM_BOOTSTRAP_TEACHER_LOGIN")
            ?? "blossom0948";
        if (bootstrapLogin.Length is < 3 or > 64
            || bootstrapLogin.Any(character => !char.IsLetterOrDigit(character) && character is not ('.' or '_' or '-')))
        {
            throw new InvalidOperationException(
                "CLASSROOM_BOOTSTRAP_TEACHER_LOGIN must be 3 to 64 letters, digits, '.', '_' or '-'.");
        }

        var sessionMinutes = ParseInt(
            configuration["Classroom:TeacherSessionLifetimeMinutes"],
            480,
            minimum: 5,
            maximum: 1_440);
        var firebaseProjectId = configuration["Classroom:FirebaseProjectId"]
            ?? Environment.GetEnvironmentVariable("CLASSROOM_FIREBASE_PROJECT_ID")
            ?? string.Empty;
        var firebaseWebApiKey = configuration["Classroom:FirebaseWebApiKey"]
            ?? Environment.GetEnvironmentVariable("CLASSROOM_FIREBASE_WEB_API_KEY")
            ?? string.Empty;

        return new ServerOptions(
            ParseGuid(configuration["Classroom:DevSchoolId"], DefaultSchoolId),
            ParseGuid(configuration["Classroom:DevClassId"], DefaultClassId),
            ParseGuid(configuration["Classroom:DevTeacherId"], DefaultTeacherId),
            TokenSecurity.HashToken(token),
            TimeSpan.FromSeconds(ParseInt(
                configuration["Classroom:HeartbeatTimeoutSeconds"],
                30,
                minimum: 5,
                maximum: 300)),
            TimeSpan.FromMinutes(ParseInt(
                configuration["Classroom:EnrollmentLifetimeMinutes"],
                10,
                minimum: 1,
                maximum: 60)),
            databasePath,
            environment.IsDevelopment(),
            bootstrapLogin,
            bootstrapPassword,
            configuration["Classroom:BootstrapTeacherDisplayName"] ?? "담임 교사",
            configuration["Classroom:BootstrapClassName"] ?? "2학년 3반",
            configuration["Classroom:BootstrapClassSubject"] ?? "정보",
            TimeSpan.FromMinutes(sessionMinutes),
            firebaseProjectId.Trim(),
            firebaseWebApiKey.Trim());
    }

    public bool FirebaseConfigured =>
        !string.IsNullOrWhiteSpace(FirebaseProjectId)
        && !string.IsNullOrWhiteSpace(FirebaseWebApiKey);

    public bool CanTeacherAccess(Guid teacherId, Guid classId) =>
        teacherId == DevelopmentTeacherId && classId == DevelopmentClassId;

    private static Guid ParseGuid(string? value, Guid fallback) =>
        string.IsNullOrWhiteSpace(value)
            ? fallback
            : Guid.TryParse(value, out var parsed)
                ? parsed
                : throw new InvalidOperationException($"Invalid Classroom GUID setting: {value}");

    private static int ParseInt(string? value, int fallback, int minimum, int maximum) =>
        string.IsNullOrWhiteSpace(value)
            ? fallback
            : int.TryParse(value, out var parsed) && parsed >= minimum && parsed <= maximum
                ? parsed
                : throw new InvalidOperationException($"Invalid Classroom numeric setting: {value}");
}
