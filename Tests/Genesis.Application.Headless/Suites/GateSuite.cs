using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using Genesis.Application.Core.Diagnostics;
using Genesis.Application.Core.Editing;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Settings;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Studio;
using Genesis.Application.Studio.Docking;
using Genesis.Application.Studio.Editing;
using Genesis.Application.Studio.Forms;
using Genesis.Application.Studio.Resources;
using Genesis.Runtime.Project;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// The Build.bat gate: one consolidated workflow per editor, plus one 2D PGSL runtime workflow and
/// one 3D PGSL runtime workflow.
/// </summary>
/// <remarks>
/// This suite replaced a hand-picked list of forty-odd narrow cases. The list was the problem: it
/// had to be maintained by hand beside the tests it named, it drifted, and reading its output told
/// you which *assertions* ran rather than whether the application works. Here the unit of reporting
/// is the thing a person cares about — "the Room Editor", "the 2D template runs" — and each case is
/// a workflow with named steps, so a failure still says exactly which step broke.
///
/// Every editor case follows the same spine, because that is the shape of the bugs this codebase
/// keeps producing: <b>author → assert in the editor → save → reload from disk → assert it survived</b>,
/// and for anything the game consumes, <b>→ assert the runtime reads it</b>. An editor that only
/// looks right is the failure mode; a round trip through the file the runtime actually loads is what
/// catches it.
///
/// The detailed per-feature regressions still exist and still run under <c>--full-tests</c>; they are
/// what Issues.md cites entry by entry. This suite is the gate, not a replacement for them.
/// </remarks>
internal static class GateSuite
{
    /// <summary>How long the real player is allowed to run before the gate calls it hung.</summary>
    private const int PlayerTimeoutMilliseconds = 60_000;

    /// <summary>Seconds of real gameplay the 2D template run records before exiting.</summary>
    private const float PlayerRunSeconds = 2.5f;

    public static void Run(HeadlessContext ctx)
    {
        SettingsService settings = new();
        bool previousStoredVSync = settings.Current.Runtime.VSyncInPreview;
        bool previousEffectiveVSync = Genesis.Rendering.Core.EditorPreviewSettings.VSync;

        // This gate opens multiple real hardware editor controls and advances their WM_TIMER work
        // through Pump/DoEvents. Dumps measured repeated Present(sync=1) starvation first in the
        // hidden/occluded Studio Room preview, then in Editor.Image when only the shell was guarded:
        // each VSync tick can take until the next 16 ms timer is due, so DoEvents never drains. This
        // unattended-only override stays in memory and is restored after the consolidated gate.
        settings.Current.Runtime.VSyncInPreview = false;
        Genesis.Rendering.Core.EditorPreviewSettings.Configure(vsync: false);
        try
        {
            GateFixture fixture = new(ctx);
            int startingTests = ctx.Report.Tests.Count;

            EditorGate.Run(ctx, fixture);
            RuntimeGate.Run(ctx, fixture);
            int gateTests = ctx.Report.Tests.Count - startingTests;
            if (gateTests != 13)
                throw new InvalidOperationException($"The build gate must report exactly 13 workflows, but reported {gateTests}.");
        }
        finally
        {
            settings.Current.Runtime.VSyncInPreview = previousStoredVSync;
            Genesis.Rendering.Core.EditorPreviewSettings.Configure(previousEffectiveVSync);
        }
    }

    /// <summary>Run one consolidated editor/runtime workflow by friendly command-line name.</summary>
    public static void RunFocused(HeadlessContext ctx, string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        SettingsService settings = new();
        bool previousStoredVSync = settings.Current.Runtime.VSyncInPreview;
        bool previousEffectiveVSync = Genesis.Rendering.Core.EditorPreviewSettings.VSync;
        settings.Current.Runtime.VSyncInPreview = false;
        Genesis.Rendering.Core.EditorPreviewSettings.Configure(vsync: false);
        try
        {
            GateFixture fixture = new(ctx);
            string normalized = target.Trim().TrimStart('-').Replace('_', '-').ToLowerInvariant();
            if (normalized is "2d" or "runtime-2d" or "pgsl-2d")
                RuntimeGate.RunFocused(ctx, fixture, twoD: true);
            else if (normalized is "3d" or "runtime-3d" or "pgsl-3d")
                RuntimeGate.RunFocused(ctx, fixture, twoD: false);
            else
                EditorGate.RunFocused(ctx, fixture, normalized);
        }
        finally
        {
            settings.Current.Runtime.VSyncInPreview = previousStoredVSync;
            Genesis.Rendering.Core.EditorPreviewSettings.Configure(previousEffectiveVSync);
        }
    }

    // ── Shared fixture ──────────────────────────────────────────────────────────

    /// <summary>
    /// Projects the gate authors into. Created lazily so a case that does not need one does not pay
    /// for it, and shared so the editor cases author into a project that already has real content.
    /// </summary>
    internal sealed class GateFixture(HeadlessContext ctx, string workspaceName = "Gate")
    {
        private ProjectSession? _blank;
        private ProjectSession? _twoD;
        private ProjectSession? _threeD;
        private ProjectSession? _natureWalk;

        public string Root { get; } = Path.Combine(ctx.Workspace, workspaceName);

        /// <summary>An empty project, for authoring from nothing the way a designer starts.</summary>
        public ProjectSession Blank => _blank ??= Create("Gate Blank", "Blank");

        /// <summary>The 2D platformer template — the fixture the runtime gate actually plays.</summary>
        public ProjectSession TwoD => _twoD ??= Create("Gate Platformer", "2D");

        /// <summary>The 3D climbing template.</summary>
        public ProjectSession ThreeD => _threeD ??= Create("Gate Climbing", "3D");

