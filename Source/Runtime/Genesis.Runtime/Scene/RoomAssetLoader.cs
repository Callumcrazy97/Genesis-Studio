using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Genesis.Runtime.Climate;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scripting;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Assets;
using Genesis.Physics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Scene;

public sealed class UnsupportedRoomFormatException : IOException
{
    public UnsupportedRoomFormatException(string message) : base(message) { }
}

public static class RoomAssetLoader
{
    public static RoomAsset Parse(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("Room path is required.", nameof(filePath));
        JObject root = JObject.Parse(File.ReadAllText(filePath));
        string schema = root["$schema"]?.ToString();
        int version = root["version"]?.Value<int>() ?? 0;
        if (!string.Equals(schema, RoomAsset.SchemaName, StringComparison.Ordinal) || version != RoomAsset.CurrentVersion)
            throw new UnsupportedRoomFormatException($"'{Path.GetFileName(filePath)}' is not a Genesis Room v{RoomAsset.CurrentVersion}. Legacy rooms are intentionally unsupported.");
        RoomAsset asset = root.ToObject<RoomAsset>() ?? throw new InvalidDataException("Room document is empty.");
        string projectRoot = ResourceNames.FindProjectRoot(filePath);
        if (!string.IsNullOrEmpty(projectRoot)) asset.Name = ResourceNames.Name(projectRoot, filePath, ResourceType.Room);
        asset.Normalize();
        Validate(asset);
        return asset;
    }

    public static void Save(RoomAsset asset, string filePath)
    {
        if (asset == null) throw new ArgumentNullException(nameof(asset));
        asset.Normalize(); Validate(asset);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        string text = JsonConvert.SerializeObject(asset, Formatting.Indented);
        string projectRoot = ResourceNames.FindProjectRoot(filePath);
        if (!string.IsNullOrEmpty(projectRoot)) text = ResourceReferenceRewriter.Normalize(projectRoot, filePath, text);
        File.WriteAllText(filePath, text);
    }

    public static void Validate(RoomAsset asset)
    {
        if (asset.Schema != RoomAsset.SchemaName || asset.Version != RoomAsset.CurrentVersion)
            throw new UnsupportedRoomFormatException("Unsupported room schema or version.");
        var nodeIds = new HashSet<string>(asset.Nodes.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);
        foreach (RoomNode node in asset.Nodes)
        {
            if (!string.IsNullOrEmpty(node.ParentId) && !nodeIds.Contains(node.ParentId))
                throw new InvalidDataException($"Node '{node.Name}' has a missing parent.");
            if (CreatesCycle(asset, node.Id, node.ParentId))
                throw new InvalidDataException($"Node hierarchy contains a cycle at '{node.Name}'.");
        }
        if (!string.IsNullOrEmpty(asset.ActiveGameCameraId) && !nodeIds.Contains(asset.ActiveGameCameraId))
            throw new InvalidDataException("The active GameCamera does not exist.");
    }

    private static bool CreatesCycle(RoomAsset room, string id, string parentId)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
        string next = parentId;
        while (!string.IsNullOrEmpty(next))
        {
            if (!visited.Add(next)) return true;
            next = room.Nodes.FirstOrDefault(n => n.Id == next)?.ParentId;
        }
        return false;
    }
}

public sealed class RoomBuildResult
{
    public RoomAsset Asset { get; internal set; }
    public Dictionary<string, Entity> EntitiesByNodeId { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Entity> SpawnedEntities { get; } = new();
    internal List<(Entity Entity, RoomNode Terrain, RoomTransform Local)> TerrainParts { get; } = new();

    /// <summary>Time spent placing the room's objects, excluding their Create events.</summary>
    public double SpawnMilliseconds { get; internal set; }

    /// <summary>Time spent running the Create events held back until every object was placed.</summary>
    public double CreateEventsMilliseconds { get; internal set; }

    /// <summary>Time spent building terrain services before any object exists.</summary>
    public double TerrainMilliseconds { get; internal set; }

    /// <summary>Time spent in the scripts' room-start events.</summary>
    public double RoomStartMilliseconds { get; internal set; }

    /// <summary>Scenery objects left for the room's scenery streamer to create as the camera nears them.</summary>
    public int DeferredScenery { get; internal set; }

    /// <summary>The objects that took longest to place, slowest first, at most three.</summary>
    public List<(string Name, double Milliseconds)> SlowestSpawns { get; } = new();

    internal void RecordSpawn(string name, double milliseconds)
    {
        if (SlowestSpawns.Count == 3 && milliseconds <= SlowestSpawns[2].Milliseconds) return;
        SlowestSpawns.Add((name, milliseconds));
        SlowestSpawns.Sort(static (a, b) => b.Milliseconds.CompareTo(a.Milliseconds));
        if (SlowestSpawns.Count > 3) SlowestSpawns.RemoveAt(3);
    }
}

/// <summary>
/// Fully resolved authored camera values shared by the Player and the Room Editor preview.
/// Keeping this in the runtime removes the old second, approximate editor interpretation.
/// </summary>
public sealed class RoomCameraState
{
    public RoomNode Node { get; internal set; }
    public Vector3 Position { get; internal set; }
    public float YawRadians { get; internal set; }
    public float PitchRadians { get; internal set; }
    public float FieldOfViewDegrees { get; internal set; } = 60f;
    public float NearPlane { get; internal set; } = 0.1f;
    public float FarPlane { get; internal set; } = 500f;
    public float Zoom2D { get; internal set; } = 1f;
    public bool HasCameraComponent { get; internal set; }
}

public sealed class RoomSceneBuilder
{
    private readonly string _projectPath;
    private readonly ScriptHostSystem _scriptHost;
    private readonly Dictionary<string, JObject> _prefabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _objectEvents = new(StringComparer.OrdinalIgnoreCase);

