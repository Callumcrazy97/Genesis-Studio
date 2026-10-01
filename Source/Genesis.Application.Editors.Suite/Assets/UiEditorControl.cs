using System.Drawing.Drawing2D;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Shared.Assets;
using Genesis.Application.Core.Images;
using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>Visual authoring surface for portable HUD, menu and overlay layouts.</summary>
public sealed partial class UiEditorControl : EditorSurfaceControl, IResourceInspectorTarget, ILiveResourceInspectorTarget
{
    private UiAssetDocument _document;
    private readonly UiDesignCanvas _canvas = new();
    private readonly TreeView _hierarchy = new();
    private readonly ComboBox _anchor = new UiKit.ThemedComboBox();
    private readonly ComboBox _parent = new UiKit.ThemedComboBox();
    private readonly TextBox _id = new();
    private readonly TextBox _text = new();
    private readonly TextBox _image = new();
    private readonly ComboBox _font = new UiKit.ThemedComboBox();
    private readonly NumericUpDown _imageScaleX = Number(.001m, 100, 1);
    private readonly NumericUpDown _imageScaleY = Number(.001m, 100, 1);
    private readonly NumericUpDown _opacity = Number(0, 1, 1);
    private readonly NumericUpDown _x = Number(-100000, 100000);
    private readonly NumericUpDown _y = Number(-100000, 100000);
    private readonly NumericUpDown _width = Number(1, 100000, 240);
    private readonly NumericUpDown _height = Number(1, 100000, 80);
    private readonly NumericUpDown _fontSize = Number(1, 512, 18);
    private readonly NumericUpDown _value = Number(-100000, 100000, 100);
    private readonly NumericUpDown _maximum = Number(0.001m, 100000, 100);
    private readonly CheckBox _visible = new() { Text = "Visible", AutoSize = true };
    private readonly Button _background = new() { Text = "Background…" };
    private readonly Button _foreground = new() { Text = "Text…" };
    private readonly Button _accent = new() { Text = "Accent…" };
    private readonly Label _selectionTitle = new();
    private readonly Label _status = new();
    private bool _syncing;
    private bool _loadFailed;
    private string _journalState = string.Empty;
    private string? _journalSelection;
    private Panel? _libraryDock;
    private Panel? _detailsDock;
    private ToolStripComboBox? _resolutions;

    public UiEditorControl(string resourcePath, string projectRoot) : base(resourcePath, projectRoot)
    {
        Dock = DockStyle.Fill;
        try { _document = File.Exists(resourcePath) ? UiAssetDocument.Load(resourcePath) : new UiAssetDocument(); }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            _document = new UiAssetDocument();
            LoadWarning = exception.Message;
            _loadFailed = true;
        }

        _canvas.Document = _document;
        _canvas.ProjectRoot = projectRoot;
        _canvas.SelectionChanged += (_, _) =>
        { SelectHierarchyNode(); RefreshInspector(); InspectorStateChanged?.Invoke(this, EventArgs.Empty); };
        _canvas.ElementChanged += (_, _) => { MarkDirty(); RefreshInspector(); };
        _canvas.ElementEditCommitted += (_, _) => JournalChange("Move or resize UI element");
        _canvas.ViewChanged += (_, _) => UpdateCanvasStatus();

