using System;
using System.Numerics;
using Genesis.Rendering.Particles;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Particles;

/// <summary>
/// One authored emitter running on the renderer that was actually selected.
/// Hardware owns state on the GPU; the scalar simulator exists only for the explicit Software backend.
/// </summary>
public sealed class ParticleRuntimeEmitter : IDisposable
{
    private IRenderController _renderer;
    private IGpuParticleRenderer _gpuRenderer;
    private GpuParticleEmitter _gpu;
    private ParticleSimulation _cpu;
    private ParticleConfig _config;
    private GpuParticleDefinition _definition;
    private Vector3[] _meshSamples = [];
    private Vector3 _origin;
    private float _emitAccumulator;
    private int _pendingBurst;
    private uint _sequence;
    private int _seed;
    private bool _disposed;

    public ParticleExecutionDecision Execution { get; private set; }
    public bool UsesGpu => Execution.UsesGpu;
    public bool UsesCpu => Execution.UsesCpu;
    public int Capacity => UsesGpu ? _gpu?.Capacity ?? 0 : _cpu?.Capacity ?? 0;
    public int ActiveCount => UsesGpu ? _gpu?.Diagnostics.Alive ?? 0 : _cpu?.ActiveCount ?? 0;
    public ParticleDiagnostics? GpuDiagnostics => UsesGpu ? _gpu?.Diagnostics : null;
    internal GpuParticleEmitter GpuEmitter => _gpu;

    public ParticleRuntimeEmitter(
        IRenderController renderer,
        ParticleConfig config,
        int seed,
        ReadOnlySpan<Vector3> meshSurfaceSamples = default)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _config = config?.CloneEmitter() ?? throw new ArgumentNullException(nameof(config));
        _seed = seed;
        _meshSamples = meshSurfaceSamples.ToArray();
        Execution = ParticleExecutionPolicy.Resolve(renderer);
        if (!Execution.CanExecute)
            ParticleExecutionPolicy.ThrowIfHardwareWouldFallbackToCpu(renderer);

        if (Execution.UsesGpu)
        {
            _gpuRenderer = renderer as IGpuParticleRenderer
                ?? throw new InvalidOperationException(
                    $"Renderer '{renderer.BackendName}' advertises GPU particles but does not implement the GPU particle renderer.");
            _definition = ParticleGpuDefinitionBuilder.Build(_config, Matrix4x4.CreateTranslation(_origin), _meshSamples);
            _gpu = _gpuRenderer.CreateParticleEmitter(_definition, seed);
        }
        else
        {
            _cpu = new ParticleSimulation();
            _cpu.LoadConfig(_config);
            _cpu.SetMeshSurfaceSamples(_meshSamples);
            _cpu.SetEmitterOrigin(_origin);
            _cpu.Reset(seed);
        }

        QueueInitialBurst();
    }

    public void UpdateConfig(ParticleConfig config)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(config);
        ParticleConfig next = config.CloneEmitter();
        if (UsesGpu)
        {
            GpuParticleDefinition nextDefinition =
                ParticleGpuDefinitionBuilder.Build(next, Matrix4x4.CreateTranslation(_origin), _meshSamples);
            if (_gpu is null || nextDefinition.Capacity != _gpu.Capacity)
            {
                _gpu?.Dispose();
                _gpu = _gpuRenderer.CreateParticleEmitter(nextDefinition, _seed);
                _emitAccumulator = 0;
                _pendingBurst = 0;
                _sequence = 0;
                _definition = nextDefinition;
                _config = next;
                QueueInitialBurst();
                return;
            }
            _definition = nextDefinition;
            _gpu.UpdateDefinition(_definition);
        }
        else
        {
            _cpu.UpdateConfig(next);
        }
        _config = next;
    }

    public void SetMeshSurfaceSamples(ReadOnlySpan<Vector3> samples)
    {
        _meshSamples = samples.ToArray();
        if (UsesGpu)
        {
            _definition = ParticleGpuDefinitionBuilder.Build(
                _config, Matrix4x4.CreateTranslation(_origin), _meshSamples);
            if (_definition.Capacity == _gpu.Capacity) _gpu.UpdateDefinition(_definition);
        }
        else _cpu.SetMeshSurfaceSamples(_meshSamples);
    }

    public void SetEmitterOrigin(Vector3 worldPosition)
    {
        _origin = worldPosition;
        if (UsesGpu)
        {
            _definition = ParticleGpuDefinitionBuilder.WithWorld(
                _definition, Matrix4x4.CreateTranslation(_origin));
            _gpu.UpdateDefinition(_definition);
        }
        else _cpu.SetEmitterOrigin(worldPosition);
    }

    public void UpdateCameraPosition(Vector3 cameraPosition)
    {
        if (_config.FollowCameraXZ)
            SetEmitterOrigin(new Vector3(cameraPosition.X, _origin.Y, cameraPosition.Z));
        else if (UsesCpu)
            _cpu.UpdateCameraPosition(cameraPosition);
    }

    public void SetCollisionHeightProvider(Func<Vector3, float> provider)
    {
        if (UsesCpu) _cpu.SetCollisionHeightProvider(provider);
    }

    public void Reset(int seed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _seed = seed;
        _emitAccumulator = 0;
        _pendingBurst = 0;
        _sequence = 0;
        if (UsesGpu) _gpuRenderer.EnqueueParticleReset(_gpu, seed);
        else _cpu.Reset(seed);
        QueueInitialBurst();
    }

    public void Burst(int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int safe = Math.Clamp(count, 0, Math.Max(1, _config.MaxParticles));
        if (UsesGpu) _pendingBurst = Math.Min(_config.MaxParticles, _pendingBurst + safe);
        else _cpu.Burst(safe);
    }

    public void Step(float deltaSeconds, ParticleEventConnection[] parents = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0f) return;
        float dt = Math.Clamp(deltaSeconds, 0f, .25f);
        if (UsesCpu)
        {
            _cpu.Step(dt);
            return;
        }

        int births = _pendingBurst;
        _pendingBurst = 0;
        if (_config.Loop && _config.EmitRate > 0)
        {
            _emitAccumulator += (float)_config.EmitRate * dt;
            int continuous = (int)MathF.Floor(_emitAccumulator);
            if (continuous > 0)
            {
                _emitAccumulator -= continuous;
                births = Math.Min(_config.MaxParticles, births + continuous);
            }
        }
        _gpuRenderer.EnqueueParticleStep(_gpu, dt, births, ++_sequence, parents ?? []);
    }

    public void Draw3D(MeshHandle mesh, TextureHandle texture)
    {
        if (UsesGpu)
        {
            if (mesh.IsValid) _gpuRenderer.SubmitParticles3D(_gpu, mesh, texture);
        }
    }

    public void Draw2D(MeshHandle mesh, TextureHandle texture,
        float offsetX, float offsetY, float scale, float sizeScale, int depth, Vector4 clip)
    {
        if (UsesGpu)
        {
            if (mesh.IsValid)
                _gpuRenderer.SubmitParticles2D(_gpu, mesh, texture, offsetX, offsetY, scale, sizeScale, depth, clip);
        }
    }

    public ParticleSimulation CpuSimulation => _cpu;

    private void QueueInitialBurst()
    {
        if (!_config.Loop && _config.BurstCount > 0)
        {
            if (UsesGpu) _pendingBurst = Math.Min(_config.BurstCount, _config.MaxParticles);
            else _cpu.Burst(_config.BurstCount);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gpu?.Dispose();
        _gpu = null;
        _cpu = null;
    }
}
