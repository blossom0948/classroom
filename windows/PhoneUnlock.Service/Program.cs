using System.Net.WebSockets;
using PhoneUnlock.Service.Configuration;
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
builder.Services.AddSingleton<WindowsCredentialStore>();
builder.Services.AddSingleton<WindowsAccountValidator>();
builder.Services.AddSingleton<PairingCoordinator>();
builder.Services.AddSingleton<PhoneConnectionRegistry>();
builder.Services.AddSingleton<PhoneAuthenticationCoordinator>();
builder.Services.AddHostedService<SetupPipeService>();
builder.Services.AddHostedService<AuthPipeService>();

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
    CancellationToken cancellationToken) =>
{
    var token = context.Request.Headers["X-Pairing-Token"].ToString();
    if (string.IsNullOrWhiteSpace(token))
    {
        return Results.Unauthorized();
    }

    try
    {
        var response = await coordinator.PairAsync(token, request, cancellationToken);
        return response is null ? Results.Unauthorized() : Results.Json(response);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
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
        context.RequestAborted);
    if (phone is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await registry.AcceptAsync(phone, socket, context.RequestAborted);
});

await app.RunAsync();
