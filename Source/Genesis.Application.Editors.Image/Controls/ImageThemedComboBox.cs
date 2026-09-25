using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Image.Controls;

/// <summary>
/// Owner-drawn combo painted with <see cref="ImageEditorChrome"/> (Image assembly cannot
/// reference Suite UiKit). Drop-in replacement for themed DropDownList combos — same events/API.
/// </summary>
public sealed class ImageThemedComboBox : ComboBox
{
    private IntPtr _listBrush = IntPtr.Zero;

    public ImageThemedComboBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        ItemHeight = 24;
        IntegralHeight = false;
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        ImageEditorChrome.Changed += OnChromeChanged;
        Disposed += (_, _) =>
        {
            ImageEditorChrome.Changed -= OnChromeChanged;
            DestroyBrush();
        };
        ApplyChrome();
    }

    private void OnChromeChanged(object? sender, EventArgs e) => ApplyChrome();

    private void ApplyChrome()
    {
        BackColor = ImageEditorChrome.Raised;
        ForeColor = ImageEditorChrome.Text;
        Font = ImageEditorChrome.BaseFont;
        DestroyBrush();
        Invalidate();
    }

    private void DestroyBrush()
    {
        if (_listBrush != IntPtr.Zero)
        {
            DeleteObject(_listBrush);
            _listBrush = IntPtr.Zero;
        }
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count)
        {
            e.DrawBackground();
            return;
        }

        bool selected = (e.State & DrawItemState.Selected) != 0;
        Color fill = selected ? ImageEditorChrome.Accent : ImageEditorChrome.Raised;
        Color text = selected ? Color.White : ImageEditorChrome.Text;
        using SolidBrush background = new(fill);
        e.Graphics.FillRectangle(background, e.Bounds);
        TextRenderer.DrawText(
            e.Graphics,
            GetItemText(Items[e.Index]),
            Font,
            Rectangle.Inflate(e.Bounds, -6, 0),
            text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    protected override void WndProc(ref Message m)
    {
        const int wmCtlColorListBox = 0x0134;
        if (m.Msg == wmCtlColorListBox)
        {
            IntPtr dc = m.WParam;
            SetBkColor(dc, ColorTranslator.ToWin32(ImageEditorChrome.Raised));
            SetTextColor(dc, ColorTranslator.ToWin32(ImageEditorChrome.Text));
            if (_listBrush == IntPtr.Zero)
            {
                _listBrush = CreateSolidBrush(ColorTranslator.ToWin32(ImageEditorChrome.Raised));
            }

            m.Result = _listBrush;
            return;
        }

        base.WndProc(ref m);
    }

    [DllImport("gdi32.dll")]
    private static extern int SetBkColor(IntPtr hdc, int color);

    [DllImport("gdi32.dll")]
    private static extern int SetTextColor(IntPtr hdc, int color);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(int color);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
}
