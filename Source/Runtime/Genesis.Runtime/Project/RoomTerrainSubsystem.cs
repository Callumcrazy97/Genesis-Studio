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
public sealed class RoomTerrainSubsystem : ISceneSubsystem, IStreamingProvider
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
        public AuthoredTerrainGround Ground;
        public TerrainAsset Terrain;
        public int ColliderRegistrationId;
        public int ColliderRebuildGeneration;
        public readonly List<string> WaterVolumeIds = new();
        public bool Bound;
        public TerrainNatureDocument Nature;
        public FoliageField Foliage;
        public FoliageStreamingPlanner FoliagePlanner;
        public FoliagePerformanceSnapshot FoliagePerformance;
        public MeshInstanceData[] FoliageInstanceBuffer = Array.Empty<MeshInstanceData>();
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
        foreach (RoomNode node in room.Nodes)
        {
            if (node.Kind != RoomNodeKind.Terrain || !node.Enabled || !node.Supports(room.Dimension) ||
                room.Layers.Find(layer => layer.Id == node.LayerId)?.Enabled == false ||
                string.IsNullOrWhiteSpace(node.Terrain?.Asset)) continue;
            string binaryPath = ResolveTerrainFile(_projectPath, node.Terrain.Asset);
            if (binaryPath == null) continue;
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
            entry.ComponentShaders = TerrainNatureSerializer.LoadComponentShaders(resourcePath);
            foreach (TerrainWaterDefinition definition in nature.WaterBodies)
            {
                WaterBody body = definition.ToWaterBody();
                WaterBodySimulation simulation = body.SimulationEnabled
                    ? new WaterBodySimulation(body, (x, z) => MathF.Max(0.02f, body.SurfaceY - terrain.SampleHeight(x, z)))
                    : null;
                entry.Waters.Add(new WaterRuntime { Definition = definition, Body = body, Simulation = simulation });
            }
            _entries.Add(entry);
        }
    }

    public string Name => "Authored terrain world";
    public StreamingStats Stats { get; } = new();
    public int ColliderRebuildGeneration { get; private set; }
    public IWorldQuery WorldQuery => _entries.Count > 0 ? _entries[0].Query : null;
    /// <summary>
    /// Canonical authored counts loaded from each terrain foliage cache. Exposed for runtime
    /// diagnostics so the editor, F5 player, and profiler can prove they consumed the same asset.
    /// </summary>
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
            if (entry.ColliderRegistrationId == 0)
                entry.ColliderRegistrationId = RegisterTerrainCollider(scene.Physics, entry);
            if (entry.WaterVolumeIds.Count == 0)
                RegisterWaterVolumes(scene, entry);
        }
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
            capacity = checked(capacity + entry.Ground.MeshCount + entry.PathMeshes.Count + entry.Waters.Count);
        }
        return capacity;
    }

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
            entry.Ground.AppendDrawCalls(buffer, ref count, terrainRaster);
            Matrix4x4 placement = Placement(entry);
            for (int i = terrainStart; i < count; i++) buffer[i].World *= placement;
            SubmitPaths(entry, placement, buffer, ref count);
            SubmitFoliage(entry, camera, viewProjection, placement, renderer);
            SubmitWater(entry, camera, placement, renderer, buffer, ref count);
        }
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
        foreach (Entry entry in _entries)
        {
            if (_game?.Scene?.Physics != null && entry.ColliderRegistrationId != 0)
                _game.Scene.Physics.UnregisterStaticSurface(entry.ColliderRegistrationId);
            if (_game?.Scene != null)
                foreach (string id in entry.WaterVolumeIds) _game.Scene.RemoveWaterVolume(id);
            entry.Ground?.Dispose();
            if (entry.Renderer != null)
            {
                foreach (MeshHandle mesh in entry.PathMeshes) if (mesh.IsValid) entry.Renderer.ReleaseMesh(mesh);
                foreach (MeshHandle mesh in entry.FoliageMeshes.Values) if (mesh.IsValid) entry.Renderer.ReleaseMesh(mesh);
            }
            entry.WaterCache?.Dispose();
            if (_game?.Scene is { } activeScene) DestroyWaterImpactEntities(activeScene, entry);
            entry.PathMeshes.Clear(); entry.FoliageMeshes.Clear(); entry.Waters.Clear();
        }
        _entries.Clear();
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

            entry.Manifest = WorldManifest.Build(entry.Terrain, entry.Nature);
            entry.Query = new WorldQuery(entry.Terrain, entry.Manifest);
            entry.Streamer = new WorldManifestStreamingProvider(entry.Manifest);
            if (scene.Physics != null)
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
        entry.Bound = false;
    }

    private void BindEntry(Entry entry, IRenderController renderer)
    {
        if (entry.Bound) return;
        string albedo = string.IsNullOrWhiteSpace(entry.Node.Terrain.Albedo) ? "Textures/Terrain_ForestGround.png" : entry.Node.Terrain.Albedo;
        TextureHandle texture = _game?.LoadTexture(albedo) ?? TextureHandle.Invalid;
        if (!texture.IsValid)
        {
            string path = Genesis.Runtime.Assets.SpriteAssetLoader.ResolveFrameTexturePath(_projectPath, albedo, 0);
            if (File.Exists(path)) texture = renderer.LoadTexture(path);
        }
        entry.Ground.Bind(renderer, texture, Math.Max(.01f, entry.Node.Terrain.UvScale));
        entry.Renderer = renderer;
        entry.WaterCache = new WaterMeshCache(renderer);
        foreach (TerrainPathDefinition path in entry.Nature.Paths)
        {
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
                if (!string.IsNullOrWhiteSpace(shaderPath))
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
