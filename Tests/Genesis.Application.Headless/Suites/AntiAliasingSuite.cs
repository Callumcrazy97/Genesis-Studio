using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Rendering.Core;
using Genesis.Rendering.Viewport;
using Genesis.Runtime.Project;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Post-process anti-aliasing (FXAA and SMAA) and graphics quality tiers. A white shape with a
/// shallow top edge and a steep side on black, with GUI text over it: without anti-aliasing the
/// edges hold no value between black and white; with FXAA or SMAA they do; the GUI text is the
/// same to the last pixel either way, and a project post effect sees the anti-aliased picture.
/// On every renderer (the CPU rasterizer runs no anti-aliasing and must draw exactly as before).
/// </summary>
internal static class AntiAliasingSuite
{
    private const int Width = 640, Height = 360;
    // A name of its own each run: the focused project may already hold one from an earlier run.
    private static readonly string InvertShader = "AA Invert " + Guid.NewGuid().ToString("N")[..8];
    private const string InvertSource = """
        cbuffer GenesisFrame : register(b4) { float Time; float Frame; float2 Resolution; };
        Texture2D SceneColor : register(t0);
        struct PreviewVSOut { float4 SvPos : SV_Position; float2 UV : TEXCOORD0; };
        float4 MainPS(PreviewVSOut IN) : SV_Target
        {
            float4 c = SceneColor.Load(int3(int2(IN.SvPos.xy), 0));
            return float4(1.0 - saturate(c.rgb), c.a);
        }
        """;

