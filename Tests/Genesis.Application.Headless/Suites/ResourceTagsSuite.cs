using System.Text.Json.Nodes;
using System.Windows.Forms;
using Genesis.Application.Core.Diagnostics;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Settings;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Application.Studio;
using Genesis.Application.Studio.Docking;
using Genesis.Application.Studio.Forms;

namespace Genesis.Application.Headless.Suites;

internal static class ResourceTagsSuite
{
    public static void Run(HeadlessContext context)
    {
        HeadlessHarness.BeginMajor(context.Report, "Studio H20 Library Tags");
        void Check(string name, Action test) => HeadlessHarness.RunCase(context.Report, "Studio.LibraryTags." + name, test);
        ResourceTagsCoreCases.Run(Check);
        Check("Resource.SnapshotAndPickerIndexPublishSameTags", () => WithFixture(f =>
        {
            ResourceItem tree = f.Create(ResourceKind.Image, "Sprites", "H20 Tree"); f.Tag(tree, "forest", "night");
            Assert(f.Current(tree).LibraryTags.SequenceEqual(["forest", "night"]), "Resource tree lost metadata tags.");
            Assert(ProjectAssetIndex.Enumerate(f.Project.RootPath, ResourceKind.Image).Single(e => e.AssetId == tree.AssetId)
                .LibraryTags.SequenceEqual(["forest", "night"]), "Picker index disagrees with resource tree.");
        }));
        Check("Resource.RenameAndMovePreserveLibraryTags", () => WithFixture(f =>
        {
            ResourceItem note = f.Create(ResourceKind.Note, "Notes", "H20 Notes"); f.Tag(note, "forest");
            string renamed = f.Resources.Rename(note.FullPath, "H20 Renamed");
            string folder = f.Resources.CreateFolder(Path.Combine(f.Project.AssetsPath, "Notes"), "Nested");
            string moved = f.Resources.Move(renamed, folder);
            ResourceItem item = Flatten(f.Resources.BuildTree()).Single(e => e.FullPath == moved);
            Assert(item.AssetId == note.AssetId && item.Name == "H20 Renamed" && item.LibraryTags.SequenceEqual(["forest"]),
                "Moving or renaming changed classification/identity.");
        }));
        Check("Resource.DuplicatePreservesTagsButGetsFreshIdentity", () => WithFixture(f =>
        {
            ResourceItem note = f.Create(ResourceKind.Note, "Notes", "H20 Notes"); f.Tag(note, "forest", "night");
            string copy = f.Resources.Duplicate(note.FullPath);
            ResourceItem item = Flatten(f.Resources.BuildTree()).Single(e => e.FullPath == copy);
            Assert(item.AssetId != note.AssetId && item.Name != note.Name && item.LibraryTags.SequenceEqual(["forest", "night"]),
                "Duplicate lost tags or shared identity/name.");
        }));
        Check("Resource.ImportPreservesLibraryClassification", () => WithFixture(f =>
        {
            string source = Path.Combine(f.Root, "Imported.md"); File.WriteAllText(source, "External note.");
            File.WriteAllText(source + ".meta", new JsonObject
            { ["guid"] = Guid.NewGuid().ToString("N"), ["libraryTags"] = new JsonArray("forest", "reference") }.ToJsonString());
            string imported = f.Resources.ImportFiles(Path.Combine(f.Project.AssetsPath, "Notes"), [source]).Single();
            Assert(ResourceLibraryTags.ReadOrEmpty(imported).SequenceEqual(["forest", "reference"]), "Import discarded library tags.");
        }));
        Check("Resource.BadTagsDoNotRegenerateValidIdentityOnBrowse", () => WithFixture(f =>
        {
            ResourceItem note = f.Create(ResourceKind.Note, "Notes", "H20 Notes");
            JsonObject metadata = JsonNode.Parse(File.ReadAllText(note.FullPath + ".meta"))!.AsObject();
            metadata["libraryTags"] = 42; File.WriteAllText(note.FullPath + ".meta", metadata.ToJsonString());
            ResourceLibraryTags.Invalidate(note.FullPath); string before = File.ReadAllText(note.FullPath + ".meta");
            ResourceItem current = f.Current(note);
            Assert(current.AssetId == note.AssetId && current.LibraryTags.Count == 0 && File.ReadAllText(note.FullPath + ".meta") == before,
                "Malformed optional tags caused destructive identity repair.");
        }));
        Check("Resource.ProtectedFoldersCannotHaveLibraryTagEdits", () => WithFixture(f =>
        {
            ResourceItem folder = Flatten(f.Resources.BuildTree()).Single(e => e.IsProtectedRoot && e.Name == "Sprites");
            f.Shell.AssetBrowser.RefreshTree(); f.Shell.AssetBrowser.SelectPath(folder.FullPath);
            Assert(!f.Shell.AssetBrowser.CanExecute(ResourceBrowserCommand.EditLibraryTags), "Protected folder received an asset tag editor.");
        }));
        Check("Picker.TagsNeverBypassBackgroundUsageOrResourceType", () => WithFixture(f =>
        {
            ResourceItem background = f.Create(ResourceKind.Image, "Sprites", "H20 Sky"); f.Tag(background, "night"); f.Usage(background, ImageUsage.Background);
            ResourceItem sprite = f.Create(ResourceKind.Image, "Sprites", "H20 Hero"); f.Tag(sprite, "night"); f.Usage(sprite, ImageUsage.Sprite);
            using AssetPickerModal picker = new(new(f.Project.RootPath, ResourceKind.Image, RequiredImageUsage: ImageUsage.Background),
                ProjectAssetIndex.Enumerate(f.Project.RootPath, ResourceKind.Image));
            picker.SetFilter("tag:night");
            Assert(picker.FilteredAssets.Count == 1 && picker.FilteredAssets[0].Reference == background.Name,
                "A tag query bypassed H19's image-usage contract.");
        }));
        Check("Picker.QuotedAndNegativeTagQueriesReturnResourceNames", () => WithFixture(f =>
        {
            ResourceItem sky = f.Create(ResourceKind.Image, "Sprites", "H20 Sky"); f.Tag(sky, "night sky");
            ResourceItem hud = f.Create(ResourceKind.Image, "Sprites", "H20 HUD"); f.Tag(hud, "night sky", "ui");
            using AssetPickerModal picker = new(new(f.Project.RootPath, ResourceKind.Image), ProjectAssetIndex.Enumerate(f.Project.RootPath, ResourceKind.Image));
            picker.SetFilter("tag:\"night sky\" -tag:ui");
            Assert(picker.FilteredAssets.Single().Reference == sky.Name, "Picker tags broke name-only references or exclusion.");
            picker.SetFilter("tag:"); Assert(picker.FilteredAssets.Count == 0, "Incomplete picker query exposed all assets.");
        }));
        Check("Finder.TagOnlyQueryNeedsNoContentReads", () => WithFixture(f =>
        {
            ResourceItem note = f.Create(ResourceKind.Note, "Notes", "H20 Notes"); f.Tag(note, "forest");
            ResourceSearchResponse response = new ResourceSearchService().Search(f.Resources.BuildTree(), new()
            { Term = "tag:forest", ScopePath = f.Project.AssetsPath, SearchContents = true });
            Assert(response.Results.Count == 1 && response.Results[0].Resource.AssetId == note.AssetId
                && response.Results[0].MatchLabel == "Library tags" && response.SkippedContentFiles == 0, "Finder tag-only matching read content or lost classification.");
        }));
        Check("Finder.ContentSearchRespectsTagRestrictions", () => WithFixture(f =>
        {
            ResourceItem selected = f.Create(ResourceKind.Note, "Notes", "H20 Selected"), excluded = f.Create(ResourceKind.Note, "Notes", "H20 Excluded");
            File.WriteAllText(selected.FullPath, "spectral_marker"); File.WriteAllText(excluded.FullPath, "spectral_marker"); f.Tag(selected, "night");
            ResourceSearchResponse response = new ResourceSearchService().Search(f.Resources.BuildTree(), new()
            { Term = "tag:night spectral_marker", ScopePath = f.Project.AssetsPath, SearchContents = true });
            Assert(response.Results.Count == 1 && response.Results[0].Resource.AssetId == selected.AssetId
                && response.Results[0].MatchKind.HasFlag(ResourceSearchMatchKind.Content), "Content scan bypassed tag constraints.");
        }));
        Check("Dialog.ValidatesDraftAndCancelDoesNotSave", () => WithFixture(f =>
        {
            ResourceItem note = f.Create(ResourceKind.Note, "Notes", "H20 Notes"); string before = File.ReadAllText(note.FullPath + ".meta");
            using ResourceLibraryTagsDialog dialog = new(note.Name, [], ["forest", "night"]);
            dialog.TagInput.Text = "night, forest, FOREST";
            Assert(dialog.ApplyButton.Enabled && dialog.SelectedTags.SequenceEqual(["forest", "night"]), "Dialog did not validate/deduplicate its draft.");
            dialog.DialogResult = DialogResult.Cancel;
            Assert(File.ReadAllText(note.FullPath + ".meta") == before, "Draft/cancel wrote a resource.");
        }));
        Check("Dialog.InvalidDraftDisablesApply", () =>
        {
            using ResourceLibraryTagsDialog dialog = new("Hero", [], []);
            dialog.TagInput.Text = new string('a', 49);
            Assert(!dialog.ApplyButton.Enabled, "An invalid tag can be submitted.");
            dialog.TagInput.Text = "forest"; Assert(dialog.ApplyButton.Enabled, "Fixing a tag did not restore Apply.");
        });
        Check("Browser.TagFilterUsesSnapshotAndResetClearsIt", () => WithFixture(f =>
        {
            ResourceItem note = f.Create(ResourceKind.Note, "Notes", "H20 Notes"); f.Tag(note, "forest");
            ResourceBrowserDock browser = f.Shell.AssetBrowser; browser.RefreshTree(); long generation = browser.SnapshotGeneration;
            browser.SetLibraryTagFilter("forest"); Assert(browser.VisibleResourceCount == 1 && browser.SnapshotGeneration == generation, "Tag filtering rescanned disk.");
            browser.SetLibraryTagFilter("missing"); Assert(browser.VisibleResourceCount == 0, "Missing tag was interpreted as no filter.");
            browser.ResetLibraryFilters(); Assert(browser.VisibleResourceCount > 1 && browser.LibraryScope == ResourceBrowserScope.All, "Reset retained a hidden tag filter.");
        }));
        Check("Browser.TagEditAndSharedUndoRedoUseMetadataHistory", () => WithFixture(f =>
        {
            ResourceItem note = f.Create(ResourceKind.Note, "Notes", "H20 Notes"); ResourceBrowserDock browser = f.Shell.AssetBrowser;
            browser.ApplyLibraryTagEdit(ResourceLibraryTags.Capture(f.Project.AssetsPath, note.FullPath, note.AssetId), ["forest"]);
            browser.RefreshTree(); browser.SelectPath(note.FullPath);
            ShellCommandContext target = new(browser.BrowserTree, null, browser.SelectedResource, true);
            Assert(f.Shell.CommandCatalog.TryExecute("edit.undo", target).Succeeded && ResourceLibraryTags.ReadOrEmpty(note.FullPath).Count == 0,
                "Shared Undo failed to route to the browser tag journal.");
            Assert(f.Shell.CommandCatalog.TryExecute("edit.redo", target).Succeeded && ResourceLibraryTags.ReadOrEmpty(note.FullPath).SequenceEqual(["forest"]),
                "Shared Redo failed to restore tags.");
        }));
        Check("Browser.EmptyTagHistoryCannotUndoUnrelatedEditor", () => WithFixture(f =>
        {
            ResourceBrowserDock browser = f.Shell.AssetBrowser;
            ShellCommandContext target = new(browser.BrowserTree, null, browser.SelectedResource, true);
            Assert(!f.Shell.CommandCatalog.GetAvailability("edit.undo", target).Enabled, "Empty browser history fell through to another editor.");
        }));
        Check("Commands.ExistAndRespectOriginalBrowserContext", () => WithFixture(f =>
        {
            foreach (string id in new[] { "resource.tags", "resource.tags.undo", "resource.tags.redo" })
                Assert(f.Shell.CommandCatalog.Find(id) is not null, "Missing shared command " + id);
            ResourceItem note = f.Create(ResourceKind.Note, "Notes", "H20 Notes"); f.Shell.AssetBrowser.SelectPath(note.FullPath);
            using Panel unrelated = new();
            Assert(!f.Shell.CommandCatalog.GetAvailability("resource.tags", new(unrelated, null, note, false)).Enabled,
                "Tag command escaped its originating resource context.");
            Assert(f.Shell.CommandCatalog.GetAvailability("resource.tags", new(f.Shell.AssetBrowser.BrowserTree, null, note, true)).Enabled,
                "Tag command is unavailable for a valid resource.");
        }));
    }

