using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Primitives;
using Genesis.Shared.Interfaces;

namespace Genesis.Rendering.Particles;

/// <summary>Device-owned shader/geometry cache. No CPU particle state exists in this class.</summary>
internal sealed class GpuParticleLibrary : IDisposable
{
    internal readonly IGpuComputeDevice Device;
    private readonly Dictionary<string, GpuShaderProgramHandle> _compute = new(StringComparer.Ordinal);
    private readonly HashSet<GpuParticleEmitter> _emitters = new();
    internal GpuShaderProgramHandle DrawProgram;
    internal GpuVertexLayoutHandle Layout;
    internal GpuSamplerHandle Sampler;
    internal GpuParticleMesh Quad;
    internal GpuParticleMesh Strip;
    internal long AllocatedBytes { get; private set; }
    internal long BudgetBytes { get; set; } = GpuParticleProtocol.DefaultDeviceBudgetBytes;
    private bool _disposed;

    internal GpuParticleLibrary(IGpuDevice device)
    {
        if (device is not IGpuComputeDevice compute || !device.Capabilities.SupportsComputeShaders
            || !device.Capabilities.SupportsIndirectDraw || !device.Capabilities.SupportsStructuredBuffers)
            throw new NotSupportedException($"{device.BackendName} does not provide the required GPU particle operations. "
                + "No software fallback was selected. Choose Software explicitly to use scalar particles.");
        Device = compute;
        try
        {
            foreach (string entry in GpuParticleShaders.ComputeEntries)
            {
                byte[] code = ShaderCompiler.CompileForBackend(
                    GpuParticleShaders.ComputeFor(Device.ShaderBinaryFormat), entry,
                    GpuShaderStage.Compute, Device.ShaderBinaryFormat).Blob;
                _compute.Add(entry, Device.CreateShaderProgram(new GpuShaderProgramDesc
                {
                    BinaryFormat = Device.ShaderBinaryFormat, ComputeShader = code,
                    DebugName = "Particles." + entry,
                }));
            }
            byte[] vs = ShaderCompiler.CompileForBackend(GpuParticleShaders.Draw, "VS", GpuShaderStage.Vertex, Device.ShaderBinaryFormat).Blob;
            byte[] ps = ShaderCompiler.CompileForBackend(GpuParticleShaders.Draw, "PS", GpuShaderStage.Pixel, Device.ShaderBinaryFormat).Blob;
            DrawProgram = Device.CreateShaderProgram(new GpuShaderProgramDesc
            {
                BinaryFormat = Device.ShaderBinaryFormat, VertexShader = vs, PixelShader = ps,
                DebugName = "Particles.IndirectDraw",
            });
            Layout = Device.CreateVertexLayout(new GpuVertexLayoutDesc
            {
                Elements = [
                    new() { Semantic = "POSITION", Format = GpuFormat.R32Float3, Slot = 0, OffsetBytes = 0 },
                    new() { Semantic = "TEXCOORD", SemanticIndex = 0, Format = GpuFormat.R32Float2, Slot = 0, OffsetBytes = 40 },
                ],
                SlotStrides = [48], DebugName = "Particles.MeshVertexLayout",
            }, DrawProgram);
            Sampler = Device.CreateSampler(new GpuSamplerDesc
            {
                Filter = GpuFilter.Linear, AddressU = GpuAddressMode.Clamp, AddressV = GpuAddressMode.Clamp,
                AddressW = GpuAddressMode.Clamp, CompareOp = GpuCompare.Never, MaxAnisotropy = 1,
                DebugName = "Particles.Linear",
            });
            Quad = CreateGeometry(false);
            Strip = CreateGeometry(true);
        }
        catch { Dispose(); throw; }
    }

    internal GpuShaderProgramHandle Kernel(string name) => _compute[name];

    internal GpuParticleEmitter Create(GpuParticleDefinition definition, int seed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = new GpuParticleEmitter(this, definition, seed);
        _emitters.Add(result);
        return result;
    }

