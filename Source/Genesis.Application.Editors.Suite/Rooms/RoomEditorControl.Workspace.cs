using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Runtime.Scene;

namespace Genesis.Application.Editors.Suite.Rooms;

public sealed partial class RoomEditorControl
{
    private bool _workspaceReady;
    private TrackBar? _environmentClock;
    private Label? _environmentReadout;
    private Panel? _environmentBar;
    private ListBox? _workspaceTerrains;
    private int _preferredRoomPaletteWidth = 350;
    private int _preferredRoomInspectorWidth = 308;
    private bool _arrangingRoomPanels;
    private bool _movingRoomPalette;
    private bool _movingRoomInspector;
    private bool _roomLayoutQueued;
    private bool _refreshingTerrainList;
    private bool? _workspaceThreeD;
    private ThemedComboBox? _workspaceSceneCamera;
    private bool _syncingWorkspaceSceneCamera;
    private RoomInspectorPanel? _workspaceSettings;

    private void BuildRoomWorkspace(EditorCommandBar toolbar, Panel viewportHost)
    {
        ToolStripItem file = toolbar.Items[0];
        ToolStripItem edit = toolbar.Items[1];
        ToolStripDropDownButton view = toolbar.Items.OfType<ToolStripDropDownButton>().Last(item => item.Text == "View");
        AddFogViewControls(view);
        ToolStripMenuItem palette = new("Objects and navigation") { CheckOnClick = true, Checked = true };
        ToolStripMenuItem inspector = new("Inspector") { CheckOnClick = true, Checked = true };
        palette.Click += (_, _) => { _palettePanelVisible = palette.Checked; _paletteToggle.Checked = palette.Checked; ApplyResponsiveLayout(); };
        inspector.Click += (_, _) => { _inspectorPanelVisible = inspector.Checked; _inspectorToggle.Checked = inspector.Checked; ApplyResponsiveLayout(); };
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(palette);
        view.DropDownItems.Add(inspector);
        view.DropDownOpening += (_, _) => { palette.Checked = _palettePanelVisible; inspector.Checked = _inspectorPanelVisible; };
        _editorToolbar.AttachTo(toolbar, [file, edit, view], [_selectToolButton, _moveGizmoButton, _rotateGizmoButton, _scaleGizmoButton, _localGizmoButton]);
        _localGizmoButton.Text = "World";
        _editorToolbar.TerrainSnapChanged += SetSnapToTerrain;
        EnableRoomAssetDrop();

        Panel skybox = WorkspaceStack();
        WorkspaceGroup(skybox, "Sky and time", true, ("", _dynamicSkyCheck), ("Clock (hours)", _timeOfDay), ("Time scale", _timeScale), ("Atmosphere", _atmospherePreset));
        WorkspaceGroup(skybox, "Weather", true, ("Weather", _weatherCombo), ("", _automaticWeatherCheck), ("Day of year", _dayOfYear), ("Latitude", _latitude), ("Climate seed", _climateSeed));
        WorkspaceGroup(skybox, "Clouds and haze", false, ("", _volumetricCloudsCheck), ("Quality", _cloudQuality), ("Cloud base", _cloudBaseHeight), ("Thickness", _cloudThickness), ("Coverage", _cloudCoverage), ("Haze", _atmosphericHaze));
        WorkspaceGroup(skybox, "Soundscape", false, ("", _soundscapeButton));

        Panel cameras = WorkspaceStack();
        var layouts = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = Padding.Empty };
        foreach ((int count, string label) in new[] { (1, "Single"), (2, "Split 2"), (4, "Split 4") })
        {
            var button = new Button { AutoSize = true, Text = label, Name = "RoomCameraLayout" + count };
            EditorChrome.StyleField(button); button.Click += (_, _) => SetViewportLayout(count); layouts.Controls.Add(button);
        }
        _workspaceSceneCamera = new ThemedComboBox { Name = "RoomSceneCamera", DropDownStyle = ComboBoxStyle.DropDownList };
        _workspaceSceneCamera.SelectedIndexChanged += (_, _) =>
        {
            if (!_syncingWorkspaceSceneCamera && _workspaceSceneCamera.SelectedIndex >= 0)
                _sceneViewBox.SelectedIndex = _workspaceSceneCamera.SelectedIndex;
        };
        WorkspaceGroup(cameras, "Camera views", true, ("", layouts), ("", _viewportSlotCombo), ("", _viewportEnabledCheck), ("Scene camera", _workspaceSceneCamera));
        SyncWorkspaceSceneCameras();
        WorkspaceGroup(cameras, "Source region", true, ("X", _viewportSource[0]), ("Y", _viewportSource[1]), ("Z", _viewportSource[2]), ("Width", _viewportSource[3]), ("Height", _viewportSource[4]), ("Depth", _viewportSource[5]));
        WorkspaceGroup(cameras, "Projection", false, ("Near", _viewportFrustum[0]), ("Far", _viewportFrustum[1]), ("Field of view", _viewportFrustum[2]));
        WorkspaceGroup(cameras, "Follow and dead zone", false, ("Target", _viewportFollowCombo), ("Margin X", _viewportMargin[0]), ("Margin Y", _viewportMargin[1]), ("Margin Z", _viewportMargin[2]), ("Speed X", _viewportSpeed[0]), ("Speed Y", _viewportSpeed[1]), ("Speed Z", _viewportSpeed[2]));
        WorkspaceGroup(cameras, "Output viewport", false, ("X", _viewportPort[0]), ("Y", _viewportPort[1]), ("Width", _viewportPort[2]), ("Height", _viewportPort[3]));
        WorkspaceGroup(cameras, "Editor guides", false, ("", _viewportEditorVisibleCheck), ("", _viewportColorButton), ("Style", _viewportFillCombo));
        _viewportEnabledCheck.CheckedChanged += (_, _) => SyncCameraWorkspaceFields();

