#nullable enable annotations
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Genesis.Rendering.Particles;
using Genesis.Runtime.Particles;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Rendering;

/// <summary>
/// What many emitters of the same effect share, so hundreds of short bursts (falling leaves,
/// sparks) cost little: each emitter's GPU definition is made once and only moved afterwards,
/// played-out GPU emitters are kept for the next burst, the flipbook quads and the default
/// texture are made once, one look at the file serves every emitter of an effect, and batched
/// bursts (<see cref="ParticleBursts.PlayBatched"/>) of one effect share one emitter: one
/// simulation and one draw a frame for all of them.
/// </summary>
public sealed partial class ObjectCompositionSubsystem
{
    // ── Definitions ─────────────────────────────────────────────────────────

    /// <summary>
    /// The layer's GPU definition, made again only when its transform or wind has changed (its
    /// table of curves and colours is kept then). A running game used to make two a frame for
    /// every emitter.
    /// </summary>
    private static GpuParticleDefinition CurrentGpuDefinition(ParticleLayerState layer)
    {
        GpuParticleDefinition? kept = layer.GpuDefinition;
        if (kept is not null && layer.GpuDefinitionWorld == layer.World
            && layer.GpuDefinitionWindX == layer.Config.WindX && layer.GpuDefinitionWindZ == layer.Config.WindZ)
            return kept;
        GpuParticleDefinition definition = kept is null
            ? GpuParticleDefinitionBuilder.Build(layer.Config, layer.World, layer.MeshSurfaceSamples, previousLookup: layer.GpuLookup)
            : GpuParticleDefinitionBuilder.Move(kept, layer.Config, layer.World);
        layer.GpuLookup = definition.Lookup;
        layer.GpuDefinition = definition;
        layer.GpuDefinitionWorld = layer.World;
        layer.GpuDefinitionWindX = layer.Config.WindX;
        layer.GpuDefinitionWindZ = layer.Config.WindZ;
        return definition;
    }

    // ── GPU emitters kept for the next burst ────────────────────────────────

    private const int MaxIdleEmitters = 64;
    private readonly Dictionary<int, Stack<GpuParticleEmitter>> _idleEmitters = [];
    private IGpuParticleRenderer? _idleEmitterOwner;
    private int _idleEmitterCount;

    /// <summary>
    /// How often an emitter's live counts are read back from the GPU: every tenth of a second
    /// with a few emitters (the editors' figures), less often with many, so that a game with
    /// hundreds of bursts reads about 160 a second in all rather than ten a second each.
    /// </summary>
    private float DiagnosticsInterval => Math.Clamp(_particles.Count / 160f, .1f, 2f);

    private GpuParticleEmitter RentEmitter(IGpuParticleRenderer renderer, GpuParticleDefinition definition, int seed)
    {
        if (ReferenceEquals(renderer, _idleEmitterOwner)
            && _idleEmitters.TryGetValue(definition.Capacity, out Stack<GpuParticleEmitter>? idle))
        {
            while (idle.Count > 0)
            {
                GpuParticleEmitter emitter = idle.Pop();
                _idleEmitterCount--;
                if (emitter.IsDisposed) continue;
                emitter.UpdateDefinition(definition);
                // Starts it over: its old particles go, and nothing it was still asked to do runs.
                renderer.EnqueueParticleReset(emitter, seed);
                emitter.DiagnosticsIntervalSeconds = DiagnosticsInterval;
                return emitter;
            }
        }
        GpuParticleEmitter created = renderer.CreateParticleEmitter(definition, seed);
        created.DiagnosticsIntervalSeconds = DiagnosticsInterval;
        return created;
    }

