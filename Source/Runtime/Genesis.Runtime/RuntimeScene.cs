using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Rendering.Core;
using Genesis.Physics;
using Genesis.Physics.Systems;
using Genesis.Runtime.Core;
using Genesis.Runtime.AI;
using Genesis.Runtime.ECS;
using Genesis.Runtime.Input;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Systems;
using Genesis.Rendering.D3dMath;
using Genesis.Rendering.Meshes;
using Genesis.Runtime.Culling;
using Genesis.Runtime.Climate;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Rendering;
using Genesis.Shared.Assets;
using Genesis.Streaming;
using Genesis.World.Navigation;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime
{
    public sealed class RuntimeScene : IDisposable
    {
        private readonly List<ISceneSubsystem> _subsystems = new List<ISceneSubsystem>(4);
        private byte[] _weatherMapRgbaScratch;
        private PhysicsWorld _physics;
        private PhysicsMotorway _physicsMotorway;
        private bool _physicsSystemsRegistered;
        private PhysicsRegistrationSystem _physicsRegistrationSystem;
        private PhysicsPlayerSystem _physicsPlayerSystem;
        private PhysicsGrabSystem _physicsGrabSystem;
        private RaycastHighlightSystem _raycastHighlightSystem;
        private readonly FrameBudgetSystem _frameBudgetSystem;
        private readonly SceneEnvironmentQuery _environmentQuery;
        private readonly List<PhysicsWaterVolume> _waterVolumes = new();

        public string Name { get; set; }

        public EcsWorld        World { get; }
        public SystemScheduler Scheduler { get; }
        public FixedTimestep   FixedTimestep { get; } = new FixedTimestep();
        public GameTime        GameTime { get; private set; } = new GameTime();
        public SceneEnvironment Environment { get; } = new SceneEnvironment();
        /// <summary>Optional authored day/night and weather simulation for the active room.</summary>
        public EnvironmentService Climate { get; private set; }
        /// <summary>Backend-neutral atmospheric profile and deterministic volumetric cloud field.</summary>
        public AtmosphereService Atmosphere { get; private set; }
        /// <summary>
        /// AF2.2 advected weather map (coverage / type / erosion / vertical).
        /// Updated from <see cref="Climate"/>; AF2.3 packs RGBA8 into the raymarched cloud pass
        /// when enabled. FogVolumes remain the Software / disabled fallback.
        /// </summary>
        public WeatherMapService WeatherMap { get; } = new();
        /// <summary>Authoritative terrain, route, water and shelter query for the active world.</summary>
        public IWorldQuery WorldQuery { get; private set; }
        /// <summary>Saveable exploration mask associated with <see cref="WorldQuery"/>.</summary>
        public MapDiscovery MapDiscovery { get; private set; }
        public Camera3D        Camera3D { get; } = new Camera3D();

        /// <summary>High-performance 2D spatial broadphase for constant-time collision queries.</summary>
        public Genesis.Runtime.Spatial.SpatialHashGrid2D SpatialGrid { get; } = new();

        public void RebuildSpatialGrid()
        {
            SpatialGrid.Clear();
            if (World == null) return;

            World.Query<Genesis.Runtime.ECS.Components.TransformComponent>((Genesis.Shared.ECS.Entity entity, ref Genesis.Runtime.ECS.Components.TransformComponent transform) =>
            {
                string objName = string.Empty;
                if (Genesis.Runtime.Rendering.ObjectDrawAssetRegistry.TryGet(entity, out var asset))
                {
                    objName = asset.Prefab ?? asset.Image ?? string.Empty;
                }

                float w = 32f * Math.Max(0.1f, Math.Abs(transform.ScaleX));
                float h = 32f * Math.Max(0.1f, Math.Abs(transform.ScaleY));

                var bounds = new System.Drawing.RectangleF(
                    transform.X - (w * 0.5f),
                    transform.Y - (h * 0.5f),
                    w,
                    h);

                bool isSolid = objName.Contains("solid", StringComparison.OrdinalIgnoreCase)
                    || objName.Contains("platform", StringComparison.OrdinalIgnoreCase)
                    || objName.Contains("tile", StringComparison.OrdinalIgnoreCase);

                SpatialGrid.Insert(entity, entity.Id, objName, bounds, isSolid);
            });
        }

        /// <summary>
        /// Current render-target height used by resolution-aware CPU LOD selection. The host keeps
        /// this in sync with the live viewport; 720 is a deterministic fallback for headless use.
        /// </summary>
        public float RenderViewportHeightPixels { get; set; } = 720f;

        /// <summary>Opt-in global LOD/frame budget policy. Disabled by default.</summary>
        public FrameBudgetSettings FrameBudgetSettings => _frameBudgetSystem.Settings;

        /// <summary>Latest CPU/GPU budget diagnostics for tooling and future PGSL/editor surfaces.</summary>
        public FrameBudgetSnapshot FrameBudgetState => _frameBudgetSystem.Snapshot;

        /// <summary>Dedicated 60Hz physics worker (R7.12). Null only after dispose.</summary>
        public PhysicsMotorway PhysicsMotorway => _physicsMotorway ??= new PhysicsMotorway();

        public PhysicsWorld Physics
        {
            get => _physics;
            set
            {
                if (ReferenceEquals(_physics, value))
                    return;

                RemovePhysicsSystems();
                _physics?.Dispose();
                _physics = value;
                _physicsSystemsRegistered = false;

                if (value != null)
                    EnsurePhysicsSystems();
            }
        }
        public StreamingManager Streaming { get; } = new StreamingManager();
        public InputState        Input { get; set; }

        /// <summary>When false, <see cref="StreamingManager"/> skips its per-frame tick (boot splash).</summary>
        public bool AllowStreamingUpdates { get; set; } = true;

        /// <summary>When true, the host drives fly camera movement (sandbox WinForms shell).</summary>
        public bool HostControlsFlyCamera { get; set; }

        public IReadOnlyList<ISceneSubsystem> Subsystems => _subsystems;
        public IReadOnlyList<PhysicsWaterVolume> WaterVolumes => _waterVolumes;

        public RuntimeScene(string name = "Scene")
        {
            Name = name;

            // Studio configures these directly; the Player receives the same values through the
            // F5 environment. A Room may subsequently override them, but a room with no explicit
            // environment inherits the engine defaults in both 2D and 3D.
            EngineRenderingDefaults.ApplyEnvironmentOverrides();
            EngineFogDefaults fog = EngineRenderingDefaults.Fog;
            Environment.FogEnabled = fog.Enabled;
            Environment.FogColor = fog.Color;
            Environment.FogStart = fog.Start;
            Environment.FogEnd = fog.End;
            Environment.FogScreenSpace = fog.Enabled;

            World = new EcsWorld();
            _environmentQuery = new SceneEnvironmentQuery(this);
            Scheduler = new SystemScheduler(World);
            Scheduler.AddSystem(new FlyCameraSystem(this));
            Scheduler.AddSystem(new WildlifeSystem(this));
            Scheduler.AddSystem(new VisibilitySystem(this));
            Scheduler.AddSystem(new LodSystem(this));
            _frameBudgetSystem = new FrameBudgetSystem();
            Scheduler.AddSystem(_frameBudgetSystem);
        }

        /// <summary>
        /// Feeds the most recently resolved backend GPU timestamp into the next variable update.
        /// Timestamp queries are asynchronous, so this is deliberately treated as last-frame feedback.
        /// </summary>
        public void ObserveRenderFrame(double gpuMilliseconds)
        {
            _frameBudgetSystem.ObserveGpuMilliseconds(gpuMilliseconds);
        }

        public Entity CreateEntity(Vector3? position = null)
        {
            Entity entity = World.CreateEntity();
            World.Set(entity, new EntityLifecycleComponent { NeedsStart = true, Enabled = true });
            World.Set(entity, new Transform3DComponent
            {
                Position = position ?? Vector3.Zero,
                Rotation = Quaternion.Identity,
                Scale    = Vector3.One,
            });
            return entity;
        }

        public T AddSubsystem<T>(T subsystem) where T : ISceneSubsystem
        {
            _subsystems.Add(subsystem);
            if (subsystem is IStreamingProvider provider)
                Streaming.Register(provider);
            return subsystem;
        }

        public void RegisterWaterVolume(PhysicsWaterVolume volume)
        {
            if (volume == null) throw new ArgumentNullException(nameof(volume));
            _waterVolumes.RemoveAll(existing => string.Equals(existing.Id, volume.Id, StringComparison.OrdinalIgnoreCase));
            _waterVolumes.Add(volume);
        }

        public bool RemoveWaterVolume(string id) =>
            _waterVolumes.RemoveAll(volume => string.Equals(volume.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;

        public PhysicsWaterVolume FindWater(Vector3 position)
        {
            for (int i = _waterVolumes.Count - 1; i >= 0; i--)
                if (_waterVolumes[i].Contains(position)) return _waterVolumes[i];
            return null;
        }

        public PhysicsWaterVolume FindWater(float worldX, float worldZ)
        {
            for (int i = _waterVolumes.Count - 1; i >= 0; i--)
            {
                PhysicsWaterVolume volume = _waterVolumes[i];
                if (worldX >= volume.Minimum.X && worldX <= volume.Maximum.X &&
                    worldZ >= volume.Minimum.Z && worldZ <= volume.Maximum.Z)
                    return volume;
            }
            return null;
        }

        public void UpdateFixed(float fixedDelta)
        {
            RunLifecycleStart();

            for (int i = 0; i < _subsystems.Count; i++)
                _subsystems[i].FixedUpdate(this, fixedDelta);

            Scheduler.RunFixedPhase(fixedDelta, FixedPhase.PrePhysics);

            Physics?.ApplyWater(World, _waterVolumes, fixedDelta);
            // R7.12: Bepu Timestep on the dedicated physics motorway thread.
            Physics?.StepOnMotorway(World, fixedDelta, PhysicsMotorway);

            Scheduler.RunFixedPhase(fixedDelta, FixedPhase.PostPhysics);

            Physics?.SyncTransforms(World);
            SyncPhysicsTransforms();
        }

        public void UpdateVariable(float dt)
        {
            GameTime.Advance(dt);
            if (Climate != null)
            {
                Climate.Update(dt, Camera3D.Position);
                Climate.ApplyTo(Environment);
                if (Atmosphere != null)
                    WeatherMap.ConfigureViewCoverage(Camera3D.Position, Atmosphere.Options.CloudBaseHeight, Atmosphere.Options.CloudThickness);
                WeatherMap.Update(Climate.Current, Climate.Options.Seed);
                Atmosphere?.Update(Climate.Current, Camera3D.Position);
            }
            Scheduler.RunVariablePhase(dt);

            // Drain completed streaming jobs even during boot splash (providers stay gated below).
            Streaming.ProcessMainThreadCallbacks();

            if (AllowStreamingUpdates)
                Streaming.UpdateProviders(this, GameTime);

            for (int i = 0; i < _subsystems.Count; i++)
                _subsystems[i].Update(this, GameTime);
        }

        public void ConfigureClimate(EnvironmentOptions options, WeatherKind initialWeather)
        {
            Climate = new EnvironmentService(options, _environmentQuery);
            if (!options.AutomaticWeather)
                Climate.SetWeather(initialWeather, 0f);
            Climate.Update(0f, Camera3D.Position);
            Climate.ApplyTo(Environment);
            WeatherMap.Update(Climate.Current, Climate.Options.Seed);
        }

        /// <summary>Creates a service using this scene's shelter query without enabling visual sky effects.</summary>
        public EnvironmentService CreateEnvironmentService(EnvironmentOptions options) => new(options, _environmentQuery);

        public void DisableClimate() => Climate = null;

        public void ConfigureAtmosphere(AtmosphereOptions options)
        {
            Atmosphere = new AtmosphereService(options);
            if (Climate != null)
            {
                Atmosphere.Update(Climate.Current, Camera3D.Position);
                WeatherMap.ConfigureViewCoverage(Camera3D.Position, options.CloudBaseHeight, options.CloudThickness);
                WeatherMap.Update(Climate.Current, Climate.Options.Seed);
            }
        }

        public void DisableAtmosphere() => Atmosphere = null;

        public void SetWorldQuery(IWorldQuery query, MapDiscovery discovery = null)
        {
            WorldQuery = query;
            MapDiscovery = discovery ?? (query == null ? null : new MapDiscovery(query.Manifest.Bounds));
        }

        public int SubmitInstanceBatches(IRenderController renderer)
        {
            int submitted = 0;
            for (int i = 0; i < _subsystems.Count; i++)
                if (_subsystems[i] is IInstanceSceneSubsystem instances)
                    submitted += instances.SubmitInstances(this, renderer);
            return submitted;
        }

        public void CollectMeshes(MeshDrawCall[] buffer, ref int count, IRenderController renderer)
        {
            if (renderer != null && Atmosphere?.Current.CloudVolumes != null)
            {
                IReadOnlyList<FogVolume> clouds = Atmosphere.Current.CloudVolumes;
                for (int i = 0; i < clouds.Count; i++)
                    renderer.AddFogVolume(clouds[i]);
            }

            // AF2.3: push weather-map RGBA8 when raymarched clouds are enabled (FogVolumes stay).
            if ((Atmosphere?.Options.VolumetricClouds == true || MeshLightingDefaults.RaymarchedCloudsEnabled) && renderer != null)
            {
                int res = WeatherMap.Resolution;
                int bytes = res * res * 4;
                if (_weatherMapRgbaScratch == null || _weatherMapRgbaScratch.Length < bytes)
                    _weatherMapRgbaScratch = new byte[bytes];
                if (WeatherMap.TryCopyRgba8(
                        _weatherMapRgbaScratch,
                        out int w,
                        out int h,
                        out float halfExtent,
                        1f)) // The shader applies authored coverage once, after sampling this map.
                {
                    renderer.SetWeatherMapRgba8(
                        _weatherMapRgbaScratch.AsSpan(0, w * h * 4), w, h, halfExtent, WeatherMap.WorldCenter);
                }
            }

            for (int i = 0; i < _subsystems.Count; i++)
                _subsystems[i].SubmitMeshes(this, buffer, ref count, renderer);

            Vector3 cameraPos = Camera3D.Position;
            Matrix4x4 viewProjection = Camera3D.ViewMatrix * Camera3D.ProjectionMatrix;
            Frustum frustum = new(viewProjection);
            bool cull = RenderAutoState.FrustumCulling;
            bool occlude = RenderAutoState.OcclusionCulling;
            LodPolicy lodPolicy = LodPolicy.Current;
            int drawCount = count;

            World.Query<Transform3DComponent, EntityLifecycleComponent>((entity, ref transform, ref lifecycle) =>
            {
                if (!lifecycle.Enabled || drawCount >= buffer.Length) return;

                bool hasDraw = World.Has<MeshDrawComponent>(entity);
                bool hasLod  = World.Has<LodMeshComponent>(entity);
                if (!hasDraw && !hasLod) return;

                float radius = BoundsHelper.BoundingRadiusFromScale(
                    transform.Scale.X, transform.Scale.Y, transform.Scale.Z);

                // R7.10: VisibilitySystem already wrote LastResult — honour it and skip a second
                // frustum test. Unknown / missing component still uses collect-time Visibility.
                if (World.Has<VisibilityComponent>(entity))
                {
                    VisibilityResult result = World.GetRef<VisibilityComponent>(entity).LastResult;
                    if (result is VisibilityResult.FrustumCulled
                        or VisibilityResult.DistanceCulled
                        or VisibilityResult.SubPixelCulled
                        or VisibilityResult.Disabled
                        or VisibilityResult.Occluded)
                    {
                        return;
                    }

                    if (result == VisibilityResult.Visible)
                    {
                        if (occlude
                            && !Visibility.IsVisible(frustum, viewProjection, transform.Position, radius, testOcclusion: true))
                        {
                            return;
                        }
                    }
                    else if (cull && !Visibility.IsVisible(frustum, viewProjection, transform.Position, radius, occlude))
                    {
                        return;
                    }
                }
                else if (cull && !Visibility.IsVisible(frustum, viewProjection, transform.Position, radius, occlude))
                {
                    return;
                }

                MeshHandle mesh = MeshHandle.Invalid;
                MeshHandle shadowMesh = MeshHandle.Invalid;
                Vector4 tint = Vector4.One;
                MeshDrawFlags flags = MeshDrawFlags.None;
                float emissive = 0f;

                if (hasDraw)
                {
                    ref MeshDrawComponent draw = ref World.GetRef<MeshDrawComponent>(entity);
                    tint     = draw.Tint;
                    flags    = draw.Flags;
                    emissive = draw.Emissive;
                    mesh     = draw.Mesh;
                }

                if (hasLod)
                {
                    ref LodMeshComponent lod = ref World.GetRef<LodMeshComponent>(entity);
                    float distance = Vector3.Distance(cameraPos, transform.Position);
                    var policyLod = lod;
                    policyLod.MediumDistance = lodPolicy.EffectiveNear;
                    policyLod.FarDistance = lodPolicy.EffectiveMid;
                    policyLod.VeryFarDistance = lodPolicy.EffectiveFar;
                    mesh       = MeshLodBuilder.SelectMesh(policyLod, distance);
                    shadowMesh = MeshLodBuilder.SelectMeshForShadow(policyLod, distance);
                }

                if (!mesh.IsValid) return;

                float alpha = FixedTimestep.InterpolationAlpha;
                buffer[drawCount++] = new MeshDrawCall
                {
                    Mesh       = mesh,
                    ShadowMesh = shadowMesh,
                    World      = transform.InterpolatedWorldMatrix(alpha),
                    Tint      = new RenderColor(tint.X, tint.Y, tint.Z, tint.W),
                    Flags     = flags,
                    Alpha     = tint.W > 0f ? tint.W : 1f,
                    Emissive  = emissive,
                };
                RenderAutoState.SubmittedMeshes++;
                if ((flags & MeshDrawFlags.NoShadow) == 0)
                    RenderAutoState.ShadowCastersSubmitted++;
            });

            count = drawCount;
        }

        /// <summary>
        /// Tear down the current room so another can be loaded into the same scene.
        /// Keeps subsystems for which <paramref name="keepSubsystem"/> returns true.
        /// </summary>
        public void UnloadRoomContent(ScriptHostSystem scriptHost, Func<ISceneSubsystem, bool> keepSubsystem, bool preservePersistent = false)
        {
            Scripting.PgslCommands.ClearNavigation(this);
            bool KeepEntity(Entity entity) => preservePersistent && World.Has<ECS.Components.PersistentObjectComponent>(entity);
            scriptHost?.Clear(KeepEntity);

            for (int i = _subsystems.Count - 1; i >= 0; i--)
            {
                ISceneSubsystem sub = _subsystems[i];
                if (keepSubsystem != null && keepSubsystem(sub))
                    continue;
                sub.Dispose();
                _subsystems.RemoveAt(i);
            }

            RemovePhysicsSystems();
            _physics?.Dispose();
            _physics = null;
            _physicsSystemsRegistered = false;
            _waterVolumes.Clear();

            World.FlushDeferred();
            World.DestroyAllEntities(KeepEntity);
            // The next room owns a fresh physics world; retained bodies must register there.
            World.Query<RigidBodyComponent>((Entity entity, ref RigidBodyComponent body) => body.RegistrationId = 0);
            SetWorldQuery(null);
            HostControlsFlyCamera = false;
            Streaming.RequestRefresh();
        }

        private void EnsurePhysicsSystems()
        {
            if (_physicsSystemsRegistered || _physics == null)
                return;

            _physicsRegistrationSystem = new PhysicsRegistrationSystem(_physics);
            _physicsPlayerSystem = new PhysicsPlayerSystem(this);
            _physicsGrabSystem = new PhysicsGrabSystem(this);
            _raycastHighlightSystem = new RaycastHighlightSystem(this);
            Scheduler.AddFixedSystem(_physicsRegistrationSystem);
            Scheduler.AddFixedSystem(_physicsPlayerSystem);
            Scheduler.AddFixedSystem(_physicsGrabSystem);
            Scheduler.AddSystem(_raycastHighlightSystem);
            _physicsSystemsRegistered = true;
        }

        private void RemovePhysicsSystems()
        {
            Scheduler.RemoveSystem(_physicsRegistrationSystem);
            Scheduler.RemoveSystem(_physicsPlayerSystem);
            Scheduler.RemoveSystem(_physicsGrabSystem);
            Scheduler.RemoveSystem(_raycastHighlightSystem);
            _physicsRegistrationSystem = null;
            _physicsPlayerSystem = null;
            _physicsGrabSystem = null;
            _raycastHighlightSystem = null;
            _physicsSystemsRegistered = false;
        }

        private void SyncPhysicsTransforms()
        {
            World.Query<Transform3DComponent>((entity, ref transform3D) =>
            {
                if (!World.Has<Genesis.Runtime.ECS.Components.TransformComponent>(entity)) return;
                ref Genesis.Runtime.ECS.Components.TransformComponent transform = ref World.GetRef<Genesis.Runtime.ECS.Components.TransformComponent>(entity);
                transform.X = transform3D.Position.X;
                transform.Y = transform3D.Position.Y;
                transform.Z = transform3D.Position.Z;
                transform.ScaleX = transform3D.Scale.X;
                transform.ScaleY = transform3D.Scale.Y;
                transform.ScaleZ = transform3D.Scale.Z;
            });
        }

        public void Dispose()
        {
            for (int i = _subsystems.Count - 1; i >= 0; i--)
            {
                try { _subsystems[i].Dispose(); }
                catch (Exception error) { System.Diagnostics.Trace.WriteLine("Subsystem shutdown error: " + error); }
                _subsystems.RemoveAt(i);
            }

            RemovePhysicsSystems();
            _physics?.Dispose();
            _physics = null;
            _physicsSystemsRegistered = false;
            _physicsMotorway?.Dispose();
            _physicsMotorway = null;
            Streaming.Dispose();
            World.Dispose();
        }

        private void RunLifecycleStart()
        {
            World.Query<EntityLifecycleComponent>((entity, ref lifecycle) =>
            {
                if (!lifecycle.Enabled || !lifecycle.NeedsStart) return;
                lifecycle.NeedsStart = false;
                World.Set(entity, lifecycle);
            });
        }

        private sealed class SceneEnvironmentQuery : IEnvironmentQuery
        {
            private readonly RuntimeScene _scene;
            public SceneEnvironmentQuery(RuntimeScene scene) => _scene = scene;

            public EnvironmentLocalSample Sample(Vector3 observerPosition, Vector3 windDirection)
            {
                IWorldQuery world = _scene.WorldQuery;
                if (world == null) return EnvironmentLocalSample.Open(observerPosition);
                TerrainSurfaceSample terrain = world.SampleTerrain(observerPosition.X, observerPosition.Z);
                ShelterSample shelter = world.SampleShelter(observerPosition, windDirection);
                return new EnvironmentLocalSample(
                    shelter.RainOcclusion,
                    shelter.WindOcclusion,
                    terrain.Height,
                    world.WaterDepthAt(observerPosition.X, observerPosition.Z),
                    shelter.HasSolidCover);
            }
        }
    }
}
