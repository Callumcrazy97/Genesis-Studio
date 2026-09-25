using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Suite.Assets;

public enum NoteEditorViewMode
{
    Source,
    Split,
    Preview,
}

/// <summary>
/// Note Editor: a project-note workspace with outline/library navigation, markdown authoring,
/// preview, note metadata and live Inspector integration. The persisted resource remains raw
/// CommonMark-compatible <c>.md</c> text.
/// </summary>
public sealed partial class NoteEditorControl : EditorSurfaceControl, IResourceInspectorTarget, ILiveResourceInspectorTarget
{
    private readonly ListBox _outline = new();
    private readonly TextBox _title = new();
    private readonly TextBox _tags = new();
    private readonly RichTextBox _source;
    private readonly RichTextBox _preview;
    private readonly EditorScrollHost _sourceHost;
    private readonly EditorScrollHost _previewHost;
    private readonly SplitContainer _split;
    private readonly Panel _leftPanel;
    private readonly Panel _rightPanel;
    private readonly Label _statsWords = new() { Name = "NoteStatsWords" };
    private readonly Label _statsHeadings = new() { Name = "NoteStatsHeadings" };
    private readonly Label _statsLinks = new() { Name = "NoteStatsLinks" };
    private readonly Label _statsModified = new() { Name = "NoteStatsModified" };
    private readonly Label _stats = new();
    private readonly ListBox _links = new();
    private readonly TreeView _noteLibrary = new() { BorderStyle = BorderStyle.None, HideSelection = false, FullRowSelect = true };
    private readonly TextBox _noteSearch = new() { PlaceholderText = "Search project notes…" };
    private readonly Dictionary<NoteEditorViewMode, ToolStripButton> _viewButtons = [];
    private readonly System.Windows.Forms.Timer _renderTimer;
    private NoteEditorViewMode _viewMode = NoteEditorViewMode.Split;
    private bool _updatingMetadata;
    private bool _highlightingSource;

