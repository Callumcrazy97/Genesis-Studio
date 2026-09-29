using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Navigation;
using Genesis.Runtime.Project;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Rendering.Core;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Interfaces;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless.Suites;

internal static class PathingThreeDWorkflowSuite
{
    public static void Run(HeadlessContext ctx, bool export = false)
    {
        Fixture? fixture = null;
        HeadlessHarness.RunCase(ctx.Report, "Editor.Pathing.ThreeD.GeneratedModelObjectRunsSavedRouteAndLiveEdits", () =>
        {
            fixture = new Fixture(ctx);
            VMEngine.Initialize();
            using RuntimeScene scene = new();
            ScriptHostSystem scripts = new();
            ProjectGameContext game = new(fixture.Project.RootPath, scene, null, null, fixture.Room, null);
            scripts.SetContext(game);
            var oldGame = PgslCommands.ActiveGameContext;
            string oldPath = PgslCommands.ProjectPath;
            PgslCommands.ActiveGameContext = game; PgslCommands.ProjectPath = fixture.Project.RootPath;
            try
            {
                RoomBuildResult built = ProjectRoomLoader.Build(fixture.Project.RootPath, scene,
                    RoomAssetLoader.Parse(fixture.RoomPath), scripts, game, beginGame: true);
                var actor = built.EntitiesByNodeId["patrol"];
                scene.AddSubsystem(new ObjectCompositionSubsystem(fixture.Project.RootPath, game.Audio, false));
                scene.AddSubsystem(new ScriptHostSubsystem(scripts));
                void Step(int frames) { for (int frame = 0; frame < frames; frame++) { scene.UpdateFixed(1f / 60); scene.UpdateVariable(1f / 60); } }
                Step(120);
                Transform3DComponent moved = scene.World.GetRef<Transform3DComponent>(actor);
                ModelRendererComponent model = scene.World.GetRef<ModelRendererComponent>(actor);
                ModelAnimatorComponent animation = scene.World.GetRef<ModelAnimatorComponent>(actor);
                Check(moved.Position.X > -1 && moved.Position.Y > .6f && moved.Position.Z > -.4f
                    && model.ScaleX == .7f && model.ScaleY == 1.1f && model.ScaleZ == .8f,
                    "Generated 3D patrol lost its model scale or failed to move through XYZ.");
                Check(animation.ClipName == "Patrol" && animation.Playing && animation.TimeSeconds > .8f
                    && !scene.World.GetRef<NavMeshAgentComponent>(actor).Agent.PlanarXY,
                    "The saved route did not play its real Model animation in 3D gameplay.");
                using (PathingEditorControl editor = new(fixture.RoutePath, fixture.Project.RootPath))
                {
                    Check(editor.TryApplyInspectorValue("Pathing.Speed", 8f), "The saved route speed cannot be edited.");
                    editor.Save();
                }
                Step(24);
                Check(scene.World.GetRef<NavMeshAgentComponent>(actor).Agent.Speed == 8
                    && scene.World.GetRef<Transform3DComponent>(actor).Position.X - moved.Position.X > 2,
                    "Saving Pathing did not refresh the active Model Object's actual speed.");
                byte[] route = File.ReadAllBytes(fixture.RoutePath);
                File.WriteAllText(fixture.RoutePath, "{ broken"); Step(20);
                Check(scene.World.GetRef<NavMeshAgentComponent>(actor).Agent.Speed == 8,
                    "An invalid live route destroyed the last valid movement routine.");
                File.WriteAllBytes(fixture.RoutePath, route); Step(120);
                Check(scripts.RecentDiagnostics.Count == 0, "Generated 3D gameplay reported script errors.");
                using (PathingEditorControl editor = new(fixture.RoutePath, fixture.Project.RootPath))
                { editor.TryApplyInspectorValue("Pathing.Speed", 4f); editor.Save(); }
            }
            finally { scripts.Shutdown(); PgslCommands.ActiveGameContext = oldGame; PgslCommands.ProjectPath = oldPath; }
        });
        if (!export) return;
        GameExportResult? package = null;
        HeadlessHarness.RunCase(ctx.Report, "Acceptance.Pathing.ThreeD.Export.Package", () =>
        {
            Check(fixture != null, "Saved workflow failed.");
            string runtime = RuntimePaths.ResolveRuntimeDir() ?? throw new InvalidOperationException("No selected Player.");
            Check(Hash(typeof(RuntimePaths).Assembly.Location) == Hash(Path.Combine(runtime, "Genesis.Runtime.dll")),
                "Use Build.bat --quick --test pathing-3d-export to select a matching Player.");
            package = GameExportService.Export(new(fixture!.Project, Path.Combine(ctx.OutputRoot, "PathingGame"), GameExportFormat.Folder));
            Check(package.Success, "Export failed: " + package.ErrorMessage);
            Check(!Directory.GetFiles(package.OutputPath, "Genesis.Application*.dll").Any()
                && !File.Exists(Path.Combine(package.OutputPath, "GameScripts.dll")), "Export contains alternate Studio/game code.");
            foreach (string asset in Directory.GetFiles(fixture.Project.AssetsPath, "*", SearchOption.AllDirectories))
            {
                if (asset.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
                string target = Path.Combine(package.OutputPath, Path.GetRelativePath(fixture.Project.RootPath, asset));
                Check(File.Exists(target) && Hash(asset) == Hash(target), "Export changed a saved asset: " + asset);
            }
        });
        foreach (var backend in RenderBackendCatalog.All)
        {
            HeadlessHarness.RunCase(ctx.Report, "Acceptance.Pathing.ThreeD.Export." + backend.ShortName, () =>
            {
                Check(package?.Success == true, "Package failed.");
                string output = Path.Combine(ctx.Captures, "PathingExport", backend.ShortName);
                Directory.CreateDirectory(output);
                ProcessStartInfo start = new(Path.Combine(package!.OutputPath, package.ExecutableName))
                { WorkingDirectory = package.OutputPath, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("--acceptance-pathing"); start.ArgumentList.Add(output);
                start.Environment[RenderBackendSelection.EnvironmentVariable] = backend.SettingsValue;
                start.Environment["GENESIS_UNATTENDED_WINDOW"] = "1"; start.Environment["GENESIS_AUTOSHOT"] = "0";
                foreach (string key in new[] { "GENESIS_PROJECT_PATH", "GENESIS_START_ROOM", "GENESIS_BOOT_COORDINATED" }) start.Environment.Remove(key);
                using Process process = Process.Start(start) ?? throw new InvalidOperationException("Player did not start.");
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                bool exited = process.WaitForExit(120000);
                if (!exited) { process.Kill(true); process.WaitForExit(); }
                File.WriteAllText(Path.Combine(output, "player.log"), stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
                string reportPath = Path.Combine(output, "acceptance.json");
                Check(exited && File.Exists(reportPath), "No exported acceptance report: " + output);
                using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath));
                JsonElement result = report.RootElement;
                Check(process.ExitCode == 0 && result.GetProperty("Success").GetBoolean(), result.GetProperty("Error").GetString() ?? "Player failed.");
                Check(result.GetProperty("Backend").GetString() == backend.DisplayName
                    || result.GetProperty("Backend").GetString() == backend.ShortName, "Export switched to a different backend.");
                Check(result.GetProperty("SavedRoutineVerified").GetBoolean() && result.GetProperty("ModelAnimationVerified").GetBoolean()
                    && result.GetProperty("Diagnostics").GetArrayLength() == 0, "Saved 3D route or Model animation did not execute.");
                var captures = result.GetProperty("Captures").EnumerateArray().ToArray();
                Check(captures.Length == 5 && result.GetProperty("RenderedAnimationPixelChanges").GetInt32() > 30,
                    "Missing patrol movement or physical animation states.");
                foreach (var capture in captures)
                {
                    string file = capture.GetProperty("File").GetString()!;
                    Check(Hash(file) == capture.GetProperty("Sha256").GetString(), "Capture changed after recording.");
                    using Bitmap bitmap = new(file);
                    ctx.Report.Images.Add(ImageResult.From("Pathing.ThreeD." + backend.ShortName + "." + capture.GetProperty("Name").GetString(), file, VisualCapture.Measure(bitmap)));
                }
            });
        }
    }

    private sealed class Fixture
    {
        public Fixture(HeadlessContext ctx)
        {
            Project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "Pathing3D"), "Saved 3D patrol");
            ResourceService resources = new(Project);
            string image = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, "Patrol gold");
            ImageDocumentSession imageSession = new(ImageDocumentSerializer.LoadAtomic(image).Document, image, ImageDocumentAccess.Editor);
            ImageWorkspaceStorage.Save(imageSession, ImageWorkspace.CreateBlank(16, 16, Color.Goldenrod));
            string model = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Patrol model");
            GModelAsset asset = GModelPrimitiveFactory.CreateCube("Animated patrol", 1.5f);
            asset.Rig = new() { Bones = [new() { Name = "Root" }], InverseBindMatrices = [Matrix4x4.Identity] };
            foreach (var mesh in asset.Meshes)
            {
                mesh.IsSkinned = true;
                mesh.SkinnedVertices = mesh.Vertices.Select(vertex => new SkinnedMeshVertex { Position = vertex.Position,
                    Normal = vertex.Normal, Color = vertex.Color, UV = vertex.UV, JointIndices = Vector4.Zero, JointWeights = new(1, 0, 0, 0) }).ToArray();
            }
            asset.Animations.Add(new GModelAnimationClip { Name = "Patrol", Fps = 20, Frames = Enumerable.Range(0, 20)
                .Select(frame => new GModelAnimationFrame { LocalBoneTransforms = [Matrix4x4.CreateRotationY(MathF.Sin(frame * MathF.Tau / 20) * .65f)] }).ToList() });
            using (ModelEditorControl editor = new(model, Project.RootPath)) { editor.ApplyAnimationWorkspace(asset, "Patrol"); editor.Save(); }
            string preview = resources.CreateResource(resources.AssetsRoot, ResourceKind.GameObject, "Patrol preview");
            JObject document = JObject.Parse(ResourceDefinitions.Get(ResourceKind.GameObject).DefaultContent);
            document["dimension"] = "ThreeD"; document["solid"] = false;
            ObjectCompositionModel composition = new(document);
            composition.SetAsset("ModelRendererComponent", ResourceNames.Name(Project.RootPath, model));
            composition.SetProperty("ModelRendererComponent", "ScaleX", .7f);
            composition.SetProperty("ModelRendererComponent", "ScaleY", 1.1f);
            composition.SetProperty("ModelRendererComponent", "ScaleZ", .8f);
            composition.SetAsset("MaterialComponent", ResourceNames.Name(Project.RootPath, image));
            composition.SetProperty("ModelAnimatorComponent", "ClipName", "Idle");
            composition.SetProperty("ModelAnimatorComponent", "ClipFps", 20f);
            File.WriteAllText(preview, document.ToString());
            RoutePath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Pathing, "3D patrol route");
            PathingAssetSerializer.Save(RoutePath, new PathingAsset { Name = "3D patrol route", Dimension = PathingDimension.ThreeD,
                TargetObject = ResourceNames.Name(Project.RootPath, preview), Route = new() { Speed = 4, StoppingDistance = .02f,
                    LoopMode = PathingLoopMode.PingPong, AnimationState = "Patrol",
                    Waypoints = [new() { X = -4, Y = 0, Z = -1, WaitSeconds = .8f }, new() { X = 4, Y = 2, Z = 1 }] } });
            using (PathingEditorControl editor = new(RoutePath, Project.RootPath))
            using (Form host = UnattendedWindowing.NewHost(1280, 780))
            {
                host.Controls.Add(editor); ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host);
                editor.CommandBar.Items.OfType<ToolStripButton>().Single(button => button.Text == "Use in game").PerformClick();
                Check(Controls(editor).OfType<Label>().Any(label => label.Text == "Use this route in a 3D game"), "3D game guide still describes a 2D sprite.");
                Controls(editor).OfType<TextBox>().Single(box => box.Name == "PathingObjectName").Text = "Patrol unit";
                string? created = null; editor.OpenLinkedResourceRequested += (_, path) => created = path;
                Controls(editor).OfType<Button>().Single(button => button.Name == "PathingCreateObject").PerformClick();
                Check(created != null && File.Exists(created), "The actual 3D Use in game button could not create an Object.");
                ObjectPath = created!;
                string capture = "pathing-3d-use-in-game.png";
                ctx.Report.Images.Add(ImageResult.From("Pathing 3D Use in game", capture,
                    VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, capture), includeViewports: true)));
            }
            Room = RoomAsset.Create("3D patrol Room", RoomDimension.ThreeD);
            Room.Environment.DynamicSky = false; Room.Environment.FogDensity = 0;
            Room.Environment.BackgroundColor = [.035f, .055f, .085f, 1];
            Room.Nodes.Add(new() { Id = "patrol", Name = "Patrol Unit", Kind = RoomNodeKind.GameObject,
                LayerId = Room.Layers[0].Id, Transform = new() { X = -4, Y = 0, Z = -1 },
                GameObject = new() { Prefab = ResourceNames.Name(Project.RootPath, ObjectPath) } });
            string camera = resources.CreateResource(resources.AssetsRoot, ResourceKind.GameObject, "Patrol camera");
            File.WriteAllText(camera, """{ "dimension": "ThreeD", "components": [{"type":"Camera3DComponent","props":{"FOV":60,"Near":0.1,"Far":100}}] }""");
            Room.Nodes.Add(new() { Id = "camera", Name = "Camera", Kind = RoomNodeKind.GameObject,
                LayerId = Room.Layers[0].Id, Transform = new() { X = 0, Y = 6, Z = 16, RotationX = -16 },
                GameObject = new() { Prefab = ResourceNames.Name(Project.RootPath, camera) } });
            Room.ActiveGameCameraId = "camera";
            RoomPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Room, "3D patrol Room");
            RoomAssetLoader.Save(Room, RoomPath); Project.Manifest.StartRoom = ResourceNames.Name(Project.RootPath, RoomPath);
            new ProjectService().Save(Project);
            JObject generated = JObject.Parse(File.ReadAllText(ObjectPath));
            Check((string?)generated["dimension"] == "ThreeD" && (string?)generated["model"] == ResourceNames.Name(Project.RootPath, model)
                && (string?)generated["material"] == ResourceNames.Name(Project.RootPath, image)
                && ObjectEventStore.Load(ObjectPath)["Create"].Contains("PathFollow", StringComparison.Ordinal),
                "Generated Object lost its saved model, material or gameplay invocation.");
        }
        public ProjectSession Project { get; }
        public string RoutePath { get; }
        public string ObjectPath { get; }
        public RoomAsset Room { get; }
        public string RoomPath { get; }
    }
    private static IEnumerable<Control> Controls(Control root)
    { foreach (Control control in root.Controls) { yield return control; foreach (var child in Controls(control)) yield return child; } }
    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
