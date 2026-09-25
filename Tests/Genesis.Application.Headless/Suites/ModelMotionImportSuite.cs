using System.Buffers.Binary;
using System.Drawing;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Assimp;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Modeling;
using Newtonsoft.Json;

namespace Genesis.Application.Headless.Suites;

internal static class ModelMotionImportSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.Model.MotionImport");
        string source = AnimatedGlbFixture.Write(Path.Combine(ctx.Workspace, "Motion donor"));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.MotionImport.ViewerAppendSaveUndoReopen", () =>
        {
            string path = NewModel(ctx, "Animation import viewer");
            using var viewer = new ModelViewerControl(path, ctx.Project!.RootPath); viewer.ImportExternalModel(source);
            using var host = Host(viewer);
            var options = viewer.Controls.Find("ModelViewerCommands", true).OfType<ToolStrip>().Single().Items.OfType<ToolStripDropDownButton>().Single(b => b.Name == "ModelMoreOptions");
            Assert(options.Enabled && options.Overflow == ToolStripItemOverflow.Never && options.DropDownItems.Cast<ToolStripItem>().Select(i => i.Text)
                .SequenceEqual(["Import Rig From Model", "Import Model as animation"]), "More Options has missing, hidden or incorrect actions.");
            options.ShowDropDown(); Editor3DInspectionSuite.Pump(); options.HideDropDown();
            var old = ModelPoseWorkflow.Copy(viewer.PreviewAsset); string geometry = JsonConvert.SerializeObject(old.Meshes), materials = JsonConvert.SerializeObject(old.Materials);
            string resourceSource = StudioModelResourceLoader.ResolveSource(path);
            string[] resources = Directory.GetFiles(ctx.Resources!.AssetsRoot, "*.model.json", SearchOption.AllDirectories);
            var importing = viewer.ImportModelAsAnimationAsync(source);
            Assert(!viewer.Enabled, "Background import did not guard concurrent edits.");
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!importing.IsCompleted && DateTime.UtcNow < deadline) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(2); }
            Assert(importing.IsCompleted, "Background import did not complete while the UI pumped messages.");
            var names = importing.GetAwaiter().GetResult(); Assert(viewer.Enabled, "Import left the viewer disabled.");
            Assert(names.SequenceEqual(["Wave (2)"]) && viewer.ActiveClip == names[0] && viewer.AnimationFrameCount == 31, "Imported clip was not selected in the dropdown or its timing was lost.");
            Assert(JsonConvert.SerializeObject(viewer.PreviewAsset.Meshes) == geometry && JsonConvert.SerializeObject(viewer.PreviewAsset.Materials) == materials && StudioModelResourceLoader.ResolveSource(path) == resourceSource, "Animation import replaced current geometry, materials or source.");
            Assert(resources.SequenceEqual(Directory.GetFiles(ctx.Resources.AssetsRoot, "*.model.json", SearchOption.AllDirectories)), "Animation import created another model resource.");
            viewer.SetSpin(false); viewer.SetCameraView("Front"); viewer.SetFrame(0); using var start = viewer.Viewport.CaptureFrame(5);
            Editor3DInspectionSuite.Capture(ctx, host, "model-imported-animation-start");
            viewer.SetFrame(30); using var end = viewer.Viewport.CaptureFrame(5);
            Assert(start is not null && end is not null && Editor3DInspectionSuite.Difference(start, end) > 20, "Imported animation does not change rendered pixels.");
            Editor3DInspectionSuite.Capture(ctx, host, "model-imported-animation-end");
            viewer.PlayClip(names[0]); viewer.AdvancePreview(.1f); Assert(viewer.IsPlaying, "Imported animation does not play."); viewer.SetPlaying(false);
            viewer.Undo(); Assert(viewer.ClipNames.SequenceEqual(["Wave"]) && viewer.IsDirty, "Import undo did not restore the previous clips."); viewer.Save();
            Assert(StudioModelResourceLoader.Load(path).Animations.Count == 1, "Viewer Save did not persist undo.");
            viewer.Redo(); viewer.Save();
            using var reopened = new ModelViewerControl(path, ctx.Project.RootPath);
            Assert(reopened.ClipNames.SequenceEqual(["Wave", "Wave (2)"]), "Save/reopen lost imported clips.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.MotionImport.RigKeepsGeometryAndAnimation", () =>
        {
            string path = NewModel(ctx, "Rig import viewer");
            using var viewer = new ModelViewerControl(path, ctx.Project!.RootPath); viewer.ImportExternalModel(source);
            var original = ModelPoseWorkflow.Copy(viewer.PreviewAsset);
            viewer.ImportRigFromModel(source);
            Assert(viewer.PreviewAsset.Rig.Bones.Count == 2 && viewer.PreviewAsset.Meshes.All(m => m.IsSkinned), "Imported skeleton was not applied to the current mesh.");
            Assert(viewer.ClipNames.SequenceEqual(["Wave"]), "Rig import removed current animation or imported donor animations implicitly.");
            for (int i = 0; i < original.Meshes.Count; i++)
            {
                Assert(original.Meshes[i].Indices.SequenceEqual(viewer.PreviewAsset.Meshes[i].Indices), "Rig import changed triangle topology.");
                Assert(original.Meshes[i].SkinnedVertices.Select(v => v.JointWeights).SequenceEqual(viewer.PreviewAsset.Meshes[i].SkinnedVertices.Select(v => v.JointWeights)), "Compatible rig import lost authored weights.");
            }
            Near(original.Animations[0].Frames[30].LocalBoneTransforms, viewer.PreviewAsset.Animations[0].Frames[30].LocalBoneTransforms, "Compatible rig import changed current motion.");
            var plain = ModelPoseWorkflow.Copy(original); plain.Rig = new(); plain.Animations.Clear();
            foreach (var mesh in plain.Meshes)
            {
                mesh.Vertices = mesh.SkinnedVertices.Select(v => new Genesis.Shared.Interfaces.MeshVertex { Position = v.Position, Normal = v.Normal, Color = v.Color, UV = v.UV }).ToArray();
                mesh.IsSkinned = false; mesh.SkinnedVertices = [];
            }
            string unrigged = NewModel(ctx, "Unrigged target"); StudioModelResourceLoader.SaveCanonical(unrigged, plain);
            using var empty = new ModelViewerControl(unrigged, ctx.Project.RootPath); using var host = Host(empty);
            empty.ImportRigFromModel(source);
            Assert(empty.PreviewAsset.Meshes.All(m => m.IsSkinned) && empty.PreviewAsset.RigLibrary.Count == 1, "Rig import did not bind an unrigged model or save its library entry.");
            empty.ImportModelAsAnimation(source); empty.SetCameraView("Front"); empty.SetFrame(15);
            using var frame = empty.Viewport.CaptureFrame(5); Assert(frame is not null, "Imported rig/animation stopped the viewer rendering.");
            Editor3DInspectionSuite.Capture(ctx, host, "model-imported-rig-and-animation");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.MotionImport.BoneNamesAndIndexOrder", () =>
        {
            var target = ModelPoseWorkflowSuite.Fixture(); var pose = ModelPoseWorkflow.BindPose(target);
            pose[1] = Matrix4x4.CreateRotationZ(.45f) * pose[1];
            var saved = ModelPoseWorkflow.SavePose(target, "Arm up", pose);
            ModelPoseWorkflow.Generate(target, new() { Name = "Pose clip", FrameCount = 3, Keys = [new() { PoseId = saved.Id }] });
            var donor = ModelPoseWorkflow.Copy(target); int[] permutation = [2, 0, 1];
            donor.Rig.Bones = permutation.Select(i => new GModelBone { Name = "mixamorig:" + target.Rig.Bones[i].Name, BindLocal = target.Rig.Bones[i].BindLocal,
                ParentIndex = Array.IndexOf(permutation, target.Rig.Bones[i].ParentIndex) }).ToList();
            foreach (var clip in donor.Animations) foreach (var frame in clip.Frames) frame.LocalBoneTransforms = permutation.Select(i => frame.LocalBoneTransforms[i]).ToArray();
            GModelPrimitiveFactory.RebuildInverseBindMatrices(donor.Rig);
            var imported = ModelMotionImport.Animations(target, donor, "Names");
            Near(imported.Asset.Animations.Last().Frames[2].LocalBoneTransforms, pose, "Namespace/index mapping attached motion to the wrong joint.");
            Assert(imported.Asset.Poses[0].Id == saved.Id && imported.Asset.Animations.Last().PoseAnimationId == "" && target.Animations.Count == 1, "Import changed existing poses, retained a foreign recipe link or mutated its source.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.MotionImport.AnimationOnlyGltfAndFbx", () =>
        {
            string animationOnly = AnimationOnly(source, ctx.Workspace);
            string path = NewModel(ctx, "Animation-only target");
            using var viewer = new ModelViewerControl(path, ctx.Project!.RootPath); viewer.ImportExternalModel(source);
            Assert(viewer.ImportModelAsAnimation(animationOnly).Count == 1, "Animation-only glTF was rejected for having no mesh.");
            using var converter = new AssimpContext(); var scene = converter.ImportFile(animationOnly, PostProcessSteps.Triangulate);
            string fbx = Path.Combine(ctx.Workspace, "Animation-only.fbx");
            Assert(converter.ExportFile(scene, fbx, "fbxa"), "Could not create animation-only FBX fixture.");
            Assert(viewer.ImportModelAsAnimation(fbx).Count == 1 && viewer.AnimationFrameCount > 1, "Animation-only FBX lost its clips during conversion.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.MotionImport.InvalidDonorLeavesFilesUnchanged", () =>
        {
            string path = NewModel(ctx, "Rejected animation target");
            using var viewer = new ModelViewerControl(path, ctx.Project!.RootPath); viewer.ImportExternalModel(source);
            var donor = ModelPoseWorkflow.Copy(viewer.PreviewAsset); donor.Rig.Bones[1].Name = "Unrelated joint";
            string bad = Path.Combine(ctx.Workspace, "Different skeleton.gmodel"); RuntimeModelStore.Save(bad, donor);
            string original = File.ReadAllText(path), canonical = File.ReadAllText(StudioModelResourceLoader.CanonicalPath(path));
            bool rejected = false; try { viewer.ImportModelAsAnimation(bad); } catch (InvalidDataException ex) { rejected = ex.Message.Contains("incompatible"); }
            Assert(rejected && !viewer.IsDirty && viewer.ClipNames.SequenceEqual(["Wave"]), "Incompatible skeleton silently changed the target.");
            Assert(File.ReadAllText(path) == original && File.ReadAllText(StudioModelResourceLoader.CanonicalPath(path)) == canonical, "Rejected import changed files on disk.");
            donor.Animations.Clear(); RuntimeModelStore.Save(bad, donor); rejected = false;
            try { viewer.ImportModelAsAnimation(bad); } catch (InvalidDataException ex) { rejected = ex.Message.Contains("no animation"); }
            Assert(rejected, "A static donor silently created a blank animation.");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                rejected = false;
                try { viewer.ImportModelAsAnimation(source); } catch (IOException) { rejected = true; }
                Assert(rejected, "A locked descriptor did not report a save failure.");
            }
            Assert(File.ReadAllText(path) == original && File.ReadAllText(StudioModelResourceLoader.CanonicalPath(path)) == canonical
                && viewer.ClipNames.SequenceEqual(["Wave"]), "A failed save left a partially imported model on disk or in the Viewer.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.MotionImport.EditorPreservesUnsavedGeometry", () =>
        {
            string path = NewModel(ctx, "Editor animation import");
            using var editor = new ModelEditorControl(path, ctx.Project!.RootPath); editor.ImportExternalModel(source);
            editor.PreviewAsset.Meshes[0].SkinnedVertices[0].Color = new Vector4(.2f, .7f, .3f, 1);
            var colour = editor.PreviewAsset.Meshes[0].SkinnedVertices[0].Color;
            editor.ImportModelAsAnimation(source);
            Assert(editor.PreviewAsset.Meshes[0].SkinnedVertices[0].Color == colour && editor.AnimationClipCount == 2, "Motion import discarded the editor's working geometry.");
            editor.Undo(); Assert(editor.AnimationClipCount == 1 && editor.PreviewAsset.Meshes[0].SkinnedVertices[0].Color == colour, "Undo removed unrelated model work.");
            editor.Redo(); editor.Save();
            Assert(StudioModelResourceLoader.Load(path).Animations.Count == 2, "Editor save lost the imported animation.");
        });
    }
    private static string NewModel(HeadlessContext ctx, string name) => ctx.Resources!.CreateResource(ctx.Resources.AssetsRoot, ResourceKind.Model, name);
    private static Form Host(ModelViewerControl viewer)
    {
        var form = UnattendedWindowing.NewHost(1440, 920); form.Controls.Add(viewer); ThemeService.Apply(form); UnattendedWindowing.ShowWithoutFocus(form); Editor3DInspectionSuite.Pump(); return form;
    }
    private static string AnimationOnly(string glb, string folder)
    {
        byte[] bytes = File.ReadAllBytes(glb); int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        var root = JsonNode.Parse(bytes.AsSpan(20, length))!.AsObject();
        root.Remove("meshes"); root.Remove("skins"); root.Remove("images"); root.Remove("textures"); root.Remove("materials");
        foreach (var node in root["nodes"]!.AsArray()) { node!.AsObject().Remove("mesh"); node.AsObject().Remove("skin"); }
        root["buffers"]![0]!["uri"] = "Animation-only.bin";
        File.WriteAllBytes(Path.Combine(folder, "Animation-only.bin"), bytes.AsSpan(28 + length, root["buffers"]![0]!["byteLength"]!.GetValue<int>()).ToArray());
        string path = Path.Combine(folder, "Animation-only.gltf"); File.WriteAllText(path, root.ToJsonString()); return path;
    }
    private static void Near(Matrix4x4[] a, Matrix4x4[] b, string message)
    {
        Assert(a.Length == b.Length && a.Zip(b).All(p => Vector3.Distance(p.First.Translation, p.Second.Translation) < .0001f
            && Vector3.Distance(Vector3.TransformNormal(Vector3.UnitX, p.First), Vector3.TransformNormal(Vector3.UnitX, p.Second)) < .0001f
            && Vector3.Distance(Vector3.TransformNormal(Vector3.UnitY, p.First), Vector3.TransformNormal(Vector3.UnitY, p.Second)) < .0001f), message);
    }
    private static void Assert(bool value, string message) => HeadlessHarness.Assert(value, message);
}
