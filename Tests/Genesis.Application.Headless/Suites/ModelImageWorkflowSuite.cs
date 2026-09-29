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
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

internal static class ModelImageWorkflowSuite
{
    public static void Run(HeadlessContext ctx)
    {
        var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "ImageToModel"), "Image to Model");
        ResourceService resources = new(project);
        string image = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, "Pixel arch");
        WriteImage(image, Color.Goldenrod);
        string model = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Pixel arch Model");
        string? objectPath = null;
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Image.PreviewEditableGeometryPreservesRigAndUndo", () =>
        {
            using ModelEditorControl editor = new(model, project.RootPath);
            GModelAsset original = GModelPrimitiveFactory.CreateCube("Existing rig", .3f);
            original.Rig = new() { Bones = [new() { Name = "Root" }], InverseBindMatrices = [Matrix4x4.Identity] };
            original.Meshes[0].IsSkinned = true;
            original.Meshes[0].SkinnedVertices = original.Meshes[0].Vertices.Select(v => new SkinnedMeshVertex
            { Position = v.Position, Normal = v.Normal, Color = v.Color, UV = v.UV, JointWeights = new(1, 0, 0, 0) }).ToArray();
            original.Animations.Add(new() { Name = "Existing clip", Frames = [new() { LocalBoneTransforms = [Matrix4x4.CreateRotationY(.3f)] }] });
            editor.ApplyAnimationWorkspace(original, "Existing clip"); editor.Save();
            byte[] beforeFile = File.ReadAllBytes(editor.CanonicalModelPath);
            using ModelImageDialog dialog = editor.CreateImageDialog(image);
            Check(dialog.Result is not null, "The saved Image did not produce a preview.");
            dialog.Configure(2, .25f, 32);
            GModelAsset generated = dialog.Result!;
            Check(Math.Abs(generated.Bounds.Size.X - 2) < .001f && Math.Abs(generated.Bounds.Size.Y - 1) < .001f
                && Math.Abs(generated.Bounds.Size.Z - .25f) < .001f, "Image proportions or depth were lost.");
            GModelMesh mesh = generated.Meshes.Single();
            Check(mesh.Vertices.Length > 100 && mesh.Indices.All(index => index < mesh.Vertices.Length)
                && !ModelSurfaceBrush.Raycast(mesh.Vertices, mesh.Indices, new(0, 0, 3), -Vector3.UnitZ, out _)
                && ModelSurfaceBrush.Raycast(mesh.Vertices, mesh.Indices, new(-.8f, 0, 3), -Vector3.UnitZ, out _),
                "Transparency did not create a real editable hole.");
            Check(File.ReadAllBytes(editor.CanonicalModelPath).SequenceEqual(beforeFile), "Opening or changing the preview saved the Model.");
            editor.AddImageGeometry(generated);
            Check(editor.CanonicalMeshCount == 2 && editor.HasPersistedSkin && editor.AnimationClipCount == 1
                && editor.ActiveClip == "Existing clip", "Creating from Image replaced rigging or animation.");
            editor.Undo(); Check(editor.CanonicalMeshCount == 1 && editor.HasPersistedSkin, "Undo damaged existing model data.");
            editor.Redo(); editor.Save();
            using ModelEditorControl reopened = new(model, project.RootPath);
            Check(reopened.CanonicalMeshCount == 2 && reopened.HasPersistedSkin && reopened.AnimationClipCount == 1
                && reopened.PreviewAsset.Materials[reopened.PreviewAsset.Meshes[1].MaterialIndex].AlbedoTexture == dialog.ImageReference,
                "Save/reopen lost geometry, the linked Image or retained rigging.");
            // The gameplay fixture contains just the generated part, while the preservation check above keeps an existing rig.
            reopened.ApplyAnimationWorkspace(generated, ""); reopened.Save();
            objectPath = reopened.CreateModelObject("Pixel arch Object");
            Check(ObjectEventStore.Load(objectPath)["Create"].Contains("ModelSet", StringComparison.Ordinal),
                "Use in game did not create a real saved Object event.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Image.EmptyImageRejectsCreationAndDenseGeometryKeepsValidIndices", () =>
        {
            string empty = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, "Empty Image");
            WriteImage(empty, Color.Transparent);
            using ModelImageDialog dialog = new(project.RootPath, empty);
            Check(dialog.Result is null && !dialog.Controls.Find("CreateModelFromImage", true).Single().Enabled,
                "An empty Image silently generated placeholder geometry.");
            string dense = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, "Dense pixels");
            ImageDocumentSession session = new(ImageDocumentSerializer.LoadAtomic(dense).Document, dense, ImageDocumentAccess.Editor);
            ImageWorkspace workspace = ImageWorkspace.CreateBlank(64, 64, Color.Transparent);
            byte[] pixels = workspace.Frames[0].Layers[0].Pixels;
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
                if ((x + y) % 2 == 0) { int offset = (y * 64 + x) * 4; pixels[offset] = 255; pixels[offset + 3] = 255; }
            ImageWorkspaceStorage.Save(session, workspace);
            Check(dialog.SelectImage(dense), "Dense Image failed to load."); dialog.Configure(2, .2f, 64);
            GModelMesh mesh = dialog.Result!.Meshes.Single();
            Check(mesh.Vertices.Length == 49_152 && mesh.Indices.All(index => index < mesh.Vertices.Length),
                "The highest detail overflowed mesh indices.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Image.OpacityCutoffControlsGeometryAndPersists", () =>
        {
            string faded = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, "Faint edge");
            ImageDocumentSession session = new(ImageDocumentSerializer.LoadAtomic(faded).Document, faded, ImageDocumentAccess.Editor);
            ImageWorkspace workspace = ImageWorkspace.CreateBlank(2, 1, Color.White);
            workspace.Frames[0].Layers[0].Pixels[3] = 64; ImageWorkspaceStorage.Save(session, workspace);
            using ModelImageDialog dialog = new(project.RootPath, faded);
            dialog.Configure(2, .25f, 2, 48);
            Check(Math.Abs(dialog.Result!.Bounds.Size.X - 2) < .001f, "Low cutoff removed the faint pixel.");
            dialog.Configure(2, .25f, 2, 128);
            Check(Math.Abs(dialog.Result!.Bounds.Size.X - 1) < .001f
                && !ModelSurfaceBrush.Raycast(dialog.Result.Meshes[0].Vertices, dialog.Result.Meshes[0].Indices, new(-.5f, 0, 3), -Vector3.UnitZ, out _),
                "Increasing the cutoff did not remove actual geometry.");
            string target = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Cutoff Model");
            using ModelEditorControl editor = new(target, project.RootPath); editor.AddImageGeometry(dialog.Result); editor.Save();
            using ModelEditorControl reopened = new(target, project.RootPath);
            Check(Math.Abs(reopened.PreviewAsset.Bounds.Size.X - 1) < .001f
                && reopened.PreviewAsset.Meshes[0].Metadata["genesis.imageOpacityCutoff"] == "128", "Saving lost the chosen cutoff or geometry.");
        });
        foreach ((int width, int height, float scale, bool lower) in new[] { (1100, 740, 1f, false), (780, 600, 1f, false), (1100, 740, 2f, false), (1100, 740, 2f, true) })
        {
            HeadlessHarness.RunCase(ctx.Report, $"Editor.Model.Image.Layout.{width}.Scale{scale}" + (lower ? ".GameSteps" : ""), () =>
            {
                GenesisSettings settings = new(); settings.Appearance.InterfaceScale = scale;
                try
                {
                    ThemeService.ApplySettings(settings);
                    using ModelImageDialog dialog = new(project.RootPath, image);
                    dialog.ClientSize = new Size(width, height); ThemeService.Apply(dialog);
                    UnattendedWindowing.ShowWithoutFocus(dialog); System.Windows.Forms.Application.DoEvents(); dialog.ApplyInterfaceLayout(); dialog.FramePreview();
                    using (var frame = dialog.Viewport.CaptureFrame(6)) Check(frame is not null, "No native preview frame.");
                    if (lower) { dialog.ShowGameSteps(); System.Windows.Forms.Application.DoEvents(); }
                    if (lower)
                    {
                        Control steps = dialog.Controls.Find("ModelImageGameSteps", true).Single();
                        Check(dialog.ClientRectangle.Contains(dialog.RectangleToClient(steps.RectangleToScreen(steps.ClientRectangle))),
                            "The final game instructions cannot scroll fully into view.");
                    }
                    Button add = (Button)dialog.Controls.Find("CreateModelFromImage", true).Single();
                    Check(add.Visible && add.Parent!.ClientRectangle.Contains(add.Bounds) && dialog.Viewport.Width >= 350
                        && dialog.Viewport.Height >= 350, "Settings or scaling hid the primary action or crowded out the preview.");
                    string name = $"model-from-image-{width}-scale{scale}" + (lower ? "-game-steps" : "");
                    var metrics = VisualCapture.CaptureOpenForm(dialog, Path.Combine(ctx.Captures, name + ".png"), true);
                    ctx.Report.Images.Add(ImageResult.From(name, name + ".png", metrics));
                }
                finally { settings.Appearance.InterfaceScale = 1; ThemeService.ApplySettings(settings); }
            });
        }
        foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
        {
            HeadlessHarness.RunCase(ctx.Report, "Runtime.Model.Image.SavedObjectAndLiveImage." + backend.ShortName, () =>
            {
                Check(objectPath is not null, "The saved Object workflow failed.");
                WriteImage(image, Color.Goldenrod);
                RoomAsset room = RoomAsset.Create("Image Model Room", RoomDimension.ThreeD);
                room.Nodes.Add(new() { Id = "arch", Name = "Pixel arch", Kind = RoomNodeKind.GameObject, LayerId = room.Layers[0].Id,
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
                    Check(scripts.RecentDiagnostics.Count == 0, "The generated Object's Create event failed.");
                    int Capture(string state, bool changed)
                    {
                        Mesh3DState lighting = Mesh3DState.Default; lighting.LightingEnabled = false; lighting.FogEnabled = false;
                        lighting.ShowFloor = lighting.ShowSunVisual = false;
                        renderer.SetMesh3DState(lighting); renderer.Set3DFrameActive(true);
                        renderer.SetCamera3D(Matrix4x4.CreateLookAt(new(1, .35f, 3.5f), Vector3.Zero, Vector3.UnitY),
                            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 1.6f, .1f, 100));
                        MeshDrawCall[] draws = new MeshDrawCall[32]; models.BeginFrame();
                        int count = models.SubmitWorld(scene.World, project.RootPath, draws, 0, renderer); models.EndFrame();
                        Check(count > 0, "The real saved Object did not submit its Model geometry.");
                        renderer.BeginFrame(); renderer.Clear(.025f, .04f, .07f);
                        for (int index = 0; index < count; index++) { var draw = draws[index]; draw.Flags |= MeshDrawFlags.NoShadow | MeshDrawFlags.NoCull | MeshDrawFlags.NoFog; renderer.DrawMesh(draw); }
                        renderer.EndFrame();
                        Check(renderer.TryReadSubmittedFramePixels(out int w, out int h, out byte[] pixels), "No submitted gameplay pixels.");
                        int colored = 0;
                        for (int index = 0; index < pixels.Length; index += 4)
                            if (changed ? pixels[index] > pixels[index + 2] * 1.4 && pixels[index + 1] > 100
                                : pixels[index + 2] > pixels[index] * 2 && pixels[index + 1] > 100) colored++;
                        using Bitmap bitmap = new(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        var data = bitmap.LockBits(new(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
                        try { System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); } finally { bitmap.UnlockBits(data); }
                        string name = "model-image-game-" + backend.ShortName + "-" + state;
                        string file = Path.Combine(ctx.Captures, name + ".png"); bitmap.Save(file);
                        ctx.Report.Images.Add(ImageResult.From(name, file, VisualCapture.Measure(bitmap))); renderer.Present();
                        return colored;
                    }
                    Check(Capture("saved", false) > 1000, "Saved Image colours are absent in native gameplay.");
                    WriteImage(image, Color.DeepSkyBlue);
                    Check(Capture("live-edit", true) > 1000, "Saving the linked Image did not update actual gameplay pixels.");
                }
                finally { models.InvalidateAssets(renderer); scripts.Shutdown(); PgslCommands.ActiveGameContext = oldGame; PgslCommands.ProjectPath = oldPath; }
            });
        }
    }
    internal static void WriteImage(string file, Color color)
    {
        ImageDocumentSession session = new(ImageDocumentSerializer.LoadAtomic(file).Document, file, ImageDocumentAccess.Editor);
        ImageWorkspace workspace = ImageWorkspace.CreateBlank(32, 16, Color.Transparent);
        byte[] pixels = workspace.Frames[0].Layers[0].Pixels;
        for (int y = 0; y < 16; y++) for (int x = 0; x < 32; x++)
        {
            if (x is >= 12 and < 20 && y is >= 6 and < 10) continue;
            int index = (y * 32 + x) * 4;
            pixels[index] = color.R; pixels[index + 1] = color.G; pixels[index + 2] = color.B; pixels[index + 3] = color.A;
        }
        ImageWorkspaceStorage.Save(session, workspace);
    }
    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
