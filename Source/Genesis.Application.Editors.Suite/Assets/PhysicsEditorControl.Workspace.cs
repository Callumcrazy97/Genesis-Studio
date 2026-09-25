using System.Drawing.Drawing2D;
using System.Numerics;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Physics;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;
using Genesis.World.Terrain;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class PhysicsEditorControl
{
    private bool _referencePhysicsLayout;
    private readonly List<float> _kineticEnergy = [];
    private PhysicsTelemetryGraph? _telemetryGraph;
    private CollisionLayerMatrixControl? _collisionMatrix;
    private TrackBar? _simulationTimeline;
    private ToolStripLabel? _toolbarSpeed;
    private ToolStripButton? _toolbarPlay;
    private ToolStripButton? _toolbarPause;
    private Label? _computedMass;
    private float _simulationSeconds;
    private bool _seekingTimeline;
    private readonly RuntimeModelRenderSystem _physicsTargetModelRenderer = new();
    private GModelAsset? _physicsTargetModel;
    private TerrainAsset? _physicsTargetTerrain;
    private Matrix4x4 _physicsTargetWorld = Matrix4x4.Identity;
    private MeshHandle _physicsTargetTerrainMesh;
    private IRenderController? _physicsTargetRenderer;
    private string _physicsTargetKey = string.Empty;
    private Vector3 _physicsTargetSpawnOrigin;
    private SplitContainer? _physicsAuthoringSplit;
    private Panel? _physicsLeftPageHost;
    private Panel? _physicsRail;
    private readonly Dictionary<string, Control> _physicsLeftPages = new(StringComparer.OrdinalIgnoreCase);

    private void BuildReferenceWorkspace(EditorCommandBar toolbar, FlowLayoutPanel properties)
    {
        _referencePhysicsLayout = true;
        RebuildPhysicsCommandBar(toolbar);
        Panel left = BuildPhysicsLeftDock();
        Panel right = BuildPhysicsInspector(properties);
        Panel bottom = BuildPhysicsBottomDock();

        _codeSurface.Visible = false;
        _codeSurface.Dock = DockStyle.Fill;
        _codeSurface.Controls.Add(EditorChrome.SectionLabel("PGSL PHYSICS CODE EDITOR"));

        Panel viewportPanel = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Canvas };
        viewportPanel.Controls.Add(_viewport);
        viewportPanel.Controls.Add(EditorChrome.SectionLabel("3D PHYSICS VIEWPORT  ·  BEPUPHYSICS 2.4  ·  LIVE 60 FPS"));

        _physicsAuthoringSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            Size = new Size(900, 600),
            SplitterDistance = 340,
            SplitterWidth = EditorChrome.SplitterWidth,
            Panel1MinSize = 280,
            Panel2MinSize = 360,
            BackColor = EditorChrome.Border,
        };
        _physicsAuthoringSplit.Panel1.Controls.Add(_codeSurface);
        _physicsAuthoringSplit.Panel2.Controls.Add(viewportPanel);
        _physicsAuthoringSplit.Panel1Collapsed = true;

        TableLayoutPanel centre = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = EditorChrome.Canvas,
        };
        centre.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        centre.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
        centre.Controls.Add(_physicsAuthoringSplit, 0, 0);
        centre.Controls.Add(bottom, 0, 1);

        TableLayoutPanel workspace = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = EditorChrome.Canvas,
        };
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 292));
        workspace.Controls.Add(left, 0, 0);
        workspace.Controls.Add(centre, 1, 0);
        workspace.Controls.Add(right, 2, 0);

        Controls.Clear();
        Controls.Add(workspace);
        Controls.Add(toolbar);
        Controls.Add(_statusLabel);
        // A fill-docked child must be first in z-order so WinForms reserves the
        // top command bar and bottom status strip before laying out the workspace.
        workspace.BringToFront();
    }

    private void RebuildPhysicsCommandBar(EditorCommandBar toolbar)
    {
        ToolStripItem[] menus = toolbar.Items.Cast<ToolStripItem>().Take(2).ToArray();
        ToolStripControlHost? targetHost = toolbar.Items.OfType<ToolStripControlHost>()
            .FirstOrDefault(host => ReferenceEquals(host.Control, _previewTargetControls.KindCombo));
        ToolStripControlHost? presetHost = toolbar.Items.OfType<ToolStripControlHost>()
            .FirstOrDefault(host => ReferenceEquals(host.Control, _presetCombo));
        toolbar.Items.Clear();
        toolbar.Items.Add(new ToolStripLabel("◆  " + ResourceDisplayName.Format(ResourcePath))
        {
            Font = new Font(EditorChrome.BaseFont, FontStyle.Bold),
            ForeColor = EditorChrome.Text,
            ToolTipText = ResourcePath,
        });
        foreach (ToolStripItem menu in menus) toolbar.Items.Add(menu);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(new ToolStripLabel("Type") { ForeColor = EditorChrome.Muted });
        if (targetHost is not null) toolbar.Items.Add(targetHost);
        toolbar.Items.Add(_previewTargetControls.PathLabel);
        toolbar.Items.Add(_previewTargetControls.BrowseButton);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(new ToolStripLabel("Preset") { ForeColor = EditorChrome.Muted });
        if (presetHost is not null) toolbar.Items.Add(presetHost);
        toolbar.Items.Add(new ToolStripSeparator());
        _toolbarPlay = EditorChrome.ToolButton("▶", "Run the live physics sandbox", () => SetPhysicsPaused(false));
        _toolbarPause = EditorChrome.ToolButton("⏸", "Pause the live physics sandbox", () => SetPhysicsPaused(true));
        toolbar.Items.Add(_toolbarPlay);
        toolbar.Items.Add(_toolbarPause);
        toolbar.Items.Add(EditorChrome.ToolButton("⏭", "Advance exactly one 60 Hz physics frame", () => { SetPhysicsPaused(true); StepSandbox(1f / 60f); }));
        toolbar.Items.Add(EditorChrome.ToolButton("↺", "Restart the sandbox", RebuildSandbox));
        toolbar.Items.Add(EditorChrome.ToolButton("½×", "Run slower", () => SetPhysicsSpeed(_simulationSpeed * 0.5f)));
        toolbar.Items.Add(EditorChrome.ToolButton("2×", "Run faster", () => SetPhysicsSpeed(_simulationSpeed * 2f)));
        _toolbarSpeed = new ToolStripLabel("Speed: 1.0×") { ForeColor = EditorChrome.Text };
        toolbar.Items.Add(_toolbarSpeed);
    }

    private Panel BuildPhysicsLeftDock()
    {
        Panel root = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface };
        _physicsLeftPages.Clear();
        Panel rail = new() { Dock = DockStyle.Left, Width = 72, BackColor = EditorChrome.Canvas };
        _physicsRail = rail;
        string[] items = ["Presets", "Code", "Properties", "Colliders", "Joints", "Settings"];
        string[] glyphs = ["▦", "</>", "☷", "⬡", "⛓", "⚙"];
        for (int i = items.Length - 1; i >= 0; i--)
        {
            string name = items[i];
            Button button = new()
            {
                Dock = DockStyle.Top,
                Height = 64,
                Text = glyphs[i] + Environment.NewLine + name,
                FlatStyle = FlatStyle.Flat,
                BackColor = i == 0 ? EditorChrome.Hover : EditorChrome.Canvas,
                ForeColor = i == 0 ? EditorChrome.Accent : EditorChrome.Muted,
                Tag = name,
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += (_, _) => SelectPhysicsWorkspaceMode(name, rail);
            rail.Controls.Add(button);
        }

        Panel content = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface, Padding = new Padding(8) };
        FlowLayoutPanel presets = new()
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            WrapContents = true,
            BackColor = EditorChrome.Surface,
            Padding = new Padding(0, 4, 0, 4),
        };
        foreach (string name in new[] { "Heavy Rock", "Rubber Ball", "Slick Ice", "Hard Wood", "Steel", "Water Basin", "Mini-Planet" })
        {
            PhysicsPresetTile tile = new(name) { Margin = new Padding(3) };
            tile.Click += (_, _) => ApplyPreset(name);
            presets.Controls.Add(tile);
        }

        Panel spawner = new() { Dock = DockStyle.Bottom, Height = 174, BackColor = EditorChrome.Surface };
        FlowLayoutPanel shapes = new() { Dock = DockStyle.Top, Height = 82, WrapContents = false, BackColor = EditorChrome.Surface };
        foreach (PhysicsBodyShape shape in new[] { PhysicsBodyShape.Box, PhysicsBodyShape.Sphere, PhysicsBodyShape.Capsule })
        {
            PhysicsShapeButton button = new(shape) { Margin = new Padding(3) };
            button.Click += (_, _) => SelectPhysicsSpawnShape(shape);
            shapes.Controls.Add(button);
        }
        Button spawn = new()
        {
            Dock = DockStyle.Bottom,
            Height = 36,
            Text = "Spawn Now",
            BackColor = EditorChrome.Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        spawn.Click += (_, _) => SpawnPhysicsBodyNow();
        spawner.Controls.Add(spawn);
        spawner.Controls.Add(shapes);
        spawner.Controls.Add(EditorChrome.SectionLabel("SPAWNER STACK"));
        content.Controls.Add(presets);
        content.Controls.Add(spawner);
        content.Controls.Add(EditorChrome.SectionLabel("MATERIAL PRESETS"));

        Panel codePage = BuildPhysicsRailPage("CODE", "Edit the canonical physics definition beside the live sandbox. Changes update the same Bepu configuration used at runtime.");
        Panel propertiesPage = BuildPhysicsRailPage("PROPERTIES", "Material, collider, mass and world controls are available in the inspector on the right while the sandbox keeps the full centre canvas.");
        Panel collidersPage = BuildPhysicsColliderPage();
        Panel jointsPage = BuildPhysicsJointPage();
        Panel settingsPage = BuildPhysicsSettingsPage();
        _physicsLeftPageHost = new Panel { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface };
        _physicsLeftPages["Presets"] = content;
        _physicsLeftPages["Code"] = codePage;
        _physicsLeftPages["Properties"] = propertiesPage;
        _physicsLeftPages["Colliders"] = collidersPage;
        _physicsLeftPages["Joints"] = jointsPage;
        _physicsLeftPages["Settings"] = settingsPage;
        foreach (Control page in _physicsLeftPages.Values)
        {
            page.Visible = false;
            _physicsLeftPageHost.Controls.Add(page);
        }
        content.Visible = true;
        root.Controls.Add(_physicsLeftPageHost);
        root.Controls.Add(rail);
        return root;
    }

    private static Panel BuildPhysicsRailPage(string title, string description)
    {
        Panel page = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface, Padding = new Padding(12) };
        page.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Height = 150,
            ForeColor = EditorChrome.Muted,
            Font = EditorChrome.BaseFont,
            Text = description,
        });
        page.Controls.Add(EditorChrome.SectionLabel(title));
        return page;
    }

    private Panel BuildPhysicsColliderPage()
    {
        Panel page = BuildPhysicsRailPage("COLLIDERS", "Choose the reusable body shape, then spawn it into the sandbox to inspect contacts, mass and motion.");
        FlowLayoutPanel shapes = new()
        {
            Dock = DockStyle.Top,
            Height = 190,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(8),
        };
        foreach (PhysicsBodyShape shape in Enum.GetValues<PhysicsBodyShape>())
        {
            Button button = new() { Width = 188, Height = 30, Text = shape.ToString(), TextAlign = ContentAlignment.MiddleLeft };
            EditorChrome.StyleField(button);
            button.Click += (_, _) => SelectPhysicsSpawnShape(shape);
            shapes.Controls.Add(button);
        }
        page.Controls.Add(shapes);
        shapes.BringToFront();
        return page;
    }

    private Panel BuildPhysicsJointPage()
    {
        Panel page = BuildPhysicsRailPage("JOINTS", "Runtime joints are generic entity constraints. Use the command names below from an Object or Script routine, with two physics body ids and an anchor.");
        ListBox commands = new()
        {
            Dock = DockStyle.Top,
            Height = 126,
            BackColor = EditorChrome.Canvas,
            ForeColor = EditorChrome.Text,
            BorderStyle = BorderStyle.FixedSingle,
        };
        commands.Items.AddRange(["physics_create_joint", "physics_destroy_joint", "Ball socket", "Hinge / constrained rotation"]);
        page.Controls.Add(commands);
        commands.BringToFront();
        return page;
    }

    private Panel BuildPhysicsSettingsPage()
    {
        Panel page = BuildPhysicsRailPage("SANDBOX SETTINGS", "Preview settings control editor simulation only. The authored physics asset remains reusable for any compatible target.");
        FlowLayoutPanel settings = new() { Dock = DockStyle.Top, Height = 120, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(8) };
        CheckBox twoD = new() { AutoSize = true, ForeColor = EditorChrome.Text, Text = "2D simulation", Checked = _document.Dimension == PhysicsDimension.TwoD };
        twoD.CheckedChanged += (_, _) => SetPreview2D(twoD.Checked);
        CheckBox sleeping = new() { AutoSize = true, ForeColor = EditorChrome.Text, Text = "Allow sleeping", Checked = _document.AllowSleep };
        sleeping.CheckedChanged += (_, _) => SetPhysicsValue(() => _document.AllowSleep = sleeping.Checked);
        settings.Controls.Add(twoD);
        settings.Controls.Add(sleeping);
        page.Controls.Add(settings);
        settings.BringToFront();
        return page;
    }

    private void SelectPhysicsWorkspaceMode(string mode, Panel rail)
    {
        foreach (Button button in rail.Controls.OfType<Button>())
        {
            bool selected = string.Equals(button.Tag as string, mode, StringComparison.OrdinalIgnoreCase);
            button.BackColor = selected ? EditorChrome.Hover : EditorChrome.Canvas;
            button.ForeColor = selected ? EditorChrome.Accent : EditorChrome.Muted;
        }
        foreach ((string key, Control page) in _physicsLeftPages)
            page.Visible = string.Equals(key, mode, StringComparison.OrdinalIgnoreCase);

        if (string.Equals(mode, "Code", StringComparison.OrdinalIgnoreCase))
        {
            SetAuthoringMode(PhysicsAuthoringMode.Code);
            _code.Focus();
        }
        else
        {
            SetAuthoringMode(PhysicsAuthoringMode.Properties);
            if (string.Equals(mode, "Properties", StringComparison.OrdinalIgnoreCase))
                _computedMass?.Focus();
        }
        _physicsLeftPageHost?.PerformLayout();
    }

    private Panel BuildPhysicsInspector(FlowLayoutPanel properties)
    {
        properties.Controls.Remove(_shapeCombo);
        foreach (Control control in properties.Controls.Cast<Control>().ToArray()) control.Dispose();
        properties.Controls.Clear();
        properties.AutoScroll = false;
        properties.Padding = new Padding(0);

        FlowLayoutPanel stack = new()
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(8),
            BackColor = EditorChrome.Surface,
        };
        CollapsibleSection material = PhysicsSection("Material Properties", 192, out FlowLayoutPanel materialFields);
        AddInspectorSlider(materialFields, "Friction", (float)_document.Friction, SetFriction, out _frictionSlider);
        AddInspectorSlider(materialFields, "Restitution", (float)_document.Restitution, SetRestitution, out _restitutionSlider);
        AddInspectorNumeric(materialFields, "Density", (float)_document.Density, 0.01f, 100f, SetDensity, out _densityInput);
        stack.Controls.Add(material);

        CollapsibleSection collider = PhysicsSection("Collider & Shape", 134, out FlowLayoutPanel colliderFields);
        _shapeCombo.Width = 244;
        colliderFields.Controls.Add(FieldCaption("COLLIDER SHAPE"));
        colliderFields.Controls.Add(_shapeCombo);
        _computedMass = new Label { Width = 244, Height = 36, ForeColor = EditorChrome.Text, Padding = new Padding(0, 8, 0, 0) };
        colliderFields.Controls.Add(_computedMass);
        stack.Controls.Add(collider);

        CollapsibleSection world = PhysicsSection("World & Gravity", 370, out FlowLayoutPanel worldFields);
        AddInspectorNumeric(worldFields, "Gravity strength", _document.GravityStrength, 0f, 100f, SetGravityStrength, out _gravityInput);
        worldFields.Controls.Add(PhysicsNumber("Gravity X", _document.GravityDirX, -100, 100, value => SetGravityDirection(0, value)));
        worldFields.Controls.Add(PhysicsNumber("Gravity Y", _document.GravityDirY, -100, 100, value => SetGravityDirection(1, value)));
        worldFields.Controls.Add(PhysicsNumber("Gravity Z", _document.GravityDirZ, -100, 100, value => SetGravityDirection(2, value)));
        worldFields.Controls.Add(PhysicsNumber("Linear damping", _document.LinearDamping, 0, 100, value => SetPhysicsValue(() => _document.LinearDamping = value)));
        worldFields.Controls.Add(PhysicsNumber("Angular damping", _document.AngularDamping, 0, 100, value => SetPhysicsValue(() => _document.AngularDamping = value)));
        worldFields.Controls.Add(PhysicsNumber("Sleep threshold", _document.SleepThreshold, 0, 10, value => SetPhysicsValue(() => _document.SleepThreshold = value)));
        ThemedComboBox collisionLayer = new() { Width = 244, DropDownStyle = ComboBoxStyle.DropDownList };
        collisionLayer.Items.AddRange(["A", "B", "C", "D", "E", "F", "G"]);
        collisionLayer.SelectedIndex = Math.Clamp(_document.CollisionLayer, 0, 6);
        collisionLayer.SelectedIndexChanged += (_, _) =>
        {
            SetPhysicsValue(() => _document.CollisionLayer = collisionLayer.SelectedIndex);
            RebuildSandbox();
        };
        worldFields.Controls.Add(FieldCaption("DEFAULT COLLISION LAYER"));
        worldFields.Controls.Add(collisionLayer);
        stack.Controls.Add(world);

        Panel root = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface };
        root.Controls.Add(stack);
        root.Controls.Add(EditorChrome.SectionLabel("PROPERTY INSPECTOR"));
        RefreshComputedMass();
        return root;

        CollapsibleSection PhysicsSection(string title, int height, out FlowLayoutPanel fields)
        {
            CollapsibleSection section = new(title, height, 260) { Width = 260 };
            fields = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = false,
                BackColor = EditorChrome.Surface,
                FlowDirection = FlowDirection.TopDown,
                Padding = new Padding(8, 4, 8, 4),
                WrapContents = false,
            };
            section.Content.Controls.Add(fields);
            return section;
        }
    }

    private Panel BuildPhysicsBottomDock()
    {
        Panel root = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface };
        TableLayoutPanel body = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = EditorChrome.Surface };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        Panel telemetry = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Canvas, Padding = new Padding(8) };
        _simulationTimeline = new TrackBar { Dock = DockStyle.Top, Height = 34, Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None };
        _simulationTimeline.MouseUp += (_, _) => SeekPhysicsTimeline(_simulationTimeline.Value / 100f);
        _telemetryGraph = new PhysicsTelemetryGraph(() => _kineticEnergy) { Dock = DockStyle.Fill };
        telemetry.Controls.Add(_telemetryGraph);
        telemetry.Controls.Add(_simulationTimeline);
        telemetry.Controls.Add(EditorChrome.SectionLabel("KINETIC ENERGY TELEMETRY"));
        _collisionMatrix = new CollisionLayerMatrixControl(
            () => NormalizeCollisionMatrix(_document.CollisionLayerMatrix),
            matrix => { _document.CollisionLayerMatrix = matrix; MarkDirty(); PushCodeFromConfig(); RebuildSandbox(); }) { Dock = DockStyle.Fill };
        Panel matrixPanel = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface, Padding = new Padding(8) };
        matrixPanel.Controls.Add(_collisionMatrix);
        matrixPanel.Controls.Add(EditorChrome.SectionLabel("COLLISION LAYER MATRIX"));
        body.Controls.Add(telemetry, 0, 0);
        body.Controls.Add(matrixPanel, 1, 0);
        root.Controls.Add(body);
        return root;
    }

    private void SetPhysicsPaused(bool paused)
    {
        _paused = paused;
        if (_playPauseButton is { IsDisposed: false }) _playPauseButton.Text = paused ? "Play" : "Pause";
        if (_toolbarPlay is { IsDisposed: false }) _toolbarPlay.Checked = !paused;
        if (_toolbarPause is { IsDisposed: false }) _toolbarPause.Checked = paused;
        UpdateStatus();
    }

    private void SetPhysicsSpeed(float speed)
    {
        _simulationSpeed = Math.Clamp(speed, 0.125f, 8f);
        if (_speedSlider is { IsDisposed: false }) _speedSlider.Value = (int)Math.Clamp(_simulationSpeed * 60f, _speedSlider.Minimum, _speedSlider.Maximum);
        if (_toolbarSpeed is { IsDisposed: false }) _toolbarSpeed.Text = $"Speed: {_simulationSpeed:0.###}×";
        UpdateStatus();
    }

    private void SelectPhysicsSpawnShape(PhysicsBodyShape shape)
    {
        _document.Shape = shape;
        _document.SpawnShape = shape;
        _shapeCombo.SelectedItem = shape;
        RefreshComputedMass();
        MarkCustom();
        PushCodeFromConfig();
        MarkDirty();
    }

    private void SpawnPhysicsBodyNow()
    {
        if (_session is null) return;
        Func<float, float, float> ground = _physicsTargetTerrain is null
            ? (_, _) => 0f
            : (x, z) => _physicsTargetTerrain.SampleHeight(x, z);
        Vector3 origin = _physicsTargetSpawnOrigin + Vector3.UnitY * 4f;
        _session.SpawnBulkProps(MapSpawnShape(_document.SpawnShape), 1, ground, origin, 0f, _document.SpawnMass);
        SetPhysicsPaused(false);
        _viewport.Invalidate(true);
    }

    private void SetGravityDirection(int component, float value)
    {
        if (component == 0) _document.GravityDirX = value;
        else if (component == 1) _document.GravityDirY = value;
        else _document.GravityDirZ = value;
        _document.GravityModel = PhysicsGravityModel.Directional;
        SetPhysicsValue(() => { });
    }

    private void SetPhysicsValue(Action update)
    {
        update();
        _session?.ApplyPhysicsSceneConfig(_document);
        MarkCustom();
        PushCodeFromConfig();
        MarkDirty();
        UpdateStatus();
    }

    private Control PhysicsNumber(string label, float value, float minimum, float maximum, Action<float> changed)
    {
        Panel row = new() { Width = 244, Height = 38, BackColor = EditorChrome.Surface };
        Label caption = new() { Text = label, ForeColor = EditorChrome.Muted, Location = new Point(0, 8), Size = new Size(122, 24) };
        NumericUpDown input = new()
        {
            DecimalPlaces = 2,
            Increment = 0.05m,
            Minimum = (decimal)minimum,
            Maximum = (decimal)maximum,
            Value = Math.Clamp((decimal)value, (decimal)minimum, (decimal)maximum),
            Location = new Point(126, 5),
            Size = new Size(112, 27),
        };
        EditorChrome.StyleField(input);
        input.ValueChanged += (_, _) => { if (!_syncing) changed((float)input.Value); };
        row.Controls.Add(caption);
        row.Controls.Add(input);
        return row;
    }

    private static Label FieldCaption(string text) => new()
    {
        AutoSize = false, Width = 244, Height = 24, Text = text,
        ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont,
    };

    private void RefreshComputedMass()
    {
        if (_computedMass is null) return;
        double volume = _document.Shape switch
        {
            PhysicsBodyShape.Sphere => 4d / 3d * Math.PI * 0.75d * 0.75d * 0.75d,
            PhysicsBodyShape.Capsule => Math.PI * 0.35d * 0.35d * 1d + 4d / 3d * Math.PI * 0.35d * 0.35d * 0.35d,
            _ => 1d,
        };
        double mass = Math.Max(0.001, _document.Density * volume);
        _document.SpawnMass = (float)mass;
        _computedMass.Text = $"Computed mass   {mass:0.###} kg";
    }

    private void RecordPhysicsTelemetry(float dt)
    {
        if (_session is null) return;
        _simulationSeconds += dt;
        float energy = 0;
        for (int i = 0; i < _session.PropCount; i++)
        {
            if (!_session.IsPropActive(i)) continue;
            Vector3 velocity = _session.GetPropLinearVelocity(i);
            energy += 0.5f * Math.Max(0.001f, _document.SpawnMass) * velocity.LengthSquared();
        }
        _kineticEnergy.Add(energy);
        if (_kineticEnergy.Count > 360) _kineticEnergy.RemoveAt(0);
        if (!_seekingTimeline && _simulationTimeline is not null)
            _simulationTimeline.Value = (int)Math.Clamp(_simulationSeconds * 100f, 0f, 1000f);
        _telemetryGraph?.Invalidate();
    }

    private void ResetPhysicsTelemetry()
    {
        _simulationSeconds = 0;
        _kineticEnergy.Clear();
        if (_simulationTimeline is not null) _simulationTimeline.Value = 0;
        _telemetryGraph?.Invalidate();
    }

    private void SeekPhysicsTimeline(float seconds)
    {
        _seekingTimeline = true;
        try
        {
            RebuildSandbox();
            int frames = Math.Clamp((int)MathF.Round(seconds * 60f), 0, 600);
            for (int i = 0; i < frames; i++) StepSandbox(1f / 60f);
            SetPhysicsPaused(true);
        }
        finally
        {
            _seekingTimeline = false;
            if (_simulationTimeline is { IsDisposed: false })
                _simulationTimeline.Value = (int)Math.Clamp(seconds * 100f, 0f, 1000f);
        }
    }

    private bool ConfigureTargetSandbox(out Func<float, float, float> sampleGround, out Vector3 spawnOrigin)
    {
        sampleGround = (_, _) => 0f;
        spawnOrigin = Vector3.Zero;
        _physicsTargetSpawnOrigin = Vector3.Zero;
        if (_session is null || string.IsNullOrWhiteSpace(_document.PreviewAssetPath)) return false;
        string kind = _document.PreviewAssetKind ?? string.Empty;
        string path = ResolvePhysicsTargetPath(_document.PreviewAssetPath);
        try
        {
            if (kind.Equals("Terrain", StringComparison.OrdinalIgnoreCase))
            {
                TerrainAsset terrain = LoadPhysicsTerrain(path);
                _physicsTargetTerrain = terrain;
                _physicsTargetModel = null;
                int width = terrain.ResolutionX;
                int depth = terrain.ResolutionZ;
                List<Vector3> vertices = new(width * depth);
                for (int z = 0; z < depth; z++)
                    for (int x = 0; x < width; x++)
                        vertices.Add(new Vector3(terrain.OriginX + x * terrain.CellSize, terrain.GetHeight(x, z), terrain.OriginZ + z * terrain.CellSize));
                ushort[] sourceIndices = TerrainMeshBuilder.BuildIndices(width, depth);
                int[] indices = sourceIndices.Select(index => (int)index).ToArray();
                sampleGround = terrain.SampleHeight;
                float centerX = terrain.OriginX + (width - 1) * terrain.CellSize * 0.5f;
                float centerZ = terrain.OriginZ + (depth - 1) * terrain.CellSize * 0.5f;
                spawnOrigin = new Vector3(centerX, terrain.SampleHeight(centerX, centerZ), centerZ);
                _physicsTargetSpawnOrigin = spawnOrigin;
                _session.BuildMinimalSandbox(sampleGround);
                _session.AddStaticTriangleMesh(vertices, indices);
                FramePhysicsTerrain(terrain);
                return true;
            }

            string modelPath = kind.Equals("Object", StringComparison.OrdinalIgnoreCase)
                ? ResolvePhysicsObjectModel(path)
                : path;
            if (!kind.Equals("Model", StringComparison.OrdinalIgnoreCase) && !kind.Equals("Object", StringComparison.OrdinalIgnoreCase)) return false;
            GModelAsset asset = LoadPhysicsModel(modelPath);
            List<Vector3> modelVertices = [];
            List<int> modelIndices = [];
            foreach (GModelMesh mesh in asset.Meshes)
            {
                Vector3[] positions = mesh.IsSkinned
                    ? mesh.SkinnedVertices.Select(vertex => vertex.Position).ToArray()
                    : mesh.Vertices.Select(vertex => vertex.Position).ToArray();
                int offset = modelVertices.Count;
                modelVertices.AddRange(positions);
                modelIndices.AddRange(mesh.Indices.Select(index => offset + index));
            }
            if (modelVertices.Count == 0 || modelIndices.Count == 0) return false;
            Vector3 min = modelVertices.Aggregate(Vector3.Min);
            Vector3 max = modelVertices.Aggregate(Vector3.Max);
            Vector3 center = (min + max) * 0.5f;
            Vector3 translation = new(-center.X, -min.Y, -center.Z);
            for (int i = 0; i < modelVertices.Count; i++) modelVertices[i] += translation;
            _physicsTargetWorld = Matrix4x4.CreateTranslation(translation);
            _physicsTargetModel = asset;
            _physicsTargetTerrain = null;
            spawnOrigin = new Vector3(0, max.Y - min.Y, 0);
            _physicsTargetSpawnOrigin = spawnOrigin;
            _session.BuildMinimalSandbox(sampleGround);
            _session.AddStaticTriangleMesh(modelVertices, modelIndices);
            _viewport.Camera.Target = new Vector3(0, Math.Max(0.5f, (max.Y - min.Y) * 0.5f), 0);
            _viewport.Camera.Distance = Math.Max(5f, Vector3.Distance(min, max) * 1.4f);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or Newtonsoft.Json.JsonException)
        {
            LoadWarning = "Physics target could not be loaded: " + exception.Message;
            return false;
        }
    }

    private void DrawPhysicsPreviewTarget(IRenderController renderer)
    {
        if (_physicsTargetTerrain is not null)
        {
            if (!ReferenceEquals(_physicsTargetRenderer, renderer))
            {
                InvalidatePhysicsTargetRenderer();
                _physicsTargetRenderer = renderer;
            }
            if (!_physicsTargetTerrainMesh.IsValid)
            {
                MeshVertex[] vertices = new MeshVertex[_physicsTargetTerrain.ResolutionX * _physicsTargetTerrain.ResolutionZ];
                TerrainMeshBuilder.BuildVertices(_physicsTargetTerrain, TerrainMeshBuilder.DefaultLayerColors, vertices);
                _physicsTargetTerrainMesh = renderer.RegisterMesh(vertices, TerrainMeshBuilder.BuildIndices(_physicsTargetTerrain.ResolutionX, _physicsTargetTerrain.ResolutionZ));
            }
            renderer.DrawMesh(new MeshDrawCall { Mesh = _physicsTargetTerrainMesh, World = Matrix4x4.Identity, Tint = RenderColor.White, Flags = MeshDrawFlags.TerrainGround });
        }
        else if (_physicsTargetModel is not null)
        {
            _physicsTargetModelRenderer.DrawAsset(_physicsTargetModel, ProjectRoot, _physicsTargetWorld, default, renderer);
        }
    }

    private void DrawPhysicsTelemetryOverlay(IRenderController renderer)
    {
        if (_session is null) return;
        for (int i = 0; i < _session.PropCount; i++)
        {
            if (!_session.IsPropActive(i)) continue;
            _session.GetPropPose(i, out Vector3 position, out Quaternion rotation);
            PhysicsBody body = _session.GetPropBody(i);
            Matrix4x4 transform = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
            DrawWireBox(renderer, body.HalfExtents, transform, new RenderColor(0.25f, 1f, 0.38f, 0.85f));
            Vector3 screen = _viewport.WorldToSurface(position);
            if (screen.Z is <= 0 or >= 1) continue;
            EditorTransformGizmo.DrawCircle(renderer, new Vector2(screen.X, screen.Y), 6f, new RenderColor(1f, 0.88f, 0.05f), RenderColor.Black);
            Vector3 velocity = _session.GetPropLinearVelocity(i);
            if (velocity.LengthSquared() > 0.0025f)
            {
                Vector3 tip = _viewport.WorldToSurface(position + velocity * 0.18f);
                renderer.DrawLine(screen.X, screen.Y, tip.X, tip.Y, new RenderColor(0.05f, 0.95f, 1f), 2.2f, -9000);
            }
            if (_session.IsPropGrounded(i) && velocity.LengthSquared() > 1f)
            {
                Vector3 impact = _viewport.WorldToSurface(position - Vector3.UnitY * body.HalfExtents.Y);
                EditorTransformGizmo.DrawCircleOutline(renderer, new Vector2(impact.X, impact.Y), 10f, new RenderColor(1f, 0.1f, 0.06f), 3f);
            }
        }
    }

    private void DrawWireBox(IRenderController renderer, Vector3 half, Matrix4x4 transform, RenderColor colour)
    {
        Vector3[] corners =
        [
            new(-half.X,-half.Y,-half.Z), new(half.X,-half.Y,-half.Z), new(half.X,half.Y,-half.Z), new(-half.X,half.Y,-half.Z),
            new(-half.X,-half.Y,half.Z), new(half.X,-half.Y,half.Z), new(half.X,half.Y,half.Z), new(-half.X,half.Y,half.Z),
        ];
        for (int i = 0; i < corners.Length; i++) corners[i] = Vector3.Transform(corners[i], transform);
        int[] edges = [0,1,1,2,2,3,3,0,4,5,5,6,6,7,7,4,0,4,1,5,2,6,3,7];
        for (int i = 0; i < edges.Length; i += 2)
        {
            Vector3 a = _viewport.WorldToSurface(corners[edges[i]]);
            Vector3 b = _viewport.WorldToSurface(corners[edges[i + 1]]);
            if (a.Z is > 0 and < 1 && b.Z is > 0 and < 1)
                renderer.DrawLine(a.X, a.Y, b.X, b.Y, colour, 1.2f, -8999);
        }
    }

    private string ResolvePhysicsTargetPath(string value) => ResourceNames.Resolve(ProjectRoot, value);

    private static TerrainAsset LoadPhysicsTerrain(string path)
    {
        string binary = path + ".gterrain";
        if (File.Exists(binary)) return TerrainAsset.Load(binary);
        JObject settings = JObject.Parse(File.ReadAllText(path));
        TerrainGenParams parameters = settings.ToObject<TerrainGenParams>() ?? new TerrainGenParams();
        if (settings["resolution"] is JArray { Count: >= 2 } resolution)
        {
            parameters.ResolutionX = Math.Clamp((int)resolution[0], 2, 255);
            parameters.ResolutionZ = Math.Clamp((int)resolution[1], 2, 255);
        }
        return TerrainGenerator.Generate(parameters);
    }

    private static GModelAsset LoadPhysicsModel(string path) => path.EndsWith(".gmodel", StringComparison.OrdinalIgnoreCase)
        ? RuntimeModelStore.Load(path)
        : StudioModelResourceLoader.LoadReadOnly(path);

    private string ResolvePhysicsObjectModel(string path)
    {
        JObject prefab = ObjectDefinitionResolver.PreviewPrefab(ObjectDefinitionResolver.Load(ProjectRoot, path));
        string model = (string?)prefab["model"] ?? string.Empty;
        JObject? renderer = (prefab["components"] as JArray)?.OfType<JObject>()
            .FirstOrDefault(component => string.Equals((string?)component["type"], "ModelRendererComponent", StringComparison.OrdinalIgnoreCase));
        model = (string?)renderer?["props"]?["ModelAsset"] ?? model;
        return ResolvePhysicsTargetPath(model);
    }

    private void FramePhysicsTerrain(TerrainAsset terrain)
    {
        float width = (terrain.ResolutionX - 1) * terrain.CellSize;
        float depth = (terrain.ResolutionZ - 1) * terrain.CellSize;
        _viewport.Camera.Target = new Vector3(terrain.OriginX + width * 0.5f, 0, terrain.OriginZ + depth * 0.5f);
        _viewport.Camera.Distance = Math.Max(6f, Math.Max(width, depth) * 0.8f);
    }

    private void InvalidatePhysicsTarget()
    {
        _physicsTargetKey = string.Empty;
        _physicsTargetModel = null;
        _physicsTargetTerrain = null;
        InvalidatePhysicsTargetRenderer();
    }

    private void InvalidatePhysicsTargetRenderer()
    {
        if (_physicsTargetRenderer is not null && _physicsTargetTerrainMesh.IsValid)
            _physicsTargetRenderer.ReleaseMesh(_physicsTargetTerrainMesh);
        _physicsTargetModelRenderer.InvalidateAssets(_physicsTargetRenderer);
        _physicsTargetTerrainMesh = MeshHandle.Invalid;
        _physicsTargetRenderer = null;
    }

    private static bool[][] NormalizeCollisionMatrix(bool[][]? source)
    {
        bool[][] matrix = PhysicsSceneConfig.CreateDefaultCollisionLayerMatrix();
        if (source is null) return matrix;
        for (int y = 0; y < Math.Min(7, source.Length); y++)
            for (int x = 0; x < Math.Min(7, source[y]?.Length ?? 0); x++) matrix[y][x] = source[y][x];
        return matrix;
    }

    private sealed class PhysicsTelemetryGraph(Func<IReadOnlyList<float>> values) : Control
    {
        public PhysicsTelemetryGraph() : this(() => Array.Empty<float>()) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(EditorChrome.Canvas);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle graph = Rectangle.Inflate(ClientRectangle, -10, -12);
            using Pen grid = new(Color.FromArgb(72, EditorChrome.Border));
            for (int i = 0; i <= 4; i++)
            {
                int y = graph.Top + graph.Height * i / 4;
                e.Graphics.DrawLine(grid, graph.Left, y, graph.Right, y);
            }
            IReadOnlyList<float> data = values();
            if (data.Count < 2) return;
            float max = Math.Max(1f, data.Max());
            using Pen line = new(EditorChrome.Accent, 2f);
            PointF previous = new(graph.Left, graph.Bottom - data[0] / max * graph.Height);
            for (int i = 1; i < data.Count; i++)
            {
                PointF next = new(graph.Left + graph.Width * i / (float)(data.Count - 1), graph.Bottom - data[i] / max * graph.Height);
                e.Graphics.DrawLine(line, previous, next);
                previous = next;
            }
        }
    }

    private sealed class CollisionLayerMatrixControl(Func<bool[][]> read, Action<bool[][]> write) : Control
    {
        public CollisionLayerMatrixControl() : this(PhysicsSceneConfig.CreateDefaultCollisionLayerMatrix, _ => { }) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(EditorChrome.Surface);
            bool[][] matrix = read();
            const int cell = 13, left = 22, top = 12;
            using SolidBrush text = new(EditorChrome.Muted);
            for (int i = 0; i < 7; i++)
            {
                e.Graphics.DrawString(((char)('A' + i)).ToString(), EditorChrome.SmallFont, text, left + i * cell + 2, 0);
                e.Graphics.DrawString(((char)('A' + i)).ToString(), EditorChrome.SmallFont, text, 5, top + i * cell);
                for (int x = 0; x < 7; x++)
                {
                    Rectangle box = new(left + x * cell, top + i * cell, cell - 3, cell - 3);
                    using SolidBrush fill = new(matrix[i][x] ? EditorChrome.Accent : EditorChrome.Raised);
                    e.Graphics.FillRectangle(fill, box);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            const int cell = 13, left = 22, top = 12;
            int x = (e.X - left) / cell, y = (e.Y - top) / cell;
            if (x is < 0 or >= 7 || y is < 0 or >= 7) return;
            bool[][] matrix = read();
            bool enabled = !matrix[y][x];
            matrix[y][x] = enabled;
            matrix[x][y] = enabled;
            write(matrix);
            Invalidate();
        }
    }

    private sealed class PhysicsPresetTile : Button
    {
        private readonly string _name;
        public PhysicsPresetTile(string name)
        {
            _name = name;
            Size = new Size(100, 82);
            FlatStyle = FlatStyle.Flat;
            BackColor = EditorChrome.Raised;
            ForeColor = EditorChrome.Text;
            Text = name;
            TextAlign = ContentAlignment.BottomCenter;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color tint = _name switch
            {
                "Rubber Ball" => Color.IndianRed,
                "Slick Ice" => Color.LightSkyBlue,
                "Hard Wood" => Color.SaddleBrown,
                "Steel" => Color.LightSteelBlue,
                "Water Basin" => Color.DeepSkyBlue,
                "Mini-Planet" => Color.ForestGreen,
                _ => Color.Gray,
            };
            using SolidBrush brush = new(tint);
            e.Graphics.FillEllipse(brush, Width / 2 - 18, 8, 36, 36);
        }
    }

    private sealed class PhysicsShapeButton : Button
    {
        private readonly PhysicsBodyShape _shape;
        public PhysicsShapeButton(PhysicsBodyShape shape)
        {
            _shape = shape;
            Text = shape.ToString();
            TextAlign = ContentAlignment.BottomCenter;
            Size = new Size(68, 72);
            FlatStyle = FlatStyle.Flat;
            BackColor = EditorChrome.Raised;
            ForeColor = EditorChrome.Text;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using Pen pen = new(EditorChrome.Warning, 2f);
            Rectangle box = new(Width / 2 - 13, 8, 26, 30);
            if (_shape == PhysicsBodyShape.Sphere) e.Graphics.DrawEllipse(pen, box);
            else if (_shape == PhysicsBodyShape.Capsule)
            {
                using GraphicsPath capsule = new();
                capsule.AddArc(box.X, box.Y, box.Width, box.Width, 180, 180);
                capsule.AddLine(box.Right, box.Y + box.Width / 2, box.Right, box.Bottom - box.Width / 2);
                capsule.AddArc(box.X, box.Bottom - box.Width, box.Width, box.Width, 0, 180);
                capsule.CloseFigure();
                e.Graphics.DrawPath(pen, capsule);
            }
            else e.Graphics.DrawRectangle(pen, box);
        }
    }
}
