using System.Drawing;
using System.Numerics;
using Genesis.Runtime.Input;
using Genesis.Runtime.Particles;
using Genesis.Runtime.Project;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Overlay;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Room-owned 2D lights and particles, numeric saves, and the sprite and mouse contracts that
/// games build on. These began as checks on a fan-game template; they test the engine alone.
/// </summary>
internal static class Effects2DSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Effects2D");
        string parent = Path.Combine(ctx.Workspace, "Effects2D");
        Directory.CreateDirectory(parent);

        HeadlessHarness.RunCase(ctx.Report, "Engine.Effects2D.LightOcclusionUsesSharedRayContract", () =>
        {
            using RoomEffects2D effects = new();
            effects.SetObstacle(1, 40, 10, 16, 64);
            Assert(!effects.LineClear(20, 40, 90, 40), "A solid obstacle failed to block a light ray.");
            Assert(effects.LineClear(20, 5, 90, 5), "A ray above the obstacle is incorrectly blocked.");
            Assert(!effects.LineClear(float.NaN, 0, 1, 1), "Nonfinite ray accepted.");
            Assert(RoomEffects2D.RayRectangle(new Vector2(48, 20), Vector2.UnitX, new RectangleF(40, 10, 16, 64), 100) == 0, "Inside-blocker ray must hit at its origin.");
            effects.SetObstacle(1, 0, 0, 0, 0); Assert(effects.LineClear(20, 40, 90, 40), "Removed blocker is still active.");
            effects.SetLight(1, 0, 0, 90, 1, 1, .5f, 0);
            effects.SetLight(2, float.NaN, 0, 90, 1, 1, 1, 1); Assert(effects.LightCount == 1, "Invalid light was registered.");
            effects.RemoveLight(1); Assert(effects.LightCount == 0, "Removed light is retained.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Effects2D.ParticlePoolPauseExpiryAndRoomIsolation", () =>
        {
            string project = Path.Combine(parent, "Particles");
            string folder = Path.Combine(project, "Assets", "Particles");
            Directory.CreateDirectory(folder);
            string[] particles =
            [
                Path.Combine(folder, "Sparks.particle.json"),
                Path.Combine(folder, "Dust.particle.json"),
            ];
            File.WriteAllText(particles[0], "{\"emitRate\":40}");
            File.WriteAllText(particles[1], "{\"emitRate\":12}");
            ResourceCatalog.Invalidate(project);

            using RoomEffects2D effects = new();
            foreach (string path in particles)
            {
                ParticleConfig config = ParticleAssetLoader.Load(project, path);
                Assert(ParticleAssetLoader.EnumerateEnabledEmitters(config).Count > 0, "Empty particle resource: " + path);
                effects.Emit(project, path, 100, 100, 200, 90);
            }
            for (int i = 0; i < 32; i++) effects.Emit(project, particles[0], 0, 0, 64, 0);
            Assert(effects.ParticleCount == RoomEffects2D.ParticleBudget, "Emission exceeded or did not reach the bounded pool.");
            effects.Paused = true; effects.Advance(10); Assert(effects.ParticleCount == RoomEffects2D.ParticleBudget, "Paused particles advanced.");
            effects.Paused = false; for (int i = 0; i < 260; i++) effects.Advance(.05f);
            Assert(effects.ParticleCount == 0, "Expired particles were retained.");
            RoomAsset a = RoomAsset.Create("A", RoomDimension.TwoD), b = RoomAsset.Create("B", RoomDimension.TwoD);
            RoomEffects2D.For(a).SetAmbient(.4f, 0, 0, 0);
            Assert(!RoomEffects2D.For(b).Enabled, "Effects leaked into an unrelated room.");
            RoomEffects2D.For(a).Dispose(); RoomEffects2D.For(b).Dispose();
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Saves.NumbersRoundTripAtomicallyWithBoundsAndCorruption", () =>
        {
            string file = Path.Combine(parent, "NumericSave", "campaign.json");
            ProjectNumberSave save = new(file); save.Set("chapter", 3); save.Set("bank", 143);
            save.Set("invalid", double.NaN); save.Set(new string('x', 97), 1);
            Assert(save.Flush() && !File.Exists(file + ".tmp"), "Atomic checkpoint write failed.");
            ProjectNumberSave loaded = new(file);
            Assert(loaded.Get("chapter", 0) == 3 && loaded.Get("bank", 0) == 143 && loaded.Get("invalid", -1) == -1, "Checkpoint round-trip changed values.");
            loaded.Clear(); for (int i = 0; i < 300; i++) loaded.Set("slot" + i, i);
            Assert(loaded.Get("slot255", -1) == 255 && loaded.Get("slot256", -1) == -1, "Save-slot capacity is not enforced.");
            File.WriteAllText(file, "{\"version\":999999999999999999999,\"values\":{}}");
            Assert(!new ProjectNumberSave(file).Exists, "Invalid version was accepted or threw an unhandled exception.");
            File.WriteAllText(file, "{\"version\":1,\"values\":{\"bad\":[],\"good\":2}}");
            Assert(new ProjectNumberSave(file).Get("good", -1) == 2, "Malformed individual slot discarded valid data.");
            File.WriteAllText(file, "not json"); Assert(!new ProjectNumberSave(file).Exists, "Corrupt checkpoint did not recover safely.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Text.AProjectFontIsNotLookedUpAgainAtEveryDraw", () =>
        {
            // A menu draws text about a hundred times a frame. Each draw in a project font used to walk
            // the resource catalogue and ask the file system a dozen times (0.16 ms a draw).
            string project = Path.Combine(parent, "Fonts");
            string fonts = Path.Combine(project, "Assets", "Fonts");
            Directory.CreateDirectory(fonts);
            string source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
            if (!File.Exists(source)) throw new CheckNotRunException("This machine has no Arial font file to copy as a project font.");
            File.Copy(source, Path.Combine(fonts, "Menu.ttf"), overwrite: true);
            ResourceCatalog.Invalidate(project);
            PgslRenderDrawSurface surface = new(null, null, 1280, 720, projectPath: project, isGui: true);
            var resolve = typeof(PgslRenderDrawSurface).GetMethod("ResolveFont", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            string first = (string)resolve.Invoke(surface, ["Assets/Fonts/Menu.ttf"])!;
            Assert(File.Exists(first), "The project font did not resolve to its file: " + first);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 2000; i++) resolve.Invoke(surface, ["Assets/Fonts/Menu.ttf"]);
            clock.Stop();
            Assert(clock.Elapsed.TotalMilliseconds < 40,
                $"2000 text draws spent {clock.Elapsed.TotalMilliseconds:F0} ms finding their font; it should be looked up about once a second.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Gui.ShapesAndTextReachTheScreenInTheOrderDrawn", () =>
        {
            // A dimming panel or a padlock plate drawn after a label must cover it.
            OrderRecordingHud hud = new();
            PgslRenderDrawSurface gui = new(null, hud, 1280, 720, isGui: true);
            gui.FillRectangle(Color.Black, new RectangleF(0, 0, 100, 40));
            gui.DrawText("UNDER", "Arial", 20, Color.White, new Rectangle(10, 10, 80, 20));
            gui.FillRectangle(Color.FromArgb(150, 0, 0, 0), new RectangleF(0, 0, 100, 40));
            gui.DrawLine(0, 0, 10, 10, Color.Red, 2);
            Assert(string.Join(",", hud.Order) == "rect,text,rect,line",
                "GUI draws did not keep their order: " + string.Join(",", hud.Order));
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Gui.TextIsMeasuredSpacedAndAligned", () =>
        {
            PgslRecordingDrawSurface surface = new();
            ObjectSandboxResult result = ObjectSandbox.Run(new Dictionary<string, string>
            {
                ["Create"] =
                    "DrawSetFont(\"Arial\");\n" +
                    "w = DrawTextWidth(\"PLAY\", 23);\n" +
                    "DrawSetTextTracking(8);\n" +
                    "ws = DrawTextWidth(\"PLAY\", 23);\n" +
                    "h = DrawTextHeight(23);\n" +
                    "DrawSetTextAlign(\"right\");\n" +
                    "DrawTextScaled(500, 10, \"PLAY\", 23);\n" +
                    "DrawSetTextAlign(\"center\");\n" +
                    "DrawTextScaled(500, 50, \"PLAY\", 23);\n",
            }, frames: 0, surface);
            Assert(result.Ok, "The text layout script failed: " + string.Join(" | ", result.Errors));
            double w = result.Numbers["w"], ws = result.Numbers["ws"], h = result.Numbers["h"];
            Assert(w > 20 && w < 120, $"\"PLAY\" at 23 px measured {w:F1} px wide.");
            Assert(Math.Abs(ws - w - 24) < 0.01, $"8 px of letter spacing over four letters added {ws - w:F2} px, not 24.");
            Assert(h > 20 && h < 40, $"The line height of 23 px Arial measured {h:F1} px.");
            Assert(surface.Texts.Count == 2, $"Expected two texts, got {surface.Texts.Count}.");
            Assert(surface.Texts[0].X == (int)(500 - ws) && surface.Texts[0].Tracking == 8,
                $"Right-aligned text started at {surface.Texts[0].X} (expected {(int)(500 - ws)}) with spacing {surface.Texts[0].Tracking}.");
            Assert(surface.Texts[1].X == (int)(500 - (ws / 2)),
                $"Centred text started at {surface.Texts[1].X} (expected {(int)(500 - (ws / 2))}).");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Gui.LetterSpacingMovesEachGlyphAlong", () =>
        {
            using GlyphAtlas atlas = new();
            List<GlyphQuad> plain = [], spaced = [];
            atlas.LayoutRun("AB", "Arial", 20, false, 0, 0, plain);
            atlas.LayoutRun("AB", "Arial", 20, false, 0, 0, spaced, 10);
            Assert(plain.Count == 2 && spaced.Count == 2, "Two letters did not lay out as two glyphs.");
            Assert(Math.Abs(spaced[0].X - plain[0].X) < 0.01 && Math.Abs(spaced[1].X - plain[1].X - 10) < 0.01,
                $"Spacing of 10 moved the glyphs by {spaced[0].X - plain[0].X:F2} and {spaced[1].X - plain[1].X:F2}.");
            Assert(Math.Abs(atlas.MeasureRun("AB", "Arial", 20, false, 10) - atlas.MeasureRun("AB", "Arial", 20, false) - 10) < 0.01,
                "A spaced run's measured width does not include its spacing.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Gui.RoundedShapesCoverEachPixelOnceWithSmoothEdges", () =>
        {
            PgslRecordingDrawSurface surface = new();
            ObjectSandboxResult result = ObjectSandbox.Run(new Dictionary<string, string>
            {
                ["Create"] = "DrawSetAlpha(0.5);\nDrawRoundRectEx(10, 10, 110, 60, 12, 0);\nDrawCircleEx(200, 200, 30, 4);\n",
            }, frames: 0, surface);
            Assert(result.Ok, "The shapes script failed: " + string.Join(" | ", result.Errors));
            var hits = new Dictionary<(int X, int Y), (int Count, double Alpha)>();
            foreach (var rect in surface.Rectangles)
            {
                Assert(rect.Filled && rect.H == 1, "Smooth shapes are drawn as filled one-pixel rows.");
                for (int x = (int)rect.X; x < (int)(rect.X + rect.W); x++)
                {
                    var key = (x, (int)rect.Y);
                    hits.TryGetValue(key, out var hit);
                    hits[key] = (hit.Count + 1, hit.Alpha + (rect.Color.A / 255.0));
                }
            }
            Assert(hits.Values.All(hit => hit.Count == 1), "A pixel of a translucent shape was drawn more than once (darker seams).");
            double Covered(Func<(int X, int Y), bool> where) => hits.Where(pair => where(pair.Key)).Sum(pair => pair.Value.Alpha) / 0.5;
            double box = Covered(key => key.X < 150);
            double expectedBox = (100 * 50) - ((4 - Math.PI) * 12 * 12);
            Assert(Math.Abs(box - expectedBox) < expectedBox * 0.01, $"The rounded rectangle covered {box:F1} px, expected {expectedBox:F1}.");
            double ring = Covered(key => key.X >= 150);
            double expectedRing = Math.PI * ((30 * 30) - (26 * 26));
            Assert(Math.Abs(ring - expectedRing) < expectedRing * 0.02, $"The ring covered {ring:F1} px, expected {expectedRing:F1}.");
            Assert(!hits.ContainsKey((10, 10)) && !hits.ContainsKey((200, 200)), "A rounded corner or the ring's middle was filled.");
            Assert(Math.Abs(hits[(60, 35)].Alpha - (127 / 255.0)) < 0.01, "The rectangle's inside is not the draw alpha.");
            Assert(hits.Values.Any(hit => hit.Alpha > 0.05 && hit.Alpha < 0.45), "No edge pixel is partly covered: the edges are not smooth.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Gui.GradientShadesFromOneColourToTheOther", () =>
        {
            PgslRecordingDrawSurface surface = new();
            ObjectSandboxResult result = ObjectSandbox.Run(new Dictionary<string, string>
            {
                ["Create"] = "DrawRectangleGradient(0, 0, 100, 50, 255, 0, 0, 1, 0, 0, 255, 0.5, true);\n",
            }, frames: 0, surface);
            Assert(result.Ok && surface.Rectangles.Count == 50, $"A 50 px tall gradient drew {surface.Rectangles.Count} bands: " + string.Join(" | ", result.Errors));
            var first = surface.Rectangles[0];
            var last = surface.Rectangles[^1];
            Assert(first.Y == 0 && first.H == 1 && first.W == 100 && last.Y == 49, "The gradient's bands are not one pixel rows across the rectangle.");
            Assert(first.Color.R == 255 && first.Color.B == 0 && first.Color.A == 255 && last.Color.B == 255 && last.Color.R == 0 && last.Color.A == 128,
                $"The gradient runs from {first.Color} to {last.Color}.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Gui.AClipLimitsLaterDrawingUntilReset", () =>
        {
            OverlayCommandList list = new();
            list.Reset(1280, 720);
            PgslRenderDrawSurface gui = new(null, new OverlayHudCanvas(list, 1280, 720), 1280, 720, isGui: true);
            gui.SetClip(new RectangleF(10, 20, 300, 40));
            gui.FillRectangle(Color.White, new RectangleF(0, 0, 400, 100));
            gui.DrawTextRun("SPACED", "Arial", 20, Color.White, 15, 25, 6);
            gui.SetClip(RectangleF.Empty);
            gui.FillRectangle(Color.White, new RectangleF(0, 0, 400, 100));
            Assert(list.Count == 3, $"Expected three overlay commands, got {list.Count}.");
            Vector4 clip = new(10, 20, 300, 40);
            Assert(list.Commands[0].Clip == clip && list.Commands[1].Clip == clip, "Drawing after DrawSetClip was not limited to the clip.");
            Assert(list.Commands[1].Tracking == 6, "Letter spacing did not reach the overlay.");
            Assert(list.Commands[2].Clip == default, "Drawing after DrawResetClip was still clipped.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Effects2D.MouseButtonsAndSmoothEffectSpritesPreserveContract", () =>
        {
            Assert(PgslCommands.MbRight == (int)MouseButton.Right && PgslCommands.MbMiddle == (int)MouseButton.Middle, "Right/middle mouse mapping is swapped.");
            PgslRenderDrawSurface gui = new(null, null, 1440, 810, isGui: true);
            PgslRenderDrawSurface world = new(null, null, 1440, 810);
            Assert(gui.SpriteDepth == -10000 && world.SpriteDepth == 0, "GUI sprites must share the panel layer without changing world sprite depth.");
            RecordingSink sink = new(); ViewportSpriteSink viewport = new(sink, new Rectangle(0, 40, 1280, 720));
            viewport.DrawSprite(new SpriteDrawCall { Texture = new TextureHandle(1), X = 10, Y = 10, Width = 20, Height = 20, SmoothSampling = true });
            Assert(sink.Calls.Count == 1 && sink.Calls[0].SmoothSampling && sink.Calls[0].Y == 50, "Viewport clipping lost smooth effect sampling.");
            Assert(!new SpriteDrawCall().SmoothSampling, "Ordinary pixel sprites should inherit the existing room sampler.");
        });
    }

    private static void Assert(bool condition, string message) => HeadlessHarness.Assert(condition, message);

    private sealed class OrderRecordingHud : Genesis.Runtime.Scripting.IHudCanvas
    {
        public List<string> Order { get; } = [];
        public int Width => 1280;
        public int Height => 720;
        public void Text(string text, float x, float y, float size, Vector4 color) => Order.Add("text");
        public void TextCentered(string text, float centerX, float y, float width, float size, Vector4 color) => Order.Add("text");
        public void Rect(float x, float y, float w, float h, Vector4 color, bool filled = true) => Order.Add("rect");
        public void Line(float x1, float y1, float x2, float y2, Vector4 color, float thickness = 1.5f) => Order.Add("line");
    }

    private sealed class RecordingSink : IRenderCommandSink
    {
        public List<SpriteDrawCall> Calls { get; } = [];
        public void DrawSprite(in SpriteDrawCall call) => Calls.Add(call);
        public void DrawSpriteBatch(ReadOnlySpan<SpriteDrawCall> calls) { foreach (SpriteDrawCall call in calls) Calls.Add(call); }
        public void DrawMesh(in MeshDrawCall call) { }
        public void DrawMeshBatch(ReadOnlySpan<MeshDrawCall> calls) { }
    }
}
