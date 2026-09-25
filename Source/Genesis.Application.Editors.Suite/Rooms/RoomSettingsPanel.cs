using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Runtime.Scene;

namespace Genesis.Application.Editors.Suite.Rooms;

/// <summary>
/// Left subpanel for Room Settings: dimensions, speed, persistent, background clear,
/// grid & snapping, and room boundary options.
/// </summary>
public sealed class RoomSettingsPanel : Panel
{
    private readonly RoomEditorControl _editor;
    private readonly NumericUpDown _numWidth;
    private readonly NumericUpDown _numHeight;
    private readonly NumericUpDown _numWindowWidth;
    private readonly NumericUpDown _numWindowHeight;
    private readonly CheckBox _chkScaleViewports;
    private readonly CheckBox _chkPixelArt;
    private readonly NumericUpDown _numSpeed;
    private readonly CheckBox _chkPersistent;
    private readonly CheckBox _chkClearBuffer;
    private readonly Button _btnRoomColor;
    private readonly CheckBox _chkShowGrid;
    private readonly CheckBox _chkSnapToGrid;
    private readonly NumericUpDown _numGridSize;
    private readonly Button _btnGridColor;
    private readonly CheckBox _chkShowBounds;
    private readonly Button _btnBoundsColor;
    private bool _syncing;

