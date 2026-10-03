using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Projects.Templates;
using Genesis.Rendering.Core;
using Genesis.Runtime;
using Genesis.Runtime.Project;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// What has to be true of the product once it leaves this workspace: an installed Studio and an
/// exported game may be unable to write beside themselves, every copy must carry the notices of
/// the libraries inside it, and material Genesis may not distribute must be separable.
/// </summary>
internal static class ReleaseSuite
{
    // A package in the shipped product, and the words its entry in the notices is found by.
    // The most specific name comes first. A package that matches nothing fails the check: add its
    // notice to Documentation/ThirdPartyNotices.txt, then name it here.
    private static readonly (string Package, string Notice)[] Notices =
    [
        ("Silk.NET.SPIRV.Cross.Native", "SPIRV-Cross"),
        ("Silk.NET.", "Silk.NET"),
        ("Ultz.Native.GLFW", "GLFW"),
        ("BepuPhysics", "BepuPhysics"),
        ("BepuUtilities", "BepuUtilities"),
        ("SkiaSharp", "SkiaSharp"),
        ("StbImageSharp", "StbImageSharp"),
        ("BCnEncoder.Net", "BCnEncoder.NET"),
        ("Newtonsoft.Json", "Newtonsoft.Json"),
        ("LiteNetLib", "LiteNetLib"),
        ("Vortice.", "Vortice.Windows"),
        ("SharpGen.Runtime", "SharpGen.Runtime"),
        ("ZstdSharp.Port", "ZstdSharp"),
        ("DockPanelSuite", "DockPanel Suite"),
        ("AssimpNetter", "AssimpNetter"),
        ("CommunityToolkit.HighPerformance", "CommunityToolkit.HighPerformance"),
        ("Microsoft.CodeAnalysis", "Roslyn"),
        ("Microsoft.Direct3D.DXC", "DirectX Shader Compiler"),
        ("runtimepack.", ".NET runtime"),
        ("Microsoft.", ".NET runtime"),
        ("System.", ".NET runtime"),
    ];

    public static void Run(HeadlessContext context)
    {
        HeadlessHarness.BeginMajor(context.Report, "Release");
        string runtime = RuntimePaths.ResolveRuntimeDir() ?? throw new InvalidOperationException("No current Player payload.");

        HeadlessHarness.RunCase(context.Report, "Release.Notices.EveryBundledLibraryIsNamed", () =>
        {
            foreach ((string product, string folder, string dependencies) in new[]
                     {
                         ("Studio", AppContext.BaseDirectory, "Genesis Application.deps.json"),
                         ("the Player", runtime, "GenesisEngine.deps.json"),
                     })
            {
                string noticesPath = Path.Combine(folder, "Licenses", "ThirdPartyNotices.txt");
                Check(File.Exists(noticesPath), product + " carries no Licenses\\ThirdPartyNotices.txt.");
                Check(File.Exists(Path.Combine(folder, "Licenses", "SkiaSharp-THIRD-PARTY-NOTICES.txt")),
                    product + " carries Skia without the notices for the libraries inside it.");
                string notices = File.ReadAllText(noticesPath);
                Check(notices.Contains("Apache License", StringComparison.Ordinal)
                    && notices.Contains("END OF TERMS AND CONDITIONS", StringComparison.Ordinal)
                    && notices.Contains("Permission is hereby granted, free of charge", StringComparison.Ordinal)
                    && notices.Contains("Altered source versions must be plainly marked", StringComparison.Ordinal)
                    && notices.Contains("Redistributions in binary form must reproduce", StringComparison.Ordinal),
                    "The notices name licences whose text they do not reproduce.");

                string dependenciesPath = Path.Combine(folder, dependencies);
                Check(File.Exists(dependenciesPath), product + " has no dependency list to check the notices against: " + dependenciesPath);
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(dependenciesPath));
                List<string> unnamed = [];
                int packages = 0;
                foreach (JsonProperty library in document.RootElement.GetProperty("libraries").EnumerateObject())
                {
                    string type = library.Value.GetProperty("type").GetString() ?? "";
                    if (type is not ("package" or "runtimepack")) continue;
                    packages++;
                    string name = library.Name.Split('/')[0];
                    (string Package, string Notice) match = Notices.FirstOrDefault(entry =>
                        name.StartsWith(entry.Package, StringComparison.OrdinalIgnoreCase));
                    if (match.Notice is null || !notices.Contains(match.Notice, StringComparison.Ordinal))
                        unnamed.Add(name);
                }
                Check(packages > 20, product + "'s dependency list was not read: only " + packages + " packages.");
                Check(unnamed.Count == 0, product + " ships libraries the notices do not name: " + string.Join(", ", unnamed));
            }

            // The native libraries arrive inside packages; their own notices are separate entries.
            string playerNotices = File.ReadAllText(Path.Combine(runtime, "Licenses", "ThirdPartyNotices.txt"));
            foreach ((string file, string notice) in new[]
                     {
                         ("glfw3.dll", "GLFW"), ("spirv-cross.dll", "SPIRV-Cross"), ("libSkiaSharp.dll", "Skia"),
                         ("vcruntime140.dll", "Microsoft Visual C++ runtime"),
                     })
                Check(!File.Exists(Path.Combine(runtime, file)) || playerNotices.Contains(notice, StringComparison.Ordinal),
                    "The Player ships " + file + " without a notice for " + notice + ".");
        });

