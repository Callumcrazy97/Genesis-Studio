using System.Text.Json;

namespace Genesis.Application.Core.Backup;

/// <summary>This PC's backup choices for one project, kept in <c>.genesis/Backup/settings.json</c>.</summary>
public sealed class ProjectBackupSettings
{
    public bool Enabled { get; set; }

    /// <summary>Files you chose to back up although they are large or models (project-relative, '/' separated).</summary>
    public List<string> Included { get; set; } = [];

    public DateTime? LastBackupUtc { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string PathFor(string projectRoot) => System.IO.Path.Combine(projectRoot, ".genesis", "Backup", "settings.json");

    public static ProjectBackupSettings Load(string projectRoot)
    {
        try
        {
            string path = PathFor(projectRoot);
            return File.Exists(path) ? JsonSerializer.Deserialize<ProjectBackupSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(string projectRoot)
    {
        string path = PathFor(projectRoot);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}

/// <summary>
/// The project's <c>.gitignore</c>, kept in three parts so Studio can show it as a list rather
/// than text: what is always skipped, what Studio skipped automatically (files over 10 MB and
/// models, until you choose to back them up) and your own entries.
/// </summary>
public sealed class BackupIgnoreFile
{
    private const string Header = "# Genesis Studio online backup: edit in Preferences › Project › Online backup.";
    private const string AlwaysHeader = "# Always skipped";
    private const string AutomaticHeader = "# Skipped automatically: over 10 MB, or a model. Choose 'Back up anyway' in Genesis to include one.";
    private const string YoursHeader = "# Added by you";

    public static readonly string[] Defaults =
        [".genesis/Cache/", ".genesis/Backup/", ".git/", "Debug/", "Exports/", "Builds/", "Recordings/", "*.tmp", "Thumbs.db", "desktop.ini"];

    public List<string> Always { get; } = [];
    public List<string> Automatic { get; } = [];
    public List<string> Yours { get; } = [];

    public IEnumerable<string> All => Always.Concat(Automatic).Concat(Yours);

    public static string PathFor(string projectRoot) => Path.Combine(projectRoot, ".gitignore");

    public static BackupIgnoreFile Load(string projectRoot)
    {
        BackupIgnoreFile file = new();
        string path = PathFor(projectRoot);
        if (!File.Exists(path))
        {
            file.Always.AddRange(Defaults);
            return file;
        }
        List<string> section = file.Yours;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                if (line.StartsWith(AlwaysHeader, StringComparison.Ordinal)) section = file.Always;
                else if (line.StartsWith("# Skipped automatically", StringComparison.Ordinal)) section = file.Automatic;
                else if (line.StartsWith(YoursHeader, StringComparison.Ordinal)) section = file.Yours;
                continue;
            }
            if (!file.All.Contains(line, StringComparer.Ordinal)) section.Add(line);
        }
        return file;
    }

    public void Save(string projectRoot)
    {
        List<string> lines = [Header, string.Empty, AlwaysHeader, .. Always, string.Empty, AutomaticHeader, .. Automatic, string.Empty, YoursHeader, .. Yours];
        File.WriteAllLines(PathFor(projectRoot), lines);
    }

    public bool Remove(string entry) => Always.Remove(entry) | Automatic.Remove(entry) | Yours.Remove(entry);
}

/// <summary>What happened in a backup or restore, for the status line.</summary>
public sealed record BackupReport(int Uploaded, int Deleted, int Rounds, int SkippedLarge, string Summary);

/// <summary>
/// Backs a project up to <c>github.com/&lt;you&gt;/Genesis-Projects/&lt;Project Name&gt;/</c>: one private
/// repository for all projects, a folder each. Changed files go up in rounds of at most 10 MB, one
/// commit per round with a pause between, so a large first backup never floods the connection and
/// an interrupted one resumes from the last finished round.
/// </summary>
public sealed class ProjectBackup(GitHubAccount account)
{
    public const string RepositoryName = "Genesis-Projects";
    public const long RoundBytes = 10L * 1024 * 1024;
    public const long LargeFileBytes = 10L * 1024 * 1024;
    /// <summary>GitHub refuses files over 100 MB.</summary>
    public const long GitHubFileLimit = 100L * 1024 * 1024;
    public static readonly TimeSpan RoundPause = TimeSpan.FromSeconds(20);

    private static readonly string[] ModelExtensions = [".fbx", ".obj", ".glb", ".gltf", ".blend", ".dae", ".3ds", ".stl", ".ply", ".gmodel"];

    private readonly GitHubAccount _account = account;

    public static string WebAddress(string login, string projectName) =>
        $"https://github.com/{login}/{RepositoryName}/tree/main/{Uri.EscapeDataString(projectName)}";

    /// <summary>Why a file is skipped automatically, or null when it is backed up like any other.</summary>
    public static string? AutomaticReason(string path, long length)
    {
        if (length > LargeFileBytes) return $"Over 10 MB ({length / (1024.0 * 1024.0):0.#} MB)";
        return ModelExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) ? "Model file" : null;
    }

    /// <summary>
    /// The files that will be backed up. Large files and models not yet decided on are added to the
    /// .gitignore's automatic list first, so they are skipped until you choose otherwise.
    /// </summary>
    public static List<(string Relative, string FullPath, long Length)> CollectFiles(string projectRoot, ProjectBackupSettings settings)
    {
        BackupIgnoreFile ignore = BackupIgnoreFile.Load(projectRoot);
        bool changed = !File.Exists(BackupIgnoreFile.PathFor(projectRoot));
        IgnoreRules rules = IgnoreRules.Build(ignore.All, null, null);
        HashSet<string> included = new(settings.Included, StringComparer.OrdinalIgnoreCase);
        List<(string, string, long)> files = [];
        Walk(projectRoot, string.Empty);

        void Walk(string folder, string relativeFolder)
        {
            foreach (FileSystemInfo item in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                string relative = relativeFolder.Length == 0 ? item.Name : relativeFolder + "/" + item.Name;
                if (item is DirectoryInfo directory)
                {
                    if (!rules.IsIgnored(relative, true)) Walk(directory.FullName, relative);
                    continue;
                }
                if (item is not FileInfo file || rules.IsIgnored(relative, false)) continue;
                if (!included.Contains(relative) && AutomaticReason(relative, file.Length) is not null)
                {
                    ignore.Automatic.Add("/" + relative);
                    changed = true;
                    continue;
                }
                files.Add((relative, file.FullName, file.Length));
            }
        }

        if (changed) ignore.Save(projectRoot);
        return files;
    }

    private async Task<(GitHubClient Client, string Owner, string Branch)> ConnectAsync(bool create, CancellationToken ct)
    {
        if (!await _account.EnsureSignedInAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException("Sign in to GitHub first, in Preferences › General.");
        GitHubClient client = _account.Client;
        string owner = _account.Login!;
        GhRepo repo;
        try
        {
            repo = await client.GetRepositoryAsync(owner, RepositoryName, ct).ConfigureAwait(false);
        }
        catch (GitHubApiException error) when (error.IsNotFound && create)
        {
            // Private, and started with a README: GitHub cannot make files in a repository with no commit.
            repo = await client.CreateRepositoryAsync(RepositoryName, "Genesis Studio project backups", isPrivate: true, autoInit: true, ct: ct).ConfigureAwait(false);
            for (int i = 0; i < 20 && await client.TryGetBranchHeadAsync(owner, RepositoryName, repo.DefaultBranch, ct).ConfigureAwait(false) is null; i++)
                await Task.Delay(500, ct).ConfigureAwait(false);
        }
        return (client, owner, repo.DefaultBranch);
    }

    /// <summary>The project's folder in the repository: its tree sha, or null when it has never been backed up.</summary>
    private static async Task<(string Head, string RootTree, List<GhTreeEntry> RootEntries, string? ProjectTree)> ReadRootAsync(
        GitHubClient client, string owner, string branch, string projectName, CancellationToken ct)
    {
        string head = await client.TryGetBranchHeadAsync(owner, RepositoryName, branch, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The backup repository has no commits yet.");
        GhCommit commit = await client.GetCommitAsync(owner, RepositoryName, head, ct).ConfigureAwait(false);
        string rootTree = commit.Tree?.Sha ?? throw new InvalidOperationException("GitHub returned a commit without a tree.");
        GhTree root = await client.GetTreeAsync(owner, RepositoryName, rootTree, recursive: false, ct).ConfigureAwait(false);
        string? projectTree = root.Tree.FirstOrDefault(entry => entry.Type == "tree" && entry.Path == projectName)?.Sha;
        return (head, rootTree, root.Tree, projectTree);
    }

    /// <summary>True when the project has a folder in the backup repository.</summary>
    public async Task<bool> ExistsOnlineAsync(string projectName, CancellationToken ct = default)
    {
        var (client, owner, branch) = await ConnectAsync(create: false, ct).ConfigureAwait(false);
        try
        {
            return (await ReadRootAsync(client, owner, branch, projectName, ct).ConfigureAwait(false)).ProjectTree is not null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public async Task<BackupReport> BackUpAsync(string projectRoot, string projectName, IProgress<string>? progress, CancellationToken ct = default)
    {
        progress?.Report("Connecting to GitHub…");
        var (client, owner, branch) = await ConnectAsync(create: true, ct).ConfigureAwait(false);
        ProjectBackupSettings settings = ProjectBackupSettings.Load(projectRoot);
        progress?.Report("Finding what changed…");
        List<(string Relative, string FullPath, long Length)> files = await Task.Run(() => CollectFiles(projectRoot, settings), ct).ConfigureAwait(false);
        int skippedLarge = files.RemoveAll(file => file.Length > GitHubFileLimit);

        var (head, rootTree, _, projectTree) = await ReadRootAsync(client, owner, branch, projectName, ct).ConfigureAwait(false);
        Dictionary<string, string> remote = new(StringComparer.Ordinal);
        if (projectTree is not null)
        {
            GhTree tree = await client.GetTreeAsync(owner, RepositoryName, projectTree, recursive: true, ct).ConfigureAwait(false);
            foreach (GhTreeEntry entry in tree.Tree.Where(entry => entry.Type == "blob" && entry.Sha is not null))
                remote[entry.Path] = entry.Sha!;
        }

        List<(string Relative, string FullPath, long Length)> changed = [];
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            string sha = await GitBlob.ComputeShaAsync(file.FullPath, file.Length, ct).ConfigureAwait(false);
            if (!remote.TryGetValue(file.Relative, out string? existing) || existing != sha) changed.Add(file);
        }
        HashSet<string> local = new(files.Select(file => file.Relative), StringComparer.Ordinal);
        List<string> deleted = [.. remote.Keys.Where(path => !local.Contains(path))];
        if (changed.Count == 0 && deleted.Count == 0)
        {
            settings.LastBackupUtc = DateTime.UtcNow;
            settings.Save(projectRoot);
            return new BackupReport(0, 0, 0, skippedLarge, "Backup is up to date.");
        }

        // Rounds of at most 10 MB (a single larger file goes alone); deletions ride with the last.
        List<List<(string Relative, string FullPath, long Length)>> rounds = [];
        List<(string Relative, string FullPath, long Length)> current = [];
        long bytes = 0;
        foreach (var file in changed.OrderBy(file => file.Relative, StringComparer.Ordinal))
        {
            if (current.Count > 0 && bytes + file.Length > RoundBytes)
            {
                rounds.Add(current);
                current = [];
                bytes = 0;
            }
            current.Add(file);
            bytes += file.Length;
        }
        if (current.Count > 0 || rounds.Count == 0) rounds.Add(current);

        int uploaded = 0;
        for (int round = 0; round < rounds.Count; round++)
        {
            if (round > 0)
            {
                progress?.Report($"Pausing {RoundPause.TotalSeconds:0} s before the next 10 MB ({round + 1} of {rounds.Count})…");
                await Task.Delay(RoundPause, ct).ConfigureAwait(false);
            }
            List<GhTreeEntry> entries = [];
            foreach (var file in rounds[round])
            {
                progress?.Report($"Uploading {file.Relative} ({uploaded + 1} of {changed.Count})…");
                byte[] content = await File.ReadAllBytesAsync(file.FullPath, ct).ConfigureAwait(false);
                string sha = await client.CreateBlobAsync(owner, RepositoryName, content, null, ct).ConfigureAwait(false);
                entries.Add(new GhTreeEntry { Path = projectName + "/" + file.Relative, Sha = sha });
                uploaded++;
            }
            if (round == rounds.Count - 1)
                entries.AddRange(deleted.Select(path => new GhTreeEntry { Path = projectName + "/" + path, Sha = null }));
            string tree = await client.CreateTreeAsync(owner, RepositoryName, rootTree, entries, ct).ConfigureAwait(false);
            string message = rounds.Count == 1
                ? $"Back up {projectName}"
                : $"Back up {projectName} ({round + 1} of {rounds.Count})";
            string commit = await client.CreateCommitAsync(owner, RepositoryName, message, tree, [head], ct).ConfigureAwait(false);
            await client.UpdateBranchAsync(owner, RepositoryName, branch, commit, force: false, ct).ConfigureAwait(false);
            head = commit;
            rootTree = tree;
        }

        settings.LastBackupUtc = DateTime.UtcNow;
        settings.Save(projectRoot);
        string summary = $"Backed up {uploaded} file{(uploaded == 1 ? "" : "s")}"
            + (deleted.Count > 0 ? $", removed {deleted.Count}" : "")
            + (rounds.Count > 1 ? $" in {rounds.Count} rounds" : "")
            + (skippedLarge > 0 ? $"; {skippedLarge} over GitHub's 100 MB limit skipped" : "") + ".";
        return new BackupReport(uploaded, deleted.Count, rounds.Count, skippedLarge, summary);
    }

    /// <summary>
    /// Makes the project folder match its backup. Files the restore would replace or remove are
    /// moved to <c>.genesis/Backup/Before restore …/</c> first, so nothing is lost. Skipped files
    /// (large files, models, your ignore entries) are left alone.
    /// </summary>
    public async Task<BackupReport> RestoreAsync(string projectRoot, string projectName, IProgress<string>? progress, CancellationToken ct = default)
    {
        progress?.Report("Connecting to GitHub…");
        var (client, owner, branch) = await ConnectAsync(create: false, ct).ConfigureAwait(false);
        var (_, _, _, projectTree) = await ReadRootAsync(client, owner, branch, projectName, ct).ConfigureAwait(false);
        if (projectTree is null) throw new InvalidOperationException($"'{projectName}' has no backup on GitHub yet.");
        GhTree tree = await client.GetTreeAsync(owner, RepositoryName, projectTree, recursive: true, ct).ConfigureAwait(false);
        List<GhTreeEntry> remote = [.. tree.Tree.Where(entry => entry.Type == "blob" && entry.Sha is not null)];
        HashSet<string> remotePaths = new(remote.Select(entry => entry.Path), StringComparer.OrdinalIgnoreCase);

        string safety = Path.Combine(projectRoot, ".genesis", "Backup", "Before restore " + DateTime.Now.ToString("yyyy-MM-dd HHmmss"));
        void KeepCopy(string relative, string fullPath, bool move)
        {
            string target = Path.Combine(safety, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (move) File.Move(fullPath, target, overwrite: true);
            else File.Copy(fullPath, target, overwrite: true);
        }

        int restored = 0, removed = 0;
        for (int i = 0; i < remote.Count; i++)
        {
            GhTreeEntry entry = remote[i];
            string target = Path.Combine(projectRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target))
            {
                long length = new FileInfo(target).Length;
                if (await GitBlob.ComputeShaAsync(target, length, ct).ConfigureAwait(false) == entry.Sha) continue;
                KeepCopy(entry.Path, target, move: false);
            }
            progress?.Report($"Restoring {entry.Path} ({i + 1} of {remote.Count})…");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temp = target + ".restore-tmp";
            await client.DownloadBlobAsync(owner, RepositoryName, entry.Sha!, temp, null, null, ct).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);
            restored++;
        }

        // Files the backup would have included but does not have: newer local work, set aside.
        ProjectBackupSettings settings = ProjectBackupSettings.Load(projectRoot);
        foreach (var file in CollectFiles(projectRoot, settings))
        {
            if (remotePaths.Contains(file.Relative)) continue;
            KeepCopy(file.Relative, file.FullPath, move: true);
            removed++;
        }

        string summary = restored == 0 && removed == 0
            ? "The project already matches its backup."
            : $"Restored {restored} file{(restored == 1 ? "" : "s")} from GitHub"
              + (removed > 0 ? $"; {removed} newer file{(removed == 1 ? "" : "s")} set aside" : "")
              + (restored + removed > 0 ? " (earlier copies are in .genesis/Backup)." : ".");
        return new BackupReport(restored, removed, 1, 0, summary);
    }

    /// <summary>Renames the project's folder in the repository in one commit (no files are uploaded).</summary>
    public async Task RenameOnlineAsync(string oldName, string newName, CancellationToken ct = default)
    {
        var (client, owner, branch) = await ConnectAsync(create: false, ct).ConfigureAwait(false);
        var (head, _, rootEntries, projectTree) = await ReadRootAsync(client, owner, branch, oldName, ct).ConfigureAwait(false);
        if (projectTree is null) return;
        if (rootEntries.Any(entry => entry.Path == newName))
            throw new InvalidOperationException($"The backup already has a folder named '{newName}'.");
        // The whole root listing, with the project's folder under its new name: no base tree, so the old name is gone.
        List<GhTreeEntry> entries = [.. rootEntries
            .Select(entry => new GhTreeEntry { Path = entry.Path == oldName ? newName : entry.Path, Mode = entry.Mode, Type = entry.Type, Sha = entry.Sha })];
        string tree = await client.CreateTreeAsync(owner, RepositoryName, entries, ct).ConfigureAwait(false);
        string commit = await client.CreateCommitAsync(owner, RepositoryName, $"Rename {oldName} to {newName}", tree, [head], ct).ConfigureAwait(false);
        await client.UpdateBranchAsync(owner, RepositoryName, branch, commit, force: false, ct).ConfigureAwait(false);
    }
}
