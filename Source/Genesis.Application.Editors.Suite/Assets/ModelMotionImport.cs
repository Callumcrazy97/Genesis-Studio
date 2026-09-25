using System.Numerics;
using Genesis.Application.Core.Resources;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed record ModelMotionImportResult(GModelAsset Asset, IReadOnlyList<string> Clips, string Message);

/// <summary>Imports motion onto an existing asset without replacing its geometry or source file.</summary>
public static partial class ModelMotionImport
{
    public const string FileFilter = "Model files|*.glb;*.gltf;*.fbx;*.obj;*.dae;*.blend;*.model.json;*.gmodel";

    public static GModelAsset ReadDonor(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Model file not found.", path);
        if (path.EndsWith(".gmodel", StringComparison.OrdinalIgnoreCase)) return ReadCanonical(path);
        if (path.EndsWith(".model.json", StringComparison.OrdinalIgnoreCase))
        {
            string canonical = StudioModelResourceLoader.CanonicalPath(path);
            if (File.Exists(canonical)) return ReadCanonical(canonical);
            path = StudioModelResourceLoader.ResolveSource(path);
            if (!File.Exists(path)) throw new InvalidDataException("This model has no saved rig or source model.");
        }
        using var converted = ModelSourceConversion.Convert(path, requireGeometry: false);
        // This path does not extract materials/textures or create a resource beside the donor.
        return ExternalModelImporter.Import(converted.Path, Path.GetDirectoryName(converted.Path)!,
            Path.Combine(Path.GetDirectoryName(converted.Path)!, "motion.model.json"), rigAndAnimationsOnly: true);
    }

    private static GModelAsset ReadCanonical(string path)
    {
        var asset = RuntimeModelStore.Load(path);
        if (asset.ImportRequired) throw new InvalidDataException(asset.ImportMessage);
        return asset;
    }

    public static ModelMotionImportResult Animations(GModelAsset target, GModelAsset donor, string sourceName)
    {
        RequireMesh(target);
        ValidateRig(target.Rig, "The current model has no rig. Use Import Rig From Model first.");
        ValidateRig(donor.Rig, "The selected file contains no skeleton or animated nodes.");
        if (donor.Animations.Count == 0) throw new InvalidDataException("The selected model contains no animation clips.");
        var result = ModelPoseWorkflow.Copy(target);
        var mapping = new MotionMapping(donor.Rig, result.Rig);
        var names = new List<string>(); int unmatched = 0;
        foreach (var source in donor.Animations)
        {
            var clip = RemapClip(source, mapping, out int missed);
            unmatched = Math.Max(unmatched, missed);
            clip.Name = UniqueName(string.IsNullOrWhiteSpace(source.Name) ? sourceName : source.Name, result.Animations.Select(c => c.Name));
            clip.PoseAnimationId = ""; // Donor recipe IDs do not belong to the current model's pose library.
            result.Animations.Add(clip); names.Add(clip.Name);
        }
        string note = unmatched > 0 ? $" {unmatched} unmatched animated joint(s) were left out." : "";
        return new(result, names, $"Imported {names.Count} animation clip(s). Select a clip below and press Play.{note}");
    }

