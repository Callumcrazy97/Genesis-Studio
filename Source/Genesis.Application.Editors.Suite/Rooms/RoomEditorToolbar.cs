using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.Rooms;

/// <summary>Room commands, hosted in the document's single command bar.</summary>
public sealed class RoomEditorToolbar : Panel
{
    private ToolStrip _strip = new() { Dock = DockStyle.Fill, GripStyle = ToolStripGripStyle.Hidden };
    private readonly ToolStripButton _btn2D = new("2D");
    private readonly ToolStripButton _btn3D = new("3D");
    private readonly ToolStripButton _btnGrid = new("Grid") { CheckOnClick = true };
    private readonly ToolStripButton _btnSnap = new("Snap") { CheckOnClick = true };
    private readonly ToolStripComboBox _snapSize = new() { AutoSize = false, Width = 74, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly ToolStripButton _terrain = new("Terrain snap") { CheckOnClick = true, ToolTipText = "Place and move on the terrain surface" };
    private readonly ToolStripDropDownButton _btnCamera = new("Camera");
    private readonly ToolStripSplitButton _btnPlay = new("▶ Play") { Alignment = ToolStripItemAlignment.Right, Overflow = ToolStripItemOverflow.Never };
    private readonly ToolStripButton _btnPause = new("‖ Pause") { Alignment = ToolStripItemAlignment.Right, Enabled = false, Overflow = ToolStripItemOverflow.Never };
    private readonly ToolStripButton _btnStop = new("■ Stop") { Alignment = ToolStripItemAlignment.Right, Enabled = false, Overflow = ToolStripItemOverflow.Never };
    private bool _syncing;
    private bool _is2D = true;

    public event Action<bool>? DimensionChanged;
    public event Action? ZoomInRequested;
    public event Action? ZoomOutRequested;
    public event Action? ResetViewRequested;
    public event Action<bool>? GridToggled;
    public event Action<bool>? SnapToggled;
    public event Action<float>? SnapSizeChanged;
    public event Action<bool>? TerrainSnapChanged;
    public event Action? CameraMenuOpening;
    public event Action? PlayRequested;
    public event Action? PauseRequested;
    public event Action? StopRequested;
    public event Action? PlayExternalRequested;

    public RoomEditorToolbar()
    {
        Dock = DockStyle.Top;
        Height = EditorChrome.CommandBarHeight;
        _btn2D.Click += (_, _) => DimensionChanged?.Invoke(true);
        _btn3D.Click += (_, _) => DimensionChanged?.Invoke(false);
        _btnGrid.Click += (_, _) => GridToggled?.Invoke(_btnGrid.Checked);
        _btnSnap.Click += (_, _) => SnapToggled?.Invoke(_btnSnap.Checked);
        _terrain.Click += (_, _) => TerrainSnapChanged?.Invoke(_terrain.Checked);
        _snapSize.SelectedIndexChanged += (_, _) => CommitSnap();
        _snapSize.Leave += (_, _) => CommitSnap();
        _snapSize.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { CommitSnap(); e.SuppressKeyPress = true; } };
        _btnCamera.DropDownOpening += (_, _) =>
        {
            CameraMenuOpening?.Invoke();
            _btnCamera.DropDownItems.Add(new ToolStripSeparator());
            _btnCamera.DropDownItems.Add("Zoom in", null, (_, _) => ZoomInRequested?.Invoke());
            _btnCamera.DropDownItems.Add("Zoom out", null, (_, _) => ZoomOutRequested?.Invoke());
            _btnCamera.DropDownItems.Add("Reset view", null, (_, _) => ResetViewRequested?.Invoke());
        };
        _btnPlay.ButtonClick += (_, _) => PlayRequested?.Invoke();
        _btnPause.Click += (_, _) => PauseRequested?.Invoke();
        _btnStop.Click += (_, _) => StopRequested?.Invoke();
        _btnPlay.DropDownItems.Add("Restart in Genesis Player", null, (_, _) => PlayExternalRequested?.Invoke());
        Controls.Add(_strip);
    }

    public ToolStrip Strip => _strip;
    public ToolStripDropDownButton CameraDropdown => _btnCamera;
    public ToolStripComboBox SnapSizePicker => _snapSize;

    public void AttachTo(ToolStrip commandBar, ToolStripItem[] menus, ToolStripItem[] transforms)
    {
        ToolStrip orphan = _strip;
        _strip = commandBar;
        _strip.Items.Clear();
        _strip.Items.AddRange(menus);
        _strip.Items.Add(new ToolStripSeparator());
        _strip.Items.AddRange([_btn2D, _btn3D, new ToolStripSeparator()]);
        _strip.Items.AddRange(transforms);
        _strip.Items.AddRange([new ToolStripSeparator(), _btnGrid, _btnSnap, _snapSize, _terrain, _btnCamera,
            _btnStop, _btnPause, _btnPlay]);
        foreach (ToolStripItem item in _strip.Items)
        {
            item.ForeColor = EditorChrome.Text;
            if (item is ToolStripButton or ToolStripSplitButton) item.Padding = new Padding(5, 2, 5, 2);
        }
        Controls.Remove(orphan);
        orphan.Dispose();
    }

    public void SyncTerrain(bool visible, bool enabled)
    {
        _terrain.Available = visible;
        _terrain.Checked = enabled;
    }

    public void Sync(bool is2D, bool gridVisible, bool snapEnabled, float gridSize, bool isSimulating, bool isPaused)
    {
        _syncing = true;
        try
        {
            _btn2D.Checked = is2D;
            _btn3D.Checked = !is2D;
            _btnGrid.Checked = gridVisible;
            _btnSnap.Checked = snapEnabled;
            if (_snapSize.Items.Count == 0 || _is2D != is2D)
            {
                _snapSize.Items.Clear();
                _snapSize.Items.AddRange(is2D ? ["8 px", "16 px", "32 px", "64 px"] : ["0.1 m", "0.5 m", "1 m", "2 m"]);
            }
            _is2D = is2D;
            if (!_snapSize.Focused) _snapSize.Text = gridSize.ToString("0.##", CultureInfo.InvariantCulture) + (is2D ? " px" : " m");
            _btnPlay.Available = !isSimulating || isPaused;
            _btnPlay.Text = isPaused ? "▶ Resume" : "▶ Play";
            _btnPlay.ForeColor = EditorChrome.Success;
            _btnPause.Available = isSimulating && !isPaused;
            _btnPause.Enabled = isSimulating && !isPaused;
            _btnPause.ForeColor = EditorChrome.Warning;
            _btnStop.Enabled = isSimulating;
            _btnStop.ForeColor = isSimulating ? EditorChrome.Error : EditorChrome.Muted;
        }
        finally { _syncing = false; }
    }

    private void CommitSnap()
    {
        if (_syncing) return;
        string text = _snapSize.Text.Replace("px", "", StringComparison.OrdinalIgnoreCase).Replace("m", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value) && value >= .01f)
            SnapSizeChanged?.Invoke(value);
    }
}
