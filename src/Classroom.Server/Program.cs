using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;
using Blossom.Classroom.Protocol.Models;
using Blossom.Classroom.Server.Configuration;
using Blossom.Classroom.Server.Models;
using Blossom.Classroom.Server.Networking;
using Blossom.Classroom.Server.Security;
using Blossom.Classroom.Server.Storage;

var builder = WebApplication.CreateBuilder(args);
if (OperatingSystem.IsWindows())
{
    builder.Services.AddWindowsService(serviceOptions =>
    {
        serviceOptions.ServiceName = "ClassroomServer";
    });
}
builder.Services.ConfigureHttpJsonOptions(jsonOptions =>
{
    jsonOptions.SerializerOptions.PropertyNameCaseInsensitive = false;
    jsonOptions.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
var serverOptions = ServerOptions.FromConfiguration(builder.Configuration, builder.Environment);
var tlsTerminatedByProxy = string.Equals(
    builder.Configuration["Classroom:TlsTerminatedByProxy"]
        ?? Environment.GetEnvironmentVariable("CLASSROOM_TLS_TERMINATED_BY_PROXY"),
    "true",
    StringComparison.OrdinalIgnoreCase);
var tlsCertificatePath = builder.Configuration["Classroom:TlsCertificatePath"]
    ?? Environment.GetEnvironmentVariable("CLASSROOM_TLS_CERT_PATH");
var consoleOriginsValue = builder.Configuration["Classroom:ConsoleOrigins"]
    ?? Environment.GetEnvironmentVariable("CLASSROOM_CONSOLE_ORIGINS")
    ?? string.Empty;
var consoleOrigins = new List<string>();
foreach (var originValue in consoleOriginsValue.Split(
    ',',
    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
{
    if (!Uri.TryCreate(originValue, UriKind.Absolute, out var origin)
        || origin.Scheme is not ("http" or "https")
        || origin.AbsolutePath != "/"
        || !string.IsNullOrEmpty(origin.Query)
        || !string.IsNullOrEmpty(origin.Fragment))
    {
        throw new InvalidOperationException(
            $"Invalid CLASSROOM_CONSOLE_ORIGINS entry: {originValue}");
    }

    consoleOrigins.Add(origin.GetLeftPart(UriPartial.Authority));
}
if (!serverOptions.DevelopmentMode && !tlsTerminatedByProxy)
{
    if (string.IsNullOrWhiteSpace(tlsCertificatePath))
    {
        throw new InvalidOperationException(
            "Production server requires CLASSROOM_TLS_CERT_PATH or CLASSROOM_TLS_TERMINATED_BY_PROXY=true.");
    }

    tlsCertificatePath = Path.GetFullPath(
        Path.IsPathRooted(tlsCertificatePath)
            ? tlsCertificatePath
            : Path.Combine(builder.Environment.ContentRootPath, tlsCertificatePath));
    if (!File.Exists(tlsCertificatePath))
    {
        throw new FileNotFoundException("The configured Classroom TLS certificate was not found.", tlsCertificatePath);
    }

    var tlsPortValue = builder.Configuration["Classroom:TlsPort"]
        ?? Environment.GetEnvironmentVariable("CLASSROOM_TLS_PORT")
        ?? "443";
    if (!int.TryParse(tlsPortValue, out var tlsPort) || tlsPort is < 1 or > 65_535)
    {
        throw new InvalidOperationException("CLASSROOM_TLS_PORT must be between 1 and 65535.");
    }

    var certificatePassword = builder.Configuration["Classroom:TlsCertificatePassword"]
        ?? Environment.GetEnvironmentVariable("CLASSROOM_TLS_CERT_PASSWORD");
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.ListenAnyIP(tlsPort, listenOptions =>
        {
            listenOptions.UseHttps(tlsCertificatePath, certificatePassword);
        });
    });
}

builder.Services.AddSingleton(serverOptions);
var classroomDatabase = new ClassroomDatabase(serverOptions.DatabasePath);
classroomDatabase.Initialize(serverOptions);
builder.Services.AddSingleton(classroomDatabase);
builder.Services.AddSingleton<TeacherLoginRateLimiter>();
builder.Services.AddHttpClient<FirebaseIdentityVerifier>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(8);
});
builder.Services.AddSingleton<ClassroomStore>();
builder.Services.AddSingleton<StudentWebSocketHandler>();
builder.Services.AddCors(cors => cors.AddPolicy("TeacherConsole", policy =>
{
    if (consoleOrigins.Count > 0)
    {
        policy.WithOrigins([.. consoleOrigins])
            .AllowAnyHeader()
            .WithMethods("GET", "POST", "DELETE", "OPTIONS")
            .SetPreflightMaxAge(TimeSpan.FromHours(1));
    }
}));

