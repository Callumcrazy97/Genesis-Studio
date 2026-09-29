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
    public static void AttachGeometry(EcsWorld world, Entity entity, GModelAsset asset, Vector3 scale)
    {
        List<Vector3> vertices = new(); List<int> indices = new();
        Vector3 pivot = asset.Pivot?.Position ?? Vector3.Zero;
        Matrix4x4[] palette = asset.Rig?.IsValid == true ? GModelPrimitiveFactory.EvaluateBindPosePalette(asset.Rig) : [];
        bool mirrored = scale.X * scale.Y * scale.Z < 0;
        foreach (GModelMesh mesh in asset.Meshes.Where(mesh => mesh.Lod == 0))
        {
            int start = vertices.Count;
            vertices.AddRange(mesh.IsSkinned && mesh.SkinnedVertices.Length > 0
                ? mesh.SkinnedVertices.Select(vertex => (GModelAsset.SkinBindPosition(vertex, palette) - pivot) * scale)
                : mesh.Vertices.Select(vertex => (vertex.Position - pivot) * scale));
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
        world.Set(entity, new MeshColliderComponent { Vertices = vertices.ToArray(), Indices = indices.ToArray(), Scale = Vector3.One });
    }

    public static float ClosedVolume(MeshColliderComponent geometry)
    {
        double volume = 0;
        for (int triangle = 0; triangle < geometry.Indices.Length; triangle += 3)
            volume += Vector3.Dot(geometry.Vertices[geometry.Indices[triangle]],
                Vector3.Cross(geometry.Vertices[geometry.Indices[triangle + 1]], geometry.Vertices[geometry.Indices[triangle + 2]])) / 6d;
        return (float)System.Math.Abs(volume);
    }
}
