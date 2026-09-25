using Genesis.Runtime.Particles;

namespace Genesis.Application.Core.Editing.Particles;

/// <summary>
/// Structural operations on the runtime particle schema. The first emitter is stored in the
/// effect itself; promotion/reordering must never promote its preview settings or lose identity.
/// This service is UI-independent. Callers journal the returned, detached document as one edit.
/// </summary>
public static class ParticleEffectEditing
{
    public const int MaximumEmitters = 64;
    public const int MaximumNameLength = 64;

    public static int Count(ParticleConfig effect) => effect.Emitters.Count + 1;
    public static ParticleConfig Emitter(ParticleConfig effect, int index)
    {
        CheckIndex(effect, index);
        return index == 0 ? effect : effect.Emitters[index - 1].Config;
    }
    public static string Id(ParticleConfig effect, int index) => index == 0
        ? effect.EmitterId : effect.Emitters[index - 1].Id;
    public static string Name(ParticleConfig effect, int index) => index == 0
        ? effect.EmitterName : effect.Emitters[index - 1].Name;
    public static bool Enabled(ParticleConfig effect, int index) => index == 0
        ? effect.EmitterEnabled : effect.Emitters[index - 1].Enabled;

    public static void SetEnabled(ParticleConfig effect, int index, bool enabled)
    {
        Emitter(effect, index).EmitterEnabled = enabled;
        if (index > 0) effect.Emitters[index - 1].Enabled = enabled;
    }

    public static void Rename(ParticleConfig effect, int index, string name)
    {
        CheckIndex(effect, index);
        name = ValidateName(name);
        for (int i = 0; i < Count(effect); i++)
            if (i != index && string.Equals(Name(effect, i), name, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Another emitter in this effect already uses that name.", nameof(name));
        Emitter(effect, index).EmitterName = name;
        if (index > 0) effect.Emitters[index - 1].Name = name;
    }

    public static ParticleConfig Add(ParticleConfig effect, ParticleConfig template, string name, out int selected)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (Count(effect) >= MaximumEmitters)
            throw new InvalidOperationException($"An authored effect supports up to {MaximumEmitters} emitters.");
        List<ParticleEmitterLayer> emitters = Flatten(effect);
        string unique = UniqueName(emitters, name);
        emitters.Add(NewLayer(template, unique));
        selected = emitters.Count - 1;
        return Pack(effect, emitters);
    }

    public static ParticleConfig Duplicate(ParticleConfig effect, int index, out int selected)
    {
        CheckIndex(effect, index);
        if (Count(effect) >= MaximumEmitters)
            throw new InvalidOperationException($"An authored effect supports up to {MaximumEmitters} emitters.");
        List<ParticleEmitterLayer> emitters = Flatten(effect);
        ParticleEmitterLayer source = emitters[index];
        ParticleEmitterLayer copy = NewLayer(source.Config, UniqueName(emitters, source.Name));
        copy.Enabled = source.Enabled;
        copy.Config.EmitterEnabled = source.Enabled;
        selected = index + 1;
        emitters.Insert(selected, copy);
        return Pack(effect, emitters);
    }

    public static ParticleConfig Remove(ParticleConfig effect, int index, out int selected)
    {
        CheckIndex(effect, index);
        if (Count(effect) == 1)
            throw new InvalidOperationException("Keep at least one emitter. Untick it to preview an empty effect.");
        List<ParticleEmitterLayer> emitters = Flatten(effect);
        emitters.RemoveAt(index);
        selected = Math.Min(index, emitters.Count - 1);
        return Pack(effect, emitters);
    }

    public static ParticleConfig Move(ParticleConfig effect, int index, int destination)
    {
        CheckIndex(effect, index); CheckIndex(effect, destination);
        List<ParticleEmitterLayer> emitters = Flatten(effect);
        ParticleEmitterLayer layer = emitters[index];
        emitters.RemoveAt(index);
        emitters.Insert(destination, layer);
        return Pack(effect, emitters);
    }

    /// <summary>Replace emitter parameters while retaining the effect envelope and stack identity.</summary>
    public static ParticleConfig Replace(ParticleConfig effect, int index, ParticleConfig parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        CheckIndex(effect, index);
        List<ParticleEmitterLayer> emitters = Flatten(effect);
        emitters[index].Config = parameters.CloneEmitter();
        return Pack(effect, emitters);
    }

    private static List<ParticleEmitterLayer> Flatten(ParticleConfig effect)
    {
        List<ParticleEmitterLayer> result =
        [new() { Id = effect.EmitterId, Name = effect.EmitterName, Enabled = effect.EmitterEnabled, Config = effect.CloneEmitter() }];
        result.AddRange(effect.Emitters.Select(layer => layer.Clone()));
        return result;
    }

    private static ParticleConfig Pack(ParticleConfig envelope, List<ParticleEmitterLayer> layers)
    {
        foreach (ParticleEmitterLayer layer in layers)
        {
            layer.Config.EmitterId = layer.Id;
            layer.Config.EmitterName = layer.Name;
            layer.Config.EmitterEnabled = layer.Enabled;
            layer.Config.Emitters.Clear();
        }
        ParticleConfig result = layers[0].Config;
        result.Emitters = layers.Skip(1).ToList();
        // These belong to the effect, never to whichever emitter happens to be first today.
        result.EffectName = envelope.EffectName;
        result.Duration = envelope.Duration;
        result.Light = envelope.Light.Clone();
        result.Preview2D = envelope.Preview2D;
        result.PreviewTargetType = envelope.PreviewTargetType;
        result.PreviewTargetAsset = envelope.PreviewTargetAsset;
        result.BackdropSprite = envelope.BackdropSprite;
        result.Notes = envelope.Notes;
        result.Script = envelope.Script;
        return result;
    }

    private static ParticleEmitterLayer NewLayer(ParticleConfig source, string name)
    {
        string id = Guid.NewGuid().ToString("N");
        ParticleConfig copy = source.CloneEmitter();
        copy.EmitterId = id; copy.EmitterName = name;
        return new ParticleEmitterLayer { Id = id, Name = name, Enabled = copy.EmitterEnabled, Config = copy };
    }

    private static string UniqueName(List<ParticleEmitterLayer> layers, string proposed)
    {
        string stem = ValidateName(proposed);
        HashSet<string> used = new(layers.Select(layer => layer.Name), StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(stem)) return stem;
        for (int suffix = 2; ; suffix++)
        {
            string tail = " " + suffix;
            string candidate = stem[..Math.Min(stem.Length, MaximumNameLength - tail.Length)] + tail;
            if (!used.Contains(candidate)) return candidate;
        }
    }

    private static string ValidateName(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length is 0 or > MaximumNameLength || name.Any(char.IsControl))
            throw new ArgumentException($"Use an emitter name of 1–{MaximumNameLength} characters, without line breaks.", nameof(name));
        return name;
    }

    private static void CheckIndex(ParticleConfig effect, int index)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (index < 0 || index >= Count(effect)) throw new ArgumentOutOfRangeException(nameof(index));
    }
}
