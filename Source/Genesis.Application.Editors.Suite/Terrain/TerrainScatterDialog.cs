using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.World.Terrain;

namespace Genesis.Application.Editors.Suite.Terrain;

/// <summary>
/// Edits a terrain's scatter layers: the rules that cover ground with copies of a Model (forests,
/// rocks, undergrowth). Each layer is a model and where it may stand; nothing is placed by hand.
/// </summary>
public sealed class TerrainScatterDialog : DpiAwareForm
{
    private readonly List<TerrainScatterLayer> _layers;
    private readonly ListBox _list = new() { Name = "TerrainScatterLayerList", Dock = DockStyle.Fill };
    private readonly TextBox _name = new() { Name = "TerrainScatterLayerName" };
    private readonly ThemedComboBox _model = new() { Name = "TerrainScatterLayerModel", DropDownStyle = ComboBoxStyle.DropDown };
    private readonly CheckBox _enabled = new() { Text = "Layer is drawn", Checked = true, AutoSize = true };
    private readonly NumericUpDown _density = TerrainPathDialog.DecimalNumber(0m, 20000m, 120m);
    private readonly NumericUpDown _spacing = TerrainPathDialog.DecimalNumber(0.25m, 500m, 3m);
    private readonly NumericUpDown _clump = TerrainPathDialog.DecimalNumber(0m, 5000m, 0m);
    private readonly NumericUpDown _clumpStrength = TerrainPathDialog.DecimalNumber(0m, 1m, 0.6m);
    private readonly NumericUpDown _minimumHeight = TerrainPathDialog.DecimalNumber(-100000m, 100000m, -100000m);
    private readonly NumericUpDown _maximumHeight = TerrainPathDialog.DecimalNumber(-100000m, 100000m, 100000m);
    private readonly NumericUpDown _slope = TerrainPathDialog.DecimalNumber(0m, 90m, 30m);
    private readonly ThemedComboBox _paint = new() { Name = "TerrainScatterLayerPaint", DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _minimumScale = TerrainPathDialog.DecimalNumber(0.05m, 64m, 0.8m);
    private readonly NumericUpDown _maximumScale = TerrainPathDialog.DecimalNumber(0.05m, 64m, 1.25m);
    private readonly NumericUpDown _sink = TerrainPathDialog.DecimalNumber(-10m, 10m, 0.1m);
    private readonly CheckBox _align = new() { Text = "Tilt copies to the ground", AutoSize = true };
    private readonly NumericUpDown _drawDistance = TerrainPathDialog.DecimalNumber(0m, 100000m, 0m);
    private readonly CheckBox _shadows = new() { Text = "Near copies cast shadows", Checked = true, AutoSize = true };
    private readonly NumericUpDown _collisionRadius = TerrainPathDialog.DecimalNumber(0m, 100m, 0m);
    private readonly NumericUpDown _collisionHeight = TerrainPathDialog.DecimalNumber(0m, 500m, 4m);
    // The whole range a pattern number can hold: a layer made by a recipe takes the recipe's seed,
    // and a narrower field would change such a layer's pattern the first time anything was edited.
    private readonly NumericUpDown _seed = TerrainPathDialog.Number(int.MinValue, int.MaxValue, 1);
    private bool _syncing;

    /// <param name="layers">The terrain's current layers; they are copied, so Cancel changes nothing.</param>
    /// <param name="models">Names of the project's Model resources, offered in the model list.</param>
    /// <param name="paintLayers">Names of the terrain's painted layers, first to fourth.</param>
    public TerrainScatterDialog(IEnumerable<TerrainScatterLayer> layers, IEnumerable<string> models, IReadOnlyList<string> paintLayers)
    {
        _layers = TerrainNatureSerializer.Clone((layers ?? Array.Empty<TerrainScatterLayer>()).ToList());
        // A hand-edited file may hold values no field can show; bring them into range first.
        foreach (TerrainScatterLayer layer in _layers) layer.Normalize();
        foreach (string model in (models ?? Array.Empty<string>()).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            _model.Items.Add(model);
        _paint.Items.Add("Any ground");
        for (int i = 0; i < 4; i++)
            _paint.Items.Add(paintLayers != null && i < paintLayers.Count && !string.IsNullOrWhiteSpace(paintLayers[i])
                ? $"Only on {paintLayers[i]}" : $"Only on layer {i + 1}");

        Text = "Forests and scatter"; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1040, 720); MinimumSize = new Size(900, 600);
        Tag = "font-measured-layout";
        SplitContainer split = new() { Dock = DockStyle.Fill, Size = new Size(1040, 660), SplitterDistance = 260, FixedPanel = FixedPanel.Panel1 };
        Panel left = new() { Dock = DockStyle.Fill, Padding = new Padding(10) };
        FlowLayoutPanel listButtons = new() { Dock = DockStyle.Bottom, AutoSize = true };
        Button add = new() { Name = "TerrainScatterAddLayer", Text = "+ Add", AutoSize = true };
        Button remove = new() { Name = "TerrainScatterRemoveLayer", Text = "Remove", AutoSize = true };
        listButtons.Controls.Add(add); listButtons.Controls.Add(remove); left.Controls.Add(_list); left.Controls.Add(listButtons);
        split.Panel1.Controls.Add(left);
        TabControl tabs = new() { Name = "TerrainScatterSettingsTabs", Dock = DockStyle.Fill };
        EditorChrome.StyleTabs(tabs);
        void AddPage(string title, (string, Control)[] rows)
        {
            TabPage page = new(title) { AutoScroll = true, BackColor = EditorChrome.Surface };
            TerrainDialogLayout.Fields(page, rows);
            tabs.TabPages.Add(page);
        }

        AddPage("What", [("Name", _name), ("Model", _model), ("", _enabled), ("Copies per hectare (100 m x 100 m)", _density),
            ("Closest two copies may stand (m)", _spacing), ("Gather into clumps this wide (m, 0 = spread evenly)", _clump),
            ("How strongly clumps gather (0 to 1)", _clumpStrength), ("Pattern number", _seed)]);
        AddPage("Where", [("Lowest ground (m)", _minimumHeight), ("Highest ground (m)", _maximumHeight),
            ("Steepest slope (degrees)", _slope), ("Painted ground", _paint)]);
        AddPage("Look and feel", [("Smallest size", _minimumScale), ("Largest size", _maximumScale), ("Sink into the ground (m)", _sink),
            ("", _align), ("", _shadows), ("Hide beyond (m, 0 = draw to the horizon)", _drawDistance),
            ("Solid trunk or rock half-width (m, 0 = walk through)", _collisionRadius), ("Solid height (m)", _collisionHeight)]);
        split.Panel2.Controls.Add(tabs);
        Controls.Add(split);
        TerrainDialogLayout.Actions(this);
        foreach (Button button in new[] { add, remove })
        {
            EditorChrome.StyleField(button);
            button.MinimumSize = new Size(0, button.Font.Height + 18);
            button.FontChanged += (_, _) => button.MinimumSize = new Size(0, button.Font.Height + 18);
        }

        _list.BackColor = EditorChrome.Surface;
        _list.ForeColor = EditorChrome.Text;
        _list.BorderStyle = BorderStyle.None;
        _list.FontChanged += (_, _) => _list.ItemHeight = _list.Font.Height + 10;
        add.Click += (_, _) => AddLayer(); remove.Click += (_, _) => RemoveLayer(); _list.SelectedIndexChanged += (_, _) => LoadSelected();
        foreach (Control control in new Control[] { _name, _model, _enabled, _density, _spacing, _clump, _clumpStrength, _minimumHeight,
                     _maximumHeight, _slope, _paint, _minimumScale, _maximumScale, _sink, _align, _drawDistance, _shadows,
                     _collisionRadius, _collisionHeight, _seed })
        {
            if (control is TextBox text) text.TextChanged += (_, _) => StoreSelected();
            else if (control is ComboBox combo) { combo.SelectedIndexChanged += (_, _) => StoreSelected(); combo.TextChanged += (_, _) => StoreSelected(); }
            else if (control is NumericUpDown number) number.ValueChanged += (_, _) => StoreSelected();
            else if (control is CheckBox check) check.CheckedChanged += (_, _) => StoreSelected();
        }

        RefreshList();
        if (_layers.Count > 0) _list.SelectedIndex = 0;
        else LoadSelected();
    }

    /// <summary>The layers as edited, normalised and ready to store on the terrain.</summary>
    public IReadOnlyList<TerrainScatterLayer> Layers
    {
        get
        {
            List<TerrainScatterLayer> copy = TerrainNatureSerializer.Clone(_layers);
            foreach (TerrainScatterLayer layer in copy) layer.Normalize();
            return copy;
        }
    }

    /// <summary>Adds a layer, as the Add button does.</summary>
    public void AddLayer()
    {
        var layer = new TerrainScatterLayer
        {
            Name = $"Scatter {_layers.Count + 1}", Seed = 1 + _layers.Count * 7919,
            Model = _model.Items.Count > 0 ? _model.Items[0]?.ToString() ?? "" : "",
        };
        _layers.Add(layer); RefreshList(); _list.SelectedIndex = _layers.Count - 1;
    }

    private void RemoveLayer()
    {
        int index = _list.SelectedIndex; if (index < 0) return;
        _layers.RemoveAt(index); RefreshList();
        if (_layers.Count > 0) _list.SelectedIndex = Math.Min(index, _layers.Count - 1);
        else LoadSelected();
    }

    private void RefreshList()
    {
        int selected = _list.SelectedIndex; _list.Items.Clear();
        foreach (TerrainScatterLayer layer in _layers)
            _list.Items.Add(string.IsNullOrWhiteSpace(layer.Model) ? layer.Name : $"{layer.Name}  ·  {layer.Model}");
        if (selected >= 0 && _layers.Count > 0) _list.SelectedIndex = Math.Min(selected, _layers.Count - 1);
    }

    private void LoadSelected()
    {
        int index = _list.SelectedIndex;
        bool any = index >= 0 && index < _layers.Count;
        foreach (Control control in new Control[] { _name, _model, _enabled, _density, _spacing, _clump, _clumpStrength, _minimumHeight,
                     _maximumHeight, _slope, _paint, _minimumScale, _maximumScale, _sink, _align, _drawDistance, _shadows,
                     _collisionRadius, _collisionHeight, _seed })
            control.Enabled = any;
        if (!any) return;
        TerrainScatterLayer layer = _layers[index]; _syncing = true;
        _name.Text = layer.Name; _model.Text = layer.Model; _enabled.Checked = layer.Enabled;
        Set(_density, layer.DensityPerHectare); Set(_spacing, layer.MinimumSpacing); Set(_clump, layer.ClumpSize);
        Set(_clumpStrength, layer.ClumpStrength); Set(_minimumHeight, layer.MinimumHeight); Set(_maximumHeight, layer.MaximumHeight);
        Set(_slope, layer.MaximumSlopeDegrees);
        _paint.SelectedIndex = layer.PaintLayerMask switch { 1 => 1, 2 => 2, 4 => 3, 8 => 4, _ => 0 };
        Set(_minimumScale, layer.MinimumScale); Set(_maximumScale, layer.MaximumScale); Set(_sink, layer.Sink);
        _align.Checked = layer.AlignToGround; Set(_drawDistance, layer.DrawDistance); _shadows.Checked = layer.CastShadows;
        Set(_collisionRadius, layer.CollisionRadius); Set(_collisionHeight, layer.CollisionHeight);
        _seed.Value = layer.Seed;
        _syncing = false;
    }

    private void StoreSelected()
    {
        int index = _list.SelectedIndex; if (_syncing || index < 0 || index >= _layers.Count) return;
        TerrainScatterLayer layer = _layers[index];
        string previous = layer.Name + layer.Model;
        layer.Name = string.IsNullOrWhiteSpace(_name.Text) ? "Scatter" : _name.Text.Trim();
        layer.Model = _model.Text.Trim(); layer.Enabled = _enabled.Checked;
        layer.DensityPerHectare = (float)_density.Value; layer.MinimumSpacing = (float)_spacing.Value;
        layer.ClumpSize = (float)_clump.Value; layer.ClumpStrength = (float)_clumpStrength.Value;
        layer.MinimumHeight = (float)_minimumHeight.Value; layer.MaximumHeight = (float)_maximumHeight.Value;
        layer.MaximumSlopeDegrees = (float)_slope.Value;
        // "Any ground" also stands for a layer allowed on several painted layers but not all; that
        // choice cannot be made here, so it is kept rather than widened to all four.
        bool several = layer.PaintLayerMask is not (1 or 2 or 4 or 8 or 15);
        layer.PaintLayerMask = _paint.SelectedIndex switch { 1 => 1, 2 => 2, 3 => 4, 4 => 8, _ => several ? layer.PaintLayerMask : 15 };
        layer.MinimumScale = (float)_minimumScale.Value; layer.MaximumScale = (float)Math.Max(_minimumScale.Value, _maximumScale.Value);
        layer.Sink = (float)_sink.Value; layer.AlignToGround = _align.Checked;
        layer.DrawDistance = (float)_drawDistance.Value; layer.CastShadows = _shadows.Checked;
        layer.CollisionRadius = (float)_collisionRadius.Value; layer.CollisionHeight = (float)_collisionHeight.Value;
        layer.Seed = (int)_seed.Value;
        if (previous != layer.Name + layer.Model)
        {
            _syncing = true;
            _list.Items[index] = string.IsNullOrWhiteSpace(layer.Model) ? layer.Name : $"{layer.Name}  ·  {layer.Model}";
            _syncing = false;
        }
    }

    private static void Set(NumericUpDown control, float value) =>
        control.Value = Math.Clamp((decimal)value, control.Minimum, control.Maximum);
}
