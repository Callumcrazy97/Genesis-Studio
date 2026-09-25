using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Genesis.Application.Editors.Suite.Assets;

namespace Genesis.Application.Editors.Suite.Rooms;

/// <summary>
/// The tile sheet, drawn as a clickable grid. Click a tile to paint with it.
/// </summary>
/// <remarks>
/// Closes NEXT-061. Until this existed, <c>SelectTileIndex</c> was reachable only from code: the
/// palette let a designer choose a tile *set* but never a *tile*, so by hand every tilemap was
/// built entirely out of tile 0. The 2D pipeline audit had to call the API directly to paint two
/// different tiles, which is exactly the reaching-past-the-UI that makes an audit worthless.
///
/// Solid tiles (flagged in the Image's tile-set block) are marked, because "which tiles are ground"
/// is the question a designer asks constantly while building a level and it is already authored
/// next door in the Image Editor.
/// </remarks>
public sealed class TilePickerPanel : Panel
{
    private const int MinCellPixels = 12;
    private const int MaxCellPixels = 64;

    private TileSetInfo? _tileSet;
    private Bitmap? _sheet;
    private int _columns;
    private int _rows;
    private int _selected;
    private int _hovered = -1;
    private int _cellSize = 32;

    public TilePickerPanel()
    {
        BackColor = EditorChrome.Canvas;
        DoubleBuffered = true;
        AutoScroll = true;
        Dock = DockStyle.Fill;
    }

    /// <summary>Raised when the designer picks a different tile.</summary>
    public event Action<int>? TileSelected;

    /// <summary>The tile index the brush will paint. -1 when no sheet is loaded.</summary>
    public int SelectedIndex => _sheet is null ? -1 : _selected;

    /// <summary>Tiles the loaded sheet contains, or 0 when nothing is loaded.</summary>
    public int TileCount => _columns * _rows;

    /// <summary>True once a sheet has been loaded and can be picked from.</summary>
    public bool HasSheet => _sheet is not null && TileCount > 0;

    /// <summary>
    /// Why the last <see cref="Load"/> produced no usable sheet, or empty when one is loaded.
    /// Every route out of <see cref="Load"/> used to leave the picker silently blank, which made
    /// "the picker is empty" indistinguishable from "the sheet path did not resolve", "the file is
    /// missing" and "the tile size divides to zero columns". Callers surface this instead of
    /// guessing.
    /// </summary>
    public string LoadDiagnostic { get; private set; } = "No tile set has been loaded yet.";

