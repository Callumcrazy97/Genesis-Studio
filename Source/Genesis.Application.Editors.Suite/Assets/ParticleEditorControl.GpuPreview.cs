using System.Numerics;
using Genesis.Rendering.Particles;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Particles;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ParticleEditorControl
{
    private readonly List<GpuParticleEmitter> _gpuPreviewEmitters = [];
    private readonly List<float> _gpuPreviewEmitAccumulators = [];
    private readonly List<int> _gpuPreviewPendingBursts = [];
    private readonly List<uint> _gpuPreviewSequences = [];
    private readonly List<Vector3[]> _gpuPreviewSurfaceSamples = [];
    private IGpuParticleRenderer? _gpuPreviewOwner;

    private int PreviewLiveParticleCount
    {
        get
        {
            ParticleExecutionDecision execution = ParticleExecutionPolicy.Resolve(_particlePreviewRenderer);
            if (execution.Target == ParticleExecutionTarget.Gpu)
                return _gpuPreviewEmitters.Where(emitter => !emitter.IsDisposed).Sum(emitter => emitter.Diagnostics.Alive);
            if (execution.Target == ParticleExecutionTarget.CpuSoftware)
                return _previewSimulations.Sum(simulation => simulation.ActiveCount);
            return 0;
        }
    }

    private int PreviewParticleCapacity =>
        _previewEmitterConfigs.Sum(config => Math.Clamp(config.MaxParticles, 1, GpuParticleProtocol.MaximumCapacity));

    private string PreviewGpuDiagnostics
    {
        get
        {
            if (ParticleExecutionPolicy.Resolve(_particlePreviewRenderer).Target != ParticleExecutionTarget.Gpu
                || _gpuPreviewEmitters.Count == 0)
                return string.Empty;

            long bytes = 0;
            long dropped = 0;
            double milliseconds = 0;
            bool hasTime = false;
            bool pending = false;
            foreach (GpuParticleEmitter emitter in _gpuPreviewEmitters)
            {
                if (emitter.IsDisposed) continue;
                ParticleDiagnostics d = emitter.Diagnostics;
                bytes += d.AllocatedBytes;
                dropped += d.Dropped;
                pending |= d.CountsPending;
                if (d.SimulationMilliseconds is double ms)
                {
                    milliseconds += ms;
                    hasTime = true;
                }
            }

            string result = $"GPU {(bytes / 1048576d):0.0} MiB";
            if (hasTime) result += $" · sim {milliseconds:0.00} ms";
            if (pending) result += " · counters pending";
            if (dropped > 0) result += $" · dropped {dropped:N0}";
            return result;
        }
    }

    private void StepPreviewExecution(float step)
    {
        ParticleExecutionDecision execution = ParticleExecutionPolicy.Resolve(_particlePreviewRenderer);
        if (execution.Target == ParticleExecutionTarget.Pending)
            return;

        if (execution.Target == ParticleExecutionTarget.UnsupportedHardware)
        {
            LoadWarning = $"{execution.BackendName}: GPU particle execution unavailable; CPU fallback is disabled.";
            return;
        }

        if (execution.Target == ParticleExecutionTarget.CpuSoftware)
        {
            foreach (ParticleSimulation simulation in _previewSimulations)
                simulation.Step(step);
            return;
        }

        if (_particlePreviewRenderer is not IGpuParticleRenderer gpuRenderer)
        {
            LoadWarning = "The selected hardware renderer reports GPU particle support but exposes no GPU particle renderer.";
            return;
        }

        EnsureGpuPreviewEmitters(gpuRenderer);
        foreach (int index in PreviewEventOrder())
        {
            GpuParticleEmitter emitter = _gpuPreviewEmitters[index];
            ParticleConfig config = _previewEmitterConfigs[index];
            GpuParticleDefinition definition = BuildGpuPreviewDefinition(index);
            emitter.UpdateDefinition(definition);

            int births = TakeGpuPreviewBirths(index, step);
            gpuRenderer.EnqueueParticleStep(
                emitter,
                step,
                births,
                ++_gpuPreviewSequences[index],
                BuildPreviewEventConnections(index));
        }
    }

    private void ResetPreviewExecution()
    {
        ParticleExecutionDecision execution = ParticleExecutionPolicy.Resolve(_particlePreviewRenderer);
        EnsureGpuPreviewStateLists();

        for (int i = 0; i < _gpuPreviewEmitAccumulators.Count; i++)
        {
            _gpuPreviewEmitAccumulators[i] = 0f;
            _gpuPreviewSequences[i] = 0;
            _gpuPreviewPendingBursts[i] = !_previewEmitterConfigs[i].Loop
                ? Math.Max(0, _previewEmitterConfigs[i].BurstCount)
                : 0;
        }

        if (execution.Target == ParticleExecutionTarget.Gpu
            && _particlePreviewRenderer is IGpuParticleRenderer gpuRenderer)
        {
            EnsureGpuPreviewEmitters(gpuRenderer);
            for (int i = 0; i < _gpuPreviewEmitters.Count; i++)
                gpuRenderer.EnqueueParticleReset(
                    _gpuPreviewEmitters[i],
                    ParticlePreviewClock.SeedForEmitter(_previewSeed, _previewEmitterIds[i]));
            return;
        }

        if (execution.Target == ParticleExecutionTarget.CpuSoftware)
        {
            for (int i = 0; i < _previewSimulations.Count; i++)
                _previewSimulations[i].Reset(
                    ParticlePreviewClock.SeedForEmitter(_previewSeed, _previewEmitterIds[i]));
        }
    }

    private void BurstPreview(int? explicitCount = null)
    {
        ParticleExecutionDecision execution = ParticleExecutionPolicy.Resolve(_particlePreviewRenderer);
        if (execution.Target == ParticleExecutionTarget.CpuSoftware)
        {
            foreach (ParticleSimulation simulation in _previewSimulations)
            {
                if (explicitCount is int count) simulation.Burst(count);
                else simulation.Burst();
            }
            return;
        }

        EnsureGpuPreviewStateLists();
        for (int i = 0; i < _gpuPreviewPendingBursts.Count; i++)
        {
            int count = explicitCount ?? (_previewEmitterConfigs[i].BurstCount > 0
                ? _previewEmitterConfigs[i].BurstCount
                : 200);
            _gpuPreviewPendingBursts[i] = Math.Min(
                GpuParticleProtocol.MaximumCapacity,
                _gpuPreviewPendingBursts[i] + Math.Max(0, count));
        }
    }

    private void DrawPreviewParticles3D(IRenderController renderer)
    {
        ParticleExecutionDecision execution = ParticleExecutionPolicy.Resolve(renderer);
        if (execution.Target == ParticleExecutionTarget.Gpu)
        {
            if (renderer is not IGpuParticleRenderer gpuRenderer)
            {
                LoadWarning = "Hardware particle renderer interface is missing.";
                return;
            }

            EnsureGpuPreviewEmitters(gpuRenderer);
            for (int i = 0; i < _gpuPreviewEmitters.Count; i++)
            {
                if (i >= _previewFrames.Count || _previewFrames[i].Length == 0
                    || !_previewFrames[i][0].IsValid)
                    continue;
                TextureHandle texture = i < _previewTextures.Count
                    ? _previewTextures[i]
                    : TextureHandle.Invalid;
                gpuRenderer.SubmitParticles3D(
                    _gpuPreviewEmitters[i],
                    _previewFrames[i][0],
                    texture);
            }
            return;
        }

        if (execution.Target == ParticleExecutionTarget.UnsupportedHardware)
        {
            LoadWarning = execution.StatusText;
            return;
        }

        for (int i = 0; i < _previewSimulations.Count; i++)
        {
            if (i >= _previewFrames.Count || _previewFrames[i].Length == 0) continue;
            TextureHandle texture = i < _previewTextures.Count
                ? _previewTextures[i]
                : TextureHandle.Invalid;
            _previewSimulations[i].DrawInstances3D(
                renderer,
                _previewFrames[i],
                texture,
                _viewport.Camera.Eye,
                _viewport.Camera.Forward);
        }
    }

    private void DrawPreviewParticles2D(IRenderController renderer)
    {
        ParticleExecutionDecision execution = ParticleExecutionPolicy.Resolve(renderer);
        if (execution.Target == ParticleExecutionTarget.Gpu)
        {
            if (renderer is not IGpuParticleRenderer gpuRenderer)
            {
                LoadWarning = "Hardware particle renderer interface is missing.";
                return;
            }

            EnsureGpuPreviewEmitters(gpuRenderer);
            for (int i = 0; i < _gpuPreviewEmitters.Count; i++)
            {
                if (i >= _previewFrames.Count || _previewFrames[i].Length == 0
                    || !_previewFrames[i][0].IsValid)
                    continue;
                TextureHandle texture = i < _previewTextures.Count
                    ? _previewTextures[i]
                    : TextureHandle.Invalid;
                gpuRenderer.SubmitParticles2D(
                    _gpuPreviewEmitters[i],
                    _previewFrames[i][0],
                    texture,
                    0f,
                    0f,
                    12f,
                    MathF.Max(4f, 12f * 12f),
                    0,
                    default);
            }
            return;
        }

        if (execution.Target == ParticleExecutionTarget.UnsupportedHardware)
        {
            LoadWarning = execution.StatusText;
            return;
        }

        int capacity = Math.Max(1, _previewSimulations
            .Select(simulation => simulation.Capacity)
            .DefaultIfEmpty(1)
            .Max());
        if (_spriteCalls.Length < capacity) _spriteCalls = new SpriteDrawCall[capacity];
        for (int i = 0; i < _previewSimulations.Count; i++)
        {
            TextureHandle texture = i < _previewTextures.Count
                ? _previewTextures[i]
                : TextureHandle.Invalid;
            int count = _previewSimulations[i].FillSpriteDrawCalls2D(
                _spriteCalls, 0f, 0f, 12f, texture);
            if (count > 0) renderer.DrawSpriteBatch(_spriteCalls.AsSpan(0, count));
        }
    }

    private void EnsureGpuPreviewEmitters(IGpuParticleRenderer renderer)
    {
        EnsureGpuPreviewStateLists();

        if (!ReferenceEquals(_gpuPreviewOwner, renderer))
        {
            DisposeGpuPreviewEmitters();
            _gpuPreviewOwner = renderer;
            EnsureGpuPreviewStateLists();
        }

        while (_gpuPreviewSurfaceSamples.Count < _previewEmitterConfigs.Count)
            _gpuPreviewSurfaceSamples.Add(LoadGpuPreviewSurfaceSamples(
                _previewEmitterConfigs[_gpuPreviewSurfaceSamples.Count]));

        for (int i = 0; i < _previewEmitterConfigs.Count; i++)
        {
            GpuParticleDefinition definition = BuildGpuPreviewDefinition(i);
            bool replace = i >= _gpuPreviewEmitters.Count
                || _gpuPreviewEmitters[i].IsDisposed
                || _gpuPreviewEmitters[i].Capacity != definition.Capacity;

            if (replace)
            {
                if (i < _gpuPreviewEmitters.Count)
                {
                    _gpuPreviewEmitters[i].Dispose();
                    _gpuPreviewEmitters.RemoveAt(i);
                }

                GpuParticleEmitter emitter = renderer.CreateParticleEmitter(
                    definition,
                    ParticlePreviewClock.SeedForEmitter(_previewSeed, _previewEmitterIds[i]));
                _gpuPreviewEmitters.Insert(i, emitter);
            }
            else
            {
                _gpuPreviewEmitters[i].UpdateDefinition(definition);
            }
        }

        while (_gpuPreviewEmitters.Count > _previewEmitterConfigs.Count)
        {
            int last = _gpuPreviewEmitters.Count - 1;
            _gpuPreviewEmitters[last].Dispose();
            _gpuPreviewEmitters.RemoveAt(last);
        }
    }

    private GpuParticleDefinition BuildGpuPreviewDefinition(int index)
    {
        ParticleConfig config = _previewEmitterConfigs[index];
        Matrix4x4 world = Matrix4x4.CreateTranslation(_previewOrigin);
        Vector3[] surface = index < _gpuPreviewSurfaceSamples.Count
            ? _gpuPreviewSurfaceSamples[index]
            : [];
        return GpuParticleDefinitionBuilder.Build(config, world, surface);
    }

    private Vector3[] LoadGpuPreviewSurfaceSamples(ParticleConfig config)
    {
        if (config.Shape != ParticleEmitShape.MeshSurface
            || string.IsNullOrWhiteSpace(config.MeshSurfaceAsset))
            return [];

        try
        {
            GModelAsset asset = _particleMeshAssets.Load(ProjectRoot, config.MeshSurfaceAsset);
            List<Vector3> positions = [];
            foreach (GModelMesh mesh in asset.Meshes)
            {
                if (mesh.Vertices is { Length: > 0 })
                    positions.AddRange(mesh.Vertices.Select(vertex => vertex.Position));
                else if (mesh.SkinnedVertices is { Length: > 0 })
                    positions.AddRange(mesh.SkinnedVertices.Select(vertex => vertex.Position));
                if (positions.Count >= 100_000) break;
            }
            if (positions.Count > 100_000)
                positions.RemoveRange(100_000, positions.Count - 100_000);
            return positions.ToArray();
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            LoadWarning = "GPU mesh-surface emitter could not be sampled: " + error.Message;
            return [];
        }
    }

    private int TakeGpuPreviewBirths(int index, float delta)
    {
        int births = _gpuPreviewPendingBursts[index];
        _gpuPreviewPendingBursts[index] = 0;
        ParticleConfig config = _previewEmitterConfigs[index];
        if (!config.Loop || config.EmitRate <= 0) return births;

        _gpuPreviewEmitAccumulators[index] += (float)config.EmitRate * delta;
        int continuous = Math.Min(
            (int)_gpuPreviewEmitAccumulators[index],
            GpuParticleProtocol.MaximumCapacity);
        _gpuPreviewEmitAccumulators[index] -= continuous;
        return Math.Min(GpuParticleProtocol.MaximumCapacity, births + continuous);
    }

    private ParticleEventConnection[] BuildPreviewEventConnections(int targetIndex)
    {
        if (_effect.EventLinks is not { Count: > 0 }) return [];
        string targetId = _previewEmitterIds[targetIndex];
        List<ParticleEventConnection> result = [];
        foreach (ParticleEventLink link in _effect.EventLinks)
        {
            if (link.Trigger == ParticleEventTrigger.None
                || !string.Equals(link.TargetEmitterId, targetId, StringComparison.OrdinalIgnoreCase))
                continue;

            int sourceIndex = _previewEmitterIds.FindIndex(id =>
                string.Equals(id, link.SourceEmitterId, StringComparison.OrdinalIgnoreCase));
            if (sourceIndex < 0 || sourceIndex >= _gpuPreviewEmitters.Count) continue;

            result.Add(new ParticleEventConnection(
                _gpuPreviewEmitters[sourceIndex],
                (int)link.Trigger,
                (float)Math.Clamp(link.Probability, 0d, 1d),
                Math.Clamp(link.Count, 1, 32),
                (float)Math.Clamp(link.InheritVelocity, 0d, 4d)));
        }
        return result.ToArray();
    }

    private IEnumerable<int> PreviewEventOrder()
    {
        if (_effect.EventLinks is not { Count: > 0 })
            return Enumerable.Range(0, _previewEmitterConfigs.Count);

        Dictionary<string, int> byId = _previewEmitterIds
            .Select((id, index) => (id, index))
            .GroupBy(pair => pair.id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase);
        List<int> result = [];
        HashSet<int> complete = [];
        HashSet<int> visiting = [];

        void Visit(int index)
        {
            if (!complete.Add(index)) return;
            if (!visiting.Add(index)) return;
            string id = _previewEmitterIds[index];
            foreach (ParticleEventLink link in _effect.EventLinks)
            {
                if (!string.Equals(link.TargetEmitterId, id, StringComparison.OrdinalIgnoreCase)) continue;
                if (byId.TryGetValue(link.SourceEmitterId, out int parent)) Visit(parent);
            }
            visiting.Remove(index);
            if (!result.Contains(index)) result.Add(index);
        }

        for (int i = 0; i < _previewEmitterConfigs.Count; i++) Visit(i);
        return result;
    }

    private void EnsureGpuPreviewStateLists()
    {
        int count = _previewEmitterConfigs.Count;
        while (_gpuPreviewEmitAccumulators.Count < count) _gpuPreviewEmitAccumulators.Add(0f);
        while (_gpuPreviewPendingBursts.Count < count)
        {
            int index = _gpuPreviewPendingBursts.Count;
            ParticleConfig config = _previewEmitterConfigs[index];
            _gpuPreviewPendingBursts.Add(!config.Loop ? Math.Max(0, config.BurstCount) : 0);
        }
        while (_gpuPreviewSequences.Count < count) _gpuPreviewSequences.Add(0);
        if (_gpuPreviewEmitAccumulators.Count > count)
            _gpuPreviewEmitAccumulators.RemoveRange(count, _gpuPreviewEmitAccumulators.Count - count);
        if (_gpuPreviewPendingBursts.Count > count)
            _gpuPreviewPendingBursts.RemoveRange(count, _gpuPreviewPendingBursts.Count - count);
        if (_gpuPreviewSequences.Count > count)
            _gpuPreviewSequences.RemoveRange(count, _gpuPreviewSequences.Count - count);
    }

    private void DisposeGpuPreviewEmitters()
    {
        foreach (GpuParticleEmitter emitter in _gpuPreviewEmitters)
            emitter.Dispose();
        _gpuPreviewEmitters.Clear();
        _gpuPreviewSurfaceSamples.Clear();
        _gpuPreviewOwner = null;
    }
}
