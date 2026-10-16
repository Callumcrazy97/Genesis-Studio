using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Backup;
using Genesis.Application.Core.Projects;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Forms;

/// <summary>What Studio should do to the project after closing it: rename it or restore it from GitHub.</summary>
public enum ProjectMaintenanceKind
{
    Rename,
    Restore,
}

public sealed class ProjectMaintenanceEventArgs(ProjectSession session, ProjectMaintenanceKind kind, string newName) : EventArgs
{
    public ProjectSession Session { get; } = session;
    public ProjectMaintenanceKind Kind { get; } = kind;
    public string NewName { get; } = newName;
}

/// <summary>A small window that says what Studio is doing while no project window is open.</summary>
public sealed class MaintenanceProgressForm : DpiAwareForm
{
    private readonly Label _status;

    public MaintenanceProgressForm(string title)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = ThemeService.Palette.Canvas;
        ClientSize = new Size(560, 150);
        ControlBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        Icon = Branding.WindowIcon ?? Icon;
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Genesis Studio — " + title;
        Controls.Add(new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(24, 20, 512, 32),
            Font = new Font("Segoe UI Variable Display", 14f, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Text,
            Text = title,
        });
        _status = new Label
        {
            AutoEllipsis = true,
            AutoSize = false,
            Bounds = new Rectangle(24, 60, 512, 24),
            ForeColor = ThemeService.Palette.TextMuted,
            Text = "Starting…",
        };
        Controls.Add(_status);
        Controls.Add(new ProgressBar { Bounds = new Rectangle(24, 100, 512, 8), Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 });
        ThemeService.Apply(this);
    }

    public void SetStatus(string text)
    {
        if (!IsDisposed) _status.Text = text;
    }
}

public sealed partial class StudioShellForm
{
    private bool _backupRunning;

    /// <summary>Raised when the project must be closed for a rename or a restore, then opened again.</summary>
    public event EventHandler<ProjectMaintenanceEventArgs>? MaintenanceRequested;

    private void RequestMaintenance(ProjectMaintenanceKind kind, string newName = "") =>
        BeginInvoke(() => MaintenanceRequested?.Invoke(this, new ProjectMaintenanceEventArgs(_project, kind, newName)));

    /// <summary>Backs the project up in the background when its backup is switched on and GitHub is signed in.</summary>
    private void BackUpIfEnabled()
    {
        if (ProjectBackupSettings.Load(_project.RootPath).Enabled) _ = BackUpAsync();
    }

    private async Task BackUpAsync()
    {
        if (_backupRunning) return;
        _backupRunning = true;
        string root = _project.RootPath, name = _project.Manifest.Name;
        Progress<string> progress = new(text => { if (!IsDisposed) SetStatus("GitHub backup: " + text); });
        try
        {
            BackupReport report = await new ProjectBackup(GitHubAccount.Shared).BackUpAsync(root, name, progress);
            if (!IsDisposed) SetStatus("GitHub backup: " + report.Summary);
            _services.Log.Information("Backup", report.Summary);
        }
        catch (Exception error) when (error is GitHubApiException or HttpRequestException or InvalidOperationException
            or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            string message = error is GitHubApiException api ? api.FullText : error.Message;
            if (!IsDisposed) SetStatus("GitHub backup failed: " + message);
            _services.Log.Warning("Backup", message);
        }
        finally
        {
            _backupRunning = false;
        }
    }
}
