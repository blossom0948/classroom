namespace Blossom.Classroom.Student.Desktop.Configuration;

public sealed record StudentDesktopOptions(
    Guid DeviceId,
    string IpcToken,
    string AgentVersion,
    TimeSpan StatusInterval,
    IReadOnlyDictionary<string, string> ApprovedApplications)
{
    public static StudentDesktopOptions FromEnvironment()
    {
        var deviceIdValue = Environment.GetEnvironmentVariable("CLASSROOM_DEVICE_ID");
        if (!Guid.TryParse(deviceIdValue, out var deviceId) || deviceId == Guid.Empty)
        {
            throw new InvalidOperationException("CLASSROOM_DEVICE_ID must be a non-empty GUID.");
        }

        var ipcToken = Environment.GetEnvironmentVariable("CLASSROOM_IPC_TOKEN");
        if (string.IsNullOrWhiteSpace(ipcToken)
            || ipcToken.Length is < 16 or > 256
            || ipcToken.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                "CLASSROOM_IPC_TOKEN must contain 16 to 256 printable characters.");
        }

        var configuredAgentVersion = Environment.GetEnvironmentVariable("CLASSROOM_AGENT_VERSION")
            ?? "0.1.0-dev";
        var executableVersion = typeof(StudentDesktopOptions).Assembly.GetName().Version;
        var agentVersion = PreferExecutableVersion(configuredAgentVersion, executableVersion);
        if (agentVersion.Length is < 1 or > 64 || agentVersion.Any(char.IsControl))
        {
            throw new InvalidOperationException("CLASSROOM_AGENT_VERSION is invalid.");
        }

        var intervalValue = Environment.GetEnvironmentVariable("CLASSROOM_DESKTOP_STATUS_INTERVAL_SECONDS");
        var intervalSeconds = string.IsNullOrWhiteSpace(intervalValue)
            ? 3
            : int.TryParse(intervalValue, out var parsed) && parsed is >= 3 and <= 60
                ? parsed
                : throw new InvalidOperationException(
                    "CLASSROOM_DESKTOP_STATUS_INTERVAL_SECONDS must be between 3 and 60.");

        return new StudentDesktopOptions(
            deviceId,
            ipcToken,
            agentVersion,
            TimeSpan.FromSeconds(intervalSeconds),
            ParseApprovedApplications());
    }

    private static IReadOnlyDictionary<string, string> ParseApprovedApplications()
    {
        var value = Environment.GetEnvironmentVariable("CLASSROOM_APPROVED_APPS")
            ?? "notepad=notepad.exe;calculator=calc.exe";
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0 || separator == entry.Length - 1)
            {
                throw new InvalidOperationException(
                    "CLASSROOM_APPROVED_APPS must use id=executable; pairs.");
            }

            var id = entry[..separator].Trim();
            var executable = entry[(separator + 1)..].Trim();
            if (id.Length is < 1 or > 128
                || id.Any(character => !(char.IsAsciiLetterOrDigit(character)
                    || character is '.' or '_' or '-'))
                || executable.Length is < 1 or > 512
                || executable.Any(char.IsControl)
                || !result.TryAdd(id, executable))
            {
                throw new InvalidOperationException(
                    "CLASSROOM_APPROVED_APPS contains an invalid or duplicate entry.");
            }
        }

        return result;
    }

    private static string PreferExecutableVersion(string configured, Version? executable)
    {
        var normalized = configured.Trim().TrimStart('v', 'V');
        var separator = normalized.IndexOfAny(['-', '+']);
        if (separator >= 0) normalized = normalized[..separator];
        return executable is not null
            && Version.TryParse(normalized, out var parsed)
            && executable > parsed
                ? executable.ToString(3)
                : configured;
    }
}
