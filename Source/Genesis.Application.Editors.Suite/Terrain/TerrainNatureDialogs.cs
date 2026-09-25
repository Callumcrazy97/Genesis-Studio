using System.Numerics;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.World.Foliage;
using Genesis.World.Terrain;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed class TerrainPathDialog : DpiAwareForm
{
    public TerrainPathSettings Settings { get; private set; }

    public TerrainPathDialog(TerrainPathSettings current)
    {
        Settings = current ?? new TerrainPathSettings();
        Text = "Generate Connected Terrain Paths";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(430, 320);
        MaximizeBox = false; MinimizeBox = false;
        BackColor = EditorChrome.Surface;
        
        var form = Genesis.Application.Editors.Suite.Inspector.InspectorBuilder.BuildForObject(Settings, "Generation is deterministic and grades/paints the terrain with full Undo support.");
        Controls.Add(form);
        
        // Find Ok and Cancel buttons
        foreach (Control c in form.Controls)
        {
            if (c is FlowLayoutPanel flow)
            {
                foreach (Control b in flow.Controls)
                {
                    if (b is Button btn)
                    {
                        if (btn.DialogResult == DialogResult.OK) AcceptButton = btn;
                        if (btn.DialogResult == DialogResult.Cancel) CancelButton = btn;
                    }
                }
            }
        }
    }

    internal static NumericUpDown Number(decimal min, decimal max, decimal value) => new() { Minimum = min, Maximum = max, Value = value };
    internal static NumericUpDown DecimalNumber(decimal min, decimal max, decimal value) => new() { Minimum = min, Maximum = max, Value = value, DecimalPlaces = 2, Increment = 0.05m };
    internal static ThemedComboBox Combo<T>() where T : struct, Enum
    {
        ThemedComboBox combo = new();
        combo.Items.AddRange(Enum.GetNames<T>()); combo.SelectedIndex = 0; return combo;
    }
}

public sealed class FoliageScatterDialog : DpiAwareForm
{
    public FoliageScatterSettings Settings { get; private set; }

    public FoliageScatterDialog(FoliageScatterSettings current)
    {
        Settings = current ?? new FoliageScatterSettings();
        Text = "Scatter Ecological Foliage";
        StartPosition = FormStartPosition.CenterParent; 
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(500, 650); 
        MaximizeBox = false; MinimizeBox = false;
        BackColor = EditorChrome.Surface;

        var form = Genesis.Application.Editors.Suite.Inspector.InspectorBuilder.BuildForObject(Settings, "The same deterministic cell streaming, LOD and performance budgets run in this preview and in gameplay.");
        Controls.Add(form);
        
        foreach (Control c in form.Controls)
        {
            if (c is FlowLayoutPanel flow)
            {
                foreach (Control b in flow.Controls)
                {
                    if (b is Button btn)
                    {
                        if (btn.DialogResult == DialogResult.OK) AcceptButton = btn;
                        if (btn.DialogResult == DialogResult.Cancel) CancelButton = btn;
                    }
                }
            }
        }
    }
}

