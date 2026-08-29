namespace Blossom.Classroom.Student.Service.Configuration;

public sealed record StudentAgentOptions(
    Uri ServerUri,
    Guid DeviceId,
    Guid SessionId,
    string DeviceToken,
    string IpcToken,
    string AgentVersion,
    TimeSpan HeartbeatInterval)
{
    public static StudentAgentOptions FromConfiguration(IConfiguration configuration)
    {
        var serverValue = configuration["Classroom:ServerUrl"]
            ?? Environment.GetEnvironmentVariable("CLASSROOM_SERVER_URL")
            ?? "ws://127.0.0.1:48240";
        if (!Uri.TryCreate(serverValue, UriKind.Absolute, out var serverUri)
            || serverUri.Scheme is not ("ws" or "wss" or "http" or "https"))
        {
            throw new InvalidOperationException("CLASSROOM_SERVER_URL must be an absolute ws:// or wss:// URL.");
        }

        var deviceId = ParseRequiredGuid(
            configuration["Classroom:DeviceId"] ?? Environment.GetEnvironmentVariable("CLASSROOM_DEVICE_ID"),
            "CLASSROOM_DEVICE_ID");
        var sessionId = ParseOptionalGuid(
            configuration["Classroom:SessionId"] ?? Environment.GetEnvironmentVariable("CLASSROOM_SESSION_ID"),
            "CLASSROOM_SESSION_ID");
        var token = configuration["Classroom:DeviceToken"]
            ?? Environment.GetEnvironmentVariable("CLASSROOM_DEVICE_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("CLASSROOM_DEVICE_TOKEN must be configured after device enrollment.");
        }

        var ipcToken = configuration["Classroom:IpcToken"]
            ?? Environment.GetEnvironmentVariable("CLASSROOM_IPC_TOKEN")
            ?? string.Empty;
        if (ipcToken.Length > 256 || ipcToken.Any(char.IsControl))
        {
            throw new InvalidOperationException("CLASSROOM_IPC_TOKEN is invalid.");
        }

        var agentVersion = configuration["Classroom:AgentVersion"]
            ?? Environment.GetEnvironmentVariable("CLASSROOM_AGENT_VERSION")
            ?? "0.1.0-dev";
        if (string.IsNullOrWhiteSpace(agentVersion) || agentVersion.Length > 64)
        {
            throw new InvalidOperationException("CLASSROOM_AGENT_VERSION is missing or too long.");
        }

        var intervalValue = configuration["Classroom:HeartbeatIntervalSeconds"]
            ?? Environment.GetEnvironmentVariable("CLASSROOM_HEARTBEAT_INTERVAL_SECONDS");
        var intervalSeconds = string.IsNullOrWhiteSpace(intervalValue)
            ? 10
            : int.TryParse(intervalValue, out var parsed) && parsed is >= 5 and <= 60
                ? parsed
                : throw new InvalidOperationException("Heartbeat interval must be between 5 and 60 seconds.");

        return new StudentAgentOptions(
            NormalizeWebSocketUri(serverUri),
            deviceId,
            sessionId,
            token,
            ipcToken,
            agentVersion,
            TimeSpan.FromSeconds(intervalSeconds));
    }

    private static Guid ParseRequiredGuid(string? value, string name) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new InvalidOperationException($"{name} must be a non-empty GUID.");

    private static Guid ParseOptionalGuid(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Guid.Empty;
        }

        return Guid.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"{name} must be a GUID when configured.");
    }

    private static Uri NormalizeWebSocketUri(Uri value)
    {
        var builder = new UriBuilder(value)
        {
            Scheme = value.Scheme switch
            {
                "http" => "ws",
                "https" => "wss",
                _ => value.Scheme
            },
            Port = value.IsDefaultPort ? -1 : value.Port
        };
        return builder.Uri;
    }
}
