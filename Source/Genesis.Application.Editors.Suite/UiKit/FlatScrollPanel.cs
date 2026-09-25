using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.UiKit;

/// <summary>
/// Panel that hides the native WinForms scrollbar and draws a thin Genesis Dark thumb instead.
/// Put content in <see cref="Content"/>; set content height larger than the host to scroll.
/// </summary>
public sealed class FlatScrollPanel : Panel
{
    private const int BarWidth = 8;
    private readonly Panel _viewport = new();
    private readonly Panel _content = new() { Location = Point.Empty };
    private bool _dragging;
    private int _dragOffset;

    public FlatScrollPanel()
    {
        DoubleBuffered = true;
        BackColor = EditorChrome.Canvas;
        Controls.Add(_viewport);
        _viewport.Controls.Add(_content);
        _viewport.BackColor = EditorChrome.Canvas;
        _content.BackColor = EditorChrome.Canvas;
        _viewport.Resize += (_, _) => LayoutContent();
        MouseDown += OnBarMouseDown;
        MouseMove += OnBarMouseMove;
        MouseUp += (_, _) => _dragging = false;
        MouseWheel += OnWheel;
        EditorChrome.Changed += OnChromeChanged;
        Disposed += (_, _) => EditorChrome.Changed -= OnChromeChanged;
    }

    /// <summary>Host for child controls. Grow its Height to enable scrolling.</summary>
    public Panel Content => _content;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int ScrollOffset
    {
        get => -_content.Top;
        set
        {
            int max = Math.Max(0, _content.Height - _viewport.Height);
            int next = Math.Clamp(value, 0, max);
            _content.Top = -next;
            Invalidate();
        }
    }

    public void SetContentHeight(int height)
    {
        _content.Width = Math.Max(1, _viewport.ClientSize.Width);
        _content.Height = Math.Max(height, _viewport.Height);
        LayoutContent();
    }

    private void OnChromeChanged(object? sender, EventArgs e)
    {
        BackColor = EditorChrome.Canvas;
        _viewport.BackColor = EditorChrome.Canvas;
        _content.BackColor = EditorChrome.Canvas;
        Invalidate();
    }

    private void LayoutContent()
    {
        _content.Width = Math.Max(1, _viewport.ClientSize.Width);
        if (_content.Height < _viewport.Height)
        {
            _content.Height = _viewport.Height;
        }

        ScrollOffset = ScrollOffset;
    }

    private void OnWheel(object? sender, MouseEventArgs e)
    {
        ScrollOffset -= Math.Sign(e.Delta) * 48;
    }

    private Rectangle BarBounds
    {
        get
        {
            Rectangle client = ClientRectangle;
            return new Rectangle(client.Right - BarWidth, client.Top, BarWidth, client.Height);
        }
    }

    private Rectangle ThumbBounds
    {
        get
        {
            Rectangle bar = BarBounds;
            int range = Math.Max(1, _content.Height - _viewport.Height);
            if (range <= 0 || _content.Height <= _viewport.Height)
            {
                return Rectangle.Empty;
            }

            int thumbH = Math.Max(24, (int)(bar.Height * (_viewport.Height / (float)_content.Height)));
            int travel = Math.Max(1, bar.Height - thumbH);
            int thumbY = bar.Top + (int)(travel * (ScrollOffset / (float)range));
            return new Rectangle(bar.Left + 1, thumbY, BarWidth - 2, thumbH);
        }
    }

    private void OnBarMouseDown(object? sender, MouseEventArgs e)
    {
        if (!BarBounds.Contains(e.Location))
        {
            return;
        }

        Rectangle thumb = ThumbBounds;
        if (thumb.Contains(e.Location))
        {
            _dragging = true;
            _dragOffset = e.Y - thumb.Top;
            return;
        }

        int range = Math.Max(1, _content.Height - _viewport.Height);
        int travel = Math.Max(1, BarBounds.Height - Math.Max(24, thumb.Height));
        float t = (e.Y - BarBounds.Top - (thumb.Height / 2f)) / travel;
        ScrollOffset = (int)(range * Math.Clamp(t, 0f, 1f));
    }

    private void OnBarMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        Rectangle bar = BarBounds;
        Rectangle thumb = ThumbBounds;
        int travel = Math.Max(1, bar.Height - Math.Max(24, thumb.Height));
        int range = Math.Max(1, _content.Height - _viewport.Height);
        float t = (e.Y - _dragOffset - bar.Top) / (float)travel;
        ScrollOffset = (int)(range * Math.Clamp(t, 0f, 1f));
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        _viewport.SetBounds(0, 0, Math.Max(0, Width - BarWidth), Height);
        LayoutContent();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Rectangle bar = BarBounds;
        using SolidBrush track = new(EditorChrome.Surface);
        e.Graphics.FillRectangle(track, bar);
        Rectangle thumb = ThumbBounds;
        if (!thumb.IsEmpty)
        {
            KitDraw.FillRound(e.Graphics, thumb, EditorChrome.Border, 3);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using SolidBrush brush = new(EditorChrome.Canvas);
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }
}
