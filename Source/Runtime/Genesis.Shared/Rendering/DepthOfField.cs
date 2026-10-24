using System;

namespace Genesis.Shared.Rendering;

/// <summary>
/// Depth of field (Engine.Camera.Focus.*): what is near the focus distance stays sharp, what is
/// nearer or farther blurs more the farther it is from it. Off unless a game turns it on; Auto
/// focuses on whatever is in the middle of the screen.
/// </summary>
public static class DepthOfField
{
    public const float DefaultDistance = 10f, DefaultRange = 4f, DefaultBlur = 8f;

    private static float _distance = DefaultDistance, _range = DefaultRange, _blur = DefaultBlur;

    /// <summary>Whether the 3D view is drawn with depth of field (false unless set).</summary>
    public static bool Enabled { get; set; }

    /// <summary>Focus on whatever is in the middle of the screen instead of <see cref="Distance"/> (false unless set).</summary>
    public static bool Auto { get; set; }

    /// <summary>How far from the camera things are sharpest, 0.1 to 100 000 metres (10 unless set).</summary>
    public static float Distance
    {
        get => _distance;
        set => _distance = float.IsFinite(value) ? Math.Clamp(value, 0.1f, 100_000f) : DefaultDistance;
    }

    /// <summary>How far either side of the focus distance stays sharp, 0 to 100 000 metres (4 unless set).</summary>
    public static float Range
    {
        get => _range;
        set => _range = float.IsFinite(value) ? Math.Clamp(value, 0f, 100_000f) : DefaultRange;
    }

    /// <summary>The largest blur, in pixels of a 1080-line picture, 0 to 32 (8 unless set).</summary>
    public static float Blur
    {
        get => _blur;
        set => _blur = float.IsFinite(value) ? Math.Clamp(value, 0f, 32f) : DefaultBlur;
    }

    /// <summary>Back to off, at the usual distance, range and blur (a new play session).</summary>
    public static void Reset()
    {
        Enabled = false;
        Auto = false;
        _distance = DefaultDistance;
        _range = DefaultRange;
        _blur = DefaultBlur;
    }
}
