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

    public bool IsDisposed { get; private set; }
    public int Capacity => _definition.Capacity;
    public GpuParticleDefinition Definition => _definition;
    public long AllocatedBytes => _allocation + _lookupBytes;
    public string Backend => _gpu.BackendName;
    public double SimulationTime => _time;
    public ParticleDiagnostics Diagnostics => new(
        Backend + " compute", Capacity, (int)Math.Min(_counters[0], (uint)Capacity),
        _counters[4], _counters[5], _counters[6], (long)_counters[7] + _counters[16],
        AllocatedBytes, _lastGpuMilliseconds, !_hasCounts || _readback.IsValid,
        _completedSteps, _counters[16] > 0 ? "Particle event budget exceeded; excess events were dropped." : string.Empty);

    internal GpuParticleEmitter(GpuParticleLibrary library, GpuParticleDefinition definition, int seed)
    {
        _library = library; _gpu = library.Device;
        ValidateDefinition(definition);
        _definition = definition; _seed = unchecked((uint)seed);
        _allocation = GpuParticleProtocol.AllocationBytes(definition.Capacity);
        library.Reserve(_allocation);
        try
        {
            _state = Storage(definition.Capacity * GpuParticleProtocol.StateStride, GpuParticleProtocol.StateStride, "States");
            _pool = Storage(GpuParticleProtocol.PoolBytes(definition.Capacity), 4, "Pool");
            _events = Storage(GpuParticleProtocol.EventCapacity * 2 * GpuParticleProtocol.EventStride, GpuParticleProtocol.EventStride, "Events");
            _arguments = Storage(GpuParticleProtocol.ArgumentsBytes, 4, "Indirect", GpuBindFlags.IndirectArguments);
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
        ValidateDefinition(definition);
        if (definition.Capacity != Capacity)
            throw new InvalidOperationException("Changing GPU particle capacity requires an explicit emitter restart.");
        _definition = definition;
    }

    internal void RequestReset(int seed)
    {
        if (IsDisposed) return;
        _seed = unchecked((uint)seed); _time = 0; _resetPending = true; _hasCounts = false;
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
        if (query.IsValid) { _gpu.EndTimestampScope(query); _timestamps.Enqueue(query); }
        _time += delta; _completedSteps++;
        _sinceReadback += delta;
        if (!_readback.IsValid && (!_hasCounts || _sinceReadback >= .1f))
        {
            _readback = _gpu.BeginBufferReadback(_pool, 0, GpuParticleProtocol.CounterBytes);
            _sinceReadback = 0;
        }
    }

    internal void PrepareDraw(GpuParticleMesh mesh)
    {
        if (IsDisposed) return;
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
        PollDiagnostics();
    }

    internal void Draw(in GpuParticleDraw draw, GpuParticleMesh mesh, GpuTextureHandle texture, int blendMode)
    {
        if (IsDisposed) return;
        _gpu.UpdateConstantBuffer(_parametersConstants, _definition.Parameters);
        _gpu.UpdateConstantBuffer(_drawConstants, draw);
        _gpu.SetShaderProgram(_library.DrawProgram);
        _gpu.SetVertexLayout(_library.Layout);
        _gpu.SetVertexBuffer(0, mesh.Vertices, mesh.VertexStride);
        _gpu.SetIndexBuffer(mesh.Indices, mesh.IndexFormat);
        _gpu.SetPrimitiveTopology(GpuPrimitiveTopology.TriangleList);
        _gpu.SetRasterState(new GpuRasterState { CullMode = GpuCullMode.None, FillMode = GpuFillMode.Solid,
            DepthClipEnabled = draw.Mode.X < .5f, ScissorEnabled = true });
        _gpu.SetDepthState(draw.Mode.X > .5f ? GpuDepthState.Disabled : GpuDepthState.ReadOnly);
        _gpu.SetBlendState(blendMode switch { 1 => GpuBlendState.Additive, 2 => GpuBlendState.Multiply, _ => GpuBlendState.AlphaBlend });
        _gpu.SetStructuredBuffer(GpuShaderStage.Vertex, 0, _state);
        _gpu.SetStructuredBuffer(GpuShaderStage.Vertex, 1, _pool);
        _gpu.SetStructuredBuffer(GpuShaderStage.Vertex, 2, _lookup);
        _gpu.SetTexture(GpuShaderStage.Pixel, 3, texture);
        _gpu.SetSampler(GpuShaderStage.Pixel, 0, _library.Sampler);
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
    private GpuBufferHandle Constants<T>(string name) where T:unmanaged => _gpu.CreateBuffer(new GpuBufferDesc
    {
        SizeBytes=Marshal.SizeOf<T>(),Usage=GpuBufferUsage.Dynamic,BindFlags=GpuBindFlags.ConstantBuffer,DebugName="Particles."+name,
    },ReadOnlySpan<byte>.Empty);

    private void EnsureLookup(Vector4[] lookup)
    {
        if(ReferenceEquals(lookup,_uploadedLookup))return;
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
        _lookup=next;_lookupBytes=bytes;_uploadedLookup=lookup;
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
