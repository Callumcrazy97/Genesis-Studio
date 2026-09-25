using System.Text.Json.Nodes;
using Genesis.Application.Core.Resources;

namespace Genesis.Application.Headless.Suites;

/// <summary>Exact production metadata/query/history implementations; also linked by the portable runner.</summary>
internal static class ResourceTagsCoreCases
{
    public static void Run(Action<string, Action> check)
    {
        check("Tags.NormalizeTrimDeduplicateSort", () => Assert(ResourceLibraryTags.ParseInput(" Forest,forest; UI\nnight   sky ")
            .SequenceEqual(["Forest", "night sky", "UI"]), "Tag normalization lost identity or order."));
        check("Tags.EmptyRemovesTags", () => Assert(ResourceLibraryTags.ParseInput(" , ;\r\n ").Count == 0, "Blank did not clear tags."));
        check("Tags.UnicodeIsPreserved", () => Assert(ResourceLibraryTags.Normalize(["Café", "Cafe\u0301", "森"])
            .Count == 2, "Unicode normalization changed meaning or kept duplicate forms."));
        check("Tags.LimitsAndReservedCharacters", () =>
        {
            Throws<ArgumentException>(() => ResourceLibraryTags.Normalize([new string('x', 49)]));
            Throws<ArgumentException>(() => ResourceLibraryTags.Normalize(Enumerable.Range(0, 33).Select(i => "tag " + i)));
            Throws<ArgumentException>(() => ResourceLibraryTags.Normalize(["bad\tvalue"]));
            Throws<ArgumentException>(() => ResourceLibraryTags.Normalize(["bad\"quote"]));
            Throws<ArgumentException>(() => ResourceLibraryTags.ParseInput(new string('x', 4097)));
        });
        check("Tags.ReadsOnlyLibraryMetadataNotAnimationTags", () => WithFixture(f =>
        {
            File.WriteAllText(f.Resource, "{\"tags\":[{\"name\":\"Run\"}],\"tag\":\"enemy\"}");
            Assert(ResourceLibraryTags.ReadOrEmpty(f.Resource).Count == 0, "Animation/gameplay tags leaked into library classification.");
        }));
        check("Tags.AtomicWritePreservesIdentitySourceAndUnknownFields", () => WithFixture(f =>
        {
            string source = File.ReadAllText(f.Resource);
            JsonObject before = JsonNode.Parse(File.ReadAllText(f.Meta))!.AsObject();
            ResourceLibraryTagSnapshot result = ResourceLibraryTags.Apply(f.Capture(), ["forest", "night"]);
            JsonObject after = JsonNode.Parse(File.ReadAllText(f.Meta))!.AsObject();
            Assert(File.ReadAllText(f.Resource) == source && after["guid"]!.ToString() == before["guid"]!.ToString()
                && after["resourceName"]!.ToString() == "Hero" && after["custom"]!.ToJsonString() == before["custom"]!.ToJsonString()
                && result.Tags.SequenceEqual(["forest", "night"]), "Tag writing damaged unrelated source/metadata.");
            Assert(!Directory.EnumerateFiles(f.Assets, ".tags-*", SearchOption.AllDirectories).Any(), "Temporary tag file leaked.");
        }));
        check("Tags.NoOpDoesNotRewriteMetadata", () => WithFixture(f =>
        {
            ResourceLibraryTagSnapshot first = ResourceLibraryTags.Apply(f.Capture(), ["forest"]);
            byte[] before = File.ReadAllBytes(f.Meta);
            ResourceLibraryTagSnapshot same = ResourceLibraryTags.Apply(first, ["FOREST", "forest"]);
            Assert(first.Revision == same.Revision && before.SequenceEqual(File.ReadAllBytes(f.Meta)), "A no-op rewrote metadata.");
        }));
        check("Tags.StaleDialogIsRejectedWithoutOverwriting", () => WithFixture(f =>
        {
            ResourceLibraryTagSnapshot stale = f.Capture();
            f.Change(root => root["custom"] = "externally changed"); string changed = File.ReadAllText(f.Meta);
            Throws<InvalidOperationException>(() => ResourceLibraryTags.Apply(stale, ["forest"]));
            Assert(File.ReadAllText(f.Meta) == changed, "Stale editor overwrote an external metadata edit.");
        }));
        check("Tags.ReusedPathCannotReplaceAnotherIdentity", () => WithFixture(f =>
        {
            ResourceLibraryTagSnapshot stale = f.Capture(); f.Change(root => root["guid"] = Guid.NewGuid().ToString("N"));
            Throws<InvalidOperationException>(() => ResourceLibraryTags.Apply(stale, ["forest"]));
        }));
        check("Tags.NewerMetadataVersionIsPreserved", () => WithFixture(f =>
        {
            f.Change(root => root["schemaVersion"] = 999); string before = File.ReadAllText(f.Meta);
            Assert(ResourceLibraryTags.ReadOrEmpty(f.Resource).Count == 0, "Unknown metadata version was interpreted as editable tags.");
            Throws<InvalidDataException>(() => f.Capture());
            Assert(File.ReadAllText(f.Meta) == before, "Future metadata was repaired/destructively downgraded.");
        }));
        check("Tags.CorruptMetadataDoesNotBreakBrowseOrGetOverwritten", () => WithFixture(f =>
        {
            File.WriteAllText(f.Meta, "{broken"); ResourceLibraryTags.Invalidate(f.Resource);
            Assert(ResourceLibraryTags.ReadOrEmpty(f.Resource).Count == 0, "Corrupt metadata broke enumeration.");
            Throws<System.Text.Json.JsonException>(() => f.Capture());
            Assert(File.ReadAllText(f.Meta) == "{broken", "Corruption was silently overwritten.");
        }));
        check("Tags.MalformedTagValuesAreRejected", () => WithFixture(f =>
        {
            f.Change(root => root["libraryTags"] = new JsonArray(JsonValue.Create(123)));
            Assert(ResourceLibraryTags.ReadOrEmpty(f.Resource).Count == 0, "Invalid tag data escaped the tolerant reader.");
            Throws<InvalidDataException>(() => f.Capture());
        }));
        check("Tags.MetadataReadIsSizeBounded", () => WithFixture(f =>
        {
            File.WriteAllText(f.Meta, new string(' ', ResourceLibraryTags.MaximumMetadataBytes + 1));
            Assert(ResourceLibraryTags.ReadOrEmpty(f.Resource).Count == 0, "Oversized metadata was loaded as tags.");
            Throws<InvalidDataException>(() => f.Capture());
        }));
        check("Tags.PathBoundaryAndMissingResourceAreRejected", () => WithFixture(f =>
        {
            string outside = Path.Combine(f.Root, "Outside.md"); File.WriteAllText(outside, "outside");
            Throws<UnauthorizedAccessException>(() => ResourceLibraryTags.Capture(f.Assets, outside));
            File.Delete(f.Resource);
            Throws<UnauthorizedAccessException>(() => f.Capture());
        }));
        check("Tags.CacheInvalidationReplacesSameLengthSameTimestamp", () => WithFixture(f =>
        {
            ResourceLibraryTags.Apply(f.Capture(), ["night"]);
            Assert(ResourceLibraryTags.ReadOrEmpty(f.Resource).Single() == "night", "Initial cached tag missing.");
            DateTime time = File.GetLastWriteTimeUtc(f.Meta);
            File.WriteAllText(f.Meta, File.ReadAllText(f.Meta).Replace("night", "light", StringComparison.Ordinal));
            File.SetLastWriteTimeUtc(f.Meta, time); ResourceLibraryTags.Invalidate(f.Resource);
            Assert(ResourceLibraryTags.ReadOrEmpty(f.Resource).Single() == "light", "An invalidated metadata cache retained stale values.");
        }));
        check("Tags.HistoryUndoRedoRestoresOnlyTags", () => WithFixture(f =>
        {
            ResourceLibraryTagHistory history = new();
            ResourceLibraryTagSnapshot before = f.Capture(), after = ResourceLibraryTags.Apply(before, ["forest"]); history.Record(before, after);
            f.Change(root => root["custom"] = "keep current");
            history.Undo(f.Assets, _ => f.Resource);
            Assert(f.Capture().Tags.Count == 0 && JsonNode.Parse(File.ReadAllText(f.Meta))!["custom"]!.ToString() == "keep current",
                "Undo rewound unrelated metadata.");
            history.Redo(f.Assets, _ => f.Resource);
            Assert(f.Capture().Tags.SequenceEqual(["forest"]) && history.CanUndo && !history.CanRedo, "Redo failed.");
        }));
        check("Tags.HistorySurvivesLogicalRenameAndMove", () => WithFixture(f =>
        {
            ResourceLibraryTagHistory history = new();
            ResourceLibraryTagSnapshot before = f.Capture(), after = ResourceLibraryTags.Apply(before, ["forest"]); history.Record(before, after);
            f.Change(root => root["resourceName"] = "Renamed Hero");
            string next = Path.Combine(f.Assets, "Moved.md"); File.Move(f.Resource, next); File.Move(f.Meta, next + ".meta");
            history.Undo(f.Assets, id => id == before.AssetId ? next : null);
            Assert(ResourceLibraryTags.Capture(f.Assets, next).Tags.Count == 0
                && JsonNode.Parse(File.ReadAllText(next + ".meta"))!["resourceName"]!.ToString() == "Renamed Hero", "Undo depended on a filename/name.");
        }));
        check("Tags.HistoryRejectsExternalTagConflicts", () => WithFixture(f =>
        {
            ResourceLibraryTagHistory history = new();
            ResourceLibraryTagSnapshot before = f.Capture(), after = ResourceLibraryTags.Apply(before, ["forest"]); history.Record(before, after);
            ResourceLibraryTags.Apply(f.Capture(), ["ui"]);
            Throws<InvalidOperationException>(() => history.Undo(f.Assets, _ => f.Resource));
            Assert(history.CanUndo && f.Capture().Tags.SequenceEqual(["ui"]), "Conflicting undo changed tags or lost its history slot.");
        }));
        check("Tags.HistoryRejectsDeletedResource", () => WithFixture(f =>
        {
            ResourceLibraryTagHistory history = new();
            ResourceLibraryTagSnapshot before = f.Capture(), after = ResourceLibraryTags.Apply(before, ["forest"]); history.Record(before, after);
            Throws<InvalidOperationException>(() => history.Undo(f.Assets, _ => null));
            Assert(history.CanUndo, "Failed undo consumed history.");
        }));
        check("Tags.HistoryNewEditAfterUndoClearsRedoButNoOpDoesNot", () => WithFixture(f =>
        {
            ResourceLibraryTagHistory history = new();
            ResourceLibraryTagSnapshot before = f.Capture(), after = ResourceLibraryTags.Apply(before, ["forest"]); history.Record(before, after);
            history.Undo(f.Assets, _ => f.Resource); before = f.Capture(); history.Record(before, before);
            Assert(history.CanRedo, "A no-op discarded redo.");
            after = ResourceLibraryTags.Apply(before, ["ui"]); history.Record(before, after);
            Assert(!history.CanRedo && history.CanUndo, "New branch retained invalid redo.");
        }));
        check("Tags.HistoryIsBounded", () => WithFixture(f =>
        {
            ResourceLibraryTagHistory history = new();
            for (int i = 0; i < ResourceLibraryTagHistory.Capacity + 4; i++)
            { ResourceLibraryTagSnapshot before = f.Capture(); history.Record(before, ResourceLibraryTags.Apply(before, ["tag " + i])); }
            int count = 0;
            while (history.CanUndo) { history.Undo(f.Assets, _ => f.Resource); count++; }
            Assert(count == ResourceLibraryTagHistory.Capacity, "History exceeded its fixed capacity.");
        }));
        check("TagQuery.PlainWordsMatchNameOrTag", () =>
        {
            Assert(ResourceLibraryQuery.Parse("sky").Matches("Morning Sky", []), "Name search regressed.");
            Assert(ResourceLibraryQuery.Parse("forest").Matches("Tree", ["forest"]), "Plain tag search failed.");
            Assert(!ResourceLibraryQuery.Parse(".image.json").Matches("Tree", []), "Private filename became a public search reference.");
        });
        check("TagQuery.ExplicitTagsAreExactNotFuzzy", () =>
        {
            Assert(ResourceLibraryQuery.Parse("tag:FOREST").Matches("Tree", ["forest"]), "Exact tags are not case-insensitive.");
            Assert(ResourceLibraryQuery.Parse("tag:Cafe\u0301").Matches("Tree", ["Café"]), "Exact tag query did not normalize Unicode.");
            Assert(!ResourceLibraryQuery.Parse("tag:for").Matches("Tree", ["forest"]), "Tag prefix bypassed exact matching.");
            Assert(ResourceLibraryQuery.Parse("tag:frst").Score("Tree", ["forest"], fuzzy: true) < 0, "Fuzzy picker tags are unsafe.");
        });
        check("TagQuery.MultipleAndNegativeTagsIntersect", () =>
        {
            ResourceLibraryQuery query = ResourceLibraryQuery.Parse("tag:forest tag:night -tag:ui Tree");
            Assert(query.Matches("Oak Tree", ["forest", "night"]) && !query.Matches("Oak Tree", ["forest", "night", "ui"])
                && !query.Matches("Oak Tree", ["forest"]), "Tag predicates were ORed or ignored.");
        });
        check("TagQuery.QuotedTagAndNamePhrases", () =>
        {
            ResourceLibraryQuery query = ResourceLibraryQuery.Parse("tag:\"night sky\" \"Cloud Hills\"");
            Assert(query.Matches("Cloud Hills", ["night sky"]) && !query.Matches("Cloudy Hills", ["night sky"]), "Quoted phrases lost their boundary.");
        });
        check("TagQuery.InvalidOrIncompleteQueriesDoNotExposeAll", () =>
        {
            foreach (string query in new[] { "tag:", "-tag:", "tag:\"night", "\"\"", new string('a', 513) })
            { ResourceLibraryQuery parsed = ResourceLibraryQuery.Parse(query); Assert(parsed.Error is not null && !parsed.Matches("Any", []), "Invalid query became an unfiltered result."); }
        });
        check("TagQuery.EmptyAndNegativeOnlyQueries", () =>
        {
            Assert(ResourceLibraryQuery.Parse("").Matches("Tree", []), "Empty browser query did not show resources.");
            Assert(ResourceLibraryQuery.Parse("-tag:ui").Matches("Tree", []) && !ResourceLibraryQuery.Parse("-tag:ui").Matches("HUD", ["ui"]), "Negative-only tag filtering failed.");
        });
        check("TagQuery.FuzzyNameSupportRemainsPickerOnly", () =>
        {
            ResourceLibraryQuery query = ResourceLibraryQuery.Parse("MrgSky");
            Assert(query.Score("Morning Sky", [], fuzzy: true) >= 0 && !query.Matches("Morning Sky", []), "Fuzzy matching leaked outside picker or regressed.");
        });
        check("TagQuery.ContentTermExcludesTagOperators", () => Assert(ResourceLibraryQuery.Parse("tag:forest SoundPlay -tag:ui").ContentTerm == "SoundPlay", "Tag syntax leaked into source-content search."));
        check("TagProjection.ExactAndUntaggedFilters", () => WithProjection((root, prefs) =>
        {
            Assert(Names(ResourceBrowserProjection.Build(root, new(RequiredTag: "forest"), prefs)).SequenceEqual(["Tree"]), "Tag dropdown ignored exact selection.");
            Assert(Names(ResourceBrowserProjection.Build(root, new(UntaggedOnly: true), prefs)).SequenceEqual(["Wind"]), "Untagged filter included tagged resources.");
        }));
        check("TagProjection.TypeNameAndFavouriteFiltersStillIntersect", () => WithProjection((root, prefs) =>
        {
            ResourceItem tree = root.Children[0]; prefs.SetFavourite(tree.AssetId, tree.Name, true);
            Assert(ResourceBrowserProjection.Build(root, new(ResourceBrowserScope.Favourites, ResourceKind.Audio, RequiredTag: "forest"), prefs).VisibleResources == 0,
                "Tag or favourite bypassed the kind filter.");
            Assert(Names(ResourceBrowserProjection.Build(root, new(NameFilter: "tag:forest -tag:ui"), prefs)).SequenceEqual(["Tree"]), "Tag query projection failed.");
        }));
        check("TagProjection.NoDiskAccessAndSnapshotTagsAreNotMutated", () => WithProjection((root, prefs) =>
        {
            string[] tags = root.Children[0].LibraryTags.ToArray();
            ResourceBrowserProjection.Build(root, new(NameFilter: "tag:forest"), prefs);
            Assert(!Directory.Exists(root.FullPath) && tags.SequenceEqual(root.Children[0].LibraryTags), "In-memory filtering touched disk or mutated tags.");
        }));
    }

