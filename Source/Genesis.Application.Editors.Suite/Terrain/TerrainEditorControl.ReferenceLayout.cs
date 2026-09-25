using System.Drawing;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    private FlowLayoutPanel? _referencePalette;
    private readonly List<(TerrainToolTile Tile, TerrainBrush Brush)> _referenceBrushes = [];
    private readonly List<(TerrainToolTile Tile, int Layer)> _referenceLayers = [];
    private readonly ToolTip _referenceTips = new();
    private static Color ReferenceSurface => Color.FromArgb(35, 38, 41);

    private void InstallReferenceLayout(ToolStrip toolbar, ToolStrip rail, Panel right)
    {
        right.Width = 284; right.BackColor = ReferenceSurface;
        _terrainObjectSplit.BackColor = Color.FromArgb(21, 24, 27);
        _contextHeader.Font = new Font("Segoe UI", 11f);
        _contextHeader.ForeColor = Color.WhiteSmoke;
        _contextHeader.BackColor = ReferenceSurface;
        _contextHeader.Height = 34;
        _contextHeader.Padding = new Padding(8, 5, 0, 0);
        _toolPanel.BackColor = ReferenceSurface;
        rail.Width = 52; rail.Padding = new Padding(2, 4, 2, 2); rail.BackColor = Color.FromArgb(42, 46, 50);
        foreach (var pair in _modeButtons)
        {
            pair.Value.Width = 46; pair.Value.Height = 48; pair.Value.Font = new Font("Segoe UI Symbol", 20f);
            pair.Value.Text = pair.Key switch
            {
                TerrainEditorMode.Select => "➤", TerrainEditorMode.Generate => "♧", TerrainEditorMode.Sculpt => "▰",
                TerrainEditorMode.Paint => "◒", _ => "⚙",
            };
            pair.Value.ForeColor = pair.Key switch
            {
                TerrainEditorMode.Generate => Color.Goldenrod, TerrainEditorMode.Sculpt => Color.Coral,
                TerrainEditorMode.Paint => Color.DeepSkyBlue, TerrainEditorMode.Entities => Color.MediumPurple, _ => Color.WhiteSmoke,
            };
        }

        _referencePalette = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false, FlowDirection = FlowDirection.TopDown,
            BackColor = ReferenceSurface, Padding = new Padding(4, 2, 4, 16),
        };
        _toolPanel.Controls.Add(_referencePalette); _referencePalette.BringToFront();
        _referencePalette.HandleCreated += (_, _) => EditorScrollHost.ApplyDarkScrollTheme(_referencePalette);
        _contextHeader.SendToBack();
        InstallActiveToolCard();
        AddToolGroup("Terrain Tools", 82,
            Tile("Create", "⬇", Color.DeepSkyBlue, () => ShowCreateMenu(), 96, 66),
            Tile("Region / Lasso", "◌", Color.DeepSkyBlue, () => ShowCreateMenu(drawingOnly: true), 96, 66));
        AddToolGroup("Selection & Placement", 82,
            Tile("Select", "➤", Color.WhiteSmoke, ActivateSelectTool, 96, 60),
            Tile("Place", "+", Color.DeepSkyBlue, ArmSelectedEntity, 96, 60),
            Tile("Select Region", "□", Color.SkyBlue, () => BeginPaintSelection(TerrainCreationSource.Region), 96, 72),
            Tile("Select Lasso", "◌", Color.SkyBlue, () => BeginPaintSelection(TerrainCreationSource.Lasso), 96, 72));
        var textures = new List<Control>();
        for (int index = 0; index < Math.Min(4, _settings.Layers.Count); index++)
        {
            int layer = index;
            var tile = Tile(_settings.Layers[index].Name, "", Color.White, () =>
            {
                _layerListBox.SelectedIndex = layer; SetMode(TerrainEditorMode.Paint); SyncReferencePalette();
            }, 96, 90);
            tile.TextureTile = true;
            string? raster = ResolveNamedImageRaster(_settings.Layers[index].Image);
            if (raster is not null)
            {
                try { using var source = new Bitmap(raster); tile.Image = new Bitmap(source, 96, 64); }
                catch (Exception exception) when (exception is IOException or ArgumentException) { }
            }
            if (tile.Image is null)
            {
                var swatch = new Bitmap(96, 64); using var graphics = Graphics.FromImage(swatch);
                float[] color = _settings.Layers[index].Color;
                graphics.Clear(Color.FromArgb((int)(Math.Clamp(color[0], 0, 1) * 255), (int)(Math.Clamp(color[1], 0, 1) * 255), (int)(Math.Clamp(color[2], 0, 1) * 255)));
                tile.Image = swatch;
            }
            tile.Disposed += (_, _) => tile.Image?.Dispose();
            _referenceLayers.Add((tile, index)); textures.Add(tile);
        }
        textures.Add(Tile("Fill", "▣", Color.DeepSkyBlue, FillPaintSelection, 96, 72));
        textures.Add(Tile("Clear Selection", "×", Color.LightGray, () => { _paintSelection.Clear(); _viewport.Invalidate(); }, 96, 72));
        AddToolGroup("Paint", 192, textures.ToArray());
        var sculpt = new List<Control>();
        foreach (var entry in new[] { (TerrainBrush.Raise, "Raise", "⬆", Color.YellowGreen), (TerrainBrush.Lower, "Lower", "⬇", Color.DeepSkyBlue),
            (TerrainBrush.Smooth, "Smooth", "∿", Color.MediumSeaGreen), (TerrainBrush.Flatten, "Flatten", "▬", Color.SkyBlue),
            (TerrainBrush.Erase, "Erase", "▱", Color.LightCoral), (TerrainBrush.InFill, "In Fill", "▦", Color.DeepSkyBlue) })
        {
            var tile = Tile(entry.Item2, entry.Item3, entry.Item4, () => { SetMode(TerrainEditorMode.Sculpt); ActiveBrush = entry.Item1; SyncToolbar(); SyncReferencePalette(); });
            _referenceBrushes.Add((tile, entry.Item1)); sculpt.Add(tile);
        }
        AddToolGroup("Sculpt", 146, sculpt.ToArray());
        AddToolGroup("Other Tools", 76, Tile("Erosion (Thermal)", "♨", Color.Coral, () => ApplyErosion(8, .35f), 196, 62));
        RestyleReferenceToolbar(toolbar);
        HandleCreated += (_, _) => BeginInvoke(() =>
        {
            if (IsDisposed || _viewport.HasSecondaryCamera) return;
            if (_terrainObjectSplit.Height > 400)
                _terrainObjectSplit.SplitterDistance = Math.Clamp((int)(_terrainObjectSplit.Height * .48f), 150, _terrainObjectSplit.Height - 186);
            _viewport.PinSecondaryFromCurrentView("Pinned view");
            if (_viewport.SecondaryViewport?.Parent is Control inset)
            {
                inset.Size = new Size(250, 164);
                inset.Location = new Point(Math.Max(12, _viewport.ClientSize.Width - inset.Width - 12), 12);
            }
            if (_viewport.SecondaryCamera is { } camera)
            {
                camera.Camera.Pitch = -1.05f;
                camera.Camera.Distance *= 1.6f;
            }
        });
    }

    private TerrainToolTile Tile(string caption, string glyph, Color color, Action action, int width = 62, int height = 66)
    {
        var tile = new TerrainToolTile { Text = caption, Glyph = glyph, GlyphColor = color, Width = width, Height = height, AccessibleName = caption };
        tile.Click += (_, _) => action();
        return tile;
    }

    private TerrainToolTile PendingTile(string caption, string glyph, string hint, int width = 62, int height = 66)
    {
        var tile = Tile(caption, glyph, Color.Gray, () => { }, width, height);
        tile.Enabled = false; tile.AccessibleDescription = hint; _referenceTips.SetToolTip(tile, hint);
        return tile;
    }

    private void AddToolGroup(string name, int contentHeight, params Control[] controls)
    {
        int columns = controls.Length == 1 ? 1 : controls.Length == 6 && name == "Sculpt" ? 3 : 2;
        var group = new Panel { Width = 208, Height = contentHeight + 38, BackColor = Color.FromArgb(29, 32, 35), Margin = new Padding(0, 0, 0, 10) };
        var label = new Label { Text = name, Height = 28, Font = new Font("Segoe UI", 10f), ForeColor = Color.WhiteSmoke };
        group.Controls.Add(label); group.Controls.AddRange(controls);
        void Arrange()
        {
            int gap = DpiLayout.Scale(group, 6), inset = DpiLayout.Scale(group, 5);
            label.SetBounds(inset, 0, group.Width - inset * 2, Math.Max(DpiLayout.Scale(group, 28), label.PreferredHeight + gap));
            int width = Math.Max(1, (group.Width - inset * 2 - gap * (columns - 1)) / columns);
            int y = label.Bottom;
            for (int row = 0; row < controls.Length; row += columns)
            {
                int height = controls.Skip(row).Take(columns).Max(control => control.Height);
                for (int col = 0; col < columns && row + col < controls.Length; col++)
                    controls[row + col].SetBounds(inset + col * (width + gap), y, width, height);
                y += height + gap;
            }
            group.Height = y + inset;
        }
        group.SizeChanged += (_, _) => Arrange();
        void FitWidth() => group.Width = Math.Max(100, _referencePalette!.ClientSize.Width - _referencePalette.Padding.Horizontal - group.Margin.Horizontal);
        _referencePalette!.ClientSizeChanged += (_, _) => FitWidth();
        _referencePalette.Controls.Add(group); FitWidth(); Arrange();
    }

    private void SyncReferencePalette()
    {
        if (_referencePalette is null) return;
        bool basic = ActiveMode is TerrainEditorMode.Select or TerrainEditorMode.Sculpt or TerrainEditorMode.Paint;
        _referencePalette.Visible = basic; _modeHost.Visible = !basic;
        _toolPanel.Width = basic ? 236 : 276;
        RefreshActiveToolCard();
        foreach (var entry in _referenceBrushes) { entry.Tile.Active = ActiveMode == TerrainEditorMode.Sculpt && ActiveBrush == entry.Brush; entry.Tile.Invalidate(); }
        foreach (var entry in _referenceLayers) { entry.Tile.Active = ActiveMode == TerrainEditorMode.Paint && _layerListBox.SelectedIndex == entry.Layer; entry.Tile.Invalidate(); }
    }

    private void RestyleReferenceToolbar(ToolStrip toolbar)
    {
        toolbar.Font = new Font("Segoe UI", 10f); toolbar.BackColor = Color.FromArgb(43, 47, 51);
        var tools = toolbar.Items.OfType<ToolStripDropDownItem>().FirstOrDefault(item => item.Text == "Tools");
        tools?.DropDownItems.Add("Flatten terrain brush", null, (_, _) =>
        {
            SetMode(TerrainEditorMode.Sculpt); ActiveBrush = TerrainBrush.Flatten; SyncToolbar(); SyncReferencePalette();
        });
        var view = toolbar.Items.OfType<ToolStripDropDownItem>().FirstOrDefault(item => item.Text == "View");
        if (view is not null)
        {
            toolbar.Items.Remove(view); toolbar.Items.Insert(3, view);
            foreach (string name in new[] { "Camera", "Gizmo", "Wizard" })
            {
                var item = toolbar.Items.OfType<ToolStripDropDownItem>().FirstOrDefault(candidate => candidate.Text == name);
                if (item is null) continue;
                toolbar.Items.Remove(item); view.DropDownItems.Add(item);
            }
        }
        foreach (ToolStripItem item in toolbar.Items)
        {
            item.Font = toolbar.Font;
            if (item is ToolStripLabel && item.Text == "View") item.Visible = false;
            if (item.Text is "2D" or "3D") item.Visible = false;
            if (item.Text is "Play" or "Stop")
            {
                item.AutoSize = false; item.Width = 82; item.Height = 32;
                item.BackColor = item.Text == "Play" ? Color.FromArgb(50, 139, 63) : Color.FromArgb(152, 57, 53);
                item.ForeColor = Color.White; item.Margin = new Padding(3, 2, 3, 2);
            }
        }
        var dimension = new ToolStripDropDownButton("3D Mode");
        dimension.AutoSize = false; dimension.Width = 108;
        dimension.DropDownItems.Add("3D Mode", null, (_, _) => { _viewport.Mode2D = false; dimension.Text = "3D Mode"; });
        dimension.DropDownItems.Add("2D Mode", null, (_, _) => { _viewport.Mode2D = true; dimension.Text = "2D Mode"; });
        toolbar.Items.Insert(Math.Min(4, toolbar.Items.Count), dimension);
        var snap = new ToolStripDropDownButton("Grid Snap (off)");
        snap.AutoSize = false; snap.Width = 158;
        snap.DropDownItems.Add("Off", null, (_, _) => { _viewportSession.SnapToGrid = false; snap.Text = "Grid Snap (off)"; });
        foreach (float step in new[] { .1f, .5f, 1f, 2f, 5f })
            snap.DropDownItems.Add($"{step:0.#} m", null, (_, _) => { _viewportSession.GridCellSize = step; _viewportSession.SnapToGrid = true; snap.Text = $"Grid Snap ({step:0.#} m)"; });
        toolbar.Items.Insert(Math.Min(5, toolbar.Items.Count), snap);
        bool previousSeparator = false;
        foreach (ToolStripItem item in toolbar.Items)
        {
            if (!item.Available) continue;
            if (item is ToolStripSeparator)
            {
                item.Available = !previousSeparator;
                previousSeparator = true;
            }
            else previousSeparator = false;
        }
    }
}
