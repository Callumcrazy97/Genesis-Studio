using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Headless.Suites;

internal static class ModelBranchSidebarSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.Model.BranchesAndSidebar");
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Branches.MultipleLimbsPreservePosesAndReopen", () =>
        {
            string path = ctx.Resources!.CreateResource(ctx.Resources.AssetsRoot, ResourceKind.Model, "Branch rig");
            StudioModelResourceLoader.SaveCanonical(path, ModelPoseWorkflowSuite.Fixture());
            using var editor = new ModelEditorControl(path, ctx.Project!.RootPath);
            using var dialog = editor.CreateAnimationStudio(); dialog.NewSkeleton();
            Vector3 root = new(0, 1, 0), hip = new(0, 2, 0), left = new(-1, 1, 0), right = new(1, 1, 0);
            dialog.DrawBone(root, hip); dialog.DrawBone(hip, left);
            dialog.DrawBone(right, hip); // Drawing toward a joint must not remove the first leg or spine.
            Assert(Edges(dialog.Result).SetEquals(["0-1", "1-2", "1-3"]), "Drawing a second limb replaced an existing bone.");
            var before = Edges(dialog.Result); dialog.DrawBone(left, hip);
            Assert(Edges(dialog.Result).SetEquals(before) && dialog.Result.Rig.Bones.Count == 4, "Redrawing a bone duplicated or reversed it.");
            // Join two already drawn chains by their endpoints; no edge may be lost when rerooting.
            dialog.DrawBone(new(3, 0, 0), new(3, 1, 0));
            dialog.BindMesh();
            var asset = dialog.Preview.RiggedAsset!; var pose = dialog.SaveCurrentPose("Rest");
            var end = (Matrix4x4[])pose.LocalBoneTransforms.Clone(); end[4] *= Matrix4x4.CreateTranslation(.2f, .3f, .4f);
            var other = ModelPoseWorkflow.SavePose(asset, "Offset", end);
            var clip = ModelPoseWorkflow.Generate(asset, new() { Name = "Branch motion", FrameCount = 3,
                Keys = [new() { Frame = 1, PoseId = pose.Id }, new() { Frame = 3, PoseId = other.Id }] });
            var poseWorld = Worlds(asset, other.LocalBoneTransforms);
            var clipWorld = clip.Frames.Select(f => Worlds(asset, f.LocalBoneTransforms)).ToArray();
            before = Edges(asset); dialog.GoToPage(0); dialog.DrawBone(hip, new(3, 1, 0));
            Assert(before.IsSubsetOf(Edges(asset)) && Edges(asset).Count == before.Count + 1, "Joining chains dropped an existing bone.");
            Near(Worlds(asset, other.LocalBoneTransforms), poseWorld, "Joining changed a saved pose.");
            for (int i = 0; i < clip.Frames.Count; i++) Near(Worlds(asset, clip.Frames[i].LocalBoneTransforms), clipWorld[i], "Joining changed an animation frame.");
            before = Edges(asset); bool rejected = false;
            try { dialog.DrawBone(left, right); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected && before.SetEquals(Edges(asset)), "A skeleton loop was accepted or partially changed the rig.");
            UnattendedWindowing.Configure(dialog); ThemeService.Apply(dialog); UnattendedWindowing.ShowWithoutFocus(dialog);
            using (var frame = dialog.Preview.Viewport.CaptureFrame(6)) { }
            Editor3DInspectionSuite.Capture(ctx, dialog, "model-shared-joint-branches");
            dialog.BindMesh(); dialog.ApplyToModel(); editor.ApplyAnimationWorkspace(dialog.Result, "Branch motion"); editor.Save();
            using var reopened = new ModelViewerControl(path, ctx.Project.RootPath);
            Assert(before.SetEquals(Edges(reopened.PreviewAsset)), "Save/reopen changed branches.");
            Near(Worlds(reopened.PreviewAsset, reopened.PreviewAsset.Poses.Single(p => p.Name == "Offset").LocalBoneTransforms), poseWorld, "Save/reopen changed the pose.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Sidebar.MeasuredStackResizeAndCollapse", () =>
        {
            string path = ctx.Resources!.CreateResource(ctx.Resources.AssetsRoot, ResourceKind.Model, "Long source name for sidebar wrapping check");
            StudioModelResourceLoader.SaveCanonical(path, ModelPoseWorkflowSuite.Fixture());
            using var viewer = new ModelViewerControl(path, ctx.Project!.RootPath);
            using var host = UnattendedWindowing.NewHost(1400, 980); viewer.Dock = DockStyle.Fill; host.Controls.Add(viewer);
            ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host); Editor3DInspectionSuite.Pump();
            var sections = Editor3DInspectionSuite.Descendants(viewer).OfType<CollapsibleSection>().Where(s => s.HeaderText is "Source" or "Hierarchy / Meshes" or "Materials" or "Model properties").ToArray();
            var source = sections.Single(s => s.HeaderText == "Source"); var tree = sections.Single(s => s.HeaderText == "Hierarchy / Meshes");
            var info = (Label)viewer.Controls.Find("ModelSourceInfo", true).Single();
            int largeTree = tree.Content.Height;
            viewer.SetSidebarWidth(340); Editor3DInspectionSuite.Pump();
            using (var frame = viewer.Viewport.CaptureFrame(6)) { }
            CheckLabels(); Editor3DInspectionSuite.Capture(ctx, host, "model-sidebar-tall");
            host.ClientSize = new Size(1080, 600); viewer.SetSidebarWidth(215); Editor3DInspectionSuite.Pump();
            Assert(tree.Content.Height < largeTree && tree.Content.Height >= 110, "Hierarchy did not resize into available space.");
            Assert(((ScrollableControl)source.Parent!).VerticalScroll.Visible, "Short sidebar did not become scrollable.");
            using var largeFont = new Font(info.Font.FontFamily, 15);
            info.Font = largeFont; Editor3DInspectionSuite.Pump(); CheckLabels();
            var bounds = sections.Select(s => s.Bounds).ToArray();
            for (int i = 0; i < 3; i++) { tree.IsExpanded = false; tree.IsExpanded = true; Editor3DInspectionSuite.Pump(); }
            Assert(bounds.SequenceEqual(sections.Select(s => s.Bounds)), "Collapsing repeatedly changed the sidebar layout.");
            Assert(Editor3DInspectionSuite.Descendants(viewer).Any(c => c.Name == "ModelViewerDivider" && c.Cursor == Cursors.VSplit), "Sidebar divider missing.");
            using (var frame = viewer.Viewport.CaptureFrame(4)) { }
            Editor3DInspectionSuite.Capture(ctx, host, "model-sidebar-short-large-text");
            void CheckLabels()
            {
                foreach (var label in sections.SelectMany(s => s.Content.Controls.OfType<Label>()))
                {
                    var size = TextRenderer.MeasureText(label.Text, label.Font, new Size(Math.Max(1, label.ClientSize.Width - label.Padding.Horizontal), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                    Assert(label.Height >= size.Height + label.Padding.Vertical, "Sidebar text is clipped: " + label.Text);
                }
                for (int i = 1; i < sections.Length; i++) Assert(sections[i].Top >= sections[i - 1].Bottom, "Sidebar sections overlap.");
            }
        });
    }
    private static HashSet<string> Edges(GModelAsset asset) => asset.Rig.Bones.Select((b, i) => (b.ParentIndex, i)).Where(e => e.ParentIndex >= 0)
        .Select(e => Math.Min(e.ParentIndex, e.i) + "-" + Math.Max(e.ParentIndex, e.i)).ToHashSet();
    private static Matrix4x4[] Worlds(GModelAsset asset, Matrix4x4[] locals) => GModelPrimitiveFactory.ComputeWorldTransforms(asset.Rig.Bones, locals);
    private static void Near(Matrix4x4[] actual, Matrix4x4[] expected, string message)
    {
        Assert(actual.Length == expected.Length, message);
        for (int i = 0; i < actual.Length; i++) foreach (var axis in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            Assert(Vector3.Distance(Vector3.Transform(axis, actual[i]), Vector3.Transform(axis, expected[i])) < .001f, message);
    }
    private static void Assert(bool value, string message) => HeadlessHarness.Assert(value, message);
}
