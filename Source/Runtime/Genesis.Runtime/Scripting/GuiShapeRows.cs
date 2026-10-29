using System;
using System.Drawing;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// The pixel runs a smooth shape (a rounded or gradient box, a border, a circle) is drawn with, sent
/// to the surface as one batch. Such a shape is worked out one pixel row at a time (a column at a
/// time for a left-to-right gradient), a row being up to a few runs, and each run used to be a draw
/// call of its own: a 384-pixel panel was over a thousand, Mushroom Meadow's main menu twelve
/// thousand a frame. Here the runs that touch in a row in one colour join, and a run directly under
/// one of the row above with the same place, width and colour grows it (every row of a solid box
/// between its rounded corners, every row of a border's sides), so a solid box is a few rectangles
/// and a gradient about one a row. The rectangles do not overlap and cover the same pixels in the
/// same colours, so the picture is the one the rows made.
/// </summary>
internal sealed class GuiShapeRows
{
    /// <summary>False sends every run on its own, as shapes were drawn before (a test compares the two pictures).</summary>
    internal static bool Merge = true;

    private struct Run
    {
        public int Start, Length, Line, Depth, Argb;
    }

    [ThreadStatic] private static GuiShapeRows _spare;

    private IPgslDrawSurface _surface;
    private bool _transposed, _merge;

    // Every run made so far; the row being added; and the indexes in the first of the runs the row
    // above made, and (as a row is finished) of the runs this one makes, swapped as the next row starts.
    private Run[] _runs = new Run[256];
    private Run[] _row = new Run[32];
    private int[] _above = new int[32], _made = new int[32];
    private int _runCount, _rowCount, _aboveCount;
    private int _rowLine = int.MinValue, _aboveLine = int.MinValue;
    private GuiRectangle[] _batch = new GuiRectangle[256];

    /// <summary>Starts a shape; give every run to <see cref="Add"/> in row order, then call <see cref="End"/>. One per thread is reused.</summary>
    public static GuiShapeRows Begin(IPgslDrawSurface surface, bool transposed)
    {
        GuiShapeRows rows = _spare ?? new GuiShapeRows();
        _spare = null;
        rows._surface = surface;
        rows._transposed = transposed;
        rows._merge = Merge;
        return rows;
    }

    /// <summary>A run of <paramref name="length"/> pixels from <paramref name="start"/> along row (or column) <paramref name="line"/>, in <paramref name="color"/>.</summary>
    public void Add(int start, int line, int length, Color color)
    {
        if (length <= 0) return;
        if (!_merge)
        {
            _surface.FillRectangle(color, _transposed ? new RectangleF(line, start, 1, length) : new RectangleF(start, line, length, 1));
            return;
        }

        if (line != _rowLine)
        {
            FinishRow();
            _rowLine = line;
        }
        int argb = color.ToArgb();
        // Runs that touch in one colour are one run.
        if (_rowCount > 0)
        {
            ref Run last = ref _row[_rowCount - 1];
            if (last.Argb == argb && last.Start + last.Length == start)
            {
                last.Length += length;
                return;
            }
        }
        if (_rowCount == _row.Length) Array.Resize(ref _row, _rowCount * 2);
        _row[_rowCount++] = new Run { Start = start, Length = length, Line = line, Depth = 1, Argb = argb };
    }

    /// <summary>Sends the shape to the surface as one batch and makes this ready for the next.</summary>
    public void End()
    {
        if (_merge)
        {
            FinishRow();
            if (_runCount > 0)
            {
                if (_batch.Length < _runCount) _batch = new GuiRectangle[Math.Max(_runCount, _batch.Length * 2)];
                for (int i = 0; i < _runCount; i++)
                {
                    ref Run run = ref _runs[i];
                    Color color = Color.FromArgb(run.Argb);
                    Vector4 colour = new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
                    _batch[i] = _transposed
                        ? new GuiRectangle(run.Line, run.Start, run.Depth, run.Length, colour)
                        : new GuiRectangle(run.Start, run.Line, run.Length, run.Depth, colour);
                }
                _surface.FillRectangles(_batch.AsSpan(0, _runCount));
            }
        }

        _surface = null;
        _runCount = _rowCount = _aboveCount = 0;
        _rowLine = _aboveLine = int.MinValue;
        // A shape that made a very large batch is not kept: the next one starts small.
        if (_runs.Length <= 1 << 14) _spare = this;
    }

    // The row just added becomes the one above: each run of it either grows the run of the row above
    // that it sits directly under, or starts a new one.
    private void FinishRow()
    {
        if (_rowCount == 0) return;
        if (_made.Length < _rowCount) _made = new int[_row.Length];
        for (int i = 0; i < _rowCount; i++)
        {
            ref Run run = ref _row[i];
            int index = -1;
            if (_aboveLine == run.Line - 1)
            {
                for (int a = 0; a < _aboveCount; a++)
                {
                    ref Run above = ref _runs[_above[a]];
                    if (above.Start != run.Start || above.Length != run.Length || above.Argb != run.Argb) continue;
                    above.Depth++;
                    index = _above[a];
                    break;
                }
            }
            if (index < 0)
            {
                if (_runCount == _runs.Length) Array.Resize(ref _runs, _runCount * 2);
                index = _runCount++;
                _runs[index] = run;
            }
            _made[i] = index;
        }
        (_above, _made) = (_made, _above);
        _aboveCount = _rowCount;
        _aboveLine = _rowLine;
        _rowCount = 0;
    }
}
