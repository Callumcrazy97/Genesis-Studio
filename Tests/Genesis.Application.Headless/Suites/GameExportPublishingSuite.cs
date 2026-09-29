using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Genesis.Application.Core.Projects;
using Microsoft.Win32.SafeHandles;

namespace Genesis.Application.Headless.Suites;

internal static class GameExportPublishingSuite
{
    private static readonly MethodInfo PublishMethod = typeof(GameExportService).GetMethod("Publish", BindingFlags.Static | BindingFlags.NonPublic)!;

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Export.Publication.FolderWaitsForStagingHandle", () =>
        {
            var fixture = Fixture("Staging", false);
            using SafeFileHandle handle = LockDirectory(fixture.Staging);
            SyncProgress progress = new(handle.Dispose);
            Publish(fixture, GameExportFormat.Folder, true, progress);
            Check(progress.Count == 1 && File.ReadAllText(Path.Combine(fixture.Output, "game.txt")) == "new game"
                && !Directory.Exists(fixture.Staging), "A real Windows staging handle did not recover on retry.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Export.Publication.FolderWaitsForPreviousHandle", () =>
        {
            var fixture = Fixture("Previous", true);
            using SafeFileHandle handle = LockDirectory(fixture.Output);
            SyncProgress progress = new(handle.Dispose);
            Publish(fixture, GameExportFormat.Folder, true, progress);
            Check(progress.Count == 1 && File.ReadAllText(Path.Combine(fixture.Output, "game.txt")) == "new game",
                "A brief lock on the existing release prevented replacement.");
            NoLeftovers(fixture);
        });
        HeadlessHarness.RunCase(ctx.Report, "Export.Publication.ArchiveWaitsForPreviousHandle", () =>
        {
            var fixture = ArchiveFixture("Archive");
            using FileStream handle = File.Open(fixture.Output, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            SyncProgress progress = new(handle.Dispose);
            Publish(fixture, GameExportFormat.Zip, true, progress);
            Check(progress.Count == 1 && ReadArchive(fixture.Output) == "new game" && !Directory.Exists(fixture.Staging),
                "A brief archive handle did not recover or published incomplete content.");
            NoLeftovers(fixture);
        });
        HeadlessHarness.RunCase(ctx.Report, "Export.Publication.FolderCancellationRestoresPreviousRelease", () =>
        {
            var fixture = Fixture("CancelledFolder", true);
            using SafeFileHandle handle = LockDirectory(fixture.Staging);
            using CancellationTokenSource cancellation = new();
            SyncProgress progress = new(cancellation.Cancel);
            Expect<OperationCanceledException>(() => Publish(fixture, GameExportFormat.Folder, true, progress, cancellation.Token));
            Check(progress.Count == 1 && File.ReadAllText(Path.Combine(fixture.Output, "game.txt")) == "previous game"
                && File.ReadAllText(Path.Combine(fixture.Staging, "game.txt")) == "new game",
                "Cancelling a locked promotion lost the previous release or the staged content.");
            NoLeftovers(fixture);
        });
        HeadlessHarness.RunCase(ctx.Report, "Export.Publication.ArchiveCancellationPreservesPreviousRelease", () =>
        {
            var fixture = ArchiveFixture("CancelledArchive");
            using FileStream handle = File.Open(fixture.Output, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using CancellationTokenSource cancellation = new();
            SyncProgress progress = new(cancellation.Cancel);
            Expect<OperationCanceledException>(() => Publish(fixture, GameExportFormat.Zip, true, progress, cancellation.Token));
            Check(progress.Count == 1 && ReadArchive(fixture.Output) == "previous game", "Cancellation replaced the existing archive.");
            NoLeftovers(fixture);
        });
        HeadlessHarness.RunCase(ctx.Report, "Export.Publication.LockedFolderFailureIsBoundedAndRestoresRelease", () =>
        {
            var fixture = Fixture("PermanentLock", true);
            using SafeFileHandle handle = LockDirectory(fixture.Staging);
            SyncProgress progress = new(() => { });
            Stopwatch elapsed = Stopwatch.StartNew();
            IOException error = Expect<IOException>(() => Publish(fixture, GameExportFormat.Folder, true, progress));
            Check(progress.Count == 1 && elapsed.Elapsed.TotalSeconds < 5 && error.Message.Contains(fixture.Output, StringComparison.Ordinal)
                && File.ReadAllText(Path.Combine(fixture.Output, "game.txt")) == "previous game",
                "A permanent lock was hidden, retried indefinitely, or lost the previous release.");
            NoLeftovers(fixture);
        });
        HeadlessHarness.RunCase(ctx.Report, "Export.Publication.NoReplacePreservesFolderAndArchive", () =>
        {
            var folder = Fixture("NoReplaceFolder", true);
            var archive = ArchiveFixture("NoReplaceArchive");
            SyncProgress progress = new(() => { });
            Expect<IOException>(() => Publish(folder, GameExportFormat.Folder, false, progress));
            Expect<IOException>(() => Publish(archive, GameExportFormat.Zip, false, progress));
            Check(progress.Count == 0 && File.ReadAllText(Path.Combine(folder.Output, "game.txt")) == "previous game"
                && ReadArchive(archive.Output) == "previous game", "Do not replace changed an existing release or waited for a non-sharing error.");
            NoLeftovers(folder); NoLeftovers(archive);
        });

        (string Root, string Staging, string Output) Fixture(string name, bool previous)
        {
            string root = Path.Combine(ctx.Workspace, "ExportPublication" + name);
            string staging = Path.Combine(root, "stage"), output = Path.Combine(root, "release");
            Directory.CreateDirectory(staging); File.WriteAllText(Path.Combine(staging, "game.txt"), "new game");
            if (previous) { Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "game.txt"), "previous game"); }
            return (root, staging, output);
        }
        (string Root, string Staging, string Output) ArchiveFixture(string name)
        {
            var fixture = Fixture(name, true);
            string archive = fixture.Output + ".zip";
            ZipFile.CreateFromDirectory(fixture.Output, archive);
            return (fixture.Root, fixture.Staging, archive);
        }
    }

    private static void Publish((string Root, string Staging, string Output) fixture, GameExportFormat format,
        bool replace, IProgress<string> progress, CancellationToken cancellation = default)
    {
        // Exercise the actual filesystem publication without recooking a Player for each lock scenario.
        try { PublishMethod.Invoke(null, [fixture.Staging, fixture.Output, format, replace, progress, cancellation]); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
        }
    }

    private static SafeFileHandle LockDirectory(string path)
    {
        SafeFileHandle handle = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        Check(!handle.IsInvalid, "Could not create the real Windows directory-lock fixture: " + Marshal.GetLastWin32Error());
        return handle;
    }

    private static string ReadArchive(string path)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        using StreamReader content = new(archive.GetEntry("game.txt")!.Open());
        return content.ReadToEnd();
    }

    private static void NoLeftovers((string Root, string Staging, string Output) fixture) =>
        Check(!Directory.EnumerateFileSystemEntries(fixture.Root).Any(path => Path.GetFileName(path).Contains(".previous-", StringComparison.Ordinal)
            || Path.GetFileName(path).Contains(".new-", StringComparison.Ordinal)), "Publishing left an unused backup or partial archive.");

    private static T Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
    }

    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);

    private sealed class SyncProgress(Action release) : IProgress<string>
    {
        public int Count { get; private set; }
        public void Report(string value) { Count++; release(); }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
}
