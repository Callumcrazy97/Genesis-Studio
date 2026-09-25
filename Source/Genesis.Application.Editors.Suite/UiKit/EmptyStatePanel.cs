using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.UiKit;

/// <summary>Instructional empty canvas / list placeholder.</summary>
public sealed class EmptyStatePanel : Panel
{
    public EmptyStatePanel()
    {
        DoubleBuffered = true;
        BackColor = EditorChrome.Canvas;
        EditorChrome.Changed += (_, _) => Invalidate();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string TitleText { get; set; } = "Nothing here yet";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string BodyText { get; set; } = "Pick a preset to get started.";

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        using SolidBrush fill = new(EditorChrome.Canvas);
        g.FillRectangle(fill, ClientRectangle);

        Rectangle title = new(24, Math.Max(24, (Height / 2) - 28), Width - 48, 24);
        Rectangle body = new(24, title.Bottom + 6, Width - 48, 48);
        TextRenderer.DrawText(
            g,
            TitleText,
            EditorChrome.HeadingFont,
            title,
            EditorChrome.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(
            g,
            BodyText,
            EditorChrome.BaseFont,
            body,
            EditorChrome.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
    }
}