    /// <summary>Keeps a layer's GPU emitter for the next emitter of its size (or frees it).</summary>
    private void ReturnEmitter(ParticleLayerState layer)
    {
        GpuParticleEmitter? emitter = layer.GpuEmitter;
        IGpuParticleRenderer? owner = layer.GpuOwner;
        layer.GpuEmitter = null;
        layer.GpuOwner = null;
        if (emitter is null || emitter.IsDisposed) return;
        if (owner is null || _idleEmitterCount >= MaxIdleEmitters)
        {
            emitter.Dispose();
            return;
        }
        if (!ReferenceEquals(owner, _idleEmitterOwner))
        {
            DisposeIdleEmitters();
            _idleEmitterOwner = owner;
        }
        if (!_idleEmitters.TryGetValue(emitter.Capacity, out Stack<GpuParticleEmitter>? idle))
            _idleEmitters[emitter.Capacity] = idle = new Stack<GpuParticleEmitter>();
        idle.Push(emitter);
        _idleEmitterCount++;
    }

    private void DisposeIdleEmitters()
    {
        foreach (Stack<GpuParticleEmitter> idle in _idleEmitters.Values)
            while (idle.Count > 0) idle.Pop().Dispose();
        _idleEmitterCount = 0;
    }

    // ── Quads and textures made once ────────────────────────────────────────

    private IRenderController? _sharedGeometryOwner;
    private readonly Dictionary<(int Columns, int Rows), MeshHandle[]> _sharedFrames = [];
    private TextureHandle _sharedSoftTexture = TextureHandle.Invalid;
    private TextureHandle _sharedSmokyTexture = TextureHandle.Invalid;

    private void BindSharedGeometry(IRenderController renderer)
    {
        if (ReferenceEquals(_sharedGeometryOwner, renderer)) return;
        // Another renderer: what was made on the old one is forgotten with it, as a layer's own was.
        _sharedFrames.Clear();
        _sharedSoftTexture = TextureHandle.Invalid;
        _sharedSmokyTexture = TextureHandle.Invalid;
        _sharedGeometryOwner = renderer;
    }

    /// <summary>The flipbook's quads (one for each frame), made once for each flipbook layout.</summary>
    private MeshHandle[] SharedFrames(IRenderController renderer, ParticleConfig config)
    {
        BindSharedGeometry(renderer);
        int columns = config.UseFlipbook ? Math.Clamp(config.FlipbookColumns, 1, 64) : 1;
        int rows = config.UseFlipbook ? Math.Clamp(config.FlipbookRows, 1, 64) : 1;
        if (_sharedFrames.TryGetValue((columns, rows), out MeshHandle[]? frames) && frames.Length > 0) return frames;
        frames = ParticleRenderGeometry.RegisterFrames(renderer, config);
        if (frames.Length > 0) _sharedFrames[(columns, rows)] = frames;
        return frames;
    }

    /// <summary>The soft sprite drawn when an effect has no Image, made once (it differs only for alpha blending).</summary>
    private TextureHandle SharedDefaultTexture(IRenderController renderer, ParticleConfig config)
    {
        BindSharedGeometry(renderer);
        bool smoky = config.BlendMode == ParticleBlendMode.Alpha;
        TextureHandle texture = smoky ? _sharedSmokyTexture : _sharedSoftTexture;
        if (texture.IsValid) return texture;
        texture = ParticleRenderGeometry.CreateDefaultTexture(renderer, config);
        if (smoky) _sharedSmokyTexture = texture;
        else _sharedSoftTexture = texture;
        return texture;
    }

    private void ReleaseSharedGeometry()
    {
        // Released only through the renderer they were made on, while it is still the one in use.
        if (_sharedGeometryOwner is { } renderer && ReferenceEquals(renderer, _lastRenderer))
        {
            foreach (MeshHandle[] frames in _sharedFrames.Values) ParticleRenderGeometry.ReleaseFrames(renderer, frames);
            if (_sharedSoftTexture.IsValid) renderer.ReleaseTexture(_sharedSoftTexture);
            if (_sharedSmokyTexture.IsValid) renderer.ReleaseTexture(_sharedSmokyTexture);
        }
        _sharedFrames.Clear();
        _sharedSoftTexture = TextureHandle.Invalid;
        _sharedSmokyTexture = TextureHandle.Invalid;
        _sharedGeometryOwner = null;
    }

