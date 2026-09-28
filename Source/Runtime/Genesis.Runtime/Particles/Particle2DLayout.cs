using System;

namespace Genesis.Runtime.Particles;

/// <summary>Converts shared effect units to planar pixels before simulation and Object placement.</summary>
public static class Particle2DLayout
{
    public const float PixelsPerUnit = 12f;

    public static ParticleConfig ForSimulation(ParticleConfig authored)
    {
        ArgumentNullException.ThrowIfNull(authored);
        if (authored.IsPlanar2D) return authored;
        ParticleConfig planar = authored.CloneEmitter();
        planar.IsPlanar2D = true;
        // Camera-XZ weather anchoring is a 3D feature. In a planar room the effect
        // follows its Object (which can be attached to the player) through ordinary XY placement.
        planar.FollowCameraXZ = false;
        planar.Speed *= PixelsPerUnit;
        planar.StartSize *= PixelsPerUnit;
        planar.EndSize *= PixelsPerUnit;
        planar.EmitRadius *= PixelsPerUnit;
        planar.BoxSizeX *= PixelsPerUnit;
        planar.BoxSizeY *= PixelsPerUnit;
        planar.BoxSizeZ = 0;
        planar.GravityX *= PixelsPerUnit;
        planar.Gravity *= -PixelsPerUnit;
        planar.GravityZ = 0;
        planar.WindX *= PixelsPerUnit;
        planar.WindZ = 0;
        planar.TurbulenceStrength *= PixelsPerUnit;
        planar.VelocityStretch /= PixelsPerUnit;
        planar.CollisionPlaneHeight *= -PixelsPerUnit;
        planar.RibbonMaxSegmentLength *= PixelsPerUnit;
        planar.BeamEndX *= PixelsPerUnit;
        planar.BeamEndY *= -PixelsPerUnit;
        planar.BeamEndZ = 0;
        planar.BeamNoise *= PixelsPerUnit;
        planar.BoundsCenterX *= PixelsPerUnit;
        planar.BoundsCenterY *= -PixelsPerUnit;
        planar.BoundsCenterZ = 0;
        planar.BoundsSizeX *= PixelsPerUnit;
        planar.BoundsSizeY *= PixelsPerUnit;
        planar.BoundsSizeZ = 1;
        return planar;
    }
}
