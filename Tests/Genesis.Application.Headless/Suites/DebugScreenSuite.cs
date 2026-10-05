using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Text.Json.Nodes;
using Genesis.Application.Core.Settings;
using Genesis.Application.Runtime;
using Genesis.Application.Studio;
using Genesis.Runtime.Debugger;
using Genesis.Runtime.Diagnostics;
using Genesis.Runtime.Input;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Diagnostics;
using Genesis.Shared.Interfaces;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// The in-game debug screen: its compact strip and tabbed panel, the Resources search and sort,
/// the Game (PGSL) figures, the developer-only Engine category, and recording a profile into a
/// session folder (frames.csv, summary.json, report.md). Captures of the strip and of each tab
/// are written for inspection.
/// </summary>
internal static class DebugScreenSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "DebugScreen");
        string? previousEngine = Environment.GetEnvironmentVariable(DebugCategories.EngineEnvironmentVariable);
        Environment.SetEnvironmentVariable(DebugCategories.EngineEnvironmentVariable, null);
        try
        {
            RunCases(ctx);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DebugCategories.EngineEnvironmentVariable, previousEngine);
            PgslProfiler.Enabled = false;
            PgslProfiler.Reset();
        }
    }

    private static void RunCases(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Runtime.DebugScreen.ResourceFilterSearchAndSort", () =>
        {
            DebugResourceRow[] rows =
            [
                new("Texture", "grass_albedo.png", 1, 4L * 1024 * 1024, "Srgb · Assets/Textures/grass_albedo.png"),
                new("Texture", "rock.png", 1, 512L * 1024, "Srgb · Assets/Textures/rock.png"),
                new("Model", "Tree", 1, 2L * 1024 * 1024, "3 meshes · 1,200 triangles"),
                new("Sound", "footstep.wav", 1, 96L * 1024, "0.3 s"),
                new("Object", "obj_grass_tuft", 120, 0, "live instances"),
                new("Object", "obj_player", 1, 0, "live instances"),
            ];

            List<DebugResourceRow> grass = DebugResourceFilter.Apply(rows, "GRASS");
            HeadlessHarness.Assert(grass.Count == 2 && grass.All(row => row.Name.Contains("grass", StringComparison.OrdinalIgnoreCase)),
                "A plain word must match names case-insensitively: " + string.Join(", ", grass.Select(row => row.Name)));
            List<DebugResourceRow> textures = DebugResourceFilter.Apply(rows, "kind:texture");
            HeadlessHarness.Assert(textures.Count == 2 && textures.All(row => row.Kind == "Texture"), "kind: must keep only that kind.");
            List<DebugResourceRow> big = DebugResourceFilter.Apply(rows, ">1mb");
            HeadlessHarness.Assert(big.Select(row => row.Name).SequenceEqual(["grass_albedo.png", "Tree"]),
                ">1mb must keep rows of at least a megabyte, largest first: " + string.Join(", ", big.Select(row => row.Name)));
            List<DebugResourceRow> both = DebugResourceFilter.Apply(rows, "grass kind:object");
            HeadlessHarness.Assert(both.Count == 1 && both[0].Name == "obj_grass_tuft", "Every word must match (AND).");
            HeadlessHarness.Assert(DebugResourceFilter.Apply(rows, "Assets/Textures").Count == 2, "The detail (path) must be searchable.");
            HeadlessHarness.Assert(DebugResourceFilter.Apply(rows, "nothing-like-this").Count == 0, "A query that matches nothing lists nothing.");

            List<DebugResourceRow> bySize = DebugResourceFilter.Apply(rows, "", DebugResourceSort.Size, descending: true);
            HeadlessHarness.Assert(bySize[0].Name == "grass_albedo.png" && bySize[1].Name == "Tree", "Size sort must put the largest first.");
            List<DebugResourceRow> byCount = DebugResourceFilter.Apply(rows, "", DebugResourceSort.Count, descending: true);
            HeadlessHarness.Assert(byCount[0].Name == "obj_grass_tuft", "Count sort must put the most numerous first.");
            List<DebugResourceRow> byName = DebugResourceFilter.Apply(rows, "", DebugResourceSort.Name, descending: false);
            HeadlessHarness.Assert(byName[0].Name == "footstep.wav" && byName[^1].Name == "Tree", "Name sort must be alphabetical.");

            HeadlessHarness.Assert(DebugResourceFilter.TryParseBytes("1.5mb", out long parsed) && parsed == (long)(1.5 * 1024 * 1024),
                "1.5mb must parse to bytes.");
            HeadlessHarness.Assert(DebugResourceFilter.FormatBytes(4L * 1024 * 1024) == "4.0 MB", "Sizes must read as MB.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.DebugScreen.ResourcesTabTypedSearch", () =>
        {
            object owner = new();
            DebugResourceCatalog.Register("debug-screen-test", owner, () =>
            [
                new DebugResourceRow("Texture", "grass_albedo.png", 1, 4L * 1024 * 1024),
                new DebugResourceRow("Model", "Tree", 1, 2L * 1024 * 1024),
            ]);
            try
            {
                using var overlay = new DebugOverlay();
                OpenPanel(overlay, DebugHudPanel.Resources);
                overlay.IsSearchFocused = true;
                var input = new InputState();
                foreach (char character in "grp") input.OnChar(character);
                overlay.HandleInput(input, debugMode: true);
                HeadlessHarness.Assert(overlay.ResourceSearch == "grp", $"Typed text must reach the search box, got '{overlay.ResourceSearch}'.");
                input.NextFrame();
                input.OnKeyDown(Key.Backspace);
                input.OnKeyDown(Key.P);
                overlay.HandleInput(input, debugMode: true);
                HeadlessHarness.Assert(overlay.ResourceSearch == "gr", "Backspace must delete the last character.");
                HeadlessHarness.Assert(!overlay.IsPaused, "While typing a search, P must not pause the game.");
                overlay.RefreshResourcesNow();
                HeadlessHarness.Assert(overlay.VisibleResourceRows.Count == 1 && overlay.VisibleResourceRows[0].Name == "grass_albedo.png",
                    "The Resources tab must list only rows matching its search.");
                var hud = new RecordingHudCanvas();
                overlay.Draw(hud, renderer: null, hud.Width, hud.Height);
                HeadlessHarness.Assert(hud.Texts.Any(text => text.Contains("grass_albedo.png", StringComparison.Ordinal))
                    && !hud.Texts.Any(text => text.Contains("Tree", StringComparison.Ordinal) && !text.Contains("triangles", StringComparison.Ordinal)),
                    "The drawn Resources tab must show the filtered rows only.\n" + string.Join('\n', hud.Texts));
                input.NextFrame();
                input.OnKeyDown(Key.Enter);
                overlay.HandleInput(input, debugMode: true);
                HeadlessHarness.Assert(!overlay.IsSearchFocused, "Enter must hand the keyboard back to the game.");
            }
            finally
            {
                DebugResourceCatalog.Unregister("debug-screen-test", owner);
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.DebugScreen.RecordingWritesCsvJsonReport", () =>
        {
            (ScriptHostSystem host, _) = TickerScene();
            using var overlay = new DebugOverlay();
            overlay.BindScriptHost(host);
            overlay.ProfilesDirectory = Path.Combine(ctx.Workspace, "DebugScreen", "Profiles");
            overlay.ProjectName = "TinyProject";
            overlay.RoomName = "Room1";
            HeadlessHarness.Assert(!overlay.EngineCategoryEnabled, "Without developer mode the Engine category must be off.");
            HeadlessHarness.Assert(overlay.StartRecording() && overlay.IsRecording, "Recording did not start.");

            var ballast = new List<byte[]>();
            for (int frame = 0; frame < 30; frame++)
            {
                host.Update(1f / 60f);
                if (frame % 5 == 0) ballast.Add(new byte[256 * 1024]);
                if (frame == 12) GC.Collect(0, GCCollectionMode.Forced, blocking: true);
                if (frame == 20) Thread.Sleep(45);
                if (frame == 25)
                    overlay.ReportScriptDiagnostic(new ScriptDiagnostic
                    {
                        TimestampUtc = DateTime.UtcNow, ObjectName = "Ticker", EventName = "Step", Message = "probe failure",
                    });
                overlay.ObserveFrame(1.0, 0.2, 0.5, 0.1, 0.05, renderer: null);
            }

            GC.KeepAlive(ballast);
            string? folder = overlay.StopRecording();
            HeadlessHarness.Assert(folder != null && Directory.Exists(folder), "Stopping did not return the session folder.");
            File.WriteAllText(Path.Combine(ctx.Logs, "debug-screen-profile-folder.txt"), folder);

            string[] csv = File.ReadAllLines(Path.Combine(folder!, DebugProfileRecording.CsvFileName));
            HeadlessHarness.Assert(csv[0] == string.Join(',', DebugProfileRecording.GameColumns),
                "frames.csv must have exactly the Game columns without developer mode: " + csv[0]);
            HeadlessHarness.Assert(csv.Length == 31, $"frames.csv must hold one row per frame (30), found {csv.Length - 1}.");
            int callsColumn = Array.IndexOf(DebugProfileRecording.GameColumns, "pgsl_calls");
            int frameColumn = Array.IndexOf(DebugProfileRecording.GameColumns, "frame_ms");
            int errorColumn = Array.IndexOf(DebugProfileRecording.GameColumns, "script_errors");
            long calls = csv.Skip(1).Sum(line => long.Parse(line.Split(',')[callsColumn], System.Globalization.CultureInfo.InvariantCulture));
            HeadlessHarness.Assert(calls >= 60, $"The Game rows must carry PGSL calls (two tickers x 30 frames), found {calls}.");
            double slowest = csv.Skip(1).Max(line => double.Parse(line.Split(',')[frameColumn], System.Globalization.CultureInfo.InvariantCulture));
            HeadlessHarness.Assert(slowest >= 40, $"The slept frame must be recorded as long (max frame_ms {slowest}).");
            HeadlessHarness.Assert(csv.Skip(1).Any(line => line.Split(',')[errorColumn] == "1"), "The script error must be counted in its frame.");

            JsonNode summary = JsonNode.Parse(File.ReadAllText(Path.Combine(folder!, DebugProfileRecording.SummaryFileName)))!;
            HeadlessHarness.Assert((int)summary["frames"]! == 30, "summary.json must count 30 frames.");
            HeadlessHarness.Assert(summary["engine"] is null && (bool)summary["engineCategory"]! == false,
                "summary.json must have no Engine figures without developer mode.");
            HeadlessHarness.Assert((int)summary["gc"]!["gen0"]! >= 1, "The forced gen0 collection must be counted.");
            HeadlessHarness.Assert((double)summary["allocations"]!["totalMegabytes"]! >= 1.0, "The 1.5 MB of ballast must be counted as allocation.");
            HeadlessHarness.Assert((int)summary["scriptErrors"]!["count"]! == 1, "The script error must be in the summary.");
            JsonArray objects = summary["pgsl"]!["objects"]!.AsArray();
            JsonNode? ticker = objects.FirstOrDefault(item => (string?)item!["object"] == "Ticker");
            HeadlessHarness.Assert(ticker != null && (int)ticker["peakInstances"]! == 2,
                "The hottest objects must include Ticker with its two instances: " + objects.ToJsonString());
            JsonNode? step = summary["pgsl"]!["events"]!.AsArray()
                .FirstOrDefault(item => (string?)item!["object"] == "Ticker" && (string?)item["event"] == "Step");
            HeadlessHarness.Assert(step != null && (long)step["calls"]! == 60,
                "Ticker.Step must have been called 60 times in the recording: " + summary["pgsl"]!["events"]!.ToJsonString());
            JsonArray slow = summary["slowestFrames"]!.AsArray();
            HeadlessHarness.Assert(slow.Count > 0 && (double)slow[0]!["frameMilliseconds"]! >= 40, "The slowest frame must lead slowestFrames.");
            HeadlessHarness.Assert(slow.All(item => item!["parts"] is null), "Slowest frames must not carry Engine parts without developer mode.");

            string report = File.ReadAllText(Path.Combine(folder!, DebugProfileRecording.ReportFileName));
            foreach (string heading in new[] { "# Genesis profile: TinyProject / Room1", "## Summary", "## Warnings", "## Slowest frames",
                         "## Hottest PGSL objects", "## Hottest PGSL events", "## Allocation and GC", "## Script errors" })
                HeadlessHarness.Assert(report.Contains(heading, StringComparison.Ordinal), $"report.md is missing '{heading}'.");
            HeadlessHarness.Assert(report.Contains("| Ticker | 2 |", StringComparison.Ordinal) && report.Contains("Ticker.Step", StringComparison.Ordinal),
                "report.md must name the hottest PGSL object and event.");
            HeadlessHarness.Assert(report.Contains("Hitches:", StringComparison.Ordinal) || report.Contains("Long frames:", StringComparison.Ordinal),
                "report.md must warn about the long frame.");
            HeadlessHarness.Assert(!report.Contains("## Engine", StringComparison.Ordinal), "report.md must have no Engine section without developer mode.");
            File.Copy(Path.Combine(folder!, DebugProfileRecording.ReportFileName), Path.Combine(ctx.Logs, "debug-screen-report.md"), overwrite: true);
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.DebugScreen.EngineCategoryOnlyInDeveloperMode", () =>
        {
            using var player = new DebugOverlay();
            HeadlessHarness.Assert(!player.EngineCategoryEnabled && !player.AvailablePanels.Contains(DebugHudPanel.Engine),
                "An ordinary game must not offer the Engine tab.");
            player.SelectPanel(DebugHudPanel.Engine);
            HeadlessHarness.Assert(player.ActivePanel != DebugHudPanel.Engine, "Selecting Engine without developer mode must not open it.");
            var hud = new RecordingHudCanvas();
            OpenPanel(player, DebugHudPanel.Overview);
            player.Draw(hud, renderer: null, hud.Width, hud.Height);
            HeadlessHarness.Assert(!hud.Texts.Contains("Engine"), "The tab row must not show Engine without developer mode.");

            Environment.SetEnvironmentVariable(DebugCategories.EngineEnvironmentVariable, "1");
            try
            {
                using var developer = new DebugOverlay();
                HeadlessHarness.Assert(developer.EngineCategoryEnabled && developer.AvailablePanels.Contains(DebugHudPanel.Engine),
                    $"{DebugCategories.EngineEnvironmentVariable}=1 must enable the Engine tab.");
                developer.ProfilesDirectory = Path.Combine(ctx.Workspace, "DebugScreen", "DeveloperProfiles");
                HeadlessHarness.Assert(developer.StartRecording(), "Developer recording did not start.");
                for (int frame = 0; frame < 5; frame++) developer.ObserveFrame(1, 0.2, 0.5, 0.1, 0.05, renderer: null);
                string folder = developer.StopRecording() ?? throw new InvalidOperationException("No developer recording folder.");
                string header = File.ReadLines(Path.Combine(folder, DebugProfileRecording.CsvFileName)).First();
                HeadlessHarness.Assert(header == string.Join(',', DebugProfileRecording.GameColumns.Concat(DebugProfileRecording.EngineColumns)),
                    "A developer recording must add the Engine columns: " + header);
                JsonNode summary = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, DebugProfileRecording.SummaryFileName)))!;
                HeadlessHarness.Assert(summary["engine"] is JsonObject && (bool)summary["engineCategory"]!, "A developer summary must carry Engine figures.");
                HeadlessHarness.Assert(File.ReadAllText(Path.Combine(folder, DebugProfileRecording.ReportFileName)).Contains("## Engine (developer)", StringComparison.Ordinal),
                    "A developer report must have its Engine section.");
            }
            finally
            {
                Environment.SetEnvironmentVariable(DebugCategories.EngineEnvironmentVariable, null);
            }

            // Studio passes its preferences to the Player: record on (default) and Engine off (default).
            IDictionary<string, string> defaults = RenderingPreferencesBridge.BuildDebugEnvironment(new GenesisSettings().Runtime);
            HeadlessHarness.Assert(defaults[DebugCategories.RecordOnStartEnvironmentVariable] == "1"
                && defaults[DebugCategories.EngineEnvironmentVariable] == "0",
                "Studio defaults must record when debugging starts and keep the Engine category off.");
            IDictionary<string, string> flipped = RenderingPreferencesBridge.BuildDebugEnvironment(
                new RuntimeSettings { RecordProfileWhenDebugging = false, ShowEngineDebugCategory = true });
            HeadlessHarness.Assert(flipped[DebugCategories.RecordOnStartEnvironmentVariable] == "0"
                && flipped[DebugCategories.EngineEnvironmentVariable] == "1",
                "The preferences must reach the Player environment when changed.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.DebugScreen.ClosedOverlayCostsNothing", () =>
        {
            PgslProfiler.Enabled = false;
            using var overlay = new DebugOverlay();
            overlay.ObserveFrame(1, 0, 0, 0, 0, renderer: null);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int frame = 0; frame < 10_000; frame++) overlay.ObserveFrame(1, 0.2, 0.5, 0.1, 0.05, renderer: null);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            HeadlessHarness.Assert(overlay.Profiler.FrameCount == 0, "A closed, idle debug screen must not sample frames.");
            HeadlessHarness.Assert(!PgslProfiler.Enabled, "A closed, idle debug screen must not switch the PGSL profiler on.");
            HeadlessHarness.Assert(allocated < 1024, $"A closed, idle debug screen must not allocate per frame ({allocated} bytes over 10,000 frames).");

            overlay.IsVisible = true;
            overlay.ObserveFrame(1, 0.2, 0.5, 0.1, 0.05, renderer: null);
            HeadlessHarness.Assert(overlay.Profiler.FrameCount == 1 && PgslProfiler.Enabled, "Opening the debug screen must start sampling.");
            overlay.IsVisible = false;
            overlay.ObserveFrame(1, 0.2, 0.5, 0.1, 0.05, renderer: null);
            HeadlessHarness.Assert(!PgslProfiler.Enabled, "Closing the debug screen must hand the PGSL profiler back.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.DebugScreen.CompactAndFullCaptures", () =>
        {
            Directory.CreateDirectory(ctx.Captures);
            string scenePath = Path.Combine(ctx.Captures, "_debug-screen-scene.png");
            using RuntimeViewportHarness harness = new(1280, 720);
            _ = harness.Capture3D(scenePath);
            IRenderController renderer = harness.Renderer;
            (ScriptHostSystem host, _) = TickerScene();

            object owner = new();
            DebugResourceCatalog.Register("debug-screen-capture", owner, () =>
            [
                new DebugResourceRow("Texture", "grass_albedo.png", 1, 4L * 1024 * 1024, "Srgb · Assets/Textures/grass_albedo.png"),
                new DebugResourceRow("Texture", "rock_normal.png", 1, 1024L * 1024, "Linear · Assets/Textures/rock_normal.png"),
                new DebugResourceRow("Model", "Tree", 1, 2L * 1024 * 1024, "3 meshes · 1,200 triangles"),
                new DebugResourceRow("Sound", "footstep.wav", 1, 96L * 1024, "0.3 s"),
            ]);
            try
            {
                using var overlay = new DebugOverlay();
                overlay.BindScriptHost(host);
                overlay.RoomName = "Room1";
                overlay.IsVisible = true;
                for (int frame = 0; frame < 40; frame++)
                {
                    host.Update(1f / 60f);
                    overlay.Advance(1f / 60f);
                    overlay.ObserveFrame(1.2, 0.3, 0.6, 0.2, 0.1, renderer);
                    Thread.Sleep(2);
                }

                IReadOnlyList<string> strip = overlay.CompactStripTexts;
                HeadlessHarness.Assert(strip.Any(text => text.StartsWith("FPS ", StringComparison.Ordinal))
                    && strip.Any(text => text.StartsWith("CPU ", StringComparison.Ordinal))
                    && strip.Any(text => text.StartsWith("RAM ", StringComparison.Ordinal) && text.Contains("priv", StringComparison.Ordinal))
                    && strip.Any(text => text.StartsWith("Heap ", StringComparison.Ordinal)),
                    "The compact strip must show FPS, CPU, RAM (working set and private) and the managed heap: " + string.Join(" | ", strip));
                HeadlessHarness.Assert(overlay.Profiler.WorkingSetBytes > 0 && overlay.Profiler.PrivateBytes > 0,
                    "Working set and private bytes must come from the process counters.");

                Capture(ctx, scenePath, overlay, renderer, "debug-screen-compact.png", "Debug screen compact strip", null);
                Capture(ctx, scenePath, overlay, renderer, "debug-screen-overview.png", "Debug screen Overview tab", DebugHudPanel.Overview);
                overlay.ResourceSearch = "kind:texture";
                Capture(ctx, scenePath, overlay, renderer, "debug-screen-resources.png", "Debug screen Resources tab", DebugHudPanel.Resources);
                HeadlessHarness.Assert(overlay.VisibleResourceRows.Count >= 2 && overlay.VisibleResourceRows.All(row => row.Kind == "Texture"),
                    "The captured Resources tab must be filtered to textures.");
                Capture(ctx, scenePath, overlay, renderer, "debug-screen-game.png", "Debug screen Game tab", DebugHudPanel.Game);
                overlay.EngineCategoryEnabled = true;
                Capture(ctx, scenePath, overlay, renderer, "debug-screen-engine.png", "Debug screen Engine tab (developer)", DebugHudPanel.Engine);
            }
            finally
            {
                DebugResourceCatalog.Unregister("debug-screen-capture", owner);
            }
        });
    }

    private static void OpenPanel(DebugOverlay overlay, DebugHudPanel panel)
    {
        overlay.IsVisible = true;
        overlay.SelectPanel(panel);
        overlay.ShowExpandedPanels = true;
    }

    /// <summary>A tiny scene: two instances of a Ticker object whose Step event counts.</summary>
    private static (ScriptHostSystem Host, EcsWorld World) TickerScene()
    {
        Genesis.Runtime.Scripting.VM.VMEngine.Initialize();
        ScriptAssetRegistry.ClearCache();
        var host = new ScriptHostSystem();
        EcsWorld world = new();
        var events = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Create"] = "var ticks = 0;\n",
            ["Step"] = "ticks = ticks + 1;\n",
        };
        using (host.UseEventSources(events))
        {
            host.Attach(world, world.CreateEntity(), "Ticker");
            host.Attach(world, world.CreateEntity(), "Ticker");
        }

        return (host, world);
    }

    private static void Capture(HeadlessContext ctx, string scenePath, DebugOverlay overlay, IRenderController renderer,
        string fileName, string label, DebugHudPanel? panel)
    {
        overlay.IsVisible = true;
        if (panel is { } selected) OpenPanel(overlay, selected);
        else overlay.ShowExpandedPanels = false;
        overlay.RefreshResourcesNow();

        using Bitmap scene = new(scenePath);
        using Bitmap composite = new(scene.Width, scene.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(composite))
        {
            graphics.DrawImage(scene, 0, 0, scene.Width, scene.Height);
            using var hud = new GdiHudCanvas(graphics, composite.Width, composite.Height);
            overlay.Draw(hud, renderer, composite.Width, composite.Height);
        }

        string path = Path.Combine(ctx.Captures, fileName);
        composite.Save(path, ImageFormat.Png);
        Genesis.Application.Runtime.ImageMetrics metrics = Genesis.Application.Runtime.ImageMetrics.Measure(composite);
        ctx.Report.Images.Add(new ImageResult(label, fileName, metrics.Width, metrics.Height, metrics.UniqueSampledColors, metrics.AverageLuminance));
        HeadlessHarness.Assert(metrics.UniqueSampledColors >= 6, $"'{fileName}' capture looks blank ({metrics.UniqueSampledColors} colours).");
    }

    /// <summary>Records what the overlay draws, for asserting on its text.</summary>
    private sealed class RecordingHudCanvas : IHudCanvas
    {
        public List<string> Texts { get; } = new();
        public int RectCount { get; private set; }
        public int LineCount { get; private set; }
        public int Width => 1280;
        public int Height => 720;
        public void Text(string text, float x, float y, float size, Vector4 color) => Texts.Add(text);
        public void TextCentered(string text, float cx, float y, float w, float size, Vector4 color) => Texts.Add(text);
        public void Rect(float x, float y, float w, float h, Vector4 color, bool filled = true) => RectCount++;
        public void Line(float x1, float y1, float x2, float y2, Vector4 color, float thickness = 1.5f) => LineCount++;
    }

    /// <summary>Draws the overlay with GDI+ onto a capture of a rendered scene.</summary>
    private sealed class GdiHudCanvas : IHudCanvas, IDisposable
    {
        private readonly Graphics _graphics;
        private readonly Dictionary<int, Font> _fonts = new();

        public GdiHudCanvas(Graphics graphics, int width, int height)
        {
            _graphics = graphics;
            Width = width;
            Height = height;
            _graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            _graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        }

        public int Width { get; }
        public int Height { get; }

        private Font FontFor(float size)
        {
            int key = Math.Max(8, (int)Math.Round(size * 1.25f));
            if (!_fonts.TryGetValue(key, out Font? font)) _fonts[key] = font = new Font("Segoe UI", key, FontStyle.Regular, GraphicsUnit.Pixel);
            return font;
        }

        public Vector2 MeasureText(string text, float size, string? font = null)
        {
            SizeF measured = _graphics.MeasureString(text ?? string.Empty, FontFor(size));
            return new Vector2(measured.Width, measured.Height);
        }

        public void Text(string text, float x, float y, float size, Vector4 color)
        {
            if (string.IsNullOrEmpty(text)) return;
            using SolidBrush brush = new(ToColor(color));
            _graphics.DrawString(text, FontFor(size), brush, x, y - 2f);
        }

        public void TextCentered(string text, float centerX, float y, float width, float size, Vector4 color)
        {
            Vector2 measured = MeasureText(text, size);
            Text(text, centerX - measured.X * 0.5f, y, size, color);
        }

        public void Rect(float x, float y, float w, float h, Vector4 color, bool filled = true)
        {
            if (w <= 0f || h <= 0f) return;
            if (filled)
            {
                using SolidBrush brush = new(ToColor(color));
                _graphics.FillRectangle(brush, x, y, w, h);
            }
            else
            {
                using Pen pen = new(ToColor(color), 1f);
                _graphics.DrawRectangle(pen, x, y, w - 1f, h - 1f);
            }
        }

        public void Line(float x1, float y1, float x2, float y2, Vector4 color, float thickness = 1.5f)
        {
            using Pen pen = new(ToColor(color), Math.Max(1f, thickness));
            _graphics.DrawLine(pen, x1, y1, x2, y2);
        }

        private static Color ToColor(Vector4 c) => Color.FromArgb(
            Math.Clamp((int)(c.W * 255f), 0, 255), Math.Clamp((int)(c.X * 255f), 0, 255),
            Math.Clamp((int)(c.Y * 255f), 0, 255), Math.Clamp((int)(c.Z * 255f), 0, 255));

        public void Dispose()
        {
            foreach (Font font in _fonts.Values) font.Dispose();
            _fonts.Clear();
        }
    }
}
