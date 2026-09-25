using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Dialogs;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Headless.Suites;

namespace Genesis.Application.Headless;

internal static class LayerFrameCaptureRunner
{
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var workspace=ImageLayerFramesSuite.Fixture();
        using var editor=new ImageEditorControl(new ImageDocumentSession(ImageDocument.CreateDefault(16,16)),workspace);
        editor.AddRasterLayer("Reference silhouette");
        for(int frame=0;frame<3;frame++)
        {
            var layer=workspace.Frames[frame].Layers[1];
            for(int y=3;y<13;y++) for(int x=3+frame*2;x<7+frame*2;x++) RasterOperations.SetPixel(layer.Pixels,16,16,x,y,Color.CadetBlue);
        }
        editor.GeneratePbrMaterialSet(new PbrMaterialSettings{Seamless=false});
        editor.RefreshFromDocument();
        using var host=new Form { Text="Genesis Studio · Layer and frame review",ClientSize=new Size(1440,1000),StartPosition=FormStartPosition.CenterScreen };
        host.Controls.Add(editor);Genesis.Application.Studio.Theme.ThemeService.Apply(host);
        UnattendedWindowing.Configure(host);UnattendedWindowing.ShowWithoutFocus(host);
        System.Windows.Forms.Application.DoEvents();
        using(var image=VisualCapture.CaptureWindowPixels(host)) image.Save(Path.Combine(output,"layers.png"));
        static IEnumerable<Control> Children(Control control)=>control.Controls.Cast<Control>().SelectMany(c=>new[]{c}.Concat(Children(c)));
        foreach(var section in Children(editor).OfType<CollapsibleSection>().Where(s=>s.HeaderText is "Layers" or "Preview")) section.IsExpanded=false;
        Children(editor).OfType<CollapsibleSection>().Single(s=>s.HeaderText=="Onion Skin").IsExpanded=true;
        workspace.SelectedLayerIndex=0;editor.RefreshFromDocument();editor.SetOnionSkin(true,1,1);
        ((ComboBox)typeof(ImageEditorControl).GetField("_onionSource",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(editor)!).SelectedIndex=1;
        var references=(CheckedListBox)editor.Controls.Find("OnionOtherLayers",true).Single();references.SetItemChecked(references.Items.Count-1,true);
        VisualCapture.Capture(host,Path.Combine(output,"onion-layers.png"),captureFromScreen:true);
        using var effect=new EffectPreviewDialog(null,workspace.CurrentLayer!.Pixels,16,16,new ImageEffectDefinition("Effect · frame targets",[],(pixels,_,_,_)=>pixels));
        var targets=ImageFrameTargetControl.Attach(effect,1,3,"Each frame uses its own pixels. Preview shows the current frame.");
        ((ComboBox)targets.Controls.Find("FrameTargetScope",true).Single()).SelectedIndex=1;
        Genesis.Application.Studio.Theme.ThemeService.Apply(effect);
        VisualCapture.Capture(effect,Path.Combine(output,"effect-frame-range.png"),captureFromScreen:true);
        Console.WriteLine("Layer, onion and frame target captures: "+output);return 0;
    }
}