public sealed class TerrainWaterDialog : DpiAwareForm
{
    private readonly List<TerrainWaterDefinition> _waters;
    private readonly ListBox _list = new() { Dock = DockStyle.Fill };
    private readonly TextBox _name = new();
    private readonly ThemedComboBox _kind = WaterKindCombo();
    private readonly NumericUpDown _x = TerrainPathDialog.DecimalNumber(-100000m, 100000m, 0m);
    private readonly NumericUpDown _z = TerrainPathDialog.DecimalNumber(-100000m, 100000m, 0m);
    private readonly NumericUpDown _height = TerrainPathDialog.DecimalNumber(-10000m, 10000m, 0m);
    private readonly NumericUpDown _sizeX = TerrainPathDialog.DecimalNumber(0.5m, 10000m, 24m);
    private readonly NumericUpDown _sizeZ = TerrainPathDialog.DecimalNumber(0.5m, 10000m, 24m);
    private readonly CheckBox _simulation = new() { Text = "Shallow-water simulation", Checked = true, AutoSize = true };
    private readonly NumericUpDown _resolution = TerrainPathDialog.Number(8, 128, 48);
    private readonly NumericUpDown _damping = TerrainPathDialog.DecimalNumber(0.8m, 1m, 0.985m);
    private readonly NumericUpDown _rain = TerrainPathDialog.DecimalNumber(0m, 2m, 0.65m);
    private readonly NumericUpDown _waves = TerrainPathDialog.DecimalNumber(0m, 8m, 0.18m);
    private readonly NumericUpDown _flow = TerrainPathDialog.DecimalNumber(-20m, 20m, 0.2m);
    private readonly NumericUpDown _flowX = TerrainPathDialog.DecimalNumber(-1m, 1m, 0m);
    private readonly NumericUpDown _flowZ = TerrainPathDialog.DecimalNumber(-1m, 1m, 1m);
    private readonly CheckBox _conform = new() { Text = "Conform surface to terrain contour", Checked = true, AutoSize = true };
    private readonly NumericUpDown _physicsDepth = TerrainPathDialog.DecimalNumber(0.1m, 10000m, 6m);
    private readonly NumericUpDown _density = TerrainPathDialog.DecimalNumber(1m, 10000m, 1000m);
    private readonly NumericUpDown _buoyancy = TerrainPathDialog.DecimalNumber(0m, 4m, 1m);
    private readonly NumericUpDown _linearDrag = TerrainPathDialog.DecimalNumber(0m, 20m, 2.5m);
    private readonly NumericUpDown _angularDrag = TerrainPathDialog.DecimalNumber(0m, 20m, 1m);
    private readonly ThemedComboBox _physicsMode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _damaging = new() { Text = "Damages occupants", AutoSize = true };
    private bool _syncing;
    private readonly Vector3 _defaultCenter;