    // The GUI text's box and where the edges run (screen pixels of the 640x360 frame).
    private static readonly Rectangle GuiBox = new(8, 6, 380, 56);

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "AntiAliasing");
        ProjectSession project = ctx.Project ?? throw new InvalidOperationException("No focused project.");
        ResourceService resources = ctx.Resources ?? throw new InvalidOperationException("No resource service.");
        try
        {
            PgslCommands.RenderSetMotionBlur(0);
            RunSettingCases(ctx, project);
            RunQualityTierCases(ctx);
            RunEdgeCase(ctx, project, resources);
            RunTierFramesCase(ctx, project);
        }
        finally
        {
            RenderQuality.Reset();
            PgslCommands.RenderSetMotionBlur(0);
            ProjectPostEffects.Clear();
        }
    }

    // ── Settings and commands ────────────────────────────────────────────────

    private static void RunSettingCases(HeadlessContext ctx, ProjectSession project)
    {
        HeadlessHarness.RunCase(ctx.Report, "Render.AntiAliasing.OffByDefaultAndCommandsReachTheFrameState", () =>
        {
            RenderQuality.Reset();
            Check(Mesh3DState.Default.AntiAliasing == AntiAliasingMode.Off, "A frame's state must start without anti-aliasing.");
            Check(MeshLightingDefaults.AntiAliasing == AntiAliasingMode.Off, "Anti-aliasing must be off unless something asks for it.");
            Check(new ProjectRenderingSettings().AntiAliasing == "Off", "A new project's anti-aliasing must be Off.");

            PgslCommands.WarmRegistry();
            foreach (string qualified in new[]
                     {
                         "RenderSetAntiAliasing", "RenderGetAntiAliasing", "RenderSetQuality", "RenderGetQuality",
                         "Engine.Rendering.AntiAliasing", "Engine.Rendering.QualityTier", "Engine.Rendering.SetQuality",
                         "Engine.Rendering.ShadowResolution", "Engine.Rendering.VolumetricFogQuality",
                         "RenderSetMotionBlur", "RenderGetMotionBlur", "Engine.Rendering.MotionBlur",
                     })
            {
                Check(PgslCommandRegistry.TryGet(qualified) is { IsImplemented: true }, $"'{qualified}' is not a registered PGSL command.");
            }

            Check(PgslCommands.RenderSetAntiAliasing("SMAA") && PgslCommands.RenderGetAntiAliasing() == "smaa",
                "RenderSetAntiAliasing(\"SMAA\") did not choose SMAA.");
            Mesh3DState state = Mesh3DState.Default;
            MeshLightingDefaults.Apply(ref state);
            Check(state.AntiAliasing == AntiAliasingMode.Smaa, "The chosen anti-aliasing did not reach the frame's state.");
            Check(!PgslCommands.RenderSetAntiAliasing("PgslAutoTest") && PgslCommands.RenderGetAntiAliasing() == "smaa",
                "An unknown name must be refused and change nothing.");
            PgslCommands.AntiAliasing = "fxaa";
            Check(PgslCommands.AntiAliasing == "fxaa" && MeshLightingDefaults.AntiAliasing == AntiAliasingMode.Fxaa,
                "Engine.Rendering.AntiAliasing did not set FXAA.");
            Mesh3DState own = Mesh3DState.Default;
            own.AntiAliasing = AntiAliasingMode.Smaa;
            MeshLightingDefaults.Apply(ref own);
            Check(own.AntiAliasing == AntiAliasingMode.Smaa, "A viewport that asks for its own anti-aliasing must keep it.");
            Check(PgslCommands.RenderSetAntiAliasing("off") && MeshLightingDefaults.AntiAliasing == AntiAliasingMode.Off,
                "RenderSetAntiAliasing(\"off\") did not turn it off.");

            // Camera motion blur: off by default, 0 to 1, reaching the frame's state.
            Check(Mesh3DState.Default.MotionBlur == 0f && PgslCommands.RenderGetMotionBlur() == 0, "Motion blur must be off by default.");
            PgslCommands.RenderSetMotionBlur(0.5);
            Mesh3DState blurred = Mesh3DState.Default;
            MeshLightingDefaults.Apply(ref blurred);
            Check(Math.Abs(blurred.MotionBlur - 0.5f) < 1e-6f, "RenderSetMotionBlur(0.5) did not reach the frame's state.");
            PgslCommands.MotionBlur = 7f;
            Check(PgslCommands.RenderGetMotionBlur() == 1, "Motion blur above 1 must clamp to 1.");
            PgslCommands.RenderSetMotionBlur(double.NaN);
            Check(PgslCommands.RenderGetMotionBlur() == 1, "A NaN amount must be ignored.");
            PgslCommands.RenderSetMotionBlur(0);
            Check(PgslCommands.RenderGetMotionBlur() == 0, "RenderSetMotionBlur(0) did not turn it off.");

            // The project's own setting, saved and read back the way the Player reads it.
            string previous = project.Manifest.Rendering.AntiAliasing;
            try
            {
                project.Manifest.Rendering.AntiAliasing = "SMAA";
                new ProjectService().Save(project);
                string file = Directory.GetFiles(project.RootPath, "*.genesisproj").Single();
                Check(File.ReadAllText(file).Contains("\"antiAliasing\": \"SMAA\"", StringComparison.Ordinal),
                    "The project file does not hold the anti-aliasing setting.");
                Check(ProjectPaths.ReadAntiAliasing(project.RootPath) == AntiAliasingMode.Smaa,
                    "The Player does not read SMAA back from the project file.");
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(MeshLightingDefaults.AntiAliasingEnvironmentVariable)))
                {
                    ProjectPaths.ApplyProjectAntiAliasing(project.RootPath);
                    Check(MeshLightingDefaults.AntiAliasing == AntiAliasingMode.Smaa, "Starting the game did not apply the project's SMAA.");
                }
            }
            finally
            {
                project.Manifest.Rendering.AntiAliasing = previous;
                new ProjectService().Save(project);
                MeshLightingDefaults.AntiAliasing = AntiAliasingMode.Off;
            }
            Check(ProjectPaths.ReadAntiAliasing(project.RootPath) == AntiAliasingMode.Off, "Setting it back to Off was not saved.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Studio.Preferences.Project.AntiAliasingIsSavedWithTheProject", () =>
        {
            Genesis.Application.Core.Settings.SettingsService settings = new(Path.Combine(ctx.OutputRoot, "UserData", "anti-aliasing-preferences.json"));
            // With no project open there is no Project page; Preferences must still open.
            using (Genesis.Application.Studio.Forms.PreferencesForm withoutProject = new(settings, null))
                Check(!withoutProject.SelectCategory("Project"), "Preferences shows a Project page with no project open.");
            using Genesis.Application.Studio.Forms.PreferencesForm preferences = new(settings, project);
            GateSuite.ShowHost(preferences);
            GateSuite.Pump(3, 15);
            Check(preferences.SelectCategory("Project"), "Preferences has no Project page.");
            GateSuite.Pump(3, 15);
            ComboBox picker = Descendants(preferences).OfType<ComboBox>().Single(combo => combo.Name == "AntiAliasingPicker");
            Check(picker.Visible && picker.SelectedIndex == 0 && string.Join(",", picker.Items.Cast<object>()) == "Off,FXAA,SMAA",
                $"The Project page does not offer Off/FXAA/SMAA with Off chosen: visible={picker.Visible} index={picker.SelectedIndex}.");
            Button apply = Descendants(preferences).OfType<Button>().Single(button => button.Name == "ApplyPreferences");
            try
            {
                picker.SelectedIndex = 2;
                apply.PerformClick();
                GateSuite.Pump(2, 15);
                for (Control? parent = picker.Parent; parent is not null; parent = parent.Parent)
                    if (parent is ScrollableControl { AutoScroll: true } scroller) { scroller.ScrollControlIntoView(picker); break; }
                GateSuite.Pump(2, 15);
                string capture = "anti-aliasing-preferences.png";
                ctx.Report.Images.Add(ImageResult.From("Studio.Preferences.Project.AntiAliasing", capture,
                    VisualCapture.CaptureOpenForm(preferences, Path.Combine(ctx.Captures, capture))));
                Check(new ProjectService().OpenProject(project.RootPath).Manifest.Rendering.AntiAliasing == "SMAA"
                        && ProjectPaths.ReadAntiAliasing(project.RootPath) == AntiAliasingMode.Smaa,
                    "Applying the Project page did not save SMAA to the project file.");
                Check(MeshLightingDefaults.AntiAliasing == AntiAliasingMode.Smaa, "Applying the Project page did not turn SMAA on in Studio's viewports.");
            }
            finally
            {
                picker.SelectedIndex = 0;
                apply.PerformClick();
                GateSuite.Pump(2, 15);
                preferences.Close();
                MeshLightingDefaults.AntiAliasing = AntiAliasingMode.Off;
            }
            Check(ProjectPaths.ReadAntiAliasing(project.RootPath) == AntiAliasingMode.Off, "Choosing Off again was not saved.");
        });
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }

    private static void RunQualityTierCases(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Render.QualityTiers.EachTierSetsItsTableAndEachSettingStaysYoursToChange", () =>
        {
            RenderQuality.Reset();
            Check(PgslCommands.RenderGetQuality() == string.Empty, "No tier should be reported before one is set.");
            Check(!PgslCommands.RenderSetQuality("PgslAutoTest"), "An unknown tier must be refused.");

            List<string> table = [];
            foreach (RenderQualityTier tier in Enum.GetValues<RenderQualityTier>())
            {
                string name = RenderQuality.Name(tier);
                Check(PgslCommands.RenderSetQuality(name.ToUpperInvariant()), $"RenderSetQuality(\"{name}\") was refused.");
                Check(PgslCommands.RenderGetQuality() == name && PgslCommands.QualityTier == name, $"The {name} tier does not read back as '{name}'.");
                RenderQualitySettings wanted = RenderQuality.For(tier);
                Check(RenderQuality.Effective == wanted, $"{name}: the engine settings are {RenderQuality.Effective}, expected {wanted}.");

                // A room that asks for ambient occlusion, a third cascade, its own fog grid and
                // cinematic clouds gets what the player's tier says instead.
                Mesh3DState room = Mesh3DState.Default;
                room.GtaoEnabled = true;
                room.ShadowCascadeCount = 3;
                room.VolumetricFogQuality = 2;
                room.AuthoredSkyEnabled = true;
                room.CloudQuality = 3;
                MeshLightingDefaults.Apply(ref room);
                Check(room.GtaoEnabled == wanted.Gtao && room.ShadowCascadeCount == wanted.ShadowCascades
                        && room.ContactShadowsEnabled == wanted.ContactShadows && room.LocalVolumetricsEnabled == wanted.LocalVolumetrics
                        && room.BloomEnabled == wanted.Bloom && room.VolumetricFogQuality == wanted.VolumetricFogQuality
                        && room.CloudQuality == wanted.CloudQuality && room.ShadowMapResolution == wanted.ShadowResolution
                        && room.AntiAliasing == wanted.AntiAliasing,
                    $"{name}: a room's frame state did not follow the tier (AO {room.GtaoEnabled}, cascades {room.ShadowCascadeCount}, "
                    + $"fog {room.VolumetricFogQuality}, clouds {room.CloudQuality}, shadows {room.ShadowMapResolution}, AA {room.AntiAliasing}).");
                table.Add($"{name}: {wanted}");
            }
            File.WriteAllLines(Path.Combine(ctx.Logs, "quality-tiers.txt"), table);

            // After a tier, each setting is still the game's to change, and the tier then reads as custom.
            PgslCommands.RenderSetQuality("high");
            PgslCommands.GtaoEnabled = false;
            Mesh3DState changed = Mesh3DState.Default;
            changed.GtaoEnabled = true;
            MeshLightingDefaults.Apply(ref changed);
            Check(!changed.GtaoEnabled, "Turning ambient occlusion off after a tier must turn it off, even where a room asks for it.");
            Check(PgslCommands.RenderGetQuality() == "custom", $"After a change the tier reads '{PgslCommands.RenderGetQuality()}', expected 'custom'.");
            string custom = PgslCommands.QualityTier;
            PgslCommands.QualityTier = custom;   // read and written back: nothing changes
            Check(!MeshLightingDefaults.GtaoEnabled && PgslCommands.RenderGetQuality() == "custom", "Writing 'custom' back changed the settings.");
            Check(PgslCommands.SetQuality("ultra") && PgslCommands.RenderGetQuality() == "ultra", "Engine.Rendering.SetQuality(\"ultra\") did not apply.");

            PgslCommands.ShadowResolution = 3000;
            Check(PgslCommands.ShadowResolution == 2048, $"3000 should round to 2048, not {PgslCommands.ShadowResolution}.");
            PgslCommands.ShadowResolution = 100;
            Check(PgslCommands.ShadowResolution == 512, $"100 should become the 512 minimum, not {PgslCommands.ShadowResolution}.");
            PgslCommands.ShadowResolution = 0;
            Check(PgslCommands.ShadowResolution == 1024, "0 should go back to the 1024 default.");
            PgslCommands.VolumetricFogQuality = 7;
            Check(PgslCommands.VolumetricFogQuality == 2, "Fog quality above 2 should clamp to 2.");

            RenderQuality.Reset();
            Mesh3DState plain = Mesh3DState.Default;
            plain.GtaoEnabled = true;
            plain.VolumetricFogQuality = 2;
            MeshLightingDefaults.Apply(ref plain);
            Check(plain.GtaoEnabled && plain.VolumetricFogQuality == 2 && plain.ShadowMapResolution == 0
                    && plain.AntiAliasing == AntiAliasingMode.Off && PgslCommands.RenderGetQuality() == string.Empty,
                "Without a tier a room's own settings must stand, exactly as before.");
        });
    }

    // ── Pixels on every renderer ─────────────────────────────────────────────

    private sealed class EdgeScene
    {
        public int Shape;
        public IReadOnlyList<PostEffectRequest> PostEffects = Array.Empty<PostEffectRequest>();
        // The camera turns by this much each frame (radians), for the motion blur check.
        public float Yaw, TurnPerFrame;
    }

    private static void RunEdgeCase(HeadlessContext ctx, ProjectSession project, ResourceService resources)
    {
        HeadlessHarness.RunCase(ctx.Report, "Render.AntiAliasing.EdgesSoftenedGuiUntouchedOnEveryBackend", () =>
        {
            RenderQuality.Reset();
            WriteInvertShader(resources);
            ResourceCatalog.Invalidate(project.RootPath);

            using Form game = GateSuite.NewHost(Width, Height);
            using D3DViewportControl viewport = new() { Dock = DockStyle.Fill, DriveMode = ViewportDriveMode.External, VSync = false };
            EdgeScene scene = new();
            PgslContext scriptContext = new() { RoomWidth = Width, RoomHeight = Height };
            string? postError = null;
            viewport.OnRender += renderer =>
            {
                Mesh3DState state = Mesh3DState.Default;
                state.ShowFloor = false; state.ShowSunVisual = false; state.FogEnabled = false;
                state.LightingEnabled = false; state.ShadowsEnabled = false;
                state.BackgroundColor = Vector3.Zero;
                state.CameraFarPlane = 100f;
                renderer.SetMesh3DState(state);
                renderer.Clear(0f, 0f, 0f);
                renderer.Set3DFrameActive(true);
                scene.Yaw += scene.TurnPerFrame;
                renderer.SetCamera3D(
                    Matrix4x4.CreateLookAt(Vector3.Zero, new Vector3(MathF.Sin(scene.Yaw) * 10, 0, MathF.Cos(scene.Yaw) * 10), Vector3.UnitY),
                    Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, Width / (float)Height, 0.1f, 100f));
                renderer.SetPostEffects(scene.PostEffects);
                if (!string.IsNullOrEmpty(renderer.LastPostEffectError)) postError = renderer.LastPostEffectError;
                ScriptMeshes.BeginFrame();
                scriptContext.DrawSurface = new PgslRenderDrawSurface(renderer, null, renderer.PixelWidth, renderer.PixelHeight,
                    projectPath: project.RootPath, is3DActive: true);
                PgslContext? previous = PgslCommands.BindContext(scriptContext);
                try
                {
                    PgslCommands.DrawMeshResetState();
                    PgslCommands.DrawMeshSetCull(false);
                    PgslCommands.DrawMeshSetShadows(false, false);
                    PgslCommands.DrawMesh3D(scene.Shape, 0, 0, 0, "");
                }
                finally { PgslCommands.BindContext(previous); }
            };
            // The GUI, as a game draws it: the overlay after the frame's own drawing.
            viewport.OnPostFrame += renderer => renderer.ComposeOverlay(canvas =>
            {
                OverlayHudCanvas hud = new(canvas, renderer.PixelWidth, renderer.PixelHeight);
                PgslRenderDrawSurface surface = new(renderer, hud, renderer.PixelWidth, renderer.PixelHeight,
                    projectPath: project.RootPath, isGui: true);
                PgslContext context = new() { RoomWidth = renderer.PixelWidth, RoomHeight = renderer.PixelHeight, DrawSurface = surface };
                PgslContext? previous = PgslCommands.BindContext(context);
                try
                {
                    PgslCommands.DrawSetColor(Color.White);
                    PgslCommands.DrawSetAlpha(1);
                    PgslCommands.DrawTextScaled(16, 12, "GUI 0123 ABC", 28);
                }
                finally { PgslCommands.BindContext(previous); }
            });
            game.Controls.Add(viewport);
            GateSuite.ShowHost(game);

            PgslContext? setup = PgslCommands.BindContext(scriptContext);
            try
            {
                ScriptMeshes.Reset();
                scene.Shape = (int)PgslCommands.MeshCreate();
                // A white quadrilateral whose top edge rises 10 degrees across the frame and whose
                // right side falls steeply. World x runs to the left of the screen.
                (double x, double y) World(double sx, double sy) => (-(sx - 320) / 320 * 10.2640, (180 - sy) / 180 * 5.7735);
                var tl = World(-200, 258.036); var tr = World(444, 142); var br = World(567, 600); var bl = World(-200, 600);
                PgslCommands.MeshAddQuad(scene.Shape, tl.x, tl.y, 10, tr.x, tr.y, 10, br.x, br.y, 10, bl.x, bl.y, 10,
                    0, 0, -1, 0, 0, 1, 1, 255, 255, 255, 1);
            }
            finally { PgslCommands.BindContext(setup); }

            List<string> failures = [];
            List<string> readings = [];
            try
            {
                foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
                {
                    viewport.BackendOverride = backend.Backend;
                    GateSuite.Pump(3, 15);
                    string name = backend.ShortName.ToLowerInvariant();
                    bool software = backend.Backend == RenderBackendOption.Software;
                    scene.PostEffects = Array.Empty<PostEffectRequest>();

                    Bitmap? Frame(string mode)
                    {
                        PgslCommands.RenderSetAntiAliasing(mode);
                        Bitmap? image = viewport.ReadbackFrameToBitmap(3);
                        image?.Save(Path.Combine(ctx.Captures, $"aa-{mode}-{name}.png"), ImageFormat.Png);
                        return image;
                    }

                    using Bitmap? off = Frame("off");
                    using Bitmap? fxaa = Frame("fxaa");
                    AntiAliasingMode fxaaRan = viewport.Renderer?.GetStats().AntiAliasing ?? AntiAliasingMode.Off;
                    using Bitmap? smaa = Frame("smaa");
                    AntiAliasingMode smaaRan = viewport.Renderer?.GetStats().AntiAliasing ?? AntiAliasingMode.Off;
                    if (off is null || fxaa is null || smaa is null || viewport.RenderFaultCount != 0 || off.Width < Width || off.Height < Height)
                    {
                        failures.Add(backend.ShortName + ": no frame " + viewport.LastRenderException);
                        continue;
                    }
                    ctx.Report.Images.Add(ImageResult.From("Render.AntiAliasing.Smaa." + backend.ShortName, $"aa-smaa-{name}.png", VisualCapture.Measure(smaa)));
                    SaveZoom(ctx, off, $"aa-zoom-off-{name}.png");
                    SaveZoom(ctx, fxaa, $"aa-zoom-fxaa-{name}.png");
                    SaveZoom(ctx, smaa, $"aa-zoom-smaa-{name}.png");

                    int bright = off.GetPixel(100, 320).R;
                    EdgeCounts plain = CountEdges(off, bright), soft = CountEdges(fxaa, bright), sharp = CountEdges(smaa, bright);
                    readings.Add($"{backend.ShortName} bright={bright} off={plain} fxaa={soft} smaa={sharp} ran={fxaaRan}/{smaaRan}");

                    bool sameGuiFxaa = SameRegion(off, fxaa, GuiBox), sameGuiSmaa = SameRegion(off, smaa, GuiBox);
                    if (!sameGuiFxaa || !sameGuiSmaa) failures.Add($"{backend.ShortName}: the GUI text changed (FXAA same={sameGuiFxaa}, SMAA same={sameGuiSmaa})");
                    if (InkPixels(off, GuiBox) < 150) failures.Add($"{backend.ShortName}: the GUI text did not draw");
                    foreach (Point flat in new[] { new Point(100, 320), new Point(100, 60), new Point(600, 40), new Point(300, 330) })
                    {
                        if (off.GetPixel(flat.X, flat.Y) != fxaa.GetPixel(flat.X, flat.Y) || off.GetPixel(flat.X, flat.Y) != smaa.GetPixel(flat.X, flat.Y))
                            failures.Add($"{backend.ShortName}: a flat pixel at {flat} changed");
                    }

                    if (software)
                    {
                        // The CPU rasterizer runs no full-screen passes: the picture must not change.
                        if (!SameRegion(off, fxaa, new Rectangle(0, 0, Width, Height)) || !SameRegion(off, smaa, new Rectangle(0, 0, Width, Height)))
                            failures.Add("Software: asking for anti-aliasing changed the picture");
                        if (fxaaRan != AntiAliasingMode.Off || smaaRan != AntiAliasingMode.Off)
                            failures.Add($"Software: reports {fxaaRan}/{smaaRan} anti-aliasing");
                        continue;
                    }

                    if (bright < 120) failures.Add($"{backend.ShortName}: the shape is too dark ({bright}) to judge its edges");
                    if (plain.TopColumns != 0 || plain.SideRows != 0)
                        failures.Add($"{backend.ShortName}: without anti-aliasing the edges already hold in-between values ({plain})");
                    foreach ((string mode, EdgeCounts counts, AntiAliasingMode ran) in new[] { ("FXAA", soft, fxaaRan), ("SMAA", sharp, smaaRan) })
                    {
                        if (counts.TopColumns < counts.TopTotal * 0.6 || counts.SideRows < counts.SideTotal * 0.6)
                            failures.Add($"{backend.ShortName}: {mode} left the edges hard ({counts})");
                        if (!string.Equals(ran.ToString(), mode, StringComparison.OrdinalIgnoreCase))
                            failures.Add($"{backend.ShortName}: the renderer reports {ran} where {mode} was asked for");
                    }

                    // A project post effect (an invert) sees the anti-aliased picture: it runs after.
                    scene.PostEffects = ProjectPostEffectsFor(project.RootPath);
                    PgslCommands.RenderSetAntiAliasing("smaa");
                    using (Bitmap? inverted = viewport.ReadbackFrameToBitmap(4))
                    {
                        scene.PostEffects = Array.Empty<PostEffectRequest>();
                        if (inverted is null || viewport.RenderFaultCount != 0)
                        {
                            failures.Add(backend.ShortName + ": no frame with the post effect " + viewport.LastRenderException);
                        }
                        else
                        {
                            inverted.Save(Path.Combine(ctx.Captures, $"aa-smaa-invert-{name}.png"), ImageFormat.Png);
                            (int matching, int total) = InvertedAlongEdges(smaa, inverted);
                            readings.Add($"{backend.ShortName} smaa+invert matching={matching}/{total}");
                            if (postError != null) failures.Add($"{backend.ShortName}: the post effect did not compile: {postError}");
                            else if (matching < total * 0.95)
                                failures.Add($"{backend.ShortName}: the post effect did not run over the anti-aliased picture ({matching} of {total} edge pixels are its inverse)");
                        }
                    }

                    CheckMotionBlur(ctx, viewport, scene, backend, failures, readings);
                }

                // The CPU rasterizer was skipped above; its motion blur check runs here.
                viewport.BackendOverride = RenderBackendOption.Software;
                GateSuite.Pump(3, 15);
                CheckMotionBlur(ctx, viewport, scene, RenderBackendCatalog.Describe(RenderBackendOption.Software), failures, readings);
            }
            finally
            {
                PgslCommands.RenderSetMotionBlur(0);
                PgslCommands.RenderSetAntiAliasing("off");
                ProjectPostEffects.Clear();
                ScriptMeshes.Reset();
            }
            File.WriteAllLines(Path.Combine(ctx.Logs, "anti-aliasing-readings.txt"), readings);
            Console.WriteLine("AntiAliasing readings: " + string.Join(" | ", readings));
            Check(failures.Count == 0, string.Join(" | ", failures));
        });
    }

    /// <summary>
    /// Camera motion blur with the camera turning 0.6 degrees a frame (about 4 pixels): off, the
    /// steep side stays one hard step; on, it smears sideways over several pixels; with the camera
    /// still, blur on changes nothing; the GUI is untouched. The CPU rasterizer never blurs.
    /// </summary>
    private static void CheckMotionBlur(HeadlessContext ctx, D3DViewportControl viewport, EdgeScene scene,
        RenderBackendDescriptor backend, List<string> failures, List<string> readings)
    {
        string name = backend.ShortName.ToLowerInvariant();
        bool software = backend.Backend == RenderBackendOption.Software;
        PgslCommands.RenderSetAntiAliasing("off");
        try
        {
            scene.Yaw = 0;
            scene.TurnPerFrame = 0.6f * MathF.PI / 180f;
            PgslCommands.RenderSetMotionBlur(0);
            using Bitmap? turning = viewport.ReadbackFrameToBitmap(3);
            PgslCommands.RenderSetMotionBlur(1);
            using Bitmap? blurred = viewport.ReadbackFrameToBitmap(3);
            scene.TurnPerFrame = 0;
            using Bitmap? still = viewport.ReadbackFrameToBitmap(3);
            PgslCommands.RenderSetMotionBlur(0);
            using Bitmap? stillOff = viewport.ReadbackFrameToBitmap(3);
            if (turning is null || blurred is null || still is null || stillOff is null || viewport.RenderFaultCount != 0)
            {
                failures.Add($"{backend.ShortName}: no frame for motion blur {viewport.LastRenderException}");
                return;
            }
            blurred.Save(Path.Combine(ctx.Captures, $"motion-blur-{name}.png"), ImageFormat.Png);
            int bright = still.GetPixel(100, 320).R;
            int hardRows = SideBlurRows(turning, bright, 1), softRows = SideBlurRows(blurred, bright, 2);
            readings.Add($"{backend.ShortName} motion blur: rows with a soft side off={hardRows} on={softRows} of 111");
            if (!SameRegion(turning, blurred, GuiBox)) failures.Add($"{backend.ShortName}: motion blur changed the GUI");
            if (!SameRegion(still, stillOff, new Rectangle(0, 0, Width, Height)))
                failures.Add($"{backend.ShortName}: motion blur changed the picture of a camera that is not moving");
            if (hardRows != 0) failures.Add($"{backend.ShortName}: without motion blur the turning view's side is already soft ({hardRows} rows)");
            if (software)
            {
                if (softRows != 0) failures.Add($"Software: motion blur ran ({softRows} rows)");
            }
            else if (softRows < 111 * 0.8)
            {
                failures.Add($"{backend.ShortName}: motion blur left the side of a turning view hard ({softRows} of 111 rows soft)");
            }
        }
        finally
        {
            PgslCommands.RenderSetMotionBlur(0);
            scene.TurnPerFrame = 0;
            scene.Yaw = 0;
        }
    }

    /// <summary>Rows 230 to 340 holding at least <paramref name="least"/> values between black and white right of x = 300 (the steep side only).</summary>
    private static int SideBlurRows(Bitmap image, int bright, int least)
    {
        int low = Math.Max(8, (int)(bright * 0.1)), high = (int)(bright * 0.9), rows = 0;
        for (int y = 230; y <= 340; y++)
        {
            int between = 0;
            for (int x = 300; x < Width; x++)
            {
                int r = image.GetPixel(x, y).R;
                if (r > low && r < high) between++;
            }
            if (between >= least) rows++;
        }
        return rows;
    }

    /// <summary>
    /// Switching quality tiers while frames are drawn, on every renderer: the sun's shadow maps are
    /// remade at the tier's size and still shadow, ambient occlusion, bloom and contact shadows run
    /// or stop, and nothing faults.
    /// </summary>
    private static void RunTierFramesCase(HeadlessContext ctx, ProjectSession project)
    {
        HeadlessHarness.RunCase(ctx.Report, "Render.QualityTiers.SwitchingTiersRemakesShadowsOnEveryBackend", () =>
        {
            RenderQuality.Reset();
            using Form game = GateSuite.NewHost(Width, Height);
            using D3DViewportControl viewport = new() { Dock = DockStyle.Fill, DriveMode = ViewportDriveMode.External, VSync = false };
            PgslContext scriptContext = new() { RoomWidth = Width, RoomHeight = Height };
            int cube = 0;
            bool shadows = true;
            viewport.OnRender += renderer =>
            {
                Mesh3DState state = Mesh3DState.Default;
                state.ShowFloor = true; state.ShowSunVisual = false; state.FogEnabled = false;
                state.LightingEnabled = true; state.ShadowsEnabled = shadows;
                state.LightDirection = Vector3.Normalize(new Vector3(-0.55f, -0.7f, -0.45f));
                state.BackgroundColor = new Vector3(0.2f, 0.3f, 0.45f);
                state.CameraFarPlane = 100f;
                renderer.SetMesh3DState(state);
                renderer.Clear(0.2f, 0.3f, 0.45f);
                renderer.Set3DFrameActive(true);
                renderer.SetCamera3D(
                    Matrix4x4.CreateLookAt(new Vector3(0, 2.4f, 0), new Vector3(0, 0.3f, 6), Vector3.UnitY),
                    Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, Width / (float)Height, 0.1f, 100f));
                ScriptMeshes.BeginFrame();
                scriptContext.DrawSurface = new PgslRenderDrawSurface(renderer, null, renderer.PixelWidth, renderer.PixelHeight,
                    projectPath: project.RootPath, is3DActive: true);
                PgslContext? previous = PgslCommands.BindContext(scriptContext);
                try
                {
                    PgslCommands.DrawMeshResetState();
                    PgslCommands.DrawMesh3D(cube, 0, 0.6, 6, "");
                }
                finally { PgslCommands.BindContext(previous); }
            };
            game.Controls.Add(viewport);
            GateSuite.ShowHost(game);
            PgslContext? setup = PgslCommands.BindContext(scriptContext);
            try
            {
                ScriptMeshes.Reset();
                cube = (int)PgslCommands.MeshCreate();
                PgslCommands.MeshAddCube(cube, 0, 0, 0, 1.2, 63, 220, 220, 220, 0, 0, 1, 1);
            }
            finally { PgslCommands.BindContext(setup); }

            List<string> failures = [];
            List<string> readings = [];
            try
            {
                foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
                {
                    viewport.BackendOverride = backend.Backend;
                    GateSuite.Pump(3, 15);
                    string name = backend.ShortName.ToLowerInvariant();
                    bool software = backend.Backend == RenderBackendOption.Software;
                    foreach (string tier in new[] { "low", "ultra", "medium" })
                    {
                        PgslCommands.RenderSetQuality(tier);
                        shadows = true;
                        using Bitmap? lit = viewport.ReadbackFrameToBitmap(4);
                        RenderStats stats = viewport.Renderer?.GetStats() ?? default;
                        shadows = false;
                        using Bitmap? unshadowed = viewport.ReadbackFrameToBitmap(3);
                        if (lit is null || unshadowed is null || viewport.RenderFaultCount != 0)
                        {
                            failures.Add($"{backend.ShortName} {tier}: no frame {viewport.LastRenderException}");
                            continue;
                        }
                        lit.Save(Path.Combine(ctx.Captures, $"tier-{tier}-{name}.png"), ImageFormat.Png);
                        int shadowed = DarkenedPixels(unshadowed, lit);
                        int expectedMap = RenderQuality.For(RenderQuality.TryParse(tier, out RenderQualityTier parsed) ? parsed : RenderQualityTier.High).ShadowResolution;
                        if (software) expectedMap = Math.Min(expectedMap, 1024);
                        readings.Add($"{backend.ShortName} {tier}: shadowed={shadowed} map={stats.ShadowMapResolution} aa={stats.AntiAliasing}");
                        // The CPU rasterizer draws no sun shadow on this floor at any size (as before
                        // tiers); there only the map size and a clean frame are checked.
                        if (!software && shadowed < 150) failures.Add($"{backend.ShortName} {tier}: the cube casts no shadow ({shadowed} pixels darker)");
                        if (stats.ShadowMapResolution != expectedMap)
                            failures.Add($"{backend.ShortName} {tier}: sun shadow maps are {stats.ShadowMapResolution}, expected {expectedMap}");
                    }
                }
            }
            finally
            {
                RenderQuality.Reset();
                ScriptMeshes.Reset();
            }
            File.WriteAllLines(Path.Combine(ctx.Logs, "quality-tier-frames.txt"), readings);
            Console.WriteLine("QualityTier readings: " + string.Join(" | ", readings));
            Check(failures.Count == 0, string.Join(" | ", failures));
        });
    }

    // ── Cost at 1920x1080 ────────────────────────────────────────────────────

    /// <summary>
    /// GPU time of a frame without anti-aliasing, with FXAA and with SMAA at 1920x1080 on each GPU
    /// backend (the difference is each pass's cost), and of the quality tiers on a lit scene.
    /// Several rounds after a warm-up; the process is held on the P-cores.
    /// </summary>
    public static void RunCost(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "AntiAliasingCost");
        ProjectSession project = ctx.Project ?? throw new InvalidOperationException("No focused project.");
        HeadlessHarness.RunCase(ctx.Report, "Render.AntiAliasing.GpuCostAt1080p", () =>
        {
            RenderQuality.Reset();
            Process self = Process.GetCurrentProcess();
            IntPtr affinity = self.ProcessorAffinity;
            if (Environment.ProcessorCount >= 16) self.ProcessorAffinity = (IntPtr)0xFFFF;
            using Form game = GateSuite.NewHost(1920, 1080);
            game.FormBorderStyle = FormBorderStyle.None;
            game.ClientSize = new Size(1920, 1080);
            using D3DViewportControl viewport = new() { Dock = DockStyle.Fill, DriveMode = ViewportDriveMode.External, VSync = false };
            PgslContext scriptContext = new() { RoomWidth = 1920, RoomHeight = 1080 };
            int shapes = 0, cube = 0;
            bool lit = false, turning = false;
            float yaw = 0f, turnStep = 0.01f;
            viewport.OnRender += renderer =>
            {
                Mesh3DState state = Mesh3DState.Default;
                state.ShowSunVisual = false; state.FogEnabled = lit;
                state.ShowFloor = lit; state.LightingEnabled = lit; state.ShadowsEnabled = lit;
                state.LightDirection = Vector3.Normalize(new Vector3(-0.55f, -0.7f, -0.45f));
                state.BackgroundColor = new Vector3(0.05f, 0.06f, 0.08f);
                state.CameraFarPlane = 200f;
                renderer.SetMesh3DState(state);
                renderer.Clear(0.05f, 0.06f, 0.08f);
                renderer.Set3DFrameActive(true);
                float aspect = renderer.PixelWidth / (float)Math.Max(1, renderer.PixelHeight);
                // Turning back and forth about half a degree a frame (some 12 pixels), for the motion blur's cost.
                if (turning)
                {
                    yaw += turnStep;
                    if (MathF.Abs(yaw) > 0.15f) turnStep = -turnStep;
                }
                else yaw = 0f;
                renderer.SetCamera3D(
                    lit ? Matrix4x4.CreateLookAt(new Vector3(0, 3f, -2), new Vector3(0, 0.5f, 10), Vector3.UnitY)
                        : Matrix4x4.CreateLookAt(Vector3.Zero, new Vector3(MathF.Sin(yaw) * 10, 0, MathF.Cos(yaw) * 10), Vector3.UnitY),
                    Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, aspect, 0.1f, 200f));
                ScriptMeshes.BeginFrame();
                scriptContext.DrawSurface = new PgslRenderDrawSurface(renderer, null, renderer.PixelWidth, renderer.PixelHeight,
                    projectPath: project.RootPath, is3DActive: true);
                PgslContext? previous = PgslCommands.BindContext(scriptContext);
                try
                {
                    PgslCommands.DrawMeshResetState();
                    PgslCommands.DrawMeshSetCull(false);
                    if (!lit) PgslCommands.DrawMesh3D(shapes, 0, 0, 0, "");
                    else
                        for (int i = 0; i < 64; i++)
                            PgslCommands.DrawMesh3DTransform(cube, (i % 8 - 3.5) * 2.2, 0.6, 4 + (i / 8) * 2.4, 1, 1 + (i % 3) * 0.6, 1, i * 23, "");
                }
                finally { PgslCommands.BindContext(previous); }
            };
            game.Controls.Add(viewport);
            GateSuite.ShowHost(game);
            PgslContext? setup = PgslCommands.BindContext(scriptContext);
            try
            {
                ScriptMeshes.Reset();
                // A field of small tilted squares: edges everywhere, the worst case for both passes.
                shapes = (int)PgslCommands.MeshCreate();
                for (int row = 0; row < 18; row++)
                {
                    for (int column = 0; column < 32; column++)
                    {
                        double cx = (column - 15.5) * 0.62, cy = (row - 8.5) * 0.62, size = 0.22, turn = 0.3 + 0.07 * ((row * 7 + column * 3) % 9);
                        (double x, double y) Corner(double dx, double dy) =>
                            (cx + dx * Math.Cos(turn) - dy * Math.Sin(turn), cy + dx * Math.Sin(turn) + dy * Math.Cos(turn));
                        var a = Corner(-size, -size); var b = Corner(size, -size); var c = Corner(size, size); var d = Corner(-size, size);
                        int shade = 120 + ((row + column) % 4) * 45;
                        PgslCommands.MeshAddQuad(shapes, a.x, a.y, 10, b.x, b.y, 10, c.x, c.y, 10, d.x, d.y, 10,
                            0, 0, -1, 0, 0, 1, 1, shade, shade, shade, 1);
                    }
                }
                cube = (int)PgslCommands.MeshCreate();
                PgslCommands.MeshAddCube(cube, 0, 0, 0, 1.2, 63, 200, 190, 170, 0, 0, 1, 1);
            }
            finally { PgslCommands.BindContext(setup); }

            List<string> lines = [];
            try
            {
                GateSuite.Pump(4, 20);
                foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
                {
                    if (backend.Backend == RenderBackendOption.Software) continue;
                    viewport.BackendOverride = backend.Backend;
                    GateSuite.Pump(4, 20);
                    using (Bitmap? first = viewport.ReadbackFrameToBitmap(3))
                        lines.Add($"{backend.ShortName}: frame {first?.Width}x{first?.Height}");
                    lit = false;
                    Dictionary<string, double[]> modes = [];
                    foreach (string mode in new[] { "off", "fxaa", "smaa", "off", "fxaa", "smaa", "off", "fxaa", "smaa" })
                    {
                        PgslCommands.RenderSetAntiAliasing(mode);
                        double median = MedianGpuMs(viewport, warmup: 30, frames: 90);
                        modes[mode] = modes.TryGetValue(mode, out double[]? earlier) ? [.. earlier, median] : [median];
                    }
                    PgslCommands.RenderSetAntiAliasing("off");
                    double off = modes["off"].Min();
                    foreach ((string mode, double[] medians) in modes)
                        lines.Add($"{backend.ShortName} {mode}: frame {Range(medians)} ms; pass {(mode == "off" ? "-" : Range(medians.Select(m => m - off).ToArray()) + " ms")}");

                    // Camera motion blur while the camera turns, against the same turning view without it.
                    turning = true;
                    Dictionary<string, double[]> blur = [];
                    foreach (double amount in new[] { 0.0, 0.5, 0.0, 0.5, 0.0, 0.5 })
                    {
                        PgslCommands.RenderSetMotionBlur(amount);
                        double median = MedianGpuMs(viewport, warmup: 30, frames: 90);
                        string key = amount > 0 ? "blur" : "none";
                        blur[key] = blur.TryGetValue(key, out double[]? earlier) ? [.. earlier, median] : [median];
                    }
                    PgslCommands.RenderSetMotionBlur(0);
                    turning = false;
                    double still = blur["none"].Min();
                    lines.Add($"{backend.ShortName} turning, no motion blur: frame {Range(blur["none"])} ms");
                    lines.Add($"{backend.ShortName} turning, motion blur 0.5: frame {Range(blur["blur"])} ms; pass {Range(blur["blur"].Select(m => m - still).ToArray())} ms");

                    lit = true;
                    foreach (string tier in new[] { "low", "medium", "high", "ultra" })
                    {
                        PgslCommands.RenderSetQuality(tier);
                        double[] rounds = [MedianGpuMs(viewport, 40, 90), MedianGpuMs(viewport, 10, 90), MedianGpuMs(viewport, 10, 90)];
                        lines.Add($"{backend.ShortName} lit scene, tier {tier}: frame {Range(rounds)} ms");
                    }
                    RenderQuality.Reset();
                    if (viewport.RenderFaultCount != 0) lines.Add($"{backend.ShortName}: render fault {viewport.LastRenderException}");
                }
            }
            finally
            {
                RenderQuality.Reset();
                PgslCommands.RenderSetMotionBlur(0);
                ScriptMeshes.Reset();
                try { self.ProcessorAffinity = affinity; } catch (System.ComponentModel.Win32Exception) { }
            }
            File.WriteAllLines(Path.Combine(ctx.Logs, "anti-aliasing-cost.txt"), lines);
            foreach (string line in lines) Console.WriteLine("[AntiAliasingCost] " + line);
            Check(viewport.RenderFaultCount == 0, "A frame faulted: " + viewport.LastRenderException);
        });
    }

    private static double MedianGpuMs(D3DViewportControl viewport, int warmup, int frames)
    {
        for (int i = 0; i < warmup; i++)
        {
            viewport.RenderFrame();
            if (i % 10 == 0) System.Windows.Forms.Application.DoEvents();
        }
        List<double> samples = [];
        for (int i = 0; i < frames; i++)
        {
            viewport.RenderFrame();
            if (i % 10 == 0) System.Windows.Forms.Application.DoEvents();
            if (viewport.LastGpuMs > 0) samples.Add(viewport.LastGpuMs);
        }
        if (samples.Count == 0) return 0;
        samples.Sort();
        return samples[samples.Count / 2];
    }

    private static string Range(double[] values) =>
        values.Length == 0 ? "-" : $"{values.Min():0.000}-{values.Max():0.000}";

    // ── Helpers ──────────────────────────────────────────────────────────────

    private readonly record struct EdgeCounts(int TopColumns, int TopTotal, int SideRows, int SideTotal)
    {
        public override string ToString() => $"top {TopColumns}/{TopTotal} side {SideRows}/{SideTotal}";
    }

    /// <summary>Columns of the shallow top edge, and rows of the steep side, holding a value between black and white.</summary>
    private static EdgeCounts CountEdges(Bitmap image, int bright)
    {
        int low = Math.Max(8, (int)(bright * 0.1)), high = (int)(bright * 0.9);
        bool Between(Color c) => c.R > low && c.R < high;
        int topColumns = 0, topTotal = 0;
        for (int x = 8; x <= 430; x++)
        {
            int edge = (int)Math.Round(222 - 0.18018 * x);
            bool found = false;
            for (int y = edge - 4; y <= edge + 4; y++) found |= Between(image.GetPixel(x, y));
            topTotal++;
            if (found) topColumns++;
        }
        int sideRows = 0, sideTotal = 0;
        for (int y = 156; y <= 352; y++)
        {
            int edge = (int)Math.Round(444 + (y - 142) * 0.26857);
            bool found = false;
            for (int x = edge - 4; x <= edge + 4; x++) found |= Between(image.GetPixel(x, y));
            sideTotal++;
            if (found) sideRows++;
        }
        return new EdgeCounts(topColumns, topTotal, sideRows, sideTotal);
    }

    /// <summary>Edge pixels of <paramref name="inverted"/> that are the inverse of <paramref name="plain"/>'s.</summary>
    private static (int Matching, int Total) InvertedAlongEdges(Bitmap plain, Bitmap inverted)
    {
        int matching = 0, total = 0;
        for (int x = 8; x <= 430; x += 2)
        {
            int edge = (int)Math.Round(222 - 0.18018 * x);
            for (int y = edge - 3; y <= edge + 3; y++)
            {
                Color a = plain.GetPixel(x, y), b = inverted.GetPixel(x, y);
                if (Math.Abs(a.R + b.R - 255) <= 3 && Math.Abs(a.G + b.G - 255) <= 3 && Math.Abs(a.B + b.B - 255) <= 3) matching++;
                total++;
            }
        }
        return (matching, total);
    }

    private static bool SameRegion(Bitmap a, Bitmap b, Rectangle region)
    {
        for (int y = region.Top; y < region.Bottom; y++)
            for (int x = region.Left; x < region.Right; x++)
                if (a.GetPixel(x, y).ToArgb() != b.GetPixel(x, y).ToArgb()) return false;
        return true;
    }

    private static int InkPixels(Bitmap image, Rectangle region)
    {
        int ink = 0;
        for (int y = region.Top; y < region.Bottom; y++)
            for (int x = region.Left; x < region.Right; x++)
                if (image.GetPixel(x, y).R > 128) ink++;
        return ink;
    }

    /// <summary>Pixels in the lower part of the frame at least a fifth darker with shadows on.</summary>
    private static int DarkenedPixels(Bitmap unshadowed, Bitmap shadowed)
    {
        int count = 0;
        for (int y = unshadowed.Height / 3; y < unshadowed.Height; y += 2)
        {
            for (int x = 0; x < unshadowed.Width; x += 2)
            {
                Color a = unshadowed.GetPixel(x, y), b = shadowed.GetPixel(x, y);
                float la = a.R * 0.299f + a.G * 0.587f + a.B * 0.114f, lb = b.R * 0.299f + b.G * 0.587f + b.B * 0.114f;
                if (la > 30 && lb < la * 0.8f) count++;
            }
        }
        return count;
    }

    /// <summary>An 8x enlargement of a stretch of the top edge, to look at.</summary>
    private static void SaveZoom(HeadlessContext ctx, Bitmap frame, string file)
    {
        Rectangle source = new(176, 172, 64, 32);
        using Bitmap zoom = new(source.Width * 8, source.Height * 8);
        using (Graphics g = Graphics.FromImage(zoom))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(frame, new Rectangle(0, 0, zoom.Width, zoom.Height), source, GraphicsUnit.Pixel);
        }
        zoom.Save(Path.Combine(ctx.Captures, file), ImageFormat.Png);
    }

    private static IReadOnlyList<PostEffectRequest> ProjectPostEffectsFor(string projectRoot)
    {
        ProjectPostEffects.SetRoomEffects([InvertShader]);
        return ProjectPostEffects.RequestsFor(projectRoot).ToList();
    }

    private static void WriteInvertShader(ResourceService resources)
    {
        string path = resources.CreateResource(Path.Combine(resources.AssetsRoot, "Shaders"), ResourceKind.Shader, InvertShader);
        var document = new ShaderAssetDocument
        {
            Pipeline = ShaderAssetPipeline.Fullscreen,
            AuthoringMode = ShaderAuthoringMode.Code,
            TargetType = ShaderTargetType.Fullscreen,
            Entry = "MainPS",
            Source = InvertSource,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
            WriteIndented = true,
        }));
    }

    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
