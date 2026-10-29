using System;
using System.Numerics;

namespace Genesis.Runtime.Climate;

/// <summary>A colour a game gives an hour of the day (0 to 24), as picked (0 to 1 each).</summary>
public readonly record struct HourColor(float Hour, Vector3 Color);

/// <summary>
/// Colours by the hour of the day that a game sets for the climate's sun and fog
/// (AtmosphereOptions.SunColorHours, FogColorHours): between two hours the colour blends, round
/// the clock (after the last hour it blends towards the first). None set leaves the climate's own.
/// </summary>
public static class HourColors
{
    /// <summary>The keys with <paramref name="color"/> at <paramref name="hour"/>, replacing one already at that hour; ordered by hour.</summary>
    public static HourColor[] Set(HourColor[] keys, float hour, Vector3 color)
    {
        keys ??= [];
        hour = Wrap(hour);
        var list = new System.Collections.Generic.List<HourColor>(keys.Length + 1);
        foreach (HourColor key in keys)
            if (MathF.Abs(key.Hour - hour) > 1e-4f) list.Add(key);
        list.Add(new HourColor(hour, Vector3.Clamp(color, Vector3.Zero, new Vector3(4f))));
        list.Sort((a, b) => a.Hour.CompareTo(b.Hour));
        return [.. list];
    }

    /// <summary>The colour at <paramref name="hour"/>, blended between the keys either side; false with no keys.</summary>
    public static bool TryEvaluate(HourColor[] keys, float hour, out Vector3 color)
    {
        color = default;
        if (keys == null || keys.Length == 0 || !float.IsFinite(hour)) return false;
        if (keys.Length == 1) { color = keys[0].Color; return true; }
        hour = Wrap(hour);
        // The last key at or before the hour, round the clock.
        int after = 0;
        while (after < keys.Length && keys[after].Hour <= hour) after++;
        HourColor next = keys[after % keys.Length];
        HourColor previous = keys[(after - 1 + keys.Length) % keys.Length];
        float span = next.Hour - previous.Hour;
        if (span <= 0f) span += 24f;
        float into = hour - previous.Hour;
        if (into < 0f) into += 24f;
        color = Vector3.Lerp(previous.Color, next.Color, span > 1e-4f ? Math.Clamp(into / span, 0f, 1f) : 0f);
        return true;
    }

    private static float Wrap(float hour)
    {
        if (!float.IsFinite(hour)) return 0f;
        hour %= 24f;
        return hour < 0f ? hour + 24f : hour;
    }
}
