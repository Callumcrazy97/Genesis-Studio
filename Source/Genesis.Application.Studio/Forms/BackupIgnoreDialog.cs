using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Backup;
using Genesis.Application.Studio.Controls;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Forms;

/// <summary>
/// The project's .gitignore as a list: what the backup skips and why, with buttons to add a
/// folder or file, remove an entry, or back up a large file or model anyway. Every change is
/// written at once.
/// </summary>
public sealed class BackupIgnoreDialog : DpiAwareForm
{
    private readonly string _projectRoot;
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = true,
        View = View.Details,
        BorderStyle = BorderStyle.None,
    };
    private readonly ModernButton _backUpAnyway = new() { Text = "Back up anyway" };
    private readonly ModernButton _skipAgain = new() { Text = "Skip again" };
    private readonly ModernButton _remove = new() { Text = "Remove from list" };

    private sealed record Row(string Entry, string Why, string Kind);

    public BackupIgnoreDialog(string projectRoot)
    {
        _projectRoot = projectRoot;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = ThemeService.Palette.Canvas;
        ClientSize = new Size(820, 540);
        MinimumSize = new Size(640, 420);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Text = "Online backup — what is skipped";

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(20, 16, 20, 16),
            BackColor = ThemeService.Palette.Canvas,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            ForeColor = ThemeService.Palette.TextMuted,
            Margin = new Padding(0, 0, 0, 10),
            Text = "Everything in the project is backed up except what is listed here. Files over 10 MB and models are "
                + "skipped automatically; choose 'Back up anyway' to include one. This list is the project's .gitignore.",
        }, 0, 0);

        _list.Columns.Add("File or folder", 470);
        _list.Columns.Add("Why", 280);
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        layout.Controls.Add(_list, 0, 1);

        ModernButton addFolder = new() { Text = "Add folder…" };
        ModernButton addFile = new() { Text = "Add file…" };
        ModernButton close = new() { Text = "Close", Accent = true, DialogResult = DialogResult.OK };
        addFolder.Click += (_, _) => AddFolder();
        addFile.Click += (_, _) => AddFile();
        _backUpAnyway.Click += (_, _) => Change(row => row.Kind == "auto", BackUpAnyway);
        _skipAgain.Click += (_, _) => Change(row => row.Kind == "included", SkipAgain);
        _remove.Click += (_, _) => Change(row => row.Kind is "always" or "yours", RemoveEntry);
        FlowLayoutPanel buttons = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0), WrapContents = true };
        foreach (ModernButton button in new[] { addFolder, addFile, _backUpAnyway, _skipAgain, _remove, close })
        {
            ShellDialogLayout.FitButton(button);
            button.Margin = new Padding(0, 0, 8, 0);
            buttons.Controls.Add(button);
        }
        layout.Controls.Add(buttons, 0, 2);
        AcceptButton = close;
        CancelButton = close;

        ThemeService.Apply(this);
        Reload();
    }

    private void Reload()
    {
        BackupIgnoreFile ignore = BackupIgnoreFile.Load(_projectRoot);
        ProjectBackupSettings settings = ProjectBackupSettings.Load(_projectRoot);
        List<Row> rows = [];
        rows.AddRange(ignore.Always.Select(entry => new Row(entry, "Always skipped (caches, builds, debug output)", "always")));
        rows.AddRange(ignore.Automatic.Select(entry => new Row(entry, AutomaticWhy(entry), "auto")));
        rows.AddRange(ignore.Yours.Select(entry => new Row(entry, "Added by you", "yours")));
        rows.AddRange(settings.Included.Select(entry => new Row(entry, "Backed up anyway (large file or model)", "included")));
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (Row row in rows)
        {
            ListViewItem item = new([row.Entry.TrimStart('/'), row.Why]) { Tag = row };
            if (row.Kind == "included") item.ForeColor = ThemeService.Palette.Success;
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        UpdateButtons();
    }

    private string AutomaticWhy(string entry)
    {
        string path = Path.Combine(_projectRoot, entry.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? ProjectBackup.AutomaticReason(path, new FileInfo(path).Length) ?? "Skipped automatically" : "Skipped automatically (file not found)";
    }

    private IEnumerable<Row> Selected => _list.SelectedItems.Cast<ListViewItem>().Select(item => (Row)item.Tag!);

    private void UpdateButtons()
    {
        _backUpAnyway.Enabled = Selected.Any(row => row.Kind == "auto");
        _skipAgain.Enabled = Selected.Any(row => row.Kind == "included");
        _remove.Enabled = Selected.Any(row => row.Kind is "always" or "yours");
    }

    private void Change(Func<Row, bool> applies, Action<BackupIgnoreFile, ProjectBackupSettings, Row> change)
    {
        BackupIgnoreFile ignore = BackupIgnoreFile.Load(_projectRoot);
        ProjectBackupSettings settings = ProjectBackupSettings.Load(_projectRoot);
        foreach (Row row in Selected.Where(applies).ToArray()) change(ignore, settings, row);
        Save(ignore, settings);
    }

    private static void BackUpAnyway(BackupIgnoreFile ignore, ProjectBackupSettings settings, Row row)
    {
        ignore.Automatic.Remove(row.Entry);
        string relative = row.Entry.TrimStart('/');
        if (!settings.Included.Contains(relative, StringComparer.OrdinalIgnoreCase)) settings.Included.Add(relative);
    }

    private static void SkipAgain(BackupIgnoreFile ignore, ProjectBackupSettings settings, Row row)
    {
        settings.Included.RemoveAll(entry => string.Equals(entry, row.Entry, StringComparison.OrdinalIgnoreCase));
        ignore.Automatic.Add("/" + row.Entry.TrimStart('/'));
    }

    private static void RemoveEntry(BackupIgnoreFile ignore, ProjectBackupSettings settings, Row row) => ignore.Remove(row.Entry);

    private void Save(BackupIgnoreFile ignore, ProjectBackupSettings settings)
    {
        try
        {
            ignore.Save(_projectRoot);
            settings.Save(_projectRoot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, error.Message, "Online backup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        Reload();
    }

    private void AddFolder()
    {
        using FolderBrowserDialog dialog = new()
        {
            Description = "Choose a folder in the project to skip",
            InitialDirectory = _projectRoot,
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddEntry(dialog.SelectedPath, folder: true);
    }

    private void AddFile()
    {
        using OpenFileDialog dialog = new() { InitialDirectory = _projectRoot, Title = "Choose files in the project to skip", Multiselect = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        foreach (string file in dialog.FileNames) AddEntry(file, folder: false);
    }

    private void AddEntry(string path, bool folder)
    {
        string relative = Path.GetRelativePath(_projectRoot, path).Replace(Path.DirectorySeparatorChar, '/');
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            MessageBox.Show(this, "Choose something inside the project folder.", "Online backup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string entry = "/" + relative + (folder ? "/" : string.Empty);
        BackupIgnoreFile ignore = BackupIgnoreFile.Load(_projectRoot);
        ProjectBackupSettings settings = ProjectBackupSettings.Load(_projectRoot);
        if (!ignore.All.Contains(entry, StringComparer.Ordinal)) ignore.Yours.Add(entry);
        settings.Included.RemoveAll(item => string.Equals(item, relative, StringComparison.OrdinalIgnoreCase));
        Save(ignore, settings);
    }
}
