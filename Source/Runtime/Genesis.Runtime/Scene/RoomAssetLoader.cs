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
        ApplySceneSettings(scene, asset);
        RoomBuildResult result = Build(scene.World, asset);
        ApplyActiveGameCamera(scene, asset);
        return result;
    }

    public RoomBuildResult Build(EcsWorld world, RoomAsset asset)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        asset.Normalize(); RoomAssetLoader.Validate(asset);
        var result = new RoomBuildResult { Asset = asset };

        // Hold every OnCreate until the whole room is spawned AND positioned — SpawnGameObject
        // applies the placement transform after PrefabSpawner returns, so an inline Create would
        // read x=0,y=0 and have its own assignments overwritten a line later (NEXT-047).
        bool deferring = _scriptHost != null && !_scriptHost.DeferCreateEvents;
        if (deferring) _scriptHost.DeferCreateEvents = true;
        try
        {
            foreach (RoomNode node in asset.Nodes.OrderBy(n => LayerOrder(asset, n)).ThenBy(n => n.Order))
            {
                if (!RoomHierarchyTransforms.IsActive(asset, node)) continue;
                if (node.Kind == RoomNodeKind.GameObject && node.GameObject != null)
                    SpawnGameObject(world, asset, node, result);
                // Tile layers are rendered as batches and collide through RoomTileCollisionMap.
                // They are not gameplay instances: no per-cell ECS transforms or phantom query hits.
                else if (node.Kind == RoomNodeKind.Terrain && node.Terrain != null)
                    SpawnTerrainObjects(world, asset, node, result);
            }
        }
        finally
        {
            if (deferring) _scriptHost.DeferCreateEvents = false;
        }

        _scriptHost?.FlushDeferredCreates();
        return result;
    }

    private void SpawnTerrainObjects(EcsWorld world, RoomAsset room, RoomNode terrain, RoomBuildResult result)
    {
        string resource = ResourceNames.Resolve(_projectPath, terrain.Terrain.Asset, ResourceType.Terrain);
        if (!resource.EndsWith(".terrain.json", StringComparison.OrdinalIgnoreCase))
        {
            string binary = Project.RoomTerrainSubsystem.ResolveTerrainFile(_projectPath, terrain.Terrain.Asset);
            if (binary == null) return;
            resource = Project.RoomTerrainSubsystem.ResolveTerrainResourcePath(binary);
        }
        var nature = Genesis.World.Terrain.TerrainNatureSerializer.LoadOrDefault(resource);
        var terrainTransform = ResolveWorldTransform(room, terrain);
        foreach (var placed in nature.PlacedEntities)
        {
            if (ResourceNames.Resolve(_projectPath, placed.Entity, ResourceType.Object).Length == 0) continue;
            var transform = RoomHierarchyTransforms.Compose(terrainTransform, new RoomTransform
            {
                X = placed.Position.X, Y = placed.Position.Y, Z = placed.Position.Z,
                RotationX = placed.Pitch, RotationY = placed.Yaw, RotationZ = placed.Roll,
                ScaleX = placed.Scale, ScaleY = placed.Scale, ScaleZ = placed.Scale,
            });
            SpawnGameObject(world, room, new RoomNode
            {
                Id = terrain.Id + ":" + placed.Id, Name = ResourceNames.Name(_projectPath, placed.Entity, ResourceType.Object),
                Kind = RoomNodeKind.GameObject, LayerId = terrain.LayerId, Transform = transform,
                GameObject = new RoomGameObjectData { Prefab = placed.Entity },
            }, result);
        }
    }

    public static void ApplySceneSettings(RuntimeScene scene, RoomAsset room)
    {
        if (room.Dimension == RoomDimension.ThreeD && scene.Physics == null)
            scene.Physics = PhysicsWorld.Create(new PhysicsWorldAsset());
        RoomEnvironment environment = room.Environment ?? new RoomEnvironment();
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

    private void SpawnGameObject(EcsWorld world, RoomAsset room, RoomNode node, RoomBuildResult result)
    {
        JObject prefab = ResolvePrefab(node.GameObject.Prefab);
        if (prefab == null) return;
        prefab = ApplyOverrides(prefab, node.GameObject.ComponentOverrides);

        // Hand the object's own event code to the script host for the duration of this spawn, so its
        // PgslBehavior compiles from the object's folder instead of resolving a global script name
        // (NEXT-044). The scope clears itself so one object's events cannot leak onto the next.
        IReadOnlyDictionary<string, string> events = ResolveObjectEvents(node.GameObject.Prefab);
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
            AttachAuthoredPhysics(world, entity, prefab, transform);
        }
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
        if ((bool?)prefab["solid"] == false) return;
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
        string modelName = world.GetRef<ModelRendererComponent>(entity).ModelAsset;
        if (string.IsNullOrWhiteSpace(modelName)) return;
        GModelAsset model = new RuntimeModelAssetRegistry().Load(_projectPath, modelName);
        if (!GModelProductionTools.TryCreateRigidBody(model, out RigidBodyComponent rigid)) return;
        rigid.Size *= new Vector3(
            MathF.Max(0.01f, MathF.Abs(transform.ScaleX)),
            MathF.Max(0.01f, MathF.Abs(transform.ScaleY)),
            MathF.Max(0.01f, MathF.Abs(transform.ScaleZ)));
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
        JObject prefab = ObjectDefinitionResolver.Load(_projectPath, path).Prefab;
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
        float.TryParse(props[name]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : fallback;

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
