using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Genesis.Runtime.Scripting;

namespace Genesis.Application.Editors.Suite.Objects;

/// <summary>
/// The row of open events above the code editor, drawn as real tabs.
/// </summary>
/// <remarks>
/// Owner-drawn rather than a `FlowLayoutPanel` of `Button`s: flat buttons in a row read as a
/// toolbar, not as tabs, so nothing told you which event the code below belonged to. Here the
/// active tab carries the canvas colour and an accent underline, so it visually joins the editor
/// beneath it — which is the entire job of a tab.
/// </remarks>
public sealed class EventTabStrip : Control
{
    private const int TabPadding = 16;
    private const int MinTabWidth = 74;
    private const int UnderlineHeight = 2;

    private readonly List<(string Id, string Label, RectangleF Bounds)> _tabs = [];
    private string? _active;
    private string? _hovered;

    public EventTabStrip()
    {
        DoubleBuffered = true;
        Height = 34;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    /// <summary>Raised when a tab is clicked.</summary>
    public event Action<string>? TabSelected;

    /// <summary>Event ids currently shown, in order.</summary>
    public IReadOnlyList<string> Tabs => [.. _tabs.Select(tab => tab.Id)];

    /// <summary>Replace the tabs. <paramref name="eventIds"/> is expected in display order.</summary>
    public void SetTabs(IEnumerable<string> eventIds, string? active)
    {
        _tabs.Clear();
        foreach (string id in eventIds)
        {
            _tabs.Add((id, ObjectEventCatalog.Find(id)?.Label ?? id, RectangleF.Empty));
        }

        _active = active;
        MeasureTabs();
        Invalidate();
    }

    /// <summary>Click a tab by id, exercising the same path a mouse click takes.</summary>
    public bool ClickTab(string eventId)
    {
        if (!_tabs.Any(tab => string.Equals(tab.Id, eventId, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        _active = eventId;
        Invalidate();
        TabSelected?.Invoke(eventId);
        return true;
    }

    private void MeasureTabs()
    {
        using Graphics graphics = CreateGraphics();
        float x = DpiLayout.Scale(this, 8);
        for (int index = 0; index < _tabs.Count; index++)
        {
            (string id, string label, _) = _tabs[index];
            float width = Math.Max(
                DpiLayout.Scale(this, MinTabWidth),
                graphics.MeasureString(label, EditorChrome.BaseFont).Width
                + DpiLayout.Scale(this, TabPadding));
            float top = DpiLayout.Scale(this, 4);
            _tabs[index] = (id, label, new RectangleF(x, top, width, Height - top));
            x += width + DpiLayout.Scale(this, 2);
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        MeasureTabs();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        string? hit = HitTest(e.Location);
        if (hit == _hovered) return;

        _hovered = hit;
        Cursor = hit is null ? Cursors.Default : Cursors.Hand;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = null;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (HitTest(e.Location) is { } id) ClickTab(id);
    }

    private string? HitTest(Point point) =>
        _tabs.FirstOrDefault(tab => tab.Bounds.Contains(point)).Id;

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(EditorChrome.Surface);

        // The strip sits on the code editor, so a rule along the bottom joins the two — except
        // under the active tab, which is drawn last and breaks the line.
        using (Pen rule = new(EditorChrome.Border))
        {
            g.DrawLine(rule, 0, Height - 1, Width, Height - 1);
        }

        if (_tabs.Count == 0)
        {
            using SolidBrush empty = new(EditorChrome.Muted);
            g.DrawString(
                "No events — add one from the left.",
                EditorChrome.SmallFont,
                empty,
                DpiLayout.Scale(this, 10),
                DpiLayout.Scale(this, 9));
            return;
        }

        using StringFormat format = new()
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
        };

        foreach ((string id, string label, RectangleF bounds) in _tabs)
        {
            bool active = string.Equals(id, _active, StringComparison.OrdinalIgnoreCase);
            bool hovered = string.Equals(id, _hovered, StringComparison.OrdinalIgnoreCase);

            if (active)
            {
                using SolidBrush fill = new(EditorChrome.Canvas);
                g.FillRectangle(fill, bounds);
            }
            else if (hovered)
            {
                using SolidBrush fill = new(EditorChrome.Hover);
                g.FillRectangle(
                    fill,
                    bounds.X,
                    bounds.Y + DpiLayout.Scale(this, 2),
                    bounds.Width,
                    bounds.Height - DpiLayout.Scale(this, 3));
            }

            using SolidBrush text = new(active ? EditorChrome.Text : EditorChrome.Muted);
            // Never `using` EditorChrome.BaseFont — disposing the shared font makes every later
            // DrawString throw "Parameter is not valid" and paints the classic WinForms red X.
            if (active)
            {
                using Font bold = new(EditorChrome.BaseFont, FontStyle.Bold);
                g.DrawString(label, bold, text, bounds, format);
                using SolidBrush accent = new(EditorChrome.Accent);
                g.FillRectangle(
                    accent,
                    bounds.X,
                    bounds.Y,
                    bounds.Width,
                    DpiLayout.Scale(this, UnderlineHeight));
            }
            else
            {
                g.DrawString(label, EditorChrome.BaseFont, text, bounds, format);
            }
        }
    }
}
