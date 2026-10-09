using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Runtime;
using Genesis.Runtime.Project;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using Genesis.World.Foliage;
using Genesis.World.Terrain;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Project post effects: a project's Fullscreen Shader resources run over the finished frame. The
/// ink outline is one such shader, kept in the project (Fixtures/InkOutline.hlsl), not in the engine.
/// </summary>
internal static class PostEffectsSuite
{
    private const string InvertSource = """
        cbuffer GenesisFrame : register(b4) { float Time; float Frame; float2 Resolution; };
        cbuffer GenesisParameters : register(b5) { float Amount; };
        Texture2D SceneColor : register(t0);
        struct PreviewVSOut { float4 SvPos : SV_Position; float2 UV : TEXCOORD0; };
        float4 MainPS(PreviewVSOut IN) : SV_Target
        {
            float4 c = SceneColor.Load(int3(int2(IN.SvPos.xy), 0));
            c.rgb = lerp(c.rgb, 1.0 - c.rgb, Amount);
            return c;
        }
        """;

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "PostEffects");
        string parent = Path.Combine(ctx.Workspace, "PostEffects");
        Directory.CreateDirectory(parent);
        ProjectSession project = new ProjectService().CreateProject(parent, "Post Effects", "Blank");
        ResourceService resources = new(project);

        string invert = WriteShader(resources, "Invert", InvertSource, ("Amount", 1f));
        string inkSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "InkOutline.hlsl"));
        WriteShader(resources, "Ink Outline", inkSource,
            ("Opacity", 1f), ("WidthPixels", 2f), ("DepthStep", 0.012f), ("CreaseDegrees", 32f),
            ("InkColor", new[] { 0.10f, 0.06f, 0.04f }), ("FullWidthDistance", 6f), ("FarDistance", 40f),
            ("FarOpacity", 0.6f), ("FadeOutDistance", 80f), ("FoliageDistance", 20f));
        ResourceCatalog.Invalidate(project.RootPath);

        string terrainPath = resources.CreateResource(Path.Combine(resources.AssetsRoot, "Terrain"), ResourceKind.Terrain, "Meadow");
        using Form host = GateSuite.NewHost(1120, 800);
        TerrainEditorControl editor = new(terrainPath, project.RootPath);
        host.Controls.Add(editor);
        GateSuite.ShowHost(host);
        GateSuite.Pump(8, 25);
        IReadOnlyList<PostEffectRequest> requests = Array.Empty<PostEffectRequest>();
        string? compileError = null;
        editor.Viewport.DrawScene += renderer =>
        {
            renderer.SetPostEffects(requests);
            if (!string.IsNullOrEmpty(renderer.LastPostEffectError)) compileError = renderer.LastPostEffectError;
        };
        Bitmap Capture(string file)
        {
            GateSuite.Pump(3, 20);
            Bitmap? frame = editor.Viewport.CaptureFrame(settleFrames: 6);
            HeadlessHarness.Assert(frame is not null, "The terrain viewport could not be read back.");
            frame!.Save(Path.Combine(ctx.Captures, file), ImageFormat.Png);
            return frame;
        }
        void Use(params string[] effects)
        {
            ProjectPostEffects.SetRoomEffects(effects);
            requests = ProjectPostEffects.RequestsFor(project.RootPath).ToList();
        }

        try
        {
            editor.ApplyGeneration(new TerrainGenParams
            {
                Preset = TerrainPreset.Flatlands, ResolutionX = 257, ResolutionZ = 257, CellSize = 1f,
                MinHeight = -2f, MaxHeight = 6f, Seed = 5101,
            });
            editor.ScatterFoliage(new FoliageScatterSettings
            {
                Seed = 5102, Preset = FoliagePreset.Meadow, MaximumInstances = 40000, Density = 1f,
                MinimumSpacing = 0.6f, NearDistance = 40f, FarDistance = 200f, StreamingCellSize = 16f,
                VisibleInstanceBudget = 30000, TriangleBudget = 600000,
            });
            FoliageInstance standing = editor.Foliage.Instances
                .OrderBy(instance => (instance.Position.X * instance.Position.X) + (instance.Position.Z * instance.Position.Z))
                .First();
            editor.Viewport.Camera.Target = standing.Position + (Vector3.UnitY * 1.6f);
            editor.Viewport.Camera.Distance = 4f;
            editor.Viewport.Camera.Pitch = -0.12f;
            editor.Viewport.Camera.Yaw = 0.4f;

            Use();
            using Bitmap plain = Capture("post-none.png");

            HeadlessHarness.RunCase(ctx.Report, "Engine.Rendering.PostEffects.AProjectShaderRunsOverTheFinishedFrame", () =>
            {
                Use("Invert");
                using Bitmap inverted = Capture("post-invert.png");
                Check(compileError is null, "The post effect did not compile: " + compileError);
                int matching = 0, total = 0;
                for (int y = 40; y < plain.Height - 40; y += 23)
                {
                    for (int x = 40; x < plain.Width - 40; x += 23)
                    {
                        Color a = plain.GetPixel(x, y), b = inverted.GetPixel(x, y);
                        if (Math.Abs(a.R + b.R - 255) < 24 && Math.Abs(a.G + b.G - 255) < 24 && Math.Abs(a.B + b.B - 255) < 24) matching++;
                        total++;
                    }
                }
                Check(matching > total * 0.8, $"The frame was not inverted by the project's shader: {matching} of {total} samples match.");
                ProjectPostEffects.SetParameter("Invert", "Amount", 0f);
                requests = ProjectPostEffects.RequestsFor(project.RootPath).ToList();
                using Bitmap undone = Capture("post-invert-zero.png");
                Color centre = plain.GetPixel(plain.Width / 2, plain.Height / 2), back = undone.GetPixel(plain.Width / 2, plain.Height / 2);
                Check(Math.Abs(centre.R - back.R) + Math.Abs(centre.G - back.G) + Math.Abs(centre.B - back.B) < 30,
                    "Setting the effect's parameter to 0 did not take it off again.");
            });

            HeadlessHarness.RunCase(ctx.Report, "Engine.Rendering.PostEffects.InkOutlineShaderDoesNotSpeckleDistantFoliage", () =>
            {
                Use("Ink Outline");
                using Bitmap inked = Capture("post-ink.png");
                Check(compileError is null, "The ink outline shader did not compile: " + compileError);
                int horizon = HorizonRow(plain);
                double h = plain.Height;
                double far = InkedShare(plain, inked, (horizon + 2) / h, Math.Min(0.99, (horizon + (0.12 * h)) / h));
                double near = InkedShare(plain, inked, 0.82, 0.99);
                string measured = $"distant meadow {far:P2} inked, near grass {near:P2}";
                // Inked with no level of detail, 46% of the distant meadow's pixels were.
                Check(near > 0.02, "The outline is not drawn on the near grass: " + measured);
                Check(far < 0.15, "Distant foliage is speckled with ink: " + measured);
            });
        }
        finally
        {
            ProjectPostEffects.Clear();
            editor.Dispose();
        }

        RunInPlayer(ctx);
    }

    /// <summary>
    /// The first post effect a game turns on, in the real Player on DX12 with a shader no cache has
    /// seen: it is compiled on a worker while frames go on being drawn, then runs. It once held one
    /// frame for 10.7 seconds.
    /// </summary>
    private static void RunInPlayer(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Engine.Rendering.PostEffects.TheFirstEffectIsCompiledOffTheFrameInTheGame", () =>
        {
            string runtime = NewestPlayer() ?? throw new InvalidOperationException("No Player found.");
            string parent = Path.Combine(ctx.Workspace, "PostEffectsPlayer");
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
            Directory.CreateDirectory(parent);
            ProjectSession project = new ProjectService().CreateProject(parent, "Post Effects Game", "Blank");
            ResourceService resources = new(project);
            // A source no shader cache holds: the first ask compiles it.
            WriteShader(resources, "Fresh Invert", InvertSource + "\n// " + Guid.NewGuid().ToString("N") + "\n", ("Amount", 1f));
            string objects = Path.Combine(project.AssetsPath, "Objects");
            Directory.CreateDirectory(objects);
            string objectFile = resources.CreateResource(objects, ResourceKind.GameObject, "Asker");
            File.WriteAllText(objectFile, "{\"schemaVersion\":2,\"dimension\":\"ThreeD\",\"components\":[{\"type\":\"ScriptComponent\",\"props\":{\"ScriptClass\":\"Asker\"}}],\"events\":[\"Create\",\"Step\"]}");
            Directory.CreateDirectory(Path.Combine(objects, "Asker"));
            File.WriteAllText(Path.Combine(objects, "Asker", "Create.pgsl"), "stage = 0; asked = 0; t0 = TimeMs();");
            // A picture half a second in, the effect asked for three seconds in, a picture six seconds after that.
            File.WriteAllText(Path.Combine(objects, "Asker", "Step.pgsl"), """
                SetCameraPosition(0, 2, 0); SetCameraTarget(0, 2, 10);
                if (stage == 0 && TimeMs() > t0 + 500) { ScreenshotSave("post-player-before"); stage = 1; }
                if (stage == 1 && ScreenshotPending() == 0 && TimeMs() > t0 + 3000) { PostEffectAdd("Fresh Invert"); asked = TimeMs(); stage = 2; }
                if (stage == 2 && TimeMs() > asked + 6000) { ScreenshotSave("post-player-after"); stage = 3; }
                if (stage == 3 && ScreenshotPending() == 0) { GameQuit(); stage = 4; }
                """);
            string roomFile = ProjectRoomResolver.ResolveRoomFile(project.RootPath, project.Manifest.StartRoom);
            RoomAsset start = RoomAsset.Create("Start", RoomDimension.ThreeD);
            start.Settings.CaptureMouse = false;
            // The engine's sky at a standing noon (a 3D frame for the effect to run over): the
            // pictures before and after differ only by the effect.
            start.Environment.DynamicSky = true;
            start.Environment.Weather = "Clear";
            start.Environment.TimeOfDayHours = 12f;
            start.Environment.TimeScale = 0f;
            start.Nodes.Add(new RoomNode
            {
                Kind = RoomNodeKind.GameObject, Name = "Asker", LayerId = start.Layers[0].Id,
                GameObject = new RoomGameObjectData { Prefab = Path.GetRelativePath(project.RootPath, objectFile).Replace('\\', '/') },
            });
            RoomAssetLoader.Save(start, roomFile);
            ResourceCatalog.Invalidate(project.RootPath);

            string images = ProjectPaths.ImagesDir(project.RootPath);
            foreach (string label in new[] { "worker" })
            {
                string cache = Path.Combine(parent, "ShaderCache-" + label);
                if (Directory.Exists(images)) Directory.Delete(images, recursive: true);
                string log = RunPlayer(runtime, project.RootPath, cache);
                File.WriteAllText(Path.Combine(ctx.Logs, $"post-effects-player-{label}.log"), log);
                string compiled = System.Text.RegularExpressions.Regex.Match(log, @"Post effect 'Fresh Invert' compiled on a worker in \d+ ms").Value;
                HeadlessHarness.Assert(compiled.Length > 0, $"[{label}] The effect was not compiled on a worker. Log:\r\n{Tail(log)}");
                Console.WriteLine($"[PostEffects] {label}: {compiled}");
                // No frame after the effect was asked for (and before the second picture) may wait for it.
                foreach (System.Text.RegularExpressions.Match slow in System.Text.RegularExpressions.Regex.Matches(log,
                    @"Slow frame: (\d+) ms in Start \(frame \d+, ([0-9.]+) s after the room began\)"))
                {
                    double milliseconds = double.Parse(slow.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    double into = double.Parse(slow.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                    HeadlessHarness.Assert(into < 2.0 || into > 8.0 || milliseconds < 250,
                        $"[{label}] A frame of {milliseconds} ms came {into} s into the room, after the effect was asked for: {slow.Value}");
                }

                string before = Path.Combine(images, "post-player-before.png"), after = Path.Combine(images, "post-player-after.png");
                HeadlessHarness.Assert(File.Exists(before) && File.Exists(after), $"[{label}] The game took no pictures. Log:\r\n{Tail(log)}");
                File.Copy(after, Path.Combine(ctx.Captures, $"post-player-{label}.png"), overwrite: true);
                using Bitmap plain = new(before), inverted = new(after);
                int matching = 0, total = 0;
                for (int y = 20; y < plain.Height - 20; y += 29)
                {
                    for (int x = 20; x < plain.Width - 20; x += 29)
                    {
                        Color a = plain.GetPixel(x, y), b = inverted.GetPixel(x, y);
                        if (Math.Abs(a.R + b.R - 255) < 40 && Math.Abs(a.G + b.G - 255) < 40 && Math.Abs(a.B + b.B - 255) < 40) matching++;
                        total++;
                    }
                }
                HeadlessHarness.Assert(matching > total * 0.7, $"[{label}] The effect never ran: {matching} of {total} samples are inverted.");
            }
        });
    }

    private static string RunPlayer(string runtime, string projectPath, string shaderCache)
    {
        System.Diagnostics.ProcessStartInfo start = new(Path.Combine(runtime, RuntimePaths.RuntimeExeName))
        {
            WorkingDirectory = runtime, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment["GENESIS_PROJECT_PATH"] = projectPath;
        start.Environment["GENESIS_UNATTENDED_WINDOW"] = "1";
        start.Environment["GENESIS_RENDER_BACKEND"] = "Direct3D12";
        start.Environment["GENESIS_SHADER_CACHE"] = shaderCache;
        // No start-up warm-up: the first ask must find the shader uncompiled.
        start.Environment["GENESIS_SHADER_WARMUP"] = "0";
        foreach (string name in new[] { "GENESIS_START_ROOM", "GENESIS_AUTOSHOT", "GENESIS_BOOT_COORDINATED" }) start.Environment.Remove(name);
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("The Player did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
        string playerLog = Path.Combine(ProjectPaths.LogsDir(projectPath), "project_player.log");
        // The renderer's own log (Genesis.Rendering.Diagnostics.RenderLog), one per process.
        string renderLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GenesisRuntime", "Logs", "render-" + process.Id + ".log");
        return (File.Exists(playerLog) ? File.ReadAllText(playerLog) : string.Empty)
            + Environment.NewLine + (File.Exists(renderLog) ? File.ReadAllText(renderLog) : "(no render log at " + renderLog + ")")
            + Environment.NewLine + output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult();
    }

    // The newest Player within reach (as PgslProjectSuite finds it).
    private static string? NewestPlayer()
    {
        List<string> candidates = [];
        if (RuntimePaths.ResolveRuntimeDir() is { } resolved) candidates.Add(resolved);
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string published = Path.Combine(directory.FullName, "Genesis Application", "Player");
            if (File.Exists(Path.Combine(published, RuntimePaths.RuntimeExeName))) { candidates.Add(published); break; }
        }
        return candidates
            .Where(path => File.Exists(Path.Combine(path, "Genesis.Runtime.dll")))
            .OrderByDescending(path => File.GetLastWriteTimeUtc(Path.Combine(path, "Genesis.Runtime.dll")))
            .FirstOrDefault();
    }

    private static string Tail(string log) => log.Length > 2500 ? log[^2500..] : log;

    private static string WriteShader(ResourceService resources, string name, string source, params (string Name, object Value)[] parameters)
    {
        string path = resources.CreateResource(Path.Combine(resources.AssetsRoot, "Shaders"), ResourceKind.Shader, name);
        var document = new ShaderAssetDocument
        {
            Pipeline = ShaderAssetPipeline.Fullscreen,
            AuthoringMode = ShaderAuthoringMode.Code,
            TargetType = ShaderTargetType.Fullscreen,
            Entry = "MainPS",
            Source = source,
            Parameters = parameters.Select(p => new ShaderParameterValue
            {
                Name = p.Name,
                Type = p.Value is float[] v ? $"float{v.Length}" : "float",
                Value = p.Value is float[] values ? values : new[] { (float)p.Value },
            }).ToList(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
            WriteIndented = true,
        }));
        return path;
    }

    private static int HorizonRow(Bitmap frame)
    {
        Color sky = frame.GetPixel(frame.Width / 2, 4);
        for (int y = 8; y < frame.Height; y += 2)
        {
            int ground = 0;
            for (int x = 0; x < frame.Width; x += 8)
            {
                Color c = frame.GetPixel(x, y);
                if (Math.Abs(c.R - sky.R) + Math.Abs(c.G - sky.G) + Math.Abs(c.B - sky.B) > 40) ground++;
            }
            if (ground * 8 > frame.Width * 0.7) return y;
        }
        return frame.Height / 2;
    }

    /// <summary>Share of pixels in a band of rows (fractions of the height) that the ink made clearly darker.</summary>
    private static double InkedShare(Bitmap plain, Bitmap inked, double fromRow, double toRow)
    {
        int width = Math.Min(plain.Width, inked.Width), height = Math.Min(plain.Height, inked.Height);
        int top = (int)(height * fromRow), bottom = (int)(height * toRow);
        int darker = 0, total = 0;
        for (int y = top; y < bottom; y += 2)
        {
            for (int x = 0; x < width; x += 2)
            {
                Color a = plain.GetPixel(x, y), b = inked.GetPixel(x, y);
                float la = (a.R * 0.299f) + (a.G * 0.587f) + (a.B * 0.114f);
                float lb = (b.R * 0.299f) + (b.G * 0.587f) + (b.B * 0.114f);
                if (la - lb > 25f) darker++;
                total++;
            }
        }
        return total == 0 ? 0 : darker / (double)total;
    }

    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
