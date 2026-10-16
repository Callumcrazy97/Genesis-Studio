using System.Security.Cryptography;
using System.Text;

namespace Genesis.Application.Core.Backup;

/// <summary>
/// Git object hashing. Files are hashed exactly as stored on disk -- no line-ending translation --
/// so a file round-trips byte for byte between devices.
/// </summary>
public static class GitBlob
{
    public static async Task<string> ComputeShaAsync(string path, long length, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes($"blob {length}\0"));

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            128 * 1024, useAsync: true);

        var buffer = new byte[128 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string ComputeSha(ReadOnlySpan<byte> content)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes($"blob {content.Length}\0"));
        hash.AppendData(content);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
