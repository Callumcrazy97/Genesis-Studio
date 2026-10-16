using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Backup;
using Genesis.Application.Core.Projects;
using Genesis.Application.Studio.Controls;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Forms;

/// <summary>GitHub sign-in (General), and the project's online backup and rename (Project).</summary>
public sealed partial class PreferencesForm
{
    private readonly Label _gitHubStatus = new() { AutoSize = false, Size = new Size(620, 24), Name = "GitHubAccountStatus" };
    private readonly TextBox _gitHubToken = new() { UseSystemPasswordChar = true, Width = 380, PlaceholderText = "ghp_… or github_pat_…" };
    private readonly ModernButton _gitHubBrowser = new() { Text = "Sign in with browser" };
    private readonly ModernButton _gitHubUseToken = new() { Text = "Use token" };
    private readonly ModernButton _gitHubSignOut = new() { Text = "Sign out" };

    private readonly CheckBox _backupEnabled = new() { Name = "ProjectBackupEnabled" };
    private readonly Label _backupStatus = new() { AutoSize = false, Size = new Size(620, 40), Name = "ProjectBackupStatus" };
    private readonly ModernButton _backupChoose = new() { Text = "Choose what is backed up…" };
    private readonly ModernButton _backupNow = new() { Text = "Back up now" };
    private readonly ModernButton _backupRestore = new() { Text = "Restore from GitHub", Enabled = false };
    private readonly TextBox _projectNewName = new() { Width = 380, Name = "ProjectRenameBox" };
    private readonly ModernButton _projectRename = new() { Text = "Rename project" };

    /// <summary>A new name to give the project once Preferences closes (Studio closes and reopens it).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string? RequestedRename { get; private set; }

    /// <summary>True when the project should be restored from its GitHub backup once Preferences closes.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool RequestedRestore { get; private set; }

    /// <summary>True when backup was just switched on, so Studio backs the project up now.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool RequestedBackup { get; private set; }

    private static Label SectionHeading(string text) => new()
    {
        AutoSize = false,
        Font = new Font(ThemeService.InterfaceFont.FontFamily, 10f, FontStyle.Bold),
        ForeColor = ThemeService.Palette.Text,
        Margin = new Padding(0, 16, 0, 4),
        Size = new Size(620, 24),
        Text = text,
    };

