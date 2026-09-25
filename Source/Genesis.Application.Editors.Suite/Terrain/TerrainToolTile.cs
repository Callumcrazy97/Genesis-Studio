using System.Drawing.Drawing2D;

namespace Genesis.Application.Editors.Suite.Terrain;

internal sealed class TerrainToolTile : Button
{
    public bool Active { get; set; }
    public string Glyph { get; set; } = "";
    public Color GlyphColor { get; set; } = Color.CornflowerBlue;
    public bool TextureTile { get; set; }

    public TerrainToolTile()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        Font = new Font("Segoe UI", 9f); Cursor = Cursors.Hand;
        Margin = new Padding(2); Size = new Size(62, 66);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(1, 1, Width - 3, Height - 3);
        using var fill = new LinearGradientBrush(bounds, Active ? Color.FromArgb(57, 135, 177) : Color.FromArgb(61, 66, 72),
            Active ? Color.FromArgb(35, 99, 141) : Color.FromArgb(42, 46, 51), 90f);
        using var card = new GraphicsPath();
        const int corner = 12;
        card.AddArc(bounds.Left, bounds.Top, corner, corner, 180, 90);
        card.AddArc(bounds.Right - corner, bounds.Top, corner, corner, 270, 90);
        card.AddArc(bounds.Right - corner, bounds.Bottom - corner, corner, corner, 0, 90);
        card.AddArc(bounds.Left, bounds.Bottom - corner, corner, corner, 90, 90); card.CloseFigure();
        g.FillPath(fill, card);
        using var border = new Pen(Active ? Color.FromArgb(102, 181, 226) : Color.FromArgb(18, 21, 25));
        g.DrawPath(border, card);
        if (TextureTile && Image is not null) g.DrawImage(Image, new Rectangle(8, 5, Width - 16, Height - 28));
        else
        {
            DrawGlyph(g);
        }
        int captionHeight = TextRenderer.MeasureText(Text, Font).Width > Width - 8 ? Font.Height * 2 + 4 : Font.Height + 6;
        TextRenderer.DrawText(g, Text, Font, new Rectangle(4, Height - captionHeight - 2, Width - 8, captionHeight), Enabled ? Color.WhiteSmoke : Color.Gray,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        if (Focused) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(bounds, -3, -3));
    }

    private void DrawGlyph(Graphics graphics)
    {
        if (Text is not ("Create" or "Raise" or "Lower" or "Smooth"))
        {
            using var font = new Font("Segoe UI Symbol", 23f);
            int captionHeight = TextRenderer.MeasureText(Text, Font).Width > Width - 8 ? Font.Height * 2 + 4 : Font.Height + 6;
            TextRenderer.DrawText(graphics, Glyph, font, new Rectangle(0, 2, Width, Math.Max(1, Height - captionHeight - 4)), Enabled ? GlyphColor : Color.Gray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return;
        }
        var state = graphics.Save(); graphics.TranslateTransform(Width / 2f - 17, 7);
        using var ground = new SolidBrush(Color.FromArgb(81, 149, 79));
        graphics.FillPolygon(ground, new Point[] { new(0, 30), new(9, 20), new(23, 23), new(34, 30) });
        if (Text == "Smooth")
        {
            using var hill = new GraphicsPath(); hill.AddBezier(0, 27, 8, 2, 18, 36, 34, 12);
            hill.AddLine(34, 12, 34, 30); hill.AddLine(34, 30, 0, 30); hill.CloseFigure(); graphics.FillPath(ground, hill);
        }
        else
        {
            using var arrow = new SolidBrush(GlyphColor); using var edge = new Pen(Color.FromArgb(29, 54, 68), 1.5f);
            Point[] points = Text == "Raise"
                ? [new(17, 0), new(6, 12), new(12, 12), new(12, 25), new(22, 25), new(22, 12), new(28, 12)]
                : [new(12, 0), new(22, 0), new(22, 13), new(28, 13), new(17, 25), new(6, 13), new(12, 13)];
            graphics.FillPolygon(arrow, points); graphics.DrawPolygon(edge, points);
        }
        graphics.Restore(state);
    }
}
