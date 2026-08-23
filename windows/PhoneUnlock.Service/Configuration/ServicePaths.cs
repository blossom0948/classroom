namespace PhoneUnlock.Service.Configuration;

public sealed record ServicePaths(string DataDirectory, bool RestrictPermissions = true)
{
    public string ConfigurationFile => Path.Combine(DataDirectory, "service-config.json");
    public string CertificateFile => Path.Combine(DataDirectory, "phone-unlock.pfx");

    public static ServicePaths Resolve(IConfiguration configuration)
    {
        var configured = configuration["data-dir"] ?? Environment.GetEnvironmentVariable("PHONE_UNLOCK_DATA_DIR");
        var directory = !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : Environment.UserInteractive
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneUnlock", "ServiceDev")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PhoneUnlock");

        var restrictPermissions = !Environment.UserInteractive;
        if (restrictPermissions)
        {
            SecureFilePermissions.EnsureServiceDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory);
        }
        return new ServicePaths(directory, restrictPermissions);
    }
}
