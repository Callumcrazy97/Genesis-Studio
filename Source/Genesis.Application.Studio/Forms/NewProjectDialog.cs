using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Projects.Templates;
using Genesis.Application.Studio.Controls;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Forms;

/// <summary>A focused final step for naming and locating a blank or template-backed project.</summary>
public sealed class NewProjectDialog : DpiAwareForm
{
    private readonly TextBox _nameBox;
    private readonly TextBox _locationBox;
    private readonly Label _validation;
    private readonly Label _destinationPreview;

    public NewProjectDialog(string? templateId = null)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = ThemeService.Palette.Canvas;
        ClientSize = new Size(920, 560);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(760, 580);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = "Genesis Studio — New Project";

        ProjectTemplate? template = ProjectTemplateCatalog.Find(templateId);
        SelectedTemplate = template?.Id ?? "Blank";

        TableLayoutPanel shell = new()
        {
            BackColor = ThemeService.Palette.Canvas,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            RowCount = 1,
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        Controls.Add(shell);
        Control summary = BuildSummary(template);
        shell.Controls.Add(summary, 0, 0);

        Panel form = new()
        {
            BackColor = Color.Transparent,
            Dock = DockStyle.Fill,
            Margin = new Padding(30, 2, 0, 0),
        };
        shell.Controls.Add(form, 1, 0);

        form.Controls.Add(new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            Font = new Font("Segoe UI Variable Text", 8f, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Accent,
            Location = new Point(0, 2),
            Text = template is null ? "NEW PROJECT" : "CREATE FROM TEMPLATE",
        });
        form.Controls.Add(new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            Font = new Font("Segoe UI Variable Display", 22f, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Text,
            Location = new Point(-2, 29),
            Size = new Size(510, 38),
            Text = "Create your project",
        });
        form.Controls.Add(new Label
        {
            AutoSize = false,
            ForeColor = ThemeService.Palette.TextMuted,
            Location = new Point(1, 76),
            Size = new Size(500, 38),
            Text = template is null
                ? "Start with an empty asset workspace. You can add rooms, code and resources in any order."
                : $"Start from {template.Name}. Its resources will be copied into your own editable project.",
        });

        form.Controls.Add(FieldLabel("PROJECT NAME", 0, 134));
        _nameBox = new TextBox
        {
            Location = new Point(0, 160),
            Size = new Size(510, 34),
            Text = template is null ? "My Genesis Game" : "My " + template.Name,
        };
        form.Controls.Add(_nameBox);

        form.Controls.Add(FieldLabel("SAVE PROJECT IN", 0, 217));
        _locationBox = new TextBox
        {
            Location = new Point(0, 243),
            Size = new Size(394, 34),
            Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Genesis Projects"),
        };
        form.Controls.Add(_locationBox);

        ModernButton browse = new()
        {
            Location = new Point(404, 241),
            Size = new Size(106, 38),
            Text = "Browse…",
        };
        browse.Click += (_, _) => BrowseLocation();
        form.Controls.Add(browse);

        RoundedSurfacePanel destination = new()
        {
            CornerRadius = 10,
            Location = new Point(0, 300),
            Size = new Size(510, 76),
        };
        destination.Controls.Add(new Label
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            Font = new Font(ThemeService.InterfaceFont, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Text,
            Location = new Point(15, 12),
            Text = "Project folder",
        });
        _destinationPreview = new Label
        {
            AutoEllipsis = true,
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = ThemeService.Palette.TextMuted,
            Location = new Point(15, 39),
            Size = new Size(480, 22),
        };
        destination.Controls.Add(_destinationPreview);
        form.Controls.Add(destination);

        _validation = new Label
        {
            AutoSize = false,
            ForeColor = ThemeService.Palette.Error,
            Location = new Point(0, 390),
            Size = new Size(510, 38),
        };
        form.Controls.Add(_validation);

        ModernButton cancel = new()
        {
            DialogResult = DialogResult.Cancel,
            Location = new Point(274, 454),
            Size = new Size(108, 40),
            Text = "Cancel",
        };
        form.Controls.Add(cancel);
        ModernButton create = new()
        {
            Accent = true,
            Glyph = "＋",
            Location = new Point(392, 454),
            Size = new Size(118, 40),
            Text = "Create",
        };
        create.Click += (_, _) => Confirm();
        form.Controls.Add(create);

        _nameBox.TextChanged += (_, _) => UpdateDestinationPreview();
        _locationBox.TextChanged += (_, _) => UpdateDestinationPreview();
        AcceptButton = create;
        CancelButton = cancel;
        void LayoutFields()
        {
            int Scale(int value) => DpiLayout.Scale(this, value);
            shell.ColumnStyles[0].Width = Scale(ClientSize.Width < Scale(880) ? 260 : 330);
            int width = Math.Max(Scale(260), form.ClientSize.Width);
            foreach (Control control in form.Controls)
            {
                if (control is Label { AutoSize: false }) control.Width = width;
            }
            _nameBox.Width = width;
            browse.Width = Scale(94);
            browse.Left = width - browse.Width;
            _locationBox.Width = Math.Max(Scale(100), browse.Left - Scale(10));
            destination.Width = width;
            _destinationPreview.Width = Math.Max(Scale(100), width - Scale(30));
            create.Left = Math.Max(0, width - create.Width);
            cancel.Left = Math.Max(0, create.Left - cancel.Width - Scale(10));
            create.Top = cancel.Top = Math.Max(Scale(454), form.ClientSize.Height - Scale(42));
            foreach (Control control in summary.Controls)
            {
                if (!control.AutoSize) control.Width = Math.Max(Scale(100), summary.ClientSize.Width - control.Left - Scale(20));
            }
        }
        form.SizeChanged += (_, _) => LayoutFields();
        Shown += (_, _) => LayoutFields();
        ThemeService.Apply(this);
        LayoutFields();
        UpdateDestinationPreview();
    }

