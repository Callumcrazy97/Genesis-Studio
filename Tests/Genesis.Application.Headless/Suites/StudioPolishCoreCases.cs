using System.Text.Json;
using Genesis.Application.Core.Resources;

namespace Genesis.Application.Headless.Suites;

/// <summary>Runs the exact preference implementation in both the native and portable harness.</summary>
internal static class StudioPolishCoreCases
{
    public static void Run(Action<string, Action> check)
    {
        check("PickerPrefs.EmptyReadDoesNotCreateProjectData", () => WithRoot(root =>
        {
            ResourcePickerPreferences prefs = new(root);
            Assert(!prefs.IsFavourite(Guid.NewGuid(), "Sky") && !prefs.IsRecent(Guid.Empty, "Sky")
                && !Directory.Exists(Path.Combine(root, ".genesis")), "Merely opening a picker changed a project.");
        }));
        check("PickerPrefs.GuidFavouritesSurviveRenameAndMove", () => WithRoot(root =>
        {
            Guid id = Guid.NewGuid(); ResourcePickerPreferences prefs = new(root);
            Assert(prefs.SetFavourite(id, "Morning Sky", true), prefs.LastError);
            ResourcePickerPreferences reopened = new(root);
            Assert(reopened.IsFavourite(id, "Evening Sky") && !reopened.IsFavourite(Guid.NewGuid(), "Morning Sky"),
                "GUID identity was replaced by a name or filename.");
            Assert(reopened.SetFavourite(id, "Evening Sky", false) && !new ResourcePickerPreferences(root).IsFavourite(id, "Morning Sky"),
                "Removing a renamed favourite failed.");
        }));
        check("PickerPrefs.NameFallbackIsCaseInsensitive", () => WithRoot(root =>
        {
            ResourcePickerPreferences prefs = new(root);
            Assert(prefs.SetFavourite(Guid.Empty, "Sky", true) && prefs.IsFavourite(Guid.Empty, "SKY"), "Name fallback mismatch.");
            Guid id = Guid.NewGuid(); Assert(prefs.SetFavourite(id, "Sky", true), prefs.LastError);
            using JsonDocument file = JsonDocument.Parse(File.ReadAllText(FilePath(root)));
            Assert(file.RootElement.GetProperty("Favourites").GetArrayLength() == 1 && prefs.IsFavourite(id, "New Sky"),
                "Promoting a fallback to a GUID created a duplicate.");
        }));
        check("PickerPrefs.TwoOpenPickersMergeWrites", () => WithRoot(root =>
        {
            ResourcePickerPreferences first = new(root), second = new(root); Guid a = Guid.NewGuid(), b = Guid.NewGuid();
            Assert(first.SetFavourite(a, "A", true) && second.SetFavourite(b, "B", true), "Preference save failed.");
            ResourcePickerPreferences saved = new(root);
            Assert(saved.IsFavourite(a, "A") && saved.IsFavourite(b, "B"), "A stale picker overwrote another picker.");
        }));
        check("PickerPrefs.RecentsBoundedUniqueAndNewestFirst", () => WithRoot(root =>
        {
            ResourcePickerPreferences prefs = new(root); Guid[] ids = Enumerable.Range(0, 30).Select(_ => Guid.NewGuid()).ToArray();
            for (int i = 0; i < ids.Length; i++) Assert(prefs.Remember(ids[i], "Image " + i), prefs.LastError);
            Assert(prefs.Remember(ids[20], "Image 20") && prefs.RecentRank(ids[20], "Renamed") == 0
                && prefs.RecentRank(ids[29], "Image 29") == 1 && !prefs.IsRecent(ids[0], "Image 0"), "Recent ordering/bounds failed.");
            using JsonDocument file = JsonDocument.Parse(File.ReadAllText(FilePath(root)));
            Assert(file.RootElement.GetProperty("Recent").GetArrayLength() == ResourcePickerPreferences.RecentLimit,
                "Repeated selection duplicated history.");
        }));
        check("PickerPrefs.ClearRecentPreservesFavourites", () => WithRoot(root =>
        {
            Guid id = Guid.NewGuid(); ResourcePickerPreferences prefs = new(root);
            Assert(prefs.SetFavourite(id, "Sky", true) && prefs.Remember(id, "Sky") && prefs.ClearRecent(), prefs.LastError);
            ResourcePickerPreferences saved = new(root);
            Assert(saved.IsFavourite(id, "Sky") && !saved.IsRecent(id, "Sky"), "Clear recent removed favourites.");
        }));
        check("PickerPrefs.ProjectsAreIsolated", () => WithRoot(root =>
        {
            Guid id = Guid.NewGuid(); ResourcePickerPreferences first = new(Path.Combine(root, "One")), second = new(Path.Combine(root, "Two"));
            Assert(first.SetFavourite(id, "Sky", true) && !second.IsFavourite(id, "Sky"), "Preferences leaked into another project.");
        }));
        check("PickerPrefs.CorruptFileIsNotOverwritten", () => WithRoot(root =>
        {
            const string damaged = "{ not valid"; Write(root, damaged); ResourcePickerPreferences prefs = new(root);
            Assert(!prefs.Remember(Guid.NewGuid(), "Sky") && prefs.LastError.Length > 0
                && File.ReadAllText(FilePath(root)) == damaged, "A damaged preference file was silently replaced.");
        }));
        check("PickerPrefs.NewerVersionIsNotOverwritten", () => WithRoot(root =>
        {
            const string future = "{\"Version\":99,\"Favourites\":[],\"Recent\":[]}"; Write(root, future);
            ResourcePickerPreferences prefs = new(root);
            Assert(!prefs.SetFavourite(Guid.NewGuid(), "Sky", true) && File.ReadAllText(FilePath(root)) == future,
                "Unsupported future data was overwritten.");
        }));
        check("PickerPrefs.OversizedFileIsNotLoadedOrReplaced", () => WithRoot(root =>
        {
            string oversized = new(' ', 256 * 1024 + 1); Write(root, oversized); ResourcePickerPreferences prefs = new(root);
            Assert(prefs.LastError.Length > 0 && !prefs.ClearRecent() && new FileInfo(FilePath(root)).Length == oversized.Length,
                "Oversized preferences were accepted or overwritten.");
        }));
        check("PickerPrefs.WriteFailureKeepsPriorMemoryState", () => WithRoot(root =>
        {
            File.WriteAllText(Path.Combine(root, ".genesis"), "not a directory"); ResourcePickerPreferences prefs = new(root);
            Assert(!prefs.SetFavourite(Guid.Empty, "Sky", true) && !prefs.IsFavourite(Guid.Empty, "Sky")
                && prefs.LastError.Length > 0, "Failed disk write was advertised as a saved favourite.");
        }));
        check("PickerPrefs.AtomicWritesLeaveNoPendingFiles", () => WithRoot(root =>
        {
            ResourcePickerPreferences prefs = new(root); Assert(prefs.Remember(Guid.Empty, "Sky"), prefs.LastError);
            Assert(Directory.GetFiles(Path.Combine(root, ".genesis", "User")).Select(Path.GetFileName).SequenceEqual(["ResourcePicker.json"]),
                "An atomic write left temporary data behind.");
        }));
        check("PickerPrefs.InvalidKeysAndDuplicateEntriesAreFiltered", () => WithRoot(root =>
        {
            Write(root, "{\"Version\":1,\"Favourites\":[\"broken\",\"name:Sky\",\"name:SKY\"],\"Recent\":[\"id:nope\",\"name:Sky\"]}");
            ResourcePickerPreferences prefs = new(root);
            Assert(prefs.IsFavourite(Guid.Empty, "Sky") && prefs.RecentRank(Guid.Empty, "Sky") == 0 && prefs.Remember(Guid.Empty, "Sky"),
                "Valid entries in a partially damaged list were lost.");
            using JsonDocument file = JsonDocument.Parse(File.ReadAllText(FilePath(root)));
            Assert(file.RootElement.GetProperty("Favourites").GetArrayLength() == 1, "Duplicate or invalid entries survived normalization.");
        }));
        check("PickerPrefs.FavouriteLimitIsSafeAndRecoverable", () => WithRoot(root =>
        {
            string[] favourites = Enumerable.Range(0, ResourcePickerPreferences.FavouriteLimit).Select(i => "name:Image " + i).ToArray();
            Write(root, JsonSerializer.Serialize(new { Version = 1, Favourites = favourites, Recent = Array.Empty<string>() }));
            ResourcePickerPreferences prefs = new(root); string before = File.ReadAllText(FilePath(root));
            Assert(!prefs.SetFavourite(Guid.Empty, "Extra", true) && File.ReadAllText(FilePath(root)) == before, "Limit failure changed saved data.");
            Assert(prefs.SetFavourite(Guid.Empty, "Image 0", false) && prefs.SetFavourite(Guid.Empty, "Extra", true),
                "Removing a favourite did not release its slot.");
        }));
    }

    private static void WithRoot(Action<string> run)
    {
        string root = Path.Combine(Path.GetTempPath(), "Genesis-H13-Prefs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { run(root); } finally { Directory.Delete(root, true); }
    }
    private static string FilePath(string root) => Path.Combine(root, ".genesis", "User", "ResourcePicker.json");
    private static void Write(string root, string text)
    { Directory.CreateDirectory(Path.GetDirectoryName(FilePath(root))!); File.WriteAllText(FilePath(root), text); }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
