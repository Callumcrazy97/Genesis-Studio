using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Genesis.Application.Core.Diagnostics;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Settings;
using Genesis.Application.Headless.Suites;
using Genesis.Application.Studio;
using Genesis.Application.Studio.Forms;
using Genesis.Shared.Assets;

namespace Genesis.Application.Headless;

/// <summary>
/// <c>--open-timing &lt;project&gt; [--repeat N] [--dump &lt;file&gt;]</c>: opens a project the way
/// Studio does (the project file, then Studio's window, shown) N times in one process and prints
/// where each open's time went, so a slow open can be measured on a copy of a real project. The
/// first open of the process pays for loading code; later opens show what reopening costs.
/// <c>--dump</c> writes what the open produced (every resource, the Assets tree and the reference
/// graph) so two builds can be compared for identical results. The project is opened for real
/// (Studio may write .meta files and caches), so point it at a copy.
/// </summary>
internal static class OpenTimingRunner
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: --open-timing <project> [--repeat N] [--dump <file>]");
            return 2;
        }

        string given = Path.GetFullPath(args[0]);
        string file = File.Exists(given) ? given : Directory.EnumerateFiles(given, "*" + ProjectService.ProjectExtension).Single();
        string root = Path.GetDirectoryName(file)!;
        int repeat = 1;
        string? dump = null;
        for (int i = 1; i + 1 < args.Length; i++)
        {
            if (args[i] == "--repeat") repeat = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
            if (args[i] == "--dump") dump = Path.GetFullPath(args[i + 1]);
        }
        // --stress: while the background work runs, the UI thread keeps asking for (and dropping)
        // the resource catalog and reports a save, as a user clicking about would.
        bool stress = args.Contains("--stress");

        string settingsFolder = Path.Combine(Path.GetTempPath(), "genesis-open-timing");
        Directory.CreateDirectory(settingsFolder);
        StudioServices services = new(new SettingsService(Path.Combine(settingsFolder, "settings.json")),
            new ProjectService(), new ProjectValidator(), new StudioLog());
        services.Settings.Current.Editing.AutoSave = false;
        services.Settings.Current.General.CheckForExternalChanges = false;

        for (int run = 1; run <= repeat; run++)
        {
            ResourceNames.Invalidate(root);
            Stopwatch total = Stopwatch.StartNew();
            ProjectSession project = services.Projects.OpenProject(file);
            ProjectOpeningNotice.Step("Indexing the project's resources…");
            StudioShellForm shell = new(services, project, persistLayout: false);
            ProjectOpeningNotice.Step("Laying out the workspace…");
            GateSuite.ShowHost(shell);
            System.Windows.Forms.Application.DoEvents();
            IReadOnlyList<(string Step, double Milliseconds)> steps = ProjectOpenTimings.End();
            double usable = total.Elapsed.TotalMilliseconds;

            Stopwatch background = Stopwatch.StartNew();
            string? anyResource = stress ? ResourceNames.For(root).Entries.FirstOrDefault()?.FullPath : null;
            for (int i = 0; i < 20_000 && (shell.AssetBrowser.PreviewBusy || shell.IsProjectStillLoading); i++)
            {
                if (stress)
                {
                    if (i % 7 == 0) ResourceNames.Invalidate(root);
                    _ = ResourceNames.For(root).Entries.Count;
                    if (i % 11 == 0 && anyResource != null) shell.AssetMonitor.Notify(anyResource);
                }
                GateSuite.Pump(1, 5);
            }
            double after = background.Elapsed.TotalMilliseconds;

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"OPEN {run}: usable after {usable:F0} ms, background work {after:F0} ms after that"));
            foreach ((string step, double ms) in steps)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {ms,8:F0} ms  {step}"));

            if (dump != null && run == repeat)
                WriteDump(dump, shell, project);
            shell.Dispose();
        }
        return 0;
    }

    private static void WriteDump(string path, StudioShellForm shell, ProjectSession project)
    {
        string root = project.RootPath;
        string Relative(string full) => Path.GetRelativePath(root, full);
        StringBuilder text = new();
        ResourceCatalog catalog = ResourceNames.For(root);
        text.AppendLine("# Resources " + catalog.Entries.Count);
        foreach (NamedResource entry in catalog.Entries)
            text.AppendLine($"{entry.Name}|{Relative(entry.FullPath)}|{entry.Type}|{entry.Extension}|{entry.AssetId:N}");

        text.AppendLine("# Tree");
        void Walk(ResourceItem item, int depth)
        {
            text.Append(' ', depth).AppendLine($"{item.Name}|{item.RelativePath}|{item.Kind}|{item.IsFolder}|{item.AssetId:N}|{string.Join(',', item.LibraryTags)}");
            foreach (ResourceItem child in item.Children) Walk(child, depth + 1);
        }
        Walk(shell.AssetBrowser.ResourceTreeSnapshot, 0);

        AssetDependencyGraph graph = shell.AssetMonitor.Graph;
        text.AppendLine($"# Graph resources={graph.ResourceCount} references={graph.ReferenceCount}");
        foreach (string resource in graph.Resources.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            string[] dependencies = graph.GetDependencies(resource).Select(Relative).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
            if (dependencies.Length > 0) text.AppendLine(Relative(resource) + " -> " + string.Join(", ", dependencies));
        }

        File.WriteAllText(path, text.ToString());
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
        Console.WriteLine($"DUMP {path} ({catalog.Entries.Count} resources, graph {graph.ResourceCount}/{graph.ReferenceCount}) hash {hash}");
    }
}
