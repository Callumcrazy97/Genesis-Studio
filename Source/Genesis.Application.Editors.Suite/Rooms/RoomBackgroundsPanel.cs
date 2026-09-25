using System.Drawing;
using DrawingImage = System.Drawing.Image;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Runtime.Scene;

namespace Genesis.Application.Editors.Suite.Rooms;

/// <summary>
/// Subpanel managing 8 background slots/layers with Image or Colour modes,
/// preview box, rendering modes, and scrolling speeds.
/// </summary>
public sealed class RoomBackgroundsPanel : Panel
{
    private readonly RoomEditorControl _editor;
    private readonly ListBox _slotList;
    private readonly ThemedComboBox _modeCombo;
    private readonly Label _assetPathLabel;
    private readonly Button _btnPickAsset;
    private readonly Button _btnPickColor;
    private readonly PictureBox _previewBox;
    private readonly ThemedComboBox _layoutCombo;
    private readonly NumericUpDown _numSpeedX;
    private readonly NumericUpDown _numSpeedY;
    private readonly NumericUpDown _numDepth;
    private readonly CheckBox _chkEnabled;
    private int _selectedSlot = 0;
    private bool _syncing;
    private string? _previewKey;
    public RoomNode? ActiveBackgroundLayer => FindBackgroundNode(_selectedSlot);

