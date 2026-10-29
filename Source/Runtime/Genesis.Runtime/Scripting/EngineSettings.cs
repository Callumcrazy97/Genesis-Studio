#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>What a setting is about: one instance (its body, model, image), or the world.</summary>
public enum EngineSettingScope
{
    Instance,
    World,
}

/// <summary>
/// Engine settings: values of the standard API (<c>Engine.Physics.Mass = 4</c>) that a resource gives
/// what uses it. An Object gives them to its instances (a room's instance of it may change or add
/// to them), a model to the instances showing it, a room and its terrain to the world. Studio's
/// Inspector adds, changes and removes them; a game applies them as if a script had set them: an
/// instance's just before its Create event, a room's just before its instances' Create events.
/// </summary>
public static class EngineSettings
{
    /// <summary>The Object component (and room override) that holds an instance's settings.</summary>
    public const string ComponentType = "EngineSettings";

    // The physics values that belong to the world; the area's others are the current instance's body.
    private static readonly HashSet<string> WorldPhysics = new(StringComparer.OrdinalIgnoreCase)
    {
        "AirDrag", "AllowSleep", "GravityStrength", "GravityX", "GravityY", "GravityZ", "MaxVelocity",
        "SleepThreshold", "RecoveryVelocity",
    };

    private static readonly HashSet<string> InstanceAreas = new(StringComparer.Ordinal) { "Model", "Image", "Instance", "Animation" };