        /// <summary>Verdant Hollow — the production 3D nature-walk template.</summary>
        public ProjectSession NatureWalk => _natureWalk ??= Create("Gate Verdant Hollow", "3DNatureWalk");

        public ResourceService Resources(ProjectSession project) => new(project);

        public string Folder(ProjectSession project, string name)
        {
            string path = Path.Combine(project.AssetsPath, name);
            Directory.CreateDirectory(path);
            return path;
        }

        private ProjectSession Create(string name, string template)
        {
            Directory.CreateDirectory(Root);
            return new ProjectService().CreateProject(Root, name, template);
        }
    }

    // ── Genesis shell ───────────────────────────────────────────────────────────

    private static class ShellGate
    {
        public static void Run(
            HeadlessContext ctx,
            GateFixture fixture,
            SettingsService unattendedSettings)
        {
            HeadlessHarness.BeginMajor(ctx.Report, "Shell");

            HeadlessHarness.RunCase(ctx.Report, "Shell.ProjectAndResources", () =>
            {
                ProjectSession project = fixture.Blank;
                ResourceService resources = fixture.Resources(project);

                HeadlessHarness.Step("project reopens with the same identity", () =>
                {
                    ProjectSession reopened = new ProjectService().OpenProject(project.ProjectFile);
                    HeadlessHarness.Assert(
                        reopened.Manifest.ProjectId == project.Manifest.ProjectId,
                        "Project ID changed across a save/open round trip.");
                    HeadlessHarness.Assert(
                        Directory.Exists(reopened.AssetsPath),
                        "Assets folder is missing after reopen.");
                });

                string folder = string.Empty;
                string created = string.Empty;
                HeadlessHarness.Step("create every resource kind", () =>
                {
                    folder = resources.CreateFolder(Path.Combine(resources.AssetsRoot, "Objects"), "Gate");
                    foreach (ResourceDefinition definition in ResourceDefinitions.Creatable)
                    {
                        string path = resources.CreateResource(resources.AssetsRoot, definition.Kind, $"Gate {definition.DisplayName}");
                        HeadlessHarness.Assert(
                            File.Exists(path),
                            $"Creating a {definition.DisplayName} produced no file.");
                        HeadlessHarness.Assert(
                            File.Exists(path + ".meta"),
                            $"A {definition.DisplayName} was created without its GUID sidecar.");
                    }

                    created = resources.CreateResource(folder, ResourceKind.GameObject, "Gate Hero");
                });

                HeadlessHarness.Step("rename, copy and duplicate keep identity rules", () =>
                {
                    string renamed = resources.Rename(created, "Gate Champion");
                    HeadlessHarness.Assert(File.Exists(renamed), "Rename did not produce the new file.");
                    HeadlessHarness.Assert(renamed == created && ResourceNames.Name(ResourceNames.FindProjectRoot(renamed), renamed) == "Gate Champion",
                        "Logical rename must change the public identity, not relocate private storage.");

                    string copy = resources.Duplicate(renamed);
                    Guid original = ReadGuid(renamed);
                    Guid duplicate = ReadGuid(copy);
                    HeadlessHarness.Assert(original != Guid.Empty, "The original resource has no GUID.");
                    HeadlessHarness.Assert(
                        original != duplicate,
                        "A duplicated resource kept the original's GUID — two assets would share an identity.");
                    created = renamed;
                });

                HeadlessHarness.Step("cut/paste moves through the clipboard", () =>
                {
                    string destination = resources.CreateFolder(Path.Combine(resources.AssetsRoot, "Objects"), "Gate Moved");
                    resources.SetClipboard(ResourceClipboardOperation.Cut, [created]);
                    IReadOnlyList<string> pasted = resources.Paste(destination);
                    HeadlessHarness.Assert(pasted.Count == 1, "Cut/paste produced no resource.");
                    HeadlessHarness.Assert(File.Exists(pasted[0]), "The pasted resource does not exist.");
                    HeadlessHarness.Assert(!File.Exists(created), "Cut/paste left the source behind.");
                    created = pasted[0];
                });

                HeadlessHarness.Step("delete goes to the project trash, not oblivion", () =>
                {
                    resources.MoveToTrash(created);
                    HeadlessHarness.Assert(!File.Exists(created), "Delete left the resource in place.");
                    string trash = Path.Combine(project.RootPath, ".genesis", "Trash");
                    HeadlessHarness.Assert(
                        Directory.Exists(trash)
                        && Directory.EnumerateFiles(trash, "*", SearchOption.AllDirectories).Any(),
                        "A deleted resource did not arrive in .genesis/Trash.");
                });

                HeadlessHarness.Step("paths outside Assets are refused", () =>
                {
                    bool blocked = false;
                    try
                    {
                        resources.CreateFolder(
                            Path.GetDirectoryName(resources.AssetsRoot) ?? resources.AssetsRoot,
                            "Escaped");
                    }
                    catch (UnauthorizedAccessException)
                    {
                        blocked = true;
                    }

                    HeadlessHarness.Assert(blocked, "A folder was created outside the Assets root.");
                });

                HeadlessHarness.Step("the project validates clean", () =>
                {
                    IReadOnlyList<ProjectValidationIssue> issues = new ProjectValidator().Validate(project);
                    string[] errors = issues
                        .Where(issue => issue.Severity == ProjectValidationSeverity.Error)
                        .Select(issue => issue.Message)
                        .ToArray();
                    HeadlessHarness.Assert(errors.Length == 0, string.Join(" | ", errors));
                });

                HeadlessHarness.Step("every preference reaches something that uses it", () =>
                {
                    // The audit that produced this found nine settings that saved, reloaded, and
                    // were read by nothing. Persistence was never the problem — the seam between
                    // "stored" and "in effect" was, and it had no test. This drives the one bridge
                    // that owns that seam and asserts the far side actually changed.
                    GenesisSettings settings = new();
                    settings.Runtime.VSyncInPreview = false;
                    settings.Runtime.EnableRuntimeDiagnostics = false;
                    settings.Editing.CreateBackups = false;
                    settings.Editing.BackupRetentionDays = 3;
                    settings.Rendering.FaceCulling = "Front";
                    settings.Rendering.FrontFaceWinding = "CounterClockwise";
                    settings.Rendering.LightingEnabled = false;
                    settings.Rendering.ShadowsEnabled = true;
                    settings.Rendering.ShadowStrength = 0.35f;
                    RenderingPreferencesBridge.Apply(settings);

                    HeadlessHarness.Assert(
                        !Genesis.Rendering.Core.EditorPreviewSettings.VSync,
                        "'Use VSync in editor previews' did not reach the viewports.");
                    HeadlessHarness.Assert(
                        !Genesis.Runtime.Debugger.RuntimeDiagnostics.Enabled,
                        "'Collect runtime diagnostics' did not reach the diagnostics collector.");
                    HeadlessHarness.Assert(
                        !Genesis.Application.Core.Projects.ResourceBackupService.Enabled
                        && Genesis.Application.Core.Projects.ResourceBackupService.RetentionDays == 3,
                        "Backup preferences did not reach the backup service.");
                    Mesh3DState rasterState = Mesh3DState.Default;
                    HeadlessHarness.Assert(
                        rasterState.CullBackFaces && rasterState.CullFrontFaces
                        && rasterState.FrontCounterClockwise,
                        "Culling/winding preferences did not reach new 3D render states.");
                    MeshLightingDefaults.Apply(ref rasterState);
                    HeadlessHarness.Assert(
                        !rasterState.LightingEnabled && !rasterState.ShadowsEnabled
                        && Math.Abs(rasterState.ShadowStrength - 0.35f) < 0.001f,
                        "Global lighting/shadow preferences did not reach the renderer boundary.");

                    settings.Runtime.VSyncInPreview = true;
                    settings.Runtime.EnableRuntimeDiagnostics = true;
                    settings.Editing.CreateBackups = true;
                    settings.Rendering.FaceCulling = "Back";
                    settings.Rendering.FrontFaceWinding = "Clockwise";
                    settings.Rendering.LightingEnabled = true;
                    settings.Rendering.ShadowsEnabled = true;
                    settings.Rendering.ShadowStrength = 1f;
                    RenderingPreferencesBridge.Apply(settings);
                    HeadlessHarness.Assert(
                        Genesis.Rendering.Core.EditorPreviewSettings.VSync
                        && Genesis.Runtime.Debugger.RuntimeDiagnostics.Enabled
                        && Genesis.Application.Core.Projects.ResourceBackupService.Enabled,
                        "Preferences applied in one direction only.");
                });

                HeadlessHarness.Step("saving a resource keeps the previous version", () =>
                {
                    // .genesis/Backups was created for every project and stayed empty forever.
                    string note = resources.CreateResource(
                        resources.AssetsRoot, ResourceKind.Note, "Backup Probe");
                    File.WriteAllText(note, "first version");

                    Genesis.Application.Core.Projects.ResourceBackupService.Enabled = true;
                    Genesis.Application.Core.Projects.ResourceBackupService.BackupBeforeOverwrite(note);
                    File.WriteAllText(note, "second version");

                    string backups = Path.Combine(project.RootPath, ".genesis", "Backups");
                    string[] copies = Directory.Exists(backups)
                        ? Directory.GetFiles(backups, "Backup Probe*", SearchOption.AllDirectories)
                        : [];
                    HeadlessHarness.Assert(
                        copies.Length > 0,
                        "Overwriting a resource left no backup, so the previous version is gone.");
                    HeadlessHarness.Assert(
                        File.ReadAllText(copies[0]) == "first version",
                        "The backup holds the new contents rather than the version it replaced.");
                });

                HeadlessHarness.Step("machine preferences persist through the settings gateway", () =>
                {
                    string settingsFile = Path.Combine(
                        ctx.Workspace, "Gate", "UserData", "preferences.json");
                    HeadlessHarness.Assert(
                        Path.GetFullPath(settingsFile).StartsWith(
                            Path.GetFullPath(ctx.Workspace) + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase),
                        "The gate preferences fixture escaped its disposable workspace.");
                    SettingsService settings = new(settingsFile);

                    settings.Current.General.ConfirmDestructiveActions = false;
                    settings.Current.Rendering.Backend = "Direct3D12";
                    settings.Current.Rendering.FaceCulling = "Front";
                    settings.Current.Rendering.FrontFaceWinding = "CounterClockwise";
                    settings.Current.Rendering.LightingEnabled = false;
                    settings.Current.Rendering.ShadowsEnabled = false;
                    settings.Current.Rendering.ShadowStrength = 0.42f;
                    settings.Save();

                    SettingsService reloaded = new(settingsFile);
                    HeadlessHarness.Assert(
                        !reloaded.Current.General.ConfirmDestructiveActions,
                        "A general preference did not survive a save/reload through SettingsService.");
                    HeadlessHarness.Assert(
                        reloaded.Current.Rendering.Backend == "Direct3D12",
                        "The rendering backend did not survive a save/reload through SettingsService.");
                    HeadlessHarness.Assert(
                        reloaded.Current.Rendering.FaceCulling == "Front"
                        && reloaded.Current.Rendering.FrontFaceWinding == "CounterClockwise"
                        && !reloaded.Current.Rendering.LightingEnabled
                        && !reloaded.Current.Rendering.ShadowsEnabled
                        && Math.Abs(reloaded.Current.Rendering.ShadowStrength - 0.42f) < 0.001f,
                        "Rendering preferences did not survive SettingsService.");
                });

                HeadlessHarness.Step("game settings persist in the project, not the profile", () =>
                {
                    // Fog and "can the player press Escape to quit" describe the *game*, so they
                    // must travel with the project and survive opening a different one. Held in
                    // preferences.json they did neither: fog was configured, saved to this machine,
                    // and never written to the project at all.
                    project.Manifest.Runtime.AllowEscapeToClose = false;
                    project.Manifest.Rendering.FogEnabled = true;
                    project.Manifest.Rendering.FogColorHex = "#123456";
                    project.Manifest.Rendering.FogStart = 44f;
                    project.Manifest.Rendering.FogEnd = 188f;
                    project.Manifest.Rendering.FogAlpha = 0.7f;
                    new ProjectService().Save(project);

                    ProjectSession reopened = new ProjectService().OpenProject(project.ProjectFile);
                    HeadlessHarness.Assert(
                        !reopened.Manifest.Runtime.AllowEscapeToClose,
                        "'Allow ESC to close the game' did not persist in the project file.");
                    HeadlessHarness.Assert(
                        reopened.Manifest.Rendering.FogEnabled
                        && reopened.Manifest.Rendering.FogColorHex == "#123456"
                        && Math.Abs(reopened.Manifest.Rendering.FogStart - 44f) < 0.001f
                        && Math.Abs(reopened.Manifest.Rendering.FogEnd - 188f) < 0.001f
                        && Math.Abs(reopened.Manifest.Rendering.FogAlpha - 0.7f) < 0.001f,
                        "Fog settings did not persist in the project file.");

                    // …and reach the engine when that project is opened.
                    RenderingPreferencesBridge.ApplyProject(reopened.Manifest);
                    Genesis.Rendering.Core.EngineFogDefaults applied =
                        Genesis.Rendering.Core.EngineRenderingDefaults.Fog;
                    HeadlessHarness.Assert(
                        applied.Enabled
                        && Genesis.Rendering.Core.EngineRenderingDefaults.ToHexColor(applied.Color) == "#123456"
                        && Math.Abs(applied.Alpha - 0.7f) < 0.001f,
                        "Opening a project did not apply its own fog to the engine.");

                    // …and reach the game F5 launches.
                    IDictionary<string, string> environment =
                        RenderingPreferencesBridge.BuildPlayerEnvironment(
                            new SettingsService().Current.Rendering, reopened.Manifest);
                    HeadlessHarness.Assert(
                        environment[Genesis.Rendering.Core.EngineRenderingDefaults.AllowEscapeEnvironmentVariable] == "0"
                        && environment[Genesis.Rendering.Core.EngineRenderingDefaults.FogColorEnvironmentVariable] == "#123456",
                        "F5 would launch the game without the project's own settings.");

                    project.Manifest.Runtime.AllowEscapeToClose = true;
                    project.Manifest.Rendering.FogEnabled = false;
                    new ProjectService().Save(project);
                    RenderingPreferencesBridge.ApplyProject(project.Manifest);
                });
            });

            HeadlessHarness.RunCase(ctx.Report, "Shell.StudioWorkspace", () =>
            {
                ProjectSession project = fixture.TwoD;
                ResourceService resources = fixture.Resources(project);
                StudioServices services = new(
                    unattendedSettings,
                    new ProjectService(),
                    new ProjectValidator(),
                    new StudioLog(Path.Combine(ctx.Logs, "gate-studio.log")));

                using StudioShellForm studio = new(services, project, persistLayout: false);
                ShowHost(studio);
                Pump(8, 30);

                HeadlessHarness.Step("the workspace opens with its docks and menu", () =>
                {
                    string[] menus = studio.MainMenuStrip!.Items
                        .OfType<ToolStripMenuItem>()
                        .Select(item => (item.Text ?? string.Empty).Replace("&", string.Empty, StringComparison.Ordinal))
                        .ToArray();
                    HeadlessHarness.Assert(
                        menus.SequenceEqual(["File", "Edit", "View", "Tools", "Help"]),
                        $"Unexpected menu map: {string.Join(", ", menus)}");
                    HeadlessHarness.Assert(
                        studio.AssetBrowser.SelectPath(Path.Combine(project.AssetsPath, "Objects", "Player.object.json")),
                        "The Assets tree could not select a resource by path.");
                });

                HeadlessHarness.Step("the Finder lives in the Assets dock, beside the tree it drives", () =>
                {
                    // It used to sit in the top-right of the command bar, at the opposite end of the
                    // window from the tree its results appear in. Only the parenting moved, so this
                    // asserts placement while the steps around it still assert every behaviour.
                    Control? searchHost = studio.FinderSearchBox.GetCurrentParent();
                    Control? filterHost = studio.FinderFilterButton.GetCurrentParent();
                    HeadlessHarness.Assert(
                        searchHost is not null && filterHost is not null,
                        "The Finder controls are not parented to anything.");
                    HeadlessHarness.Assert(
                        IsInside(searchHost, studio.AssetBrowser) && IsInside(filterHost, studio.AssetBrowser),
                        "The Finder search box and filter are not inside the Assets dock.");

                    // Ctrl+F has to reveal the dock now: a closed one cannot take focus, and the
                    // shortcut would silently do nothing.
                    studio.AssetBrowser.Hide();
                    Pump(4, 20);
                    studio.RouteShortcutForTest(Keys.Control | Keys.F);
                    Pump(4, 20);
                    HeadlessHarness.Assert(
                        studio.AssetBrowser.DockState != WeifenLuo.WinFormsUI.Docking.DockState.Hidden,
                        "Ctrl+F left the Assets dock hidden, so the Finder could not be reached.");
                });

                HeadlessHarness.Step("the Finder searches names, types, folders, and authored code", () =>
                {
                    string[] filterItems = studio.FinderFilterButton.DropDownItems
                        .OfType<ToolStripItem>()
                        .Select(item => item.Name ?? string.Empty)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .ToArray();
                    HeadlessHarness.Assert(
                        studio.FinderSearchBox.TextBox.PlaceholderText == "Find resources…"
                        && filterItems.Contains("FinderIncludeSubfolders", StringComparer.Ordinal)
                        && filterItems.Contains("FinderSearchContents", StringComparer.Ordinal)
                        && !filterItems.Contains("FinderTypeUnknown", StringComparer.Ordinal)
                        && filterItems.Contains("FinderTypeGameObject", StringComparer.Ordinal),
                        "The Finder field and its scope/content/type filters are not exposed.");

                    ToolStripMenuItem contentFilter = studio.FinderFilterButton.DropDownItems
                        .OfType<ToolStripMenuItem>()
                        .Single(item => item.Name == "FinderSearchContents");
                    contentFilter.PerformClick();
                    HeadlessHarness.Assert(
                        contentFilter.Checked && studio.FinderFilterButton.Text == "Filter (1)",
                        "Clicking the content filter did not update the live Finder options.");
                    contentFilter.PerformClick();
                    HeadlessHarness.Assert(
                        !contentFilter.Checked && studio.FinderFilterButton.Text == "Filter",
                        "Clicking the content filter again did not restore the default Finder scope.");

                    ResourceSearchResponse partialName = studio.RunFinderSearchForTest(
                        "lay",
                        includeSubfolders: true,
                        searchContents: false,
                        ResourceKind.GameObject);
                    ResourceItem finderSnapshot = studio.AssetBrowser.ResourceTreeSnapshot;
                    HeadlessHarness.Assert(
                        partialName.Results.Count == 1
                        && partialName.Results[0].Resource.Name == "Player",
                        "Partial-name Finder search did not return the nested Player Object.");

                    ResourceSearchResponse topLevel = studio.RunFinderSearchForTest(
                        "lay",
                        includeSubfolders: false,
                        searchContents: false,
                        ResourceKind.GameObject);
                    HeadlessHarness.Assert(
                        topLevel.Results.Count == 0,
                        "Finder ignored the disabled subfolder option.");

                    ResourceSearchResponse contentOff = studio.RunFinderSearchForTest(
                        "CollisionCircle",
                        includeSubfolders: true,
                        searchContents: false,
                        ResourceKind.GameObject);
                    ResourceSearchResponse contentOn = studio.RunFinderSearchForTest(
                        "CollisionCircle",
                        includeSubfolders: true,
                        searchContents: true,
                        ResourceKind.GameObject);
                    HeadlessHarness.Assert(
                        contentOff.Results.Count == 0
                        && contentOn.Results.Count == 1
                        && contentOn.Results[0].Resource.Name == "Player"
                        && contentOn.Results[0].MatchedRelativePath?.EndsWith(
                            "Player/Step.pgsl",
                            StringComparison.OrdinalIgnoreCase) == true
                        && studio.AssetBrowser.FinderResultCount == 1
                        && studio.AssetBrowser.SelectedResource?.Name == "Player",
                        "Finder did not map a PGSL event-body match back into the visible Assets result tree.");
                    HeadlessHarness.Assert(
                        ReferenceEquals(finderSnapshot, studio.AssetBrowser.ResourceTreeSnapshot),
                        "Applying Finder filters rebuilt the filesystem resource tree on the UI thread.");

                    int openBeforePendingEnter = studio.OpenStudioDocuments.Count;
                    studio.FinderSearchBox.Text = "NoSuchFinderResource";
                    HeadlessHarness.Assert(
                        !studio.FinderResultsCurrent,
                        "A newly typed Finder term was incorrectly marked as already applied.");
                    studio.UseFinderResultsForTest(open: true);
                    HeadlessHarness.Assert(
                        studio.OpenStudioDocuments.Count == openBeforePendingEnter,
                        "Enter opened a stale result while the new Finder query was pending.");
                    for (int attempt = 0; attempt < 80 && !studio.FinderResultsCurrent; attempt++)
                    {
                        Pump(1, 25);
                    }

                    HeadlessHarness.Assert(
                        studio.FinderResultsCurrent
                        && studio.OpenStudioDocuments.Count == openBeforePendingEnter,
                        "The deferred Enter path either failed to apply the new query or opened a stale result.");
                    studio.ResetFinderForTest();
                });

                HeadlessHarness.Step("the Project Hub brand keeps readable spacing", () =>
                {
                    using ProjectHubForm hub = new(services);
                    ShellLayoutSuite.AssertProjectHubBrandLayout(
                        hub,
                        Genesis.Application.Studio.Theme.ThemeService.Density);
                });

                HeadlessHarness.Step("editing shortcuts are not claimed by the menu", () =>
                {
                    ToolStripMenuItem edit = studio.MainMenuStrip!.Items
                        .OfType<ToolStripMenuItem>()
                        .First(item => (item.Text ?? string.Empty).Contains("Edit", StringComparison.Ordinal));

                    foreach (string name in new[] { "Undo", "Redo", "Cut", "Copy", "Paste", "Rename", "Delete" })
                    {
                        ToolStripMenuItem item = edit.DropDownItems
                            .OfType<ToolStripMenuItem>()
                            .First(candidate => string.Equals(candidate.Text, name, StringComparison.Ordinal));
                        HeadlessHarness.Assert(
                            item.ShortcutKeys == Keys.None,
                            $"Edit ▸ {name} registers {item.ShortcutKeys}; a menu accelerator is consumed "
                            + "before the focused editor is offered the key.");
                        HeadlessHarness.Assert(
                            !string.IsNullOrWhiteSpace(item.ShortcutKeyDisplayString),
                            $"Edit ▸ {name} no longer tells anyone what its shortcut is.");
                    }

                    using TextBox text = new();
                    HeadlessHarness.Assert(
                        EditCommandRouter.IsTextEntry(text)
                        && EditCommandRouter.Map(Keys.Control | Keys.C) == EditCommand.Copy,
                        "The focus-aware routing table lost a binding.");
                });

                HeadlessHarness.Step("every resource kind opens through the document registry", () =>
                {
                    ResourceItem root = resources.BuildTree();
                    foreach (ResourceKind kind in new[]
                    {
                        ResourceKind.Image, ResourceKind.GameObject, ResourceKind.Room,
                    })
                    {
                        ResourceItem item = Flatten(root).FirstOrDefault(candidate =>
                            !candidate.IsFolder && candidate.Kind == kind)
                            ?? throw new InvalidOperationException($"The template has no {kind} to open.");
                        IStudioDocument document = studio.OpenStudioResource(item);
                        HeadlessHarness.Assert(
                            document.Resource.Kind == kind,
                            $"Opening a {kind} produced a document for {document.Resource.Kind}.");
                    }

                    Pump(6, 30);
                    HeadlessHarness.Assert(
                        studio.OpenStudioDocuments.Count >= 3,
                        $"Only {studio.OpenStudioDocuments.Count} documents stayed open.");
                });

                HeadlessHarness.Step("object icons in the tree follow their image binding", () =>
                {
                    string coin = Path.Combine(project.AssetsPath, "Objects", "Coin.object.json");
                    ResourceItem item = Flatten(resources.BuildTree()).First(candidate =>
                        string.Equals(
                            Path.GetFullPath(candidate.FullPath), Path.GetFullPath(coin),
                            StringComparison.OrdinalIgnoreCase));
                    using ResourceThumbnailCache cache = new(project.RootPath);
                    cache.Warm(item);
                    HeadlessHarness.Assert(
                        cache.ResolvedImageFor(item) is not null,
                        "An object bound to an image still resolved no thumbnail.");
                });


                HeadlessHarness.Step("opening a resource does not modify it", () =>
                {
                    // Two things at once. The assertion is a real rule — looking at a file must not
                    // change it — and the save that follows is what keeps this suite unattended:
                    // closing a dirty document raises a modal "Save changes?" box, and a headless
                    // run has nobody to click it. A gate that can stop and wait is not a gate.
                    string[] dirty = studio.OpenStudioDocuments
                        .Where(document => document.IsDirty)
                        .Select(document => $"{document.Resource.Name} ({DescribeEdit(document)})")
                        .ToArray();

                    foreach (IStudioDocument document in studio.OpenStudioDocuments.Where(d => d.IsDirty))
                    {
                        document.Save();
                    }

                    HeadlessHarness.Assert(
                        dirty.Length == 0,
                        $"Merely opening {string.Join(", ", dirty)} marked it as edited, so closing the "
                        + "workspace would prompt to save a file nobody touched.");
                });

                HeadlessHarness.Step("the Console gives its height to messages, not a horizontal gutter", () =>
                {
                    StudioLog consoleLog = new();
                    for (int index = 0; index < 8; index++)
                    {
                        // A prior long entry used to keep the native horizontal scrollbar visible
                        // even after the Console had scrolled to two short current messages. At the
                        // default dock height that white gutter consumed nearly a quarter of the
                        // message viewport.
                        consoleLog.Information("LayoutProbe", new string('W', 240));
                    }

                    consoleLog.Information("ProjectHub", "Opened project 'Gate Platformer'.");
                    consoleLog.Information("Studio", "Workspace opened for 'Gate Platformer'.");

                    using ConsoleDock console = new(consoleLog)
                    {
                        Dock = DockStyle.Fill,
                        FormBorderStyle = FormBorderStyle.None,
                        TopLevel = false,
                    };
                    using Form consoleHost = new()
                    {
                        ClientSize = new Size(780, 160),
                        FormBorderStyle = FormBorderStyle.FixedToolWindow,
                        Location = new Point(-10_000, -10_000),
                        ShowInTaskbar = false,
                        StartPosition = FormStartPosition.Manual,
                    };
                    consoleHost.Controls.Add(console);
                    console.Show();
                    ShowHost(consoleHost);
                    Pump(2, 20);

                    ToolStrip toolbar = console.Controls.OfType<ToolStrip>().Single();
                    RichTextBox output = console.Controls.OfType<RichTextBox>().Single();
                    HeadlessHarness.Assert(
                        output.WordWrap && output.ScrollBars == RichTextBoxScrollBars.Vertical,
                        "The Console reintroduced a horizontal scroll path for long messages.");
                    HeadlessHarness.Assert(
                        output.BorderStyle == BorderStyle.None,
                        "Theme application reintroduced a border around the Console viewport.");
                    HeadlessHarness.Assert(
                        output.Left == 0
                        && output.Top == toolbar.Bottom
                        && output.Right == console.ClientSize.Width
                        && output.Bottom == console.ClientSize.Height,
                        $"Console controls do not fill the dock: toolbar={toolbar.Bounds}, output={output.Bounds}, "
                        + $"client={console.ClientRectangle}.");
                    HeadlessHarness.Assert(
                        output.ClientSize.Height >= output.Height - 1,
                        $"A scroll gutter consumed {output.Height - output.ClientSize.Height}px of message height.");
                });


                HeadlessHarness.Step("the shell renders for a visual record", () =>
                {
                    // DrawToBitmap, not a screen grab: the gate runs on every build, and screen
                    // capture costs seconds in settle-and-retry for a picture whose job here is to
                    // show the workspace chrome. The hardware viewports have their own readbacks in
                    // the editor cases, which is where GPU output actually needs proving.
                    ImageMetrics metrics = VisualCapture.Capture(
                        studio,
                        Path.Combine(ctx.Captures, "gate-01-studio-shell.png"),
                        captureFromScreen: false);
                    ctx.Report.Images.Add(
                        ImageResult.From("Gate — Studio shell", "gate-01-studio-shell.png", metrics));
                });
            });
        }

