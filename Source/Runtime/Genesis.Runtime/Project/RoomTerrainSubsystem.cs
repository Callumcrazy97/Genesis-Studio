using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Genesis.Runtime.Core;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Interfaces;
using Genesis.Streaming;
using Genesis.World;
using Genesis.World.Foliage;
using Genesis.World.Navigation;
using Genesis.World.Terrain;
using Genesis.World.Water;
using Genesis.Physics;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.ECS.Components;
using Genesis.Shared.ECS;

namespace Genesis.Runtime.Project;

/// <summary>
/// Runtime terrain placement plus its Genesis-native natural-world sidecar: routes, resident
/// instanced foliage, water simulation, manifest/query and map-discovery foundations.
/// </summary>
public sealed partial class RoomTerrainSubsystem : ISceneSubsystem, IStreamingProvider, IRoomWarmUpSubsystem
{
    private sealed class WaterRuntime
    {
        public TerrainWaterDefinition Definition;
        public WaterBody Body;
        public WaterBodySimulation Simulation;
        public double LastRainImpulse;
    }

    private sealed class Entry
    {
        public RoomAsset Room;
        public RoomNode Node;
        public string BinaryPath;
        public string ResourcePath;
        public TerrainMaterialState Material = new();
        public AuthoredTerrainGround Ground;
        public TerrainAsset Terrain;
        public int ColliderRegistrationId;
        /// <summary>A small terrain's collision being prepared on a worker thread; null once it is solid, and for a large terrain.</summary>
        public System.Threading.Tasks.Task<PhysicsWorld.PreparedStaticMesh> ColliderPreparing;
        /// <summary>Where a small terrain's prepared collision is kept between runs, in the project's own cache folder.</summary>
        public string ColliderCacheDirectory;
        /// <summary>
        /// True for a terrain that arrived as the camera neared it. Nothing stands on it yet, so
        /// it becomes solid when its collision is ready rather than holding a frame up for it.
        /// </summary>
        public bool ColliderMayWait;
        /// <summary>Streamed collision for terrains too large for one mesh; null otherwise.</summary>
        public TerrainColliderTiles ColliderTiles;
        public int ColliderRebuildGeneration;
        public readonly List<string> WaterVolumeIds = new();
        public bool Bound;
        public TerrainNatureDocument Nature;
        public TerrainScatterRenderer Scatter;
        public TerrainScatterColliders ScatterColliders;
        public FoliageField Foliage;
        public FoliageStreamingPlanner FoliagePlanner;
        public FoliagePerformanceSnapshot FoliagePerformance;
        public MeshInstanceData[] FoliageInstanceBuffer = Array.Empty<MeshInstanceData>();
        /// <summary>Grass grown around the camera from the nature document's rule; null while the rule is off.</summary>
        public TerrainGrassField GrassField;
        public WorldManifest Manifest;
        public WorldQuery Query;
        public WorldManifestStreamingProvider Streamer;
        public readonly Dictionary<(FoliageSpecies Species, bool Near), MeshHandle> FoliageMeshes = new();
        public readonly List<MeshHandle> PathMeshes = new();
        public readonly List<WaterRuntime> Waters = new();
        public readonly List<Entity> WaterImpactEntities = new();
        public bool WaterImpactEntitiesInitialized;
        public WaterMeshCache WaterCache;
        public IRenderController Renderer;
        public Dictionary<string, string> ComponentShaders = new(StringComparer.OrdinalIgnoreCase);
        public FaceCullingOverride Culling = FaceCullingOverride.Default;
        public FrontFaceWindingOverride WindingOrder = FrontFaceWindingOverride.Default;
    }

    private readonly string _projectPath;
    private readonly IGameContext _game;
    private readonly List<Entry> _entries = new();
    private bool _worldAssigned;

    public RoomTerrainSubsystem(string projectPath, RoomAsset room, IGameContext game)
    {
        _projectPath = projectPath ?? throw new ArgumentNullException(nameof(projectPath));
        ArgumentNullException.ThrowIfNull(room);
        _game = game;
        float terrainDistance = room.Environment?.TerrainDistance ?? 0f;
        _terrainDistance = float.IsFinite(terrainDistance) ? Math.Clamp(terrainDistance, 0f, 1000000f) : 0f;
        foreach (RoomNode node in room.Nodes)
        {
            if (node.Kind != RoomNodeKind.Terrain || !node.Enabled || !node.Supports(room.Dimension) ||
                room.Layers.Find(layer => layer.Id == node.LayerId)?.Enabled == false ||
                string.IsNullOrWhiteSpace(node.Terrain?.Asset)) continue;
            string binaryPath = ResolveTerrainFile(_projectPath, node.Terrain.Asset);
            if (binaryPath == null) continue;
            if (_terrainDistance > 0f && TryReadFootprint(room, node, binaryPath, out Vector2 min, out Vector2 max))
            {
                // Left for the camera to come near; see UpdateTerrainStreaming.
                _streamed.Add(new StreamedTerrain { Room = room, Node = node, BinaryPath = binaryPath, Min = min, Max = max });
                continue;
            }

            _entries.Add(CreateEntry(room, node, binaryPath, preloadModels: true));
        }

        // A running game wants these terrains solid by its first step. Workers start on that now,
        // while the room's objects are placed, instead of the first step building it all.
        if (game != null)
            foreach (Entry entry in _entries) PrepareCollider(entry);
    }

    /// <summary>
    /// Starts building a small terrain's collision on a worker thread, once. A large terrain is
    /// made solid in tiles around what can touch it and needs nothing here.
    /// </summary>
    private static void PrepareCollider(Entry entry)
    {
        if (entry.ColliderPreparing != null || entry.ColliderRegistrationId != 0 || TerrainLodGround.Applies(entry.Terrain)) return;
        TerrainAsset terrain = entry.Terrain;
        RoomTransform transform = RoomHierarchyTransforms.World(entry.Room, entry.Node);
        var scale = new Vector3(transform.ScaleX, transform.ScaleY, transform.ScaleZ);
        string cache = entry.ColliderCacheDirectory;
        entry.ColliderPreparing = System.Threading.Tasks.Task.Run(() =>
        {
            Vector3[] vertices;
            int[] indices;
            using (Genesis.Shared.Diagnostics.LoadProfile.Begin("terrain collision: triangles made on a worker"))
                TerrainColliderMesh.Build(terrain, out vertices, out indices);
            // Building the physics engine's search tree over a terrain's triangles is seconds of
            // work; the same triangles give the same tree, so it is kept and read back next time.
            using (Genesis.Shared.Diagnostics.LoadProfile.Begin("terrain collision: physics tree made or read on a worker"))
                return PhysicsWorld.PrepareStaticTriangleMesh(vertices, indices, scale, cache);
        });
    }