var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(self)";
    var connectSources = consoleOrigins.Count == 0
        ? "'self'"
        : $"'self' {string.Join(' ', consoleOrigins)}";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self' https://www.gstatic.com https://apis.google.com; style-src 'self'; " +
        "img-src 'self' data: https://lh3.googleusercontent.com; " +
        $"connect-src {connectSources} https://classroom-api.blossom0948.cloud https://identitytoolkit.googleapis.com https://securetoken.googleapis.com " +
        "https://firebaseinstallations.googleapis.com https://www.googleapis.com https://apis.google.com " +
        "https://www.gstatic.com https://www.google.com https://*.firebaseapp.com " +
        "https://accounts.google.com https://api.open-meteo.com; frame-src https://*.firebaseapp.com https://accounts.google.com; " +
        "frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    await next();
});
if (!serverOptions.DevelopmentMode && tlsTerminatedByProxy)
{
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        RequireHeaderSymmetry = true
    });
    app.Use(async (context, next) =>
    {
        if (!context.Request.IsHttps)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("HTTPS is required.");
            return;
        }

        await next();
    });
}
if (!serverOptions.DevelopmentMode)
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors("TeacherConsole");
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(15)
});

app.MapGet("/health", (ServerOptions serverOptions) => serverOptions.DevelopmentMode
    ? Results.Json(new
    {
        service = "Classroom.Server",
        version = 1,
        status = "running",
        devSchoolId = serverOptions.DevelopmentSchoolId,
        devClassId = serverOptions.DevelopmentClassId
    })
    : Results.Json(new
    {
        service = "Classroom.Server",
        version = 1,
        status = "running"
    }));

app.MapGet("/health/ready", (ClassroomDatabase database) => database.IsReady()
    ? Results.Json(new { service = "Classroom.Server", status = "ready", database = "available" })
    : Results.Json(
        new { service = "Classroom.Server", status = "not-ready", database = "unavailable" },
        statusCode: StatusCodes.Status503ServiceUnavailable));

