using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Projects.Templates;
using Genesis.Application.Headless.Showcase;

namespace Genesis.Application.Headless;

internal static class ClimbingSmokeRunner
{
    [DllImport("user32.dll")]
    static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
    const uint MOUSEEVENTF_MOVE = 0x0001;

    public static int Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        string workspace = Path.Combine(outputDirectory, "Workspace");
        
        if (Directory.Exists(workspace))
        {
            try { Directory.Delete(workspace, true); } catch { Thread.Sleep(200); try { Directory.Delete(workspace, true); } catch { } }
        }
        Directory.CreateDirectory(workspace);

        Genesis.Application.Core.Projects.ProjectService service = new();
        Genesis.Application.Core.Projects.ProjectSession project = service.CreateProject(workspace, "Climbing Demo");
        ClimbingTemplate.Apply(project);

        // Inject automated deterministic movement and camera look directly into the object script!
        string climberPath = Path.Combine(project.AssetsPath, "Objects", "Climber.object.json");
        string json = File.ReadAllText(climberPath);
        // Replace mouse look with automatic left look
        json = json.Replace(
            "lookYaw = lookYaw + (GetMouseLookDeltaX() * lookSensitivity);", 
            "lookYaw = lookYaw - 0.5; // automated look left");
        // Replace WASD movement with automated backward and right
        json = json.Replace(
            "if (KeyCheck(\"W\") || KeyCheck(\"Up\")) { moveF = moveF + 1; }", 
            "moveF = -1; // auto backward");
        json = json.Replace(
            "if (KeyCheck(\"D\") || KeyCheck(\"Right\")) { moveR = moveR + 1; }", 
            "moveR = 1; // auto right");
        File.WriteAllText(climberPath, json);

        string[] backends = ["software"];

        foreach (var backend in backends)
        {
            Console.WriteLine($"\n[Climbing Smoke] Testing {backend}...");
            
            var extraEnv = new Dictionary<string, string> {
                { "GENESIS_RENDER_BACKEND", backend },
                { "GENESIS_AUTOSHOT", "4" }
            };
            
            try
            {
                RecordClimbing(project.RootPath, backend, outputDirectory, extraEnv);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Climbing Smoke] Failed for {backend}: {ex.Message}");
            }
        }
        
        return 0;
    }

    private static void RecordClimbing(string projectRoot, string backend, string outputDirectory, Dictionary<string, string> extraEnv)
    {
        var compile = Genesis.Runtime.Project.ProjectRunLauncher.CompileScripts(projectRoot);
        if (!compile.Success) throw new InvalidOperationException("Compile failed: " + compile.ErrorMessage);

        // --autoshot will cause GenesisEngine.exe to run for 3 seconds, take a screenshot, and exit!
        var launch = Genesis.Runtime.Project.ProjectRunLauncher.Launch(
            projectRoot, 
            roomName: "Summit Trail",
            waitForExit: true,
            extraEnvironment: extraEnv);
            
        if (!launch.Success) throw new InvalidOperationException("Launch failed");

        // The screenshot is saved in the project's Debug/Images folder
        string screenshotsDir = Path.Combine(projectRoot, "Debug", "Images");
        if (Directory.Exists(screenshotsDir))
        {
            var files = Directory.GetFiles(screenshotsDir, "*.png");
            if (files.Length > 0)
            {
                string latest = files.OrderByDescending(f => File.GetCreationTimeUtc(f)).First();
                string dest = Path.Combine(outputDirectory, $"climbing-{backend}.png");
                File.Copy(latest, dest, true);
                Console.WriteLine($"[Climbing Smoke] Saved {dest}");
            }
            else
            {
                Console.WriteLine($"[Climbing Smoke] No screenshot found for {backend} in {screenshotsDir}");
            }
        }
        else
        {
             Console.WriteLine($"[Climbing Smoke] Screenshots directory not found for {backend}: {screenshotsDir}");
        }
    }
}
