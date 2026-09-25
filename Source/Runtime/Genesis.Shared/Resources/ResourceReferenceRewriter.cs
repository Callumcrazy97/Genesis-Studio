using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Genesis.Shared.Assets;

/// <summary>
/// Converts authoring references at load/save/migration boundaries, never in the draw loop.
/// Does not rewrite media payload locations, arbitrary dialogue, comments, or object event filenames.
/// The same typed argument table is used for PGSL and ordinary C# string literals.
/// </summary>
public static class ResourceReferenceRewriter
{
    public static string Normalize(string projectRoot, string documentPath, string text)
    {
        ResourceCatalog catalog = ResourceCatalog.For(projectRoot);
        string Map(string value, ResourceType type)
        {
            NamedResource entry = catalog.Find(value, type);
            if (entry is null && ResourceCatalog.LooksLikeStorageReference(value))
            {
                string local = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(documentPath) ?? projectRoot, value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
                if (ResourceCatalog.IsInside(local, projectRoot)) entry = catalog.Find(local, type);
            }
            return entry?.Name ?? value;
        }
        return RewriteDocument(documentPath, text, Map);
    }

    public static string RewriteDocument(string documentPath, string text, Func<string, ResourceType, string> map)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        if (documentPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return RewriteMarkdown(text, map);
        if (documentPath.EndsWith(".pgsl", StringComparison.OrdinalIgnoreCase) || documentPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return RewriteCode(text, map, rewriteScriptCalls: documentPath.EndsWith(".pgsl", StringComparison.OrdinalIgnoreCase));
        if (!documentPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !documentPath.EndsWith(".pathing", StringComparison.OrdinalIgnoreCase)
            && !documentPath.EndsWith(".genesisproj", StringComparison.OrdinalIgnoreCase)) return text;
        JToken document;
        using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None }) document = JToken.ReadFrom(reader);
        bool changed = RewriteJson(document, ResourceCatalog.TypeOf(documentPath), map);
        return changed ? document.ToString(Formatting.Indented) : text;
    }

    public static bool RewriteJson(JToken root, ResourceType ownerType, Func<string, ResourceType, string> map)
    {
        bool changed = false;
        foreach (JValue value in Values(root).Where(v => v.Type == JTokenType.String).ToArray())
        {
            string before = (string)value ?? string.Empty;
            if (before.Length == 0) continue;
            string key = (value.Parent as JProperty)?.Name ?? (value.Parent?.Parent as JProperty)?.Name ?? string.Empty;
            ResourceType expected = PropertyType(key, value.Path, value, ownerType);
            bool eventProgram = value.Parent?.Parent is JObject eventMap
                && eventMap.Parent is JProperty eventProperty && eventProperty.Name.Equals("events", StringComparison.OrdinalIgnoreCase);
            if (eventProgram || key.Equals("code", StringComparison.OrdinalIgnoreCase) || key.Equals("scriptBody", StringComparison.OrdinalIgnoreCase))
            {
                string code = RewriteCode(before, map, rewriteScriptCalls: true);
                if (code != before) { value.Value = code; changed = true; }
                continue;
            }
            if (expected == ResourceType.Unknown && IsProse(key)) continue;
            if (expected == ResourceType.Unknown && !IsResourceProperty(key) && !ResourceCatalog.LooksLikeStorageReference(before)) continue;
            string after = map(before, expected);
            if (after == before) continue;
            value.Value = after; changed = true;
        }
        return changed;
    }

    private static IEnumerable<JValue> Values(JToken token)
    {
        if (token is JValue value) { yield return value; yield break; }
        foreach (JToken child in token.Children()) foreach (JValue descendant in Values(child)) yield return descendant;
    }

    private static bool IsProse(string key) => new[] { "name", "displayName", "text", "label", "description", "notes", "tooltip", "message", "comment", "sourceCode" }.Contains(key, StringComparer.OrdinalIgnoreCase);

    private static bool IsResourceProperty(string key) => key.Replace("_", "").ToLowerInvariant() is
        "asset" or "resource" or "resourceasset" or "resourcename" or "previewasset" or "previewtargetasset" or "entity" or "entities";

    public static ResourceType PropertyType(string key, string path = "", JToken value = null, ResourceType ownerType = ResourceType.Unknown)
    {
        string name = key.Replace("_", "").ToLowerInvariant();
        if (name is "sprite" or "spriteindex" or "spriteasset" or "image" or "imageasset" or "imagepath" or "spritepath"
            or "tileset" or "texture" or "texturepath" or "textureasset" or "albedotexture" or "normaltexture" or "backdropsprite"
            or "binding" or "iconimage" or "projecticon" or "backgroundsprite" or "normalmap" or "emissivetexture") return ResourceType.Image;
        if (name is "model" or "modelasset" or "meshsurfaceasset" or "meshparticleasset") return ResourceType.Model;
        if (name is "object" or "objectname" or "objectindex" or "targetobject" or "objectasset" or "prefab") return ResourceType.Object;
        if (name is "room" or "roomname" or "targetroom" or "startroom") return ResourceType.Room;
        if (name is "shader" or "shaderasset") return ResourceType.Shader;
        if (name is "audio" or "audioasset" or "sound" or "soundasset" or "soundname" or "audiosource") return ResourceType.Audio;
        if (name is "particle" or "particleasset" or "effectasset") return ResourceType.Particle;
        if (name is "physicsasset") return ResourceType.Physics;
        if (name is "terrainasset") return ResourceType.Terrain;
        if (name is "routeasset" or "pathingasset") return ResourceType.Pathing;
        if (name is "uiasset") return ResourceType.UserInterface;
        if (name == "scriptclass" && ownerType == ResourceType.Object && value != null)
        {
            JToken document = value; while (document.Parent != null) document = document.Parent;
            if (document is JObject obj && string.Equals((string)obj["name"], (string)value, StringComparison.OrdinalIgnoreCase)) return ResourceType.Object;
        }
        if (name is "scriptasset" or "scriptname" or "behaviorclass" or "scriptclass") return ResourceType.Script;
        if (name == "parent" && ownerType == ResourceType.Object) return ResourceType.Object;
        if (name == "name" && path.IndexOf("prefab.", StringComparison.OrdinalIgnoreCase) >= 0) return ResourceType.Object;
        if (name == "asset")
        {
            string lower = path.ToLowerInvariant();
            if (lower.Contains("terrain")) return ResourceType.Terrain;
            if (lower.Contains("background")) return ResourceType.Image;
            if (lower.Contains("audio") || lower.Contains("sound")) return ResourceType.Audio;
            if (lower.Contains("prefab")) return ResourceType.Object;
            for (JToken node = value?.Parent; node != null; node = node.Parent)
            {
                if (node is not JObject obj) continue;
                string type = ((string)obj["type"] ?? string.Empty).ToLowerInvariant();
                if (type.Contains("sprite")) return ResourceType.Image;
                if (type.Contains("model")) return ResourceType.Model;
                if (type.Contains("audio")) return ResourceType.Audio;
                if (type.Contains("shader")) return ResourceType.Shader;
                if (type.Contains("particle")) return ResourceType.Particle;
                if (type.Contains("physics")) return ResourceType.Physics;
                if (type.Contains("terrain")) return ResourceType.Terrain;
            }
        }
        return ResourceType.Unknown;
    }

    /// <summary>Argument positions are zero-based; handles, joint names and animation names are not resources.</summary>
    public static ResourceType ArgumentType(string command, int argument)
    {
        string qualified = (command ?? string.Empty).ToLowerInvariant();
        if (qualified == "spriterigruntime.bind" && argument == 3) return ResourceType.Image;
        if (argument == 1)
        {
            if (qualified is "spriteassetloader.load" or "spriteassetloader.resolvedescriptorpath" or "pixelrigassetloader.load") return ResourceType.Image;
            if (qualified is "projectroomresolver.resolveroomfile" or "projectroomresolver.resolveroomname") return ResourceType.Room;
            if (qualified == "roomscenebuilder.resolveprefabpath") return ResourceType.Object;
            if (qualified is "physicsassetcatalog.loadprofile" or "physicsassetcatalog.loadpresetbyname") return ResourceType.Physics;
        }
        string name = qualified[(qualified.LastIndexOf('.') + 1)..];
        if (argument == 0)
        {
            if (name is "spriteset" or "spriterigbind" or "bindspriterig" or "drawsprite" or "drawspriteext" or "drawspritepart"
                or "drawspritestretched" or "drawspritegeneral" or "drawspritetransformed" or "spriteresource" or "loadtexture"
                or "assetload" or "imagegetwidth" or "imagegetheight" or "spritegetwidth" or "spritegetheight"
                or "spritegetnumber" or "spritegetbboxleft" or "spritegetbboxright" or "spritegetbboxtop" or "spritegetbboxbottom") return ResourceType.Image;
            if (name is "soundload" or "audioload" or "playsound" or "soundplayasset") return ResourceType.Audio;
            if (name is "createinstance" or "spawn" or "instancecreate" or "instancecreateat" or "objectexists" or "instancenumber" or "instancefind") return ResourceType.Object;
            if (name is "gotroom" or "gotoroom" or "roomgoto" or "roomset" or "changeroom" or "roomexists") return ResourceType.Room;
            if (name is "modelset" or "modelload" or "drawmodel3d") return ResourceType.Model;
            if (name is "shaderset" or "shaderload") return ResourceType.Shader;
            if (name is "particleload" or "particleemit" or "particlesspawn" or "particlespawn" or "particlesset" or "effectload" or "particle2dburst" or "particle2dstream" or "particle2dflow" or "spawnparticleemitter") return ResourceType.Particle;
            if (name is "uiload" or "uidraw" or "drawui" or "uishow" or "uisettext" or "uisetvalue" or "uisetvisible" or "uihittest") return ResourceType.UserInterface;
            if (name is "scrload" or "screxecute" or "scriptexecute") return ResourceType.Script;
            if (name == "pathfollow") return ResourceType.Pathing;
            if (name is "physicsload" or "physicssetprofile") return ResourceType.Physics;
        }
        if (name == "viewsetfollow" && argument == 1) return ResourceType.Object;
        if ((name is "collisioncircle3d" or "placefree3d") && argument == 3) return ResourceType.Object;
        if (name == "collisiondistance3d" && argument == 2) return ResourceType.Object;
        if (name == "pathingassign" && argument == 1) return ResourceType.Pathing;
        if ((name is "placemeeting" or "instanceposition") && argument == 2) return ResourceType.Object;
        if (name == "collisionrectangle" && argument == 4) return ResourceType.Object;
        if (name == "collisioncircle" && argument == 3) return ResourceType.Object;
        if (name == "collisionline" && argument == 4) return ResourceType.Object;
        return ResourceType.Unknown;
    }

    private static bool IsMixedResourceArgument(string command, int argument)
    {
        string qualified = command.ToLowerInvariant();
        string name = qualified[(qualified.LastIndexOf('.') + 1)..];
        if (argument == 0 && (name is "resourceexists" or "resourcename" or "resourcetypeof" or "resolveassetpath")) return true;
        return argument == 1 && (qualified is "resourcecatalog.resolve" or "resourcecatalog.name" or "resourcenames.resolve" or "resourcenames.name");
    }

    private static string RewriteMarkdown(string text, Func<string, ResourceType, string> map)
    {
        // Resource links and code examples are bindings; prose and provenance tables are not.
        string result = Regex.Replace(text, @"(?<prefix>```(?:pgsl|csharp|cs)[^\r\n]*\r?\n)(?<code>[\s\S]*?)(?<suffix>```)",
            match => match.Groups["prefix"].Value + RewriteCode(match.Groups["code"].Value, map, rewriteScriptCalls: match.Groups["prefix"].Value.StartsWith("```pgsl", StringComparison.OrdinalIgnoreCase)) + match.Groups["suffix"].Value,
            RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"(?<prefix>\]\()(?<target>[^)\r\n]+)(?<suffix>\))", match =>
        {
            string target = match.Groups["target"].Value;
            return match.Groups["prefix"].Value + map(target, ResourceType.Unknown) + match.Groups["suffix"].Value;
        });
        return result;
    }

    private sealed class CallFrame
    {
        public string Name;
        public int Argument;
        public char Close;
        public CallFrame(string name, char close) { Name = name; Close = close; }
    }

    public static string RewriteCode(string code, Func<string, ResourceType, string> map, bool rewriteScriptCalls = false)
    {
        var output = new StringBuilder(code.Length);
        var frames = new Stack<CallFrame>();
        string lastWord = string.Empty;
        string assignment = string.Empty;
        string receiver = string.Empty;
        string pendingCallResource = null;
        // Local callable/variable declarations shadow module names. Do not rename their identifiers.
        var localNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rewriteScriptCalls)
            foreach (Match declaration in Regex.Matches(code, @"\b(?:function|func|var|let|const)\s+([A-Za-z_][A-Za-z0-9_]*)"))
                localNames.Add(declaration.Groups[1].Value);
        for (int i = 0; i < code.Length;)
        {
            int start = i;
            char c = code[i];
            if (c == '/' && i + 1 < code.Length && (code[i + 1] is '/' or '*'))
            {
                bool block = code[i + 1] == '*'; i += 2;
                if (block) { while (i + 1 < code.Length && !(code[i] == '*' && code[i + 1] == '/')) i++; i = Math.Min(code.Length, i + 2); }
                else { while (i < code.Length && code[i] != '\n') i++; }
                output.Append(code, start, i - start); continue;
            }
            bool verbatim = c == '@' && i + 1 < code.Length && code[i + 1] == '"';
            if (c is '"' or '\'' || verbatim)
            {
                if (verbatim) i++;
                char quote = code[i++]; var decoded = new StringBuilder(); bool complete = false;
                // Do not reinterpret C# raw/interpolated strings; a dynamic expression is not a literal resource binding.
                if (quote == '"' && i + 1 < code.Length && code[i] == '"' && code[i + 1] == '"')
                {
                    i += 2; int end = code.IndexOf("\"\"\"", i, StringComparison.Ordinal);
                    i = end < 0 ? code.Length : end + 3; output.Append(code, start, i - start); continue;
                }
                while (i < code.Length)
                {
                    char ch = code[i++];
                    if (ch == quote)
                    {
                        if (verbatim && i < code.Length && code[i] == quote) { decoded.Append(quote); i++; continue; }
                        complete = true; break;
                    }
                    if (ch == '\\' && !verbatim && i < code.Length)
                    {
                        char escaped = code[i++];
                        decoded.Append(escaped switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => escaped });
                    }
                    else decoded.Append(ch);
                }
                string before = decoded.ToString();
                ResourceType expected = frames.Count > 0 ? ArgumentType(frames.Peek().Name, frames.Peek().Argument) : ResourceType.Unknown;
                if (expected == ResourceType.Unknown && assignment.Length > 0) expected = PropertyType(assignment);
                bool interpolation = start > 0 && code[start - 1] == '$';
                bool resourceQuery = frames.Count > 0 && IsMixedResourceArgument(frames.Peek().Name, frames.Peek().Argument);
                string after = complete && !interpolation && (expected != ResourceType.Unknown || resourceQuery || ResourceCatalog.LooksLikeStorageReference(before)) ? map(before, expected) : before;
                if (after == before) output.Append(code, start, i - start);
                else if (verbatim) output.Append("@\"").Append(after.Replace("\"", "\"\"")).Append('"');
                else output.Append(quote).Append(after.Replace("\\", "\\\\").Replace(quote.ToString(), "\\" + quote)).Append(quote);
                lastWord = string.Empty; assignment = string.Empty; continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                i++; while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '_')) i++;
                string word = code.Substring(start, i - start);
                int next = i; while (next < code.Length && char.IsWhiteSpace(code[next])) next++;
                int previous = start - 1; while (previous >= 0 && char.IsWhiteSpace(code[previous])) previous--;
                bool bareStatement = next < code.Length && code[next] == ';' && (previous < 0 || code[previous] is '{' or '}' or ';');
                bool call = next < code.Length && code[next] == '(' && (previous < 0 || code[previous] != '.');
                if (rewriteScriptCalls && (call || bareStatement) && !localNames.Contains(word))
                {
                    string mapped = map(word, ResourceType.Script);
                    if (mapped == word && word.StartsWith("scr_", StringComparison.OrdinalIgnoreCase))
                    {
                        string withoutPrefix = word.Substring(4);
                        string mappedAlias = map(withoutPrefix, ResourceType.Script);
                        if (mappedAlias != withoutPrefix) mapped = mappedAlias;
                    }
                    if (mapped != word)
                    {
                        if (bareStatement)
                        {
                            output.Append("ScriptExecute(").Append(JsonConvert.SerializeObject(mapped)).Append(')');
                            lastWord = string.Empty; continue;
                        }
                        if (Regex.IsMatch(mapped, @"^[A-Za-z_][A-Za-z0-9_]*$")) word = mapped;
                        else { word = "ScriptExecute"; pendingCallResource = mapped; }
                    }
                }
                lastWord = word; output.Append(lastWord); continue;
            }
            if (c == '(' && pendingCallResource != null)
            {
                frames.Push(new CallFrame("ScriptExecute", ')') { Argument = 1 });
                output.Append('(').Append(JsonConvert.SerializeObject(pendingCallResource));
                int next = i + 1; while (next < code.Length && char.IsWhiteSpace(code[next])) next++;
                if (next < code.Length && code[next] != ')') output.Append(", ");
                pendingCallResource = null; assignment = ""; lastWord = ""; receiver = ""; i++; continue;
            }
            if (c is '(' or '[' or '{') { frames.Push(new CallFrame(c == '(' ? receiver + lastWord : "", c == '(' ? ')' : c == '[' ? ']' : '}')); assignment = ""; lastWord = ""; receiver = ""; }
            else if (c is ')' or ']' or '}') { if (frames.Count > 0 && frames.Peek().Close == c) frames.Pop(); assignment = ""; lastWord = ""; }
            else if (c == ',' && frames.Count > 0) { frames.Peek().Argument++; assignment = ""; lastWord = ""; }
            else if (c == '.') receiver = lastWord + ".";
            else if (c == '=') { assignment = lastWord; receiver = ""; }
            else if (c == ';') { assignment = ""; lastWord = ""; }
            output.Append(c); i++;
        }
        return output.ToString();
    }
}
