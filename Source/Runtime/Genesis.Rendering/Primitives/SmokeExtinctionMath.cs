using System;
using System.Numerics;

namespace Genesis.Rendering.Primitives;

/// <summary>
/// AF1.6 CPU reference for smoke as extinction — particle density darkening what is seen through
/// it, as one Beer-Lambert transmittance term rather than a second fog. Pure math for headless
/// gates; no GPU devices.
/// </summary>
/// <remarks>
/// Ported from ForestLight's <c>segmentSphereLength</c>/<c>smokeTransmittance</c> pair (its
/// lighting fragment shader) and the age-binned <c>GetSmokeVolumes</c> accumulator on its particle
/// system. The algorithm is the port, not the engine: ForestLight evaluates this inside a deferred
/// lighting pass over its own fire/spot lights, whereas Genesis evaluates the same integral once in
/// the existing fog composite against the reconstructed world position.
///
/// The cost control is the volume count, not a reduced update rate: at most
/// <see cref="MaxVolumes"/> spheres are derived from the live particles every frame the feature is
/// on, and the shader's loop is bounded by that same constant. Nothing here is temporally skipped
/// or reprojected — smoke that only updates on one frame in four swims under camera motion.
///
/// Only alpha-blended (smoke/dust) emitters contribute. Additive fire and embers are light being
/// added, not a medium absorbing it, so feeding them into extinction would darken the scene in
/// exactly the places the fire is meant to brighten — the same smoke/fire split ForestLight makes
/// when it bins <c>ParticleKind.Smoke</c> alone.
/// </remarks>
public static class SmokeExtinctionMath
{
    /// <summary>Hard ceiling on derived volumes; the HLSL loop bound never exceeds this.</summary>
    public const int MaxVolumes = 8;

    /// <summary>
    /// Optical-depth multiplier applied to the accumulated chord length. ForestLight's constant —
    /// dense enough that a compact plume reads as smoke rather than a grey wash.
    /// </summary>
    public const float DefaultExtinctionCoefficient = 1.35f;

    /// <summary>Floor on a derived sphere radius, so a tight cluster still occupies volume.</summary>
    public const float DefaultMinRadius = 0.45f;

    /// <summary>Ceiling on a derived sphere radius, so one stray particle cannot fog the world.</summary>
    public const float MaxRadius = 12f;

    /// <summary>World-space size of the spatial cell a sample is hashed into for binning.</summary>
    public const float DefaultCellSize = 1.5f;

    /// <summary>Converts a mean sample weight (alpha x remaining life) into per-unit density.</summary>
    public const float DensityScale = 1.8f;

    /// <summary>Density floor for an emitted volume — below this the sphere is not worth a slot.</summary>
    public const float MinDensity = 0.035f;

    /// <summary>Density ceiling, so a dense plume attenuates strongly but never to pure black.</summary>
    public const float MaxDensity = 0.62f;

    /// <summary>One analytic smoke sphere: everything the shader needs to integrate a chord.</summary>
    public readonly record struct SmokeVolume(Vector3 Position, float Radius, float Density);

    /// <summary>
    /// Length of the portion of segment <paramref name="a"/>→<paramref name="b"/> that lies inside
    /// the sphere. Zero when the segment misses it entirely, so this is always safe to sum.
    /// </summary>
    public static float SegmentSphereLength(Vector3 a, Vector3 b, Vector3 center, float radius)
    {
        Vector3 delta = b - a;
        float segmentLength = delta.Length();
        if (segmentLength < 1e-4f || radius <= 0f)
            return 0f;

        Vector3 direction = delta / segmentLength;
        Vector3 toOrigin = a - center;
        float bTerm = Vector3.Dot(toOrigin, direction);
        float cTerm = Vector3.Dot(toOrigin, toOrigin) - radius * radius;
        float discriminant = bTerm * bTerm - cTerm;
        if (discriminant <= 0f)
            return 0f;

        float root = MathF.Sqrt(discriminant);
        float t0 = Math.Clamp(-bTerm - root, 0f, segmentLength);
        float t1 = Math.Clamp(-bTerm + root, 0f, segmentLength);
        return MathF.Max(0f, t1 - t0);
    }

