using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.UI;
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
            Name = "TileSetHeading",
            Text = "Tilesets",
            Font = EditorChrome.HeadingFont,
            ForeColor = EditorChrome.Text,
            Dock = DockStyle.Top,
            Height = 24,
        };

        // Top Layer section
        Panel layerSection = new()
        {
            Name = "TileLayerSection",
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
        btnTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

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
            Name = "TileAssetSection",
            Dock = DockStyle.Top,
            Height = 64,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 4, 0, 6),
        };

        Label lblTileset = new()
        {
            Name = "TileAssetHeading",
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
        _tilesetCombo.SelectionChangeCommitted += (_, _) =>
        {
            if (!_editor.IsTilePainting) OnTilesetAssetChanged(this, EventArgs.Empty);
        };

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
            Text = "Choose a tile set above to create a layer and start painting.",
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

    /// <summary>Chooses through the same commit path as the visible tile-set selector.</summary>
    public bool SelectTileset(string fullPath)
    {
        ProjectAssetEntry? entry = _tilesetEntries.FirstOrDefault(candidate =>
            string.Equals(candidate.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return false;
        if (ReferenceEquals(_tilesetCombo.SelectedItem, entry)) OnTilesetAssetChanged(this, EventArgs.Empty);
        else _tilesetCombo.SelectedItem = entry;
        return _editor.IsTilePainting;
    }

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
        if (_tilesetCombo.SelectedItem is ProjectAssetEntry entry)
            _editor.BeginTilePainting(entry.FullPath);
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
        ProjectAssetEntry? entry = _tilesetCombo.SelectedItem as ProjectAssetEntry ?? _tilesetEntries.FirstOrDefault();
        if (entry is null) return;
        _syncing = true;
        try { _tilesetCombo.SelectedItem = entry; }
        finally { _syncing = false; }
        _editor.BeginTilePainting(entry.FullPath, createNewLayer: true);
        RefreshLayers();
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

        _editor.Select(node);
        _editor.DeleteSelection();
        RefreshLayers();
    }

    private void RenameActiveLayerPrompt()
    {
        if (_layerCombo.SelectedItem is not RoomNode node || !_editor.CanEditNodeInActiveContext(node)) return;

        using DpiAwareForm prompt = CreateRenameLayerDialog(node);
        TextBox txt = prompt.Controls.Find("TileLayerName", true).OfType<TextBox>().Single();
        if (prompt.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(txt.Text))
        {
            _editor.SetNodeName(node, txt.Text.Trim());
            RefreshLayers();
        }
    }

    public DpiAwareForm CreateRenameLayerDialog(RoomNode node)
    {
        TileLayerNameDialog prompt = new()
        {
            Text = "Rename Tile Layer",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(440, 150),
            BackColor = EditorChrome.Surface,
            ForeColor = EditorChrome.Text,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
        };
        TextBox txt = new() { Name = "TileLayerName", Text = node.Name, Dock = DockStyle.Top, BackColor = EditorChrome.Canvas, ForeColor = EditorChrome.Text };
        Button ok = new() { Text = "Rename", DialogResult = DialogResult.OK, AutoSize = true };
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        EditorChrome.StyleField(txt);
        EditorChrome.StyleField(ok);
        EditorChrome.StyleField(cancel);

        FlowLayoutPanel btnPanel = new() { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        btnPanel.Controls.Add(ok);
        btnPanel.Controls.Add(cancel);

        prompt.Controls.Add(txt);
        prompt.Controls.Add(btnPanel);
        prompt.Controls.Add(new Label { Text = "Tile layer name", Dock = DockStyle.Top, AutoSize = true });
        prompt.Padding = new Padding(12);
        prompt.AcceptButton = ok;
        prompt.CancelButton = cancel;

        prompt.ApplyInterfaceLayout();
        return prompt;
    }

    private sealed class TileLayerNameDialog : DpiAwareForm
    {
        public override void ApplyInterfaceLayout()
        {
            foreach (Button button in Controls.OfType<FlowLayoutPanel>().SelectMany(panel => panel.Controls.OfType<Button>()))
                button.MinimumSize = new Size(TextRenderer.MeasureText(button.Text, button.Font).Width + 24, button.Font.Height + 16);
            int height = Controls.OfType<Label>().Sum(label => label.PreferredHeight)
                + Controls.OfType<TextBox>().Sum(text => text.PreferredHeight)
                + Controls.OfType<FlowLayoutPanel>().Sum(panel => panel.GetPreferredSize(new Size(ClientSize.Width, 0)).Height) + Padding.Vertical + 20;
            ClientSize = new Size(Math.Max(ClientSize.Width, 440), height);
        }
    }

    public void ApplyInterfaceLayout()
    {
        Label heading = Controls.OfType<Label>().Single(label => label.Name == "TileSetHeading");
        heading.Font = EditorChrome.HeadingFont; heading.Height = heading.Font.Height + 8;
        Panel layer = Controls.OfType<Panel>().Single(panel => panel.Name == "TileLayerSection");
        TableLayoutPanel fields = layer.Controls.OfType<TableLayoutPanel>().Single();
        fields.ColumnStyles[0].Width = TextRenderer.MeasureText("Depth:", EditorChrome.SmallFont).Width + 10;
        fields.RowStyles[0].Height = _layerCombo.PreferredHeight + 6;
        fields.RowStyles[1].Height = _btnAddLayer.Font.Height + 18;
        fields.RowStyles[2].Height = _numLayerDepth.PreferredHeight + 6;
        _numLayerDepth.Width = Math.Max(84, TextRenderer.MeasureText("-100000", _numLayerDepth.Font).Width + 28);
        layer.Height = fields.RowStyles.Cast<RowStyle>().Sum(row => (int)row.Height) + layer.Padding.Vertical;
        Panel asset = Controls.OfType<Panel>().Single(panel => panel.Name == "TileAssetSection");
        Label assetHeading = asset.Controls.OfType<Label>().Single(label => label.Name == "TileAssetHeading");
        assetHeading.Font = EditorChrome.HeadingFont; assetHeading.Height = assetHeading.Font.Height + 6;
        _tilesetInfoLabel.Height = _tilesetInfoLabel.Font.Height + 4;
        asset.Height = assetHeading.Height + _tilesetCombo.PreferredHeight + _tilesetInfoLabel.Height + asset.Padding.Vertical;
        _promptLabel.Height = TextRenderer.MeasureText(_promptLabel.Text, _promptLabel.Font,
            new Size(Math.Max(80, ClientSize.Width - Padding.Horizontal), int.MaxValue), TextFormatFlags.WordBreak).Height + 12;
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
