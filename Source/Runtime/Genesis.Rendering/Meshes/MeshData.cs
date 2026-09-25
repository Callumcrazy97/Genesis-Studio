using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.Interfaces;
namespace Genesis.Rendering.Meshes;

/// <summary>CPU geometry with 32-bit indices. Upload splits safely for the runtime's 16-bit mesh API.</summary>
public sealed class MeshData
{
    public MeshVertex[] Vertices { get; }
    public uint[] Indices { get; }
    public int TriangleCount => Indices.Length / 3;
    public MeshData(MeshVertex[] vertices, uint[] indices)
    {
        Vertices = vertices ?? throw new ArgumentNullException(nameof(vertices));
        Indices = indices ?? throw new ArgumentNullException(nameof(indices));
        if (indices.Length % 3 != 0) throw new ArgumentException("Triangle indices must be a multiple of three.", nameof(indices));
        foreach (uint i in indices) if (i >= vertices.Length) throw new ArgumentOutOfRangeException(nameof(indices));
    }

    public IReadOnlyList<(MeshVertex[] Vertices, ushort[] Indices)> SplitForUpload(int maximumVertices = 65535)
    {
        if (maximumVertices < 3 || maximumVertices > 65535) throw new ArgumentOutOfRangeException(nameof(maximumVertices));
        var result = new List<(MeshVertex[], ushort[])>();
        var remap = new Dictionary<uint, ushort>();
        var vertices = new List<MeshVertex>(); var indices = new List<ushort>();
        void Flush()
        {
            if (indices.Count == 0) return;
            result.Add((vertices.ToArray(), indices.ToArray()));
            remap.Clear(); vertices.Clear(); indices.Clear();
        }
        for (int t = 0; t < Indices.Length; t += 3)
        {
            // The conservative +3 bound can split a little early, but never truncates a triangle.
            if (vertices.Count + 3 > maximumVertices) Flush();
            for (int j = 0; j < 3; j++)
            {
                uint source = Indices[t + j];
                if (!remap.TryGetValue(source, out ushort target))
                {
                    target = checked((ushort)vertices.Count); remap.Add(source, target); vertices.Add(Vertices[source]);
                }
                indices.Add(target);
            }
        }
        Flush(); return result;
    }

    public RegisteredMesh Upload(IRenderController renderer) => new(renderer, this);
}

/// <summary>Owns all GPU pieces of one mesh; dispose on the renderer thread before its device.</summary>
public sealed class RegisteredMesh : IDisposable
{
    private readonly IRenderController _owner;
    private readonly List<MeshHandle> _parts = new();
    private bool _disposed;
    public IReadOnlyList<MeshHandle> Parts => _parts;
    internal RegisteredMesh(IRenderController owner, MeshData mesh)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        try
        {
            foreach (var part in mesh.SplitForUpload())
            {
                var handle = owner.RegisterMesh(part.Vertices, part.Indices);
                if (!handle.IsValid) throw new InvalidOperationException("Renderer rejected a mesh upload.");
                _parts.Add(handle);
            }
        }
        catch { Dispose(); throw; }
    }
    public void Submit(MeshDrawCall[] buffer, ref int count, in Matrix4x4 world, TextureHandle texture = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (count < 0 || count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
        if (buffer.Length - count < _parts.Count) throw new InvalidOperationException("Mesh submission buffer is full; no geometry was silently discarded.");
        foreach (var part in _parts)
            buffer[count++] = new MeshDrawCall { Mesh = part, Texture = texture, World = world, Tint = RenderColor.White, Alpha = 1f };
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var part in _parts) _owner.ReleaseMesh(part);
        _parts.Clear();
    }
}