    private static IEnumerable<ResourceItem> Flatten(ResourceItem root)
    { yield return root; foreach (ResourceItem child in root.Children) foreach (ResourceItem nested in Flatten(child)) yield return nested; }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void WithFixture(Action<Fixture> action) { using Fixture fixture = new(); action(fixture); }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Genesis-H20-ui-" + Guid.NewGuid().ToString("N"));
        internal StudioServices Services { get; }
        internal ProjectSession Project { get; }
        internal ResourceService Resources { get; }
        internal StudioShellForm Shell { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Services = new(new SettingsService(Path.Combine(Root, "settings.json")), new ProjectService(), new ProjectValidator(), new StudioLog());
            Services.Settings.Current.Editing.AutoSave = false;
            Services.Settings.Current.General.CheckForExternalChanges = false;
            Project = Services.Projects.CreateProject(Root, "Tags"); Resources = new(Project);
            Shell = new(Services, Project, persistLayout: false); GateSuite.ShowHost(Shell);
        }
        internal ResourceItem Create(ResourceKind kind, string folder, string name)
        {
            string path = Resources.CreateResource(Path.Combine(Project.AssetsPath, folder), kind, name);
            Shell.AssetBrowser.RefreshTree(); return Flatten(Resources.BuildTree()).Single(item => item.FullPath == path);
        }
        internal ResourceItem Current(ResourceItem resource) => Flatten(Resources.BuildTree()).Single(item => item.AssetId == resource.AssetId);
        internal void Tag(ResourceItem resource, params string[] tags) => Resources.SetLibraryTags(
            ResourceLibraryTags.Capture(Project.AssetsPath, resource.FullPath, resource.AssetId), tags);
        internal void Usage(ResourceItem resource, ImageUsage usage)
        {
            ImageDocument document = ImageDocumentSerializer.LoadAtomic(resource.FullPath).Document;
            document.Usage.Allowed = usage; ImageDocumentSerializer.SaveAtomic(resource.FullPath, document);
        }
        public void Dispose()
        {
            foreach (Form form in Shell.OwnedForms.ToArray()) form.Dispose();
            ResourceBrowserDock browser = Shell.AssetBrowser; Shell.Dispose();
            for (int i = 0; i < 200 && browser.PreviewBusy; i++) GateSuite.Pump(1, 10);
            Directory.Delete(Root, true);
        }
    }
}
