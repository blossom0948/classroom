using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhoneUnlock.Core.Protocol;
using PhoneUnlock.Service.Configuration;
using PhoneUnlock.Service.Models;

namespace PhoneUnlock.Service.Storage;

public sealed class WindowsCredentialStore
{
    public void Save(StoredWindowsCredential credential)
    {
        var json = JsonSerializer.Serialize(credential, ProtocolJson.Options);
        var blob = Encoding.UTF8.GetBytes(json);
        if (blob.Length > 5120)
        {
            CryptographicOperations.ZeroMemory(blob);
            throw new InvalidOperationException("Credential payload is too large.");
        }

        var blobPointer = Marshal.AllocCoTaskMem(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPointer, blob.Length);
            var native = new NativeCredential
            {
                Type = CredentialType.Generic,
                TargetName = ServiceConstants.CredentialTarget,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPointer,
                Persist = CredentialPersist.LocalMachine,
                UserName = credential.QualifiedUsername,
                Comment = "Phone Unlock protected Windows logon credential"
            };

            if (!CredWriteW(ref native, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows Credential Manager rejected the credential.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
            if (blobPointer != IntPtr.Zero)
            {
                unsafe
                {
                    NativeMemory.Clear(blobPointer.ToPointer(), (nuint)blob.Length);
                }
                Marshal.FreeCoTaskMem(blobPointer);
            }
        }
    }

    public StoredWindowsCredential? Read()
    {
        if (!CredReadW(ServiceConstants.CredentialTarget, CredentialType.Generic, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168)
            {
                return null;
            }

            throw new Win32Exception(error, "Windows Credential Manager could not read the credential.");
        }

        byte[]? blob = null;
        try
        {
            var native = Marshal.PtrToStructure<NativeCredential>(pointer);
            blob = new byte[native.CredentialBlobSize];
            Marshal.Copy(native.CredentialBlob, blob, 0, blob.Length);
            return JsonSerializer.Deserialize<StoredWindowsCredential>(blob, ProtocolJson.Options)
                ?? throw new InvalidDataException("Stored credential is empty.");
        }
        finally
        {
            if (blob is not null)
            {
                CryptographicOperations.ZeroMemory(blob);
            }
            CredFree(pointer);
        }
    }

    public bool Exists() => Read() is not null;

    public void Delete()
    {
        if (!CredDeleteW(ServiceConstants.CredentialTarget, CredentialType.Generic, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1168)
            {
                throw new Win32Exception(error, "Windows Credential Manager could not delete the credential.");
            }
        }
    }

    private enum CredentialType : uint
    {
        Generic = 1
    }

    private enum CredentialPersist : uint
    {
        LocalMachine = 2
    }

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
