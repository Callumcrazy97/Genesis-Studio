using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Genesis.Runtime.Scripting;

namespace Genesis.Application.Editors.Suite.Objects;

/// <summary>
/// The list of events an object handles, grouped by category and owner-drawn.
/// </summary>
/// <remarks>
/// Replaces a stock `TreeView`, which rendered category headers as if they were disabled items and
/// gave selection a system-blue highlight that fought the theme. Here a category is a small rule and
/// a caption, an event is a full-width row with a category glyph, and selection is an accent bar —
/// so the list reads as structure rather than as a folder tree that happens to contain events.
/// </remarks>
public sealed class EventListPanel : ScrollableControl
{
    private const int RowHeight = 34;
    private const int HeaderHeight = 24;

    private sealed record Row(string? EventId, string Text, bool IsHeader, bool HasCode = false);

    private readonly List<Row> _rows = [];
    private IReadOnlyCollection<string> _eventIds = [];
    private IReadOnlyCollection<string>? _withCode;
    private string _filter = string.Empty;
    private string? _selected;
    private int _hoveredIndex = -1;
    public bool IsThreeD { get; set; }

    public EventListPanel()
    {
        DoubleBuffered = true;
        AutoScroll = true;
        HandleCreated += (_, _) => EditorScrollHost.ApplyDarkScrollTheme(this);
        SetStyle(ControlStyles.ResizeRedraw, true);
        BackColor = EditorChrome.Surface;
        ContextMenuStrip menu = new();
        ToolStripMenuItem remove = new("Remove event");
        remove.Click += (_, _) =>
        {
            if (_selected is not null) EventRemoveRequested?.Invoke(_selected);
        };
        menu.Items.Add(remove);
        menu.Opening += (_, args) => args.Cancel = _selected is null;
        ContextMenuStrip = menu;
        Disposed += (_, _) => menu.Dispose();
    }

    /// <summary>Raised when an event row is clicked.</summary>
    public event Action<string>? EventSelected;

    /// <summary>Raised by the row context menu; the owning editor controls confirmation and persistence.</summary>
    public event Action<string>? EventRemoveRequested;

    /// <summary>Event ids currently listed.</summary>
    public IReadOnlyList<string> EventIds =>
        [.. _rows.Where(row => row.EventId is not null).Select(row => row.EventId!)];

    /// <summary>
    /// Rebuild from the events in use, grouped in catalogue order.
    /// </summary>
    /// <param name="eventIds">Every event the object handles — i.e. every event file that exists.</param>
    /// <param name="selected">The event to show as selected, if any.</param>
    /// <param name="withCode">
    /// Which of those events actually contain code. Rows outside this set draw a hollow dot, so an
    /// event you created but have not written yet is visible as such without disappearing.
    /// </param>
    public void SetEvents(
        IReadOnlyCollection<string> eventIds,
        string? selected,
        IReadOnlyCollection<string>? withCode = null)
    {
        _eventIds = eventIds;
        _withCode = withCode;
        _selected = selected;
        RebuildRows();
    }

    /// <summary>Filter by event label, id, category, or description without changing selection.</summary>
    public void SetFilter(string? filter)
    {
        _filter = filter?.Trim() ?? string.Empty;
        RebuildRows();
    }

    private void RebuildRows()
    {
        _rows.Clear();
        foreach (string category in ObjectEventCatalog.Categories)
        {
            List<ObjectEventDefinition> used =
            [
                .. ObjectEventCatalog.InCategory(category)
                    .Where(definition => _eventIds.Contains(definition.Id, StringComparer.OrdinalIgnoreCase))
                    .Where(definition => MatchesFilter(definition, category)),
            ];
            if (used.Count == 0) continue;

            foreach (ObjectEventDefinition definition in used)
            {
                bool hasCode = _withCode is null
                    || _withCode.Contains(definition.Id, StringComparer.OrdinalIgnoreCase);
                _rows.Add(new Row(definition.Id, definition.Label, IsHeader: false, hasCode));
            }
        }
        AutoScrollMinSize = new Size(0, _rows.Count * DpiLayout.Scale(this, RowHeight) + 12);
        Invalidate();
    }

