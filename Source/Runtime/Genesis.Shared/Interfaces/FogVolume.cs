using System.Numerics;

namespace Genesis.Shared.Interfaces
{
    /// <summary>Analytic shape used to evaluate a <see cref="FogVolume"/>'s density falloff
    /// in the screen-space fog post pass (terrain-and-rendering-fix-plan.md Issue 6, Stage 1).</summary>
    public enum FogVolumeShape
    {
        Box        = 0,
        Sphere     = 1,
        Ellipsoid  = 2,
        HeightSlab = 3, // infinite in X/Z, bounded in Y between Center.Y-Extents.Y and Center.Y+Extents.Y
    }

    /// <summary>Cosmetic categorisation only — does not change the density math, but lets
    /// authoring tools/PGSL pick sensible defaults (colour, density, falloff) per preset.</summary>
    public enum FogVolumeKind
    {
        GroundMist = 0,
        Cloud      = 1,
        Haze       = 2,
    }

    /// <summary>
    /// A placeable analytic fog region (Issue 6, Stage 1 — not the froxel-based volumetric
    /// scattering deferred to Stage 2). Submitted per-frame via
    /// <see cref="IRenderController.AddFogVolume"/>, mirroring the existing point-light
    /// submission pattern: the caller re-adds every frame the volume should be visible;
    /// <see cref="IRenderController.ClearFogVolumes"/> removes all of them. Capped at 8
    /// concurrent volumes (same cap as point lights) since both ride fixed fields in the
    /// shared EngineCB constant buffer.
    /// </summary>
    public struct FogVolume
    {
        public Vector3 Center;
        public Vector3 Extents;      // half-size along each axis (box/ellipsoid); radius is Extents.X for Sphere
        public Vector3 Color;
        public float   Density;
        public float   FalloffCurve; // exponent applied to the normalised inside-distance; higher = sharper edge
        public FogVolumeShape Shape;
        public FogVolumeKind  Kind;

        public static FogVolume CreateGroundMist(Vector3 center, Vector3 extents, Vector3 color, float density = 0.35f, float falloffCurve = 1.5f) =>
            new FogVolume { Center = center, Extents = extents, Color = color, Density = density, FalloffCurve = falloffCurve, Shape = FogVolumeShape.HeightSlab, Kind = FogVolumeKind.GroundMist };
    }
}