    internal void Reserve(long bytes)
    {
        if (bytes < 0 || bytes > BudgetBytes - AllocatedBytes)
            throw new InvalidOperationException($"GPU particle budget exceeded ({BudgetBytes / 1048576:N0} MiB). "
                + "Reduce effect capacity or collision data. Software fallback is disabled.");
        AllocatedBytes += bytes;
    }
    internal void ReleaseAllocation(GpuParticleEmitter emitter, long bytes)
    {
        _emitters.Remove(emitter);
        AllocatedBytes = Math.Max(0, AllocatedBytes - bytes);
    }
    internal void ReleaseBytes(long bytes) => AllocatedBytes = Math.Max(0, AllocatedBytes - bytes);

    private GpuParticleMesh CreateGeometry(bool strip)
    {
        int pairs = strip ? GpuParticleProtocol.TrailSamples : 2;
        var vertices = new MeshVertex[pairs * 2];
        for (int i = 0; i < pairs; i++)
        {
            float v = i / (float)(pairs - 1);
            vertices[i * 2] = new MeshVertex { Position = new Vector3(-.5f, .5f-v, 0), Normal = -Vector3.UnitZ, Color = Vector4.One, UV = new Vector2(0, v) };
            vertices[i * 2 + 1] = new MeshVertex { Position = new Vector3(.5f, .5f-v, 0), Normal = -Vector3.UnitZ, Color = Vector4.One, UV = new Vector2(1, v) };
        }
        var indices = new ushort[(pairs - 1) * 6];
        for (int i = 0; i < pairs - 1; i++)
        {
            int at = i * 6; ushort a = (ushort)(i * 2);
            indices[at] = a; indices[at+1] = (ushort)(a+1); indices[at+2] = (ushort)(a+2);
            indices[at+3] = (ushort)(a+2); indices[at+4] = (ushort)(a+1); indices[at+5] = (ushort)(a+3);
        }
        GpuBufferHandle vb = default;
        try
        {
            vb = Device.CreateBuffer(new GpuBufferDesc
            {
                SizeBytes = vertices.Length * 48, Usage = GpuBufferUsage.Immutable, BindFlags = GpuBindFlags.VertexBuffer,
                DebugName = strip ? "Particles.StripVertices" : "Particles.QuadVertices",
            }, MemoryMarshal.AsBytes(vertices.AsSpan()));
            GpuBufferHandle ib = Device.CreateBuffer(new GpuBufferDesc
            {
                SizeBytes = indices.Length * 2, Usage = GpuBufferUsage.Immutable, BindFlags = GpuBindFlags.IndexBuffer,
                DebugName = strip ? "Particles.StripIndices" : "Particles.QuadIndices",
            }, MemoryMarshal.AsBytes(indices.AsSpan()));
            return new GpuParticleMesh(vb, ib, 48, indices.Length, GpuIndexFormat.UInt16);
        }
        catch { if (vb.IsValid) Device.ReleaseBuffer(vb); throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (GpuParticleEmitter emitter in new List<GpuParticleEmitter>(_emitters)) emitter.Dispose();
        foreach (GpuShaderProgramHandle shader in _compute.Values) Device.ReleaseShaderProgram(shader);
        _compute.Clear();
        if (DrawProgram.IsValid) Device.ReleaseShaderProgram(DrawProgram);
        if (Layout.IsValid) Device.ReleaseVertexLayout(Layout);
        if (Sampler.IsValid) Device.ReleaseSampler(Sampler);
        ReleaseGeometry(Quad); ReleaseGeometry(Strip);
    }

    private void ReleaseGeometry(GpuParticleMesh mesh)
    {
        if (mesh.Vertices.IsValid) Device.ReleaseBuffer(mesh.Vertices);
        if (mesh.Indices.IsValid) Device.ReleaseBuffer(mesh.Indices);
    }
}
