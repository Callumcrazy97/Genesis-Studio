using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Assets.Painting;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>Texture painting on the model in the Model editor's edit mode (Texture page, Paint).</summary>
internal static class ModelLiveTexturePaintSuite
{
    public static void Run(HeadlessContext ctx)
    {
        var project = HeadlessHarness.Require(ctx.Project, "Project");
        var resources = HeadlessHarness.Require(ctx.Resources, "Resources");
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.Model.LivePaint");
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.LivePaint.CubeStrokeShowsLiveUndoRedoSaveReopen", () =>
        {
            string model = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Live paint cube");
            using (var setup = new ModelEditorControl(model, project.RootPath)) { setup.AddPart(ModelPrimitiveKind.Cube); setup.Save(); }
            using var host = UnattendedWindowing.NewHost(1440, 920);
            var editor = new ModelEditorControl(model, project.RootPath);
            host.Controls.Add(editor); ThemeService.Apply(host);
            UnattendedWindowing.ShowWithoutFocus(host); System.Windows.Forms.Application.DoEvents();
            editor.ShowToolPageForTest(ModelToolPage.Texture);
            editor.SetCameraView("Front"); editor.FrameModel();
            using (editor.Viewport.CaptureFrame(8)) { }
            Button paint = (Button)editor.Controls.Find("ModelTexturePaintTool", true).Single();
            paint.PerformClick();
            Check(editor.Mode == ModelEditorMode.TexturePaint && editor.ActiveTool == ModelAuthoringTool.TexturePaint, "The Paint button did not choose texture painting.");
            editor.TexturePaintNewTextureSize = 512;
            editor.SetTexturePaintBrush(Color.FromArgb(255, 20, 40), 96, 1f, 1f);
            int material = editor.PreviewAsset.Meshes[0].MaterialIndex;
            Check(string.IsNullOrEmpty(editor.PreviewAsset.Materials[material].AlbedoTexture), "The new cube already had an Image.");
            int before = RedPixels(editor, ctx, "model-live-paint-before");

            Point centre = new(editor.Viewport.Width / 2, editor.Viewport.Height / 2);
            editor.PointerDown(centre, MouseButtons.Left);
            editor.PointerMove(new Point(centre.X + 40, centre.Y + 10), MouseButtons.Left);
            editor.PointerUp(new Point(centre.X + 40, centre.Y + 10), MouseButtons.Left);
            ModelPaintTexture texture = editor.PaintTextureForMaterial(material)
                ?? throw new InvalidOperationException("Painting a material without an Image did not make one.");
            Check(texture.Width == 512 && texture.Height == 512, $"The new Image is {texture.Width} x {texture.Height}, not the chosen 512.");
            Check(editor.PreviewAsset.Materials[material].BaseColor.X == 1f, "The material's own colour was not baked into the new Image.");
            string named = editor.PreviewAsset.Materials[material].AlbedoTexture;
            Check(!named.Contains('/') && ProjectAssetIndex.ResolveSpriteImage(project.RootPath, named) is { } pixelsFile && File.Exists(pixelsFile),
                $"The material does not name the new Image as a resource the Inspector can show ({named}).");
            byte[] painted = (byte[])texture.Layer.Pixels.Clone();
            Check(!painted.SequenceEqual(texture.Original), "The stroke did not change the Image's pixels.");
            Check(editor.LastEditLabel == "Paint on model", $"The stroke is not one undo entry (last entry: {editor.LastEditLabel}).");
            int during = RedPixels(editor, ctx, "model-live-paint-stroke");
            Check(during > before + 800, $"The unsaved stroke did not show in the viewport ({before} red pixels before, {during} after).");
            var form = VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, "model-live-paint-editor.png"), true);
            ctx.Report.Images.Add(ImageResult.From("model-live-paint-editor", "model-live-paint-editor.png", form));