    // ── One look at an effect's file for all its emitters ───────────────────

    private sealed class AssetStamp
    {
        public long WriteTicks;
        public long NextCheck;
        public long Generation = -1;
    }

    private readonly Dictionary<string, AssetStamp> _assetStamps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When an effect's file was last written, looked at no more than every quarter second for all
    /// the emitters that play it (and not again in an exported game, as before).
    /// </summary>
    private long AssetWriteTicks(string asset, long now, long generation)
    {
        if (!_assetStamps.TryGetValue(asset, out AssetStamp? stamp))
            _assetStamps[asset] = stamp = new AssetStamp();
        else if (stamp.Generation == generation && now < stamp.NextCheck)
            return stamp.WriteTicks;
        string resolved = ParticleAssetLoader.Resolve(_projectPath, asset);
        AssetIoCounters.Check(2);
        stamp.WriteTicks = File.Exists(resolved) ? File.GetLastWriteTimeUtc(resolved).Ticks : 0;
        stamp.NextCheck = RuntimeAssetPolicy.NextCheck(now, 250, StringComparer.OrdinalIgnoreCase.GetHashCode(asset));
        stamp.Generation = generation;
        return stamp.WriteTicks;
    }

    // ── Batched bursts: one emitter for every burst of an effect ───────────

    /// <summary>Bursts of one effect a shared emitter holds at once (times the particles of one burst).</summary>
    private const int BatchedBurstsPerEmitter = 1024;
    private const int MaxBatchedCapacity = 65_536;
    private const float BatchedDiagnosticsSeconds = .5f;

    private sealed class BatchedEffect
    {
        public required string Asset;
        public long WriteTicks;
        public bool CanBatch;
        public float LongestLife;
        /// <summary>The simulation time after which every particle it holds has died.</summary>
        public float AliveUntil;
        public readonly List<ParticleLayerState> Layers = [];
        /// <summary>Where this frame's bursts are born (scale and position).</summary>
        public readonly List<Matrix4x4> Pending = [];
    }

    private readonly Dictionary<string, BatchedEffect> _batched = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ParticleBursts.BatchedBurst> _batchedRequests = [];
    private bool _meshesSubmitted;

    /// <summary>Shared emitters for batched bursts that exist now (one for each emitter layer of each effect).</summary>
    public int BatchedBurstEmitterCount
    {
        get
        {
            int count = 0;
            foreach (BatchedEffect effect in _batched.Values)
                foreach (ParticleLayerState layer in effect.Layers)
                    if (layer.GpuEmitter is { IsDisposed: false }) count++;
            return count;
        }
    }

    private int BatchedParticleCount
    {
        get
        {
            int count = 0;
            foreach (BatchedEffect effect in _batched.Values)
                foreach (ParticleLayerState layer in effect.Layers)
                    if (layer.GpuEmitter is { IsDisposed: false } emitter) count += emitter.Diagnostics.Alive;
            return count;
        }
    }

    /// <summary>
    /// Takes the bursts scripts asked for since the last update. Each goes to its effect's shared
    /// emitter when the effect and the renderer allow it, otherwise it plays as an ordinary burst.
    /// </summary>
    private void ConsumeBatchedBursts(RuntimeScene scene, float dt)
    {
        foreach (BatchedEffect effect in _batched.Values)
            foreach (ParticleLayerState layer in effect.Layers)
                layer.PendingSeconds = Math.Min(1f, layer.PendingSeconds + dt);

        _batchedRequests.Clear();
        ParticleBursts.TakeBatched(scene.World, _batchedRequests);
        if (_batchedRequests.Count == 0) return;
        // Shared emitters are GPU emitters drawn with the 3D meshes. A renderer that has not drawn
        // a 3D frame yet, a 2D room and the software renderer play ordinary bursts instead.
        bool shared = _meshesSubmitted && !_particles2D && _lastRenderer is IGpuParticleRenderer
            && ParticleExecutionPolicy.Resolve(_lastRenderer).Target == ParticleExecutionTarget.Gpu;
        long now = Environment.TickCount64;
        long generation = RuntimeAssetPolicy.Generation;
        foreach (ParticleBursts.BatchedBurst request in _batchedRequests)
        {
            BatchedEffect? effect = shared ? EnsureBatchedEffect(request.Asset, now, generation) : null;
            if (effect is null)
            {
                ParticleBursts.Play(scene.World, request.Asset, request.Position, request.Scale);
                continue;
            }
            effect.Pending.Add(Matrix4x4.CreateScale(request.Scale) * Matrix4x4.CreateTranslation(request.Position));
        }
        _batchedRequests.Clear();
    }

