using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Genesis.Application.Core.Diagnostics;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Settings;
using Genesis.Application.Studio;
using Genesis.Application.Studio.Docking;
using Genesis.Application.Studio.Forms;
using Genesis.Application.Studio.Resources;

namespace Genesis.Application.Headless.Suites;

internal static class ResourceLibrarySuite
{
    public static void Run(HeadlessContext context)
    {
        HeadlessHarness.BeginMajor(context.Report, "Studio H17 Resource Library");
        void Check(string name, Action test) => HeadlessHarness.RunCase(context.Report, "Studio.Resources." + name, test);
        ResourceLibraryCoreCases.Run(Check);
        Check("Browser.ConstructionDoesNotDecodeOrWarmWholeProject", () => WithFixture(f =>
        {
            using ResourceBrowserDock unopened = new(f.Resources, f.Services.Settings);
            Assert(unopened.CachedPreviewCount == 0 && unopened.PreviewStartCount == 0, "Constructing a browser decoded project artwork.");
        }));
        Check("Browser.OrdinaryRefreshRetainsNodesAndSelectionWithoutInspectorRebuild", () => WithFixture(f =>
        {
            ResourceItem item = f.Create(ResourceKind.Image, "Sprites", "H17 Portrait");
            ResourceBrowserDock browser = f.Shell.AssetBrowser;
            browser.SelectPath(item.FullPath);
            TreeNode selected = browser.SelectedNode!; IntPtr handle = selected.Handle;
            int selections = 0; browser.ResourceSelected += (_, _) => selections++;
            browser.RefreshTree(); browser.RefreshTree();
            Assert(ReferenceEquals(browser.SelectedNode, selected) && selected.Handle == handle && selections == 0,
                "Refresh recreated the selected node or republished an unchanged Inspector selection.");
        }));
        Check("Browser.DisclosureAndScrollAnchorSurviveRefresh", () => WithFixture(f =>
        {
            ResourceItem item = f.Create(ResourceKind.Image, "Sprites", "H17 Portrait");
            ResourceBrowserDock browser = f.Shell.AssetBrowser;
            TreeNode folder = Find(browser.BrowserTree, Path.GetDirectoryName(item.FullPath)!);
            folder.Expand(); browser.SelectPath(item.FullPath); folder.Collapse();
            TreeNode? selection = browser.SelectedNode; TreeNode? top = browser.BrowserTree.TopNode;
            browser.RefreshTree();
            Assert(!folder.IsExpanded && ReferenceEquals(selection, browser.SelectedNode)
                && ReferenceEquals(top, browser.BrowserTree.TopNode), "Refresh reopened a collapsed folder or moved the view.");
        }));
        Check("Browser.FiltersUseSnapshotAndRestoreFolderExpansion", () => WithFixture(f =>
        {
            ResourceItem item = f.Create(ResourceKind.Image, "Sprites", "H17 Portrait");
            f.Create(ResourceKind.Audio, "Audio", "H17 Wind");
            ResourceBrowserDock browser = f.Shell.AssetBrowser;
            TreeNode folder = Find(browser.BrowserTree, Path.GetDirectoryName(item.FullPath)!); folder.Expand();
            long generation = browser.SnapshotGeneration;
            browser.SetKindFilter(ResourceKind.Audio); browser.SetKindFilter(null);
            Assert(browser.SnapshotGeneration == generation && Find(browser.BrowserTree, folder.Name).IsExpanded,
                "Filtering scanned disk or discarded the normal folder disclosure state.");
        }));
        Check("Browser.HiddenSelectionCannotBeEditedThroughInspector", () => WithFixture(f =>
        {
            ResourceItem image = f.Create(ResourceKind.Image, "Sprites", "H17 Portrait");
            f.Shell.AssetBrowser.SelectPath(image.FullPath);
            byte[] before = File.ReadAllBytes(image.FullPath);
            f.Shell.AssetBrowser.SetKindFilter(ResourceKind.Audio);
            Assert(f.Shell.AssetBrowser.SelectedResource is null && f.Shell.Inspector.EditablePropertyPaths.Count == 0
                && !f.Shell.Inspector.SetEditableValue("origin.x", 14), "Hidden browser selection retained a live Inspector setter.");
            Assert(before.SequenceEqual(File.ReadAllBytes(image.FullPath)), "Clearing selection wrote hidden authored data.");
        }));
        Check("Browser.FavouriteRoundTripDoesNotModifyResource", () => WithFixture(f =>
        {
            ResourceItem item = f.Create(ResourceKind.Image, "Sprites", "H17 Portrait");
            ResourceBrowserDock browser = f.Shell.AssetBrowser; browser.SelectPath(item.FullPath);
            byte[] before = File.ReadAllBytes(item.FullPath);
            browser.Execute(ResourceBrowserCommand.ToggleFavourite); browser.Execute(ResourceBrowserCommand.ShowFavourites);
            Assert(browser.VisibleResourceCount == 1 && browser.FavouriteButton.Text == "★"
                && new ResourcePickerPreferences(f.Project.RootPath).IsFavourite(item.AssetId, item.Name), "Favourite did not persist.");
            browser.Execute(ResourceBrowserCommand.ToggleFavourite);
            Assert(browser.VisibleResourceCount == 0 && browser.SelectedResource is null
                && before.SequenceEqual(File.ReadAllBytes(item.FullPath)), "Removing a favourite changed game content or left a hidden selection.");
        }));
        Check("Browser.PickerWritesRefreshIntoOpenBrowser", () => WithFixture(f =>
        {
            ResourceItem item = f.Create(ResourceKind.Image, "Sprites", "H17 Portrait");
            ResourceBrowserDock browser = f.Shell.AssetBrowser;
            browser.SetScope(ResourceBrowserScope.Favourites);
            ResourcePickerPreferences picker = new(f.Project.RootPath);
            Assert(picker.SetFavourite(item.AssetId, item.Name, true), picker.LastError);
            browser.RefreshBookmarks();
            Assert(browser.VisibleResourceCount == 1 && browser.SelectPath(item.FullPath), "An already-open browser ignored picker favourites.");
        }));
        Check("Browser.HighlightingDoesNotRecordRecentButOpeningDoes", () => WithFixture(f =>
        {
            ResourceItem item = f.Create(ResourceKind.Note, "Notes", "H17 Notes");
            ResourceBrowserDock browser = f.Shell.AssetBrowser; browser.SelectPath(item.FullPath);
            Assert(!new ResourcePickerPreferences(f.Project.RootPath).IsRecent(item.AssetId, item.Name), "A highlighted row polluted recents.");
            f.Shell.OpenStudioResource(item); browser.SetScope(ResourceBrowserScope.Recent);
            Assert(browser.VisibleResourceCount == 1 && browser.SelectPath(item.FullPath), "A successfully opened document was not recorded.");
        }));
        Check("Browser.RenameKeepsFavouriteAndNodeIdentity", () => WithFixture(f =>
        {
            ResourceItem item = f.Create(ResourceKind.Note, "Notes", "H17 Notes");
            ResourceBrowserDock browser = f.Shell.AssetBrowser; browser.SelectPath(item.FullPath);
            TreeNode original = browser.SelectedNode!;
            browser.Execute(ResourceBrowserCommand.ToggleFavourite);
            f.Resources.Rename(item.FullPath, "H17 Renamed Notes"); browser.RefreshTree();
            Assert(ReferenceEquals(original, browser.SelectedNode) && browser.SelectedResource?.Name == "H17 Renamed Notes",
                "Logical rename broke the selected tree node.");
            browser.SetScope(ResourceBrowserScope.Favourites);
            Assert(browser.VisibleResourceCount == 1 && browser.SelectedResource?.Name == "H17 Renamed Notes", "Logical rename lost its favourite.");
        }));
        Check("Browser.EmptyFinderResultsAndResetAreUnambiguous", () => WithFixture(f =>
        {
            ResourceItem item = f.Create(ResourceKind.Note, "Notes", "H17 Notes");
            ResourceBrowserDock browser = f.Shell.AssetBrowser;
            browser.ApplyFinderResults("NoSuchResource", new([], 1, 0, false));
            Assert(browser.VisibleResourceCount == 0, "An empty Finder response exposed all assets.");
            browser.SetKindFilter(ResourceKind.Image); browser.SetScope(ResourceBrowserScope.Favourites);
            browser.Execute(ResourceBrowserCommand.ResetFilters);
            Assert(browser.LibraryScope == ResourceBrowserScope.All && browser.SelectPath(item.FullPath), "Reset left an invisible active filter.");
        }));
        Check("Browser.ProtectedRootsNeverBecomeBookmarkOrMutationTargets", () => WithFixture(f =>
        {
            ResourceBrowserDock browser = f.Shell.AssetBrowser;
            browser.SelectPath(Path.Combine(f.Project.AssetsPath, "Sprites"));
            foreach (ResourceBrowserCommand command in new[] { ResourceBrowserCommand.Delete, ResourceBrowserCommand.Rename,
                ResourceBrowserCommand.Cut, ResourceBrowserCommand.Duplicate, ResourceBrowserCommand.ToggleFavourite })
                Assert(!browser.CanExecute(command), "Protected root permits " + command);
        }));
        Check("Browser.NewActionsAreDiscoverableAndContextSafe", () => WithFixture(f =>
        {
            ResourceItem item = f.Create(ResourceKind.Note, "Notes", "H17 Notes");
            f.Shell.AssetBrowser.SelectPath(item.FullPath);
            foreach (string id in new[] { "resource.favourite", "resource.showAll", "resource.showFavourites", "resource.showRecent", "resource.resetFilters", "resource.clearRecent" })
                Assert(f.Shell.CommandCatalog.Find(id) is not null, "Missing shared command " + id);
            using TextBox unrelated = new();
            ShellCommandContext wrong = new(unrelated, null, item, false);
            Assert(!f.Shell.CommandCatalog.GetAvailability("resource.favourite", wrong).Enabled,
                "The palette changed a background browser selection while editing text.");
            ShellCommandContext correct = new(f.Shell.AssetBrowser.BrowserTree, null, item, true);
            Assert(f.Shell.CommandCatalog.TryExecute("resource.favourite", correct).Succeeded
                && new ResourcePickerPreferences(f.Project.RootPath).IsFavourite(item.AssetId, item.Name), "Focused favourite command failed.");
        }));
        Check("Browser.ClearRecentsPreservesBothResourcesAndFavourites", () => WithFixture(f =>
        {
            ResourceItem item = f.Create(ResourceKind.Note, "Notes", "H17 Notes");
            ResourceBrowserDock browser = f.Shell.AssetBrowser; browser.SelectPath(item.FullPath);
            browser.Execute(ResourceBrowserCommand.ToggleFavourite); f.Shell.OpenStudioResource(item);
            browser.Execute(ResourceBrowserCommand.ClearRecent); browser.Execute(ResourceBrowserCommand.ShowRecent);
            Assert(browser.VisibleResourceCount == 0 && File.Exists(item.FullPath), "Clear recents removed content or failed to clear history.");
            browser.Execute(ResourceBrowserCommand.ShowFavourites); Assert(browser.VisibleResourceCount == 1, "Clear recents cleared favourites.");
        }));
        Check("Thumbnails.NegativeEntriesAreBoundedAndDisposable", () => WithFixture(f =>
        {
            using ResourceThumbnailCache cache = new(f.Project.RootPath);
            for (int i = 0; i < 700; i++) cache.Warm(Fake(f.Project.RootPath, "Missing " + i));
            Assert(cache.Count == ResourceThumbnailCache.Capacity && !cache.Contains(Fake(f.Project.RootPath, "Missing 0")), "The cache is unbounded.");
            cache.Invalidate(); Assert(cache.Count == 0, "Invalidation retained entries.");
            cache.Dispose(); cache.Dispose();
            AssertThrows<ObjectDisposedException>(() => cache.GetCachedIcon(Fake(f.Project.RootPath, "Missing")));
        }));
        Check("Thumbnails.PrepareDoesNotMutateUiCacheAndReleasesInputFile", () => WithFixture(f =>
        {
            ResourceItem item = f.Create(ResourceKind.Image, "Sprites", "H17 Portrait");
            string png = Path.Combine(Path.GetDirectoryName(item.FullPath)!, "H17 Portrait.png");
            using (Bitmap pixels = new(8, 8)) pixels.Save(png, ImageFormat.Png);
            SetFrameSource(item, "./H17 Portrait.png");
            using ResourceThumbnailCache cache = new(f.Project.RootPath);
            using ResourceThumbnailCache.PreparedThumbnail prepared = Task.Run(() => cache.Prepare(item)).GetAwaiter().GetResult();
            Assert(cache.Count == 0, "A worker changed the UI cache.");
            cache.Accept(prepared); File.Delete(png);
            Assert(cache.Count == 1 && cache.ResolvedImageFor(item) is not null && !File.Exists(png), "The thumbnail retained a source-file lock.");
        }));
        Check("Thumbnails.SharedFrameAliasesUseTheNameResolvedFirstFrame", () => WithFixture(f =>
        {
            ResourceItem item = f.Create(ResourceKind.Image, "Sprites", "H17 Shared Portrait");
            string directory = Path.GetDirectoryName(item.FullPath)!;
            string sharedDirectory = Path.Combine(directory, "Shared"); Directory.CreateDirectory(sharedDirectory);
            string png = Path.Combine(sharedDirectory, "pixels.png");
            using (Bitmap pixels = new(8, 8)) pixels.Save(png, ImageFormat.Png);
            SetFrameSource(item, "./Shared/pixels.png");
            using ResourceThumbnailCache cache = new(f.Project.RootPath); cache.Warm(item);
            Assert(string.Equals(cache.ResolvedImageFor(item), png, StringComparison.OrdinalIgnoreCase),
                "A shared-frame image was treated as a missing local sidecar.");
            ResourceItem actor = f.Create(ResourceKind.GameObject, "Objects", "H17 Actor");
            JsonObject actorDocument = JsonNode.Parse(File.ReadAllText(actor.FullPath))!.AsObject();
            actorDocument["sprite"] = item.Name;
            File.WriteAllText(actor.FullPath, actorDocument.ToJsonString());
            cache.Warm(actor);
            Assert(string.Equals(cache.ResolvedImageFor(actor), png, StringComparison.OrdinalIgnoreCase),
                "The object thumbnail did not follow its public image name to shared pixels.");
        }));
        Check("Browser.CollapsedFoldersDoNotScheduleHiddenPreviews", () => WithFixture(f =>
        {
            ResourceItem item = f.Create(ResourceKind.Image, "Sprites", "H17 Hidden Portrait");
            ResourceBrowserDock browser = f.Shell.AssetBrowser;
            Find(browser.BrowserTree, Path.GetDirectoryName(item.FullPath)!).Collapse();
            GateSuite.Pump(5, 20);
            Assert(browser.CachedPreviewCount == 0, "The browser warmed thumbnails inside collapsed folders.");
        }));
        Check("Browser.RapidFilterThenCloseDoesNotLeaveWorkerUiAccess", () => WithFixture(f =>
        {
            ResourceItem item = f.Create(ResourceKind.Image, "Sprites", "H17 Portrait");
            ResourceBrowserDock browser = f.Shell.AssetBrowser; browser.SelectPath(item.FullPath);
            for (int i = 0; i < 12; i++) browser.SetKindFilter(i % 2 == 0 ? ResourceKind.Audio : ResourceKind.Image);
            browser.Dispose(); GateSuite.Pump(8, 20);
            Assert(browser.IsDisposed, "A browser stayed alive to wait for thumbnail work.");
        }));
    }

