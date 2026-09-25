using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Runtime.Scene;

namespace Genesis.Application.Editors.Suite.Rooms;

/// <summary>
/// Subpanel for tileset layer management, tileset asset selection, and interactive tile picking.
/// </summary>
public sealed class RoomTilesetsPanel : Panel
{
    private readonly RoomEditorControl _editor;
    private readonly ThemedComboBox _layerCombo;
    private readonly Button _btnAddLayer;
    private readonly Button _btnDeleteLayer;
    private readonly Button _btnRenameLayer;
    private readonly NumericUpDown _numLayerDepth;
    private readonly ThemedComboBox _tilesetCombo;
    private readonly Label _tilesetInfoLabel;
    private readonly TilePickerPanel _tilePicker;
    private readonly Label _promptLabel;
    private readonly List<ProjectAssetEntry> _tilesetEntries = [];
    private bool _syncing;
    private string? _previewTilesetPath;
    private TileSetInfo? _previewTileset;

    public event Action<TileSetInfo, int>? TileSelected;

    public RoomTilesetsPanel(RoomEditorControl editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        Dock = DockStyle.Fill;
        BackColor = EditorChrome.Surface;
        Padding = new Padding(8);

        Label heading = new()
        {
            Text = "Tilesets",
            Font = EditorChrome.HeadingFont,
            ForeColor = EditorChrome.Text,
            Dock = DockStyle.Top,
            Height = 24,
        };

        // Top Layer section
        Panel layerSection = new()
        {
            Dock = DockStyle.Top,
            Height = 100,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 0, 0, 8),
        };