        /// <summary>Names the edit that dirtied a document, so the failure points at the cause.</summary>
        private static string DescribeEdit(IStudioDocument document) =>
            document is SuiteEditorDocument suite
            && suite.Surface is Genesis.Application.Editors.Suite.EditorSurfaceControl surface
                ? surface.LastEditLabel ?? "no edit recorded"
                : "not a suite editor";

        private static Guid ReadGuid(string resourcePath)
        {
            string meta = resourcePath + ".meta";
            if (!File.Exists(meta)) return Guid.Empty;
            AssetMetadata? metadata = JsonSerializer.Deserialize<AssetMetadata>(
                File.ReadAllText(meta),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return metadata is not null && Guid.TryParse(metadata.Guid, out Guid parsed) ? parsed : Guid.Empty;
        }
    }

    // ── Runtime ─────────────────────────────────────────────────────────────────

    private static class RuntimeGate
    {
        public static void Run(HeadlessContext ctx, GateFixture fixture)
        {
            HeadlessHarness.BeginMajor(ctx.Report, "Runtime");

            HeadlessHarness.RunCase(ctx.Report, "Runtime.PGSL.TwoD", () => TwoD(ctx, fixture));
            HeadlessHarness.RunCase(ctx.Report, "Runtime.PGSL.ThreeD", () => ThreeD(ctx, fixture));
        }

