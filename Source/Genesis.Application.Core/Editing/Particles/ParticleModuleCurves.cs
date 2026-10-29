using Genesis.Runtime.Particles;

namespace Genesis.Application.Core.Editing.Particles;

/// <summary>The four lifetime curves the runtime samples for every emitter.</summary>
public enum ParticleCurveChannel
{
    /// <summary>0 is the start size and 1 the end size.</summary>
    Size,
    /// <summary>Multiplies each particle's speed.</summary>
    Speed,
    /// <summary>Where in the gradient's opacity a particle is: 0 the first key's, 1 the last key's.</summary>
    Alpha,
    /// <summary>Multiplies how far a particle moves.</summary>
    Velocity,
}

/// <summary>
/// A lifetime curve as the module stack offers it: one of the runtime's standard eases, the older
/// two-handle Bezier, or points with straight or smooth joins (<see cref="ParticleBezierCurve.Keys"/>).
/// </summary>
public static class ParticleModuleCurves
{
    public const string Points = "Points";
    public const string Bezier = "Bezier";
    public const int MaximumKeys = 32;

    private static readonly (ParticleInterpolationCurve Curve, string Label)[] Eases =
    [
        (ParticleInterpolationCurve.Linear, "Linear"),
        (ParticleInterpolationCurve.EaseIn, "Ease in"),
        (ParticleInterpolationCurve.EaseOut, "Ease out"),
        (ParticleInterpolationCurve.SmoothStep, "Smooth step"),
    ];

    /// <summary>The choices offered for a channel; the Bezier is offered only to a curve that already uses it.</summary>
    public static IReadOnlyList<string> ModesFor(ParticleCurveChannel channel) => HasEases(channel)
        ? [.. Eases.Select(ease => ease.Label), Points]
        : [Points];

    public static IReadOnlyList<string> ModesFor(ParticleConfig config, ParticleCurveChannel channel) =>
        Mode(config, channel) == Bezier ? [.. ModesFor(channel), Bezier] : ModesFor(channel);

    public static bool HasEases(ParticleCurveChannel channel) => channel is ParticleCurveChannel.Size or ParticleCurveChannel.Alpha;

    public static string Describe(ParticleCurveChannel channel) => channel switch
    {
        ParticleCurveChannel.Size => "0 is the start size, 1 the end size.",
        ParticleCurveChannel.Alpha => "0 is the first key's opacity, 1 the last key's.",
        ParticleCurveChannel.Speed => "1 keeps the launch speed; 0 stops the particle.",
        _ => "1 moves at full speed; 0 holds the particle still.",
    };

    /// <summary>The range the curve editor draws; the runtime accepts more (it clamps to ±16).</summary>
    public static (double Minimum, double Maximum) ValueRange(ParticleCurveChannel channel) =>
        channel is ParticleCurveChannel.Speed or ParticleCurveChannel.Velocity ? (0, 2) : (0, 1);

    public static ParticleBezierCurve Curve(ParticleConfig config, ParticleCurveChannel channel)
    {
        ParticleBezierCurve curve = channel switch
        {
            ParticleCurveChannel.Size => config.SizeOverLifetime ??= new ParticleBezierCurve(),
            ParticleCurveChannel.Speed => config.SpeedOverLifetime ??= new ParticleBezierCurve(),
            ParticleCurveChannel.Alpha => config.AlphaOverLifetime ??= new ParticleBezierCurve(),
            _ => config.VelocityOverLifetime ??= new ParticleBezierCurve(),
        };
        return curve;
    }

    public static bool IsCustom(ParticleConfig config, ParticleCurveChannel channel) => channel switch
    {
        ParticleCurveChannel.Size => config.UseCustomSizeCurve,
        ParticleCurveChannel.Speed => config.UseCustomSpeedCurve,
        ParticleCurveChannel.Alpha => config.UseCustomAlphaCurve,
        _ => config.UseCustomVelocityCurve,
    };

    private static void SetCustom(ParticleConfig config, ParticleCurveChannel channel, bool custom)
    {
        switch (channel)
        {
            case ParticleCurveChannel.Size: config.UseCustomSizeCurve = custom; break;
            case ParticleCurveChannel.Speed: config.UseCustomSpeedCurve = custom; break;
            case ParticleCurveChannel.Alpha: config.UseCustomAlphaCurve = custom; break;
            default: config.UseCustomVelocityCurve = custom; break;
        }
    }

