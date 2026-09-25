namespace Genesis.Physics;

/// <summary>Built-in physics scenarios shipped with the editor and sandbox.</summary>
public static class PhysicsScenePresets
{
    public static readonly string[] Names =
    {
        "Heavy Rock",
        "Rubber Ball",
        "Slick Ice",
        "Hard Wood",
        "Steel",
        "Water Basin",
        "Default",
        "Zero-G",
        "Moon",
        "Jupiter",
        "Wind Tunnel",
        "Underwater",
        "Mini-Planet",
        "Binary Planets",
        "2D Platformer",
        "Ragdoll Lab",
        "Stress Test",
    };

    public static PhysicsSceneConfig Create(string name) => name switch
    {
        "Heavy Rock" => HeavyRock(),
        "Rubber Ball" => RubberBall(),
        "Slick Ice" => SlickIce(),
        "Hard Wood" => HardWood(),
        "Steel" => Steel(),
        "Water Basin" => WaterBasin(),
        "Zero-G" => ZeroG(),
        "Moon" => Moon(),
        "Jupiter" => Jupiter(),
        "Wind Tunnel" => WindTunnel(),
        "Underwater" => Underwater(),
        "Mini-Planet" => MiniPlanet(),
        "Binary Planets" => BinaryPlanets(),
        "2D Platformer" => Platformer2D(),
        "Ragdoll Lab" => RagdollLab(),
        "Stress Test" => StressTest(),
        _ => Default(),
    };

    public static PhysicsSceneConfig HeavyRock() => new()
    {
        Name = "Heavy Rock", Shape = PhysicsBodyShape.Sphere, SpawnShape = PhysicsBodyShape.Sphere,
        Density = 2.85, Friction = 0.65, Restitution = 0.12, SpawnMass = 18.83f, SpawnCount = 1,
        Notes = "Dense stone with restrained bounce and reliable surface grip.",
    };

    public static PhysicsSceneConfig RubberBall() => new()
    {
        Name = "Rubber Ball", Shape = PhysicsBodyShape.Sphere, SpawnShape = PhysicsBodyShape.Sphere,
        Density = 1.1, Friction = 0.8, Restitution = 0.86, SpawnMass = 1.1f, SpawnCount = 1,
        Notes = "High restitution rubber for bounce and impact tuning.",
    };

    public static PhysicsSceneConfig SlickIce() => new()
    {
        Name = "Slick Ice", Density = 0.92, Friction = 0.03, Restitution = 0.02,
        Shape = PhysicsBodyShape.Box, SpawnShape = PhysicsBodyShape.Box, SpawnCount = 1,
        Notes = "Very low friction surface material.",
    };

    public static PhysicsSceneConfig HardWood() => new()
    {
        Name = "Hard Wood", Density = 0.72, Friction = 0.58, Restitution = 0.22,
        Shape = PhysicsBodyShape.Box, SpawnShape = PhysicsBodyShape.Box, SpawnCount = 1,
        Notes = "General hard timber material.",
    };

    public static PhysicsSceneConfig Steel() => new()
    {
        Name = "Steel", Density = 7.85, Friction = 0.5, Restitution = 0.08,
        Shape = PhysicsBodyShape.Box, SpawnShape = PhysicsBodyShape.Box, SpawnMass = 24f, SpawnCount = 1,
        Notes = "Dense steel with low bounce.",
    };

    public static PhysicsSceneConfig WaterBasin()
    {
        PhysicsSceneConfig config = Underwater();
        config.Name = "Water Basin";
        config.SpawnCount = 1;
        config.Notes = "Buoyancy and drag basin for testing floating bodies.";
        return config;
    }

    public static PhysicsSceneConfig Default() => new()
    {
        Name = "Default",
        Scenario = PhysicsScenarioKind.Sandbox,
        GravityModel = PhysicsGravityModel.Uniform,
        GravityStrength = 1f,
        SpawnCount = 8,
        Notes = "Earth-like defaults. Editor sandbox starts empty — use Spawn to add cubes.",
    };

    public static PhysicsSceneConfig ZeroG() => new()
    {
        Name = "Zero-G",
        Scenario = PhysicsScenarioKind.Sandbox,
        GravityModel = PhysicsGravityModel.ZeroG,
        GravityStrength = 0f,
        AllowSleep = false,
        SpawnCount = 36,
        SpawnLayout = PhysicsSpawnLayout.Orbit,
        Notes = "Floating drift; throw/grab in space.",
    };

