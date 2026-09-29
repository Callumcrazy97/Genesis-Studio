using System.Diagnostics;
using System.Drawing;
using System.Security.Cryptography;
using System.Text.Json;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Physics;
using Genesis.Rendering.Core;
using Genesis.Runtime;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Scene;
using Genesis.Shared.Assets;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless.Suites;

internal static class PhysicsModelExportSuite
{
    public static void Run(HeadlessContext ctx)
    {
        GameExportResult? package = null;
        HeadlessHarness.RunCase(ctx.Report, "Acceptance.Physics.Model.Export.Package", () =>
        {
            string runtime = RuntimePaths.ResolveRuntimeDir() ?? throw new InvalidOperationException("No selected Player.");
            Check(Hash(typeof(RuntimePaths).Assembly.Location) == Hash(Path.Combine(runtime, "Genesis.Runtime.dll")),
                "Use Build.bat --quick --test physics-model-export to select a matching Player.");
            var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "PhysicsModelExport"), "Saved Physics game");
            ResourceService resources = new(project);
            string model = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Physics cube Model");
            GModelAsset geometry = GModelPrimitiveFactory.CreateCube("Saved body", 1); geometry.Pivot.Position = new(0, -.5f, 0);
            using (ModelEditorControl editor = new(model, project.RootPath)) { editor.ApplyAnimationWorkspace(geometry, ""); editor.Save(); }
            string Body(string name, PhysicsBodyKind kind, Color colour)
            {
                string image = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, name + " colour");
                ImageDocumentSession session = new(ImageDocumentSerializer.LoadAtomic(image).Document, image, ImageDocumentAccess.Editor);
                ImageWorkspaceStorage.Save(session, ImageWorkspace.CreateBlank(16, 16, colour));
                string physics = resources.CreateResource(resources.AssetsRoot, ResourceKind.Physics, name + " Physics");
                PhysicsSceneConfig config = PhysicsScenePresets.Default(); config.Dimension = PhysicsDimension.ThreeD;
                config.BodyType = kind; config.Shape = PhysicsBodyShape.Box; config.Friction = .35; config.Restitution = 0;
                config.Density = 2; config.LockRotation = true;
                using PhysicsEditorControl author = new(physics, project.RootPath);
                Check(author.SetDefinition(PhysicsCodeCodec.Serialize(config)) && author.ChooseModel(ResourceNames.Name(project.RootPath, model)), "Saved Physics setup failed.");
                string obj = author.CreatePhysicsObject(name + " Object");
                using ObjectEditorControl editor = new(obj, project.RootPath);
                editor.Composition.SetAsset("MaterialComponent", ResourceNames.Name(project.RootPath, image));
                if (kind == PhysicsBodyKind.Dynamic)
                {
                    editor.SetEventBody("Create", "ModelSet(\"Physics cube Model\"); age = 0; jumped = false; PhysicsSetGravityStrength(0); PhysicsSetVelocity(InstanceSelf(), 0, 0, 0);");
                    editor.SetEventBody("Step", "age = age + DeltaTime; if (age > 0.3) { PhysicsSetGravityStrength(1); } if (!jumped && age > 2.4 && PhysicsIsGrounded(InstanceSelf(), 0.2)) { jumped = true; PhysicsSetVelocity(InstanceSelf(), 0, 6, 0); }");
                }
                editor.Save(); return ResourceNames.Name(project.RootPath, obj);
            }
            string actor = Body("Falling body", PhysicsBodyKind.Dynamic, Color.Goldenrod);
            string floor = Body("Solid ground", PhysicsBodyKind.Static, Color.SteelBlue);
            RoomAsset room = RoomAsset.Create("Saved Physics Room", RoomDimension.ThreeD);
            room.Environment.DynamicSky = false; room.Environment.FogDensity = 0;
            room.Environment.BackgroundColor = [.035f, .055f, .085f, 1];
            room.Nodes.Add(new() { Name = "Physics actor", Kind = RoomNodeKind.GameObject, LayerId = room.Layers[0].Id,
                GameObject = new() { Prefab = actor }, Transform = new() { Y = 4 } });
            room.Nodes.Add(new() { Name = "Physics floor", Kind = RoomNodeKind.GameObject, LayerId = room.Layers[0].Id,
                GameObject = new() { Prefab = floor }, Transform = new() { Y = -.25f, ScaleX = 8, ScaleY = .25f, ScaleZ = 8 } });
            string camera = resources.CreateResource(resources.AssetsRoot, ResourceKind.GameObject, "Physics camera");
            File.WriteAllText(camera, """{ "dimension": "ThreeD", "components": [{"type":"Camera3DComponent","props":{"FOV":60,"Near":0.1,"Far":100}}] }""");
            room.Nodes.Add(new() { Id = "camera", Kind = RoomNodeKind.GameObject, LayerId = room.Layers[0].Id,
                GameObject = new() { Prefab = ResourceNames.Name(project.RootPath, camera) }, Transform = new() { Y = 5, Z = 14, RotationX = -12 } });
            room.ActiveGameCameraId = "camera";
            string roomFile = resources.CreateResource(resources.AssetsRoot, ResourceKind.Room, "Physics Room"); RoomAssetLoader.Save(room, roomFile);
            project.Manifest.StartRoom = ResourceNames.Name(project.RootPath, roomFile); new ProjectService().Save(project);
            package = GameExportService.Export(new(project, Path.Combine(ctx.OutputRoot, "PhysicsGame"), GameExportFormat.Folder));
            Check(package.Success, "Export failed: " + package.ErrorMessage);
            Check(!Directory.GetFiles(package.OutputPath, "Genesis.Application*.dll").Any()
                && !File.Exists(Path.Combine(package.OutputPath, "GameScripts.dll")), "Export contains alternate Studio/game code.");
            foreach (string asset in Directory.GetFiles(project.AssetsPath, "*", SearchOption.AllDirectories))
            {
                if (asset.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
                string target = Path.Combine(package.OutputPath, Path.GetRelativePath(project.RootPath, asset));
                Check(File.Exists(target) && Hash(asset) == Hash(target), "Export changed a saved asset: " + asset);
            }
        });
        foreach (var backend in RenderBackendCatalog.All)
            HeadlessHarness.RunCase(ctx.Report, "Acceptance.Physics.Model.Export." + backend.ShortName, () =>
            {
                Check(package?.Success == true, "Package failed.");
                string output = Path.Combine(ctx.Captures, "PhysicsExport", backend.ShortName); Directory.CreateDirectory(output);
                ProcessStartInfo start = new(Path.Combine(package!.OutputPath, package.ExecutableName))
                { WorkingDirectory = package.OutputPath, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("--acceptance-physics-model"); start.ArgumentList.Add(output);
                start.Environment[RenderBackendSelection.EnvironmentVariable] = backend.SettingsValue;
                start.Environment["GENESIS_UNATTENDED_WINDOW"] = "1"; start.Environment["GENESIS_AUTOSHOT"] = "0";
                foreach (string key in new[] { "GENESIS_PROJECT_PATH", "GENESIS_START_ROOM", "GENESIS_BOOT_COORDINATED" }) start.Environment.Remove(key);
                using Process process = Process.Start(start) ?? throw new InvalidOperationException("Player did not start.");
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                bool exited = process.WaitForExit(60000); if (!exited) { process.Kill(true); process.WaitForExit(); }
                File.WriteAllText(Path.Combine(output, "player.log"), stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
                string reportPath = Path.Combine(output, "acceptance.json"); Check(exited && File.Exists(reportPath), "No exported acceptance report: " + output);
                using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath)); JsonElement result = report.RootElement;
                Check(process.ExitCode == 0 && result.GetProperty("Success").GetBoolean(), result.GetProperty("Error").GetString() ?? "Player failed.");
                Check(result.GetProperty("Backend").GetString() == backend.DisplayName || result.GetProperty("Backend").GetString() == backend.ShortName, "Export substituted its renderer.");
                Check(result.GetProperty("SavedPhysicsVerified").GetBoolean() && result.GetProperty("RenderedPhysicsPixelChanges").GetInt32() > 100
                    && result.GetProperty("Diagnostics").GetArrayLength() == 0, "Saved Physics or rendered body movement failed.");
                var captures = result.GetProperty("Captures").EnumerateArray().ToArray(); Check(captures.Length == 5, "Missing falling/landing/script jump states.");
                foreach (var capture in captures)
                {
                    string file = capture.GetProperty("File").GetString()!; Check(Hash(file) == capture.GetProperty("Sha256").GetString(), "A frame changed after capture.");
                    using Bitmap bitmap = new(file); ctx.Report.Images.Add(ImageResult.From("Physics export " + backend.ShortName + " " + capture.GetProperty("Name").GetString(), file, VisualCapture.Measure(bitmap)));
                }
            });
    }
    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
