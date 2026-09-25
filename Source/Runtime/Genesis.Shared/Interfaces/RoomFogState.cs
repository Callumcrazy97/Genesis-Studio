using System;
using System.Numerics;

namespace Genesis.Shared.Interfaces;

/// <summary>
/// Backend-neutral Room fog state for 2D/world-layer rendering. In a 2D Room the authored
/// Z/layer depth is the fog depth axis: larger depth values are farther behind the scene.
/// The same engine fog settings are used by 3D rendering, where depth is camera/world distance.
/// </summary>
public readonly record struct RoomFogState(
    bool Enabled,
    Vector4 Color,
    float Depth,
    float Thickness)
{
    public static RoomFogState Disabled => new(false, new Vector4(0f, 0f, 0f, 0f), 0f, 1f);

    public float Alpha => Math.Clamp(Color.W, 0f, 1f);
    public float EndDepth => Depth + Math.Max(0.001f, Thickness);

    public static RoomFogState Create(bool enabled, Vector4 color, float start, float end)
        => new(enabled, color, start, Math.Max(0.001f, end - start));

    public static RoomFogState CreateDepthThickness(
        bool enabled,
        Vector4 color,
        float depth,
        float thickness,
        float alpha)
        => new(
            enabled,
            new Vector4(color.X, color.Y, color.Z, Math.Clamp(alpha, 0f, 1f)),
            depth,
            Math.Max(0.001f, thickness));

    public float EvaluateDepth(float drawDepth)
    {
        if (!Enabled || Alpha <= 0f) return 0f;
        float t = Math.Clamp((drawDepth - Depth) / Math.Max(0.001f, Thickness), 0f, 1f);
        return t * Alpha;
    }

    /// <summary>2D clear/background colour is behind the Room and receives the maximum fog blend.</summary>
    public Vector4 ApplyToBackground(Vector4 background)
    {
        if (!Enabled || Alpha <= 0f) return background;
        float a = Alpha;
        return new Vector4(
            background.X + (Color.X - background.X) * a,
            background.Y + (Color.Y - background.Y) * a,
            background.Z + (Color.Z - background.Z) * a,
            background.W);
    }
}
