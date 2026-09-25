using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Genesis.Physics;

/// <summary>
/// Lists and loads physics assets from a project folder.
/// Built-in presets are always available; user assets live in <c>Physics/*.physics.json</c>.
/// </summary>
public static class PhysicsAssetCatalog
{
    public static IReadOnlyList<string> ListProfileNames(string? projectPath)
    {
        var names = new List<string>(PhysicsScenePresets.Names);
        if (!string.IsNullOrWhiteSpace(projectPath))
            names.AddRange(ResourceNames.For(projectPath).Entries.Where(e => e.Type == ResourceType.Physics).Select(e => e.Name).Except(names, StringComparer.OrdinalIgnoreCase));
        return names;
    }

    public static PhysicsSceneConfig LoadProfile(string? projectPath, string profileName)
    {
        string named = string.IsNullOrWhiteSpace(projectPath) ? string.Empty : ResourceNames.Resolve(projectPath, profileName, ResourceType.Physics);
        if (named.Length > 0) return PhysicsSceneConfig.LoadFromFile(named);
        if (Array.IndexOf(PhysicsScenePresets.Names, profileName) >= 0)
            return PhysicsScenePresets.Create(profileName);

        if (string.IsNullOrEmpty(projectPath))
            return PhysicsScenePresets.Default();

        string path = Path.Combine(projectPath, "Physics", profileName + ".physics.json");
        return PhysicsSceneConfig.LoadFromFile(path);
    }

    public static string GetAssetPath(string projectPath, string assetName)
    {
        string path = ResourceNames.Resolve(projectPath, assetName, ResourceType.Physics);
        return path.Length > 0 ? path : Path.Combine(projectPath, "Assets", "Physics", ResourceNames.ValidateName(assetName) + ".physics.json");
    }

    public static string PresetsDir(string projectPath) =>
        Path.Combine(projectPath, "Physics", "Presets");

    public static IEnumerable<string> ListUserPresetNames(string projectPath)
    {
        string dir = PresetsDir(projectPath);
        if (!Directory.Exists(dir))
            return Enumerable.Empty<string>();
        return Directory.GetFiles(dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))!;
    }

    public static void SaveUserPreset(string projectPath, string presetName, PhysicsSceneConfig config)
    {
        string dir = PresetsDir(projectPath);
        Directory.CreateDirectory(dir);
        config.Clone().SaveToFile(Path.Combine(dir, presetName + ".json"));
    }

    public static PhysicsSceneConfig LoadPresetByName(string projectPath, string name)
    {
        string named = ResourceNames.Resolve(projectPath, name, ResourceType.Physics);
        if (named.Length > 0) return PhysicsSceneConfig.LoadFromFile(named);
        if (Array.IndexOf(PhysicsScenePresets.Names, name) >= 0)
            return PhysicsScenePresets.Create(name);

        string path = Path.Combine(PresetsDir(projectPath), name + ".json");
        if (File.Exists(path))
            return PhysicsSceneConfig.LoadFromFile(path);

        string assetPath = GetAssetPath(projectPath, name);
        if (File.Exists(assetPath))
            return PhysicsSceneConfig.LoadFromFile(assetPath);

        return PhysicsScenePresets.Default();
    }

    public static string StripPresetSuffix(string name)
    {
        int idx = name.LastIndexOf('_');
        if (idx > 0 && idx < name.Length - 1 && int.TryParse(name[(idx + 1)..], out _))
            return name[..idx];
        return name;
    }

    public static string NextPresetName(string projectPath, string root)
    {
        var existing = new HashSet<string>(PhysicsScenePresets.Names, StringComparer.OrdinalIgnoreCase);
        foreach (string p in ListUserPresetNames(projectPath))
            existing.Add(p);

        int n = 1;
        while (existing.Contains(root + "_" + n))
            n++;
        return root + "_" + n;
    }
}
