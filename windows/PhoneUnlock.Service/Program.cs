using System.Net.WebSockets;
using PhoneUnlock.Core.Protocol;
using PhoneUnlock.Service.Configuration;
using PhoneUnlock.Service.Interop;
using PhoneUnlock.Service.Models;
using PhoneUnlock.Service.Networking;
using PhoneUnlock.Service.Pipes;
using PhoneUnlock.Service.Security;
using PhoneUnlock.Service.Storage;

var builder = WebApplication.CreateBuilder(args);
var paths = ServicePaths.Resolve(builder.Configuration);
var certificateManager = new CertificateManager(paths);
var certificate = certificateManager.LoadOrCreate();

builder.Host.UseWindowsService(options => options.ServiceName = ServiceConstants.ServiceName);
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.ListenAnyIP(ServiceConstants.Port, listen => listen.UseHttps(certificate));
});

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(certificateManager);
builder.Services.AddSingleton<ConfigurationStore>();
builder.Services.AddSingleton<AuditLogStore>();
builder.Services.AddSingleton<WindowsCredentialStore>();
builder.Services.AddSingleton<WindowsSecretStore>();
builder.Services.AddSingleton<PresenceSensorClient>();
builder.Services.AddSingleton<WindowsAccountValidator>();
builder.Services.AddSingleton<PairingCoordinator>();
builder.Services.AddSingleton<PhoneConnectionRegistry>();
builder.Services.AddSingleton<PhoneAuthenticationCoordinator>();
builder.Services.AddSingleton<ProximityUnlockSignal>();
builder.Services.AddSingleton<RemoteUnlockGrantStore>();
builder.Services.AddSingleton<RemotePowerController>();
builder.Services.AddSingleton<WorkstationLockSignal>();
builder.Services.AddSingleton<AgentConnectionState>();
builder.Services.AddSingleton<AgentNotificationQueue>();
builder.Services.AddHostedService<SetupPipeService>();
builder.Services.AddHostedService<AuthPipeService>();
builder.Services.AddHostedService<AgentPipeService>();
builder.Services.AddHostedService<ProximityPresenceService>();
builder.Services.AddHostedService<RemoteUnlockService>();
builder.Services.AddHostedService<RemoteLockService>();
builder.Services.AddHostedService<RemotePowerService>();

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(15),
    AllowedOrigins = { }
});

app.MapGet("/health", () => Results.Json(new
{
    service = "PhoneUnlock",
    version = 1,
    status = "running"
}));

app.MapPost("/pair", async (
    HttpContext context,
    PairRequest request,
    PairingCoordinator coordinator,
    AuditLogStore auditLog,
    CancellationToken cancellationToken) =>
{
    var token = context.Request.Headers["X-Pairing-Token"].ToString();
    if (string.IsNullOrWhiteSpace(token))
    {
        await auditLog.AppendAsync(new AuditEntry(
            DateTimeOffset.UtcNow,
            "PAIRING",
            "REJECTED",
            request.PhoneId,
            request.PhoneName,
            context.Connection.RemoteIpAddress?.ToString(),
            null,
            "페어링 토큰이 없는 요청",
            Suspicious: true), cancellationToken);
        return Results.Unauthorized();
    }

    try
    {
        var response = await coordinator.PairAsync(
            token,
            request,
            context.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        if (response is null)
        {
            await auditLog.AppendAsync(new AuditEntry(
                DateTimeOffset.UtcNow,
                "PAIRING",
                "REJECTED",
                request.PhoneId,
                request.PhoneName,
                context.Connection.RemoteIpAddress?.ToString(),
                null,
                "만료되었거나 이미 사용된 페어링 토큰",
                Suspicious: true), cancellationToken);
            return Results.Unauthorized();
        }

        return Results.Json(response);
    }
    catch (ArgumentException exception)
    {
        await auditLog.AppendAsync(new AuditEntry(
            DateTimeOffset.UtcNow,
            "PAIRING",
            "REJECTED",
            request.PhoneId,
            request.PhoneName,
            context.Connection.RemoteIpAddress?.ToString(),
            null,
            $"잘못된 페어링 요청: {exception.Message}",
            Suspicious: true), cancellationToken);
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapGet("/connection-info", async (
    HttpContext context,
    PhoneConnectionRegistry registry,
    ConfigurationStore configurationStore,
    CancellationToken cancellationToken) =>
{
    var phone = await registry.AuthenticateDeviceAsync(
        context.Request.Query["phoneId"].ToString(),
        context.Request.Headers.Authorization.ToString(),
        context.Connection.RemoteIpAddress?.ToString(),
        cancellationToken);
    if (phone is null)
    {
        return Results.Unauthorized();
    }

    var configuration = await configurationStore.GetAsync(cancellationToken);
    var hosts = CertificateManager.GetLocalAddresses().Select(address => address.ToString()).ToArray();
    return Results.Json(new
    {
        version = ProtocolConstants.Version,
        computerId = configuration.ComputerId,
        computerName = configuration.ComputerName,
        hosts,
        port = ServiceConstants.Port,
        wakeOnLanTargets = CertificateManager.GetWakeOnLanTargets()
    });
});

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var registry = context.RequestServices.GetRequiredService<PhoneConnectionRegistry>();
    var phone = await registry.AuthenticateDeviceAsync(
        context.Request.Query["phoneId"].ToString(),
        context.Request.Headers.Authorization.ToString(),
        context.Connection.RemoteIpAddress?.ToString(),
        context.RequestAborted);
    if (phone is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await registry.AcceptAsync(
        phone,
        socket,
        context.Connection.RemoteIpAddress?.ToString(),
        context.RequestAborted);
});

await app.RunAsync();