    private BatchedEffect? EnsureBatchedEffect(string asset, long now, long generation)
    {
        long writeTicks = AssetWriteTicks(asset, now, generation);
        if (_batched.TryGetValue(asset, out BatchedEffect? effect))
        {
            if (effect.WriteTicks == writeTicks) return effect.CanBatch ? effect : null;
            // The effect was changed on disk: start its shared emitter again from the new file.
            ReleaseBatchedEffect(effect);
            _batched.Remove(asset);
        }

        effect = new BatchedEffect { Asset = asset, WriteTicks = writeTicks };
        try
        {
            ParticleConfig config = ParticleAssetLoader.Load(_projectPath, asset);
            effect.CanBatch = config.EventLinks is not { Count: > 0 } && config.Light?.Enabled != true;
            foreach ((string emitterId, string _, ParticleConfig emitter) in ParticleAssetLoader.EnumerateEnabledEmitters(config))
            {
                if (!CanShareEmitter(emitter))
                {
                    effect.CanBatch = false;
                    break;
                }
                effect.Layers.Add(new ParticleLayerState
                {
                    EmitterId = emitterId,
                    Config = emitter,
                    MeshSurfaceSamples = LoadMeshSurfaceSamples(emitter),
                    BurstBirths = Math.Clamp(Math.Min(emitter.BurstCount, emitter.MaxParticles), 1, GpuParticleProtocol.MaximumCapacity),
                });
                float trail = emitter.RendererKind == ParticleRendererKind.Trail ? (float)Math.Max(0, emitter.TrailDuration) : 0f;
                effect.LongestLife = MathF.Max(effect.LongestLife, (float)(emitter.Lifetime * (1.0 + emitter.LifetimeVariance)) + trail);
            }
            if (effect.Layers.Count == 0) effect.CanBatch = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            effect.CanBatch = false;
        }
        if (!effect.CanBatch) effect.Layers.Clear();
        _batched[asset] = effect;
        return effect.CanBatch ? effect : null;
    }

    /// <summary>
    /// Whether bursts of an emitter can share one emitter and look as they would alone: a single
    /// burst whose particles move in the world once born, drawn as quads or trails.
    /// </summary>
    private static bool CanShareEmitter(ParticleConfig config) =>
        !config.Loop && config.BurstCount > 0
        && config.SimulationSpace == ParticleSimulationSpace.World
        && !config.IsPlanar2D && !config.FollowCameraXZ
        && config.RendererKind is ParticleRendererKind.Billboard or ParticleRendererKind.Trail;

