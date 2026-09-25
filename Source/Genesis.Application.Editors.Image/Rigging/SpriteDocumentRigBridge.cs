using System.Numerics;
using Genesis.Application.Core.Images;
using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Image.Rigging;

public static class SpriteDocumentRigBridge
{
    public static SpriteRig ToRuntimeRig(ImageDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        SpriteRig rig = new();
        if (document.Armature == null) return rig;

        Dictionary<string, int> indexById = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < document.Armature.Bones.Count; i++)
            indexById[document.Armature.Bones[i].Id] = i;

        foreach (ImageBone source in document.Armature.Bones)
        {
            rig.Bones.Add(new SpriteRigBone
            {
                Id = Guid.TryParse(source.Id, out Guid parsed) ? parsed : Guid.NewGuid(),
                Name = source.Name,
                ParentIndex = source.ParentId != null && indexById.TryGetValue(source.ParentId, out int parent)
                    ? parent
                    : -1,
                BindPosition = new Vector2((float)source.BindTransform.Position.X, (float)source.BindTransform.Position.Y),
                BindRotation = (float)(source.BindTransform.RotationDegrees * Math.PI / 180.0),
                BindScale = new Vector2(
                    (float)(source.BindTransform.Scale.X <= 0 ? 1 : source.BindTransform.Scale.X),
                    (float)(source.BindTransform.Scale.Y <= 0 ? 1 : source.BindTransform.Scale.Y)),
                Length = (float)Math.Max(1, source.Length),
            });
        }

        foreach (ImageDeformMesh source in document.DeformMeshes)
        {
            int baseIndex = rig.Vertices.Count;
            foreach (ImageMeshVertex vertex in source.Vertices)
            {
                SpriteDeformVertex runtime = new()
                {
                    Position = new Vector2((float)vertex.Position.X, (float)vertex.Position.Y),
                    Uv = new Vector2((float)vertex.TextureCoordinate.X, (float)vertex.TextureCoordinate.Y),
                };
                foreach (ImageBoneWeight weight in vertex.Weights)
                {
                    if (!indexById.TryGetValue(weight.BoneId, out int boneIndex)) continue;
                    runtime.Weights.Add(new SpriteVertexWeight(boneIndex, (float)weight.Weight));
                }
                runtime.NormalizeAndPrune();
                rig.Vertices.Add(runtime);
            }
            foreach (int index in source.Indices)
                rig.Indices.Add((ushort)(baseIndex + index));
        }

