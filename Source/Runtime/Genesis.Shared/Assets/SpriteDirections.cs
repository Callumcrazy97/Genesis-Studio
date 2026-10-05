using System;

namespace Genesis.Shared.Assets;

/// <summary>
/// How a directional sprite's frames are laid out: <see cref="Count"/> facing angles, each with
/// <see cref="FramesPerDirection"/> animation frames, ordered direction-major (all frames of
/// direction 0, then all frames of direction 1, ...).
/// </summary>
/// <remarks>
/// Direction <c>d</c> shows the model facing <c>StartAngleDegrees + d * 360 / Count</c> degrees,
/// where 0 faces +X (screen right) and angles grow counter-clockwise (90 faces up the screen),
/// the same convention as <c>PointDirection</c>.
/// </remarks>
public sealed class SpriteRuntimeDirections
{
    public int Count { get; set; }

    public int FramesPerDirection { get; set; } = 1;

    public double StartAngleDegrees { get; set; }
}

/// <summary>Frame selection for directional sprites, shared by the editor and PGSL.</summary>
public static class SpriteDirections
{
    /// <summary>
    /// The direction whose angle is nearest <paramref name="angleDegrees"/>. Each direction covers
    /// half a step either side of its own angle; any finite angle (negative, or past 360) wraps.
    /// </summary>
    public static int DirectionIndex(int directions, double angleDegrees, double startAngleDegrees = 0)
    {
        if (directions <= 1 || !double.IsFinite(angleDegrees) || !double.IsFinite(startAngleDegrees)) return 0;
        double step = 360.0 / directions;
        double relative = (angleDegrees - startAngleDegrees) % 360.0;
        if (relative < 0) relative += 360.0;
        int index = (int)Math.Floor(relative / step + 0.5);
        return index % directions;
    }

    /// <summary>The direction-major frame index for an angle and an animation frame (which wraps).</summary>
    public static int FrameIndex(int directions, int framesPerDirection, double angleDegrees, double animationFrame, double startAngleDegrees = 0)
    {
        int perDirection = Math.Max(1, framesPerDirection);
        int frame = 0;
        if (double.IsFinite(animationFrame))
        {
            frame = (int)(Math.Floor(animationFrame) % perDirection);
            if (frame < 0) frame += perDirection;
        }
        return DirectionIndex(Math.Max(1, directions), angleDegrees, startAngleDegrees) * perDirection + frame;
    }

    /// <summary>
    /// The layout of an image: its saved direction metadata, or, for an image without it, one
    /// direction per frame so hand-drawn sheets ordered by angle also work.
    /// </summary>
    public static SpriteRuntimeDirections Resolve(SpriteRuntimeAsset asset)
    {
        if (asset == null || asset.Frames.Count == 0) return null;
        SpriteRuntimeDirections saved = asset.Usage?.Directions;
        if (saved is { Count: > 0 })
        {
            int perDirection = Math.Max(1, saved.FramesPerDirection);
            int count = Math.Max(1, Math.Min(saved.Count, asset.Frames.Count / perDirection));
            return new SpriteRuntimeDirections { Count = count, FramesPerDirection = perDirection, StartAngleDegrees = saved.StartAngleDegrees };
        }
        return new SpriteRuntimeDirections { Count = asset.Frames.Count, FramesPerDirection = 1 };
    }
}
