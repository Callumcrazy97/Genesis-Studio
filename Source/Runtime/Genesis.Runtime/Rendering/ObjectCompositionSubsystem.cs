#nullable enable annotations
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Genesis.Runtime.Assets;
using Genesis.Runtime.Core;
using Genesis.Runtime.ECS;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Particles;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Scene;
using Genesis.Rendering.Meshes;
using Genesis.Rendering.Primitives;
using Genesis.Rendering.Particles;
using Genesis.Shared.Assets;
using Genesis.Shared.Audio;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Rendering;

/// <summary>
/// Runs the non-visual and compound visual components authored on Object prefabs: particle assets,
/// autoplay/spatial audio, and point lights. The same subsystem is used by F5 and Object preview.
/// </summary>
public sealed partial class ObjectCompositionSubsystem : ISceneSubsystem, IRoomWarmUpSubsystem
{
    private sealed class ParticleState
    {
        public required Entity Entity;
        public required string Asset;
        public required ParticleConfig Effect;
        public readonly List<ParticleLayerState> Layers = [];
        public long WriteTicks;
        public long NextFreshnessCheckMilliseconds;
        public long AssetGeneration;
    }

    private sealed class ParticleLayerState
    {
        public required string EmitterId;
        public required ParticleConfig Config;
        public double AuthoredRate;
        public double AuthoredWindX, AuthoredWindZ;
        public ParticleSimulation? Simulation;
        public GpuParticleEmitter? GpuEmitter;
        public IGpuParticleRenderer? GpuOwner;
        public Vector3[] MeshSurfaceSamples = [];
        public SpriteDrawCall[] SpriteCalls = [];
        public MeshHandle Quad;
        public MeshHandle[] Frames = [];
        public bool OwnsFrames;
        public TextureHandle Texture;
        public bool OwnsTexture;
        public IRenderController? Renderer;
        public float PendingSeconds;
        public float EmitAccumulator;
        public int PendingBurst;
        public uint Sequence;
        public Matrix4x4 World = Matrix4x4.Identity;
        public ParticleDiagnostics LastDiagnostics;
        public readonly List<SoftwareParticleBirth> PendingSoftwareBirths = [];
        public long DroppedSoftwareBirths;
    }

    private sealed class AudioState
    {
        public required Entity Entity;
        public AudioChannel Channel;
    }

    private readonly string _projectPath;
    private readonly bool _particles2D;
    private readonly IAudioSystem _audio;
    private readonly Dictionary<int, ParticleState> _particles = [];
    private readonly Dictionary<int, AudioState> _audioStates = [];
    private readonly HashSet<int> _seenParticles = [];
    private readonly HashSet<int> _seenAudio = [];
    private IRenderController? _lastRenderer;
    private RuntimeScene? _lastScene;
    // The shared registry: this subsystem is created again for every room, and private registries
    // here parsed attachment parents and particle models again on every room change.
    private readonly RuntimeModelAssetRegistry _particleModelAssets = RuntimeModelAssetRegistry.Shared;
    private readonly RuntimeModelAssetRegistry _attachmentModelAssets = RuntimeModelAssetRegistry.Shared;
    private readonly ModelGpuCache _particleModelGpu = new();
    private float _totalTime;

    public ObjectCompositionSubsystem(string projectPath, IAudioSystem? audio = null, bool particles2D = false)
    {
        _projectPath = projectPath ?? string.Empty;
        _audio = audio ?? NullAudioSystem.Instance;
        _particles2D = particles2D;
    }

    public int ParticleEmitterCount => _particles.Count;
    public int ActiveParticleCount => _particles.Values.Sum(state => state.Layers.Sum(layer =>
        layer.GpuEmitter is { IsDisposed: false } ? layer.GpuEmitter.Diagnostics.Alive : layer.Simulation?.ActiveCount ?? 0));
    public ParticleExecutionDecision ParticleExecution => ParticleExecutionPolicy.Resolve(_lastRenderer);
    public int ActiveAudioCount => _audioStates.Values.Count(state => state.Channel.IsValid);
    public int PointLightCount { get; private set; }