    public NoteEditorControl(string resourcePath, string projectRoot)
        : base(resourcePath, projectRoot)
    {
        Dock = DockStyle.Fill;

        EditorCommandBar toolbar = EditorChrome.MakeToolbar();
        EditorViewportChrome.AttachDocumentMenus(toolbar, this);
        toolbar.Items.Add(new ToolStripLabel(ResourceDisplayName.Format(ResourcePath)) { ForeColor = EditorChrome.Text, Font = EditorChrome.HeadingFont, ToolTipText = ResourcePath });
        ToolStripButton save = EditorChrome.ToolButton("Save", "Save note (Ctrl+S)", Save); save.BackColor = EditorChrome.Accent; toolbar.Items.Add(save);
        ToolStripDropDownButton export = new("Export As…");
        export.DropDownItems.Add("Markdown…", null, (_, _) => ExportMarkdown());
        export.DropDownItems.Add("PDF…", null, (_, _) => ExportPdf());
        toolbar.Items.Add(export);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(ToolbarCaption("View"));
        foreach (NoteEditorViewMode mode in Enum.GetValues<NoteEditorViewMode>())
        {
            NoteEditorViewMode captured = mode;
            ToolStripButton button = EditorChrome.ToolButton(
                mode.ToString(),
                $"Show the note in {mode.ToString().ToLowerInvariant()} view",
                () => SetViewMode(captured),
                toggle: true);
            _viewButtons.Add(mode, button);
            toolbar.Items.Add(button);
        }

        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(EditorChrome.ToolButton("H1", "Heading 1", () => WrapSelection("# ", "")));
        toolbar.Items.Add(EditorChrome.ToolButton("H2", "Heading 2", () => WrapSelection("## ", "")));
        toolbar.Items.Add(EditorChrome.ToolButton("B", "Bold selection", () => WrapSelection("**", "**")));
        toolbar.Items.Add(EditorChrome.ToolButton("I", "Italic selection", () => WrapSelection("*", "*")));
        toolbar.Items.Add(EditorChrome.ToolButton("Code", "Inline code", () => WrapSelection("`", "`")));
        toolbar.Items.Add(EditorChrome.ToolButton("Link", "Insert markdown link", InsertLink));
        toolbar.Items.Add(EditorChrome.ToolButton("Task", "Insert task item", () => InsertAtCaret("- [ ] ")));

        _leftPanel = EditorChrome.MakePanel(DockStyle.Left, EditorChrome.LeftPanelWidth, padding: new Padding(10));
        _rightPanel = EditorChrome.MakePanel(DockStyle.Right, EditorChrome.RightPanelWidth, padding: new Padding(10));

        BuildLeftPanel();
        BuildRightPanel();

        _split = new SplitContainer
        {
            BackColor = EditorChrome.Border,
            Dock = DockStyle.Fill,
            SplitterWidth = 1,
        };

        _sourceHost = new EditorScrollHost(EditorChrome.Canvas) { Dock = DockStyle.Fill };
        _previewHost = new EditorScrollHost(EditorChrome.Surface) { Dock = DockStyle.Fill };

        _source = new RichTextBox
        {
            AcceptsTab = true,
            BackColor = EditorChrome.Canvas,
            BorderStyle = BorderStyle.None,
            DetectUrls = false,
            Dock = DockStyle.Top,
            Font = EditorChrome.CodeFont,
            ForeColor = EditorChrome.Text,
            Multiline = true,
            ScrollBars = RichTextBoxScrollBars.None,
        };

        _preview = new RichTextBox
        {
            BackColor = EditorChrome.Surface,
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Top,
            Font = EditorChrome.BaseFont,
            ForeColor = EditorChrome.Text,
            ReadOnly = true,
            ScrollBars = RichTextBoxScrollBars.None,
        };

        _sourceHost.Controls.Add(_source);
        _previewHost.Controls.Add(_preview);
        _split.Panel1.Controls.Add(_sourceHost);
        _split.Panel2.Controls.Add(_previewHost);
        Controls.Add(_split);
        Controls.Add(_rightPanel);
        Controls.Add(_leftPanel);
        Controls.Add(toolbar);

        try
        {
            _source.Text = File.Exists(ResourcePath) ? File.ReadAllText(ResourcePath) : "# New Note\n";
        }
        catch (IOException exception)
        {
            LoadWarning = exception.Message;
        }

        _source.SelectionStart = 0;
        _source.SelectionLength = 0;

        _renderTimer = new System.Windows.Forms.Timer { Interval = 260 };
        _renderTimer.Tick += (_, _) =>
        {
            _renderTimer.Stop();
            RefreshDerivedState();
        };
        _source.TextChanged += (_, _) =>
        {
            if (_highlightingSource) return;
            MarkDirty();
            UpdateSourceHeight();
            _renderTimer.Stop();
            _renderTimer.Start();
        };
        _title.TextChanged += (_, _) =>
        {
            if (!_updatingMetadata)
            {
                ApplyTitle(_title.Text);
            }
        };
        _tags.TextChanged += (_, _) =>
        {
            if (!_updatingMetadata)
            {
                InspectorStateChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        _outline.DoubleClick += (_, _) => NavigateToSelectedHeading();
        _links.MouseClick += (_, _) => OpenSelectedAssetReference();
        _noteLibrary.NodeMouseDoubleClick += (_, args) => { if (args.Node?.Tag is string path) RequestOpenLinkedResource(path); };
        _noteSearch.TextChanged += (_, _) => RefreshNoteLibrary();
        _preview.MouseDoubleClick += (_, args) => ToggleTaskFromPreview(args.Location);
        _split.SizeChanged += (_, _) =>
        {
            UpdateSourceHeight();
            UpdatePreviewHeight();
        };
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        Disposed += (_, _) => _renderTimer.Dispose();

        RefreshDerivedState();
        RefreshNoteLibrary();
        SetViewMode(NoteEditorViewMode.Split);
        ApplyResponsiveLayout();
    }

    public event EventHandler? InspectorStateChanged;

    public NoteEditorViewMode ViewMode => _viewMode;

    public string NoteText
    {
        get => _source.Text;
        set
        {
            _source.Text = value;
            _source.SelectionStart = 0;
            _source.SelectionLength = 0;
        }
    }

    public override void Save()
    {
        WriteResourceText(_source.Text);
        AcceptSave();
    }

    public IReadOnlyList<ResourceInspectorLiveValue> GetLiveInspectorValues()
    {
        string title = ExtractTitle(_source.Text);
        string tags = _tags.Text.Trim();
        return
        [
            new("Note", "Note.Title", "Title", title, Description: "First H1 heading in the note."),
            new("Note", "Note.Tags", "Tags", tags, Description: "Comma-separated project note tags."),
            new("Note", "Note.ViewMode", "View", _viewMode.ToString(), Choices: Enum.GetNames<NoteEditorViewMode>()),
            new("Stats", "Stats.Words", "Words", CountWords(_source.Text), ReadOnly: true),
            new("Stats", "Stats.Headings", "Headings", _outline.Items.Count, ReadOnly: true),
            new("Links", "Links.Count", "Links", _links.Items.Count, ReadOnly: true),
        ];
    }

    public bool TryApplyInspectorValue(string propertyPath, object? value) =>
        TryApplyLiveInspectorValue(propertyPath, value);

    public bool TryApplyLiveInspectorValue(string propertyPath, object? value)
    {
        string text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        switch (propertyPath)
        {
            case "Note.Title":
                ApplyTitle(text);
                return true;
            case "Note.Tags":
                _tags.Text = text;
                InspectorStateChanged?.Invoke(this, EventArgs.Empty);
                return true;
            case "Note.ViewMode" when Enum.TryParse(text, ignoreCase: true, out NoteEditorViewMode mode):
                SetViewMode(mode);
                return true;
            default:
                return false;
        }
    }

    public void SetViewMode(NoteEditorViewMode mode)
    {
        _viewMode = mode;
        _split.Panel1Collapsed = mode == NoteEditorViewMode.Preview;
        _split.Panel2Collapsed = mode == NoteEditorViewMode.Source;
        foreach ((NoteEditorViewMode candidate, ToolStripButton button) in _viewButtons)
        {
            button.Checked = candidate == mode;
        }

        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BuildLeftPanel()
    {
        _leftPanel.Controls.Add(_outline);
        _leftPanel.Controls.Add(EditorChrome.SectionHeader("Outline"));
        _leftPanel.Controls.Add(BuildSummaryCard());
        Panel library = new() { Dock = DockStyle.Top, Height = 250, BackColor = EditorChrome.Surface, Padding = new Padding(0, 34, 0, 38) };
        _noteLibrary.Dock = DockStyle.Fill; _noteLibrary.BackColor = EditorChrome.Raised; _noteLibrary.ForeColor = EditorChrome.Text; _noteLibrary.ItemHeight = 27;
        _noteSearch.Dock = DockStyle.Top; _noteSearch.Height = 30; EditorChrome.StyleField(_noteSearch);
        Button create = new() { Dock = DockStyle.Bottom, Height = 31, Text = "+ New Note" }; EditorChrome.StyleField(create); create.Click += (_, _) => CreateProjectNote();
        library.Controls.Add(_noteLibrary); library.Controls.Add(_noteSearch); library.Controls.Add(create);
        _leftPanel.Controls.Add(library);
        _leftPanel.Controls.Add(EditorChrome.SectionHeader("Note Library"));

        _outline.Dock = DockStyle.Fill;
        _outline.IntegralHeight = false;
        _outline.DrawMode = DrawMode.OwnerDrawFixed;
        _outline.ItemHeight = 26;
        _outline.DrawItem += DrawOutlineItem;
        EditorChrome.StyleField(_outline);
    }

    private Control BuildSummaryCard()
    {
        Panel card = EditorChrome.MakePanel(DockStyle.Top, height: 96, padding: new Padding(12, 10, 10, 10));
        card.Name = "NoteSummaryCard";
        card.BackColor = EditorChrome.Raised;
        card.Paint += (_, e) =>
        {
            using SolidBrush accent = new(EditorChrome.Accent);
            e.Graphics.FillRectangle(accent, new Rectangle(0, 8, 3, Math.Max(8, card.Height - 16)));
        };
        Label name = new()
        {
            AutoEllipsis = true,
            Dock = DockStyle.Top,
            Font = new Font(EditorChrome.BaseFont, FontStyle.Bold),
            ForeColor = EditorChrome.Text,
            Height = 28,
            Name = "NoteSummaryTitle",
            Padding = new Padding(6, 0, 0, 0),
            Text = ResourceDisplayName.Format(ResourcePath),
        };
        Label hint = new()
        {
            Dock = DockStyle.Fill,
            ForeColor = EditorChrome.Muted,
            Padding = new Padding(6, 0, 0, 0),
            Text = "Project note · headings become outline entries · links are indexed on the right.",
        };
        card.Controls.Add(hint);
        card.Controls.Add(name);
        return card;
    }

    private void BuildRightPanel()
    {
        _rightPanel.Controls.Add(_links);
        _rightPanel.Controls.Add(EditorChrome.SectionHeader("ASSET & REFERENCE INSPECTOR"));
        _rightPanel.Controls.Add(BuildStatsCard());
        _rightPanel.Controls.Add(EditorChrome.SectionHeader("Document Stats"));
        _rightPanel.Controls.Add(BuildMetadataFields());
        _rightPanel.Controls.Add(EditorChrome.SectionHeader("Metadata"));

        _links.Dock = DockStyle.Fill;
        _links.IntegralHeight = false;
        EditorChrome.StyleField(_links);
    }

    private Control BuildStatsCard()
    {
        Panel card = EditorChrome.MakePanel(DockStyle.Top, height: 148, padding: new Padding(10, 8, 10, 8));
        card.Name = "NoteStatsCard";
        card.BackColor = EditorChrome.Raised;
        TableLayoutPanel grid = new()
        {
            BackColor = Color.Transparent,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 8,
        };
        for (int i = 0; i < 8; i++)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 12.5f));
        }

        StyleStatValue(_statsWords, "0");
        StyleStatValue(_statsHeadings, "0");
        StyleStatValue(_statsLinks, "0");
        StyleStatValue(_statsModified, "—");
        _statsWords.Font = new Font(EditorChrome.BaseFont.FontFamily, 16f, FontStyle.Bold);
        _statsWords.ForeColor = EditorChrome.Text;

        grid.Controls.Add(FieldLabel("Words"), 0, 0);
        grid.Controls.Add(_statsWords, 0, 1);
        grid.Controls.Add(FieldLabel("Headings"), 0, 2);
        grid.Controls.Add(_statsHeadings, 0, 3);
        grid.Controls.Add(FieldLabel("Links"), 0, 4);
        grid.Controls.Add(_statsLinks, 0, 5);
        grid.Controls.Add(FieldLabel("Last modified"), 0, 6);
        grid.Controls.Add(_statsModified, 0, 7);
        card.Controls.Add(grid);

        // Keep legacy label for any older tests that looked for a single stats block.
        _stats.Visible = false;
        _stats.Height = 0;
        return card;
    }

