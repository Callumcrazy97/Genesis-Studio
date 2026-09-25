using System.ComponentModel;

namespace Genesis.Application.Editors.Image.Controls;

/// <summary>Paint colour is document-tool state, independent of theme background colours.</summary>
internal sealed class ImageColourSwatch : Panel
{
    private Color _colour = Color.Black;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Colour { get => _colour; set { _colour = value; Invalidate(); } }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.Clear(Color.White);
        using var grey = new SolidBrush(Color.Gray);
        for(int y=0;y<Height;y+=6)for(int x=0;x<Width;x+=6)if((x/6+y/6)%2==0)e.Graphics.FillRectangle(grey,x,y,6,6);
        using var colour = new SolidBrush(Colour); e.Graphics.FillRectangle(colour,ClientRectangle);
    }
}

internal sealed class ImagePaletteButton(Color colour) : Button
{
    protected override void OnPaint(PaintEventArgs e)
    {
        using var fill = new SolidBrush(colour); e.Graphics.FillRectangle(fill,ClientRectangle);
        using var border = new Pen(Focused ? Color.White : ImageEditorChrome.Border);e.Graphics.DrawRectangle(border,0,0,Width-1,Height-1);
    }
}