    /// <summary>
    /// Makes a small terrain solid from the collision a worker prepared. Returns false when that
    /// is not ready and <paramref name="wait"/> is false.
    /// </summary>
    private static bool TryRegisterTerrainCollider(PhysicsWorld physics, Entry entry, bool wait)
    {
        if (entry.ColliderRegistrationId != 0) return true;
        PrepareCollider(entry);
        System.Threading.Tasks.Task<PhysicsWorld.PreparedStaticMesh> preparing = entry.ColliderPreparing;
        if (!preparing.IsCompleted && !wait) return false;
        using var timed = Genesis.Shared.Assets.LoadClock.Measure(Genesis.Shared.Assets.LoadWork.TerrainCollider);
        entry.ColliderPreparing = null;
        PhysicsWorld.PreparedStaticMesh prepared = preparing.GetAwaiter().GetResult();
        RoomTransform transform = RoomHierarchyTransforms.World(entry.Room, entry.Node);
        entry.ColliderRegistrationId = physics.RegisterStaticTriangleMesh(
            prepared,
            new Vector3(transform.X, transform.Y, transform.Z),
            Quaternion.CreateFromYawPitchRoll(
                transform.RotationY * MathF.PI / 180f,
                transform.RotationX * MathF.PI / 180f,
                transform.RotationZ * MathF.PI / 180f),
            $"Terrain:{entry.Node.Name}");
        return true;
    }

    /// <summary>Lets go of collision a worker is preparing, or has prepared, for heights that are no longer wanted.</summary>
    private static void DiscardPreparedCollider(Entry entry)
    {
        System.Threading.Tasks.Task<PhysicsWorld.PreparedStaticMesh> preparing = entry.ColliderPreparing;
        entry.ColliderPreparing = null;
        preparing?.ContinueWith(static task =>
        {
            if (task.IsCompletedSuccessfully) task.Result?.Dispose();
        }, System.Threading.Tasks.TaskScheduler.Default);
    }

    /// <summary>
    /// Prepares the terrain for a room that is about to be shown: reads the terrains near the
    /// camera and makes the ground and the scattered objects around the camera and every moving
    /// body solid, all on worker threads. Returns true when none of that is still under way.
    /// </summary>
    public bool WarmUp(RuntimeScene scene)
    {
        bool ready = true;
        UpdateTerrainStreaming(scene, inBackground: true);
        foreach (StreamedTerrain terrain in _streamed)
            if (terrain.Loading != null) ready = false;
        if (scene.Physics == null) return ready;
        foreach (Entry entry in _entries)
        {
            if (TerrainLodGround.Applies(entry.Terrain))
            {
                UpdateColliderTiles(scene, entry, groundBeneathAtOnce: false);
                ready &= entry.ColliderTiles.Settled;
            }
            else
            {
                ready &= TryRegisterTerrainCollider(scene.Physics, entry, wait: false);
            }

            UpdateScatterColliders(scene, entry, firstCellsAtOnce: false);
            ready &= !entry.ScatterColliders.HasLayers || entry.ScatterColliders.Settled;
            if (entry.WaterVolumeIds.Count == 0)
                RegisterWaterVolumes(scene, entry);
        }

        return ready;
    }

    /// <summary>
    /// Reads one terrain and everything that belongs to it. Touches no state of this subsystem and
    /// no GPU, so a streamed terrain can be read on a worker thread.
    /// </summary>
    private Entry CreateEntry(RoomAsset room, RoomNode node, string binaryPath, bool preloadModels)
    {
        TerrainAsset terrain = TerrainAsset.Load(binaryPath);
        string resourcePath = ResolveTerrainResourcePath(binaryPath);
        TerrainNatureDocument nature;
        try { nature = TerrainNatureSerializer.LoadOrDefault(resourcePath); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            Console.WriteLine($"[Terrain] Nature sidecar '{resourcePath}' could not be loaded: {exception.Message}");
            nature = new TerrainNatureDocument();
        }

        foreach (TerrainWaterDefinition definition in nature.WaterBodies)
            TerrainRiverSystem.ConformStandingWater(terrain, definition);

        var manifest = WorldManifest.Build(terrain, nature);
        var query = new WorldQuery(terrain, manifest);
        var entry = new Entry
        {
            Room = room,
            Node = node,
            BinaryPath = binaryPath,
            ResourcePath = resourcePath,
            ColliderCacheDirectory = Path.Combine(_projectPath, ".genesis", "Cache", "Colliders"),
            Terrain = terrain,
            Ground = new AuthoredTerrainGround(terrain),
            Nature = nature,
            Foliage = LoadFoliage(resourcePath, nature),
            Manifest = manifest,
            Query = query,
            Streamer = new WorldManifestStreamingProvider(manifest),
        };
        (entry.Culling, entry.WindingOrder) = LoadRasterOverrides(resourcePath);
        entry.FoliagePlanner = new FoliageStreamingPlanner(
            entry.Foliage,
            nature.FoliageSettings.StreamingCellSize);
        entry.Scatter = new TerrainScatterRenderer(_projectPath, terrain, nature.ScatterLayers, preloadModels);
        entry.ComponentShaders = TerrainNatureSerializer.LoadComponentShaders(resourcePath);
        foreach (TerrainWaterDefinition definition in nature.WaterBodies)
        {
            WaterBody body = definition.ToWaterBody();
            WaterBodySimulation simulation = body.SimulationEnabled
                ? new WaterBodySimulation(body, (x, z) => MathF.Max(0.02f, body.SurfaceY - terrain.SampleHeight(x, z)))
                : null;
            entry.Waters.Add(new WaterRuntime { Definition = definition, Body = body, Simulation = simulation });
        }
        return entry;
    }

    private sealed class StreamedTerrain
    {
        public RoomAsset Room;
        public RoomNode Node;
        public string BinaryPath;
        /// <summary>The terrain's footprint on the ground, in world space.</summary>
        public Vector2 Min, Max;
        public System.Threading.Tasks.Task<Entry> Loading;
        public Entry Entry;
        public bool Failed;
    }

    private readonly List<StreamedTerrain> _streamed = new();
    private readonly float _terrainDistance;
    private bool _terrainsPrimed;

    /// <summary>Terrains this room streams by distance, and how many of them are loaded now.</summary>
    public (int Total, int Loaded) StreamedTerrainCounts
    {
        get
        {
            int loaded = 0;
            foreach (StreamedTerrain terrain in _streamed) if (terrain.Entry != null) loaded++;
            return (_streamed.Count, loaded);
        }
    }

