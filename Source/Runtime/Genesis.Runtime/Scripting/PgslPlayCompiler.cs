using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Genesis.Runtime.Scripting
{
    /// <summary>
    /// Prototype PGSL-to-C# generator retained for Phase 3 performance work. It is deliberately not
    /// used by Play: the old per-event output could not bind all advertised events and allowed VM/AOT
    /// semantic drift. Studio and Player currently share the validated VM backend.
    /// </summary>
    public static class PgslPlayCompiler
    {
        private static readonly Regex EventNameRx = new(
            @"^(?<obj>.+)_(?<evt>Create|Update|Collision|Destroy|Draw)\.pgsl$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Recognise a script as one object's event: it sits in a folder with a sibling
        /// <c>&lt;FolderName&gt;.object.json</c>, and its filename is the event id.
        /// </summary>
        internal static bool TryObjectEventFile(string file, out string objectName, out string eventName)
        {
            objectName = null;
            eventName = null;

            string folder = Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(folder)) return false;

            string parent = Path.GetDirectoryName(folder);
            string folderName = Path.GetFileName(folder);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(folderName)) return false;
            if (!File.Exists(Path.Combine(parent, folderName + ".object.json"))) return false;

            string stem = Path.GetFileNameWithoutExtension(file);
            if (!ObjectEventCatalog.Exists(stem)) return false;

            objectName = folderName;

            // Keep the authored event id here. Several designer events normalize to the same runtime
            // hook, but each still needs a unique generated type/file or Roslyn sees the same source
            // path twice (Step + Alarm0 used to duplicate Player_Update_PgslBehavior).
            eventName = stem;
            return true;
        }

        public static IReadOnlyList<string> TranspileProject(string projectPath, out IReadOnlyList<string> errors)
        {
            var err = new List<string>();
            var outputs = new List<string>();
            errors = err;

            if (string.IsNullOrEmpty(projectPath) || !Directory.Exists(projectPath))
                return outputs;

            string outDir = Path.Combine(projectPath, "Build", "Intermediate", "GeneratedPGSL");
            Directory.CreateDirectory(outDir);

            // Clear previous generated files so renamed objects don't leave stale types.
            foreach (string old in Directory.EnumerateFiles(outDir, "*.cs"))
            {
                try { File.Delete(old); } catch { }
            }

            var groups = new Dictionary<string, List<(string Event, string Path)>>(StringComparer.OrdinalIgnoreCase);

            foreach (string dir in new[]
                     {
                         Path.Combine(projectPath, "Scripts"),
                         Path.Combine(projectPath, "Objects"),
                         // Studio projects keep every resource (including .pgsl) under Assets/.
                         Path.Combine(projectPath, "Assets"),
                     })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.EnumerateFiles(dir, "*.pgsl", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(file);

                    // T2 layout: an object's events live in a folder named after the object, one file
                    // per event ("Player/Step.pgsl"). The object is the FOLDER and the event is the
                    // FILE — not the old "<Object>_<Event>.pgsl" sibling convention. Without this the
                    // transpiler grouped every object's Create.pgsl under the stem "Create" and emitted
                    // duplicate class names, so a project with two scripted objects would not compile.
                    if (TryObjectEventFile(file, out string ownerObject, out string ownerEvent))
                    {
                        if (!groups.TryGetValue(ownerObject, out var ownerList))
                        {
                            ownerList = new List<(string, string)>();
                            groups[ownerObject] = ownerList;
                        }

                        ownerList.Add((ownerEvent, file));
                        continue;
                    }

                    Match m = EventNameRx.Match(name);
                    if (m.Success)
                    {
                        string obj = m.Groups["obj"].Value;
                        string evt = m.Groups["evt"].Value;
                        if (!groups.TryGetValue(obj, out var list))
                        {
                            list = new List<(string, string)>();
                            groups[obj] = list;
                        }
                        list.Add((evt, file));
                    }
                    else
                    {
                        // Single-file script → Update event under file stem.
                        string stem = Path.GetFileNameWithoutExtension(file);
                        if (!groups.TryGetValue(stem, out var list))
                        {
                            list = new List<(string, string)>();
                            groups[stem] = list;
                        }
                        list.Add(("Update", file));
                    }
                }
            }

            foreach (var kv in groups)
            {
                string objectName = kv.Key;
                // Prefer Update/Create when multiple; transpile each event into its own class file
                // (transpiler emits one method per call — one file per event avoids method merge complexity).
                foreach (var (evt, path) in kv.Value)
                {
                    try
                    {
                        string source = File.ReadAllText(path);

                        // Language-spec scripts declare `event Name { … }` blocks. The transpiler
                        // consumes one flat statement body per event, so split block-style files
                        // into per-event bodies; flat files stay a single Update body.
                        var eventBodies = SplitEventBlocks(source);
                        if (eventBodies.Count == 0)
                        {
                            // Event-style file whose bodies are all empty/comments: nothing to
                            // emit (fresh template scripts). Only flat statement files fall back
                            // to a single Update body.
                            if (EventBlockRx.IsMatch(source))
                                continue;
                            eventBodies[evt] = source;
                        }

                        foreach (var pair in eventBodies)
                        {
                            string authoredEventName = pair.Key;
                            string eventName = NormalizeEventName(authoredEventName);
                            PgslTranspileResult result = PgslToCSharpTranspiler.TranspileEvent(
                                objectName + "_" + authoredEventName, eventName, pair.Value);
                            if (!result.Success)
                            {
                                err.AddRange(result.Errors.Select(e => path + ": " + e));
                                continue;
                            }

                            string outPath = Path.Combine(outDir, result.ClassName + ".cs");
                            File.WriteAllText(outPath, result.CSharpSource, Encoding.UTF8);
                            outputs.Add(outPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        err.Add(path + ": " + ex.Message);
                    }
                }
            }

            return outputs;
        }

        private static readonly Regex EventBlockRx = new(
            @"\bevent\s+([A-Za-z_]\w*)\s*\{",
            RegexOptions.Compiled);

        internal static bool HasEventBlocks(string source) =>
            !string.IsNullOrEmpty(source) && EventBlockRx.IsMatch(source);

        /// <summary>Maps designer event names onto the transpiler's canonical set.</summary>
        private static string NormalizeEventName(string name)
        {
            if (string.Equals(name, "Step", StringComparison.OrdinalIgnoreCase)) return "Update";
            foreach (string known in new[] { "Create", "Update", "Collision", "Destroy", "Draw" })
            {
                if (string.Equals(name, known, StringComparison.OrdinalIgnoreCase))
                    return known;
            }

            return "Update";
        }

        /// <summary>
        /// Extracts `event Name { body }` blocks with brace matching (strings/comments naive but
        /// adequate for designer scripts). Empty result means the file has no event blocks.
        /// </summary>
        internal static Dictionary<string, string> SplitEventBlocks(string source)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(source))
                return result;

            foreach (Match match in EventBlockRx.Matches(source))
            {
                int depth = 1;
                int bodyStart = match.Index + match.Length;
                int i = bodyStart;
                while (i < source.Length && depth > 0)
                {
                    char c = source[i];
                    if (c == '{') depth++;
                    else if (c == '}') depth--;
                    else if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
                    {
                        while (i < source.Length && source[i] != '\n') i++;
                    }
                    else if (c == '"')
                    {
                        i++;
                        while (i < source.Length && source[i] != '"')
                        {
                            if (source[i] == '\\') i++;
                            i++;
                        }
                    }

                    i++;
                }

                if (depth == 0)
                {
                    string name = match.Groups[1].Value;
                    string body = source.Substring(bodyStart, i - bodyStart - 1);
                    // Ignore bodies that are only whitespace/comments — nothing to run.
                    string effective = Regex.Replace(body, @"//[^\n]*", string.Empty).Trim();
                    if (effective.Length > 0)
                        result[name] = body;
                }
            }

            return result;
        }
    }
}
