using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.UiKit;

/// <summary>
/// Owner-drawn combo that paints with <see cref="EditorChrome"/> instead of the system white list.
/// </summary>
public sealed class ThemedComboBox : ComboBox
{
    private IntPtr _listBrush = IntPtr.Zero;

    public ThemedComboBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        ItemHeight = 24;
        IntegralHeight = false;
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        EditorChrome.Changed += OnChromeChanged;
        Disposed += (_, _) =>
        {
            EditorChrome.Changed -= OnChromeChanged;
            DestroyBrush();
        };
        ApplyChrome();
    }

    private void OnChromeChanged(object? sender, EventArgs e) => ApplyChrome();

    private void ApplyChrome()
    {
        BackColor = EditorChrome.Raised;
        ForeColor = EditorChrome.Text;
        Font = EditorChrome.BaseFont;
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
        Color fill = selected ? EditorChrome.Accent : EditorChrome.Raised;
        Color text = selected ? Color.White : EditorChrome.Text;
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
        // WM_CTLCOLORLISTBOX — keep the drop-down list on Genesis Dark, not system white.
        const int wmCtlColorListBox = 0x0134;
        if (m.Msg == wmCtlColorListBox)
        {
            IntPtr dc = m.WParam;
            SetBkColor(dc, ColorTranslator.ToWin32(EditorChrome.Raised));
            SetTextColor(dc, ColorTranslator.ToWin32(EditorChrome.Text));
            if (_listBrush == IntPtr.Zero)
            {
                _listBrush = CreateSolidBrush(ColorTranslator.ToWin32(EditorChrome.Raised));
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