    /// <summary>The ground a terrain file covers, from its header alone.</summary>
    private static bool TryReadFootprint(RoomAsset room, RoomNode node, string binaryPath, out Vector2 min, out Vector2 max)
    {
        min = max = default;
        try
        {
            Span<byte> header = stackalloc byte[36];
            using (FileStream stream = File.OpenRead(binaryPath))
                if (stream.Read(header) != header.Length) return false;
            if (BitConverter.ToInt32(header) != 0x4E525447) return false;
            int resolutionX = BitConverter.ToInt32(header[8..]), resolutionZ = BitConverter.ToInt32(header[12..]);
            float cell = BitConverter.ToSingle(header[16..]), originX = BitConverter.ToSingle(header[20..]), originZ = BitConverter.ToSingle(header[24..]);
            if (resolutionX < 2 || resolutionZ < 2 || !(cell > 0f)) return false;
            Matrix4x4 placement = RoomHierarchyTransforms.Matrix(RoomHierarchyTransforms.World(room, node));
            min = new Vector2(float.MaxValue);
            max = new Vector2(float.MinValue);
            for (int corner = 0; corner < 4; corner++)
            {
                Vector3 world = Vector3.Transform(new Vector3(
                    originX + ((corner & 1) == 0 ? 0f : (resolutionX - 1) * cell), 0f,
                    originZ + ((corner & 2) == 0 ? 0f : (resolutionZ - 1) * cell)), placement);
                min = Vector2.Min(min, new Vector2(world.X, world.Z));
                max = Vector2.Max(max, new Vector2(world.X, world.Z));
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Loads terrains the camera has come within the room's terrain distance of, and unloads the
    /// ones it has left well behind. The first pass loads at once so a room never opens on a
    /// hole; later terrains are read on a worker and joined when ready.
    /// </summary>
    /// <param name="inBackground">
    /// True while the room waits behind a loading screen: terrains in range are read on worker
    /// threads. The first update after that still reads, or waits for, whatever is in range of
    /// where the camera then is, so the ground is never missing when the room is shown.
    /// </param>
    private void UpdateTerrainStreaming(RuntimeScene scene, bool inBackground = false)
    {
        if (_streamed.Count == 0) return;
        Vector3 camera = scene.Camera3D.Position;
        float unload = _terrainDistance * 1.25f + 64f;
        foreach (StreamedTerrain terrain in _streamed)
        {
            float dx = MathF.Max(0f, MathF.Max(terrain.Min.X - camera.X, camera.X - terrain.Max.X));
            float dz = MathF.Max(0f, MathF.Max(terrain.Min.Y - camera.Z, camera.Z - terrain.Max.Y));
            float distance = MathF.Sqrt(dx * dx + dz * dz);
            if (terrain.Entry != null)
            {
                if (distance <= unload) continue;
                ReleaseEntry(scene, terrain.Entry);
                _entries.Remove(terrain.Entry);
                terrain.Entry = null;
                _worldAssigned = false;
                continue;
            }

            // Read in the background for a camera that then turned away: let the terrain go rather
            // than hold a hundred megabytes for a visit that may never come.
            if (distance > unload && terrain.Loading is { IsCompleted: true }) terrain.Loading = null;
            if (terrain.Failed || distance > _terrainDistance) continue;
            if (!_terrainsPrimed && !inBackground)
            {
                try
                {
                    // A worker may already be reading it for the loading screen: take its result.
                    System.Threading.Tasks.Task<Entry> underWay = terrain.Loading;
                    terrain.Loading = null;
                    terrain.Entry = underWay != null
                        ? underWay.GetAwaiter().GetResult()
                        : CreateEntry(terrain.Room, terrain.Node, terrain.BinaryPath, preloadModels: true);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    Console.WriteLine($"[Terrain] '{terrain.BinaryPath}' could not be loaded: {exception.Message}");
                    terrain.Failed = true;
                    continue;
                }
            }
            else if (terrain.Loading == null)
            {
                StreamedTerrain pending = terrain;
                terrain.Loading = System.Threading.Tasks.Task.Run(() =>
                    CreateEntry(pending.Room, pending.Node, pending.BinaryPath, preloadModels: false));
                continue;
            }
            else if (!terrain.Loading.IsCompleted)
            {
                continue;
            }
            else
            {
                System.Threading.Tasks.Task<Entry> loading = terrain.Loading;
                terrain.Loading = null;
                if (!loading.IsCompletedSuccessfully)
                {
                    Console.WriteLine($"[Terrain] '{terrain.BinaryPath}' could not be loaded: {loading.Exception?.GetBaseException().Message}");
                    terrain.Failed = true;
                    continue;
                }

                terrain.Entry = loading.Result;
                // It arrived at a distance: nothing stands on it, so its collision need not hold a frame up.
                terrain.Entry.ColliderMayWait = _terrainsPrimed;
            }

            _entries.Add(terrain.Entry);
            _worldAssigned = false;
        }

        if (!inBackground) _terrainsPrimed = true;
    }

    public string Name => "Authored terrain world";

    /// <summary>
    /// Level-of-detail multiplier for large terrains: 1 is the default, 2 keeps full detail twice
    /// as far from the camera, 0.5 half as far.
    /// </summary>
    public float TerrainDetail { get; set; } = 1f;

    /// <summary>Level-of-detail counts for each terrain, in Room order (zero for small terrains).</summary>
    public IReadOnlyList<TerrainLodStatistics> TerrainLodStatistics =>
        _entries.Select(entry => entry.Ground?.LodStatistics ?? default).ToArray();
    public StreamingStats Stats { get; } = new();
    public int ColliderRebuildGeneration { get; private set; }
    public IWorldQuery WorldQuery => _entries.Count > 0 ? _entries[0].Query : null;
    /// <summary>
    /// Canonical authored counts loaded from each terrain foliage cache. Exposed for runtime
    /// diagnostics so the editor, F5 player, and profiler can prove they consumed the same asset.
    /// </summary>
    /// <summary>What each terrain's scatter layers drew in the last frame.</summary>
    public IReadOnlyList<TerrainScatterStatistics> ScatterStatistics =>
        _entries.Select(entry => entry.Scatter?.Statistics ?? default).ToArray();

    /// <summary>True while scattered cells the camera can see are still being made.</summary>
    public bool ScatterBusy => _entries.Exists(entry => entry.Scatter?.Busy == true);

    /// <summary>Applies view settings to every terrain's scatter.</summary>
    public void ConfigureScatter(float nearDistance, float midDistance, float maximumDistance)
    {
        foreach (Entry entry in _entries)
        {
            if (entry.Scatter == null) continue;
            entry.Scatter.NearDistance = MathF.Max(32f, nearDistance);
            entry.Scatter.MidDistance = MathF.Max(entry.Scatter.NearDistance, midDistance);
            entry.Scatter.MaximumDistance = MathF.Max(entry.Scatter.MidDistance, maximumDistance);
        }
    }

    public IReadOnlyList<int> AuthoredFoliageInstanceCounts =>
        _entries.Select(entry => entry.Foliage?.Instances?.Count ?? 0).ToArray();
    public IReadOnlyList<FoliagePerformanceSnapshot> FoliagePerformance =>
        _entries.Select(entry => entry.FoliagePerformance).ToArray();

    public string GetComponentShader(string targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId) || _entries.Count == 0) return string.Empty;
        return _entries[0].ComponentShaders != null
            && _entries[0].ComponentShaders.TryGetValue(targetId, out string path)
            ? path ?? string.Empty
            : string.Empty;
    }

    public static string ResolveTerrainFile(string projectPath, string asset)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || string.IsNullOrWhiteSpace(asset)) return null;
        string descriptor = ResourceNames.Resolve(projectPath, asset, ResourceType.Terrain);
        if (descriptor.Length > 0 && File.Exists(descriptor + ".gterrain")) return descriptor + ".gterrain";
        string relative = asset.Trim().Replace('/', Path.DirectorySeparatorChar);
        foreach (string candidate in new[]
        {
            Path.Combine(projectPath, "Terrains", relative + ".gterrain"),
            Path.Combine(projectPath, relative),
            Path.Combine(projectPath, relative + ".gterrain"),
        })
            if (candidate.EndsWith(".gterrain", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate)) return candidate;

