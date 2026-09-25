using System.Drawing.Drawing2D;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Shared.Assets;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>Visual authoring surface for portable HUD, menu and overlay layouts.</summary>
public sealed class UiEditorControl : EditorSurfaceControl
{
    private readonly UiAssetDocument _document;
    private readonly UiDesignCanvas _canvas = new();
    private readonly TreeView _hierarchy = new();
    private readonly ComboBox _anchor = new UiKit.ThemedComboBox();
    private readonly ComboBox _parent = new UiKit.ThemedComboBox();
    private readonly TextBox _id = new();
    private readonly TextBox _text = new();
    private readonly TextBox _image = new();
    private readonly NumericUpDown _x = Number(-100000, 100000);
    private readonly NumericUpDown _y = Number(-100000, 100000);
    private readonly NumericUpDown _width = Number(1, 100000, 240);
    private readonly NumericUpDown _height = Number(1, 100000, 80);
    private readonly NumericUpDown _fontSize = Number(1, 512, 18);
    private readonly NumericUpDown _value = Number(-100000, 100000, 100);
    private readonly NumericUpDown _maximum = Number(0.001m, 100000, 100);
    private readonly CheckBox _visible = new() { Text = "Visible", AutoSize = true };
    private readonly Button _background = new() { Text = "Background…" };
    private readonly Button _foreground = new() { Text = "Text / Border…" };
    private readonly Label _selectionTitle = new();
    private readonly Label _status = new();
    private bool _syncing;

    public UiEditorControl(string resourcePath, string projectRoot) : base(resourcePath, projectRoot)
    {
        Dock = DockStyle.Fill;
        try { _document = File.Exists(resourcePath) ? UiAssetDocument.Load(resourcePath) : new UiAssetDocument(); }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            _document = new UiAssetDocument();
            LoadWarning = exception.Message;
        }

        _canvas.Document = _document;
        _canvas.SelectionChanged += (_, _) => { SelectHierarchyNode(); RefreshInspector(); };
        _canvas.ElementChanged += (_, _) => { MarkDirty(); RefreshInspector(); };

