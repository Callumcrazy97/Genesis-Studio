using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Input;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.ECS;
using WinFormsApplication = System.Windows.Forms.Application;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Application.Headless.Showcase;

/// <summary>
/// Produces the two showcase GIFs: authoring a 2D object and placing it in a room, then playing
/// that room with virtualised input.
/// </summary>
/// <remarks>
/// Driven through the editors' real public surfaces — the same ones a mouse and keyboard reach —
/// rather than by writing files directly. A recording that bypassed the editor would look identical
/// and prove nothing about whether the workflow actually works, which is the whole point of
/// showing it.
/// </remarks>
internal static class ShowcaseRunner
{
    private const int FrameDelayMs = 140;

    public static int Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        string workspace = Path.Combine(outputDirectory, "Workspace");
        if (Directory.Exists(workspace))
        {
            TryClear(workspace);
        }

        Directory.CreateDirectory(workspace);

        Console.WriteLine("=== Showcase ===");
        ShowcaseAssets assets = BuildProject(workspace);

        string roomEditorGif = Path.Combine(outputDirectory, "Showcase_2DRoomEditor.gif");
        string runtimeGif = Path.Combine(outputDirectory, "Showcase_2DRuntime.gif");

        RecordRoomEditor(assets, roomEditorGif);
        RecordRuntime(assets, runtimeGif);

