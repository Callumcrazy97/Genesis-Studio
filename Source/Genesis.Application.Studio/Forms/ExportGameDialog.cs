using Genesis.Application.Core.Projects;
using Genesis.Application.Studio.Controls;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Forms;

/// <summary>A focused release wizard: destination, package form, cook, and progress.</summary>
public sealed class ExportGameDialog : DpiAwareForm
{
    private readonly ProjectSession _project;
    private readonly TextBox _gameTitle;
    private readonly ComboBox _platform;
    private readonly ComboBox _windowMode;
    private readonly TextBox _icon;
    private readonly TextBox _destination;
    private readonly RadioButton _folder;
    private readonly RadioButton _zip;
    private readonly CheckBox _shaders;
    private readonly Label _status;
    private readonly ProgressBar _progress;
    private readonly Button _export;
    private readonly Button _cancel;
    private CancellationTokenSource? _cancellation;

    public ExportGameDialog(ProjectSession project)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        Text = "Export Game";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(680, 570);
        BackColor = ThemeService.Palette.Canvas;
        ForeColor = ThemeService.Palette.Text;

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            ColumnCount = 1,
            RowCount = 12,
            BackColor = ThemeService.Palette.Canvas,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        Panel heading = new() { Dock = DockStyle.Fill };
        heading.Controls.Add(new Label
        {
            Text = "Build a distributable game",
            Font = new Font(Font.FontFamily, 15f, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Text,
            AutoSize = true,
            Location = new Point(0, 0),
        });
        heading.Controls.Add(new Label
        {
            Text = "Cook assets, compile scripts and shaders, then package the standalone Player.",
            ForeColor = ThemeService.Palette.TextMuted,
            AutoSize = true,
            Location = new Point(1, 31),
        });
        layout.Controls.Add(heading, 0, 0);

        _gameTitle = new TextBox { Text = project.Manifest.Name, Dock = DockStyle.Fill };
        StyleField(_gameTitle);
        layout.Controls.Add(LabeledRow("Game title", _gameTitle), 0, 1);

        _platform = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _platform.Items.Add("Windows x64");
        _platform.SelectedIndex = 0;
        StyleField(_platform);
        layout.Controls.Add(LabeledRow("Target platform", _platform), 0, 2);

        _windowMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _windowMode.Items.AddRange(["Windowed", "Fullscreen"]);
        _windowMode.SelectedIndex = 0;
        StyleField(_windowMode);
        layout.Controls.Add(LabeledRow("Default display", _windowMode), 0, 3);

        TableLayoutPanel iconRow = new() { Dock = DockStyle.Fill, ColumnCount = 3 };
        iconRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        iconRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        iconRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        iconRow.Controls.Add(FieldLabel("Game icon"), 0, 0);
        _icon = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Optional Windows .ico" };
        StyleField(_icon);
        Button browseIcon = ButtonOf("Browse…");
        browseIcon.Click += (_, _) => BrowseIcon();
        iconRow.Controls.Add(_icon, 1, 0);
        iconRow.Controls.Add(browseIcon, 2, 0);
        layout.Controls.Add(iconRow, 0, 4);

        TableLayoutPanel pathRow = new() { Dock = DockStyle.Fill, ColumnCount = 2 };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        _destination = new TextBox
        {
            Dock = DockStyle.Fill,
            BackColor = ThemeService.Palette.SurfaceRaised,
            ForeColor = ThemeService.Palette.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Text = DefaultDestination(project),
            Margin = new Padding(0, 6, 10, 6),
        };
        Button browse = ButtonOf("Browse…");
        browse.Click += (_, _) => Browse();
        pathRow.Controls.Add(_destination, 0, 0);
        pathRow.Controls.Add(browse, 1, 0);
        layout.Controls.Add(pathRow, 0, 5);

        GroupBox package = new()
        {
            Text = "Package",
            Dock = DockStyle.Fill,
            ForeColor = ThemeService.Palette.Text,
            Padding = new Padding(12, 8, 12, 6),
        };
        _folder = new RadioButton { Text = "Distributable folder", Checked = true, AutoSize = true, Location = new Point(14, 23) };
        _zip = new RadioButton { Text = "ZIP archive for upload", AutoSize = true, Location = new Point(215, 23) };
        _folder.CheckedChanged += (_, _) => UpdateDestinationExtension();
        _zip.CheckedChanged += (_, _) => UpdateDestinationExtension();
        package.Controls.Add(_folder);
        package.Controls.Add(_zip);
        layout.Controls.Add(package, 0, 6);

        _shaders = new CheckBox
        {
            Text = "Precompile built-in and project shaders for Direct3D, Vulkan and OpenGL",
            Checked = true,
            AutoSize = true,
            ForeColor = ThemeService.Palette.Text,
            Margin = new Padding(4, 10, 0, 0),
        };
        layout.Controls.Add(_shaders, 0, 7);

        _status = new Label
        {
            Text = "Ready to export.",
            Dock = DockStyle.Fill,
            ForeColor = ThemeService.Palette.TextMuted,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        layout.Controls.Add(_status, 0, 8);

        _progress = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 8,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 0,
            Visible = false,
        };
        layout.Controls.Add(_progress, 0, 10);

        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        _export = ButtonOf("Export Game", primary: true);
        _export.Click += async (sender, args) =>
        {
            if (Result?.Success == true) OpenLocation(sender, args);
            else await ExportAsync();
        };
        _cancel = ButtonOf("Cancel");
        _cancel.Click += (_, _) =>
        {
            if (_cancellation is null) Close();
            else _cancellation.Cancel();
        };
        buttons.Controls.Add(_export);
        buttons.Controls.Add(_cancel);
        layout.Controls.Add(buttons, 0, 11);

        Controls.Add(layout);
        ThemeService.Apply(this);
    }

