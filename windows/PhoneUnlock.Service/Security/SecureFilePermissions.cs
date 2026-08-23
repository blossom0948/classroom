using System.Security.AccessControl;
using System.Security.Principal;

namespace PhoneUnlock.Service.Configuration;

public static class SecureFilePermissions
{
    public static void EnsureServiceDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(CreateDirectoryRule(WellKnownSidType.LocalSystemSid));
        security.AddAccessRule(CreateDirectoryRule(WellKnownSidType.BuiltinAdministratorsSid));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    public static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(CreateFileRule(WellKnownSidType.LocalSystemSid));
        security.AddAccessRule(CreateFileRule(WellKnownSidType.BuiltinAdministratorsSid));
        new FileInfo(path).SetAccessControl(security);
    }

    private static FileSystemAccessRule CreateDirectoryRule(WellKnownSidType sidType) =>
        new(
            new SecurityIdentifier(sidType, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow);

    private static FileSystemAccessRule CreateFileRule(WellKnownSidType sidType) =>
        new(
            new SecurityIdentifier(sidType, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow);
}