            editor.Undo();
            Check(texture.Layer.Pixels.SequenceEqual(texture.Original), "Undo did not take the whole stroke back.");
            int undone = RedPixels(editor, ctx, "model-live-paint-undone");
            Check(undone < before + 200, $"The viewport still shows the stroke after undo ({undone} red pixels).");
            editor.Redo();
            Check(texture.Layer.Pixels.SequenceEqual(painted), "Redo did not restore the stroke.");
            Check(RedPixels(editor, ctx, "model-live-paint-redone") > before + 800, "The viewport did not show the stroke again after redo.");

            editor.Save();
            Check(!texture.IsUnsaved && !editor.IsDirty, "Save left the painted Image or the model unsaved.");
            string reference = editor.PreviewAsset.Materials[material].AlbedoTexture;
            host.Controls.Remove(editor); editor.Dispose();

            using var viewerHost = UnattendedWindowing.NewHost(1200, 820);
            var viewer = new ModelViewerControl(model, project.RootPath);
            viewerHost.Controls.Add(viewer); ThemeService.Apply(viewerHost);
            UnattendedWindowing.ShowWithoutFocus(viewerHost); System.Windows.Forms.Application.DoEvents();
            Check(viewer.PreviewAsset.Materials[material].AlbedoTexture == reference, "The reopened model lost its painted Image.");
            ImageDocumentSession session = new(ImageDocumentSerializer.LoadAtomic(texture.ImagePath).Document, texture.ImagePath, ImageDocumentAccess.Editor);
            Check(ImageWorkspaceStorage.Load(session).Frames[0].Layers[^1].Pixels.SequenceEqual(painted), "The saved Image does not hold the stroke.");
            viewer.SetCameraView("Front"); viewer.FrameModel();
            using (viewer.Viewport.CaptureFrame(8)) { }
            using Bitmap reopened = viewer.Viewport.CaptureFrame(4) ?? throw new InvalidOperationException("No reopened model frame.");
            Save(ctx, reopened, "model-live-paint-reopened");
            int shown = CountRed(reopened);
            Check(shown > 800, $"The reopened model does not show the saved stroke ({shown} red pixels).");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.LivePaint.EachTriangleWritesItsMaterialsImageAndEraserRestores", () =>
        {
            string model = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Live paint two materials");
            using var editor = new ModelEditorControl(model, project.RootPath);
            editor.AddPart(ModelPrimitiveKind.Cube);
            editor.AddPart(ModelPrimitiveKind.Sphere);
            editor.TransformSelectedMesh(new Vector3(3, 0, 0), Vector3.Zero, Vector3.One);
            int cubeMaterial = editor.PreviewAsset.Meshes[0].MaterialIndex, sphereMaterial = editor.PreviewAsset.Meshes[1].MaterialIndex;
            Check(cubeMaterial != sphereMaterial, "The two parts share one material.");
            editor.SelectTool(ModelAuthoringTool.TexturePaint);
            editor.TexturePaintNewTextureSize = 512;
            editor.SetTexturePaintBrush(Color.Lime, 24, 1f, 1f);
            Vector3 pivot = editor.PreviewAsset.Pivot.Position;
            (Vector3 cubeRay, Vector3 sphereRay) = (Front(editor.PreviewAsset.Meshes[0]) - pivot, Front(editor.PreviewAsset.Meshes[1]) - pivot);

            Check(editor.PaintTextureRay(cubeRay, -Vector3.UnitZ), "The cube could not be painted."); editor.FinishStroke();
            ModelPaintTexture cube = editor.PaintTextureForMaterial(cubeMaterial) ?? throw new InvalidOperationException("No Image for the cube's material.");
            byte[] cubePainted = (byte[])cube.Layer.Pixels.Clone();
            Check(editor.PaintTextureForMaterial(sphereMaterial) is null, "Painting the cube touched the sphere's material.");
            Check(editor.PaintTextureRay(sphereRay, -Vector3.UnitZ), "The sphere could not be painted."); editor.FinishStroke();
            ModelPaintTexture sphere = editor.PaintTextureForMaterial(sphereMaterial) ?? throw new InvalidOperationException("No Image for the sphere's material.");
            Check(!ReferenceEquals(cube, sphere) && cube.ImagePath != sphere.ImagePath, "Two materials were painted into one Image.");
            Check(!sphere.Layer.Pixels.SequenceEqual(sphere.Original) && cube.Layer.Pixels.SequenceEqual(cubePainted),
                "The sphere's stroke did not go only into the sphere's Image.");

            editor.SetTexturePaintEraser(true);
            Check(editor.PaintTextureRay(cubeRay, -Vector3.UnitZ), "The eraser could not reach the cube."); editor.FinishStroke();
            Check(cube.Layer.Pixels.SequenceEqual(cube.Original), "The eraser did not restore the cube's original pixels.");
            editor.Undo();
            Check(cube.Layer.Pixels.SequenceEqual(cubePainted), "Undoing the eraser did not bring the stroke back.");
            editor.SetTexturePaintEraser(false);

            // One part, two materials: the triangle under the brush decides which Image is painted.
            GModelMesh mesh = editor.PreviewAsset.Meshes[0];
            Check(ModelPaintPicker.TryPick(editor.PreviewAsset, index => index == 0 ? mesh.Vertices : null, cubeRay + pivot, -Vector3.UnitZ, out ModelPaintHit hit),
                "The picker missed the cube.");
            mesh.TriangleMaterialIndices = Enumerable.Range(0, mesh.Indices.Length / 3).Select(triangle => triangle == hit.Triangle ? sphereMaterial : cubeMaterial).ToArray();
            Check(ModelPaintPicker.MaterialOf(mesh, hit.Triangle) == sphereMaterial && ModelPaintPicker.MaterialOf(mesh, (hit.Triangle + 1) % (mesh.Indices.Length / 3)) == cubeMaterial,
                "The picker ignored per-face materials.");
            byte[] sphereBefore = (byte[])sphere.Layer.Pixels.Clone();
            editor.SetTexturePaintBrush(Color.Magenta, 8, 1f, 1f);
            Check(editor.PaintTextureRay(cubeRay, -Vector3.UnitZ), "The per-face triangle could not be painted."); editor.FinishStroke();
            Check(!sphere.Layer.Pixels.SequenceEqual(sphereBefore) && cube.Layer.Pixels.SequenceEqual(cubePainted),
                "A triangle with its own material was painted into its part's Image instead of its own.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.LivePaint.NoUvModelSaysSoAndViewerHasNoPaint", () =>
        {
            string model = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Live paint no UVs");
            using var host = UnattendedWindowing.NewHost(1440, 920);
            var editor = new ModelEditorControl(model, project.RootPath);
            host.Controls.Add(editor); ThemeService.Apply(host);
            UnattendedWindowing.ShowWithoutFocus(host); System.Windows.Forms.Application.DoEvents();
            MeshVertex Corner(float x, float y) => new() { Position = new Vector3(x, y, 0), Normal = Vector3.UnitZ, Color = Vector4.One };
            editor.AddImageGeometry(new GModelAsset
            {
                Materials = [new GModelMaterial { Name = "Card", BaseColor = Vector4.One }],
                Meshes = [new GModelMesh { Name = "Flat card", MaterialIndex = 0, Indices = [0, 1, 2, 0, 2, 3], Vertices = [Corner(-1, -1), Corner(1, -1), Corner(1, 1), Corner(-1, 1)] }],
            });
            editor.ShowToolPageForTest(ModelToolPage.Texture);
            string? lastEdit = editor.LastEditLabel;
            ((Button)editor.Controls.Find("ModelTexturePaintTool", true).Single()).PerformClick();
            Check(editor.TexturePaintMessage.Contains("no UVs", StringComparison.Ordinal), $"Choosing Paint on a model without UVs did not say so: {editor.TexturePaintMessage}");
            int material = editor.PreviewAsset.Meshes[0].MaterialIndex;
            Check(!editor.PaintTextureRay(new Vector3(0, 0, 5) - editor.PreviewAsset.Pivot.Position, -Vector3.UnitZ), "A model without UVs was painted.");
            editor.FinishStroke();
            Label message = (Label)editor.Controls.Find("ModelTexturePaintMessage", true).Single();
            Check(message.Text.Contains("“Flat card” has no UVs", StringComparison.Ordinal) && message.Visible,
                $"The editor does not show why the part cannot be painted: {message.Text}");
            Check(editor.PaintTextureForMaterial(material) is null && editor.LastEditLabel == lastEdit && string.IsNullOrEmpty(editor.PreviewAsset.Materials[material].AlbedoTexture),
                "Refusing a model without UVs still made an Image or an undo entry.");
            Check(message.Text.Contains("Material & PBR", StringComparison.Ordinal) && !message.UseMnemonic, "The message hides the '&' of Material & PBR.");
            editor.SetCameraView("Front"); editor.FrameModel();
            using (editor.Viewport.CaptureFrame(8)) { }
            var form = VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, "model-live-paint-no-uv.png"), true);
            ctx.Report.Images.Add(ImageResult.From("model-live-paint-no-uv", "model-live-paint-no-uv.png", form));
            editor.Save();
            using var viewerRole = new ModelEditorControl(model, project.RootPath, ModelEditorRole.Viewer);
            using var viewer = new ModelViewerControl(model, project.RootPath);
            Check(viewerRole.Controls.Find("ModelTexturePaintTool", true).Length == 0 && viewer.Controls.Find("ModelTexturePaintTool", true).Length == 0,
                "The Viewer offers texture painting outside the editor.");
        });
    }

    /// <summary>A point in front of a part's centre, on the +Z side, for a ray straight back at it.</summary>
    private static Vector3 Front(GModelMesh mesh)
    {
        Vector3 min = mesh.Vertices.Select(vertex => vertex.Position).Aggregate(Vector3.Min);
        Vector3 max = mesh.Vertices.Select(vertex => vertex.Position).Aggregate(Vector3.Max);
        Vector3 centre = (min + max) * .5f;
        return new Vector3(centre.X + (max.X - min.X) * .1f, centre.Y + (max.Y - min.Y) * .1f, max.Z + 5f);
    }

    /// <summary>The viewport's red pixels, with the brush outline moved off the model first.</summary>
    private static int RedPixels(ModelEditorControl editor, HeadlessContext ctx, string name)
    {
        editor.PointerMove(new Point(2, 2), MouseButtons.None);
        using Bitmap frame = editor.Viewport.CaptureFrame(4) ?? throw new InvalidOperationException("No model editor frame.");
        Save(ctx, frame, name);
        return CountRed(frame);
    }

    private static void Save(HeadlessContext ctx, Bitmap frame, string name)
    {
        string file = Path.Combine(ctx.Captures, name + ".png");
        frame.Save(file);
        ctx.Report.Images.Add(ImageResult.From(name, file, VisualCapture.Measure(frame)));
    }

    private static int CountRed(Bitmap frame)
    {
        BitmapData data = frame.LockBits(new Rectangle(0, 0, frame.Width, frame.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] pixels = new byte[data.Stride * frame.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            int count = 0;
            for (int y = 0; y < frame.Height; y++)
                for (int x = 0; x < frame.Width; x++)
                {
                    int index = y * data.Stride + x * 4; // BGRA
                    byte blue = pixels[index], green = pixels[index + 1], red = pixels[index + 2];
                    if (red > 120 && red > green * 2 && red > blue * 2) count++;
                }
            return count;
        }
        finally { frame.UnlockBits(data); }
    }

    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
