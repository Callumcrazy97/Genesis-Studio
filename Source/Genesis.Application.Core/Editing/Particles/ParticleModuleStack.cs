using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Genesis.Runtime.Particles;

namespace Genesis.Application.Core.Editing.Particles;

/// <summary>A module in an emitter's stack and whether it is switched on.</summary>
public sealed record ParticleModuleEntry(ParticleModuleDefinition Definition, bool Enabled);

/// <summary>
/// The module stack of one emitter: a view over its runtime settings (<see cref="ParticleConfig"/>).
/// The settings stay the truth. A module that is on writes its values into them; a module that is
/// switched off writes the values that make it do nothing, and keeps its own in
/// <see cref="ParticleConfig.Modules"/> to give them back. So a saved effect plays the same in a game
/// with nothing new at run time, and an older effect, whose stack was never edited, opens with the
/// modules its settings use. UI-independent: callers journal the changed emitter as one edit.
/// </summary>
public static class ParticleModuleStack
{
    private static readonly JsonSerializerOptions StashJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Dictionary<string, PropertyInfo> Settings = typeof(ParticleConfig)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(property => property.CanRead && property.CanWrite)
        .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The modules in the stack, in catalog order (Spawn, Initialise, Update, Render).</summary>
    public static IReadOnlyList<ParticleModuleEntry> Entries(ParticleModuleContext context)
    {
        List<ParticleModuleEntry> result = [];
        foreach (ParticleModuleDefinition module in ParticleModuleCatalog.All)
        {
            if (!module.Available(context)) continue;
            if (module.Required) { result.Add(new ParticleModuleEntry(module, true)); continue; }
            ParticleModuleState? state = State(context.Emitter, module.Id);
            bool on = IsOn(module, context.Emitter);
            // Settings changed elsewhere (the classic inspector, the definition text) win over a
            // remembered "off": the stack shows what the runtime will do.
            if (state is null && !on) continue;
            result.Add(new ParticleModuleEntry(module, on || state?.Enabled == true));
        }
        return result;
    }

    public static bool IsEnabled(ParticleModuleContext context, string id) =>
        Entries(context).Any(entry => string.Equals(entry.Definition.Id, id, StringComparison.OrdinalIgnoreCase) && entry.Enabled);

