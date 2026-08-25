using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PhoneUnlock.Service.Storage;

public sealed class WindowsSecretStore
{
    public string? Read(string target)
    {
        if (!CredReadW(target, CredentialType.Generic, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error, "Windows Credential Manager could not read the secret.");
        }

        byte[]? blob = null;
        try
        {
            var native = Marshal.PtrToStructure<NativeCredential>(pointer);
            blob = new byte[native.CredentialBlobSize];
            Marshal.Copy(native.CredentialBlob, blob, 0, blob.Length);
            return Encoding.UTF8.GetString(blob);
        }
        finally
        {
            if (blob is not null) CryptographicOperations.ZeroMemory(blob);
            CredFree(pointer);
        }
    }

    public void Save(string target, string value)
    {
        var blob = Encoding.UTF8.GetBytes(value);
        var pointer = Marshal.AllocCoTaskMem(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, pointer, blob.Length);
            var native = new NativeCredential
            {
                Type = CredentialType.Generic,
                TargetName = target,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = pointer,
                Persist = CredentialPersist.LocalMachine,
                UserName = Environment.UserName,
                Comment = "Phone Unlock presence sensor token"
            };
            if (!CredWriteW(ref native, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows Credential Manager rejected the secret.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    public void Delete(string target)
    {
        if (!CredDeleteW(target, CredentialType.Generic, 0)
            && Marshal.GetLastWin32Error() != 1168)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows Credential Manager could not delete the secret.");
        }
    }

    private enum CredentialType : uint { Generic = 1 }
    private enum CredentialPersist : uint { LocalMachine = 2 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public CredentialType Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public CredentialPersist Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, CredentialType type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, CredentialType type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