    private static void StyleStatValue(Label label, string text)
    {
        label.AutoSize = false;
        label.Dock = DockStyle.Fill;
        label.Font = EditorChrome.BaseFont;
        label.ForeColor = EditorChrome.Text;
        label.Text = text;
        label.TextAlign = ContentAlignment.TopLeft;
    }

    private void DrawOutlineItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _outline.Items.Count)
        {
            return;
        }

        bool selected = (e.State & DrawItemState.Selected) != 0;
        Color fill = selected
            ? Color.FromArgb(55, EditorChrome.Accent)
            : EditorChrome.Raised;
        using SolidBrush brush = new(fill);
        e.Graphics.FillRectangle(brush, e.Bounds);
        if (selected)
        {
            using SolidBrush accent = new(EditorChrome.Accent);
            e.Graphics.FillRectangle(
                accent,
                new Rectangle(e.Bounds.X, e.Bounds.Y + 3, 3, Math.Max(6, e.Bounds.Height - 6)));
        }

        string text = _outline.Items[e.Index]?.ToString() ?? string.Empty;
        TextRenderer.DrawText(
            e.Graphics,
            text,
            selected ? new Font(EditorChrome.BaseFont, FontStyle.Bold) : EditorChrome.BaseFont,
            new Rectangle(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 14, e.Bounds.Height),
            selected ? EditorChrome.Text : EditorChrome.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    private Control BuildMetadataFields()
    {
        TableLayoutPanel table = new()
        {
            BackColor = EditorChrome.Surface,
            ColumnCount = 1,
            Dock = DockStyle.Top,
            Height = 104,
            Padding = new Padding(0, 6, 0, 8),
            RowCount = 4,
        };
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        table.Controls.Add(FieldLabel("Title"), 0, 0);
        table.Controls.Add(_title, 0, 1);
        table.Controls.Add(FieldLabel("Tags"), 0, 2);
        table.Controls.Add(_tags, 0, 3);
        _title.Dock = DockStyle.Fill;
        _tags.Dock = DockStyle.Fill;
        EditorChrome.StyleField(_title);
        EditorChrome.StyleField(_tags);
        return table;
    }

    private void RefreshDerivedState()
    {
        ApplyMarkdownHighlighting();
        RenderPreview();
        RefreshOutline();
        RefreshLinks();
        RefreshMetadata();
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyMarkdownHighlighting()
    {
        if (_highlightingSource || _source.TextLength == 0) return;

        _highlightingSource = true;
        int selectionStart = _source.SelectionStart;
        int selectionLength = _source.SelectionLength;
        try
        {
            _source.SuspendLayout();
            _source.SelectAll();
            _source.SelectionColor = EditorChrome.Text;

            ColourMatches(HeadingLine(), EditorChrome.Accent);
            ColourMatches(MarkdownLink(), Color.FromArgb(91, 192, 235));
            ColourMatches(InlineToken(), Color.FromArgb(155, 214, 132));
            ColourMatches(MarkdownTaskToken(), Color.FromArgb(225, 181, 90));
            ColourMatches(MarkdownCallout(), Color.FromArgb(196, 151, 255));
        }
        finally
        {
            _source.Select(Math.Clamp(selectionStart, 0, _source.TextLength),
                Math.Clamp(selectionLength, 0, Math.Max(0, _source.TextLength - selectionStart)));
            _source.ResumeLayout();
            _highlightingSource = false;
        }

        void ColourMatches(Regex regex, Color colour)
        {
            foreach (Match match in regex.Matches(_source.Text))
            {
                _source.Select(match.Index, match.Length);
                _source.SelectionColor = colour;
            }
        }
    }

    private void RefreshMetadata()
    {
        _updatingMetadata = true;
        _title.Text = ExtractTitle(_source.Text);
        int words = CountWords(_source.Text);
        int linkCount = MarkdownLink().Matches(_source.Text).Count;
        _statsWords.Text = words.ToString("N0");
        _statsHeadings.Text = _outline.Items.Cast<object>()
            .Count(item => item is NoteHeading { Placeholder: false })
            .ToString("N0");
        _statsLinks.Text = linkCount.ToString("N0");
        _statsModified.Text = File.Exists(ResourcePath)
            ? File.GetLastWriteTime(ResourcePath).ToLocalTime().ToString("d MMM yyyy HH:mm")
            : "Unsaved";
        _stats.Text =
            $"Words: {words:N0}\n" +
            $"Headings: {_statsHeadings.Text}\n" +
            $"Links: {linkCount:N0}";
        _updatingMetadata = false;
    }

    private void RefreshOutline()
    {
        _outline.BeginUpdate();
        _outline.Items.Clear();
        string[] lines = _source.Text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            Match match = HeadingLine().Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            int level = match.Groups[1].Value.Length;
            _outline.Items.Add(new NoteHeading(level, match.Groups[2].Value.Trim(), i));
        }

        if (_outline.Items.Count == 0)
        {
            _outline.Items.Add(new NoteHeading(1, "No headings yet", 0, Placeholder: true));
        }

        _outline.EndUpdate();
    }

    private void RefreshLinks()
    {
        _links.BeginUpdate();
        _links.Items.Clear();
        foreach (NoteAssetReference reference in DetectAssetReferences())
            _links.Items.Add(reference);
        foreach (Match match in MarkdownLink().Matches(_source.Text))
        {
            string label = match.Groups[1].Value.Trim();
            string target = match.Groups[2].Value.Trim();
            if (!_links.Items.Cast<object>().OfType<NoteAssetReference>().Any(item => item.Path.Equals(target, StringComparison.OrdinalIgnoreCase)))
                _links.Items.Add(new NoteAssetReference("↗", "Link", label, target, false));
        }

        if (_links.Items.Count == 0)
        {
            _links.Items.Add("No links detected");
        }

        _links.EndUpdate();
    }

    private void NavigateToSelectedHeading()
    {
        if (_outline.SelectedItem is not NoteHeading { Placeholder: false } heading)
        {
            return;
        }

        int charIndex = 0;
        string[] lines = _source.Text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < heading.Line && i < lines.Length; i++)
        {
            charIndex += lines[i].Length + Environment.NewLine.Length;
        }

        _source.Focus();
        _source.SelectionStart = Math.Clamp(charIndex, 0, _source.TextLength);
        _source.SelectionLength = 0;
    }

    private void ApplyTitle(string title)
    {
        title = string.IsNullOrWhiteSpace(title) ? "Untitled Note" : title.Trim();
        string text = _source.Text.Replace("\r\n", "\n");
        Match match = HeadingLine().Match(text);
        string next = match.Success && match.Index == 0
            ? "# " + title + text[match.Length..]
            : "# " + title + Environment.NewLine + Environment.NewLine + _source.Text;

        if (next == _source.Text)
        {
            return;
        }

        int caret = _source.SelectionStart;
        _source.Text = next;
        _source.SelectionStart = Math.Clamp(caret, 0, _source.TextLength);
        MarkDirty();
        RefreshDerivedState();
    }

    private void RenderPreview()
    {
        _preview.SuspendLayout();
        _preview.Clear();
        bool codeBlock = false;
        foreach (string rawLine in _source.Text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine;
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                codeBlock = !codeBlock;
                if (!codeBlock) _preview.AppendText(Environment.NewLine);
                continue;
            }
            if (codeBlock)
            {
                _preview.SelectionFont = EditorChrome.CodeFont;
                _preview.SelectionColor = Color.FromArgb(155, 214, 132);
                _preview.SelectionBackColor = EditorChrome.Canvas;
                _preview.AppendText("  " + line + Environment.NewLine);
                _preview.SelectionBackColor = _preview.BackColor;
            }
            else if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                Append(line[4..], 11.5f, FontStyle.Bold, EditorChrome.Text);
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Append(line[3..], 13.5f, FontStyle.Bold, EditorChrome.Text);
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                Append(line[2..], 16f, FontStyle.Bold, EditorChrome.Text);
            }
            else if (line.StartsWith("- [ ] ", StringComparison.Ordinal) || line.StartsWith("- [x] ", StringComparison.OrdinalIgnoreCase))
            {
                AppendInline("   " + (line.StartsWith("- [x] ", StringComparison.OrdinalIgnoreCase) ? "☑" : "□") + "  " + line[6..]);
            }
            else if (line.StartsWith("> [!", StringComparison.OrdinalIgnoreCase))
            {
                Append("  " + line.TrimStart('>', ' '), 10.5f, FontStyle.Bold, EditorChrome.Accent);
            }
            else if (line.StartsWith("|", StringComparison.Ordinal) && line.EndsWith("|", StringComparison.Ordinal))
            {
                Append(line, 9.5f, FontStyle.Regular, EditorChrome.Muted);
            }
            else if (line.StartsWith("![", StringComparison.Ordinal) && MarkdownLink().Match(line.TrimStart('!')) is Match { Success: true } image)
            {
                string caption = image.Groups[1].Value;
                string target = image.Groups[2].Value;
                if (!TryAppendPreviewImage(target, caption))
                    Append("🖼  " + caption + "  ·  " + target, 10f, FontStyle.Italic, EditorChrome.Accent);
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                AppendInline("   •  " + line[2..]);
            }
            else if (line.StartsWith("---", StringComparison.Ordinal))
            {
                Append(new string('─', 42), 8f, FontStyle.Regular, EditorChrome.Muted);
            }
            else
            {
                AppendInline(line);
            }
        }

        _preview.ResumeLayout();
        _preview.SelectionStart = 0;
        UpdatePreviewHeight();
    }

    private void UpdateSourceHeight()
    {
        int lineCount = Math.Max(1, _source.Lines.Length);
        int lineHeight = (int)Math.Ceiling(_source.Font.GetHeight());
        int width = Math.Max(120, _sourceHost.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
        _source.Width = width;
        _source.Height = lineCount * lineHeight + lineHeight + 8;
    }

    private void UpdatePreviewHeight()
    {
        int width = Math.Max(120, _previewHost.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
        _preview.Width = width;
        if (_preview.TextLength == 0)
        {
            _preview.Height = (int)Math.Ceiling(_preview.Font.GetHeight()) + 12;
            return;
        }

        int index = Math.Max(0, _preview.TextLength - 1);
        Point bottom = _preview.GetPositionFromCharIndex(index);
        _preview.Height = bottom.Y + (int)Math.Ceiling(_preview.Font.GetHeight() * 1.6f) + 16;
    }

    private void WrapSelection(string before, string after)
    {
        string selected = _source.SelectedText;
        _source.SelectedText = before + selected + after;
        int start = Math.Max(0, _source.SelectionStart - after.Length - selected.Length);
        _source.SelectionStart = start + before.Length;
        _source.SelectionLength = selected.Length;
        _source.Focus();
    }

    private void InsertLink()
    {
        string selected = string.IsNullOrWhiteSpace(_source.SelectedText) ? "link text" : _source.SelectedText;
        _source.SelectedText = $"[{selected}](Assets/)";
        _source.Focus();
    }

    private void InsertAtCaret(string text)
    {
        _source.SelectedText = text;
        _source.Focus();
    }

    private void ApplyResponsiveLayout()
    {
        bool narrow = ClientSize.Width < EditorChrome.NarrowWidthBreakpoint;
        _leftPanel.Visible = !narrow || _viewMode != NoteEditorViewMode.Source;
        _rightPanel.Visible = !narrow || _viewMode == NoteEditorViewMode.Preview;
        _leftPanel.Width = narrow ? EditorChrome.CompactSidePanelWidth : EditorChrome.LeftPanelWidth;
        _rightPanel.Width = narrow ? EditorChrome.CompactSidePanelWidth : EditorChrome.RightPanelWidth;
    }

    private void Append(string text, float size, FontStyle style, Color color)
    {
        _preview.SelectionFont = new Font(EditorChrome.BaseFont.FontFamily, size, style);
        _preview.SelectionColor = color;
        _preview.AppendText(text + Environment.NewLine);
    }

    private void AppendInline(string line)
    {
        int index = 0;
        foreach (Match match in InlineToken().Matches(line))
        {
            if (match.Index > index)
            {
                AppendRun(line[index..match.Index], FontStyle.Regular, EditorChrome.Text, code: false);
            }

            string token = match.Value;
            if (token.StartsWith("**", StringComparison.Ordinal))
            {
                AppendRun(token.Trim('*'), FontStyle.Bold, EditorChrome.Text, code: false);
            }
            else if (token.StartsWith('`'))
            {
                AppendRun(token.Trim('`'), FontStyle.Regular, EditorChrome.Warning, code: true);
            }
            else
            {
                AppendRun(token.Trim('*'), FontStyle.Italic, EditorChrome.Text, code: false);
            }

            index = match.Index + match.Length;
        }

        if (index < line.Length)
        {
            AppendRun(line[index..], FontStyle.Regular, EditorChrome.Text, code: false);
        }

        _preview.AppendText(Environment.NewLine);
    }

    private void AppendRun(string text, FontStyle style, Color color, bool code)
    {
        _preview.SelectionFont = code
            ? new Font(EditorChrome.CodeFont.FontFamily, EditorChrome.BaseFont.SizeInPoints, FontStyle.Regular)
            : new Font(EditorChrome.BaseFont, style);
        _preview.SelectionColor = color;
        _preview.AppendText(text);
    }

    private bool TryAppendPreviewImage(string target, string caption)
    {
        try
        {
            string? path = ResolveMarkdownImagePath(target);
            if (path is null) return false;

            byte[] rgba;
            int width;
            int height;
            if (path.EndsWith(".image.json", StringComparison.OrdinalIgnoreCase))
            {
                ImageDocument document = ImageDocumentSerializer.LoadAtomic(path).Document;
                ImageDocumentSession session = new(document, path, ImageDocumentAccess.Viewer);
                ImageWorkspace workspace = ImageWorkspaceStorage.Load(session);
                rgba = workspace.CompositeCurrentFrame();
                width = workspace.Width;
                height = workspace.Height;
            }
            else
            {
                rgba = Genesis.Shared.Assets.ImageAssetDecoder.DecodeToRgba(path, out width, out height);
            }

            if (rgba.Length != checked(width * height * 4)) return false;
            using Bitmap bitmap = RgbaToBitmap(rgba, width, height);
            int availableWidth = Math.Max(160, _previewHost.ClientSize.Width - 40);
            float scale = Math.Min(1f, Math.Min(availableWidth / (float)width, 300f / height));
            int displayWidth = Math.Max(1, (int)Math.Round(width * scale));
            int displayHeight = Math.Max(1, (int)Math.Round(height * scale));
            _preview.SelectedRtf = BuildImageRtf(bitmap, displayWidth, displayHeight);
            _preview.AppendText(Environment.NewLine);
            if (!string.IsNullOrWhiteSpace(caption))
                Append(caption, 9f, FontStyle.Italic, EditorChrome.Muted);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or ExternalException)
        {
            return false;
        }
    }

    private string? ResolveMarkdownImagePath(string target)
    {
        string value = Uri.UnescapeDataString(target.Trim().Trim('<', '>'));
        int fragment = value.IndexOf('#');
        if (fragment >= 0) value = value[..fragment];
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            if (!uri.IsFile) return null;
            value = uri.LocalPath;
        }

        string? named = ProjectAssetIndex.ResolveSpriteImage(ProjectRoot, value);
        if (named is not null) return named;
        IEnumerable<string> candidates = Path.IsPathRooted(value)
            ? [value]
            : [Path.Combine(Path.GetDirectoryName(ResourcePath) ?? ProjectRoot, value), Path.Combine(ProjectRoot, value)];
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private static Bitmap RgbaToBitmap(byte[] rgba, int width, int height)
    {
        Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        Rectangle bounds = new(0, 0, width, height);
        BitmapData data = bitmap.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] bgra = new byte[rgba.Length];
            for (int index = 0; index < rgba.Length; index += 4)
            {
                bgra[index] = rgba[index + 2];
                bgra[index + 1] = rgba[index + 1];
                bgra[index + 2] = rgba[index];
                bgra[index + 3] = rgba[index + 3];
            }
            Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    private static string BuildImageRtf(Bitmap bitmap, int displayWidth, int displayHeight)
    {
        byte[] dib = BuildDib(bitmap);
        StringBuilder rtf = new(dib.Length * 2 + 128);
        rtf.Append(@"{\rtf1\ansi{\pict\dibitmap0\picw")
            .Append(bitmap.Width).Append(@"\pich").Append(bitmap.Height)
            .Append(@"\picwgoal").Append(displayWidth * 15)
            .Append(@"\pichgoal").Append(displayHeight * 15).Append(' ');
        foreach (byte value in dib) rtf.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return rtf.Append("}}").ToString();
    }

    private static byte[] BuildDib(Bitmap bitmap)
    {
        int rowBytes = checked(bitmap.Width * 4);
        int imageBytes = checked(rowBytes * bitmap.Height);
        byte[] dib = new byte[40 + imageBytes];
        using MemoryStream header = new(dib, writable: true);
        using BinaryWriter writer = new(header, Encoding.ASCII, leaveOpen: true);
        writer.Write(40);
        writer.Write(bitmap.Width);
        writer.Write(bitmap.Height);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(0);
        writer.Write(imageBytes);
        writer.Write(3780);
        writer.Write(3780);
        writer.Write(0);
        writer.Write(0);

        Rectangle bounds = new(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[rowBytes];
            for (int y = 0; y < bitmap.Height; y++)
            {
                IntPtr source = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(source, row, 0, rowBytes);
                Buffer.BlockCopy(row, 0, dib, 40 + (bitmap.Height - 1 - y) * rowBytes, rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return dib;
    }

    protected override void OnChromeChanged()
    {
        base.OnChromeChanged();
        _leftPanel.BackColor = EditorChrome.Surface;
        _rightPanel.BackColor = EditorChrome.Surface;
        _source.BackColor = EditorChrome.Canvas;
        _source.ForeColor = EditorChrome.Text;
        _preview.BackColor = EditorChrome.Surface;
        _preview.ForeColor = EditorChrome.Text;
        _sourceHost.BackColor = EditorChrome.Canvas;
        _previewHost.BackColor = EditorChrome.Surface;
        _sourceHost.ApplyDarkScrollTheme();
        _previewHost.ApplyDarkScrollTheme();
        RenderPreview();
    }

    private static int CountWords(string text) =>
        WordToken().Matches(text).Count;

    private static string ExtractTitle(string text)
    {
        Match match = HeadingLine().Match(text.Replace("\r\n", "\n"));
        return match.Success ? match.Groups[2].Value.Trim() : Path.GetFileNameWithoutExtension("Note");
    }

    private static Label FieldLabel(string text) => new()
    {
        AutoSize = false,
        Dock = DockStyle.Fill,
        Font = EditorChrome.HeadingFont,
        ForeColor = EditorChrome.Muted,
        Text = text.ToUpperInvariant(),
        TextAlign = ContentAlignment.BottomLeft,
    };

    private static ToolStripLabel ToolbarCaption(string text) => new(text)
    {
        AutoSize = true,
        ForeColor = EditorChrome.Muted,
        Margin = new Padding(0, 0, 6, 0),
        Padding = new Padding(0, 2, 0, 0),
    };

    private sealed record NoteHeading(int Level, string Text, int Line, bool Placeholder = false)
    {
        public override string ToString() => Placeholder
            ? Text
            : $"{new string(' ', Math.Max(0, Level - 1) * 2)}{Text}";
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"\*\*[^*]+\*\*|\*[^*]+\*|`[^`]+`|\[[^\]]+\]\([^)]+\)")]
    private static partial Regex InlineToken();

    [GeneratedRegex(@"(?m)^\s*[-*]\s+\[[ xX]\]")]
    private static partial Regex MarkdownTaskToken();

    [GeneratedRegex(@"(?m)^\s*>\s*\[![A-Za-z]+\]")]
    private static partial Regex MarkdownCallout();

    [GeneratedRegex(@"\b[\p{L}\p{N}_'-]+\b")]
    private static partial Regex WordToken();
}
