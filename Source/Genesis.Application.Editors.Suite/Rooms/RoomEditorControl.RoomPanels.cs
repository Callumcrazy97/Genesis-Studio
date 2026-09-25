using System;
using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Runtime.Climate;
using Genesis.Runtime.Core;
using Genesis.Runtime.Scene;
using Genesis.Shared.Commands;
using Genesis.Shared.Interfaces;
using Genesis.Application.Editors.Suite.UiKit;

namespace Genesis.Application.Editors.Suite.Rooms;

/// <summary>
/// The Room and Viewports panels: room-wide settings, and the eight camera regions a room can
/// present through.
/// </summary>
/// <remarks>
/// Separate from the instance Inspector because they answer a different question. The Inspector is
/// "what is this thing I selected"; these are "what is this room", which has no selection and stays
/// put while you click around the scene.
/// </remarks>
public sealed partial class RoomEditorControl
{
    private Panel _roomSettingsPanel = null!;
    private Panel _viewportsPanel = null!;

    // Room settings
    private TextBox _roomNameBox = null!;
    private Label _nameHintLabel = null!;
    private NumericUpDown _roomFrameRate = null!;
    private NumericUpDown _roomPhysicsRate = null!;
    private NumericUpDown _roomSettingsWidth = null!;
    private NumericUpDown _roomSettingsHeight = null!;
    private NumericUpDown _roomSettingsDepth = null!;
    private CheckBox _roomPersistentCheck = null!;
    private Button _roomBackgroundButton = null!;
    private CheckBox _dynamicSkyCheck = null!;
    private CheckBox _automaticWeatherCheck = null!;
    private ThemedComboBox _weatherCombo = null!;
    private NumericUpDown _timeOfDay = null!;
    private NumericUpDown _timeScale = null!;
    private NumericUpDown _climateSeed = null!;
    private NumericUpDown _dayOfYear = null!;
    private NumericUpDown _latitude = null!;
    private ThemedComboBox _atmospherePreset = null!;
    private CheckBox _volumetricCloudsCheck = null!;
    private ThemedComboBox _cloudQuality = null!;
    private NumericUpDown _cloudBaseHeight = null!;
    private NumericUpDown _cloudThickness = null!;
    private NumericUpDown _cloudCoverage = null!;
    private NumericUpDown _atmosphericHaze = null!;
    private Button _soundscapeButton = null!;

    // Viewports
    private ThemedComboBox _viewportSlotCombo = null!;
    private CheckBox _viewportEnabledCheck = null!;
    private readonly NumericUpDown[] _viewportSource = new NumericUpDown[6];   // X, Y, Z, W, H, D
    private readonly NumericUpDown[] _viewportPort = new NumericUpDown[4];     // X, Y, W, H
    private readonly NumericUpDown[] _viewportMargin = new NumericUpDown[3];
    private readonly NumericUpDown[] _viewportSpeed = new NumericUpDown[3];
    private readonly NumericUpDown[] _viewportFrustum = new NumericUpDown[3]; // Near, Far, FOV
    private ComboBox _viewportFollowCombo = null!;
    private CheckBox _viewportEditorVisibleCheck = null!;
    private Button _viewportColorButton = null!;
    private ThemedComboBox _viewportFillCombo = null!;
    private readonly Control[] _viewportFields = new Control[32];
    private int _viewportFieldCount;
    private int _activeViewport;
    private bool _syncingViewport;

    /// <summary>The viewport slot whose settings are on screen.</summary>
    public int ActiveViewportIndex => _activeViewport;

    // ── Test/automation surface ─────────────────────────────────────────────────
    // These drive the same fields and commit paths a designer's clicks do, rather than writing to
    // the room model directly — asserting against the model would prove the document works and
    // nothing at all about whether the panel is wired to it.

    /// <summary>Selects a viewport slot, exactly as the slot combo does.</summary>
    public void SelectViewport(int index)
    {
        if (index < 0 || index >= RoomAsset.MaxViewports) return;
        _viewportSlotCombo.SelectedIndex = index;
    }

    /// <summary>Ticks or clears "Enabled in game" for the selected slot.</summary>
    public void SetViewportEnabled(bool enabled) => _viewportEnabledCheck.Checked = enabled;

    /// <summary>Sets the selected slot's source region through the panel's own fields.</summary>
    public void SetViewportSource(float x, float y, float width, float height)
    {
        _viewportSource[0].Value = Clamp(_viewportSource[0], (decimal)x);
        _viewportSource[1].Value = Clamp(_viewportSource[1], (decimal)y);
        _viewportSource[3].Value = Clamp(_viewportSource[3], (decimal)width);
        _viewportSource[4].Value = Clamp(_viewportSource[4], (decimal)height);
    }

    /// <summary>Sets the selected slot's 3D source eye + volume through the panel's own fields.</summary>
    public void SetViewportSource3D(float x, float y, float z, float width, float height, float depth)
    {
        _viewportSource[0].Value = Clamp(_viewportSource[0], (decimal)x);
        _viewportSource[1].Value = Clamp(_viewportSource[1], (decimal)y);
        _viewportSource[2].Value = Clamp(_viewportSource[2], (decimal)z);
        _viewportSource[3].Value = Clamp(_viewportSource[3], (decimal)width);
        _viewportSource[4].Value = Clamp(_viewportSource[4], (decimal)height);
        _viewportSource[5].Value = Clamp(_viewportSource[5], (decimal)depth);
    }

    /// <summary>Sets Near / Far / FOV through the Viewports panel fields.</summary>
    public void SetViewportFrustum(float near, float far, float fieldOfViewDegrees)
    {
        _viewportFrustum[0].Value = Clamp(_viewportFrustum[0], (decimal)near);
        _viewportFrustum[1].Value = Clamp(_viewportFrustum[1], (decimal)far);
        _viewportFrustum[2].Value = Clamp(_viewportFrustum[2], (decimal)fieldOfViewDegrees);
    }

    /// <summary>
    /// Resolved eye/forward/frustum for a viewport slot — the same values the Room overlay draws
    /// and a marker click uses for the second-camera inset (CAM-2).
    /// </summary>
    public bool TryGetViewportFrustumPose(
        int index,
        out Vector3 eye,
        out Vector3 forward,
        out float fieldOfViewDegrees,
        out float nearPlane,
        out float farPlane,
        out float aspect)
    {
        eye = default;
        forward = -Vector3.UnitZ;
        fieldOfViewDegrees = nearPlane = farPlane = aspect = 0f;
        if (_room.Viewports is null || index < 0 || index >= _room.Viewports.Count) return false;
        RoomViewport viewport = _room.Viewports[index];
        ResolveViewportFrustumPose(index, viewport, out eye, out forward, out fieldOfViewDegrees, out nearPlane, out farPlane, out aspect);
        return true;
    }

    /// <summary>The exact source-space follow limits used by the runtime's margin rule.</summary>
    public bool TryGetViewportDeadZoneBounds3D(int index, out Vector3 min, out Vector3 max)
    {
        min = max = default;
        if (!ViewMode3D || _room.Viewports is null || index < 0 || index >= _room.Viewports.Count) return false;
        RoomViewport viewport = _room.Viewports[index];
        if (!viewport.Enabled || string.IsNullOrWhiteSpace(viewport.FollowTarget)) return false;
        Vector3 origin = new(viewport.SourceX, viewport.SourceY, viewport.SourceZ);
        Vector3 size = new(viewport.SourceWidth, viewport.SourceHeight, viewport.SourceDepth);
        Vector3 margin = Vector3.Clamp(new Vector3(viewport.FollowMarginX, viewport.FollowMarginY, viewport.FollowMarginZ),
            Vector3.Zero, size * .5f);
        min = origin + margin;
        max = origin + size - margin;
        return true;
    }

    /// <summary>Ray-picks a camera marker (viewport slot or active game camera) under a client point.</summary>
    public bool TryHitCameraMarker(Point client, out int viewportIndex, out bool isGameCamera) =>
        TryHitCameraMarkerCore(client, out viewportIndex, out isGameCamera);

    /// <summary>Sets the selected slot's destination port through the panel's own fields.</summary>
    public void SetViewportPort(int x, int y, int width, int height)
    {
        _viewportPort[0].Value = Clamp(_viewportPort[0], x);
        _viewportPort[1].Value = Clamp(_viewportPort[1], y);
        _viewportPort[2].Value = Clamp(_viewportPort[2], width);
        _viewportPort[3].Value = Clamp(_viewportPort[3], height);
    }

    /// <summary>Sets the selected slot's follow target through the panel's own field.</summary>
    public void SetViewportFollowTarget(string target) => _viewportFollowCombo.Text = target ?? string.Empty;

    /// <summary>True when the selected slot's editable fields are greyed out.</summary>
    public bool ViewportFieldsGreyed
    {
        get
        {
            for (int i = 0; i < _viewportFieldCount; i++)
                if (_viewportFields[i] is { Enabled: true }) return false;
            return true;
        }
    }

    /// <summary>Sets room settings through the panel's own fields.</summary>
    public void SetRoomSettingsFields(int frameRate, int width, int height)
    {
        _roomFrameRate.Value = Math.Clamp(frameRate, 0, 1000);
        _roomSettingsWidth.Value = Math.Clamp(width, 1, 100000);
        _roomSettingsHeight.Value = Math.Clamp(height, 1, 100000);
    }

    /// <summary>What the Name field shows. Read-only: a room is named by its file.</summary>
    public string RoomNameFieldText => _roomNameBox.Text;

    /// <summary>True when the Name field can be typed into. It must not be.</summary>
    public bool RoomNameEditable => !_roomNameBox.ReadOnly;

