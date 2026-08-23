using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using PhoneUnlock.Service.Models;

namespace PhoneUnlock.Service.Security;

public sealed class WindowsAccountValidator
{
    public StoredWindowsCredential Validate(string qualifiedUsername, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedUsername);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var (domain, username) = SplitAccountName(qualifiedUsername.Trim());
        if (!LogonUserW(
                username,
                domain,
                password,
                Logon32LogonInteractive,
                Logon32ProviderDefault,
                out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected the supplied account credential.");
        }

        token.Dispose();
        var sid = ((SecurityIdentifier)new NTAccount(qualifiedUsername.Trim()).Translate(typeof(SecurityIdentifier))).Value;
        return new StoredWindowsCredential(
            sid,
            qualifiedUsername.Trim(),
            domain,
            username,
            password);
    }

    private static (string Domain, string Username) SplitAccountName(string qualifiedUsername)
    {
        var separator = qualifiedUsername.IndexOf('\\');
        if (separator > 0 && separator < qualifiedUsername.Length - 1)
        {
            var domain = qualifiedUsername[..separator];
            if (domain == ".")
            {
                domain = Environment.MachineName;
            }

            return (domain, qualifiedUsername[(separator + 1)..]);
        }

        if (qualifiedUsername.Contains('@', StringComparison.Ordinal))
        {
            return ("MicrosoftAccount", qualifiedUsername);
        }

        return (Environment.MachineName, qualifiedUsername);
    }

    private const int Logon32LogonInteractive = 2;
    private const int Logon32ProviderDefault = 0;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUserW(
        string username,
        string domain,
        string password,
        int logonType,
        int logonProvider,
        out SafeAccessTokenHandle token);
}
