using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Starlight.Security;

public sealed record StoredSecret(string Key, string UserName, string Secret);

/// <summary>Keys and passwords (the Brave key, web logins). Never in settings.json or the logs.</summary>
public interface ISecretStore
{
    StoredSecret? Get(string key);
    void Set(string key, string userName, string secret);
    bool Delete(string key);
    /// <summary>Keys that start with the prefix.</summary>
    IReadOnlyList<string> List(string prefix);
}

/// <summary>Windows Credential Manager (generic credentials named "Starlight/...").</summary>
public sealed partial class CredentialManagerStore : ISecretStore
{
    private const string Prefix = "Starlight/";
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    public StoredSecret? Get(string key)
    {
        if (!CredReadW(Prefix + key, CRED_TYPE_GENERIC, 0, out var ptr))
        {
            return Marshal.GetLastWin32Error() == ERROR_NOT_FOUND ? null : throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            var secret = cred.CredentialBlobSize == 0 ? "" : Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
            return new StoredSecret(key, cred.UserName ?? "", secret);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public void Set(string key, string userName, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try
        {
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = Prefix + key,
                UserName = userName,
                CredentialBlob = handle.AddrOfPinnedObject(),
                CredentialBlobSize = (uint)blob.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
            };
            if (!CredWriteW(ref cred, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            handle.Free();
            Array.Clear(blob);
        }
    }

    public bool Delete(string key) => CredDeleteW(Prefix + key, CRED_TYPE_GENERIC, 0);

    public IReadOnlyList<string> List(string prefix)
    {
        if (!CredEnumerateW(Prefix + prefix + "*", 0, out var count, out var list))
        {
            return [];
        }
        try
        {
            var keys = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(Marshal.ReadIntPtr(list, i * IntPtr.Size));
                if (cred.TargetName?.StartsWith(Prefix, StringComparison.Ordinal) == true)
                {
                    keys.Add(cred.TargetName[Prefix.Length..]);
                }
            }
            return keys;
        }
        finally
        {
            CredFree(list);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public int Type;
        public string? TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public int Persist;
        public uint AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, int type, int flags, out nint credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, int type, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredEnumerateW(string filter, int flags, out int count, out nint credentials);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);
}

/// <summary>For tests and the eval: secrets in memory only.</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, StoredSecret> _items = new();

    public StoredSecret? Get(string key) => _items.GetValueOrDefault(key);
    public void Set(string key, string userName, string secret) => _items[key] = new StoredSecret(key, userName, secret);
    public bool Delete(string key) => _items.TryRemove(key, out _);
    public IReadOnlyList<string> List(string prefix) => _items.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order().ToList();
}
