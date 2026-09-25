using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// Reads and writes an object's event scripts.
/// </summary>
/// <remarks>
/// Event code lives in one file per event inside a folder named after the object, beside the object
/// document:
/// <code>
/// Assets/Objects/Player.object.json
/// Assets/Objects/Player/Create.pgsl
/// Assets/Objects/Player/Step.pgsl
/// Assets/Objects/Player/DrawGui.pgsl
/// </code>
/// GMS2-style, and chosen over inlining the code in the JSON so event scripts stay diffable in
/// version control.
///
/// The critical property is that an event is resolved <b>relative to its object's folder</b>, never
/// through the global script-name registry. That is what makes the NEXT-044/046 failure mode
/// impossible: there is no naming convention to get wrong and no global name to collide with. The
/// old scheme exported <c>Player_Step.pgsl</c> as a sibling and re-bound it by name, which is how
/// Create ended up running every frame and Draw never registered at all.
///
/// PGSL Scripts remain an entirely separate resource — a library of reusable functions, resolved by
/// name when called. Nothing here touches them.
/// </remarks>
public static class ObjectEventStore
{
    /// <summary>Folder holding an object's event scripts, derived from the document path.</summary>
    public static string FolderFor(string objectDocumentPath)
    {
        if (string.IsNullOrWhiteSpace(objectDocumentPath)) return null;

        string directory = Path.GetDirectoryName(objectDocumentPath);
        string stem = StemFor(objectDocumentPath);
        return directory is null || stem is null ? null : Path.Combine(directory, stem);
    }

    /// <summary>Private storage stem used to locate the event directory; never a public resource name.</summary>
    public static string StemFor(string objectDocumentPath)
    {
        if (string.IsNullOrWhiteSpace(objectDocumentPath)) return null;

        string name = Path.GetFileName(objectDocumentPath);
        const string suffix = ".object.json";
        return name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^suffix.Length]
            : Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>Absolute path of one event's script file.</summary>
    public static string PathFor(string objectDocumentPath, string eventId)
    {
        string folder = FolderFor(objectDocumentPath);
        if (folder is null || !ObjectEventCatalog.Exists(eventId)) return null;
        return Path.Combine(folder, eventId + ".pgsl");
    }

    /// <summary>
    /// Every event that has a non-empty script on disk, keyed by event id. The folder is the source
    /// of truth: an event exists because its file does.
    /// </summary>
    public static Dictionary<string, string> Load(string objectDocumentPath)
    {
        Dictionary<string, string> events = new(StringComparer.OrdinalIgnoreCase);
        string folder = FolderFor(objectDocumentPath);
        if (folder is null || !Directory.Exists(folder)) return events;

        foreach (string file in Directory.EnumerateFiles(folder, "*.pgsl"))
        {
            string id = Path.GetFileNameWithoutExtension(file);
            if (!ObjectEventCatalog.Exists(id)) continue;   // stray file, not an event we know

            try
            {
                string body = File.ReadAllText(file);
                if (!string.IsNullOrWhiteSpace(body)) events[id] = body;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A single unreadable event must not stop the object loading; the others still work
                // and the editor will show the event as empty rather than refusing to open.
            }
        }

        return events;
    }

    /// <summary>
    /// Write the event set, creating the folder and deleting files for events that are now empty.
    /// </summary>
    /// <returns>How many event files exist afterwards.</returns>
    public static int Save(string objectDocumentPath, IReadOnlyDictionary<string, string> events)
    {
        string folder = FolderFor(objectDocumentPath);
        if (folder is null) return 0;

        bool anyContent = events is not null
            && events.Any(pair => ObjectEventCatalog.Exists(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value));

        // Don't leave an empty folder littering the resource tree for an object with no events.
        if (!anyContent)
        {
            DeleteAllEvents(folder);
            return 0;
        }

        Directory.CreateDirectory(folder);
        string projectRoot = ResourceNames.FindProjectRoot(objectDocumentPath);
        int written = 0;

        foreach (ObjectEventDefinition definition in ObjectEventCatalog.All)
        {
            string file = Path.Combine(folder, definition.FileName);
            bool hasBody = events.TryGetValue(definition.Id, out string body) && !string.IsNullOrWhiteSpace(body);

            try
            {
                if (hasBody)
                {
                    string code = string.IsNullOrEmpty(projectRoot) ? body
                        : Genesis.Shared.Assets.ResourceReferenceRewriter.Normalize(projectRoot, file, body);
                    File.WriteAllText(file, code);
                    written++;
                }
                else if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Surfacing this needs UI context the store does not have. The editor re-reads after
                // save, so a failed write shows up as the event reverting rather than as silent loss.
            }
        }

        return written;
    }

    private static void DeleteAllEvents(string folder)
    {
        if (!Directory.Exists(folder)) return;

        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*.pgsl"))
            {
                if (ObjectEventCatalog.Exists(Path.GetFileNameWithoutExtension(file))) File.Delete(file);
            }

            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Leaving a stale folder behind is untidy but harmless — Load ignores unknown files.
        }
    }
}
