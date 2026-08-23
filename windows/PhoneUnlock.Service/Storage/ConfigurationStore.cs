using System.Text.Json;
using PhoneUnlock.Core.Protocol;
using PhoneUnlock.Service.Configuration;
using PhoneUnlock.Service.Models;

namespace PhoneUnlock.Service.Storage;

public sealed class ConfigurationStore
{
    private readonly ServicePaths paths;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ServiceConfiguration configuration;

    public ConfigurationStore(ServicePaths paths)
    {
        this.paths = paths;
        configuration = Load();
    }

    public async Task<ServiceConfiguration> GetAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return Clone(configuration);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ServiceConfiguration> UpdateAsync(
        Func<ServiceConfiguration, ServiceConfiguration> update,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var updated = update(Clone(configuration));
            await SaveAsync(updated, cancellationToken);
            configuration = updated;
            return Clone(updated);
        }
        finally
        {
            gate.Release();
        }
    }

    private ServiceConfiguration Load()
    {
        if (!File.Exists(paths.ConfigurationFile))
        {
            var created = new ServiceConfiguration();
            File.WriteAllText(paths.ConfigurationFile, ProtocolJson.Serialize(created));
            RestrictFile(paths.ConfigurationFile);
            return created;
        }

        var json = File.ReadAllText(paths.ConfigurationFile);
        return JsonSerializer.Deserialize<ServiceConfiguration>(json, ProtocolJson.Options)
            ?? throw new InvalidDataException("Service configuration is empty.");
    }

    private async Task SaveAsync(ServiceConfiguration value, CancellationToken cancellationToken)
    {
        var temporaryPath = paths.ConfigurationFile + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, ProtocolJson.Serialize(value), cancellationToken);
        RestrictFile(temporaryPath);
        File.Move(temporaryPath, paths.ConfigurationFile, overwrite: true);
        RestrictFile(paths.ConfigurationFile);
    }

    private static ServiceConfiguration Clone(ServiceConfiguration value) => value with
    {
        Phones = value.Phones.Select(phone => phone with { }).ToList()
    };

    private void RestrictFile(string path)
    {
        if (paths.RestrictPermissions)
        {
            SecureFilePermissions.RestrictFile(path);
        }
    }
}