        string terrainJson = Path.Combine(projectPath, relative);
        if (relative.EndsWith(".terrain.json", StringComparison.OrdinalIgnoreCase) && File.Exists(terrainJson + ".gterrain"))
            return terrainJson + ".gterrain";
        string assets = Path.Combine(projectPath, "Assets");
        if (!Directory.Exists(assets)) return null;
        string bare = Path.GetFileName(relative);
        foreach (string suffix in new[] { ".terrain.json", ".gterrain" })
            if (bare.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) bare = bare[..^suffix.Length];
        return Directory.EnumerateFiles(assets, "*.gterrain", SearchOption.AllDirectories).FirstOrDefault(file =>
        {
            string name = Path.GetFileName(file);
            return name.Equals(bare + ".gterrain", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals(bare + ".terrain.json.gterrain", StringComparison.OrdinalIgnoreCase);
        });
    }

    public static bool ShouldRegister(RoomAsset room) => room?.Nodes.Exists(node =>
        node.Kind == RoomNodeKind.Terrain && node.Enabled && node.Supports(room.Dimension) &&
        room.Layers.Find(layer => layer.Id == node.LayerId)?.Enabled != false &&
        !string.IsNullOrWhiteSpace(node.Terrain?.Asset)) == true;

    public void Update(RuntimeScene scene, GameTime time)
    {
        UpdateTerrainStreaming(scene);
        if (!_worldAssigned && _entries.Count > 0)
        {
            scene.SetWorldQuery(_entries[0].Query, new MapDiscovery(_entries[0].Manifest.Bounds));
            _worldAssigned = true;
        }
        float rain = scene.Climate?.Current.LocalRain ?? 0f;
        foreach (Entry entry in _entries)
        {
            EnsureWaterImpactEntities(scene, entry);
            foreach (WaterRuntime water in entry.Waters)
            {
                if (water.Simulation == null) continue;
                water.Simulation.Update(time.Delta);
                if (rain <= 0.03f || water.Definition.RainCoupling <= 0f || water.Definition.TemperatureCelsius <= -1f) continue;
                double interval = Math.Max(0.055, 0.34 - rain * 0.26);
                if (time.Total - water.LastRainImpulse < interval) continue;
                water.LastRainImpulse = time.Total;
                uint hash = StableHash(water.Body.Id, (uint)Math.Floor(time.Total / interval));
                float x = water.Body.Center.X + (Random01(hash) - 0.5f) * water.Body.SizeX * 0.86f;
                float z = water.Body.Center.Z + (Random01(hash + 1u) - 0.5f) * water.Body.SizeZ * 0.86f;
                water.Simulation.Disturb(new Vector3(x, water.Body.SurfaceY, z),
                    MathF.Max(0.18f, MathF.Min(water.Body.SizeX, water.Body.SizeZ) * 0.018f),
                    rain * water.Definition.RainCoupling * 0.035f, foam: rain * 0.12f);
            }
        }
    }

    public void FixedUpdate(RuntimeScene scene, float fixedDelta)
    {
        if (scene.Physics == null) return;
        foreach (Entry entry in _entries)
        {
            if (TerrainLodGround.Applies(entry.Terrain))
                UpdateColliderTiles(scene, entry);
            else if (entry.ColliderRegistrationId == 0)
                TryRegisterTerrainCollider(scene.Physics, entry, wait: !entry.ColliderMayWait);
            UpdateScatterColliders(scene, entry);
            if (entry.WaterVolumeIds.Count == 0)
                RegisterWaterVolumes(scene, entry);
        }
    }

    /// <summary>Metres of collision kept around the camera on a large terrain.</summary>
    public float ColliderRadiusAroundCamera { get; set; } = 160f;

    /// <summary>Metres of collision kept around each moving body on a large terrain.</summary>
    public float ColliderRadiusAroundBodies { get; set; } = 24f;

    /// <summary>Registered and pending collision tiles across all large terrains.</summary>
    public (int Resident, int Pending) ColliderTileCounts
    {
        get
        {
            int resident = 0, pending = 0;
            foreach (Entry entry in _entries)
            {
                if (entry.ColliderTiles == null) continue;
                resident += entry.ColliderTiles.ResidentTiles;
                pending += entry.ColliderTiles.PendingTiles;
            }

            return (resident, pending);
        }
    }

    private readonly List<TerrainColliderFocus> _colliderFocus = new();
    private readonly List<TerrainColliderFocus> _scatterFocus = new();
    private readonly List<Genesis.Runtime.Scene.CollisionFocus> _namedFoci = new();

    /// <summary>Metres around the camera and each moving body in which scattered copies are solid.</summary>
    public float ScatterColliderRadius { get; set; } = 14f;

    /// <summary>Scatter colliders currently in the physics world, across all terrains.</summary>
    public int ScatterColliderCount => _entries.Sum(entry => entry.ScatterColliders?.ResidentColliders ?? 0);

