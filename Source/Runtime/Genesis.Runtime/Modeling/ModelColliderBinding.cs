using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Modeling;

internal static class ModelColliderBinding
{
    // A model's collision triangles, unscaled, built once per model (and winding): every instance
    // shares them and carries its own scale, so the physics world can share one mesh between them.
    private sealed class SharedGeometry
    {
        public MeshColliderComponent Plain, Mirrored;
        public bool HasPlain, HasMirrored;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GModelAsset, SharedGeometry> Shared = new();

    public static void AttachGeometry(EcsWorld world, Entity entity, GModelAsset asset, Vector3 scale)
    {
        using var timed = Genesis.Shared.Assets.LoadClock.Measure(Genesis.Shared.Assets.LoadWork.Collider);
        bool mirrored = scale.X * scale.Y * scale.Z < 0;
        SharedGeometry shared = Shared.GetValue(asset, _ => new SharedGeometry());
        MeshColliderComponent geometry;
        lock (shared)
        {
            if (mirrored ? !shared.HasMirrored : !shared.HasPlain)
            {
                MeshColliderComponent built = Build(asset, mirrored);
                if (mirrored) { shared.Mirrored = built; shared.HasMirrored = true; }
                else { shared.Plain = built; shared.HasPlain = true; }
            }
            geometry = mirrored ? shared.Mirrored : shared.Plain;
        }
        geometry.Scale = scale;
        world.Set(entity, geometry);
    }

    private static MeshColliderComponent Build(GModelAsset asset, bool mirrored)
    {
        List<Vector3> vertices = new(); List<int> indices = new();
        Vector3 pivot = asset.Pivot?.Position ?? Vector3.Zero;
        Matrix4x4[] palette = asset.Rig?.IsValid == true ? GModelPrimitiveFactory.EvaluateBindPosePalette(asset.Rig) : [];
        // COL_/UCX_ meshes are the whole collider when there are any; NOCOL_ meshes are left out.
        foreach (GModelMesh mesh in asset.Meshes.Where((mesh, index) => mesh.Lod == 0 && ModelCollisionNames.InCollider(asset, index)))
        {
            int start = vertices.Count;
            vertices.AddRange(mesh.IsSkinned && mesh.SkinnedVertices.Length > 0
                ? mesh.SkinnedVertices.Select(vertex => GModelAsset.SkinBindPosition(vertex, palette) - pivot)
                : mesh.Vertices.Select(vertex => vertex.Position - pivot));
            foreach (ushort index in mesh.Indices)
                if (index >= vertices.Count - start) throw new InvalidDataException("Model collider has an invalid triangle index.");
            for (int triangle = 0; triangle + 2 < mesh.Indices.Length; triangle += 3)
            {
                // Render meshes are counterclockwise; Bepu expects clockwise exterior faces.
                indices.Add(start + mesh.Indices[triangle]);
                indices.Add(start + mesh.Indices[triangle + (mirrored ? 1 : 2)]);
                indices.Add(start + mesh.Indices[triangle + (mirrored ? 2 : 1)]);
            }
        }
        if (indices.Count == 0) throw new InvalidDataException("A Mesh collider requires saved, renderable model geometry.");
        return new MeshColliderComponent { Vertices = vertices.ToArray(), Indices = indices.ToArray(), Scale = Vector3.One };
    }

    public static float ClosedVolume(MeshColliderComponent geometry)
    {
        double volume = 0;
        for (int triangle = 0; triangle < geometry.Indices.Length; triangle += 3)
            volume += Vector3.Dot(geometry.Vertices[geometry.Indices[triangle]],
                Vector3.Cross(geometry.Vertices[geometry.Indices[triangle + 1]], geometry.Vertices[geometry.Indices[triangle + 2]])) / 6d;
        Vector3 scale = geometry.Scale == Vector3.Zero ? Vector3.One : geometry.Scale;
        return (float)System.Math.Abs(volume * scale.X * scale.Y * scale.Z);
    }
}
