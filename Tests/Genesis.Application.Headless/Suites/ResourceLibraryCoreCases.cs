using Genesis.Application.Core.Resources;

namespace Genesis.Application.Headless.Suites;

/// <summary>Exact production projection/preferences, linked unchanged into the portable runner.</summary>
internal static class ResourceLibraryCoreCases
{
    public static void Run(Action<string, Action> check)
    {
        check("Library.AllRetainsProtectedAndEmptyFolders", () => WithFixture(f =>
        {
            ResourceBrowserView view = f.View();
            Assert(view.TotalResources == 3 && view.VisibleResources == 3 && !view.Filtered, "All scope lost resources.");
            Assert(view.Root.Children.Count == 3 && view.Root.Children[2].Resource.IsProtectedRoot,
                "Empty protected roots were hidden from the unfiltered browser.");
        }));
        check("Library.ProjectionNeverTouchesTheFilesystem", () => WithFixture(f =>
        {
            f.View(new(NameFilter: "Sky"));
            Assert(!Directory.Exists(Path.Combine(f.Root, "Assets")) && !Directory.Exists(Path.Combine(f.Root, ".genesis")),
                "Filtering an in-memory snapshot created source files or preferences.");
        }));
        check("Library.EmptySearchTypeFilterWorks", () => WithFixture(f =>
        {
            ResourceBrowserView view = f.View(new(Kind: ResourceKind.Audio));
            Assert(view.VisibleResources == 1 && Names(view).SequenceEqual(["Wind"]), "Type filtering required a search term.");
        }));
        check("Library.NameFilterIsCaseInsensitiveAndNameOnly", () => WithFixture(f =>
        {
            Assert(Names(f.View(new(NameFilter: "SKY"))).SequenceEqual(["Morning Sky", "Night Sky"]), "Names were not matched case-insensitively.");
            Assert(f.View(new(NameFilter: ".image.json")).VisibleResources == 0,
                "Private storage filenames leaked into public name filtering.");
        }));
        check("Library.TypeAndNameFiltersIntersect", () => WithFixture(f =>
        {
            Assert(f.View(new(Kind: ResourceKind.Audio, NameFilter: "Sky")).VisibleResources == 0,
                "Filters were ORed instead of intersected.");
        }));
        check("Library.FavouritesAreFlatSortedAndShared", () => WithFixture(f =>
        {
            ResourcePickerPreferences picker = new(f.Root);
            Assert(picker.SetFavourite(f.Wind.AssetId, f.Wind.Name, true) && picker.SetFavourite(f.Night.AssetId, f.Night.Name, true), picker.LastError);
            Assert(f.Preferences.Reload(), f.Preferences.LastError);
            ResourceBrowserView view = f.View(new(ResourceBrowserScope.Favourites));
            Assert(Names(view).SequenceEqual(["Night Sky", "Wind"]) && view.Root.Children.All(row => !row.Resource.IsFolder),
                "Favourites were not shared with the picker or not presented by name.");
        }));
        check("Library.FavouritesStillRespectTypeAndFinder", () => WithFixture(f =>
        {
            f.Favourite(f.Morning); f.Favourite(f.Wind);
            HashSet<string> hits = [f.Wind.FullPath];
            Assert(f.View(new(ResourceBrowserScope.Favourites, ResourceKind.Image, FinderPaths: hits)).VisibleResources == 0,
                "A favourite bypassed the active type or Finder intersection.");
        }));
        check("Library.RecentsUseGlobalNewestFirstAcrossFolders", () => WithFixture(f =>
        {
            f.Remember(f.Morning); f.Remember(f.Wind); f.Remember(f.Night);
            Assert(Names(f.View(new(ResourceBrowserScope.Recent))).SequenceEqual(["Night Sky", "Wind", "Morning Sky"]),
                "Folder sorting replaced chronological recent order.");
            f.Remember(f.Morning);
            Assert(Names(f.View(new(ResourceBrowserScope.Recent))).SequenceEqual(["Morning Sky", "Night Sky", "Wind"]),
                "Reopening duplicated history instead of moving the resource to the front.");
        }));
        check("Library.BookmarksSurvivePublicRename", () => WithFixture(f =>
        {
            f.Favourite(f.Morning); f.Remember(f.Morning);
            f.Morning.Name = "New Dawn";
            Assert(Names(f.View(new(ResourceBrowserScope.Favourites))).SequenceEqual(["New Dawn"])
                && Names(f.View(new(ResourceBrowserScope.Recent))).SequenceEqual(["New Dawn"]), "Rename broke GUID bookmarks.");
        }));
        check("Library.DeletedResourcesAreNotResurrected", () => WithFixture(f =>
        {
            f.Favourite(f.Morning); f.Remember(f.Morning); f.Assets.Children[0].Children.Remove(f.Morning);
            Assert(f.View(new(ResourceBrowserScope.Favourites)).VisibleResources == 0
                && f.View(new(ResourceBrowserScope.Recent)).VisibleResources == 0, "Stale bookmarks invented a resource.");
        }));
        check("Library.RecreatedSameNameDoesNotInheritOldGuidBookmark", () => WithFixture(f =>
        {
            f.Favourite(f.Morning); f.Assets.Children[0].Children.Remove(f.Morning);
            f.Assets.Children[0].Children.Add(FileItem(f.Root, "Morning Sky", ResourceKind.Image));
            Assert(f.View(new(ResourceBrowserScope.Favourites)).VisibleResources == 0, "A new resource inherited an old resource's bookmark.");
        }));
        check("Library.NoFinderHitsIsNotAnUnfilteredView", () => WithFixture(f =>
        {
            ResourceBrowserView view = f.View(new(FinderPaths: new HashSet<string>()));
            Assert(view.VisibleResources == 0 && view.Root.Children.Count == 0 && view.Filtered, "Empty search results exposed every asset.");
        }));
        check("Library.FinderIdentityComparisonIgnoresCase", () => WithFixture(f =>
        {
            HashSet<string> hits = [f.Wind.FullPath.ToUpperInvariant()];
            Assert(Names(f.View(new(FinderPaths: hits))).SequenceEqual(["Wind"]), "Finder key casing hid a valid resource.");
        }));
        check("Library.FolderSearchDoesNotInventResourceMatches", () => WithFixture(f =>
        {
            ResourceBrowserView view = f.View(new(NameFilter: "Sprites"));
            Assert(view.VisibleResources == 0 && view.Root.Children.Single().Resource.Name == "Sprites", "Folder matches changed resource counts.");
        }));
        check("Library.FilteringDoesNotMutateSourceOrderOrIdentity", () => WithFixture(f =>
        {
            ResourceItem[] before = f.Assets.Children[0].Children.ToArray();
            f.Favourite(f.Night); f.View(new(ResourceBrowserScope.Favourites)); f.View(new(Kind: ResourceKind.Audio)); f.View();
            Assert(before.SequenceEqual(f.Assets.Children[0].Children)
                && ReferenceEquals(f.View().Root.Resource, f.Assets), "Projection edited its shared resource snapshot.");
        }));
        check("Library.UiKeySurvivesMoveAndRename", () => WithFixture(f =>
        {
            ResourceItem moved = new() { Name = "Renamed", FullPath = Path.Combine(f.Root, "Elsewhere", "private.image.json"),
                RelativePath = "Elsewhere/private.image.json", Kind = ResourceKind.Image, IsFolder = false, AssetId = f.Morning.AssetId };
            Assert(ResourceBrowserProjection.Key(moved) == ResourceBrowserProjection.Key(f.Morning), "UI identity depends on a resource's filename.");
        }));
        check("Library.AllTypesIncludesEveryNonFolderKind", () => WithFixture(f =>
        {
            f.Assets.Children.Clear();
            foreach (ResourceKind kind in Enum.GetValues<ResourceKind>().Where(kind => kind != ResourceKind.Folder))
                f.Assets.Children.Add(FileItem(f.Root, kind.ToString(), kind));
            Assert(f.View().VisibleResources == Enum.GetValues<ResourceKind>().Length - 1, "A supported type disappeared from All.");
        }));
        check("Library.LargeSnapshotIsFilteredWithoutFilesystemAccess", () => WithFixture(f =>
        {
            f.Assets.Children.Clear();
            for (int i = 0; i < 20_000; i++) f.Assets.Children.Add(FileItem(f.Root, "Asset " + i,
                i % 2 == 0 ? ResourceKind.Image : ResourceKind.Audio));
            ResourceBrowserView view = f.View(new(Kind: ResourceKind.Audio));
            Assert(view.TotalResources == 20_000 && view.VisibleResources == 10_000 && !Directory.Exists(Path.Combine(f.Root, "Assets")),
                "Large snapshot filtering read source files or lost matches.");
        }));
        check("Library.InvalidRootAndScopeAreRejected", () => WithFixture(f =>
        {
            AssertThrows<ArgumentException>(() => ResourceBrowserProjection.Build(f.Morning, new(), f.Preferences));
            AssertThrows<ArgumentOutOfRangeException>(() => f.View(new((ResourceBrowserScope)999)));
        }));
        check("Library.ReloadOnlyAdvancesRevisionWhenChoicesChange", () => WithFixture(f =>
        {
            long revision = f.Preferences.SnapshotRevision;
            Assert(f.Preferences.Reload() && f.Preferences.SnapshotRevision == revision, "A no-op reload dirtied the view.");
            ResourcePickerPreferences second = new(f.Root); Assert(second.SetFavourite(f.Night.AssetId, f.Night.Name, true), second.LastError);
            Assert(f.Preferences.Reload() && f.Preferences.SnapshotRevision > revision, "External preferences were not noticed.");
            revision = f.Preferences.SnapshotRevision;
            Assert(f.Preferences.Reload() && f.Preferences.SnapshotRevision == revision, "An unchanged disk file forced a refresh.");
            Assert(second.SetFavourite(f.Night.AssetId, f.Night.Name, false) && second.Remember(f.Wind.AssetId, f.Wind.Name), second.LastError);
            Assert(f.Preferences.Reload() && !f.Preferences.IsFavourite(f.Night.AssetId, f.Night.Name)
                && f.Preferences.IsRecent(f.Wind.AssetId, f.Wind.Name) && f.Preferences.RecentRank(f.Wind.AssetId, f.Wind.Name) == 0,
                "Derived bookmark indexes were stale after external removal/history update.");
        }));
        check("Library.CorruptReloadRetainsLastGoodChoices", () => WithFixture(f =>
        {
            f.Favourite(f.Morning); string path = PreferencePath(f.Root); File.WriteAllText(path, "corrupt");
            Assert(!f.Preferences.Reload() && f.Preferences.IsFavourite(f.Morning.AssetId, f.Morning.Name)
                && File.ReadAllText(path) == "corrupt", "Reload discarded the last good selection or overwrote broken data.");
        }));
        check("Library.DeletedPreferenceFileClearsOnlyPreferences", () => WithFixture(f =>
        {
            f.Favourite(f.Morning); File.Delete(PreferencePath(f.Root));
            Assert(f.Preferences.Reload() && f.View(new(ResourceBrowserScope.Favourites)).VisibleResources == 0
                && f.View().VisibleResources == 3, "Deleting preferences affected authored resources.");
        }));
        check("Library.ClearHistoryDoesNotClearFavourites", () => WithFixture(f =>
        {
            f.Favourite(f.Night); f.Remember(f.Night); Assert(f.Preferences.ClearRecent(), f.Preferences.LastError);
            Assert(f.View(new(ResourceBrowserScope.Recent)).VisibleResources == 0
                && Names(f.View(new(ResourceBrowserScope.Favourites))).SequenceEqual(["Night Sky"]), "Clearing recent history lost favourites.");
        }));
        check("Library.KindValuesRemainSerializationCompatible", () =>
        {
            string[] expected = ["Unknown", "Folder", "Image", "Audio", "Shader", "PgslScript", "GameObject", "Room", "Model", "Particle", "Physics", "Terrain", "TerrainEntity", "Pathing", "UserInterface", "Note"];
            Assert(Enum.GetValues<ResourceKind>().Select(kind => kind.ToString()).SequenceEqual(expected), "Resource kind values changed during file separation.");
        });
    }