        if (rig.Bones.Count > 0)
            SpriteRigSolver.BuildBindPose(rig);
        return rig;
    }

    public static IReadOnlyList<string> ValidateDocumentRig(ImageDocument document)
    {
        SpriteRig rig = ToRuntimeRig(document);
        List<string> issues = SpriteRigSolver.Validate(rig).ToList();
        if (document.Armature != null)
        {
            HashSet<string> ids = [];
            foreach (ImageBone bone in document.Armature.Bones)
            {
                if (!ids.Add(bone.Id)) issues.Add($"Duplicate bone id: {bone.Id}");
                if (bone.ParentId != null && !document.Armature.Bones.Any(candidate => candidate.Id == bone.ParentId))
                    issues.Add($"Missing parent for {bone.Name}.");
            }
        }
        return issues;
    }

    public static ImageDeformMesh EnsureLayerMesh(
        ImageDocument document,
        ImageWorkspace workspace,
        ImageLayerBuffer layer,
        int gridStep = 8)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(layer);
        ImageFrameBuffer? frame = workspace.CurrentFrame;
        string? frameId = frame?.Id.ToString("N");
        string? layerId = layer.Id.ToString("N");
        ImageDeformMesh? existing = document.DeformMeshes.FirstOrDefault(mesh =>
            string.Equals(mesh.LayerId, layerId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(mesh.FrameId, frameId, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;

        ImageDeformMesh mesh = new()
        {
            Name = $"{layer.Name} Mesh",
            LayerId = layerId,
            FrameId = frameId,
        };
        int width = workspace.Width;
        int height = workspace.Height;
        int step = Math.Clamp(gridStep, 4, 64);
        for (int y = 0; y < height; y += step)
        {
            for (int x = 0; x < width; x += step)
            {
                int index = (y * width + x) * 4;
                if (index + 3 >= layer.Pixels.Length || layer.Pixels[index + 3] == 0) continue;
                mesh.Vertices.Add(new ImageMeshVertex
                {
                    Position = new ImageVector2 { X = x + step * 0.5, Y = y + step * 0.5 },
                    TextureCoordinate = new ImageVector2
                    {
                        X = width <= 0 ? 0 : (x + step * 0.5) / width,
                        Y = height <= 0 ? 0 : (y + step * 0.5) / height,
                    },
                });
            }
        }

        for (int i = 0; i + 2 < mesh.Vertices.Count; i += 3)
        {
            mesh.Indices.Add(i);
            mesh.Indices.Add(i + 1);
            mesh.Indices.Add(i + 2);
        }

        document.DeformMeshes.Add(mesh);
        return mesh;
    }

    public static void AutoWeightMesh(ImageDocument document, ImageDeformMesh mesh, float falloff = 2f)
    {
        if (document.Armature == null || document.Armature.Bones.Count == 0) return;
        SpriteRig rig = ToRuntimeRig(document);
        if (rig.Bones.Count == 0 || rig.Vertices.Count == 0) return;

        int start = 0;
        foreach (ImageDeformMesh candidate in document.DeformMeshes)
        {
            if (ReferenceEquals(candidate, mesh)) break;
            start += candidate.Vertices.Count;
        }

        SpriteRigSolver.AutoWeightByDistance(rig, falloff);
        for (int i = 0; i < mesh.Vertices.Count; i++)
        {
            if (start + i >= rig.Vertices.Count) break;
            ImageMeshVertex vertex = mesh.Vertices[i];
            vertex.Weights.Clear();
            foreach (SpriteVertexWeight weight in rig.Vertices[start + i].Weights)
            {
                if ((uint)weight.BoneIndex >= (uint)document.Armature.Bones.Count) continue;
                vertex.Weights.Add(new ImageBoneWeight
                {
                    BoneId = document.Armature.Bones[weight.BoneIndex].Id,
                    Weight = weight.Weight,
                });
            }
        }
    }

    public static (Vector2 Start, Vector2 End) GetBoneSegment(ImageBone bone)
    {
        Vector2 start = new((float)bone.BindTransform.Position.X, (float)bone.BindTransform.Position.Y);
        float rotation = (float)(bone.BindTransform.RotationDegrees * Math.PI / 180.0);
        float length = (float)Math.Max(1, bone.Length);
        Vector2 end = start + new Vector2(MathF.Cos(rotation), MathF.Sin(rotation)) * length;
        return (start, end);
    }

    public static void SetBoneSegment(ImageBone bone, Vector2 start, Vector2 end)
    {
        Vector2 delta = end - start;
        bone.BindTransform.Position = new ImageVector2 { X = start.X, Y = start.Y };
        bone.Length = Math.Max(1, delta.Length());
        bone.BindTransform.RotationDegrees = Math.Atan2(delta.Y, delta.X) * 180.0 / Math.PI;
    }

    public static ImageBone CreateBone(Vector2 start, Vector2 end, string name, string? parentId = null) =>
        new()
        {
            Name = name,
            ParentId = parentId,
            BindTransform = new ImageTransform
            {
                Position = new ImageVector2 { X = start.X, Y = start.Y },
                RotationDegrees = Math.Atan2(end.Y - start.Y, end.X - start.X) * 180.0 / Math.PI,
                Scale = new ImageVector2 { X = 1, Y = 1 },
            },
            Length = Math.Max(1, Vector2.Distance(start, end)),
        };
}
