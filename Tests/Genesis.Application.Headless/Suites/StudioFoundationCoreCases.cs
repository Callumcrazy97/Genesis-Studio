using Genesis.Application.Core.Commands;
using Genesis.Application.Core.Editing;

namespace Genesis.Application.Headless.Suites;

/// <summary>These cases compile unchanged into the Windows harness and the portable net10 runner.</summary>
internal static class StudioFoundationCoreCases
{
    public static void Run(Action<string, Action> check)
    {
        check("Commands.UniqueCaseInsensitiveIds", () =>
        {
            StudioCommandCatalog<int> catalog = new(); catalog.Register(Command("save"));
            Throws<ArgumentException>(() => catalog.Register(Command("SAVE")));
            Assert(catalog.Commands.Count == 1 && catalog.Find("sAvE") is not null, "Command IDs are not unique.");
        });
        check("Commands.ShortcutCollisionRegistrationIsAtomic", () =>
        {
            StudioCommandCatalog<int> catalog = new(); catalog.Register(Command("a", keys: [new(1, "Ctrl+A")]));
            Throws<ArgumentException>(() => catalog.Register(Command("b", keys: [new(2, "Ctrl+B"), new(1, "Ctrl+A")])));
            Assert(catalog.Find("b") is null && catalog.FindShortcut(2) is null, "Failed registration leaked an ID or shortcut.");
        });
        check("Commands.AliasShortcutsUseOneAction", () =>
        {
            int calls = 0; StudioCommandCatalog<int> catalog = new();
            catalog.Register(Command("redo", _ => calls++, keys: [new(1, "Ctrl+Y"), new(2, "Ctrl+Shift+Z")]));
            Assert(catalog.TryExecute(catalog.FindShortcut(1)!, 0).Succeeded
                && catalog.TryExecute(catalog.FindShortcut(2)!, 0).Succeeded && calls == 2, "Aliases diverged.");
        });
        check("Commands.DuplicateAndZeroShortcutRejected", () =>
        {
            StudioCommandCatalog<int> catalog = new();
            Throws<ArgumentException>(() => catalog.Register(Command("zero", keys: [new(0, "None")])));
            Throws<ArgumentException>(() => catalog.Register(Command("duplicate", keys: [new(1, "A"), new(1, "A")])));
            Assert(catalog.Commands.Count == 0, "Invalid shortcut registration changed the catalog.");
        });
        check("Commands.DisabledVisibleButNotExecutable", () =>
        {
            int calls = 0; StudioCommandCatalog<int> catalog = new();
            catalog.Register(Command("delete", _ => calls++, _ => CommandAvailability.Unavailable("Select the active layer.")));
            CommandSearchResult<int> result = catalog.Search("delete", 0).Single();
            Assert(!result.State.Enabled && result.State.Reason.Contains("layer") && calls == 0, "Search hid or ran a disabled action.");
            Assert(catalog.TryExecute("delete", 0).Status == CommandExecutionStatus.Unavailable && calls == 0, "Disabled action ran.");
        });
        check("Commands.AvailabilityFailureIsVisibleAndSafe", () =>
        {
            StudioCommandCatalog<int> catalog = new(); int calls = 0;
            catalog.Register(Command("stale", _ => calls++, _ => throw new InvalidOperationException("Closed editor")));
            CommandSearchResult<int> result = catalog.Search("stale", 0).Single();
            Assert(!result.State.Enabled && result.State.Reason.Contains("Closed editor")
                && !catalog.TryExecute("stale", 0).Succeeded && calls == 0, "Availability failure escaped or enabled execution.");
        });
        check("Commands.ExecutionRechecksContext", () =>
        {
            bool allowed = true; int calls = 0; StudioCommandCatalog<int> catalog = new();
            catalog.Register(Command("delete", _ => calls++, _ => allowed ? CommandAvailability.Available : CommandAvailability.Unavailable("Layer changed.")));
            Assert(catalog.Search("delete", 0).Single().State.Enabled, "Setup failed."); allowed = false;
            Assert(!catalog.TryExecute("delete", 0).Succeeded && calls == 0, "A stale palette selection bypassed validation.");
        });
        check("Commands.ExceptionDoesNotPoisonLaterExecution", () =>
        {
            bool fail = true; StudioCommandCatalog<int> catalog = new();
            catalog.Register(Command("save", _ => { if (fail) throw new IOException("Disk full"); }));
            Assert(catalog.TryExecute("save", 0).Status == CommandExecutionStatus.Failed && catalog.RecentIds.Count == 0,
                "Failed action was marked successful or added to recents.");
            fail = false; Assert(catalog.TryExecute("save", 0).Succeeded, "Execution lock survived an exception.");
        });
        check("Commands.UnknownIdIsSafe", () =>
        {
            StudioCommandCatalog<int> catalog = new();
            Assert(catalog.TryExecute("gone", 0).Status == CommandExecutionStatus.Unknown
                && !catalog.GetAvailability("gone", 0).Enabled && catalog.FindShortcut(9) is null, "Unknown command was unsafe.");
        });
        check("Commands.SelfReentryIsBlocked", () =>
        {
            StudioCommandCatalog<int> catalog = new(); CommandExecutionResult? nested = null;
            catalog.Register(Command("save", _ => nested = catalog.TryExecute("save", 0)));
            Assert(catalog.TryExecute("save", 0).Succeeded && nested?.Status == CommandExecutionStatus.Unavailable, "Reentrant command was not guarded.");
        });
        check("Commands.PaletteMayInvokeAnotherCommand", () =>
        {
            StudioCommandCatalog<int> catalog = new(); int calls = 0;
            catalog.Register(Command("save", _ => calls++));
            catalog.Register(Command("palette", _ => Assert(catalog.TryExecute("save", 0).Succeeded, "Nested selected command was blocked.")));
            Assert(catalog.TryExecute("palette", 0).Succeeded && calls == 1, "Palette command could not dispatch its selection.");
        });
        check("Commands.SearchUsesAllTokensAndShortcuts", () =>
        {
            StudioCommandCatalog<int> catalog = new();
            catalog.Register(new("save", "Save Project", "File", "Save all windows", _ => { }, shortcuts: [new(1, "Ctrl+Shift+S")]));
            catalog.Register(new("run", "Run", "Project", "Play", _ => { }));
            Assert(catalog.Search("PROJECT save", 0).Single().Command.Id == "save"
                && catalog.Search("Ctrl+Shift+S", 0).Single().Command.Id == "save"
                && catalog.Search("save missing", 0).Count == 0, "Multi-word/shortcut matching failed.");
        });
        check("Commands.SearchOrderIsStableAndExactFirst", () =>
        {
            StudioCommandCatalog<int> first = new(), second = new();
            foreach (string id in new[] { "Save Project", "Save", "Save As" }) first.Register(Command(id));
            foreach (string id in new[] { "Save As", "Save", "Save Project" }) second.Register(Command(id));
            string[] ids = first.Search("save", 0).Select(hit => hit.Command.Id).ToArray();
            Assert(ids[0] == "Save" && ids.SequenceEqual(second.Search("save", 0).Select(hit => hit.Command.Id)), "Search depended on registration order.");
            Assert(first.Search("", 0, 1).Count == 1 && first.Search("", 0, 0).Count == 0, "Result limits failed.");
        });
        check("Commands.RecentHistoryIsBoundedAndUnique", () =>
        {
            StudioCommandCatalog<int> catalog = new();
            for (int index = 0; index < 20; index++) { string id = "command" + index; catalog.Register(Command(id)); catalog.TryExecute(id, 0); }
            catalog.TryExecute("command19", 0);
            Assert(catalog.RecentIds.Count == 12 && catalog.RecentIds.Distinct().Count() == 12
                && catalog.Search("", 0).First().Command.Id == "command19", "Recent history is unbounded or duplicates entries.");
        });
        check("Save.CleanAndClosedDocumentsAreSkipped", () =>
        {
            Draft clean = new("Clean") { Dirty = false }, closed = new("Closed") { Alive = false };
            DocumentSaveResult result = new DocumentSaveCoordinator().Save([clean.Target(), closed.Target()]);
            Assert(result.Success && result.SavedNames.Count == 0 && clean.Saves == 0 && closed.Saves == 0, "Clean/closed documents were written.");
        });
        check("Save.SharedViewerEditorSessionSavedOnce", () =>
        {
            Draft shared = new("Portrait"); DocumentSaveTarget first = shared.Target();
            DocumentSaveTarget second = first with { Save = () => throw new InvalidOperationException("Shared session was saved twice.") };
            DocumentSaveResult result = new DocumentSaveCoordinator().Save([first, second]);
            Assert(result.Success && shared.Saves == 1 && result.SavedNames.SequenceEqual(["Portrait"]), "Shared session save duplicated or failed.");
        });
        check("Save.IndependentWritersConflictBeforeAnyWrite", () =>
        {
            Draft other = new("Other"), first = new("Armour"), second = new("armour");
            DocumentSaveResult result = new DocumentSaveCoordinator().Save([other.Target(), first.Target(), second.Target()]);
            Assert(!result.Success && result.SavedNames.Count == 0 && other.Saves == 0 && first.Dirty && second.Dirty,
                "Conflicting edits were overwritten or another document saved before conflict preflight.");
        });
        check("Save.CleanIndependentViewerDoesNotBlockAuthor", () =>
        {
            Draft author = new("Armour"), viewer = new("Armour") { Dirty = false };
            DocumentSaveResult result = new DocumentSaveCoordinator().Save([viewer.Target(), author.Target()]);
            Assert(result.Success && author.Saves == 1 && viewer.Saves == 0, "A clean independent viewer prevented saving.");
        });
        check("Save.FailureStopsLaterWritesAndReportsEarlierSuccess", () =>
        {
            Draft first = new("First"), broken = new("Broken"), later = new("Later");
            DocumentSaveTarget failure = broken.Target() with { Save = () => throw new IOException("Disk full") };
            DocumentSaveResult result = new DocumentSaveCoordinator().Save([first.Target(), failure, later.Target()]);
            Assert(!result.Success && result.FailedName == "Broken" && result.SavedNames.SequenceEqual(["First"])
                && !first.Dirty && broken.Dirty && later.Dirty && later.Saves == 0, "Save failure lost edits or wrote later drafts.");
        });
        check("Save.EditorThatRemainsDirtyIsAFailure", () =>
        {
            Draft draft = new("Unsaved");
            DocumentSaveResult result = new DocumentSaveCoordinator().Save([draft.Target() with { Save = () => { } }]);
            Assert(!result.Success && draft.Dirty, "A no-op save was treated as a successful save boundary.");
        });
        check("Save.NewEditsDuringSaveBlockContinuation", () =>
        {
            Draft first = new("First"), later = new("Later") { Dirty = false };
            DocumentSaveResult result = new DocumentSaveCoordinator().Save([
                first.Target() with { Save = () => { first.Dirty = false; later.Dirty = true; } }, later.Target()]);
            Assert(!result.Success && result.FailedName == "Later" && later.Dirty, "A newly dirty document was silently omitted.");
        });
        check("Save.DocumentClosingDuringSaveStopsPlan", () =>
        {
            Draft first = new("First"), later = new("Later");
            DocumentSaveResult result = new DocumentSaveCoordinator().Save([
                first.Target() with { Save = () => { first.Dirty = false; later.Alive = false; } }, later.Target()]);
            Assert(!result.Success && result.FailedName == "Later" && later.Saves == 0, "A closed writer was invoked.");
        });
        check("Save.ReentrantCallAndResetAfterFailure", () =>
        {
            DocumentSaveCoordinator coordinator = new(); Draft draft = new("Note"); DocumentSaveResult? nested = null;
            DocumentSaveResult result = coordinator.Save([draft.Target() with
            {
                Save = () => { nested = coordinator.Save([]); draft.Dirty = false; },
            }]);
            Assert(result.Success && nested is { Success: false } && !coordinator.IsSaving, "Reentrant save was not contained.");
            draft.Dirty = true;
            Assert(!coordinator.Save([draft.Target() with { Save = () => throw new IOException() }]).Success
                && coordinator.Save([draft.Target()]).Success, "Coordinator stayed locked after failure.");
        });
        check("Save.PreflightErrorsDoNotWriteAnything", () =>
        {
            Draft first = new("First"), broken = new("Broken");
            DocumentSaveResult result = new DocumentSaveCoordinator().Save([
                first.Target(), broken.Target() with { IsDirty = () => throw new InvalidOperationException("Disposed draft") }]);
            Assert(!result.Success && first.Saves == 0 && first.Dirty, "Preflight failed after writes began.");
        });
    }

    private static StudioCommand<int> Command(string id, Action<int>? action = null,
        Func<int, CommandAvailability>? available = null, CommandShortcut[]? keys = null) =>
        new(id, id, "Test", "Test command", action ?? (_ => { }), available, keys);
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private sealed class Draft(string name)
    {
        public bool Dirty = true;
        public bool Alive = true;
        public int Saves;
        public DocumentSaveTarget Target() => new(name, name, this, () => Dirty,
            () => { Saves++; Dirty = false; }, () => Alive);
    }
}