    public RoomBackgroundsPanel(RoomEditorControl editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        Dock = DockStyle.Fill;
        AutoScroll = true;
        BackColor = EditorChrome.Surface;
        Padding = new Padding(8);

        Label heading = new()
        {
            Text = "Backgrounds",
            Font = EditorChrome.HeadingFont,
            ForeColor = EditorChrome.Text,
            Dock = DockStyle.Top,
            Height = 24,
        };

        // 8 background slots
        _slotList = new ListBox
        {
            Dock = DockStyle.Top,
            Height = 120,
            BackColor = EditorChrome.Canvas,
            ForeColor = EditorChrome.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = EditorChrome.BaseFont,
        };
        for (int i = 1; i <= 8; i++)
        {
            _slotList.Items.Add($"Background {i}");
        }
        _slotList.SelectedIndex = 0;
        _slotList.SelectedIndexChanged += (_, _) =>
        {
            _selectedSlot = _slotList.SelectedIndex;
            _editor.ActiveRoomEditContextChanged();
            SyncFromSelectedSlot();
        };

        Panel configHost = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 8, 0, 0),
        };

        TableLayoutPanel table = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 12,
            BackColor = Color.Transparent,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60f));

        _chkEnabled = new CheckBox { Text = "Visible / Enabled", ForeColor = EditorChrome.Text, AutoSize = true, Checked = true };
        _chkEnabled.CheckedChanged += (_, _) => CommitBackground();

        _modeCombo = new ThemedComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _modeCombo.Items.AddRange(["Image", "Colour"]);
        _modeCombo.SelectedIndex = 0;
        _assetPathLabel = new Label
        {
            Text = "(No Image)",
            ForeColor = EditorChrome.Muted,
            Font = EditorChrome.SmallFont,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _btnPickAsset = new Button
        {
            Text = "Select Image…",
            Dock = DockStyle.Fill,
            Height = 26,
            FlatStyle = FlatStyle.Flat,
        };
        EditorChrome.StyleField(_btnPickAsset);
        _btnPickAsset.Click += (_, _) => PickBackgroundImage();

        _btnPickColor = new Button
        {
            Text = "Pick Colour…",
            Dock = DockStyle.Fill,
            Height = 26,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(18, 23, 36),
        };
        EditorChrome.StyleField(_btnPickColor);
        _btnPickColor.Click += (_, _) => PickBackgroundColor();

        // Preview box
        _previewBox = new PictureBox
        {
            Height = 90,
            Dock = DockStyle.Top,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = EditorChrome.Canvas,
            SizeMode = PictureBoxSizeMode.Zoom,
            Margin = new Padding(0, 4, 0, 8),
        };

        _layoutCombo = new ThemedComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _layoutCombo.Items.AddRange(["Normal (Room Position)", "Stretch to Room", "Tiled", "Stretch to View"]);
        _layoutCombo.SelectedIndex = 0;
        _layoutCombo.SelectedIndexChanged += (_, _) => CommitBackground();

        _modeCombo.SelectedIndexChanged += (_, _) =>
        {
            UpdateModeVisibility();
            // A colour-only slot has no positioned image to reveal. When the author switches it
            // back to Image, choose the camera-filling presentation as the visible/safe default;
            // they can still deliberately select Normal afterwards.
            if (!_syncing && _modeCombo.SelectedIndex == 0
                && string.IsNullOrWhiteSpace(ActiveBackgroundLayer?.Background?.Asset)
                && _layoutCombo.SelectedIndex == 0)
            {
                _syncing = true;
                try { _layoutCombo.SelectedIndex = 3; }
                finally { _syncing = false; }
            }
            CommitBackground();
        };

        _numSpeedX = MakeNumeric(-1000, 1000, 1);
        _numSpeedX.ValueChanged += (_, _) => CommitBackground();

        _numSpeedY = MakeNumeric(-1000, 1000, 1);
        _numSpeedY.ValueChanged += (_, _) => CommitBackground();

        _numDepth = MakeNumeric(-100000, 100000, 0);
        _numDepth.Value = 1000;
        _numDepth.ValueChanged += (_, _) => CommitBackground();

        int row = 0;
        void AddRow(string labelText, Control field)
        {
            field.Dock = DockStyle.Fill;
            field.Margin = new Padding(3, 5, 3, 5);
            Label lbl = new() { Text = labelText, ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont, AutoSize = true, Anchor = AnchorStyles.Left };
            table.Controls.Add(lbl, 0, row);
            table.Controls.Add(field, 1, row);
            row++;
        }

        table.SetColumnSpan(_chkEnabled, 2);
        table.Controls.Add(_chkEnabled, 0, row++);
        AddRow("Source", _modeCombo);
        AddRow("Asset", _btnPickAsset);
        AddRow("Colour", _btnPickColor);
        AddRow("Mode", _layoutCombo);
        AddRow("H Speed", _numSpeedX);
        AddRow("V Speed", _numSpeedY);
        AddRow("Depth", _numDepth);

        configHost.Controls.Add(table);
        configHost.Controls.Add(_previewBox);

        Label previewLbl = new()
        {
            Text = "PREVIEW",
            Font = EditorChrome.HeadingFont,
            ForeColor = EditorChrome.Accent,
            Dock = DockStyle.Top,
            Height = 20,
        };
        configHost.Controls.Add(previewLbl);

        Controls.Add(configHost);
        Controls.Add(_slotList);
        Controls.Add(heading);
    }

    public void InvalidatePreview() => _previewKey = null;

    private bool CanEditSlot => _editor.Navigation.CurrentSection == RoomNavSection.Backgrounds
        && (ActiveBackgroundLayer is not { } node || _editor.CanEditNodeInActiveContext(node));

    public void SyncFromRoom()
    {
        SyncFromSelectedSlot();
        _slotList.Invalidate();
    }

    public void SyncFromSelectedSlot()
    {
        _syncing = true;
        try
        {
            RoomNode? node = FindBackgroundNode(_selectedSlot);
            if (node?.Background is not null)
            {
                RoomBackgroundData bg = node.Background;
                _chkEnabled.Checked = node.Enabled;
                bool isColorOnly = string.IsNullOrWhiteSpace(bg.Asset);
                _modeCombo.SelectedIndex = isColorOnly ? 1 : 0;

                _layoutCombo.SelectedIndex = bg.Layout switch
                {
                    RoomBackgroundLayout.StretchRoom => 1,
                    RoomBackgroundLayout.Tile => 2,
                    RoomBackgroundLayout.StretchView => 3,
                    _ => 0,
                };

                _numSpeedX.Value = Math.Clamp((decimal)(bg.Scroll is { Length: > 0 } ? bg.Scroll[0] : 0f), _numSpeedX.Minimum, _numSpeedX.Maximum);
                _numSpeedY.Value = Math.Clamp((decimal)(bg.Scroll is { Length: > 1 } ? bg.Scroll[1] : 0f), _numSpeedY.Minimum, _numSpeedY.Maximum);
                _numDepth.Value = Math.Clamp(bg.Depth, _numDepth.Minimum, _numDepth.Maximum);

                Color c = Color.FromArgb(bg.TintArgb);
                _btnPickColor.BackColor = c;
                _btnPickColor.ForeColor = (c.R * 0.299 + c.G * 0.587 + c.B * 0.114) > 186 ? Color.Black : Color.White;

                UpdatePreview(bg);
            }
            else
            {
                _chkEnabled.Checked = false;
                _modeCombo.SelectedIndex = 0;
                _layoutCombo.SelectedIndex = 0;
                _numSpeedX.Value = 0;
                _numSpeedY.Value = 0;
                _numDepth.Value = 1000 + _selectedSlot * 10;
                _previewBox.Image?.Dispose();
                _previewBox.Image = null;
                _previewKey = null;
            }

            UpdateModeVisibility();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void UpdateModeVisibility()
    {
        bool isImage = _modeCombo.SelectedIndex == 0;
        _btnPickAsset.Enabled = isImage;
        _btnPickColor.Enabled = !isImage;
    }

    private void UpdatePreview(RoomBackgroundData bg)
    {
        string key = bg.Asset + "|" + bg.TintArgb;
        if (_previewKey == key && _previewBox.Image is not null) return;
        _previewKey = key;
        _previewBox.Image?.Dispose();
        _previewBox.Image = null;

        if (!string.IsNullOrWhiteSpace(bg.Asset))
        {
            string? imagePath = ProjectAssetIndex.ResolveSpriteImage(_editor.ProjectRoot, bg.Asset);
            if (imagePath is not null && File.Exists(imagePath))
            {
                try
                {
                    using FileStream stream = new(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using Bitmap decoded = new(stream);
                    _previewBox.Image = new Bitmap(decoded);
                    return;
                }
                catch { }
            }
        }

        // Draw colour rectangle
        Bitmap swatch = new(120, 60);
        using (Graphics g = Graphics.FromImage(swatch))
        {
            Color c = Color.FromArgb(bg.TintArgb);
            g.Clear(c.A == 0 ? Color.FromArgb(18, 23, 36) : c);
        }
        _previewBox.Image = swatch;
    }

    private void PickBackgroundImage()
    {
        if (!CanEditSlot) return;
        int slot = _selectedSlot;
        string? current = ActiveBackgroundLayer?.Background?.Asset;
        ProjectAssetEntry? asset = AssetPickerService.PickBackgroundImage(_editor.ProjectRoot, this, current);
        // A nested modal loop can process a project/context change. Never apply its result to
        // another slot or a now-locked layer.
        if (asset is null || slot != _selectedSlot || !CanEditSlot) return;
        AssignBackgroundImage(asset.Reference);
    }

    private void PickBackgroundColor()
    {
        if (!CanEditSlot) return;
        using ColorDialog dialog = new() { Color = _btnPickColor.BackColor, FullOpen = true };
        int slot = _selectedSlot;
        if (dialog.ShowDialog(this) != DialogResult.OK || slot != _selectedSlot || !CanEditSlot) return;
        ApplyBackgroundChange("Change background colour", node =>
        {
            node.Background ??= new RoomBackgroundData();
            node.Background.TintArgb = dialog.Color.ToArgb();
        });
    }

    private void CommitBackground()
    {
        if (_syncing || !CanEditSlot) return;
        bool enabled = _chkEnabled.Checked;
        bool colourOnly = _modeCombo.SelectedIndex == 1;
        RoomBackgroundLayout layout = _layoutCombo.SelectedIndex switch
        {
            1 => RoomBackgroundLayout.StretchRoom,
            2 => RoomBackgroundLayout.Tile,
            3 => RoomBackgroundLayout.StretchView,
            _ => RoomBackgroundLayout.Single,
        };
        float speedX = (float)_numSpeedX.Value;
        float speedY = (float)_numSpeedY.Value;
        int depth = (int)_numDepth.Value;
        ApplyBackgroundChange("Change background settings", node =>
        {
            node.Enabled = enabled;
            node.Background ??= new RoomBackgroundData();
            node.Background.Mode = RoomBackgroundMode.TwoD;
            if (colourOnly) node.Background.Asset = string.Empty;
            node.Background.Layout = layout;
            node.Background.Scroll = [speedX, speedY];
            node.Background.Depth = depth;
        }, synchronizeControls: false);
    }

    /// <summary>Assigns a public resource name through the same undoable path as the picker.</summary>
    internal bool AssignBackgroundImage(string resourceName)
    {
        if (string.IsNullOrWhiteSpace(resourceName)) return false;
        return ApplyBackgroundChange("Assign background image", node =>
        {
            node.Background ??= new RoomBackgroundData();
            bool firstImage = string.IsNullOrWhiteSpace(node.Background.Asset);
            node.Enabled = true;
            node.Background.Mode = RoomBackgroundMode.TwoD;
            node.Background.Asset = resourceName;
            // A newly assigned Background resource should be visible immediately. Single/Normal
            // is a positioned room image and can legitimately be off-screen when the editor camera
            // is elsewhere; default first assignment to the fixed-view presentation instead.
            if (firstImage && node.Background.Layout == RoomBackgroundLayout.Single)
                node.Background.Layout = RoomBackgroundLayout.StretchView;
        });
    }

    private bool ApplyBackgroundChange(string label, Action<RoomNode> change, bool synchronizeControls = true)
    {
        if (_syncing || !CanEditSlot) return false;
        RoomNode? existing = FindBackgroundNode(_selectedSlot);
        // A new slot must belong to an editable layer; never fall back into a locked layer.
        RoomLayer? targetLayer = existing is null ? _editor.Room.Layers.FirstOrDefault(layer => !layer.Locked) : null;
        if (existing is null && targetLayer is null) return false;
        RoomNode node = existing ?? CreateBackgroundNode(_selectedSlot, targetLayer!.Id);
        BackgroundState before = BackgroundState.Capture(node);
        change(node);
        BackgroundState after = BackgroundState.Capture(node);
        if (existing is not null && before.Matches(after)) return false;
        int insertionIndex = existing is null ? _editor.Room.Nodes.Count : _editor.Room.Nodes.IndexOf(node);
        if (existing is null) _editor.Room.Nodes.Add(node);

        void Repaint(bool sync)
        {
            _editor.ActiveRoomEditContextChanged();
            if (sync) SyncFromSelectedSlot();
            else if (node.Background is { } background) UpdatePreview(background);
            _editor.InvalidateBackgroundViewport();
        }
        Repaint(synchronizeControls);
        _editor.PushEdit(label, () =>
        {
            if (existing is null && !_editor.Room.Nodes.Contains(node))
                _editor.Room.Nodes.Insert(Math.Min(insertionIndex, _editor.Room.Nodes.Count), node);
            after.Restore(node);
            Repaint(true);
        }, () =>
        {
            if (existing is null) _editor.Room.Nodes.Remove(node);
            else before.Restore(node);
            Repaint(true);
        });
        return true;
    }

    // Only authored background fields are captured: no JSON serialization, texture loading,
    // transform mutation or repeated hierarchy construction is required for scalar edits.
    private sealed record BackgroundState(bool Enabled, RoomBackgroundData? Data)
    {
        public static BackgroundState Capture(RoomNode node) => new(node.Enabled, CopyData(node.Background));
        public void Restore(RoomNode node) { node.Enabled = Enabled; node.Background = CopyData(Data); }
        public bool Matches(BackgroundState other)
        {
            if (Enabled != other.Enabled || (Data is null) != (other.Data is null)) return false;
            if (Data is not { } a || other.Data is not { } b) return true;
            return a.Asset == b.Asset && a.Mode == b.Mode && a.Layout == b.Layout && a.Depth == b.Depth
                && a.RepeatX == b.RepeatX && a.RepeatY == b.RepeatY && a.DepthTest == b.DepthTest
                && a.TintArgb == b.TintArgb && a.Opacity == b.Opacity
                && (a.Scroll ?? []).SequenceEqual(b.Scroll ?? []);
        }
        private static RoomBackgroundData? CopyData(RoomBackgroundData? value) => value is null ? null : new()
        {
            Asset = value.Asset, Mode = value.Mode, Layout = value.Layout, Depth = value.Depth,
            Scroll = value.Scroll?.ToArray() ?? [0f, 0f], RepeatX = value.RepeatX, RepeatY = value.RepeatY,
            DepthTest = value.DepthTest, TintArgb = value.TintArgb, Opacity = value.Opacity,
        };
    }

    private RoomNode? FindBackgroundNode(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= 8) return null;
        RoomNode?[] slots = new RoomNode?[8];
        List<RoomNode> nodes = _editor.Room.Nodes.Where(node => node.Kind == RoomNodeKind.Background).ToList();
        // Named slots are explicit. Unnamed imported backgrounds occupy distinct remaining slots;
        // equal Order values must never alias two slots to the same resource.
        for (int i = 0; i < slots.Length; i++)
            slots[i] = nodes.FirstOrDefault(node => string.Equals(node.Name, $"Background {i + 1}", StringComparison.OrdinalIgnoreCase));
        foreach (RoomNode node in nodes.OrderBy(node => node.Order))
        {
            if (slots.Contains(node)) continue;
            int slot = Array.FindIndex(slots, candidate => candidate is null);
            if (slot < 0) break;
            slots[slot] = node;
        }
        return slots[slotIndex];
    }

    private static RoomNode CreateBackgroundNode(int slotIndex, string layerId) => new()
    {
        Kind = RoomNodeKind.Background,
        LayerId = layerId,
        Name = $"Background {slotIndex + 1}",
        Order = slotIndex,
        Enabled = true,
        Background = new RoomBackgroundData
        {
            Depth = 1000 + slotIndex * 10,
            TintArgb = unchecked((int)0xffffffff),
        },
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) { DrawingImage? old = _previewBox.Image; _previewBox.Image = null; old?.Dispose(); }
        base.Dispose(disposing);
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
