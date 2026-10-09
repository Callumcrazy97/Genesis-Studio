using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;

namespace Genesis.Rendering.Particles;

/// <summary>
/// A GPU-resident emitter. CPU work is per emitter: uniforms, dispatches and delayed statistics.
/// Alive indices, births, motion, collisions, trails, events and indirect counts never leave the GPU.
/// </summary>
public sealed class GpuParticleEmitter : IDisposable
{
    private readonly GpuParticleLibrary _library;
    private readonly IGpuComputeDevice _gpu;
    private readonly GpuBufferHandle _state, _pool, _events, _arguments, _stepConstants, _parametersConstants, _drawConstants;
    private GpuBufferHandle _lookup;
    private Vector4[] _uploadedLookup;
    private long _lookupBytes;
    private GpuParticleDefinition _definition;
    private uint _seed;
    private float _time;
    private bool _resetPending = true;
    private GpuBufferReadbackHandle _readback;
    private readonly uint[] _counters = new uint[GpuParticleProtocol.HeaderWords];
    private readonly Queue<GpuQueryHandle> _timestamps = new();
    private double? _lastGpuMilliseconds;
    private bool _hasCounts;
    private float _sinceReadback;
    private long _completedSteps;
    private readonly long _allocation;
    private readonly string _execution;
    // The index count the indirect draw arguments were last written with (by a step or by
    // PrepareDraw); -1 when unknown, as after a reset, which clears them.
    private int _argumentsIndexCount = -1;
    private float _diagnosticsInterval = .1f;

    public bool IsDisposed { get; private set; }
    public int Capacity => _definition.Capacity;
    public GpuParticleDefinition Definition => _definition;
    public long AllocatedBytes => _allocation + _lookupBytes;
    public string Backend => _gpu.BackendName;
    public double SimulationTime => _time;

    /// <summary>
    /// Seconds of simulation between reads of the live counts back from the GPU (0.1 by default,
    /// for the Particle Editor's figures). Each read is a copy the CPU waits a frame or two for,
    /// so a game with hundreds of emitters reads them less often.
    /// </summary>
    public float DiagnosticsIntervalSeconds
    {
        get => _diagnosticsInterval;
        set => _diagnosticsInterval = float.IsFinite(value) ? Math.Clamp(value, 0f, 60f) : .1f;
    }

    public ParticleDiagnostics Diagnostics => new(
        _execution, Capacity, (int)Math.Min(_counters[0], (uint)Capacity),
        _counters[4], _counters[5], _counters[6], (long)_counters[7] + _counters[16],
        AllocatedBytes, _lastGpuMilliseconds, !_hasCounts || _readback.IsValid,
        _completedSteps, _counters[16] > 0 ? "Particle event budget exceeded; excess events were dropped." : string.Empty);

    internal GpuParticleEmitter(GpuParticleLibrary library, GpuParticleDefinition definition, int seed)
    {
        _library = library; _gpu = library.Device;
        _execution = _gpu.BackendName + " compute";
        ValidateDefinition(definition);
        _definition = definition; _seed = unchecked((uint)seed);
        _allocation = GpuParticleProtocol.AllocationBytes(definition.Capacity);
        library.Reserve(_allocation);
        try
        {
            _state = Storage(definition.Capacity * GpuParticleProtocol.StateStride, GpuParticleProtocol.StateStride, "States");
            _pool = Storage(GpuParticleProtocol.PoolBytes(definition.Capacity), 4, "Pool");
            _events = Storage(GpuParticleProtocol.EventCapacity * 2 * GpuParticleProtocol.EventStride, GpuParticleProtocol.EventStride, "Events");
            _arguments = ArgumentStorage(GpuParticleProtocol.ArgumentsBytes, "Indirect");
            _stepConstants = Constants<GpuParticleStep>("Step");
            _parametersConstants = Constants<GpuParticleParameters>("Parameters");
            _drawConstants = Constants<GpuParticleDraw>("Draw");
            EnsureLookup(definition.Lookup);
        }
        catch { Dispose(); throw; }
    }

