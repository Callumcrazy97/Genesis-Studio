using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Headless.Suites;

internal static class ModelEditorResetSuite
{
    public static void Run(HeadlessContext ctx)
    {
        var project = HeadlessHarness.Require(ctx.Project, "Project");
        var resources = HeadlessHarness.Require(ctx.Resources, "Resources");
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.Model.Reset");
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Reset.MaterialGroupsTransformAndPersistence", () =>
        {
            string path = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Materials and groups");
            using var editor = new ModelEditorControl(path, project.RootPath); editor.AddPart(ModelPrimitiveKind.Cube);
            editor.RenameSelectedGroup("Body"); editor.PreviewAsset.Meshes[0].TriangleMaterialIndices = Enumerable.Repeat(0, editor.BakedTriangleCount).ToArray();
            editor.AddMaterial("Body paint");
            Assert(editor.PreviewAsset.Meshes[0].TriangleMaterialIndices.Length == 0, "Whole-mesh material assignment left stale per-face overrides.");
            string source = Path.Combine(ctx.Workspace, "paint source.png");
            using (var texture = new Bitmap(4, 4)) { using var draw = Graphics.FromImage(texture); draw.Clear(Color.Coral); texture.Save(source); }
            editor.AssignSelectedTexture(source);
            var before = editor.BakedVerticesForTest;
            editor.TransformSelectedMesh(new Vector3(0, 2, 0), new Vector3(0, 35, 0), new Vector3(1, 2, .5f));
            Assert(editor.ModelBounds.Min.Y > 0 && before.Where((v, i) => v.Position != editor.BakedVerticesForTest[i].Position).Any(), "Transform did not move and resize the mesh.");
            editor.Undo(); Assert(before.SequenceEqual(editor.BakedVerticesForTest), "Transform undo changed the mesh."); editor.Redo(); editor.Save();
            using var reopened = new ModelViewerControl(path, project.RootPath);
            var mesh = reopened.PreviewAsset.Meshes.Single(); var material = reopened.PreviewAsset.Materials[mesh.MaterialIndex];
            Assert(mesh.Name == "Body" && material.Name == "Body paint" && File.Exists(Path.Combine(project.RootPath, material.AlbedoTexture)), "Group name, material assignment or owned texture was lost.");
            Assert(reopened.GroundHeight == reopened.ModelBounds.Min.Y, "Transform persistence moved the model below its floor.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Reset.PointerSculptPaintUndoSaveAndShell", () =>
        {
            string path = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Sculpt and paint");
            using var editor = new ModelEditorControl(path, project.RootPath);
            editor.AddPart(ModelPrimitiveKind.Sphere); editor.Save();
            using var host = UnattendedWindowing.NewHost(1440, 920); host.Controls.Add(editor); ThemeService.Apply(host);
            UnattendedWindowing.ShowWithoutFocus(host); System.Windows.Forms.Application.DoEvents();
            editor.SetCameraView("Front"); editor.FrameModel();
            using (var frame = editor.Viewport.CaptureFrame(8)) { }
            var point = new Point(editor.Viewport.Width / 2, editor.Viewport.Height / 2);
            Assert(editor.TryPickSurface(point, out var hit), "Canvas pointer cannot pick the surface.");
            editor.SetMode(ModelEditorMode.Sculpt); editor.SetBrushRadius(2); editor.SetBrushStrength(.15f);
            var initial = editor.BakedVerticesForTest;
            editor.PointerDown(point, MouseButtons.Left);
            var sculpted = editor.BakedVerticesForTest;
            Assert(initial.Where((v, i) => v.Position != sculpted[i].Position).Any(), "Sculpt must update before mouse release.");
            editor.FinishStroke(); editor.Undo();
            Assert(initial.SequenceEqual(editor.BakedVerticesForTest), "Undo did not restore the entire stroke.");
            editor.Redo();
            Assert(sculpted.SequenceEqual(editor.BakedVerticesForTest), "Redo did not restore sculpting.");
            editor.SetMode(ModelEditorMode.Paint); editor.SetPaintColor(new Vector4(.1f, .7f, .9f, 1)); editor.SetBrushStrength(1);
            Assert(editor.BrushAt(hit) > 0, "Paint did not affect vertices.");
            var painted = editor.BakedVerticesForTest;
            Assert(painted.Where((v, i) => v.Color != sculpted[i].Color).Any(), "Paint did not apply the chosen colour.");
            editor.AddPart(ModelPrimitiveKind.Cube); editor.Undo(); editor.Undo();
            Assert(sculpted.SequenceEqual(editor.BakedVerticesForTest), "Undo after a mesh-list edit targeted a stale mesh instance.");
            editor.Redo(); editor.Save();
            using var reopened = new ModelViewerControl(path, project.RootPath);
            Assert(reopened.PreviewAsset.Meshes.Count == 1 && reopened.PreviewAsset.Meshes[0].Vertices.SequenceEqual(painted), "Save/reopen lost painted/sculpted vertices.");
            foreach (int width in new[] { 1440, 1100 })
            {
                host.ClientSize = new Size(width, 920); System.Windows.Forms.Application.DoEvents(); editor.FrameModel();
                Assert(editor.Controls.Find("ModelEditorTools", true).Single().Visible && editor.Controls.Find("ModelEditorInspector", true).Single().Visible, "Editor lost its left tools or right inspector.");
                Assert(editor.Viewport.Width >= 450 && editor.Viewport.Height >= 350, "Editor panels leave insufficient canvas space.");
                using (var ready = editor.Viewport.CaptureFrame(8)) { }
                string name = "model-editor-reset-" + width;
                var metrics = VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, name + ".png"), true);
                ctx.Report.Images.Add(ImageResult.From(name, name + ".png", metrics));
            }
        });
    }
    private static void Assert(bool value, string text) => HeadlessHarness.Assert(value, text);
}
