using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Projects.Templates;
using Genesis.Rendering.Core;
using Genesis.Runtime;

namespace Genesis.Application.Headless.Suites;

/// <summary>Exports the actual template once, then launches its Player sequentially on each exact
/// backend. The opt-in runtime driver supplies input, leaving authored gameplay in charge.</summary>
internal static class MushroomMeadowExportSuite
{
    public static void Run(HeadlessContext context)
    {
        HeadlessHarness.BeginMajor(context.Report, "MushroomMeadowExport");
        string parent = Path.Combine(context.Workspace, "MeadowExport");
        Directory.CreateDirectory(parent);
        ProjectSession? project = null;
        GameExportResult? export = null;
        HeadlessHarness.RunCase(context.Report, "Acceptance.MushroomMeadow.Package", () =>
        {
            string runtime = RuntimePaths.ResolveRuntimeDir() ?? throw new InvalidOperationException("No current Player payload.");
            Check(Hash(typeof(RuntimePaths).Assembly.Location) == Hash(Path.Combine(runtime, "Genesis.Runtime.dll")),
                "The selected Player differs from the source-built harness. Use Build.bat --quick --test mushroom-export to stage a matching Player.");
            project = new ProjectService().CreateProject(parent, "Mushroom Meadow", TwoDShowcaseTemplate.TemplateId);
            string directory = Path.Combine(context.OutputRoot, "ExportedGame");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "previous-release.txt"), "A previous export must be replaced only after staging succeeds.");
            export = GameExportService.Export(new(project, directory, GameExportFormat.Folder));
            Check(export.Success, "Game export failed: " + export.ErrorMessage);
            Check(File.Exists(Path.Combine(directory, export.ExecutableName)), "Export omitted the game executable.");
            Check(!File.Exists(Path.Combine(directory, "previous-release.txt")), "The previous export was mixed with the new release.");
            Check(Hash(Path.Combine(runtime, "Genesis.Runtime.dll")) == Hash(Path.Combine(directory, "Genesis.Runtime.dll")),
                "Export did not use the selected Player payload.");
            Check(!Directory.GetFiles(directory, "Genesis.Application*.dll").Any(), "Export leaked Studio assemblies.");
            foreach (string source in Directory.GetFiles(project.AssetsPath, "*", SearchOption.AllDirectories))
            {
                if (source.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
                string target = Path.Combine(directory, Path.GetRelativePath(project.RootPath, source));
                Check(File.Exists(target) && Hash(source) == Hash(target), "Export dropped or changed authored content: " + source);
            }
            Check(File.Exists(Path.Combine(directory, "Assets", "Scripts", "Time Bonus.pgsl")),
                "The public Script resource used by the finish event was excluded from the release.");
        });

        foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
        {
            HeadlessHarness.RunCase(context.Report, "Acceptance.MushroomMeadow.Export." + backend.ShortName, () =>
            {
                Check(project != null && export?.Success == true, "Packaging failed; cannot run an exported game.");
                string output = Path.Combine(context.Captures, "MushroomMeadowExport", backend.ShortName);
                Directory.CreateDirectory(output);
                string directory = export!.OutputPath;
                ProcessStartInfo start = new(Path.Combine(directory, export.ExecutableName))
                {
                    WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                start.ArgumentList.Add("--acceptance-meadow"); start.ArgumentList.Add(output);
                start.Environment[RenderBackendSelection.EnvironmentVariable] = backend.SettingsValue;
                start.Environment["GENESIS_UNATTENDED_WINDOW"] = "1";
                start.Environment["GENESIS_AUTOSHOT"] = "0";
                // Exercise standalone discovery instead of the editor's project launch path.
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
                Check(exited, "Exported Player timed out on " + backend.ShortName + "; see " + output);
                string reportPath = Path.Combine(output, "acceptance.json");
                Check(File.Exists(reportPath), "Player produced no acceptance report; exit=" + process.ExitCode + "; see " + output);
                using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath));
                JsonElement root = report.RootElement;
                Check(root.GetProperty("Success").GetBoolean() && process.ExitCode == 0,
                    "Exported gameplay failed (exit " + process.ExitCode + "): " + root.GetProperty("Error").GetString() + "; see " + output);
                string actual = root.GetProperty("Backend").GetString() ?? "";
                Check(actual == backend.DisplayName || actual == backend.ShortName,
                    "Requested " + backend.ShortName + ", actually rendered " + actual);
                Check(root.GetProperty("InputOnly").GetBoolean() && root.GetProperty("PauseVerified").GetBoolean(),
                    "Traversal or pause verification was omitted.");
                Check(root.GetProperty("AudioAvailable").GetBoolean(), "Runtime fell back to silent audio.");
                Check(root.GetProperty("AudioPlaybackVerified").GetBoolean(), "Authored music did not start and stop through the runtime audio service.");
                Check(root.GetProperty("RigPlaybackVerified").GetBoolean(), "Saved Image rig animation did not reach visible exported gameplay.");
                Check(root.GetProperty("Diagnostics").GetArrayLength() == 0, "Exported game emitted script diagnostics.");
                JsonElement[] captures = root.GetProperty("Captures").EnumerateArray().ToArray();
                string[] required = ["title", "running-a", "running-b", "paused", "rig-a", "rig-b", "win", "restarted"];
                Check(required.All(name => captures.Count(capture => capture.GetProperty("Name").GetString() == name) == 1),
                    "Missing or repeated rendered state evidence.");
                foreach (JsonElement capture in captures)
                {
                    string file = capture.GetProperty("File").GetString()!;
                    Check(File.Exists(file) && Hash(file) == capture.GetProperty("Sha256").GetString(), "Capture hash does not match " + file);
                    using Bitmap bitmap = new(file);
                    ImageMetrics metrics = VisualCapture.Measure(bitmap);
                    Check(metrics.UniqueSampledColors > 12, "Rendered state is blank or has too little scene content: " + file);
                    context.Report.Images.Add(ImageResult.From("Meadow." + backend.ShortName + "." + capture.GetProperty("Name").GetString(), file, metrics));
                }
                Check(!File.Exists(Path.Combine(directory, "GameScripts.dll")), "An alternate C# entry point replaced template gameplay.");
            });
        }
    }

    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
