using System.Numerics;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>
/// Bridges the Model Editor's kitbash/sculpt mesh to the runtime's <see cref="GModelAsset"/> rig and
/// animation system — which already implements humanoid/quadruped auto-rigging, auto-skinning and a
/// library of preset clips, but had no editor surface at all until now (Track H).
/// </summary>
public static class ModelRigBridge
{
    /// <summary>Wraps baked editor geometry in a runtime model asset ready for auto-rigging.</summary>
    public static GModelAsset BuildAsset(string name, MeshVertex[] vertices, ushort[] indices)
    {
        GModelAsset asset = new() { Name = string.IsNullOrWhiteSpace(name) ? "Model" : name };
        asset.Meshes.Add(new GModelMesh
        {
            Name = "Body",
            Vertices = (MeshVertex[])vertices.Clone(),
            Indices = (ushort[])indices.Clone(),
        });

        // Bounds are NOT cosmetic: the rigger's SampleLeafBoneTip uses them to give childless bones
        // (hands, feet) a real tail direction. Leaving them at the default unit box collapses the leg
        // bones to 0.15-unit stubs, so auto-skinning binds the legs to the Hips instead — and a Walk
        // clip then rotates leg bones that no vertices follow.
        asset.Bounds = ComputeBounds(vertices);
        return asset;
    }

    /// <summary>Axis-aligned bounds of the mesh, in the shape the rigger expects.</summary>
    public static GModelBounds ComputeBounds(MeshVertex[] vertices)
    {
        if (vertices.Length == 0)
        {
            return new GModelBounds();
        }

        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        foreach (MeshVertex v in vertices)
        {
            min = Vector3.Min(min, v.Position);
            max = Vector3.Max(max, v.Position);
        }

        return new GModelBounds
        {
            Min = min,
            Max = max,
            Center = (min + max) * 0.5f,
            Size = max - min,
        };
    }

    /// <summary>
    /// Runs the runtime's auto-rig: builds the template skeleton, fits its joints to this mesh's
    /// vertex clusters, auto-binds skin weights, and bakes the preset animation clips (Idle, Walk,
    /// Run, Jump, …). One call because the runtime already does all four steps together.
    /// </summary>
    public static void AutoRig(GModelAsset asset, string category = "Humanoid", string subcategory = "Generic")
    {
        GModelPrimitiveFactory.ApplyTemplateRigAndClips(asset, category, subcategory);
        GModelPrimitiveFactory.EnsureRigIntegrity(asset);
    }

    public static bool HasRig(GModelAsset? asset) => asset?.Rig is { IsValid: true };

    public static IReadOnlyList<string> ClipNames(GModelAsset? asset) =>
        asset?.Animations?.Select(a => a.Name).ToList() ?? [];

    /// <summary>Bone world transforms at a given animation time — used to draw the skeleton overlay.</summary>
    public static Matrix4x4[] BoneWorldTransforms(GModelAsset asset, string clip, float time)
    {
        if (!HasRig(asset)) return [];
        Matrix4x4[] locals = GModelPrimitiveFactory.EvaluateAnimatedLocals(
            asset, AnimationState(asset, clip, time));
        return GModelPrimitiveFactory.ComputeWorldTransforms(asset.Rig.Bones, locals);
    }

    /// <summary>
    /// CPU-skins the model at an animation time into <paramref name="destination"/>. The editor
    /// preview skins on the CPU (rather than via a GPU skin palette) so a posed frame can be
    /// captured deterministically in headless tests.
    /// </summary>
    public static bool SkinInto(GModelAsset asset, string clip, float time, MeshVertex[] destination)
    {
        if (!HasRig(asset) || asset.Meshes.Count == 0) return false;
        GModelMesh mesh = asset.Meshes[0];
        SkinnedMeshVertex[] skinned = mesh.SkinnedVertices;
        if (skinned == null || skinned.Length == 0 || destination.Length != skinned.Length) return false;

        Matrix4x4[] locals = GModelPrimitiveFactory.EvaluateAnimatedLocals(
            asset, AnimationState(asset, clip, time));
        Matrix4x4[] palette = GModelPrimitiveFactory.EvaluateSkinPalette(asset.Rig, locals);
        if (palette.Length == 0) return false;

        for (int i = 0; i < skinned.Length; i++)
        {
            SkinnedMeshVertex v = skinned[i];
            Vector3 position = Vector3.Zero;
            Vector3 normal = Vector3.Zero;
            float total = 0f;

            for (int influence = 0; influence < 4; influence++)
            {
                float weight = Component(v.JointWeights, influence);
                if (weight <= 0f) continue;
                int joint = (int)Component(v.JointIndices, influence);
                if (joint < 0 || joint >= palette.Length) continue;

                position += Vector3.Transform(v.Position, palette[joint]) * weight;
                normal += Vector3.TransformNormal(v.Normal, palette[joint]) * weight;
                total += weight;
            }

            if (total <= 0.0001f)
            {
                // Unweighted vertex: leave it in bind pose rather than collapsing it to the origin.
                position = v.Position;
                normal = v.Normal;
            }

            destination[i] = new MeshVertex
            {
                Position = position,
                Normal = normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : v.Normal,
                Color = v.Color,
                UV = v.UV,
            };
        }

        return true;
    }

    /// <summary>Bind-pose vertices as plain mesh vertices (what to show when not animating).</summary>
    public static MeshVertex[] BindPoseVertices(GModelAsset asset)
    {
        if (asset.Meshes.Count == 0) return [];
        GModelMesh mesh = asset.Meshes[0];
        if (mesh.SkinnedVertices is { Length: > 0 })
        {
            return mesh.SkinnedVertices.Select(v => new MeshVertex
            {
                Position = v.Position,
                Normal = v.Normal,
                Color = v.Color,
                UV = v.UV,
            }).ToArray();
        }

        return (MeshVertex[])mesh.Vertices.Clone();
    }

    private static RuntimeModelAnimationState AnimationState(GModelAsset asset, string clip, float time)
    {
        var animation = asset.Animations.FirstOrDefault(a => a.Name.Equals(clip, StringComparison.OrdinalIgnoreCase));
        return new RuntimeModelAnimationState(clip ?? "", time, animation?.Fps ?? 30, animation?.Loop ?? false);
    }

    private static float Component(Vector4 v, int index) => index switch
    {
        0 => v.X,
        1 => v.Y,
        2 => v.Z,
        _ => v.W,
    };
}
