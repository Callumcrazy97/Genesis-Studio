using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Runtime.Scene;

namespace Genesis.Application.Editors.Suite.Rooms;

/// <summary>
/// Subpanel for runtime Viewports and Cameras matching GameMaker / reference design:
/// Enable viewports, clear background, slots 0-7, Camera properties, Viewport properties,
/// and Object Following with tracking margins and speeds.
/// </summary>
public sealed class RoomViewsPanel : Panel
{
    private readonly RoomEditorControl _editor;
    private readonly CheckBox _chkEnableViewports;
    private readonly CheckBox _chkClearBackground;
    private readonly ThemedComboBox _slotCombo;
    private readonly CheckBox _chkVisible;
    private readonly NumericUpDown _camX;
    private readonly NumericUpDown _camY;
    private readonly NumericUpDown _camW;
    private readonly NumericUpDown _camH;
    private readonly NumericUpDown _portX;
    private readonly NumericUpDown _portY;
    private readonly NumericUpDown _portW;
    private readonly NumericUpDown _portH;
    private readonly Button _btnFollowObject;
    private readonly NumericUpDown _hBorder;
    private readonly NumericUpDown _vBorder;
    private readonly NumericUpDown _hSpeed;
    private readonly NumericUpDown _vSpeed;
    private int _activeSlot = 0;
    private bool _syncing;

