using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Inspector;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Objects;

/// <summary>Designer-facing ordered Object component stack with a runtime-path live preview.</summary>
public sealed class ObjectCompositionDialog : DpiAwareForm
{
    private sealed record ComponentRow(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly string _projectRoot;
    private readonly ListBox _stack = new();
    private readonly CheckBox _enabled = new();
    private readonly TextBox _asset = new();
    private readonly Button _browseAsset = new();
    private readonly Label _assetLabel = new();
    private readonly DataGridView _properties = new();
    private readonly ToolStripDropDownButton _add = new("＋ Add");
    private readonly ToolStripButton _remove = new("Remove");
    private readonly ToolStripButton _up = new("↑");
    private readonly ToolStripButton _down = new("↓");
    private readonly ToolStripButton _reset = new("Reset");
    private readonly ToolStripButton _copy = new("Copy");
    private readonly ToolStripButton _paste = new("Paste");
    private JObject? _componentClipboard;
    private bool _syncing;

    public ObjectCompositionDialog(JObject document, string projectRoot)
    {
        _projectRoot = projectRoot;
        Document = (JObject)(document ?? new JObject()).DeepClone();
        Composition = new ObjectCompositionModel(Document);
        Preview = new ObjectCompositionPreviewControl(projectRoot);

        Text = "Object Components & Runtime Preview";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(900, 600);
        Size = new Size(1120, 720);
        BackColor = EditorChrome.Canvas;
        ForeColor = EditorChrome.Text;
        Font = EditorChrome.BaseFont;
        ShowInTaskbar = false;

        Controls.Add(BuildButtons());
        Controls.Add(BuildBody());
        RebuildStack();
        Preview.Reload(Document);
    }

    public JObject Document { get; }
    public ObjectCompositionModel Composition { get; }
    public ObjectCompositionPreviewControl Preview { get; }

    private Control BuildBody()
    {
        SplitContainer split = new()
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            IsSplitterFixed = false,
            SplitterDistance = 330,
            BackColor = EditorChrome.Border,
        };

        Panel left = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface };
        ToolStrip tools = EditorChrome.MakeToolbar();
        foreach (ObjectComponentDefinition definition in ObjectCompositionModel.Definitions.Where(item => item.Removable))
        {
            ObjectComponentDefinition captured = definition;
            _add.DropDownItems.Add(definition.DisplayName, null, (_, _) => Add(captured.Type));
        }
        _remove.Click += (_, _) => RemoveSelected();
        _up.ToolTipText = "Move selected component earlier";
        _down.ToolTipText = "Move selected component later";
        _up.Click += (_, _) => MoveSelected(-1);
        _down.Click += (_, _) => MoveSelected(1);
        _reset.ToolTipText = "Restore the selected component's defaults";
        _copy.ToolTipText = "Copy the selected component settings";
        _paste.ToolTipText = "Apply copied component settings";
        _reset.Click += (_, _) => ResetSelectedComponent();
        _copy.Click += (_, _) => CopySelectedComponent();
        _paste.Click += (_, _) => PasteComponent();
        _paste.Enabled = false;
        tools.Items.Add(_add);
        tools.Items.Add(_remove);
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(_up);
        tools.Items.Add(_down);
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(_reset);
        tools.Items.Add(_copy);
        tools.Items.Add(_paste);

        _stack.Dock = DockStyle.Fill;
        _stack.BackColor = EditorChrome.Canvas;
        _stack.ForeColor = EditorChrome.Text;
        _stack.BorderStyle = BorderStyle.None;
        _stack.Font = new Font(EditorChrome.BaseFont, FontStyle.Bold);
        _stack.SelectedIndexChanged += (_, _) => ShowSelection();

