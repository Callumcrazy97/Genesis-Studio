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
using Genesis.Shared.Assets;
using Genesis.Shared.Audio;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Rendering;

/// <summary>
/// Runs the non-visual and compound visual components authored on Object prefabs: particle assets,
/// autoplay/spatial audio, and point lights. The same subsystem is used by F5 and Object preview.
/// </summary>
public sealed class ObjectCompositionSubsystem : ISceneSubsystem
{
    private sealed class ParticleState
    {
        public required Entity Entity;
        public required string Asset;
        public required ParticleConfig Effect;
        public readonly List<ParticleLayerState> Layers = [];
        public long WriteTicks;
    }

    private sealed class ParticleLayerState
    {
        public required ParticleConfig Config;
        public required ParticleSimulation Simulation;
        public SpriteDrawCall[] SpriteCalls = [];
        public MeshHandle Quad;
        public MeshHandle[] Frames = [];
        public bool OwnsFrames;
        public TextureHandle Texture;
        public bool OwnsTexture;
        public IRenderController? Renderer;
    }

    private sealed class AudioState
    {
        public required Entity Entity;
        public AudioChannel Channel;
    }

    private readonly string _projectPath;
    private readonly IAudioSystem _audio;
    private readonly Dictionary<int, ParticleState> _particles = [];
    private readonly Dictionary<int, AudioState> _audioStates = [];
    private readonly HashSet<int> _seenParticles = [];
    private readonly HashSet<int> _seenAudio = [];
    private IRenderController? _lastRenderer;
    private readonly RuntimeModelAssetRegistry _particleModelAssets = new();
    private readonly RuntimeModelAssetRegistry _attachmentModelAssets = new();
    private readonly ModelGpuCache _particleModelGpu = new();
    private float _totalTime;

    public ObjectCompositionSubsystem(string projectPath, IAudioSystem? audio = null)
    {
        _projectPath = projectPath ?? string.Empty;
        _audio = audio ?? NullAudioSystem.Instance;
    }

    public int ParticleEmitterCount => _particles.Count;
    public int ActiveParticleCount => _particles.Values.Sum(state => state.Layers.Sum(layer => layer.Simulation.ActiveCount));
    public int ActiveAudioCount => _audioStates.Values.Count(state => state.Channel.IsValid);
    public int PointLightCount { get; private set; }

    public void FixedUpdate(RuntimeScene scene, float fixedDelta) { }

    public void Update(RuntimeScene scene, GameTime time)
    {
        if (scene?.World == null) return;
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
            if (state == null) return;
            foreach (ParticleLayerState layer in state.Layers)
            {
                if (component.FollowEntity)
                    layer.Simulation.SetEmitterOrigin(new Vector3(transform.X, transform.Y, transform.Z));
                layer.Simulation.UpdateCameraPosition(scene.Camera3D.Position);
                layer.Simulation.Step(dt);
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
                        Math.Clamp(component.Volume <= 0f ? 1f : component.Volume, 0f, 1f),
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
        });

        scene.World.Query<PointLightComponent>((Entity entity, ref PointLightComponent component) =>
        {
            if (component.Enabled && component.Intensity > 0f && component.Radius > 0f)
                PointLightCount++;
        });

        RemoveMissingStates();
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

        foreach (ParticleState state in _particles.Values)
        {
            foreach (ParticleLayerState layer in state.Layers)
            {
                EnsureParticleRenderResources(layer, renderer);
                if (layer.Frames.Length == 0 || !layer.Frames[0].IsValid) continue;
                layer.Simulation.DrawInstances3D(
                    renderer,
                    layer.Frames,
                    layer.Texture,
                    scene.Camera3D.Position,
                    scene.Camera3D.Forward);

                // First emitters to fill the eight slots win. A per-emitter re-bin would be the wrong
                // fix for a scene with more smoke than budget — the cap is the cost control.
                if (smokeExtinction && smokeCount < SmokeExtinctionMath.MaxVolumes)
                    smokeCount += layer.Simulation.TryGetSmokeVolumes(smokeVolumes[smokeCount..]);
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
        {
            foreach (ParticleLayerState layer in state.Layers)
            {
                int capacity = Math.Max(1, layer.Simulation.Capacity);
                if (layer.SpriteCalls.Length != capacity) layer.SpriteCalls = new SpriteDrawCall[capacity];
                int count = layer.Simulation.FillSpriteDrawCalls2D(
                    layer.SpriteCalls,
                    offsetX,
                    offsetY,
                    zoom,
                    layer.Texture);
                if (count > 0) commands.DrawSpriteBatch(layer.SpriteCalls.AsSpan(0, count));
            }
        }
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
        string resolved = ParticleAssetLoader.Resolve(_projectPath, component.Asset);
        long writeTicks = File.Exists(resolved) ? File.GetLastWriteTimeUtc(resolved).Ticks : 0;
        if (_particles.TryGetValue(entity.Id, out ParticleState? existing)
            && string.Equals(existing.Asset, component.Asset, StringComparison.OrdinalIgnoreCase)
            && existing.WriteTicks == writeTicks)
            return existing;

        if (existing is not null) ReleaseParticleState(existing);

        try
        {
            ParticleConfig config = ParticleAssetLoader.Load(_projectPath, component.Asset);
            float rateScale = component.RateScale <= 0f ? 1f : component.RateScale;
            ParticleState state = new()
            {
                Entity = entity,
                Asset = component.Asset,
                Effect = config,
                WriteTicks = writeTicks,
            };
            foreach ((string _, string _, ParticleConfig emitter) in ParticleAssetLoader.EnumerateEnabledEmitters(config))
            {
                emitter.EmitRate = component.EmitRate > 0f ? component.EmitRate : emitter.EmitRate * rateScale;
                ParticleSimulation simulation = new();
                simulation.LoadConfig(emitter);
                ConfigureMeshSurfaceSamples(simulation, emitter);
                if (emitter.CollisionMode != ParticleCollisionMode.None && scene.Physics is not null)
                {
                    simulation.SetCollisionHeightProvider(position =>
                    {
                        if (!scene.Physics.Raycast(scene.World, position + Vector3.UnitY * 0.5f, -Vector3.UnitY, 1000f, out var hit, entity))
                            return float.NaN;
                        bool terrainOrStatic = hit.Entity.IsNull;
                        return (terrainOrStatic ? emitter.CollideWithTerrain : emitter.CollideWithGeometry)
                            ? hit.Point.Y
                            : float.NaN;
                    });
                }
                state.Layers.Add(new ParticleLayerState { Config = emitter, Simulation = simulation });
            }
            _particles[entity.Id] = state;
            return state;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
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
        _attachmentModelAssets.Clear();
    }

    private void ReleaseParticleState(ParticleState state)
    {
        if (_lastRenderer is null) return;
        foreach (ParticleLayerState layer in state.Layers)
        {
            if (layer.OwnsFrames) ParticleRenderGeometry.ReleaseFrames(_lastRenderer, layer.Frames);
            if (layer.OwnsTexture && layer.Texture.IsValid) _lastRenderer.ReleaseTexture(layer.Texture);
        }
    }
}

