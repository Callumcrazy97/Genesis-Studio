using System;
using System.Numerics;
using Genesis.Rendering.Lights;

namespace Genesis.Rendering.Primitives;

/// <summary>
/// AF1.3 bounded omnidirectional-shadow budget and staggered face schedule.
/// GPU path uses six separate 512² depth RTs for slot 0 (not TextureCube).
/// </summary>
public static class OmniShadowMath
{
    /// <summary>Default: one cubemap, never a map per local light.</summary>
    public const int DefaultBudget = 1;
    public const int MinBudget = 0;
    public const int MaxBudget = 4;
    /// <summary>Faces refreshed on a staggered schedule after the cubemap is warm.</summary>
    public const int SteadyFacesPerFrame = 3;
    /// <summary>All six faces on the first update.</summary>
    public const int InitialFacesPerFrame = 6;
    public const float DefaultNearPlane = 0.08f;
    public const float DefaultFarPlane = 31f;

    /// <summary>Clamp an authored omni-shadow budget to the AF1.3 range.</summary>
    public static int ClampBudget(int budget) => Math.Clamp(budget, MinBudget, MaxBudget);

    /// <summary>
    /// Same camera weight as <see cref="TiledLightGrid.SelectStrongest"/>:
    /// intensity × radius² / (1 + dist²).
    /// </summary>
    public static float CameraWeight(in ClusterPointLightGpu light, Vector3 cameraPos)
    {
        float radius = MathF.Max(light.PosRadius.W, 0.001f);
        float intensity = MathF.Max(light.ColorIntensity.W, 0f);
        Vector3 pos = new(light.PosRadius.X, light.PosRadius.Y, light.PosRadius.Z);
        float distSq = Vector3.DistanceSquared(pos, cameraPos);
        return intensity * radius * radius / (1f + distSq);
    }

    /// <summary>
    /// Pick up to <paramref name="budget"/> light indices for omni cubemap slots, strongest first.
    /// Does not mutate <paramref name="lights"/>.
    /// </summary>
    public static int SelectSlots(
        ReadOnlySpan<ClusterPointLightGpu> lights,
        int budget,
        Vector3 cameraPos,
        Span<int> slotIndices)
    {
        int cap = ClampBudget(budget);
        if (cap <= 0 || lights.IsEmpty || slotIndices.IsEmpty)
            return 0;

        int outCap = Math.Min(cap, Math.Min(lights.Length, slotIndices.Length));
        // Partial selection into a small scratch of indices (budget ≤ 4).
        Span<int> order = stackalloc int[lights.Length];
        for (int i = 0; i < lights.Length; i++)
            order[i] = i;

        for (int i = 0; i < outCap; i++)
        {
            int best = i;
            float bestW = CameraWeight(lights[order[i]], cameraPos);
            for (int j = i + 1; j < lights.Length; j++)
            {
                float w = CameraWeight(lights[order[j]], cameraPos);
                if (w > bestW)
                {
                    bestW = w;
                    best = j;
                }
            }

            (order[i], order[best]) = (order[best], order[i]);
            slotIndices[i] = order[i];
        }

        return outCap;
    }

    /// <summary>
    /// ForestLight incremental face schedule: 6 faces on first update, then 3/frame.
    /// Returns how many faces to draw this frame and advances the cursor.
    /// </summary>
    public static int NextFaceBatch(bool initialised, ref int faceCursor, Span<int> facesOut)
    {
        int count = initialised ? SteadyFacesPerFrame : InitialFacesPerFrame;
        count = Math.Min(count, facesOut.Length);
        int cursor = ((faceCursor % 6) + 6) % 6;
        for (int i = 0; i < count; i++)
            facesOut[i] = (cursor + i) % 6;
        faceCursor = (cursor + count) % 6;
        return count;
    }

    /// <summary>Cube face look direction (+X,-X,+Y,-Y,+Z,-Z).</summary>
    public static Vector3 FaceDirection(int face) => face switch
    {
        0 => Vector3.UnitX,
        1 => -Vector3.UnitX,
        2 => Vector3.UnitY,
        3 => -Vector3.UnitY,
        4 => Vector3.UnitZ,
        _ => -Vector3.UnitZ,
    };

    /// <summary>Cube-face up vector used by the omnidirectional shadow maps.</summary>
    public static Vector3 FaceUp(int face) => face switch
    {
        0 => -Vector3.UnitY,
        1 => -Vector3.UnitY,
        2 => Vector3.UnitZ,
        3 => -Vector3.UnitZ,
        4 => -Vector3.UnitY,
        _ => -Vector3.UnitY,
    };
}
