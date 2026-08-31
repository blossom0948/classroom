using System.Text.Json;

namespace Blossom.Classroom.Core.Desktop;

/// <summary>
/// A short-lived marker written by the LocalSystem student service only after
/// the server accepts an administrator-managed exit PIN. The per-user desktop
/// watchdog reads it to avoid immediately relaunching a deliberately closed
/// window during the current Windows boot.
/// </summary>
public static class StudentDesktopExitAuthorization
{
    private const string DirectoryName = "Blossom Classroom Student";
    private const string MarkerDirectoryName = "exit-authorizations";
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(30);

    public static void Grant(Guid deviceId)
    {
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("Device ID is required.", nameof(deviceId));
        }

        var marker = new ExitAuthorizationMarker(
            deviceId,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.Add(MaximumLifetime),
            Environment.TickCount64);
        var path = GetMarkerPath(deviceId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(marker));
    }

    public static void Clear(Guid deviceId)
    {
        try
        {
            File.Delete(GetMarkerPath(deviceId));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static bool IsGrantedForCurrentBoot(Guid deviceId)
    {
        try
        {
            var json = File.ReadAllText(GetMarkerPath(deviceId));
            var marker = JsonSerializer.Deserialize<ExitAuthorizationMarker>(json);
            if (marker is null
                || marker.DeviceId != deviceId
                || marker.ExpiresAtUtc <= DateTimeOffset.UtcNow
                || marker.GrantedAtTickCountMilliseconds < 0
                || Environment.TickCount64 < marker.GrantedAtTickCountMilliseconds)
            {
                return false;
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string GetMarkerPath(Guid deviceId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        DirectoryName,
        MarkerDirectoryName,
        $"{deviceId:N}.json");

    private sealed record ExitAuthorizationMarker(
        Guid DeviceId,
        DateTimeOffset GrantedAtUtc,
        DateTimeOffset ExpiresAtUtc,
        long GrantedAtTickCountMilliseconds);
}
