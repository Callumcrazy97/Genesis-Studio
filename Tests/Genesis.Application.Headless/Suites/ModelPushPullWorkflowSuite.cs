using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Settings;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Headless.Suites;

internal static class ModelPushPullWorkflowSuite
{
    public static void Run(HeadlessContext ctx)
    {
        var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "ModelPushPull"), "Push and pull");
        ResourceService resources = new(project);
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.PushPull.SignedNumericFacesUndoAndReopen", () =>
        {
            string path = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Numeric faces");
            using ModelEditorControl editor = new(path, project.RootPath); using Form host = UnattendedWindowing.NewHost(1440, 900);
            Prepare(editor, host);
            GModelMesh original = ModelPoseWorkflow.Copy(editor.PreviewAsset).Meshes[0];
            foreach (decimal distance in new[] { -.25m, .25m })
            {
                Descendants(editor).OfType<Button>().Single(button => button.Text == "Face").PerformClick();
                Point point = At(editor, new(.32f, -.32f, -.5f));
                Check(editor.TryPickSurface(point, out Vector3 hit) && Math.Abs(Math.Abs(hit.Z) - .5f) < .001, "The pointer did not hit the camera-facing cube surface.");
                editor.PointerDown(point, MouseButtons.Left); editor.PointerUp(point, MouseButtons.Left);
                Check(editor.SelectedFaceCount == 1, "The real Face selection did not select a visible triangle.");
                NumericUpDown amount = (NumericUpDown)editor.Controls.Find("ModelTopologyAmount", true).Single(); amount.Value = 0;
                editor.ExtrudeSelectedFaces(); Check(Same(editor.PreviewAsset.Meshes[0], original), "Zero distance created degenerate geometry.");
                amount.Value = distance;
                Descendants(editor).OfType<Button>().Single(button => button.Text == "Extrude  E").PerformClick();
                GModelMesh changed = editor.PreviewAsset.Meshes[0];
                float expected = hit.Z + MathF.Sign(hit.Z) * (float)distance;
                Check(changed.Indices.Length == original.Indices.Length + 18
                    && changed.Vertices.Skip(original.Vertices.Length).All(vertex => Math.Abs(vertex.Position.Z - expected) < .001),
                    "Signed numeric distance did not move the selected face along its normal.");
                editor.Undo(); Check(Same(editor.PreviewAsset.Meshes[0], original), "Numeric undo did not restore the original mesh.");
                editor.Redo(); editor.Save(); using (ModelEditorControl reopened = new(path, project.RootPath))
                    Check(Same(reopened.PreviewAsset.Meshes[0], changed) && reopened.PreviewAsset.Rig.Bones.Count == 1
                        && reopened.PreviewAsset.Animations.Count == 1, "Signed extrusion did not save/reopen or damaged existing animation.");
                editor.Undo();
            }
        });
        foreach ((int width, float scale) in new[] { (1440, 1f), (1000, 1f), (1440, 2f) })
            HeadlessHarness.RunCase(ctx.Report, $"Editor.Model.PushPull.PointerLayout.{width}.Scale{scale}", () =>
            {
                GenesisSettings settings = new(); settings.Appearance.InterfaceScale = scale;
                try
                {
                    ThemeService.ApplySettings(settings);
                    string path = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, $"Drag faces {width} {scale}");
                    using ModelEditorControl editor = new(path, project.RootPath); using Form host = UnattendedWindowing.NewHost(width, 900);
                    Prepare(editor, host); editor.SelectPart(0);
                    GModelMesh original = ModelPoseWorkflow.Copy(editor.PreviewAsset).Meshes[0];
                    foreach (int direction in new[] { -1, 1 })
                    {
                        Descendants(editor).OfType<Button>().Single(button => button.Text == "Push / Pull selected face").PerformClick();
                        Point start = At(editor, new(0, -.1f, -.5f)); Point end = new(start.X, start.Y + direction * 80);
                        Check(editor.TryPickSurface(start, out Vector3 hit), "The pointer did not hit the saved Model.");
                        editor.PointerDown(start, MouseButtons.Left); editor.PointerMove(end, MouseButtons.Left);
                        Check(editor.SelectedFaceCount == 1 && !editor.Viewport.NavigationEnabled
                            && editor.PreviewAsset.Meshes[0].Indices.Length > original.Indices.Length, "The transform gizmo intercepted a face pull or the preview did not update.");
                        editor.SelectTool(ModelAuthoringTool.Select);
                        Check(Same(editor.PreviewAsset.Meshes[0], original) && editor.Viewport.NavigationEnabled, "Changing tools committed a cancelled face pull.");
                        editor.SelectTool(ModelAuthoringTool.Push); editor.PointerDown(start, MouseButtons.Left); editor.PointerMove(end, MouseButtons.Left); editor.PointerUp(end, MouseButtons.Left);
                        GModelMesh changed = ModelPoseWorkflow.Copy(editor.PreviewAsset).Meshes[0];
                        Check(changed.Vertices.Skip(original.Vertices.Length).All(vertex => direction < 0
                            ? (vertex.Position.Z - hit.Z) * MathF.Sign(hit.Z) > 0 : (vertex.Position.Z - hit.Z) * MathF.Sign(hit.Z) < 0),
                            "The actual face drag did not pull outward/push inward.");
                        Check(editor.Viewport.NavigationEnabled && editor.IsDirty, "A completed face pull retained navigation capture or lost dirty state.");
                        editor.Undo(); Check(Same(editor.PreviewAsset.Meshes[0], original), "Drag undo did not restore the mesh.");
                        editor.Redo(); Check(Same(editor.PreviewAsset.Meshes[0], changed), "Drag redo differs from the preview.");
                        editor.Save(); using (ModelEditorControl reopened = new(path, project.RootPath)) Check(Same(reopened.PreviewAsset.Meshes[0], changed), "Face drag did not persist.");
                        editor.Undo();
                    }
                    editor.Redo();
                    editor.SelectTool(ModelAuthoringTool.Push);
                    editor.Viewport.Camera.Yaw = MathF.PI - .55f; editor.Viewport.Camera.Pitch = -.2f;
                    using (var frame = editor.Viewport.CaptureFrame(3)) { }
                    string name = $"model-push-pull-{width}-scale{scale}";
                    var metrics = VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, name + ".png"), true);
                    ctx.Report.Images.Add(ImageResult.From(name, name + ".png", metrics));
                    Descendants(editor).OfType<ToolStrip>().SelectMany(strip => strip.Items.OfType<ToolStripButton>())
                        .Single(button => button.Name == "ModelGizmoMove").PerformClick();
                    Check(editor.ActiveTool == ModelAuthoringTool.Select, "Move did not leave face pull mode.");
                }
                finally { settings.Appearance.InterfaceScale = 1; ThemeService.ApplySettings(settings); }
            });
    }
    private static void Prepare(ModelEditorControl editor, Form host)
    {
        GModelAsset asset = GModelPrimitiveFactory.CreateCube("Existing model", 1);
        asset.Rig = new() { Bones = [new() { Name = "Root" }], InverseBindMatrices = [Matrix4x4.Identity] };
        asset.Animations.Add(new() { Name = "Preserved clip", Frames = [new() { LocalBoneTransforms = [Matrix4x4.Identity] }] });
        editor.ApplyAnimationWorkspace(asset, "Preserved clip"); editor.Save(); host.Controls.Add(editor);
        ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host); GateSuite.Pump(3, 20);
        Descendants(editor).OfType<ToolStrip>().SelectMany(strip => strip.Items.OfType<ToolStripButton>()).Single(button => button.Text?.EndsWith("Edit", StringComparison.Ordinal) == true).PerformClick();
        editor.Viewport.Camera.Target = Vector3.Zero; editor.Viewport.Camera.Distance = 3;
        editor.Viewport.Camera.Pitch = 0; editor.Viewport.Camera.Yaw = MathF.PI;
        Descendants(editor).OfType<CheckBox>().Single(check => check.Text == "Snap to Grid").Checked = false;
        using (var frame = editor.Viewport.CaptureFrame(3)) { }
    }
    private static Point At(ModelEditorControl editor, Vector3 point) { var p = editor.Viewport.WorldToSurface(point); return new((int)p.X, (int)p.Y); }
    private static bool Same(GModelMesh a, GModelMesh b) => a.Vertices.SequenceEqual(b.Vertices) && a.Indices.SequenceEqual(b.Indices);
    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
    private static IEnumerable<Control> Descendants(Control root)
    { foreach (Control child in root.Controls) { yield return child; foreach (Control nested in Descendants(child)) yield return nested; } }
}
