using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.UiKit;

/// <summary>Compact on/off switch matching Application Design Images toggles.</summary>
public sealed class KitToggle : Control
{
    private bool _hovered;

    public KitToggle()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);
        Size = new Size(44, 24);
        Cursor = Cursors.Hand;
        EditorChrome.Changed += (_, _) => Invalidate();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked { get; set; }

    public event EventHandler? CheckedChanged;

    protected override void OnClick(EventArgs e)
    {
        Checked = !Checked;
        CheckedChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
        base.OnClick(e);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Rectangle track = new(0, 2, Width - 1, Height - 5);
        Color trackColor = Checked
            ? EditorChrome.Accent
            : (_hovered ? EditorChrome.Hover : EditorChrome.Raised);
        KitDraw.FillRound(g, track, trackColor, Height / 2);
        KitDraw.StrokeRound(g, track, Checked ? EditorChrome.Accent : EditorChrome.Border, Height / 2);

        int knob = Height - 8;
        int x = Checked ? Width - knob - 4 : 4;
        Rectangle knobRect = new(x, 4, knob, knob);
        KitDraw.FillRound(g, knobRect, Color.White, knob / 2);
    }
}