    /// <summary>
    /// Advances every shared emitter that has live particles, births this frame's bursts where they
    /// were asked for (one step each, taking no time), and draws each emitter once.
    /// </summary>
    private void SubmitBatchedBursts(IRenderController renderer, IGpuParticleRenderer gpu)
    {
        foreach (BatchedEffect effect in _batched.Values)
        {
            if (!effect.CanBatch) continue;
            bool births = effect.Pending.Count > 0;
            if (!births && _totalTime >= effect.AliveUntil)
            {
                // Every particle has died: nothing to simulate or draw until the next burst.
                foreach (ParticleLayerState layer in effect.Layers) layer.PendingSeconds = 0f;
                continue;
            }

            foreach (ParticleLayerState layer in effect.Layers)
            {
                EnsureParticleRenderResources(layer, renderer);
                GpuParticleEmitter emitter = EnsureBatchedEmitter(effect, layer, gpu);
                const float step = 1f / 60f;
                int safety = 0;
                while (layer.PendingSeconds >= step && safety++ < 16)
                {
                    gpu.EnqueueParticleStep(emitter, step, 0, ++layer.Sequence, []);
                    layer.PendingSeconds -= step;
                }
                if (layer.PendingSeconds > 0f && safety == 0)
                {
                    float remainder = Math.Min(layer.PendingSeconds, .25f);
                    gpu.EnqueueParticleStep(emitter, remainder, 0, ++layer.Sequence, []);
                    layer.PendingSeconds -= remainder;
                }

                // Particles are born where their burst was asked for; once born they move in the
                // world, so the emitter's transform matters only for the step that births them.
                foreach (Matrix4x4 world in effect.Pending)
                {
                    emitter.UpdateDefinition(GpuParticleDefinitionBuilder.Move(layer.GpuDefinition!, layer.Config, world));
                    gpu.EnqueueParticleStep(emitter, 0f, layer.BurstBirths, ++layer.Sequence, []);
                }

                if (layer.Frames.Length > 0 && layer.Frames[0].IsValid)
                    gpu.SubmitParticles3D(emitter, layer.Frames[0], layer.Texture);
                layer.LastDiagnostics = emitter.Diagnostics;
            }

            if (births)
            {
                effect.AliveUntil = MathF.Max(effect.AliveUntil, _totalTime + effect.LongestLife + .25f);
                effect.Pending.Clear();
            }
        }
    }

    private GpuParticleEmitter EnsureBatchedEmitter(BatchedEffect effect, ParticleLayerState layer, IGpuParticleRenderer gpu)
    {
        if (layer.GpuEmitter is { IsDisposed: false } existing && ReferenceEquals(layer.GpuOwner, gpu)) return existing;
        layer.GpuEmitter?.Dispose();
        GpuParticleDefinition single = GpuParticleDefinitionBuilder.Build(layer.Config, Matrix4x4.Identity, layer.MeshSurfaceSamples);
        int capacity = Math.Max(single.Capacity, Math.Min(MaxBatchedCapacity, layer.BurstBirths * BatchedBurstsPerEmitter));
        layer.GpuDefinition = GpuParticleDefinitionBuilder.Unbounded(single, capacity);
        layer.GpuLookup = single.Lookup;
        layer.GpuEmitter = gpu.CreateParticleEmitter(layer.GpuDefinition,
            StableEmitterSeed(StringComparer.OrdinalIgnoreCase.GetHashCode(effect.Asset), layer.EmitterId));
        layer.GpuEmitter.DiagnosticsIntervalSeconds = BatchedDiagnosticsSeconds;
        layer.GpuOwner = gpu;
        layer.Sequence = 0;
        return layer.GpuEmitter;
    }

    private static void ReleaseBatchedEffect(BatchedEffect effect)
    {
        // Its quads and textures are shared or cached by the renderer; only the emitters are its own.
        foreach (ParticleLayerState layer in effect.Layers)
        {
            layer.GpuEmitter?.Dispose();
            layer.GpuEmitter = null;
            layer.GpuOwner = null;
        }
    }

    /// <summary>Frees what the subsystem shares between emitters (it is being disposed).</summary>
    private void ReleaseParticlePools()
    {
        foreach (BatchedEffect effect in _batched.Values) ReleaseBatchedEffect(effect);
        _batched.Clear();
        DisposeIdleEmitters();
        _idleEmitters.Clear();
        _idleEmitterOwner = null;
        ReleaseSharedGeometry();
        _assetStamps.Clear();
    }
}
