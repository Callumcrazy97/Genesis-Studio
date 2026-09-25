using System.ComponentModel;

namespace Genesis.Application.Editors.Image.Controls;

/// <summary>Keyboard-accessible page buttons with the same theme as the editor command bars.</summary>
public sealed class ImageRigPages : UserControl
{
    private readonly FlowLayoutPanel _header = new() { Dock = DockStyle.Top, Height = 38, WrapContents = false };
    private readonly Panel _body = new() { Dock = DockStyle.Fill };
    private readonly List<Panel> _pages = [];
    private readonly List<Button> _buttons = [];
    private int _selected = -1;
    public ImageRigPages() { Controls.Add(_body); Controls.Add(_header); }
    public event EventHandler? SelectedIndexChanged;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value < 0 || value >= _pages.Count || value == _selected) return;
            _selected = value;
            for (int i=0;i<_pages.Count;i++) { _pages[i].Visible=i==value; _buttons[i].BackColor=i==value?ImageEditorChrome.Hover:ImageEditorChrome.Raised; }
            SelectedIndexChanged?.Invoke(this,EventArgs.Empty);
        }
    }
    public void AddPage(Panel page)
    {
        int index=_pages.Count;_pages.Add(page);page.Dock=DockStyle.Fill;_body.Controls.Add(page);
        var button=new Button { Text=page.Text,AutoSize=true,MinimumSize=new Size(90,32),AccessibleName=page.Text+" window" };
        ImageEditorChrome.StyleButton(button);button.Click+=(_,_)=>SelectedIndex=index;_buttons.Add(button);_header.Controls.Add(button);
        if(_selected<0)SelectedIndex=0;else page.Visible=false;
    }
}