    /// <summary>
    /// Trees and rocks are solid only where something could walk into them: near the camera and
    /// near every body that moves.
    /// </summary>
    private void UpdateScatterColliders(RuntimeScene scene, Entry entry, bool firstCellsAtOnce = true)
    {
        if (entry.ScatterColliders == null)
        {
            entry.ScatterColliders = new TerrainScatterColliders(entry.Terrain, entry.Nature?.ScatterLayers,
                Placement(entry), $"Scatter:{entry.Node.Name}");
        }

        if (!entry.ScatterColliders.HasLayers) return;
        _scatterFocus.Clear();
        float radius = ScatterColliderRadius;
        _scatterFocus.Add(new TerrainColliderFocus(scene.Camera3D.Position, radius));
        // Instances a script asked colliders to follow (PhysicsAddCollisionFocus) count as moving bodies.
        _namedFoci.Clear();
        Genesis.Runtime.Scene.CollisionFoci.Collect(scene.World, _namedFoci, 127);
        foreach (Genesis.Runtime.Scene.CollisionFocus focus in _namedFoci)
            _scatterFocus.Add(new TerrainColliderFocus(focus.Position, radius));
        scene.World.Query<Genesis.Shared.ECS.Components.Transform3DComponent, Genesis.Shared.ECS.Components.RigidBodyComponent>(
            (Entity _, ref Genesis.Shared.ECS.Components.Transform3DComponent transform,
                ref Genesis.Shared.ECS.Components.RigidBodyComponent body) =>
            {
                if (body.Motion != Genesis.Shared.ECS.Components.PhysicsMotionType.Static && _scatterFocus.Count < 128)
                    _scatterFocus.Add(new TerrainColliderFocus(transform.Position, radius));
            });
        entry.ScatterColliders.Update(scene.Physics,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_scatterFocus), firstCellsAtOnce);
    }

    /// <summary>
    /// A large terrain has collision only where something can touch it: around the camera and
    /// around every body that moves.
    /// </summary>
    private void UpdateColliderTiles(RuntimeScene scene, Entry entry, bool groundBeneathAtOnce = true)
    {
        if (entry.ColliderTiles == null)
        {
            RoomTransform transform = RoomHierarchyTransforms.World(entry.Room, entry.Node);
            entry.ColliderTiles = new TerrainColliderTiles(
                entry.Terrain,
                new Vector3(transform.ScaleX, transform.ScaleY, transform.ScaleZ),
                new Vector3(transform.X, transform.Y, transform.Z),
                Quaternion.CreateFromYawPitchRoll(
                    transform.RotationY * MathF.PI / 180f,
                    transform.RotationX * MathF.PI / 180f,
                    transform.RotationZ * MathF.PI / 180f),
                $"Terrain:{entry.Node.Name}");
        }

        _colliderFocus.Clear();
        _colliderFocus.Add(new TerrainColliderFocus(scene.Camera3D.Position, ColliderRadiusAroundCamera));
        // Instances a script asked colliders to follow, each with its own radius.
        _namedFoci.Clear();
        Genesis.Runtime.Scene.CollisionFoci.Collect(scene.World, _namedFoci, 511);
        foreach (Genesis.Runtime.Scene.CollisionFocus focus in _namedFoci)
            _colliderFocus.Add(new TerrainColliderFocus(focus.Position, focus.Radius));
        float bodyRadius = ColliderRadiusAroundBodies;
        scene.World.Query<Genesis.Shared.ECS.Components.Transform3DComponent, Genesis.Shared.ECS.Components.RigidBodyComponent>(
            (Entity _, ref Genesis.Shared.ECS.Components.Transform3DComponent transform,
                ref Genesis.Shared.ECS.Components.RigidBodyComponent body) =>
            {
                if (body.Motion != Genesis.Shared.ECS.Components.PhysicsMotionType.Static && _colliderFocus.Count < 512)
                    _colliderFocus.Add(new TerrainColliderFocus(transform.Position, bodyRadius));
            });
        entry.ColliderTiles.Update(scene.Physics,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_colliderFocus), groundBeneathAtOnce);
    }

    public void SubmitMeshes(RuntimeScene scene, MeshDrawCall[] buffer, ref int count, IRenderController renderer)
        => SubmitPreviewMeshes(scene.Camera3D.Position, scene.Camera3D.ViewProjection, buffer, ref count, renderer);

    /// <summary>Reserves every authored ground chunk, path and water surface before submission.
    /// Editors use the same terrain renderer without creating or stepping a gameplay scene.</summary>
    public int GetMeshDrawCapacity(IRenderController renderer)
    {
        int capacity = 0;
        foreach (Entry entry in _entries)
        {
            BindEntry(entry, renderer);
            capacity = checked(capacity + entry.Ground.MeshCount + entry.PathMeshes.Count + entry.Waters.Count * 2
                + (entry.Scatter?.MaximumDrawCalls ?? 0));
        }
        return capacity;
    }

    /// <summary>Visible legacy/unpainted route ribbons; painted routes use the ground surface.</summary>
    public int VisiblePathMeshCount => _entries.Sum(entry => entry.PathMeshes.Count);

    /// <summary>Draws the F5 terrain composition from an editor camera. This does not register
    /// physics, run object scripts, or alter the authored room/terrain resources.</summary>
    public void SubmitPreviewMeshes(Vector3 camera, Matrix4x4 viewProjection,
        MeshDrawCall[] buffer, ref int count, IRenderController renderer)
    {
        foreach (Entry entry in _entries)
        {
            BindEntry(entry, renderer);
            int terrainStart = count;
            MeshDrawFlags terrainRaster = MeshRasterDefaults.ApplyOverride(
                MeshDrawFlags.None,
                entry.Culling,
                entry.WindingOrder);
            Matrix4x4 placement = Placement(entry);
            TerrainLodView view = default;
            if (entry.Ground.UsesLevelOfDetail)
            {
                // Detail is chosen in the terrain's own space, so a placed or scaled terrain behaves.
                Vector3 cameraLocal = Matrix4x4.Invert(placement, out Matrix4x4 inverse)
                    ? Vector3.Transform(camera, inverse)
                    : camera;
                view = new TerrainLodView(cameraLocal, placement * viewProjection, TerrainDetail);
            }

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            entry.Ground.AppendDrawCalls(buffer, ref count, view, terrainRaster);
            for (int i = terrainStart; i < count; i++) buffer[i].World *= placement;
            SubmitPaths(entry, placement, buffer, ref count);
            long afterGround = System.Diagnostics.Stopwatch.GetTimestamp();
            SubmitFoliage(entry, camera, viewProjection, placement, renderer);
            entry.Scatter?.Submit(renderer, camera, viewProjection, placement, buffer, ref count);
            SubmitGrassRule(entry, camera, viewProjection, placement, renderer);
            long afterScatter = System.Diagnostics.Stopwatch.GetTimestamp();
            SubmitWater(entry, camera, placement, renderer, buffer, ref count);
            ReportSlowSubmit(started, afterGround, afterScatter, System.Diagnostics.Stopwatch.GetTimestamp(), entry.Scatter);
        }
    }

    /// <summary>Longest time one frame has spent submitting a terrain, in milliseconds.</summary>
    public double SlowestSubmitMilliseconds { get; private set; }
    private long _lastSlowReport;

    /// <summary>
    /// Names the part of the terrain that held a frame up. Streaming work is budgeted per frame,
    /// so a long submit means a budget is wrong; the log line says which.
    /// </summary>
    private void ReportSlowSubmit(long started, long afterGround, long afterScatter, long finished, TerrainScatterRenderer scatter)
    {
        double total = System.Diagnostics.Stopwatch.GetElapsedTime(started, finished).TotalMilliseconds;
        if (total > SlowestSubmitMilliseconds) SlowestSubmitMilliseconds = total;
        if (total < 25.0 || Environment.TickCount64 - _lastSlowReport < 1000) return;
        _lastSlowReport = Environment.TickCount64;
        Console.WriteLine($"[Terrain] Submitting the terrain took {total:F0} ms: ground "
            + $"{System.Diagnostics.Stopwatch.GetElapsedTime(started, afterGround).TotalMilliseconds:F0} ms, scatter "
            + $"{System.Diagnostics.Stopwatch.GetElapsedTime(afterGround, afterScatter).TotalMilliseconds:F0} ms, water "
            + $"{System.Diagnostics.Stopwatch.GetElapsedTime(afterScatter, finished).TotalMilliseconds:F0} ms."
            + (scatter == null ? "" : $" Scatter: {scatter.DescribeLastPhases()}."));
    }

    public float SampleHeight(float worldX, float worldZ)
    {
        float maximum = float.MinValue;
        bool found = false;
        foreach (Entry entry in _entries)
        {
            (Vector3 min, Vector3 max) = EntryBounds(entry);
            if (worldX < min.X || worldX > max.X || worldZ < min.Z || worldZ > max.Z
                || !TerrainSurfaceRaycast.Raycast(entry.Terrain, Placement(entry), new Vector3(worldX, max.Y + 1f, worldZ),
                    -Vector3.UnitY, out TerrainSurfaceHit hit)) continue;
            float value = hit.Position.Y;
            if (!found || value > maximum) { maximum = value; found = true; }
        }
        return found ? maximum : 0f;
    }

    public bool TryGetNavigationBounds(out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue); max = new Vector3(float.MinValue);
        foreach (Entry entry in _entries)
        {
            (Vector3 entryMin, Vector3 entryMax) = EntryBounds(entry);
            min = Vector3.Min(min, entryMin); max = Vector3.Max(max, entryMax);
        }
        return _entries.Count > 0;
    }

    public void Tick(in StreamingContext context)
    {
        int loaded = 0, visible = 0, pending = 0, loads = 0, unloads = 0;
        foreach (Entry entry in _entries)
        {
            entry.Streamer.Tick(context);
            loaded += entry.Streamer.Stats.CellsLoaded;
            visible += entry.Streamer.Stats.CellsVisible;
            pending += entry.Streamer.Stats.CellsPending;
            loads += entry.Streamer.Stats.LoadsStartedLastFrame;
            unloads += entry.Streamer.Stats.UnloadsLastFrame;
        }
        Stats.CellsLoaded = loaded; Stats.CellsVisible = visible; Stats.CellsPending = pending;
        Stats.LoadsStartedLastFrame = loads; Stats.UnloadsLastFrame = unloads;
    }

    public void RequestStreamingRefresh()
    {
        foreach (Entry entry in _entries) entry.Streamer.RequestStreamingRefresh();
    }

    public void Dispose()
    {
        foreach (Entry entry in _entries) ReleaseEntry(_game?.Scene, entry);
        _entries.Clear();
        _streamed.Clear();
    }

    /// <summary>Gives back everything one terrain holds: collision, water volumes, meshes, material.</summary>
    private void ReleaseEntry(RuntimeScene scene, Entry entry)
    {
        if (scene?.Physics != null && entry.ColliderRegistrationId != 0)
            scene.Physics.UnregisterStaticSurface(entry.ColliderRegistrationId);
        entry.ColliderRegistrationId = 0;
        DiscardPreparedCollider(entry);
        entry.ColliderTiles?.Clear(scene?.Physics);
        entry.ColliderTiles?.Dispose();
        entry.ColliderTiles = null;
        entry.ScatterColliders?.Clear(scene?.Physics);
        entry.ScatterColliders = null;
        if (scene != null)
            foreach (string id in entry.WaterVolumeIds) scene.RemoveWaterVolume(id);
        entry.WaterVolumeIds.Clear();
        entry.Ground?.Dispose();
        entry.Scatter?.Dispose();
        entry.Scatter = null;
        entry.GrassField?.Dispose();
        entry.GrassField = null;
        if (entry.Renderer != null)
        {
            foreach (MeshHandle mesh in entry.PathMeshes) if (mesh.IsValid) entry.Renderer.ReleaseMesh(mesh);
            foreach (MeshHandle mesh in entry.FoliageMeshes.Values) if (mesh.IsValid) entry.Renderer.ReleaseMesh(mesh);
        }
        entry.WaterCache?.Dispose();
        ReleaseMaterial(entry);
        if (scene != null) DestroyWaterImpactEntities(scene, entry);
        entry.PathMeshes.Clear(); entry.FoliageMeshes.Clear(); entry.Waters.Clear();
    }

    /// <summary>
    /// Reloads authored heightfields and water from disk and re-registers colliders/volumes
    /// without unloading the rest of the room. Used when F5 live-reload sees only terrain surfaces.
    /// </summary>
    public bool ReloadAuthoredSurfaces(RuntimeScene scene)
    {
        if (scene == null) return false;
        bool reloaded = false;
        foreach (Entry entry in _entries)
        {
            if (string.IsNullOrWhiteSpace(entry.BinaryPath) || !File.Exists(entry.BinaryPath))
                continue;
            UnregisterPhysics(scene, entry);
            ReleaseBoundMeshes(entry);
            entry.Material = new();
            entry.Terrain = TerrainAsset.Load(entry.BinaryPath);
            entry.Ground = new AuthoredTerrainGround(entry.Terrain);
            string resourcePath = ResolveTerrainResourcePath(entry.BinaryPath);
            try { entry.Nature = TerrainNatureSerializer.LoadOrDefault(resourcePath); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                Console.WriteLine($"[Terrain] Nature sidecar '{resourcePath}' could not be reloaded: {exception.Message}");
                entry.Nature = new TerrainNatureDocument();
            }

            foreach (TerrainWaterDefinition definition in entry.Nature.WaterBodies)
                TerrainRiverSystem.ConformStandingWater(entry.Terrain, definition);

            DestroyWaterImpactEntities(scene, entry);
            entry.Waters.Clear();
            foreach (TerrainWaterDefinition definition in entry.Nature.WaterBodies)
            {
                WaterBody body = definition.ToWaterBody();
                WaterBodySimulation simulation = body.SimulationEnabled
                    ? new WaterBodySimulation(body, (x, z) => MathF.Max(0.02f, body.SurfaceY - entry.Terrain.SampleHeight(x, z)))
                    : null;
                entry.Waters.Add(new WaterRuntime { Definition = definition, Body = body, Simulation = simulation });
            }
            entry.ComponentShaders = TerrainNatureSerializer.LoadComponentShaders(resourcePath);
            (entry.Culling, entry.WindingOrder) = LoadRasterOverrides(resourcePath);
            entry.Scatter?.Dispose();
            entry.Scatter = new TerrainScatterRenderer(_projectPath, entry.Terrain, entry.Nature.ScatterLayers);
            entry.ScatterColliders?.Clear(scene.Physics);
            entry.ScatterColliders = null;

            entry.Manifest = WorldManifest.Build(entry.Terrain, entry.Nature);
            entry.Query = new WorldQuery(entry.Terrain, entry.Manifest);
            entry.Streamer = new WorldManifestStreamingProvider(entry.Manifest);
            if (scene.Physics != null && !TerrainLodGround.Applies(entry.Terrain))
                entry.ColliderRegistrationId = RegisterTerrainCollider(scene.Physics, entry);
            RegisterWaterVolumes(scene, entry);
            entry.ColliderRebuildGeneration++;
            reloaded = true;
        }

        if (reloaded)
        {
            ColliderRebuildGeneration++;
            _worldAssigned = false;
        }

        return reloaded;
    }

    private static int RegisterTerrainCollider(PhysicsWorld physics, Entry entry)
    {
        TerrainColliderMesh.Build(entry.Terrain, out Vector3[] vertices, out int[] indices);
        RoomTransform transform = RoomHierarchyTransforms.World(entry.Room, entry.Node);
        Quaternion rotation = Quaternion.CreateFromYawPitchRoll(
            transform.RotationY * MathF.PI / 180f,
            transform.RotationX * MathF.PI / 180f,
            transform.RotationZ * MathF.PI / 180f);
        return physics.RegisterStaticTriangleMesh(
            vertices, indices,
            new Vector3(transform.ScaleX, transform.ScaleY, transform.ScaleZ),
            new Vector3(transform.X, transform.Y, transform.Z),
            rotation,
            $"Terrain:{entry.Node.Name}");
    }

    private static void RegisterWaterVolumes(RuntimeScene scene, Entry entry)
    {
        Matrix4x4 placement = Placement(entry);
        foreach (WaterRuntime runtime in entry.Waters)
        {
            if (runtime.Definition.PhysicsMode == WaterPhysicsMode.None) continue;
            PhysicsWaterVolume volume = TerrainColliderMesh.CreateVolume(runtime.Definition, placement);
            scene.RegisterWaterVolume(volume);
            entry.WaterVolumeIds.Add(volume.Id);
        }
    }

    private static void UnregisterPhysics(RuntimeScene scene, Entry entry)
    {
        if (scene.Physics != null && entry.ColliderRegistrationId != 0)
            scene.Physics.UnregisterStaticSurface(entry.ColliderRegistrationId);
        entry.ColliderRegistrationId = 0;
        DiscardPreparedCollider(entry);
        // The tiles describe the old heights and the old asset; the next fixed update rebuilds them.
        entry.ColliderTiles?.Clear(scene.Physics);
        entry.ColliderTiles?.Dispose();
        entry.ColliderTiles = null;
        // Scattered copies stand on the old heights too; they are placed again on the new ground.
        entry.ScatterColliders?.Clear(scene.Physics);
        entry.ScatterColliders = null;
        foreach (string id in entry.WaterVolumeIds)
            scene.RemoveWaterVolume(id);
        entry.WaterVolumeIds.Clear();
    }

    private static void ReleaseBoundMeshes(Entry entry)
    {
        if (entry.Renderer != null)
        {
            foreach (MeshHandle mesh in entry.PathMeshes)
                if (mesh.IsValid) entry.Renderer.ReleaseMesh(mesh);
        }
        entry.PathMeshes.Clear();
        entry.Ground?.Dispose();
        entry.WaterCache?.Dispose();
        entry.WaterCache = null;
        ReleaseMaterial(entry);
        entry.Bound = false;
    }

    private void BindEntry(Entry entry, IRenderController renderer)
    {
        if (entry.Bound && !ReferenceEquals(entry.Renderer, renderer))
        {
            foreach (MeshHandle mesh in entry.FoliageMeshes.Values) if (mesh.IsValid) entry.Renderer.ReleaseMesh(mesh);
            entry.FoliageMeshes.Clear(); ReleaseBoundMeshes(entry);
        }
        UpdateMaterial(entry, renderer);
        if (entry.Bound) return;
        string albedo = string.IsNullOrWhiteSpace(entry.Node.Terrain.Albedo) ? "Textures/Terrain_ForestGround.png" : entry.Node.Terrain.Albedo;
        // Terrain albedo is lit 3D colour: request it as such so it gets mipmaps (distant terrain
        // shimmered without them) and is decoded under the linear colour pipeline.
        TextureHandle texture = _game?.LoadTexture(albedo, Genesis.Shared.Materials.TextureColorSpace.Srgb) ?? TextureHandle.Invalid;
        if (!texture.IsValid)
        {
            string path = Genesis.Runtime.Assets.SpriteAssetLoader.ResolveFrameTexturePath(_projectPath, albedo, 0);
            if (File.Exists(path)) texture = renderer.LoadTexture(path, Genesis.Shared.Materials.TextureColorSpace.Srgb);
        }
        if (entry.Material.Draw.HasValue)
        {
            entry.Ground.SurfaceMaterial = entry.Material.Draw;
            entry.Ground.Bind(renderer, entry.Material.Draw.Value.Texture, 1);
        }
        else entry.Ground.Bind(renderer, texture, Math.Max(.01f, entry.Node.Terrain.UvScale));
        entry.Renderer = renderer;
        entry.WaterCache = new WaterMeshCache(renderer);
        foreach (TerrainPathDefinition path in entry.Nature.Paths)
        {
            if (path.SurfacePainted) continue;
            MeshData data = TerrainPathGeometry.BuildRibbon(
                path,
                entry.Terrain.SampleHeight,
                MathF.Max(1f, entry.Terrain.CellSize * 2f));
            if (data.Vertices != null && data.Vertices.Length > 0) entry.PathMeshes.Add(renderer.RegisterMesh(data.Vertices, data.Indices));
        }
        entry.Bound = true;
    }

    private static void SubmitPaths(Entry entry, Matrix4x4 placement, MeshDrawCall[] buffer, ref int count)
    {
        foreach (MeshHandle mesh in entry.PathMeshes)
        {
            if (count >= buffer.Length) break;
            buffer[count++] = new MeshDrawCall
            {
                Mesh = mesh,
                World = placement,
                Tint = RenderColor.White,
                Alpha = 1f,
                Flags = MeshRasterDefaults.ApplyOverride(
                    MeshDrawFlags.None,
                    entry.Culling,
                    entry.WindingOrder),
            };
        }
    }

    private static (FaceCullingOverride Culling, FrontFaceWindingOverride Winding)
        LoadRasterOverrides(string terrainResourcePath)
    {
        if (string.IsNullOrWhiteSpace(terrainResourcePath) || !File.Exists(terrainResourcePath))
            return (FaceCullingOverride.Default, FrontFaceWindingOverride.Default);
        try
        {
            using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(terrainResourcePath));
            System.Text.Json.JsonElement root = json.RootElement;
            string culling = root.TryGetProperty("culling", out System.Text.Json.JsonElement cull)
                ? cull.GetString()
                : null;
            string winding = root.TryGetProperty("windingOrder", out System.Text.Json.JsonElement wind)
                ? wind.GetString()
                : null;
            return (
                MeshRasterDefaults.ParseCulling(culling, FaceCullingOverride.Default),
                MeshRasterDefaults.ParseWinding(winding, FrontFaceWindingOverride.Default));
        }
        catch (Exception exception) when (
            exception is IOException or System.Text.Json.JsonException)
        {
            return (FaceCullingOverride.Default, FrontFaceWindingOverride.Default);
        }
    }

    private static void SubmitFoliage(
        Entry entry,
        Vector3 camera,
        Matrix4x4 viewProjection,
        Matrix4x4 placement,
        IRenderController renderer)
    {
        if (entry.Foliage?.Instances == null || entry.Renderer == null || entry.FoliagePlanner == null) return;
        FoliageFramePlan plan = entry.FoliagePlanner.Build(
            camera,
            viewProjection,
            placement,
            entry.Nature.FoliageSettings,
            renderer.LastGpuMilliseconds);
        entry.FoliagePerformance = plan.Snapshot;

        foreach (FoliageRenderBatch batch in plan.Batches)
        {
            var key = (batch.Key.Species, batch.Key.NearLod);
            if (!entry.FoliageMeshes.TryGetValue(key, out MeshHandle mesh) || !mesh.IsValid)
            {
                MeshData data = FoliageGeometry.Build(batch.Key.Species, batch.Key.NearLod);
                mesh = entry.Renderer.RegisterMesh(data.Vertices, data.Indices);
                entry.FoliageMeshes[key] = mesh;
            }

            if (entry.FoliageInstanceBuffer.Length < batch.Instances.Count)
                Array.Resize(ref entry.FoliageInstanceBuffer, NextPowerOfTwo(batch.Instances.Count));
            for (int i = 0; i < batch.Instances.Count; i++)
            {
                FoliageInstance instance = batch.Instances[i];
                float hue = instance.HueVariation;
                RenderColor tint = new(
                    Math.Clamp(1f + hue * 0.06f, 0.72f, 1.2f),
                    Math.Clamp(1f - MathF.Abs(hue) * 0.03f, 0.72f, 1.1f),
                    Math.Clamp(1f - hue * 0.08f, 0.72f, 1.2f), 1f);
                Matrix4x4 local = Matrix4x4.CreateScale(instance.Scale)
                    * Matrix4x4.CreateRotationY(instance.Rotation)
                    * Matrix4x4.CreateTranslation(instance.Position);
                entry.FoliageInstanceBuffer[i] = new MeshInstanceData(local * placement, tint);
            }
            MeshDrawCall template = new()
            {
                Mesh = mesh,
                Tint = RenderColor.White,
                Alpha = 1f,
                Flags = MeshDrawFlags.Foliage | MeshDrawFlags.NoCull | MeshDrawFlags.NoShadow,
            };
            renderer.DrawMeshInstances(
                template,
                entry.FoliageInstanceBuffer.AsSpan(0, batch.Instances.Count));
        }
    }

    private static int NextPowerOfTwo(int value)
    {
        int capacity = 128;
        while (capacity < value && capacity < 32768) capacity <<= 1;
        return Math.Max(value, capacity);
    }

    private void SubmitWater(Entry entry, Vector3 camera, Matrix4x4 placement, IRenderController renderer, MeshDrawCall[] buffer, ref int count)
    {
        var draws = new List<MeshDrawCall>(1);
        foreach (WaterRuntime water in entry.Waters)
        {
            draws.Clear();
            WaterDrawSystem.BuildDrawCalls(water.Body, entry.WaterCache, camera, draws, water.Simulation);
            string shaderPath = null;
            entry.ComponentShaders?.TryGetValue("water:" + water.Definition.Id, out shaderPath);
            foreach (MeshDrawCall drawValue in draws)
            {
                if (count >= buffer.Length) break;
                MeshDrawCall draw = drawValue;
                draw.World *= placement;
                if (!string.IsNullOrWhiteSpace(shaderPath) && (draw.Flags & MeshDrawFlags.Water) != 0)
                    ObjectDrawPass.TryApplyAuthoredWaterShader(renderer, _projectPath, shaderPath, ref draw);
                buffer[count++] = draw;
            }
            if (WaterDrawSystem.TryBuildGroundMist(water.Body, out FogVolume mist))
            {
                mist.Center = Vector3.Transform(mist.Center, placement);
                renderer.AddFogVolume(mist);
            }
        }
    }

    private static void EnsureWaterImpactEntities(RuntimeScene scene, Entry entry)
    {
        if (entry.WaterImpactEntitiesInitialized || scene?.World == null) return;
        entry.WaterImpactEntitiesInitialized = true;
        Matrix4x4 placement = Placement(entry);
        foreach (WaterRuntime water in entry.Waters)
        foreach (WaterImpactHook hook in water.Body.ImpactHooks ?? Array.Empty<WaterImpactHook>())
        {
            if (string.IsNullOrWhiteSpace(hook.ParticleAsset)) continue;
            Vector3 position = Vector3.Transform(hook.Position, placement);
            Entity entity = scene.CreateEntity(position);
            scene.World.Set(entity, new TransformComponent
            {
                X = position.X,
                Y = position.Y,
                Z = position.Z,
                ScaleX = 1f,
                ScaleY = 1f,
                ScaleZ = 1f,
            });
            scene.World.Set(entity, new ParticleComponent
            {
                Asset = hook.ParticleAsset,
                ParticleTypeId = -1,
                RateScale = Math.Clamp(hook.Radius * .35f, .4f, 4f),
                FollowEntity = true,
                Emitting = true,
            });
            entry.WaterImpactEntities.Add(entity);
        }
    }

    private static void DestroyWaterImpactEntities(RuntimeScene scene, Entry entry)
    {
        if (scene?.World != null)
            foreach (Entity entity in entry.WaterImpactEntities)
                if (scene.World.IsAlive(entity)) scene.World.DestroyEntity(entity);
        entry.WaterImpactEntities.Clear();
        entry.WaterImpactEntitiesInitialized = false;
    }

    private static FoliageField LoadFoliage(string resourcePath, TerrainNatureDocument nature)
    {
        string cache = TerrainNatureSerializer.ResolveFoliageCache(resourcePath, nature);
        if (cache == null || !File.Exists(cache)) return new FoliageField { Seed = nature.FoliageSettings.Seed, Preset = nature.FoliageSettings.Preset };
        try { return FoliageFieldCache.Load(cache, nature.FoliageCacheSha256); }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            Console.WriteLine($"[Terrain] Foliage cache '{cache}' could not be loaded: {exception.Message}");
            return new FoliageField { Seed = nature.FoliageSettings.Seed, Preset = nature.FoliageSettings.Preset };
        }
    }

    public static string ResolveTerrainResourcePath(string binaryPath)
    {
        if (binaryPath.EndsWith(".terrain.json.gterrain", StringComparison.OrdinalIgnoreCase)) return binaryPath[..^".gterrain".Length];
        string candidate = Path.ChangeExtension(binaryPath, ".terrain.json");
        return File.Exists(candidate) ? candidate : binaryPath;
    }

    private static Matrix4x4 Placement(Entry entry) =>
        RoomHierarchyTransforms.Matrix(RoomHierarchyTransforms.World(entry.Room, entry.Node));

    private static (Vector3 Min, Vector3 Max) EntryBounds(Entry entry)
    {
        TerrainAsset terrain = entry.Terrain;
        Matrix4x4 placement = Placement(entry);
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        for (int mask = 0; mask < 8; mask++)
        {
            Vector3 corner = new(terrain.OriginX + ((mask & 1) == 0 ? 0 : (terrain.ResolutionX - 1) * terrain.CellSize),
                (mask & 2) == 0 ? terrain.MinHeight : terrain.MaxHeight,
                terrain.OriginZ + ((mask & 4) == 0 ? 0 : (terrain.ResolutionZ - 1) * terrain.CellSize));
            corner = Vector3.Transform(corner, placement);
            min = Vector3.Min(min, corner); max = Vector3.Max(max, corner);
        }
        return (min, max);
    }

    private static uint StableHash(string value, uint salt)
    {
        uint hash = 2166136261u ^ salt;
        foreach (char character in value ?? string.Empty) { hash ^= character; hash *= 16777619u; }
        hash ^= hash >> 16; hash *= 0x7feb352du; hash ^= hash >> 15; hash *= 0x846ca68bu;
        return hash ^ (hash >> 16);
    }
    private static float Random01(uint value) => StableHash(value.ToString(System.Globalization.CultureInfo.InvariantCulture), value) * (1f / uint.MaxValue);
}
