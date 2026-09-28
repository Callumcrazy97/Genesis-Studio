using Genesis.Shared.Assets;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class UiEditorControl
{
    private readonly Panel _workspace = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Canvas };
    private readonly List<Size> _resolutionSizes = [new(1280, 720), new(1920, 1080), new(2560, 1440), new(1080, 1920)];
    private TableLayoutPanel? _inspectorFields;
    private Control? _sizeRow, _textRow, _fontRow, _fontSizeRow, _imageRow, _imageScaleRow, _progressRow;
    private Control? _backgroundRow, _foregroundRow, _accentRow;
    private Control? _opacityRow;
    private bool? _libraryPreference, _detailsPreference;
    private bool _uiLayoutQueued, _layingOutUi;
    private sealed record UiFieldLayout(Label Caption, Control Input);
    private sealed record UiPairLayout(Label Title, TableLayoutPanel Columns, Control Left, Control Right);

    private static bool HasText(UiElementType type) => type is UiElementType.Text or UiElementType.Button;

    private void SetContextualFields(UiElement? element)
    {
        UiElementType? type = element?.Type;
        foreach (Control? row in new[] { _textRow, _fontRow, _fontSizeRow })
            if (row is not null) row.Visible = type.HasValue && HasText(type.Value);
        foreach (Control? row in new[] { _imageRow, _imageScaleRow, _opacityRow })
            if (row is not null) row.Visible = type == UiElementType.Image;
        if (_progressRow is not null) _progressRow.Visible = type == UiElementType.ProgressBar;
        if (_backgroundRow is not null) _backgroundRow.Visible = type is UiElementType.Panel or UiElementType.Button or UiElementType.ProgressBar;
        if (_foregroundRow is not null)
        {
            _foregroundRow.Visible = type is UiElementType.Text or UiElementType.Button or UiElementType.ProgressBar;
            if (_foregroundRow.Tag is UiFieldLayout foreground) foreground.Caption.Text = type == UiElementType.ProgressBar ? "Border colour" : "Text colour";
            _foreground.Text = type == UiElementType.ProgressBar ? "Border…" : "Text…";
        }
        if (_accentRow is not null) _accentRow.Visible = type is UiElementType.Button or UiElementType.ProgressBar;
        bool stretch = element?.Anchor == UiAnchor.Stretch;
        bool syncing = _syncing;
        _syncing = true;
        try { _width.Minimum = _height.Minimum = stretch ? 0 : 1; }
        finally { _syncing = syncing; }
        if (_sizeRow?.Tag is UiPairLayout size)
        {
            size.Title.Text = stretch ? "Insets" : "Size";
            ((UiFieldLayout)size.Left.Tag!).Caption.Text = stretch ? "Right" : "Width";
            ((UiFieldLayout)size.Right.Tag!).Caption.Text = stretch ? "Bottom" : "Height";
        }
        QueueUiLayout();
    }

    private void UpdateColourButtons()
    {
        foreach (Button button in new[] { _background, _foreground, _accent })
        {
            Color colour = button.BackColor;
            float alpha = colour.A / 255f;
            Color visible = Color.FromArgb((int)(colour.R * alpha + EditorChrome.Surface.R * (1 - alpha)),
                (int)(colour.G * alpha + EditorChrome.Surface.G * (1 - alpha)), (int)(colour.B * alpha + EditorChrome.Surface.B * (1 - alpha)));
            button.ForeColor = visible.GetBrightness() > .55f ? Color.Black : Color.White;
            button.FlatAppearance.BorderColor = EditorChrome.Border;
        }
    }

    private void ToggleSidebar(bool library)
    {
        if (library && _libraryDock is not null)
        {
            _libraryPreference = !_libraryDock.Visible;
            if (_libraryPreference == true && LogicalClientWidth < 1050) _detailsPreference = false;
        }
        else if (_detailsDock is not null)
        {
            _detailsPreference = !_detailsDock.Visible;
            if (_detailsPreference == true && LogicalClientWidth < 1050) _libraryPreference = false;
        }
        ApplyResponsiveLayout(); QueueUiLayout();
    }

    protected override void OnChromeChanged()
    {
        base.OnChromeChanged(); QueueUiLayout();
    }

    private void QueueUiLayout()
    {
        if (_uiLayoutQueued || !IsHandleCreated || IsDisposed) return;
        _uiLayoutQueued = true;
        BeginInvoke((Action)(() => { _uiLayoutQueued = false; if (!IsDisposed) ApplyResponsiveLayout(); }));
    }

    private void ApplyResponsiveLayout()
    {
        if (_layingOutUi) return;
        _layingOutUi = true;
        try
        {
            float scale = Math.Max(1, DeviceDpi / 96f * EditorChrome.BaseFont.SizeInPoints / 9.5f);
            if (_libraryDock is not null)
            {
                _libraryDock.Width = (int)Math.Min(240 * scale, ClientSize.Width * .32f);
                _libraryDock.Visible = !_showWorkflowGuide && (_libraryPreference ?? LogicalClientWidth >= 700);
            }
            if (_detailsDock is not null)
            {
                _detailsDock.Width = (int)Math.Min(320 * scale, ClientSize.Width * .46f);
                _detailsDock.Visible = !_showWorkflowGuide && (_detailsPreference ?? LogicalClientWidth >= 1000);
            }
            if (_canvas.Parent is { } centre) centre.Visible = !_showWorkflowGuide;
            PerformLayout();
            _workspace.PerformLayout();
            _libraryDock?.PerformLayout(); _detailsDock?.PerformLayout();
            _hierarchy.Font = EditorChrome.BaseFont;
            _hierarchy.ItemHeight = _hierarchy.Font.Height + 8;
            _selectionTitle.Font = EditorChrome.BaseFont;
            _selectionTitle.Height = _selectionTitle.Font.Height + 18;
            if (_inspectorFields is not null)
            {
                _inspectorFields.Parent?.PerformLayout();
                int width = Math.Max(80, _inspectorFields.ClientSize.Width - _inspectorFields.Padding.Horizontal - 6);
                foreach (Control row in _inspectorFields.Controls)
                {
                    if (row.Tag is UiFieldLayout or UiPairLayout) LayoutProperty(row, width);
                    else if (row is Button button) { button.Font = EditorChrome.BaseFont; button.Height = button.Font.Height + 16; }
                }
                _inspectorFields.PerformLayout();
            }
            _status.Font = EditorChrome.SmallFont;
            _status.Height = _status.Font.Height + 10;
            if (_resolutions is not null)
            {
                _resolutions.Font = EditorChrome.BaseFont;
                _resolutions.ComboBox.Font = EditorChrome.BaseFont;
                if (_resolutions.Owner is { } canvasBar)
                {
                    canvasBar.Font = EditorChrome.BaseFont;
                    canvasBar.MinimumSize = new Size(0, _resolutions.ComboBox.PreferredHeight + 10);
                }
            }
            UpdateColourButtons();
            LayoutUiGuide();
        }
        finally { _layingOutUi = false; }
    }

    private static void LayoutProperty(Control row, int width)
    {
        row.Width = width;
        if (row.Tag is UiFieldLayout field)
        {
            field.Caption.Font = EditorChrome.SmallFont;
            field.Caption.Bounds = new Rectangle(0, 0, width, field.Caption.Font.Height + 6);
            field.Input.Font = EditorChrome.BaseFont;
            field.Input.Bounds = new Rectangle(0, field.Caption.Bottom + 3, width, field.Input.Font.Height + 14);
            row.Height = field.Input.Bottom + 5;
        }
        else if (row.Tag is UiPairLayout pair)
        {
            pair.Title.Font = EditorChrome.SmallFont;
            pair.Title.Bounds = new Rectangle(0, 0, width, pair.Title.Font.Height + 6);
            int half = Math.Max(1, (width - 12) / 2);
            LayoutProperty(pair.Left, half); LayoutProperty(pair.Right, half);
            pair.Left.Margin = new Padding(0, 0, 6, 0);
            pair.Right.Margin = new Padding(6, 0, 0, 0);
            pair.Columns.Bounds = new Rectangle(0, pair.Title.Bottom + 2, width, Math.Max(pair.Left.Height, pair.Right.Height));
            row.Height = pair.Columns.Bottom + 5;
        }
    }

    private void UpdateCanvasStatus() => _status.Text = _showWorkflowGuide
        ? _workflowGuide?.Name == "UiUseInGame" ? "Save → Create GUI Object → place one instance in a Room → Run" : "Choose a starting layout, or open a blank canvas and Add element"
        : $"View: {_canvas.ViewZoom:P0} of fit · Drag to move · corner handle to resize · Wheel: zoom · middle drag: pan";

    private static bool IsApplicable(UiElementType type, string property) => property switch
    {
        "Ui.Text" or "Ui.Font" or "Ui.FontSize" => HasText(type),
        "Ui.Foreground" => type is UiElementType.Text or UiElementType.Button or UiElementType.ProgressBar,
        "Ui.Image" or "Ui.ImageScaleX" or "Ui.ImageScaleY" or "Ui.Opacity" => type == UiElementType.Image,
        "Ui.Value" or "Ui.Maximum" => type == UiElementType.ProgressBar,
        "Ui.Background" => type is UiElementType.Panel or UiElementType.Button or UiElementType.ProgressBar,
        "Ui.Accent" => type is UiElementType.Button or UiElementType.ProgressBar,
        _ => true,
    };
}
