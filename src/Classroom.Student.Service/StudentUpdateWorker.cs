using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using Blossom.Classroom.Student.Service.Configuration;

namespace Blossom.Classroom.Student.Service;

/// <summary>
/// Stages signed-in-school-device updates without interrupting a lesson. Files
/// are replaced by Windows during the next boot, before the automatic service
/// and per-user desktop watchdog start. No student action or reinstall is
/// required and running binaries are never overwritten in place.
/// </summary>
public sealed class StudentUpdateWorker(
    StudentAgentOptions options,
    ILogger<StudentUpdateWorker> logger) : BackgroundService
{
    private const string ManifestUrl = "https://classroom-2en.pages.dev/classroom-update.json";
    private const long MaximumPackageBytes = 500L * 1024 * 1024;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows() || !TryGetInstallRoot(out _))
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndStageAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (
                exception is HttpRequestException
                or IOException
                or InvalidDataException
                or JsonException
                or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
            {
                logger.LogWarning("Classroom automatic update check failed: {Message}", exception.Message);
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    private async Task CheckAndStageAsync(CancellationToken cancellationToken)
    {
        if (!TryGetInstallRoot(out var installRoot))
        {
            return;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Blossom-Classroom-Student-Updater/0.4");
        var manifest = await client.GetFromJsonAsync<UpdateManifest>(
            $"{ManifestUrl}?deviceUpdate={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
            cancellationToken);
        var availableVersion = ParseVersion(manifest?.Version);
        var currentVersion = ParseVersion(options.AgentVersion) ?? new Version(0, 0);
        if (availableVersion is null
            || availableVersion <= currentVersion
            || manifest is null
            || !IsAllowedPackageUrl(manifest.PackageUrl))
        {
            return;
        }

        var updateRoot = Path.Combine(installRoot, ".updates", availableVersion.ToString(3));
        var markerPath = Path.Combine(updateRoot, "pending.json");
        if (File.Exists(markerPath))
        {
            return;
        }

        Directory.CreateDirectory(updateRoot);
        var zipPath = Path.Combine(updateRoot, "Classroom-Windows-x64.zip.download");
        var payloadRoot = Path.Combine(updateRoot, "payload");
        await DownloadPackageAsync(client, new Uri(manifest.PackageUrl), zipPath, cancellationToken);
        ExtractStudentPayload(zipPath, payloadRoot);
        VerifyPayloadVersion(payloadRoot, availableVersion);
        SchedulePayload(payloadRoot, installRoot);
        await File.WriteAllTextAsync(
            markerPath,
            JsonSerializer.Serialize(new
            {
                version = availableVersion.ToString(3),
                stagedAtUtc = DateTimeOffset.UtcNow,
                activation = "next-windows-start"
            }),
            cancellationToken);
        TryDelete(zipPath);
        logger.LogInformation(
            "Classroom Student {Version} is staged and will activate automatically at the next Windows start.",
            availableVersion.ToString(3));
    }

    private static async Task DownloadPackageAsync(
        HttpClient client,
        Uri packageUri,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            packageUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumPackageBytes)
        {
            throw new InvalidDataException("Classroom update package is larger than the allowed limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > MaximumPackageBytes)
            {
                throw new InvalidDataException("Classroom update package exceeded the allowed limit.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static void ExtractStudentPayload(string zipPath, string payloadRoot)
    {
        if (Directory.Exists(payloadRoot))
        {
            Directory.Delete(payloadRoot, recursive: true);
        }
        Directory.CreateDirectory(payloadRoot);
        var resolvedRoot = Path.GetFullPath(payloadRoot) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (!normalized.StartsWith("student-service/", StringComparison.OrdinalIgnoreCase)
                && !normalized.StartsWith("student-desktop/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var destination = Path.GetFullPath(Path.Combine(payloadRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Classroom update package contains an unsafe path.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private static void VerifyPayloadVersion(string payloadRoot, Version expected)
    {
        var service = Path.Combine(payloadRoot, "student-service", "Classroom.Student.Service.exe");
        var desktop = Path.Combine(payloadRoot, "student-desktop", "Classroom.Student.Desktop.exe");
        if (!File.Exists(service) || !File.Exists(desktop))
        {
            throw new InvalidDataException("Classroom update package is missing the student components.");
        }
        foreach (var executable in new[] { service, desktop })
        {
            var actual = ParseVersion(FileVersionInfo.GetVersionInfo(executable).FileVersion);
            if (actual is null || actual.Major != expected.Major || actual.Minor != expected.Minor || actual.Build != expected.Build)
            {
                throw new InvalidDataException("Classroom update package version does not match its manifest.");
            }
        }
    }

    private static void SchedulePayload(string payloadRoot, string installRoot)
    {
        ScheduleDirectory(
            Path.Combine(payloadRoot, "student-service"),
            Path.Combine(installRoot, "service"));
        ScheduleDirectory(
            Path.Combine(payloadRoot, "student-desktop"),
            Path.Combine(installRoot, "desktop"));
    }

    private static void ScheduleDirectory(string sourceRoot, string destinationRoot)
    {
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, source);
            var destination = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!MoveFileEx(
                source,
                destination,
                MoveFileFlags.ReplaceExisting | MoveFileFlags.DelayUntilReboot))
            {
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not schedule Classroom update file: {relative}");
            }
        }
    }

    private static bool TryGetInstallRoot(out string installRoot)
    {
        var serviceDirectory = Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        installRoot = Directory.GetParent(serviceDirectory)?.FullName ?? string.Empty;
        var programFiles = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return !string.IsNullOrWhiteSpace(installRoot)
            && serviceDirectory.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(serviceDirectory), "service", StringComparison.OrdinalIgnoreCase);
    }

    private static Version? ParseVersion(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().TrimStart('v', 'V');
        var separator = normalized.IndexOfAny(['-', '+']);
        if (separator >= 0) normalized = normalized[..separator];
        return Version.TryParse(normalized, out var version) ? version : null;
    }

    private static bool IsAllowedPackageUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.Equals(
            "/blossom0948/classroom/releases/latest/download/Classroom-Windows-x64.zip",
            StringComparison.Ordinal);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record UpdateManifest(string Version, string PackageUrl);

    [Flags]
    private enum MoveFileFlags : uint
    {
        ReplaceExisting = 0x1,
        DelayUntilReboot = 0x4
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, MoveFileFlags flags);
}
