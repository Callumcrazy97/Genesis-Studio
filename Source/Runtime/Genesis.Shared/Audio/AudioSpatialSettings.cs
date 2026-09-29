using System;

namespace Genesis.Shared.Audio;

public enum AudioFalloffCurve { Linear, Logarithmic }

/// <summary>Per-emitter distance settings. Overrides a playback channel, never the reusable sound.</summary>
public readonly record struct AudioSpatialSettings(float MinDistance, float MaxDistance, AudioFalloffCurve Curve)
{
    public float AttenuationAt(float distance)
    {
        float near = float.IsFinite(MinDistance) ? MathF.Max(0, MinDistance) : 0;
        float far = float.IsFinite(MaxDistance) ? MathF.Max(near + .001f, MaxDistance) : near + 1;
        if (distance <= near) return 1;
        if (!float.IsFinite(distance) || distance >= far) return 0;
        float fraction = (distance - near) / (far - near);
        return Curve == AudioFalloffCurve.Logarithmic ? 1 - MathF.Log10(1 + 9 * fraction) : 1 - fraction;
    }
}
