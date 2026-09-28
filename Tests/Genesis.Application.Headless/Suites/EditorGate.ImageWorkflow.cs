using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Projects.Templates;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Core;
using Genesis.Rendering.Viewport;
using Genesis.Runtime;
using Genesis.Runtime.ECS;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Imaging;
using Genesis.Runtime.Project;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

internal static partial class EditorGate
{
    private static void ImageNoviceWorkflow(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        ProjectSession project = new ProjectService().CreateProject(fixture.Root, "Image Meadow", TwoDShowcaseTemplate.TemplateId);
        ResourceItem image = GateSuite.Flatten(new ResourceService(project).BuildTree()).First(item => item.Kind == ResourceKind.Image && Path.GetFileName(item.FullPath) == "Checkpoint.image.json");
        ImageDocumentSession session = new(ImageDocumentSerializer.LoadAtomic(image.FullPath).Document, image.FullPath, ImageDocumentAccess.Editor);
        using ImageEditorControl editor = new(session, ImageWorkspaceStorage.Load(session));
        using Form host = GateSuite.NewHost(1280, 780); host.Controls.Add(editor); GateSuite.ShowHost(host);
        Dictionary<string, string> created = new(StringComparer.Ordinal);
        HeadlessHarness.Step("Image has a short primary workflow, real origin/usage navigation and one saved Object per playback choice", () =>
        {
            string[] primary = editor.CommandBar.Items.Cast<ToolStripItem>().Where(item => item.Available && item.Alignment != ToolStripItemAlignment.Right)
                .Select(item => item.Text ?? string.Empty).ToArray();
            HeadlessHarness.Assert(primary.SequenceEqual(new[] { "Draw", "Animate", "Rig", "Use in game", "Options" }), "Image retains a crowded or duplicate primary toolbar: " + string.Join(',', primary));
            editor.CommandBar.Items.OfType<ToolStripButton>().Single(item => item.Text == "Use in game").PerformClick();
            FlowLayoutPanel guide = SurfaceControls(editor).OfType<FlowLayoutPanel>().Single(control => control.Name == "ImageUseInGame");
            HeadlessHarness.Assert(guide.Top >= 0 && guide.Parent!.Top >= editor.CommandBar.Parent!.Bottom && editor.CommandBar.Visible,
                "Use in game covered the primary toolbar instead of occupying the authoring workspace.");
            string? opened = null; editor.OpenLinkedResourceRequested += (_, path) => opened = path;
            SurfaceControls(editor).OfType<Button>().Single(button => button.Name == "ImageOpenProperties").PerformClick();
            HeadlessHarness.Assert(opened == image.FullPath, "Image properties did not route to the actual shared resource.");
            ComboBox choice = SurfaceControls(editor).OfType<ComboBox>().Single(control => control.Name == "ImageGameplayPlayback");
            TextBox name = SurfaceControls(editor).OfType<TextBox>().Single(control => control.Name == "ImageObjectName");
            Button create = SurfaceControls(editor).OfType<Button>().Single(control => control.Name == "ImageCreateObject");
            foreach (string mode in choice.Items.Cast<string>())
            {
                choice.SelectedItem = mode; name.Text = "Image example " + created.Count; create.PerformClick();
                HeadlessHarness.Assert(opened is not null && opened.EndsWith(".object.json", StringComparison.Ordinal) && File.Exists(opened), "The actual button failed to create/open " + mode);
                var events = ObjectEventStore.Load(opened!);
                HeadlessHarness.Assert(events.Count == 1 && events.ContainsKey("Create"), "Image created no executable Create event for " + mode);
                created.Add(mode, opened!);
                byte[] before = File.ReadAllBytes(opened!); create.PerformClick();
                HeadlessHarness.Assert(File.ReadAllBytes(opened!).SequenceEqual(before)
                    && SurfaceControls(editor).OfType<Label>().Single(label => label.Name == "ImageCreateResult").Text.Contains("already", StringComparison.OrdinalIgnoreCase), "A repeated click replaced the existing sprite Object.");
            }
            HeadlessHarness.Assert(created.Count == 4 && !editor.IsDirty, "The populated Image did not offer still, frame, clip and rig playback.");
            string capture = "image-use-in-game.png";
            ctx.Report.Images.Add(ImageResult.From("Image Use in game", capture, VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, capture))));
        });
        HeadlessHarness.Step("created Image Objects execute their chosen animation in gameplay and render changing rig pixels on all five backends", () =>
        {
            using Form gameHost = GateSuite.NewHost(640, 360);
            using D3DViewportControl viewport = new() { Dock = DockStyle.Fill, DriveMode = ViewportDriveMode.External, VSync = false };
            gameHost.Controls.Add(viewport); GateSuite.ShowHost(gameHost); VMEngine.Initialize();
            foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
            {
                viewport.BackendOverride = backend.Backend; GateSuite.Pump(3, 15);
                using (Bitmap? warm = viewport.ReadbackFrameToBitmap(3)) { }
                IRenderController renderer = viewport.Renderer ?? throw new InvalidOperationException("No Image gameplay renderer.");
                string expected = backend.Backend switch { RenderBackendOption.OpenGL => "OpenGL", RenderBackendOption.Software => "Software", _ => backend.DisplayName };
                HeadlessHarness.Assert(renderer.BackendName == expected, "Image Objects rendered on another backend.");
                RoomAsset room = RoomAsset.Create("Image workflow Room", RoomDimension.TwoD); room.Settings.Width = 640; room.Settings.Height = 360;
                int index = 0;
                foreach ((string mode, string path) in created)
                    room.Nodes.Add(new RoomNode { Name = mode, Kind = RoomNodeKind.GameObject,
                        Transform = new() { X = 110 + index++ * 130, Y = 240, ScaleX = 3, ScaleY = 3 }, GameObject = new() { Prefab = ResourceNames.Name(project.RootPath, path) } });
                using RuntimeScene scene = new("Image gameplay proof");
                ProjectGameContext game = new(project.RootPath, scene, renderer, null, room, null);
                ScriptHostSystem scripts = new(); scripts.SetContext(game);
                IGameContext? oldGame = PgslCommands.ActiveGameContext; string oldPath = PgslCommands.ProjectPath;
                PgslContext? oldContext = PgslCommands.BindContext(new PgslContext());
                PgslCommands.ActiveGameContext = game; PgslCommands.ProjectPath = project.RootPath;
                RoomBuildResult? built = null;
                try
                {
                    built = new RoomSceneBuilder(project.RootPath, scripts).Build(scene, room);
                    Entity For(string mode) => built.EntitiesByNodeId[room.Nodes.Single(node => node.Name == mode).Id];
                    HeadlessHarness.Assert(scene.World.GetRef<SpriteComponent>(For("Still image")).ImageSpeed == 0
                        && scene.World.GetRef<SpriteComponent>(For("All frames")).ImageSpeed == 1
                        && scene.World.GetRef<SpriteComponent>(For(created.Keys.Single(mode => mode.StartsWith("Clip: ", StringComparison.Ordinal)))).AnimationTagIndex >= 0,
                        "The real Create events did not set still, frame and clip playback.");
                    Entity rigEntity = For(created.Keys.Single(mode => mode.StartsWith("Rig: ", StringComparison.Ordinal)));
                    PixelRigSprite rig = scene.World.GetRef<PixelRigSpriteComponent>(rigEntity).Binding ?? throw new InvalidOperationException("The created Object did not bind its saved Image rig.");
                    HeadlessHarness.Assert(rig.Player.Playing && scripts.RecentDiagnostics.Count == 0, "The actual Image rig Create event did not play: " + string.Join(';', scripts.RecentDiagnostics));
                    byte[] initial = rig.Player.GetPixels().ToArray();
                    using Bitmap before = Draw("first");
                    for (int step = 0; step < 12; step++)
                    {
                        scripts.Update(1f / 60);
                        foreach (Entity entity in built.SpawnedEntities) ComponentLifecycle.OnUpdate(scene.World, entity, 1f / 60);
                    }
                    using Bitmap after = Draw("animated");
                    HeadlessHarness.Assert(!initial.SequenceEqual(rig.Player.GetPixels().ToArray()), "Rig playback advanced without changing the authored pose pixels.");
                    int visible = 0, changed = 0;
                    for (int y = 70; y < 260; y++) for (int x = 440; x < 570; x++)
                    {
                        Color a = before.GetPixel(x, y), b = after.GetPixel(x, y);
                        if (a.R > 30 || a.G > 30 || a.B > 35) visible++;
                        if (a.ToArgb() != b.ToArgb()) changed++;
                    }
                    HeadlessHarness.Assert(visible > 100 && changed > 15 && scripts.RecentDiagnostics.Count == 0,
                        "Created rig Object has no visible animated output on " + expected + ": visible=" + visible + "; changed=" + changed);

                    Bitmap Draw(string phase)
                    {
                        renderer.BeginFrame(); renderer.Set3DFrameActive(false); renderer.SetViewport(0, 0, renderer.PixelWidth, renderer.PixelHeight);
                        renderer.SetCamera2D(320, 180, 1, 0); renderer.Clear(.02f, .03f, .05f, 1);
                        FrameRenderQueue queue = new();
                        foreach (Entity entity in built.SpawnedEntities) ObjectDrawPass.EnqueueEntity2D(scene.World, entity, project.RootPath, renderer, 0, 0, 1, queue);
                        queue.Flush(renderer, includeMeshes: false); renderer.EndFrame();
                        HeadlessHarness.Assert(renderer.TryReadSubmittedFramePixels(out int width, out int height, out byte[] pixels), "No physical Image gameplay frame.");
                        Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
                        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                        for (int row = 0; row < height; row++) Marshal.Copy(pixels, row * width * 4, data.Scan0 + row * data.Stride, width * 4);
                        bitmap.UnlockBits(data);
                        string capture = "image-created-objects-" + backend.ShortName.ToLowerInvariant() + "-" + phase + ".png";
                        bitmap.Save(Path.Combine(ctx.Captures, capture)); ctx.Report.Images.Add(ImageResult.From("Created Image Objects · " + expected + " · " + phase, capture, VisualCapture.Measure(bitmap)));
                        return bitmap;
                    }
                }
                finally
                {
                    scripts.Shutdown();
                    if (built is not null) foreach (Entity entity in built.SpawnedEntities) ObjectDrawAssetRegistry.Remove(entity);
                    PgslCommands.BindContext(oldContext); PgslCommands.ActiveGameContext = oldGame; PgslCommands.ProjectPath = oldPath;
                }
            }
        });
    }
}
