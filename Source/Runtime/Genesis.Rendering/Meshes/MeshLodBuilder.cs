using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Rendering;

namespace Genesis.Rendering.Meshes
{
    /// <summary>Builds simplified mesh variants from a source mesh.</summary>
    public static class MeshLodBuilder
    {
        public static (MeshVertex[] Vertices, ushort[] Indices)[] BuildFourLevels(
            ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<ushort> indices)
        {
            var sourceVerts = vertices.ToArray();
            var sourceIndices = indices.ToArray();
            return new[]
            {
                (sourceVerts, sourceIndices),
                Decimate(sourceVerts, sourceIndices, 2),
                Decimate(sourceVerts, sourceIndices, 4),
                BoundsProxy(sourceVerts),
            };
        }

        public static LodMeshComponent RegisterFourLevels(
            IRenderController renderer,
            ReadOnlySpan<MeshVertex> vertices,
            ReadOnlySpan<ushort> indices,
            float mediumDistance = 25f,
            float farDistance = 60f,
            float veryFarDistance = 120f)
        {
            // Prefer live Engine LodPolicy when callers leave default distances.
            LodPolicy policy = LodPolicy.Current;
            if (mediumDistance == 25f && farDistance == 60f && veryFarDistance == 120f)
            {
                mediumDistance = policy.EffectiveNear;
                farDistance = policy.EffectiveMid;
                veryFarDistance = policy.EffectiveFar;
            }

            var levels = BuildFourLevels(vertices, indices);
            return new LodMeshComponent
            {
                Mesh0           = renderer.RegisterMesh(levels[0].Vertices, levels[0].Indices),
                Mesh1           = renderer.RegisterMesh(levels[1].Vertices, levels[1].Indices),
                Mesh2           = renderer.RegisterMesh(levels[2].Vertices, levels[2].Indices),
                Mesh3           = renderer.RegisterMesh(levels[3].Vertices, levels[3].Indices),
                MediumDistance  = mediumDistance,
                FarDistance     = farDistance,
                VeryFarDistance = veryFarDistance,
                ShadowLodBias   = 1.35f,
            };
        }

        public static MeshHandle SelectMesh(in LodMeshComponent lod, float distance)
        {
            if (distance < lod.MediumDistance) return lod.Mesh0;
            if (distance < lod.FarDistance) return lod.Mesh1;
            if (distance < lod.VeryFarDistance) return lod.Mesh2;
            return lod.Mesh3;
        }

        /// <summary>Picks a coarser mesh for shadow casting than the view mesh at the same distance.</summary>
        public static MeshHandle SelectMeshForShadow(in LodMeshComponent lod, float cameraDistance)
        {
            float bias = lod.ShadowLodBias > 0f ? lod.ShadowLodBias : 1.35f;
            return SelectMesh(lod, cameraDistance * bias);
        }

        private static (MeshVertex[] Vertices, ushort[] Indices) Decimate(
            MeshVertex[] vertices, ushort[] sourceIndices, int triangleStride)
        {
            if (sourceIndices.Length < 3 || triangleStride <= 1)
                return (vertices, sourceIndices);

            var indices = new List<ushort>();
            int triCount = sourceIndices.Length / 3;
            for (int tri = 0; tri < triCount; tri += triangleStride)
            {
                int baseIdx = tri * 3;
                if (baseIdx + 2 >= sourceIndices.Length) break;
                indices.Add(sourceIndices[baseIdx]);
                indices.Add(sourceIndices[baseIdx + 1]);
                indices.Add(sourceIndices[baseIdx + 2]);
            }

            if (indices.Count == 0)
                return BoundsProxy(vertices);

            return (vertices, indices.ToArray());
        }

        private static (MeshVertex[] Vertices, ushort[] Indices) BoundsProxy(ReadOnlySpan<MeshVertex> vertices)
        {
            if (vertices.Length == 0)
                return (Array.Empty<MeshVertex>(), Array.Empty<ushort>());

            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (ref readonly var v in vertices)
            {
                min = Vector3.Min(min, v.Position);
                max = Vector3.Max(max, v.Position);
            }

            Vector3 size = max - min;
            float sx = MathF.Max(size.X, 0.05f);
            float sy = MathF.Max(size.Y, 0.05f);
            float sz = MathF.Max(size.Z, 0.05f);
            return MeshGeometry.BuildCube(RenderColor.White, MathF.Max(sx, MathF.Max(sy, sz)));
        }
    }
}