    public void FixedUpdate(RuntimeScene scene, float fixedDelta) { }

    public void Update(RuntimeScene scene, GameTime time)
    {
        if (scene?.World == null) return;
        _lastScene = scene;
        float dt = Math.Clamp(time?.Delta ?? 0f, 0f, 0.25f);
        _totalTime = time?.Total ?? (_totalTime + dt);
        _seenParticles.Clear();
        _seenAudio.Clear();
        PointLightCount = 0;

        // Advance component-owned playback exactly once per live entity. Before this subsystem
        // existed, ModelAnimatorLifecycle and SpriteLifecycle were attachable but never ticked by
        // either the Player or an editor preview.
        scene.World.Query<TransformComponent>((Entity entity, ref TransformComponent transform) =>
            ComponentLifecycle.OnUpdate(scene.World, entity, dt));

        ModelSocketRuntime.Update(scene.World, _projectPath, _attachmentModelAssets);

        Genesis.Runtime.Scripting.PgslCommands.UpdateNavigation(scene, dt);
        Cameras.ThirdPersonCamera.UpdateScene(scene, dt);

        scene.World.Query<TransformComponent, ParticleComponent>((entity, ref transform, ref component) =>
        {
            if (!component.Emitting || string.IsNullOrWhiteSpace(component.Asset)) return;
            _seenParticles.Add(entity.Id);
            ParticleState? state = EnsureParticle(entity, component, scene);
            if (state == null)
            {
                // A burst whose effect cannot be loaded would otherwise wait for ever: give it a few
                // seconds (the file may be being written), then let it go.
                if (component.RemoveWhenDone)
                {
                    component.Age += dt;
                    if (component.Age >= 5f) scene.World.DestroyEntity(entity);
                }
                return;
            }
            if (component.Restart)
            {
                // The same emitter fires its burst again: a second blow, a second footfall.
                component.Restart = false;
                component.Age = 0f;
                foreach (ParticleLayerState layer in state.Layers)
                {
                    layer.PendingBurst = !layer.Config.Loop ? Math.Max(0, layer.Config.BurstCount) : 0;
                    layer.EmitAccumulator = 0f;
                }
            }

            component.Age += dt;
            if (component.RemoveWhenDone)
            {
                // A burst is over once its last particle has lived its life. An effect that emits
                // continuously is given its time, stopped, and then left to die away.
                bool continuous = false;
                float longestLife = 0f;
                foreach (ParticleLayerState layer in state.Layers)
                {
                    continuous |= layer.Config.Loop;
                    longestLife = MathF.Max(longestLife, (float)(layer.Config.Lifetime * (1.0 + layer.Config.LifetimeVariance)));
                }

                float emitting = continuous ? (component.EmitSeconds > 0f ? component.EmitSeconds : 0.25f) : 0f;
                if (continuous && component.Age >= emitting)
                {
                    component.HasEmitRateOverride = true;
                    component.EmitRate = 0f;
                }

                if (component.Age >= emitting + longestLife + 0.25f)
                {
                    scene.World.DestroyEntity(entity);
                    return;
                }
            }

            foreach (ParticleLayerState layer in state.Layers)
            {
                layer.World = ResolveParticleWorld(layer.Config, component, transform, scene.Camera3D.Position);
                layer.PendingSeconds = Math.Min(1f, layer.PendingSeconds + dt);
            }
        });

        scene.World.Query<TransformComponent, AudioComponent>((entity, ref transform, ref component) =>
        {
            if (string.IsNullOrWhiteSpace(component.Asset)) return;
            _seenAudio.Add(entity.Id);
            if (!_audioStates.TryGetValue(entity.Id, out AudioState? state))
            {
                state = new AudioState { Entity = entity, Channel = AudioChannel.Invalid };
                _audioStates[entity.Id] = state;
            }

            if (component.AutoPlay && !state.Channel.IsValid)
            {
                try
                {
                    int sound = _audio.LoadSound(component.Asset);
                    state.Channel = _audio.Play(
                        sound,
                        Math.Clamp(component.Volume, 0f, 1f),
                        component.Pitch <= 0f ? 1f : component.Pitch,
                        component.Looping);
                    component.SoundIndex = sound;
                    component.Playing = state.Channel.IsValid;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                {
                    // A missing/unavailable sound must not bring down F5 or the Object preview.
                    // Keep the component idle so selecting/fixing the asset can recover live.
                    component.SoundIndex = -1;
                    component.Playing = false;
                }
            }

            if (component.Spatial && state.Channel.IsValid)
                _audio.SetChannelPosition(state.Channel, new Vector3(transform.X, transform.Y, transform.Z));
            if (state.Channel.IsValid)
            {
                _audio.SetChannelVolume(state.Channel, Math.Clamp(component.Volume, 0f, 1f));
                if (component.SpatialSettings is { } spatial)
                    _audio.SetChannelSpatialSettings(state.Channel, component.Spatial, spatial);
            }
        });

        scene.World.Query<PointLightComponent>((Entity entity, ref PointLightComponent component) =>
        {
            if (component.Enabled && component.Intensity > 0f && component.Radius > 0f)
                PointLightCount++;
        });

        RemoveMissingStates();
    }

    private readonly HashSet<string> _soundsReadAhead = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Makes the room's particle emitters and the sounds its Objects play on arrival ready while
    /// the room waits behind a loading screen. An emitter is set up and run for one sixtieth of a
    /// second, so that the frame drawn behind the cover makes its buffers and compiles its
    /// shaders; without this they were all made in the room's first running frame. Returns false
    /// in the frame it sets a new emitter up, so that one more frame is drawn before the room is
    /// shown.
    /// </summary>
    public bool WarmUp(RuntimeScene scene)
    {
        if (scene?.World == null) return true;
        _lastScene = scene;
        bool settled = true;
        scene.World.Query<TransformComponent, ParticleComponent>((entity, ref transform, ref component) =>
        {
            if (!component.Emitting || string.IsNullOrWhiteSpace(component.Asset)) return;
            bool known = _particles.ContainsKey(entity.Id);
            ParticleState? state = EnsureParticle(entity, component, scene);
            if (state == null) return;
            foreach (ParticleLayerState layer in state.Layers)
            {
                layer.World = ResolveParticleWorld(layer.Config, component, transform, scene.Camera3D.Position);
                if (!known) layer.PendingSeconds = 1f / 60f;
            }

            if (!known) settled = false;
        });

        scene.World.Query<TransformComponent, AudioComponent>((entity, ref transform, ref component) =>
        {
            if (!component.AutoPlay || string.IsNullOrWhiteSpace(component.Asset) || !_soundsReadAhead.Add(component.Asset)) return;
            try
            {
                _audio.LoadSound(component.Asset);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                // The update that plays it reports a sound that cannot be read.
            }
        });
        return settled;
    }

    public void SubmitMeshes(
        RuntimeScene scene,
        MeshDrawCall[] buffer,
        ref int count,
        IRenderController renderer)
    {
        if (scene?.World == null || buffer == null || renderer == null) return;
        _lastRenderer = renderer;
        SubmitPointLights(scene, renderer);

        // AF1.6: derived from the same live sims that are about to be drawn, so the smoke that
        // darkens the scene is the smoke on screen. Gated on the installation master so the binning
        // costs nothing at all in the default-off configuration.
        bool smokeExtinction = MeshLightingDefaults.SmokeExtinctionEnabled;
        Span<SmokeExtinctionMath.SmokeVolume> smokeVolumes =
            stackalloc SmokeExtinctionMath.SmokeVolume[SmokeExtinctionMath.MaxVolumes];
        int smokeCount = 0;

        ParticleExecutionDecision execution = ParticleExecutionPolicy.Resolve(renderer);
        foreach (ParticleState state in _particles.Values)
        {
            if (execution.Target == ParticleExecutionTarget.Gpu)
            {
                if (renderer is not IGpuParticleRenderer gpuRenderer)
                    throw new InvalidOperationException("Renderer reports GPU particle capability but does not expose IGpuParticleRenderer.");

                EnsureGpuEmitters(state, gpuRenderer);
                foreach (ParticleLayerState layer in EventOrderedLayers(state))
                {
                    EnsureParticleRenderResources(layer, renderer);
                    AdvanceGpuLayer(state, layer, gpuRenderer);
                    if (layer.Frames.Length > 0 && layer.Frames[0].IsValid && layer.GpuEmitter is not null)
                        gpuRenderer.SubmitParticles3D(layer.GpuEmitter, layer.Frames[0], layer.Texture);
                }
            }
            else if (execution.Target == ParticleExecutionTarget.CpuSoftware)
            {
                foreach (ParticleLayerState layer in state.Layers)
                {
                    EnsureParticleRenderResources(layer, renderer);
                    AdvanceSoftwareLayer(state, layer, scene);
                    if (layer.Frames.Length == 0 || !layer.Frames[0].IsValid || layer.Simulation is null) continue;
                    layer.Simulation.DrawInstances3D(
                        renderer,
                        layer.Frames,
                        layer.Texture,
                        scene.Camera3D.Position,
                        scene.Camera3D.Forward);

                    if (smokeExtinction && smokeCount < SmokeExtinctionMath.MaxVolumes)
                        smokeCount += layer.Simulation.TryGetSmokeVolumes(smokeVolumes[smokeCount..]);
                }
            }
            else if (execution.Target == ParticleExecutionTarget.UnsupportedHardware)
            {
                ParticleExecutionPolicy.ThrowIfHardwareWouldFallbackToCpu(renderer);
            }
        }

        if (smokeExtinction)
        {
            renderer.ClearSmokeVolumes();
            for (int i = 0; i < smokeCount; i++)
            {
                SmokeExtinctionMath.SmokeVolume volume = smokeVolumes[i];
                renderer.AddSmokeVolume(volume.Position, volume.Radius, volume.Density);
            }
        }
    }

    /// <summary>Queues attached particle systems into the ordinary 2D frame.</summary>
    public void Render2D(IRenderCommandSink commands, float offsetX, float offsetY, float zoom)
    {
        if (commands == null) return;
        foreach (ParticleState state in _particles.Values)
            foreach (ParticleLayerState layer in EventOrderedLayers(state))
                commands.DrawDeferred2D(new DeferredParticleDraw(this, state, layer, offsetX, offsetY, zoom));
    }

    public int SubmitPointLights(RuntimeScene scene, IRenderController renderer)
    {
        if (scene?.World == null || renderer == null) return 0;
        int submitted = 0;
        scene.World.Query<TransformComponent, PointLightComponent>((entity, ref transform, ref light) =>
        {
            if (!light.Enabled || light.Intensity <= 0f || light.Radius <= 0f) return;
            EvaluateLight(entity.Id, light, out Vector3 color, out float radius, out float intensity);
            renderer.AddPointLight(
                new Vector3(transform.X, transform.Y, transform.Z) + light.Offset,
                color,
                radius,
                intensity,
                light.Falloff <= 0f ? 2f : light.Falloff);
            submitted++;
        });
        foreach (ParticleState state in _particles.Values)
        {
            ParticleLightConfig light = state.Effect.Light;
            if (light?.Enabled != true || light.Intensity <= 0 || light.Radius <= 0) continue;
            if (!scene.World.Has<TransformComponent>(state.Entity)) continue;
            ref TransformComponent transform = ref scene.World.GetRef<TransformComponent>(state.Entity);
            float flicker = EvaluateParticleLightFlicker(state.Entity.Id, light);
            ParticleColor color = light.Color ?? new ParticleColor(1f, 0.45f, 0.1f, 1f);
            renderer.AddPointLight(
                new Vector3(transform.X + (float)light.OffsetX, transform.Y + (float)light.OffsetY, transform.Z + (float)light.OffsetZ),
                new Vector3(color.R, color.G, color.B),
                (float)light.Radius,
                (float)light.Intensity * flicker,
                (float)Math.Max(0.1, light.Falloff));
            submitted++;
        }
        PointLightCount = submitted;
        return submitted;
    }

    private float EvaluateParticleLightFlicker(int entityId, ParticleLightConfig light)
    {
        float amount = (float)Math.Clamp(light.FlickerAmount, 0d, 1d);
        if (amount <= 0f) return 1f;
        float frequency = (float)Math.Max(0.1, light.FlickerFrequency);
        float phase = entityId * 0.6180339f;
        float noise = MathF.Sin((_totalTime * frequency + phase) * MathF.Tau)
            * MathF.Sin((_totalTime * frequency * 0.47f + phase * 1.7f) * MathF.Tau);
        return MathF.Max(0f, 1f - amount * 0.5f + noise * amount * 0.5f);
    }

    private void EvaluateLight(
        int entityId,
        in PointLightComponent light,
        out Vector3 color,
        out float radius,
        out float intensity)
    {
        Vector3 primary = light.Color.LengthSquared() <= 0f
            ? Vector3.One
            : Vector3.Clamp(light.Color, Vector3.Zero, Vector3.One);
        Vector3 secondary = Vector3.Clamp(light.SecondaryColor, Vector3.Zero, Vector3.One);
        Vector3 tertiary = Vector3.Clamp(light.TertiaryColor, Vector3.Zero, Vector3.One);
        float speed = MathF.Max(0f, light.ActionSpeed);
        float amount = Math.Clamp(light.ActionAmount, 0f, 1f);
        float phase = light.Phase + entityId * 0.6180339f;
        float wave = MathF.Sin((_totalTime * speed + phase) * MathF.Tau) * 0.5f + 0.5f;

        color = primary;
        radius = MathF.Max(0.01f, light.Radius);
        intensity = MathF.Max(0f, light.Intensity);

        switch (light.Action)
        {
            case LightEmitterAction.Flicker:
                // A deterministic multi-frequency signal avoids frame-rate-dependent random pops.
                float flicker = MathF.Sin((_totalTime * speed * 13.17f + phase) * MathF.Tau)
                    * MathF.Sin((_totalTime * speed * 7.31f + phase * 1.7f) * MathF.Tau);
                intensity *= MathF.Max(0f, 1f - amount + amount * (0.5f + 0.5f * flicker));
                break;
            case LightEmitterAction.Pulse:
                intensity *= 1f - amount + amount * wave;
                break;
            case LightEmitterAction.Glow:
                float glow = 1f - amount * 0.5f + amount * wave;
                intensity *= glow;
                radius *= glow;
                break;
            case LightEmitterAction.ColourCycle:
                if (light.ColorCount >= 3)
                {
                    float cycle = wave * 3f;
                    color = cycle < 1f
                        ? Vector3.Lerp(primary, secondary, cycle)
                        : cycle < 2f
                            ? Vector3.Lerp(secondary, tertiary, cycle - 1f)
                            : Vector3.Lerp(tertiary, primary, cycle - 2f);
                }
                else if (light.ColorCount >= 2)
                {
                    color = Vector3.Lerp(primary, secondary, wave);
                }
                break;
        }
    }

    private ParticleState? EnsureParticle(Entity entity, ParticleComponent component, RuntimeScene scene)
    {
        long now = Environment.TickCount64;
        bool sameAsset = _particles.TryGetValue(entity.Id, out ParticleState? existing)
            && string.Equals(existing.Asset, component.Asset, StringComparison.OrdinalIgnoreCase);
        long generation = RuntimeAssetPolicy.Generation;
        if (sameAsset && existing!.AssetGeneration == generation && now < existing.NextFreshnessCheckMilliseconds)
        {
            ApplyParticleRate(existing, component);
            return existing;
        }
        string resolved = ParticleAssetLoader.Resolve(_projectPath, component.Asset);
        AssetIoCounters.Check(2);
        long writeTicks = File.Exists(resolved) ? File.GetLastWriteTimeUtc(resolved).Ticks : 0;
        if (sameAsset && existing!.WriteTicks == writeTicks)
        {
            existing.NextFreshnessCheckMilliseconds = RuntimeAssetPolicy.NextCheck(now, 250, entity.Id);
            existing.AssetGeneration = generation;
            ApplyParticleRate(existing, component);
            return existing;
        }

        if (existing is not null) ReleaseParticleState(existing);

        try
        {
            ParticleConfig config = ParticleAssetLoader.Load(_projectPath, component.Asset);
            ParticleState state = new()
            {
                Entity = entity,
                Asset = component.Asset,
                Effect = config,
                WriteTicks = writeTicks,
                NextFreshnessCheckMilliseconds = RuntimeAssetPolicy.NextCheck(now, 250, entity.Id),
                AssetGeneration = generation,
            };
            foreach ((string emitterId, string _, ParticleConfig emitter) in ParticleAssetLoader.EnumerateEnabledEmitters(config))
            {
                ParticleConfig layout = _particles2D ? Particle2DLayout.ForSimulation(emitter) : emitter;
                state.Layers.Add(new ParticleLayerState
                {
                    EmitterId = emitterId,
                    Config = layout,
                    AuthoredRate = emitter.EmitRate,
                    AuthoredWindX = layout.WindX,
                    AuthoredWindZ = layout.WindZ,
                    MeshSurfaceSamples = LoadMeshSurfaceSamples(emitter),
                    PendingBurst = !emitter.Loop ? Math.Max(0, emitter.BurstCount) : 0,
                });
            }
            ApplyParticleRate(state, component);
            _particles[entity.Id] = state;
            return state;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void ApplyParticleRate(ParticleState state, ParticleComponent component)
    {
        float scale = component.RateScale <= 0 ? 1 : component.RateScale;
        foreach (ParticleLayerState layer in state.Layers)
        {
            double rate = Math.Max(0, component.HasEmitRateOverride || component.EmitRate > 0 ? component.EmitRate : layer.AuthoredRate * scale);
            // The emitter's own wind, when it has been given one, in place of the effect's.
            double windX = component.Wind?.X ?? layer.AuthoredWindX, windZ = component.Wind?.Y ?? layer.AuthoredWindZ;
            if (layer.Config.EmitRate == rate && layer.Config.WindX == windX && layer.Config.WindZ == windZ) continue;
            layer.Config.EmitRate = rate;
            layer.Config.WindX = windX;
            layer.Config.WindZ = windZ;
            layer.Simulation?.UpdateConfig(layer.Config);
        }
    }

    /// <summary>
    /// The wind an entity's effect is drifting on and where its particles are starting from, as
    /// the engine last worked them out. False when the entity has no running effect.
    /// </summary>
    public bool TryGetParticleFlow(Entity entity, out Vector2 wind, out Vector3 origin)
    {
        wind = default;
        origin = default;
        if (!_particles.TryGetValue(entity.Id, out ParticleState? state) || state.Layers.Count == 0) return false;
        ParticleLayerState layer = state.Layers[0];
        wind = new Vector2((float)layer.Config.WindX, (float)layer.Config.WindZ);
        origin = layer.World.Translation;
        return true;
    }

    private void EnsureParticleRenderResources(ParticleLayerState state, IRenderController renderer)
    {
        if (!ReferenceEquals(state.Renderer, renderer))
        {
            state.Quad = MeshHandle.Invalid;
            state.Frames = [];
            state.OwnsFrames = false;
            state.Texture = TextureHandle.Invalid;
            state.OwnsTexture = false;
            state.Renderer = renderer;
        }
        if (state.Frames.Length == 0)
        {
            if (state.Config.Alignment == ParticleAlignment.Mesh3D
                && !string.IsNullOrWhiteSpace(state.Config.MeshParticleAsset))
            {
                try
                {
                    GModelAsset asset = _particleModelAssets.Load(_projectPath, state.Config.MeshParticleAsset);
                    ModelGpuCache.CachedAsset cached = _particleModelGpu.GetOrCreate(renderer, asset);
                    MeshHandle modelMesh = cached.Meshes.FirstOrDefault(mesh => !mesh.IsSkinned && mesh.Lod == 0)?.Mesh ?? MeshHandle.Invalid;
                    if (modelMesh.IsValid) state.Frames = [modelMesh];
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    _ = exception;
                }
            }
            if (state.Frames.Length == 0)
            {
                state.Frames = ParticleRenderGeometry.RegisterFrames(renderer, state.Config);
                state.OwnsFrames = true;
            }
            state.Quad = state.Frames.Length > 0 ? state.Frames[0] : MeshHandle.Invalid;
        }
        if (!state.Texture.IsValid && !string.IsNullOrWhiteSpace(state.Config.TexturePath))
        {
            string path = ResolveParticleTexture(state.Config.TexturePath);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                state.Texture = renderer.LoadTexture(path);
        }
        if (!state.Texture.IsValid)
        {
            state.Texture = ParticleRenderGeometry.CreateDefaultTexture(renderer, state.Config);
            state.OwnsTexture = state.Texture.IsValid;
        }
    }

    private void ConfigureMeshSurfaceSamples(ParticleSimulation simulation, ParticleConfig config)
    {
        if (config.Shape != ParticleEmitShape.MeshSurface || string.IsNullOrWhiteSpace(config.MeshSurfaceAsset)) return;
        try
        {
            GModelAsset asset = _particleModelAssets.Load(_projectPath, config.MeshSurfaceAsset);
            var positions = new List<Vector3>();
            foreach (GModelMesh mesh in asset.Meshes)
            {
                if (mesh.Vertices is { Length: > 0 }) positions.AddRange(mesh.Vertices.Select(vertex => vertex.Position));
                else if (mesh.SkinnedVertices is { Length: > 0 }) positions.AddRange(mesh.SkinnedVertices.Select(vertex => vertex.Position));
                if (positions.Count >= 100_000) break;
            }
            if (positions.Count > 0) simulation.SetMeshSurfaceSamples(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(positions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _ = exception;
        }
    }

    private string ResolveParticleTexture(string asset)
    {
        string resolved = TexturePathResolver.Resolve(_projectPath, asset);
        if (string.IsNullOrWhiteSpace(resolved)) return string.Empty;
        if (!SpriteAssetLoader.IsSpriteDescriptorPath(resolved)) return resolved;
        SpriteRuntimeAsset sprite = SpriteAssetLoader.Load(resolved);
        return SpriteAssetLoader.ResolveFrameTexturePath(resolved, sprite, 0);
    }

    private void RemoveMissingStates()
    {
        foreach (int id in _particles.Keys.Where(id => !_seenParticles.Contains(id)).ToArray())
        {
            ReleaseParticleState(_particles[id]);
            _particles.Remove(id);
        }
        foreach (int id in _audioStates.Keys.Where(id => !_seenAudio.Contains(id)).ToArray())
        {
            AudioState state = _audioStates[id];
            if (state.Channel.IsValid) _audio.Stop(state.Channel);
            _audioStates.Remove(id);
        }
    }

    public void Dispose()
    {
        foreach (AudioState state in _audioStates.Values)
            if (state.Channel.IsValid) _audio.Stop(state.Channel);
        if (_lastRenderer != null)
        {
            foreach (ParticleState state in _particles.Values)
                ReleaseParticleState(state);
            _particleModelGpu.Clear(_lastRenderer);
        }
        _particles.Clear();
        _audioStates.Clear();
    }

    private void ReleaseParticleState(ParticleState state)
    {
        if (_lastRenderer is null) return;
        foreach (ParticleLayerState layer in state.Layers)
        {
            layer.GpuEmitter?.Dispose();
            layer.GpuEmitter = null;
            layer.GpuOwner = null;
            if (layer.OwnsFrames) ParticleRenderGeometry.ReleaseFrames(_lastRenderer, layer.Frames);
            if (layer.OwnsTexture && layer.Texture.IsValid) _lastRenderer.ReleaseTexture(layer.Texture);
        }
    }
}