        TableLayoutPanel layerTable = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            BackColor = Color.Transparent,
        };
        layerTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50f));
        layerTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layerTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
        layerTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
        layerTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));

        _layerCombo = new ThemedComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
            DisplayMember = "Name",
        };
        _layerCombo.Format += (_, e) =>
        {
            if (e.ListItem is RoomNode n) e.Value = n.Name;
        };
        _layerCombo.SelectedIndexChanged += OnLayerSelectionChanged;

        TableLayoutPanel btnTable = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 2, 0, 2),
        };
        btnTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        btnTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.4f));
        btnTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));

        _btnAddLayer = new Button { Text = "+ Add", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 1, 2, 1) };
        _btnRenameLayer = new Button { Text = "Rename", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, Margin = new Padding(2, 1, 2, 1) };
        _btnDeleteLayer = new Button { Text = "Delete", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, Margin = new Padding(2, 1, 0, 1) };
        EditorChrome.StyleField(_btnAddLayer);
        EditorChrome.StyleField(_btnRenameLayer);
        EditorChrome.StyleField(_btnDeleteLayer);

        _btnAddLayer.Click += (_, _) => AddLayerPrompt();
        _btnRenameLayer.Click += (_, _) => RenameActiveLayerPrompt();
        _btnDeleteLayer.Click += (_, _) => DeleteActiveLayerPrompt();

        btnTable.Controls.Add(_btnAddLayer, 0, 0);
        btnTable.Controls.Add(_btnRenameLayer, 1, 0);
        btnTable.Controls.Add(_btnDeleteLayer, 2, 0);

        _numLayerDepth = MakeNumeric(-100000, 100000, 0);
        _numLayerDepth.Value = 100;
        _numLayerDepth.Width = 84;
        _numLayerDepth.Dock = DockStyle.Left;
        _numLayerDepth.ValueChanged += (_, _) => CommitLayerDepth();

        Label lblLayer = new() { Text = "Layer:", ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont, AutoSize = false, Width = 48, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
        Label lblDepth = new() { Text = "Depth:", ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont, AutoSize = false, Width = 48, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };

        layerTable.Controls.Add(lblLayer, 0, 0);
        layerTable.Controls.Add(_layerCombo, 1, 0);

        layerTable.SetColumnSpan(btnTable, 2);
        layerTable.Controls.Add(btnTable, 0, 1);

        layerTable.Controls.Add(lblDepth, 0, 2);
        layerTable.Controls.Add(_numLayerDepth, 1, 2);

        layerSection.Controls.Add(layerTable);

        // Tileset Asset selector section
        Panel assetSection = new()
        {
            Dock = DockStyle.Top,
            Height = 64,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 4, 0, 6),
        };

        Label lblTileset = new()
        {
            Text = "Active Tileset:",
            ForeColor = EditorChrome.Accent,
            Font = EditorChrome.HeadingFont,
            Dock = DockStyle.Top,
            Height = 18,
        };

        _tilesetCombo = new ThemedComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Top,
            DisplayMember = "DisplayName",
        };
        _tilesetCombo.Format += (_, e) =>
        {
            if (e.ListItem is ProjectAssetEntry entry)
            {
                e.Value = entry.DisplayName;
            }
        };
        _tilesetCombo.SelectedIndexChanged += OnTilesetAssetChanged;

        _tilesetInfoLabel = new Label
        {
            Text = "",
            ForeColor = EditorChrome.Muted,
            Font = EditorChrome.SmallFont,
            Dock = DockStyle.Top,
            Height = 16,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        assetSection.Controls.Add(_tilesetInfoLabel);
        assetSection.Controls.Add(_tilesetCombo);
        assetSection.Controls.Add(lblTileset);

        _promptLabel = new Label
        {
            Text = "No tile layer found. Click '+ Add' above to start painting tiles.",
            ForeColor = EditorChrome.Warning,
            Font = EditorChrome.SmallFont,
            Dock = DockStyle.Top,
            Height = 34,
            TextAlign = ContentAlignment.MiddleCenter,
            Visible = false,
        };

        // Interactive Tile Picker Grid
        _tilePicker = new TilePickerPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(4),
        };
        _tilePicker.TileSelected += index =>
        {
            if (_tilesetCombo.SelectedItem is ProjectAssetEntry entry)
            {
                LoadTilesetPreview();
                TileSetInfo? info = _previewTileset;
                if (info is not null)
                {
                    TileSelected?.Invoke(info, index);
                }
            }
        };

        Controls.Add(_tilePicker);
        Controls.Add(_promptLabel);
        Controls.Add(assetSection);
        Controls.Add(layerSection);
        Controls.Add(heading);
    }

    public RoomNode? ActiveTileLayer => _layerCombo.SelectedItem as RoomNode;

    public TilePickerPanel TilePicker => _tilePicker;

    public void RefreshTilesets(IReadOnlyList<ProjectAssetEntry> images)
    {
        _previewTilesetPath = null;
        _syncing = true;
        try
        {
            _tilesetEntries.Clear();
            _tilesetCombo.Items.Clear();

            foreach (ProjectAssetEntry entry in images)
            {
                if (TileSetInfo.Load(entry.FullPath) is not null)
                {
                    _tilesetEntries.Add(entry);
                    _tilesetCombo.Items.Add(entry);
                }
            }

            if (_tilesetCombo.Items.Count > 0 && _tilesetCombo.SelectedIndex < 0)
            {
                _tilesetCombo.SelectedIndex = 0;
            }
        }
        finally
        {
            _syncing = false;
        }

        RefreshLayers();
    }

    public void RefreshLayers()
    {
        RoomNode? selected = ActiveTileLayer;
        List<RoomNode> layers = _editor.Room.Nodes.Where(node => node.Kind == RoomNodeKind.TileLayer).ToList();
        _syncing = true;
        try
        {
            if (!_layerCombo.Items.Cast<RoomNode>().SequenceEqual(layers))
            {
                _layerCombo.BeginUpdate();
                try
                {
                    _layerCombo.Items.Clear();
                    foreach (RoomNode node in layers) _layerCombo.Items.Add(node);
                    _layerCombo.SelectedItem = layers.FirstOrDefault(node => node.Id == selected?.Id) ?? layers.FirstOrDefault();
                }
                finally { _layerCombo.EndUpdate(); }
            }
            bool hasLayer = ActiveTileLayer is not null;
            _promptLabel.Visible = !hasLayer;
            _tilePicker.Visible = hasLayer;
            _btnDeleteLayer.Enabled = hasLayer && !_editor.IsNodeLocked(ActiveTileLayer!);
            _btnRenameLayer.Enabled = _btnDeleteLayer.Enabled;
        }
        finally { _syncing = false; }
        SyncSelectedLayer();
    }

    public void SelectLayer(RoomNode? node)
    {
        RefreshLayers();
        if (node is null || _layerCombo.Items.Contains(node)) _layerCombo.SelectedItem = node;
    }

    private void OnLayerSelectionChanged(object? sender, EventArgs e)
    {
        SyncSelectedLayer();
    }

    private void SyncSelectedLayer()
    {
        if (_syncing) return;
        if (_layerCombo.SelectedItem is RoomNode node && node.TileLayer is not null)
        {
            _syncing = true;
            try
            {
                _numLayerDepth.Value = Math.Clamp(node.TileLayer.Depth, _numLayerDepth.Minimum, _numLayerDepth.Maximum);
                // Tile layers have their own active target. Never overwrite the Objects tab's layer.

                // Sync tileset asset if layer already has one
                if (!string.IsNullOrWhiteSpace(node.TileLayer.Tileset))
                {
                    for (int i = 0; i < _tilesetCombo.Items.Count; i++)
                    {
                        if (_tilesetCombo.Items[i] is ProjectAssetEntry entry
                            && string.Equals(ResourceNames.Name(_editor.ProjectRoot, entry.FullPath),
                                node.TileLayer.Tileset, StringComparison.OrdinalIgnoreCase))
                        {
                            _tilesetCombo.SelectedIndex = i;
                            break;
                        }
                    }
                }
            }
            finally
            {
                _syncing = false;
            }
            LoadTilesetPreview();
        }
        _editor.ActiveRoomEditContextChanged();
    }

    private void CommitLayerDepth()
    {
        if (_syncing || ActiveTileLayer is not { } node) return;
        _editor.SetNodeDepth(node, (int)_numLayerDepth.Value);
    }

    private void OnTilesetAssetChanged(object? sender, EventArgs e)
    {
        if (_syncing) return;
        LoadTilesetPreview();
        if (_tilesetCombo.SelectedItem is ProjectAssetEntry entry && ActiveTileLayer is { } node)
            _editor.ConfigureTileLayerTileset(node,
                ResourceNames.Name(_editor.ProjectRoot, entry.FullPath), _previewTileset);
    }

    private void LoadTilesetPreview()
    {
        if (_tilesetCombo.SelectedItem is not ProjectAssetEntry entry) return;
        if (string.Equals(_previewTilesetPath, entry.FullPath, StringComparison.OrdinalIgnoreCase)) return;
        _previewTilesetPath = entry.FullPath;
        _previewTileset = TileSetInfo.Load(entry.FullPath);
        string? sheetPath = ProjectAssetIndex.ResolveSpriteImage(_editor.ProjectRoot, entry.FullPath);
        _tilePicker.Load(_previewTileset, sheetPath);
        _tilesetInfoLabel.Text = _previewTileset is { } info
            ? $"{info.TileWidth}×{info.TileHeight} px  •  {_tilePicker.TileCount} tiles" : entry.DisplayName;
    }

    private void AddLayerPrompt()
    {
        if (_editor.Navigation.CurrentSection != RoomNavSection.Tilesets) return;
        string name = $"TileLayer {_layerCombo.Items.Count + 1}";
        RoomNode node = new()
        {
            Kind = RoomNodeKind.TileLayer,
            Name = name,
            LayerId = _editor.Room.Layers.FirstOrDefault()?.Id ?? "default",
            TileLayer = new RoomTileLayerData
            {
                Depth = 100 - _layerCombo.Items.Count * 10,
                CellWidth = 32,
                CellHeight = 32,
            },
        };
        _editor.Room.Nodes.Add(node);
        _editor.MarkDirty();
        RefreshLayers();
        _layerCombo.SelectedItem = node;
    }

    private void DeleteActiveLayerPrompt()
    {
        if (_layerCombo.SelectedItem is not RoomNode node || !_editor.CanEditNodeInActiveContext(node)) return;

        if (node.TileLayer?.Cells.Count > 0)
        {
            DialogResult res = MessageBox.Show(
                $"The layer '{node.Name}' contains {node.TileLayer.Cells.Count} placed tiles. Are you sure you want to delete it?",
                "Delete Tile Layer",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (res != DialogResult.Yes) return;
        }

        _editor.Room.Nodes.Remove(node);
        _editor.MarkDirty();
        RefreshLayers();
    }

    private void RenameActiveLayerPrompt()
    {
        if (_layerCombo.SelectedItem is not RoomNode node || !_editor.CanEditNodeInActiveContext(node)) return;

        using Form prompt = new()
        {
            Text = "Rename Tile Layer",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(320, 110),
            BackColor = EditorChrome.Surface,
            ForeColor = EditorChrome.Text,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
        };
        TextBox txt = new() { Text = node.Name, Dock = DockStyle.Top, BackColor = EditorChrome.Canvas, ForeColor = EditorChrome.Text };
        Button ok = new() { Text = "OK", DialogResult = DialogResult.OK, Dock = DockStyle.Right, Width = 64 };
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Dock = DockStyle.Right, Width = 64 };
        EditorChrome.StyleField(txt);
        EditorChrome.StyleField(ok);
        EditorChrome.StyleField(cancel);

        Panel btnPanel = new() { Dock = DockStyle.Bottom, Height = 32 };
        btnPanel.Controls.Add(ok);
        btnPanel.Controls.Add(cancel);

        prompt.Controls.Add(txt);
        prompt.Controls.Add(btnPanel);
        prompt.AcceptButton = ok;
        prompt.CancelButton = cancel;

        if (prompt.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(txt.Text))
        {
            _editor.SetNodeName(node, txt.Text.Trim());
            RefreshLayers();
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