        Controls.Add(BuildCentre());
        Controls.Add(BuildInspector());
        Controls.Add(BuildLibrary());
        Controls.Add(BuildToolbar());
        Controls.Add(BuildStatus());
        RefreshHierarchy();
        _canvas.SelectedElement = _document.Elements.FirstOrDefault();
        RefreshInspector();
    }

    public UiAssetDocument Document => _document;

    public override void Save()
    {
        NormalizeOrders();
        UiAssetDocument.Save(ResourcePath, _document);
        AcceptSave();
        _status.Text = $"Saved · {_document.Elements.Count} UI element(s)";
    }

    private ToolStrip BuildToolbar()
    {
        ToolStrip toolbar = EditorChrome.MakeToolbar();
        EditorViewportChrome.AttachDocumentMenus(toolbar, this);
        toolbar.Items.Add(new ToolStripLabel("Canvas") { ForeColor = EditorChrome.Muted });
        ToolStripComboBox resolutions = new() { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
        resolutions.Items.AddRange(["1280 × 720", "1920 × 1080", "2560 × 1440", "1080 × 1920"]);
        resolutions.SelectedIndex = ResolutionIndex();
        resolutions.SelectedIndexChanged += (_, _) =>
        {
            (int width, int height) = resolutions.SelectedIndex switch
            {
                1 => (1920, 1080),
                2 => (2560, 1440),
                3 => (1080, 1920),
                _ => (1280, 720),
            };
            _document.DesignWidth = width;
            _document.DesignHeight = height;
            _canvas.Invalidate();
            MarkDirty();
        };
        toolbar.Items.Add(resolutions);
        toolbar.Items.Add(EditorChrome.ToolButton("Fit", "Fit the full interface canvas", () => _canvas.Invalidate()));
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(new ToolStripLabel("Drag elements to move · drag the lower-right handle to resize") { ForeColor = EditorChrome.Muted });
        return toolbar;
    }

    private Control BuildLibrary()
    {
        Panel left = EditorChrome.SidePanel(240, DockStyle.Left);
        _hierarchy.Dock = DockStyle.Fill;
        _hierarchy.BackColor = EditorChrome.Surface;
        _hierarchy.ForeColor = EditorChrome.Text;
        _hierarchy.BorderStyle = BorderStyle.None;
        _hierarchy.HideSelection = false;
        _hierarchy.AfterSelect += (_, args) =>
        {
            if (args.Node?.Tag is UiElement element)
            {
                _canvas.SelectedElement = element;
                RefreshInspector();
            }
        };

        FlowLayoutPanel tools = new()
        {
            Dock = DockStyle.Bottom,
            Height = 184,
            Padding = new Padding(10, 8, 10, 8),
            BackColor = EditorChrome.Surface,
        };
        foreach ((UiElementType type, string label) in new[]
        {
            (UiElementType.Panel, "Panel"),
            (UiElementType.Text, "Text"),
            (UiElementType.Image, "Image"),
            (UiElementType.Button, "Button"),
            (UiElementType.ProgressBar, "Progress Bar"),
        })
        {
            Button button = new() { Text = "+  " + label, Width = 102, Height = 30, FlatStyle = FlatStyle.Flat };
            button.FlatAppearance.BorderColor = EditorChrome.Border;
            button.BackColor = EditorChrome.Raised;
            button.ForeColor = EditorChrome.Text;
            button.Click += (_, _) => AddElement(type);
            tools.Controls.Add(button);
        }

        left.Controls.Add(_hierarchy);
        left.Controls.Add(tools);
        left.Controls.Add(EditorChrome.SectionLabel("ELEMENTS"));
        return left;
    }

    private Control BuildCentre()
    {
        Panel centre = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Canvas, Padding = new Padding(20) };
        _canvas.Dock = DockStyle.Fill;
        centre.Controls.Add(_canvas);
        return centre;
    }

    private Control BuildInspector()
    {
        Panel right = EditorChrome.SidePanel(310, DockStyle.Right);
        Panel scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = EditorChrome.Surface };
        TableLayoutPanel fields = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            Padding = new Padding(12),
        };
        _selectionTitle.AutoSize = false;
        _selectionTitle.Height = 38;
        _selectionTitle.Font = new Font(EditorChrome.BaseFont, FontStyle.Bold);
        _selectionTitle.ForeColor = EditorChrome.Text;
        _selectionTitle.TextAlign = ContentAlignment.MiddleLeft;
        fields.Controls.Add(_selectionTitle);
        fields.Controls.Add(Field("ID", _id));
        fields.Controls.Add(Field("Parent", _parent));
        fields.Controls.Add(Field("Anchor", _anchor));
        fields.Controls.Add(Pair("Position", _x, _y, "X", "Y"));
        fields.Controls.Add(Pair("Size", _width, _height, "W", "H"));
        fields.Controls.Add(Field("Text", _text));
        fields.Controls.Add(Field("Font Size", _fontSize));

        FlowLayoutPanel imageRow = new() { Dock = DockStyle.Top, Height = 36, WrapContents = false };
        _image.Width = 180;
        Button browse = new() { Text = "Pick…", Width = 70, Height = 28, FlatStyle = FlatStyle.Flat };
        browse.Click += (_, _) => PickImage();
        imageRow.Controls.Add(_image);
        imageRow.Controls.Add(browse);
        fields.Controls.Add(Field("Image Resource", imageRow));
        fields.Controls.Add(Pair("Progress", _value, _maximum, "Value", "Max"));

        FlowLayoutPanel colors = new() { Dock = DockStyle.Top, Height = 38, WrapContents = false };
        foreach (Button button in new[] { _background, _foreground })
        {
            button.Width = 127;
            button.Height = 29;
            button.FlatStyle = FlatStyle.Flat;
            colors.Controls.Add(button);
        }
        fields.Controls.Add(Field("Colours", colors));
        fields.Controls.Add(_visible);

        FlowLayoutPanel order = new() { Dock = DockStyle.Top, Height = 40, WrapContents = false };
        Button moveBack = new() { Text = "Move Back", Width = 127, Height = 30, FlatStyle = FlatStyle.Flat };
        Button moveFront = new() { Text = "Move Front", Width = 127, Height = 30, FlatStyle = FlatStyle.Flat };
        moveBack.Click += (_, _) => MoveSelected(-1);
        moveFront.Click += (_, _) => MoveSelected(1);
        order.Controls.Add(moveBack);
        order.Controls.Add(moveFront);
        fields.Controls.Add(Field("Draw Order", order));
        Button delete = new() { Text = "Delete Element", Height = 34, Dock = DockStyle.Top, FlatStyle = FlatStyle.Flat };
        delete.ForeColor = EditorChrome.Error;
        delete.Click += (_, _) => DeleteSelected();
        fields.Controls.Add(delete);

        foreach (Control control in new Control[] { _id, _text, _image, _x, _y, _width, _height, _fontSize, _value, _maximum, _parent, _anchor, _visible })
            EditorChrome.StyleField(control);
        _parent.DropDownStyle = ComboBoxStyle.DropDownList;
        _anchor.DropDownStyle = ComboBoxStyle.DropDownList;
        _anchor.DataSource = Enum.GetValues<UiAnchor>();
        _id.TextChanged += (_, _) => ApplyInspector();
        _text.TextChanged += (_, _) => ApplyInspector();
        _image.TextChanged += (_, _) => ApplyInspector();
        foreach (NumericUpDown number in new[] { _x, _y, _width, _height, _fontSize, _value, _maximum })
            number.ValueChanged += (_, _) => ApplyInspector();
        _parent.SelectedIndexChanged += (_, _) => ApplyInspector();
        _anchor.SelectedIndexChanged += (_, _) => ApplyInspector();
        _visible.CheckedChanged += (_, _) => ApplyInspector();
        _background.Click += (_, _) => PickColor(background: true);
        _foreground.Click += (_, _) => PickColor(background: false);

        scroll.Controls.Add(fields);
        right.Controls.Add(scroll);
        right.Controls.Add(EditorChrome.SectionLabel("CONTEXTUAL INSPECTOR"));
        return right;
    }

    private Control BuildStatus()
    {
        _status.Dock = DockStyle.Bottom;
        _status.Height = 23;
        _status.Padding = new Padding(10, 4, 0, 0);
        _status.BackColor = EditorChrome.Surface;
        _status.ForeColor = EditorChrome.Muted;
        _status.Text = "UI layouts render through DrawUi() in a DrawGui event.";
        return _status;
    }

    private void AddElement(UiElementType type)
    {
        int count = _document.Elements.Count(element => element.Type == type) + 1;
        UiElement element = new()
        {
            Id = UniqueId(type + count.ToString()),
            Type = type,
            Text = type switch
            {
                UiElementType.Text => "Text",
                UiElementType.Button => "Button",
                _ => string.Empty,
            },
            Width = type == UiElementType.ProgressBar ? 320f : type == UiElementType.Text ? 180f : 240f,
            Height = type == UiElementType.ProgressBar ? 28f : type == UiElementType.Text ? 42f : 80f,
            X = 40 + (_document.Elements.Count % 8) * 18,
            Y = 40 + (_document.Elements.Count % 8) * 18,
            Order = _document.Elements.Count,
        };
        _document.Elements.Add(element);
        _canvas.SelectedElement = element;
        RefreshHierarchy();
        RefreshInspector();
        MarkDirty();
    }

    private void DeleteSelected()
    {
        UiElement? element = _canvas.SelectedElement;
        if (element is null) return;
        _document.Elements.Remove(element);
        foreach (UiElement child in _document.Elements.Where(candidate =>
                     string.Equals(candidate.ParentId, element.Id, StringComparison.OrdinalIgnoreCase)))
            child.ParentId = string.Empty;
        _canvas.SelectedElement = _document.Elements.FirstOrDefault();
        RefreshHierarchy();
        RefreshInspector();
        MarkDirty();
    }

    private void MoveSelected(int direction)
    {
        if (_canvas.SelectedElement is not UiElement selected) return;
        List<UiElement> ordered = _document.Elements.OrderBy(element => element.Order).ToList();
        int from = ordered.IndexOf(selected);
        int to = Math.Clamp(from + direction, 0, ordered.Count - 1);
        if (from < 0 || from == to) return;
        ordered.RemoveAt(from);
        ordered.Insert(to, selected);
        _document.Elements.Clear();
        _document.Elements.AddRange(ordered);
        NormalizeOrders();
        RefreshHierarchy();
        _canvas.Invalidate();
        MarkDirty();
    }

    private void RefreshHierarchy()
    {
        _hierarchy.BeginUpdate();
        _hierarchy.Nodes.Clear();
        Dictionary<string, TreeNode> nodes = new(StringComparer.OrdinalIgnoreCase);
        foreach (UiElement element in _document.Elements.OrderBy(element => element.Order))
            nodes[element.Id] = new TreeNode($"{Glyph(element.Type)}  {element.Id}") { Tag = element };
        foreach (UiElement element in _document.Elements.OrderBy(element => element.Order))
        {
            TreeNode node = nodes[element.Id];
            if (!string.IsNullOrWhiteSpace(element.ParentId) && nodes.TryGetValue(element.ParentId, out TreeNode? parent))
                parent.Nodes.Add(node);
            else _hierarchy.Nodes.Add(node);
        }
        _hierarchy.ExpandAll();
        _hierarchy.EndUpdate();
        SelectHierarchyNode();
    }

    private void SelectHierarchyNode()
    {
        UiElement? selected = _canvas.SelectedElement;
        if (selected is null) { _hierarchy.SelectedNode = null; return; }
        foreach (TreeNode node in AllNodes(_hierarchy.Nodes))
            if (ReferenceEquals(node.Tag, selected)) { _hierarchy.SelectedNode = node; return; }
    }

    private void RefreshInspector()
    {
        UiElement? element = _canvas.SelectedElement;
        _syncing = true;
        try
        {
            _selectionTitle.Text = element is null ? "No selection" : element.Type + " · " + element.Id;
            foreach (Control control in new Control[] { _id, _text, _image, _x, _y, _width, _height, _fontSize, _value, _maximum, _parent, _anchor, _visible, _background, _foreground })
                control.Enabled = element is not null;
            if (element is null) return;
            string parentSelection = string.IsNullOrWhiteSpace(element.ParentId) ? "(Canvas)" : element.ParentId;
            _parent.Items.Clear();
            _parent.Items.Add("(Canvas)");
            foreach (UiElement candidate in _document.Elements
                         .Where(candidate => !ReferenceEquals(candidate, element) && !IsDescendant(candidate, element))
                         .OrderBy(candidate => candidate.Order))
                _parent.Items.Add(candidate.Id);
            _parent.SelectedItem = _parent.Items.Contains(parentSelection) ? parentSelection : "(Canvas)";
            _id.Text = element.Id;
            _text.Text = element.Text;
            _image.Text = element.Image;
            _x.Value = Clamp(_x, element.X);
            _y.Value = Clamp(_y, element.Y);
            _width.Value = Clamp(_width, element.Width);
            _height.Value = Clamp(_height, element.Height);
            _fontSize.Value = Clamp(_fontSize, element.FontSize);
            _value.Value = Clamp(_value, element.Value);
            _maximum.Value = Clamp(_maximum, Math.Max(0.001f, element.Maximum));
            _anchor.SelectedItem = element.Anchor;
            _visible.Checked = element.Visible;
            _background.BackColor = ParseColor(element.Background, EditorChrome.Raised);
            _foreground.BackColor = ParseColor(element.Foreground, EditorChrome.Text);
        }
        finally { _syncing = false; }
    }

    private void ApplyInspector()
    {
        if (_syncing || _canvas.SelectedElement is not UiElement element) return;
        string oldId = element.Id;
        string requested = string.IsNullOrWhiteSpace(_id.Text) ? oldId : _id.Text.Trim();
        if (!string.Equals(requested, oldId, StringComparison.OrdinalIgnoreCase)
            && _document.Elements.Any(candidate => !ReferenceEquals(candidate, element)
                && string.Equals(candidate.Id, requested, StringComparison.OrdinalIgnoreCase)))
            requested = oldId;
        element.Id = requested;
        if (!string.Equals(oldId, element.Id, StringComparison.Ordinal))
            foreach (UiElement child in _document.Elements.Where(candidate => string.Equals(candidate.ParentId, oldId, StringComparison.OrdinalIgnoreCase))) child.ParentId = element.Id;
        element.Text = _text.Text;
        element.ParentId = _parent.SelectedItem is string parent && parent != "(Canvas)" ? parent : string.Empty;
        element.Image = _image.Text.Trim();
        element.X = (float)_x.Value;
        element.Y = (float)_y.Value;
        element.Width = (float)_width.Value;
        element.Height = (float)_height.Value;
        element.FontSize = (float)_fontSize.Value;
        element.Value = (float)_value.Value;
        element.Maximum = (float)_maximum.Value;
        element.Anchor = _anchor.SelectedItem is UiAnchor anchor ? anchor : UiAnchor.TopLeft;
        element.Visible = _visible.Checked;
        _canvas.Invalidate();
        RefreshHierarchy();
        MarkDirty();
    }

    private bool IsDescendant(UiElement candidate, UiElement ancestor)
    {
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        string parentId = candidate.ParentId;
        while (!string.IsNullOrWhiteSpace(parentId) && visited.Add(parentId))
        {
            if (string.Equals(parentId, ancestor.Id, StringComparison.OrdinalIgnoreCase)) return true;
            parentId = _document.Elements.FirstOrDefault(element =>
                string.Equals(element.Id, parentId, StringComparison.OrdinalIgnoreCase))?.ParentId ?? string.Empty;
        }
        return false;
    }

    private void PickImage()
    {
        ProjectAssetEntry? picked = AssetPickerService.PickImage(ProjectRoot, FindForm(), _image.Text);
        if (picked is not null) _image.Text = picked.Reference;
    }

    private void PickColor(bool background)
    {
        UiElement? element = _canvas.SelectedElement;
        if (element is null) return;
        using ColorDialog dialog = new() { FullOpen = true, Color = ParseColor(background ? element.Background : element.Foreground, Color.White) };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string value = "#" + dialog.Color.ToArgb().ToString("X8");
        if (background) element.Background = value; else element.Foreground = value;
        RefreshInspector();
        _canvas.Invalidate();
        MarkDirty();
    }

    private string UniqueId(string requested)
    {
        string id = requested;
        int suffix = 2;
        while (_document.Elements.Any(element => string.Equals(element.Id, id, StringComparison.OrdinalIgnoreCase))) id = requested + suffix++;
        return id;
    }

    private void NormalizeOrders()
    {
        for (int i = 0; i < _document.Elements.Count; i++) _document.Elements[i].Order = i;
    }

    private int ResolutionIndex() => (_document.DesignWidth, _document.DesignHeight) switch
    {
        (1920, 1080) => 1,
        (2560, 1440) => 2,
        (1080, 1920) => 3,
        _ => 0,
    };

    private static NumericUpDown Number(decimal minimum, decimal maximum, decimal value = 0) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        Value = value,
        DecimalPlaces = 1,
        Increment = 1,
        Dock = DockStyle.Fill,
    };

    private static decimal Clamp(NumericUpDown control, float value) => Math.Clamp((decimal)value, control.Minimum, control.Maximum);

    private static Control Field(string label, Control control)
    {
        Panel panel = new() { Dock = DockStyle.Top, Height = 62, Margin = new Padding(0, 0, 0, 5) };
        control.Dock = DockStyle.Bottom;
        control.Height = 31;
        panel.Controls.Add(control);
        panel.Controls.Add(new Label { Text = label, Dock = DockStyle.Top, Height = 25, ForeColor = EditorChrome.Muted, TextAlign = ContentAlignment.BottomLeft });
        return panel;
    }

    private static Control Pair(string title, Control left, Control right, string leftLabel, string rightLabel)
    {
        TableLayoutPanel pair = new() { Dock = DockStyle.Fill, ColumnCount = 2 };
        pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        pair.Controls.Add(Field(leftLabel, left), 0, 0);
        pair.Controls.Add(Field(rightLabel, right), 1, 0);
        Panel host = new() { Dock = DockStyle.Top, Height = 88 };
        host.Controls.Add(pair);
        host.Controls.Add(new Label { Text = title, Dock = DockStyle.Top, Height = 22, ForeColor = EditorChrome.Text });
        return host;
    }

    private static IEnumerable<TreeNode> AllNodes(TreeNodeCollection roots)
    {
        foreach (TreeNode node in roots)
        {
            yield return node;
            foreach (TreeNode child in AllNodes(node.Nodes)) yield return child;
        }
    }

    private static string Glyph(UiElementType type) => type switch
    {
        UiElementType.Text => "T",
        UiElementType.Image => "▧",
        UiElementType.Button => "▣",
        UiElementType.ProgressBar => "▬",
        _ => "□",
    };

    internal static Color ParseColor(string value, Color fallback)
    {
        string hex = (value ?? string.Empty).Trim().TrimStart('#');
        try { return hex.Length == 8 ? Color.FromArgb(unchecked((int)Convert.ToUInt32(hex, 16))) : ColorTranslator.FromHtml("#" + hex); }
        catch { return fallback; }
    }
}

