using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Settings;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Studio.Theme;
using Genesis.Rendering.Core;
using Genesis.Runtime;
using Genesis.Runtime.Assets;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

internal static class ModelTexturePaintingSuite
{
    public static void Run(HeadlessContext ctx)
    {
        var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "ModelImagePainting"), "Model Image painting");
        ResourceService resources = new(project);
        string image = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, "Painted arch");
        ModelImageWorkflowSuite.WriteImage(image, Color.Goldenrod);
        string model = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Painted Model");
        using (ModelEditorControl editor = new(model, project.RootPath))
        {
            using var creation = editor.CreateImageDialog(image);
            editor.AddImageGeometry(creation.Result!); editor.Save();
        }
        string? objectPath = null;
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.TexturePainting.SharedImageHistorySaveReopenAndCancelledStroke", () =>
        {
            using ModelEditorControl editor = new(model, project.RootPath); editor.SelectPart(0);
            using ModelTexturePaintDialog painting = editor.CreateTexturePaintDialog();
            byte[] before = (byte[])painting.Image.Workspace.CurrentLayer!.Pixels.Clone();
            byte[] savedBefore = ImageMaterialAssetLoader.Load(project.RootPath, image).Albedo;
            painting.SetBrush(Color.Fuchsia, 5); painting.BeginStroke();
            Check(painting.PaintRay(new(-.75f, .25f, 3), -Vector3.UnitZ)
                && painting.PaintRay(new(-.55f, .15f, 3), -Vector3.UnitZ), "A real mesh hit could not paint its linked Image UVs.");
            painting.FinishStroke();
            byte[] projected = (byte[])painting.Image.Workspace.CurrentLayer.Pixels.Clone();
            Check(!projected.SequenceEqual(before) && ImageMaterialAssetLoader.Load(project.RootPath, image).Albedo.SequenceEqual(savedBefore),
                "Painting did not edit the draft, or it silently saved the Image.");
            painting.Image.DrawStroke(new(24, 4), new(30, 4), Color.DeepSkyBlue, 3);
            byte[] both = (byte[])painting.Image.Workspace.CurrentLayer.Pixels.Clone();
            Check(painting.Image.Undo() && painting.Image.Workspace.CurrentLayer.Pixels.SequenceEqual(projected), "2D and 3D strokes did not share undo order.");
            Check(painting.Image.Undo() && painting.Image.Workspace.CurrentLayer.Pixels.SequenceEqual(before), "One 3D drag was split into multiple undo commands.");
            Check(painting.Image.Redo() && painting.Image.Redo() && painting.Image.Workspace.CurrentLayer.Pixels.SequenceEqual(both), "Redo lost linked painting.");
            painting.BeginStroke(); painting.PaintRay(new(.65f, -.3f, 3), -Vector3.UnitZ); painting.FinishStroke(cancel: true);
            Check(painting.Image.Workspace.CurrentLayer.Pixels.SequenceEqual(both), "Cancelling a projected stroke kept draft changes.");
            painting.BeginStroke();
            Check(!painting.PaintRay(new(0, 0, 3), -Vector3.UnitZ), "Painting crossed a real hole in the Model."); painting.FinishStroke();
            painting.Image.Workspace.CurrentLayer.Locked = true; painting.BeginStroke();
            Check(!painting.PaintRay(new(-.75f, .25f, 3), -Vector3.UnitZ), "A locked Image layer accepted a Model stroke.");
            painting.Image.Workspace.CurrentLayer.Locked = false;
            painting.SaveImage();
            Check(!painting.Image.IsDirty && ImageMaterialAssetLoader.Load(project.RootPath, image).Albedo.SequenceEqual(both),
                "Save Image did not preserve the shared pixels.");
            ImageDocumentSession reopened = new(ImageDocumentSerializer.LoadAtomic(image).Document, image, ImageDocumentAccess.Editor);
            Check(ImageWorkspaceStorage.Load(reopened).CurrentLayer!.Pixels.SequenceEqual(both), "Reopening in Image lost Model painting.");
            objectPath = editor.CreateModelObject("Painted Model Object");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.TexturePainting.PerFaceMaterialsAndTiledUvs", () =>
        {
            GModelAsset asset = StudioModelResourceLoader.Load(model);
            GModelMesh mesh = asset.Meshes[0]; mesh.TriangleMaterialIndices = Enumerable.Repeat(mesh.MaterialIndex, mesh.Indices.Length / 3).ToArray();
            asset.Materials[mesh.MaterialIndex].Metadata["UvScale"] = "2";
            using ModelTexturePaintDialog painting = new(project.RootPath, asset, mesh.MaterialIndex);
            painting.SetBrush(Color.Lime, 1); painting.BeginStroke();
            Check(painting.PaintRay(new(-.75f, .25f, 3), -Vector3.UnitZ), "An authored per-face material could not be painted.");
            painting.FinishStroke();
            byte[] pixels = painting.Image.Workspace.CurrentLayer!.Pixels;
            int offset = (8 * 32 + 8) * 4;
            Check(pixels[offset] == 0 && pixels[offset + 1] == 255 && pixels[offset + 2] == 0,
                "Projected painting ignored the material UV scale.");
            painting.Image.Undo();
        });
        foreach ((int width, float scale) in new[] { (1440, 1f), (1000, 1f), (1440, 2f) })
            HeadlessHarness.RunCase(ctx.Report, $"Editor.Model.TexturePainting.Layout.{width}.Scale{scale}", () =>
            {
                GenesisSettings settings = new(); settings.Appearance.InterfaceScale = scale;
                try
                {
                    ThemeService.ApplySettings(settings);
                    using ModelEditorControl editor = new(model, project.RootPath); editor.SelectPart(0);
                    using ModelTexturePaintDialog painting = editor.CreateTexturePaintDialog();
                    painting.ClientSize = new Size(width, 900); ThemeService.Apply(painting); UnattendedWindowing.ShowWithoutFocus(painting);
                    System.Windows.Forms.Application.DoEvents(); painting.ApplyInterfaceLayout(); painting.FrameModel(); painting.Image.Canvas.FitToView();
                    using (var frame = painting.Viewport.CaptureFrame(6)) Check(frame is not null, "No native painted Model preview.");
                    painting.SetBrush(Color.Lime, 3); painting.BeginStroke();
                    Vector3 surface = painting.Viewport.WorldToSurface(new(.65f, -.3f, .125f));
                    Check(painting.PaintAt(new((int)surface.X, (int)surface.Y)), "Viewport pointer coordinates did not reach the linked Image.");
                    painting.FinishStroke();
                    using (var frame = painting.Viewport.CaptureFrame(3))
                    {
                        int green = 0;
                        for (int y = 0; y < frame!.Height; y++) for (int x = 0; x < frame.Width; x++)
                        { Color pixel = frame.GetPixel(x, y); if (pixel.G > 100 && pixel.R < 70 && pixel.B < 70) green++; }
                        Check(green > 20, "Unsaved Image pixels did not appear in the live 3D preview.");
                    }
                    Control save = painting.Controls.Find("SaveModelImage", true).Single();
                    Check(save.Visible && save.Parent!.ClientRectangle.Contains(save.Bounds) && painting.Viewport.Width > 350
                        && painting.Image.Canvas.Width > 300 && painting.Image.Canvas.Height > 400 && !painting.Image.IsTimelinePanelVisible
                        && painting.Image.CommandBar.Items.Cast<ToolStripItem>().All(item => item.Text != "Save" || !item.Visible),
                        "The linked workspace duplicates Save or hides the primary painting areas/actions.");
                    string name = $"model-linked-image-{width}-scale{scale}";
                    var metrics = VisualCapture.CaptureOpenForm(painting, Path.Combine(ctx.Captures, name + ".png"), true);
                    ctx.Report.Images.Add(ImageResult.From(name, name + ".png", metrics));
                    painting.Image.Undo();
                }
                finally { settings.Appearance.InterfaceScale = 1; ThemeService.ApplySettings(settings); }
            });
        foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
            HeadlessHarness.RunCase(ctx.Report, "Runtime.Model.TexturePainting.SavedObject." + backend.ShortName, () =>
            {
                Check(objectPath is not null, "The saved painting workflow failed.");
                RoomAsset room = RoomAsset.Create("Painted Image Model Room", RoomDimension.ThreeD);
                room.Nodes.Add(new() { Id = "painted", Name = "Painted Model", Kind = RoomNodeKind.GameObject, LayerId = room.Layers[0].Id,
                    GameObject = new() { Prefab = ResourceNames.Name(project.RootPath, objectPath!) } });
                VMEngine.Initialize(); using RuntimeScene scene = new(); ScriptHostSystem scripts = new();
                ProjectGameContext game = new(project.RootPath, scene, null, null, room, null); scripts.SetContext(game);
                var oldGame = PgslCommands.ActiveGameContext; string oldPath = PgslCommands.ProjectPath;
                using Form host = UnattendedWindowing.NewHost(640, 400); UnattendedWindowing.ShowWithoutFocus(host);
                using IRenderController renderer = RenderControllerFactory.Create(backend.Backend); renderer.Initialize(host.Handle, 640, 400);
                RuntimeModelRenderSystem models = new();
                try
                {
                    PgslCommands.ActiveGameContext = game; PgslCommands.ProjectPath = project.RootPath;
                    ProjectRoomLoader.Build(project.RootPath, scene, room, scripts, game, beginGame: true);
                    scene.AddSubsystem(new ScriptHostSubsystem(scripts)); scene.UpdateFixed(1f / 60); scene.UpdateVariable(1f / 60);
                    Check(scripts.RecentDiagnostics.Count == 0, "The saved painted Model's Object event failed.");
                    Mesh3DState state = Mesh3DState.Default; state.LightingEnabled = state.FogEnabled = state.ShowFloor = state.ShowSunVisual = false;
                    renderer.SetMesh3DState(state); renderer.Set3DFrameActive(true);
                    renderer.SetCamera3D(Matrix4x4.CreateLookAt(new(1, .35f, 3.5f), Vector3.Zero, Vector3.UnitY),
                        Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 1.6f, .1f, 100));
                    MeshDrawCall[] draws = new MeshDrawCall[32]; models.BeginFrame();
                    int count = models.SubmitWorld(scene.World, project.RootPath, draws, 0, renderer); models.EndFrame();
                    Check(count > 0, "The saved Object did not submit painted Model geometry.");
                    renderer.BeginFrame(); renderer.Clear(.025f, .04f, .07f);
                    for (int index = 0; index < count; index++) { var draw = draws[index]; draw.Flags |= MeshDrawFlags.NoShadow | MeshDrawFlags.NoCull | MeshDrawFlags.NoFog; renderer.DrawMesh(draw); }
                    renderer.EndFrame();
                    Check(renderer.TryReadSubmittedFramePixels(out int w, out int h, out byte[] pixels), "No native painted Object frame.");
                    int magenta = 0, blue = 0;
                    for (int index = 0; index < pixels.Length; index += 4)
                    { if (pixels[index] > 100 && pixels[index + 2] > 100 && pixels[index + 1] < 50) magenta++;
                        if (pixels[index] > pixels[index + 2] * 1.4 && pixels[index + 1] > 100) blue++; }
                    using Bitmap bitmap = new(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    var data = bitmap.LockBits(new(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
                    try { System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); } finally { bitmap.UnlockBits(data); }
                    string name = "model-painted-object-" + backend.ShortName;
                    string file = Path.Combine(ctx.Captures, name + ".png"); bitmap.Save(file);
                    ctx.Report.Images.Add(ImageResult.From(name, file, VisualCapture.Measure(bitmap))); renderer.Present();
                    Check(magenta > 50 && blue > 50, $"Gameplay lost projected or ordinary Image strokes ({magenta} magenta, {blue} blue pixels).");
                }
                finally { models.InvalidateAssets(renderer); scripts.Shutdown(); PgslCommands.ActiveGameContext = oldGame; PgslCommands.ProjectPath = oldPath; }
            });
    }
    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