    private static void SetFrameSource(ResourceItem item, string relativePath)
    {
        ImageDocument document = ImageDocumentSerializer.LoadAtomic(item.FullPath).Document;
        // A fresh Image descriptor legitimately has no frames before the Image Editor opens it.
        if (document.Frames.Count == 0) document.Frames.Add(new ImageFrame());
        document.Frames[0].Source = relativePath;
        ImageDocumentSerializer.SaveAtomic(item.FullPath, document);
    }

    private static ResourceItem Fake(string root, string name) => new()
    { Name = name, FullPath = Path.Combine(root, name + ".md"), RelativePath = name + ".md", IsFolder = false, Kind = ResourceKind.Note };
    private static TreeNode Find(TreeView tree, string path) => tree.Nodes.Find(path, true).Single();
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void AssertThrows<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void WithFixture(Action<Fixture> test) { using Fixture fixture = new(); test(fixture); }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Genesis-H17-ui-" + Guid.NewGuid().ToString("N"));
        internal StudioServices Services { get; }
        internal ProjectSession Project { get; }
        internal ResourceService Resources { get; }
        internal StudioShellForm Shell { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Services = new(new SettingsService(Path.Combine(_root, "settings.json")), new ProjectService(), new ProjectValidator(), new StudioLog());
            Services.Settings.Current.Editing.AutoSave = false;
            Services.Settings.Current.General.CheckForExternalChanges = false;
            Project = Services.Projects.CreateProject(_root, "Library"); Resources = new(Project);
            Shell = new(Services, Project, persistLayout: false); GateSuite.ShowHost(Shell);
        }
        internal ResourceItem Create(ResourceKind kind, string folder, string name)
        {
            string path = Resources.CreateResource(Path.Combine(Project.AssetsPath, folder), kind, name);
            Shell.AssetBrowser.RefreshTree();
            return Flatten(Resources.BuildTree()).Single(item => string.Equals(item.FullPath, path, StringComparison.OrdinalIgnoreCase));
        }
        private static IEnumerable<ResourceItem> Flatten(ResourceItem item)
        { yield return item; foreach (ResourceItem child in item.Children) foreach (ResourceItem nested in Flatten(child)) yield return nested; }
        public void Dispose()
        {
            foreach (Form window in Shell.OwnedForms.ToArray()) window.Dispose();
            ResourceBrowserDock browser = Shell.AssetBrowser; Shell.Dispose();
            for (int i = 0; i < 200 && browser.PreviewBusy; i++) GateSuite.Pump(1, 10);
            Directory.Delete(_root, true);
        }
    }
}