    public static ModelMotionImportResult Rig(GModelAsset target, GModelAsset donor, string sourceName)
    {
        RequireMesh(target);
        ValidateRig(donor.Rig, "The selected model contains no rig. Choose a skinned or animated model.");
        var result = ModelPoseWorkflow.Copy(target);
        var originalRig = result.Rig;
        result.Rig = ModelPoseWorkflow.Copy(donor.Rig); result.Rig.TemplateName = "";
        GModelPrimitiveFactory.RebuildInverseBindMatrices(result.Rig);
        MotionMapping? mapping = originalRig.IsValid ? new(originalRig, result.Rig) : null;
        if (mapping is not null)
        {
            result.Animations = target.Animations.Select(c => RemapClip(c, mapping, out _)).ToList();
            foreach (var pose in result.Poses)
            {
                mapping.RequireMatch([pose.LocalBoneTransforms]);
                pose.LocalBoneTransforms = mapping.Frame(pose.LocalBoneTransforms);
            }
        }
        var palette = originalRig.IsValid ? GModelPrimitiveFactory.EvaluateBindPosePalette(originalRig) : [];
        var retained = new Dictionary<GModelMesh, SkinnedMeshVertex[]>();
        foreach (var mesh in result.Meshes)
        {
            if (!mesh.IsSkinned || mesh.SkinnedVertices.Length == 0) continue;
            var skin = (SkinnedMeshVertex[])mesh.SkinnedVertices.Clone();
            bool retainWeights = mapping is not null;
            mesh.Vertices = new MeshVertex[skin.Length];
            for (int i = 0; i < skin.Length; i++)
            {
                var vertex = skin[i]; Vector3 position = Vector3.Zero, normal = Vector3.Zero; float total = 0;
                for (int j = 0; j < 4; j++)
                {
                    float weight = vertex.JointWeights[j]; int oldJoint = (int)vertex.JointIndices[j];
                    if (weight <= 0) continue;
                    if (oldJoint < 0 || oldJoint >= palette.Length) throw new InvalidDataException("The current mesh has invalid skin weights.");
                    position += Vector3.Transform(vertex.Position, palette[oldJoint]) * weight;
                    normal += Vector3.TransformNormal(vertex.Normal, palette[oldJoint]) * weight; total += weight;
                    int mapped = mapping?.TargetForSource[oldJoint] ?? -1;
                    if (mapped < 0) retainWeights = false; else skin[i].JointIndices[j] = mapped;
                }
                if (total > .00001f) { vertex.Position = position / total; vertex.Normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : vertex.Normal; }
                mesh.Vertices[i] = new() { Position = vertex.Position, Normal = vertex.Normal, Color = vertex.Color, UV = vertex.UV };
                skin[i].Position = vertex.Position; skin[i].Normal = vertex.Normal;
            }
            if (retainWeights) retained.Add(mesh, skin);
            mesh.IsSkinned = false; mesh.SkinnedVertices = [];
        }
        result.RecalculateBounds();
        GModelPrimitiveFactory.BindSkinToMesh(result);
        foreach (var (mesh, skin) in retained) { mesh.SkinnedVertices = skin; mesh.IsSkinned = true; }
        result.LastSkinDiagnostics = GModelPrimitiveFactory.ValidateSkin(result);
        string name = UniqueName(sourceName, result.RigLibrary.Select(r => r.Name));
        result.RigLibrary.Add(new() { Name = name, Rig = ModelPoseWorkflow.Copy(result.Rig), Binding = result.SkinBindingMode });
        result.Metadata["genesis.editor.activeRig"] = name;
        return new(result, [], $"Imported {result.Rig.Bones.Count} joints and bound the current mesh. Open Rigging to adjust the fit.");
    }

    private static GModelAnimationClip RemapClip(GModelAnimationClip source, MotionMapping mapping, out int unmatched)
    {
        if (source.Frames.Count == 0 || !float.IsFinite(source.Fps) || source.Fps <= 0)
            throw new InvalidDataException($"Animation '{source.Name}' contains no valid sampled frames.");
        unmatched = mapping.RequireMatch(source.Frames.Select(f => f.LocalBoneTransforms));
        return new() { Name = source.Name, Fps = source.Fps, Loop = source.Loop, PoseAnimationId = source.PoseAnimationId,
            Frames = source.Frames.Select(f => new GModelAnimationFrame { LocalBoneTransforms = mapping.Frame(f.LocalBoneTransforms) }).ToList() };
    }

    private static void RequireMesh(GModelAsset asset)
    {
        if (!asset.HasRenderableMeshes) throw new InvalidOperationException("Import a model into this Viewer first.");
    }
    private static string UniqueName(string name, IEnumerable<string> existing)
    {
        string stem = string.IsNullOrWhiteSpace(name) ? "Imported" : name.Trim();
        var names = existing.ToHashSet(StringComparer.OrdinalIgnoreCase); string candidate = stem;
        for (int suffix = 2; names.Contains(candidate); suffix++) candidate = $"{stem} ({suffix})";
        return candidate;
    }
}