    public static PhysicsSceneConfig Moon() => new()
    {
        Name = "Moon",
        Scenario = PhysicsScenarioKind.Sandbox,
        GravityModel = PhysicsGravityModel.Uniform,
        GravityStrength = 0.165f,
        JumpSpeed = 7.2f,
        SpawnCount = 20,
        Notes = "Low-G hops and long arcs.",
    };

    public static PhysicsSceneConfig Jupiter() => new()
    {
        Name = "Jupiter",
        Scenario = PhysicsScenarioKind.Sandbox,
        GravityModel = PhysicsGravityModel.Uniform,
        GravityStrength = 2.5f,
        MaxVelocity = 60f,
        SpawnCount = 48,
        SpawnLayout = PhysicsSpawnLayout.Pile,
        Notes = "Heavy, fast settling — solver/stability test.",
    };

    public static PhysicsSceneConfig WindTunnel() => new()
    {
        Name = "Wind Tunnel",
        Scenario = PhysicsScenarioKind.Sandbox,
        GravityModel = PhysicsGravityModel.Directional,
        GravityStrength = 1f,
        GravityDirection = new System.Numerics.Vector3(1f, 0f, 0f),
        SpawnCount = 32,
        Notes = "Sideways gravity; tests non-vertical down.",
    };

    public static PhysicsSceneConfig Underwater() => new()
    {
        Name = "Underwater",
        Scenario = PhysicsScenarioKind.Sandbox,
        GravityModel = PhysicsGravityModel.Uniform,
        GravityStrength = 1f,
        FluidEnabled = true,
        AirDrag = 2.8f,
        SpawnCount = 28,
        FluidVolumes =
        {
            new PhysicsFluidVolumeConfig
            {
                MinX = -12f, MinY = -1f, MinZ = -12f,
                MaxX = 12f, MaxY = 8f, MaxZ = 12f,
                Density = 1.2f,
                LinearDrag = 3.5f,
                Buoyancy = 1.15f,
            },
        },
        Notes = "Buoyancy/drag volume (phase-1 fluid feel).",
    };

    public static PhysicsSceneConfig MiniPlanet() => new()
    {
        Name = "Mini-Planet",
        Scenario = PhysicsScenarioKind.Sandbox,
        GravityModel = PhysicsGravityModel.Radial,
        GravityStrength = 0f,
        SpawnCount = 24,
        Attractors =
        {
            new PhysicsAttractorConfig
            {
                Position = new System.Numerics.Vector3(0f, 6f, 0f),
                Strength = 18f,
                Radius = 28f,
                OrientBodies = true,
            },
        },
        Notes = "Radial attractor data (full surface walk in phase 2).",
    };

    public static PhysicsSceneConfig BinaryPlanets() => new()
    {
        Name = "Binary Planets",
        Scenario = PhysicsScenarioKind.Sandbox,
        GravityModel = PhysicsGravityModel.Radial,
        GravityStrength = 0f,
        SpawnCount = 40,
        SpawnLayout = PhysicsSpawnLayout.Orbit,
        Attractors =
        {
            new PhysicsAttractorConfig { Position = new(-10f, 8f, 0f), Strength = 14f, Radius = 22f },
            new PhysicsAttractorConfig { Position = new(10f, 8f, 0f), Strength = 14f, Radius = 22f },
        },
        Notes = "Two attractors — objects tugged between bodies.",
    };

    public static PhysicsSceneConfig Platformer2D() => new()
    {
        Name = "2D Platformer",
        Scenario = PhysicsScenarioKind.Platformer2D,
        Dimension = PhysicsDimension.TwoD,
        GravityModel = PhysicsGravityModel.Uniform,
        GravityStrength = 1f,
        LockRotation = true,
        SpawnCount = 16,
        SpawnLayout = PhysicsSpawnLayout.Grid,
        Notes = "Side-view plane-locked sandbox.",
    };

    public static PhysicsSceneConfig RagdollLab() => new()
    {
        Name = "Ragdoll Lab",
        Scenario = PhysicsScenarioKind.RagdollLab,
        BodyType = PhysicsBodyKind.Ragdoll,
        GravityModel = PhysicsGravityModel.Uniform,
        GravityStrength = 1f,
        SpawnCount = 4,
        Notes = "Ragdoll foundation hook (constraints in a later phase).",
    };

    public static PhysicsSceneConfig StressTest() => new()
    {
        Name = "Stress Test",
        Scenario = PhysicsScenarioKind.StressTest,
        GravityModel = PhysicsGravityModel.Uniform,
        GravityStrength = 1f,
        SpawnCount = 500,
        SpawnLayout = PhysicsSpawnLayout.Pile,
        EnableThreadDispatcher = true,
        Notes = "Large body count for perf checks.",
    };
}