    public GameExportResult? Result { get; private set; }

    private async Task ExportAsync()
    {
        string destination = _destination.Text.Trim();
        if (string.IsNullOrWhiteSpace(destination))
        {
            _status.Text = "Choose an export destination.";
            return;
        }

        SetBusy(true);
        _cancellation = new CancellationTokenSource();
        Progress<string> progress = new(message => _status.Text = message);
        GameExportRequest request = new(
            _project,
            destination,
            _zip.Checked ? GameExportFormat.Zip : GameExportFormat.Folder,
            ReplaceExisting: true,
            PrecompileShaders: _shaders.Checked,
            Platform: GameExportPlatform.WindowsX64,
            GameTitle: _gameTitle.Text.Trim(),
            IconPath: _icon.Text.Trim(),
            WindowMode: _windowMode.SelectedIndex == 1 ? GameExportWindowMode.Fullscreen : GameExportWindowMode.Windowed);
        Result = await Task.Run(() => GameExportService.Export(request, progress, _cancellation.Token));
        _cancellation.Dispose();
        _cancellation = null;
        SetBusy(false);

        if (Result.Success)
        {
            _status.Text = $"Exported {Result.ExecutableName}, {Result.ModelsCooked} model cook(s) and {Result.ShadersCooked} shader binary entries.";
            _export.Text = "Open Location";
            _export.Enabled = true;
            _cancel.Text = "Close";
        }
        else
        {
            _status.Text = Result.ErrorMessage;
        }
    }

    private void OpenLocation(object? sender, EventArgs e)
    {
        if (Result is null) return;
        string path = Result.OutputPath;
        string target = File.Exists(path) ? Path.GetDirectoryName(path) ?? path : path;
        if (Directory.Exists(target))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
    }

    private void SetBusy(bool busy)
    {
        _destination.Enabled = !busy;
        _gameTitle.Enabled = !busy;
        _platform.Enabled = !busy;
        _windowMode.Enabled = !busy;
        _icon.Enabled = !busy;
        _folder.Enabled = !busy;
        _zip.Enabled = !busy;
        _shaders.Enabled = !busy;
        _export.Enabled = !busy;
        _progress.Visible = busy;
        _progress.MarqueeAnimationSpeed = busy ? 28 : 0;
        _cancel.Text = busy ? "Cancel Export" : "Cancel";
    }

    private void Browse()
    {
        if (_zip.Checked)
        {
            using SaveFileDialog dialog = new()
            {
                Title = "Export game archive",
                Filter = "ZIP archive (*.zip)|*.zip",
                FileName = Path.GetFileName(_destination.Text),
                InitialDirectory = Path.GetDirectoryName(_destination.Text),
                AddExtension = true,
                DefaultExt = "zip",
            };
            if (dialog.ShowDialog(this) == DialogResult.OK) _destination.Text = dialog.FileName;
            return;
        }

        using FolderBrowserDialog folder = new()
        {
            Description = "Choose the distributable game folder",
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(_destination.Text)
                ? _destination.Text
                : Path.GetDirectoryName(_destination.Text) ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (folder.ShowDialog(this) == DialogResult.OK) _destination.Text = folder.SelectedPath;
    }

    private void BrowseIcon()
    {
        using OpenFileDialog dialog = new()
        {
            Title = "Choose exported game icon",
            Filter = "Windows icon (*.ico)|*.ico",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _icon.Text = dialog.FileName;
    }

    private void UpdateDestinationExtension()
    {
        string path = _destination.Text.Trim();
        if (string.IsNullOrWhiteSpace(path)) return;
        _destination.Text = _zip.Checked
            ? (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? path : path.TrimEnd(Path.DirectorySeparatorChar) + ".zip")
            : (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path);
    }

    private static string DefaultDestination(ProjectSession project)
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Genesis Exports");
        return Path.Combine(root, project.Manifest.Name);
    }

    private static Button ButtonOf(string text, bool primary = false) => new()
    {
        Text = text,
        AutoSize = false,
        Width = 120,
        Height = 34,
        FlatStyle = FlatStyle.Flat,
        BackColor = primary ? ThemeService.Palette.Accent : ThemeService.Palette.SurfaceRaised,
        ForeColor = primary ? Color.White : ThemeService.Palette.Text,
        Margin = new Padding(8, 4, 0, 4),
    };

    private static Control LabeledRow(string label, Control field)
    {
        TableLayoutPanel row = new() { Dock = DockStyle.Fill, ColumnCount = 2 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(FieldLabel(label), 0, 0);
        row.Controls.Add(field, 1, 0);
        return row;
    }

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = ThemeService.Palette.TextMuted,
        TextAlign = ContentAlignment.MiddleLeft,
    };

    private static void StyleField(Control control)
    {
        control.BackColor = ThemeService.Palette.SurfaceRaised;
        control.ForeColor = ThemeService.Palette.Text;
        control.Margin = new Padding(0, 6, 10, 6);
    }
}
