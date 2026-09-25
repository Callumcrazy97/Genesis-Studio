using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Genesis.Physics;

public enum PhysicsDimension
{
    ThreeD = 0,
    TwoD = 1,
}

public enum PhysicsGravityModel
{
    Uniform = 0,
    Directional = 1,
    ZeroG = 2,
    Radial = 3,
    Custom = 4,
}

public enum PhysicsScenarioKind
{
    Playground = 0,
    Sandbox = 1,
    StressTest = 2,
    Platformer2D = 3,
    RagdollLab = 4,
}

public enum PhysicsBodyKind
{
    Dynamic = 0,
    Static = 1,
    Kinematic = 2,
    Ragdoll = 3,
}

public enum PhysicsBodyShape
{
    Box = 0,
    Sphere = 1,
    Capsule = 2,
    Cylinder = 3,
    Mesh = 4,
}

public enum PhysicsSpawnLayout
{
    Drop = 0,
    Pile = 1,
    Grid = 2,
    Orbit = 3,
}

public enum PhysicsAttractorFalloff
{
    Constant = 0,
    Linear = 1,
    InverseSquare = 2,
}

/// <summary>
/// Authoring asset for a reusable physics world + default body material.
/// Supersedes the legacy <see cref="PhysicsConfig"/> while remaining load-compatible.
/// </summary>
public sealed class PhysicsSceneConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static bool[][] CreateDefaultCollisionLayerMatrix() =>
        Enumerable.Range(0, 7).Select(_ => Enumerable.Repeat(true, 7).ToArray()).ToArray();

    // Meta
    public string Name { get; set; } = "Default";
    public string Notes { get; set; } = string.Empty;

    /// <summary>Optional PGSL script attached to this physics asset. Authoring is wired through the
    /// unified code editor; runtime execution of attached physics scripts is pending (see ToDo.md).</summary>
    public string Script { get; set; } = string.Empty;
    public PhysicsDimension Dimension { get; set; } = PhysicsDimension.ThreeD;
    /// <summary>Sprite shown in 2D editor preview (e.g. Player).</summary>
    public string BackdropSprite { get; set; } = "Player";
    /// <summary>Optional project asset path used as the editor preview target (Model/Image/Object).</summary>
    public string PreviewAssetPath { get; set; } = string.Empty;
    /// <summary>Resource kind name for <see cref="PreviewAssetPath"/>: Model, Image, or Object.</summary>
    public string PreviewAssetKind { get; set; } = string.Empty;
    public PhysicsScenarioKind Scenario { get; set; } = PhysicsScenarioKind.Sandbox;

    // World
    public PhysicsGravityModel GravityModel { get; set; } = PhysicsGravityModel.Uniform;
    public float GravityStrength { get; set; } = 1f;
    public float GravityDirX { get; set; } = 0f;
    public float GravityDirY { get; set; } = -1f;
    public float GravityDirZ { get; set; } = 0f;
    public int SolverIterations { get; set; } = 8;
    public int SubstepCount { get; set; } = 1;
    public bool AllowSleep { get; set; } = true;
    public float SleepThreshold { get; set; } = 0.25f;
    public float AirDrag { get; set; } = 0.15f;
    public float MaxVelocity { get; set; } = 45f;
    public bool EnableThreadDispatcher { get; set; } = true;
    public int DispatcherThreadCount { get; set; } = Math.Max(1, Environment.ProcessorCount);

    // Attractors (data for radial scenarios; full runtime in a later phase)
    public List<PhysicsAttractorConfig> Attractors { get; set; } = new();

    // Default body material
    public PhysicsBodyKind BodyType { get; set; } = PhysicsBodyKind.Dynamic;
    public PhysicsBodyShape Shape { get; set; } = PhysicsBodyShape.Box;
    public double Density { get; set; } = 1.0;
    public double Friction { get; set; } = 0.5;
    public double Restitution { get; set; } = 0.1;
    public bool IsSensor { get; set; }
    public float GravityScale { get; set; } = 1f;
    public float LinearDamping { get; set; }
    public float AngularDamping { get; set; }
    public bool LockRotation { get; set; }
    /// <summary>Default A-G collision layer assigned to bodies using this asset.</summary>
    public int CollisionLayer { get; set; }
    /// <summary>Symmetric seven-layer authoring matrix used by runtime collision filtering.</summary>
    public bool[][] CollisionLayerMatrix { get; set; } = CreateDefaultCollisionLayerMatrix();

    // Fluid (phase-1 data hook)
    public bool FluidEnabled { get; set; }
    public List<PhysicsFluidVolumeConfig> FluidVolumes { get; set; } = new();

    // Character interaction (playground scenarios)
    public float MoveSpeed { get; set; } = 8f;
    public float JumpSpeed { get; set; } = 5.45f;
    public float SprintMultiplier { get; set; } = 1.6f;
    public float GroundAcceleration { get; set; } = 40f;
    public float GroundDeceleration { get; set; } = 30f;
    public float AirAcceleration { get; set; } = 8f;
    public float AirDeceleration { get; set; } = 1.2f;
    public float AirControlForward { get; set; } = 0.24f;
    public float AirControlSide { get; set; } = 0.10f;
    public float AirControlBack { get; set; } = 0.045f;

    // Spawning
    public PhysicsBodyShape SpawnShape { get; set; } = PhysicsBodyShape.Box;
    public float SpawnMass { get; set; } = 1f;
    public int SpawnCount { get; set; } = 8;
    public PhysicsSpawnLayout SpawnLayout { get; set; } = PhysicsSpawnLayout.Grid;

    public Vector3 GravityDirection
    {
        get => new(GravityDirX, GravityDirY, GravityDirZ);
        set { GravityDirX = value.X; GravityDirY = value.Y; GravityDirZ = value.Z; }
    }

    public PhysicsSceneConfig Clone() => new()
    {
        Name = Name,
        Notes = Notes,
        Script = Script,
        Dimension = Dimension,
        BackdropSprite = BackdropSprite,
        PreviewAssetPath = PreviewAssetPath,
        PreviewAssetKind = PreviewAssetKind,
        Scenario = Scenario,
        GravityModel = GravityModel,
        GravityStrength = GravityStrength,
        GravityDirX = GravityDirX,
        GravityDirY = GravityDirY,
        GravityDirZ = GravityDirZ,
        SolverIterations = SolverIterations,
        SubstepCount = SubstepCount,
        AllowSleep = AllowSleep,
        SleepThreshold = SleepThreshold,
        AirDrag = AirDrag,
        MaxVelocity = MaxVelocity,
        EnableThreadDispatcher = EnableThreadDispatcher,
        DispatcherThreadCount = DispatcherThreadCount,
        Attractors = CloneAttractors(Attractors),
        BodyType = BodyType,
        Shape = Shape,
        Density = Density,
        Friction = Friction,
        Restitution = Restitution,
        IsSensor = IsSensor,
        GravityScale = GravityScale,
        LinearDamping = LinearDamping,
        AngularDamping = AngularDamping,
        LockRotation = LockRotation,
        CollisionLayer = CollisionLayer,
        CollisionLayerMatrix = (CollisionLayerMatrix ?? CreateDefaultCollisionLayerMatrix())
            .Select(row => row?.ToArray() ?? new bool[7]).ToArray(),
        FluidEnabled = FluidEnabled,
        FluidVolumes = CloneFluidVolumes(FluidVolumes),
        MoveSpeed = MoveSpeed,
        JumpSpeed = JumpSpeed,
        SprintMultiplier = SprintMultiplier,
        GroundAcceleration = GroundAcceleration,
        GroundDeceleration = GroundDeceleration,
        AirAcceleration = AirAcceleration,
        AirDeceleration = AirDeceleration,
        AirControlForward = AirControlForward,
        AirControlSide = AirControlSide,
        AirControlBack = AirControlBack,
        SpawnShape = SpawnShape,
        SpawnMass = SpawnMass,
        SpawnCount = SpawnCount,
        SpawnLayout = SpawnLayout,
    };

    public Vector3 ResolveGravityVector()
    {
        const float g = 9.81f;
        return GravityModel switch
        {
            PhysicsGravityModel.ZeroG => Vector3.Zero,
            PhysicsGravityModel.Directional or PhysicsGravityModel.Custom =>
                NormalizeOrDown(GravityDirection) * (g * MathF.Max(0f, GravityStrength)),
            PhysicsGravityModel.Radial when Attractors.Count > 0 =>
                Vector3.Zero,
            _ => new Vector3(0f, -g * MathF.Max(0f, GravityStrength), 0f),
        };
    }

    public PhysicsConfig ToLegacyPhysicsConfig() => new()
    {
        BodyType = BodyType.ToString(),
        Density = Density,
        Friction = Friction,
        Restitution = Restitution,
        IsSensor = IsSensor,
        Notes = Notes,
        EnableThreadDispatcher = EnableThreadDispatcher,
        DispatcherThreadCount = DispatcherThreadCount,
        AllowSleep = AllowSleep,
        SleepThreshold = SleepThreshold,
        AirDrag = AirDrag,
        MaxVelocity = MaxVelocity,
        GravityStrength = GravityStrength,
    };

    public static PhysicsSceneConfig FromLegacy(PhysicsConfig? legacy)
    {
        var cfg = PhysicsScenePresets.Default();
        if (legacy == null)
            return cfg;

        cfg.Density = legacy.Density;
        cfg.Friction = legacy.Friction;
        cfg.Restitution = legacy.Restitution;
        cfg.IsSensor = legacy.IsSensor;
        cfg.Notes = legacy.Notes ?? string.Empty;
        cfg.EnableThreadDispatcher = legacy.EnableThreadDispatcher;
        cfg.DispatcherThreadCount = legacy.DispatcherThreadCount;
        cfg.AllowSleep = legacy.AllowSleep;
        cfg.SleepThreshold = legacy.SleepThreshold;
        cfg.AirDrag = legacy.AirDrag;
        cfg.MaxVelocity = legacy.MaxVelocity;
        cfg.GravityStrength = legacy.GravityStrength;
        if (Enum.TryParse(legacy.BodyType, true, out PhysicsBodyKind bodyKind))
            cfg.BodyType = bodyKind;
        return cfg;
    }

    public static PhysicsSceneConfig LoadFromFile(string path)
    {
        if (!File.Exists(path))
            return PhysicsScenePresets.Default();

        string json = File.ReadAllText(path);
        try
        {
            var cfg = JsonSerializer.Deserialize<PhysicsSceneConfig>(json, JsonOptions);
            if (cfg != null)
                return cfg;
        }
        catch
        {
            // fall through to legacy
        }

        try
        {
            var legacy = JsonSerializer.Deserialize<PhysicsConfig>(json);
            return FromLegacy(legacy);
        }
        catch
        {
            return PhysicsScenePresets.Default();
        }
    }

    public void SaveToFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    private static List<PhysicsAttractorConfig> CloneAttractors(List<PhysicsAttractorConfig>? src)
    {
        var list = new List<PhysicsAttractorConfig>();
        if (src == null) return list;
        foreach (var a in src)
            list.Add(a.Clone());
        return list;
    }

    private static List<PhysicsFluidVolumeConfig> CloneFluidVolumes(List<PhysicsFluidVolumeConfig>? src)
    {
        var list = new List<PhysicsFluidVolumeConfig>();
        if (src == null) return list;
        foreach (var v in src)
            list.Add(v.Clone());
        return list;
    }

    private static Vector3 NormalizeOrDown(Vector3 v)
    {
        if (v.LengthSquared() < 1e-8f)
            return new Vector3(0f, -1f, 0f);
        return Vector3.Normalize(v);
    }

}

