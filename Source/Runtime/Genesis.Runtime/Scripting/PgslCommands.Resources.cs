using System;
using System.Collections.Generic;
using Genesis.Shared.Scripting;
using Genesis.Runtime.Scripting.VM;

namespace Genesis.Runtime.Scripting;

public static partial class PgslCommands
{
    [PgslCommand("ResourceExists", "ResourceExists(name) -> bool", "Check a globally unique project resource name; no path or extension", "Resources")]
    public static bool ResourceExists(string name) => !string.IsNullOrWhiteSpace(ProjectPath)
        && ResourceNames.For(ProjectPath).Find(name) is not null;

    [PgslCommand("ResourceName", "ResourceName(name) -> string", "Return the canonical resource name, or an empty string when absent", "Resources")]
    public static string ResourceName(string name) => string.IsNullOrWhiteSpace(ProjectPath) ? string.Empty
        : ResourceNames.For(ProjectPath).Find(name)?.Name ?? string.Empty;

    [PgslCommand("ResourceTypeOf", "ResourceTypeOf(name) -> string", "Return the resource type for a project name, or an empty string when absent", "Resources")]
    public static string ResourceTypeOf(string name) => string.IsNullOrWhiteSpace(ProjectPath) ? string.Empty
        : ResourceNames.For(ProjectPath).Find(name)?.Type.ToString() ?? string.Empty;
    [PgslCommand("ScriptExecute", "ScriptExecute(name, ...) -> value", "Execute a named PGSL Script resource; supports spaces in resource names. Returns 0 when no Script VM is active", "Resources")]
    public static object ScriptExecute(string name, params object[] arguments)
    {
        if (GetContext()?.ActiveVm is not PgslVm vm)
            return 0;
        ScriptAssetRegistry.EnsureProjectLoaded(ProjectPath);
        string canonical = ResourceNames.For(ProjectPath).Find(name, ResourceType.Script)?.Name;
        if (string.IsNullOrEmpty(canonical)) throw new InvalidOperationException($"Unknown Script resource '{name}'.");
        return ScriptAssetRegistry.ExecuteWithReturn(vm, canonical, arguments ?? Array.Empty<object>()) ?? 0;
    }

    [PgslCommand("ResourceList", "ResourceList(folder, type, subfolders?) -> id",
        "A new list (DsList) of the names of the resources of one kind in a project folder, sorted, for a menu that offers whatever the folder holds. "
        + "folder from Assets (\"Shaders/Packs\") or from the project (\"Assets/Shaders/Packs\"), \"\" for Assets; type as ResourceTypeOf names it "
        + "(\"Shader\", \"Image\", \"Audio\", \"Object\", \"Room\", \"Model\"...), \"Fullscreen shader\", \"Mesh shader\" or \"Sprite shader\" for one kind "
        + "of shader, or \"\" for every kind; subfolders true to include theirs. Free it with DsListDestroy", "Resources")]
    public static double ResourceList(string folder, string type, bool subfolders = false)
    {
        double list = DsListCreate();
        if (list == 0 || string.IsNullOrWhiteSpace(ProjectPath)) return list;
        foreach (string name in ResourceNamesIn(ProjectPath, folder, type, subfolders)) ListAppend(list, name);
        return list;
    }

    /// <summary>
    /// The names of the resources of one kind in a project folder (see <see cref="ResourceList"/>),
    /// sorted by name. Empty for a folder outside the project, one that does not exist, or a kind
    /// that is not one.
    /// </summary>
    public static IReadOnlyList<string> ResourceNamesIn(string projectPath, string folder, string type, bool subfolders)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return Array.Empty<string>();
        string directory = ResourceFolder(projectPath, folder);
        if (directory == null || !TryResourceKind(type, out ResourceType kind, out Genesis.Shared.Assets.ShaderAssetPipeline? pipeline))
            return Array.Empty<string>();
        var names = new List<string>();
        foreach (Genesis.Shared.Assets.NamedResource entry in ResourceNames.For(projectPath).Entries)
        {
            if (kind != ResourceType.Unknown && entry.Type != kind) continue;
            string parent = System.IO.Path.GetDirectoryName(entry.FullPath) ?? string.Empty;
            bool inFolder = string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase)
                || (subfolders && parent.StartsWith(directory + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (!inFolder) continue;
            if (pipeline is { } wanted && !ShaderHasPipeline(entry.FullPath, wanted)) continue;
            names.Add(entry.Name);
        }
        return names;
    }

    // A folder named from Assets, else from the project; null when it is not a folder of the project.
    private static string ResourceFolder(string projectPath, string folder)
    {
        try
        {
            string root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(projectPath));
            string relative = (folder ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
            if (System.IO.Path.IsPathRooted(relative)) return null;
            string assets = System.IO.Path.Combine(root, "Assets");
            foreach (string candidate in new[] { System.IO.Path.Combine(assets, relative), System.IO.Path.Combine(root, relative) })
            {
                string full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(
                    candidate.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                bool inside = string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                    || full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                if (inside && System.IO.Directory.Exists(full)) return full;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or System.IO.IOException)
        {
        }
        return null;
    }

    private static bool TryResourceKind(string type, out ResourceType kind, out Genesis.Shared.Assets.ShaderAssetPipeline? pipeline)
    {
        pipeline = null;
        kind = ResourceType.Unknown;
        string key = (type ?? string.Empty).Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty).ToLowerInvariant();
        switch (key)
        {
            case "": case "all": case "any": return true;
            case "fullscreenshader": case "fullscreen": case "posteffect":
                kind = ResourceType.Shader; pipeline = Genesis.Shared.Assets.ShaderAssetPipeline.Fullscreen; return true;
            case "meshshader": kind = ResourceType.Shader; pipeline = Genesis.Shared.Assets.ShaderAssetPipeline.Mesh; return true;
            case "spriteshader": kind = ResourceType.Shader; pipeline = Genesis.Shared.Assets.ShaderAssetPipeline.Sprite; return true;
            case "sprite": case "picture": kind = ResourceType.Image; return true;
            case "sound": case "music": kind = ResourceType.Audio; return true;
            case "gameobject": kind = ResourceType.Object; return true;
            case "ui": case "userinterfaces": kind = ResourceType.UserInterface; return true;
            case "particles": kind = ResourceType.Particle; return true;
        }
        if (key.EndsWith("s", StringComparison.Ordinal) && Enum.TryParse(key[..^1], ignoreCase: true, out ResourceType plural) && plural != ResourceType.Unknown)
        {
            kind = plural;
            return true;
        }
        return Enum.TryParse(key, ignoreCase: true, out kind) && kind != ResourceType.Unknown;
    }

    private static bool ShaderHasPipeline(string path, Genesis.Shared.Assets.ShaderAssetPipeline wanted)
    {
        try { return Genesis.Shared.Assets.ShaderAssetDocument.Load(path).Pipeline == wanted; }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException
            or System.IO.InvalidDataException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