    public TerrainWaterDialog(IEnumerable<TerrainWaterDefinition> waters, Vector3 defaultCenter)
    {
        _physicsMode.Items.AddRange(new object[] { "None — decorative water", "Shallow — drag, no swimming", "Swimmable volume — buoyancy and swimming" });
        _defaultCenter = defaultCenter;
        _waters = (waters ?? Array.Empty<TerrainWaterDefinition>()).Select(Clone).ToList();
        Text = "Water Bodies"; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 520); MinimumSize = new Size(680, 500);
        SplitContainer split = new() { Dock = DockStyle.Fill, SplitterDistance = 205, FixedPanel = FixedPanel.Panel1 };
        Panel left = new() { Dock = DockStyle.Fill, Padding = new Padding(10) };
        FlowLayoutPanel listButtons = new() { Dock = DockStyle.Bottom, Height = 38 };
        Button add = new() { Text = "+ Add", AutoSize = true }; Button remove = new() { Text = "Remove", AutoSize = true };
        listButtons.Controls.Add(add); listButtons.Controls.Add(remove); left.Controls.Add(_list); left.Controls.Add(listButtons);
        split.Panel1.Controls.Add(left);
        (string, Control)[] rows = [
            ("Name", _name), ("Type", _kind), ("Centre X", _x), ("Centre Z", _z), ("Surface height", _height),
            ("Size X", _sizeX), ("Size Z", _sizeZ), ("", _simulation), ("Simulation resolution", _resolution),
            ("Damping", _damping), ("Rain coupling", _rain), ("Wave amplitude", _waves), ("Flow speed", _flow),
            ("Flow direction X", _flowX), ("Flow direction Z", _flowZ), ("", _conform),
            ("Water physics", _physicsMode), ("Physics depth", _physicsDepth), ("Fluid density", _density), ("Buoyancy", _buoyancy),
            ("Linear drag", _linearDrag), ("Angular drag", _angularDrag), ("", _damaging)];
        TableLayoutPanel form = new() { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12), AutoScroll = true };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160)); form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < rows.Length; i++)
        {
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            form.Controls.Add(new Label { Text = rows[i].Item1, AutoSize = true, Anchor = AnchorStyles.Left }, 0, i);
            rows[i].Item2.Dock = DockStyle.Fill; form.Controls.Add(rows[i].Item2, 1, i);
        }
        FlowLayoutPanel actions = new() { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(5) };
        Button ok = new() { Text = "Apply", DialogResult = DialogResult.OK, AutoSize = true };
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        actions.Controls.Add(ok); actions.Controls.Add(cancel); split.Panel2.Controls.Add(form); split.Panel2.Controls.Add(actions);
        Controls.Add(split); AcceptButton = ok; CancelButton = cancel;
        add.Click += (_, _) => AddWater(); remove.Click += (_, _) => RemoveWater(); _list.SelectedIndexChanged += (_, _) => LoadSelected();
        foreach (Control control in new Control[] { _name, _kind, _x, _z, _height, _sizeX, _sizeZ, _simulation, _resolution, _damping, _rain, _waves, _flow, _flowX, _flowZ, _conform, _physicsDepth, _density, _buoyancy, _linearDrag, _angularDrag, _physicsMode, _damaging })
        {
            if (control is TextBox text) text.TextChanged += (_, _) => StoreSelected();
            else if (control is ComboBox combo) combo.SelectedIndexChanged += (_, _) => StoreSelected();
            else if (control is NumericUpDown number) number.ValueChanged += (_, _) => StoreSelected();
            else if (control is CheckBox check) check.CheckedChanged += (_, _) => StoreSelected();
        }
        RefreshList(); if (_waters.Count == 0) AddWater(); else _list.SelectedIndex = 0;
    }

    public IReadOnlyList<TerrainWaterDefinition> WaterBodies => _waters.Select(Clone).ToArray();

    private void AddWater()
    {
        TerrainWaterDefinition water = new() { Name = $"Water {_waters.Count + 1}", Kind = TerrainWaterKind.Water, Center = _defaultCenter, SurfaceHeight = _defaultCenter.Y };
        _waters.Add(water); RefreshList(); _list.SelectedIndex = _waters.Count - 1;
    }

    private void RemoveWater()
    {
        int index = _list.SelectedIndex; if (index < 0) return;
        _waters.RemoveAt(index); RefreshList(); if (_waters.Count > 0) _list.SelectedIndex = Math.Min(index, _waters.Count - 1);
    }

    private void RefreshList()
    {
        int selected = _list.SelectedIndex; _list.Items.Clear();
        foreach (TerrainWaterDefinition water in _waters) _list.Items.Add($"{water.Name}  ·  {water.Kind}");
        if (selected >= 0 && _waters.Count > 0) _list.SelectedIndex = Math.Min(selected, _waters.Count - 1);
    }

    private void LoadSelected()
    {
        int index = _list.SelectedIndex; if (index < 0 || index >= _waters.Count) return;
        TerrainWaterDefinition water = _waters[index]; _syncing = true;
        _name.Text = water.Name;
        _kind.SelectedItem = water.Kind switch
        {
            TerrainWaterKind.Waterfall => nameof(TerrainWaterKind.Waterfall),
            TerrainWaterKind.River => nameof(TerrainWaterKind.River),
            _ => nameof(TerrainWaterKind.Water),
        };
        Set(_x, water.Center.X); Set(_z, water.Center.Z); Set(_height, water.SurfaceHeight);
        Set(_sizeX, water.SizeX); Set(_sizeZ, water.SizeZ); _simulation.Checked = water.SimulationEnabled;
        Set(_resolution, water.SimulationResolution); Set(_damping, water.SimulationDamping); Set(_rain, water.RainCoupling);
        Set(_waves, water.WaveAmplitude); Set(_flow, water.FlowSpeed);
        Set(_flowX, water.FlowDirection.X); Set(_flowZ, water.FlowDirection.Y); _conform.Checked = water.ConformToTerrain;
        Set(_physicsDepth, water.PhysicsDepth); Set(_density, water.FluidDensity); Set(_buoyancy, water.BuoyancyStrength);
        Set(_linearDrag, water.LinearDrag); Set(_angularDrag, water.AngularDrag);
        _physicsMode.SelectedIndex = (int)water.PhysicsMode; _damaging.Checked = water.Damaging; _syncing = false;
        RefreshPhysicsControls(water.PhysicsMode);
    }

    private void StoreSelected()
    {
        int index = _list.SelectedIndex; if (_syncing || index < 0 || index >= _waters.Count) return;
        TerrainWaterDefinition water = _waters[index]; water.Name = string.IsNullOrWhiteSpace(_name.Text) ? "Water" : _name.Text.Trim();
        water.Kind = _kind.SelectedItem?.ToString() switch
        {
            nameof(TerrainWaterKind.Waterfall) => TerrainWaterKind.Waterfall,
            nameof(TerrainWaterKind.River) => TerrainWaterKind.River,
            _ => TerrainWaterKind.Water,
        };
        Vector3 nextCenter = new((float)_x.Value, (float)_height.Value, (float)_z.Value);
        Vector3 previousCenter = new(water.Center.X, water.SurfaceHeight, water.Center.Z);
        bool contourChanged = nextCenter != previousCenter
            || MathF.Abs(water.SizeX - (float)_sizeX.Value) > .0001f
            || MathF.Abs(water.SizeZ - (float)_sizeZ.Value) > .0001f;
        Vector3 delta = nextCenter - previousCenter;
        water.Center += delta; water.SurfaceHeight = nextCenter.Y; water.TranslateGeometry(delta);
        water.SizeX = (float)_sizeX.Value; water.SizeZ = (float)_sizeZ.Value; water.SimulationEnabled = _simulation.Checked;
        water.SimulationResolution = (int)_resolution.Value; water.SimulationDamping = (float)_damping.Value;
        water.RainCoupling = (float)_rain.Value; water.WaveAmplitude = (float)_waves.Value; water.FlowSpeed = (float)_flow.Value;
        Vector2 flow = new((float)_flowX.Value, (float)_flowZ.Value);
        water.FlowDirection = flow.LengthSquared() < .0001f ? Vector2.UnitY : Vector2.Normalize(flow);
        water.ConformToTerrain = _conform.Checked;
        if (!water.ConformToTerrain || contourChanged) water.ClearFootprint();
        water.PhysicsDepth = (float)_physicsDepth.Value; water.FluidDensity = (float)_density.Value;
        water.BuoyancyStrength = (float)_buoyancy.Value; water.LinearDrag = (float)_linearDrag.Value;
        water.AngularDrag = (float)_angularDrag.Value; water.PhysicsMode = (WaterPhysicsMode)Math.Max(0, _physicsMode.SelectedIndex); water.Damaging = _damaging.Checked;
        RefreshPhysicsControls(water.PhysicsMode);
        // Rebuilding the whole list on every TextChanged event reloaded the selected water and
        // moved the Name caret to the end after every keystroke. Only its display label changed.
        if (index < _list.Items.Count)
        {
            _list.Items[index] = $"{water.Name}  ·  {water.Kind}";
        }
    }

    private void RefreshPhysicsControls(WaterPhysicsMode mode)
    {
        bool physical = mode != WaterPhysicsMode.None;
        _physicsDepth.Enabled = _linearDrag.Enabled = _damaging.Enabled = physical;
        _density.Enabled = _buoyancy.Enabled = _angularDrag.Enabled = mode == WaterPhysicsMode.SwimmableVolume;
    }

    private static ThemedComboBox WaterKindCombo()
    {
        ThemedComboBox combo = new();
        combo.Items.Add(nameof(TerrainWaterKind.Water));
        combo.Items.Add(nameof(TerrainWaterKind.River));
        combo.Items.Add(nameof(TerrainWaterKind.Waterfall));
        combo.SelectedIndex = 0;
        return combo;
    }

    private static void Set(NumericUpDown control, float value) => control.Value = Math.Clamp((decimal)value, control.Minimum, control.Maximum);
    private static TerrainWaterDefinition Clone(TerrainWaterDefinition source) => source.Clone();
}
