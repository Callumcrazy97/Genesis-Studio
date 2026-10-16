using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Studio.Controls;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Forms;

/// <summary>
/// The first step of New Project inside an open project: a blank project or one of the Hub's
/// templates. Before this, File › New Project could only make a blank project, so starting from a
/// template meant closing Studio and going through the Hub.
/// </summary>
public sealed class ProjectTemplatePickerDialog : DpiAwareForm
{
    private readonly List<ProjectTemplateCard> _cards = [];
    private readonly FlowLayoutPanel _gallery;

    public ProjectTemplatePickerDialog()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = ThemeService.Palette.Canvas;
        ClientSize = new Size(1040, 640);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(640, 520);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = "Genesis Studio — New Project";

        TableLayoutPanel shell = new()
        {
            BackColor = ThemeService.Palette.Canvas,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(28, 24, 28, 20),
            RowCount = 3,
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(shell);

        FlowLayoutPanel header = new()
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Margin = new Padding(0, 0, 0, 12),
            WrapContents = false,
        };
        header.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI Variable Display", 20f, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Text,
            Text = "New project",
        });
        header.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = ThemeService.Palette.TextMuted,
            Margin = new Padding(3, 4, 3, 10),
            Text = "Start from a template you can play and learn from, or from an empty project.",
        });
        ModernButton blank = new()
        {
            AutoSize = true,
            Glyph = "＋",
            Name = "NewProjectBlank",
            Text = "Blank project",
        };
        blank.Click += (_, _) => Choose(null);
        header.Controls.Add(blank);
        shell.Controls.Add(header, 0, 0);

        _gallery = new FlowLayoutPanel
        {
            AutoScroll = true,
            BackColor = Color.Transparent,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = new Padding(0, 4, 8, 0),
            Tag = "transparent",
            WrapContents = true,
        };
        ProjectTemplateGallery.Fill(_gallery, template => Choose(template.Id), _cards);
        shell.Controls.Add(_gallery, 0, 1);

        ModernButton cancel = new()
        {
            Anchor = AnchorStyles.Right,
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
            Text = "Cancel",
        };
        shell.Controls.Add(cancel, 0, 2);
        CancelButton = cancel;

        _gallery.SizeChanged += (_, _) => ApplyInterfaceLayout();
        Shown += (_, _) => ApplyInterfaceLayout();
        ThemeService.Apply(this);
        ApplyInterfaceLayout();
    }

    /// <summary>The chosen template id, or null for a blank project.</summary>
    public string? TemplateId { get; private set; }

    internal IReadOnlyList<ProjectTemplateCard> TemplateCards => _cards;

    public override void ApplyInterfaceLayout()
    {
        _gallery.SuspendLayout();
        ProjectHubForm.LayoutFlowCards(_gallery, 254, 3, 410, DeviceDpi, _gallery.ClientSize.Width);
        _gallery.ResumeLayout();
    }

    private void Choose(string? templateId)
    {
        TemplateId = templateId;
        DialogResult = DialogResult.OK;
        Close();
    }
}
