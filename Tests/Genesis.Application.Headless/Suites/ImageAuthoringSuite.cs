using System.Drawing;
using System.Text.Json;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Dialogs;
using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Headless.Suites;

internal static class ImageAuthoringSuite
{
    public static void Run(HeadlessContext ctx)
    {
        ImageInteractionSuite.Run(ctx);
        RigPoseControlSuite.Run(ctx);
        ImageLayerFramesSuite.Run(ctx);
        ImageCanvasFollowupSuite.Run(ctx);
        var resources = HeadlessHarness.Require(ctx.Resources,"Resources");
        void Check(string name, Action<ImageEditorControl> test) => HeadlessHarness.RunCase(ctx.Report,"Editor.Image.Authoring."+name,()=>
        {
            string path = resources.CreateResource(resources.AssetsRoot,ResourceKind.Image,"Authoring "+name);
            var session = new ImageDocumentSession(ImageDocument.CreateDefault(4,4),path,ImageDocumentAccess.Editor);
            using var editor = new ImageEditorControl(session,ImageWorkspace.CreateBlank(4,4,Color.FromArgb(128,40,80,120)));
            test(editor);
        });
        Check("FrameDurationTargetsOriginalFrame",editor=>
        {
            editor.AddFrameForTests(true);
            var first = editor.Workspace.Frames[0]; var second = editor.Workspace.Frames[1];
            editor.SetFrameDuration(240);
            editor.Workspace.SelectedFrameIndex = 0; editor.RefreshFromDocument();
            HeadlessHarness.Assert(first.DurationMilliseconds == 100 && second.DurationMilliseconds == 240,"Selecting frames must not edit their duration.");
            editor.Undo();
            HeadlessHarness.Assert(first.DurationMilliseconds == 100 && second.DurationMilliseconds == 100,"Undo must target the originally edited frame.");
            editor.Redo();
            HeadlessHarness.Assert(first.DurationMilliseconds == 100 && second.DurationMilliseconds == 240,"Redo must retain the target frame.");
        });
        Check("FrameRedoRetainsIdentityAndPixels",editor=>
        {
            editor.AddFrameForTests(true); var frame = editor.Workspace.CurrentFrame!;
            editor.DrawStroke(new(1,1),new(1,1),Color.Red);
            byte[] painted = (byte[])frame.Layers[0].Pixels.Clone();
            editor.Undo(); editor.Undo(); editor.Redo(); editor.Redo();
            HeadlessHarness.Assert(ReferenceEquals(editor.Workspace.CurrentFrame,frame),"Frame redo replaced the cel objects used by pixel history.");
            HeadlessHarness.AssertPixelsEqual(frame.Layers[0].Pixels,painted,"Pixel redo after structural redo");
        });
        Check("LayerStructureAcrossFramesAndReopen",editor=>
        {
            editor.AddFrameForTests(true); editor.AddRasterLayer("Glint"); editor.RenameLayer("Highlight");
            editor.SetLayerLocked(true); editor.SetLayerVisible(false); editor.Save();
            var reopened = new ImageDocumentSession(ImageDocumentSerializer.LoadAtomic(editor.Session.DocumentPath!).Document,editor.Session.DocumentPath);
            var workspace = ImageWorkspaceStorage.Load(reopened);
            HeadlessHarness.Assert(workspace.Frames.All(frame=>frame.Layers.Count==2 && frame.Layers[1].Name=="Highlight" && frame.Layers[1].Locked && !frame.Layers[1].Visible),"Layer metadata did not survive every frame on reopen.");
            editor.Undo(); editor.Undo(); editor.Undo(); editor.Undo();
            HeadlessHarness.Assert(editor.Workspace.Frames.All(frame=>frame.Layers.Count==1),"Undo left layers in another frame.");
            editor.Redo(); editor.Redo(); editor.Redo(); editor.Redo();
            HeadlessHarness.Assert(editor.Workspace.Frames.All(frame=>frame.Layers[1].Name=="Highlight"),"Layer redo lost names.");
        });
        Check("DuplicateLayerHasIndependentPixels",editor=>
        {
            var original = editor.Workspace.CurrentLayer!; editor.DuplicateLayer();
            var copy = editor.Workspace.CurrentLayer!;
            HeadlessHarness.Assert(copy.Id != original.Id && !ReferenceEquals(copy.Pixels,original.Pixels),"Duplicate layer must have independent identity and pixel storage.");
            editor.DrawStroke(new(0,0),new(0,0),Color.Red);
            HeadlessHarness.Assert(original.Pixels[0]==40,"Painting the copy altered the original.");
        });
        Check("ImportUndoRedoAndReopen",editor=>
        {
            string png = Path.ChangeExtension(editor.Session.DocumentPath!,"png");
            ImageWorkspaceStorage.WritePng(png,4,4,editor.Workspace.CurrentLayer!.Pixels);
            editor.ImportFrames(png); var imported = editor.Workspace.CurrentFrame!;
            editor.Undo(); HeadlessHarness.Assert(editor.Workspace.Frames.Count==1,"Import undo did not remove imported frames.");
            editor.Redo(); HeadlessHarness.Assert(ReferenceEquals(imported,editor.Workspace.CurrentFrame),"Import redo lost frame identity.");
            editor.Save(); var loaded = new ImageDocumentSession(ImageDocumentSerializer.LoadAtomic(editor.Session.DocumentPath!).Document,editor.Session.DocumentPath);
            HeadlessHarness.Assert(ImageWorkspaceStorage.Load(loaded).Frames.Count==2,"Imported animation did not reopen.");
        });
        Check("ResizeExactPixelsUndoRedo",editor=>
        {
            editor.DrawStroke(new(1,1),new(1,1),Color.Red);
            byte[] before = (byte[])editor.Workspace.CurrentLayer!.Pixels.Clone();
            editor.ResizeCanvas(8,8,true);
            var pixel = RasterOperations.GetPixel(editor.Workspace.CurrentLayer!.Pixels,8,8,2,2);
            HeadlessHarness.Assert(pixel.R==255 && pixel.G==0,"Nearest-neighbour scaling did not preserve the marked pixel.");
            editor.Undo(); HeadlessHarness.Assert(editor.Workspace.Width==4,"Resize undo retained the wrong dimensions.");
            HeadlessHarness.AssertPixelsEqual(editor.Workspace.CurrentLayer.Pixels,before,"Resize undo exact RGBA");
            editor.Redo(); HeadlessHarness.Assert(editor.Workspace.Width==8,"Resize redo failed.");
        });
        Check("SheetMetadataAndTags",editor=>
        {
            editor.AddFrameForTests(true); editor.SetFrameDuration(240); editor.AddFrameForTests(true);
            editor.SetAnimationTag("Attack",0,2,ImagePlaybackDirection.PingPong,true);
            string png = Path.ChangeExtension(editor.Session.DocumentPath!,"sheet.png"); editor.ExportSpriteSheet(png,2);
            var pixels = Genesis.Shared.Assets.ImageAssetDecoder.DecodeToRgba(png,out int width,out int height);
            HeadlessHarness.Assert(width==8 && height==8 && pixels[(6*width+6)*4+3]==0,"Sheet layout or transparent unused cell is wrong.");
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(png,"json")));
            HeadlessHarness.Assert(metadata.RootElement.GetProperty("frames")[1].GetProperty("duration").GetInt32()==240,"Export lost frame timing.");
            HeadlessHarness.Assert(metadata.RootElement.GetProperty("tags")[0].GetProperty("direction").GetString()=="PingPong","Export lost tag direction.");
            editor.Undo(); HeadlessHarness.Assert(editor.Session.Document.Tags.Count==0,"Tag creation undo failed.");
        });
        Check("FailedSavePreservesPreviousPixels",editor=>
        {
            editor.Save();
            byte[] previous = (byte[])editor.Workspace.CurrentLayer!.Pixels.Clone();
            editor.DrawStroke(new(1,1),new(1,1),Color.Red);
            using (var locked = new FileStream(editor.Session.DocumentPath!,FileMode.Open,FileAccess.Read,FileShare.None))
            {
                bool rejected=false;
                try { editor.Save(); } catch(Exception ex) when (ex is IOException or UnauthorizedAccessException) { rejected=true; }
                HeadlessHarness.Assert(rejected && editor.IsDirty,"Failed save must retain unsaved state.");
            }
            var old = new ImageDocumentSession(ImageDocumentSerializer.LoadAtomic(editor.Session.DocumentPath!).Document,editor.Session.DocumentPath);
            HeadlessHarness.AssertPixelsEqual(ImageWorkspaceStorage.Load(old).CurrentLayer!.Pixels,previous,"Failed save corrupted previous pixel files");
            editor.Save(); HeadlessHarness.Assert(!editor.IsDirty,"Retry should complete the save.");
        });
        Check("MergeAndTransformHistory",editor=>
        {
            editor.AddRasterLayer("Mark"); editor.DrawStroke(new(1,2),new(1,2),Color.Red);
            byte[] composite = (byte[])editor.Workspace.CompositeCurrentFrame().Clone();
            HeadlessHarness.Assert(editor.MergeLayerDown(),"Normal colour layers should merge.");
            HeadlessHarness.AssertPixelsEqual(editor.Workspace.CompositeCurrentFrame(),composite,"Merge must preserve appearance");
            editor.Undo(); HeadlessHarness.Assert(editor.Workspace.CurrentFrame!.Layers.Count==2,"Merge undo lost layers.");
            editor.MirrorLayer(true);
            HeadlessHarness.Assert(RasterOperations.GetPixel(editor.Workspace.CurrentLayer!.Pixels,4,4,2,2).R==255,"Mirror did not reflect marked pixel.");
            editor.Undo();
            editor.RotateCanvasClockwise();
            HeadlessHarness.Assert(RasterOperations.GetPixel(editor.Workspace.CurrentLayer!.Pixels,4,4,1,1).R==255,"Rotation did not rotate marked pixel.");
            editor.Undo(); editor.Undo();
            HeadlessHarness.Assert(editor.Workspace.CurrentLayer!.Pixels.All(value=>value==0),"Earlier stroke undo no longer targets the restored layer.");
        });
        Check("CanvasNavigation",editor=>
        {
            using var host = new System.Windows.Forms.Form { ClientSize=new Size(1200,800) };
            host.Controls.Add(editor);
            host.Show(); System.Windows.Forms.Application.DoEvents();
            editor.Canvas.ActualPixels();
            var point = new PointF(1,1);
            Point before = editor.Canvas.ImageToClient(point);
            editor.Canvas.PanByClientDelta(18,12);
            Point after = editor.Canvas.ImageToClient(point);
            HeadlessHarness.Assert(Math.Abs(after.X-before.X-18)<=1 && Math.Abs(after.Y-before.Y-12)<=1,"Dragging must move artwork with the pointer.");
            Point anchor = new(editor.Canvas.Width/3,editor.Canvas.Height/3);
            PointF imageBefore = editor.Canvas.ScreenToImage(anchor);
            editor.Canvas.ZoomAt(anchor,true);
            PointF imageAfter = editor.Canvas.ScreenToImage(anchor);
            HeadlessHarness.Assert(Math.Abs(imageBefore.X-imageAfter.X)<0.01 && Math.Abs(imageBefore.Y-imageAfter.Y)<0.01,"Zoom must retain the pixel under the pointer.");
            editor.ResizeCanvas(2048,2048,false); editor.Canvas.FitToView();
            HeadlessHarness.Assert(editor.Canvas.Zoom<1,"Fit must reduce large images below 100%.");
            host.Controls.Remove(editor);
        });
        Check("PasteSaveAndCancel",editor=>
        {
            using var source = new Bitmap(2,2);
            using(var graphics = Graphics.FromImage(source)) graphics.Clear(Color.Lime);
            editor.TryPasteExternalImage(source);
            editor.Save();
            var loaded = new ImageDocumentSession(ImageDocumentSerializer.LoadAtomic(editor.Session.DocumentPath!).Document,editor.Session.DocumentPath);
            HeadlessHarness.AssertPixel(ImageWorkspaceStorage.Load(loaded).CurrentLayer!.Pixels,4,4,0,0,Color.Lime,"Save must commit floating clipboard pixels");
            editor.TryPasteExternalImage(source);
            HeadlessHarness.Assert(editor.IsDirty,"Pending paste must be dirty even after a save.");
            editor.Undo(); HeadlessHarness.Assert(!editor.IsDirty,"Cancelling a pending paste should restore the saved state.");
        });
        Check("CopyPasteUsesSelectionNotStaleClipboard",editor=>
        {
            // A leftover Win32 screenshot must not override Copy/Cut → Paste of the live marquee.
            using (Bitmap stale = new(64, 64))
            using (Graphics graphics = Graphics.FromImage(stale))
            {
                graphics.Clear(Color.Magenta);
                try { Clipboard.SetImage(stale); }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    HeadlessHarness.Assert(false, "Could not seed a stale system clipboard image for the regression.");
                }
            }

            editor.DrawFilledRectangle(new Rectangle(0, 0, 2, 2), Color.Lime);
            editor.Workspace.Selection.SetRectangle(new Rectangle(0, 0, 2, 2), false, false);
            HeadlessHarness.Assert(editor.TryEdit(Genesis.Application.Core.Editing.EditCommand.Cut), "Cut must accept a live selection.");
            HeadlessHarness.Assert(
                RasterOperations.GetPixel(editor.Workspace.CurrentLayer!.Pixels, 4, 4, 0, 0).A == 0,
                "Cut must clear the selected pixels.");
            HeadlessHarness.Assert(editor.TryEdit(Genesis.Application.Core.Editing.EditCommand.Paste), "Paste must run after Cut.");
            editor.CommitFloatingSelection();

            HeadlessHarness.Assert(
                editor.Workspace.Width == 4 && editor.Workspace.Height == 4,
                $"Paste grew the canvas to {editor.Workspace.Width}×{editor.Workspace.Height} — the stale 64×64 screenshot won.");
            Color atOrigin = RasterOperations.GetPixel(editor.Workspace.CurrentLayer!.Pixels, 4, 4, 0, 0);
            HeadlessHarness.Assert(
                atOrigin.R < 40 && atOrigin.G > 200 && atOrigin.B < 40 && atOrigin.A > 200,
                $"Paste used the stale clipboard screenshot ({atOrigin}) instead of the cut 2×2 selection.");
        });
        Check("LayerRowsMatchPaintOrder",editor=>
        {
            editor.AddRasterLayer("Foreground");
            var layers=editor.Controls.Find("ImageEditorLayerList",true).OfType<System.Windows.Forms.ListBox>().Single();
            HeadlessHarness.Assert(layers.Items[0].ToString()!.Contains("Foreground"),"The frontmost layer must appear at the top of the list.");
            layers.SelectedIndex=layers.Items.Count-1;
            HeadlessHarness.Assert(editor.Workspace.SelectedLayerIndex==0,"Selecting the bottom row must select the background cel.");
            layers.SelectedIndex=0;
            HeadlessHarness.Assert(editor.Workspace.CurrentLayer!.Name=="Foreground","Selecting the top row must target foreground pixels.");
        });
        foreach(var blend in Enum.GetValues<ImageBlendMode>())
            Check("BlendRoundtrip"+blend,editor=>
            {
                editor.Workspace.CurrentLayer!.BlendMode=blend; editor.Workspace.Touch();
                HeadlessHarness.AssertPixelsEqual(editor.Workspace.CompositeCurrentFrame(),editor.Workspace.CurrentLayer.Pixels,"Blend over transparent backdrop must retain source colour");
                editor.Save();
                var loaded=new ImageDocumentSession(ImageDocumentSerializer.LoadAtomic(editor.Session.DocumentPath!).Document,editor.Session.DocumentPath);
                HeadlessHarness.Assert(ImageWorkspaceStorage.Load(loaded).CurrentLayer!.BlendMode==blend,"Blend mode changed when reopening.");
            });
        foreach(var definition in LegacyImageEffectCatalog.All)
            Check("LegacyEffect"+new string(definition.Title.Where(char.IsLetter).ToArray()),editor=>
            {
                var layer=editor.Workspace.CurrentLayer!;
                int marked = (1*4+1)*4; layer.Pixels[marked]=50; layer.Pixels[marked+1]=90; layer.Pixels[marked+2]=130;
                byte[] before=(byte[])layer.Pixels.Clone();
                editor.Workspace.Selection.SetRectangle(new Rectangle(1,1,2,2),false,false);
                editor.ApplyEffect(definition,definition.Parameters.ToDictionary(parameter=>parameter.Name,parameter=>parameter.Default));
                HeadlessHarness.Assert(layer.Pixels[0]==40 && layer.Pixels[3]==128,"Effect escaped the selected area.");
                HeadlessHarness.Assert(editor.Undo(),"Legacy effect did not record undo.");
                HeadlessHarness.AssertPixelsEqual(layer.Pixels,before,"Legacy effect exact undo");
                editor.Redo();
            });
    }
}
