using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Genesis.Shared.Assets
{
    // The quoted strings each text asset holds (its possible references), kept between sessions in
    // the project's .genesis/Cache. "Reading how the project files refer to each other" read and
    // parsed every text asset in the project each time a project opened (8-9 s for a large one).
    // The store answers for every file whose time and size are unchanged; only edited files are read.
    // It keeps the strings, not what they resolved to, so a renamed resource still resolves afresh.
    public sealed partial class AssetDependencyGraph
    {
        private const string LiteralStoreFile = "reference-literals.bin";
        private const int LiteralStoreVersion = 1;
        private sealed record LiteralEntry(long Ticks, long Length, string[] Literals);
        private static readonly ConcurrentDictionary<string, LiteralEntry> Literals = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, bool> LiteralStoresLoaded = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object LiteralStoreGate = new();
        private int _literalChanges;

        private string LiteralStorePath => Path.Combine(ProjectRoot, ".genesis", "Cache", LiteralStoreFile);

        private bool HasLiteralStoreFolder => Directory.Exists(Path.Combine(ProjectRoot, ".genesis"));

        private void LoadLiteralStore()
        {
            if (!LiteralStoresLoaded.TryAdd(ProjectRoot, true) || !HasLiteralStoreFolder) return;
            try
            {
                if (!File.Exists(LiteralStorePath)) return;
                using var reader = new BinaryReader(File.OpenRead(LiteralStorePath), Encoding.UTF8);
                if (reader.ReadInt32() != LiteralStoreVersion) return;
                int files = reader.ReadInt32();
                for (int i = 0; i < files; i++)
                {
                    string relative = reader.ReadString();
                    long ticks = reader.ReadInt64();
                    long length = reader.ReadInt64();
                    int count = reader.ReadInt32();
                    var literals = new string[count];
                    for (int j = 0; j < count; j++) literals[j] = reader.ReadString();
                    Literals.TryAdd(Path.Combine(ProjectRoot, relative), new LiteralEntry(ticks, length, literals));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or EndOfStreamException or ArgumentException)
            {
                // A damaged store costs one slow read of the project; the files are the authority.
            }
        }

        /// <summary>The possible references a file holds: from the store while it is unchanged, else read now.</summary>
        private IEnumerable<string> ReadReferenceLiteralsCached(string path)
        {
            if (!TextExtensions.Contains(Path.GetExtension(path))) return Array.Empty<string>();
            FileInfo info = new(path);
            if (!info.Exists) return Array.Empty<string>();
            long ticks = info.LastWriteTimeUtc.Ticks, length = info.Length;
            if (Literals.TryGetValue(path, out LiteralEntry entry) && entry.Ticks == ticks && entry.Length == length)
                return entry.Literals;
            string[] literals = ReadReferenceLiterals(path).Distinct(StringComparer.Ordinal).ToArray();
            // A file written in the last few seconds could change again within one tick of the file
            // system's clock, keeping its time: it is read again next time rather than remembered.
            if (DateTime.UtcNow.Ticks - ticks > TimeSpan.TicksPerSecond * 3)
            {
                Literals[path] = new LiteralEntry(ticks, length, literals);
                _literalChanges++;
            }
            return literals;
        }

        private void SaveLiteralStore()
        {
            if (_literalChanges == 0 || !HasLiteralStoreFolder) return;
            _literalChanges = 0;
            var rows = new List<(string Relative, LiteralEntry Entry)>(_resources.Count);
            foreach (string resource in _resources)
                if (Literals.TryGetValue(resource, out LiteralEntry entry))
                    rows.Add((Path.GetRelativePath(ProjectRoot, resource), entry));
            lock (LiteralStoreGate)
            {
                string path = LiteralStorePath;
                string temp = path + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var writer = new BinaryWriter(File.Create(temp), Encoding.UTF8))
                    {
                        writer.Write(LiteralStoreVersion);
                        writer.Write(rows.Count);
                        foreach ((string relative, LiteralEntry entry) in rows)
                        {
                            writer.Write(relative);
                            writer.Write(entry.Ticks);
                            writer.Write(entry.Length);
                            writer.Write(entry.Literals.Length);
                            foreach (string literal in entry.Literals) writer.Write(literal);
                        }
                    }
                    File.Move(temp, path, true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    try { File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                }
            }
        }
    }
}
