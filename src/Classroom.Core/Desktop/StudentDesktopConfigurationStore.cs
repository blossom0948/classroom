using System.Text.Json;

namespace Blossom.Classroom.Core.Desktop;

/// <summary>
/// Stores the small amount of configuration that the per-user tray process
/// needs after Windows signs a user in. The Windows service already keeps its
/// own copy in the service registry; this machine-level copy prevents the
/// tray process from losing the enrollment when a user environment is rebuilt
/// by a school image or a startup policy.
/// </summary>
public static class StudentDesktopConfigurationStore
{
    private const string DirectoryName = "Blossom Classroom Student";
    private const string ConfigurationFileName = "desktop-config.json";
    private const string ConfigurationFormat = "BLOSSOM-CLASSROOM-DESKTOP-V1";

    public static string ConfigurationPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        DirectoryName,
        ConfigurationFileName);

    public static void Save(Guid deviceId, string ipcToken, string agentVersion)
    {
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("Device ID is required.", nameof(deviceId));
        }

        if (string.IsNullOrWhiteSpace(ipcToken)
            || ipcToken.Length is < 16 or > 256
            || ipcToken.Any(char.IsControl))
        {
            throw new ArgumentException("IPC token is invalid.", nameof(ipcToken));
        }

        if (string.IsNullOrWhiteSpace(agentVersion)
            || agentVersion.Length > 64
            || agentVersion.Any(char.IsControl))
        {
            throw new ArgumentException("Agent version is invalid.", nameof(agentVersion));
        }

        var path = ConfigurationPath;
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Student configuration directory is unavailable.");
        Directory.CreateDirectory(directory);
        var temporaryPath = string.Concat(path, ".", Guid.NewGuid().ToString("N"), ".tmp");
        var configuration = new PersistentConfiguration(
            ConfigurationFormat,
            deviceId,
            ipcToken,
            agentVersion,
            DateTimeOffset.UtcNow);
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(configuration, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // The next successful installation can replace this temporary
                // file; it must not hide a completed installation.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup only.
            }
        }
    }

    public static bool TryLoad(out PersistentConfiguration configuration)
    {
        try
        {
            var path = ConfigurationPath;
            if (!File.Exists(path))
            {
                configuration = default!;
                return false;
            }

            var parsed = JsonSerializer.Deserialize<PersistentConfiguration>(
                File.ReadAllText(path).TrimStart('\uFEFF'),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (parsed is null
                || !string.Equals(parsed.Format, ConfigurationFormat, StringComparison.Ordinal)
                || parsed.DeviceId == Guid.Empty
                || string.IsNullOrWhiteSpace(parsed.IpcToken)
                || parsed.IpcToken.Length is < 16 or > 256
                || parsed.IpcToken.Any(char.IsControl)
                || string.IsNullOrWhiteSpace(parsed.AgentVersion)
                || parsed.AgentVersion.Length > 64
                || parsed.AgentVersion.Any(char.IsControl))
            {
                configuration = default!;
                return false;
            }

            configuration = parsed;
            return true;
        }
        catch (IOException)
        {
            configuration = default!;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            configuration = default!;
            return false;
        }
        catch (JsonException)
        {
            configuration = default!;
            return false;
        }
    }

    public sealed record PersistentConfiguration(
        string Format,
        Guid DeviceId,
        string IpcToken,
        string AgentVersion,
        DateTimeOffset SavedAtUtc);
}