public sealed class PhysicsAttractorConfig
{
    public float PosX { get; set; }
    public float PosY { get; set; }
    public float PosZ { get; set; }
    public float Strength { get; set; } = 12f;
    public float Radius { get; set; } = 24f;
    public PhysicsAttractorFalloff Falloff { get; set; } = PhysicsAttractorFalloff.InverseSquare;
    public bool OrientBodies { get; set; }

    public Vector3 Position
    {
        get => new(PosX, PosY, PosZ);
        set { PosX = value.X; PosY = value.Y; PosZ = value.Z; }
    }

    public PhysicsAttractorConfig Clone() => new()
    {
        PosX = PosX,
        PosY = PosY,
        PosZ = PosZ,
        Strength = Strength,
        Radius = Radius,
        Falloff = Falloff,
        OrientBodies = OrientBodies,
    };
}

public sealed class PhysicsFluidVolumeConfig
{
    public float MinX { get; set; } = -8f;
    public float MinY { get; set; } = -2f;
    public float MinZ { get; set; } = -8f;
    public float MaxX { get; set; } = 8f;
    public float MaxY { get; set; } = 4f;
    public float MaxZ { get; set; } = 8f;
    public float Density { get; set; } = 1f;
    public float LinearDrag { get; set; } = 2.5f;
    public float AngularDrag { get; set; } = 1.2f;
    public float Buoyancy { get; set; } = 1f;

    public PhysicsFluidVolumeConfig Clone() => new()
    {
        MinX = MinX, MinY = MinY, MinZ = MinZ,
        MaxX = MaxX, MaxY = MaxY, MaxZ = MaxZ,
        Density = Density,
        LinearDrag = LinearDrag,
        AngularDrag = AngularDrag,
        Buoyancy = Buoyancy,
    };
}
