using System;
using System.Numerics;
using Genesis.Rendering.Lights;

namespace Genesis.Rendering.Primitives;

/// <summary>
/// AF1.3 bounded local-light shadow budget. The GPU path packs up to <see cref="MaxBudget"/>
/// shadowed point or spot lights into one depth atlas: a row of six 512² tiles per light (cube
/// faces), of which a spot light uses the first.
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
    /// <summary>Edge of one atlas tile in texels.</summary>
    public const int AtlasTileSize = 512;
    /// <summary>Tiles per atlas row: one per cube face.</summary>
    public const int AtlasColumns = 6;
    /// <summary>A light holding a slot keeps it unless a challenger outweighs it by this factor.</summary>
    public const float SlotHysteresis = 1.5f;
    /// <summary>Widest spot cone half-angle; the shadow projection must stay below 180°.</summary>
    public const float MaxSpotAngleDegrees = 85f;

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

    /// <summary>
    /// Stable slot assignment for the shadow atlas. <paramref name="previous"/> holds last frame's
    /// light per slot (xyz = position, w = radius; w &lt;= 0 = empty). A light still near its previous
    /// position keeps that slot and has its weight multiplied by <see cref="SlotHysteresis"/>, so the
    /// shadowing light does not flip as the camera walks between similar lanterns, and cached tiles
    /// stay with their light. Writes, per slot, the index into <paramref name="lights"/> or -1.
    /// </summary>
    public static void AssignSlots(
        ReadOnlySpan<ClusterPointLightGpu> lights,
        int budget,
        Vector3 cameraPos,
        ReadOnlySpan<Vector4> previous,
        Span<int> assignment)
    {
        assignment.Fill(-1);
        int cap = Math.Min(ClampBudget(budget), assignment.Length);
        if (cap <= 0 || lights.IsEmpty)
            return;

        Span<int> heldSlot = lights.Length <= 256 ? stackalloc int[lights.Length] : new int[lights.Length];
        Span<float> weight = lights.Length <= 256 ? stackalloc float[lights.Length] : new float[lights.Length];
        for (int i = 0; i < lights.Length; i++)
        {
            heldSlot[i] = -1;
            weight[i] = CameraWeight(lights[i], cameraPos);
        }

        for (int s = 0; s < cap && s < previous.Length; s++)
        {
            Vector4 held = previous[s];
            if (held.W <= 0f) continue;
            Vector3 heldPos = new(held.X, held.Y, held.Z);
            int best = -1;
            float bestDistSq = float.MaxValue;
            for (int i = 0; i < lights.Length; i++)
            {
                if (heldSlot[i] >= 0) continue;
                float radius = lights[i].PosRadius.W;
                float larger = MathF.Max(radius, held.W);
                if (MathF.Abs(radius - held.W) > 0.25f * larger) continue;
                float tolerance = MathF.Max(0.5f, 0.25f * larger);
                Vector3 position = new(lights[i].PosRadius.X, lights[i].PosRadius.Y, lights[i].PosRadius.Z);
                float distSq = Vector3.DistanceSquared(position, heldPos);
                if (distSq > tolerance * tolerance || distSq >= bestDistSq) continue;
                best = i;
                bestDistSq = distSq;
            }
            if (best < 0) continue;
            heldSlot[best] = s;
            weight[best] *= SlotHysteresis;
        }

        // The strongest `cap` lights by (hysteresis-adjusted) weight win a slot.
        Span<int> chosen = stackalloc int[cap];
        int chosenCount = 0;
        for (int pick = 0; pick < cap; pick++)
        {
            int best = -1;
            float bestWeight = 0f;
            for (int i = 0; i < lights.Length; i++)
            {
                if (weight[i] <= bestWeight || chosen[..chosenCount].Contains(i)) continue;
                best = i;
                bestWeight = weight[i];
            }
            if (best < 0) break;
            chosen[chosenCount++] = best;
        }

        // Continuing lights keep their slot; newcomers take the lowest free one.
        foreach (int i in chosen[..chosenCount])
            if (heldSlot[i] >= 0 && heldSlot[i] < cap)
                assignment[heldSlot[i]] = i;
        foreach (int i in chosen[..chosenCount])
        {
            if (heldSlot[i] >= 0 && heldSlot[i] < cap) continue;
            for (int s = 0; s < cap; s++)
            {
                if (assignment[s] >= 0) continue;
                assignment[s] = i;
                break;
            }
        }
    }

    /// <summary>
    /// True when a sphere (centre relative to the light) can appear in cube face
    /// <paramref name="face"/>'s 90° frustum. Each side plane passes through the light at 45° to
    /// the face axis, so the test is |c·u| − c·a ≤ r√2 for both perpendicular axes.
    /// </summary>
    public static bool SphereTouchesCubeFace(Vector3 relativeCenter, float radius, int face)
    {
        Vector3 axis = FaceDirection(face);
        float along = Vector3.Dot(relativeCenter, axis);
        float r = MathF.Max(radius, 0f);
        if (along + r < 0f) return false;
        float slack = r * 1.41421356f;
        (Vector3 u, Vector3 v) = face switch
        {
            0 or 1 => (Vector3.UnitY, Vector3.UnitZ),
            2 or 3 => (Vector3.UnitX, Vector3.UnitZ),
            _ => (Vector3.UnitX, Vector3.UnitY),
        };
        return MathF.Abs(Vector3.Dot(relativeCenter, u)) - along <= slack
            && MathF.Abs(Vector3.Dot(relativeCenter, v)) - along <= slack;
    }

    /// <summary>True when a sphere (centre relative to the light) overlaps a spot light's cone.</summary>
    public static bool SphereTouchesSpotCone(Vector3 relativeCenter, float radius, Vector3 axis, float cosOuter)
    {
        float distance = relativeCenter.Length();
        float r = MathF.Max(radius, 0f);
        if (distance <= r || distance < 1e-5f) return true;
        float cosToCenter = Math.Clamp(Vector3.Dot(relativeCenter / distance, axis), -1f, 1f);
        float angleToCenter = MathF.Acos(cosToCenter);
        float angularRadius = MathF.Asin(Math.Clamp(r / distance, 0f, 1f));
        return angleToCenter - angularRadius <= MathF.Acos(Math.Clamp(cosOuter, -1f, 1f));
    }

    /// <summary>Perspective light view-projection covering a spot light's cone (one atlas tile).</summary>
    public static Matrix4x4 SpotViewProjection(Vector3 position, Vector3 axis, float cosOuter, float farPlane)
    {
        Vector3 forward = axis.LengthSquared() > 1e-8f ? Vector3.Normalize(axis) : -Vector3.UnitY;
        Vector3 up = MathF.Abs(forward.Y) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;
        float halfAngle = MathF.Acos(Math.Clamp(cosOuter, -1f, 1f));
        float degree = MathF.PI / 180f;
        float fov = Math.Clamp(2f * halfAngle + 2f * degree, 2f * degree, 175f * degree);
        Matrix4x4 view = Genesis.Rendering.D3dMath.D3dMatrixHelper.CreateLookAtLh(position, position + forward, up);
        Matrix4x4 proj = Genesis.Rendering.D3dMath.D3dMatrixHelper.CreatePerspectiveLh(
            fov, 1f, DefaultNearPlane, MathF.Max(farPlane, DefaultNearPlane * 2f));
        return view * proj;
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
