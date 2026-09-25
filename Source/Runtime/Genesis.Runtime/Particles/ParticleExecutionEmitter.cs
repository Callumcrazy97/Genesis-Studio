using System;
using System.Numerics;
using Genesis.Rendering.Particles;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Particles;

/// <summary>
/// One runtime/editor emitter that owns exactly one simulation implementation at a time.
/// Hardware renderers own GPU state; the scalar <see cref="ParticleSimulation"/> is created only
/// when the user explicitly selected the Software renderer.
/// </summary>
public sealed class ParticleExecutionEmitter : IDisposable
{
    private ParticleConfig _config;
    private IRenderController _renderer;
    private IGpuParticleRenderer _gpuRenderer;
    private GpuParticleEmitter _gpu;
    private GpuParticleDefinition _gpuDefinition;
    private ParticleSimulation _cpu;
    private Vector3[] _meshSurfaceSamples = Array.Empty<Vector3>();
    private Func<Vector3, float> _collisionHeightProvider;
    private Vector3 _origin;
    private double _emitAccumulator;
    private int _pendingBirths;
    private uint _sequence;
    private int _seed;
    private bool _definitionDirty = true;
    private bool _worldDirty = true;

    public ParticleExecutionEmitter(ParticleConfig config, int seed = 1337)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _seed = seed;
        ArmInitialBurst();
    }

    public ParticleExecutionDecision Execution => ParticleExecutionPolicy.Resolve(_renderer);
    public bool UsesGpu => _gpu != null;
    public bool UsesCpu => _cpu != null;
    public int Capacity => Math.Clamp(_config.MaxParticles, 1, GpuParticleProtocol.MaximumCapacity);
    public int ActiveCount => _gpu?.Diagnostics.Alive ?? _cpu?.ActiveCount ?? 0;
    public ParticleSimulation CpuSimulation => _cpu;
    public GpuParticleEmitter GpuEmitter => _gpu;
    public ParticleDiagnostics Diagnostics => _gpu?.Diagnostics
        ?? new ParticleDiagnostics(
            _cpu != null ? "Software CPU" : "Pending",
            Capacity,
            _cpu?.ActiveCount ?? 0,
            0, 0, 0, 0, 0, null,
            _cpu == null,
            0,
            string.Empty);

    public void BindRenderer(IRenderController renderer)
    {
        if (renderer == null) throw new ArgumentNullException(nameof(renderer));
        if (ReferenceEquals(_renderer, renderer)) return;

        ReleaseExecution();
        _renderer = renderer;
        ParticleExecutionDecision decision = ParticleExecutionPolicy.Resolve(renderer);
        if (decision.Target == ParticleExecutionTarget.CpuSoftware)
        {
            _cpu = new ParticleSimulation();
            _cpu.LoadConfig(_config);
            _cpu.SetEmitterOrigin(_origin);
            if (_meshSurfaceSamples.Length > 0)
                _cpu.SetMeshSurfaceSamples(_meshSurfaceSamples);
            if (_collisionHeightProvider != null)
                _cpu.SetCollisionHeightProvider(_collisionHeightProvider);
            _definitionDirty = true;
            _worldDirty = true;
            return;
        }

        if (decision.Target != ParticleExecutionTarget.Gpu || renderer is not IGpuParticleRenderer gpuRenderer)
        {
            throw new NotSupportedException(
                $"Particle execution is unavailable on '{renderer.BackendName}'. "
                + "Hardware renderers never fall back to CPU particles; select Software explicitly for CPU simulation.");
        }

        _gpuRenderer = gpuRenderer;
        _gpuDefinition = ParticleGpuDefinitionBuilder.Build(
            _config, Matrix4x4.CreateTranslation(_origin), _meshSurfaceSamples);
        _gpu = _gpuRenderer.CreateParticleEmitter(_gpuDefinition, _seed);
        _definitionDirty = false;
        _worldDirty = false;
    }

    public void UpdateConfig(ParticleConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (_cpu != null) _cpu.UpdateConfig(_config);
        _definitionDirty = true;
        if (_gpu != null && Capacity != _gpu.Capacity)
        {
            _gpu.Dispose();
            _gpu = null;
        }
    }

    public void SetEmitterOrigin(Vector3 origin)
    {
        if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z))
            return;
        if (_origin == origin) return;
        _origin = origin;
        _cpu?.SetEmitterOrigin(origin);
        _worldDirty = true;
    }

    public void UpdateCameraPosition(Vector3 cameraPosition)
    {
        if (_config.FollowCameraXZ)
            SetEmitterOrigin(new Vector3(cameraPosition.X, _origin.Y, cameraPosition.Z));
        _cpu?.UpdateCameraPosition(cameraPosition);
    }

    public void SetMeshSurfaceSamples(ReadOnlySpan<Vector3> positions)
    {
        _meshSurfaceSamples = positions.ToArray();
        _cpu?.SetMeshSurfaceSamples(_meshSurfaceSamples);
        _definitionDirty = true;
    }

    public void SetCollisionHeightProvider(Func<Vector3, float> provider)
    {
        _collisionHeightProvider = provider;
        _cpu?.SetCollisionHeightProvider(provider);
    }

    public void Burst(int count = 0)
    {
        int requested = count > 0 ? count : (_config.BurstCount > 0 ? _config.BurstCount : 200);
        if (_cpu != null)
        {
            _cpu.Burst(requested);
            return;
        }
        _pendingBirths = Math.Clamp(_pendingBirths + Math.Max(0, requested), 0, GpuParticleProtocol.MaximumCapacity);
    }

    public void Reset(int seed)
    {
        _seed = seed;
        _emitAccumulator = 0;
        _pendingBirths = 0;
        _sequence = 0;
        ArmInitialBurst();
        if (_cpu != null) _cpu.Reset(seed);
        if (_gpu != null) _gpuRenderer.EnqueueParticleReset(_gpu, seed);
    }

    public void Step(float deltaSeconds, ParticleEventConnection[] parents = null)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds < 0f) return;
        float dt = Math.Clamp(deltaSeconds, 0f, 0.25f);

        if (_cpu != null)
        {
            _cpu.Step(dt);
            return;
        }

        if (_renderer == null) return;
        EnsureGpu();
        ApplyGpuDefinition();

        int births = ConsumeBirths(dt);
        _gpuRenderer.EnqueueParticleStep(_gpu, dt, births, ++_sequence, parents ?? Array.Empty<ParticleEventConnection>());
    }

    public void Submit3D(MeshHandle mesh, TextureHandle texture)
    {
        if (_renderer == null) return;
        if (_cpu != null)
            throw new InvalidOperationException("CPU particle drawing requires the scalar DrawInstances3D path.");
        EnsureGpu();
        ApplyGpuDefinition();
        _gpuRenderer.SubmitParticles3D(_gpu, mesh, texture);
    }

    public void Submit2D(
        MeshHandle mesh, TextureHandle texture,
        float offsetX, float offsetY, float scale, float sizeScale,
        int depth = -100, Vector4 clip = default)
    {
        if (_renderer == null) return;
        if (_cpu != null)
            throw new InvalidOperationException("CPU particle drawing requires the scalar sprite-batch path.");
        EnsureGpu();
        ApplyGpuDefinition();
        _gpuRenderer.SubmitParticles2D(
            _gpu, mesh, texture, offsetX, offsetY, scale, sizeScale, depth, clip);
    }

    public void Dispose()
    {
        ReleaseExecution();
        _renderer = null;
    }

    private void EnsureGpu()
    {
        if (_gpu != null) return;
        if (_renderer is not IGpuParticleRenderer gpuRenderer)
            throw new InvalidOperationException("The selected hardware renderer does not expose GPU particle execution.");
        _gpuRenderer = gpuRenderer;
        _gpuDefinition = ParticleGpuDefinitionBuilder.Build(
            _config, Matrix4x4.CreateTranslation(_origin), _meshSurfaceSamples);
        _gpu = gpuRenderer.CreateParticleEmitter(_gpuDefinition, _seed);
        _definitionDirty = false;
        _worldDirty = false;
    }

    private void ApplyGpuDefinition()
    {
        if (_gpu == null) return;
        if (_definitionDirty)
        {
            GpuParticleDefinition next = ParticleGpuDefinitionBuilder.Build(
                _config, Matrix4x4.CreateTranslation(_origin), _meshSurfaceSamples);
            if (next.Capacity != _gpu.Capacity)
            {
                _gpu.Dispose();
                _gpu = _gpuRenderer.CreateParticleEmitter(next, _seed);
                _gpuRenderer.EnqueueParticleReset(_gpu, _seed);
            }
            else
            {
                _gpu.UpdateDefinition(next);
            }
            _gpuDefinition = next;
            _definitionDirty = false;
            _worldDirty = false;
        }
        else if (_worldDirty)
        {
            _gpuDefinition = ParticleGpuDefinitionBuilder.WithWorld(
                _gpuDefinition, Matrix4x4.CreateTranslation(_origin));
            _gpu.UpdateDefinition(_gpuDefinition);
            _worldDirty = false;
        }
    }

    private int ConsumeBirths(float dt)
    {
        long births = _pendingBirths;
        _pendingBirths = 0;
        if (_config.Loop && _config.EmitRate > 0 && dt > 0f)
        {
            _emitAccumulator += _config.EmitRate * dt;
            int regular = (int)Math.Floor(_emitAccumulator);
            _emitAccumulator -= regular;
            births += regular;
        }
        return (int)Math.Clamp(births, 0, GpuParticleProtocol.MaximumCapacity);
    }

    private void ArmInitialBurst()
    {
        if (!_config.Loop && _config.BurstCount > 0)
            _pendingBirths = Math.Clamp(_config.BurstCount, 0, GpuParticleProtocol.MaximumCapacity);
    }

    private void ReleaseExecution()
    {
        _gpu?.Dispose();
        _gpu = null;
        _gpuRenderer = null;
        _cpu = null;
    }
}