    private static readonly Dictionary<string, string[]> KnownChoices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Engine.Physics.BodyType"] = new[] { "dynamic", "kinematic", "static" },
        ["Engine.Physics.Shape"] = new[] { "box", "sphere", "capsule", "cylinder" },
        ["Engine.Audio.Acoustics"] = new[] { "outdoors", "room", "hall", "cave" },
        ["Engine.Audio.Acoustics.Place"] = new[] { "outdoors", "room", "hall", "cave" },
        ["Engine.Audio.Music.Filter.Type"] = Genesis.Shared.Audio.AudioBusEffects.FilterTypes,
        ["Engine.Audio.Effects.Filter.Type"] = Genesis.Shared.Audio.AudioBusEffects.FilterTypes,
        ["Engine.Audio.Music.Reverb.Place"] = Genesis.Shared.Audio.AudioBusEffects.ReverbPlaces,
        ["Engine.Audio.Effects.Reverb.Place"] = Genesis.Shared.Audio.AudioBusEffects.ReverbPlaces,
        ["Engine.Rendering.AntiAliasing.Mode"] = new[] { "off", "fxaa", "smaa", "taa" },
        ["Engine.Lighting.Ambient.Mode"] = new[] { "zenith", "hemisphere" },
        ["Engine.Camera.Rigs.BlendCurve"] = new[] { "easeinout", "linear", "easein", "easeout", "cut" },
    };

    /// <summary>One setting a resource can carry: its full name, the type of its value and what it is.</summary>
    public sealed record Setting(string Name, EngineSettingScope Scope, Type Type, string Description, IReadOnlyList<string>? Choices);

    private static readonly object Gate = new();
    private static Dictionary<string, Setting>? _settings;
    private static PgslEngineBridge? _bridge;

    /// <summary>Every property a script can set that suits <paramref name="scope"/>, in name order.</summary>
    public static IReadOnlyList<Setting> Available(EngineSettingScope scope) =>
        All().Values.Where(setting => setting.Scope == scope).OrderBy(setting => setting.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public static bool TryFind(string name, out Setting setting)
    {
        setting = null!;
        return !string.IsNullOrWhiteSpace(name) && All().TryGetValue(name.Trim(), out setting!);
    }

    /// <summary>Whether a standard name is a value of one instance or of the world.</summary>
    public static EngineSettingScope ScopeOf(string name)
    {
        string[] parts = name.Split('.');
        if (parts.Length < 3) return EngineSettingScope.World;
        if (InstanceAreas.Contains(parts[1])) return EngineSettingScope.Instance;
        // Parts of an area that belong to one instance or to the world (Engine.Camera.Orbit.Distance).
        if (parts.Length >= 4 && InstanceParts.Contains(parts[1] + "." + parts[2])) return EngineSettingScope.Instance;
        if (parts.Length >= 4 && WorldParts.Contains(parts[1] + "." + parts[2])) return EngineSettingScope.World;
        return parts[1] == "Physics" && !WorldPhysics.Contains(parts[^1]) ? EngineSettingScope.Instance : EngineSettingScope.World;
    }

    private static readonly HashSet<string> InstanceParts = new(StringComparer.Ordinal) { "Camera.Orbit", "Camera.Shoulder", "Camera.Collision" };
    private static readonly HashSet<string> WorldParts = new(StringComparer.Ordinal) { "Physics.World", "Physics.Gravity", "Physics.Sleep" };

    /// <summary>A setting's saved text as the value its property takes (a number, true/false or text).</summary>
    public static object Parse(string? text, Type type)
    {
        text = text?.Trim() ?? "";
        if (type == typeof(bool))
            return bool.TryParse(text, out bool flag) ? flag : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && n != 0;
        if (type == typeof(string)) return text;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : 0d;
    }

    /// <summary>A value as the text a setting is saved as (invariant numbers, true/false).</summary>
    public static string Format(object? value) => value switch
    {
        null => "",
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>
    /// The text a new setting starts with: the value's own default, as its description gives it
    /// ("(35 unless set)") or as it reads where nothing has set it; otherwise 1, true, or the first
    /// of its choices.
    /// </summary>
    public static string DefaultText(Setting setting)
    {
        System.Text.RegularExpressions.Match stated = System.Text.RegularExpressions.Regex.Match(setting.Description ?? "",
            @"\((?<value>-?[0-9]+(?:\.[0-9]+)?|true|false|""[^""]*"")(?:[^()]*?)\bunless\b");
        if (stated.Success)
        {
            string value = stated.Groups["value"].Value.Trim('"');
            if (setting.Choices is not { Count: > 0 } choices || choices.Contains(value, StringComparer.OrdinalIgnoreCase)) return value;
        }
        if (TryRead(setting.Name, new PgslContext(), out object? current) && current != null
            && (setting.Type != typeof(double) || Convert.ToDouble(current, CultureInfo.InvariantCulture) != 0)
            && (setting.Type != typeof(string) || current is string { Length: > 0 }))
        {
            string text = Format(current);
            if (setting.Choices is not { Count: > 0 } choices || choices.Contains(text, StringComparer.OrdinalIgnoreCase)) return text;
        }
        return setting.Choices is { Count: > 0 } list ? list[0] : setting.Type == typeof(bool) ? "true" : setting.Type == typeof(string) ? "" : "1";
    }

    /// <summary>
    /// The range a number setting keeps to, as its description gives it ("0 to 600 seconds");
    /// false when it states none.
    /// </summary>
    public static bool TryRange(Setting setting, out double minimum, out double maximum)
    {
        minimum = maximum = 0;
        if (setting.Type != typeof(double)) return false;
        foreach (System.Text.RegularExpressions.Match range in System.Text.RegularExpressions.Regex.Matches(setting.Description ?? "",
                     @"(?<!\w)(?<min>-?[0-9]+(?:\.[0-9]+)?) to (?<max>-?[0-9]+(?:\.[0-9]+)?)(?!\w)"))
        {
            double low = double.Parse(range.Groups["min"].Value, CultureInfo.InvariantCulture);
            double high = double.Parse(range.Groups["max"].Value, CultureInfo.InvariantCulture);
            if (!(low < high)) continue;
            minimum = low;
            maximum = high;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Sets each setting through the standard API with <paramref name="context"/> as the current
    /// instance; a name that is not a property that can be set is skipped and reported.
    /// Returns how many were set.
    /// </summary>
    public static int Apply(IEnumerable<KeyValuePair<string, string>>? settings, PgslContext context, ICollection<string>? problems = null)
    {
        if (settings == null) return 0;
        int applied = 0;
        PgslEngineBridge bridge = Bridge();
        PgslContext previous = PgslCommands.BindContext(context);
        try
        {
            foreach ((string name, string text) in settings)
            {
                if (!TryFind(name, out Setting setting))
                {
                    problems?.Add($"{name} is not an engine value that can be set.");
                    continue;
                }
                try
                {
                    if (bridge.TrySetProperty(setting.Name, Parse(text, setting.Type))) applied++;
                }
                catch (Exception exception)
                {
                    problems?.Add($"{name} = {text}: {exception.GetBaseException().Message}");
                }
            }
        }
        finally
        {
            PgslCommands.BindContext(previous);
        }
        return applied;
    }

    /// <summary>A standard value as a script reads it, with <paramref name="context"/> as the current instance.</summary>
    public static bool TryRead(string name, PgslContext context, out object? value)
    {
        value = null;
        PgslEngineBridge bridge = Bridge();
        PgslContext previous = PgslCommands.BindContext(context);
        try
        {
            return bridge.TryInvokeKnown(name, Array.Empty<object>(), out value);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            PgslCommands.BindContext(previous);
        }
    }

    // What the debug screen shows of an instance, by what it has: a body, a model, an animator, an image.
    private static readonly string[] BodyValues =
    {
        "Engine.Physics.BodyType", "Engine.Physics.Mass", "Engine.Physics.Friction", "Engine.Physics.Bounciness",
        "Engine.Physics.GravityScale", "Engine.Physics.LinearDamping", "Engine.Physics.VelocityX", "Engine.Physics.VelocityY",
        "Engine.Physics.VelocityZ", "Engine.Physics.Sensor", "Engine.Physics.Enabled",
    };
    private static readonly string[] ModelValues =
    {
        "Engine.Model.Glow", "Engine.Model.EmissionScale", "Engine.Model.CastShadows", "Engine.Model.ReceiveShadows", "Engine.Model.InFront", "Engine.Model.Layer",
    };
    private static readonly string[] AnimationValues = { "Engine.Animation.Speed", "Engine.Animation.Loop" };
    // Shown only for an instance that looks or reaches, so the others' lists are as they were.
    private static readonly string[] BoneTargetValues =
    {
        "Engine.Animation.LookAt.Enabled", "Engine.Animation.LookAt.Weight", "Engine.Animation.Reach.Enabled", "Engine.Animation.Reach.Weight",
    };
    private static readonly string[] ImageValues =
    {
        "Engine.Image.Frame", "Engine.Image.Speed", "Engine.Image.Alpha", "Engine.Image.Angle", "Engine.Image.ScaleX", "Engine.Image.ScaleY",
    };
    private static readonly string[] WorldValues =
    {
        "Engine.Physics.AirDrag", "Engine.Physics.GravityStrength", "Engine.Physics.MaxVelocity", "Engine.Audio.MasterVolume",
        "Engine.Audio.MusicVolume", "Engine.Audio.SfxVolume", "Engine.Game.Speed", "Engine.Game.Threads",
        "Engine.Lighting.Enabled", "Engine.Lighting.Bounce.Enabled", "Engine.Lighting.Bounce.Strength", "Engine.Lighting.Bounce.Floor",
        "Engine.Lighting.Reflections.Enabled", "Engine.Lighting.Reflections.Strength",
        "Engine.Rendering.Shadows.Enabled", "Engine.Rendering.Shadows.Distance", "Engine.Rendering.Shadows.Cascades",
        "Engine.Rendering.Shadows.Fade", "Engine.Rendering.Shadows.LampBudget",
    };

    /// <summary>
    /// The values the debug screen shows for an instance, as they are now: its body's when it has one,
    /// its model's, its animation's and its image's.
    /// </summary>
    public static IReadOnlyList<(Setting Setting, object? Value)> InstanceValues(Genesis.Runtime.ECS.World world,
        Genesis.Shared.ECS.Entity entity, PgslContext context)
    {
        var names = new List<string>();
        if (world.Has<Genesis.Shared.ECS.Components.RigidBodyComponent>(entity)) names.AddRange(BodyValues);
        if (world.Has<Genesis.Runtime.ECS.Components.ModelRendererComponent>(entity)) names.AddRange(ModelValues);
        if (world.Has<Genesis.Runtime.ECS.Components.ModelAnimatorComponent>(entity))
        {
            names.AddRange(AnimationValues);
            if (world.GetRef<Genesis.Runtime.ECS.Components.ModelAnimatorComponent>(entity).Targets != null) names.AddRange(BoneTargetValues);
        }
        if (world.Has<Genesis.Runtime.ECS.Components.Draw2DComponent>(entity)) names.AddRange(ImageValues);
        return Read(names, context);
    }

    // Shown only once the room has camera rigs, so a game without them sees the list it always had.
    private static readonly string[] CameraRigValues = { "Engine.Camera.Rigs.Enabled", "Engine.Camera.Rigs.Active", "Engine.Camera.Rigs.BlendTime" };

    /// <summary>The world values the debug screen shows: physics, the audio buses, the game's speed and any camera rigs.</summary>
    public static IReadOnlyList<(Setting Setting, object? Value)> WorldValuesNow() =>
        Read(PgslCommands.CameraRigsCount > 0 ? WorldValues.Concat(CameraRigValues) : WorldValues, new PgslContext());

    private static List<(Setting Setting, object? Value)> Read(IEnumerable<string> names, PgslContext context)
    {
        var values = new List<(Setting, object?)>();
        foreach (string name in names)
            if (TryFind(name, out Setting setting) && TryRead(name, context, out object? value)) values.Add((setting, value));
        return values;
    }

    /// <summary>
    /// A debug-screen edit as the text to set: a number nudged by <paramref name="steps"/> (tenths
    /// below 10, whole numbers above), true/false flipped, a choice moved on; null for plain text.
    /// </summary>
    public static string? NudgedText(Setting setting, object? current, int steps)
    {
        if (setting.Type == typeof(bool)) return current is true ? "false" : "true";
        if (setting.Choices is { Count: > 0 } choices)
        {
            int index = Math.Max(0, choices.ToList().FindIndex(choice => string.Equals(choice, current as string, StringComparison.OrdinalIgnoreCase)));
            return choices[((index + steps) % choices.Count + choices.Count) % choices.Count];
        }
        if (setting.Type == typeof(string)) return null;
        double number = Convert.ToDouble(current ?? 0d, CultureInfo.InvariantCulture);
        double step = Math.Abs(number) >= 10 ? 1 : 0.1;
        return Format(Math.Round(number + steps * step, 4));
    }

    private static PgslEngineBridge Bridge()
    {
        lock (Gate)
        {
            if (_bridge != null) return _bridge;
            var bridge = new PgslEngineBridge();
            bridge.LoadFromRegistry();
            return _bridge = bridge;
        }
    }

    private static Dictionary<string, Setting> All()
    {
        lock (Gate)
        {
            if (_settings != null) return _settings;
            var settings = new Dictionary<string, Setting>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in PgslApi.Reference())
            {
                if (!row.IsProperty || !row.CanSet || PgslApi.ValueType(row.Name) is not { } type) continue;
                // One camera rig's values are for the script that makes it; a resource has no rig to give them to.
                if (row.Name.StartsWith("Engine.Camera.Rig.", StringComparison.OrdinalIgnoreCase)) continue;
                KnownChoices.TryGetValue(row.Name, out string[]? choices);
                settings[row.Name] = new Setting(row.Name, ScopeOf(row.Name), type, row.Description ?? "", choices);
            }
            return _settings = settings;
        }
    }
}