app.MapPost("/auth/login", (
    HttpContext context,
    TeacherLoginRequest request,
    ServerOptions serverOptions,
    ClassroomDatabase database,
    TeacherLoginRateLimiter rateLimiter) =>
{
    var normalizedLogin = request.LoginName?.Trim() ?? string.Empty;
    var rateLimitKey = $"{context.Connection.RemoteIpAddress}|{normalizedLogin.ToUpperInvariant()}";
    if (!rateLimiter.TryAcquire(rateLimitKey))
    {
        context.Response.Headers.RetryAfter = "60";
        return Results.Json(
            new { code = "LOGIN_RATE_LIMITED", message = "Too many login attempts. Try again later." },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    if (string.IsNullOrWhiteSpace(request.LoginName)
        || string.IsNullOrWhiteSpace(request.Password)
        || request.LoginName.Length > 64
        || request.Password.Length > 256
        || !database.TryGetTeacher(normalizedLogin, out var account)
        || account is null
        || !PasswordSecurity.VerifyPassword(request.Password, account.PasswordHash))
    {
        return Results.Json(
            new { code = "INVALID_CREDENTIALS", message = "Login name or password is incorrect." },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    rateLimiter.Reset(rateLimitKey);
    var lifetime = serverOptions.TeacherSessionLifetime ?? TimeSpan.FromHours(8);
    var accessToken = database.CreateTeacherSession(account.Id, lifetime);
    var classes = database.GetClassesForTeacher(account.Id);
    return Results.Ok(new TeacherLoginResponse(
        accessToken,
        DateTimeOffset.UtcNow.Add(lifetime),
        account.Id,
        account.DisplayName,
        classes,
        database.IsTeacherAdmin(account.Id)));
});

app.MapPost("/auth/firebase-login", async (
    HttpContext context,
    FirebaseLoginRequest? request,
    ServerOptions serverOptions,
    ClassroomDatabase database,
    FirebaseIdentityVerifier verifier,
    TeacherLoginRateLimiter rateLimiter,
    CancellationToken cancellationToken) =>
{
    if (!serverOptions.FirebaseConfigured)
    {
        return Results.Json(
            new { code = "FIREBASE_NOT_CONFIGURED", message = "Firebase authentication is not configured on the server." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var rateLimitKey = $"{context.Connection.RemoteIpAddress}|FIREBASE";
    if (!rateLimiter.TryAcquire(rateLimitKey))
    {
        context.Response.Headers.RetryAfter = "60";
        return Results.Json(
            new { code = "LOGIN_RATE_LIMITED", message = "Too many login attempts. Try again later." },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    if (request is null
        || string.IsNullOrWhiteSpace(request.IdToken)
        || request.IdToken.Length > 16_384)
    {
        return Results.Json(
            new { code = "INVALID_FIREBASE_TOKEN", message = "A Firebase ID token is required." },
            statusCode: StatusCodes.Status400BadRequest);
    }

    var identity = await verifier.VerifyAsync(request.IdToken, cancellationToken);
    if (identity is null)
    {
        return Results.Json(
            new { code = "INVALID_FIREBASE_TOKEN", message = "The Firebase sign-in could not be verified." },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    rateLimiter.Reset(rateLimitKey);
    TeacherAccount account;
    try
    {
        account = database.CreateOrGetFirebaseTeacher(
            identity,
            request.DisplayName,
            request.Subject);
    }
    catch (ArgumentException exception)
    {
        return Results.Json(
            new { code = "INVALID_PROFILE", message = exception.Message },
            statusCode: StatusCodes.Status400BadRequest);
    }
    var lifetime = serverOptions.TeacherSessionLifetime ?? TimeSpan.FromHours(8);
    var accessToken = database.CreateTeacherSession(account.Id, lifetime);
    var classes = database.GetClassesForTeacher(account.Id);
    return Results.Ok(new TeacherLoginResponse(
        accessToken,
        DateTimeOffset.UtcNow.Add(lifetime),
        account.Id,
        account.DisplayName,
        classes,
        database.IsTeacherAdmin(account.Id)));
});

app.MapGet("/auth/me", (
    HttpContext context,
    ServerOptions serverOptions,
    ClassroomDatabase database,
    ClassroomStore store) =>
{
    if (!TeacherAuthentication.TryGetTeacherId(context.Request, serverOptions, database, out var teacherId))
    {
        return Results.Unauthorized();
    }

    var classes = store.GetClassesForTeacher(teacherId);
    var displayName = database.TryGetTeacher(teacherId, out var account)
        && account is not null
        ? account.DisplayName
        : "담임 교사";
    return Results.Ok(new TeacherSessionResponse(
        teacherId,
        displayName,
        classes,
        database.IsTeacherAdmin(teacherId)));
});

app.MapPost("/auth/logout", (
    HttpContext context,
    ClassroomDatabase database) =>
{
    var token = TeacherAuthentication.GetBearerToken(context.Request);
    if (token is not null)
    {
        database.RevokeTeacherSession(token);
    }

    return Results.NoContent();
});

app.MapPost("/auth/change-password", (
    ChangePasswordRequest request,
    HttpContext context,
    ServerOptions serverOptions,
    ClassroomDatabase database) =>
{
    if (!TeacherAuthentication.TryGetTeacherId(context.Request, serverOptions, database, out var teacherId)
        || !database.TryGetTeacher(teacherId, out var account)
        || account is null
        || !PasswordSecurity.VerifyPassword(request.CurrentPassword, account.PasswordHash))
    {
        return Results.Json(
            new { code = "INVALID_CREDENTIALS", message = "The current password is incorrect." },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    try
    {
        var hash = PasswordSecurity.HashPassword(request.NewPassword);
        if (!database.UpdateTeacherPassword(teacherId, hash))
        {
            return Results.NotFound();
        }

        var currentToken = TeacherAuthentication.GetBearerToken(context.Request);
        if (currentToken is not null)
        {
            database.RevokeOtherTeacherSessions(teacherId, currentToken);
        }

        return Results.NoContent();
    }
    catch (ArgumentException exception)
    {
        return Results.Json(
            new { code = "INVALID_PASSWORD", message = exception.Message },
            statusCode: StatusCodes.Status400BadRequest);
    }
});

app.MapGet("/api/classes", (
    HttpContext context,
    ServerOptions serverOptions,
    ClassroomDatabase database,
    ClassroomStore store) =>
{
    if (!TeacherAuthentication.TryGetTeacherId(context.Request, serverOptions, database, out var teacherId))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(store.GetClassesForTeacher(teacherId));
});

app.MapGet("/api/classes/{classId:guid}/session", (
    Guid classId,
    HttpContext context,
    ServerOptions serverOptions,
    ClassroomDatabase database,
    ClassroomStore store) =>
{
    if (!TeacherAuthentication.TryGetTeacherId(context.Request, serverOptions, database, out var teacherId))
    {
        return Results.Unauthorized();
    }

    try
    {
        return Results.Ok(store.GetActiveSession(teacherId, classId));
    }
    catch (ClassroomStoreException exception)
    {
        return Results.Json(
            new { code = exception.Code, message = exception.Message },
            statusCode: StatusCodes.Status403Forbidden);
    }
});

app.MapGet("/api/dev/context", (
    HttpContext context,
    ServerOptions serverOptions,
    ClassroomDatabase database) =>
{
    if (!app.Environment.IsDevelopment())
    {
        return Results.NotFound();
    }

    if (!TeacherAuthentication.TryGetTeacherId(context.Request, serverOptions, database, out var teacherId))
    {
        return Results.Unauthorized();
    }

    return Results.Json(new
    {
        teacherId,
        schoolId = serverOptions.DevelopmentSchoolId,
        classId = serverOptions.DevelopmentClassId
    });
});

app.MapPost("/api/classes/{classId:guid}/enrollment-tickets", (
    Guid classId,
    CreateEnrollmentTicketRequest request,
    HttpContext context,
    ServerOptions serverOptions,
    ClassroomDatabase database,
    ClassroomStore store) =>
{
    if (!TeacherAuthentication.TryGetTeacherId(context.Request, serverOptions, database, out var teacherId))
    {
        return Results.Unauthorized();
    }

    if (!database.IsTeacherAdmin(teacherId))
    {
        return Results.Json(
            new { code = "ADMIN_REQUIRED", message = "학생 코드는 관리자만 발급할 수 있습니다." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    try
    {
        var ticket = store.CreateEnrollmentTicket(
            teacherId,
            classId,
            request.StudentId.GetValueOrDefault(),
            request.StudentDisplayName);
        return Results.Ok(ticket);
    }
    catch (ClassroomStoreException exception)
    {
        return Results.Json(
            new { code = exception.Code, message = exception.Message },
        statusCode: exception.Code == "FORBIDDEN" ? StatusCodes.Status403Forbidden : StatusCodes.Status400BadRequest);
    }
});

app.MapGet("/api/student-codes", (
    HttpContext context,
    ServerOptions serverOptions,
    ClassroomDatabase database,
    ClassroomStore store) =>
{
    if (!TeacherAuthentication.TryGetTeacherId(context.Request, serverOptions, database, out var teacherId))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(store.GetStudentCodes(teacherId));
});

app.MapGet("/api/admin/teachers", (
    HttpContext context,
    ServerOptions serverOptions,
    ClassroomDatabase database) =>
{
    if (!TeacherAuthentication.TryGetTeacherId(context.Request, serverOptions, database, out var teacherId))
    {
        return Results.Unauthorized();
    }

    if (!database.IsTeacherAdmin(teacherId)
        || !database.GetTeacherSchoolId(teacherId, out var schoolId))
    {
        return Results.Json(
            new { code = "ADMIN_REQUIRED", message = "관리자만 관리자 목록을 확인할 수 있습니다." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    return Results.Ok(new
    {
        teachers = database.GetTeacherDirectory(schoolId),
        grants = database.GetActiveAdministratorGrants(schoolId)
    });
});

app.MapPost("/api/admin/teachers", (
    AdministratorRequest? request,
    HttpContext context,
    ServerOptions serverOptions,
    ClassroomDatabase database) =>
{
    if (!TeacherAuthentication.TryGetTeacherId(context.Request, serverOptions, database, out var teacherId))
    {
        return Results.Unauthorized();
    }

    if (!database.IsTeacherAdmin(teacherId))
    {
        return Results.Json(
            new { code = "ADMIN_REQUIRED", message = "관리자만 권한을 변경할 수 있습니다." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    if (request is null)
    {
        return Results.Json(
            new { code = "INVALID_REQUEST", message = "관리자 이메일 또는 아이디가 필요합니다." },
            statusCode: StatusCodes.Status400BadRequest);
    }

    try
    {
        var accountFound = database.SetTeacherAdmin(teacherId, request.Identifier, request.IsAdmin);
        return Results.Ok(new
        {
            identifier = request.Identifier.Trim().ToLowerInvariant(),
            isAdmin = request.IsAdmin,
            accountFound
        });
    }
    catch (ArgumentException exception)
    {
        return Results.Json(
            new { code = "INVALID_IDENTIFIER", message = exception.Message },
            statusCode: StatusCodes.Status400BadRequest);
    }
    catch (InvalidOperationException exception)
    {
        return Results.Json(
            new { code = "ADMIN_CHANGE_REJECTED", message = exception.Message },
            statusCode: StatusCodes.Status409Conflict);
    }
});

app.MapGet("/api/admin/student-exit-pin", (
    HttpContext context,
    ServerOptions serverOptions,
    ClassroomDatabase database) =>
{
    if (!TeacherAuthentication.TryGetTeacherId(context.Request, serverOptions, database, out var teacherId))
    {
        return Results.Unauthorized();
    }

    if (!database.IsTeacherAdmin(teacherId)
        || !database.GetTeacherSchoolId(teacherId, out var schoolId))
    {
        return Results.Json(
            new { code = "ADMIN_REQUIRED", message = "관리자만 학생 앱 종료 비밀번호를 확인할 수 있습니다." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    return Results.Ok(database.GetStudentExitPinStatus(schoolId));
});

app.MapPut("/api/admin/student-exit-pin", (
    StudentExitPinUpdateRequest? request,
    HttpContext context,
    ServerOptions serverOptions,
    ClassroomDatabase database) =>
{
    if (!TeacherAuthentication.TryGetTeacherId(context.Request, serverOptions, database, out var teacherId))
    {
        return Results.Unauthorized();
    }

    if (!database.IsTeacherAdmin(teacherId))
    {
        return Results.Json(
            new { code = "ADMIN_REQUIRED", message = "관리자만 학생 앱 종료 비밀번호를 설정할 수 있습니다." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    try
    {
        database.SetStudentExitPin(teacherId, request?.Pin);
        return database.GetTeacherSchoolId(teacherId, out var schoolId)
            ? Results.Ok(database.GetStudentExitPinStatus(schoolId))
            : Results.Json(
                new { code = "ADMIN_REQUIRED", message = "관리자 학교 정보를 확인할 수 없습니다." },
                statusCode: StatusCodes.Status403Forbidden);
    }
    catch (ArgumentException exception)
    {
        return Results.Json(
            new { code = "INVALID_EXIT_PIN", message = exception.Message },
            statusCode: StatusCodes.Status400BadRequest);
    }
    catch (InvalidOperationException exception)
    {
        return Results.Json(
            new { code = "ADMIN_REQUIRED", message = exception.Message },
            statusCode: StatusCodes.Status403Forbidden);
    }
});

app.MapPost("/api/devices/enroll", (
    DeviceEnrollmentRequest request,
    ClassroomStore store) =>
{
    var result = store.Enroll(request);
    return result.Succeeded
        ? Results.Ok(result.Value)
        : Results.Json(
            new { code = result.Code, message = result.Message },
            statusCode: result.Code is "ENROLLMENT_INVALID" or "ENROLLMENT_NOT_FOUND"
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status409Conflict);
});

app.MapPost("/api/devices/enroll-code", (
    HttpContext context,
    JoinCodeEnrollmentRequest? request,
    ClassroomStore store,
    TeacherLoginRateLimiter rateLimiter) =>
{
    var normalizedCode = request?.JoinCode?.Trim().ToUpperInvariant() ?? string.Empty;
    var rateLimitKey = $"{context.Connection.RemoteIpAddress}|STUDENT-CODE|{normalizedCode}";
    if (!rateLimiter.TryAcquire(rateLimitKey))
    {
        context.Response.Headers.RetryAfter = "60";
        return Results.Json(
            new { code = "ENROLLMENT_RATE_LIMITED", message = "잠시 후 다시 시도해 주세요." },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    if (request is null)
    {
        return Results.Json(
            new { code = "INVALID_REQUEST", message = "학생 코드와 장치 정보가 필요합니다." },
            statusCode: StatusCodes.Status400BadRequest);
    }

    var result = store.EnrollByJoinCode(request);
    if (result.Succeeded)
    {
        rateLimiter.Reset(rateLimitKey);
        return Results.Ok(result.Value);
    }

    var statusCode = result.Code switch
    {
        "ENROLLMENT_USED" or "ENROLLMENT_EXPIRED" => StatusCodes.Status409Conflict,
        "INVALID_REQUEST" => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status401Unauthorized
    };
    return Results.Json(
        new { code = result.Code, message = result.Message },
        statusCode: statusCode);
});

app.MapPost("/api/classes/{classId:guid}/sessions", (
    Guid classId,
    StartClassSessionRequest request,
    HttpContext context,
    ServerOptions serverOptions,
    ClassroomDatabase database,
    ClassroomStore store) =>
{
    if (!TeacherAuthentication.TryGetTeacherId(context.Request, serverOptions, database, out var teacherId))
    {
        return Results.Unauthorized();
    }

    try
    {
        return Results.Ok(store.StartSession(teacherId, classId, request.Subject));
    }
    catch (ClassroomStoreException exception)
    {
        return Results.Json(
            new { code = exception.Code, message = exception.Message },
            statusCode: exception.Code == "FORBIDDEN" ? StatusCodes.Status403Forbidden : StatusCodes.Status409Conflict);
    }
});

app.MapDelete("/api/classes/{classId:guid}/sessions/{sessionId:guid}", (
    Guid classId,
    Guid sessionId,
    HttpContext context,
    ServerOptions serverOptions,
    ClassroomDatabase database,
    ClassroomStore store) =>
{
    if (!TeacherAuthentication.TryGetTeacherId(context.Request, serverOptions, database, out var teacherId))
    {
        return Results.Unauthorized();
    }

    try
    {
        return Results.Ok(store.EndSession(teacherId, classId, sessionId));
    }
    catch (ClassroomStoreException exception)
    {
        return Results.Json(
            new { code = exception.Code, message = exception.Message },
            statusCode: exception.Code == "FORBIDDEN" ? StatusCodes.Status403Forbidden : StatusCodes.Status404NotFound);
    }
});

app.MapGet("/api/classes/{classId:guid}/students", (
    Guid classId,
    HttpContext context,
    ServerOptions serverOptions,
    ClassroomDatabase database,
    ClassroomStore store) =>
{
    if (!TeacherAuthentication.TryGetTeacherId(context.Request, serverOptions, database, out var teacherId))
    {
        return Results.Unauthorized();
    }

    try
    {
        return Results.Ok(store.GetClassStatuses(teacherId, classId));
    }
    catch (ClassroomStoreException exception)
    {
        return Results.Json(
            new { code = exception.Code, message = exception.Message },
            statusCode: StatusCodes.Status403Forbidden);
    }
});

app.MapGet("/api/classes/{classId:guid}/screens", (
    Guid classId,
    HttpContext context,
    ServerOptions serverOptions,
    ClassroomDatabase database,
    ClassroomStore store) =>
{
    if (!TeacherAuthentication.TryGetTeacherId(context.Request, serverOptions, database, out var teacherId))
    {
        return Results.Unauthorized();
    }

    try
    {
        return Results.Ok(store.GetClassScreenFrames(teacherId, classId));
    }
    catch (ClassroomStoreException exception)
    {
        return Results.Json(
            new { code = exception.Code, message = exception.Message },
            statusCode: StatusCodes.Status403Forbidden);
    }
});

app.MapDelete("/api/classes/{classId:guid}/devices/{deviceId:guid}", (
    Guid classId,
    Guid deviceId,
    HttpContext context,
    ServerOptions serverOptions,
    ClassroomDatabase database,
    ClassroomStore store) =>
{
    if (!TeacherAuthentication.TryGetTeacherId(context.Request, serverOptions, database, out var teacherId))
    {
        return Results.Unauthorized();
    }

    try
    {
        return Results.Ok(store.RevokeDevice(teacherId, classId, deviceId));
    }
    catch (ClassroomStoreException exception)
    {
        return Results.Json(
            new { code = exception.Code, message = exception.Message },
            statusCode: exception.Code == "FORBIDDEN"
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status404NotFound);
    }
});

app.MapPost("/api/classes/{classId:guid}/commands", (
    Guid classId,
    CommandRequest command,
    HttpContext context,
    ServerOptions serverOptions,
    ClassroomDatabase database,
    ClassroomStore store) =>
{
    if (!TeacherAuthentication.TryGetTeacherId(context.Request, serverOptions, database, out var teacherId))
    {
        return Results.Unauthorized();
    }

    StoreResult<CommandDispatchSummary> result;
    try
    {
        result = store.QueueCommand(teacherId, classId, command);
    }
    catch (ClassroomStoreException exception)
    {
        return Results.Json(
            new { code = exception.Code, message = exception.Message },
            statusCode: exception.Code == "FORBIDDEN"
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status400BadRequest);
    }

    if (result.Succeeded)
    {
        return Results.Ok(result.Value);
    }

    var statusCode = result.Code switch
    {
        "TARGET_FORBIDDEN" or "FORBIDDEN" => StatusCodes.Status403Forbidden,
        "SESSION_NOT_ACTIVE" => StatusCodes.Status409Conflict,
        "COMMAND_QUEUE_FULL" => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest
    };
    return Results.Json(new { code = result.Code, message = result.Message }, statusCode: statusCode);
});

app.MapGet("/api/classes/{classId:guid}/commands/{requestId:guid}", (
    Guid classId,
    Guid requestId,
    HttpContext context,
    ServerOptions serverOptions,
    ClassroomDatabase database,
    ClassroomStore store) =>
{
    if (!TeacherAuthentication.TryGetTeacherId(context.Request, serverOptions, database, out var teacherId))
    {
        return Results.Unauthorized();
    }

    try
    {
        return Results.Ok(store.GetCommandStatus(teacherId, classId, requestId));
    }
    catch (ClassroomStoreException exception)
    {
        return Results.Json(
            new { code = exception.Code, message = exception.Message },
            statusCode: exception.Code == "FORBIDDEN"
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status404NotFound);
    }
});

app.MapGet("/api/classes/{classId:guid}/audit", (
    Guid classId,
    int? limit,
    HttpContext context,
    ServerOptions serverOptions,
    ClassroomDatabase database,
    ClassroomStore store) =>
{
    if (!TeacherAuthentication.TryGetTeacherId(context.Request, serverOptions, database, out var teacherId))
    {
        return Results.Unauthorized();
    }

    try
    {
        return Results.Ok(store.GetAuditEvents(teacherId, classId, limit ?? 100));
    }
    catch (ClassroomStoreException exception)
    {
        return Results.Json(
            new { code = exception.Code, message = exception.Message },
            statusCode: StatusCodes.Status403Forbidden);
    }
});

app.Map("/ws/student", async (
    HttpContext context,
    StudentWebSocketHandler handler) =>
{
    await handler.HandleAsync(context);
});

await app.RunAsync();