        ProjectSession? project = null;
        GameExportResult? export = null;
        HeadlessHarness.RunCase(context.Report, "Release.Export.CarriesLicencesAndNothingOfStudio", () =>
        {
            string parent = Path.Combine(context.Workspace, "ReleaseExport");
            Directory.CreateDirectory(parent);
            project = new ProjectService().CreateProject(parent, "Release Meadow", TwoDShowcaseTemplate.TemplateId);
            string directory = Path.Combine(context.OutputRoot, "ReleaseGame");
            export = GameExportService.Export(new(project, directory, GameExportFormat.Folder));
            Check(export.Success, "Game export failed: " + export.ErrorMessage);
            Check(File.Exists(Path.Combine(directory, "Licenses", "ThirdPartyNotices.txt"))
                && File.Exists(Path.Combine(directory, "Licenses", "SkiaSharp-THIRD-PARTY-NOTICES.txt")),
                "An exported game does not carry the notices its libraries require.");
            Check(File.Exists(Path.Combine(directory, "Licenses", "Genesis-LICENSE.txt")),
                "An exported game does not carry Genesis's licence, which its players must receive.");
            string[] leaked = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(file => Path.GetFileName(file) is string name
                    && (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("Genesis.Application", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("WeifenLuo", StringComparison.OrdinalIgnoreCase)))
                .Select(file => Path.GetRelativePath(directory, file)).ToArray();
            Check(leaked.Length == 0, "An exported game carries Studio's own files: " + string.Join(", ", leaked.Take(6)));
        });

        HeadlessHarness.RunCase(context.Report, "Release.Export.GameRunsFromAFolderItCannotWriteTo", () =>
        {
            Check(project != null && export?.Success == true, "Packaging failed; there is no exported game to run.");
            string directory = export!.OutputPath;
            string output = Path.Combine(context.Captures, "ReleaseReadOnly");
            Directory.CreateDirectory(output);
            DateTime started = DateTime.UtcNow;
            using IDisposable locked = ReadAndRunOnly(directory);
            Check(!CanWriteIn(directory), "The exported game's folder could not be made read-only for this check.");

            ProcessStartInfo start = new(Path.Combine(directory, export.ExecutableName))
            {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add("--acceptance-meadow"); start.ArgumentList.Add(output);
            start.Environment[RenderBackendSelection.EnvironmentVariable] =
                RenderBackendCatalog.All.First(backend => backend.ShortName == "DX11").SettingsValue;
            start.Environment["GENESIS_UNATTENDED_WINDOW"] = "1";
            start.Environment["GENESIS_AUTOSHOT"] = "0";
            foreach (string inherited in new[] { "GENESIS_PROJECT_PATH", "GENESIS_START_ROOM", "GENESIS_BOOT_COORDINATED", RuntimePaths.GameScriptsEnvironmentVariable })
                start.Environment.Remove(inherited);
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch the exported game.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            bool exited = process.WaitForExit(240000);
            if (!exited) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
            string said = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(output, "player.log"), said);
            Check(exited, "The exported game did not finish from a read-only folder; see " + output);
            string reportPath = Path.Combine(output, "acceptance.json");
            Check(File.Exists(reportPath), "The game did not start from a folder it cannot write to (exit "
                + process.ExitCode + "): " + FirstLine(said));
            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath));
            Check(report.RootElement.GetProperty("Success").GetBoolean() && process.ExitCode == 0,
                "The game did not play through from a read-only folder (exit " + process.ExitCode + "): "
                + report.RootElement.GetProperty("Error").GetString());

            // Its log went to the player's own folder, beside its saves, instead of being lost.
            string[] candidates =
            [
                Path.Combine(ProjectNumberSave.GetWritableDirectory(directory), "Debug", "Logs", "project_player.log"),
                Path.Combine(ProjectNumberSave.GetWritableDirectory(directory + Path.DirectorySeparatorChar), "Debug", "Logs", "project_player.log"),
            ];
            string? log = candidates.FirstOrDefault(path => File.Exists(path) && File.GetLastWriteTimeUtc(path) >= started);
            Check(log != null, "A game that cannot write beside itself kept no log in the player's own folder: " + candidates[0]);
            Check(File.ReadAllText(log!).Contains("room=", StringComparison.Ordinal), "The relocated log is empty: " + log);
        });

        HeadlessHarness.RunCase(context.Report, "Release.Run.CompiledScriptsStayInsideTheProject", () =>
        {
            string parent = Path.Combine(context.Workspace, "ReleaseScripts");
            Directory.CreateDirectory(parent);
            ProjectSession scripted = new ProjectService().CreateProject(parent, "Scripted", "Blank");
            string scripts = Path.Combine(scripted.AssetsPath, "Scripts");
            Directory.CreateDirectory(scripts);
            File.WriteAllText(Path.Combine(scripts, "ReleaseProbe.cs"),
                "namespace ReleaseProbeGame { public static class ReleaseProbe { public static int Answer() => 42; } }\n");
            // The project's list of resources was made before this file existed.
            Genesis.Shared.Assets.ResourceCatalog.Invalidate(scripted.RootPath);
            string beside = Path.Combine(runtime, "GameScripts.dll");
            // What an earlier version left beside the Player: it must not outlive this run.
            File.WriteAllBytes(beside, [0x4D, 0x5A]);

            ProjectRunLauncher.CompileOutcome compile = ProjectRunLauncher.CompileScripts(scripted.RootPath);
            Check(compile.Success, "The project's C# did not compile: " + compile.ErrorMessage);
            string expected = RuntimePaths.ProjectScriptsDll(scripted.RootPath);
            Check(string.Equals(Path.GetFullPath(compile.TargetDll), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase)
                && File.Exists(expected) && new FileInfo(expected).Length > 1024,
                "Run did not write the compiled scripts inside the project: " + compile.TargetDll);
            Check(!File.Exists(beside),
                "Run still writes or leaves GameScripts.dll in the Player's own folder, which an installed Studio may not write to.");

            // A project with no C# of its own must not inherit the last project's scripts.
            File.Delete(Path.Combine(scripts, "ReleaseProbe.cs"));
            Genesis.Shared.Assets.ResourceCatalog.Invalidate(scripted.RootPath);
            ProjectRunLauncher.CompileOutcome without = ProjectRunLauncher.CompileScripts(scripted.RootPath);
            Check(without.Success && !File.Exists(expected), "Compiled scripts outlived the C# they were made from.");
        });

        HeadlessHarness.RunCase(context.Report, "Release.Licence.GenesisTermsTravelWithStudioPlayerAndInstaller", () =>
        {
            string licence = File.ReadAllText(FindInRepository("LICENSE.md"));
            foreach ((string product, string folder) in new[] { ("Studio", AppContext.BaseDirectory), ("the Player", runtime) })
            {
                string shipped = Path.Combine(folder, "Licenses", "Genesis-LICENSE.txt");
                Check(File.Exists(shipped) && File.ReadAllText(shipped) == licence,
                    product + " does not carry Genesis's licence as written in LICENSE.md.");
            }
            // The terms the libraries require a licence to carry, and the rights a game's author needs.
            foreach (string term in new[] { "Reverse-engineer", "DirectX Shader Compiler", "Visual C++ runtime",
                                            "Give away or sell the games you make", "Licenses\\ThirdPartyNotices.txt", "NO WARRANTY" })
                Check(licence.Contains(term, StringComparison.Ordinal), "Genesis's licence no longer says: " + term);
            string installer = File.ReadAllText(FindInRepository("Installer", "GenesisStudio.iss"));
            Check(installer.Contains(@"LicenseFile={#PublishDir}\Licenses\Genesis-LICENSE.txt", StringComparison.Ordinal),
                "The installer no longer shows Genesis's licence before installing.");
        });

        HeadlessHarness.RunCase(context.Report, "Release.Templates.NoFanGameTemplateRemains", () =>
        {
            // The Luigi's Mansion fan-game template was removed on 3 October 2026: it was made from
            // another publisher's artwork and audio. Nothing of it may come back by accident.
            Check(ProjectTemplateCatalog.All.All(template => !template.Id.Contains("Luigi", StringComparison.OrdinalIgnoreCase)
                    && !template.Name.Contains("Luigi", StringComparison.OrdinalIgnoreCase)),
                "The template gallery lists the removed fan-game template.");
            foreach (string folder in new[] { AppContext.BaseDirectory, runtime })
                Check(!File.Exists(Path.Combine(folder, "Templates", "LuigisMansion.zip"))
                    && !Directory.Exists(Path.Combine(folder, "Projects", "Templates", "Assets", "LuigisMansion")),
                    "The removed fan-game template's content is still published in " + folder);
            string parent = Path.Combine(context.Workspace, "OldTemplateName");
            Directory.CreateDirectory(parent);
            ProjectSession made = new ProjectService().CreateProject(parent, "Old Name", "LuigisMansion");
            Check(made.Manifest.Template == "Blank", "Asking for the removed template's name did not give an ordinary blank project.");
            Check(ProjectTemplateCatalog.Available.Any(template => template.Id == TwoDShowcaseTemplate.TemplateId),
                "Removing the fan-game template removed a template Genesis does own.");
        });
    }

    /// <summary>What Program Files gives an ordinary user: read and run, nothing else.</summary>
    private static IDisposable ReadAndRunOnly(string directory)
    {
        DirectoryInfo info = new(directory);
        DirectorySecurity locked = new();
        locked.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        locked.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        locked.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(locked);
        return new Restore(info);
    }

    private sealed class Restore(DirectoryInfo directory) : IDisposable
    {
        public void Dispose()
        {
            // The folder's owner may always rewrite its permissions: take the parent's again.
            DirectorySecurity inherited = new();
            inherited.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            directory.SetAccessControl(inherited);
        }
    }

    private static bool CanWriteIn(string directory)
    {
        string probe = Path.Combine(directory, "write-probe.tmp");
        try
        {
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static string FindInRepository(params string[] relative)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string candidate = Path.Combine([directory.FullName, .. relative]);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Not found above the test host: " + Path.Combine(relative));
    }

    private static string FirstLine(string text)
    {
        string line = text.Split('\n').FirstOrDefault(part => !string.IsNullOrWhiteSpace(part)) ?? "(nothing was printed)";
        return line.Trim();
    }

    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
