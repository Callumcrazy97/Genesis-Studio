using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Dialogs;
using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Headless.Suites;

internal static class ImageLayerFramesSuite
{
    private static T Field<T>(ImageEditorControl e,string name)=>(T)typeof(ImageEditorControl).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(e)!;
    private static void Scope(ImageEditorControl e,ImageFrameSelection selection,Action action)=>typeof(ImageEditorControl)
        .GetMethod("InFrameScope",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(e,[selection.Resolve(e.Workspace.SelectedFrameIndex,e.Workspace.Frames.Count),action]);

    internal static ImageWorkspace Fixture()
    {
        var workspace=ImageWorkspace.CreateBlank(16,16,Color.Transparent);
        for(int i=0;i<3;i++)
        {
            if(i>0) workspace.AddFrame(true);
            RasterOperations.Clear(workspace.CurrentLayer!.Pixels,16,16,Color.Transparent);
            RasterOperations.SetPixel(workspace.CurrentLayer.Pixels,16,16,2+i*4,8,Color.FromArgb(255,30+i*60,80,120));
        }
        workspace.SelectedFrameIndex=1; workspace.Touch(); return workspace;
    }
    public static void Run(HeadlessContext ctx)
    {
        var resources=HeadlessHarness.Require(ctx.Resources,"Resources");
        void Check(string name,Action<ImageEditorControl> test)=>HeadlessHarness.RunCase(ctx.Report,"Editor.Image.LayerFrames."+name,()=>
        {
            string path=resources.CreateResource(resources.AssetsRoot,ResourceKind.Image,"Layer frames "+name);
            using var editor=new ImageEditorControl(new ImageDocumentSession(ImageDocument.CreateDefault(16,16),path,ImageDocumentAccess.Editor),Fixture());
            test(editor);
        });
        Check("OnionCheckboxAndOpacityRefreshPixels",editor=>
        {
            byte[] before=editor.GetPreviewPixels();
            Field<CheckBox>(editor,"_onion").Checked=true;
            byte[] after=editor.GetPreviewPixels();
            int previous=(8*16+2)*4,next=(8*16+10)*4;
            HeadlessHarness.Assert(before[previous+3]==0 && after[previous+3]>0 && after[next+3]>0,"Enable onion skin did not show previous/next artwork.");
            HeadlessHarness.Assert(after[previous+2]>after[previous] && after[next]>after[next+2],"Ghost direction tints are wrong.");
            byte alpha=after[previous+3]; Field<NumericUpDown>(editor,"_onionOpacity").Value=70;
            HeadlessHarness.Assert(editor.GetPreviewPixels()[previous+3]>alpha,"Ghost opacity did not update.");
            Field<CheckBox>(editor,"_onion").Checked=false;
            HeadlessHarness.AssertPixelsEqual(editor.GetPreviewPixels(),before,"Disabling onion did not restore the artwork");
        });
        Check("SpecifiedHiddenReferenceLayersAndSave",editor=>
        {
            editor.AddRasterLayer("Reference");
            var reference=editor.Workspace.CurrentLayer!;
            RasterOperations.SetPixel(reference.Pixels,16,16,3,3,Color.Black);
            editor.SetLayerVisible(false); editor.Workspace.SelectedLayerIndex=0; editor.RefreshFromDocument();
            Field<ComboBox>(editor,"_onionSource").SelectedIndex=1;
            Field<CheckedListBox>(editor,"_onionLayers").SetItemChecked(0,true);
            editor.SetOnionSkin(true,0,0);
            byte[] pixels=editor.GetPreviewPixels(); int p=(3*16+3)*4;
            HeadlessHarness.Assert(pixels[p+3]>0 && pixels[p]>0,"A selected hidden black reference did not produce a visible current-frame ghost.");
            editor.Save();
            var doc=ImageDocumentSerializer.LoadAtomic(editor.Session.DocumentPath!).Document;
            using var reopened=new ImageEditorControl(new ImageDocumentSession(doc,editor.Session.DocumentPath),ImageWorkspaceStorage.Load(new ImageDocumentSession(doc,editor.Session.DocumentPath)));
            reopened.Workspace.SelectedFrameIndex=1; reopened.RefreshFromDocument();
            HeadlessHarness.Assert(doc.OnionSkin.SpecifiedLayers && doc.OnionSkin.LayerIds.Contains(reference.Id),"Onion reference preferences were not saved.");
            HeadlessHarness.Assert(reopened.GetPreviewPixels()[p+3]>0,"Reopened reference ghosts are missing.");
        });
        Check("GhostsVisibleOverOpaqueArtwork",editor=>
        {
            RasterOperations.Clear(editor.Workspace.CurrentLayer!.Pixels,16,16,Color.White); editor.Workspace.Touch();
            editor.SetOnionSkin(true,1,1);
            byte[] pixels=editor.GetPreviewPixels();int p=(8*16+2)*4;
            HeadlessHarness.Assert(pixels[p]<255 && pixels[p+3]==255,"Opaque current artwork hid enabled onion ghosts.");
        });
        Check("AllVisibleChannelsAndColourExport",editor=>
        {
            editor.AddRasterLayer("Normal map");
            var layer=editor.Workspace.CurrentLayer!;layer.Channel=ImageMaterialChannel.Normal;
            RasterOperations.SetPixel(layer.Pixels,16,16,2,2,Color.Magenta);editor.Workspace.Touch();editor.RefreshFromDocument();
            int p=(2*16+2)*4;
            HeadlessHarness.Assert(editor.GetPreviewPixels()[p+3]==255 && editor.CompositeEditorFrame(1)[p+3]==255,"A visible material layer is absent from the canvas/composite preview.");
            HeadlessHarness.Assert(editor.Workspace.CompositeCurrentFrame()[p+3]==0,"Material preview contaminated exported colour pixels.");
            editor.SetLayerVisible(false);HeadlessHarness.Assert(editor.GetPreviewPixels()[p+3]==0,"Hidden layer remained in preview.");
            editor.SetLayerVisible(true);Field<NumericUpDown>(editor,"_opacity").Value=50;
            HeadlessHarness.Assert(Math.Abs(editor.GetPreviewPixels()[p+3]-128)<2,"Layer opacity is not reflected in the composite.");
        });
        Check("LayerSelectionDoesNotRebuildAndEyeToggles",editor=>
        {
            for(int i=0;i<7;i++) editor.AddRasterLayer("Layer "+i);
            var list=Field<ListBox>(editor,"_layers");list.CreateControl(); list.TopIndex=3;
            object[] rows=list.Items.Cast<object>().ToArray();int changes=0;list.SelectedIndexChanged+=(_,_)=>changes++;
            list.SelectedIndex=4;
            HeadlessHarness.Assert(changes==1 && rows.SequenceEqual(list.Items.Cast<object>()),"Layer selection rebuilt/reset its list.");
            editor.SetLayerVisible(false);editor.SetLayerVisible(true);
            HeadlessHarness.Assert(editor.Workspace.CurrentLayer!.Visible && list.SelectedIndex==4 && rows.SequenceEqual(list.Items.Cast<object>()),"Visibility toggles reset the selected layer.");
            var row=list.GetItemRectangle(list.SelectedIndex);
            typeof(Control).GetMethod("OnMouseDown",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(list,[new MouseEventArgs(MouseButtons.Left,1,18,row.Top+15,0)]);
            HeadlessHarness.Assert(!editor.Workspace.CurrentLayer.Visible,"The eye hit area did not toggle visibility.");
            editor.RenameLayer("Foreground"); Field<NumericUpDown>(editor,"_layerDepth").Value=1;
            HeadlessHarness.Assert(editor.Workspace.Frames.All(f=>f.Layers[0].Name=="Foreground"),"Depth/name edit did not follow the selected layer across frames.");
        });
        Check("FrameScopeBoundaries",editor=>
        {
            int[] Resolve(ImageFrameScope scope,int current=1,int first=1,int last=1)=>new ImageFrameSelection(scope,first,last).Resolve(current,3);
            HeadlessHarness.Assert(Resolve(ImageFrameScope.ThisFrame).SequenceEqual([1])&&Resolve(ImageFrameScope.AllFrames).SequenceEqual([0,1,2]),"This/All frame targets are wrong.");
            HeadlessHarness.Assert(Resolve(ImageFrameScope.Range,first:2,last:3).SequenceEqual([1,2])&&Resolve(ImageFrameScope.Range,first:2,last:2).SequenceEqual([1]),"Inclusive numbered range is wrong.");
            HeadlessHarness.Assert(Resolve(ImageFrameScope.PreviousFrames).SequenceEqual([0])&&Resolve(ImageFrameScope.NextFrames).SequenceEqual([2]),"Previous/Next included the current frame.");
            HeadlessHarness.Assert(Resolve(ImageFrameScope.PreviousFrames,0).Length==0&&Resolve(ImageFrameScope.NextFrames,2).Length==0,"Boundary target wrapped frames.");
            bool invalid=false;try{Resolve(ImageFrameScope.Range,first:3,last:2);}catch(ArgumentException){invalid=true;}
            HeadlessHarness.Assert(invalid,"Invalid range was silently accepted.");
        });
        Check("PerFrameEffectAtomicUndoRedo",editor=>
        {
            var frames=editor.Workspace.Frames;var before=frames.Select(f=>(byte[])f.Layers[0].Pixels.Clone()).ToArray();
            byte[] Effect(byte[] pixels){for(int i=0;i<pixels.Length;i+=4)pixels[i]=(byte)(255-pixels[i]);return pixels;}
            editor.ApplyFrameOperation("Invert red",Effect,new(ImageFrameScope.Range,2,3));
            HeadlessHarness.AssertPixelsEqual(frames[0].Layers[0].Pixels,before[0],"Effect changed a frame outside the range");
            HeadlessHarness.Assert(frames[1].Layers[0].Pixels[(8*16+6)*4]==165 && frames[2].Layers[0].Pixels[(8*16+10)*4]==105,"Effect reused the current frame instead of each cel.");
            HeadlessHarness.Assert(editor.Workspace.SelectedFrameIndex==1,"Range processing moved the current frame.");
            editor.Undo();for(int i=0;i<3;i++)HeadlessHarness.AssertPixelsEqual(frames[i].Layers[0].Pixels,before[i],"One undo did not restore the full range");
            editor.Redo();HeadlessHarness.Assert(frames[2].Layers[0].Pixels[(8*16+10)*4]==105,"Range redo lost target pixels.");
        });
        Check("EffectFailureRollsBackAndLockedCelsSkip",editor=>
        {
            var frames=editor.Workspace.Frames;var before=frames.Select(f=>(byte[])f.Layers[0].Pixels.Clone()).ToArray();int call=0;
            try{editor.ApplyFrameOperation("Failure",pixels=>{pixels[0]=255;if(++call==2)throw new InvalidOperationException("Test failure");return pixels;},new(ImageFrameScope.AllFrames));}catch(InvalidOperationException){}
            for(int i=0;i<3;i++)HeadlessHarness.AssertPixelsEqual(frames[i].Layers[0].Pixels,before[i],"Failed range partially modified the image");
            frames[1].Layers[0].Locked=true;
            editor.ApplyEffect(ImageEffectCatalog.Invert,new Dictionary<string,object>());
            HeadlessHarness.AssertPixelsEqual(frames[1].Layers[0].Pixels,before[1],"Default single-frame effect changed a locked layer");
            editor.ApplyFrameOperation("Unlocked",pixels=>{pixels[0]=17;return pixels;},new(ImageFrameScope.AllFrames));
            HeadlessHarness.Assert(frames[0].Layers[0].Pixels[0]==17&&frames[1].Layers[0].Pixels[0]==before[1][0]&&frames[2].Layers[0].Pixels[0]==17,
                "Locked cel was edited or unlocked cels skipped: "+string.Join(", ",frames.Select(f=>f.Layers[0].Pixels[0])));
        });
        Check("ScopedMaterialGenerationPersistsEmptyCels",editor=>
        {
            Scope(editor,new(ImageFrameScope.NextFrames),()=>editor.GeneratePbrMaterialSet(new PbrMaterialSettings { Seamless=false }));
            var frames=editor.Workspace.Frames;int count=frames[2].Layers.Count;
            HeadlessHarness.Assert(count>1&&frames.All(f=>f.Layers.Count==count),"Generated material layers have inconsistent frame structure.");
            HeadlessHarness.Assert(frames[0].Layers[1].Pixels.All(b=>b==0)&&frames[1].Layers[1].Pixels.All(b=>b==0)&&frames[2].Layers[1].Pixels.Any(b=>b!=0),"PBR generation escaped its target range.");
            editor.Save();var document=ImageDocumentSerializer.LoadAtomic(editor.Session.DocumentPath!).Document;
            var loaded=ImageWorkspaceStorage.Load(new ImageDocumentSession(document,editor.Session.DocumentPath));
            HeadlessHarness.AssertPixelsEqual(loaded.Frames[2].Layers[1].Pixels,frames[2].Layers[1].Pixels,"Material cel persistence");
        });
        Check("ScopedCanvasKeepsUntargetedFrames",editor=>
        {
            byte[] before=(byte[])editor.Workspace.Frames[0].Layers[0].Pixels.Clone();
            Scope(editor,new(ImageFrameScope.Range,2,2),()=>editor.RotateCanvas(90));
            HeadlessHarness.AssertPixelsEqual(editor.Workspace.Frames[0].Layers[0].Pixels,before,"Partial rotation changed another frame");
            HeadlessHarness.Assert(editor.Workspace.Width==16&&editor.Workspace.Height==16,"Partial rotation changed shared canvas size.");
            HeadlessHarness.Assert(RasterOperations.GetPixel(editor.Workspace.Frames[1].Layers[0].Pixels,16,16,7,6).A>0,"Selected frame did not rotate.");
        });
        Check("TargetSelectorLayoutAndFiveModes",editor=>
        {
            using var dialog=new EffectPreviewDialog(null,editor.Workspace.CurrentLayer!.Pixels,16,16,new ImageEffectDefinition("Scope probe",[],(p,_,_,_)=>p));
            var selector=ImageFrameTargetControl.Attach(dialog,1,3,"Each target uses its own artwork.");
            UnattendedWindowing.Configure(dialog);UnattendedWindowing.ShowWithoutFocus(dialog);System.Windows.Forms.Application.DoEvents();
            var combo=(ComboBox)selector.Controls.Find("FrameTargetScope",true).Single();
            HeadlessHarness.Assert(combo.Items.Count==5&&selector.FrameIndices.SequenceEqual([1]),"Dialog lacks all five targets or defaults incorrectly.");
            combo.SelectedIndex=2;HeadlessHarness.Assert(selector.FrameIndices.SequenceEqual([0,1,2]),"Dialog All Frames selection does not resolve.");
            var body=dialog.Controls.Cast<Control>().Single(c=>c!=selector);
            HeadlessHarness.Assert(!body.Bounds.IntersectsWith(selector.Bounds)&&selector.Height>45&&selector.Height<155,"Frame target header overlaps the effect controls.");
            dialog.Close();
        });
    }
}