    public static bool Contains(ParticleModuleContext context, string id) =>
        Entries(context).Any(entry => string.Equals(entry.Definition.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The modules of a group that can still be added (the runtime honours every one of them).</summary>
    public static IReadOnlyList<ParticleModuleDefinition> Addable(ParticleModuleContext context, ParticleModuleGroup group)
    {
        HashSet<string> present = Entries(context).Select(entry => entry.Definition.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ParticleModuleCatalog.All
            .Where(module => module.Group == group && !module.Required && module.Available(context) && !present.Contains(module.Id))
            .ToArray();
    }

    /// <summary>Whether the module's settings, as they are, change what the runtime plays.</summary>
    public static bool IsOn(ParticleModuleDefinition module, ParticleConfig emitter)
    {
        if (module.Required) return true;
        if (module.IsOnCheck is not null) return module.IsOnCheck(emitter);
        if (module.SwitchOffValues is null) return true;
        ParticleConfig off = emitter.CloneEmitter();
        module.SwitchOffValues(off);
        return module.Owns.Any(name => !SameSetting(name, emitter, off));
    }

    public static void Add(ParticleModuleContext context, string id)
    {
        ParticleModuleDefinition module = ParticleModuleCatalog.Find(id);
        if (!module.Available(context)) throw new InvalidOperationException($"{module.Title} does not apply to this emitter.");
        if (Contains(context, id)) { SetEnabled(context, id, true); return; }
        HashSet<string> off = SwitchedOffIds(context);
        off.Remove(module.Id);
        Remember(context);
        SwitchOn(context, module, Ensure(context.Emitter, module.Id));
        Settle(context, off);
    }

    public static void Remove(ParticleModuleContext context, string id)
    {
        ParticleModuleDefinition module = ParticleModuleCatalog.Find(id);
        if (module.Required) throw new InvalidOperationException($"Every emitter needs {module.Title}; it cannot be removed.");
        HashSet<string> off = SwitchedOffIds(context);
        Remember(context);
        if (IsOn(module, context.Emitter)) module.SwitchOffValues?.Invoke(context.Emitter);
        context.Emitter.Modules!.RemoveAll(state => string.Equals(state.Id, module.Id, StringComparison.OrdinalIgnoreCase));
        Settle(context, off);
    }

    public static void SetEnabled(ParticleModuleContext context, string id, bool enabled)
    {
        ParticleModuleDefinition module = ParticleModuleCatalog.Find(id);
        if (module.Required)
        {
            if (enabled) return;
            throw new InvalidOperationException($"Every emitter needs {module.Title}; it cannot be switched off.");
        }
        if (!Contains(context, id))
        {
            if (enabled) Add(context, id);
            return;
        }
        if (IsEnabled(context, id) == enabled) return;
        HashSet<string> off = SwitchedOffIds(context);
        Remember(context);
        ParticleModuleState state = Ensure(context.Emitter, module.Id);
        if (enabled)
        {
            SwitchOn(context, module, state);
            off.Remove(module.Id);
        }
        else
        {
            SwitchOff(context.Emitter, module, state);
            off.Add(module.Id);
        }
        Settle(context, off);
    }

    /// <summary>Gives the module its first values back (and switches it on).</summary>
    public static void Reset(ParticleModuleContext context, string id)
    {
        ParticleModuleDefinition module = ParticleModuleCatalog.Find(id);
        HashSet<string> off = SwitchedOffIds(context);
        off.Remove(module.Id);
        Remember(context);
        ParticleConfig defaults = new();
        foreach (string name in module.Owns)
            if (Settings.TryGetValue(name, out PropertyInfo? property))
                property.SetValue(context.Emitter, Copy(property, property.GetValue(defaults)));
        if (!module.Required)
        {
            ParticleModuleState state = Ensure(context.Emitter, module.Id);
            state.Stash = null;
            state.Enabled = true;
            foreach (string excluded in module.Excludes)
                if (IsEnabled(context, excluded)) SetEnabled(context, excluded, false);
            module.StarterValues?.Invoke(context.Emitter);
        }
        module.SwitchOnValues?.Invoke(context.Emitter);
        Settle(context, off);
    }

    /// <summary>Changes one field of a module that is switched on.</summary>
    public static void SetField(ParticleModuleContext context, string id, string key, object? value)
    {
        ParticleModuleDefinition module = ParticleModuleCatalog.Find(id);
        ParticleModuleField field = module.Field(key);
        if (!IsEnabled(context, id)) throw new InvalidOperationException($"Switch {module.Title} on to change it.");
        HashSet<string> off = SwitchedOffIds(context);
        Remember(context);
        field.Write(context, value);
        Settle(context, off);
    }

    /// <summary>Replaces the points of a switched-on module's curve, making it a Points curve.</summary>
    public static void SetCurvePoints(ParticleModuleContext context, string id, string key, IEnumerable<ParticleCurveKey> points)
    {
        ParticleModuleDefinition module = ParticleModuleCatalog.Find(id);
        ParticleModuleField field = module.Field(key);
        if (field.Kind != ParticleModuleFieldKind.Curve) throw new ArgumentException(field.Label + " is not a curve.", nameof(key));
        List<ParticleCurveKey> copy = points.Where(point => point is not null).Select(point => point.Clone()).ToList();
        if (copy.Count is < 2 or > ParticleModuleCurves.MaximumKeys)
            throw new ArgumentException($"A curve needs 2 to {ParticleModuleCurves.MaximumKeys} points.", nameof(points));
        if (copy.Any(point => !double.IsFinite(point.Time) || !double.IsFinite(point.Value)))
            throw new FormatException("Curve points need finite times and values.");
        if (!IsEnabled(context, id)) throw new InvalidOperationException($"Switch {module.Title} on to change it.");
        HashSet<string> off = SwitchedOffIds(context);
        Remember(context);
        List<ParticleCurveKey> target = ParticleModuleCurves.EnsureKeys(context.Emitter, field.Channel);
        target.Clear();
        target.AddRange(copy);
        ParticleModuleCurves.Normalise(target, field.Channel);
        Settle(context, off);
    }

    /// <summary>
    /// After a change made somewhere else (a key dragged in a curve, a gradient recoloured), keeps
    /// the switched-off modules off: an end size that follows the start size, a gradient that
    /// stays the start colour.
    /// </summary>
    public static void Settle(ParticleModuleContext context, IReadOnlySet<string> switchedOff)
    {
        foreach (ParticleModuleDefinition module in ParticleModuleCatalog.All)
            if (module.FollowsOthers && module.SwitchOffValues is not null && switchedOff.Contains(module.Id))
                module.SwitchOffValues(context.Emitter);
    }

    /// <summary>The modules that are not on now (switched off or not in the stack).</summary>
    public static HashSet<string> SwitchedOffIds(ParticleModuleContext context)
    {
        HashSet<string> on = Entries(context).Where(entry => entry.Enabled)
            .Select(entry => entry.Definition.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ParticleModuleCatalog.All.Where(module => !on.Contains(module.Id))
            .Select(module => module.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A copy of the emitter's runtime settings without the stack's own record, to compare what a game plays.</summary>
    public static ParticleConfig RuntimeSettings(ParticleConfig emitter)
    {
        ParticleConfig copy = emitter.CloneEmitter();
        copy.Modules = null;
        return copy;
    }

    private static void SwitchOn(ParticleModuleContext context, ParticleModuleDefinition module, ParticleModuleState state)
    {
        foreach (string excluded in module.Excludes)
            if (IsEnabled(context, excluded)) SetEnabled(context, excluded, false);
        bool wasOn = IsOn(module, context.Emitter);
        if (state.Stash is { Count: > 0 } stash)
        {
            foreach ((string name, JsonElement value) in stash)
                if (Settings.TryGetValue(name, out PropertyInfo? property))
                    property.SetValue(context.Emitter, value.Deserialize(property.PropertyType, StashJson));
        }
        else if (!wasOn) module.StarterValues?.Invoke(context.Emitter);
        module.SwitchOnValues?.Invoke(context.Emitter);
        state.Stash = null;
        state.Enabled = true;
    }

    private static void SwitchOff(ParticleConfig emitter, ParticleModuleDefinition module, ParticleModuleState state)
    {
        Dictionary<string, JsonElement> stash = new(StringComparer.Ordinal);
        foreach (string name in module.Owns)
            if (Settings.TryGetValue(name, out PropertyInfo? property))
                stash[property.Name] = JsonSerializer.SerializeToElement(property.GetValue(emitter), property.PropertyType, StashJson);
        state.Stash = stash;
        state.Enabled = false;
        module.SwitchOffValues?.Invoke(emitter);
    }

    /// <summary>Writes the stack as it is shown into the emitter, once, so it stays as the user left it.</summary>
    private static void Remember(ParticleModuleContext context)
    {
        IReadOnlyList<ParticleModuleEntry> entries = Entries(context);
        List<ParticleModuleState> states = context.Emitter.Modules ??= [];
        states.RemoveAll(state => state is null);
        foreach (ParticleModuleEntry entry in entries)
        {
            if (entry.Definition.Required) continue;
            ParticleModuleState state = Ensure(context.Emitter, entry.Definition.Id);
            if (entry.Enabled && !state.Enabled)
            {
                // Its settings were changed elsewhere: they are the module's now.
                state.Enabled = true;
                state.Stash = null;
            }
        }
    }

    private static ParticleModuleState? State(ParticleConfig emitter, string id) =>
        emitter.Modules?.FirstOrDefault(state => state is not null && string.Equals(state.Id, id, StringComparison.OrdinalIgnoreCase));

    private static ParticleModuleState Ensure(ParticleConfig emitter, string id)
    {
        if (State(emitter, id) is { } existing) return existing;
        ParticleModuleState created = new() { Id = id, Enabled = true };
        (emitter.Modules ??= []).Add(created);
        return created;
    }

    private static bool SameSetting(string name, ParticleConfig left, ParticleConfig right)
    {
        if (!Settings.TryGetValue(name, out PropertyInfo? property)) return true;
        return JsonSerializer.Serialize(property.GetValue(left), property.PropertyType, StashJson)
            == JsonSerializer.Serialize(property.GetValue(right), property.PropertyType, StashJson);
    }

    private static object? Copy(PropertyInfo property, object? value) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(value, property.PropertyType, StashJson), property.PropertyType, StashJson);
}
