using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Genesis.Application.Core.Diagnostics;
using Genesis.Application.Core.Resources;
using Genesis.Application.Studio.Theme;
using WeifenLuo.WinFormsUI.Docking;
using Genesis.Shared.Assets;

namespace Genesis.Application.Studio.Docking;

public sealed class ResourceDocument : GenesisDockContent, IStudioDocument
{
    private readonly StudioLog _log;
    private readonly RichTextBox _editor;
    private readonly ToolStripLabel _dirtyLabel;
    private bool _loading;
    private bool _dirty;

    public ResourceDocument(ResourceItem resource, StudioLog log)
    {
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        Text = resource.Name;
        TabText = resource.Name;
        ToolTipText = resource.Name;
        DockAreas = DockAreas.Document | DockAreas.Float;
        ShowHint = DockState.Document;
        HideOnClose = false;

        ToolStrip toolbar = new()
        {
            BackColor = ThemeService.Palette.Surface,
            GripStyle = ToolStripGripStyle.Hidden,
            Padding = new Padding(8, 5, 8, 5),
            Renderer = ThemeService.CreateToolStripRenderer(),
        };
        ToolStripButton save = new("Save")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Save (Ctrl+S)",
        };
        save.Click += (_, _) => Save();
        toolbar.Items.Add(save);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(new ToolStripLabel(resource.Name)
        {
            ForeColor = ThemeService.Palette.TextMuted,
        });
        _dirtyLabel = new ToolStripLabel("Saved")
        {
            Alignment = ToolStripItemAlignment.Right,
            ForeColor = ThemeService.Palette.Success,
        };
        toolbar.Items.Add(_dirtyLabel);
        Controls.Add(toolbar);

        SplitContainer split = new()
        {
            BackColor = ThemeService.Palette.Border,
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel2,
            SplitterWidth = 1,
        };
        Controls.Add(split);
        split.BringToFront();
        split.HandleCreated += (_, _) => ApplySafeSplitterDistance(split);
        split.SizeChanged += (_, _) => ApplySafeSplitterDistance(split);

        _editor = new RichTextBox
        {
            AcceptsTab = true,
            BackColor = ThemeService.Palette.Canvas,
            BorderStyle = BorderStyle.None,
            DetectUrls = false,
            Dock = DockStyle.Fill,
            Font = ThemeService.CodeFont,
            ForeColor = ThemeService.Palette.Text,
            HideSelection = false,
            Padding = new Padding(14),
            WordWrap = false,
        };
        _editor.TextChanged += (_, _) =>
        {
            if (!_loading)
            {
                SetDirty(true);
            }
        };
        split.Panel1.Controls.Add(_editor);

        Panel details = new()
        {
            AutoScroll = true,
            BackColor = ThemeService.Palette.Surface,
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
        };
        split.Panel2.Controls.Add(details);
        details.Controls.Add(CreateDetail("RESOURCE TYPE", DisplayKind(resource.Kind), 18));
        details.Controls.Add(CreateDetail(
            "ASSET GUID",
            resource.AssetId == Guid.Empty ? "Unassigned" : resource.AssetId.ToString("N"),
            92));
        details.Controls.Add(CreateDetail("RESOURCE NAME", resource.Name, 166));
        details.Controls.Add(CreateDetail(
            "EDITOR",
            "Foundation text surface\nSpecialised editor routing follows this contract.",
            240,
            92));

