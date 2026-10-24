using System.Globalization;
using Genesis.Runtime.Scripting;

namespace Genesis.Application.Editors.Suite.Inspector;

/// <summary>
/// The Inspector's "Engine settings" group, the same in every editor that has them (Object, Room
/// and its instances, Model, Terrain): each setting the resource carries as a value to edit, an
/// "Add a setting" list of the standard API's values that suit it (Engine.Physics.Mass for an
/// instance, Engine.Physics.AirDrag for a room) and a "Remove a setting" list. The editor keeps the
/// settings (a name to its text) and saves them with the resource; the game sets them as a script
/// would (<see cref="EngineSettings"/>).
/// </summary>
public static class EngineSettingsInspector
{
    public const string Group = "Engine settings";
    public const string Prefix = "EngineSettings.";
    public const string AddPath = Prefix + "+Add";
    public const string RemovePath = Prefix + "+Remove";

    /// <summary>The rows for <paramref name="settings"/>: one per setting, then Add and Remove.</summary>
    public static void AddValues(List<ResourceInspectorLiveValue> values, IReadOnlyDictionary<string, string>? settings,
        EngineSettingScope scope, bool readOnly = false)
    {
        foreach ((string name, string text) in (settings ?? new Dictionary<string, string>()).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            bool known = EngineSettings.TryFind(name, out EngineSettings.Setting setting);
            object value = known ? EngineSettings.Parse(text, setting.Type) : text;
            values.Add(new ResourceInspectorLiveValue(Group, Prefix + name, ShortName(name), value, readOnly,
                known ? setting.Description : "Not an engine value that can be set: the game skips it.", known ? setting.Choices : null));
        }
        if (readOnly) return;
        // Listed without "Engine." (Rendering.Fog.Start), so typing the area's first letters finds it.
        string[] add = EngineSettings.Available(scope).Select(setting => setting.Name)
            .Where(name => settings == null || !settings.ContainsKey(name)).Select(ListName).ToArray();
        values.Add(new ResourceInspectorLiveValue(Group, AddPath, "Add a setting", "",
            Description: scope == EngineSettingScope.Instance
                ? "A value each instance starts with, set just before its Create event (as a script would set it). It starts at the value's default."
                : "A value the world starts with, set just before the room's Create events. It starts at the value's default.",
            Choices: [.. add.Prepend("")]));
        if (settings is { Count: > 0 })
            values.Add(new ResourceInspectorLiveValue(Group, RemovePath, "Remove a setting", "",
                Choices: [.. settings.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Select(ListName).Prepend("")]));
    }

    /// <summary>A setting as the Add and Remove lists show it: Engine.Rendering.Fog.Start as Rendering.Fog.Start.</summary>
    public static string ListName(string name) =>
        name.StartsWith("Engine.", StringComparison.Ordinal) ? name["Engine.".Length..] : name;

    // A list's choice back to the full name; a full name is taken as it is.
    private static string FullName(string chosen) =>
        chosen.Length == 0 || chosen.StartsWith("Engine.", StringComparison.Ordinal) ? chosen : "Engine." + chosen;

    /// <summary>
    /// Applies an Inspector edit to <paramref name="settings"/>: a value, an added setting (starting
    /// at 1, true or its first choice) or a removed one. False when the path is not one of these.
    /// </summary>
    public static bool TryApply(string propertyPath, object? value, IDictionary<string, string> settings, EngineSettingScope scope)
    {
        if (string.IsNullOrEmpty(propertyPath) || !propertyPath.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        string chosen = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "";
        if (propertyPath is AddPath or RemovePath) chosen = FullName(chosen);
        if (propertyPath == AddPath)
        {
            if (chosen.Length == 0 || settings.ContainsKey(chosen) || !EngineSettings.TryFind(chosen, out EngineSettings.Setting setting)
                || setting.Scope != scope) return false;
            settings[setting.Name] = EngineSettings.DefaultText(setting);
            return true;
        }
        if (propertyPath == RemovePath) return chosen.Length > 0 && settings.Remove(chosen);
        string name = propertyPath[Prefix.Length..];
        if (!settings.ContainsKey(name)) return false;
        string text = EngineSettings.Format(value);
        if (EngineSettings.TryFind(name, out EngineSettings.Setting known) && known.Choices is { Count: > 0 } choices
            && !choices.Contains(text, StringComparer.OrdinalIgnoreCase)) return false;
        settings[name] = text;
        return true;
    }

    /// <summary>Engine.Physics.Mass shows as "Physics · Mass".</summary>
    public static string ShortName(string name)
    {
        string[] parts = name.Split('.');
        return parts.Length >= 3 && parts[0] == "Engine" ? string.Join(" · ", parts.Skip(1)) : name;
    }
}
