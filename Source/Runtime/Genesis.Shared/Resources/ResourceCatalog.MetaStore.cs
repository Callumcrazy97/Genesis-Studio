using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Genesis.Shared.Assets;

// What every .meta said (guid and authored name), kept between sessions in the project's
// .genesis/Cache. Opening a project read every .meta file on the first catalog build of each
// Studio session: thousands of small files, each opened (and scanned by antivirus) at a cost of
// seconds in all. The store answers for every .meta whose time and size are unchanged, so only
// the files edited since are read. The .meta stays the only authority: a changed file is always
// read again, and a missing or damaged store is simply rebuilt.
public sealed partial class ResourceCatalog
{
    private const string MetaStoreFile = "resource-identities.bin";
    private const int MetaStoreVersion = 1;
    private static readonly ConcurrentDictionary<string, bool> MetaStoresLoaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object MetaStoreGate = new();
    private static int _metaStoreChanges;
    // The .meta files the build in progress on this thread has met, so the store keeps only those.
    [ThreadStatic] private static HashSet<string> _metasSeen;

    private static string MetaStorePath(string projectRoot) => Path.Combine(projectRoot, ".genesis", "Cache", MetaStoreFile);

    // Only a project being worked on has a .genesis folder; an exported game (which has an index
    // instead) and a read-only install are never written to.
    private static bool HasMetaStoreFolder(string projectRoot) => Directory.Exists(Path.Combine(projectRoot, ".genesis"));

    private static void BeginMetaStore(string projectRoot)
    {
        _metasSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!MetaStoresLoaded.TryAdd(projectRoot, true) || !HasMetaStoreFolder(projectRoot)) return;
        try
        {
            string path = MetaStorePath(projectRoot);
            if (!File.Exists(path)) return;
            using var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
            if (reader.ReadInt32() != MetaStoreVersion) return;
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string relative = reader.ReadString();
                long ticks = reader.ReadInt64();
                long length = reader.ReadInt64();
                var guid = new Guid(reader.ReadBytes(16));
                string authored = reader.ReadBoolean() ? reader.ReadString() : null;
                MetaIdentities.TryAdd(Path.Combine(projectRoot, relative), new MetaIdentity(ticks, length, guid, authored));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or EndOfStreamException or ArgumentException or FormatException)
        {
            // A damaged store costs one slow build; the .meta files are the authority.
        }
    }

    private static void NoteMetaSeen(string metaPath) => _metasSeen?.Add(metaPath);

    private static void NoteMetaRead() => Interlocked.Increment(ref _metaStoreChanges);

    private static void EndMetaStore(string projectRoot)
    {
        HashSet<string> seen = _metasSeen;
        _metasSeen = null;
        if (seen == null || Interlocked.Exchange(ref _metaStoreChanges, 0) == 0 || !HasMetaStoreFolder(projectRoot)) return;
        var rows = new List<(string Relative, MetaIdentity Identity)>(seen.Count);
        foreach (string meta in seen)
            if (MetaIdentities.TryGetValue(meta, out MetaIdentity identity))
                rows.Add((Path.GetRelativePath(projectRoot, meta), identity));
        lock (MetaStoreGate)
        {
            string path = MetaStorePath(projectRoot);
            string temp = path + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var writer = new BinaryWriter(File.Create(temp), Encoding.UTF8))
                {
                    writer.Write(MetaStoreVersion);
                    writer.Write(rows.Count);
                    foreach ((string relative, MetaIdentity identity) in rows)
                    {
                        writer.Write(relative);
                        writer.Write(identity.Ticks);
                        writer.Write(identity.Length);
                        writer.Write(identity.Guid.ToByteArray());
                        writer.Write(identity.Authored != null);
                        if (identity.Authored != null) writer.Write(identity.Authored);
                    }
                }
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
