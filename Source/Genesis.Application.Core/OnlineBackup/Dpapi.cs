using System.Runtime.InteropServices;
using System.Text;

namespace Genesis.Application.Core.Backup;

/// <summary>
/// Minimal DPAPI wrapper (CryptProtectData / CryptUnprotectData) so the access token is
/// encrypted to the current Windows user account without pulling in a NuGet package.
/// </summary>
internal static class Dpapi
{
    private const int CryptprotectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GenesisStudio/github/token");

    public static byte[] Protect(byte[] plaintext)
    {
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var inputHandle = GCHandle.Alloc(plaintext, GCHandleType.Pinned);
        try
        {
            var entropyBlob = new DataBlob { cbData = Entropy.Length, pbData = entropyHandle.AddrOfPinnedObject() };
            var entropyPtr = GCHandle.Alloc(entropyBlob, GCHandleType.Pinned);
            try
            {
                var input = new DataBlob { cbData = plaintext.Length, pbData = inputHandle.AddrOfPinnedObject() };
                if (!CryptProtectData(ref input, "GitHub AutoSync", entropyPtr.AddrOfPinnedObject(),
                        IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out var output))
                {
                    throw new InvalidOperationException("CryptProtectData failed: " + Marshal.GetLastWin32Error());
                }
                return CopyAndFree(output);
            }
            finally { entropyPtr.Free(); }
        }
        finally
        {
            inputHandle.Free();
            entropyHandle.Free();
        }
    }

    public static byte[] Unprotect(byte[] ciphertext)
    {
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var inputHandle = GCHandle.Alloc(ciphertext, GCHandleType.Pinned);
        try
        {
            var entropyBlob = new DataBlob { cbData = Entropy.Length, pbData = entropyHandle.AddrOfPinnedObject() };
            var entropyPtr = GCHandle.Alloc(entropyBlob, GCHandleType.Pinned);
            try
            {
                var input = new DataBlob { cbData = ciphertext.Length, pbData = inputHandle.AddrOfPinnedObject() };
                if (!CryptUnprotectData(ref input, IntPtr.Zero, entropyPtr.AddrOfPinnedObject(),
                        IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out var output))
                {
                    throw new InvalidOperationException("CryptUnprotectData failed: " + Marshal.GetLastWin32Error());
                }
                return CopyAndFree(output);
            }
            finally { entropyPtr.Free(); }
        }
        finally
        {
            inputHandle.Free();
            entropyHandle.Free();
        }
    }

    private static byte[] CopyAndFree(DataBlob blob)
    {
        try
        {
            var result = new byte[blob.cbData];
            Marshal.Copy(blob.pbData, result, 0, blob.cbData);
            return result;
        }
        finally
        {
            if (blob.pbData != IntPtr.Zero) LocalFree(blob.pbData);
        }
    }
}

/// <summary>Stores the GitHub access token, encrypted at rest with DPAPI.</summary>
public static class CredentialStore
{
    private static readonly object Gate = new();

    public static string? Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(AppPaths.CredentialFile)) return null;
                var bytes = File.ReadAllBytes(AppPaths.CredentialFile);
                if (bytes.Length == 0) return null;
                var plain = Dpapi.Unprotect(bytes);
                var token = Encoding.UTF8.GetString(plain).Trim();
                Array.Clear(plain);
                return string.IsNullOrEmpty(token) ? null : token;
            }
            catch (Exception ex)
            {
                Logger.Warn("Stored credential could not be read (it may have been created by another Windows user). " + ex.Message);
                return null;
            }
        }
    }

    public static void Save(string token)
    {
        lock (Gate)
        {
            var bytes = Encoding.UTF8.GetBytes(token.Trim());
            var protectedBytes = Dpapi.Protect(bytes);
            Array.Clear(bytes);
            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.CredentialFile)!);
            File.WriteAllBytes(AppPaths.CredentialFile, protectedBytes);
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            try { if (File.Exists(AppPaths.CredentialFile)) File.Delete(AppPaths.CredentialFile); }
            catch (Exception ex) { Logger.Warn("Could not remove stored credential: " + ex.Message); }
        }
    }
}