        LoadResource();
        ThemeService.Apply(this);
    }

    public ResourceItem Resource { get; }

    public string DocumentIdentity =>
        Resource.AssetId != Guid.Empty
            ? Resource.AssetId.ToString("N")
            : Path.GetFullPath(Resource.FullPath);

    public bool IsDirty => _dirty;

    public bool CanExecute(StudioDocumentCommand command) =>
        command switch
        {
            StudioDocumentCommand.Save => _dirty,
            StudioDocumentCommand.Cut => _editor.SelectionLength > 0,
            StudioDocumentCommand.Copy => _editor.SelectionLength > 0,
            StudioDocumentCommand.Paste => _editor.CanPaste(DataFormats.GetFormat(DataFormats.Text)),
            StudioDocumentCommand.Delete => _editor.SelectionLength > 0,
            StudioDocumentCommand.SelectAll => _editor.TextLength > 0,
            _ => false,
        };

    public void Execute(StudioDocumentCommand command)
    {
        switch (command)
        {
            case StudioDocumentCommand.Save:
                Save();
                break;
            case StudioDocumentCommand.Cut:
                _editor.Cut();
                break;
            case StudioDocumentCommand.Copy:
                _editor.Copy();
                break;
            case StudioDocumentCommand.Paste:
                _editor.Paste();
                break;
            case StudioDocumentCommand.Delete:
                _editor.SelectedText = string.Empty;
                break;
            case StudioDocumentCommand.SelectAll:
                _editor.SelectAll();
                break;
        }
    }

    public void Save()
    {
        if (Resource.IsFolder || !File.Exists(Resource.FullPath))
        {
            return;
        }

        string root = ResourceNames.FindProjectRoot(Resource.FullPath);
        string text = ResourceReferenceRewriter.Normalize(root, Resource.FullPath, _editor.Text);
        ProjectAssetWriteRegistry.MarkLocalWrite(Resource.FullPath);
        File.WriteAllText(Resource.FullPath, text, new UTF8Encoding(false));
        ResourceNames.Invalidate(root);
        SetDirty(false);
        _log.Information("Editor", $"Saved {Resource.Name}.");
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.S))
        {
            Save();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_dirty)
        {
            if (Genesis.Application.Core.Diagnostics.UnattendedSession.IsActive)
            {
                Save();
                base.OnFormClosing(e);
                return;
            }

            DialogResult answer = MessageBox.Show(
                this,
                $"Save changes to '{Resource.Name}'?",
                "Unsaved resource",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (answer == DialogResult.Yes)
            {
                Save();
            }
        }

        base.OnFormClosing(e);
    }

    private void LoadResource()
    {
        _loading = true;
        try
        {
            _editor.Text = File.Exists(Resource.FullPath)
                ? File.ReadAllText(Resource.FullPath)
                : string.Empty;
            SetDirty(false);
        }
        catch (IOException exception)
        {
            _editor.ReadOnly = true;
            _editor.Text = $"This resource could not be opened.{Environment.NewLine}{exception.Message}";
            _log.Error("Editor", $"Failed to open {Resource.Name}.", exception);
        }
        finally
        {
            _loading = false;
        }
    }

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        TabText = dirty ? $"{Resource.Name} •" : Resource.Name;
        _dirtyLabel.Text = dirty ? "Modified" : "Saved";
        _dirtyLabel.ForeColor = dirty
            ? ThemeService.Palette.Warning
            : ThemeService.Palette.Success;
    }

    private static void ApplySafeSplitterDistance(SplitContainer split)
    {
        if (!split.IsHandleCreated || split.Width <= split.SplitterWidth)
        {
            return;
        }

        const int preferredPanel2 = 240;
        int usable = Math.Max(0, split.Width - split.SplitterWidth);
        int panel2 = Math.Min(preferredPanel2, Math.Max(80, usable / 3));
        int panel1 = Math.Max(80, usable - panel2);

        // Setting MinSize validates against SplitterDistance — clear mins first.
        split.Panel1MinSize = 0;
        split.Panel2MinSize = 0;
        split.SplitterDistance = panel1;
        split.Panel1MinSize = Math.Min(120, Math.Max(0, panel1));
        split.Panel2MinSize = Math.Min(160, Math.Max(0, panel2));
    }

    private static Control CreateDetail(
        string heading,
        string value,
        int y,
        int height = 58)
    {
        Panel panel = new()
        {
            BackColor = ThemeService.Palette.SurfaceRaised,
            Location = new Point(18, y),
            Padding = new Padding(12, 8, 12, 8),
            Size = new Size(230, height),
        };
        Label headingLabel = new()
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Variable Text", 7.5f, FontStyle.Bold),
            ForeColor = ThemeService.Palette.TextMuted,
            Location = new Point(12, 7),
            Text = heading,
        };
        panel.Controls.Add(headingLabel);
        Label valueLabel = new()
        {
            AutoEllipsis = true,
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = ThemeService.Palette.Text,
            Location = new Point(12, 26),
            Size = new Size(204, height - 30),
            Text = value,
        };
        panel.Controls.Add(valueLabel);
        return panel;
    }

    private static string DisplayKind(ResourceKind kind) =>
        ResourceDefinitions.All.FirstOrDefault(definition => definition.Kind == kind)
            ?.DisplayName ?? "Unregistered";
}
