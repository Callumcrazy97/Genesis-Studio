using System;
using Genesis.Shared.Scripting;
using Genesis.World.Water;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// AF3.2 <c>Engine.Terrain.Water</c> hole authoring. Full fill/leak/tap/temperature surface is AF3.7;
/// this tranche exposes punch/clear + tool params via <see cref="WaterAuthoringDefaults"/>.
/// </summary>
public static partial class PgslCommands
{
    [PgslCommand("HoleRadius", "Engine.Terrain.Water.HoleRadius",
        "Leak aperture radius in metres (punch tool)", "Engine · Terrain · Water",
        Namespace = "Engine.Terrain.Water")]
    public static float WaterHoleRadius
    {
        get => WaterAuthoringDefaults.HoleRadius;
        set => WaterAuthoringDefaults.HoleRadius = Math.Clamp(value, 0.0005f, 0.08f);
    }

    [PgslCommand("HoleShape", "Engine.Terrain.Water.HoleShape",
        "Leak shape 0=Round 1=Slit 2=Crack", "Engine · Terrain · Water",
        Namespace = "Engine.Terrain.Water")]
    public static int WaterHoleShape
    {
        get => (int)WaterAuthoringDefaults.HoleShape;
        set => WaterAuthoringDefaults.HoleShape = (ReservoirHoleShape)Math.Clamp(value, 0, 2);
    }

    [PgslCommand("HoleDischargeCoefficient", "Engine.Terrain.Water.HoleDischargeCoefficient",
        "Orifice discharge coefficient (Cd)", "Engine · Terrain · Water",
        Namespace = "Engine.Terrain.Water")]
    public static float WaterHoleDischargeCoefficient
    {
        get => WaterAuthoringDefaults.HoleDischargeCoefficient;
        set => WaterAuthoringDefaults.HoleDischargeCoefficient = Math.Clamp(value, 0.05f, 1f);
    }

    [PgslCommand("HoleAngleBias", "Engine.Terrain.Water.HoleAngleBias",
        "Jet direction bias in radians", "Engine · Terrain · Water",
        Namespace = "Engine.Terrain.Water")]
    public static float WaterHoleAngleBias
    {
        get => WaterAuthoringDefaults.HoleAngleBiasRadians;
        set => WaterAuthoringDefaults.HoleAngleBiasRadians =
            Math.Clamp(value, -MathF.PI * 0.5f, MathF.PI * 0.5f);
    }

    [PgslCommand("HoleCount", "Engine.Terrain.Water.HoleCount",
        "Number of punched holes in the authoring scratchpad", "Engine · Terrain · Water",
        Namespace = "Engine.Terrain.Water")]
    public static int WaterHoleCount => WaterAuthoringDefaults.HoleCount;

    [PgslCommand("PunchHole", "Engine.Terrain.Water.PunchHole",
        "Punch a leak at position with outward surface normal", "Engine · Terrain · Water",
        Namespace = "Engine.Terrain.Water")]
    public static void PunchWaterHole(float x, float y, float z, float nx, float ny, float nz) =>
        WaterAuthoringDefaults.PunchHole(new System.Numerics.Vector3(x, y, z), new System.Numerics.Vector3(nx, ny, nz));

    [PgslCommand("ClearHoles", "Engine.Terrain.Water.ClearHoles",
        "Clear punched holes from the authoring scratchpad", "Engine · Terrain · Water",
        Namespace = "Engine.Terrain.Water")]
    public static void ClearWaterHoles() => WaterAuthoringDefaults.ClearHoles();
}