    /// <summary>
    /// Beer-Lambert transmittance along <paramref name="a"/>→<paramref name="b"/> through every
    /// volume. Returns 1 (smoke has no effect) for an empty set, and never returns 0 — smoke thins
    /// what is behind it, it does not delete it.
    /// </summary>
    public static float Transmittance(
        Vector3 a,
        Vector3 b,
        ReadOnlySpan<SmokeVolume> volumes,
        float k = DefaultExtinctionCoefficient)
    {
        if (volumes.IsEmpty || k <= 0f)
            return 1f;

        float opticalDepth = 0f;
        int count = Math.Min(volumes.Length, MaxVolumes);
        for (int i = 0; i < count; i++)
        {
            ref readonly SmokeVolume volume = ref volumes[i];
            if (volume.Density <= 0f)
                continue;
            opticalDepth += SegmentSphereLength(a, b, volume.Position, volume.Radius) * volume.Density;
        }

        if (opticalDepth <= 0f)
            return 1f;

        return Math.Clamp(MathF.Exp(-opticalDepth * k), 0f, 1f);
    }

    /// <summary>
    /// Bins weighted particle samples into at most <paramref name="maxVolumes"/> spheres and writes
    /// them to <paramref name="destination"/>, returning how many were emitted.
    /// </summary>
    /// <param name="samples">Live particle positions with an alpha x remaining-life weight.</param>
    /// <param name="minRadius">
    /// Floor on the derived radius. Callers pass their emitter's particle size so a tightly packed
    /// puff still occupies the volume its billboards cover, rather than the spread of their centres.
    /// </param>
    /// <remarks>
    /// Binning by particle age works for a compact plume that rises along a
    /// single column. Genesis emitters can be anywhere, so the bin key is a spatial hash of the
    /// sample's cell instead: two plumes on opposite sides of a room stay separate spheres rather
    /// than averaging into one useless volume between them.
    /// </remarks>
    public static int BuildVolumesFromSamples(
        ReadOnlySpan<(Vector3 Position, float Weight)> samples,
        Span<SmokeVolume> destination,
        int maxVolumes = MaxVolumes,
        float minRadius = DefaultMinRadius,
        float cellSize = DefaultCellSize)
    {
        int bins = Math.Clamp(maxVolumes, 0, Math.Min(MaxVolumes, destination.Length));
        if (bins == 0 || samples.IsEmpty)
            return 0;

        Span<Vector3> positionSums = stackalloc Vector3[MaxVolumes];
        Span<float> weightSums = stackalloc float[MaxVolumes];
        Span<float> squareSums = stackalloc float[MaxVolumes];
        Span<int> counts = stackalloc int[MaxVolumes];
        positionSums.Clear();
        weightSums.Clear();
        squareSums.Clear();
        counts.Clear();

        float cell = MathF.Max(cellSize, 1e-3f);
        for (int i = 0; i < samples.Length; i++)
        {
            (Vector3 position, float weight) = samples[i];
            if (weight <= 0f || !float.IsFinite(weight))
                continue;

            int bin = BinIndex(position, cell, bins);
            positionSums[bin] += position * weight;
            weightSums[bin] += weight;
            // Weighted second moment of |position|, so the spread below is one pass, not two.
            squareSums[bin] += position.LengthSquared() * weight;
            counts[bin]++;
        }

        float floorRadius = Math.Clamp(minRadius, 1e-3f, MaxRadius);
        int emitted = 0;
        for (int bin = 0; bin < bins; bin++)
        {
            float weight = weightSums[bin];
            if (weight <= 1e-4f || counts[bin] == 0)
                continue;

            Vector3 centre = positionSums[bin] / weight;
            // Variance of the cluster about its own centroid: E[|p|^2] - |E[p]|^2, clamped because
            // float cancellation can push a perfectly coincident cluster fractionally negative.
            float variance = MathF.Max(0f, squareSums[bin] / weight - centre.LengthSquared());
            float radius = Math.Clamp(MathF.Sqrt(variance) * 1.5f + floorRadius, floorRadius, MaxRadius);

            float density = Math.Clamp(weight / counts[bin] * DensityScale, 0f, MaxDensity);
            if (density < MinDensity)
                continue;

            destination[emitted++] = new SmokeVolume(centre, radius, density);
        }

        return emitted;
    }

    /// <summary>Spatial hash of the cell a world position falls in, folded onto the bin count.</summary>
    private static int BinIndex(Vector3 position, float cellSize, int bins)
    {
        unchecked
        {
            uint x = (uint)(int)MathF.Floor(position.X / cellSize);
            uint y = (uint)(int)MathF.Floor(position.Y / cellSize);
            uint z = (uint)(int)MathF.Floor(position.Z / cellSize);
            uint hash = (x * 73856093u) ^ (y * 19349663u) ^ (z * 83492791u);
            hash ^= hash >> 16;
            hash *= 0x7feb352du;
            hash ^= hash >> 15;
            return (int)(hash % (uint)bins);
        }
    }
}
