using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Studio.Theme;
using Genesis.Rendering.Core;
using Genesis.Runtime;
using Genesis.Runtime.ECS;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>Image Editor "Convert to 3D Model": voxel meshing, flipbook clip, saved resource and preview.</summary>
internal static class PixelModelSuite
{
    private static readonly Color Red = Color.FromArgb(255, 220, 40, 40);
    private static readonly Color Blue = Color.FromArgb(255, 40, 90, 230);
    private static readonly Color Green = Color.FromArgb(255, 60, 200, 80);

    public static void Run(HeadlessContext ctx)
    {
        ProjectSession project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "PixelModel"), "Pixel Model");
        ResourceService resources = new(project);

        HeadlessHarness.RunCase(ctx.Report, "Editor.Image.ToModel.TwoColourImageMergesFacesKeepsColoursAndSize", () =>
        {
            PixelModelSource source = Source(4, 4, [Frame("Frame 1", 100, (x, _) => x < 2 ? Red : Blue)]);
            PixelModelSettings settings = new() { Name = "Two colours" };
            GModelAsset asset = PixelModelBuilder.Build(source, settings);
            Vector3 size = asset.Bounds.Size;
            Check(Near(size, new Vector3(.25f, .25f, .0625f)), $"4 px at 0.0625 should be 0.25 × 0.25 × 0.0625 units, got {size}.");
            int triangles = PixelModelBuilder.TriangleCount(asset);
            int naive = PixelModelBuilder.NaiveCubeTriangles(source.Frames[0], 4, 4, settings);
            Check(naive == 192 && triangles == 20, $"Expected 20 merged triangles against 192 naive, got {triangles} against {naive}.");
            GModelMesh mesh = asset.Meshes.Single();
            Check(!mesh.IsSkinned && asset.Rig.Bones.Count == 0 && asset.Animations.Count == 0, "A single frame should be a plain static mesh.");
            Check(mesh.Indices.All(index => index < mesh.Vertices.Length), "Indices point past the vertices.");
            // No face between the two solid halves; every outward side lies on the outside.
            Check(!mesh.Vertices.Any(v => MathF.Abs(v.Normal.X) > .5f && MathF.Abs(v.Position.X) < .01f), "An internal face was kept between solid pixels.");
            // Each front quad's colour matches the half of the image it covers.
            for (int quad = 0; quad < mesh.Vertices.Length; quad += 4)
            {
                if (mesh.Vertices[quad].Normal.Z < .5f) continue;
                float centre = (mesh.Vertices[quad].Position.X + mesh.Vertices[quad + 2].Position.X) / 2f;
                Color expected = centre < 0f ? Red : Blue;
                Check(SameColour(mesh.Vertices[quad].Color, expected), $"Front quad centred at x={centre} has colour {mesh.Vertices[quad].Color}, expected {expected}.");
            }
            Check(mesh.Vertices.Select(v => v.Color).Distinct().Count() == 2, "Only the image's two colours should be used.");

            // A transparent pixel becomes a hole; the threshold decides what is solid.
            PixelModelSource holed = Source(4, 4, [Frame("Frame 1", 100, (x, y) => x == 1 && y == 1 ? Color.FromArgb(100, Red) : Red)]);
            Check(PixelModelBuilder.TriangleCount(PixelModelBuilder.Build(holed, new PixelModelSettings { AlphaThreshold = 128 }))
                > PixelModelBuilder.TriangleCount(PixelModelBuilder.Build(holed, new PixelModelSettings { AlphaThreshold = 50 })),
                "The alpha threshold did not open a hole for the faint pixel.");

            // The gizmo's move, rotation and resize are baked into the saved vertices.
            settings.Scale = new Vector3(2f, 1f, 3f); settings.Position = new Vector3(0f, 1f, 0f); settings.DepthPixels = 2;
            GModelAsset placed = PixelModelBuilder.Build(source, settings);
            Check(Near(placed.Bounds.Size, new Vector3(.5f, .25f, .375f)) && MathF.Abs(placed.Bounds.Min.Y - .875f) < .001f,
                $"Transform or depth was not baked: size {placed.Bounds.Size}, min {placed.Bounds.Min}.");
            settings.Scale = Vector3.One; settings.Position = Vector3.Zero; settings.RotationDegrees = new Vector3(0f, 90f, 0f); settings.DepthPixels = 1;
            Check(Near(PixelModelBuilder.Build(source, settings).Bounds.Size, new Vector3(.0625f, .25f, .25f)), "A 90° turn did not swap width and depth.");

            // Saved as a Model resource the Studio lists, and reopened intact.
            string image = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, "Two colour tile");
            source = Source(4, 4, source.Frames, image);
            string model = PixelModelResource.Create(image, PixelModelBuilder.Build(source, new PixelModelSettings()), "Two colour tile 3D");
            Check(File.Exists(model) && File.Exists(StudioModelResourceLoader.CanonicalPath(model)), "The Model resource or its canonical model was not written.");
            Check(string.Equals(Path.GetFullPath(ResourceNames.Resolve(project.RootPath, "Two colour tile 3D", ResourceType.Model)), Path.GetFullPath(model),
                StringComparison.OrdinalIgnoreCase), "The project does not list the new Model by name.");
            GModelAsset reopened = StudioModelResourceLoader.Load(model);
            Check(!reopened.ImportRequired && PixelModelBuilder.TriangleCount(reopened) == 20 && Near(reopened.Bounds.Size, new Vector3(.25f, .25f, .0625f))
                && reopened.Meshes[0].Vertices.Select(v => v.Color).Distinct().Count() == 2, "Save and reopen lost geometry or colours.");
            bool duplicate = false;
            try { PixelModelResource.Create(image, PixelModelBuilder.Build(source, new PixelModelSettings()), "Two colour tile 3D"); }
            catch (InvalidOperationException) { duplicate = true; }
            Check(duplicate, "A second Model with the same name silently replaced or shadowed the first.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Image.ToModel.ThreeFramesPlayAsFlipbookThroughAnimators", () =>
        {
            PixelModelSource source = Source(4, 4,
            [
                Frame("Frame 1", 100, (x, y) => x == 0 && y == 0 ? Red : Color.Transparent),
                Frame("Frame 2", 200, (_, _) => Green),
                Frame("Frame 3", 100, (x, y) => x == 3 && y == 3 ? Blue : Color.Transparent),
            ]);
            GModelAsset asset = PixelModelBuilder.Build(source, new PixelModelSettings { AllFrames = true });
            Check(asset.Rig.Bones.Count == 4 && asset.Rig.Bones.Skip(1).All(bone => bone.ParentIndex == 0)
                && asset.Rig.InverseBindMatrices.Length == 4, "Expected a root bone and one bone per frame.");
            Check(asset.Meshes.Count == 3 && asset.Meshes.Select((mesh, index) => mesh.IsSkinned
                && mesh.SkinnedVertices.Length == mesh.Vertices.Length
                && mesh.SkinnedVertices.All(v => (int)v.JointIndices.X == index + 1 && v.JointWeights == new Vector4(1, 0, 0, 0))).All(ok => ok),
                "Each frame's mesh should be fully skinned to its own bone.");
            GModelAnimationClip clip = asset.Animations.Single();
            Check(clip.Name == "Frames" && clip.Loop && clip.Fps == 60f && clip.Frames.Count == 24,
                $"Clip should be a looping 60 fps 'Frames' clip of 6+12+6 frames, got {clip.Name} {clip.Fps} fps × {clip.Frames.Count}.");
            Check(VisibleFrames(asset, GModelPrimitiveFactory.EvaluateBindPosePalette(asset.Rig)).SequenceEqual([0]),
                "The bind pose should show only the first frame.");

            (float Time, int Frame)[] expectations = [(0.05f, 0), (0.15f, 1), (0.28f, 1), (0.35f, 2), (0.45f, 0), (0.62f, 1)];
            foreach ((float time, int expected) in expectations)
            {
                // The renderer's path: a ModelAnimatorComponent's state, at its default clip rate.
                ModelAnimatorComponent animator = new() { ClipName = "Frames", TimeSeconds = time, ClipFps = 0f, Loop = true, Playing = true, PlaybackSpeed = 1f };
                Matrix4x4[] locals = GModelPrimitiveFactory.EvaluateAnimatedLocals(asset, RuntimeModelAnimationState.From(animator, false));
                int[] shown = VisibleFrames(asset, GModelPrimitiveFactory.EvaluateSkinPalette(asset.Rig, locals));
                Check(shown.SequenceEqual([expected]), $"Animator at {time:0.00}s showed frames [{string.Join(",", shown)}], expected {expected}.");

                // The animation graph's path: an AnimationController advanced by simulation.
                AnimationController controller = new();
                controller.Layers[0].StateMachine.AddState("Flipbook", new ClipNode("Frames"));
                controller.Advance(time, 1f, asset);
                shown = VisibleFrames(asset, GModelPrimitiveFactory.EvaluateSkinPalette(asset.Rig, controller.Evaluate(asset)));
                Check(shown.SequenceEqual([expected]), $"AnimationController at {time:0.00}s showed frames [{string.Join(",", shown)}], expected {expected}.");
            }

            string image = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, "Three frames");
            string model = PixelModelResource.Create(image, PixelModelBuilder.Build(Source(4, 4, source.Frames, image), new PixelModelSettings { AllFrames = true }), "Three frames 3D");
            GModelAsset reopened = StudioModelResourceLoader.Load(model);
            ModelAnimatorComponent saved = new() { ClipName = "Frames", TimeSeconds = .35f, Loop = true, Playing = true, PlaybackSpeed = 1f };
            Check(reopened.Animations.Count == 1 && reopened.Rig.Bones.Count == 4
                && VisibleFrames(reopened, GModelPrimitiveFactory.EvaluateSkinPalette(reopened.Rig,
                    GModelPrimitiveFactory.EvaluateAnimatedLocals(reopened, RuntimeModelAnimationState.From(saved, false)))).SequenceEqual([2]),
                "Save and reopen lost the flipbook rig or clip.");

            // The Model Editor opens it as a skinned, animated model and saves it without breaking the flipbook.
            using (ModelEditorControl modelEditor = new(model, project.RootPath))
            {
                Check(modelEditor.AnimationClipCount == 1 && modelEditor.CanonicalMeshCount == 3 && modelEditor.HasPersistedSkin,
                    $"The Model Editor opened {modelEditor.CanonicalMeshCount} meshes and {modelEditor.AnimationClipCount} clips.");
                modelEditor.Save();
            }
            GModelAsset resaved = StudioModelResourceLoader.Load(model);
            Check(VisibleFrames(resaved, GModelPrimitiveFactory.EvaluateBindPosePalette(resaved.Rig)).SequenceEqual([0])
                && VisibleFrames(resaved, GModelPrimitiveFactory.EvaluateSkinPalette(resaved.Rig,
                    GModelPrimitiveFactory.EvaluateAnimatedLocals(resaved, RuntimeModelAnimationState.From(saved, false)))).SequenceEqual([2]),
                "Saving from the Model Editor broke the flipbook.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Image.ToModel.AnimatorPlaysFlipbookInGameplay", () =>
        {
            string image = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, "Traffic light");
            PixelModelSource source = Source(4, 4,
                [Frame("Red", 100, (_, _) => Red), Frame("Green", 100, (_, _) => Green), Frame("Blue", 100, (_, _) => Blue)], image);
            string model = PixelModelResource.Create(image,
                PixelModelBuilder.Build(source, new PixelModelSettings { AllFrames = true, PixelSize = .25f }), "Traffic light 3D");
            using RuntimeScene scene = new("Pixel model flipbook");
            var world = scene.World;
            var entity = world.CreateEntity();
            world.Set(entity, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
            world.Set(entity, new Draw3DComponent { Visible = true });
            world.Set(entity, new ModelRendererComponent { ModelAsset = ResourceNames.Name(project.RootPath, model), ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
            world.Set(entity, new ModelAnimatorComponent { ClipName = "Frames", Playing = true, Loop = true, PlaybackSpeed = 1 });
            using Form host = UnattendedWindowing.NewHost(320, 240); UnattendedWindowing.ShowWithoutFocus(host);
            RenderBackendDescriptor backend = RenderBackendCatalog.All.First();
            using IRenderController renderer = RenderControllerFactory.Create(backend.Backend); renderer.Initialize(host.Handle, 320, 240);
            RuntimeModelRenderSystem models = new();
            string previousProject = PgslCommands.ProjectPath;
            try
            {
                PgslCommands.ProjectPath = project.RootPath;
                int stepped = 0;
                // Each frame lasts 6 ticks at 60 per second; sample the middle of each, then after the loop.
                foreach ((int tick, string expected) in new[] { (3, "red"), (9, "green"), (15, "blue"), (21, "red") })
                {
                    for (; stepped < tick; stepped++) ComponentLifecycle.OnUpdate(world, entity, 1f / 60f);
                    (int red, int green, int blue) = RenderCounts(renderer, models, world, project.RootPath, ctx, $"pixel-model-game-{tick}");
                    string shown = red > green && red > blue ? "red" : green > blue ? "green" : "blue";
                    Check(Math.Max(red, Math.Max(green, blue)) > 2000 && shown == expected
                        && new[] { red, green, blue }.Count(count => count > 200) == 1,
                        $"At tick {tick} gameplay drew red {red}, green {green}, blue {blue}; expected only {expected}.");
                }
            }
            finally { models.InvalidateAssets(renderer); PgslCommands.ProjectPath = previousProject; }
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Image.ToModel.EditorCommandSendsCompositedFrames", () =>
        {
            string image = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, "Editor frames");
            ImageDocumentSession session = new(ImageDocumentSerializer.LoadAtomic(image).Document, image, ImageDocumentAccess.Editor);
            ImageWorkspace workspace = ImageWorkspace.CreateBlank(8, 8, Red);
            workspace.Frames[0].DurationMilliseconds = 120;
            workspace.AddFrame(duplicateCurrent: false).DurationMilliseconds = 250;
            RasterPaint(workspace.Frames[1].Layers[0].Pixels, 8, (x, y) => y < 4 ? Blue : Color.Transparent);
            ImageWorkspaceStorage.Save(session, workspace);
            ImageWorkspace loaded = ImageWorkspaceStorage.Load(session);
            using ImageEditorControl editor = new(session, loaded);
            using Form host = UnattendedWindowing.NewHost(1280, 780); host.Controls.Add(editor); UnattendedWindowing.ShowWithoutFocus(host);
            Func<IWin32Window?, PixelModelSource, string?>? previous = ImageEditorControl.ModelConversionDialog;
            PixelModelSource? received = null;
            int opened = 0;
            try
            {
                ImageEditorControl.ModelConversionDialog = (_, source) => { received = source; opened++; return null; };
                ToolStripItem? command = FindItem(editor.CommandBar.Items, "Convert to 3D Model…");
                Check(command is not null, "The Image Editor's Options menu has no Convert to 3D Model command.");
                command!.PerformClick();
                editor.CommandBar.Items.OfType<ToolStripButton>().Single(item => item.Text == "Use in game").PerformClick();
                System.Windows.Forms.Application.DoEvents();
                Button guide = (Button)editor.Controls.Find("ImageConvertToModel", true).Single();
                guide.PerformClick();
            }
            finally { ImageEditorControl.ModelConversionDialog = previous; }
            Check(received is not null && opened == 2, $"The menu command and the Use in game button should both open the conversion dialog; opened {opened} times.");
            Check(received!.Width == 8 && received.Height == 8 && received.Frames.Count == 2 && received.ImagePath == image
                && received.Frames[0].DurationMilliseconds == 120 && received.Frames[1].DurationMilliseconds == 250,
                "The conversion did not receive the Image's size, frames and durations.");
            Check(received.Frames[1].Rgba[3] == 255 && received.Frames[1].Rgba[2] == Blue.B && received.Frames[1].Rgba[(7 * 8) * 4 + 3] == 0,
                "Frame pixels were not composited as the canvas shows them.");
            GModelAsset asset = PixelModelBuilder.Build(received, new PixelModelSettings { AllFrames = true });
            Check(asset.Animations.Single().Frames.Count == 7 + 15, "Frame durations did not set the clip timing.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Image.ToModel.PreviewRendersAndGizmoBakesTransform", () =>
        {
            string image = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, "Mushroom");
            PixelModelSource source = Source(16, 16,
            [
                Frame("Frame 1", 150, Mushroom(Red), 16),
                Frame("Frame 2", 150, Mushroom(Blue), 16),
            ], image);
            using PixelModelDialog dialog = new(source) { ClientSize = new Size(1100, 740) };
            ThemeService.Apply(dialog);
            dialog.Configure(.0625f, 3, 128, allFrames: true, name: "Mushroom 3D");
            dialog.SetTransform(Vector3.Zero, new Vector3(0f, -20f, 0f), Vector3.One);
            dialog.PreviewTime = .05f;
            UnattendedWindowing.ShowWithoutFocus(dialog); System.Windows.Forms.Application.DoEvents();
            dialog.ApplyInterfaceLayout(); dialog.FramePreview();
            using (Bitmap? frame = dialog.Viewport.CaptureFrame(6))
            {
                Check(frame is not null, "No native preview frame.");
                string file = Path.Combine(ctx.Captures, "pixel-model-preview.png");
                frame!.Save(file);
                ctx.Report.Images.Add(ImageResult.From("pixel-model-preview", file, VisualCapture.Measure(frame)));
                Check(CountPixels(frame, c => c.R > 120 && c.G < 90 && c.B < 90) > 400, "The red first frame did not render in the preview.");
                Check(CountPixels(frame, c => c.B > 150 && c.R < 90) < 400, "The hidden second frame was drawn over the first.");
            }
            dialog.PreviewTime = .2f; System.Windows.Forms.Application.DoEvents();
            using (Bitmap? frame = dialog.Viewport.CaptureFrame(6))
            {
                Check(frame is not null && CountPixels(frame, c => c.B > 150 && c.R < 90) > 400, "The preview did not play on to the blue second frame.");
                string file = Path.Combine(ctx.Captures, "pixel-model-preview-frame2.png");
                frame!.Save(file);
                ctx.Report.Images.Add(ImageResult.From("pixel-model-preview-frame2", file, VisualCapture.Measure(frame)));
            }
            var metrics = VisualCapture.CaptureOpenForm(dialog, Path.Combine(ctx.Captures, "pixel-model-dialog.png"), true);
            ctx.Report.Images.Add(ImageResult.From("pixel-model-dialog", "pixel-model-dialog.png", metrics));
            Button create = (Button)dialog.Controls.Find("PixelModelCreate", true).Single();
            Check(create.Visible && create.Enabled && dialog.Viewport.Width >= 350 && dialog.Viewport.Height >= 350,
                "The create action is hidden or the preview is crowded out.");

            dialog.SetTransform(Vector3.Zero, Vector3.Zero, Vector3.One);
            Vector3 before = dialog.BuildModel().Bounds.Size;
            dialog.ApplyGizmoDrag(EditorGizmoMode.Scale, 0, 1f);
            dialog.ApplyGizmoDrag(EditorGizmoMode.Move, 1, .5f);
            PixelModelSettings moved = dialog.Settings;
            GModelAsset baked = dialog.BuildModel();
            Check(MathF.Abs(moved.Scale.X - 2f) < .001f && MathF.Abs(moved.Position.Y - .5f) < .001f
                && MathF.Abs(baked.Bounds.Size.X - before.X * 2f) < .001f && MathF.Abs(baked.Bounds.Size.Y - before.Y) < .001f,
                $"Resize or move did not reach the baked model: {before} → {baked.Bounds.Size}.");
            dialog.ApplyGizmoDrag(EditorGizmoMode.Rotate, 1, .5f);
            Check(MathF.Abs(dialog.Settings.RotationDegrees.Y - 90f) < .01f
                && MathF.Abs(dialog.BuildModel().Bounds.Size.Z - before.X * 2f) < .002f, "Rotating a quarter turn was not baked.");
            string model = dialog.CreateModel();
            GModelAsset saved = StudioModelResourceLoader.Load(model);
            Check(saved.Animations.Count == 1 && MathF.Abs(saved.Bounds.Size.Z - before.X * 2f) < .002f, "The created Model lost its clip or transform.");
        });
    }

    private static PixelModelSource Source(int width, int height, IReadOnlyList<PixelModelFrame> frames, string? image = null) => new()
    {
        Width = width, Height = height, Frames = frames, ImagePath = image, Origin = new Vector2(.5f, .5f), SuggestedName = "Test 3D",
    };

    private static PixelModelFrame Frame(string name, int milliseconds, Func<int, int, Color> paint, int size = 4)
    {
        byte[] pixels = new byte[size * size * 4];
        RasterPaint(pixels, size, paint);
        return new PixelModelFrame(name, pixels, milliseconds);
    }

    private static void RasterPaint(byte[] pixels, int width, Func<int, int, Color> paint)
    {
        int height = pixels.Length / 4 / width;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            Color colour = paint(x, y);
            int i = (y * width + x) * 4;
            pixels[i] = colour.R; pixels[i + 1] = colour.G; pixels[i + 2] = colour.B; pixels[i + 3] = colour.A;
        }
    }

    private static Func<int, int, Color> Mushroom(Color cap) => (x, y) =>
    {
        float dx = x - 7.5f, dy = y - 7f;
        if (y <= 8 && dx * dx / 49f + dy * dy / 36f <= 1f) return (x + y) % 5 == 0 ? Color.White : cap;
        if (y > 8 && y < 15 && x is >= 5 and <= 10) return Color.FromArgb(255, 235, 220, 190);
        return Color.Transparent;
    };

    /// <summary>Draws the world's models the way gameplay submits them and counts strongly red, green and blue pixels.</summary>
    private static (int Red, int Green, int Blue) RenderCounts(IRenderController renderer, RuntimeModelRenderSystem models,
        Genesis.Runtime.ECS.World world, string projectRoot, HeadlessContext ctx, string name)
    {
        Mesh3DState lighting = Mesh3DState.Default; lighting.LightingEnabled = false; lighting.FogEnabled = false;
        lighting.ShowFloor = lighting.ShowSunVisual = false;
        renderer.SetMesh3DState(lighting); renderer.Set3DFrameActive(true);
        renderer.SetCamera3D(Matrix4x4.CreateLookAt(new Vector3(.4f, .3f, 3f), Vector3.Zero, Vector3.UnitY),
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 320f / 240f, .1f, 100));
        MeshDrawCall[] draws = new MeshDrawCall[16]; models.BeginFrame();
        int count = models.SubmitWorld(world, projectRoot, draws, 0, renderer); models.EndFrame();
        Check(count > 0, "Gameplay submitted no Model geometry.");
        renderer.BeginFrame(); renderer.Clear(.02f, .02f, .03f);
        for (int index = 0; index < count; index++) { MeshDrawCall draw = draws[index]; draw.Flags |= MeshDrawFlags.NoShadow | MeshDrawFlags.NoFog; renderer.DrawMesh(draw); }
        renderer.EndFrame();
        Check(renderer.TryReadSubmittedFramePixels(out int width, out int height, out byte[] pixels), "No gameplay pixels were read back.");
        int red = 0, green = 0, blue = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            if (r > 100 && r > g * 2 && r > b * 2) red++;
            else if (g > 100 && g > r * 2 && g > b * 1.5) green++;
            else if (b > 100 && b > r * 2 && b > g * 1.5) blue++;
        }
        using Bitmap bitmap = new(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try { System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); } finally { bitmap.UnlockBits(data); }
        string file = Path.Combine(ctx.Captures, name + ".png"); bitmap.Save(file);
        ctx.Report.Images.Add(ImageResult.From(name, file, VisualCapture.Measure(bitmap)));
        renderer.Present();
        return (red, green, blue);
    }

    /// <summary>Which frames' meshes have any extent under a skin palette.</summary>
    private static int[] VisibleFrames(GModelAsset asset, Matrix4x4[] palette)
    {
        List<int> visible = [];
        foreach (GModelMesh mesh in asset.Meshes)
        {
            int bone = (int)mesh.SkinnedVertices[0].JointIndices.X;
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (SkinnedMeshVertex vertex in mesh.SkinnedVertices)
            {
                Vector3 position = Vector3.Transform(vertex.Position, palette[bone]);
                min = Vector3.Min(min, position); max = Vector3.Max(max, position);
            }
            if ((max - min).Length() > 1e-4f) visible.Add(bone - 1);
        }
        return visible.Distinct().ToArray();
    }

    private static ToolStripItem? FindItem(ToolStripItemCollection items, string text)
    {
        foreach (ToolStripItem item in items)
        {
            if (item.Text == text) return item;
            if (item is ToolStripDropDownItem dropDown && FindItem(dropDown.DropDownItems, text) is { } found) return found;
        }
        return null;
    }

    private static int CountPixels(Bitmap bitmap, Func<Color, bool> match)
    {
        int count = 0;
        for (int y = 0; y < bitmap.Height; y += 2)
            for (int x = 0; x < bitmap.Width; x += 2)
                if (match(bitmap.GetPixel(x, y))) count++;
        return count;
    }

    private static bool SameColour(Vector4 colour, Color expected) =>
        MathF.Abs(colour.X - expected.R / 255f) < .002f && MathF.Abs(colour.Y - expected.G / 255f) < .002f
        && MathF.Abs(colour.Z - expected.B / 255f) < .002f && MathF.Abs(colour.W - 1f) < .002f;

    private static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < .001f;

    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