    public void UpdateDefinition(GpuParticleDefinition definition)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        // The same definition again (a game's emitter whose definition is kept between frames):
        // it was checked when it was first given.
        if (ReferenceEquals(definition, _definition)) return;
        ValidateDefinition(definition);
        if (definition.Capacity != Capacity)
            throw new InvalidOperationException("Changing GPU particle capacity requires an explicit emitter restart.");
        _definition = definition;
    }

    internal void RequestReset(int seed)
    {
        if (IsDisposed) return;
        _seed = unchecked((uint)seed); _time = 0; _resetPending = true; _hasCounts = false;
        _argumentsIndexCount = -1;
        Array.Clear(_counters);
        if (_readback.IsValid) { _gpu.ReleaseBufferReadback(_readback); _readback = default; }
    }

    internal void Execute(float delta, int births, uint sequence, GpuParticleDefinition definition,
        ReadOnlySpan<ParticleEventConnection> parents)
    {
        if (IsDisposed) return;
        if (!float.IsFinite(delta) || delta < 0 || delta > .25f)
            throw new ArgumentOutOfRangeException(nameof(delta));
        EnsureLookup(definition.Lookup);
        var step = new GpuParticleStep
        {
            Timing = new Vector4(delta, Math.Clamp(births, 0, GpuParticleProtocol.MaximumCapacity), _time, 1),
            Capacity = (uint)Capacity, Groups = (uint)GpuParticleProtocol.Groups(Capacity), Seed = _seed, Sequence = sequence,
            Limits = new Vector4(GpuParticleProtocol.EventCapacity, IndexCount(definition.Parameters), 0, 0),
        };
        BindCompute(step, definition.Parameters);
        if (_resetPending)
        {
            Dispatch("Reset", GpuParticleProtocol.Groups(Capacity)); _resetPending = false;
        }
        PollDiagnostics();
        GpuQueryHandle query = default;
        if (_gpu.Capabilities.SupportsTimestampQueries && _timestamps.Count < 3)
            query = _gpu.BeginTimestampScope();
        Dispatch("BeginStep", 1);
        foreach (ParticleEventConnection link in parents)
        {
            GpuParticleEmitter parent = link.Parent;
            if (parent == null || parent.IsDisposed || parent._resetPending) continue;
            if (!ReferenceEquals(parent._library, _library) || ReferenceEquals(parent, this))
                throw new InvalidOperationException("Particle event links must connect distinct emitters on the same device.");
            step.Event = new Vector4(link.Mask & 7, Math.Clamp(link.Probability,0,1), Math.Clamp(link.Count,1,32), Math.Clamp(link.InheritVelocity,0,4));
            _gpu.UpdateConstantBuffer(_stepConstants, step);
            _gpu.SetStructuredBuffer(GpuShaderStage.Compute, 1, parent._events);
            _gpu.SetStructuredBuffer(GpuShaderStage.Compute, 2, parent._pool);
            Dispatch("CollectEvents", GpuParticleProtocol.EventCapacity / GpuParticleProtocol.Threads);
            _gpu.SetStructuredBuffer(GpuShaderStage.Compute, 1, GpuBufferHandle.Invalid);
            _gpu.SetStructuredBuffer(GpuShaderStage.Compute, 2, GpuBufferHandle.Invalid);
        }
        Dispatch("UpdateScan", GpuParticleProtocol.Groups(Capacity));
        Dispatch("PrefixGroups", 1);
        Dispatch("SpawnCompact", GpuParticleProtocol.Groups(Capacity));
        Dispatch("FinishStep", GpuParticleProtocol.Groups(Capacity));
        UnbindCompute();
        // FinishStep wrote the draw arguments with this index count and the step's live count.
        _argumentsIndexCount = (int)step.Limits.Y;
        if (query.IsValid) { _gpu.EndTimestampScope(query); _timestamps.Enqueue(query); }
        _time += delta; _completedSteps++;
        _sinceReadback += delta;
        if (!_readback.IsValid && (!_hasCounts || _sinceReadback >= _diagnosticsInterval))
        {
            _readback = _gpu.BeginBufferReadback(_pool, 0, GpuParticleProtocol.CounterBytes);
            _sinceReadback = 0;
        }
    }

    internal void PrepareDraw(GpuParticleMesh mesh)
    {
        if (IsDisposed) return;
        // The last step (this frame's, or an earlier one's when the effect is paused) already
        // wrote the arguments for this mesh: the live count only changes in a step.
        if (!_resetPending && _argumentsIndexCount == mesh.IndexCount)
        {
            PollDiagnostics();
            return;
        }
        // A paused effect must still display renderer/mesh changes without a simulation step.
        // Only the index-count word is updated; the instance count remains GPU-owned.
        var step = new GpuParticleStep
        {
            Capacity = (uint)Capacity, Groups = (uint)GpuParticleProtocol.Groups(Capacity), Seed = _seed,
            Limits = new Vector4(0, mesh.IndexCount, 0, 0),
        };
        EnsureLookup(_definition.Lookup);
        BindCompute(step, _definition.Parameters);
        if (_resetPending) { Dispatch("Reset", GpuParticleProtocol.Groups(Capacity)); _resetPending = false; }
        Dispatch("DrawArguments", 1);
        UnbindCompute();
        _argumentsIndexCount = mesh.IndexCount;
        PollDiagnostics();
    }

    // Premultiplied "over": the particle layer's alpha particles cover what is behind them and its
    // additive ones (alpha 0) add without changing coverage.
    private static readonly GpuBlendState PremultipliedOver = new()
    {
        Enabled = true,
        SrcColor = GpuBlendFactor.One, DstColor = GpuBlendFactor.InvSrcAlpha, ColorOp = GpuBlendOp.Add,
        SrcAlpha = GpuBlendFactor.One, DstAlpha = GpuBlendFactor.InvSrcAlpha, AlphaOp = GpuBlendOp.Add,
        WriteR = true, WriteG = true, WriteB = true, WriteA = true,
    };

    /// <param name="layer">
    /// Draw into the forward renderer's particle layer: no depth attachment (the shader tests the
    /// scene depth itself), premultiplied output, self-applied froxel fog.
    /// </param>
    internal void Draw(in GpuParticleDraw draw, GpuParticleMesh mesh, GpuTextureHandle texture, int blendMode,
        bool layer = false)
    {
        if (IsDisposed) return;
        _gpu.UpdateConstantBuffer(_parametersConstants, _definition.Parameters);
        GpuParticleDraw blendDraw = draw;
        blendDraw.Mode.Z = blendMode;
        _gpu.UpdateConstantBuffer(_drawConstants, blendDraw);
        _gpu.SetShaderProgram(_library.DrawProgram);
        _gpu.SetVertexLayout(_library.Layout);
        _gpu.SetVertexBuffer(0, mesh.Vertices, mesh.VertexStride);
        _gpu.SetIndexBuffer(mesh.Indices, mesh.IndexFormat);
        _gpu.SetPrimitiveTopology(GpuPrimitiveTopology.TriangleList);
        _gpu.SetRasterState(new GpuRasterState { CullMode = GpuCullMode.None, FillMode = GpuFillMode.Solid,
            DepthClipEnabled = draw.Mode.X < .5f, ScissorEnabled = draw.Mode.X > .5f });
        // Tested against the scene, never written: nearer-or-equal, whichever way round depth is stored.
        GpuDepthState tested = GpuDepthState.ReadOnly;
        if (draw.FogLayer.W > .5f) tested.Compare = GpuCompare.GreaterEqual;
        _gpu.SetDepthState(layer || draw.Mode.X > .5f ? GpuDepthState.Disabled : tested);
        _gpu.SetBlendState(layer ? PremultipliedOver
            : blendMode switch { 1 => GpuBlendState.Additive, 2 => GpuBlendState.Multiply, _ => GpuBlendState.AlphaBlend });
        _gpu.SetStructuredBuffer(GpuShaderStage.Vertex, 0, _state);
        _gpu.SetStructuredBuffer(GpuShaderStage.Vertex, 1, _pool);
        _gpu.SetStructuredBuffer(GpuShaderStage.Vertex, 2, _lookup);
        _gpu.SetTexture(GpuShaderStage.Pixel, 3, texture);
        _gpu.SetSampler(GpuShaderStage.Pixel, 0, _definition.PointSampling && _library.PointSampler.IsValid ? _library.PointSampler : _library.Sampler);
        _gpu.SetConstantBuffer(GpuShaderStage.Vertex, 1, _parametersConstants);
        _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 1, _parametersConstants);
        _gpu.SetConstantBuffer(GpuShaderStage.Vertex, 2, _drawConstants);
        _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 2, _drawConstants);
        _gpu.DrawIndexedIndirect(_arguments);
        for (int i = 0; i < 3; i++) _gpu.SetStructuredBuffer(GpuShaderStage.Vertex, i, GpuBufferHandle.Invalid);
        _gpu.ClearTexture(GpuShaderStage.Pixel, 3);
    }

    internal GpuParticleMesh Geometry(GpuParticleMesh authored)
    {
        int kind = (int)_definition.Parameters.Render.X;
        if (kind is 1 or 3) return _library.Strip;
        if ((int)_definition.Parameters.Render.Y == 3 && kind == 0)
        {
            if (!authored.Vertices.IsValid || authored.VertexStride != 48)
                throw new InvalidOperationException("Mesh particles require a rigid Model resource with a MeshVertex stream.");
            return authored;
        }
        return _library.Quad;
    }

    private static int IndexCount(in GpuParticleParameters parameters) => (int)parameters.Render.X is 1 or 3 ? 42 : 6;

    private void BindCompute(in GpuParticleStep step, in GpuParticleParameters parameters)
    {
        // Remove graphics aliases before making the same state writable (required by D3D11).
        for (int i=0;i<4;i++)
        {
            _gpu.SetStructuredBuffer(GpuShaderStage.Vertex,i,GpuBufferHandle.Invalid);
            _gpu.SetStructuredBuffer(GpuShaderStage.Pixel,i,GpuBufferHandle.Invalid);
        }
        _gpu.SetUnorderedAccessBuffer(0,_state); _gpu.SetUnorderedAccessBuffer(1,_pool);
        _gpu.SetUnorderedAccessBuffer(2,_events); _gpu.SetUnorderedAccessBuffer(3,_arguments);
        _gpu.UpdateConstantBuffer(_stepConstants,step);
        _gpu.UpdateConstantBuffer(_parametersConstants,parameters);
        _gpu.SetConstantBuffer(GpuShaderStage.Compute,0,_stepConstants);
        _gpu.SetConstantBuffer(GpuShaderStage.Compute,1,_parametersConstants);
        _gpu.SetStructuredBuffer(GpuShaderStage.Compute,0,_lookup);
    }

    private void Dispatch(string kernel,int groups)
    {
        _gpu.SetShaderProgram(_library.Kernel(kernel)); _gpu.Dispatch(groups); _gpu.ComputeMemoryBarrier();
    }
    private void UnbindCompute()
    {
        for(int i=0;i<4;i++)_gpu.SetUnorderedAccessBuffer(i,GpuBufferHandle.Invalid);
        for(int i=0;i<3;i++)_gpu.SetStructuredBuffer(GpuShaderStage.Compute,i,GpuBufferHandle.Invalid);
    }

    internal void PollCompletedDiagnostics() => PollDiagnostics();
    internal void RequestDiagnosticSample()
    {
        if (_readback.IsValid) _gpu.ReleaseBufferReadback(_readback);
        _readback = _gpu.BeginBufferReadback(_pool,0,GpuParticleProtocol.CounterBytes);
    }

    private void PollDiagnostics()
    {
        if(_readback.IsValid && _gpu.TryCompleteBufferReadback(_readback,MemoryMarshal.AsBytes(_counters.AsSpan())))
        {
            _gpu.ReleaseBufferReadback(_readback); _readback=default; _hasCounts=true;
        }
        while(_timestamps.Count>0 && _gpu.TryResolveTimestamp(_timestamps.Peek(),out double ms))
        {
            _lastGpuMilliseconds=ms; _timestamps.Dequeue();
        }
    }

    private GpuBufferHandle Storage(int bytes,int stride,string name,GpuBindFlags extra=0) => _gpu.CreateBuffer(new GpuBufferDesc
    {
        SizeBytes=bytes,StructureStride=stride,Usage=GpuBufferUsage.Gpu,
        BindFlags=GpuBindFlags.StructuredBuffer|GpuBindFlags.ShaderResource|GpuBindFlags.UnorderedAccess|extra,DebugName="Particles."+name,
    },ReadOnlySpan<byte>.Empty);
    private GpuBufferHandle ArgumentStorage(int bytes,string name) => _gpu.CreateBuffer(new GpuBufferDesc
    {
        SizeBytes=bytes,StructureStride=0,Usage=GpuBufferUsage.Gpu,
        BindFlags=GpuBindFlags.UnorderedAccess|GpuBindFlags.IndirectArguments,DebugName="Particles."+name,
    },ReadOnlySpan<byte>.Empty);
    private GpuBufferHandle Constants<T>(string name) where T:unmanaged => _gpu.CreateBuffer(new GpuBufferDesc
    {
        SizeBytes=Marshal.SizeOf<T>(),Usage=GpuBufferUsage.Dynamic,BindFlags=GpuBindFlags.ConstantBuffer,DebugName="Particles."+name,
    },ReadOnlySpan<byte>.Empty);

    private void EnsureLookup(Vector4[] lookup)
    {
        // Builders create a new array when only emitter uniforms (world/rate) change.
        // Reusing identical lookup data avoids allocating and retiring a GPU buffer each frame.
        // Compare against our own snapshot so in-place authored edits are detected as well.
        if(_lookup.IsValid && lookup.AsSpan().SequenceEqual(_uploadedLookup))return;
        long bytes=(long)lookup.Length*16;
        _library.Reserve(bytes);
        GpuBufferHandle next;
        try
        {
            next=_gpu.CreateBuffer(new GpuBufferDesc
            {
                SizeBytes=checked(lookup.Length*16),StructureStride=16,Usage=GpuBufferUsage.Immutable,
                BindFlags=GpuBindFlags.StructuredBuffer|GpuBindFlags.ShaderResource,DebugName="Particles.CurvesAndCollision",
            },MemoryMarshal.AsBytes(lookup.AsSpan()));
        }
        catch {_library.ReleaseBytes(bytes);throw;}
        if(_lookup.IsValid)_gpu.ReleaseBuffer(_lookup);
        _library.ReleaseBytes(_lookupBytes);
        _lookup=next;_lookupBytes=bytes;_uploadedLookup=(Vector4[])lookup.Clone();
    }

    public static void ValidateDefinition(GpuParticleDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        GpuParticleProtocol.ValidateCapacity(definition.Capacity);
        if(definition.Lookup==null || definition.Lookup.Length<GpuParticleProtocol.CurveSamples*2)
            throw new ArgumentException("Particle lookup requires complete curve and colour tables.",nameof(definition));
        GpuParticleParameters parameters=definition.Parameters;
        foreach(float value in MemoryMarshal.Cast<GpuParticleParameters,float>(MemoryMarshal.CreateReadOnlySpan(ref parameters,1)))
            if(!float.IsFinite(value))throw new ArgumentException("Particle parameters must be finite.",nameof(definition));
        foreach(Vector4 value in definition.Lookup)
            if(!float.IsFinite(value.X)||!float.IsFinite(value.Y)||!float.IsFinite(value.Z)||!float.IsFinite(value.W))
                throw new ArgumentException("Particle lookup values must be finite.",nameof(definition));
        if(MathF.Abs(parameters.World.GetDeterminant())<1e-8f)
            throw new ArgumentException("Particle emitter transform must be invertible.",nameof(definition));
    }

    public void Dispose()
    {
        if(IsDisposed)return;IsDisposed=true;
        if(_readback.IsValid)_gpu.ReleaseBufferReadback(_readback);
        foreach(GpuBufferHandle buffer in new[]{_state,_pool,_events,_arguments,_lookup,_stepConstants,_parametersConstants,_drawConstants})
            if(buffer.IsValid)_gpu.ReleaseBuffer(buffer);
        _library.ReleaseAllocation(this,_allocation+_lookupBytes);
    }
}
