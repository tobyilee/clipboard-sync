using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ClipSync.App;

/// passphrase를 Credential Manager(generic credential, DPAPI 보호)에 저장한다 (spec 8, D-46).
static class CredentialStore
{
    public const string ProductionTarget = "ClipSync/passphrase";
    /// 자체 테스트(`--selftest-credential`)는 실제 항목을 건드리지 않도록 다른 대상을 쓴다 (Mac selftest 사고 재발 방지).
    public static string Target = ProductionTarget;

    public static void Save(string passphrase)
    {
        var blob = Encoding.UTF8.GetBytes(passphrase);
        var p = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, p, blob.Length);
            var c = new Native.CREDENTIAL
            {
                Type = Native.CRED_TYPE_GENERIC,
                TargetName = Target,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = p,
                Persist = Native.CRED_PERSIST_LOCAL_MACHINE,
                UserName = Environment.UserName,
            };
            if (!Native.CredWrite(ref c, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.Copy(new byte[blob.Length], 0, p, blob.Length);
            Marshal.FreeHGlobal(p);
        }
    }

    public static string? Load()
    {
        if (!Native.CredRead(Target, Native.CRED_TYPE_GENERIC, 0, out var p))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == Native.ERROR_NOT_FOUND) return null;
            throw new Win32Exception(err);
        }
        try
        {
            var c = Marshal.PtrToStructure<Native.CREDENTIAL>(p);
            var blob = new byte[c.CredentialBlobSize];
            if (blob.Length > 0) Marshal.Copy(c.CredentialBlob, blob, 0, blob.Length);
            return Encoding.UTF8.GetString(blob);
        }
        finally { Native.CredFree(p); }
    }

    public static void Delete()
    {
        if (!Native.CredDelete(Target, Native.CRED_TYPE_GENERIC, 0))
        {
            var err = Marshal.GetLastWin32Error();
            if (err != Native.ERROR_NOT_FOUND) throw new Win32Exception(err);
        }
    }
}