    private static FlowLayoutPanel ButtonRow(params Control[] controls)
    {
        FlowLayoutPanel row = new()
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 0, 0, 10),
            Tag = "transparent",
            WrapContents = true,
        };
        foreach (Control control in controls)
        {
            control.Margin = new Padding(0, 0, 8, 0);
            if (control is ModernButton button) ShellDialogLayout.FitButton(button);
            row.Controls.Add(control);
        }
        return row;
    }

    private IEnumerable<Control> BuildGitHubSection()
    {
        _gitHubStatus.ForeColor = ThemeService.Palette.TextMuted;
        _gitHubBrowser.Click += async (_, _) => await SignInAsync(() => GitHubAccount.Shared.SignInWithBrowserAsync());
        _gitHubUseToken.Click += async (_, _) => await SignInAsync(() => GitHubAccount.Shared.SignInWithTokenAsync(_gitHubToken.Text));
        _gitHubSignOut.Click += (_, _) =>
        {
            GitHubAccount.Shared.SignOut();
            ShowGitHubAccount();
        };
        yield return SectionHeading("GitHub");
        yield return _gitHubStatus;
        yield return ButtonRow(_gitHubBrowser, _gitHubSignOut);
        yield return Field("Or paste a personal access token (it needs the \"repo\" scope)", ButtonRow(_gitHubToken, _gitHubUseToken));
        yield return Note("Browser sign-in uses Git Credential Manager (part of Git for Windows). The token is kept encrypted "
            + "for your Windows account, never inside a project. Projects back up to github.com/<you>/"
            + ProjectBackup.RepositoryName + ", one folder per project.");
        _ = RefreshGitHubAccountAsync();
    }

    private async Task RefreshGitHubAccountAsync()
    {
        _gitHubStatus.Text = "Checking GitHub…";
        try { await GitHubAccount.Shared.EnsureSignedInAsync(); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException) { }
        if (!IsDisposed) ShowGitHubAccount();
    }

    private void ShowGitHubAccount()
    {
        string? login = GitHubAccount.Shared.Login;
        _gitHubStatus.Text = login is null ? "Not signed in." : $"Signed in as {login}.";
        _gitHubStatus.ForeColor = login is null ? ThemeService.Palette.TextMuted : ThemeService.Palette.Success;
        _gitHubSignOut.Enabled = login is not null;
        if (_project is not null) _ = RefreshBackupStatusAsync();
    }

    private async Task SignInAsync(Func<Task<string>> signIn)
    {
        _gitHubBrowser.Enabled = _gitHubUseToken.Enabled = false;
        _gitHubStatus.Text = "Signing in…";
        try
        {
            await signIn();
            _gitHubToken.Clear();
        }
        catch (Exception error) when (error is GitHubApiException or HttpRequestException or InvalidOperationException
            or ArgumentException or TimeoutException or TaskCanceledException)
        {
            string message = error is GitHubApiException api && api.IsUnauthorised ? "GitHub did not accept that token." : error.Message;
            if (!IsDisposed) MessageBox.Show(this, message, "GitHub sign-in", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        if (IsDisposed) return;
        _gitHubBrowser.Enabled = _gitHubUseToken.Enabled = true;
        ShowGitHubAccount();
    }

    private IEnumerable<Control> BuildBackupSection()
    {
        ProjectSession project = _project!;
        ProjectBackupSettings settings = ProjectBackupSettings.Load(project.RootPath);
        ConfigureCheckBox(_backupEnabled, "Back up this project to GitHub");
        _backupEnabled.Checked = settings.Enabled;
        _backupStatus.ForeColor = ThemeService.Palette.TextMuted;
        _backupChoose.Click += (_, _) =>
        {
            using BackupIgnoreDialog dialog = new(project.RootPath);
            dialog.ShowDialog(this);
        };
        _backupNow.Click += async (_, _) => await BackUpNowAsync(project);
        _backupRestore.Click += (_, _) =>
        {
            DialogResult answer = MessageBox.Show(this,
                $"Replace '{project.Manifest.Name}' with its GitHub backup?\n\nFiles the restore changes or removes are kept in "
                + ".genesis\\Backup first. Studio closes the project, restores it and opens it again.",
                "Restore from GitHub", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes || !TrySaveAndApply()) return;
            RequestedRestore = true;
            DialogResult = DialogResult.OK;
            Close();
        };
        yield return SectionHeading("Online backup");
        yield return _backupEnabled;
        yield return _backupStatus;
        yield return ButtonRow(_backupChoose, _backupNow, _backupRestore);
        yield return Note("Each backup sends only what changed, at most 10 MB per round with a short pause between rounds. "
            + "Files over 10 MB and models are skipped until you choose them under 'Choose what is backed up'.");
    }

    private async Task RefreshBackupStatusAsync()
    {
        if (_project is null) return;
        string? login = GitHubAccount.Shared.Login;
        ProjectBackupSettings settings = ProjectBackupSettings.Load(_project.RootPath);
        if (login is null)
        {
            _backupStatus.Text = "Sign in to GitHub in General to back this project up.";
            _backupNow.Enabled = _backupRestore.Enabled = false;
            return;
        }
        _backupNow.Enabled = true;
        _backupStatus.Text = $"Backs up to github.com/{login}/{ProjectBackup.RepositoryName}/{_project.Manifest.Name}"
            + (settings.LastBackupUtc is DateTime last ? $"\nLast backup {last.ToLocalTime():d MMM yyyy, HH:mm}." : "\nNot backed up yet.");
        try
        {
            bool online = await new ProjectBackup(GitHubAccount.Shared).ExistsOnlineAsync(_project.Manifest.Name);
            if (!IsDisposed) _backupRestore.Enabled = online;
        }
        catch (Exception error) when (error is GitHubApiException or HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            if (!IsDisposed) _backupRestore.Enabled = false;
        }
    }

    private async Task BackUpNowAsync(ProjectSession project)
    {
        _backupNow.Enabled = false;
        Progress<string> progress = new(text => { if (!IsDisposed) _backupStatus.Text = text; });
        try
        {
            BackupReport report = await new ProjectBackup(GitHubAccount.Shared).BackUpAsync(project.RootPath, project.Manifest.Name, progress);
            if (!IsDisposed) _backupStatus.Text = report.Summary;
            if (!IsDisposed) _backupRestore.Enabled = true;
        }
        catch (Exception error) when (error is GitHubApiException or HttpRequestException or InvalidOperationException
            or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            if (!IsDisposed) _backupStatus.Text = "Backup failed: " + (error is GitHubApiException api ? api.FullText : error.Message);
        }
        if (!IsDisposed) _backupNow.Enabled = true;
    }

    private IEnumerable<Control> BuildRenameSection()
    {
        ProjectSession project = _project!;
        _projectNewName.Text = project.Manifest.Name;
        _projectRename.Click += (_, _) =>
        {
            string name = _projectNewName.Text.Trim();
            if (name.Length == 0 || string.Equals(name, project.Manifest.Name, StringComparison.Ordinal)) return;
            try
            {
                ProjectService.SanitizeProjectName(name);
            }
            catch (ArgumentException error)
            {
                MessageBox.Show(this, error.Message, "Rename project", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult answer = MessageBox.Show(this,
                $"Rename '{project.Manifest.Name}' to '{name}'?\n\nStudio closes the project, renames its folder and project file "
                + "(and its online backup, if it has one) and opens it again.",
                "Rename project", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes || !TrySaveAndApply()) return;
            RequestedRename = name;
            DialogResult = DialogResult.OK;
            Close();
        };
        yield return SectionHeading("Project name");
        yield return Field("Name (the folder, the project file and the online backup follow it)", ButtonRow(_projectNewName, _projectRename));
    }

    private void SaveBackupSettings()
    {
        if (_project is null) return;
        ProjectBackupSettings settings = ProjectBackupSettings.Load(_project.RootPath);
        if (settings.Enabled == _backupEnabled.Checked) return;
        settings.Enabled = _backupEnabled.Checked;
        settings.Save(_project.RootPath);
        RequestedBackup = settings.Enabled && GitHubAccount.Shared.IsSignedIn;
    }
}
