using System.Numerics;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Editors.Suite.Assets;

public static partial class ModelPoseWorkflow
{
    // Geometry and dense animation frames are already value-type arrays. Copy them directly:
    // serializing hundreds of thousands of vertices to JSON stalls the UI and inflates memory.
    private static GModelAsset CopyAsset(GModelAsset source) => new()
    {
        Schema = source.Schema, Name = source.Name, SourceFile = source.SourceFile, ImportedUtc = source.ImportedUtc,
        ImportRequired = source.ImportRequired, ImportMessage = source.ImportMessage, Culling = source.Culling, WindingOrder = source.WindingOrder,
        Bounds = Copy(source.Bounds), ImportSettings = Copy(source.ImportSettings), Nodes = source.Nodes.Select(Copy).ToList(),
        Sockets = (source.Sockets ?? []).Select(socket => new GModelSocket
        {
            Name = socket.Name, BoneIndex = socket.BoneIndex, NodeIndex = socket.NodeIndex,
            LocalTransform = socket.LocalTransform,
        }).ToList(),
        Metadata = new(source.Metadata, source.Metadata.Comparer), Materials = source.Materials.Select(Copy).ToList(),
        Meshes = source.Meshes.Select(CopyMesh).ToList(), Lods = source.Lods.Select(Copy).ToList(),
        Pivot = Copy(source.Pivot), Colliders = source.Colliders.Select(Copy).ToList(), Rig = CopyRig(source.Rig),
        RigWizard = source.RigWizard is null ? null : Copy(source.RigWizard),
        Poses = source.Poses.Select(Copy).ToList(), PoseAnimations = source.PoseAnimations.Select(Copy).ToList(),
        RigLibrary = source.RigLibrary.Select(r => new GModelRigPreset { Name = r.Name, Binding = r.Binding, Rig = CopyRig(r.Rig) }).ToList(),
        Animations = source.Animations.Select(CopyClip).ToList(), SkinBindingMode = source.SkinBindingMode,
        SkinningMode = source.SkinningMode, LastSkinDiagnostics = Copy(source.LastSkinDiagnostics), OwnedTextureFolder = source.OwnedTextureFolder,
    };

    private static GModelMesh CopyMesh(GModelMesh source) => new()
    {
        Name = source.Name, SourceNodeIndex = source.SourceNodeIndex, MaterialIndex = source.MaterialIndex,
        Vertices = (Genesis.Shared.Interfaces.MeshVertex[])source.Vertices.Clone(),
        SkinnedVertices = (Genesis.Shared.Interfaces.SkinnedMeshVertex[])source.SkinnedVertices.Clone(),
        Indices = (ushort[])source.Indices.Clone(), TriangleMaterialIndices = (int[])source.TriangleMaterialIndices.Clone(),
        Tangents = (Vector4[])source.Tangents.Clone(),
        FaceGroups = source.FaceGroups.Select(g => new GModelFaceGroup { Name = g.Name, TriangleIndices = new(g.TriangleIndices) }).ToList(),
        IsSkinned = source.IsSkinned, Lod = source.Lod, SourceUvProtected = source.SourceUvProtected,
        SourceUVs = (Vector2[])source.SourceUVs.Clone(), UvOverride = source.UvOverride, UvProjection = source.UvProjection,
        SubdivisionLevel = source.SubdivisionLevel,
        SubdivisionCageVertices = (Genesis.Shared.Interfaces.MeshVertex[])(source.SubdivisionCageVertices ?? []).Clone(),
        SubdivisionCageIndices = (ushort[])(source.SubdivisionCageIndices ?? []).Clone(),
        SubdivisionCageMaterials = (int[])(source.SubdivisionCageMaterials ?? []).Clone(),
        SmoothShading = source.SmoothShading,
        MorphTargets = (source.MorphTargets ?? []).Select(target => new GModelMorphTarget
        {
            Name = target.Name,
            DefaultWeight = target.DefaultWeight,
            PositionDeltas = (Vector3[])(target.PositionDeltas ?? []).Clone(),
            NormalDeltas = (Vector3[])(target.NormalDeltas ?? []).Clone(),
        }).ToList(),
        Metadata = new(source.Metadata, source.Metadata.Comparer),
    };

    private static GModelRig CopyRig(GModelRig source) => new()
    {
        TemplateName = source.TemplateName, RigVersion = source.RigVersion,
        InverseBindMatrices = (Matrix4x4[])source.InverseBindMatrices.Clone(),
        Bones = source.Bones.Select(b => new GModelBone { Name = b.Name, ParentIndex = b.ParentIndex, BindLocal = b.BindLocal, JointRadius = b.JointRadius, Deforms = b.Deforms }).ToList(),
    };

    private static GModelAnimationClip CopyClip(GModelAnimationClip source) => new()
    {
        Name = source.Name, PoseAnimationId = source.PoseAnimationId, Fps = source.Fps, Loop = source.Loop,
        WizardRecipeId = source.WizardRecipeId,
        Frames = source.Frames.Select(f => new GModelAnimationFrame
        {
            LocalBoneTransforms = (Matrix4x4[])f.LocalBoneTransforms.Clone(),
            MorphWeights = new Dictionary<string, float>(f.MorphWeights ?? new Dictionary<string, float>(), StringComparer.OrdinalIgnoreCase),
        }).ToList(),
        Tracks = source.Tracks.Select(t => new GModelAnimationTrack { BoneIndex = t.BoneIndex,
            Keys = t.Keys.Select(k => new GModelTrsKey { Frame = k.Frame, Translation = k.Translation, Rotation = k.Rotation, Scale = k.Scale }).ToList() }).ToList(),
    };
}