        Panel terrain = new() { Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = EditorChrome.Surface };
        _paletteList.Dock = DockStyle.Fill;
        _paletteSearch.PlaceholderText = "Find terrain…";
        _paletteList.Columns[0].Width = 220;
        _workspaceTerrains = new ListBox { Dock = DockStyle.Bottom, Height = 132, BorderStyle = BorderStyle.None, BackColor = EditorChrome.Surface, ForeColor = EditorChrome.Text, DisplayMember = "Name", Name = "RoomTerrainInstances" };
        _workspaceTerrains.SelectedIndexChanged += (_, _) =>
        {
            if (_refreshingTerrainList) return;
            ActiveRoomEditContextChanged();
            if (_workspaceTerrains.SelectedItem is RoomNode node) Select(node);
        };
        Label terrainHelp = new() { Dock = DockStyle.Top, Height = 54, Text = "TERRAIN\nChoose an asset, then click in the room.", ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont };
        terrain.Controls.Add(_paletteList);
        terrain.Controls.Add(_paletteSearch);
        terrain.Controls.Add(terrainHelp);
        terrain.Controls.Add(_workspaceTerrains);
        RoomInspectorPanel settings = _workspaceSettings = new RoomInspectorPanel(this, roomSettingsOnly: true);
        settings.InspectNode(null);
        _navigation.SetWorkspacePanels(skybox, terrain, cameras, settings);
        _navigation.SectionChanged += ActivateRoomWorkspaceSection;

