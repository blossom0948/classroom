using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace PhoneUnlock.Setup;

internal sealed record InstallerRelease(string Tag, Uri DownloadUri, string? Sha256Digest);

internal static class ReleaseUpdateService
{
    private const string ReleasesUrl = "https://api.github.com/repos/blossom0948/windowslogin/releases?per_page=10";
    private const string InstallerAssetName = "PhoneUnlock-Setup.exe";
    private static readonly HttpClient Client = CreateClient();

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static async Task<InstallerRelease> GetLatestInstallerAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(ReleasesUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean())
            {
                continue;
            }

            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(asset.GetProperty("name").GetString(), InstallerAssetName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var tag = release.GetProperty("tag_name").GetString()
                    ?? throw new InvalidDataException("릴리스 버전 정보가 없습니다.");
                var url = asset.GetProperty("browser_download_url").GetString()
                    ?? throw new InvalidDataException("설치 프로그램 주소가 없습니다.");
                var digest = asset.TryGetProperty("digest", out var digestElement)
                    ? digestElement.GetString()
                    : null;
                return new InstallerRelease(tag, new Uri(url), ParseSha256(digest));
            }
        }

        throw new InvalidDataException("최신 릴리스에서 PhoneUnlock-Setup.exe를 찾지 못했습니다.");
    }

    public static bool IsNewerThanCurrent(string tag) => CompareVersions(tag, CurrentVersion) > 0;

    public static async Task<string> DownloadInstallerAsync(
        InstallerRelease release,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var versionDirectory = SafeFilePart(release.Tag);
        var directory = Path.Combine(Path.GetTempPath(), "PhoneUnlock", "Updates", versionDirectory);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, InstallerAssetName);
        var partial = destination + ".download";

        using var response = await Client.GetAsync(release.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[128 * 1024];
            long received = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                received += count;
                if (total is > 0)
                {
                    progress?.Report((int)(received * 100 / total.Value));
                }
            }
        }

        if (release.Sha256Digest is not null)
        {
            await using var file = File.OpenRead(partial);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
            if (!string.Equals(actual, release.Sha256Digest, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new InvalidDataException("다운로드한 설치 프로그램의 SHA-256 검증에 실패했습니다.");
            }
        }

        File.Move(partial, destination, overwrite: true);
        return destination;
    }

    public static Process? LaunchInstaller(string path) => Process.Start(new ProcessStartInfo
    {
        FileName = path,
        UseShellExecute = true,
        Verb = "runas",
        WorkingDirectory = Path.GetDirectoryName(path)!
    });

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"PhoneUnlock-Setup/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static string? ParseSha256(string? digest) =>
        digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true
            ? digest[7..]
            : null;

    private static int CompareVersions(string left, string right)
    {
        var leftVersion = ParseVersion(left);
        var rightVersion = ParseVersion(right);
        var count = Math.Max(leftVersion.Core.Length, rightVersion.Core.Length);
        for (var index = 0; index < count; index++)
        {
            var leftValue = index < leftVersion.Core.Length ? leftVersion.Core[index] : 0;
            var rightValue = index < rightVersion.Core.Length ? rightVersion.Core[index] : 0;
            if (leftValue != rightValue)
            {
                return leftValue.CompareTo(rightValue);
            }
        }

        if (leftVersion.PreRelease.Length == 0 || rightVersion.PreRelease.Length == 0)
        {
            return rightVersion.PreRelease.Length.CompareTo(leftVersion.PreRelease.Length);
        }

        count = Math.Max(leftVersion.PreRelease.Length, rightVersion.PreRelease.Length);
        for (var index = 0; index < count; index++)
        {
            if (index >= leftVersion.PreRelease.Length) return -1;
            if (index >= rightVersion.PreRelease.Length) return 1;
            var leftPart = leftVersion.PreRelease[index];
            var rightPart = rightVersion.PreRelease[index];
            var leftIsNumber = int.TryParse(leftPart, out var leftNumber);
            var rightIsNumber = int.TryParse(rightPart, out var rightNumber);
            if (leftIsNumber && rightIsNumber && leftNumber != rightNumber) return leftNumber.CompareTo(rightNumber);
            if (leftIsNumber != rightIsNumber) return leftIsNumber ? -1 : 1;
            var comparison = string.Compare(leftPart, rightPart, StringComparison.OrdinalIgnoreCase);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    private static string SafeFilePart(string value) =>
        string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private static ParsedVersion ParseVersion(string value)
    {
        var normalized = value.Trim().TrimStart('v', 'V').Split('+')[0];
        var pieces = normalized.Split('-', 2);
        var core = pieces[0].Split('.').Select(part => int.TryParse(part, out var number) ? number : 0).ToArray();
        var preRelease = pieces.Length == 2 ? pieces[1].Split('.') : [];
        return new ParsedVersion(core, preRelease);
    }

    private sealed record ParsedVersion(int[] Core, string[] PreRelease);
}