        Console.WriteLine($"PASS  Showcase → {roomEditorGif}");
        Console.WriteLine($"PASS  Showcase → {runtimeGif}");
        return 0;
    }

    private sealed class ShowcaseAssets
    {
        public required ProjectSession Project { get; init; }
        public required string HeroImage { get; init; }
        public required string TileImage { get; init; }
        public required string SkyImage { get; init; }
        public required string HeroObject { get; init; }
        public required string Room { get; init; }
    }

    // ── Project + assets ────────────────────────────────────────────────────────

    private static ShowcaseAssets BuildProject(string workspace)
    {
        ProjectService service = new();
        ProjectSession project = service.CreateProject(workspace, "Showcase 2D");
        ResourceService resources = new(project);
        string assets = resources.AssetsRoot;

        string images = Path.Combine(assets, "Showcase");
        Directory.CreateDirectory(images);

        string heroImage = CreateImage(resources, images, "Hero", 32, 32, ImageUsage.Sprite, Paint.Hero);
        string tileImage = CreateImage(resources, images, "Tiles", 128, 128, ImageUsage.Tileset | ImageUsage.Texture,
            Paint.Tiles, tileWidth: 32, tileHeight: 32);
        string skyImage = CreateImage(resources, images, "Sky", 256, 128, ImageUsage.Background, Paint.Sky);

        string objects = Path.Combine(assets, "Objects");
        Directory.CreateDirectory(objects);
        string heroObject = resources.CreateResource(objects, ResourceKind.GameObject, "Player");

        string rooms = Path.Combine(assets, "Rooms");
        Directory.CreateDirectory(rooms);
        string room = resources.CreateResource(rooms, ResourceKind.Room, "Level1");

        return new ShowcaseAssets
        {
            Project = project,
            HeroImage = heroImage,
            TileImage = tileImage,
            SkyImage = skyImage,
            HeroObject = heroObject,
            Room = room,
        };
    }

    private static string CreateImage(
        ResourceService resources, string folder, string name,
        int width, int height, ImageUsage usage, Action<byte[], int, int> paint,
        int tileWidth = 0, int tileHeight = 0)
    {
        string path = resources.CreateResource(folder, ResourceKind.Image, name);
        ImageDocument document = ImageDocument.CreateDefault(width, height);
        document.Usage.Allowed = usage;
        if (tileWidth > 0)
        {
            document.Usage.Tileset.TileWidth = tileWidth;
            document.Usage.Tileset.TileHeight = tileHeight;
        }

        document.Layers.Add(new ImageLayer { Name = "Base" });
        ImageDocumentSerializer.SaveAtomic(path, document);

        ImageWorkspace workspace = ImageWorkspace.CreateBlank(width, height, Color.Transparent);
        ImageDocumentSession session = new(document, path);
        paint(workspace.Frames[0].Layers[0].Pixels, width, height);
        ImageWorkspaceStorage.Save(session, workspace);
        return path;
    }

    // ── GIF 1: the Room Editor ──────────────────────────────────────────────────

    private static void RecordRoomEditor(ShowcaseAssets assets, string outputFile)
    {
        var frames = new List<Bitmap>();

        // Author the object first: name, sprite, and WASD movement in PGSL.
        using (Form objectHost = Host(new ObjectEditorControl(assets.HeroObject, assets.Project.RootPath),
                   "Genesis Studio — Object Editor"))
        {
            ObjectEditorControl editor = (ObjectEditorControl)objectHost.Controls[0];
            Pump(6, 25);
            Capture(frames, objectHost, 3);

            editor.Document["sprite"] = Relative(assets.Project.RootPath, assets.HeroImage);
            editor.SetEventBody("Create", "x = 320;\ny = 240;\n");
            Pump(3, 20);
            Capture(frames, objectHost, 3);

            // WASD. x/y are built-in instance variables, so they are assigned directly rather than
            // declared with `var` — declaring them would silently shadow the built-ins (NEXT-032).
            editor.SetEventBody(
                "Step",
                "if (KeyCheck(\"A\")) { x = x - 4; }\n"
                + "if (KeyCheck(\"D\")) { x = x + 4; }\n"
                + "if (KeyCheck(\"W\")) { y = y - 4; }\n"
                + "if (KeyCheck(\"S\")) { y = y + 4; }\n");
            editor.Save();
            Pump(4, 25);
            Capture(frames, objectHost, 6);

            // Fail loudly here rather than recording a GIF of a game whose script does not run.
            ObjectSandboxResult? sandbox = editor.RunSandbox();
            if (sandbox is null || !sandbox.Ok)
            {
                throw new InvalidOperationException(
                    "The Player object's WASD events do not run cleanly in the sandbox.");
            }
        }

        // Then the room: background, tiles, and placing the object with a live preview.
        using Form roomHost = Host(new RoomEditorControl(assets.Room, assets.Project.RootPath),
            "Genesis Studio — Room Editor");
        RoomEditorControl room = (RoomEditorControl)roomHost.Controls[0];
        Pump(10, 30);
        Capture(frames, roomHost, 4);

        room.AddBackground(assets.SkyImage);
        Pump(4, 25);
        Capture(frames, roomHost, 4);

        room.BeginTilePainting(assets.TileImage);
        Pump(3, 20);
        Capture(frames, roomHost, 3);

        room.SelectTileIndex(1);
        for (int column = 0; column < 20; column++)
        {
            room.PaintTileAtWorld(column * 32f, 416f);
            if (column % 4 == 0)
            {
                Pump(1, 12);
                Capture(frames, roomHost, 1);
            }
        }

        room.SelectTileIndex(5);
        for (int column = 6; column < 11; column++)
        {
            room.PaintTileAtWorld(column * 32f, 320f);
        }

        Pump(3, 20);
        Capture(frames, roomHost, 4);

        // Arm the object, then walk the virtual cursor across the room so the ghost preview tracks
        // it — this is the part that shows what is about to be placed, and where.
        room.BeginPlacement(assets.HeroObject);
        Pump(3, 20);

        var path = new[]
        {
            new Vector2(120f, 120f), new Vector2(200f, 150f), new Vector2(300f, 190f),
            new Vector2(400f, 230f), new Vector2(470f, 270f), new Vector2(512f, 288f),
        };
        foreach (Vector2 point in path)
        {
            room.MoveVirtualCursorTo(point);
            Pump(2, 16);
            Capture(frames, roomHost, 2);
        }

        room.ClickVirtualCursorAt(new Vector2(512f, 288f));
        Pump(4, 25);
        Capture(frames, roomHost, 4);

        // Select it so the bounding box and the inspector are both visible.
        RoomNode? placed = room.Room.Nodes.LastOrDefault(n => n.Kind == RoomNodeKind.GameObject)
            ?? throw new InvalidOperationException("The object was not placed by the virtual click.");
        room.Select(placed);
        room.Save();
        Pump(6, 30);
        Capture(frames, roomHost, 10);

        GifWriter.Write(outputFile, frames, FrameDelayMs);
        foreach (Bitmap frame in frames) frame.Dispose();
    }

    // ── GIF 2: the runtime ──────────────────────────────────────────────────────

    private static void RecordRuntime(ShowcaseAssets assets, string outputFile)
    {
        // Record the shipped 2D Platformer template rather than the minimal room authored above:
        // it is a complete playable game (player physics, coins, score, HUD), so the recording
        // shows the engine doing real work, and regenerating it here means the template itself is
        // exercised end to end on every run instead of only at New Project time.
        string platformerParent = Path.Combine(
            Path.GetDirectoryName(assets.Project.RootPath)!, "PlatformerDemo");
        Directory.CreateDirectory(platformerParent);

        ProjectService service = new();
        ProjectSession platformer = service.CreateProject(platformerParent, "Platformer Demo");
        Genesis.Application.Core.Projects.Templates.PlatformerTemplate.Apply(platformer);
        Console.WriteLine($"      regenerated the 2D Platformer template at {platformer.RootPath}");

        // The real thing: compile and launch the Player exactly as File → Run does, then drive it
        // with OS-level keystrokes. Nothing here is simulated in-process.
        List<Bitmap> frames = GameRunRecorder.Record(
            platformer.RootPath, roomName: "Level 1", out string diagnostics);

        try
        {
            Console.WriteLine($"      {diagnostics}");

            // A still image would mean the keys never reached the game, which is the whole thing
            // this recording is meant to demonstrate — so check before writing it out.
            int moving = CountChangedFrames(frames);
            Console.WriteLine($"      {moving} of {frames.Count} frames differ from the first");

            // Always drop a couple of raw frames next to the GIF: when this fails, the question is
            // "what did the game actually show?" and a still is the only thing that answers it.
            string probeDir = Path.GetDirectoryName(outputFile)!;
            frames[0].Save(Path.Combine(probeDir, "runtime-frame-first.png"));
            frames[^1].Save(Path.Combine(probeDir, "runtime-frame-last.png"));
            if (frames.Count > 20)
            {
                frames[20].Save(Path.Combine(probeDir, "runtime-frame-mid.png"));
            }

            if (moving < frames.Count / 4)
            {
                throw new InvalidOperationException(
                    $"Only {moving} of {frames.Count} captured frames differ, so the game did not visibly "
                    + "respond to the synthesised WASD input. Inspect runtime-frame-*.png beside the GIF: "
                    + "a black or empty window means the room never rendered, a static scene means the "
                    + "keys never reached it.");
            }

            GifWriter.Write(outputFile, frames, 90);
        }
        finally
        {
            foreach (Bitmap frame in frames) frame.Dispose();
        }
    }

    /// <summary>Frames differing from the first, on a coarse grid — cheap proof the picture moved.</summary>
    private static int CountChangedFrames(IReadOnlyList<Bitmap> frames)
    {
        if (frames.Count < 2) return 0;

        string first = Digest(frames[0]);
        int changed = 0;
        for (int i = 1; i < frames.Count; i++)
        {
            if (!string.Equals(Digest(frames[i]), first, StringComparison.Ordinal))
            {
                changed++;
            }
        }

        return changed;
    }

    private static string Digest(Bitmap bitmap)
    {
        System.Text.StringBuilder builder = new();
        for (int y = 0; y < bitmap.Height; y += 16)
        {
            for (int x = 0; x < bitmap.Width; x += 16)
            {
                Color c = bitmap.GetPixel(x, y);
                builder.Append((char)('A' + (c.R >> 5)));
                builder.Append((char)('A' + (c.G >> 5)));
                builder.Append((char)('A' + (c.B >> 5)));
            }
        }

        return builder.ToString();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static Form Host(Control control, string title)
    {
        Form form = new()
        {
            Text = title,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(60, 60),
            ClientSize = new Size(1180, 700),
            ShowInTaskbar = false,
        };
        control.Dock = DockStyle.Fill;
        form.Controls.Add(control);
        form.Show();
        form.Activate();
        Pump(4, 25);
        return form;
    }

    /// <summary>Grabs the form <paramref name="repeat"/> times so a step can dwell on screen.</summary>
    private static void Capture(List<Bitmap> frames, Form form, int repeat)
    {
        Bitmap shot = Grab(form);
        frames.Add(shot);
        for (int i = 1; i < repeat; i++)
        {
            frames.Add((Bitmap)shot.Clone());
        }
    }

    /// <summary>
    /// Screen-grabs the form. <c>DrawToBitmap</c> is not usable here: the editors host their scene
    /// in a D3D viewport, and GDI+ renders that region black — the whole reason
    /// <c>TryReadbackFrame</c> exists elsewhere. Copying the composed screen pixels is what gets
    /// both the WinForms chrome and the rendered room in one image.
    /// </summary>
    internal static Bitmap Grab(Form form)
    {
        form.TopMost = true;
        form.Activate();
        form.Invalidate(true);
        form.Refresh();
        Pump(2, 16);

        Size size = form.ClientSize;
        Bitmap shot = new(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(shot))
        {
            graphics.CopyFromScreen(
                form.PointToScreen(Point.Empty), Point.Empty, size, CopyPixelOperation.SourceCopy);
        }

        return shot;
    }

    internal static void Pump(int iterations, int delayMilliseconds)
    {
        for (int i = 0; i < iterations; i++)
        {
            WinFormsApplication.DoEvents();
            Thread.Sleep(delayMilliseconds);
        }
    }

    private static string Relative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>Pixel art for the showcase, generated so the demo depends on no external files.</summary>
    private static class Paint
    {
        public static void Hero(byte[] pixels, int width, int height)
        {
            // A readable little character: body, head, and two eyes.
            Fill(pixels, width, height, 6, 12, 20, 18, Color.FromArgb(255, 66, 135, 245));
            Fill(pixels, width, height, 9, 4, 14, 12, Color.FromArgb(255, 245, 205, 160));
            Fill(pixels, width, height, 12, 8, 3, 3, Color.FromArgb(255, 25, 25, 35));
            Fill(pixels, width, height, 18, 8, 3, 3, Color.FromArgb(255, 25, 25, 35));
            Fill(pixels, width, height, 6, 26, 7, 5, Color.FromArgb(255, 40, 70, 130));
            Fill(pixels, width, height, 18, 26, 7, 5, Color.FromArgb(255, 40, 70, 130));
        }

        public static void Tiles(byte[] pixels, int width, int height)
        {
            // A 4x4 sheet where every tile is clearly different, so painting the wrong one shows.
            Color[] palette =
            [
                Color.FromArgb(255, 96, 160, 72),  Color.FromArgb(255, 128, 96, 56),
                Color.FromArgb(255, 110, 110, 120),Color.FromArgb(255, 220, 220, 235),
                Color.FromArgb(255, 60, 120, 190), Color.FromArgb(255, 180, 140, 70),
                Color.FromArgb(255, 150, 60, 60),  Color.FromArgb(255, 90, 70, 130),
                Color.FromArgb(255, 70, 150, 140), Color.FromArgb(255, 200, 170, 90),
                Color.FromArgb(255, 120, 190, 90), Color.FromArgb(255, 80, 80, 100),
                Color.FromArgb(255, 210, 120, 60), Color.FromArgb(255, 60, 90, 150),
                Color.FromArgb(255, 170, 200, 210),Color.FromArgb(255, 140, 100, 160),
            ];

            for (int tile = 0; tile < 16; tile++)
            {
                int tx = (tile % 4) * 32;
                int ty = (tile / 4) * 32;
                Fill(pixels, width, height, tx + 1, ty + 1, 30, 30, palette[tile]);
                // A lighter top edge so tiles read as surfaces rather than flat squares.
                Fill(pixels, width, height, tx + 1, ty + 1, 30, 4,
                    ControlPaintLighter(palette[tile]));
            }
        }

        public static void Sky(byte[] pixels, int width, int height)
        {
            for (int y = 0; y < height; y++)
            {
                float t = y / (float)Math.Max(1, height - 1);
                Color band = Color.FromArgb(
                    255,
                    (int)(70 + (120 * t)),
                    (int)(120 + (95 * t)),
                    (int)(200 - (40 * t)));
                Fill(pixels, width, height, 0, y, width, 1, band);
            }

            Fill(pixels, width, height, 30, 22, 46, 12, Color.FromArgb(255, 245, 248, 255));
            Fill(pixels, width, height, 150, 40, 62, 14, Color.FromArgb(255, 240, 244, 252));
        }

        private static Color ControlPaintLighter(Color color) => Color.FromArgb(
            255,
            Math.Min(255, color.R + 28),
            Math.Min(255, color.G + 28),
            Math.Min(255, color.B + 28));

        private static void Fill(byte[] rgba, int width, int height, int x0, int y0, int w, int h, Color colour)
        {
            for (int y = y0; y < y0 + h; y++)
            {
                for (int x = x0; x < x0 + w; x++)
                {
                    if (x >= 0 && y >= 0 && x < width && y < height)
                    {
                        RasterOperations.SetPixel(rgba, width, height, x, y, colour);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Removes the previous run's workspace outright. <c>CreateProject</c> refuses a non-empty
    /// directory, so clearing only the files (leaving the tree) is not enough.
    /// </summary>
    private static void TryClear(string directory)
    {
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"      (could not clear read-only on {Path.GetFileName(file)})");
            }
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Antivirus and explorer both hold handles briefly after a previous run.
                Thread.Sleep(150);
            }
        }

        throw new IOException(
            $"The previous showcase workspace at '{directory}' could not be removed. Close anything "
            + "holding it open (Explorer, an editor) and run again.");
    }
}