        Panel binding = new() { Dock = DockStyle.Bottom, Height = 94, Padding = new Padding(10), BackColor = EditorChrome.Raised };
        _enabled.Text = "Enabled";
        _enabled.AutoSize = true;
        _enabled.Location = new Point(10, 9);
        _enabled.CheckedChanged += (_, _) => CommitEnabled();
        _assetLabel.Text = "Asset";
        _assetLabel.AutoSize = false;
        _assetLabel.ForeColor = EditorChrome.Muted;
        _assetLabel.Location = new Point(10, 40);
        _assetLabel.Size = new Size(62, 22);
        _asset.ReadOnly = true;
        _asset.Location = new Point(72, 37);
        _asset.Width = 194;
        EditorChrome.StyleField(_asset);
        _browseAsset.Text = "…";
        _browseAsset.Location = new Point(272, 36);
        _browseAsset.Size = new Size(36, 28);
        _browseAsset.Click += (_, _) => BrowseAsset();
        EditorChrome.StyleField(_browseAsset);
        binding.Controls.Add(_enabled);
        binding.Controls.Add(_assetLabel);
        binding.Controls.Add(_asset);
        binding.Controls.Add(_browseAsset);

        left.Controls.Add(_stack);
        left.Controls.Add(binding);
        left.Controls.Add(tools);
        split.Panel1.Controls.Add(left);

