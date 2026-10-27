using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace PDM.Core.Security;

/// <summary>Windows DPAPI (current user scope): data can only be decrypted by the same Windows user on this machine.</summary>
public static class Dpapi
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PDM.cookies.v1");

    public static string Protect(string plain)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        try { return Convert.ToBase64String(Transform(data, protect: true)); }
        finally { CryptographicZero(data); }
    }

    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return null;
        try
        {
            var bytes = Transform(Convert.FromBase64String(protectedBase64), protect: false);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { CryptographicZero(bytes); }
        }
        catch
        {
            return null;
        }
    }

    private static void CryptographicZero(byte[] b) => Array.Clear(b);

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inBlob = new DATA_BLOB();
        var entropyBlob = new DATA_BLOB();
        var outBlob = new DATA_BLOB();
        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            inBlob.cbData = input.Length;
            inBlob.pbData = inHandle.AddrOfPinnedObject();
            entropyBlob.cbData = Entropy.Length;
            entropyBlob.pbData = entHandle.AddrOfPinnedObject();
            const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
            var ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            inHandle.Free();
            entHandle.Free();
            if (outBlob.pbData != IntPtr.Zero)
            {
                // Clear the decrypted copy before giving the buffer back.
                for (var i = 0; i < outBlob.cbData; i++) Marshal.WriteByte(outBlob.pbData, i, 0);
                LocalFree(outBlob.pbData);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