internal sealed class UiDesignCanvas : Control
{
    private UiElement? _selected;
    private Point _dragStart;
    private float _elementStartX;
    private float _elementStartY;
    private float _elementStartWidth;
    private float _elementStartHeight;
    private bool _resizing;

    public UiAssetDocument Document { get; set; } = new();
    public UiElement? SelectedElement
    {
        get => _selected;
        set { _selected = value; SelectionChanged?.Invoke(this, EventArgs.Empty); Invalidate(); }
    }

    public event EventHandler? SelectionChanged;
    public event EventHandler? ElementChanged;

    public UiDesignCanvas()
    {
        DoubleBuffered = true;
        Cursor = Cursors.Default;
        BackColor = Color.FromArgb(18, 21, 27);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        RectangleF canvas = CanvasRect();
        using SolidBrush canvasBrush = new(Color.FromArgb(28, 34, 44));
        e.Graphics.FillRectangle(canvasBrush, canvas);
        using Pen grid = new(Color.FromArgb(38, 105, 130, 158));
        for (float x = canvas.Left; x < canvas.Right; x += Math.Max(8f, canvas.Width / 32f)) e.Graphics.DrawLine(grid, x, canvas.Top, x, canvas.Bottom);
        for (float y = canvas.Top; y < canvas.Bottom; y += Math.Max(8f, canvas.Height / 18f)) e.Graphics.DrawLine(grid, canvas.Left, y, canvas.Right, y);

        Dictionary<string, RectangleF> elementRects = BuildElementRects(canvas);
        foreach (UiElement element in Document.Elements.OrderBy(element => element.Order))
        {
            if (!element.Visible) continue;
            if (!elementRects.TryGetValue(element.Id, out RectangleF rect)) continue;
            Color background = UiEditorControl.ParseColor(element.Background, Color.FromArgb(204, 22, 27, 34));
            Color foreground = UiEditorControl.ParseColor(element.Foreground, Color.White);
            Color accent = UiEditorControl.ParseColor(element.Accent, Color.FromArgb(108, 140, 255));
            switch (element.Type)
            {
                case UiElementType.Panel:
                    using (SolidBrush brush = new(background)) e.Graphics.FillRectangle(brush, rect);
                    break;
                case UiElementType.Text:
                    TextRenderer.DrawText(e.Graphics, element.Text, Font, Rectangle.Round(rect), foreground, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    break;
                case UiElementType.Image:
                    using (SolidBrush brush = new(Color.FromArgb(42, accent))) e.Graphics.FillRectangle(brush, rect);
                    using (Pen pen = new(accent)) { e.Graphics.DrawRectangle(pen, Rectangle.Round(rect)); e.Graphics.DrawLine(pen, rect.Left, rect.Top, rect.Right, rect.Bottom); e.Graphics.DrawLine(pen, rect.Right, rect.Top, rect.Left, rect.Bottom); }
                    TextRenderer.DrawText(e.Graphics, string.IsNullOrWhiteSpace(element.Image) ? "Choose Image" : ResourceDisplayName.Format(element.Image), Font, Rectangle.Round(rect), foreground, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    break;
                case UiElementType.Button:
                    using (SolidBrush brush = new(background)) e.Graphics.FillRectangle(brush, rect);
                    using (Pen pen = new(accent, 1.5f)) e.Graphics.DrawRectangle(pen, Rectangle.Round(rect));
                    TextRenderer.DrawText(e.Graphics, element.Text, Font, Rectangle.Round(rect), foreground, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    break;
                case UiElementType.ProgressBar:
                    using (SolidBrush brush = new(background)) e.Graphics.FillRectangle(brush, rect);
                    float amount = Math.Clamp(element.Value / Math.Max(0.001f, element.Maximum), 0f, 1f);
                    using (SolidBrush brush = new(accent)) e.Graphics.FillRectangle(brush, new RectangleF(rect.X, rect.Y, rect.Width * amount, rect.Height));
                    break;
            }
            if (ReferenceEquals(element, _selected))
            {
                using Pen selected = new(Color.FromArgb(68, 180, 255), 2f) { DashStyle = DashStyle.Dash };
                e.Graphics.DrawRectangle(selected, Rectangle.Round(rect));
                e.Graphics.FillRectangle(Brushes.White, rect.Right - 5, rect.Bottom - 5, 10, 10);
            }
        }
        using Pen border = new(Color.FromArgb(95, 110, 130));
        e.Graphics.DrawRectangle(border, Rectangle.Round(canvas));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        RectangleF canvas = CanvasRect();
        Dictionary<string, RectangleF> elementRects = BuildElementRects(canvas);
        UiElement? hit = Document.Elements.OrderByDescending(element => element.Order)
            .FirstOrDefault(element => element.Visible
                && elementRects.TryGetValue(element.Id, out RectangleF rect)
                && rect.Contains(e.Location));
        SelectedElement = hit;
        if (hit is null) return;
        RectangleF rect = elementRects[hit.Id];
        _resizing = new RectangleF(rect.Right - 14, rect.Bottom - 14, 18, 18).Contains(e.Location);
        _dragStart = e.Location;
        _elementStartX = hit.X;
        _elementStartY = hit.Y;
        _elementStartWidth = hit.Width;
        _elementStartHeight = hit.Height;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!Capture || _selected is null || e.Button != MouseButtons.Left) return;
        RectangleF canvas = CanvasRect();
        float dx = (e.X - _dragStart.X) * Document.DesignWidth / Math.Max(1f, canvas.Width);
        float dy = (e.Y - _dragStart.Y) * Document.DesignHeight / Math.Max(1f, canvas.Height);
        if (_resizing)
        {
            _selected.Width = Math.Max(1f, _elementStartWidth + dx);
            _selected.Height = Math.Max(1f, _elementStartHeight + dy);
        }
        else
        {
            bool rightAnchored = _selected.Anchor is UiAnchor.TopRight or UiAnchor.Right or UiAnchor.BottomRight;
            bool bottomAnchored = _selected.Anchor is UiAnchor.BottomLeft or UiAnchor.Bottom or UiAnchor.BottomRight;
            _selected.X = _elementStartX + (rightAnchored ? -dx : dx);
            _selected.Y = _elementStartY + (bottomAnchored ? -dy : dy);
        }
        ElementChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); Capture = false; }

    private RectangleF CanvasRect()
    {
        float availableWidth = Math.Max(1, ClientSize.Width - 40);
        float availableHeight = Math.Max(1, ClientSize.Height - 40);
        float scale = Math.Min(availableWidth / Math.Max(1, Document.DesignWidth), availableHeight / Math.Max(1, Document.DesignHeight));
        float width = Document.DesignWidth * scale;
        float height = Document.DesignHeight * scale;
        return new RectangleF((ClientSize.Width - width) * 0.5f, (ClientSize.Height - height) * 0.5f, width, height);
    }

    private Dictionary<string, RectangleF> BuildElementRects(RectangleF canvas)
    {
        float sx = canvas.Width / Math.Max(1, Document.DesignWidth);
        float sy = canvas.Height / Math.Max(1, Document.DesignHeight);
        Dictionary<string, UiElement> elements = Document.Elements
            .Where(element => !string.IsNullOrWhiteSpace(element.Id))
            .GroupBy(element => element.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, RectangleF> designRects = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, RectangleF> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (UiElement element in Document.Elements)
        {
            RectangleF design = ResolveDesignRect(element, elements, designRects, []);
            result[element.Id] = new RectangleF(
                canvas.X + design.X * sx,
                canvas.Y + design.Y * sy,
                design.Width * sx,
                design.Height * sy);
        }
        return result;
    }

    private RectangleF ResolveDesignRect(
        UiElement element,
        IReadOnlyDictionary<string, UiElement> elements,
        IDictionary<string, RectangleF> cache,
        HashSet<string> visiting)
    {
        if (cache.TryGetValue(element.Id, out RectangleF cached)) return cached;
        RectangleF parent = new(0, 0, Document.DesignWidth, Document.DesignHeight);
        if (!string.IsNullOrWhiteSpace(element.ParentId)
            && elements.TryGetValue(element.ParentId, out UiElement? parentElement)
            && visiting.Add(element.Id))
        {
            parent = ResolveDesignRect(parentElement, elements, cache, visiting);
            visiting.Remove(element.Id);
        }
        RectangleF result = AnchorRect(parent, element);
        cache[element.Id] = result;
        return result;
    }

    private static RectangleF AnchorRect(RectangleF parent, UiElement element)
    {
        float width = Math.Max(1f, element.Width);
        float height = Math.Max(1f, element.Height);
        float x = parent.X + element.X;
        float y = parent.Y + element.Y;
        switch (element.Anchor)
        {
            case UiAnchor.Top: x = parent.X + (parent.Width - width) * 0.5f + element.X; break;
            case UiAnchor.TopRight: x = parent.Right - width - element.X; break;
            case UiAnchor.Left: y = parent.Y + (parent.Height - height) * 0.5f + element.Y; break;
            case UiAnchor.Center:
                x = parent.X + (parent.Width - width) * 0.5f + element.X;
                y = parent.Y + (parent.Height - height) * 0.5f + element.Y;
                break;
            case UiAnchor.Right:
                x = parent.Right - width - element.X;
                y = parent.Y + (parent.Height - height) * 0.5f + element.Y;
                break;
            case UiAnchor.BottomLeft: y = parent.Bottom - height - element.Y; break;
            case UiAnchor.Bottom:
                x = parent.X + (parent.Width - width) * 0.5f + element.X;
                y = parent.Bottom - height - element.Y;
                break;
            case UiAnchor.BottomRight:
                x = parent.Right - width - element.X;
                y = parent.Bottom - height - element.Y;
                break;
            case UiAnchor.Stretch:
                width = Math.Max(1f, parent.Width - element.X - element.Width);
                height = Math.Max(1f, parent.Height - element.Y - element.Height);
                break;
        }
        return new RectangleF(x, y, width, height);
    }
}
