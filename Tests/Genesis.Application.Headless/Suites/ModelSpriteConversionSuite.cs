using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Runtime.Assets;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// "Convert to 2D sprites": a cube with a different colour on every face is rendered by the engine
/// from 8 directions into an Image. Facing right shows the model's right side (red), facing left
/// its left side (blue), away its back (yellow), toward the camera its front (green); the top is
/// white and the bottom (magenta) never shows. PGSL then picks frames by angle.
/// </summary>
internal static class ModelSpriteConversionSuite
{
    private static readonly Vector4 Red = new(1, 0, 0, 1), Blue = new(0, 0, 1, 1), Green = new(0, 1, 0, 1),
        Yellow = new(1, 1, 0, 1), White = new(1, 1, 1, 1), Magenta = new(1, 0, 1, 1);

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "ModelSprites");
        ProjectSession project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "ModelSprites"), "Model Sprites");
        ResourceService resources = new(project);
        string model = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Faces");
        string stillImage = "", animatedImage = "";

        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Sprites.PanelOffersOptionsAndLivePreview", () =>
        {
            using (ModelEditorControl editor = new(model, project.RootPath))
            {
                editor.ApplyAnimationWorkspace(FacesCube(skinned: false), "");
                editor.Save();
            }
            using Form host = new() { Text = "Model viewer", ClientSize = new Size(1280, 800) };
            using ModelViewerControl viewer = new(model, project.RootPath);
            host.Controls.Add(viewer);
            _ = host.Handle; _ = viewer.Handle;
            Pump();
            ToolStrip commands = (ToolStrip)viewer.Controls.Find("ModelViewerCommands", true).Single();
            HeadlessHarness.Assert(commands.Items.OfType<ToolStripItem>().Any(item => item.Name == "ModelConvertToSprites"),
                "The Model Viewer has no Convert to 2D sprites command.");

            using ModelSpriteConversionDialog dialog = viewer.CreateSpriteConversionDialog();
            ModelSpriteBakeSettings defaults = dialog.Settings;
            ComboBox directions = (ComboBox)dialog.Controls.Find("ModelSpriteDirections", true).Single();
            HeadlessHarness.Assert(directions.Items.Cast<int>().SequenceEqual([8, 16, 32]) && defaults.Directions == 16
                && defaults.ElevationDegrees == 30 && defaults.FrameSize == 128 && defaults.TransparentBackground && defaults.Orthographic
                && defaults.Clip.Length == 0 && defaults.Lighting,
                $"Defaults should be 16 directions, 30 degrees, 128 pixels, transparent: {defaults}.");
            foreach (string name in new[] { "ModelSpriteElevation", "ModelSpriteFrameSize", "ModelSpriteProjection", "ModelSpriteClip",
                "ModelSpriteFps", "ModelSpriteFrames", "ModelSpriteLighting", "ModelSpriteOutline", "ModelSpriteTransparent", "ModelSpriteCreate" })
                HeadlessHarness.Assert(dialog.Controls.Find(name, true).Length == 1, $"The panel has no {name} control.");

            dialog.Configure(defaults with { Directions = 8, FrameSize = 96, OutlinePixels = 1 });
            using Bitmap preview = dialog.RenderPreview();
            HeadlessHarness.Assert(preview.Width == 4 * 96 && preview.Height == 2 * 96, $"The preview is {preview.Width}x{preview.Height}, not 4x2 frames.");
            VisualCapture.Capture(dialog, Path.Combine(ctx.Captures, "model-sprites-panel.png"));
            dialog.Hide();
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Sprites.EightDirectionsShowEachSideOverTransparency", () =>
        {
            using ModelEditorControl editor = new(model, project.RootPath);
            using ModelSpriteConversionDialog dialog = editor.CreateSpriteConversionDialog();
            dialog.Configure(dialog.Settings with { Directions = 8, FrameSize = 64, ElevationDegrees = 30, Orthographic = true });
            dialog.ImageName = "Faces Sprites";
            stillImage = dialog.CreateImage();
            HeadlessHarness.Assert(File.Exists(stillImage) && ResourceNames.Name(project.RootPath, stillImage) == "Faces Sprites",
                "Create Image did not add an Image resource named after the panel.");

            ImageDocument document = ImageDocumentSerializer.LoadAtomic(stillImage).Document;
            ImageDirectionalSpriteSettings? meta = document.Usage.Directions;
            HeadlessHarness.Assert(document.Frames.Count == 8 && meta is { Count: 8, FramesPerDirection: 1 } && meta.SourceModel == "Faces"
                && Math.Abs(meta.ElevationDegrees - 30) < .001 && document.Canvas.Width == 64 && document.Canvas.Height == 64,
                $"Expected 8 frames of 64x64 with direction metadata; found {document.Frames.Count} frames, {document.Canvas.Width}x{document.Canvas.Height}.");
            SpriteRuntimeAsset runtime = SpriteAssetLoader.Load(stillImage);
            HeadlessHarness.Assert(runtime.Frames.Count == 8 && runtime.Usage.Directions is { Count: 8, FramesPerDirection: 1 },
                "The runtime loader does not see the direction metadata.");

            List<(byte[] Rgba, int Width, int Height)> frames = document.Frames
                .Select(frame => ReadRgba(SpriteAssetLoader.ResolveFrameTexturePath(stillImage, runtime, document.Frames.IndexOf(frame)))).ToList();
            using (Bitmap sheet = ModelSpriteBaker.ContactSheet(frames.Select(f => f.Rgba).ToList(), 64, 64, 8, 1))
                Save(sheet, ctx, "model-sprites-8-directions.png");

            for (int index = 0; index < frames.Count; index++)
            {
                (byte[] rgba, int width, int height) = frames[index];
                foreach ((int x, int y) in new[] { (0, 0), (width - 1, 0), (0, height - 1), (width - 1, height - 1) })
                    HeadlessHarness.Assert(rgba[(y * width + x) * 4 + 3] == 0, $"Direction {index}: corner ({x},{y}) is not transparent.");
                int opaque = 0, empty = 0;
                for (int i = 3; i < rgba.Length; i += 4) { if (rgba[i] == 255) opaque++; else if (rgba[i] == 0) empty++; }
                HeadlessHarness.Assert(opaque > width * height / 10 && empty > width * height / 4,
                    $"Direction {index}: {opaque} solid and {empty} empty pixels; the model or the transparent background is missing.");
                Dictionary<char, int> colours = Census(rgba);
                HeadlessHarness.Assert(colours.GetValueOrDefault('W') > 20 && colours.GetValueOrDefault('M') == 0,
                    $"Direction {index}: the top (white) should show and the bottom (magenta) never: {Describe(colours)}.");
            }

            // Straight-on directions show one side face; opposite directions show opposite faces.
            (int Direction, char Side, string What)[] expected =
                [(0, 'R', "right side (facing right)"), (2, 'Y', "back (facing away)"), (4, 'B', "left side (facing left)"), (6, 'G', "front (facing the camera)")];
            foreach ((int direction, char side, string what) in expected)
            {
                Dictionary<char, int> colours = Census(frames[direction].Rgba);
                char dominant = colours.Where(pair => pair.Key is 'R' or 'G' or 'B' or 'Y').OrderByDescending(pair => pair.Value).FirstOrDefault().Key;
                HeadlessHarness.Assert(dominant == side, $"Direction {direction} should show the {what} ({side}); it shows {Describe(colours)}.");
            }

            // Facing down-right (315 degrees) shows the front on the right and the right side on the left;
            // a mirrored render would swap them.
            (float redX, float redY) = Centroid(frames[7].Rgba, 64, 'R');
            (float greenX, _) = Centroid(frames[7].Rgba, 64, 'G');
            HeadlessHarness.Assert(redX < greenX - 4, $"At 315 degrees the right side (red, x={redX:0.#}) should be left of the front (green, x={greenX:0.#}).");
            (_, float whiteY) = Centroid(frames[0].Rgba, 64, 'W');
            (_, float sideY) = Centroid(frames[0].Rgba, 64, 'R');
            HeadlessHarness.Assert(whiteY < sideY - 4, $"The top (y={whiteY:0.#}) should sit above the side (y={sideY:0.#}); the frame is upside down.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Sprites.AnimationFramesAreDirectionMajorWithOutline", () =>
        {
            GModelAsset bobbing = FacesCube(skinned: true);
            var settings = new ModelSpriteBakeSettings { Directions = 8, FrameSize = 64, Clip = "Bob", FramesPerSecond = 12, AnimationFrames = 3, OutlinePixels = 1, OutlineColor = Color.Black };
            ModelSpriteSheet sheet = ModelSpriteBaker.Bake(bobbing, project.RootPath, settings);
            HeadlessHarness.Assert(sheet.Frames.Count == 24 && sheet.Directions == 8 && sheet.FramesPerDirection == 3,
                $"8 directions x 3 frames should give 24 frames, not {sheet.Frames.Count}.");
            using (Bitmap contact = ModelSpriteBaker.ContactSheet(sheet, columns: 3)) Save(contact, ctx, "model-sprites-animated.png");
            for (int direction = 0; direction < 8; direction++)
            {
                float[] heights = Enumerable.Range(0, 3).Select(frame => Centroid(sheet.Frames[direction * 3 + frame], 64, 'W').Y).ToArray();
                HeadlessHarness.Assert(heights[1] < heights[0] - 1 && heights[2] < heights[1] - 1,
                    $"Direction {direction}: frames should rise through the clip (top at y={string.Join(", ", heights.Select(h => h.ToString("0.#")))}).");
            }
            foreach ((int direction, char side) in new[] { (0, 'R'), (4, 'B') })
                for (int frame = 0; frame < 3; frame++)
                {
                    Dictionary<char, int> colours = Census(sheet.Frames[direction * 3 + frame]);
                    char dominant = colours.Where(pair => pair.Key is 'R' or 'G' or 'B' or 'Y').OrderByDescending(pair => pair.Value).FirstOrDefault().Key;
                    HeadlessHarness.Assert(dominant == side, $"Frame {direction * 3 + frame} is not direction {direction} ({side}): {Describe(colours)}.");
                }
            byte[] first = sheet.Frames[0];
            int outline = 0;
            for (int i = 0; i < first.Length; i += 4) if (first[i + 3] == 255 && first[i] < 30 && first[i + 1] < 30 && first[i + 2] < 30) outline++;
            HeadlessHarness.Assert(outline > 40 && first[3] == 0, $"The 1-pixel black outline is missing ({outline} outline pixels).");
            animatedImage = ModelSpriteBaker.WriteImage(resources, ResourceFolderPolicy.RootFor(project, ResourceKind.Image), "Bobbing Sprites", sheet, "Faces");
            ImageDocument document = ImageDocumentSerializer.LoadAtomic(animatedImage).Document;
            HeadlessHarness.Assert(document.Frames.Count == 24 && document.Usage.Directions is { Count: 8, FramesPerDirection: 3, Clip: "Bob" }
                && document.Tags.Count == 8 && document.Tags[1].StartFrameId == document.Frames[3].Id && document.Tags[1].EndFrameId == document.Frames[5].Id,
                "The animated Image lost its frames, metadata or per-direction loops.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.SpriteDirections.PickFramesByAngle", () =>
        {
            // The rule itself: nearest direction, 0 = right, counter-clockwise, wrapping both ways.
            (double Angle, int Frame)[] eight = [(0, 0), (22, 0), (23, 1), (44, 1), (45, 1), (46, 1), (90, 2), (180, 4), (270, 6),
                (337, 7), (338, 0), (359, 0), (360, 0), (-1, 0), (-23, 7), (-45, 7), (-90, 6), (450, 2), (810, 2)];
            foreach ((double angle, int frame) in eight)
                HeadlessHarness.Assert(SpriteDirections.FrameIndex(8, 1, angle, 0) == frame,
                    $"8 directions: {angle} degrees picked {SpriteDirections.FrameIndex(8, 1, angle, 0)}, not {frame}.");
            HeadlessHarness.Assert(SpriteDirections.FrameIndex(16, 1, 44, 0) == 2 && SpriteDirections.FrameIndex(16, 1, 46, 0) == 2
                && SpriteDirections.FrameIndex(16, 1, 34, 0) == 2 && SpriteDirections.FrameIndex(16, 1, 33, 0) == 1
                && SpriteDirections.FrameIndex(32, 1, 359, 0) == 0 && SpriteDirections.FrameIndex(32, 1, 350, 0) == 31,
                "16 and 32 directions picked the wrong frames.");
            HeadlessHarness.Assert(SpriteDirections.FrameIndex(8, 1, double.NaN, 0) == 0, "A non-finite angle should fall back to direction 0.");

            PgslStatics saved = PgslStatics.Take();
            PgslContext instance = new() { SpriteIndex = "Bobbing Sprites", ImageIndex = 0 };
            PgslContext? previous = PgslCommands.BindContext(instance);
            PgslCommands.ProjectPath = project.RootPath;
            try
            {
                Check(PgslCommands.SpriteDirectionCount("Faces Sprites") == 8 && PgslCommands.SpriteDirectionFrames("Faces Sprites") == 1, "still image layout");
                foreach ((double angle, int frame) in new (double, int)[] { (0, 0), (44, 1), (46, 1), (359, 0), (-45, 7), (-1, 0), (180, 4), (90, 2) })
                    Check(PgslCommands.SpriteDirectionFrame("Faces Sprites", angle) == frame, $"SpriteDirectionFrame at {angle} degrees should be {frame}");

                Check(PgslCommands.SpriteDirectionCount("Bobbing Sprites") == 8 && PgslCommands.SpriteDirectionFrames("Bobbing Sprites") == 3, "animated image layout");
                Check(PgslCommands.SpriteDirectionFrame("Bobbing Sprites", 90, 2) == 8, "direction 2, frame 2 is frame 8");
                Check(PgslCommands.SpriteDirectionFrame("Bobbing Sprites", 90, 4) == 7, "animation frames wrap (frame 4 of 3 is frame 1)");
                Check(PgslCommands.SpriteDirectionFrame("Bobbing Sprites", 90, -1) == 8, "negative animation frames wrap backwards");
                Check(PgslCommands.SpriteDirectionFrame("Bobbing Sprites", 359, 1) == 1, "359 degrees is direction 0");
                Check(PgslCommands.SpriteDirectionFrame("No such image", 0) == -1 && PgslCommands.SpriteDirectionCount("No such image") == 0, "a missing image");

                Check(PgslCommands.SpriteSetDirection(180) && instance.ImageIndex == 12, $"SpriteSetDirection(180) set frame {instance.ImageIndex}, not 12");
                instance.ImageIndex = 13; // ImageSpeed moved on one frame.
                Check(PgslCommands.SpriteSetDirection(90) && instance.ImageIndex == 7, $"turning keeps the animation frame (got {instance.ImageIndex}, not 7)");
                instance.ImageIndex = 9; // Played past the last frame of direction 2: wraps to its first.
                Check(PgslCommands.SpriteSetDirection(90) && instance.ImageIndex == 6, $"playing past a direction wraps within it (got {instance.ImageIndex}, not 6)");
                Check(PgslCommands.SpriteSetDirection(-45, 2) && instance.ImageIndex == 23, $"SpriteSetDirection(-45, 2) set frame {instance.ImageIndex}, not 23");
                instance.SpriteIndex = "No such image"; instance.ImageIndex = 5;
                Check(!PgslCommands.SpriteSetDirection(0) && instance.ImageIndex == 5, "a missing sprite must leave the frame alone");
            }
            finally
            {
                PgslCommands.BindContext(previous!);
                saved.Restore();
            }
        });
    }

    /// <summary>A unit cube with one colour per face, optionally skinned to one bone that rises through a 3-frame clip.</summary>
    private static GModelAsset FacesCube(bool skinned)
    {
        var asset = new GModelAsset { Name = "Faces", ImportRequired = false, ImportMessage = "" };
        (Vector3 Normal, Vector3 U, Vector3 V, Vector4 Colour, string Name)[] faces =
        [
            (Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Blue, "Left +X"),
            (-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY, Red, "Right -X"),
            (Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX, White, "Top"),
            (-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, Magenta, "Bottom"),
            (Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, Green, "Front +Z"),
            (-Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX, Yellow, "Back -Z"),
        ];
        foreach ((Vector3 normal, Vector3 u, Vector3 v, Vector4 colour, string name) in faces)
        {
            Vector3 centre = normal * .5f, du = u * .5f, dv = v * .5f;
            MeshVertex[] vertices = new[] { centre - du - dv, centre + du - dv, centre + du + dv, centre - du + dv }
                .Select((position, corner) => new MeshVertex
                {
                    Position = position, Normal = normal, Color = colour,
                    UV = new Vector2(corner is 1 or 2 ? 1 : 0, corner is 2 or 3 ? 1 : 0),
                }).ToArray();
            asset.Materials.Add(new GModelMaterial { Name = name, BaseColor = colour });
            var mesh = new GModelMesh { Name = name, MaterialIndex = asset.Materials.Count - 1, Vertices = vertices, Indices = [0, 1, 2, 0, 2, 3] };
            if (skinned)
            {
                mesh.IsSkinned = true;
                mesh.SkinnedVertices = vertices.Select(vertex => new SkinnedMeshVertex
                { Position = vertex.Position, Normal = vertex.Normal, Color = vertex.Color, UV = vertex.UV, JointWeights = new(1, 0, 0, 0) }).ToArray();
            }
            asset.Meshes.Add(mesh);
        }
        if (skinned)
        {
            asset.Rig = new GModelRig { Bones = [new GModelBone { Name = "Root" }], InverseBindMatrices = [Matrix4x4.Identity] };
            asset.Animations.Add(new GModelAnimationClip
            {
                Name = "Bob", Fps = 12,
                Frames = [.. Enumerable.Range(0, 3).Select(frame => new GModelAnimationFrame { LocalBoneTransforms = [Matrix4x4.CreateTranslation(0, frame * .15f, 0)] })],
            });
        }
        asset.RecalculateBounds();
        return asset;
    }

    /// <summary>Counts opaque pixels by hue: R, G, B, Y(ellow), M(agenta), W(hite or grey top).</summary>
    private static Dictionary<char, int> Census(byte[] rgba)
    {
        var counts = new Dictionary<char, int>();
        for (int i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i + 3] < 200) continue;
            char kind = Classify(rgba[i], rgba[i + 1], rgba[i + 2]);
            if (kind != '-') counts[kind] = counts.GetValueOrDefault(kind) + 1;
        }
        return counts;
    }

    private static char Classify(int r, int g, int b)
    {
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (max - min < 45) return max > 120 ? 'W' : '-';
        if (r > g + 45 && r > b + 45) return 'R';
        if (b > r + 45 && b > g + 45) return 'B';
        if (g > r + 45 && g > b + 45) return 'G';
        if (r > b + 45 && g > b + 45) return 'Y';
        if (r > g + 45 && b > g + 45) return 'M';
        return '-';
    }

    private static (float X, float Y) Centroid(byte[] rgba, int width, char kind)
    {
        double x = 0, y = 0; int count = 0;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i + 3] < 200 || Classify(rgba[i], rgba[i + 1], rgba[i + 2]) != kind) continue;
            int pixel = i / 4; x += pixel % width; y += pixel / width; count++;
        }
        return count == 0 ? (float.NaN, float.NaN) : ((float)(x / count), (float)(y / count));
    }

    private static string Describe(Dictionary<char, int> colours) =>
        colours.Count == 0 ? "no coloured pixels" : string.Join(" ", colours.OrderByDescending(p => p.Value).Select(p => $"{p.Key}={p.Value}"));

    private static (byte[] Rgba, int Width, int Height) ReadRgba(string path)
    {
        HeadlessHarness.Assert(File.Exists(path), $"Frame file {path} is missing.");
        using var bitmap = new Bitmap(path);
        var rgba = new byte[bitmap.Width * bitmap.Height * 4];
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color pixel = bitmap.GetPixel(x, y); int offset = (y * bitmap.Width + x) * 4;
                rgba[offset] = pixel.R; rgba[offset + 1] = pixel.G; rgba[offset + 2] = pixel.B; rgba[offset + 3] = pixel.A;
            }
        return (rgba, bitmap.Width, bitmap.Height);
    }

    private static void Save(Bitmap bitmap, HeadlessContext ctx, string name)
    {
        // Four times larger so the captured frames can be inspected by eye.
        using var large = new Bitmap(bitmap.Width * 4, bitmap.Height * 4, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(large))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            graphics.DrawImage(bitmap, 0, 0, large.Width, large.Height);
        }
        Directory.CreateDirectory(ctx.Captures);
        large.Save(Path.Combine(ctx.Captures, name), ImageFormat.Png);
    }

    private static void Check(bool condition, string what) => HeadlessHarness.Assert(condition, "PGSL directional sprites: " + what + ".");

    private static void Pump()
    {
        for (int i = 0; i < 6; i++) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(15); }
    }

    /// <summary>Restores the PGSL statics this suite changes.</summary>
    private sealed class PgslStatics
    {
        private string? _projectPath;
        public static PgslStatics Take() => new() { _projectPath = PgslCommands.ProjectPath };
        public void Restore() => PgslCommands.ProjectPath = _projectPath;
    }
}
