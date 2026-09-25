using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Rendering.Meshes
{
    /// <summary>CPU-side mesh merge utility shared by all render subsystems.</summary>
    public static class MeshCombiner
    {
        public static (MeshVertex[] vertices, ushort[] indices) Combine(ReadOnlySpan<MeshCombinePart> parts)
        {
            if (parts.Length == 0)
                return (Array.Empty<MeshVertex>(), Array.Empty<ushort>());

            int vertCap = 0, idxCap = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].IsEmpty) continue;
                vertCap += parts[i].Vertices.Length;
                idxCap += parts[i].Indices.Length;
            }

            if (vertCap == 0)
                return (Array.Empty<MeshVertex>(), Array.Empty<ushort>());

            var verts = new MeshVertex[vertCap];
            var indices = new ushort[idxCap];
            int vWrite = 0, iWrite = 0;

            for (int p = 0; p < parts.Length; p++)
            {
                ref readonly MeshCombinePart part = ref parts[p];
                if (part.IsEmpty) continue;
                AppendTransformed(verts, ref vWrite, indices, ref iWrite, part.Vertices, part.Indices, part.Transform);
            }

            if (vWrite == vertCap && iWrite == idxCap)
                return (verts, indices);

            Array.Resize(ref verts, vWrite);
            Array.Resize(ref indices, iWrite);
            return (verts, indices);
        }

        public static void AppendTransformed(
            MeshVertex[] dstVerts, ref int vertWrite,
            ushort[] dstIndices, ref int indexWrite,
            ReadOnlySpan<MeshVertex> srcVerts, ReadOnlySpan<ushort> srcIndices,
            Matrix4x4 transform)
        {
            if (srcVerts.Length == 0 || srcIndices.Length == 0)
                return;

            int baseVertex = vertWrite;
            Matrix4x4 normalTransform = Matrix4x4.Invert(transform, out var inverse)
                ? Matrix4x4.Transpose(inverse) : Matrix4x4.Identity;
            bool reflected = transform.GetDeterminant() < 0;
            for (int i = 0; i < srcVerts.Length; i++)
            {
                MeshVertex v = srcVerts[i];
                v.Position = Vector3.Transform(v.Position, transform);
                v.Normal = Vector3.Normalize(Vector3.TransformNormal(v.Normal, normalTransform));
                dstVerts[vertWrite++] = v;
            }

            for (int i = 0; i < srcIndices.Length; i++)
            {
                int source = reflected && i / 3 * 3 + 2 < srcIndices.Length
                    ? i / 3 * 3 + (i % 3 == 1 ? 2 : i % 3 == 2 ? 1 : 0) : i;
                dstIndices[indexWrite++] = (ushort)(srcIndices[source] + baseVertex);
            }
        }

        public static void AppendTransformed(
            List<MeshVertex> verts, List<ushort> indices,
            ReadOnlySpan<MeshVertex> srcVerts, ReadOnlySpan<ushort> srcIndices,
            Matrix4x4 transform)
        {
            if (srcVerts.Length == 0 || srcIndices.Length == 0)
                return;

            int baseVertex = verts.Count;
            Matrix4x4 normalTransform = Matrix4x4.Invert(transform, out var inverse)
                ? Matrix4x4.Transpose(inverse) : Matrix4x4.Identity;
            bool reflected = transform.GetDeterminant() < 0;
            for (int i = 0; i < srcVerts.Length; i++)
            {
                MeshVertex v = srcVerts[i];
                v.Position = Vector3.Transform(v.Position, transform);
                v.Normal = Vector3.Normalize(Vector3.TransformNormal(v.Normal, normalTransform));
                verts.Add(v);
            }

            for (int i = 0; i < srcIndices.Length; i++)
            {
                int source = reflected && i / 3 * 3 + 2 < srcIndices.Length
                    ? i / 3 * 3 + (i % 3 == 1 ? 2 : i % 3 == 2 ? 1 : 0) : i;
                indices.Add((ushort)(srcIndices[source] + baseVertex));
            }
        }
    }
}