    public RoomSceneBuilder(string projectPath, ScriptHostSystem scriptHost = null)
    {
        _projectPath = projectPath ?? throw new ArgumentNullException(nameof(projectPath));
        _scriptHost = scriptHost;
    }

    public RoomBuildResult Build(RuntimeScene scene, RoomAsset asset)
    {
        if (scene == null) throw new ArgumentNullException(nameof(scene));
        var result = new RoomBuildResult { Asset = asset };
        foreach (float _ in BuildSteps(scene, asset, result)) { }
        return result;
    }

    /// <summary>
    /// Builds a room a piece at a time: one object is placed between each value returned, and the
    /// value is how far along the room is, from 0 to 1. The caller decides whether to carry on at
    /// once or after the next frame has been drawn. Create events still run together, after every
    /// object is placed, as one last piece. Nothing in the room may be updated or drawn until the
    /// last value has been taken.
    /// </summary>
    public IEnumerable<float> BuildSteps(RuntimeScene scene, RoomAsset asset, RoomBuildResult result)
    {
        if (scene == null) throw new ArgumentNullException(nameof(scene));
        if (result == null) throw new ArgumentNullException(nameof(result));
        result.Asset = asset;
        ApplySceneSettings(scene, asset);
        if (asset.Dimension == RoomDimension.TwoD && asset.Nodes.Any(node => node.Kind == RoomNodeKind.GameObject
                && node.GameObject != null && RoomHierarchyTransforms.IsActive(asset, node)
                && SpritePhysicsBinding.UsesSavedTwoDAsset(_projectPath, ApplyOverrides(ResolvePrefab(node.GameObject.Prefab), node.GameObject.ComponentOverrides))))
            SpritePhysicsBinding.EnsureScene(scene, asset, _projectPath);
        foreach (float done in Steps(scene.World, asset, terrainOnly: false, result)) yield return done;
        bool spritePhysics = false;
        scene.World.Query<SpritePhysicsBindingComponent>((Entity entity, ref SpritePhysicsBindingComponent binding) => spritePhysics = true);
        if (asset.Dimension == RoomDimension.TwoD && spritePhysics) SpritePhysicsBinding.EnsureScene(scene, asset, _projectPath);
        ApplyActiveGameCamera(scene, asset);
    }

    public RoomBuildResult Build(EcsWorld world, RoomAsset asset)
        => Build(world, asset, terrainOnly: false);

    /// <summary>
    /// Starts reading the models of the room's Objects on worker threads. Called before the slow
    /// parts of a room load (terrain, spawning), so the files are read during them rather than
    /// one after another when each object is first created or drawn. Returns how many distinct
    /// models were named. <paramref name="keepMilliseconds"/> is how long a model that has been
    /// read waits to be used: short for the room being loaded, long for a room read ahead of a
    /// door the player has not gone through yet.
    /// </summary>
    public int PrefetchModels(RoomAsset room, long keepMilliseconds = Modeling.RuntimeModelStore.PrefetchKeepMilliseconds)
    {
        if (room == null || room.Dimension != RoomDimension.ThreeD) return 0;
        var objects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void ReadAheadFor(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || !objects.Add(name)) return;
            JObject prefab;
            try
            {
                prefab = ResolvePrefab(name);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                or Newtonsoft.Json.JsonException or InvalidOperationException or ArgumentException or FormatException)
            {
                // Reading ahead must not be where a broken Object stops a room; creating it reports the fault.
                return;
            }

            if (prefab?["components"] is not JArray components) return;
            foreach (JObject component in components.OfType<JObject>())
            {
                if ((string)component["type"] is not ("ModelRendererComponent" or "ModelComponent")) continue;
                if (component["props"] is not JObject props) continue;
                string model = ((string)props["ModelAsset"] ?? "").Trim();
                if (model.Length == 0) model = ((string)props["Model"] ?? "").Trim();
                if (model.Length > 0 && models.Add(model))
                    Modeling.RuntimeModelAssetRegistry.Shared.Prefetch(_projectPath, model, keepMilliseconds);
            }
        }

        foreach (RoomNode node in room.Nodes)
        {
            if (node.Kind == RoomNodeKind.GameObject && node.GameObject != null)
            {
                ReadAheadFor(node.GameObject.Prefab);
                continue;
            }

            // A terrain places Objects of its own (a village's houses): they are created with the
            // room like any other, so their models are wanted just as soon.
            if (node.Kind != RoomNodeKind.Terrain || string.IsNullOrWhiteSpace(node.Terrain?.Asset)) continue;
            try
            {
                string resource = ResourceNames.Resolve(_projectPath, node.Terrain.Asset, ResourceType.Terrain);
                if (!resource.EndsWith(".terrain.json", StringComparison.OrdinalIgnoreCase))
                {
                    string binary = Project.RoomTerrainSubsystem.ResolveTerrainFile(_projectPath, node.Terrain.Asset);
                    if (binary == null) continue;
                    resource = Project.RoomTerrainSubsystem.ResolveTerrainResourcePath(binary);
                }

                foreach (var placed in Genesis.World.Terrain.TerrainNatureSerializer.LoadOrDefault(resource).PlacedEntities)
                    ReadAheadFor(placed.Entity);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                or System.Text.Json.JsonException or Newtonsoft.Json.JsonException or ArgumentException)
            {
                // The terrain reports its own faults when it loads.
            }
        }

        return models.Count;
    }

    /// <summary>Loads the authored terrain placements for a visual editor preview. No script
    /// host is needed; a preview must not execute gameplay or start sound/physics subsystems.</summary>
    public RoomBuildResult BuildTerrainParts(EcsWorld world, RoomAsset asset)
        => Build(world, asset, terrainOnly: true);

