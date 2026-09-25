using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Reflection;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;
using Newtonsoft.Json;

namespace Genesis.Application.Headless.Suites;

internal static class ModelRigEditingSuite
{
    public static void Run(HeadlessContext ctx)
    {
        ModelSkinControlSuite.Run(ctx);
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.Model.RigEditing");
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.RigEditing.CopyIsLosslessAndIndependent", () =>
        {
            var asset = ModelPoseWorkflowSuite.Fixture();
            asset.Metadata["Tag"] = "test"; asset.Meshes[0].Metadata["Mesh"] = "test";
            asset.Meshes[0].FaceGroups.Add(new() { Name = "Face", TriangleIndices = [0] });
            asset.Meshes[0].Tangents = [new(1, 0, 0, 1)]; asset.Meshes[0].SourceUVs = [Vector2.One];
            var pose = ModelPoseWorkflow.SavePose(asset, "Rest", ModelPoseWorkflow.BindPose(asset));
            ModelPoseWorkflow.Generate(asset, new() { Keys = [new() { PoseId = pose.Id }] });
            asset.RigLibrary.Add(new() { Name = "Rig", Rig = ModelPoseWorkflow.Copy(asset.Rig) });
            var clone = ModelPoseWorkflow.Copy(asset);
            Assert(JsonConvert.SerializeObject(asset) == JsonConvert.SerializeObject(clone), "Typed copy lost model fields.");
            clone.Meshes[0].Vertices[0].Position += Vector3.One;
            clone.Meshes[0].SkinnedVertices[0].JointWeights = Vector4.Zero;
            clone.Meshes[0].FaceGroups[0].TriangleIndices.Clear(); clone.Metadata["Tag"] = "changed";
            clone.Poses[0].LocalBoneTransforms[0] = default;
            clone.Animations[0].Frames[0].LocalBoneTransforms[0] = default;
            clone.RigLibrary[0].Rig.Bones.Clear();
            Assert(asset.Metadata["Tag"] == "test" && asset.Meshes[0].FaceGroups[0].TriangleIndices.Count == 1
                && asset.RigLibrary[0].Rig.Bones.Count > 0 && asset.Animations[0].Frames[0].LocalBoneTransforms[0] != default(Matrix4x4),
                "Working copy changes leaked into its source or undo snapshot.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.RigEditing.DeleteKeyBoneJointAndTextFocus", () =>
        {
            string path = Resource(ctx, "Delete rig elements");
            using var editor = new ModelEditorControl(path, ctx.Project!.RootPath);
            using var dialog = editor.CreateAnimationStudio(); Show(dialog); dialog.NewSkeleton();
            Vector3[] points = [new(0, 0, 0), new(0, 1, 0), new(1, 1, 0), new(0, 2, 0)];
            foreach (var point in points) dialog.PlaceJoint(point, .055f);
            dialog.DrawBone(points[0], points[1]); dialog.DrawBone(points[1], points[2]); dialog.DrawBone(points[1], points[3]);
            dialog.BindMesh(); dialog.GoToPage(0);
            var preview = dialog.Preview; preview.Viewport.Camera.Yaw = MathF.PI; preview.Viewport.Camera.Pitch = 0;
            var authored = preview.RiggedAsset!;
            var offset = preview.CaptureWorkingPose(); offset[0] *= Matrix4x4.CreateRotationZ(.3f) * Matrix4x4.CreateTranslation(.2f, .1f, 0);
            var savedPose = ModelPoseWorkflow.SavePose(authored, "Offset", offset);
            var savedWorld = Worlds(authored, savedPose.LocalBoneTransforms);
            ModelPoseWorkflow.Generate(authored, new() { Name = "Deletion clip", FrameCount = 3, Keys = [new() { PoseId = savedPose.Id }] });
            preview.FrameModelForTest(); Render(dialog);
            var before = Worlds(dialog.Result, preview.CaptureWorkingPose());
            Point mid = PointAt(dialog, (points[1] + points[2]) / 2);
            Assert(preview.DirectPointerDown(mid), "Bone click missed."); preview.DirectPointerUp(mid);
            Assert(preview.SelectedRigElementIsBone, "Bone body was classified as a joint.");
            Assert(PressDelete(dialog), "Delete shortcut was not handled.");
            var after = dialog.Result;
            Assert(after.Rig.Bones.Count == 4 && after.Rig.Bones[2].ParentIndex == -1 && after.Rig.Bones[3].ParentIndex == 1, "Deleting a bone removed a joint or a different branch.");
            Near(Worlds(after, preview.CaptureWorkingPose()), before, "Bone deletion moved a joint.");
            Near(Worlds(after, after.Poses.Single(p => p.Name == "Offset").LocalBoneTransforms), savedWorld, "Bone deletion changed a saved pose.");
            Point centre = PointAt(dialog, points[1]); preview.DirectPointerDown(centre); preview.DirectPointerUp(centre);
            Assert(!preview.SelectedRigElementIsBone, "Joint centre was classified as a bone.");
            PressDelete(dialog); after = dialog.Result;
            Assert(after.Rig.Bones.Count == 3 && after.Rig.Bones.All(b => b.ParentIndex == -1), "Joint deletion did not detach incident bones safely.");
            Near(Worlds(after, preview.CaptureWorkingPose()), before.Where((_, i) => i != 1).ToArray(), "Joint deletion moved neighbours.");
            Near(Worlds(after, after.Animations.Single(c => c.Name == "Deletion clip").Frames[0].LocalBoneTransforms), savedWorld.Where((_, i) => i != 1).ToArray(), "Joint deletion changed an animation frame.");
            using var field = new TextBox { Text = "keep typing", Dock = DockStyle.Top };
            dialog.Controls.Add(field); dialog.ActiveControl = field;
            Assert(!PressDelete(dialog) && dialog.Result.Rig.Bones.Count == 3, "Delete in a text field edited the skeleton.");
            dialog.Controls.Remove(field);
            Render(dialog); Editor3DInspectionSuite.Capture(ctx, dialog, "rig-deleted-joint-preserves-neighbours");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.RigEditing.UnbindApplyAndManagement", () =>
        {
            string path = Resource(ctx, "Simplified rigging");
            using var editor = new ModelEditorControl(path, ctx.Project!.RootPath);
            using var dialog = editor.CreateAnimationStudio(); Show(dialog);
            var options = dialog.Controls.Find("ModelRigOptions", true).Single();
            var sections = options.Controls.OfType<CollapsibleSection>().ToArray();
            Assert(sections.Select(s => s.HeaderText).SequenceEqual(["Rig Management", "Drawing", "Binding"]), "Rigging has extra or missing option groups.");
            var buttons = Editor3DInspectionSuite.Descendants(options).OfType<Button>().ToArray();
            Assert(buttons.Select(b => b.Text).Order().SequenceEqual(new[] { "New", "Load", "Save Rig", "Delete", "Templates…", "Draw Bone", "Draw Joint", "Select", "Bind To Mesh", "Unbind" }.Order()), "Rigging page has extra or missing actions.");
            dialog.NewSkeleton(); dialog.DrawBone(Vector3.Zero, Vector3.UnitY);
            var saveRig = buttons.Single(b => b.Text == "Save Rig");
            Assert(saveRig.Visible && saveRig.Parent!.ClientRectangle.Contains(saveRig.Bounds), "Save Rig is clipped or hidden.");
            saveRig.PerformClick();
            Assert(dialog.DialogResult == DialogResult.None && dialog.StatusText.StartsWith("Rig saved", StringComparison.Ordinal)
                && dialog.Result.RigLibrary.Any(r => r.Rig.Bones.Count == 2), "Save Rig did not save the drawn rig into the library while keeping the window open.");
            dialog.BindMesh(); dialog.GoToPage(0);
            Assert(dialog.Result.Meshes.All(m => m.IsSkinned), "Binding did not skin the mesh.");
            buttons.Single(b => b.Text == "Unbind").PerformClick();
            Assert(dialog.Result.Meshes.All(m => !m.IsSkinned && m.SkinnedVertices.Length == 0) && dialog.Result.Rig.Bones.Count == 2 && dialog.Result.Poses.Count > 0, "Unbind removed the rig/poses or retained old GPU weights.");
            Render(dialog); Editor3DInspectionSuite.Capture(ctx, dialog, "rig-management-drawing-binding");
            dialog.BeginDrawingJoints(); // Applying an unbound layout must not run binding or discard it.
            dialog.PlaceJoint(new(1, 1, 0), .1f);
            ((Button)dialog.Controls.Find("ApplyModelAnimation", true).Single()).PerformClick();
            Assert(dialog.DialogResult == DialogResult.OK && dialog.Result.Rig.Bones.Count == 3, "Apply refused an unbound layout.");
            editor.ApplyAnimationWorkspace(dialog.Result, dialog.SelectedClip); editor.Save();
            using var reopened = new ModelEditorControl(path, ctx.Project.RootPath);
            using var rig = reopened.CreateAnimationStudio(); Show(rig);
            Assert(rig.Result.Rig.Bones.Count == 3 && rig.Result.RigLibrary.Any(r => r.Rig.Bones.Count == 3), "Apply/save/reopen lost the automatic rig library.");
            var savedRigNames = rig.Result.RigLibrary.Select(r => r.Name).ToArray();
            rig.BindMesh(); Assert(rig.Result.Meshes.All(m => m.IsSkinned), "Rebinding after reopen failed.");
            Assert(rig.Result.RigLibrary.Select(r => r.Name).SequenceEqual(savedRigNames), "Reopening forgot the active rig name and created a duplicate.");
            var legacy = rig.Result; legacy.Metadata.Remove("genesis.editor.activeRig");
            using (var oldRig = new ModelAnimationStudioDialog(path, ctx.Project.RootPath, legacy))
            {
                oldRig.ApplyToModel();
                Assert(oldRig.Result.RigLibrary.Select(r => r.Name).SequenceEqual(savedRigNames), "Opening an older model duplicated its matching saved rig.");
            }
            rig.UnbindMesh();
            var library = (ListBox)rig.Controls.Find("SavedModelRigs", true).Single();
            library.SelectedIndex = library.Items.Count - 1;
            ((Button)rig.Controls.Find("ModelActionLoad", true).Single()).PerformClick();
            Assert(rig.StatusText.StartsWith("Rig loaded", StringComparison.Ordinal) && rig.Result.Rig.Bones.Count == 3 && rig.Result.Meshes.All(m => !m.IsSkinned), "Load silently rebound the mesh or lost joints.");
            ((Button)rig.Controls.Find("ModelActionDelete", true).Single()).PerformClick();
            Assert(rig.Result.Rig.Bones.Count == 0, "Deleting the current rig did not clear it.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.RigEditing.DenseApplyAllocationAndUndo", () =>
        {
            string path = ctx.Resources!.CreateResource(ctx.Resources.AssetsRoot, ResourceKind.Model, "Dense apply");
            var asset = ModelPoseWorkflowSuite.Fixture(); var original = asset.Meshes[0];
            asset.Meshes.Clear();
            for (int mesh = 0; mesh < 2; mesh++)
            {
                var vertices = Enumerable.Range(0, 120).SelectMany(_ => original.Vertices).ToArray();
                var indices = Enumerable.Range(0, 120).SelectMany(i => original.Indices.Select(j => (ushort)(j + i * original.Vertices.Length))).ToArray();
                asset.Meshes.Add(new() { Vertices = vertices, Indices = indices });
            }
            asset.Rig.Bones = Enumerable.Range(0, 22).Select(i => new GModelBone { Name = "Joint " + i, ParentIndex = i == 0 ? -1 : i - 1, BindLocal = Matrix4x4.CreateTranslation(0, .05f, 0) }).ToList();
            GModelPrimitiveFactory.RebuildInverseBindMatrices(asset.Rig); GModelPrimitiveFactory.BindSkinToMesh(asset);
            Assert(asset.Meshes.SelectMany(m => m.SkinnedVertices).Any(v => v.JointWeights.X > 0 && v.JointIndices.X >= 9), "Custom rig binding applied a fixed quadruped bone map.");
            var pose = ModelPoseWorkflow.SavePose(asset, "Rest", ModelPoseWorkflow.BindPose(asset));
            ModelPoseWorkflow.Generate(asset, new() { FrameCount = 60, Keys = [new() { PoseId = pose.Id }] });
            using var editor = new ModelEditorControl(path, ctx.Project!.RootPath);
            long allocated = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
            editor.ApplyAnimationWorkspace(asset, "Animation");
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated; watch.Stop();
            Console.WriteLine($"Dense apply: {asset.Meshes.Sum(m => m.Vertices.Length)} vertices, 22 joints, {watch.ElapsedMilliseconds} ms, {allocated / 1048576d:F1} MiB allocated.");
            Assert(allocated < 160L * 1024 * 1024 && watch.Elapsed < TimeSpan.FromSeconds(10), "Apply allocated serialized geometry or stalled the UI.");
            Assert(editor.PreviewAsset.Meshes.Count == 2 && editor.PreviewAsset.Rig.Bones.Count == 22, "Apply lost geometry or the rig.");
            editor.Undo(); Assert(!editor.PreviewAsset.HasRenderableMeshes, "Apply undo did not restore the empty model.");
            editor.Redo(); Assert(editor.PreviewAsset.Rig.Bones.Count == 22 && editor.PreviewAsset.Animations[0].Frames.Count == 60, "Apply redo lost animation data.");
        });
        string? fox = Environment.GetEnvironmentVariable("GENESIS_MODEL_REVIEW_SOURCE");
        if (!string.IsNullOrWhiteSpace(fox) && File.Exists(fox))
            HeadlessHarness.RunCase(ctx.Report, "Editor.Model.RigEditing.ProductionFoxApplyAndRender", () =>
            {
                string path = ctx.Resources!.CreateResource(ctx.Resources.AssetsRoot, ResourceKind.Model, "Fox apply");
                using var editor = new ModelEditorControl(path, ctx.Project!.RootPath); editor.ImportExternalModel(fox);
                using var host = UnattendedWindowing.NewHost(1440, 920); host.Controls.Add(editor); ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host);
                using var dialog = editor.CreateAnimationStudio(); Show(dialog); dialog.NewSkeleton();
                var bounds = dialog.Result.Bounds;
                for (int i = 0; i < 22; i++) dialog.AddJointAt(i == 0 ? -1 : i - 1, bounds.Center + new Vector3(0, (i / 21f - .5f) * bounds.Size.Y * .8f, 0));
                dialog.BindMesh(); dialog.GoToPage(0); Render(dialog);
                Editor3DInspectionSuite.Capture(ctx, dialog, "rig-fox-binding-controls");
                long allocated = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
                ((Button)dialog.Controls.Find("ApplyModelAnimation", true).Single()).PerformClick();
                Assert(dialog.DialogResult == DialogResult.OK, "Apply button did not accept the fox rig.");
                editor.ApplyAnimationWorkspace(dialog.Result, dialog.SelectedClip);
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated; watch.Stop();
                Console.WriteLine($"Fox apply button + transfer: {watch.ElapsedMilliseconds} ms, {allocated / 1048576d:F1} MiB allocated.");
                Assert(watch.Elapsed < TimeSpan.FromSeconds(10) && allocated < 200L * 1024 * 1024, "Fox apply stalled or allocated excessive memory.");
                using var frame = editor.Viewport.CaptureFrame(5); Assert(frame is not null, "Editor stopped rendering after Apply.");
                Editor3DInspectionSuite.Capture(ctx, host, "rig-fox-after-apply");
            });
    }
    private static string Resource(HeadlessContext ctx, string name)
    {
        string path = ctx.Resources!.CreateResource(ctx.Resources.AssetsRoot, ResourceKind.Model, name);
        StudioModelResourceLoader.SaveCanonical(path, ModelPoseWorkflowSuite.Fixture()); return path;
    }
    private static void Show(ModelAnimationStudioDialog dialog) { UnattendedWindowing.Configure(dialog); ThemeService.Apply(dialog); UnattendedWindowing.ShowWithoutFocus(dialog); Editor3DInspectionSuite.Pump(); }
    private static void Render(ModelAnimationStudioDialog dialog) { Editor3DInspectionSuite.Pump(); using var frame = dialog.Preview.Viewport.CaptureFrame(4); Assert(frame is not null, "Rig viewport did not render."); }
    private static Point PointAt(ModelAnimationStudioDialog dialog, Vector3 point)
    {
        var view = dialog.Preview.Viewport; var p = view.WorldToSurface(point - dialog.Preview.RiggedAsset!.Pivot.Position);
        return new((int)(p.X * view.Host.Width / view.SurfaceWidth), (int)(p.Y * view.Host.Height / view.SurfaceHeight));
    }
    private static bool PressDelete(ModelAnimationStudioDialog dialog)
    {
        object[] arguments = [Message.Create(dialog.Handle, 0x100, (IntPtr)Keys.Delete, IntPtr.Zero), Keys.Delete];
        return (bool)typeof(ModelAnimationStudioDialog).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, arguments)!;
    }
    private static Matrix4x4[] Worlds(GModelAsset asset, Matrix4x4[] pose) => GModelPrimitiveFactory.ComputeWorldTransforms(asset.Rig.Bones, pose);
    private static void Near(Matrix4x4[] a, Matrix4x4[] b, string message) => Assert(a.Length == b.Length && a.Zip(b).All(pair => Vector3.Distance(pair.First.Translation, pair.Second.Translation) < .001f), message);
    private static void Assert(bool value, string message) => HeadlessHarness.Assert(value, message);
}
