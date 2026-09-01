using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using Blossom.Classroom.Student.Service.Commands;
using Blossom.Classroom.Student.Service.Configuration;

namespace Blossom.Classroom.Student.Service;

/// <summary>
/// Downloads and validates signed-in-school-device updates without requiring a
/// Windows restart. A separate executable from the validated staging folder
/// stops the running service, replaces the service and desktop payload, and
/// starts them again. The currently running binaries are never overwritten by
/// the process that has them open.
/// </summary>
public sealed class StudentUpdateWorker(
    StudentAgentOptions options,
    ILogger<StudentUpdateWorker> logger) : BackgroundService
{
    private const string ManifestUrl = "https://classroom-2en.pages.dev/classroom-update.json";
    private const long MaximumPackageBytes = 500L * 1024 * 1024;
    private static readonly TimeSpan InitialCheckDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim checkGate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows() || !TryGetInstallRoot(out _))
        {
            return;
        }

        await Task.Delay(InitialCheckDelay, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await CheckNowAsync(stoppingToken);
                if (!result.Success)
                {
                    logger.LogWarning("Classroom automatic update check failed: {Message}", result.Message);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    public async Task<StudentUpdateCheckResult> CheckNowAsync(CancellationToken cancellationToken)
    {
        var currentVersion = ParseVersion(options.AgentVersion) ?? new Version(0, 0);
        var currentVersionText = currentVersion.ToString(3);
        if (!OperatingSystem.IsWindows())
        {
            return new StudentUpdateCheckResult(
                false,
                "WINDOWS_REQUIRED",
                "학생 앱 업데이트는 Windows에서만 사용할 수 있습니다.",
                currentVersionText,
                null,
                false);
        }

        if (!TryGetInstallRoot(out _))
        {
            return new StudentUpdateCheckResult(
                false,
                "INSTALL_ROOT_NOT_FOUND",
                "설치된 Classroom 학생 앱의 업데이트 위치를 찾을 수 없습니다.",
                currentVersionText,
                null,
                false);
        }

        await checkGate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                return await CheckAndStageAsync(currentVersion, currentVersionText, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is HttpRequestException
                or IOException
                or InvalidDataException
                or JsonException
                or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
            {
                logger.LogWarning("Classroom update check failed: {Message}", exception.Message);
                return new StudentUpdateCheckResult(
                    false,
                    "UPDATE_CHECK_FAILED",
                    "업데이트를 확인하지 못했습니다. 네트워크 연결 후 다시 시도해 주세요.",
                    currentVersionText,
                    null,
                    false);
            }
        }
        finally
        {
            checkGate.Release();
        }
    }

    private async Task<StudentUpdateCheckResult> CheckAndStageAsync(
        Version currentVersion,
        string currentVersionText,
        CancellationToken cancellationToken)
    {
        if (!TryGetInstallRoot(out var installRoot))
        {
            return new StudentUpdateCheckResult(
                false,
                "INSTALL_ROOT_NOT_FOUND",
                "설치된 Classroom 학생 앱의 업데이트 위치를 찾을 수 없습니다.",
                currentVersionText,
                null,
                false);
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Blossom-Classroom-Student-Updater/0.4");
        var manifest = await client.GetFromJsonAsync<UpdateManifest>(
            $"{ManifestUrl}?deviceUpdate={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
            cancellationToken);
        var availableVersion = ParseVersion(manifest?.Version);
        if (manifest is null
            || availableVersion is null
            || !IsAllowedPackageUrl(manifest.PackageUrl))
        {
            return new StudentUpdateCheckResult(
                false,
                "UPDATE_MANIFEST_INVALID",
                "업데이트 정보가 올바르지 않습니다.",
                currentVersionText,
                null,
                false);
        }

        var availableVersionText = availableVersion.ToString(3);
        if (availableVersion <= currentVersion)
        {
            return new StudentUpdateCheckResult(
                true,
                "UP_TO_DATE",
                $"최신 버전입니다 · v{currentVersionText}",
                currentVersionText,
                availableVersionText,
                false);
        }

        var updateRoot = Path.Combine(installRoot, ".updates", availableVersionText);
        var markerPath = Path.Combine(updateRoot, "pending.json");
        var payloadRoot = Path.Combine(updateRoot, "payload");
        if (File.Exists(markerPath) && Directory.Exists(payloadRoot))
        {
            if (TryLaunchUpdateHelper(payloadRoot, installRoot, availableVersionText))
            {
                return UpdateApplyingResult(currentVersionText, availableVersionText);
            }

            return new StudentUpdateCheckResult(
                false,
                "UPDATE_HELPER_START_FAILED",
                "업데이트 도우미를 시작하지 못했습니다. 잠시 후 자동으로 다시 시도합니다.",
                currentVersionText,
                availableVersionText,
                false);
        }

        Directory.CreateDirectory(updateRoot);
        var zipPath = Path.Combine(updateRoot, "Classroom-Windows-x64.zip.download");
        await DownloadPackageAsync(client, new Uri(manifest.PackageUrl), zipPath, cancellationToken);
        ExtractStudentPayload(zipPath, payloadRoot);
        VerifyPayloadVersion(payloadRoot, availableVersion);
        await File.WriteAllTextAsync(
            markerPath,
            JsonSerializer.Serialize(new
            {
                version = availableVersion.ToString(3),
                stagedAtUtc = DateTimeOffset.UtcNow,
                activation = "immediate-helper"
            }),
            cancellationToken);
        TryDelete(zipPath);
        if (!TryLaunchUpdateHelper(payloadRoot, installRoot, availableVersionText))
        {
            return new StudentUpdateCheckResult(
                false,
                "UPDATE_HELPER_START_FAILED",
                "업데이트 도우미를 시작하지 못했습니다. 잠시 후 자동으로 다시 시도합니다.",
                currentVersionText,
                availableVersionText,
                false);
        }

        logger.LogInformation(
            "Classroom Student {Version} is applying immediately through the isolated update helper.",
            availableVersionText);
        return UpdateApplyingResult(currentVersionText, availableVersionText);
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

    private static StudentUpdateCheckResult UpdateApplyingResult(
        string currentVersionText,
        string availableVersionText) =>
        new(
            true,
            "UPDATE_APPLYING",
            $"v{availableVersionText} 업데이트를 적용 중입니다. 잠시 후 학생 앱이 다시 연결됩니다.",
            currentVersionText,
            availableVersionText,
            false);

    private static bool TryLaunchUpdateHelper(
        string payloadRoot,
        string installRoot,
        string version)
    {
        var helper = Path.Combine(payloadRoot, "student-service", "Classroom.Student.Service.exe");
        if (!File.Exists(helper))
        {
            return false;
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = helper,
                WorkingDirectory = Path.GetDirectoryName(helper)!,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--classroom-update-helper");
        process.StartInfo.ArgumentList.Add("--install-root");
        process.StartInfo.ArgumentList.Add(installRoot);
        process.StartInfo.ArgumentList.Add("--payload-root");
        process.StartInfo.ArgumentList.Add(payloadRoot);
        process.StartInfo.ArgumentList.Add("--parent-pid");
        process.StartInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        process.StartInfo.ArgumentList.Add("--version");
        process.StartInfo.ArgumentList.Add(version);

        try
        {
            return process.Start();
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
        finally
        {
            process.Dispose();
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

}
