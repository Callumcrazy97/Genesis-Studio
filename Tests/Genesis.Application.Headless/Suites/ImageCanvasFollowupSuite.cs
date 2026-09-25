using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Dialogs;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Image.Rigging;

namespace Genesis.Application.Headless.Suites;

internal static class ImageCanvasFollowupSuite
{
    public static void Run(HeadlessContext ctx)
    {
        var resources = HeadlessHarness.Require(ctx.Resources,"Resources");
        void Check(string name, Action<ImageEditorControl> test) => HeadlessHarness.RunCase(ctx.Report,"Editor.Image.Followup."+name,() =>
        {
            string path = resources.CreateResource(resources.AssetsRoot,ResourceKind.Image,"Followup "+name);
            using var editor = new ImageEditorControl(new ImageDocumentSession(ImageDocument.CreateDefault(16,16),path),ImageWorkspace.CreateBlank(16,16,Color.Transparent));
            test(editor);
        });
        static void Gesture(ImageEditorControl e, Point start, Point end, Keys modifiers = Keys.None)
        { e.SimulateImagePointer(start,true,false,modifiers); e.SimulateImagePointer(end,false,false,modifiers); e.SimulateImagePointer(end,false,true,modifiers); }
        static void Escape(ImageEditorControl e) => Call(e,"OnEditorKeyDown",e,new KeyEventArgs(Keys.Escape));
        foreach (var tool in new[] {ImageToolKind.RectSelect,ImageToolKind.EllipseSelect,ImageToolKind.MagicWand,ImageToolKind.LassoSelect})
            Check(tool+"AutomaticMoveAndEscape",e =>
            {
                e.DrawFilledRectangle(new(2,2,4,4),Color.Red); byte[] before=e.Workspace.CurrentLayer!.Pixels.ToArray();
                e.SetActiveTool(tool); e.Workspace.Selection.SetRectangle(new(2,2,4,4),false,false); e.RefreshFromDocument();
                e.SimulateImagePointer(new(3,3),false,false);
                HeadlessHarness.Assert(e.EffectiveTool == ImageToolKind.Move,"Hover inside must activate Move.");
                Gesture(e,new(3,3),new(10,10));
                HeadlessHarness.Assert(RasterOperations.GetPixel(e.GetPreviewPixels(),16,16,3,3).A == 0 && RasterOperations.GetPixel(e.GetPreviewPixels(),16,16,10,10).A == 255,"Automatic Move copied pixels or did not apply on release.");
                HeadlessHarness.Assert(e.Workspace.Selection.Contains(10,10),"Move lost the selected region.");
                e.SimulateImagePointer(new(0,0),false,false); HeadlessHarness.Assert(e.EffectiveTool == tool,"Hover outside did not restore the selection tool.");
                e.Undo(); HeadlessHarness.AssertPixelsEqual(e.Workspace.CurrentLayer.Pixels,before,"Automatic move atomic undo");
                Escape(e); HeadlessHarness.Assert(!e.Workspace.Selection.HasSelection && e.Canvas.Overlay.SelectionEdges.Count == 0 && !e.Canvas.Overlay.ShowShapePreview,"Escape left a selection or marching ants.");
                e.Workspace.Selection.SetRectangle(new(2,2,4,4),false,false);
                e.SimulateImagePointer(new(3,3),true,false); e.SimulateImagePointer(new(10,10),false,false); Escape(e); e.SimulateImagePointer(new(10,10),false,true);
                HeadlessHarness.AssertPixelsEqual(e.GetPreviewPixels(),before,"Escape during automatic move must restore source pixels");
                HeadlessHarness.Assert(!e.Workspace.Selection.HasSelection,"Cancelled move kept a selection.");
            });
        foreach (var tool in new[]{ImageToolKind.RectSelect,ImageToolKind.EllipseSelect,ImageToolKind.LassoSelect,ImageToolKind.MagicWand})
            Check(tool+"ReplaceAddSubtract",e =>
            {
                e.DrawFilledRectangle(new(1,1,3,3),Color.Red); e.DrawFilledRectangle(new(10,10,3,3),Color.Blue); e.SetActiveTool(tool);
                void Select(int x,int y,Keys modifiers = Keys.None)
                {
                    if (tool==ImageToolKind.MagicWand) Gesture(e,new(x+1,y+1),new(x+1,y+1),modifiers);
                    else if (tool==ImageToolKind.LassoSelect)
                    {
                        e.SimulateImagePointer(new(x,y),true,false,modifiers);
                        foreach(var p in new[]{new Point(x+3,y),new Point(x+3,y+3),new Point(x,y+3)}) e.SimulateImagePointer(p,false,false,modifiers);
                        e.SimulateImagePointer(new(x,y),false,true,modifiers);
                    }
                    else Gesture(e,new(x,y),new(x+3,y+3),modifiers);
                }
                Select(1,1); Select(10,10,Keys.Control);
                HeadlessHarness.Assert(e.Workspace.Selection.Contains(2,2)&&e.Workspace.Selection.Contains(11,11),"Ctrl did not extend selection.");
                Select(1,1,Keys.Shift);
                HeadlessHarness.Assert(!e.Workspace.Selection.Contains(2,2)&&e.Workspace.Selection.Contains(11,11),"Shift did not subtract selection.");
                Select(1,1);
                HeadlessHarness.Assert(e.Workspace.Selection.Contains(2,2)&&!e.Workspace.Selection.Contains(11,11),"Click outside failed to replace selection.");
                Escape(e);
            });
        Check("MovingSelectionRetainsTransparentAreas",e =>
        {
            e.DrawFilledRectangle(new(3,3,1,1),Color.Red);e.SetActiveTool(ImageToolKind.RectSelect);
            e.Workspace.Selection.SetRectangle(new(2,2,4,4),false,false);
            Gesture(e,new(2,2),new(7,7));
            HeadlessHarness.Assert(e.Workspace.Selection.GetBounds()==new Rectangle(7,7,4,4)&&e.Workspace.Selection.Contains(7,7),"Moving a selection shrank it to opaque pixels.");
        });
        Check("EscapeCancelsUnreleasedSelection",e =>
        {
            e.SetActiveTool(ImageToolKind.RectSelect); e.SimulateImagePointer(new(1,1),true,false); e.SimulateImagePointer(new(12,12),false,false);
            Escape(e); e.SimulateImagePointer(new(12,12),false,true);
            HeadlessHarness.Assert(!e.Workspace.Selection.HasSelection&&!e.Canvas.Overlay.ShowShapePreview,"Release resurrected an escaped selection.");
        });
        Check("LoopSharedHistoryPersistenceAndReadOnlyPreview",e =>
        {
            e.AddFrameForTests(true); e.SetAnimationTag("Attack",0,1,ImagePlaybackDirection.Forward,true);
            Field<ComboBox>(e,"_clip").SelectedIndex=1;
            using var viewer = new ImageViewerControl(e.Session,e.Workspace); Field<ComboBox>(viewer,"_clip").SelectedIndex=1;
            var editorLoop=Field<ImagePlaybackLoopButton>(e,"_playbackLoop"); var viewerLoop=Field<ImagePlaybackLoopButton>(viewer,"_playbackLoop");
            editorLoop.Checked=false;
            HeadlessHarness.Assert(!e.Session.Document.Tags[0].Loop&&!viewerLoop.Checked,"Loop changes are not shared with Viewer.");
            e.Undo(); HeadlessHarness.Assert(editorLoop.Checked&&viewerLoop.Checked,"Loop undo did not refresh both controls.");
            viewerLoop.Checked=false; e.Save(); var loaded=ImageDocumentSerializer.LoadAtomic(e.Session.DocumentPath!).Document;
            HeadlessHarness.Assert(!loaded.Tags[0].Loop,"Loop did not persist.");
            var readOnly=new ImageDocumentSession(loaded,access:ImageDocumentAccess.Viewer);
            using var preview=new ImagePlaybackLoopButton(readOnly,()=>1,false); preview.Checked=true;
            HeadlessHarness.Assert(!loaded.Tags[0].Loop&&!readOnly.IsDirty&&preview.Checked,"Read-only Loop modified metadata.");
            Call(e,"AdvancePlaybackFrame"); Call(e,"AdvancePlaybackFrame");
            HeadlessHarness.Assert(e.Workspace.SelectedFrameIndex==1,"Non-looping editor playback wrapped.");
            editorLoop.Checked=true; Call(e,"AdvancePlaybackFrame");
            HeadlessHarness.Assert(e.Workspace.SelectedFrameIndex==0,"Looping editor playback did not wrap.");
            Field<ComboBox>(viewer,"_clip").SelectedIndex=0;
            HeadlessHarness.Assert(!viewerLoop.Enabled&&!viewerLoop.Checked,"Viewer None must disable playback Loop.");
            foreach(var direction in Enum.GetValues<ImagePlaybackDirection>())
            {
                var single=ImageAnimationPlayback.AdvancePlayback(3,[3],1,direction,false);
                HeadlessHarness.Assert(single.FrameIndex==3&&single.ShouldStop,"Single-frame clip did not stop safely.");
            }
        });
        Check("TrimUnionMetadataHistoryAndReopen",e =>
        {
            e.DrawFilledRectangle(new(3,4,2,2),Color.Red); e.AddFrameForTests(false); e.DrawFilledRectangle(new(10,11,2,2),Color.Blue);
            var origin=e.Session.Document.Origin; origin.Space=ImageCoordinateSpace.Pixels; origin.X=8; origin.Y=9;
            var rig=new ImagePixelRig { Width=16,Height=16,LayerId=e.Workspace.CurrentLayer!.Id.ToString("N"),SourceFrameId=e.Workspace.CurrentFrame!.Id.ToString("N"),BindPixels=e.Workspace.CurrentLayer.Pixels.ToArray(),Bones=[new(){Start=new(){X=10,Y=11},End=new(){X=11,Y=12}}] };
            e.SavePixelRig(rig); var savedRig=e.Session.Document.PixelRigs[0];
            byte[][] before=e.Workspace.Frames.Select(f=>f.Layers[0].Pixels.ToArray()).ToArray();
            HeadlessHarness.Assert(e.RemoveBlankSpace()&&e.Workspace.Width==9&&e.Workspace.Height==9,"Trim must use the union of every frame.");
            HeadlessHarness.Assert(origin.X==5&&origin.Y==5&&savedRig.Width==9&&savedRig.Bones[0].Start.X==7&&savedRig.Bones[0].Start.Y==7,"Trim did not translate origin and saved rig.");
            e.Undo();
            HeadlessHarness.Assert(ReferenceEquals(origin,e.Session.Document.Origin)&&origin.X==8&&savedRig.Width==16,"Trim metadata undo replaced objects or failed.");
            for(int i=0;i<2;i++) HeadlessHarness.AssertPixelsEqual(e.Workspace.Frames[i].Layers[0].Pixels,before[i],"Trim undo frame "+i);
            e.Redo(); e.Save(); var loaded=ImageDocumentSerializer.LoadAtomic(e.Session.DocumentPath!).Document;
            HeadlessHarness.Assert(loaded.Canvas.Width==9&&loaded.PixelRigs[0].BindPixels.Length==9*9*4,"Trim did not reopen with matching rig pixels.");
        });
        Check("TrimEmptyIsNoOpAndHiddenLayersCount",e =>
        {
            long state=e.Session.History.CurrentStateToken;
            HeadlessHarness.Assert(!e.RemoveBlankSpace()&&e.Workspace.Width==16&&e.Session.History.CurrentStateToken==state,"Empty trim changed the document.");
            e.DrawFilledRectangle(new(2,3,4,5),Color.FromArgb(1,20,30,40)); e.Workspace.CurrentLayer!.Visible=false;
            HeadlessHarness.Assert(e.RemoveBlankSpace()&&e.Workspace.Width==4&&e.Workspace.Height==5,"Trim discarded faint or hidden pixels.");
        });
        Check("RotateQuarterTurnsArbitraryPreviewAndUndo",e =>
        {
            e.ResizeCanvas(3,2,false); byte[] p=e.Workspace.CurrentLayer!.Pixels;
            for(int i=0;i<6;i++) {p[i*4]=(byte)(i+1);p[i*4+3]=255;} e.Workspace.Touch(); byte[] before=p.ToArray();
            e.RotateCanvas(90); HeadlessHarness.Assert(e.Workspace.Width==2&&e.Workspace.Height==3,"Quarter turn dimensions wrong.");
            HeadlessHarness.Assert(Enumerable.Range(0,6).Select(i=>e.GetPreviewPixels()[i*4]).SequenceEqual(new byte[]{4,1,5,2,6,3}),"Clockwise pixel mapping is incorrect.");
            e.Undo(); HeadlessHarness.AssertPixelsEqual(e.GetPreviewPixels(),before,"Rotate undo");
            var expected=ImageCanvasTransforms.Rotate(before,3,2,35,true);e.RotateCanvas(35);
            HeadlessHarness.AssertPixelsEqual(e.GetPreviewPixels(),expected,"Rotation preview differs from commit");
            e.Undo(); e.RotateCanvas(-90); e.RotateCanvas(90); HeadlessHarness.AssertPixelsEqual(e.GetPreviewPixels(),before,"Opposite quarter turns are not lossless");
        });
        Check("NineSlicePreservesCornersModesAndHistory",e =>
        {
            e.ResizeCanvas(4,4,false); byte[] p=e.Workspace.CurrentLayer!.Pixels;
            for(int i=0;i<16;i++){p[i*4]=(byte)(i+1);p[i*4+3]=255;} e.Workspace.Touch(); byte[] before=p.ToArray();
            var s=new ImageNineSlice{Enabled=true,Left=1,Top=1,Right=1,Bottom=1,Centre=ImageSliceMode.Repeat};
            var result=ImageCanvasTransforms.NineSlice(before,4,4,new(8,7),s);
            HeadlessHarness.Assert(result[0]==1&&result[7*4]==4&&result[6*8*4]==13&&result[(7*8-1)*4]==16,"Nine-slice changed corner pixels.");
            HeadlessHarness.Assert(result[(1*8+1)*4]==6&&result[(1*8+3)*4]==6,"Centre repeat did not tile.");
            s.Centre=ImageSliceMode.Mirror;var mirrored=ImageCanvasTransforms.NineSlice(before,4,4,new(8,7),s);
            HeadlessHarness.Assert(mirrored[(1*8+2)*4]==7&&mirrored[(1*8+3)*4]==7,"Mirror repeats did not reverse.");
            s.Centre=ImageSliceMode.Transparent;var blank=ImageCanvasTransforms.NineSlice(before,4,4,new(8,7),s);
            HeadlessHarness.Assert(blank[(3*8+3)*4+3]==0&&blank[3]==255,"Transparent centre affected corners.");
            e.ApplyNineSlice(s,8,7); HeadlessHarness.AssertPixelsEqual(e.GetPreviewPixels(),blank,"Nine-slice preview/commit");
            e.Undo(); HeadlessHarness.AssertPixelsEqual(e.GetPreviewPixels(),before,"Nine-slice undo");
            e.Redo(); e.Save();var loaded=ImageDocumentSerializer.LoadAtomic(e.Session.DocumentPath!).Document;
            HeadlessHarness.Assert(loaded.NineSlice.Enabled&&loaded.NineSlice.Centre==ImageSliceMode.Transparent&&loaded.Canvas.Width==8,"Nine-slice save/reopen failed.");
            e.Undo();e.SetNineSlice(s);HeadlessHarness.Assert(e.Workspace.Width==4,"Save guides changed pixels.");
            e.Undo(); HeadlessHarness.Assert(!e.Session.Document.NineSlice.Enabled,"Save guides undo failed.");
        });
        Check("NineSliceDialogGuidesClampAndCancel",e =>
        {
            var original = new ImageNineSlice{Enabled=true,Left=3,Top=3,Right=3,Bottom=3};
            using var dialog = new ImageNineSliceDialog(e.GetPreviewPixels(),16,16,original);
            var margins=Field<NumericUpDown[]>(dialog,"_margins"); margins[0].Value=15;
            HeadlessHarness.Assert(dialog.Settings.Right==0&&dialog.OutputSize.Width>=16,"Crossing guides failed to push the opposite guide.");
            margins[0].Value=3; Call(dialog,"Drag",new PointF(6,7));
            HeadlessHarness.Assert(dialog.Settings.Left==3,"Hover moved an ungrabbed guide.");
            typeof(ImageNineSliceDialog).GetField("_dragGuide",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(dialog,0);
            Call(dialog,"Drag",new PointF(6,7)); HeadlessHarness.Assert(dialog.Settings.Left==6,"Dragging a guide failed.");
            dialog.Close(); HeadlessHarness.Assert(original.Left==3&&!e.Session.Document.NineSlice.Enabled,"Cancelled dialog mutated saved settings.");
            original.Enabled=false;
            using var disabled=new ImageNineSliceDialog(e.GetPreviewPixels(),16,16,original);
            HeadlessHarness.Assert(!disabled.Settings.Enabled,"Reopening nine-slice silently enabled saved disabled guides.");
        });
        Check("CanvasResizeRetainsValidGuidesAndBoundedPreviews",e =>
        {
            var settings=new ImageNineSlice{Enabled=true,Left=4,Right=4,Top=4,Bottom=4};e.SetNineSlice(settings);
            e.ResizeCanvas(3,3,false);e.Save();HeadlessHarness.Assert(e.Session.Document.NineSlice.Left+e.Session.Document.NineSlice.Right<3,"Canvas resize left crossing guides.");
            e.Undo();HeadlessHarness.Assert(e.Session.Document.NineSlice.Right==4,"Resize did not undo guide margins.");
            var preview=ImageCanvasTransforms.NineSlicePreview(e.GetPreviewPixels(),16,16,new(8192,8192),settings);
            HeadlessHarness.Assert(preview.Pixels.Length<=1024*1024*4,"Large preview allocated the full output image.");
            var rotation=ImageCanvasTransforms.RotatePreview(e.GetPreviewPixels(),16,16,35,true);
            HeadlessHarness.AssertPixelsEqual(rotation.Pixels,ImageCanvasTransforms.Rotate(e.GetPreviewPixels(),16,16,35,true),"Small rotation preview no longer matches applied pixels");
        });
        foreach (string handle in new[]{"Start","End","Middle"}) Check("Bone"+handle+"Drag",e =>
        {
            e.DrawFilledRectangle(new(3,2,3,11),Color.Red);
            var bone=new ImagePixelBone{Start=new(){X=4,Y=2},End=new(){X=4,Y=12}};
            var rig=new ImagePixelRig{Name="Arm",Width=16,Height=16,BindPixels=e.GetPreviewPixels(),Bones=[bone],Poses=[new(){Bones=[PixelRigRasterizer.Copy(bone)]}]};
            using var dialog=new PixelRigStudioDialog([rig],rig.BindPixels,16,16,"","",_=>{},_=>{},_=>{},(_,_,_)=>Task.CompletedTask);
            UnattendedWindowing.Configure(dialog);UnattendedWindowing.ShowWithoutFocus(dialog);
            Point start=handle=="Start" ? new(4,2) : handle=="End" ? new(4,12) : new(4,7);
            Point end=handle=="Start" ? new(14,12) : handle=="End" ? new(14,2) : new(7,9);
            void Pointer(string name,Point p)=>Call(dialog,name,new ImageCanvasPointerEventArgs(p,MouseButtons.Left,Keys.None,1));
            Pointer("PointerDown",start);Pointer("PointerMove",end);Pointer("PointerUp",end);
            var moved=Field<List<ImagePixelBone>>(dialog,"_pose")[0];
            bool Near(ImageVector2 a,int x,int y) => Math.Abs(a.X-x)<.001&&Math.Abs(a.Y-y)<.001;
            HeadlessHarness.Assert(handle=="Start" ? Near(moved.Start,14,12)&&Near(moved.End,4,12) : handle=="End" ? Near(moved.Start,4,2)&&Near(moved.End,14,2) : Near(moved.Start,7,4)&&Near(moved.End,7,14),"Bone "+handle+" used the wrong pivot or translated an endpoint.");
            HeadlessHarness.Assert(!new PixelRigRasterizer(rig).Render([moved]).SequenceEqual(rig.BindPixels),"Bone moved without its bound pixels.");
        });
        Check("ExternalReloadKeepsRigsPaletteAndSlices",e =>
        {
            var source=ImageDocument.CreateDefault(16,16);source.Palette=[ImageColor.White];source.PixelRigs=[new(){Width=16,Height=16}];source.NineSlice=new(){Enabled=true,Left=2};
            e.Session.ReloadFrom(source);
            HeadlessHarness.Assert(e.Session.Document.Palette.Count==1&&e.Session.Document.PixelRigs.Count==1&&e.Session.Document.NineSlice.Left==2,"External metadata reload omitted new authoring data.");
        });
    }
    private static T Field<T>(object obj,string name) => (T)obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(obj)!;
    private static object? Call(object obj,string name,params object[] args) => obj.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(obj,args);
}
