namespace Genesis.Application.Editors.Suite.UiKit;

/// <summary>Retains native tab navigation while painting the entire header in the editor palette.</summary>
internal sealed class EditorTabControl : TabControl
{
    public EditorTabControl()
    {
        SizeMode = TabSizeMode.Fixed;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        RefreshTabSize();
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        RefreshTabSize();
    }

    private void RefreshTabSize()
    {
        int width = TabPages.Cast<TabPage>().Select(page => TextRenderer.MeasureText(page.Text, Font).Width + 20)
            .DefaultIfEmpty(80).Max();
        ItemSize = new Size(width, Font.Height + 12);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using SolidBrush surface = new(EditorChrome.Surface);
        e.Graphics.FillRectangle(surface, ClientRectangle);
        using Pen border = new(EditorChrome.Border);
        Rectangle page = DisplayRectangle;
        page.Inflate(1, 1);
        e.Graphics.DrawRectangle(border, page);
        for (int index = 0; index < TabCount; index++)
            OnDrawItem(new DrawItemEventArgs(e.Graphics, Font, GetTabRect(index), index,
                index == SelectedIndex ? DrawItemState.Selected : DrawItemState.None));
        base.OnPaint(e);
    }
}
