using System.Text.Json;
using PhoneUnlock.Core.Protocol;
using PhoneUnlock.Service.Configuration;
using PhoneUnlock.Service.Models;

namespace PhoneUnlock.Service.Storage;

public sealed class AuditLogStore(ServicePaths paths, ILogger<AuditLogStore> logger)
{
    private const int MaxEntries = 2_000;
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(paths.DataDirectory);
            await File.AppendAllTextAsync(
                paths.AuditLogFile,
                ProtocolJson.SerializeCompact(entry) + Environment.NewLine,
                cancellationToken);
            RestrictFile(paths.AuditLogFile);
            await TrimIfNeededAsync(cancellationToken);
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Could not persist an audit event.");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<AuditEntry>> GetRecentAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(paths.AuditLogFile))
            {
                return [];
            }

            var entries = new List<AuditEntry>();
            foreach (var line in await File.ReadAllLinesAsync(paths.AuditLogFile, cancellationToken))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<AuditEntry>(line, ProtocolJson.Options);
                    if (entry is not null)
                    {
                        entries.Add(entry);
                    }
                }
                catch (JsonException)
                {
                    logger.LogWarning("Skipped a malformed audit event.");
                }
            }

            return entries
                .OrderByDescending(entry => entry.OccurredAt)
                .Take(Math.Clamp(limit, 1, MaxEntries))
                .ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task TrimIfNeededAsync(CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(paths.AuditLogFile, cancellationToken);
        if (lines.Length <= MaxEntries)
        {
            return;
        }

        var temporaryPath = paths.AuditLogFile + ".tmp";
        await File.WriteAllLinesAsync(temporaryPath, lines[^MaxEntries..], cancellationToken);
        RestrictFile(temporaryPath);
        File.Move(temporaryPath, paths.AuditLogFile, overwrite: true);
        RestrictFile(paths.AuditLogFile);
    }

    private void RestrictFile(string path)
    {
        if (paths.RestrictPermissions)
        {
            SecureFilePermissions.RestrictFile(path);
        }
    }
}