    private RoomBuildResult Build(EcsWorld world, RoomAsset asset, bool terrainOnly)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        var result = new RoomBuildResult { Asset = asset };
        foreach (float _ in Steps(world, asset, terrainOnly, result)) { }
        return result;
    }

    private IEnumerable<float> Steps(EcsWorld world, RoomAsset asset, bool terrainOnly, RoomBuildResult result)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        asset.Normalize(); RoomAssetLoader.Validate(asset);

        // Hold every OnCreate until the whole room is spawned AND positioned — SpawnGameObject
        // applies the placement transform after PrefabSpawner returns, so an inline Create would
        // read x=0,y=0 and have its own assignments overwritten a line later (NEXT-047).
        bool deferring = _scriptHost != null && !_scriptHost.DeferCreateEvents;
        if (deferring) _scriptHost.DeferCreateEvents = true;
        // Only the time spent placing objects is counted, not the frames drawn between pieces.
        long spawnTicks = 0;
        long mark = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            List<RoomNode> ordered = asset.Nodes.OrderBy(n => LayerOrder(asset, n)).ThenBy(n => n.Order).ToList();
            for (int index = 0; index < ordered.Count; index++)
            {
                RoomNode node = ordered[index];
                if (!RoomHierarchyTransforms.IsActive(asset, node)) continue;
                if (!terrainOnly && node.Kind == RoomNodeKind.GameObject && node.GameObject != null)
                {
                    if (TryDeferScenery(asset, node)) continue;
                    long one = System.Diagnostics.Stopwatch.GetTimestamp();
                    SpawnGameObject(world, asset, node, result);
                    result.RecordSpawn(node.Name ?? node.GameObject.Prefab,
                        System.Diagnostics.Stopwatch.GetElapsedTime(one).TotalMilliseconds);
                    spawnTicks += System.Diagnostics.Stopwatch.GetTimestamp() - mark;
                    yield return (index + 1f) / ordered.Count;
                    mark = System.Diagnostics.Stopwatch.GetTimestamp();
                }
                // Tile layers are rendered as batches and collide through RoomTileCollisionMap.
                // They are not gameplay instances: no per-cell ECS transforms or phantom query hits.
                else if (node.Kind == RoomNodeKind.Terrain && node.Terrain != null)
                {
                    foreach (float part in SpawnTerrainObjects(world, asset, node, result))
                    {
                        spawnTicks += System.Diagnostics.Stopwatch.GetTimestamp() - mark;
                        yield return (index + part) / ordered.Count;
                        mark = System.Diagnostics.Stopwatch.GetTimestamp();
                    }
                }
            }

            spawnTicks += System.Diagnostics.Stopwatch.GetTimestamp() - mark;
            // The Create events are one piece of their own, after a pause the caller may take.
            yield return 1f;
        }
        finally
        {
            if (deferring) _scriptHost.DeferCreateEvents = false;
        }

        result.SpawnMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(0, spawnTicks).TotalMilliseconds;
        long createStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        _scriptHost?.FlushDeferredCreates();
        result.CreateEventsMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(createStarted).TotalMilliseconds;
    }

    /// <summary>Places the Objects a terrain carries, one between each value returned; the value is the share placed, from 0 to 1.</summary>
    private IEnumerable<float> SpawnTerrainObjects(EcsWorld world, RoomAsset room, RoomNode terrain, RoomBuildResult result)
    {
        string resource = ResourceNames.Resolve(_projectPath, terrain.Terrain.Asset, ResourceType.Terrain);
        if (!resource.EndsWith(".terrain.json", StringComparison.OrdinalIgnoreCase))
        {
            string binary = Project.RoomTerrainSubsystem.ResolveTerrainFile(_projectPath, terrain.Terrain.Asset);
            if (binary == null) yield break;
            resource = Project.RoomTerrainSubsystem.ResolveTerrainResourcePath(binary);
        }
        var nature = Genesis.World.Terrain.TerrainNatureSerializer.LoadOrDefault(resource);
        var terrainTransform = ResolveWorldTransform(room, terrain);
        int placedCount = 0, placedTotal = Math.Max(1, nature.PlacedEntities.Count);
        foreach (var placed in nature.PlacedEntities)
        {
            placedCount++;
            bool isObject = ResourceNames.Resolve(_projectPath, placed.Entity, ResourceType.Object).Length > 0;
            TerrainPartDefinition part = isObject ? null : TerrainPartBinding.Load(_projectPath, placed);
            if (!isObject && part == null) continue;
            var local = new RoomTransform
            {
                X = placed.Position.X, Y = placed.Position.Y, Z = placed.Position.Z,
                RotationX = placed.Pitch, RotationY = placed.Yaw, RotationZ = placed.Roll,
                ScaleX = placed.Scale, ScaleY = placed.Scale, ScaleZ = placed.Scale,
            };
            var transform = RoomHierarchyTransforms.Compose(terrainTransform, local);
            var node = new RoomNode
            {
                Id = terrain.Id + ":" + placed.Id,
                Name = part?.Name ?? ResourceNames.Name(_projectPath, placed.Entity, ResourceType.Object),
                Kind = RoomNodeKind.GameObject, LayerId = terrain.LayerId, Transform = transform,
                GameObject = new RoomGameObjectData { Prefab = placed.Entity },
            };
            if (isObject && TryDeferScenery(room, node)) continue;
            if (isObject) SpawnGameObject(world, room, node, result);
            else SpawnResolvedObject(world, room, node, result, part.Definition, part.Events);
            if (result.EntitiesByNodeId.TryGetValue(node.Id, out Entity entity))
                result.TerrainParts.Add((entity, terrain, local));
            yield return placedCount / (float)placedTotal;
        }
    }

    /// <summary>Follows live Room placement edits without rebuilding assets or simulating game logic.</summary>
    public static void UpdateTerrainPartPreviewTransforms(EcsWorld world, RoomBuildResult preview)
    {
        foreach (var part in preview.TerrainParts)
        {
            RoomTransform placement = RoomHierarchyTransforms.Compose(RoomHierarchyTransforms.World(preview.Asset, part.Terrain), part.Local);
            ref TransformComponent transform = ref world.GetRef<TransformComponent>(part.Entity);
            transform.X = placement.X; transform.Y = placement.Y; transform.Z = placement.Z;
            transform.ScaleX = SafeScale(placement.ScaleX); transform.ScaleY = SafeScale(placement.ScaleY); transform.ScaleZ = SafeScale(placement.ScaleZ);
            transform.RotationX = placement.RotationX; transform.RotationY = placement.RotationY; transform.RotationZ = placement.RotationZ;
            transform.Rotation = placement.RotationY;
            world.Set(part.Entity, new Transform3DComponent
            {
                Position = new Vector3(transform.X, transform.Y, transform.Z),
                Rotation = Quaternion.CreateFromYawPitchRoll(transform.RotationY * MathF.PI / 180,
                    transform.RotationX * MathF.PI / 180, transform.RotationZ * MathF.PI / 180),
                Scale = new Vector3(transform.ScaleX, transform.ScaleY, transform.ScaleZ),
            });
        }
    }

    public static void ApplySceneSettings(RuntimeScene scene, RoomAsset room)
    {
        if (room.Dimension == RoomDimension.ThreeD && scene.Physics == null)
            scene.Physics = PhysicsWorld.Create(new PhysicsWorldAsset());
        RoomEnvironment environment = room.Environment ?? new RoomEnvironment();
        scene.Environment.ShadowDistance = float.IsFinite(environment.ShadowDistance)
            ? Math.Clamp(environment.ShadowDistance, 0f, 20000f)
            : 0f;
        scene.Environment.SimulationDistance = float.IsFinite(environment.SimulationDistance)
            ? Math.Clamp(environment.SimulationDistance, 0f, 100000f)
            : 0f;
        scene.Environment.SceneryDistance = float.IsFinite(environment.SceneryDistance)
            ? Math.Clamp(environment.SceneryDistance, 0f, 100000f)
            : 0f;
        scene.Environment.EnvironmentReflection = float.IsFinite(environment.EnvironmentReflection)
            ? Math.Clamp(environment.EnvironmentReflection, 0f, 4f)
            : 0f;
        float[] bg = environment.BackgroundColor;
        if (bg is { Length: >= 3 }) scene.Environment.BackgroundColor = new Vector4(bg[0], bg[1], bg[2], bg.Length > 3 ? bg[3] : 1f);
        if (scene.Physics != null && environment.Gravity is { Length: >= 3 })
        {
            Vector3 gravity = new(environment.Gravity[0], environment.Gravity[1], environment.Gravity[2]);
            float magnitude = gravity.Length();
            scene.Physics.GravityStrength = magnitude / 9.81f;
            scene.Physics.GravityDirection = magnitude > 0.000001f
                ? gravity / magnitude
                : -Vector3.UnitY;
        }
        if (environment.DynamicSky)
        {
            WeatherKind weather = Enum.TryParse(environment.Weather, true, out WeatherKind parsed)
                ? parsed
                : WeatherKind.Clear;
            scene.ConfigureClimate(new EnvironmentOptions
            {
                Seed = environment.ClimateSeed,
                StartTimeHours = environment.TimeOfDayHours,
                TimeScale = environment.TimeScale,
                DayOfYear = environment.DayOfYear,
                LatitudeDegrees = environment.LatitudeDegrees,
                AutomaticWeather = environment.AutomaticWeather,
            }, weather);
            AtmospherePreset atmosphere = Enum.TryParse(environment.AtmospherePreset, true, out AtmospherePreset authoredAtmosphere)
                ? authoredAtmosphere
                : AtmospherePreset.Natural;
            scene.ConfigureAtmosphere(new AtmosphereOptions
            {
                Preset = atmosphere,
                VolumetricClouds = environment.VolumetricClouds,
                CloudQuality = environment.CloudQuality,
                CloudBaseHeight = environment.CloudBaseHeight,
                CloudThickness = environment.CloudThickness,
                CloudCoverageScale = environment.CloudCoverageScale,
                Haze = environment.AtmosphericHaze,
                VisibilityMetres = MathF.Max(0f, environment.VisibilityKilometres) * 1000f,
                AmbientScale = float.IsFinite(environment.AmbientIntensity) ? Math.Clamp(environment.AmbientIntensity, 0f, 8f) : 1f,
                WeatherFogScale = float.IsFinite(environment.WeatherFogScale) ? Math.Clamp(environment.WeatherFogScale, 0f, 4f) : 1f,
                SunDiscScale = float.IsFinite(environment.SunDiscScale) && environment.SunDiscScale > 0f ? environment.SunDiscScale : 1f,
                Seed = environment.ClimateSeed,
            });
        }
        else
        {
            scene.DisableClimate();
            scene.DisableAtmosphere();
        }
        scene.FixedTimestep.FixedDelta = 1f / Math.Max(1, room.Settings.FixedFps);
    }

    /// <summary>
    /// When set, plain scenery is handed to it instead of being created with the room, and it
    /// creates each object when the camera comes within its distance.
    /// </summary>
    public RoomSceneryStreamer Scenery { get; set; }

    private static readonly HashSet<string> SceneryComponents = new(StringComparer.Ordinal)
    {
        "TransformComponent", "ModelRendererComponent", "ModelComponent", "Draw3DComponent",
        "MaterialComponent", "ShaderComponent",
    };

    /// <summary>
    /// True when an Object only shows a model: nothing scripted, moving or remembered, so creating
    /// it late and destroying it early loses nothing.
    /// </summary>
    public static bool IsScenery(JObject prefab)
    {
        if (prefab == null) return false;
        if (!string.IsNullOrWhiteSpace((string)prefab["physics"]) || prefab["terrainPhysics"] != null) return false;
        if ((bool?)prefab["persistent"] == true) return false;
        if (prefab["events"] is JArray { Count: > 0 }) return false;
        if (prefab["components"] is not JArray components) return false;
        bool model = false;
        foreach (JObject component in components.OfType<JObject>())
        {
            string type = (string)component["type"] ?? "";
            if (!SceneryComponents.Contains(type)) return false;
            if (type is "ModelRendererComponent" or "ModelComponent") model = true;
        }

        return model;
    }

    /// <summary>
    /// True when an Object says it may be created and destroyed with distance although it has a
    /// script, events or a body: <c>"streamable": true</c> in its definition. Its Create event
    /// runs each time it comes into range and its Destroy event each time it leaves, and it
    /// starts again from its definition each time, so it suits things that keep nothing: wildlife,
    /// torches, ambient machinery. Anything that must be remembered stays as it is.
    /// </summary>
    public static bool IsStreamable(JObject prefab) =>
        prefab != null && (bool?)prefab["streamable"] == true && (bool?)prefab["persistent"] != true;

    private bool TryDeferScenery(RoomAsset room, RoomNode node)
    {
        if (Scenery == null || room.Dimension != RoomDimension.ThreeD) return false;
        JObject prefab = ResolvePrefab(node.GameObject.Prefab);
        if (prefab == null) return false;
        prefab = ApplyOverrides(prefab, node.GameObject.ComponentOverrides);
        bool streamable = IsStreamable(prefab);
        if (!streamable && (!IsScenery(prefab) || ResolveObjectEvents(node.GameObject.Prefab) is { Count: > 0 })) return false;
        RoomTransform placed = ResolveWorldTransform(room, node);
        Scenery.Add(node, new Vector3(placed.X, placed.Y, placed.Z), streamable);
        return true;
    }

    /// <summary>Runs an Object's Destroy event and lets go of its scripts, before it is removed.</summary>
    internal void DetachScripts(Entity entity) => _scriptHost?.Detach(entity);

    private static readonly RoomAsset CopyRoom3D = RoomAsset.Create("Copies", RoomDimension.ThreeD);
    private static readonly RoomAsset CopyRoom2D = RoomAsset.Create("Copies", RoomDimension.TwoD);

    /// <summary>
    /// Creates an Object by name at a place, outside any room's own list: the visual copy of an
    /// object that lives on another machine. A builder made without a script host gives a copy
    /// that runs none of the Object's scripts.
    /// </summary>
    public Entity SpawnCopy(EcsWorld world, string prefabName, Vector3 position, Vector3 rotationDegrees, Vector3 scale)
    {
        JObject prefab = ResolvePrefab(prefabName);
        if (prefab == null) return Entity.Null;
        bool twoD = string.Equals((string)prefab["dimension"], "TwoD", StringComparison.OrdinalIgnoreCase);
        var node = new RoomNode
        {
            Id = "copy:" + Guid.NewGuid().ToString("N"), Name = prefabName, Kind = RoomNodeKind.GameObject,
            Transform = new RoomTransform
            {
                X = position.X, Y = position.Y, Z = position.Z,
                RotationX = rotationDegrees.X, RotationY = rotationDegrees.Y, RotationZ = rotationDegrees.Z,
                ScaleX = scale.X == 0f ? 1f : scale.X, ScaleY = scale.Y == 0f ? 1f : scale.Y, ScaleZ = scale.Z == 0f ? 1f : scale.Z,
            },
            GameObject = new RoomGameObjectData { Prefab = prefabName },
        };
        RoomAsset room = twoD ? CopyRoom2D : CopyRoom3D;
        var result = new RoomBuildResult { Asset = room };
        // A copy is moved by the machine that owns the object. A body of its own would fall,
        // collide and fight the positions it is sent, and a fixed collider fitted where the copy
        // first appeared would stay behind as an invisible wall when it walked away.
        _spawningCopy = true;
        try
        {
            SpawnGameObject(world, room, node, result);
        }
        finally
        {
            _spawningCopy = false;
        }

        if (!result.EntitiesByNodeId.TryGetValue(node.Id, out Entity entity)) return Entity.Null;
        if (world.Has<Genesis.Shared.ECS.Components.RigidBodyComponent>(entity))
        {
            // A body the Object declares for itself follows the copy and pushes what it touches.
            ref Genesis.Shared.ECS.Components.RigidBodyComponent body = ref world.GetRef<Genesis.Shared.ECS.Components.RigidBodyComponent>(entity);
            if (body.Motion == Genesis.Shared.ECS.Components.PhysicsMotionType.Dynamic)
                body.Motion = Genesis.Shared.ECS.Components.PhysicsMotionType.Kinematic;
        }

        return entity;
    }

    private bool _spawningCopy;

    /// <summary>Creates one deferred scenery object now and returns it.</summary>
    internal Entity SpawnScenery(EcsWorld world, RoomAsset room, RoomNode node)
    {
        var result = new RoomBuildResult { Asset = room };
        // As when a room is built: the object is put in its place before its Create event runs,
        // or the event would see it at the origin.
        bool deferring = _scriptHost != null && !_scriptHost.DeferCreateEvents;
        if (deferring) _scriptHost.DeferCreateEvents = true;
        try
        {
            SpawnGameObject(world, room, node, result);
        }
        finally
        {
            if (deferring) _scriptHost.DeferCreateEvents = false;
        }

        if (deferring) _scriptHost.FlushDeferredCreates();
        return result.EntitiesByNodeId.TryGetValue(node.Id, out Entity entity) ? entity : Entity.Null;
    }

    private void SpawnGameObject(EcsWorld world, RoomAsset room, RoomNode node, RoomBuildResult result)
    {
        JObject prefab = ResolvePrefab(node.GameObject.Prefab);
        if (prefab == null) return;
        prefab = ApplyOverrides(prefab, node.GameObject.ComponentOverrides);

        // Hand the object's own event code to the script host for the duration of this spawn, so its
        // PgslBehavior compiles from the object's folder instead of resolving a global script name
        // (NEXT-044). The scope clears itself so one object's events cannot leak onto the next.
        IReadOnlyDictionary<string, string> events = ResolveObjectEvents(node.GameObject.Prefab);
        SpawnResolvedObject(world, room, node, result, prefab, events);
    }

    private void SpawnResolvedObject(EcsWorld world, RoomAsset room, RoomNode node, RoomBuildResult result,
        JObject prefab, IReadOnlyDictionary<string, string> events)
    {
        Entity entity;
        if (events is { Count: > 0 } && _scriptHost != null)
        {
            using (_scriptHost.UseEventSources(events))
            {
                entity = PrefabSpawner.Spawn(world, prefab, _scriptHost);
            }
        }
        else
        {
            entity = PrefabSpawner.Spawn(world, prefab, _scriptHost);
        }
        RoomTransform worldTransform = ResolveWorldTransform(room, node);
        ref TransformComponent transform = ref world.GetRef<TransformComponent>(entity);
        transform.X = worldTransform.X; transform.Y = worldTransform.Y; transform.Z = worldTransform.Z;
        transform.ScaleX = SafeScale(worldTransform.ScaleX); transform.ScaleY = SafeScale(worldTransform.ScaleY); transform.ScaleZ = SafeScale(worldTransform.ScaleZ);
        transform.RotationX = worldTransform.RotationX; transform.RotationY = worldTransform.RotationY; transform.RotationZ = worldTransform.RotationZ;
        transform.Rotation = room.Dimension == RoomDimension.TwoD ? worldTransform.RotationZ : worldTransform.RotationY;
        if (room.Dimension == RoomDimension.ThreeD)
        {
            world.Set(entity, new Transform3DComponent
            {
                Position = new Vector3(transform.X, transform.Y, transform.Z),
                Rotation = Quaternion.CreateFromYawPitchRoll(
                    transform.RotationY * MathF.PI / 180f,
                    transform.RotationX * MathF.PI / 180f,
                    transform.RotationZ * MathF.PI / 180f),
                Scale = new Vector3(transform.ScaleX, transform.ScaleY, transform.ScaleZ),
            });
            if (!_spawningCopy) AttachAuthoredPhysics(world, entity, prefab, transform);
        }
        else if (!_spawningCopy) SpritePhysicsBinding.Attach(world, entity, _projectPath, prefab, transform);
        if (world.Has<WildlifeComponent>(entity))
        {
            ref WildlifeComponent wildlife = ref world.GetRef<WildlifeComponent>(entity);
            wildlife.Home = new Vector3(transform.X, transform.Y, transform.Z);
            if (world.Has<AgentComponent>(entity))
                world.GetRef<AgentComponent>(entity).Target = wildlife.Home;
        }

        // Keep the authored object identity beside its render assets. PGSL instance/collision
        // queries accept object names (for example "Coin") and must not mistake a nearby tile,
        // camera or unrelated object for the requested type.
        ObjectDrawAssetEntry drawAssets = ObjectDrawAssetRegistry.TryGet(entity, out ObjectDrawAssetEntry existing)
            ? existing
            : new ObjectDrawAssetEntry();
        drawAssets.Prefab = node.GameObject.Prefab;
        drawAssets.RoomNodeId = node.Id;
        drawAssets.InstanceName = node.Name;
        ObjectDrawAssetRegistry.Set(entity, drawAssets);

        // Room layers and per-instance order are additive offsets, preserving any depth authored
        // on the shared prefab while allowing a room to arrange instances without modifying it.
        if (room.Dimension == RoomDimension.TwoD)
        {
            int roomDepth = LayerOrder(room, node) + node.Order;
            if (world.Has<Draw2DComponent>(entity))
            {
                ref Draw2DComponent draw2D = ref world.GetRef<Draw2DComponent>(entity);
                draw2D.Depth += roomDepth;
            }
            if (world.Has<SpriteComponent>(entity))
            {
                ref SpriteComponent sprite = ref world.GetRef<SpriteComponent>(entity);
                sprite.Depth += roomDepth;
            }
        }

        result.EntitiesByNodeId[node.Id] = entity; result.SpawnedEntities.Add(entity);
    }

    public void AttachAuthoredPhysics(EcsWorld world, Entity entity, JObject prefab, TransformComponent transform)
    {
        if (prefab["terrainPhysics"] is JObject terrainPhysics)
        {
            TerrainPartBinding.AttachPhysics(world, entity, _projectPath, terrainPhysics, transform);
            return;
        }
        if ((bool?)prefab["solid"] == false || (prefab["components"] as JArray)?.OfType<JObject>()
            .Any(component => (string)component["type"] == "PhysicsComponent" && (bool?)component["enabled"] == false) == true) return;
        if (ModelPhysicsBinding.Attach(world, entity, _projectPath, prefab, transform)) return;
        string preset = (string)prefab["physics"];
        if (string.IsNullOrWhiteSpace(preset))
        {
            AttachModelCollider(world, entity, transform);
            return;
        }
        Vector3 size = new(
            MathF.Max(0.1f, MathF.Abs(transform.ScaleX) * 0.5f),
            MathF.Max(0.1f, MathF.Abs(transform.ScaleY) * 0.5f),
            MathF.Max(0.1f, MathF.Abs(transform.ScaleZ) * 0.5f));
        RigidBodyComponent rigid;
        bool character = false;
        bool topDown = false;
        switch (preset.Trim().ToLowerInvariant())
        {
            case "platformercharacter":
                rigid = RigidBodyComponent.Character(MathF.Max(0.2f, size.X), MathF.Max(0.6f, size.Y));
                character = true;
                break;
            case "topdowncharacter":
                rigid = RigidBodyComponent.Character(MathF.Max(0.2f, size.X), MathF.Max(0.6f, size.Y));
                rigid.Flags &= ~RigidBodyFlags.UseGravity;
                character = true; topDown = true;
                break;
            case "staticsolid": rigid = RigidBodyComponent.StaticBox(size); break;
            case "trigger":
                rigid = RigidBodyComponent.StaticBox(size);
                rigid.Flags |= RigidBodyFlags.Sensor;
                break;
            case "projectile":
                rigid = RigidBodyComponent.DynamicBox(size, 0.25f);
                rigid.Shape = Genesis.Shared.ECS.Components.CollisionShape.Sphere;
                break;
            default: rigid = RigidBodyComponent.DynamicBox(size); break;
        }
        world.Set(entity, rigid);
        if (!character) return;
        world.Set(entity, new PhysicsPlayerTag());
        CharacterMotorComponent motor = CharacterMotorComponent.Default;
        JObject authored = prefab["characterMotor"] as JObject;
        if (authored != null)
        {
            motor.WalkSpeed = Float(authored, "walkSpeed", motor.WalkSpeed);
            motor.SprintMultiplier = Float(authored, "sprintMultiplier", motor.SprintMultiplier);
            motor.GroundAcceleration = Float(authored, "groundAcceleration", motor.GroundAcceleration);
            motor.AirAcceleration = Float(authored, "airAcceleration", motor.AirAcceleration);
            motor.JumpSpeed = Float(authored, "jumpSpeed", motor.JumpSpeed);
            motor.StepHeight = Float(authored, "stepHeight", motor.StepHeight);
            motor.MaximumSlopeDegrees = Float(authored, "maximumSlopeDegrees", motor.MaximumSlopeDegrees);
            motor.SwimSpeed = Float(authored, "swimSpeed", motor.SwimSpeed);
            motor.SwimVerticalSpeed = Float(authored, "swimVerticalSpeed", motor.SwimVerticalSpeed);
            motor.SwimAcceleration = Float(authored, "swimAcceleration", motor.SwimAcceleration);
            motor.SwimDrag = Float(authored, "swimDrag", motor.SwimDrag);
        }
        if (topDown) motor.JumpSpeed = 0f;
        world.Set(entity, motor);
    }

    private void AttachModelCollider(EcsWorld world, Entity entity, TransformComponent transform)
    {
        if (!world.Has<ModelRendererComponent>(entity)) return;
        ModelRendererComponent renderer = world.GetRef<ModelRendererComponent>(entity);
        string modelName = renderer.ModelAsset;
        if (string.IsNullOrWhiteSpace(modelName)) return;
        // The shared registry: a fresh one here parsed the model file again for every object
        // that uses it, on every room load.
        GModelAsset model = RuntimeModelAssetRegistry.Shared.Load(_projectPath, modelName);
        if (!GModelProductionTools.TryCreateRigidBody(model, out RigidBodyComponent rigid)) return;
        Vector3 scale = new Vector3(SafeScale(transform.ScaleX), SafeScale(transform.ScaleY), SafeScale(transform.ScaleZ))
            * new Vector3(SafeScale(renderer.ScaleX), SafeScale(renderer.ScaleY), SafeScale(renderer.ScaleZ));
        Vector3 absolute = Vector3.Abs(scale);
        rigid.Size = rigid.Shape switch
        {
            Genesis.Shared.ECS.Components.CollisionShape.Sphere => new Vector3(rigid.Size.X * MathF.Max(absolute.X, MathF.Max(absolute.Y, absolute.Z))),
            Genesis.Shared.ECS.Components.CollisionShape.Capsule or Genesis.Shared.ECS.Components.CollisionShape.Cylinder
                => new Vector3(rigid.Size.X * MathF.Max(absolute.X, absolute.Z), rigid.Size.Y * absolute.Y, rigid.Size.Z * absolute.Z),
            _ => rigid.Size * absolute,
        };
        if (rigid.Shape is Genesis.Shared.ECS.Components.CollisionShape.Mesh or Genesis.Shared.ECS.Components.CollisionShape.ConvexHull)
            ModelColliderBinding.AttachGeometry(world, entity, model, scale);
        else rigid.LocalOffset = (model.Colliders[0].Center - (model.Pivot?.Position ?? Vector3.Zero)) * scale;
        world.Set(entity, rigid);
    }

    private static float Float(JObject value, string name, float fallback) =>
        value[name]?.Value<float?>() is float parsed && float.IsFinite(parsed) ? parsed : fallback;

    public RoomTransform ResolveWorldTransform(RoomAsset room, RoomNode node) => RoomHierarchyTransforms.World(room, node);
    /// <summary>
    /// Resolves the selected/active room camera through its parent transform and per-instance
    /// component overrides. The same result drives runtime play and Studio's Game Camera view.
    /// </summary>
    public RoomCameraState ResolveCameraState(RoomAsset room, RoomNode node = null)
    {
        if (room == null) throw new ArgumentNullException(nameof(room));
        node ??= room.Nodes.FirstOrDefault(n => n.Id == room.ActiveGameCameraId);
        if (node?.Kind != RoomNodeKind.GameObject || node.GameObject == null) return null;

        RoomTransform transform = ResolveWorldTransform(room, node);
        RoomCameraState state = new RoomCameraState
        {
            Node = node,
            Position = new Vector3(transform.X, transform.Y, transform.Z),
            YawRadians = transform.RotationY * MathF.PI / 180f,
            PitchRadians = transform.RotationX * MathF.PI / 180f,
        };

        JObject prefab = ApplyOverrides(ResolvePrefab(node.GameObject.Prefab), node.GameObject.ComponentOverrides);
        string componentType = room.Dimension == RoomDimension.TwoD ? "Camera2DComponent" : "Camera3DComponent";
        JObject camera = (prefab?["components"] as JArray)?.OfType<JObject>()
            .FirstOrDefault(c => string.Equals(c["type"]?.ToString(), componentType, StringComparison.OrdinalIgnoreCase));
        state.HasCameraComponent = camera != null;
        JObject props = camera?["props"] as JObject;

        if (room.Dimension == RoomDimension.TwoD)
        {
            state.Zoom2D = Math.Clamp(PropFloat(props, "Zoom", 1f), .05f, 40f);
        }
        else
        {
            state.FieldOfViewDegrees = Math.Clamp(PropFloat(props, "FOV", 60f), 1f, 179f);
            state.NearPlane = Math.Max(.001f, PropFloat(props, "Near", .1f));
            state.FarPlane = Math.Max(state.NearPlane + 1f, PropFloat(props, "Far", 500f));
        }

        return state;
    }

    /// <summary>
    /// The event scripts stored in an object's own folder, cached per prefab reference.
    /// </summary>
    private IReadOnlyDictionary<string, string> ResolveObjectEvents(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_objectEvents.TryGetValue(name, out Dictionary<string, string> cached)) return cached;

        string path = ResolvePrefabPath(_projectPath, name);
        Dictionary<string, string> events = path == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : ObjectDefinitionResolver.Load(_projectPath, path).Events;

        _objectEvents[name] = events;
        return events;
    }

    private JObject ResolvePrefab(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_prefabs.TryGetValue(name, out JObject cached)) return cached;
        string path = ResolvePrefabPath(_projectPath, name);
        if (path == null || !File.Exists(path)) return null;
        // The Object editor exposes a Model binding as ordinary Create-event PGSL. Resolve its
        // literal initial visual before body attachment, so Physics uses the same Model/pivot
        // as authoring previews even after the Object's hidden compatibility fields are cleared.
        JObject prefab = ObjectDefinitionResolver.PreviewPrefab(ObjectDefinitionResolver.Load(_projectPath, path));
        _prefabs[name] = prefab;
        return prefab;
    }

    /// <summary>
    /// Resolves the unique Object resource name. Old storage references are accepted only at this compatibility boundary.
    /// </summary>
    public static string ResolvePrefabPath(string projectPath, string name)
    {
        string path = ResourceNames.Resolve(projectPath, name, ResourceType.Object);
        return path.Length == 0 ? null : path;
    }

    private void ApplyActiveGameCamera(RuntimeScene scene, RoomAsset room)
    {
        if (room.Dimension != RoomDimension.ThreeD) return;

        RoomCameraState state = ResolveCameraState(room);
        if (state != null && state.HasCameraComponent)
        {
            scene.Camera3D.Position = state.Position;
            scene.Camera3D.Yaw = state.YawRadians;
            scene.Camera3D.Pitch = state.PitchRadians;
            scene.Camera3D.FieldOfView = state.FieldOfViewDegrees * MathF.PI / 180f;
            scene.Camera3D.NearPlane = state.NearPlane;
            scene.Camera3D.FarPlane = state.FarPlane;
        }

        // Apply Viewport config if present
        RoomViewport viewport = room.Viewports?.FirstOrDefault(v => v.Enabled);
        if (viewport != null)
        {
            if (viewport.FieldOfView > 0) scene.Camera3D.FieldOfView = viewport.FieldOfView * MathF.PI / 180f;
            if (viewport.FrustumNear > 0) scene.Camera3D.NearPlane = viewport.FrustumNear;
            if (viewport.FrustumFar > viewport.FrustumNear) scene.Camera3D.FarPlane = viewport.FrustumFar;
        }
    }

    public float ResolveCamera2DZoom(RoomAsset room, RoomNode node = null)
    {
        return ResolveCameraState(room, node)?.Zoom2D ?? 1f;
    }

    private static float PropFloat(JObject props, string name, float fallback) =>
        float.TryParse(props?[name]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value) ? value : fallback;

    public static JObject ApplyOverrides(JObject source, List<RoomComponentOverride> overrides)
    {
        if (source == null) return null;
        var clone = (JObject)source.DeepClone();
        if (overrides == null || clone["components"] is not JArray components) return clone;
        foreach (RoomComponentOverride ov in overrides)
        {
            JObject component = components.OfType<JObject>().FirstOrDefault(c =>
                string.Equals(c["id"]?.ToString(), ov.ComponentId, StringComparison.OrdinalIgnoreCase)
                || (c["id"] == null && string.Equals(c["type"]?.ToString(), ov.ComponentId, StringComparison.OrdinalIgnoreCase)));
            if (component == null) continue;
            if (ov.Enabled.HasValue) component["enabled"] = ov.Enabled.Value;
            var props = component["props"] as JObject;
            if (props == null) { props = new JObject(); component["props"] = props; }
            foreach ((string key, JToken value) in ov.Properties) props[key] = ToComponentString(value);
        }
        return clone;
    }

    private static string ToComponentString(JToken value) => value?.Type switch
    {
        JTokenType.Boolean => value.Value<bool>() ? "true" : "false",
        JTokenType.Float => value.Value<double>().ToString(CultureInfo.InvariantCulture),
        JTokenType.Integer => value.Value<long>().ToString(CultureInfo.InvariantCulture),
        _ => value?.ToString() ?? "",
    };

    public static int LayerOrder(RoomAsset room, RoomNode node) => room.Layers.FirstOrDefault(l => l.Id == node.LayerId)?.Order ?? 0;
    private static float SafeScale(float value) => Math.Abs(value) < .0001f ? 1f : value;
}
