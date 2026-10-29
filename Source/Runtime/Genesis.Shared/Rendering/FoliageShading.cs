using System;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Shared.Rendering;

/// <summary>
/// The grass shader's settings as a <see cref="MeshDrawFlags.Foliage"/> draw carries them. A foliage
/// draw has no material maps, so two of <see cref="MeshDrawCall"/>'s material vectors hold them
/// instead; left at zero, the draw looks and sways as foliage always has.
/// <list type="bullet">
/// <item><see cref="MeshDrawCall.SubsurfaceColorSteps"/>: the colour ramp. x the root colour and y
/// the tip colour (8 bits a channel, <see cref="PackColor"/>), z the curve, w 1 when on. The blades
/// are then coloured from root to tip by their UV (v 1 at the root, 0 at the tip) instead of by
/// their vertex colours; the instance tint still varies each tuft.</item>
/// <item><see cref="MeshDrawCall.SurfaceParams"/>: the wind. x strength, y speed, z gusts, w 1 when
/// set (strength 0 holds the blades still).</item>
/// </list>
/// The forward shader reads them only on foliage draws (ForwardShaders.cs, the Foliage branches).
/// </summary>
public static class FoliageShading
{
    /// <summary>A colour as one exactly representable float: 8 bits each of red, green and blue.</summary>
    public static float PackColor(Vector3 color)
    {
        static uint Channel(float value) => (uint)MathF.Round(Math.Clamp(float.IsFinite(value) ? value : 0f, 0f, 1f) * 255f);
        return Channel(color.X) | Channel(color.Y) << 8 | Channel(color.Z) << 16;
    }

    /// <summary>The colour <see cref="PackColor"/> stored, to 1/255.</summary>
    public static Vector3 UnpackColor(float packed)
    {
        uint value = (uint)MathF.Max(0f, MathF.Round(packed));
        return new Vector3(value & 255u, (value >> 8) & 255u, (value >> 16) & 255u) / 255f;
    }

    /// <summary>The colour ramp for <see cref="MeshDrawCall.SubsurfaceColorSteps"/>.</summary>
    public static Vector4 ColorRamp(Vector3 root, Vector3 tip, float curve) =>
        new(PackColor(root), PackColor(tip), Math.Clamp(float.IsFinite(curve) ? curve : 1f, 0.1f, 8f), 1f);

    /// <summary>The wind for <see cref="MeshDrawCall.SurfaceParams"/>.</summary>
    public static Vector4 Wind(float strength, float speed, float gusts) =>
        new(Clamp(strength, 0f, 4f, 1f), Clamp(speed, 0f, 4f, 1f), Clamp(gusts, 0f, 1f, 0f), 1f);

    /// <summary>The ramp's colour at height <paramref name="t"/> up a blade, 0 at the root and 1 at the tip.</summary>
    public static Vector3 RampAt(Vector3 root, Vector3 tip, float curve, float t) =>
        Vector3.Lerp(root, tip, MathF.Pow(Math.Clamp(t, 0f, 1f), Math.Clamp(curve, 0.1f, 8f)));

    private static float Clamp(float value, float minimum, float maximum, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
