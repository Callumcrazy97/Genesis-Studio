namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class NoteEditorControl
{
    private readonly Panel _noteWorkspace = new() { Dock = DockStyle.Fill };
    private readonly EditorScrollHost _leftScroll = new(EditorChrome.Surface) { Dock = DockStyle.Fill };
    private readonly EditorScrollHost _rightScroll = new(EditorChrome.Surface) { Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _leftContents = NoteStack();
    private readonly FlowLayoutPanel _rightContents = NoteStack();
    private Panel? _librarySection, _summaryCard, _statsCard;
    private Label? _summaryTitle, _summaryHint;
    private TableLayoutPanel? _statsGrid, _metadataFields;
    private bool _noteLayoutQueued, _layingOutNote;

    private static FlowLayoutPanel NoteStack() => new()
    {
        Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlowDirection = FlowDirection.TopDown, WrapContents = false,
        BackColor = EditorChrome.Surface,
    };

    private static Control NoteSection(string title)
    {
        Control section = EditorChrome.SectionHeader(title); section.Tag = "NoteSection"; return section;
    }

    private void QueueNoteLayout()
    {
        if (_noteLayoutQueued || !IsHandleCreated || IsDisposed) return;
        _noteLayoutQueued = true;
        BeginInvoke((Action)(() => { _noteLayoutQueued = false; if (!IsDisposed) ApplyResponsiveLayout(); }));
    }

    private void ApplyResponsiveLayout()
    {
        if (_layingOutNote || _split is null) return;
        _layingOutNote = true;
        try
        {
            bool narrow = LogicalClientWidth < EditorChrome.NarrowWidthBreakpoint;
            _leftPanel.Visible = _libraryPreference ?? (LogicalClientWidth >= 850 && (!narrow || _viewMode == NoteEditorViewMode.Preview));
            _rightPanel.Visible = _detailsPreference ?? LogicalClientWidth >= 1150;
            float scale = Math.Max(1, DeviceDpi / 96f * EditorChrome.BaseFont.SizeInPoints / 9.5f);
            int maximum = Math.Max(100, ClientSize.Width - 360);
            _leftPanel.Width = Math.Min(maximum, (int)((narrow ? EditorChrome.CompactSidePanelWidth : EditorChrome.LeftPanelWidth) * scale));
            _rightPanel.Width = Math.Min(maximum, (int)((narrow ? EditorChrome.CompactSidePanelWidth : EditorChrome.RightPanelWidth) * scale));
            _libraryToggle.Checked = _leftPanel.Visible; _detailsToggle.Checked = _rightPanel.Visible;
            PerformLayout(); _noteWorkspace.PerformLayout();
            _leftPanel.PerformLayout(); _rightPanel.PerformLayout();
            _leftScroll.PerformLayout(); _rightScroll.PerformLayout();
            _noteLibrary.Font = _outline.Font = _links.Font = EditorChrome.BaseFont;
            _noteLibrary.ItemHeight = _outline.ItemHeight = _links.ItemHeight = EditorChrome.BaseFont.Height + 8;
            _noteSearch.Font = EditorChrome.BaseFont;
            if (_librarySection is not null)
            {
                Button create = _librarySection.Controls.OfType<Button>().Single();
                create.Font = EditorChrome.BaseFont; create.Height = create.Font.Height + 16;
                _librarySection.Height = Math.Max(_noteSearch.Height + create.Height + _noteLibrary.ItemHeight * 3 + 12,
                    Math.Min((int)(250 * scale), _leftScroll.Height * 2 / 5));
            }
            if (_metadataFields is not null)
            {
                _title.Font = _tags.Font = EditorChrome.BaseFont;
                for (int row = 0; row < 4; row++)
                {
                    Control input = _metadataFields.GetControlFromPosition(0, row)!;
                    input.Font = row % 2 == 0 ? EditorChrome.HeadingFont : EditorChrome.BaseFont;
                    _metadataFields.RowStyles[row].Height = input.Font.Height + 8;
                }
                _metadataFields.Height = (int)_metadataFields.RowStyles.Cast<RowStyle>().Sum(row => row.Height) + _metadataFields.Padding.Vertical;
            }
            if (_statsGrid is not null && _statsCard is not null)
            {
                for (int row = 0; row < 8; row++)
                {
                    Label label = (Label)_statsGrid.GetControlFromPosition(0, row)!;
                    label.Font = row % 2 == 0 ? EditorChrome.HeadingFont : EditorChrome.BaseFont;
                    label.TextAlign = ContentAlignment.MiddleLeft;
                    _statsGrid.RowStyles[row].SizeType = SizeType.Absolute;
                    _statsGrid.RowStyles[row].Height = label.Font.Height + 6;
                }
                _statsCard.Height = (int)_statsGrid.RowStyles.Cast<RowStyle>().Sum(row => row.Height) + _statsCard.Padding.Vertical + 4;
            }
            _outline.Height = Math.Clamp(_outline.Items.Count, 2, 8) * _outline.ItemHeight + 8;
            _links.Height = Math.Clamp(_links.Items.Count, 2, 8) * _links.ItemHeight + 8;
            LayoutNoteStack(_leftContents, _leftScroll);
            LayoutNoteStack(_rightContents, _rightScroll);
            if (_summaryCard is not null && _summaryTitle is not null && _summaryHint is not null)
            {
                _summaryTitle.Font = EditorChrome.HeadingFont;
                _summaryTitle.Height = _summaryTitle.Font.Height + 10;
                _summaryHint.Font = EditorChrome.SmallFont;
                int width = Math.Max(80, _summaryCard.Width - _summaryCard.Padding.Horizontal - 8);
                int height = TextRenderer.MeasureText(_summaryHint.Text, _summaryHint.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height;
                _summaryCard.Height = _summaryTitle.Height + height + _summaryCard.Padding.Vertical + 8;
            }
            _sourceHost.PerformLayout(); _previewHost.PerformLayout();
            EditorScrollHost.ApplyDarkScrollTheme(_source); EditorScrollHost.ApplyDarkScrollTheme(_preview);
        }
        finally { _layingOutNote = false; }
    }

    private static void LayoutNoteStack(FlowLayoutPanel stack, Panel scroll)
    {
        stack.Width = Math.Max(80, scroll.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2);
        int width = Math.Max(80, stack.Width - 6);
        foreach (Control child in stack.Controls)
        {
            child.Dock = DockStyle.None; child.Width = width; child.Margin = new Padding(0, 0, 0, 8);
            if (child is Label label) { label.Font = EditorChrome.HeadingFont; label.Height = label.Font.Height + 14; }
            else if (child.Tag as string == "NoteSection") child.Height = EditorChrome.SmallFont.Height + 20;
        }
        stack.PerformLayout();
    }
}