        Panel right = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Canvas };
        _properties.Dock = DockStyle.Bottom;
        _properties.Height = 235;
        _properties.BackgroundColor = EditorChrome.Surface;
        _properties.BorderStyle = BorderStyle.None;
        _properties.AllowUserToAddRows = false;
        _properties.AllowUserToDeleteRows = false;
        _properties.AllowUserToResizeRows = false;
        _properties.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _properties.RowHeadersVisible = false;
        _properties.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _properties.Columns.Add(new DataGridViewTextBoxColumn { Name = "Property", ReadOnly = true, FillWeight = 42 });
        _properties.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", FillWeight = 58 });
        _properties.CellEndEdit += (_, e) => CommitProperty(e.RowIndex);
        _properties.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_properties.IsCurrentCellDirty) _properties.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _properties.CellValueChanged += (_, e) =>
        {
            if (!_syncing && e.ColumnIndex == _properties.Columns["Value"]?.Index)
                CommitProperty(e.RowIndex);
        };
        _properties.EnableHeadersVisualStyles = false;
        _properties.ColumnHeadersDefaultCellStyle.BackColor = EditorChrome.Raised;
        _properties.ColumnHeadersDefaultCellStyle.ForeColor = EditorChrome.Text;
        _properties.DefaultCellStyle.BackColor = EditorChrome.Surface;
        _properties.DefaultCellStyle.ForeColor = EditorChrome.Text;
        _properties.DefaultCellStyle.SelectionBackColor = EditorChrome.Accent;
        _properties.DefaultCellStyle.SelectionForeColor = Color.White;
        _properties.GridColor = EditorChrome.Border;

        Label previewLabel = new()
        {
            Dock = DockStyle.Top,
            Height = 28,
            Text = "  LIVE RUNTIME PREVIEW — orbit: right-drag · pan: middle-drag · zoom: wheel",
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = EditorChrome.Raised,
            ForeColor = EditorChrome.Muted,
            Font = EditorChrome.SmallFont,
        };
        Label propertyLabel = new()
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            Text = "  COMPONENT PROPERTIES",
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = EditorChrome.Raised,
            ForeColor = EditorChrome.Muted,
            Font = EditorChrome.SmallFont,
        };

        right.Controls.Add(Preview);
        right.Controls.Add(previewLabel);
        right.Controls.Add(_properties);
        right.Controls.Add(propertyLabel);
        Preview.BringToFront();
        split.Panel2.Controls.Add(right);
        return split;
    }

    private Control BuildButtons()
    {
        Panel panel = new() { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(10), BackColor = EditorChrome.Surface };
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Dock = DockStyle.Right, Width = 92 };
        Button accept = new() { Text = "Apply", DialogResult = DialogResult.OK, Dock = DockStyle.Right, Width = 104 };
        EditorChrome.StyleField(cancel);
        EditorChrome.StyleField(accept);
        accept.BackColor = EditorChrome.Accent;
        accept.ForeColor = Color.White;
        panel.Controls.Add(cancel);
        panel.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        panel.Controls.Add(accept);
        AcceptButton = accept;
        CancelButton = cancel;
        return panel;
    }

    private void Add(string type)
    {
        JObject component = Composition.Add(type);
        RebuildStack((string?)component["id"]);
        ReloadPreview();
    }

    private void RemoveSelected()
    {
        if (SelectedId is not { } id || !Composition.Remove(id)) return;
        RebuildStack();
        ReloadPreview();
    }

    private void MoveSelected(int delta)
    {
        if (SelectedId is not { } id || !Composition.Move(id, delta)) return;
        RebuildStack(id);
        ReloadPreview();
    }

    /// <summary>Select a component by stable id or type; used by keyboard/UI automation.</summary>
    public bool SelectComponent(string idOrType)
    {
        for (int index = 0; index < _stack.Items.Count; index++)
        {
            if (_stack.Items[index] is not ComponentRow row) continue;
            JObject? component = Composition.Components.OfType<JObject>().FirstOrDefault(candidate =>
                string.Equals((string?)candidate["id"], row.Id, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(row.Id, idOrType, StringComparison.OrdinalIgnoreCase)
                || string.Equals((string?)component?["type"], idOrType, StringComparison.OrdinalIgnoreCase))
            {
                _stack.SelectedIndex = index;
                return true;
            }
        }
        return false;
    }

    public bool CopySelectedComponent()
    {
        if (SelectedComponent is not { } component) return false;
        _componentClipboard = (JObject)component.DeepClone();
        _paste.Enabled = true;
        return true;
    }

    public bool PasteComponent()
    {
        if (_componentClipboard is not { } copied) return false;
        string type = (string?)copied["type"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(type)) return false;
        JObject target = Composition.Ensure(type);
        target["enabled"] = (bool?)copied["enabled"] != false;
        target["props"] = copied["props"]?.DeepClone() ?? new JObject();
        Composition.SynchronizeLegacyBindings();
        RebuildStack((string?)target["id"]);
        ReloadPreview();
        return true;
    }

    public bool ResetSelectedComponent()
    {
        if (SelectedComponent is not { } component) return false;
        string type = (string?)component["type"] ?? string.Empty;
        ObjectComponentDefinition definition = ObjectCompositionModel.Definition(type);
        component["enabled"] = true;
        component["props"] = definition.Defaults.DeepClone();
        Composition.SynchronizeLegacyBindings();
        RebuildStack((string?)component["id"]);
        ReloadPreview();
        return true;
    }

    private void RebuildStack(string? selectId = null)
    {
        selectId ??= SelectedId;
        _syncing = true;
        _stack.Items.Clear();
        foreach (JObject component in Composition.Components.OfType<JObject>())
        {
            string type = (string?)component["type"] ?? "UnknownComponent";
            ObjectComponentDefinition? definition = ObjectCompositionModel.Definitions.FirstOrDefault(item =>
                string.Equals(item.Type, type, StringComparison.OrdinalIgnoreCase));
            string name = definition?.DisplayName ?? type;
            string state = (bool?)component["enabled"] == false ? "  (disabled)" : string.Empty;
            _stack.Items.Add(new ComponentRow((string?)component["id"] ?? type, name + state));
        }
        int selected = 0;
        if (!string.IsNullOrWhiteSpace(selectId))
            for (int i = 0; i < _stack.Items.Count; i++)
                if (_stack.Items[i] is ComponentRow row && string.Equals(row.Id, selectId, StringComparison.OrdinalIgnoreCase))
                    selected = i;
        if (_stack.Items.Count > 0) _stack.SelectedIndex = Math.Clamp(selected, 0, _stack.Items.Count - 1);
        _syncing = false;
        ShowSelection();
    }

    private void ShowSelection()
    {
        if (_syncing) return;
        JObject? component = SelectedComponent;
        _syncing = true;
        try
        {
            _properties.Rows.Clear();
            if (component == null)
            {
                _enabled.Enabled = false;
                _reset.Enabled = _copy.Enabled = _remove.Enabled = false;
                _asset.Visible = _assetLabel.Visible = _browseAsset.Visible = false;
                return;
            }

            _enabled.Enabled = true;
            _enabled.Checked = (bool?)component["enabled"] != false;
            string type = (string?)component["type"] ?? string.Empty;
            ObjectComponentDefinition? definition = ObjectCompositionModel.Definitions.FirstOrDefault(item =>
                string.Equals(item.Type, type, StringComparison.OrdinalIgnoreCase));
            _remove.Enabled = definition?.Removable == true;
            _reset.Enabled = definition is not null;
            _copy.Enabled = definition is not null;
            bool hasAsset = definition is { AssetKind: not null } && !string.IsNullOrWhiteSpace(definition.AssetProperty);
            _asset.Visible = _assetLabel.Visible = _browseAsset.Visible = hasAsset;
            if (hasAsset)
            {
                _assetLabel.Text = definition!.AssetProperty;
                _asset.Text = (string?)ObjectCompositionModel.Props(component)[definition.AssetProperty]
                              ?? string.Empty;
            }

            foreach (JProperty property in ObjectCompositionModel.Props(component).Properties())
            {
                if (hasAsset && string.Equals(property.Name, definition!.AssetProperty, StringComparison.OrdinalIgnoreCase)) continue;
                int row = _properties.Rows.Add(property.Name, Display(property.Value));
                _properties.Rows[row].Tag = property.Name;
                if (property.Value.Type == JTokenType.Boolean)
                {
                    _properties.Rows[row].Cells["Value"] = new DataGridViewCheckBoxCell
                    {
                        Value = (bool)property.Value,
                        ThreeState = false,
                    };
                }
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void CommitEnabled()
    {
        if (_syncing || SelectedId is not { } id) return;
        Composition.SetEnabled(id, _enabled.Checked);
        RebuildStack(id);
        ReloadPreview();
    }

    private void BrowseAsset()
    {
        if (_syncing || SelectedComponent is not { } component) return;
        string type = (string?)component["type"] ?? string.Empty;
        ObjectComponentDefinition definition = ObjectCompositionModel.Definition(type);
        if (!definition.AssetKind.HasValue) return;
        ProjectAssetEntry? selected = AssetPickerService.PickAsset(
            new AssetPickerRequest(
                _projectRoot,
                definition.AssetKind.Value,
                _asset.Text,
                $"Select {definition.DisplayName} asset",
                AllowNone: true),
            this);
        if (selected is null) return;
        _asset.Text = selected.Reference;
        Composition.SetAsset(type, selected.Reference);
        ReloadPreview();
    }

    private void CommitProperty(int rowIndex)
    {
        if (_syncing || rowIndex < 0 || rowIndex >= _properties.Rows.Count || SelectedId is not { } id) return;
        DataGridViewRow row = _properties.Rows[rowIndex];
        string property = row.Tag as string ?? string.Empty;
        JObject component = SelectedComponent!;
        JToken? existing = ObjectCompositionModel.Props(component)[property];
        string text = Convert.ToString(row.Cells["Value"].Value, CultureInfo.InvariantCulture) ?? string.Empty;
        Composition.SetPropertyById(id, property, ParseValue(text, existing));
        ReloadPreview();
    }

    private void ReloadPreview()
    {
        Composition.SynchronizeLegacyBindings();
        Preview.Reload(Document);
    }

    private string? SelectedId => _stack.SelectedItem is ComponentRow row ? row.Id : null;
    private JObject? SelectedComponent => SelectedId is not { } id
        ? null
        : Composition.Components.OfType<JObject>().FirstOrDefault(component =>
            string.Equals((string?)component["id"], id, StringComparison.OrdinalIgnoreCase));

    private static string Display(JToken value) => value.Type is JTokenType.Array or JTokenType.Object
        ? value.ToString(Formatting.None)
        : value.ToString();

    private static JToken ParseValue(string text, JToken? existing)
    {
        if (existing?.Type == JTokenType.Boolean && bool.TryParse(text, out bool boolean)) return boolean;
        if (existing?.Type == JTokenType.Integer && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer)) return integer;
        if (existing?.Type == JTokenType.Float && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return number;
        if (existing?.Type is JTokenType.Array or JTokenType.Object)
        {
            try { return JToken.Parse(text); }
            catch (JsonReaderException) { return existing.DeepClone(); }
        }
        return text;
    }
}
