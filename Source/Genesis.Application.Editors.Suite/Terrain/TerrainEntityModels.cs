namespace Genesis.Application.Editors.Suite.Terrain;

using Genesis.Shared.Interfaces;

/// <summary>The five terrain-dressing categories the entity library groups by.</summary>
public enum TerrainEntityType
{
    Terrain,
    Foliage,
    Object,
    Fluid,
    Environment,
    Tree,
}

/// <summary>
/// One component attached to a Terrain Entity. Mirrors the existing Object component
/// convention (<c>type</c> + string-keyed <c>props</c>, see <c>ObjectEditorControl</c> /
/// <c>PrefabSpawner</c>) so a future runtime spawner can read Terrain Entities the same way
/// Room Objects are read, without inventing a second component serialization scheme.
/// </summary>
public sealed class TerrainEntityComponent
{
    public string Type { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public Dictionary<string, string> Props { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string Get(string key, string fallback = "") =>
        Props.TryGetValue(key, out string? value) && value is not null ? value : fallback;

    public void Set(string key, string value) => Props[key] = value;
}

/// <summary>The kinds of component a Terrain Entity can carry, and their prop schema.</summary>
public static class TerrainEntityComponentKinds
{
    public const string Shader = "Shader";
    public const string ParticleEmitter = "ParticleEmitter";
    public const string Texture = "Texture";
    public const string Model = "Model";
    public const string AudioEmitter = "AudioEmitter";
    public const string Physics = "Physics";
    public const string Condition = "Condition";
    public const string Script = "Script";

    public static readonly string[] All =
    [
        Model, Texture, Shader, Script, ParticleEmitter, Physics, AudioEmitter, Condition,
    ];

    public static string DisplayName(string kind) => kind switch
    {
        Shader => "Shader",
        ParticleEmitter => "Particle Emitter",
        Texture => "Texture",
        Model => "Model",
        AudioEmitter => "Audio Emitter",
        Physics => "Physics",
        Condition => "Condition",
        Script => "Script / PGSL",
        _ => kind,
    };
}

/// <summary>2D/3D texture-component render modes.</summary>
public enum TerrainEntityTextureMode
{
    Billboard2D,
    Diagonal2D,
    Extruded2D,
    Plane3D,
}

/// <summary>Audio-component spatialisation mode.</summary>
public enum TerrainEntityAudioMode
{
    TwoD,
    ThreeD,
}

public enum TerrainEntityAudioFalloff
{
    Linear,
    Logarithmic,
}

/// <summary>Physics-component collider shape.</summary>
public enum TerrainEntityPhysicsShape
{
    Box,
    Sphere,
    Capsule,
    Mesh,
}

/// <summary>
/// The <c>.terrainentity.json</c> document. A reusable terrain-dressing definition (a tree,
/// a water body, a fog volume, …) — analogous to how <c>.object.json</c> is a reusable
/// gameplay prefab. Referenced by name/path from a room's Terrain node once a spawner
/// consumes the library; this schema only defines what one entity IS.
/// </summary>
public sealed class TerrainEntityDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "New Terrain Entity";
    public TerrainEntityType Type { get; set; } = TerrainEntityType.Foliage;
    public FaceCullingOverride Culling { get; set; } = FaceCullingOverride.Default;
    public FrontFaceWindingOverride WindingOrder { get; set; } = FrontFaceWindingOverride.Default;

    /// <summary>Project-relative path to a Sprite resource used as the browser thumbnail.</summary>
    public string Icon { get; set; } = "";

    public List<TerrainEntityComponent> Components { get; set; } = [];
    public TerrainCreationRecipe? Creation { get; set; }
}
