using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Projects.Templates;
using Genesis.Runtime.Assets;
using Genesis.Shared.Assets;
using Genesis.World.Terrain;
using Genesis.Rendering.Core;
using Genesis.Runtime;

namespace Genesis.Application.Headless.Suites;

internal static class VerdantHollowExportSuite
{
    public static void Run(HeadlessContext context)
    {
        HeadlessHarness.BeginMajor(context.Report, "VerdantHollowExport");
        string parent = Path.Combine(context.Workspace, "VerdantExport");
        Directory.CreateDirectory(parent);
        ProjectSession? project = null;
        GameExportResult? export = null;
        HeadlessHarness.RunCase(context.Report, "Acceptance.VerdantHollow.Export.Package", () =>
        {
            string runtime = RuntimePaths.ResolveRuntimeDir() ?? throw new InvalidOperationException("No selected Player payload.");
            Check(Hash(typeof(RuntimePaths).Assembly.Location) == Hash(Path.Combine(runtime, "Genesis.Runtime.dll")),
                "The selected Player differs from the source-built harness. Use Build.bat --quick --test verdant-export to stage a matching Player.");
            project = new ProjectService().CreateProject(parent, "Verdant Hollow", "3DNatureWalk");
            string terrainFile = Path.Combine(project.AssetsPath, "Terrain", NatureWalkTemplate.TerrainName + ".terrain.json");
            var layers = TerrainSurfaceMaterialBaker.LoadLayers(terrainFile);
            Check(layers.Count == 4 && layers.Take(3).All(layer => !string.IsNullOrWhiteSpace(layer.Image)
                && File.Exists(ResourceNames.Resolve(project.RootPath, layer.Image, ResourceType.Image))),
                "The template's ground palette does not reference its saved grass, soil and rock Images.");
            Check(TerrainNatureSerializer.LoadOrDefault(terrainFile).Paths.All(path => path.SurfacePainted),
                "The template trails are not marked as already painted into the terrain surface.");
            string directory = Path.Combine(context.OutputRoot, "ExportedGame");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "previous-release.txt"), "Previous export");
            export = GameExportService.Export(new(project, directory, GameExportFormat.Folder));
            Check(export.Success, "Game export failed: " + export.ErrorMessage);
            Check(File.Exists(Path.Combine(directory, export.ExecutableName)), "No exported executable.");
            Check(!File.Exists(Path.Combine(directory, "previous-release.txt")), "Previous export survived package replacement.");
            Check(Hash(Path.Combine(runtime, "Genesis.Runtime.dll")) == Hash(Path.Combine(directory, "Genesis.Runtime.dll")),
                "Export did not use the selected Player payload.");
            Check(!Directory.GetFiles(directory, "Genesis.Application*.dll").Any(), "Export leaked Studio assemblies.");
            foreach (string source in Directory.GetFiles(project.AssetsPath, "*", SearchOption.AllDirectories))
            {
                if (source.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
                string target = Path.Combine(directory, Path.GetRelativePath(project.RootPath, source));
                Check(File.Exists(target) && Hash(source) == Hash(target), "Authored asset missing or modified: " + source);
            }
        });

        foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
        {
            HeadlessHarness.RunCase(context.Report, "Acceptance.VerdantHollow.Export." + backend.ShortName, () =>
            {
                Check(project != null && export?.Success == true, "Package failed.");
                string output = Path.Combine(context.Captures, "VerdantHollowExport", backend.ShortName);
                Directory.CreateDirectory(output);
                string directory = export!.OutputPath;
                ProcessStartInfo start = new(Path.Combine(directory, export.ExecutableName))
                {
                    WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                start.ArgumentList.Add("--acceptance-verdant"); start.ArgumentList.Add(output);
                start.Environment[RenderBackendSelection.EnvironmentVariable] = backend.SettingsValue;
                start.Environment["GENESIS_UNATTENDED_WINDOW"] = "1";
                start.Environment["GENESIS_AUTOSHOT"] = "0";
                start.Environment.Remove("GENESIS_PROJECT_PATH");
                start.Environment.Remove("GENESIS_START_ROOM");
                start.Environment.Remove("GENESIS_BOOT_COORDINATED");
                start.Environment.Remove(EngineRenderingDefaults.AllowEscapeEnvironmentVariable);
                using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch exported Player.");
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                bool exited = process.WaitForExit(240000);
                if (!exited) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
                File.WriteAllText(Path.Combine(output, "player.log"), stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
                Check(exited, "Exported Player timed out: " + output);
                string reportPath = Path.Combine(output, "acceptance.json");
                Check(File.Exists(reportPath), "No acceptance report; exit=" + process.ExitCode + "; see " + output);
                using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath));
                JsonElement root = report.RootElement;
                Check(root.GetProperty("Success").GetBoolean() && process.ExitCode == 0,
                    "Exported gameplay failed: " + root.GetProperty("Error").GetString() + "; see " + output);
                string actual = root.GetProperty("Backend").GetString() ?? "";
                Check(actual == backend.DisplayName || actual == backend.ShortName, "Backend mismatch: " + actual);
                foreach (string property in new[] { "InputOnly", "GuideVerified", "CameraLookVerified", "LandingVerified", "AudioPlaybackVerified", "AuthoredTerrainSurfaceVerified", "ScriptedGrassGroundVerified" })
                    Check(root.GetProperty(property).GetBoolean(), "Missing verification: " + property);
                Check(root.GetProperty("Diagnostics").GetArrayLength() == 0, "Exported game emitted script faults.");
                Check(root.GetProperty("SprintSpeed").GetDouble() > root.GetProperty("WalkSpeed").GetDouble() * 1.5,
                    "Exported sprint ignored saved character settings.");
                Check(root.GetProperty("JumpRise").GetDouble() > .8, "No actual physics jump.");
                JsonElement[] captures = root.GetProperty("Captures").EnumerateArray().ToArray();
                string[] required = ["spawn-guide", "look", "walking", "sprinting", "jumping", "landed", "guide-reopened"];
                Check(captures.Length == required.Length && required.All(name => captures.Count(capture => capture.GetProperty("Name").GetString() == name) == 1),
                    "Incomplete or repeated gameplay states.");
                foreach (JsonElement capture in captures)
                {
                    string file = capture.GetProperty("File").GetString()!;
                    Check(File.Exists(file) && Hash(file) == capture.GetProperty("Sha256").GetString(), "Capture hash mismatch: " + file);
                    using Bitmap bitmap = new(file);
                    ImageMetrics metrics = VisualCapture.Measure(bitmap);
                    Check(metrics.UniqueSampledColors > 64, "Rendered scene is blank or has insufficient content: " + file);
                    context.Report.Images.Add(ImageResult.From("Verdant." + backend.ShortName + "." + capture.GetProperty("Name").GetString(), file, metrics));
                }
                Check(!File.Exists(Path.Combine(directory, "GameScripts.dll")), "Alternate compiled code replaced saved template gameplay.");
            });
        }
    }

    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