    /// <summary>
    /// Show a tile set. Pass null to clear.
    /// </summary>
    /// <param name="tileSet">Geometry and collision flags.</param>
    /// <param name="sheetPath">Resolved path of the sheet image.</param>
    public void Load(TileSetInfo? tileSet, string? sheetPath)
    {
        _sheet?.Dispose();
        _sheet = null;
        _tileSet = tileSet;
        _columns = 0;
        _rows = 0;
        _selected = 0;
        _hovered = -1;

        if (tileSet is null)
        {
            LoadDiagnostic = "No tile set was supplied — the image is not enabled for tile-set use, "
                + "or its tile width/height is zero.";
        }
        else if (string.IsNullOrWhiteSpace(sheetPath))
        {
            LoadDiagnostic =
                $"Tile set '{tileSet.ImagePath}' did not resolve to a sheet image. Its frame-0 texture "
                + "could not be found, so there is nothing to draw a grid over.";
        }
        else if (!File.Exists(sheetPath))
        {
            LoadDiagnostic = $"Sheet image '{sheetPath}' does not exist on disk.";
        }
        else
        {
            try
            {
                // Decode through an explicit read-shared stream, then copy the bitmap off it.
                // `new Bitmap(path)` keeps its own handle on the file for the bitmap's lifetime,
                // which fails with GDI+'s unhelpful "Parameter is not valid" as soon as anything
                // else in the process already holds that PNG — and the Asset Browser's first-frame
                // thumbnail cache (NEXT-019) holds exactly this file. Copying off the decoded image
                // also means the sheet is not held open while a designer re-saves it in the Image
                // Editor. Same pattern as TerrainEntityListPanel/TerrainEntityWizardDialog.
                using FileStream stream = File.Open(
                    sheetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using Bitmap decoded = new(stream);
                _sheet = new Bitmap(decoded);
                _columns = tileSet.ColumnsFor(_sheet.Width);
                _rows = tileSet.RowsFor(_sheet.Height);

                LoadDiagnostic = TileCount > 0
                    ? string.Empty
                    : $"Sheet '{sheetPath}' is {_sheet.Width}x{_sheet.Height} but "
                      + $"{tileSet.TileWidth}x{tileSet.TileHeight} tiles (margin {tileSet.Margin}, "
                      + $"separation {tileSet.Separation}) divide it into {_columns}x{_rows} tiles.";
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or OutOfMemoryException)
            {
                // A sheet mid-write or in an unreadable format leaves the picker empty rather than
                // taking the Room Editor down with it — but it must say so.
                _sheet = null;
                LoadDiagnostic = $"Sheet '{sheetPath}' could not be decoded: {exception.Message}";
            }
        }

        Invalidate();
    }

    /// <summary>Select a tile programmatically, keeping it in range.</summary>
    public void Select(int index)
    {
        if (!HasSheet) return;

        int clamped = Math.Clamp(index, 0, TileCount - 1);
        if (clamped == _selected) return;

        _selected = clamped;
        Invalidate();
    }

    /// <summary>Client-space centre of a tile's cell — the point a designer would click.</summary>
    public Point CentreOf(int index)
    {
        if (!HasSheet || index < 0 || index >= TileCount) return Point.Empty;

        RecomputeCellSize();
        int column = index % _columns;
        int row = index / _columns;
        return new Point(
            Padding.Left + (column * _cellSize) + (_cellSize / 2) + AutoScrollPosition.X,
            Padding.Top + (row * _cellSize) + (_cellSize / 2) + AutoScrollPosition.Y);
    }

    /// <summary>
    /// Click at a client point, going through the real mouse handler.
    /// </summary>
    /// <remarks>
    /// Exists so headless tests exercise hit testing, selection and the <see cref="TileSelected"/>
    /// event together, rather than calling <see cref="Select"/> and proving only that a field can be
    /// assigned. The whole point of NEXT-061 was that the API worked and the UI did not.
    /// </remarks>
    public void SimulateClick(Point clientPoint) =>
        OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, clientPoint.X, clientPoint.Y, 0));

    /// <summary>The tile under a client point, or -1.</summary>
    public int TileAt(Point clientPoint)
    {
        if (!HasSheet) return -1;

        Point scrolled = new(clientPoint.X - AutoScrollPosition.X, clientPoint.Y - AutoScrollPosition.Y);
        int localX = scrolled.X - Padding.Left;
        int localY = scrolled.Y - Padding.Top;

        // Reject before dividing. C# integer division truncates toward ZERO, not toward negative
        // infinity, so -26 / 48 is 0 — a click above or left of the grid would resolve to tile 0
        // and silently change the brush.
        if (localX < 0 || localY < 0) return -1;

        int column = localX / _cellSize;
        int row = localY / _cellSize;
        if (column >= _columns || row >= _rows) return -1;

        int index = (row * _columns) + column;
        return index < TileCount ? index : -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int index = TileAt(e.Location);
        if (index < 0 || index == _selected) return;

        _selected = index;
        Invalidate();
        TileSelected?.Invoke(index);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = TileAt(e.Location);
        if (index == _hovered) return;

        _hovered = index;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hovered < 0) return;

        _hovered = -1;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RecomputeCellSize();
        Invalidate();
    }

    /// <summary>
    /// Fit the sheet's columns to the panel width, within legibility bounds. A 16-wide sheet in a
    /// 200px palette would otherwise render tiles too small to tell apart.
    /// </summary>
    private void RecomputeCellSize()
    {
        if (_columns <= 0)
        {
            _cellSize = 32;
            return;
        }

        int available = Math.Max(MinCellPixels, ClientSize.Width - Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
        _cellSize = Math.Clamp(available / _columns, MinCellPixels, MaxCellPixels);
        AutoScrollMinSize = new Size(0, Padding.Vertical + (_rows * _cellSize));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.Clear(EditorChrome.Canvas);

        if (!HasSheet)
        {
            using SolidBrush muted = new(EditorChrome.Muted);
            using StringFormat centre = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(
                _tileSet is null
                    ? "Pick a tile set above to choose a tile."
                    : "That image has no readable tile grid.",
                EditorChrome.SmallFont, muted, ClientRectangle, centre);
            return;
        }

        RecomputeCellSize();
        g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);

        // Nearest-neighbour: tile art is pixel art, and smoothing it in the picker misrepresents
        // what will actually be painted.
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        for (int index = 0; index < TileCount; index++)
        {
            int column = index % _columns;
            int row = index / _columns;
            Rectangle target = new(
                Padding.Left + (column * _cellSize),
                Padding.Top + (row * _cellSize),
                _cellSize,
                _cellSize);

            Rectangle source = _tileSet!.TileRect(index, _sheet!.Width);
            g.DrawImage(_sheet, target, source, GraphicsUnit.Pixel);

            if (_tileSet.IsSolid(index))
            {
                // A corner wedge rather than a full tint, so the tile's own art stays readable —
                // but outlined in the surface colour, because a bare amber triangle disappears
                // against the warm tiles it most often marks (ground, stone, sand).
                int wedge = Math.Max(7, _cellSize / 4);
                Point[] corner =
                [
                    new(target.Right - wedge, target.Top + 1),
                    new(target.Right - 1, target.Top + 1),
                    new(target.Right - 1, target.Top + wedge),
                ];

                using SolidBrush solid = new(EditorChrome.Warning);
                using Pen outline = new(EditorChrome.Surface, 1.5f);
                g.FillPolygon(solid, corner);
                g.DrawLine(outline, corner[0], corner[2]);
            }

            using Pen grid = new(Color.FromArgb(70, EditorChrome.Border));
            g.DrawRectangle(grid, target);

            if (index == _hovered && index != _selected)
            {
                using Pen hover = new(Color.FromArgb(150, EditorChrome.Text));
                g.DrawRectangle(hover, target.X + 1, target.Y + 1, target.Width - 2, target.Height - 2);
            }
        }

        // Selection last, so it is never overdrawn by a neighbour's grid line.
        int selectedColumn = _selected % _columns;
        int selectedRow = _selected / _columns;
        Rectangle selection = new(
            Padding.Left + (selectedColumn * _cellSize),
            Padding.Top + (selectedRow * _cellSize),
            _cellSize,
            _cellSize);
        using Pen selectionPen = new(Color.White, 2f);
        g.DrawRectangle(selectionPen, selection.X + 1, selection.Y + 1, selection.Width - 2, selection.Height - 2);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sheet?.Dispose();
            _sheet = null;
        }

        base.Dispose(disposing);
    }
}