    private static IEnumerable<string> Names(ResourceBrowserView view) => Flatten(view.Root)
        .Where(row => !row.Resource.IsFolder).Select(row => row.Resource.Name);
    private static IEnumerable<ResourceBrowserRow> Flatten(ResourceBrowserRow row)
    { yield return row; foreach (ResourceBrowserRow child in row.Children) foreach (ResourceBrowserRow nested in Flatten(child)) yield return nested; }
    private static string PreferencePath(string root) => Path.Combine(root, ".genesis", "User", "ResourcePicker.json");
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void AssertThrows<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void WithFixture(Action<Fixture> action) { using Fixture fixture = new(); action(fixture); }
    private static ResourceItem FileItem(string root, string name, ResourceKind kind) => new()
    { Name = name, FullPath = Path.Combine(root, "Assets", name + ".image.json"), RelativePath = name + ".image.json", Kind = kind, IsFolder = false, AssetId = Guid.NewGuid() };
    private static ResourceItem Folder(string root, string name, bool protectedRoot = false) => new()
    { Name = name, FullPath = Path.Combine(root, "Assets", name), RelativePath = name, Kind = ResourceKind.Folder, IsFolder = true, IsProtectedRoot = protectedRoot };
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Genesis-H17-core-" + Guid.NewGuid().ToString("N"));
        internal ResourceItem Assets { get; }
        internal ResourceItem Morning { get; }
        internal ResourceItem Night { get; }
        internal ResourceItem Wind { get; }
        internal ResourcePickerPreferences Preferences { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(Root); Preferences = new(Root);
            Assets = Folder(Root, ""); Morning = FileItem(Root, "Morning Sky", ResourceKind.Image);
            Night = FileItem(Root, "Night Sky", ResourceKind.Image); Wind = FileItem(Root, "Wind", ResourceKind.Audio);
            ResourceItem sprites = Folder(Root, "Sprites", true), audio = Folder(Root, "Audio", true), notes = Folder(Root, "Notes", true);
            sprites.Children.AddRange([Morning, Night]); audio.Children.Add(Wind); Assets.Children.AddRange([sprites, audio, notes]);
        }
        internal ResourceBrowserView View(ResourceBrowserQuery? query = null) => ResourceBrowserProjection.Build(Assets, query ?? new(), Preferences);
        internal void Favourite(ResourceItem item) => Assert(Preferences.SetFavourite(item.AssetId, item.Name, true), Preferences.LastError);
        internal void Remember(ResourceItem item) => Assert(Preferences.Remember(item.AssetId, item.Name), Preferences.LastError);
        public void Dispose() { Directory.Delete(Root, true); }
    }
}
