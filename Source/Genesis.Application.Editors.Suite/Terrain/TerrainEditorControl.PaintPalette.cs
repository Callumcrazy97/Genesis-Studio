using System.Drawing;
using System.Windows.Forms;
using Genesis.World.Terrain;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    private TableLayoutPanel? _paintLayerGrid;

    private TableLayoutPanel BuildPaintLayerGrid()
    {
        _paintLayerGrid = new TableLayoutPanel
        {
            Name = "TerrainPaintLayers",
            AutoSize = true,
            ColumnCount = 2,
            Width = 248,
            Margin = new Padding(0, 0, 0, 6),
        };
        _paintLayerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _paintLayerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        RebuildPaintLayerGrid();
        return _paintLayerGrid;
    }

    private void RebuildPaintLayerGrid()
    {
        if (_paintLayerGrid is null) return;
        _paintLayerGrid.SuspendLayout();
        foreach (Control tile in _paintLayerGrid.Controls.Cast<Control>().ToArray()) tile.Dispose();
        _paintLayerGrid.RowStyles.Clear();
        _paintLayerGrid.RowCount = (_settings.Layers.Count + 1) / 2;
        for (int row = 0; row < _paintLayerGrid.RowCount; row++)
            _paintLayerGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (int index = 0; index < _settings.Layers.Count; index++)
        {
            int selected = index;
            Button tile = MakeContextAction(_settings.Layers[index].Name, "Paint this terrain layer", () => SelectPaintLayer(selected));
            tile.Tag = index;
            tile.Name = "TerrainPaintLayer" + index;
            tile.Dock = DockStyle.Fill;
            tile.Height = tile.Font.Height + 36;
            tile.TextAlign = ContentAlignment.BottomCenter;
            tile.Padding = new Padding(3, 3, 3, 6);
            tile.Margin = new Padding(0, 0, 6, 4);
            tile.Paint += (_, e) =>
            {
                TerrainLayerDocument layer = _settings.Layers[selected];
                Color color = Color.FromArgb((int)(Math.Clamp(layer.Color[0], 0f, 1f) * 255),
                    (int)(Math.Clamp(layer.Color[1], 0f, 1f) * 255), (int)(Math.Clamp(layer.Color[2], 0f, 1f) * 255));
                using SolidBrush swatch = new(color);
                e.Graphics.FillRectangle(swatch, 10, 8, Math.Max(1, tile.ClientSize.Width - 20), 16);
            };
            _paintLayerGrid.Controls.Add(tile, index % 2, index / 2);
        }
        _paintLayerGrid.ResumeLayout(true);
        RefreshMaterialTiles();
    }
}
