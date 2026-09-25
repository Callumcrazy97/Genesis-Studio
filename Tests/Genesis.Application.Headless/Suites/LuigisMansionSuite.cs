using System.Drawing;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Genesis.Application.Core.Images;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Projects.Templates;
using Genesis.Application.Core.Resources;
using Genesis.Runtime;
using Genesis.Runtime.Assets;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Input;
using Genesis.Runtime.Particles;
using Genesis.Runtime.Project;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Runtime.Spatial;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>Native regression target, independent of GPU presentation and audible acceptance.</summary>
internal static class LuigisMansionSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "LuigisMansion");
        string parent = Path.Combine(ctx.Workspace, "MansionRegression");
        Directory.CreateDirectory(parent);
        ProjectSession? project = null;
        HeadlessHarness.RunCase(ctx.Report, "Mansion.TemplateBundleUsesShortPublishedPath", () =>
        {
            string bundlePath = Path.Combine(AppContext.BaseDirectory, "Templates", "LuigisMansion.zip");
            Assert(File.Exists(bundlePath), "The path-safe template bundle was not copied transitively into the build/publish output.");
            using ZipArchive bundle = ZipFile.OpenRead(bundlePath);
            string[] entries = bundle.Entries.Where(entry => !entry.FullName.EndsWith('/'))
                .Select(entry => entry.FullName.Replace('\\', '/')).ToArray();
            Assert(entries.Length > 1000, "The template bundle is incomplete.");
            Assert(entries.Distinct(StringComparer.OrdinalIgnoreCase).Count() == entries.Length, "Duplicate Windows asset paths in the bundle.");
            Assert(entries.Contains("Rooms/00 - A Light in the Dark.room.json"), "Bundle root must be the contents of Assets.");
            Assert(entries.Contains("Sprites/Archive/Configs_Default_WindowsUAP_logos_SmallishLogo.scale-100.spritedata/frames/SmallishLogo.scale-100.png"),
                "The reported long-name source asset was removed instead of packaged.");
            Assert(entries.All(path => !path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(".genesisproj", StringComparison.OrdinalIgnoreCase)), "Do not nest Assets or ship a replacement project identity.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Mansion.TemplateAndProtectedResources", () =>
        {
            ProjectSession created = new ProjectService().CreateProject(parent, "Mansion Regression", LuigisMansionTemplate.TemplateId);
            project = created;
            Assert(created.Manifest.StartRoom == LuigisMansionTemplate.StartRoom, "Wrong campaign entry room.");
            Assert(!created.Manifest.Runtime.AllowEscapeToClose, "Escape must pause, not close the process.");
            Assert(ResourceFolderPolicy.Roots.All(root =>
                Directory.Exists(Path.Combine(created.AssetsPath, root.Name))
                && ResourceFolderPolicy.IsProtected(created, Path.Combine(created.AssetsPath, root.Name))), "Typed roots are missing.");
            Assert(!Directory.Exists(Path.Combine(created.AssetsPath, "Voxels")), "Unwanted voxel root.");
            Assert(Directory.GetFiles(created.AssetsPath, "*.cs", SearchOption.AllDirectories).Length == 0, "Game must use PGSL.");
            Assert(Directory.GetFiles(created.AssetsPath, "*.gml", SearchOption.AllDirectories).Length == 0, "Original GML was copied.");
            Assert(ProjectTemplateCatalog.Find(LuigisMansionTemplate.TemplateId)?.Available == true, "Template is not offered by the hub.");
            Assert(!Directory.Exists(Path.Combine(created.AssetsPath, "Assets")), "Packed content created a nested Assets folder.");
            Assert(!Directory.Exists(Path.Combine(created.RootPath, "Templates")), "Installed projects must use editable assets, not a runtime template archive.");
            Assert(Directory.GetFiles(created.RootPath, "*.genesisproj", SearchOption.TopDirectoryOnly).Length == 1,
                "Template extraction replaced or duplicated the user-created project manifest.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Mansion.ExistingTemplateReceivesBackupFirstHotfix", () =>
        {
            ProjectSession p = Require(project);
            Assert(p.Manifest.TemplateRevision == LuigisMansionTemplate.CurrentRevision, "New Luigi projects were not stamped with the current template revision.");
            string script = Path.Combine(p.AssetsPath, "Objects", "Toad", "Step.pgsl");
            const string Sentinel = "// local pre-hotfix marker";
            File.WriteAllText(script, Sentinel);
            p.Manifest.TemplateRevision = LuigisMansionTemplate.CurrentRevision - 1;
            new ProjectService().Save(p);
            ProjectSession reopened = new ProjectService().OpenProject(p.ProjectFile);
            project = reopened;
            Assert(reopened.Manifest.TemplateRevision == LuigisMansionTemplate.CurrentRevision, "Existing Luigi project did not migrate on open.");
            Assert(!File.ReadAllText(script).Contains(Sentinel, StringComparison.Ordinal), "Template-owned PGSL was not refreshed by the hotfix migration.");
            string backup = Path.Combine(reopened.InternalPath, "Backups", "TemplateHotfix7", "Objects", "Toad", "Step.pgsl");
            Assert(File.Exists(backup) && File.ReadAllText(backup).Contains(Sentinel, StringComparison.Ordinal), "Template migration did not preserve the replaced local file.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Mansion.GameplaySpriteAliasesLoadSiblingFrames", () =>
        {
            ProjectSession p = Require(project);
            string billPath = Path.Combine(p.AssetsPath, "Sprites", "Gameplay", "Bill.image.json");
            ImageDocument document = ImageDocumentSerializer.LoadAtomic(billPath).Document;
            ImageDocumentSession session = new(document, billPath, ImageDocumentAccess.Editor);
            ImageWorkspace workspace = ImageWorkspaceStorage.Load(session);
            Assert(document.Frames.Count == 5 && workspace.Frames.Count == 5,
                "External sibling frame sources collapsed to a blank one-frame workspace.");
            Assert(workspace.Frames.All(frame => frame.Layers.Count > 0 && frame.Layers[0].Pixels.Any(pixel => pixel != 0)),
                "An aliased money frame opened blank in the Image Editor workspace.");
            Assert(ImageWorkspaceStorage.ResolveSessionImagePath(session) is { } resolved && File.Exists(resolved),
                "Image Viewer could not resolve the alias source path.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Mansion.RestoredGameMakerBehavioursRemainAuthored", () =>
        {
            ProjectSession p = Require(project);
            string ReadObject(string objectName, string eventName) => File.ReadAllText(Path.Combine(p.AssetsPath, "Objects", objectName, eventName + ".pgsl"));

            string menuDraw = ReadObject("Mansion Menu", "DrawGui");
            Assert(menuDraw.Contains("bg_MainMenu.image.json", StringComparison.Ordinal)
                && menuDraw.Contains("choice==0", StringComparison.Ordinal),
                "The supplied full-screen STORY / HAUNT MODE menu is no longer the menu baseline.");

            string toad = ReadObject("Toad", "Step");
            Assert(toad.Contains("toadBounce", StringComparison.Ordinal)
                && toad.Contains("snd_ToadOw1", StringComparison.Ordinal)
                && toad.Contains("toadDialog", StringComparison.Ordinal)
                && !toad.Contains("RoomValueSet(\"frozen\",1)", StringComparison.Ordinal),
                "Toad bounce, reaction audio, or non-pausing speech regressed.");

            string chest = ReadObject("Treasure Chest", "Step");
            Assert(chest.Contains("KeyPressed(\"E\")", StringComparison.Ordinal)
                && chest.Contains("vacuuming", StringComparison.Ordinal)
                && chest.Contains("Light2DLineClear", StringComparison.Ordinal)
                && chest.Contains("rattle", StringComparison.Ordinal)
                && chest.Contains("CreateInstance(\"Coin\"", StringComparison.Ordinal),
                "Chest E/vacuum rattle-and-open interaction regressed.");

            foreach (string ghostName in new[] { "Gold Ghost", "Gallery Wraith", "Portrait Keeper" })
            {
                string create = ReadObject(ghostName, "Create");
                string step = ReadObject(ghostName, "Step");
                Assert(create.Contains("torchExposure", StringComparison.Ordinal), "Torch exposure state missing: " + ghostName);
                Assert(step.Contains("torchHit", StringComparison.Ordinal)
                    && step.Contains("Light2DLineClear", StringComparison.Ordinal)
                    && step.Contains("state=3", StringComparison.Ordinal)
                    && step.Contains("vacuumPullX", StringComparison.Ordinal)
                    && step.Contains("playerDistance<190", StringComparison.Ordinal),
                    "Ghost chase/attack -> torch stun -> tether struggle contract regressed: " + ghostName);
            }

            string directorCreate = ReadObject("Campaign Director", "Create");
            string directorStep = ReadObject("Campaign Director", "Step");
            Assert(directorCreate.Contains("snd_WindRain", StringComparison.Ordinal)
                && directorCreate.Contains("snd_Wind", StringComparison.Ordinal)
                && directorStep.Contains("Rain.particle.json", StringComparison.Ordinal)
                && directorStep.Contains("snd_ThunderOutside1", StringComparison.Ordinal)
                && directorStep.Contains("lightning", StringComparison.Ordinal),
                "Storm wind/rain/thunder/lightning ambience regressed.");

            string luigi = ReadObject("Luigi", "Step");
            Assert(luigi.Contains("Luigi Torch Idle Aim Up.image.json", StringComparison.Ordinal)
                && luigi.Contains("Luigi Torch Idle Aim Down.image.json", StringComparison.Ordinal)
                && luigi.Contains("Luigi Vacuum Idle Aim Up.image.json", StringComparison.Ordinal)
                && luigi.Contains("Luigi Vacuum Idle Aim Down.image.json", StringComparison.Ordinal),
                "Luigi upper-body/head tool aiming poses are no longer selected by aim direction.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Mansion.AllImportedMediaRetainsOriginalHashes", () =>
        {
            ProjectSession p = Require(project);
            using JsonDocument provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(p.AssetsPath, "Notes", "Asset provenance.json")));
            JsonElement resources = provenance.RootElement.GetProperty("resources");
            JsonElement[] imported = resources.EnumerateArray().Where(item => item.TryGetProperty("sha256", out _)).ToArray();
            Assert(imported.Length == 617, "Source-media coverage changed unexpectedly.");
            foreach (JsonElement item in imported)
            {
                string relative = item.GetProperty("destination").GetString() ?? throw new InvalidDataException("Missing resource path.");
                string full = Path.GetFullPath(Path.Combine(p.RootPath, relative.Replace('/', Path.DirectorySeparatorChar)));
                Assert(full.StartsWith(p.RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Resource escapes project.");
                Assert(File.Exists(full), "Imported source missing: " + relative);
                string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full)));
                Assert(string.Equals(actual, item.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "Original bytes changed: " + relative);
            }
        });
        HeadlessHarness.RunCase(ctx.Report, "Mansion.AllEventScriptsCompileInNativeVm", () =>
        {
            ProjectSession p = Require(project);
            ProjectRunLauncher.CompileOutcome outcome = ProjectRunLauncher.CompileScripts(p.RootPath, Path.Combine(p.RootPath, "Build", "RegressionPlayer"));
            Assert(outcome.Success, outcome.ErrorMessage ?? "F5 compile gate failed.");
            string previous = PgslCommands.ProjectPath;
            try
            {
                PgslCommands.ProjectPath = p.RootPath;
                string[] scripts = Directory.GetFiles(Path.Combine(p.AssetsPath, "Objects"), "*.pgsl", SearchOption.AllDirectories);
                Assert(scripts.Length == 47, "An event file is missing.");
                foreach (string script in scripts)
                    Assert(VMEngine.Compile(File.ReadAllText(script)) is not null, "Native PGSL compilation failed: " + script);
            }
            finally { PgslCommands.ProjectPath = previous; }
        });
        HeadlessHarness.RunCase(ctx.Report, "Mansion.ChaptersHaveRealGroundAndIndependentWorldDisplaySize", () =>
        {
            ProjectSession p = Require(project);
            string[] files = Directory.GetFiles(Path.Combine(p.AssetsPath, "Rooms"), "*.room.json");
            Assert(files.Length == 8, "Expected title, six chapters and ending.");
            foreach (string file in files)
            {
                RoomAsset room = RoomAssetLoader.Parse(file);
                Assert(RoomDisplayLayout.WindowSize(room) == new Size(1440, 810), "World width leaked into window size.");
                Assert(room.Settings.VSync && room.Settings.PixelArtSampling, "Template lost VSync or crisp source pixels.");
                string name = Path.GetFileName(file);
                if (name.StartsWith("00", StringComparison.Ordinal) || name.StartsWith("07", StringComparison.Ordinal)) continue;
                RoomTileCollisionMap map = new(room, p.RootPath);
                for (int x = 0; x < room.Settings.Width; x += 16)
                    Assert(map.Intersects(new RectangleF(x + 2, 225, 8, 6)), "Unintended ground gap: " + name + " at " + x);
                Assert(!map.Intersects(new RectangleF(74, 193, 12, 31)), "Luigi spawns penetrating the floor.");
                Assert(Math.Abs(map.Sweep(new RectangleF(74, 40, 12, 31), 1000, false) - 153) < .001f, "Fast falls tunnel through ground.");
            }
        });
        HeadlessHarness.RunCase(ctx.Report, "Mansion.LuigiAnimationKeepsAuthoredFeetAndMask", () =>
        {
            ProjectSession p = Require(project);
            foreach (string file in Directory.GetFiles(Path.Combine(p.AssetsPath, "Sprites", "Gameplay"), "Luigi*.image.json"))
            {
                SpriteRuntimeAsset sprite = SpriteAssetLoader.Load(file);
                for (int frame = 0; frame < sprite.Frames.Count; frame++)
                {
                    RectangleF box = SpriteCollisionBounds.Resolve(sprite, frame, 100, 224);
                    Assert(box == new RectangleF(94, 193, 12, 31), "Animation changes Luigi's foot/collision geometry: " + file);
                    Assert(SpriteCollisionBounds.Resolve(sprite, frame, 100, 224, -1, 1) == box, "Turn-around moved his collision mask.");
                }
            }
        });
        HeadlessHarness.RunCase(ctx.Report, "Mansion.RealPgslRunningStartJumpAndPause", () =>
        {
            ProjectSession p = Require(project);
            RoomAsset room = RoomAssetLoader.Parse(Path.Combine(p.AssetsPath, "Rooms", "01 - Stormbound Approach.room.json"));
            using RuntimeScene scene = new("Mansion script regression") { Input = new InputState() };
            ProjectGameContext game = new(p.RootPath, scene, null, null, room, null);
            ScriptHostSystem host = new(); host.SetContext(game);
            int diagnostics = 0; host.DiagnosticReported += _ => diagnostics++;
            IGameContext previousGame = PgslCommands.ActiveGameContext;
            string previousProject = PgslCommands.ProjectPath;
            try
            {
                PgslCommands.ActiveGameContext = game; PgslCommands.ProjectPath = p.RootPath;
                ScriptAssetRegistry.ClearCache(); ScriptAssetRegistry.LoadFromProject(p.RootPath);
                RoomBuildResult built = new RoomSceneBuilder(p.RootPath, host).Build(scene.World, room);
                Entity hero = built.EntitiesByNodeId[room.Nodes.Single(node => node.Name == "Luigi").Id];
                Assert(scene.World.Has<SpriteComponent>(hero), "Player did not receive a live root sprite.");
                void Frame()
                {
                    scene.GameTime.Advance(1f / 60f); host.Update(1f / 60f);
                    RoomEffects2D.For(room).Advance(1f / 60f); scene.World.FlushDeferred(); scene.Input.NextFrame();
                }
                float start = scene.World.GetRef<TransformComponent>(hero).X;
                scene.Input.OnKeyDown(Key.D);
                for (int i = 0; i < 6; i++) Frame();
                float slow = scene.World.GetRef<TransformComponent>(hero).X - start;
                Assert(slow > 1 && slow < 5, "Running start did not briefly limit translation.");
                Assert(scene.World.GetRef<SpriteComponent>(hero).ImageSpeed > 5, "Running-start feet are not faster than normal running.");
                for (int i = 0; i < 18; i++) Frame();
                Assert(scene.World.GetRef<TransformComponent>(hero).X - start > 20, "Luigi never accelerated.");
                Assert(ObjectDrawAssetRegistry.TryGet(hero, out ObjectDrawAssetEntry assets)
                    && assets.Image.EndsWith("Luigi Torch Run.image.json", StringComparison.Ordinal), "Run animation did not reach the renderer.");
                Assert(Math.Abs(scene.World.GetRef<TransformComponent>(hero).Y - 224) < .01f, "Ground collision failed.");
                scene.Input.OnKeyUp(Key.D); scene.Input.OnKeyDown(Key.W); Frame(); scene.Input.OnKeyUp(Key.W);
                for (int i = 0; i < 6; i++) Frame();
                Assert(scene.World.GetRef<TransformComponent>(hero).Y < 215, "W jump failed.");
                scene.Input.OnKeyDown(Key.P); Frame(); scene.Input.OnKeyUp(Key.P);
                float pausedY = scene.World.GetRef<TransformComponent>(hero).Y;
                for (int i = 0; i < 15; i++) Frame();
                Assert(scene.World.GetRef<TransformComponent>(hero).Y == pausedY && PgslCommands.RoomValueGet("vacuum", -1) == 0, "Pause did not freeze gameplay.");
                Assert(diagnostics == 0, "PGSL raised a runtime diagnostic; inspect native test log.");
            }
            finally
            {
                RoomEffects2D.For(room).Dispose();
                PgslCommands.ActiveGameContext = previousGame; PgslCommands.ProjectPath = previousProject;
                ObjectDrawAssetRegistry.Clear(); ScriptAssetRegistry.ClearCache();
            }
        });
        HeadlessHarness.RunCase(ctx.Report, "Mansion.LightOcclusionUsesSharedRayContract", () =>
        {
            using RoomEffects2D effects = new();
            effects.SetObstacle(1, 40, 10, 16, 64);
            Assert(!effects.LineClear(20, 40, 90, 40), "Solid furniture failed to block the torch/vacuum ray.");
            Assert(effects.LineClear(20, 5, 90, 5), "Ray above furniture is incorrectly blocked.");
            Assert(!effects.LineClear(float.NaN, 0, 1, 1), "Nonfinite ray accepted.");
            Assert(RoomEffects2D.RayRectangle(new Vector2(48, 20), Vector2.UnitX, new RectangleF(40, 10, 16, 64), 100) == 0, "Inside-blocker ray must hit at its origin.");
            effects.SetObstacle(1, 0, 0, 0, 0); Assert(effects.LineClear(20, 40, 90, 40), "Removed blocker is still active.");
            effects.SetLight(1, 0, 0, 90, 1, 1, .5f, 0);
            effects.SetLight(2, float.NaN, 0, 90, 1, 1, 1, 1); Assert(effects.LightCount == 1, "Invalid light was registered.");
            effects.RemoveLight(1); Assert(effects.LightCount == 0, "Removed light is retained.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Mansion.ParticleEditorDocumentsPoolPauseExpiryAndRoomIsolation", () =>
        {
            ProjectSession p = Require(project);
            string[] particles = Directory.GetFiles(Path.Combine(p.AssetsPath, "Particles"), "*.particle.json");
            Assert(particles.Length == 10, "Expected ten editable effects.");
            using RoomEffects2D effects = new();
            foreach (string path in particles)
            {
                ParticleConfig config = ParticleAssetLoader.Load(p.RootPath, path);
                Assert(ParticleAssetLoader.EnumerateEnabledEmitters(config).Count > 0, "Empty particle resource: " + path);
                effects.Emit(p.RootPath, path, 100, 100, 200, 90);
            }
            for (int i = 0; i < 32; i++) effects.Emit(p.RootPath, particles[0], 0, 0, 64, 0);
            Assert(effects.ParticleCount == RoomEffects2D.ParticleBudget, "Emission exceeded or did not reach the bounded pool.");
            effects.Paused = true; effects.Advance(10); Assert(effects.ParticleCount == RoomEffects2D.ParticleBudget, "Paused particles advanced.");
            effects.Paused = false; for (int i = 0; i < 260; i++) effects.Advance(.05f);
            Assert(effects.ParticleCount == 0, "Expired particles were retained.");
            RoomAsset a = RoomAsset.Create("A", RoomDimension.TwoD), b = RoomAsset.Create("B", RoomDimension.TwoD);
            RoomEffects2D.For(a).SetAmbient(.4f, 0, 0, 0);
            Assert(!RoomEffects2D.For(b).Enabled, "Effects leaked into an unrelated room.");
            RoomEffects2D.For(a).Dispose(); RoomEffects2D.For(b).Dispose();
        });
        HeadlessHarness.RunCase(ctx.Report, "Mansion.NumericSavesAtomicRoundTripBoundsAndCorruption", () =>
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
        HeadlessHarness.RunCase(ctx.Report, "Mansion.MouseButtonsAndSmoothEffectSpritesPreserveContract", () =>
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
    private static ProjectSession Require(ProjectSession? project) => project ?? throw new InvalidOperationException("Template creation failed.");
    private static void Assert(bool condition, string message) => HeadlessHarness.Assert(condition, message);
    private sealed class RecordingSink : IRenderCommandSink
    {
        public List<SpriteDrawCall> Calls { get; } = [];
        public void DrawSprite(in SpriteDrawCall call) => Calls.Add(call);
        public void DrawSpriteBatch(ReadOnlySpan<SpriteDrawCall> calls) { foreach (SpriteDrawCall call in calls) Calls.Add(call); }
        public void DrawMesh(in MeshDrawCall call) { }
        public void DrawMeshBatch(ReadOnlySpan<MeshDrawCall> calls) { }
    }
}