    public static string Mode(ParticleConfig config, ParticleCurveChannel channel)
    {
        if (IsCustom(config, channel)) return Curve(config, channel).Keys is { Count: > 0 } ? Points : Bezier;
        if (!HasEases(channel)) return Points;
        ParticleInterpolationCurve ease = channel == ParticleCurveChannel.Size ? config.SizeCurve : config.AlphaCurve;
        return Eases.FirstOrDefault(item => item.Curve == ease).Label ?? "Linear";
    }

    public static void SetMode(ParticleConfig config, ParticleCurveChannel channel, string mode)
    {
        if (string.Equals(mode, Points, StringComparison.OrdinalIgnoreCase))
        {
            EnsureKeys(config, channel);
            return;
        }
        if (string.Equals(mode, Bezier, StringComparison.OrdinalIgnoreCase))
        {
            Curve(config, channel).Keys = null;
            SetCustom(config, channel, true);
            return;
        }
        foreach ((ParticleInterpolationCurve ease, string label) in Eases)
        {
            if (!HasEases(channel) || !string.Equals(label, mode, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(ease.ToString(), mode, StringComparison.OrdinalIgnoreCase)) continue;
            SetCustom(config, channel, false);
            if (channel == ParticleCurveChannel.Size) config.SizeCurve = ease;
            else config.AlphaCurve = ease;
            return;
        }
        throw new FormatException($"A {channel} curve cannot be '{mode}'. Choose {string.Join(", ", ModesFor(channel))}.");
    }

    /// <summary>What the runtime samples for this channel at a normalised age.</summary>
    public static float Evaluate(ParticleConfig config, ParticleCurveChannel channel, float time)
    {
        if (IsCustom(config, channel)) return Curve(config, channel).Evaluate(time);
        return channel switch
        {
            ParticleCurveChannel.Size => ParticleCurveMath.Evaluate(config.SizeCurve, time),
            ParticleCurveChannel.Alpha => ParticleCurveMath.Evaluate(config.AlphaCurve, time),
            _ => 1f,
        };
    }

    /// <summary>
    /// The curve as points, switched on. A curve that was an ease or a Bezier becomes points that
    /// follow the same shape (two straight points for Linear, five smooth ones otherwise).
    /// </summary>
    public static List<ParticleCurveKey> EnsureKeys(ParticleConfig config, ParticleCurveChannel channel)
    {
        ParticleBezierCurve curve = Curve(config, channel);
        if (!IsCustom(config, channel) || curve.Keys is not { Count: > 0 })
        {
            bool straight = Math.Abs(Evaluate(config, channel, 0.5f) - (Evaluate(config, channel, 0f) + Evaluate(config, channel, 1f)) * 0.5f) < 1e-4
                && Math.Abs(Evaluate(config, channel, 0.25f) - (Evaluate(config, channel, 0f) * 0.75f + Evaluate(config, channel, 1f) * 0.25f)) < 1e-4;
            float[] times = straight ? [0f, 1f] : [0f, 0.25f, 0.5f, 0.75f, 1f];
            List<ParticleCurveKey> keys = times.Select(time => new ParticleCurveKey
            {
                Time = time,
                Value = Math.Round(Evaluate(config, channel, time), 4),
                Smooth = !straight,
            }).ToList();
            curve.Keys = keys;
            SetCustom(config, channel, true);
        }
        return curve.Keys!;
    }

    /// <summary>Puts points back in time order inside the curve's range; at least two, at most <see cref="MaximumKeys"/>.</summary>
    public static void Normalise(List<ParticleCurveKey> keys, ParticleCurveChannel channel)
    {
        (double minimum, double maximum) = ValueRange(channel);
        keys.RemoveAll(key => key is null);
        if (keys.Count > MaximumKeys) keys.RemoveRange(MaximumKeys, keys.Count - MaximumKeys);
        foreach (ParticleCurveKey key in keys)
        {
            key.Time = double.IsFinite(key.Time) ? Math.Clamp(key.Time, 0, 1) : 0;
            key.Value = double.IsFinite(key.Value) ? Math.Clamp(key.Value, minimum, maximum) : minimum;
        }
        List<ParticleCurveKey> ordered = keys.OrderBy(key => key.Time).ToList();
        keys.Clear();
        keys.AddRange(ordered);
    }
}