        _environmentClock = new TrackBar { Minimum = 0, Maximum = 240, TickStyle = TickStyle.None, AutoSize = false, Dock = DockStyle.Fill, Height = 30, Margin = Padding.Empty, Name = "RoomEnvironmentClock" };
        _environmentReadout = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = EditorChrome.Muted, Padding = new Padding(4, 0, 0, 0), Font = EditorChrome.SmallFont };
        Button skySettings = new() { AutoSize = true, Height = 30, Text = "Environment", FlatStyle = FlatStyle.Flat };
        EditorChrome.StyleField(skySettings);
        skySettings.Click += (_, _) => _navigation.SetSection(RoomNavSection.Backgrounds);
        TableLayoutPanel environment = new() { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(8, 4, 8, 4), BackColor = EditorChrome.Surface };
        environment.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        environment.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        environment.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        environment.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        environment.Controls.Add(skySettings, 0, 0);
        environment.Controls.Add(_environmentClock, 1, 0);
        environment.Controls.Add(_environmentReadout, 2, 0);
        environment.SizeChanged += (_, _) => skySettings.Text = environment.Width < 390 ? "Sky" : "Environment";
        _environmentBar = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = EditorChrome.Surface, Name = "RoomEnvironmentBar" };
        _environmentBar.Controls.Add(environment);
        viewportHost.Controls.Add(_environmentBar);
        _environmentClock.Scroll += (_, _) => _timeOfDay.Value = _environmentClock.Value / 10m;
        _timeOfDay.ValueChanged += (_, _) => SyncEnvironmentBar();
        _weatherCombo.SelectedIndexChanged += (_, _) => SyncEnvironmentBar();
        _mainSplit.SplitterMoving += (_, _) => _movingRoomPalette = true;
        _centerSplit.SplitterMoving += (_, _) => _movingRoomInspector = true;
        _mainSplit.SplitterMoved += (_, _) =>
        {
            bool moved = _movingRoomPalette;
            if (_movingRoomPalette && !_arrangingRoomPanels && !_mainSplit.Panel1Collapsed) _preferredRoomPaletteWidth = _mainSplit.SplitterDistance;
            _movingRoomPalette = false;
            if (moved && !_arrangingRoomPanels) ApplyResponsiveLayout();
        };
        _centerSplit.SplitterMoved += (_, _) =>
        {
            bool moved = _movingRoomInspector;
            if (_movingRoomInspector && !_arrangingRoomPanels && !_centerSplit.Panel2Collapsed) _preferredRoomInspectorWidth = _centerSplit.Width - _centerSplit.SplitterDistance - _centerSplit.SplitterWidth;
            _movingRoomInspector = false;
            if (moved && !_arrangingRoomPanels) ApplyResponsiveLayout();
        };
        _mainSplit.SizeChanged += (_, _) => QueueResponsiveRoomLayout();
        _centerSplit.SizeChanged += (_, _) => QueueResponsiveRoomLayout();
        _workspaceReady = true;
        SyncRoomWorkspace();
    }

    private void QueueResponsiveRoomLayout()
    {
        if (_roomLayoutQueued || IsDisposed || Disposing || !IsHandleCreated) return;
        _roomLayoutQueued = true;
        // SplitContainer raises SizeChanged before its children have completed docking.
        // Let that layout finish before choosing widths, so collapsing a panel cannot
        // leave a child viewport at the previous width.
        BeginInvoke(() =>
        {
            _roomLayoutQueued = false;
            if (!IsDisposed && !Disposing) ApplyResponsiveLayout();
        });
    }

    private void SyncRoomWorkspace()
    {
        if (!_workspaceReady) return;
        _navigation.SetDimension(ViewMode3D);
        if (_workspaceThreeD != ViewMode3D)
        {
            _workspaceThreeD = ViewMode3D;
            ActivateRoomWorkspaceSection(_navigation.CurrentSection);
        }
        _editorToolbar.SyncTerrain(ViewMode3D, SnapToTerrain);
        _localGizmoButton.Text = GizmoSpace == EditorGizmoSpace.Local ? "Local" : "World";
        SyncCameraWorkspaceFields();
        SyncEnvironmentBar();
        _workspaceSettings?.RefreshInspector();
    }

    private void ActivateRoomWorkspaceSection(RoomNavSection section)
    {
        if (!_workspaceReady) return;
        ActiveRoomEditContextChanged();
        if (section == RoomNavSection.Settings) _workspaceSettings?.RefreshInspector();
        if (section == RoomNavSection.Tilesets)
        {
            SetPaletteMode(ViewMode3D ? PaletteMode.Terrain : PaletteMode.Tiles);
            RefreshWorkspaceTerrains();
        }
        if (section == RoomNavSection.Objects) SetPaletteMode(PaletteMode.Objects);
        if (section == RoomNavSection.Views) SyncViewportFields();
        if (section == RoomNavSection.Backgrounds)
        {
            SetPaletteMode(PaletteMode.Backgrounds);
            SyncRoomSettings();
        }
    }

    private void SyncCameraWorkspaceFields()
    {
        if (!_workspaceReady) return;
        bool editable = _viewportEnabledCheck.Checked;
        bool threeD = ViewMode3D;
        foreach (Control field in _viewportSource.Concat(_viewportPort).Concat(_viewportMargin).Concat(_viewportSpeed).Concat(_viewportFrustum)) field.Enabled = editable;
        _viewportFollowCombo.Enabled = editable;
        _viewportEditorVisibleCheck.Enabled = editable;
        _viewportColorButton.Enabled = editable;
        _viewportFillCombo.Enabled = editable;
        _viewportSource[2].Enabled = editable && threeD;
        _viewportSource[5].Enabled = editable && threeD;
        _viewportMargin[2].Enabled = editable && threeD;
        _viewportSpeed[2].Enabled = editable && threeD;
        foreach (Control field in _viewportFrustum) field.Enabled = editable && threeD;
    }

    private void SyncWorkspaceSceneCameras()
    {
        if (_workspaceSceneCamera is null || _syncingWorkspaceSceneCamera) return;
        _syncingWorkspaceSceneCamera = true;
        try
        {
            if (!_workspaceSceneCamera.Items.Cast<object>().SequenceEqual(_sceneViewBox.Items.Cast<object>()))
            {
                _workspaceSceneCamera.Items.Clear();
                _workspaceSceneCamera.Items.AddRange(_sceneViewBox.Items.Cast<object>().ToArray());
            }
            _workspaceSceneCamera.SelectedIndex = _sceneViewBox.SelectedIndex;
        }
        finally { _syncingWorkspaceSceneCamera = false; }
    }

    private void SyncEnvironmentBar()
    {
        if (_environmentClock is null || _environmentReadout is null || _environmentBar is null) return;
        _environmentBar.Visible = _room.Dimension == RoomDimension.ThreeD;
        if (!_environmentClock.Focused) _environmentClock.Value = Math.Clamp((int)(_timeOfDay.Value * 10), 0, 240);
        _environmentReadout.Text = $"{(int)_timeOfDay.Value:00}:{(int)((_timeOfDay.Value % 1) * 60):00} · {_weatherCombo.SelectedItem}";
    }

    private void RefreshWorkspaceTerrains()
    {
        if (_workspaceTerrains is null || _refreshingTerrainList) return;
        string? selectedId = _selected?.Kind == RoomNodeKind.Terrain ? _selected.Id : (_workspaceTerrains.SelectedItem as RoomNode)?.Id;
        int top = _workspaceTerrains.TopIndex;
        _refreshingTerrainList = true;
        _workspaceTerrains.BeginUpdate();
        try
        {
            _workspaceTerrains.Items.Clear();
            foreach (RoomNode node in _room.Nodes.Where(node => node.Kind == RoomNodeKind.Terrain))
            {
                int index = _workspaceTerrains.Items.Add(node);
                if (node.Id == selectedId) _workspaceTerrains.SelectedIndex = index;
            }
            if (_workspaceTerrains.Items.Count > 0) _workspaceTerrains.TopIndex = Math.Clamp(top, 0, _workspaceTerrains.Items.Count - 1);
        }
        finally { _workspaceTerrains.EndUpdate(); _refreshingTerrainList = false; }
    }

    private static Panel WorkspaceStack() => new EditorScrollHost(EditorChrome.Surface) { Dock = DockStyle.Fill, Padding = new Padding(4) };

    private static void WorkspaceGroup(Panel parent, string title, bool expanded, params (string Label, Control Field)[] fields)
    {
        InspectorSection section = new() { Title = title, Expanded = expanded, Dock = DockStyle.Top };
        TableLayoutPanel table = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = EditorChrome.Surface, Margin = Padding.Empty };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
        foreach ((string label, Control field) in fields)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            field.Dock = DockStyle.Fill;
            field.Margin = new Padding(3, 5, 3, 5);
            if (field is CheckBox check) { check.AutoSize = false; check.Height = 38; }
            if (label.Length == 0) { table.Controls.Add(field, 0, row); table.SetColumnSpan(field, 2); }
            else
            {
                table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont, Margin = new Padding(3, 5, 3, 5) }, 0, row);
                table.Controls.Add(field, 1, row);
            }
        }
        section.Body.Controls.Add(table);
        parent.Controls.Add(section);
        section.BringToFront();
    }
}