    public RoomSettingsPanel(RoomEditorControl editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        Dock = DockStyle.Fill;
        AutoScroll = true;
        BackColor = EditorChrome.Surface;
        Padding = new Padding(8);

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 16,
            BackColor = Color.Transparent,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55f));

        Label heading = new()
        {
            Text = "Room Settings",
            Font = EditorChrome.HeadingFont,
            ForeColor = EditorChrome.Text,
            Dock = DockStyle.Top,
            Height = 26,
        };

        _numWidth = MakeNumeric(1, 100000, 0);
        _numWidth.ValueChanged += (_, _) => CommitSettings();

        _numHeight = MakeNumeric(1, 100000, 0);
        _numHeight.ValueChanged += (_, _) => CommitSettings();

        _numWindowWidth = MakeNumeric(0, 16384, 0);
        _numWindowHeight = MakeNumeric(0, 16384, 0);
        _chkScaleViewports = new CheckBox { Text = "Fit 2D viewports on resize", ForeColor = EditorChrome.Text, AutoSize = true };
        _chkPixelArt = new CheckBox { Text = "Crisp pixel-art sampling (2D)", ForeColor = EditorChrome.Text, AutoSize = true };
        _chkPixelArt.CheckedChanged += (_, _) => CommitDisplay();
        _numWindowWidth.ValueChanged += (_, _) => CommitDisplay();
        _numWindowHeight.ValueChanged += (_, _) => CommitDisplay();
        _chkScaleViewports.CheckedChanged += (_, _) => CommitDisplay();

        _numSpeed = MakeNumeric(0, 1000, 0);
        _numSpeed.ValueChanged += (_, _) => CommitSettings();

        _chkPersistent = new CheckBox { Text = "Persistent", ForeColor = EditorChrome.Text, AutoSize = true };
        _chkPersistent.CheckedChanged += (_, _) => CommitSettings();

        _chkClearBuffer = new CheckBox { Text = "Clear display buffer", ForeColor = EditorChrome.Text, AutoSize = true, Checked = true };
        _chkClearBuffer.CheckedChanged += (_, _) => CommitSettings();

        _btnRoomColor = new Button
        {
            Height = 24,
            Width = 60,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(18, 23, 36),
        };
        _btnRoomColor.Click += (_, _) => PickRoomColor();

        _chkShowGrid = new CheckBox { Text = "Show grid", ForeColor = EditorChrome.Text, AutoSize = true, Checked = true };
        _chkShowGrid.CheckedChanged += (_, _) =>
        {
            if (!_syncing) _editor.SetGridVisible(_chkShowGrid.Checked);
        };

        _chkSnapToGrid = new CheckBox { Text = "Snap to grid", ForeColor = EditorChrome.Text, AutoSize = true, Checked = true };
        _chkSnapToGrid.CheckedChanged += (_, _) =>
        {
            if (!_syncing) _editor.SetSnapEnabled(_chkSnapToGrid.Checked);
        };

        _numGridSize = MakeNumeric(1, 4096, 0);
        _numGridSize.ValueChanged += (_, _) =>
        {
            if (!_syncing) _editor.SetGridSize((float)_numGridSize.Value);
        };

        _btnGridColor = new Button { Height = 24, Width = 60, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(80, 80, 100) };
        _btnGridColor.Click += (_, _) => PickGridColor();

        _chkShowBounds = new CheckBox { Text = "Show room bounds", ForeColor = EditorChrome.Text, AutoSize = true, Checked = true };
        _chkShowBounds.CheckedChanged += (_, _) =>
        {
            if (!_syncing) _editor.SetRoomBoundsVisible(_chkShowBounds.Checked);
        };

        _btnBoundsColor = new Button { Height = 24, Width = 60, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(108, 140, 255) };
        _btnBoundsColor.Click += (_, _) => PickBoundsColor();

        int row = 0;
        void AddRow(string labelText, Control field)
        {
            Label lbl = new() { Text = labelText, ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont, AutoSize = true, Anchor = AnchorStyles.Left };
            layout.Controls.Add(lbl, 0, row);
            layout.Controls.Add(field, 1, row);
            row++;
        }

        void AddSection(string title)
        {
            Label s = new()
            {
                Text = title,
                Font = EditorChrome.HeadingFont,
                ForeColor = EditorChrome.Accent,
                Margin = new Padding(0, 10, 0, 4),
                AutoSize = true,
            };
            layout.SetColumnSpan(s, 2);
            layout.Controls.Add(s, 0, row++);
        }

        AddSection("ROOM DIMENSIONS");
        AddRow("Width", _numWidth);
        AddRow("Height", _numHeight);
        AddRow("Speed (FPS)", _numSpeed);

        AddSection("DISPLAY & BUFFER");
        AddRow("Window width (0 = auto)", _numWindowWidth);
        AddRow("Window height (0 = auto)", _numWindowHeight);
        layout.SetColumnSpan(_chkScaleViewports, 2);
        layout.Controls.Add(_chkScaleViewports, 0, row++);
        layout.SetColumnSpan(_chkPixelArt, 2);
        layout.Controls.Add(_chkPixelArt, 0, row++);
        layout.SetColumnSpan(_chkPersistent, 2);
        layout.Controls.Add(_chkPersistent, 0, row++);
        layout.SetColumnSpan(_chkClearBuffer, 2);
        layout.Controls.Add(_chkClearBuffer, 0, row++);
        AddRow("Room colour", _btnRoomColor);

        AddSection("GRID & SNAPPING");
        layout.SetColumnSpan(_chkShowGrid, 2);
        layout.Controls.Add(_chkShowGrid, 0, row++);
        layout.SetColumnSpan(_chkSnapToGrid, 2);
        layout.Controls.Add(_chkSnapToGrid, 0, row++);
        AddRow("Grid size", _numGridSize);
        AddRow("Grid colour", _btnGridColor);

        AddSection("ROOM BOUNDS");
        layout.SetColumnSpan(_chkShowBounds, 2);
        layout.Controls.Add(_chkShowBounds, 0, row++);
        AddRow("Bounds colour", _btnBoundsColor);

        Controls.Add(layout);
        Controls.Add(heading);
    }

    public void SyncFromRoom()
    {
        _syncing = true;
        try
        {
            RoomAsset room = _editor.Room;
            _numWidth.Value = Math.Clamp(room.Settings.Width, _numWidth.Minimum, _numWidth.Maximum);
            _numHeight.Value = Math.Clamp(room.Settings.Height, _numHeight.Minimum, _numHeight.Maximum);
            _numSpeed.Value = Math.Clamp(room.Settings.TargetFps, _numSpeed.Minimum, _numSpeed.Maximum);
            _chkPersistent.Checked = room.Settings.Persistent;
            _numWindowWidth.Value = Math.Clamp(room.Settings.WindowWidth, 0, 16384);
            _numWindowHeight.Value = Math.Clamp(room.Settings.WindowHeight, 0, 16384);
            _chkScaleViewports.Checked = room.Settings.ScaleViewportsWithWindow;
            _chkPixelArt.Checked = room.Settings.PixelArtSampling;

            float[] bg = room.Environment.BackgroundColor;
            if (bg is { Length: >= 3 })
            {
                _btnRoomColor.BackColor = Color.FromArgb((int)(bg[0] * 255), (int)(bg[1] * 255), (int)(bg[2] * 255));
            }

            _chkShowGrid.Checked = _editor.ShowGrid;
            _chkSnapToGrid.Checked = room.Settings.SnapEnabled;
            _numGridSize.Value = (decimal)Math.Clamp(room.Settings.GridSize, 1f, 4096f);

            float[] gridC = _editor.GridColor;
            if (gridC is { Length: >= 3 })
            {
                _btnGridColor.BackColor = Color.FromArgb((int)(gridC[0] * 255), (int)(gridC[1] * 255), (int)(gridC[2] * 255));
            }

            _chkShowBounds.Checked = _editor.ShowRoomBounds;
            _btnBoundsColor.BackColor = _editor.RoomBoundsColor;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void CommitDisplay()
    {
        if (_syncing) return;
        RoomSettings settings = _editor.Room.Settings;
        var previous = (settings.WindowWidth, settings.WindowHeight, settings.ScaleViewportsWithWindow, settings.PixelArtSampling);
        var next = ((int)_numWindowWidth.Value, (int)_numWindowHeight.Value, _chkScaleViewports.Checked, _chkPixelArt.Checked);
        if (previous == next) return;
        void Apply((int Width, int Height, bool Fit, bool PixelArt) value)
        {
            settings.WindowWidth = value.Width; settings.WindowHeight = value.Height;
            settings.ScaleViewportsWithWindow = value.Fit; settings.PixelArtSampling = value.PixelArt;
            SyncFromRoom(); _editor.Viewport?.Host?.Invalidate();
        }
        Apply(next);
        _editor.PushEdit("Change Game Window", () => Apply(next), () => Apply(previous));
        _editor.MarkDirty();
    }

    private void CommitSettings()
    {
        if (_syncing) return;
        RoomAsset room = _editor.Room;
        int oldW = room.Settings.Width;
        int oldH = room.Settings.Height;
        int oldFps = room.Settings.TargetFps;
        int oldFixed = room.Settings.FixedFps;
        bool oldPers = room.Settings.Persistent;

        int newW = (int)_numWidth.Value;
        int newH = (int)_numHeight.Value;
        int newFps = (int)_numSpeed.Value;
        bool newPers = _chkPersistent.Checked;

        if (oldW == newW && oldH == newH && oldFps == newFps && oldPers == newPers) return;

        room.Settings.Width = newW;
        room.Settings.Height = newH;
        room.Settings.TargetFps = newFps;
        room.Settings.FixedFps = oldFixed;
        room.Settings.Persistent = newPers;

        _editor.PushEdit(
            "Change Room Dimensions",
            () =>
            {
                room.Settings.Width = newW;
                room.Settings.Height = newH;
                room.Settings.TargetFps = newFps;
                room.Settings.FixedFps = oldFixed;
                room.Settings.Persistent = newPers;
                SyncFromRoom();
                _editor.Viewport?.Host?.Invalidate();
            },
            () =>
            {
                room.Settings.Width = oldW;
                room.Settings.Height = oldH;
                room.Settings.TargetFps = oldFps;
                room.Settings.FixedFps = oldFixed;
                room.Settings.Persistent = oldPers;
                SyncFromRoom();
                _editor.Viewport?.Host?.Invalidate();
            });

        _editor.MarkDirty();
        _editor.Viewport?.Host?.Invalidate();
    }

    private void PickRoomColor()
    {
        using ColorDialog dialog = new() { Color = _btnRoomColor.BackColor, FullOpen = true };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _btnRoomColor.BackColor = dialog.Color;
            float[] next = [dialog.Color.R / 255f, dialog.Color.G / 255f, dialog.Color.B / 255f, 1f];
            _editor.SetRoomBackgroundColor(next);
        }
    }

    private void PickGridColor()
    {
        using ColorDialog dialog = new() { Color = _btnGridColor.BackColor, FullOpen = true };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _btnGridColor.BackColor = dialog.Color;
            _editor.SetGridColor(dialog.Color.R / 255f, dialog.Color.G / 255f, dialog.Color.B / 255f, 0.15f);
        }
    }

    private void PickBoundsColor()
    {
        using ColorDialog dialog = new() { Color = _btnBoundsColor.BackColor, FullOpen = true };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _btnBoundsColor.BackColor = dialog.Color;
            _editor.RoomBoundsColor = dialog.Color;
            _editor.Viewport?.Host?.Invalidate();
        }
    }

    private static NumericUpDown MakeNumeric(decimal min, decimal max, int decimals)
    {
        NumericUpDown numeric = new()
        {
            DecimalPlaces = decimals,
            Increment = decimals == 0 ? 1 : 0.5m,
            Maximum = max,
            Minimum = min,
            Width = 80,
            BackColor = EditorChrome.Canvas,
            ForeColor = EditorChrome.Text,
        };
        EditorChrome.StyleField(numeric);
        return numeric;
    }
}
