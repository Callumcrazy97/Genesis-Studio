#nullable enable
using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Genesis.Runtime.Project;

/// <summary>Bounded UTF-8 text operations shared by synchronous commands and native jobs.</summary>
public static class ProjectTextFiles
{
    public const int MaximumTextBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static string ResolvePath(string? project, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("File path is empty.");
        if (Path.IsPathFullyQualified(path)) return Path.GetFullPath(path);
        if (Path.IsPathRooted(path)) throw new ArgumentException("Use a fully qualified absolute path or a game-relative path.");
        string directory = ProjectNumberSave.GetWritableDirectory(project!);
        string resolved = Path.GetFullPath(path, directory);
        if (!resolved.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Relative file paths must remain inside the game's writable directory.");
        return resolved;
    }

    public static string Read(string absolutePath, CancellationToken cancellation = default)
    {
        using FileStream stream = new(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumTextBytes) throw new IOException("Text file exceeds the 4 MiB limit.");
        byte[] bytes = new byte[(int)stream.Length];
        int read = 0;
        while (read < bytes.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            int count = stream.Read(bytes, read, Math.Min(65536, bytes.Length - read));
            if (count == 0) throw new EndOfStreamException("Text file changed during reading.");
            read += count;
        }
        if (stream.ReadByte() != -1) throw new IOException("Text file grew during reading; retry the operation.");
        cancellation.ThrowIfCancellationRequested();
        int offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        return Utf8.GetString(bytes, offset, bytes.Length - offset);
    }

    public static void Write(string absolutePath, string text, CancellationToken cancellation = default)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        if (Utf8.GetByteCount(text) > MaximumTextBytes) throw new IOException("Text exceeds the 4 MiB limit.");
        cancellation.ThrowIfCancellationRequested();
        string directory = Path.GetDirectoryName(absolutePath) ?? throw new ArgumentException("File has no parent directory.");
        Directory.CreateDirectory(directory);
        string pending = Path.Combine(directory, ".genesis-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            byte[] bytes = Utf8.GetBytes(text);
            using (FileStream stream = new(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                for (int offset = 0; offset < bytes.Length; offset += 65536)
                {
                    cancellation.ThrowIfCancellationRequested();
                    stream.Write(bytes, offset, Math.Min(65536, bytes.Length - offset));
                }
                stream.Flush(flushToDisk: true);
            }
            // Cancellation before commit leaves the old file intact. After commit the write succeeded.
            cancellation.ThrowIfCancellationRequested();
            File.Move(pending, absolutePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(pending)) File.Delete(pending); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
        }
    }

    public static bool IsFileError(Exception error) => error is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or InvalidOperationException or System.Security.SecurityException;
}