        _workspace.Controls.Add(BuildCentre());
        _workspace.Controls.Add(BuildInspector());
        _workspace.Controls.Add(BuildLibrary());
        Controls.Add(_workspace);
        Controls.Add(BuildToolbar());
        Controls.Add(BuildStatus());
        BuildUiWorkflowBar(_commandBar!);
        RefreshHierarchy();
        _canvas.SelectedElement = _document.Elements.FirstOrDefault();
        RefreshInspector();
        _journalState = UiAssetDocument.Serialize(_document);
        _journalSelection = _canvas.SelectedElement?.Id;
        InitializeUiWorkflow();
        SizeChanged += (_, _) => { ApplyResponsiveLayout(); QueueUiLayout(); };
        ApplyResponsiveLayout();
    }

    public UiAssetDocument Document => _document;

    public event EventHandler? InspectorStateChanged;

    public bool SelectElement(string id)
    {
        UiElement? element = _document.Elements.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        _canvas.SelectedElement = element;
        _journalSelection = element?.Id;
        return element is not null;
    }

    public override void Save()
    {
        if (_loadFailed) throw new InvalidDataException("The original UI resource could not be loaded; its contents have not been replaced. " + LoadWarning);
        foreach (UiElement element in _document.Elements.Where(item => !string.IsNullOrWhiteSpace(item.Image)))
            if (!File.Exists(ProjectAssetIndex.ResolveReference(ProjectRoot, element.Image, ResourceKind.Image)))
                throw new InvalidDataException($"Image '{element.Image}' on '{element.Id}' does not resolve to a project sprite.");
        NormalizeOrders();
        ProjectAssetWriteRegistry.MarkLocalWrite(ResourcePath);
        UiAssetDocument.Save(ResourcePath, _document);
        AcceptSave();
        _status.Text = $"Saved · {_document.Elements.Count} UI element(s)";
    }

    private ToolStrip BuildToolbar()
        => BuildUiWorkflowToolbar();

    private Control BuildLibrary()
    {
        Panel left = _libraryDock = EditorChrome.SidePanel(240, DockStyle.Left);
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

        left.Controls.Add(_hierarchy);
        left.Controls.Add(EditorChrome.SectionLabel("UI ELEMENTS"));
        return left;
    }

    private Control BuildCentre()
    {
        Panel centre = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Canvas, Padding = new Padding(20) };
        _canvas.Dock = DockStyle.Fill;
        centre.Controls.Add(_canvas);
        centre.Controls.Add(BuildUiCanvasToolbar());
        return centre;
    }

    private Control BuildInspector()
    {
        Panel right = _detailsDock = EditorChrome.SidePanel(310, DockStyle.Right);
        Panel scroll = new EditorScrollHost(EditorChrome.Surface) { Dock = DockStyle.Fill };
        TableLayoutPanel fields = _inspectorFields = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            Padding = new Padding(12),
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _selectionTitle.AutoSize = false;
        _selectionTitle.Dock = DockStyle.Fill;
        _selectionTitle.AutoEllipsis = true;
        _selectionTitle.Height = 38;
        _selectionTitle.Font = new Font(EditorChrome.BaseFont, FontStyle.Bold);
        _selectionTitle.ForeColor = EditorChrome.Text;
        _selectionTitle.TextAlign = ContentAlignment.MiddleLeft;
        fields.Controls.Add(_selectionTitle);
        fields.Controls.Add(Field("ID", _id));
        fields.Controls.Add(Field("Parent", _parent));
        fields.Controls.Add(Field("Anchor", _anchor));
        fields.Controls.Add(Pair("Position", _x, _y, "X", "Y"));
        fields.Controls.Add(_sizeRow = Pair("Size", _width, _height, "W", "H"));
        fields.Controls.Add(_textRow = Field("Text", _text));
        fields.Controls.Add(_fontRow = Field("Font family", _font));
        fields.Controls.Add(_fontSizeRow = Field("Font size", _fontSize));

        TableLayoutPanel imageRow = new() { Dock = DockStyle.Top, Height = 36, ColumnCount = 2, RowCount = 1 };
        imageRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 75));
        imageRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        imageRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _image.Dock = DockStyle.Fill;
        Button browse = new() { Text = "Pick…", Dock = DockStyle.Fill, Height = 28, FlatStyle = FlatStyle.Flat };
        browse.Click += (_, _) => PickImage();
        imageRow.Controls.Add(_image);
        imageRow.Controls.Add(browse);
        fields.Controls.Add(_imageRow = Field("Image", imageRow));
        fields.Controls.Add(_imageScaleRow = Pair("Image scale", _imageScaleX, _imageScaleY, "X", "Y"));
        _opacity.DecimalPlaces = 2; _opacity.Increment = .05m;
        fields.Controls.Add(_opacityRow = Field("Opacity", _opacity));
        fields.Controls.Add(_progressRow = Pair("Progress", _value, _maximum, "Value", "Max"));

        foreach (Button button in new[] { _background, _foreground, _accent }) button.FlatStyle = FlatStyle.Flat;
        fields.Controls.Add(_backgroundRow = Field("Background", _background));
        fields.Controls.Add(_foregroundRow = Field("Text colour", _foreground));
        fields.Controls.Add(_accentRow = Field("Accent colour", _accent));
        fields.Controls.Add(_visible);

        TableLayoutPanel order = new() { Dock = DockStyle.Top, Height = 40, ColumnCount = 2 };
        order.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        order.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        order.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Button moveBack = new() { Text = "Move Back", Dock = DockStyle.Fill, Height = 30, FlatStyle = FlatStyle.Flat };
        Button moveFront = new() { Text = "Move Front", Dock = DockStyle.Fill, Height = 30, FlatStyle = FlatStyle.Flat };
        moveBack.Click += (_, _) => MoveSelected(-1);
        moveFront.Click += (_, _) => MoveSelected(1);
        order.Controls.Add(moveBack);
        order.Controls.Add(moveFront);
        fields.Controls.Add(Field("Draw Order", order));
        Button delete = new() { Text = "Delete Element", Height = 34, Dock = DockStyle.Top, FlatStyle = FlatStyle.Flat };
        delete.ForeColor = EditorChrome.Error;
        delete.Click += (_, _) => DeleteSelected();
        fields.Controls.Add(delete);

        foreach (Control control in new Control[] { _id, _text, _image, _font, _imageScaleX, _imageScaleY, _opacity, _x, _y, _width, _height, _fontSize, _value, _maximum, _parent, _anchor, _visible })
            EditorChrome.StyleField(control);
        _font.DropDownStyle = ComboBoxStyle.DropDown;
        _font.Items.AddRange(System.Drawing.FontFamily.Families.Select(item => (object)item.Name).ToArray());
        _font.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _font.AutoCompleteSource = AutoCompleteSource.ListItems;
        _parent.DropDownStyle = ComboBoxStyle.DropDownList;
        _anchor.DropDownStyle = ComboBoxStyle.DropDownList;
        _anchor.DataSource = Enum.GetValues<UiAnchor>();
        _id.TextChanged += (_, _) => ApplyInspector();
        _text.TextChanged += (_, _) => ApplyInspector();
        _image.TextChanged += (_, _) => ApplyInspector();
        _font.TextChanged += (_, _) => ApplyInspector();
        foreach (NumericUpDown number in new[] { _x, _y, _width, _height, _fontSize, _imageScaleX, _imageScaleY, _opacity, _value, _maximum })
            number.ValueChanged += (_, _) => ApplyInspector();
        _parent.SelectedIndexChanged += (_, _) => ApplyInspector();
        _anchor.SelectedIndexChanged += (_, _) => ApplyInspector();
        _visible.CheckedChanged += (_, _) => ApplyInspector();
        _background.Click += (_, _) => PickColor("Background");
        _foreground.Click += (_, _) => PickColor("Foreground");
        _accent.Click += (_, _) => PickColor("Accent");

        scroll.Controls.Add(fields);
        right.Controls.Add(scroll);
        right.Controls.Add(EditorChrome.SectionLabel("SELECTED ELEMENT"));
        return right;
    }

    private Control BuildStatus()
    {
        _status.Dock = DockStyle.Bottom;
        _status.Height = 23;
        _status.Padding = new Padding(10, 4, 0, 0);
        _status.BackColor = EditorChrome.Surface;
        _status.ForeColor = EditorChrome.Muted;
        UpdateCanvasStatus();
        return _status;
    }

    public void AddElement(UiElementType type)
    {
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
        ShowUiDesign();
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
        JournalChange("Add UI element");
    }

    public void DeleteSelected()
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
        JournalChange("Delete UI element");
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
        JournalChange("Reorder UI element");
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
            SetContextualFields(element);
            foreach (Control control in new Control[] { _id, _text, _image, _font, _imageScaleX, _imageScaleY, _x, _y, _width, _height, _fontSize, _value, _maximum, _parent, _anchor, _visible, _background, _foreground, _accent })
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
            _font.Text = element.Font;
            _imageScaleX.Value = Clamp(_imageScaleX, element.ImageScaleX);
            _imageScaleY.Value = Clamp(_imageScaleY, element.ImageScaleY);
            _opacity.Value = ParseColor(element.Foreground, Color.White).A / 255m;
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
            _accent.BackColor = ParseColor(element.Accent, EditorChrome.Accent);
            UpdateColourButtons();
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
        if (HasText(element.Type))
        {
            element.Text = _text.Text;
            element.FontSize = (float)_fontSize.Value;
            if (!string.IsNullOrWhiteSpace(_font.Text)) element.Font = _font.Text.Trim();
        }
        element.ParentId = _parent.SelectedItem is string parent && parent != "(Canvas)" ? parent : string.Empty;
        if (element.Type == UiElementType.Image)
        {
            element.Image = _image.Text.Trim();
            element.ImageScaleX = (float)_imageScaleX.Value;
            element.ImageScaleY = (float)_imageScaleY.Value;
            element.Foreground = "#" + Color.FromArgb((int)Math.Round(_opacity.Value * 255), ParseColor(element.Foreground, Color.White)).ToArgb().ToString("X8");
        }
        element.X = (float)_x.Value;
        element.Y = (float)_y.Value;
        element.Width = (float)_width.Value;
        element.Height = (float)_height.Value;
        if (element.Type == UiElementType.ProgressBar)
        {
            element.Value = (float)_value.Value;
            element.Maximum = (float)_maximum.Value;
        }
        element.Anchor = _anchor.SelectedItem is UiAnchor anchor ? anchor : UiAnchor.TopLeft;
        if (element.Anchor != UiAnchor.Stretch)
        {
            element.Width = Math.Max(1, element.Width); element.Height = Math.Max(1, element.Height);
        }
        SetContextualFields(element);
        element.Visible = _visible.Checked;
        _canvas.Invalidate();
        RefreshHierarchy();
        JournalChange("Edit UI properties");
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

    private void PickColor(string property)
    {
        UiElement? element = _canvas.SelectedElement;
        if (element is null) return;
        string original = property switch { "Background" => element.Background, "Foreground" => element.Foreground, _ => element.Accent };
        using ColorDialog dialog = new() { FullOpen = true, Color = ParseColor(original, Color.White) };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string value = "#" + dialog.Color.ToArgb().ToString("X8");
        if (property == "Background") element.Background = value;
        else if (property == "Foreground") element.Foreground = value;
        else element.Accent = value;
        RefreshInspector();
        _canvas.Invalidate();
        JournalChange("Change UI colour");
    }

    private void JournalChange(string label)
    {
        if (_syncing || _journalState.Length == 0) return;
        string after = UiAssetDocument.Serialize(_document);
        string before = _journalState;
        if (before == after) return;
        string? beforeSelection = _journalSelection;
        string? afterSelection = _canvas.SelectedElement?.Id;
        _journalState = after;
        _journalSelection = afterSelection;
        PushEdit(label, () => RestoreDocument(after, afterSelection), () => RestoreDocument(before, beforeSelection), maximumEntries: 100);
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RestoreDocument(string json, string? selection)
    {
        _syncing = true;
        try
        {
            _document = UiAssetDocument.Deserialize(json);
            _journalState = json;
            _journalSelection = selection;
            _canvas.Document = _document;
            if (_resolutions is not null) _resolutions.SelectedIndex = ResolutionIndex();
            _canvas.SelectedElement = _document.Elements.FirstOrDefault(item => item.Id == selection);
            RefreshHierarchy();
            RefreshInspector();
            _canvas.Invalidate();
        }
        finally { _syncing = false; }
        RefreshUiWorkflow();
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnAssetDependenciesChanged(ProjectAssetChangeSet changes)
    {
        _canvas.ReloadImages();
        base.OnAssetDependenciesChanged(changes);
    }

    public IReadOnlyList<ResourceInspectorLiveValue> GetLiveInspectorValues()
    {
        if (_canvas.SelectedElement is not UiElement element) return [];
        ResourceInspectorLiveValue[] values =
        [
            new("UI element", "Ui.Id", "ID", element.Id),
            new("UI element", "Ui.Text", "Text", element.Text),
            new("UI element", "Ui.Image", "Image", element.Image, AssetKind: ResourceKind.Image),
            new("UI element", "Ui.Anchor", "Anchor", element.Anchor.ToString(), Choices: Enum.GetNames<UiAnchor>()),
            new("UI element", "Ui.Parent", "Parent", element.ParentId, Choices: new[] { string.Empty }
                .Concat(_document.Elements.Where(item => !ReferenceEquals(item, element) && !IsDescendant(item, element)).Select(item => item.Id)).ToArray()),
            new("UI element", "Ui.FontSize", "Font size", element.FontSize, Minimum: 1, Maximum: 512),
            new("UI element", "Ui.Font", "Font family", element.Font, Choices: System.Drawing.FontFamily.Families.Select(item => item.Name).ToArray()),
            new("UI element", "Ui.ImageScaleX", "Image scale X", element.ImageScaleX, Minimum: .001m, Maximum: 100),
            new("UI element", "Ui.ImageScaleY", "Image scale Y", element.ImageScaleY, Minimum: .001m, Maximum: 100),
            new("UI element", "Ui.Opacity", "Opacity", ParseColor(element.Foreground, Color.White).A / 255f, Minimum: 0, Maximum: 1),
            new("UI element", "Ui.X", "X", element.X, Minimum: -100000, Maximum: 100000),
            new("UI element", "Ui.Y", "Y", element.Y, Minimum: -100000, Maximum: 100000),
            new("UI element", "Ui.Width", element.Anchor == UiAnchor.Stretch ? "Right inset" : "Width", element.Width, Minimum: element.Anchor == UiAnchor.Stretch ? 0 : 1, Maximum: 100000),
            new("UI element", "Ui.Height", element.Anchor == UiAnchor.Stretch ? "Bottom inset" : "Height", element.Height, Minimum: element.Anchor == UiAnchor.Stretch ? 0 : 1, Maximum: 100000),
            new("UI element", "Ui.Value", "Value", element.Value, Minimum: -100000, Maximum: 100000),
            new("UI element", "Ui.Maximum", "Maximum", element.Maximum, Minimum: .001m, Maximum: 100000),
            new("UI element", "Ui.Visible", "Visible", element.Visible),
            new("UI element", "Ui.Background", "Background colour", element.Background),
            new("UI element", "Ui.Foreground", element.Type == UiElementType.ProgressBar ? "Border colour" : "Text colour", element.Foreground),
            new("UI element", "Ui.Accent", "Accent colour", element.Accent),
        ];
        return values.Where(value => IsApplicable(element.Type, value.PropertyPath)).ToArray();
    }

    public bool TryApplyInspectorValue(string propertyPath, object? value) => TryApplyLiveInspectorValue(propertyPath, value);

    public bool TryApplyLiveInspectorValue(string propertyPath, object? value)
    {
        if (_canvas.SelectedElement is not UiElement element || !IsApplicable(element.Type, propertyPath)) return false;
        string text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        bool number = float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float scalar) && float.IsFinite(scalar);
        switch (propertyPath)
        {
            case "Ui.Text": element.Text = text; break;
            case "Ui.Image": element.Image = text; break;
            case "Ui.Anchor" when Enum.TryParse(text, out UiAnchor anchor) && Enum.IsDefined(anchor): element.Anchor = anchor; break;
            case "Ui.Parent" when string.IsNullOrWhiteSpace(text) || _document.Elements.Any(item => item.Id.Equals(text, StringComparison.OrdinalIgnoreCase)
                && !ReferenceEquals(item, element) && !IsDescendant(item, element)): element.ParentId = text; break;
            case "Ui.FontSize" when number && scalar is >= 1 and <= 512: element.FontSize = scalar; break;
            case "Ui.Font" when !string.IsNullOrWhiteSpace(text): element.Font = text; break;
            case "Ui.ImageScaleX" when number && scalar is > 0 and <= 100: element.ImageScaleX = scalar; break;
            case "Ui.ImageScaleY" when number && scalar is > 0 and <= 100: element.ImageScaleY = scalar; break;
            case "Ui.Opacity" when number && scalar is >= 0 and <= 1:
                element.Foreground = "#" + Color.FromArgb((int)MathF.Round(scalar * 255), ParseColor(element.Foreground, Color.White)).ToArgb().ToString("X8");
                break;
            case "Ui.X" when number: element.X = scalar; break;
            case "Ui.Y" when number: element.Y = scalar; break;
            case "Ui.Width" when number && (scalar > 0 || scalar == 0 && element.Anchor == UiAnchor.Stretch): element.Width = scalar; break;
            case "Ui.Height" when number && (scalar > 0 || scalar == 0 && element.Anchor == UiAnchor.Stretch): element.Height = scalar; break;
            case "Ui.Value" when number: element.Value = scalar; break;
            case "Ui.Maximum" when number && scalar > 0: element.Maximum = scalar; break;
            case "Ui.Visible" when bool.TryParse(text, out bool visible): element.Visible = visible; break;
            case "Ui.Background" when IsColour(text): element.Background = text; break;
            case "Ui.Foreground" when IsColour(text): element.Foreground = text; break;
            case "Ui.Accent" when IsColour(text): element.Accent = text; break;
            case "Ui.Id" when !string.IsNullOrWhiteSpace(text) && !_document.Elements.Any(item => !ReferenceEquals(item, element) && item.Id.Equals(text, StringComparison.OrdinalIgnoreCase)):
                string previous = element.Id;
                element.Id = text;
                foreach (UiElement child in _document.Elements.Where(item => item.ParentId.Equals(previous, StringComparison.OrdinalIgnoreCase))) child.ParentId = text;
                break;
            default: return false;
        }
        if (element.Anchor != UiAnchor.Stretch)
        {
            element.Width = Math.Max(1, element.Width); element.Height = Math.Max(1, element.Height);
        }
        JournalChange("Edit UI properties");
        RefreshHierarchy();
        RefreshInspector();
        _canvas.Invalidate();
        return true;
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

    private int ResolutionIndex()
    {
        Size size = new(_document.DesignWidth, _document.DesignHeight);
        int index = _resolutionSizes.IndexOf(size);
        if (index >= 0) return index;
        _resolutionSizes.Add(size);
        _resolutions?.Items.Add($"{size.Width} × {size.Height}");
        return _resolutionSizes.Count - 1;
    }

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
        control.Dock = DockStyle.None;
        control.Height = 31;
        Label caption = new() { Text = label, Height = 25, ForeColor = EditorChrome.Muted, TextAlign = ContentAlignment.BottomLeft };
        panel.Tag = new UiFieldLayout(caption, control);
        panel.Controls.Add(control);
        panel.Controls.Add(caption);
        return panel;
    }

    private static Control Pair(string title, Control left, Control right, string leftLabel, string rightLabel)
    {
        TableLayoutPanel pair = new() { ColumnCount = 2, RowCount = 1 };
        pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        pair.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Control leftField = Field(leftLabel, left), rightField = Field(rightLabel, right);
        pair.Controls.Add(leftField, 0, 0);
        pair.Controls.Add(rightField, 1, 0);
        Panel host = new() { Dock = DockStyle.Top, Height = 88 };
        Label caption = new() { Text = title, Height = 22, ForeColor = EditorChrome.Text };
        host.Tag = new UiPairLayout(caption, pair, leftField, rightField);
        host.Controls.Add(pair);
        host.Controls.Add(caption);
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

    private static bool IsColour(string text) => text.StartsWith('#') && text.Length is 7 or 9
        && text.AsSpan(1).ToArray().All(Uri.IsHexDigit);

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
    private bool _editing, _panning;
    private PointF _pan;
    private PointF _panStart;
    public float ViewZoom { get; private set; } = 1;
    private readonly Dictionary<string, (long Stamp, Bitmap Bitmap)> _images = new(StringComparer.OrdinalIgnoreCase);

    public string ProjectRoot { get; set; } = string.Empty;

    public UiAssetDocument Document { get; set; } = new();
    public UiElement? SelectedElement
    {
        get => _selected;
        set { _selected = value; SelectionChanged?.Invoke(this, EventArgs.Empty); Invalidate(); }
    }

    public event EventHandler? SelectionChanged;
    public event EventHandler? ElementChanged;
    public event EventHandler? ElementEditCommitted;
    public event EventHandler? ViewChanged;

    public void FitView()
    {
        ViewZoom = 1; _pan = PointF.Empty;
        Invalidate(); ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public UiDesignCanvas()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
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
            if (!IsVisible(element)) continue;
            if (!elementRects.TryGetValue(element.Id, out RectangleF rect)) continue;
            Color background = UiEditorControl.ParseColor(element.Background, Color.FromArgb(204, 22, 27, 34));
            Color foreground = UiEditorControl.ParseColor(element.Foreground, Color.White);
            Color accent = UiEditorControl.ParseColor(element.Accent, Color.FromArgb(108, 140, 255));
            using Font elementFont = new(string.IsNullOrWhiteSpace(element.Font) ? "Segoe UI" : element.Font,
                Math.Max(1f, element.FontSize * canvas.Width / Math.Max(1, Document.DesignWidth)), GraphicsUnit.Pixel);
            switch (element.Type)
            {
                case UiElementType.Panel:
                    using (SolidBrush brush = new(background)) e.Graphics.FillRectangle(brush, rect);
                    break;
                case UiElementType.Text:
                    TextRenderer.DrawText(e.Graphics, element.Text, elementFont, Rectangle.Round(rect), foreground, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    break;
                case UiElementType.Image:
                    Bitmap? image = LoadImage(element.Image);
                    if (image is not null)
                    {
                        using System.Drawing.Imaging.ImageAttributes attributes = new();
                        attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = foreground.A / 255f });
                        e.Graphics.DrawImage(image,
                            Rectangle.Round(new RectangleF(rect.X, rect.Y, rect.Width * element.ImageScaleX, rect.Height * element.ImageScaleY)),
                            0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
                        break;
                    }
                    using (SolidBrush brush = new(Color.FromArgb(42, accent))) e.Graphics.FillRectangle(brush, rect);
                    using (Pen pen = new(accent)) { e.Graphics.DrawRectangle(pen, Rectangle.Round(rect)); e.Graphics.DrawLine(pen, rect.Left, rect.Top, rect.Right, rect.Bottom); e.Graphics.DrawLine(pen, rect.Right, rect.Top, rect.Left, rect.Bottom); }
                    TextRenderer.DrawText(e.Graphics, string.IsNullOrWhiteSpace(element.Image) ? "Choose Image" : ResourceDisplayName.Format(element.Image), Font, Rectangle.Round(rect), foreground, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    break;
                case UiElementType.Button:
                    using (SolidBrush brush = new(background)) e.Graphics.FillRectangle(brush, rect);
                    using (Pen pen = new(accent, 1.5f)) e.Graphics.DrawRectangle(pen, Rectangle.Round(rect));
                    TextRenderer.DrawText(e.Graphics, element.Text, elementFont, Rectangle.Round(rect), foreground, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    break;
                case UiElementType.ProgressBar:
                    using (SolidBrush brush = new(background)) e.Graphics.FillRectangle(brush, rect);
                    float amount = Math.Clamp(element.Value / Math.Max(0.001f, element.Maximum), 0f, 1f);
                    using (SolidBrush brush = new(accent)) e.Graphics.FillRectangle(brush, new RectangleF(rect.X, rect.Y, rect.Width * amount, rect.Height));
                    using (Pen pen = new(foreground)) e.Graphics.DrawRectangle(pen, Rectangle.Round(rect));
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
        Focus();
        if (e.Button == MouseButtons.Middle)
        {
            _panning = true; _panStart = _pan; _dragStart = e.Location;
            Capture = true; Cursor = Cursors.SizeAll; return;
        }
        if (e.Button != MouseButtons.Left) return;
        RectangleF canvas = CanvasRect();
        Dictionary<string, RectangleF> elementRects = BuildElementRects(canvas);
        bool selectedHandle = _selected is not null && IsVisible(_selected) && elementRects.TryGetValue(_selected.Id, out RectangleF selectedRect)
            && new RectangleF(selectedRect.Right - 8, selectedRect.Bottom - 8, 16, 16).Contains(e.Location);
        UiElement? hit = selectedHandle ? _selected : Document.Elements.OrderByDescending(element => element.Order)
            .FirstOrDefault(element => IsVisible(element)
                && elementRects.TryGetValue(element.Id, out RectangleF rect)
                && rect.Contains(e.Location));
        SelectedElement = hit;
        if (hit is null) return;
        RectangleF rect = elementRects[hit.Id];
        _resizing = selectedHandle || new RectangleF(rect.Right - 14, rect.Bottom - 14, 18, 18).Contains(e.Location);
        _dragStart = e.Location;
        _elementStartX = hit.X;
        _elementStartY = hit.Y;
        _elementStartWidth = hit.Width;
        _elementStartHeight = hit.Height;
        _editing = true;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_panning && Capture)
        {
            _pan = new PointF(_panStart.X + e.X - _dragStart.X, _panStart.Y + e.Y - _dragStart.Y);
            Invalidate(); return;
        }
        if (!_editing || !Capture || _selected is null || e.Button != MouseButtons.Left) return;
        RectangleF canvas = CanvasRect();
        float dx = (e.X - _dragStart.X) * Document.DesignWidth / Math.Max(1f, canvas.Width);
        float dy = (e.Y - _dragStart.Y) * Document.DesignHeight / Math.Max(1f, canvas.Height);
        if (_resizing)
        {
            if (_selected.Anchor == UiAnchor.Stretch)
            {
                _selected.Width = Math.Max(0, _elementStartWidth - dx);
                _selected.Height = Math.Max(0, _elementStartHeight - dy);
            }
            else
            {
                _selected.Width = Math.Max(1f, _elementStartWidth + dx);
                _selected.Height = Math.Max(1f, _elementStartHeight + dy);
                float widthChange = _selected.Width - _elementStartWidth, heightChange = _selected.Height - _elementStartHeight;
                _selected.X = _elementStartX + (_selected.Anchor is UiAnchor.TopRight or UiAnchor.Right or UiAnchor.BottomRight
                    ? -widthChange : _selected.Anchor is UiAnchor.Top or UiAnchor.Center or UiAnchor.Bottom ? widthChange / 2 : 0);
                _selected.Y = _elementStartY + (_selected.Anchor is UiAnchor.BottomLeft or UiAnchor.Bottom or UiAnchor.BottomRight
                    ? -heightChange : _selected.Anchor is UiAnchor.Left or UiAnchor.Center or UiAnchor.Right ? heightChange / 2 : 0);
            }
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

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture)
        {
            _panning = false; Cursor = Cursors.Default;
            if (_editing) { _editing = false; ElementEditCommitted?.Invoke(this, EventArgs.Empty); }
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_editing || _panning) return;
        RectangleF before = CanvasRect();
        float u = (e.X - before.X) / before.Width, v = (e.Y - before.Y) / before.Height;
        ViewZoom = Math.Clamp(ViewZoom * MathF.Pow(1.2f, e.Delta / 120f), .2f, 16);
        RectangleF after = CanvasRect();
        _pan = new PointF(_pan.X + e.X - after.X - u * after.Width, _pan.Y + e.Y - after.Y - v * after.Height);
        Invalidate(); ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool IsVisible(UiElement element)
    {
        if (!element.Visible) return false;
        string parent = element.ParentId;
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        while (!string.IsNullOrWhiteSpace(parent) && seen.Add(parent))
        {
            UiElement? ancestor = Document.Elements.FirstOrDefault(item => item.Id.Equals(parent, StringComparison.OrdinalIgnoreCase));
            if (ancestor is null || !ancestor.Visible) return false;
            parent = ancestor.ParentId;
        }
        return true;
    }

    private Bitmap? LoadImage(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        string path = ProjectAssetIndex.ResolveReference(ProjectRoot, reference, ResourceKind.Image);
        if (!File.Exists(path)) return null;
        long stamp = File.GetLastWriteTimeUtc(path).Ticks;
        if (_images.TryGetValue(path, out var cached) && cached.Stamp == stamp) return cached.Bitmap;
        try
        {
            ImageDocument document = ImageDocumentSerializer.LoadAtomic(path).Document;
            ImageDocumentSession session = new(document, path, ImageDocumentAccess.Viewer);
            ImageWorkspace workspace = ImageWorkspaceStorage.Load(session);
            byte[] rgba = workspace.CompositeCurrentFrame();
            Bitmap bitmap = new(workspace.Width, workspace.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                byte[] bgra = new byte[rgba.Length];
                for (int i = 0; i < rgba.Length; i += 4)
                { bgra[i] = rgba[i + 2]; bgra[i + 1] = rgba[i + 1]; bgra[i + 2] = rgba[i]; bgra[i + 3] = rgba[i + 3]; }
                System.Runtime.InteropServices.Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
            }
            finally { bitmap.UnlockBits(data); }
            if (_images.Remove(path, out var previous)) previous.Bitmap.Dispose();
            _images[path] = (stamp, bitmap);
            return bitmap;
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        { return null; }
    }

    public void ReloadImages()
    {
        foreach (var image in _images.Values) image.Bitmap.Dispose();
        _images.Clear();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ReloadImages();
        base.Dispose(disposing);
    }

    private RectangleF CanvasRect()
    {
        float availableWidth = Math.Max(1, ClientSize.Width - 40);
        float availableHeight = Math.Max(1, ClientSize.Height - 40);
        float scale = Math.Min(availableWidth / Math.Max(1, Document.DesignWidth), availableHeight / Math.Max(1, Document.DesignHeight));
        float width = Document.DesignWidth * scale * ViewZoom;
        float height = Document.DesignHeight * scale * ViewZoom;
        return new RectangleF((ClientSize.Width - width) * 0.5f + _pan.X, (ClientSize.Height - height) * 0.5f + _pan.Y, width, height);
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