    /// <summary>True when the Depth field is editable — it must not be in a 2D room.</summary>
    public bool RoomDepthEditable => _roomSettingsDepth.Enabled;

    /// <summary>Drives the room-wide climate fields through the same commit path as the UI.</summary>
    public void SetEnvironmentFields(bool dynamicSky, WeatherKind weather, float timeHours,
        float timeScale, bool automaticWeather)
    {
        _dynamicSkyCheck.Checked = dynamicSky;
        _weatherCombo.SelectedItem = weather;
        _timeOfDay.Value = Math.Clamp((decimal)timeHours, _timeOfDay.Minimum, _timeOfDay.Maximum);
        _timeScale.Value = Math.Clamp((decimal)timeScale, _timeScale.Minimum, _timeScale.Maximum);
        _automaticWeatherCheck.Checked = automaticWeather;
    }

    public void SetAtmosphereFields(AtmospherePreset preset, bool volumetricClouds, int quality,
        float baseHeight, float thickness, float coverage, float haze)
    {
        _atmospherePreset.SelectedItem = preset;
        _volumetricCloudsCheck.Checked = volumetricClouds;
        _cloudQuality.SelectedIndex = Math.Clamp(quality, 0, 3);
        _cloudBaseHeight.Value = Math.Clamp((decimal)baseHeight, _cloudBaseHeight.Minimum, _cloudBaseHeight.Maximum);
        _cloudThickness.Value = Math.Clamp((decimal)thickness, _cloudThickness.Minimum, _cloudThickness.Maximum);
        _cloudCoverage.Value = Math.Clamp((decimal)coverage, _cloudCoverage.Minimum, _cloudCoverage.Maximum);
        _atmosphericHaze.Value = Math.Clamp((decimal)haze, _atmosphericHaze.Minimum, _atmosphericHaze.Maximum);
    }

    public void SetEnvironmentSoundscape(string wind = "", string rain = "", string water = "",
        string fire = "", string wildlife = "", string night = "", RoomSoundscapeLevels? levels = null)
    {
        RoomEnvironment previous = CloneEnvironment(_room.Environment);
        RoomEnvironment next = CloneEnvironment(_room.Environment);
        next.WindAudio = wind ?? string.Empty; next.RainAudio = rain ?? string.Empty;
        next.WaterAudio = water ?? string.Empty; next.FireAudio = fire ?? string.Empty;
        next.WildlifeAudio = wildlife ?? string.Empty; next.NightAudio = night ?? string.Empty;
        if (levels is not null) next.SoundscapeLevels = levels.Clone();
        ApplyEnvironment(next);
        PushEdit("Adaptive room soundscape", () => ApplyEnvironment(next), () => ApplyEnvironment(previous));
    }

    // ── Visibility filters (View toolbar) ───────────────────────────────────────

    /// <summary>Node kinds the editor draws and hit-tests. All on by default.</summary>
    public bool ShowObjects { get; private set; } = true;

    public bool ShowTileLayers { get; private set; } = true;

    public bool ShowBackgrounds { get; private set; } = true;

    public bool ShowTerrain { get; private set; } = true;

    /// <summary>Whether viewport regions are drawn over the room at all.</summary>
    public bool ShowViewportOutlines { get; private set; } = true;

    /// <summary>-1 draws every visible viewport; otherwise only that slot.</summary>
    public int ViewportOutlineFilter { get; private set; } = -1;

    /// <summary>Whether the editor grid is drawn.</summary>
    public bool ShowGrid { get; private set; } = true;

    private EditorFloorStyle _floorStyle = EditorFloorStyle.GridOnly;

    /// <summary>
    /// Whether the editor-only floor plate is drawn in 3D. Gameplay never sees this plate.
    /// </summary>
    public bool ShowEditorFloor => _floorStyle.DrawsPlate();

    public EditorFloorStyle FloorStyle => _floorStyle;

    /// <summary>True when the node kind is currently shown, used by draw and hit testing alike.</summary>
    public bool IsKindVisible(RoomNodeKind kind) => kind switch
    {
        RoomNodeKind.GameObject => ShowObjects,
        RoomNodeKind.TileLayer => ShowTileLayers,
        RoomNodeKind.Background => ShowBackgrounds,
        RoomNodeKind.Terrain => ShowTerrain,
        _ => true,
    };

    /// <summary>Toggles a kind filter. Named rather than a setter so tests read as intent.</summary>
    public void SetKindVisible(RoomNodeKind kind, bool visible)
    {
        switch (kind)
        {
            case RoomNodeKind.GameObject: ShowObjects = visible; break;
            case RoomNodeKind.TileLayer: ShowTileLayers = visible; break;
            case RoomNodeKind.Background: ShowBackgrounds = visible; break;
            case RoomNodeKind.Terrain: ShowTerrain = visible; break;
            default: return;
        }

        RefreshOutliner();
        _viewport?.Host?.Invalidate();
    }

    public void SetViewportOutlinesVisible(bool visible)
    {
        ShowViewportOutlines = visible;
        _viewport?.Host?.Invalidate();
    }

    public void SetViewportOutlineFilter(int index)
    {
        ViewportOutlineFilter = index < 0 ? -1 : index;
        _viewport?.Host?.Invalidate();
    }

    public void SetGridVisible(bool visible)
    {
        ShowGrid = visible;
        if (_workspaceReady) { SyncToolbar(); SyncInspector(); }
        _viewport?.Host?.Invalidate();
    }

    public void SetEditorFloorVisible(bool visible) =>
        SetFloorStyle(visible ? EditorFloorStyle.Checkerboard : EditorFloorStyle.None);

    public void SetFloorStyle(EditorFloorStyle style)
    {
        _floorStyle = style;
        if (style == EditorFloorStyle.GridOnly)
        {
            ShowGrid = true;
        }

        if (_viewport is not null)
        {
            _viewport.FloorStyle = style;
            _viewport.Host.Invalidate();
        }
        if (_workspaceReady) { SyncToolbar(); SyncInspector(); }
    }

    /// <summary>Editor-only grid colour. Outline only — a filled grid would hide the room.</summary>
    public float[] GridColor { get; private set; } = { 1f, 1f, 1f, 0.06f };

    public void SetGridColor(float r, float g, float b, float a)
    {
        GridColor = new[] { r, g, b, a };
        _viewport?.Host?.Invalidate();
    }

    /// <summary>Builds the Room and Viewports panels.</summary>
    private void BuildRoomPanels()
    {
        _roomSettingsPanel = new Panel { AutoScroll = true, BackColor = EditorChrome.Surface, Dock = DockStyle.Fill };
        _viewportsPanel = new Panel { AutoScroll = true, BackColor = EditorChrome.Surface, Dock = DockStyle.Fill };

        BuildRoomSettingsLayout();
        BuildViewportsLayout();
    }

    // ── View toolbar ────────────────────────────────────────────────────────────

    /// <summary>
    /// One toolbar-dropdown action row, showing its keyboard shortcut the way a menu does.
    /// </summary>
    /// <remarks>
    /// The shortcut text is displayed, never registered: these actions already reach the editor
    /// through its own key handling, and registering them here would claim the key before the
    /// focused control ever saw it — the mistake NEXT-099 fixed at the shell level.
    /// </remarks>
    private static ToolStripLabel ToolbarCaption(string text) => new(text)
    {
        AutoSize = true,
        ForeColor = EditorChrome.Muted,
        Margin = new Padding(0, 0, 4, 0),
        Padding = new Padding(0, 2, 0, 0),
    };

