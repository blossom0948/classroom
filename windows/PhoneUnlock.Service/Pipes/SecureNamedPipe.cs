using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PhoneUnlock.Service.Pipes;

public static class SecureNamedPipe
{
    public static NamedPipeServerStream Create(string pipeName, string? additionalUserSid = null)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var owner = Environment.UserInteractive
            ? WindowsIdentity.GetCurrent().User
            : new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (owner is not null)
        {
            security.SetOwner(owner);
        }
        security.AddAccessRule(CreateRule(WellKnownSidType.LocalSystemSid));
        security.AddAccessRule(CreateRule(WellKnownSidType.BuiltinAdministratorsSid));
        if (Environment.UserInteractive && WindowsIdentity.GetCurrent().User is { } currentUser)
        {
            security.AddAccessRule(new PipeAccessRule(
                currentUser,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));
        }
        SecurityIdentifier? configuredUser = null;
        if (!string.IsNullOrWhiteSpace(additionalUserSid))
        {
            try
            {
                configuredUser = new SecurityIdentifier(additionalUserSid);
            }
            catch (ArgumentException)
            {
                configuredUser = null;
            }
        }
        if (configuredUser is not null)
        {
            security.AddAccessRule(new PipeAccessRule(
                configuredUser,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));
        }

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            8192,
            8192,
            security);
    }

    private static PipeAccessRule CreateRule(WellKnownSidType sidType) =>
        new(
            new SecurityIdentifier(sidType, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow);
}