    private bool MatchesFilter(ObjectEventDefinition definition, string category) =>
        _filter.Length == 0
        || definition.Id.Contains(_filter, StringComparison.OrdinalIgnoreCase)
        || definition.Label.Contains(_filter, StringComparison.OrdinalIgnoreCase)
        || definition.Description.Contains(_filter, StringComparison.OrdinalIgnoreCase)
        || category.Contains(_filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>Select a row without raising the event.</summary>
    public void Select(string? eventId)
    {
        _selected = eventId;
        Invalidate();
    }

    /// <summary>Click a row by id, exercising the same path a mouse click takes.</summary>
    public bool ClickEvent(string eventId)
    {
        if (!_rows.Any(row => string.Equals(row.EventId, eventId, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        _selected = eventId;
        Invalidate();
        EventSelected?.Invoke(eventId);
        return true;
    }

    /// <summary>The currently selected event, if any.</summary>
    public string? SelectedEventId => _selected;

    private int IndexAt(Point point)
    {
        int rowHeight = DpiLayout.Scale(this, RowHeight);
        int headerHeight = DpiLayout.Scale(this, HeaderHeight);
        int y = DpiLayout.Scale(this, 6) + AutoScrollPosition.Y;
        for (int index = 0; index < _rows.Count; index++)
        {
            int height = _rows[index].IsHeader ? headerHeight : rowHeight;
            if (point.Y >= y && point.Y < y + height) return index;
            y += height;
        }

        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = IndexAt(e.Location);
        if (index >= 0 && _rows[index].IsHeader) index = -1;
        if (index == _hoveredIndex) return;

        _hoveredIndex = index;
        Cursor = index < 0 ? Cursors.Default : Cursors.Hand;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoveredIndex = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int index = IndexAt(e.Location);
        if (index >= 0 && _rows[index].EventId is { } id) ClickEvent(id);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(EditorChrome.Surface);

        if (_rows.Count == 0)
        {
            using SolidBrush muted = new(EditorChrome.Muted);
            using StringFormat centre = new() { Alignment = StringAlignment.Center };
            g.DrawString(
                "No events yet.\nAdd one below.",
                EditorChrome.SmallFont,
                muted,
                new RectangleF(
                    DpiLayout.Scale(this, 8),
                    DpiLayout.Scale(this, 24),
                    Width - DpiLayout.Scale(this, 16),
                    DpiLayout.Scale(this, 60)),
                centre);
            return;
        }

        int rowHeight = DpiLayout.Scale(this, RowHeight);
        int headerHeight = DpiLayout.Scale(this, HeaderHeight);
        int y = DpiLayout.Scale(this, 6) + AutoScrollPosition.Y;
        for (int index = 0; index < _rows.Count; index++)
        {
            Row row = _rows[index];
            string label = row.EventId == "Draw" && IsThreeD ? "Draw 3D" : row.EventId == "Destroy" ? "Clean Up" : row.Text;
            if (row.IsHeader)
            {
                using SolidBrush caption = new(EditorChrome.Muted);
                using Font font = new(EditorChrome.SmallFont, FontStyle.Bold);
                g.DrawString(row.Text, font, caption,
                    DpiLayout.Scale(this, 12), y + DpiLayout.Scale(this, 6));

                // A hairline from the caption to the panel edge groups what follows it.
                float textWidth = g.MeasureString(row.Text, font).Width;
                using Pen rule = new(Color.FromArgb(60, EditorChrome.Border));
                float ruleY = y + DpiLayout.Scale(this, 13);
                g.DrawLine(rule,
                    DpiLayout.Scale(this, 14) + textWidth,
                    ruleY,
                    Width - DpiLayout.Scale(this, 12),
                    ruleY);
                y += headerHeight;
                continue;
            }

            bool selected = string.Equals(row.EventId, _selected, StringComparison.OrdinalIgnoreCase);
            Rectangle bounds = new(0, y, Width, rowHeight);

            if (selected)
            {
                using SolidBrush fill = new(EditorChrome.Raised);
                g.FillRectangle(fill, bounds);
                using SolidBrush accent = new(EditorChrome.Accent);
                g.FillRectangle(accent, 0, y, DpiLayout.Scale(this, 3), rowHeight);
            }
            else if (index == _hoveredIndex)
            {
                using SolidBrush fill = new(EditorChrome.Hover);
                g.FillRectangle(fill, bounds);
            }

            // A filled dot means "this event has code", a hollow one means "created but empty".
            string glyph = row.EventId?.StartsWith("Alarm", StringComparison.Ordinal) == true ? "◴" : row.EventId switch { "Create" => "⚙", "Step" or "StepBegin" or "StepEnd" => "◷", "Draw" => "⬡", "Collision" => "⇄", "Destroy" => "⇥", _ => "◇" };
            using (var iconFont = new Font("Segoe UI Symbol", 13f))
                TextRenderer.DrawText(g, glyph, iconFont, new Rectangle(6, y + 3, 22, rowHeight - 6), EditorChrome.Text, TextFormatFlags.HorizontalCenter);
            using SolidBrush text = new(selected ? EditorChrome.Text : EditorChrome.Muted);
            // Never `using` EditorChrome.BaseFont — disposing the shared font makes every later
            // DrawString throw "Parameter is not valid" and paints the classic WinForms red X.
            if (selected)
            {
                using Font bold = new(EditorChrome.BaseFont, FontStyle.Bold);
                g.DrawString(label, bold, text,
                    DpiLayout.Scale(this, 26), y + DpiLayout.Scale(this, 4));
            }
            else
            {
                g.DrawString(label, EditorChrome.BaseFont, text,
                    DpiLayout.Scale(this, 26), y + DpiLayout.Scale(this, 4));
            }

            y += rowHeight;
        }
    }
}