    public RoomViewsPanel(RoomEditorControl editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        Dock = DockStyle.Fill;
        AutoScroll = true;
        BackColor = EditorChrome.Surface;
        Padding = new Padding(8);

        Label heading = new()
        {
            Text = "Viewports and Cameras",
            Font = EditorChrome.HeadingFont,
            ForeColor = EditorChrome.Text,
            Dock = DockStyle.Top,
            Height = 24,
        };

        _chkEnableViewports = new CheckBox { Text = "Enable Viewports", ForeColor = EditorChrome.Text, AutoSize = true, Checked = false };
        _chkEnableViewports.CheckedChanged += (_, _) => CommitGlobalViewports();

        _chkClearBackground = new CheckBox { Text = "Clear Viewport Background", ForeColor = EditorChrome.Text, AutoSize = true, Checked = true };
        _chkClearBackground.CheckedChanged += (_, _) => CommitGlobalViewports();

        Panel globalPanel = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 4, 0, 8),
        };
        globalPanel.Controls.Add(_chkClearBackground);
        globalPanel.Controls.Add(_chkEnableViewports);
        _chkClearBackground.Dock = DockStyle.Top;
        _chkEnableViewports.Dock = DockStyle.Top;

        _slotCombo = new ThemedComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
        for (int i = 0; i < RoomAsset.MaxViewports; i++)
        {
            _slotCombo.Items.Add($"Viewport {i}");
        }
        _slotCombo.SelectedIndex = 0;
        _slotCombo.SelectedIndexChanged += (_, _) =>
        {
            _activeSlot = _slotCombo.SelectedIndex;
            SyncFromSelectedSlot();
        };

        TableLayoutPanel table = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 18,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 8, 0, 0),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55f));

        _chkVisible = new CheckBox { Text = "Visible", ForeColor = EditorChrome.Text, AutoSize = true, Checked = true };
        _chkVisible.CheckedChanged += (_, _) => CommitActiveSlot();

        _camX = MakeNumeric(-100000, 100000, 0);
        _camY = MakeNumeric(-100000, 100000, 0);
        _camW = MakeNumeric(1, 100000, 0);
        _camH = MakeNumeric(1, 100000, 0);

        _camX.ValueChanged += (_, _) => CommitActiveSlot();
        _camY.ValueChanged += (_, _) => CommitActiveSlot();
        _camW.ValueChanged += (_, _) => CommitActiveSlot();
        _camH.ValueChanged += (_, _) => CommitActiveSlot();

        _portX = MakeNumeric(-100000, 100000, 0);
        _portY = MakeNumeric(-100000, 100000, 0);
        _portW = MakeNumeric(1, 100000, 0);
        _portH = MakeNumeric(1, 100000, 0);

        _portX.ValueChanged += (_, _) => CommitActiveSlot();
        _portY.ValueChanged += (_, _) => CommitActiveSlot();
        _portW.ValueChanged += (_, _) => CommitActiveSlot();
        _portH.ValueChanged += (_, _) => CommitActiveSlot();

        _btnFollowObject = new Button
        {
            Text = "No Object",
            Dock = DockStyle.Fill,
            Height = 26,
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };
        EditorChrome.StyleField(_btnFollowObject);
        _btnFollowObject.Click += (_, _) => PickFollowObject();

        _hBorder = MakeNumeric(0, 100000, 0);
        _vBorder = MakeNumeric(0, 100000, 0);
        _hSpeed = MakeNumeric(-1, 10000, 0);
        _vSpeed = MakeNumeric(-1, 10000, 0);

        _hBorder.ValueChanged += (_, _) => CommitActiveSlot();
        _vBorder.ValueChanged += (_, _) => CommitActiveSlot();
        _hSpeed.ValueChanged += (_, _) => CommitActiveSlot();
        _vSpeed.ValueChanged += (_, _) => CommitActiveSlot();

        int row = 0;
        void AddRow(string labelText, Control field)
        {
            Label lbl = new() { Text = labelText, ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont, AutoSize = true, Anchor = AnchorStyles.Left };
            table.Controls.Add(lbl, 0, row);
            table.Controls.Add(field, 1, row);
            row++;
        }

        void AddSectionHeader(string title)
        {
            Label s = new()
            {
                Text = title,
                Font = EditorChrome.HeadingFont,
                ForeColor = EditorChrome.Accent,
                Margin = new Padding(0, 8, 0, 2),
                AutoSize = true,
            };
            table.SetColumnSpan(s, 2);
            table.Controls.Add(s, 0, row++);
        }

        table.SetColumnSpan(_chkVisible, 2);
        table.Controls.Add(_chkVisible, 0, row++);

        AddSectionHeader("CAMERA PROPERTIES");
        AddRow("X Pos", _camX);
        AddRow("Y Pos", _camY);
        AddRow("Width", _camW);
        AddRow("Height", _camH);

        AddSectionHeader("VIEWPORT PROPERTIES");
        AddRow("X Pos", _portX);
        AddRow("Y Pos", _portY);
        AddRow("Width", _portW);
        AddRow("Height", _portH);

        AddSectionHeader("OBJECT FOLLOWING");
        table.SetColumnSpan(_btnFollowObject, 2);
        table.Controls.Add(_btnFollowObject, 0, row++);
        AddRow("Horizontal Border", _hBorder);
        AddRow("Vertical Border", _vBorder);
        AddRow("Horizontal Speed", _hSpeed);
        AddRow("Vertical Speed", _vSpeed);

        Controls.Add(table);
        Controls.Add(_slotCombo);
        Controls.Add(globalPanel);
        Controls.Add(heading);
    }

    public int SelectedViewportIndex => _activeSlot;

    public void SyncFromRoom()
    {
        _syncing = true;
        try
        {
            _chkEnableViewports.Checked = _editor.Room.UsesViewports;
            SyncFromSelectedSlot();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncFromSelectedSlot()
    {
        _syncing = true;
        try
        {
            if (_activeSlot < 0 || _activeSlot >= _editor.Room.Viewports.Count) return;
            RoomViewport vp = _editor.Room.Viewports[_activeSlot];

            _chkVisible.Checked = vp.Enabled;
            _camX.Value = (decimal)vp.SourceX;
            _camY.Value = (decimal)vp.SourceY;
            _camW.Value = (decimal)Math.Max(1, vp.SourceWidth);
            _camH.Value = (decimal)Math.Max(1, vp.SourceHeight);

            _portX.Value = vp.PortX;
            _portY.Value = vp.PortY;
            _portW.Value = Math.Max(1, vp.PortWidth);
            _portH.Value = Math.Max(1, vp.PortHeight);

            _btnFollowObject.Text = string.IsNullOrWhiteSpace(vp.FollowTarget) ? "No Object (click to pick)" : vp.FollowTarget;
            _hBorder.Value = (decimal)vp.FollowMarginX;
            _vBorder.Value = (decimal)vp.FollowMarginY;
            _hSpeed.Value = (decimal)vp.FollowSpeedX;
            _vSpeed.Value = (decimal)vp.FollowSpeedY;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void CommitGlobalViewports()
    {
        if (_syncing) return;
        bool enabled = _chkEnableViewports.Checked;
        if (_editor.Room.Viewports.Count > 0)
        {
            _editor.Room.Viewports[0].Enabled = enabled;
        }
        _editor.MarkDirty();
        _editor.Viewport?.Host?.Invalidate();
    }

    private void CommitActiveSlot()
    {
        if (_syncing) return;
        if (_activeSlot < 0 || _activeSlot >= _editor.Room.Viewports.Count) return;
        RoomViewport vp = _editor.Room.Viewports[_activeSlot];

        vp.Enabled = _chkVisible.Checked;
        vp.SourceX = (float)_camX.Value;
        vp.SourceY = (float)_camY.Value;
        vp.SourceWidth = (float)_camW.Value;
        vp.SourceHeight = (float)_camH.Value;

        vp.PortX = (int)_portX.Value;
        vp.PortY = (int)_portY.Value;
        vp.PortWidth = (int)_portW.Value;
        vp.PortHeight = (int)_portH.Value;

        vp.FollowMarginX = (float)_hBorder.Value;
        vp.FollowMarginY = (float)_vBorder.Value;
        vp.FollowSpeedX = (float)_hSpeed.Value;
        vp.FollowSpeedY = (float)_vSpeed.Value;

        _editor.MarkDirty();
        _editor.Viewport?.Host?.Invalidate();
    }

    private void PickFollowObject()
    {
        ProjectAssetEntry? asset = AssetPickerService.PickObject(_editor.ProjectRoot, this);
        if (_activeSlot < 0 || _activeSlot >= _editor.Room.Viewports.Count) return;
        RoomViewport vp = _editor.Room.Viewports[_activeSlot];

        if (asset is not null)
        {
            vp.FollowTarget = asset.DisplayName;
            _btnFollowObject.Text = asset.DisplayName;
        }
        else
        {
            vp.FollowTarget = string.Empty;
            _btnFollowObject.Text = "No Object (click to pick)";
        }

        _editor.MarkDirty();
        _editor.Viewport?.Host?.Invalidate();
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
