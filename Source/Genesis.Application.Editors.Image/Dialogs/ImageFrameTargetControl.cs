using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Image.Dialogs;

/// <summary>Shared target selector for image operations. Does not change the editor's selected frame.</summary>
public sealed class ImageFrameTargetControl : UserControl
{
    private readonly ComboBox _scope=new() { Name="FrameTargetScope",DropDownStyle=ComboBoxStyle.DropDownList,Width=190 };
    private readonly NumericUpDown _first=new() { Name="FrameTargetFirst",Minimum=1,Width=70 };
    private readonly NumericUpDown _last=new() { Name="FrameTargetLast",Minimum=1,Width=70 };
    private readonly Label _summary=new() { AutoSize=true,Padding=new Padding(4),MaximumSize=new Size(800,0) };
    private readonly int _current,_count;
    public ImageFrameTargetControl(int current,int count,string? note=null)
    {
        Name="ImageFrameTargets"; _current=current; _count=Math.Max(1,count);
        Dock=DockStyle.Top; AutoSize=true; AutoSizeMode=AutoSizeMode.GrowAndShrink;
        BackColor=ImageEditorChrome.Surface; ForeColor=ImageEditorChrome.Text; Padding=new Padding(8);
        _scope.Items.AddRange(["This Frame","Frame Number / Range","All Frames","Previous Frames","Next Frames"]);
        _first.Maximum=_last.Maximum=_count; _first.Value=_last.Value=Math.Clamp(current+1,1,_count);
        var flow=new FlowLayoutPanel { Dock=DockStyle.Top,AutoSize=true,WrapContents=true };
        static Label Label(string text)=>new(){Text=text,AutoSize=true,Padding=new Padding(0,5,0,0)};
        flow.Controls.AddRange([Label("Apply to"),_scope,Label("From"),_first,Label("To"),_last]);
        var rows=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=1,RowCount=2};
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        rows.Controls.Add(flow); rows.Controls.Add(_summary); Controls.Add(rows);
        void Update()
        {
            _first.Enabled=_last.Enabled=_scope.SelectedIndex==(int)ImageFrameScope.Range;
            int targets=Selection.Resolve(_current,_count).Length;
            _summary.Text=$"{targets} of {_count} frames · current frame {_current+1}. Previous/Next exclude this frame."+(note==null ? "" : "\n"+note);
        }
        _scope.SelectedIndexChanged+=(_,_)=>Update();
        _first.ValueChanged+=(_,_)=>{ if(_last.Value<_first.Value) _last.Value=_first.Value; Update(); };
        _last.ValueChanged+=(_,_)=>{ if(_first.Value>_last.Value) _first.Value=_last.Value; Update(); };
        _scope.SelectedIndex=0;
    }
    public ImageFrameSelection Selection=>new((ImageFrameScope)_scope.SelectedIndex,(int)_first.Value,(int)_last.Value);
    public int[] FrameIndices=>Selection.Resolve(_current,_count);
    public void SetSelection(ImageFrameSelection selection)
    {
        selection.Resolve(_current,_count);
        _first.Value=Math.Clamp(selection.First,1,_count); _last.Value=Math.Clamp(selection.Last,(int)_first.Value,_count);
        _scope.SelectedIndex=(int)selection.Scope;
    }

    public static ImageFrameTargetControl Attach(Form dialog,int current,int count,string? note=null)
    {
        // Keep existing coordinates and docked previews in a body below a common header.
        var body=new Panel { Dock=DockStyle.Fill,AutoScroll=true };
        var children=dialog.Controls.Cast<Control>().ToArray();
        foreach(var child in children) body.Controls.Add(child);
        for(int i=0;i<children.Length;i++) body.Controls.SetChildIndex(children[i],i);
        var selector=new ImageFrameTargetControl(current,count,note);
        dialog.ClientSize=new Size(Math.Max(630,dialog.ClientSize.Width),dialog.ClientSize.Height+110);
        dialog.Controls.Add(body); dialog.Controls.Add(selector); body.BringToFront();
        dialog.FormClosing+=(_,e)=>
        {
            if(dialog.DialogResult==DialogResult.OK && selector.FrameIndices.Length==0)
            { e.Cancel=true; dialog.DialogResult=DialogResult.None; selector._summary.Text="No frames in this direction. Choose another target before applying."; }
        };
        return selector;
    }
}
