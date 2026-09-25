using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Image.Dialogs;

internal static class RgbaColourDialog
{
    public static bool TryShow(IWin32Window owner,Color initial,out Color colour)
    {
        colour=initial;if(Genesis.Application.Core.Diagnostics.UnattendedSession.IsActive)return false;
        using var dialog=new DpiAwareForm { Text="Colour · RGBA",ClientSize=new Size(355,310),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false };
        var preview=new ImageColourSwatch { Colour=initial,Location=new Point(220,15),Size=new Size(110,110) };
        var fields=new NumericUpDown[4];byte[] components=[initial.R,initial.G,initial.B,initial.A];string[] labels=["Red","Green","Blue","Alpha"];
        for(int i=0;i<4;i++){fields[i]=new NumericUpDown { Minimum=0,Maximum=255,Value=components[i],Location=new Point(100,15+i*38),Width=90 };dialog.Controls.Add(new Label { Text=labels[i],AutoSize=true,Location=new Point(16,19+i*38) });dialog.Controls.Add(fields[i]);}
        var hex=new TextBox { Text=ImagePaletteStorage.Hex(initial),Location=new Point(100,176),Width=180,AccessibleName="RGBA hex colour" };
        var ok=new Button { Text="Use colour",DialogResult=DialogResult.OK,Location=new Point(215,258),Size=new Size(115,32) };
        var cancel=new Button { Text="Cancel",DialogResult=DialogResult.Cancel,Location=new Point(100,258),Size=new Size(105,32) };
        var choose=new Button { Text="Colour wheel…",Location=new Point(220,138),Size=new Size(110,32) };
        bool syncing=false;
        void Update(){if(syncing)return;syncing=true;preview.Colour=Color.FromArgb((int)fields[3].Value,(int)fields[0].Value,(int)fields[1].Value,(int)fields[2].Value);hex.Text=ImagePaletteStorage.Hex(preview.Colour);ok.Enabled=true;syncing=false;}
        foreach(var field in fields)field.ValueChanged+=(_,_)=>Update();
        hex.TextChanged+=(_,_)=>{if(syncing)return;ok.Enabled=ImagePaletteStorage.TryColour(hex.Text,out var selected);if(!ok.Enabled)return;syncing=true;fields[0].Value=selected.R;fields[1].Value=selected.G;fields[2].Value=selected.B;fields[3].Value=selected.A;preview.Colour=selected;syncing=false;};
        choose.Click+=(_,_)=>{using var picker=new ColorDialog{Color=preview.Colour,FullOpen=true};if(picker.ShowDialog(dialog)==DialogResult.OK){syncing=true;fields[0].Value=picker.Color.R;fields[1].Value=picker.Color.G;fields[2].Value=picker.Color.B;syncing=false;Update();}};
        dialog.Controls.AddRange([preview,hex,choose,ok,cancel,new Label {Text="#RRGGBB or #RRGGBBAA · Alpha 0 is transparent.",Location=new Point(16,216),Size=new Size(320,32)}]);
        dialog.AcceptButton=ok;dialog.CancelButton=cancel;ThemeMessageBox.ApplyTheme?.Invoke(dialog);
        if(dialog.ShowDialog(owner)!=DialogResult.OK)return false;colour=preview.Colour;return true;
    }
}