        public static void RunFocused(HeadlessContext ctx, GateFixture fixture, bool twoD)
        {
            HeadlessHarness.BeginMajor(ctx.Report, "Runtime");
            HeadlessHarness.RunCase(
                ctx.Report,
                twoD ? "Runtime.PGSL.TwoD" : "Runtime.PGSL.ThreeD",
                () => { if (twoD) TwoD(ctx, fixture); else ThreeD(ctx, fixture); });
        }

        private static void TwoD(HeadlessContext ctx, GateFixture fixture)
        {
            PgslLanguage(ctx);
            RenderingContracts(ctx);
            TwoDTemplate(ctx, fixture);
        }

        private static void ThreeD(HeadlessContext ctx, GateFixture fixture)
        {
            ThreeDTemplate(ctx, fixture);
            RuntimeGateChecks.VerdantHollowTemplate(ctx, fixture, PlayerRunSeconds, PlayerTimeoutMilliseconds);
        }

        private static void PgslLanguage(HeadlessContext ctx) =>
            RuntimeGateChecks.PgslLanguage(ctx);

        private static void RenderingContracts(HeadlessContext ctx) =>
            RuntimeGateChecks.RenderingContracts(ctx);

        private static void TwoDTemplate(HeadlessContext ctx, GateFixture fixture) =>
            RuntimeGateChecks.TwoDTemplate(ctx, fixture, PlayerRunSeconds, PlayerTimeoutMilliseconds);

