using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Genesis.Application.Core.Projects;
using Genesis.Rendering.Core;
using Genesis.Runtime;
using Genesis.Runtime.Diagnostics;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.ECS.Components;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Rendering;
using System.Numerics;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;

namespace Genesis.Application.Headless.Suites;

internal static class TavernBenchmarkSuite
{
    public static void Run(HeadlessContext context, bool native = true)
    {
        HeadlessHarness.BeginMajor(context.Report, "TavernBenchmark");
        ProjectSession? project = null;
        HeadlessHarness.RunCase(context.Report, "Runtime.Benchmark.PercentilesRejectNonfiniteSamples", () =>
        {
            Check(RuntimeBenchmarkRecorder.Percentile(Enumerable.Range(1, 100).Select(value => (double)value).Append(double.NaN), .95) == 95,
                "Nearest-rank percentile is incorrect.");
            Check(RuntimeBenchmarkRecorder.Percentile([10, 10, 10, 200], .99) == 200, "Slow frames were hidden.");
            string output = Path.Combine(context.OutputRoot, "IncompleteBenchmark");
            using (var recorder = new RuntimeBenchmarkRecorder(output, 1)) { }
            using JsonDocument receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "benchmark.json")));
            Check(!receipt.RootElement.GetProperty("Completed").GetBoolean(), "An unrun benchmark was marked completed.");
            Check(!receipt.RootElement.GetProperty("VramMeasurementAvailable").GetBoolean(), "Unmeasured VRAM was represented as measured.");
        });
        HeadlessHarness.RunCase(context.Report, "Runtime.Model.StaticSubmissionAvoidsTransientMorphAndLodState", () =>
        {
            var asset = new GModelAsset
            {
                Meshes =
                [
                    new GModelMesh { Lod = 0 },
                    new GModelMesh { Lod = 2 },
                ],
            };
            IReadOnlyDictionary<string, float> first = ModelMorphEvaluator.ResolveWeights(asset, default);
            IReadOnlyDictionary<string, float> second = ModelMorphEvaluator.ResolveWeights(asset, default);
            Check(ReferenceEquals(first, second) && first.Count == 0,
                "Static models recreated empty morph dictionaries during submission.");
            _ = RuntimeModelRenderSystem.ResolveLodLevel(asset, 1);
            long before = GC.GetAllocatedBytesForCurrentThread();
            int resolved = 0;
            for (int iteration = 0; iteration < 10_000; iteration++)
                resolved = RuntimeModelRenderSystem.ResolveLodLevel(asset, 1);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(resolved == 0 && allocated <= 4096,
                $"LOD resolution returned {resolved} and allocated {allocated:N0} bytes on the per-Object hot path.");
        });
        HeadlessHarness.RunCase(context.Report, "Acceptance.Tavern.AuthoredResourcesAndSavedRoute", () =>
        {
            project = TavernBenchmarkProject.Create(Path.Combine(context.Workspace, "Tavern"));
            string roomFile = ProjectRoomResolver.ResolveRoomFile(project.RootPath, project.Manifest.StartRoom);
            RoomAsset room = RoomAssetLoader.Parse(roomFile);
            Check(ProjectPaths.ReadStartRoom(project.RootPath) == project.Manifest.StartRoom, "Player ignored the saved start room.");
            Check(room.Settings.WindowWidth == 1920 && room.Settings.WindowHeight == 1080, "Benchmark is not 1080p.");
            Check(room.Nodes.Any(node => node.Name == "Balcony actor") && room.Nodes.Any(node => node.Name == "Fireplace"), "Tavern content is missing.");
            Check(room.Nodes.Count(node => node.Name == "Warm lantern") == 4, "Expected four independent warm lights.");
            foreach (string script in Directory.GetFiles(project.AssetsPath, "*.pgsl", SearchOption.AllDirectories))
                _ = ScriptAssetCompiler.Compile(Path.GetFileName(script), File.ReadAllText(script));
            string actorFile = Directory.GetFiles(project.AssetsPath, "*.gmodel", SearchOption.AllDirectories)
                .Single(file => Path.GetFileNameWithoutExtension(file).Contains("Animated actor", StringComparison.OrdinalIgnoreCase));
            GModelAsset actor = RuntimeModelStore.Load(actorFile);
            Check(actor.Rig.IsValid && actor.Animations.Count > 0 && actor.Meshes.Any(mesh => mesh.IsSkinned), "Authored character did not retain rig, skin and clips.");
            using JsonDocument content = JsonDocument.Parse(File.ReadAllText(Path.Combine(project.RootPath, "benchmark-content.json")));
            Check(content.RootElement.GetProperty("ActualDistributedFoliage").GetInt32() == 0, "Pending stress population was disguised as actual content.");
        });
        HeadlessHarness.RunCase(context.Report, "Runtime.Model.Culling.KeepsInteriorShellAndRejectsDistantBounds", () =>
        {
            Check(project != null, "Fixture failed.");
            RoomAsset room = RoomAssetLoader.Parse(ProjectRoomResolver.ResolveRoomFile(project!.RootPath, project.Manifest.StartRoom));
            using RuntimeScene scene = new();
            var built = new RoomSceneBuilder(project.RootPath).Build(scene, room);
            string shellId = room.Nodes.Single(node => node.Name == "Tavern shell").Id;
            foreach (var pair in built.EntitiesByNodeId)
                if (scene.World.Has<Draw3DComponent>(pair.Value))
                    scene.World.GetRef<Draw3DComponent>(pair.Value).Visible = pair.Key == shellId;
            using Form window = UnattendedWindowing.NewHost(320, 240);
            UnattendedWindowing.ShowWithoutFocus(window);
            using IRenderController renderer = RenderControllerFactory.Create(RenderBackendOption.Software);
            renderer.Initialize(window.Handle, 320, 240);
            bool oldCull = RenderAutoState.FrustumCulling, oldOcclusion = RenderAutoState.OcclusionCulling;
            try
            {
                RenderAutoState.FrustumCulling = true; RenderAutoState.OcclusionCulling = false;
                var draws = new MeshDrawCall[32];
                Vector3 eye = new(-6, 5.4f, -4);
                Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 4f / 3, .1f, 100);
                int count = 0;
                ObjectDrawPass.SubmitMeshes3D(scene.World, project.RootPath, draws, ref count, renderer, eye, -Vector3.UnitZ,
                    Matrix4x4.CreateLookAt(eye, eye - Vector3.UnitZ, Vector3.UnitY) * projection);
                Check(count > 0, "A large building disappeared when its origin was behind an interior camera.");
                eye = new(100, 5, 0); count = 0;
                ObjectDrawPass.SubmitMeshes3D(scene.World, project.RootPath, draws, ref count, renderer, eye, Vector3.UnitX,
                    Matrix4x4.CreateLookAt(eye, eye + Vector3.UnitX, Vector3.UnitY) * projection);
                Check(count == 0, "Model bounds fix disabled frustum rejection.");
            }
            finally
            {
                ObjectDrawPass.InvalidateAssets(renderer);
                RenderAutoState.FrustumCulling = oldCull; RenderAutoState.OcclusionCulling = oldOcclusion;
            }
        });
        if (!native) return;
        GameExportResult? export = null;
        HeadlessHarness.RunCase(context.Report, "Acceptance.Tavern.ExportPackage", () =>
        {
            Check(project != null, "Fixture failed.");
            string runtime = RuntimePaths.ResolveRuntimeDir() ?? throw new InvalidOperationException("No selected Player payload.");
            Check(Hash(typeof(RuntimePaths).Assembly.Location) == Hash(Path.Combine(runtime, "Genesis.Runtime.dll")),
                "Harness and Player differ. Run Build.bat --quick --test tavern-benchmark.");
            export = GameExportService.Export(new(project!, Path.Combine(context.OutputRoot, "ExportedTavern"), GameExportFormat.Folder,
                WindowMode: GameExportWindowMode.Fullscreen));
            Check(export.Success, "Export failed: " + export.ErrorMessage);
        });
        HeadlessHarness.RunCase(context.Report, "Acceptance.Tavern.NativeDX11Baseline", () =>
        {
            Check(export?.Success == true, "Package failed.");
            string output = Path.Combine(context.Captures, "Tavern", "DX11");
            ProcessStartInfo start = new(Path.Combine(export!.OutputPath, export.ExecutableName))
            { WorkingDirectory = export.OutputPath, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--benchmark-output"); start.ArgumentList.Add(output);
            start.ArgumentList.Add("--benchmark-seconds"); start.ArgumentList.Add("48");
            start.ArgumentList.Add("--benchmark-warmup-seconds"); start.ArgumentList.Add("3");
            start.Environment[RenderBackendSelection.EnvironmentVariable] = "DX11";
            start.Environment["GENESIS_UNATTENDED_WINDOW"] = "1";
            start.Environment["GENESIS_AUTOSHOT"] = "0";
            foreach (string name in new[] { "GENESIS_PROJECT_PATH", "GENESIS_START_ROOM", "GENESIS_BOOT_COORDINATED" }) start.Environment.Remove(name);
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Player did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(180000)) { process.Kill(); throw new TimeoutException("Tavern benchmark Player timed out."); }
            string log = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(context.Logs, "tavern-player.log"), log);
            Check(process.ExitCode == 0, "Player failed: " + log);
            using JsonDocument receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "benchmark.json")));
            var root = receipt.RootElement;
            Check(root.GetProperty("Completed").GetBoolean() && root.GetProperty("Frames").GetInt32() > 20, "No completed native trace.");
            Check(root.GetProperty("Width").GetInt32() == 1920 && root.GetProperty("Height").GetInt32() == 1080, "Native benchmark silently changed resolution.");
            Check(!log.Contains("SCRIPT ERROR", StringComparison.OrdinalIgnoreCase), "Saved Object events emitted script errors.");
            Check(root.GetProperty("Captures").EnumerateArray().Select(capture => capture.GetProperty("Sha256").GetString()).Distinct().Count() >= 4,
                "Saved route did not produce distinct native views.");
            foreach (var capture in root.GetProperty("Captures").EnumerateArray())
            {
                string file = capture.GetProperty("File").GetString()!;
                Check(Hash(file) == capture.GetProperty("Sha256").GetString(), "Native capture changed.");
                using Bitmap bitmap = new(file);
                ImageMetrics metrics = VisualCapture.Measure(bitmap);
                Check(metrics.UniqueSampledColors > 64, "Native Tavern view is blank: " + file);
                context.Report.Images.Add(ImageResult.From("Tavern.DX11." + Path.GetFileNameWithoutExtension(file), file, metrics));
            }
            // A functional trace must not turn a short run or simple fixture into performance/art acceptance.
            Check(root.GetProperty("VisualAcceptance").GetString()!.StartsWith("Pending", StringComparison.Ordinal), "Benchmark awarded itself aesthetic acceptance.");
        });
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