    private static void WithProjection(Action<ResourceItem, ResourcePickerPreferences> test)
    {
        string project = Path.Combine(Path.GetTempPath(), "Genesis-H20-projection-" + Guid.NewGuid().ToString("N"));
        ResourceItem root = new() { Name = "Assets", FullPath = Path.Combine(project, "Assets"), RelativePath = "", Kind = ResourceKind.Folder, IsFolder = true };
        root.Children.Add(Item("Tree", ResourceKind.Image, ["forest", "night"]));
        root.Children.Add(Item("HUD", ResourceKind.Image, ["ui"]));
        root.Children.Add(Item("Wind", ResourceKind.Audio, []));
        try { test(root, new(project)); }
        finally { if (Directory.Exists(project)) Directory.Delete(project, true); }
        ResourceItem Item(string name, ResourceKind kind, IReadOnlyList<string> tags) => new()
        { Name = name, FullPath = Path.Combine(root.FullPath, name), RelativePath = name, Kind = kind, IsFolder = false, AssetId = Guid.NewGuid(), LibraryTags = tags };
    }
    private static IEnumerable<string> Names(ResourceBrowserView view) => Flatten(view.Root).Where(row => !row.Resource.IsFolder).Select(row => row.Resource.Name);
    private static IEnumerable<ResourceBrowserRow> Flatten(ResourceBrowserRow row)
    { yield return row; foreach (ResourceBrowserRow child in row.Children) foreach (ResourceBrowserRow nested in Flatten(child)) yield return nested; }
    private static void WithFixture(Action<Fixture> test) { using Fixture fixture = new(); test(fixture); }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Genesis-H20-tags-" + Guid.NewGuid().ToString("N"));
        internal string Assets => Path.Combine(Root, "Assets");
        internal string Resource => Path.Combine(Assets, "Hero.md");
        internal string Meta => Resource + ".meta";
        internal Fixture()
        {
            Directory.CreateDirectory(Assets); File.WriteAllText(Resource, "Source must remain unchanged.");
            File.WriteAllText(Meta, new JsonObject
            {
                ["schemaVersion"] = 1, ["guid"] = Guid.NewGuid().ToString("N"), ["kind"] = "Note", ["resourceName"] = "Hero",
                ["displayName"] = "Hero", ["custom"] = new JsonObject { ["keep"] = true },
            }.ToJsonString());
        }
        internal ResourceLibraryTagSnapshot Capture() => ResourceLibraryTags.Capture(Assets, Resource);
        internal void Change(Action<JsonObject> edit)
        {
            JsonObject metadata = JsonNode.Parse(File.ReadAllText(Meta))!.AsObject(); edit(metadata);
            File.WriteAllText(Meta, metadata.ToJsonString()); ResourceLibraryTags.Invalidate(Resource);
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