    private static ToolStripMenuItem MenuAction(
        string text,
        string shortcut,
        string tooltip,
        Action action)
    {
        ToolStripMenuItem item = new(text)
        {
            ShortcutKeyDisplayString = shortcut,
            ToolTipText = tooltip,
        };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// The View menu: what the editor draws. Layers, node types, viewport outlines and the grid,
    /// all as checkable items rather than a dialog, because these are toggles a designer flips
    /// constantly while composing rather than settings they configure once.
    /// </summary>
    private ToolStripDropDownButton BuildViewMenu()
    {
        ToolStripMenuItem layers = new("Layers (depth)");
        ToolStripMenuItem types = new("Types");
        types.DropDownItems.Add(KindItem("Objects", RoomNodeKind.GameObject, () => ShowObjects));
        types.DropDownItems.Add(KindItem("Tile Layers", RoomNodeKind.TileLayer, () => ShowTileLayers));
        types.DropDownItems.Add(KindItem("Backgrounds", RoomNodeKind.Background, () => ShowBackgrounds));
        types.DropDownItems.Add(KindItem("Terrain", RoomNodeKind.Terrain, () => ShowTerrain));

        ToolStripMenuItem viewports = new("Viewports");
        ToolStripMenuItem showOutlines = new("Show viewport outlines")
        {
            CheckOnClick = true,
            Checked = ShowViewportOutlines,
        };
        showOutlines.CheckedChanged += (_, _) => SetViewportOutlinesVisible(showOutlines.Checked);
        viewports.DropDownItems.Add(showOutlines);
        viewports.DropDownItems.Add(new ToolStripSeparator());
        ToolStripMenuItem allViewports = new("All viewports") { CheckOnClick = false };
        allViewports.Click += (_, _) => SetViewportOutlineFilter(-1);
        viewports.DropDownItems.Add(allViewports);
        for (int i = 0; i < RoomAsset.MaxViewports; i++)
        {
            int index = i;
            ToolStripMenuItem only = new($"Only viewport {index}");
            only.Click += (_, _) => SetViewportOutlineFilter(index);
            viewports.DropDownItems.Add(only);
        }

        ToolStripMenuItem snapItem = new("Snap to grid") { CheckOnClick = true, Checked = _room.Settings.SnapEnabled };
        snapItem.CheckedChanged += (_, _) =>
        {
            if (_room.Settings.SnapEnabled != snapItem.Checked) ToggleSnap();
        };
        ToolStripMenuItem gridColour = new("Grid colour…");
        gridColour.Click += (_, _) => PickGridColour();

        ToolStripDropDownButton menu = EditorViewMenuChrome.BuildViewMenu(
            "Show, hide and filter what the room view draws",
            grid: new EditorViewMenuChrome.GridBinding
            {
                Read = () => ShowGrid,
                Write = SetGridVisible,
                Invalidate = () => _viewport?.Host?.Invalidate(),
            },
            floorStyle: new EditorViewMenuChrome.FloorStyleBinding
            {
                Read = () => _floorStyle,
                Write = SetFloorStyle,
                Invalidate = () => _viewport?.Host?.Invalidate(),
            },
            leadingItems: [layers, types, viewports],
            extraItems: [snapItem, gridColour],
            viewport: () => _viewport);
        menu.DropDownOpening += (_, _) => RebuildLayerVisibilityItems(layers);
        return menu;
    }

    private ToolStripMenuItem KindItem(string caption, RoomNodeKind kind, Func<bool> read)
    {
        ToolStripMenuItem item = new(caption) { CheckOnClick = true, Checked = read() };
        item.CheckedChanged += (_, _) => SetKindVisible(kind, item.Checked);
        return item;
    }

    private void RebuildLayerVisibilityItems(ToolStripMenuItem host)
    {
        host.DropDownItems.Clear();
        foreach (RoomLayer layer in _room.Layers)
        {
            RoomLayer captured = layer;
            ToolStripMenuItem item = new($"{layer.Name}  (order {layer.Order})")
            {
                CheckOnClick = true,
                Checked = layer.Enabled,
            };
            item.CheckedChanged += (_, _) => SetLayerEnabled(captured, item.Checked);
            host.DropDownItems.Add(item);
        }

        if (host.DropDownItems.Count == 0) host.DropDownItems.Add(new ToolStripMenuItem("(no layers)") { Enabled = false });
    }

    /// <summary>Layer visibility, undoable, shared by the outliner checkbox and the View menu.</summary>
    private void SetLayerEnabled(RoomLayer layer, bool enabled)
    {
        if (layer.Enabled == enabled) return;
        bool previous = layer.Enabled;
        layer.Enabled = enabled;
        RefreshLayers();
        RefreshOutliner();
        _viewport?.Host?.Invalidate();
        PushEdit(enabled ? "Show layer" : "Hide layer",
            () => { layer.Enabled = enabled; RefreshLayers(); RefreshOutliner(); _viewport?.Host?.Invalidate(); },
            () => { layer.Enabled = previous; RefreshLayers(); RefreshOutliner(); _viewport?.Host?.Invalidate(); });
    }

    private void PickGridColour()
    {
        using ColorDialog dialog = new()
        {
            Color = Color.FromArgb(To255(GridColor[0]), To255(GridColor[1]), To255(GridColor[2])),
            FullOpen = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        // Alpha is kept from the current value: a grid at full opacity buries the room, and asking
        // for a colour is not asking to be blinded by it.
        SetGridColor(dialog.Color.R / 255f, dialog.Color.G / 255f, dialog.Color.B / 255f, GridColor[3]);
    }

    // ── Room settings ───────────────────────────────────────────────────────────

    private void BuildRoomSettingsLayout()
    {
        int y = 8;
        RoomPanelBuilder builder = new(_roomSettingsPanel, () => y, value => y = value);

        // A room's name IS its file name — Save() derives it from the resource path, as every
        // other resource in the application does. An editable box here would revert on the next
        // save, so it reports the name and points at where renaming actually happens.
        _roomNameBox = new TextBox { ReadOnly = true, Width = 216 };
        EditorChrome.StyleField(_roomNameBox);
        _nameHintLabel = new Label
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Height = 30,
            Text = "Rename the room in the Asset Browser.",
            Width = 216,
        };

        _roomFrameRate = MakeNumeric(0, 1000, 0);
        _roomPhysicsRate = MakeNumeric(1, 1000, 0);
        _roomSettingsWidth = MakeNumeric(1, 100000, 0);
        _roomSettingsHeight = MakeNumeric(1, 100000, 0);
        _roomSettingsDepth = MakeNumeric(1, 100000, 0);
        _roomPersistentCheck = new CheckBox
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            ForeColor = EditorChrome.Text,
            Text = "Persistent (keep state when revisited)",
        };
        _roomBackgroundButton = new Button { Height = 24, Text = "Background Colour…", Width = 216 };
        EditorChrome.StyleField(_roomBackgroundButton);
        _roomBackgroundButton.Click += (_, _) => PickBackgroundColour();
        _dynamicSkyCheck = new CheckBox
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            ForeColor = EditorChrome.Text,
            Text = "Dynamic day/night sky",
        };
        _automaticWeatherCheck = new CheckBox
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            ForeColor = EditorChrome.Text,
            Text = "Automatic seeded weather",
        };
        _weatherCombo = new ThemedComboBox { Width = 130 };
        foreach (WeatherKind weather in Enum.GetValues<WeatherKind>()) _weatherCombo.Items.Add(weather);
        EditorChrome.StyleField(_weatherCombo);
        _timeOfDay = MakeNumeric(0, 24, 2);
        _timeScale = MakeNumeric(0, 100000, 1);
        _climateSeed = MakeNumeric(0, int.MaxValue, 0);
        _dayOfYear = MakeNumeric(1, 366, 0);
        _latitude = MakeNumeric(-89, 89, 2);
        _atmospherePreset = new ThemedComboBox { Width = 130 };
        foreach (AtmospherePreset preset in Enum.GetValues<AtmospherePreset>()) _atmospherePreset.Items.Add(preset);
        EditorChrome.StyleField(_atmospherePreset);
        _volumetricCloudsCheck = new CheckBox
        {
            AutoSize = true, BackColor = Color.Transparent, ForeColor = EditorChrome.Text,
            Text = "Volumetric cloud field",
        };
        _cloudQuality = new ThemedComboBox { Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
        _cloudQuality.Items.AddRange(["Performance", "Balanced", "Quality", "Cinematic"]);
        EditorChrome.StyleField(_cloudQuality);
        _cloudBaseHeight = MakeNumeric(10, 10000, 1);
        _cloudThickness = MakeNumeric(5, 2000, 1);
        _cloudCoverage = MakeNumeric(0, 4, 2);
        _atmosphericHaze = MakeNumeric(0, 1, 2);
        _soundscapeButton = new Button { Height = 26, Text = "Adaptive Soundscape…", Width = 216 };
        EditorChrome.StyleField(_soundscapeButton);
        _soundscapeButton.Click += (_, _) => EditAdaptiveSoundscape();

        _roomFrameRate.ValueChanged += (_, _) => CommitRoomSettings();
        _roomPhysicsRate.ValueChanged += (_, _) => CommitRoomSettings();
        _roomSettingsWidth.ValueChanged += (_, _) => CommitRoomSettings();
        _roomSettingsHeight.ValueChanged += (_, _) => CommitRoomSettings();
        _roomSettingsDepth.ValueChanged += (_, _) => CommitRoomSettings();
        _roomPersistentCheck.CheckedChanged += (_, _) => CommitRoomSettings();
        _dynamicSkyCheck.CheckedChanged += (_, _) => CommitEnvironmentSettings();
        _automaticWeatherCheck.CheckedChanged += (_, _) => CommitEnvironmentSettings();
        _weatherCombo.SelectedIndexChanged += (_, _) => CommitEnvironmentSettings();
        _timeOfDay.ValueChanged += (_, _) => CommitEnvironmentSettings();
        _timeScale.ValueChanged += (_, _) => CommitEnvironmentSettings();
        _climateSeed.ValueChanged += (_, _) => CommitEnvironmentSettings();
        _dayOfYear.ValueChanged += (_, _) => CommitEnvironmentSettings();
        _latitude.ValueChanged += (_, _) => CommitEnvironmentSettings();
        _atmospherePreset.SelectedIndexChanged += (_, _) => CommitEnvironmentSettings();
        _volumetricCloudsCheck.CheckedChanged += (_, _) => CommitEnvironmentSettings();
        _cloudQuality.SelectedIndexChanged += (_, _) => CommitEnvironmentSettings();
        _cloudBaseHeight.ValueChanged += (_, _) => CommitEnvironmentSettings();
        _cloudThickness.ValueChanged += (_, _) => CommitEnvironmentSettings();
        _cloudCoverage.ValueChanged += (_, _) => CommitEnvironmentSettings();
        _atmosphericHaze.ValueChanged += (_, _) => CommitEnvironmentSettings();

        builder.Section("Identity");
        builder.Full(_roomNameBox);
        builder.Full(_nameHintLabel);
        builder.Section("Timing");
        builder.Row("Frame Rate (FPS)", _roomFrameRate);
        builder.Row("Physics Step (Hz)", _roomPhysicsRate);
        builder.Section("Dimensions");
        builder.Row("Width", _roomSettingsWidth);
        builder.Row("Height", _roomSettingsHeight);
        builder.Row("Depth", _roomSettingsDepth);
        builder.Section("Behaviour");
        builder.Full(_roomPersistentCheck);
        builder.Full(_roomBackgroundButton);
        builder.Section("Environment");
        builder.Full(_dynamicSkyCheck);
        builder.Row("Weather", _weatherCombo);
        builder.Row("Time of day", _timeOfDay);
        builder.Row("Time scale", _timeScale);
        builder.Full(_automaticWeatherCheck);
        builder.Row("Climate seed", _climateSeed);
        builder.Row("Day of year", _dayOfYear);
        builder.Row("Latitude", _latitude);
        builder.Section("Atmosphere & Clouds");
        builder.Row("Preset", _atmospherePreset);
        builder.Full(_volumetricCloudsCheck);
        builder.Row("Cloud quality", _cloudQuality);
        builder.Row("Cloud base", _cloudBaseHeight);
        builder.Row("Cloud thickness", _cloudThickness);
        builder.Row("Cloud coverage", _cloudCoverage);
        builder.Row("Haze", _atmosphericHaze);
        builder.Section("Environment Audio");
        builder.Full(_soundscapeButton);

        SyncRoomSettings();
    }

    /// <summary>Loads the room's settings into the fields without raising commits.</summary>
    private void SyncRoomSettings()
    {
        _syncingInspector = true;
        try
        {
            _roomNameBox.Text = _room.Name;
            _roomFrameRate.Value = Math.Clamp(_room.Settings.TargetFps, 0, 1000);
            _roomPhysicsRate.Value = Math.Clamp(_room.Settings.FixedFps, 1, 1000);
            _roomSettingsWidth.Value = Math.Clamp(_room.Settings.Width, 1, 100000);
            _roomSettingsHeight.Value = Math.Clamp(_room.Settings.Height, 1, 100000);
            _roomSettingsDepth.Value = Math.Clamp(_room.Settings.Depth, 1, 100000);
            _roomPersistentCheck.Checked = _room.Settings.Persistent;
            RoomEnvironment environment = _room.Environment ?? new RoomEnvironment();
            _dynamicSkyCheck.Checked = environment.DynamicSky;
            _automaticWeatherCheck.Checked = environment.AutomaticWeather;
            _weatherCombo.SelectedItem = Enum.TryParse(environment.Weather, true, out WeatherKind weather)
                ? weather
                : WeatherKind.Clear;
            _timeOfDay.Value = Math.Clamp((decimal)environment.TimeOfDayHours, _timeOfDay.Minimum, _timeOfDay.Maximum);
            _timeScale.Value = Math.Clamp((decimal)environment.TimeScale, _timeScale.Minimum, _timeScale.Maximum);
            _climateSeed.Value = Math.Clamp(environment.ClimateSeed, 0, int.MaxValue);
            _dayOfYear.Value = Math.Clamp(environment.DayOfYear, 1, 366);
            _latitude.Value = Math.Clamp((decimal)environment.LatitudeDegrees, _latitude.Minimum, _latitude.Maximum);
            _atmospherePreset.SelectedItem = Enum.TryParse(environment.AtmospherePreset, true, out AtmospherePreset atmosphere)
                ? atmosphere
                : AtmospherePreset.Natural;
            _volumetricCloudsCheck.Checked = environment.VolumetricClouds;
            _cloudQuality.SelectedIndex = Math.Clamp(environment.CloudQuality, 0, 3);
            _cloudBaseHeight.Value = Math.Clamp((decimal)environment.CloudBaseHeight, _cloudBaseHeight.Minimum, _cloudBaseHeight.Maximum);
            _cloudThickness.Value = Math.Clamp((decimal)environment.CloudThickness, _cloudThickness.Minimum, _cloudThickness.Maximum);
            _cloudCoverage.Value = Math.Clamp((decimal)environment.CloudCoverageScale, _cloudCoverage.Minimum, _cloudCoverage.Maximum);
            _atmosphericHaze.Value = Math.Clamp((decimal)environment.AtmosphericHaze, _atmosphericHaze.Minimum, _atmosphericHaze.Maximum);

            // Depth is meaningless in a 2D room, so it is disabled rather than quietly ignored.
            _roomSettingsDepth.Enabled = _room.Dimension == RoomDimension.ThreeD;
        }
        finally
        {
            _syncingInspector = false;
        }
    }

    private void CommitRoomSettings()
    {
        if (_syncingInspector) return;

        RoomSettings previous = CloneSettings(_room.Settings);
        RoomSettings next = CloneSettings(_room.Settings);
        next.TargetFps = (int)_roomFrameRate.Value;
        next.FixedFps = (int)_roomPhysicsRate.Value;
        next.Width = (int)_roomSettingsWidth.Value;
        next.Height = (int)_roomSettingsHeight.Value;
        next.Depth = (int)_roomSettingsDepth.Value;
        next.Persistent = _roomPersistentCheck.Checked;
        if (SettingsEqual(previous, next)) return;

        ApplySettings(next);
        PushEdit("Room settings",
            () => ApplySettings(next),
            () => ApplySettings(previous));
    }

    private void ApplySettings(RoomSettings source)
    {
        RoomSettings target = _room.Settings;
        target.TargetFps = source.TargetFps;
        target.FixedFps = source.FixedFps;
        target.Width = source.Width;
        target.Height = source.Height;
        target.Depth = source.Depth;
        target.Persistent = source.Persistent;
        target.VSync = source.VSync;
        target.CaptureMouse = source.CaptureMouse;
        target.VoxelWorld = source.VoxelWorld;
        target.GridSize = source.GridSize;
        target.SnapEnabled = source.SnapEnabled;
        target.MetricGridSize = source.MetricGridSize;
        target.SnapToTerrain = source.SnapToTerrain;
        target.AlignToTerrainNormal = source.AlignToTerrainNormal;
        target.Normalize();
        SyncRoomSettings();
        _snapButton.Checked = target.SnapEnabled;
        _gridSizeBox.Text = target.GridSize.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        _viewport?.Host?.Invalidate();
    }

    private static RoomSettings CloneSettings(RoomSettings source) => new()
    {
        Width = source.Width,
        Height = source.Height,
        Depth = source.Depth,
        TargetFps = source.TargetFps,
        FixedFps = source.FixedFps,
        Persistent = source.Persistent,
        VSync = source.VSync,
        CaptureMouse = source.CaptureMouse,
        VoxelWorld = source.VoxelWorld,
        GridSize = source.GridSize,
        SnapEnabled = source.SnapEnabled,
        MetricGridSize = source.MetricGridSize,
        SnapToTerrain = source.SnapToTerrain,
        AlignToTerrainNormal = source.AlignToTerrainNormal,
        RandomYaw = source.RandomYaw,
        RandomYawDegrees = source.RandomYawDegrees,
        PlacementRandomSeed = source.PlacementRandomSeed,
        PlacementRandomSequence = source.PlacementRandomSequence,
    };

    private static bool SettingsEqual(RoomSettings a, RoomSettings b) =>
        a.Width == b.Width && a.Height == b.Height && a.Depth == b.Depth &&
        a.TargetFps == b.TargetFps && a.FixedFps == b.FixedFps && a.Persistent == b.Persistent
        && a.VSync == b.VSync && a.CaptureMouse == b.CaptureMouse
        && a.VoxelWorld == b.VoxelWorld && Math.Abs(a.GridSize - b.GridSize) < 0.0001f
        && a.SnapEnabled == b.SnapEnabled && a.MetricGridSize == b.MetricGridSize
        && a.SnapToTerrain == b.SnapToTerrain && a.AlignToTerrainNormal == b.AlignToTerrainNormal
        && a.RandomYaw == b.RandomYaw && a.RandomYawDegrees == b.RandomYawDegrees
        && a.PlacementRandomSeed == b.PlacementRandomSeed && a.PlacementRandomSequence == b.PlacementRandomSequence;

    private void PickBackgroundColour()
    {
        float[] current = _room.Environment.BackgroundColor;
        using ColorDialog dialog = new()
        {
            Color = current is { Length: >= 3 }
                ? Color.FromArgb(To255(current[0]), To255(current[1]), To255(current[2]))
                : Color.Black,
            FullOpen = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        float[] previous = (float[])(current ?? new[] { 0.07f, 0.09f, 0.14f, 1f }).Clone();
        float[] next = { dialog.Color.R / 255f, dialog.Color.G / 255f, dialog.Color.B / 255f, 1f };
        _room.Environment.BackgroundColor = next;
        _viewport?.Host?.Invalidate();
        PushEdit("Room background colour",
            () => { _room.Environment.BackgroundColor = next; _viewport?.Host?.Invalidate(); },
            () => { _room.Environment.BackgroundColor = previous; _viewport?.Host?.Invalidate(); });
    }

    private void CommitEnvironmentSettings()
    {
        if (_syncingInspector) return;
        RoomEnvironment previous = CloneEnvironment(_room.Environment);
        RoomEnvironment next = CloneEnvironment(_room.Environment);
        next.DynamicSky = _dynamicSkyCheck.Checked;
        next.AutomaticWeather = _automaticWeatherCheck.Checked;
        next.Weather = (_weatherCombo.SelectedItem is WeatherKind weather ? weather : WeatherKind.Clear).ToString();
        next.TimeOfDayHours = (float)_timeOfDay.Value;
        next.TimeScale = (float)_timeScale.Value;
        next.ClimateSeed = (int)_climateSeed.Value;
        next.DayOfYear = (int)_dayOfYear.Value;
        next.LatitudeDegrees = (float)_latitude.Value;
        next.AtmospherePreset = (_atmospherePreset.SelectedItem is AtmospherePreset atmosphere
            ? atmosphere : AtmospherePreset.Natural).ToString();
        next.VolumetricClouds = _volumetricCloudsCheck.Checked;
        next.CloudQuality = Math.Clamp(_cloudQuality.SelectedIndex, 0, 3);
        next.CloudBaseHeight = (float)_cloudBaseHeight.Value;
        next.CloudThickness = (float)_cloudThickness.Value;
        next.CloudCoverageScale = (float)_cloudCoverage.Value;
        next.AtmosphericHaze = (float)_atmosphericHaze.Value;
        ApplyEnvironment(next);
        PushEdit("Room environment", () => ApplyEnvironment(next), () => ApplyEnvironment(previous));
    }

    private void ApplyEnvironment(RoomEnvironment source)
    {
        _room.Environment = CloneEnvironment(source);
        SyncRoomSettings();
        _viewport?.Host?.Invalidate();
    }

    private static RoomEnvironment CloneEnvironment(RoomEnvironment source)
    {
        source ??= new RoomEnvironment();
        return new RoomEnvironment
        {
            BackgroundColor = source.BackgroundColor is null ? [0.07f, 0.09f, 0.14f, 1f] : (float[])source.BackgroundColor.Clone(),
            Gravity = source.Gravity is null ? [0f, -9.81f, 0f] : (float[])source.Gravity.Clone(),
            AmbientIntensity = source.AmbientIntensity,
            FogDensity = source.FogDensity,
            DynamicSky = source.DynamicSky,
            Weather = source.Weather,
            TimeOfDayHours = source.TimeOfDayHours,
            TimeScale = source.TimeScale,
            AutomaticWeather = source.AutomaticWeather,
            ClimateSeed = source.ClimateSeed,
            DayOfYear = source.DayOfYear,
            LatitudeDegrees = source.LatitudeDegrees,
            AtmospherePreset = source.AtmospherePreset,
            VolumetricClouds = source.VolumetricClouds,
            CloudQuality = source.CloudQuality,
            CloudBaseHeight = source.CloudBaseHeight,
            CloudThickness = source.CloudThickness,
            CloudCoverageScale = source.CloudCoverageScale,
            AtmosphericHaze = source.AtmosphericHaze,
            WindAudio = source.WindAudio,
            RainAudio = source.RainAudio,
            WaterAudio = source.WaterAudio,
            FireAudio = source.FireAudio,
            WildlifeAudio = source.WildlifeAudio,
            NightAudio = source.NightAudio,
            SoundscapeLevels = (source.SoundscapeLevels ?? new()).Clone(),
        };
    }

    private void EditAdaptiveSoundscape()
    {
        RoomEnvironment previous = CloneEnvironment(_room.Environment);
        using RoomSoundscapeDialog dialog = new(previous, ProjectRoot);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        RoomEnvironment next = CloneEnvironment(previous);
        dialog.ApplyTo(next);
        ApplyEnvironment(next);
        PushEdit("Adaptive room soundscape", () => ApplyEnvironment(next), () => ApplyEnvironment(previous));
    }

    private Mesh3DState CreateRoomSceneLighting()
    {
        Mesh3DState state = EditorSceneLighting.Create(showFloor: ShowEditorFloor && !IsGameCameraPreview);
        RoomEnvironment environment = _room.Environment;
        if (environment is null)
        {
            SkyAuthoringDefaults.Apply(ref state);
            ApplyRoomFogPreview(ref state);
            return state;
        }

        // AF2.6: always stamp room climate / cloud authoring onto Mesh3DState (preview = F5).
        state.DayOfYear = SkyAuthoringDefaults.ClampDayOfYear(environment.DayOfYear);
        state.LatitudeDegrees = SkyAuthoringDefaults.ClampLatitude(environment.LatitudeDegrees);
        state.CloudBaseHeight = SkyAuthoringDefaults.ClampBaseHeight(environment.CloudBaseHeight);
        state.CloudThickness = SkyAuthoringDefaults.ClampThickness(environment.CloudThickness);
        state.CloudCoverageScale =
            SkyAuthoringDefaults.ClampCoverageScale(environment.CloudCoverageScale);
        state.CloudDensityScale = SkyAuthoringDefaults.ClampDensityScale(
            SkyAuthoringDefaults.CloudDensityScale);

        ApplyRoomEnvironmentPreview(ref state);
        ApplyRoomFogPreview(ref state);
        return state;
    }

    private static int To255(float value) => (int)Math.Clamp(value * 255f, 0f, 255f);

    // ── Viewports ───────────────────────────────────────────────────────────────

    private void BuildViewportsLayout()
    {
        int y = 8;
        RoomPanelBuilder builder = new(_viewportsPanel, () => y, value => y = value);
        _viewportFieldCount = 0;

        _viewportSlotCombo = new ThemedComboBox { Width = 216 };
        for (int i = 0; i < RoomAsset.MaxViewports; i++) _viewportSlotCombo.Items.Add($"View {i + 1} (Viewport {i})");
        _viewportSlotCombo.SelectedIndex = 0;
        _viewportSlotCombo.SelectedIndexChanged += (_, _) =>
        {
            _activeViewport = Math.Max(0, _viewportSlotCombo.SelectedIndex);
            SyncViewportFields();
        };
        EditorChrome.StyleField(_viewportSlotCombo);

        _viewportEnabledCheck = new CheckBox
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            ForeColor = EditorChrome.Text,
            Text = "Enabled in game",
        };
        _viewportEnabledCheck.CheckedChanged += (_, _) => CommitViewport();

        for (int i = 0; i < 6; i++) _viewportSource[i] = MakeNumeric(-1000000, 1000000, 1);
        for (int i = 0; i < 4; i++) _viewportPort[i] = MakeNumeric(0, 100000, 0);
        for (int i = 0; i < 3; i++) _viewportMargin[i] = MakeNumeric(0, 100000, 1);
        for (int i = 0; i < 3; i++) _viewportSpeed[i] = MakeNumeric(-1, 100000, 1);
        _viewportFrustum[0] = MakeNumeric(0.01m, 10000m, 2); // Near
        _viewportFrustum[1] = MakeNumeric(1m, 100000m, 1); // Far
        _viewportFrustum[2] = MakeNumeric(1m, 179m, 1); // FOV

        _viewportFollowCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 216 };
        EditorChrome.StyleField(_viewportFollowCombo);
        _viewportFollowCombo.TextChanged += (_, _) => CommitViewport();

        _viewportEditorVisibleCheck = new CheckBox
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            ForeColor = EditorChrome.Text,
            Text = "Show outline in the editor",
        };
        _viewportEditorVisibleCheck.CheckedChanged += (_, _) => CommitViewport();

        _viewportColorButton = new Button { Height = 24, Text = "Outline Colour...", Width = 216 };
        EditorChrome.StyleField(_viewportColorButton);
        _viewportColorButton.Click += (_, _) => PickViewportColour();

        _viewportFillCombo = new ThemedComboBox { Width = 216 };
        _viewportFillCombo.Items.AddRange(new object[] { "Outline", "Filled", "Both" });
        _viewportFillCombo.SelectedIndex = 0;
        EditorChrome.StyleField(_viewportFillCombo);
        _viewportFillCombo.SelectedIndexChanged += (_, _) => CommitViewport();

        foreach (NumericUpDown field in _viewportSource) field.ValueChanged += (_, _) => CommitViewport();
        foreach (NumericUpDown field in _viewportPort) field.ValueChanged += (_, _) => CommitViewport();
        foreach (NumericUpDown field in _viewportMargin) field.ValueChanged += (_, _) => CommitViewport();
        foreach (NumericUpDown field in _viewportSpeed) field.ValueChanged += (_, _) => CommitViewport();
        foreach (NumericUpDown field in _viewportFrustum) field.ValueChanged += (_, _) => CommitViewport();

        builder.Section("Viewport");
        builder.Full(_viewportSlotCombo);
        builder.Full(_viewportEnabledCheck);

        builder.Section("Source Region (room)");
        Track(builder.Row("X", _viewportSource[0]));
        Track(builder.Row("Y", _viewportSource[1]));
        Track(builder.Row("Z", _viewportSource[2]));
        Track(builder.Row("Width", _viewportSource[3]));
        Track(builder.Row("Height", _viewportSource[4]));
        Track(builder.Row("Depth", _viewportSource[5]));

        builder.Section("Destination Port (window)");
        Track(builder.Row("X", _viewportPort[0]));
        Track(builder.Row("Y", _viewportPort[1]));
        Track(builder.Row("Width", _viewportPort[2]));
        Track(builder.Row("Height", _viewportPort[3]));

        builder.Section("3D Camera Config");
        Track(builder.Row("Frustum Near", _viewportFrustum[0]));
        Track(builder.Row("Frustum Far", _viewportFrustum[1]));
        Track(builder.Row("Field of View", _viewportFrustum[2]));

        builder.Section("Follow");
        Track(builder.Full(_viewportFollowCombo));
        Track(builder.Row("Margin X", _viewportMargin[0]));
        Track(builder.Row("Margin Y", _viewportMargin[1]));
        Track(builder.Row("Margin Z", _viewportMargin[2]));
        Track(builder.Row("Speed X", _viewportSpeed[0]));
        Track(builder.Row("Speed Y", _viewportSpeed[1]));
        Track(builder.Row("Speed Z", _viewportSpeed[2]));

        builder.Section("Editor Display");
        Track(builder.Full(_viewportEditorVisibleCheck));
        Track(builder.Full(_viewportColorButton));
        Track(builder.Full(_viewportFillCombo));

        SyncViewportFields();
    }

    private Control Track(Control control)
    {
        if (_viewportFieldCount < _viewportFields.Length) _viewportFields[_viewportFieldCount++] = control;
        return control;
    }

    private RoomViewport? ActiveViewport =>
        _room.Viewports is { Count: > 0 } && _activeViewport >= 0 && _activeViewport < _room.Viewports.Count
            ? _room.Viewports[_activeViewport]
            : null;

    /// <summary>Loads the selected slot into the fields and greys them when it is disabled.</summary>
    private void SyncViewportFields()
    {
        RoomViewport? viewport = ActiveViewport;
        if (viewport is null) return;

        _syncingViewport = true;
        try
        {
            _viewportEnabledCheck.Checked = viewport.Enabled;
            _viewportSource[0].Value = Clamp(_viewportSource[0], (decimal)viewport.SourceX);
            _viewportSource[1].Value = Clamp(_viewportSource[1], (decimal)viewport.SourceY);
            _viewportSource[2].Value = Clamp(_viewportSource[2], (decimal)viewport.SourceZ);
            _viewportSource[3].Value = Clamp(_viewportSource[3], (decimal)viewport.SourceWidth);
            _viewportSource[4].Value = Clamp(_viewportSource[4], (decimal)viewport.SourceHeight);
            _viewportSource[5].Value = Clamp(_viewportSource[5], (decimal)viewport.SourceDepth);
            _viewportPort[0].Value = Clamp(_viewportPort[0], viewport.PortX);
            _viewportPort[1].Value = Clamp(_viewportPort[1], viewport.PortY);
            _viewportPort[2].Value = Clamp(_viewportPort[2], viewport.PortWidth);
            _viewportPort[3].Value = Clamp(_viewportPort[3], viewport.PortHeight);
            _viewportMargin[0].Value = Clamp(_viewportMargin[0], (decimal)viewport.FollowMarginX);
            _viewportMargin[1].Value = Clamp(_viewportMargin[1], (decimal)viewport.FollowMarginY);
            _viewportMargin[2].Value = Clamp(_viewportMargin[2], (decimal)viewport.FollowMarginZ);
            _viewportSpeed[0].Value = Clamp(_viewportSpeed[0], (decimal)viewport.FollowSpeedX);
            _viewportSpeed[1].Value = Clamp(_viewportSpeed[1], (decimal)viewport.FollowSpeedY);
            _viewportSpeed[2].Value = Clamp(_viewportSpeed[2], (decimal)viewport.FollowSpeedZ);
            _viewportSpeed[2].Value = Clamp(_viewportSpeed[2], (decimal)viewport.FollowSpeedZ);
            _viewportFrustum[0].Value = Clamp(_viewportFrustum[0], (decimal)viewport.FrustumNear);
            _viewportFrustum[1].Value = Clamp(_viewportFrustum[1], (decimal)viewport.FrustumFar);
            _viewportFrustum[2].Value = Clamp(_viewportFrustum[2], (decimal)viewport.FieldOfView);
            _viewportFollowCombo.Text = viewport.FollowTarget ?? string.Empty;
            _viewportEditorVisibleCheck.Checked = viewport.EditorVisible;
            _viewportFillCombo.SelectedIndex = (int)viewport.EditorFillStyle;

            RefreshFollowTargetChoices();

            // A disabled viewport's settings stay readable but cannot be edited, so it is obvious
            // which slot is live without the values disappearing.
            bool editable = viewport.Enabled;
            for (int i = 0; i < _viewportFieldCount; i++)
                if (_viewportFields[i] is not null) _viewportFields[i].Enabled = editable;

            bool threeD = _room.Dimension == RoomDimension.ThreeD;
            _viewportSource[2].Enabled = editable && threeD;
            _viewportSource[5].Enabled = editable && threeD;
            _viewportMargin[2].Enabled = editable && threeD;
            _viewportSpeed[2].Enabled = editable && threeD;
        }
        finally
        {
            _syncingViewport = false;
        }
        SyncCameraWorkspaceFields();
    }

    private static decimal Clamp(NumericUpDown field, decimal value) =>
        Math.Clamp(value, field.Minimum, field.Maximum);

    private void RefreshFollowTargetChoices()
    {
        string current = _viewportFollowCombo.Text;
        _viewportFollowCombo.Items.Clear();
        _viewportFollowCombo.Items.Add(string.Empty);
        foreach (RoomNode node in _room.Nodes)
        {
            if (node.Kind != RoomNodeKind.GameObject) continue;
            string? prefab = node.GameObject?.Prefab;
            if (string.IsNullOrWhiteSpace(prefab)) continue;
            string name = ObjectNameMatcher.Normalize(prefab);
            int slash = name.LastIndexOf('/');
            if (slash >= 0) name = name[(slash + 1)..];
            if (!_viewportFollowCombo.Items.Contains(name)) _viewportFollowCombo.Items.Add(name);
        }

        _viewportFollowCombo.Text = current;
    }

    private void CommitViewport()
    {
        if (_syncingViewport || _syncingInspector) return;
        RoomViewport? viewport = ActiveViewport;
        if (viewport is null) return;

        int index = _activeViewport;
        RoomViewport previous = viewport.Clone();
        RoomViewport next = viewport.Clone();

        next.Enabled = _viewportEnabledCheck.Checked;
        next.SourceX = (float)_viewportSource[0].Value;
        next.SourceY = (float)_viewportSource[1].Value;
        next.SourceZ = (float)_viewportSource[2].Value;
        next.SourceWidth = (float)_viewportSource[3].Value;
        next.SourceHeight = (float)_viewportSource[4].Value;
        next.SourceDepth = (float)_viewportSource[5].Value;
        next.PortX = (int)_viewportPort[0].Value;
        next.PortY = (int)_viewportPort[1].Value;
        next.PortWidth = (int)_viewportPort[2].Value;
        next.PortHeight = (int)_viewportPort[3].Value;
        next.FollowMarginX = (float)_viewportMargin[0].Value;
        next.FollowMarginY = (float)_viewportMargin[1].Value;
        next.FollowMarginZ = (float)_viewportMargin[2].Value;
        next.FollowSpeedX = (float)_viewportSpeed[0].Value;
        next.FollowSpeedY = (float)_viewportSpeed[1].Value;
        next.FollowSpeedZ = (float)_viewportSpeed[2].Value;
        next.FrustumNear = (float)_viewportFrustum[0].Value;
        next.FrustumFar = (float)_viewportFrustum[1].Value;
        next.FieldOfView = (float)_viewportFrustum[2].Value;
        next.FollowTarget = _viewportFollowCombo.Text?.Trim() ?? string.Empty;
        next.EditorVisible = _viewportEditorVisibleCheck.Checked;
        next.EditorFillStyle = (RoomViewportFillStyle)Math.Max(0, _viewportFillCombo.SelectedIndex);
        next.Normalize();

        ApplyViewport(index, next);
        PushEdit($"Viewport {index}",
            () => ApplyViewport(index, next),
            () => ApplyViewport(index, previous));
    }

    private void ApplyViewport(int index, RoomViewport source)
    {
        if (index < 0 || index >= _room.Viewports.Count) return;
        _room.Viewports[index] = source.Clone();
        if (index == _activeViewport) SyncViewportFields();
        _viewport?.Host?.Invalidate();
    }

    public void SetViewportLayout(int count)
    {
        if (count is not (1 or 2 or 4)) throw new ArgumentOutOfRangeException(nameof(count));
        RoomViewport[] before = _room.Viewports.Select(viewport => viewport.Clone()).ToArray();
        RoomViewport[] after = before.Select(viewport => viewport.Clone()).ToArray();
        int columns = count == 1 ? 1 : 2, rows = count == 4 ? 2 : 1;
        int width = Math.Max(columns, _room.Settings.Width), height = Math.Max(rows, _room.Settings.Height);
        for (int index = 0; index < after.Length; index++)
        {
            after[index].Enabled = index < count;
            if (index >= count) continue;
            int column = index % columns, row = index / columns;
            after[index].PortX = width * column / columns; after[index].PortY = height * row / rows;
            after[index].PortWidth = width * (column + 1) / columns - after[index].PortX;
            after[index].PortHeight = height * (row + 1) / rows - after[index].PortY;
        }
        void Apply(RoomViewport[] viewports)
        {
            _room.Viewports.Clear(); _room.Viewports.AddRange(viewports.Select(viewport => viewport.Clone()));
            SyncViewportFields(); RefreshPhase4Ui();
        }
        Apply(after);
        PushEdit($"Set {count} camera output(s)", () => Apply(after), () => Apply(before));
        UpdateStatus($"{count} camera output(s). Choose each view to set its position and follow target.");
    }

    private void PickViewportColour()
    {
        RoomViewport? viewport = ActiveViewport;
        if (viewport is null) return;

        float[] current = viewport.EditorColor;
        using ColorDialog dialog = new()
        {
            Color = current is { Length: >= 3 }
                ? Color.FromArgb(To255(current[0]), To255(current[1]), To255(current[2]))
                : Color.DeepSkyBlue,
            FullOpen = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        int index = _activeViewport;
        RoomViewport previous = viewport.Clone();
        RoomViewport next = viewport.Clone();
        next.EditorColor = new[] { dialog.Color.R / 255f, dialog.Color.G / 255f, dialog.Color.B / 255f, 1f };

        ApplyViewport(index, next);
        PushEdit($"Viewport {index} colour",
            () => ApplyViewport(index, next),
            () => ApplyViewport(index, previous));
    }

    /// <summary>
    /// Draws each visible viewport's source region over the room, so a designer can see where the
    /// player's camera will sit — and, with several enabled, how a split screen carves the room up.
    /// </summary>
    /// <remarks>
    /// Drawn in the 2D scene pass rather than the screen overlay because these are regions of the
    /// <i>room</i>, so they have to pan and zoom with it. Depth is above the room fill but below the
    /// selection gizmos, which must stay clickable-looking on top.
    /// </remarks>
    private void DrawViewportRegions2D(Genesis.Shared.Interfaces.IRenderController renderer)
    {
        if (!ShowViewportOutlines || _room.Viewports is null) return;

        for (int index = 0; index < _room.Viewports.Count; index++)
        {
            RoomViewport viewport = _room.Viewports[index];
            if (!viewport.EditorVisible) continue;
            if (ViewportOutlineFilter >= 0 && ViewportOutlineFilter != index) continue;

            float[] rgba = viewport.EditorColor;
            var colour = new Genesis.Shared.Interfaces.RenderColor(rgba[0], rgba[1], rgba[2], rgba[3]);

            // A disabled slot still draws, but faintly: it is a plan, not a camera the game uses.
            float alpha = viewport.Enabled ? 1f : 0.35f;

            if (viewport.EditorFillStyle is RoomViewportFillStyle.Filled or RoomViewportFillStyle.Both)
            {
                renderer.DrawRect(
                    viewport.SourceX, viewport.SourceY, viewport.SourceWidth, viewport.SourceHeight,
                    new Genesis.Shared.Interfaces.RenderColor(rgba[0], rgba[1], rgba[2], 0.12f * alpha),
                    filled: true,
                    depth: 8600);
            }

            if (viewport.EditorFillStyle is RoomViewportFillStyle.Outline or RoomViewportFillStyle.Both)
            {
                float thickness = 2f / MathF.Max(0.0001f, _viewport.Zoom2D);
                var outline = new Genesis.Shared.Interfaces.RenderColor(rgba[0], rgba[1], rgba[2], rgba[3] * alpha);
                float x = viewport.SourceX, y = viewport.SourceY;
                float w = viewport.SourceWidth, h = viewport.SourceHeight;
                renderer.DrawLine(x, y, x + w, y, outline, thickness, depth: 8500);
                renderer.DrawLine(x + w, y, x + w, y + h, outline, thickness, depth: 8500);
                renderer.DrawLine(x + w, y + h, x, y + h, outline, thickness, depth: 8500);
                renderer.DrawLine(x, y + h, x, y, outline, thickness, depth: 8500);
            }

            // The follow margin is the dead zone the target moves inside before the camera scrolls.
            // Showing it is what makes the number tunable by eye rather than by trial and error.
            if (viewport.Enabled && !string.IsNullOrWhiteSpace(viewport.FollowTarget))
            {
                float marginX = Math.Clamp(viewport.FollowMarginX, 0f, viewport.SourceWidth * 0.5f);
                float marginY = Math.Clamp(viewport.FollowMarginY, 0f, viewport.SourceHeight * 0.5f);
                float mx = viewport.SourceX + marginX;
                float my = viewport.SourceY + marginY;
                float mw = viewport.SourceWidth - (marginX * 2f);
                float mh = viewport.SourceHeight - (marginY * 2f);
                if (mw > 1f && mh > 1f)
                {
                    float thin = 1f / MathF.Max(0.0001f, _viewport.Zoom2D);
                    var dead = new Genesis.Shared.Interfaces.RenderColor(rgba[0], rgba[1], rgba[2], 0.45f * alpha);
                    renderer.DrawLine(mx, my, mx + mw, my, dead, thin, depth: 8500);
                    renderer.DrawLine(mx + mw, my, mx + mw, my + mh, dead, thin, depth: 8500);
                    renderer.DrawLine(mx + mw, my + mh, mx, my + mh, dead, thin, depth: 8500);
                    renderer.DrawLine(mx, my + mh, mx, my, dead, thin, depth: 8500);
                }
            }
        }
    }

    private const float CameraMarkerSize = 0.5f;

    private void DrawViewportRegions3D(Genesis.Shared.Interfaces.IRenderController renderer)
    {
        if (!ShowViewportOutlines || _room.Viewports is null || _viewport.Mode2D) return;

        for (int index = 0; index < _room.Viewports.Count; index++)
        {
            RoomViewport viewport = _room.Viewports[index];
            if (!viewport.EditorVisible) continue;
            if (ViewportOutlineFilter >= 0 && ViewportOutlineFilter != index) continue;

            float[] rgba = viewport.EditorColor;
            float alpha = viewport.Enabled ? 1f : 0.35f;
            var outline = new Genesis.Shared.Interfaces.RenderColor(rgba[0], rgba[1], rgba[2], rgba[3] * alpha);

            ResolveViewportFrustumPose(
                index, viewport,
                out Vector3 eye, out Vector3 forward,
                out float fovDegrees, out float nearPlane, out float farPlane, out float aspect);

            DrawGizmoBox3D(renderer, eye, CameraMarkerSize, outline);

            // Follow tracking treats Source XYZ as the minimum edge, in both dimensions.
            if (viewport.EditorFillStyle is RoomViewportFillStyle.Filled or RoomViewportFillStyle.Both)
            {
                var volume = new Genesis.Shared.Interfaces.RenderColor(rgba[0], rgba[1], rgba[2], 0.35f * alpha);
                Vector3 source = new(viewport.SourceX, viewport.SourceY, viewport.SourceZ);
                DrawGizmoAabb3D(
                    renderer,
                    source,
                    source + new Vector3(viewport.SourceWidth, viewport.SourceHeight, viewport.SourceDepth),
                    volume);
            }

            if (TryGetViewportDeadZoneBounds3D(index, out Vector3 deadMin, out Vector3 deadMax))
            {
                var deadZone = new Genesis.Shared.Interfaces.RenderColor(1f, .76f, .32f, .72f * alpha);
                DrawGizmoAabb3D(renderer, deadMin, deadMax, deadZone);
            }

            if (viewport.Enabled && fovDegrees > 0f)
            {
                DrawFrustumWires3D(renderer, eye, forward, fovDegrees, nearPlane, farPlane, aspect, outline);
            }
        }

        DrawGameCameraFrustum3D(renderer);
    }

    private void DrawGameCameraFrustum3D(Genesis.Shared.Interfaces.IRenderController renderer)
    {
        RoomNode? camera = ActiveGameCamera;
        if (camera is null || ViewportOutlineFilter >= 0) return;

        RoomCameraState? state = new RoomSceneBuilder(ProjectRoot).ResolveCameraState(_room, camera);
        if (state is null || !state.HasCameraComponent) return;

        Vector3 forward = MathUtil.DirectionFromYawPitch(state.YawRadians, state.PitchRadians);
        float aspect = _viewport.SurfaceWidth / (float)Math.Max(1, _viewport.SurfaceHeight);
        var outline = new Genesis.Shared.Interfaces.RenderColor(1f, 0.72f, 0.22f, 1f);
        DrawGizmoBox3D(renderer, state.Position, CameraMarkerSize, outline);
        DrawFrustumWires3D(
            renderer,
            state.Position,
            forward,
            state.FieldOfViewDegrees,
            state.NearPlane,
            state.FarPlane,
            aspect,
            outline);
    }

    private void ResolveViewportFrustumPose(
        int index,
        RoomViewport viewport,
        out Vector3 eye,
        out Vector3 forward,
        out float fieldOfViewDegrees,
        out float nearPlane,
        out float farPlane,
        out float aspect)
    {
        eye = new Vector3(viewport.SourceX, viewport.SourceY, viewport.SourceZ);
        fieldOfViewDegrees = viewport.FieldOfView > 0f ? viewport.FieldOfView : 60f;
        nearPlane = viewport.FrustumNear > 0f ? viewport.FrustumNear : 0.1f;
        farPlane = viewport.FrustumFar > nearPlane ? viewport.FrustumFar : nearPlane + 100f;
        aspect = viewport.PortWidth / MathF.Max(1f, viewport.PortHeight);
        forward = -Vector3.UnitZ;

        if (Engine.TryGetCamera3DPose(
                index,
                out Vector3 registryEye,
                out float yawDegrees,
                out float pitchDegrees,
                out float registryFov,
                out float registryAspect,
                out float registryNear,
                out float registryFar))
        {
            eye = registryEye;
            forward = MathUtil.DirectionFromYawPitch(
                yawDegrees * MathUtil.DegToRad,
                pitchDegrees * MathUtil.DegToRad);
            if (registryFov > 0f) fieldOfViewDegrees = registryFov;
            if (registryAspect > 0.01f) aspect = registryAspect;
            if (registryNear > 0f) nearPlane = registryNear;
            if (registryFar > nearPlane) farPlane = registryFar;
            return;
        }

        if (TryResolveFollowLookDirection(viewport, eye, out Vector3 look))
        {
            forward = look;
        }
    }

    private bool TryResolveFollowLookDirection(RoomViewport viewport, Vector3 eye, out Vector3 forward)
    {
        forward = default;
        RoomNode? target = FindFollowTargetNode(viewport.FollowTarget);
        if (target is null) return false;

        RoomTransform world = new RoomSceneBuilder(ProjectRoot).ResolveWorldTransform(_room, target);
        Vector3 targetPos = new(world.X, world.Y, world.Z);
        Vector3 delta = targetPos - eye;
        if (delta.LengthSquared() < 1e-8f) return false;
        forward = Vector3.Normalize(delta);
        return true;
    }

    private RoomNode? FindFollowTargetNode(string? followTarget)
    {
        if (string.IsNullOrWhiteSpace(followTarget)) return null;

        foreach (RoomNode node in _room.Nodes)
        {
            if (node.Kind != RoomNodeKind.GameObject) continue;
            if (string.Equals(node.Name, followTarget, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            string? prefab = node.GameObject?.Prefab;
            if (!string.IsNullOrWhiteSpace(prefab) && ObjectNameMatcher.Matches(prefab, followTarget))
            {
                return node;
            }
        }

        return null;
    }

    private void DrawFrustumWires3D(
        Genesis.Shared.Interfaces.IRenderController renderer,
        Vector3 eye,
        Vector3 forward,
        float fieldOfViewDegrees,
        float nearPlane,
        float farPlane,
        float aspect,
        Genesis.Shared.Interfaces.RenderColor outline)
    {
        if (forward.LengthSquared() < 1e-8f) forward = -Vector3.UnitZ;
        else forward = Vector3.Normalize(forward);

        Vector3 up = Vector3.UnitY;
        Vector3 right = Vector3.Cross(forward, up);
        if (right.LengthSquared() < 1e-8f)
        {
            up = Vector3.UnitX;
            right = Vector3.Cross(forward, up);
        }

        right = Vector3.Normalize(right);
        up = Vector3.Normalize(Vector3.Cross(right, forward));

        float fovRad = fieldOfViewDegrees * MathF.PI / 180f;
        float nearH = 2f * MathF.Tan(fovRad * 0.5f) * nearPlane;
        float nearW = nearH * aspect;
        float farH = 2f * MathF.Tan(fovRad * 0.5f) * farPlane;
        float farW = farH * aspect;

        Vector3 centerNear = eye + forward * nearPlane;
        Vector3 centerFar = eye + forward * farPlane;

        Vector3 ntl = centerNear + (up * nearH * 0.5f) - (right * nearW * 0.5f);
        Vector3 ntr = centerNear + (up * nearH * 0.5f) + (right * nearW * 0.5f);
        Vector3 nbl = centerNear - (up * nearH * 0.5f) - (right * nearW * 0.5f);
        Vector3 nbr = centerNear - (up * nearH * 0.5f) + (right * nearW * 0.5f);

        Vector3 ftl = centerFar + (up * farH * 0.5f) - (right * farW * 0.5f);
        Vector3 ftr = centerFar + (up * farH * 0.5f) + (right * farW * 0.5f);
        Vector3 fbl = centerFar - (up * farH * 0.5f) - (right * farW * 0.5f);
        Vector3 fbr = centerFar - (up * farH * 0.5f) + (right * farW * 0.5f);

        DrawGizmoLine3D(renderer, ntl, ntr, outline);
        DrawGizmoLine3D(renderer, ntr, nbr, outline);
        DrawGizmoLine3D(renderer, nbr, nbl, outline);
        DrawGizmoLine3D(renderer, nbl, ntl, outline);

        DrawGizmoLine3D(renderer, ftl, ftr, outline);
        DrawGizmoLine3D(renderer, ftr, fbr, outline);
        DrawGizmoLine3D(renderer, fbr, fbl, outline);
        DrawGizmoLine3D(renderer, fbl, ftl, outline);

        DrawGizmoLine3D(renderer, ntl, ftl, outline);
        DrawGizmoLine3D(renderer, ntr, ftr, outline);
        DrawGizmoLine3D(renderer, nbl, fbl, outline);
        DrawGizmoLine3D(renderer, nbr, fbr, outline);
    }

    private bool TryHitCameraMarkerCore(Point client, out int viewportIndex, out bool isGameCamera)
    {
        viewportIndex = -1;
        isGameCamera = false;
        if (!ShowViewportOutlines || _viewport.Mode2D) return false;

        (Vector3 origin, Vector3 direction) = _viewport.PickRay(client);
        float bestT = float.MaxValue;
        float half = CameraMarkerSize * 0.5f;

        if (_room.Viewports is not null)
        {
            for (int index = 0; index < _room.Viewports.Count; index++)
            {
                RoomViewport viewport = _room.Viewports[index];
                if (!viewport.EditorVisible) continue;
                if (ViewportOutlineFilter >= 0 && ViewportOutlineFilter != index) continue;

                ResolveViewportFrustumPose(
                    index, viewport,
                    out Vector3 eye, out _, out _, out _, out _, out _);
                Vector3 min = eye - new Vector3(half);
                Vector3 max = eye + new Vector3(half);
                if (RayIntersectsAabb(origin, direction, min, max, out float t) && t < bestT)
                {
                    bestT = t;
                    viewportIndex = index;
                    isGameCamera = false;
                }
            }
        }

        if (ViewportOutlineFilter < 0 && ActiveGameCamera is RoomNode gameCamera)
        {
            RoomCameraState? state = new RoomSceneBuilder(ProjectRoot).ResolveCameraState(_room, gameCamera);
            if (state is { HasCameraComponent: true })
            {
                Vector3 min = state.Position - new Vector3(half);
                Vector3 max = state.Position + new Vector3(half);
                if (RayIntersectsAabb(origin, direction, min, max, out float t) && t < bestT)
                {
                    bestT = t;
                    viewportIndex = -1;
                    isGameCamera = true;
                }
            }
        }

        return bestT < float.MaxValue;
    }

    private void DrawGizmoAabb3D(
        Genesis.Shared.Interfaces.IRenderController renderer,
        Vector3 min,
        Vector3 max,
        Genesis.Shared.Interfaces.RenderColor color)
    {
        Vector3 c0 = new(min.X, min.Y, min.Z);
        Vector3 c1 = new(max.X, min.Y, min.Z);
        Vector3 c2 = new(max.X, max.Y, min.Z);
        Vector3 c3 = new(min.X, max.Y, min.Z);
        Vector3 c4 = new(min.X, min.Y, max.Z);
        Vector3 c5 = new(max.X, min.Y, max.Z);
        Vector3 c6 = new(max.X, max.Y, max.Z);
        Vector3 c7 = new(min.X, max.Y, max.Z);

        DrawGizmoLine3D(renderer, c0, c1, color);
        DrawGizmoLine3D(renderer, c1, c2, color);
        DrawGizmoLine3D(renderer, c2, c3, color);
        DrawGizmoLine3D(renderer, c3, c0, color);
        DrawGizmoLine3D(renderer, c4, c5, color);
        DrawGizmoLine3D(renderer, c5, c6, color);
        DrawGizmoLine3D(renderer, c6, c7, color);
        DrawGizmoLine3D(renderer, c7, c4, color);
        DrawGizmoLine3D(renderer, c0, c4, color);
        DrawGizmoLine3D(renderer, c1, c5, color);
        DrawGizmoLine3D(renderer, c2, c6, color);
        DrawGizmoLine3D(renderer, c3, c7, color);
    }

    private void DrawGizmoLine3D(Genesis.Shared.Interfaces.IRenderController renderer, Vector3 a, Vector3 b, Genesis.Shared.Interfaces.RenderColor color)
    {
        Vector3 sa = _viewport.WorldToSurface(a);
        Vector3 sb = _viewport.WorldToSurface(b);
        if (sa.Z is > 0f and < 1f && sb.Z is > 0f and < 1f)
        {
            renderer.DrawLine(sa.X, sa.Y, sb.X, sb.Y, color, 2f, depth: -8999);
        }
    }

    private void DrawGizmoBox3D(Genesis.Shared.Interfaces.IRenderController renderer, Vector3 center, float size, Genesis.Shared.Interfaces.RenderColor color)
    {
        float h = size * 0.5f;
        Vector3 c0 = center + new Vector3(-h, -h, -h);
        Vector3 c1 = center + new Vector3(h, -h, -h);
        Vector3 c2 = center + new Vector3(h, h, -h);
        Vector3 c3 = center + new Vector3(-h, h, -h);
        Vector3 c4 = center + new Vector3(-h, -h, h);
        Vector3 c5 = center + new Vector3(h, -h, h);
        Vector3 c6 = center + new Vector3(h, h, h);
        Vector3 c7 = center + new Vector3(-h, h, h);

        DrawGizmoLine3D(renderer, c0, c1, color);
        DrawGizmoLine3D(renderer, c1, c2, color);
        DrawGizmoLine3D(renderer, c2, c3, color);
        DrawGizmoLine3D(renderer, c3, c0, color);

        DrawGizmoLine3D(renderer, c4, c5, color);
        DrawGizmoLine3D(renderer, c5, c6, color);
        DrawGizmoLine3D(renderer, c6, c7, color);
        DrawGizmoLine3D(renderer, c7, c4, color);

        DrawGizmoLine3D(renderer, c0, c4, color);
        DrawGizmoLine3D(renderer, c1, c5, color);
        DrawGizmoLine3D(renderer, c2, c6, color);
        DrawGizmoLine3D(renderer, c3, c7, color);
    }

    /// <summary>
    /// Lays out a stack of labelled rows. Extracted so the Room and Viewports pages read the same
    /// as the Inspector without three copies of the same arithmetic.
    /// </summary>
    private sealed class RoomPanelBuilder
    {
        private readonly Panel _host;
        private readonly Func<int> _read;
        private readonly Action<int> _write;

        public RoomPanelBuilder(Panel host, Func<int> read, Action<int> write)
        {
            _host = host;
            _read = read;
            _write = write;
        }

        public void Section(string text)
        {
            int y = _read();
            _host.Controls.Add(new Label
            {
                AutoSize = true,
                BackColor = Color.Transparent,
                Font = EditorChrome.HeadingFont,
                ForeColor = EditorChrome.Muted,
                Location = new Point(12, y),
                Text = text.ToUpperInvariant(),
            });
            _write(y + 22);
        }

        public Control Full(Control control)
        {
            int y = _read();
            control.Location = new Point(12, y);
            _host.Controls.Add(control);
            _write(y + control.Height + 6);
            return control;
        }

        public Control Row(string caption, Control field)
        {
            int y = _read();
            Panel row = new()
            {
                BackColor = Color.Transparent,
                Location = new Point(12, y),
                Size = new Size(232, 26),
            };
            row.Controls.Add(new Label
            {
                AutoSize = false,
                BackColor = Color.Transparent,
                Font = EditorChrome.SmallFont,
                ForeColor = EditorChrome.Muted,
                Location = new Point(0, 6),
                Size = new Size(96, 18),
                Text = caption,
            });
            field.Location = new Point(100, 0);
            row.Controls.Add(field);
            _host.Controls.Add(row);
            _write(y + 30);
            return row;
        }
    }
}