    public string ProjectName => _nameBox.Text.Trim();

    public string ParentDirectory => _locationBox.Text.Trim();

    /// <summary>Template id passed to ProjectService; "Blank" for New Project.</summary>
    public string SelectedTemplate { get; }

    /// <summary>Whether the user explicitly confirmed replacing a non-empty destination.</summary>
    public bool OverwriteExisting { get; private set; }

    internal Label DestinationPreview => _destinationPreview;

    private static Control BuildSummary(ProjectTemplate? template)
    {
        RoundedSurfacePanel summary = new()
        {
            CornerRadius = 14,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Raised = true,
        };
        ProjectTemplateArtworkControl artwork = new()
        {
            Artwork = template?.Artwork ?? ProjectTemplateArtwork.Blank,
            Location = new Point(18, 18),
            Size = new Size(294, 158),
        };
        summary.Controls.Add(artwork);
        summary.Controls.Add(new Label
        {
            AutoSize = true,
            BackColor = ThemeService.Palette.SurfaceHover,
            Font = new Font(ThemeService.InterfaceFont.FontFamily, 7.3f, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Accent,
            Location = new Point(20, 193),
            Padding = new Padding(8, 4, 8, 4),
            Text = template is null
                ? "BLANK WORKSPACE"
                : template.Dimension == ProjectTemplateDimension.TwoD ? "2D GAME" : "3D GAME",
        });
        summary.Controls.Add(new Label
        {
            AutoEllipsis = true,
            AutoSize = false,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Variable Display", 15f, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Text,
            Location = new Point(20, 235),
            Size = new Size(288, 30),
            Text = template?.Name ?? "Blank project",
        });
        summary.Controls.Add(new Label
        {
            AutoEllipsis = true,
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = ThemeService.Palette.TextMuted,
            Location = new Point(20, 270),
            Size = new Size(288, 43),
            Text = template?.Tagline ?? "A clean project with no example content or hidden assumptions.",
        });

        IReadOnlyList<string> contents = template?.Contents ??
        [
            "Empty resource tree",
            "Editable project settings",
            "Ready for 2D or 3D assets",
        ];
        int y = 330;
        foreach (string line in contents.Take(4))
        {
            summary.Controls.Add(new Label
            {
                AutoEllipsis = true,
                AutoSize = false,
                BackColor = Color.Transparent,
                ForeColor = ThemeService.Palette.TextMuted,
                Location = new Point(21, y),
                Size = new Size(286, 23),
                Text = "✓  " + line,
            });
            y += 27;
        }
        return summary;
    }

    private static Label FieldLabel(string text, int x, int y) => new()
    {
        AutoSize = true,
        Font = new Font("Segoe UI Variable Text", 8f, FontStyle.Bold),
        ForeColor = ThemeService.Palette.TextMuted,
        Location = new Point(x, y),
        Text = text,
    };

    private void UpdateDestinationPreview()
    {
        string name = string.IsNullOrWhiteSpace(ProjectName) ? "Project name" : ProjectName;
        try
        {
            name = Genesis.Application.Core.Projects.ProjectService.SanitizeProjectName(name);
        }
        catch
        {
            // Keep the typed value visible; Confirm supplies the actionable validation message.
        }
        _destinationPreview.Text = string.IsNullOrWhiteSpace(ParentDirectory)
            ? name
            : Path.Combine(ParentDirectory, name);
    }

    private void BrowseLocation()
    {
        using FolderBrowserDialog dialog = new()
        {
            Description = "Choose where Genesis projects are stored",
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true,
        };
        if (Directory.Exists(_locationBox.Text)) dialog.InitialDirectory = _locationBox.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) _locationBox.Text = dialog.SelectedPath;
    }

    private void Confirm()
    {
        _validation.Text = string.Empty;
        if (string.IsNullOrWhiteSpace(ProjectName))
        {
            _validation.Text = "Enter a project name.";
            _nameBox.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(ParentDirectory))
        {
            _validation.Text = "Choose a project location.";
            _locationBox.Focus();
            return;
        }

        string safeName;
        try
        {
            safeName = Genesis.Application.Core.Projects.ProjectService.SanitizeProjectName(ProjectName);
        }
        catch (Exception exception)
        {
            _validation.Text = exception.Message;
            _nameBox.Focus();
            return;
        }

        string targetPath;
        bool occupied;
        try
        {
            targetPath = Path.Combine(Path.GetFullPath(ParentDirectory), safeName);
            if (File.Exists(targetPath)) throw new IOException("A file already exists at the project folder location.");
            occupied = Directory.Exists(targetPath) && Directory.EnumerateFileSystemEntries(targetPath).Any();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _validation.Text = "Choose a valid project location. " + exception.Message;
            _locationBox.Focus();
            return;
        }
        if (occupied)
        {
            DialogResult overwriteChoice = MessageBox.Show(
                this,
                $"The folder '{safeName}' already exists in '{ParentDirectory}' and is not empty.\n\n"
                + "Do you want to overwrite it and replace its contents?",
                "Project Folder Already Exists",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (overwriteChoice != DialogResult.Yes) return;
            OverwriteExisting = true;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