        private static void ThreeDTemplate(HeadlessContext ctx, GateFixture fixture) =>
            RuntimeGateChecks.ThreeDTemplate(ctx, fixture, PlayerRunSeconds, PlayerTimeoutMilliseconds);

        private static void TerrainWaterCharacter(HeadlessContext ctx) =>
            RuntimeGateChecks.TerrainWaterCharacter(ctx);
    }

    // ── Shared helpers ──────────────────────────────────────────────────────────

    internal static IEnumerable<ResourceItem> Flatten(ResourceItem item)
    {
        yield return item;
        foreach (ResourceItem child in item.Children)
        {
            foreach (ResourceItem descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }

    internal static void Pump(int iterations, int delay)
    {
        for (int index = 0; index < iterations; index++)
        {
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(delay);
        }
    }

    /// <summary>True when a control sits anywhere inside the given container.</summary>
    private static bool IsInside(Control? control, Control container)
    {
        for (Control? walk = control; walk is not null; walk = walk.Parent)
        {
            if (ReferenceEquals(walk, container))
            {
                return true;
            }
        }

        return false;
    }

    internal static Form NewHost(int width = 1360, int height = 860) =>
        UnattendedWindowing.NewHost(width, height);

    /// <summary>Shows a test host without stealing keyboard focus or covering the desktop.</summary>
    internal static void ShowHost(Form form) => UnattendedWindowing.ShowWithoutFocus(form);

    /// <summary>
    /// Asserts a project's gameplay is authored entirely in PGSL.
    /// </summary>
    /// <remarks>
    /// Designer gameplay is PGSL only (Rules.md 5) — generated C# is an internal AOT artefact, never
    /// authored content. Shaders are the sole exception, because HLSL is what a GPU takes. The check
    /// that matters is the second one: `CompileScripts` writes `GameScripts.dll` if and only if the
    /// project contains C# to compile, so an empty result is proof that nothing but PGSL is driving
    /// the game rather than a promise that it isn't.
    /// </remarks>
    internal static void AssertGameplayIsPgslOnly(ProjectSession project, string runtimeDir)
    {
        string[] allowed = [".pgsl", ".hlsl", ".hlsli", ".glsl", ".shader"];
        string[] offenders = Directory
            .EnumerateFiles(project.AssetsPath, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(project.AssetsPath, "*.dll", SearchOption.AllDirectories))
            .Select(path => Path.GetRelativePath(project.RootPath, path))
            .ToArray();
        HeadlessHarness.Assert(
            offenders.Length == 0,
            "Gameplay must be PGSL; only shaders may be another language. Found: "
            + string.Join(", ", offenders));

        int scripts = Directory
            .EnumerateFiles(project.AssetsPath, "*.*", SearchOption.AllDirectories)
            .Count(path => allowed.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
        HeadlessHarness.Assert(scripts > 0, "The project contains no PGSL at all.");

        ProjectRunLauncher.CompileOutcome compile =
            ProjectRunLauncher.CompileScripts(project.RootPath, runtimeDir);
        HeadlessHarness.Assert(
            compile.Success,
            "The F5 script compile failed: " + (compile.ErrorMessage ?? "unknown"));
        HeadlessHarness.Assert(
            compile.TargetDll is null || !File.Exists(compile.TargetDll),
            "A GameScripts.dll was produced, so some gameplay is authored in C# rather than PGSL.");
    }

    /// <summary>Runs the real player against a project and returns what it left behind.</summary>
    /// <remarks>
    /// This is the F5 path, not a simulation of it: the same `ProjectRunLauncher.Launch` the Run
    /// command calls, the same `GenesisEngine.exe`, the same room resolution. `GENESIS_AUTOSHOT`
    /// gives it a fixed lifetime and a screenshot on the way out, which is what makes a windowed
    /// game testable at all. The process is waited on with a timeout and killed if it overruns —
    /// a hung player must fail the gate, not stall it.
    /// </remarks>
    internal static PlayerRun RunPlayer(
        ProjectSession project,
        string runtimeDir,
        float seconds,
        int timeoutMilliseconds,
        Action<Process>? whileRunning = null)
    {
        string imagesDir = ProjectPaths.ImagesDir(project.RootPath);
        string logsDir = ProjectPaths.LogsDir(project.RootPath);
        string playerLog = Path.Combine(logsDir, "project_player.log");
        // Earlier in-process fixture work can leave startup markers in this same log.
        // Preserve that evidence separately so readiness must belong to the process being launched.
        if (File.Exists(playerLog))
            File.Move(playerLog, Path.Combine(logsDir, "previous-player-" + Guid.NewGuid().ToString("N") + ".txt"));
        HashSet<string> before = Directory.Exists(imagesDir)
            ? [.. Directory.EnumerateFiles(imagesDir)]
            : [];

        Dictionary<string, string> environment = new(StringComparer.Ordinal)
         {
            ["GENESIS_AUTOSHOT"] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["GENESIS_PERF_LABEL"] = "gate",
            ["GENESIS_UNATTENDED_WINDOW"] = "1",
        };

        ProjectRunLauncher.LaunchOutcome launch = ProjectRunLauncher.Launch(
            project.RootPath,
            roomName: project.Manifest.StartRoom,
            runtimeDir: runtimeDir,
            waitForExit: false,
            extraEnvironment: environment);

        HeadlessHarness.Assert(launch.Success, "Launch failed: " + (launch.ErrorMessage ?? "unknown"));
        Process player = launch.Process!;
        int startupAllowance = Math.Clamp(timeoutMilliseconds / 2, 1_000, 30_000);
        Task? liveMutation = whileRunning is null
            ? null
            : Task.Run(() =>
            {
                // A clean Full Build can cold-start the CLR and renderer for longer than eight
                // seconds. Autoshot counts play time after boot; allow startup its own bounded
                // share of the process timeout, then require this process's readiness markers.
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(startupAllowance);
                bool ready = false;
                string lastReadError = string.Empty;
                string lastObservedLog = string.Empty;
                while (!player.HasExited && DateTime.UtcNow < deadline)
                {
                    try
                    {
                        if (File.Exists(playerLog))
                        {
                            string log = File.ReadAllText(playerLog);
                            lastObservedLog = log;
                            if (log.Contains("AssetLiveReload watching", StringComparison.Ordinal)
                                && log.Contains("Boot splash: shaders warmed.", StringComparison.Ordinal))
                            {
                                ready = true;
                                break;
                            }
                        }
                    }
                    catch (IOException error) { lastReadError = error.Message; }
                    Thread.Sleep(50);
                }
                HeadlessHarness.Assert(ready && !player.HasExited,
                    $"The current player did not reach live-reload readiness within {startupAllowance} ms (exited={player.HasExited}). " +
                    lastReadError + Environment.NewLine + lastObservedLog[Math.Max(0,lastObservedLog.Length-1500)..]);
                whileRunning(player);
            });
        PlayerWindowIconProbe.Result windowIcons = PlayerWindowIconProbe.WaitForIcons(
            player,
            // WM_GETICON cannot respond while the window thread is warming the renderer.
            // Use the same bounded cold-start allowance as the live-reload readiness probe.
            TimeSpan.FromMilliseconds(startupAllowance));
        if (!player.WaitForExit(timeoutMilliseconds))
        {
            try { player.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new InvalidOperationException(
                $"The player did not exit within {timeoutMilliseconds} ms of a {seconds}s autoshot run.");
        }
        liveMutation?.GetAwaiter().GetResult();

        string[] shots = Directory.Exists(imagesDir)
            ? [.. Directory.EnumerateFiles(imagesDir).Where(path => !before.Contains(path))]
            : [];
        string log = Directory.Exists(logsDir)
            ? string.Join(
                Environment.NewLine,
                Directory.EnumerateFiles(logsDir, "*.log")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(1)
                    .Select(File.ReadAllText))
            : string.Empty;

        return new PlayerRun(
            player.ExitCode,
            launch.RoomName ?? string.Empty,
            shots,
            log,
            windowIcons.FoundWindow,
            windowIcons.HasSmallIcon,
            windowIcons.HasLargeIcon,
            windowIcons.SmallIconWidth,
            windowIcons.LargeIconWidth);
    }

    /// <summary>What one real player run produced.</summary>
    internal sealed record PlayerRun(
        int ExitCode,
        string RoomName,
        string[] Screenshots,
        string Log,
        bool FoundWindow,
        bool HasSmallWindowIcon,
        bool HasLargeWindowIcon,
        int SmallWindowIconWidth,
        int LargeWindowIconWidth);
}
