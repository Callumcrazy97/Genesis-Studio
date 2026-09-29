using System.Drawing;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEntityWizardPanel
{
    private static IEnumerable<Control> EnumerateControls(Control root)
    {
        yield return root;
        foreach(Control child in root.Controls)
        foreach(Control nested in EnumerateControls(child))yield return nested;
    }

    // Keep each authored label with its field, then let measured rows consume the available width.
    // Existing field bindings and resource-picker callbacks survive the change in presentation.
    private static Control ArrangeAuthoredFields(Panel original)
    {
        Control[] controls=original.Controls.Cast<Control>().ToArray();
        var used=new HashSet<Control>();
        var rows=new List<(int Top,int Left,Control Row)>();
        foreach(Control field in controls.Where(control=>control is not Label))
        {
            Label? caption=controls.OfType<Label>().Where(label=>!used.Contains(label)&&label.Left==field.Left
                &&label.Top<=field.Top&&field.Top-label.Top<=30).OrderByDescending(label=>label.Top).FirstOrDefault();
            int top=caption?.Top??field.Top,left=field.Left;
            field.Parent?.Controls.Remove(field);used.Add(field);
            Control row=field;
            if(caption is not null)
            {
                caption.Parent?.Controls.Remove(caption);used.Add(caption);
                var pair=new TableLayoutPanel {ColumnCount=1,AutoSize=true,Dock=DockStyle.Top};
                pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                caption.AutoSize=true;caption.Dock=DockStyle.Top;pair.Controls.Add(caption);
                field.Dock=DockStyle.Top;pair.Controls.Add(field);row=pair;
            }
            rows.Add((top,left,row));
        }
        foreach(Control control in controls.Where(control=>!used.Contains(control)))
        {
            int top=control.Top,left=control.Left;original.Controls.Remove(control);rows.Add((top,left,control));
        }
        var stack=new TableLayoutPanel {ColumnCount=1,AutoSize=true,Dock=DockStyle.Top};
        stack.ColumnStyles.Add(new(SizeType.Percent,100));
        foreach(var row in rows.OrderBy(row=>row.Top).ThenBy(row=>row.Left))
        {
            row.Row.Dock=DockStyle.Top;row.Row.Anchor=AnchorStyles.Left|AnchorStyles.Right|AnchorStyles.Top;
            row.Row.Margin=new Padding(0,4,0,10);stack.Controls.Add(row.Row);
        }
        void Fit()
        {
            int width=Math.Max(180,stack.ClientSize.Width-10);
            foreach(Control control in EnumerateControls(stack))
            {
                if(control is Label {AutoSize:true} label)label.MaximumSize=new Size(width,0);
                if(control is ComboBox combo)combo.ItemHeight=combo.Font.Height+8;
                if(control is Button button)button.MinimumSize=new Size(0,button.Font.Height+18);
                if(control is Genesis.Application.Editors.Suite.Scripts.CodeEditor code)code.Height=Math.Max(180,EditorChrome.BaseFont.Height*12);
            }
        }
        stack.SizeChanged+=(_,_)=>Fit();stack.FontChanged+=(_,_)=>Fit();
        original.Dispose();return stack;
    }
}
