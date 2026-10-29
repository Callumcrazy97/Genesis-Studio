using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;
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
                Assert(editor.Controls.Find("ModelEditorModeShell", true).Single().Visible
                    && !editor.Controls.Find("ModelEditorInspector", true).Single().Visible,
                    "The default Editor must retain its tool rail and leave the optional details panel closed.");
                var options = editor.Controls.OfType<System.Windows.Forms.TableLayoutPanel>().SelectMany(root => root.Controls.OfType<EditorCommandBar>())
                    .Single().Items.OfType<ToolStripDropDownButton>().Single(item => item.Text == "Options");
                ToolStripMenuItem details = options.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == "Model details panel");
                details.PerformClick();
                Assert(editor.Controls.Find("ModelEditorInspector", true).Single().Visible, "The retained details panel did not open from Options.");
                details.PerformClick();
                Assert(editor.Viewport.Width >= 450 && editor.Viewport.Height >= 350, "Editor panels leave insufficient canvas space.");
                using (var ready = editor.Viewport.CaptureFrame(8)) { }
                string name = "model-editor-reset-" + width;
                var metrics = VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, name + ".png"), true);
                ctx.Report.Images.Add(ImageResult.From(name, name + ".png", metrics));
            }
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Reset.HiddenMaterialStopsDrawingAndPersists", () =>
        {
            string path = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Hidden material");
            using var host = UnattendedWindowing.NewHost(1440, 920);
            var editor = new ModelEditorControl(path, project.RootPath);
            host.Controls.Add(editor); ThemeService.Apply(host);
            UnattendedWindowing.ShowWithoutFocus(host); System.Windows.Forms.Application.DoEvents();
            editor.AddPart(ModelPrimitiveKind.Cube);
            editor.AddPart(ModelPrimitiveKind.Sphere);
            editor.TransformSelectedMesh(new Vector3(2, 0, 0), Vector3.Zero, Vector3.One);
            int cube = editor.PreviewAsset.Meshes[0].MaterialIndex, sphere = editor.PreviewAsset.Meshes[1].MaterialIndex;
            string cubeName = editor.PreviewAsset.Materials[cube].Name;
            editor.ShowToolPageForTest(ModelToolPage.Texture);
            editor.SetCameraView("Front"); editor.FrameModel();
            int shown = GreyPixels(editor, ctx, "model-hidden-material-before");
            var list = (CheckedListBox)editor.Controls.Find("ModelHiddenMaterials", true).Single();
            Assert(list.Items.Count == editor.MaterialCount && !list.GetItemChecked(cube), "The Texture page does not list every material, shown.");
            list.SetItemChecked(cube, true); System.Windows.Forms.Application.DoEvents();
            Assert(editor.PreviewAsset.Materials[cube].Hidden && !editor.PreviewAsset.Materials[sphere].Hidden, "Ticking a material did not hide it alone.");
            int hidden = GreyPixels(editor, ctx, "model-hidden-material-hidden");
            Assert(hidden < shown * .7 && hidden > shown * .15, $"The preview did not stop drawing only the hidden material ({shown} model pixels before, {hidden} after).");
            editor.RenameSelectedGroup("Ball");
            Assert(editor.PreviewAsset.Materials[cube].Hidden, "An unrelated edit showed the hidden material again.");
            editor.Save();
            string descriptor = File.ReadAllText(path);
            Assert(descriptor.Contains("\"hiddenMaterials\"", StringComparison.Ordinal) && descriptor.Contains(cubeName, StringComparison.Ordinal),
                "Save did not keep the hidden material in the Model resource.");
            host.Controls.Remove(editor); editor.Dispose();
            using (var viewer = new ModelViewerControl(path, project.RootPath))
                Assert(viewer.PreviewAsset.Materials[cube].Hidden && !viewer.PreviewAsset.Materials[sphere].Hidden, "Reopening lost the hidden material.");
            using (var reopened = new ModelEditorControl(path, project.RootPath))
            {
                Assert(((CheckedListBox)reopened.Controls.Find("ModelHiddenMaterials", true).Single()).GetItemChecked(cube), "The reopened editor does not tick the hidden material.");
                reopened.SetMaterialHidden(cube, false); reopened.Save();
            }
            using var shownAgain = new ModelViewerControl(path, project.RootPath);
            Assert(!File.ReadAllText(path).Contains("hiddenMaterials", StringComparison.OrdinalIgnoreCase)
                && !shownAgain.PreviewAsset.Materials[cube].Hidden, "Showing the material again did not save.");
        });
    }
    private static int GreyPixels(ModelEditorControl editor, HeadlessContext ctx, string name)
    {
        using (editor.Viewport.CaptureFrame(6)) { }
        using Bitmap frame = editor.Viewport.CaptureFrame(3) ?? throw new InvalidOperationException("No model editor frame.");
        string file = Path.Combine(ctx.Captures, name + ".png");
        frame.Save(file);
        ctx.Report.Images.Add(ImageResult.From(name, file, VisualCapture.Measure(frame)));
        int count = 0;
        for (int y = 0; y < frame.Height; y += 2)
            for (int x = 0; x < frame.Width; x += 2)
            {
                Color pixel = frame.GetPixel(x, y);
                if (pixel.R > 60 && Math.Abs(pixel.R - pixel.G) < 14 && Math.Abs(pixel.G - pixel.B) < 14) count++;
            }
        return count;
    }
    private static void Assert(bool value, string text) => HeadlessHarness.Assert(value, text);
}
