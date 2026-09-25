using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Image.Rigging;
using Genesis.Application.Editors.Image.Dialogs;
using System.Reflection;

namespace Genesis.Application.Headless.Suites;

internal static class ImageInteractionSuite
{
    public static void Run(HeadlessContext ctx)
    {
        var resources = HeadlessHarness.Require(ctx.Resources,"Resources");
        void Check(string name, Action<ImageEditorControl> test) => HeadlessHarness.RunCase(ctx.Report,"Editor.Image.Interaction."+name,() =>
        {
            string path = resources.CreateResource(resources.AssetsRoot,ResourceKind.Image,"Interaction "+name);
            using var editor = new ImageEditorControl(new ImageDocumentSession(ImageDocument.CreateDefault(16,16),path,ImageDocumentAccess.Editor),ImageWorkspace.CreateBlank(16,16,Color.Transparent));
            editor.SetForegroundColor(Color.Red); test(editor);
        });
        static void Down(ImageEditorControl e,int x,int y) => e.SimulateImagePointer(new(x,y),true,false);
        static void Move(ImageEditorControl e,int x,int y) => e.SimulateImagePointer(new(x,y),false,false);
        static void Up(ImageEditorControl e,int x,int y) => e.SimulateImagePointer(new(x,y),false,true);
        static bool Alpha(byte[] p,int x,int y) => p[(y*16+x)*4+3] != 0;
        foreach (var tool in new[] { ImageToolKind.Pencil,ImageToolKind.Brush,ImageToolKind.Eraser,ImageToolKind.Line,ImageToolKind.Rectangle,ImageToolKind.Ellipse,ImageToolKind.Gradient })
            Check(tool+"PreviewEqualsCommit",e =>
            {
                if (tool==ImageToolKind.Eraser) { RasterOperations.Clear(e.Workspace.CurrentLayer!.Pixels,16,16,Color.Red); e.Workspace.Touch(); }
                e.SetActiveTool(tool); var before = e.Workspace.CurrentLayer!.Pixels.ToArray();
                Down(e,2,3); Move(e,9,11); var preview=e.GetPreviewPixels();
                HeadlessHarness.Assert(!preview.SequenceEqual(before),tool+" showed no pixel preview before release.");
                HeadlessHarness.AssertPixelsEqual(e.Workspace.CurrentLayer.Pixels,before,"Preview must not commit early");
                Up(e,9,11); HeadlessHarness.AssertPixelsEqual(e.GetPreviewPixels(),preview,"Preview/commit mismatch for "+tool);
                e.Undo(); HeadlessHarness.AssertPixelsEqual(e.Workspace.CurrentLayer.Pixels,before,"Gesture undo");
            });
        Check("RectangleAndOvalKeepVisibleSelection",e =>
        {
            foreach (var tool in new[] {ImageToolKind.RectSelect,ImageToolKind.EllipseSelect})
            {
                // Each shape starts a fresh selection; dragging inside an existing one now moves it.
                e.Workspace.Selection.Clear();
                e.SetActiveTool(tool); Down(e,2,3); Move(e,10,12); Up(e,10,12);
                HeadlessHarness.Assert(e.Workspace.Selection.HasSelection && e.Canvas.Overlay.SelectionEdges.Count>0,"Released selection has no marching-ants edges.");
                HeadlessHarness.Assert(e.Workspace.Selection.Contains(6,7),"Selection misses interior.");
            }
        });
        Check("WandAndLassoKeepVisibleSelection",e =>
        {
            e.DrawFilledRectangle(new Rectangle(2,2,4,4),Color.Red);
            e.SetActiveTool(ImageToolKind.MagicWand); Down(e,3,3); Up(e,3,3);
            HeadlessHarness.Assert(e.Workspace.Selection.HasSelection && e.Canvas.Overlay.SelectionEdges.Count>0,"Wand has no visible selected region.");
            e.SetActiveTool(ImageToolKind.LassoSelect); Down(e,1,1); Move(e,9,1); Move(e,9,9); Move(e,1,9); Up(e,1,1);
            HeadlessHarness.Assert(e.Workspace.Selection.Contains(5,5) && e.Canvas.Overlay.SelectionEdges.Count>0,"Lasso selection vanished on release.");
        });
        Check("MoveClearsSourceAndUndoesAtomically",e =>
        {
            e.DrawFilledRectangle(new Rectangle(1,1,3,3),Color.Red); var before=e.Workspace.CurrentLayer!.Pixels.ToArray();
            e.Workspace.Selection.SetRectangle(new Rectangle(1,1,3,3),false,false); e.SetActiveTool(ImageToolKind.Move);
            Down(e,1,1); Move(e,8,8); Up(e,8,8);
            HeadlessHarness.Assert(!Alpha(e.GetPreviewPixels(),1,1) && Alpha(e.GetPreviewPixels(),8,8),"Move duplicates the source.");
            e.CommitFloatingSelection(); e.Undo(); HeadlessHarness.AssertPixelsEqual(e.Workspace.CurrentLayer.Pixels,before,"Move must undo in one operation");
            e.Redo(); HeadlessHarness.Assert(!Alpha(e.Workspace.CurrentLayer.Pixels,1,1)&&Alpha(e.Workspace.CurrentLayer.Pixels,8,8),"Move redo");
        });
        Check("CancelMoveRestoresSource",e =>
        {
            e.DrawFilledRectangle(new Rectangle(1,1,3,3),Color.Red); var before=e.Workspace.CurrentLayer!.Pixels.ToArray();
            e.Workspace.Selection.SetRectangle(new Rectangle(1,1,3,3),false,false); e.SetActiveTool(ImageToolKind.Move);
            Down(e,1,1); Move(e,8,8); Up(e,8,8); e.CancelFloatingSelection();
            HeadlessHarness.AssertPixelsEqual(e.Workspace.CurrentLayer.Pixels,before,"Cancelled move lost pixels");
        });
        Check("BezierUsesUserControlHandles",e =>
        {
            e.SetActiveTool(ImageToolKind.Bezier); Down(e,2,8); Up(e,2,8); Down(e,13,8); Up(e,13,8); Down(e,2,1); Up(e,2,1);
            Move(e,13,1); var preview=e.GetPreviewPixels(); Down(e,13,1); Up(e,13,1);
            HeadlessHarness.AssertPixelsEqual(e.GetPreviewPixels(),preview,"Bezier handle preview differs from commit");
            HeadlessHarness.Assert(Enumerable.Range(0,7).Any(y=>Enumerable.Range(2,12).Any(x=>Alpha(preview,x,y))),"Bezier control handles produced only a baseline.");
        });
        Check("PolygonClosesAndUndoes",e =>
        {
            e.SetActiveTool(ImageToolKind.Polygon); Down(e,2,2); Up(e,2,2); Down(e,13,2); Up(e,13,2); Down(e,13,13); Up(e,13,13); Down(e,2,2); Up(e,2,2);
            HeadlessHarness.Assert(Alpha(e.GetPreviewPixels(),7,7),"Polygon did not close its final edge.");
            e.Undo(); HeadlessHarness.Assert(e.GetPreviewPixels().All(p=>p==0),"Polygon should be one undo command.");
        });
        Check("AnchoredScalePreviewAndUndo",e =>
        {
            e.DrawFilledRectangle(new Rectangle(7,7,2,2),Color.Red); var original=e.Workspace.CurrentLayer!.Pixels.ToArray();
            var preview=e.ScaleArtworkPreview(2,2); e.ScaleArtwork(2,2);
            HeadlessHarness.AssertPixelsEqual(e.GetPreviewPixels(),preview,"Scale preview mismatch");
            HeadlessHarness.Assert(Alpha(preview,6,6)&&Alpha(preview,9,9),"Scale failed to stay centred.");
            e.Undo(); HeadlessHarness.AssertPixelsEqual(e.GetPreviewPixels(),original,"Scale undo");
        });
        Check("ViewerHasSourceRowsAndTaggedThumbnails",e =>
        {
            e.AddFrameForTests(true); e.AddFrameForTests(true); e.SetAnimationTag("Wave",1,2,ImagePlaybackDirection.Forward,true);
            using var viewer=new ImageViewerControl(e.Session,e.Workspace);
            var source=(ListBox)Find(viewer,"ViewerSourceFrames"); var timeline=(ListBox)Find(viewer,"ViewerTimelineFrames");
            HeadlessHarness.Assert(source.Items.Count==3 && timeline.Items.Cast<int>().SequenceEqual(new[]{1,2}),"Viewer source/timeline rows are empty or unfiltered.");
            e.Session.Document.Tags.Clear(); viewer.RefreshFromDocument();
            HeadlessHarness.Assert(timeline.Items.Count==0,"No animations must use None and an empty animation strip.");
        });
        Check("TimelineUsesTagRange",e =>
        {
            e.AddFrameForTests(true); e.AddFrameForTests(true); e.SetAnimationTag("Wave",1,2,ImagePlaybackDirection.Forward,true);
            ((ComboBox)Find(e,"TimelineClipPicker")).SelectedIndex=1;
            HeadlessHarness.Assert(e.VisibleTimelineFrames.SequenceEqual(new[]{1,2}),"Editor timeline ignores selected tag.");
        });
        Check("ViewerRetainsAnimationAcrossRefreshAndReorder",e =>
        {
            e.AddFrameForTests(true); e.AddFrameForTests(true);
            e.SetAnimationTag("First",0,0,ImagePlaybackDirection.Forward,true);
            e.SetAnimationTag("Wave",1,2,ImagePlaybackDirection.Forward,true);
            using var viewer = new ImageViewerControl(e.Session,e.Workspace);
            var clip = Descendants(viewer).OfType<ComboBox>().Single(c => c.Items.Cast<object>().Any(item => item.ToString() == "Wave"));
            clip.SelectedIndex = 2;
            var wave = e.Session.Document.Tags[1]; wave.Name = "Wave renamed";
            e.Session.Document.Tags.Reverse(); viewer.RefreshFromDocument();
            var timeline = (ListBox)Find(viewer,"ViewerTimelineFrames");
            HeadlessHarness.Assert(clip.Text == "Wave renamed" && timeline.Items.Cast<int>().SequenceEqual(new[]{1,2}),"Viewer refresh lost the selected animation identity.");
            clip.SelectedIndex = 0; viewer.RefreshFromDocument();
            HeadlessHarness.Assert(clip.Text == "None" && timeline.Items.Count == 0,"Viewer refresh overrode explicit None.");
            clip.SelectedIndex = 1; e.Session.Document.Tags.Remove(wave); viewer.RefreshFromDocument();
            HeadlessHarness.Assert(clip.Text == "None","Removing the selected animation silently selected another tag.");
        });
        Check("ViewerTimelineFitsThumbnailsAtDesktopAndNarrowWidths",e =>
        {
            e.SetAnimationTag("Visible",0,0,ImagePlaybackDirection.Forward,true);
            foreach (var size in new[]{new Size(1360,840),new Size(760,620)})
            {
                using var viewer = new ImageViewerControl(e.Session,e.Workspace) { Dock = DockStyle.Fill };
                using var host = new Form { ClientSize = size }; host.Controls.Add(viewer);
                UnattendedWindowing.Configure(host); UnattendedWindowing.ShowWithoutFocus(host);
                System.Windows.Forms.Application.DoEvents();
                var strip = (ListBox)Find(viewer,"ViewerTimelineFrames");
                HeadlessHarness.Assert(strip.ClientSize.Height >= strip.ItemHeight + SystemInformation.HorizontalScrollBarHeight,"Viewer timeline clips thumbnails or duration labels at " + size);
                host.Close();
            }
        });
        Check("MenuCommandsAreUniqueAndToolGroupsExist",e =>
        {
            var menu=Descendants(e).OfType<MenuStrip>().Single();
            var paths=new List<string>(); void Walk(ToolStripItemCollection items,string prefix) { foreach (ToolStripItem item in items) if(item is ToolStripMenuItem m) { string path=prefix+m.Text; if(m.DropDownItems.Count>0) Walk(m.DropDownItems,path+" / "); else paths.Add(path); } }
            Walk(menu.Items,"");
            var leaves=paths.Select(p=>p.Split(" / ").Last().TrimEnd('…')).ToArray();
            HeadlessHarness.Assert(leaves.Distinct().Count()==leaves.Length,"Top menus repeat commands.");
            var topMenus=menu.Items.Cast<ToolStripItem>().Select(item=>item.Text).ToArray();
            HeadlessHarness.Assert(!topMenus.Contains("Rigging")
                && new[]{"Animation / Rigging…","Animation / Posing…","Animation / Animation…"}.All(paths.Contains),
                "Rigging, Posing and Animation must share the Animation top menu.");
            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(e.Session.DocumentPath!)!,"image-menu-inventory.txt"),paths);
            var groups=Descendants(e).OfType<CollapsibleSection>().Select(s=>s.HeaderText).ToArray();
            HeadlessHarness.Assert(new[]{"Brushes","Shapes","Tools","Selections"}.All(groups.Contains)&&!groups.Contains("Rigging")&&!groups.Contains("Animation"),"Tool groups or removed rig panels are wrong.");
        });
        Check("ThemeDoesNotChangePaintColour",e =>
        {
            e.SetForegroundColor(Color.FromArgb(255,235,30,70));
            Genesis.Application.Studio.Theme.ThemeService.Apply(e);
            Down(e,3,4); Up(e,3,4);
            var pixel=RasterOperations.GetPixel(e.Workspace.CurrentLayer!.Pixels,16,16,3,4);
            HeadlessHarness.Assert(pixel.R==235&&pixel.G==30&&pixel.B==70,"Applying the UI theme changed the paint colour.");
        });
        Check("PaletteAlphaInterchangeUndoAndReopen",e =>
        {
            var original=e.PaletteColours.ToArray();Color[] colours=[Color.FromArgb(0,12,45,99),Color.FromArgb(128,235,30,70),Color.White];
            e.SetPalette(colours);e.Save();var loaded=new ImageDocumentSession(ImageDocumentSerializer.LoadAtomic(e.Session.DocumentPath!).Document,e.Session.DocumentPath);
            using var reopened=new ImageEditorControl(loaded,ImageWorkspaceStorage.Load(loaded));
            HeadlessHarness.Assert(reopened.PaletteColours.Select(c=>c.ToArgb()).SequenceEqual(colours.Select(c=>c.ToArgb())),"Palette RGBA did not reopen losslessly.");
            string hex=ImagePaletteStorage.Write(colours);HeadlessHarness.Assert(ImagePaletteStorage.Read(hex).Select(c=>c.ToArgb()).SequenceEqual(colours.Select(c=>c.ToArgb())),"Hex palette dropped alpha.");
            e.Undo();HeadlessHarness.Assert(e.PaletteColours.Select(c=>c.ToArgb()).SequenceEqual(original.Select(c=>c.ToArgb())),"Palette undo failed.");
            e.Redo();HeadlessHarness.Assert(e.PaletteColours.Count==3,"Palette redo failed.");
            HeadlessHarness.Assert(ImagePaletteStorage.Read("JASC-PAL\n0100\n2\n255 0 0\n0 255 0").Count==2,"JASC palette import failed.");
            var gpl=ImagePaletteStorage.Write([Color.Red,Color.Blue],true);HeadlessHarness.Assert(ImagePaletteStorage.Read(gpl).Count==2,"GPL round trip failed.");
        });
        Check("ParentedPoseKeepsJointsConnected",e =>
        {
            var rig=Rig(e); var root=rig.Bones[0]; var child=new ImagePixelBone{Name="Hand",ParentId=root.Id,Start=PixelRigRasterizer.Copy(root.End),End=new(){X=9,Y=8}};
            rig.Bones.Add(child);rig.Poses[0].Bones.Add(PixelRigRasterizer.Copy(child));
            var rotated=PixelRigRasterizer.Copy(rig.Bones);rotated[0].End=new(){X=9,Y=3};rotated[1].Start=new(){X=9,Y=3};rotated[1].End=new(){X=9,Y=-2};
            rig.Poses.Add(new ImagePixelPose{Name="Rotated",Bones=rotated});
            var animation=new ImagePoseAnimation{Keys=[new(){Frame=1,PoseId=rig.Poses[0].Id},new(){Frame=31,PoseId=rig.Poses[1].Id}]};
            var sample=PixelRigRasterizer.Interpolate(rig,animation,16);
            HeadlessHarness.Assert(System.Numerics.Vector2.Distance(PixelRigRasterizer.Vector(sample[0].End),PixelRigRasterizer.Vector(sample[1].Start))<.001f,"Interpolated child detached from its parent joint.");
        });
        Check("PaletteExtractionPreservesAlphaFrequencyAndCancellation",e =>
        {
            byte[] pixels = [100,20,30,128, 100,20,30,128, 255,255,255,255, 45,87,93,0];
            var palette = ImagePaletteStorage.Extract([pixels],3);
            HeadlessHarness.Assert(palette.Count == 3 && palette[0].ToArgb() == Color.FromArgb(128,100,20,30).ToArgb(),"Extraction did not prioritize frequent exact RGBA colours.");
            HeadlessHarness.Assert(palette.Any(c => c.ToArgb() == 0),"Transparent pixels retained irrelevant hidden RGB.");
            e.SetPalette(palette); e.Undo(); e.Redo();
            HeadlessHarness.Assert(e.PaletteColours.Select(c => c.ToArgb()).SequenceEqual(palette.Select(c => c.ToArgb())),"Extracted palette history changed RGBA.");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { ImagePaletteStorage.Extract([pixels],256,cancellation.Token); throw new InvalidOperationException("Extraction ignored cancellation."); }
            catch (OperationCanceledException) { HeadlessHarness.Assert(cancellation.IsCancellationRequested,"Unexpected extraction cancellation."); }
        });
        Check("RigApplyRespectsSelectionUndoAndLockedLayer",e =>
        {
            e.DrawFilledRectangle(new Rectangle(0,0,16,16),Color.Red);
            var before = e.Workspace.CurrentLayer!.Pixels.ToArray();
            var pose = new byte[before.Length]; RasterOperations.Clear(pose,16,16,Color.Blue);
            e.Workspace.Selection.SetRectangle(new Rectangle(2,2,4,4),false,false); e.ApplyPixelRigPose(pose);
            HeadlessHarness.Assert(RasterOperations.GetPixel(e.Workspace.CurrentLayer.Pixels,16,16,3,3).B == 255 && RasterOperations.GetPixel(e.Workspace.CurrentLayer.Pixels,16,16,9,9).R == 255,"Applying a rig pose changed pixels outside the selection.");
            e.Undo(); HeadlessHarness.AssertPixelsEqual(e.Workspace.CurrentLayer.Pixels,before,"Rig pose undo lost original pixels");
            e.Workspace.CurrentLayer.Locked = true;
            bool rejected = false;
            try { e.ApplyPixelRigPose(pose); } catch (InvalidOperationException error) { rejected = error.Message.Contains("unlocked"); }
            HeadlessHarness.Assert(rejected,"Applying to a locked layer silently claimed success.");
            HeadlessHarness.AssertPixelsEqual(e.Workspace.CurrentLayer.Pixels,before,"Locked layer was modified");
        });
        Check("RigLibrarySwitchClearsAnimationDraftAndValidatesRows",e =>
        {
            var first = Rig(e); first.Name = "First";
            first.Animations.Add(new ImagePoseAnimation { Name = "First motion", Keys = [new(){Frame=1,PoseId=first.Poses[0].Id}] });
            var second = Rig(e); second.Name = "Second";
            using var dialog = new PixelRigStudioDialog([first,second],first.BindPixels,16,16,first.LayerId,first.SourceFrameId,_=>{},_=>{},_=>{},(_,_,_)=>Task.CompletedTask);
            T Field<T>(string name) => (T)typeof(PixelRigStudioDialog).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(dialog)!;
            void Call(string name) => typeof(PixelRigStudioDialog).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(dialog,null);
            Field<ListBox>("_animations").SelectedIndex = 0;
            HeadlessHarness.Assert(Field<DataGridView>("_keys").Rows.Count == 1,"Saved animation did not load.");
            Field<ListBox>("_library").SelectedIndex = 1;
            HeadlessHarness.Assert(Field<DataGridView>("_keys").Rows.Count == 0 && Field<TextBox>("_animationName").Text == "Animation","Switching rigs retained another rig's animation draft.");
            Call("NewAnimation"); Field<DataGridView>("_keys").Rows[0].Cells[0].Value = "invalid"; Call("AddKey");
            HeadlessHarness.Assert(Field<DataGridView>("_keys").Rows.Count == 1 && Field<Label>("_status").Text.Contains("Correct"),"Invalid frame input crashed or added a row.");
        });
        Check("SavedRigRejectsForeignPoseBones",e =>
        {
            var rig = Rig(e); rig.Poses[0].Bones[0].Id = Guid.NewGuid().ToString("N"); e.SavePixelRig(rig);
            bool rejected = false;
            try { e.Save(); } catch (InvalidDataException) { rejected = true; }
            HeadlessHarness.Assert(rejected,"Saved rig accepted a pose for a bone that does not exist.");
        });
        Check("RigWizardBindsPosesAndHandlesEscape",e =>
        {
            e.DrawFilledRectangle(new Rectangle(3,3,3,9),Color.Red);ImagePixelRig? saved=null;
            using var dialog=new PixelRigStudioDialog([],e.Workspace.CurrentLayer!.Pixels,16,16,e.Workspace.CurrentLayer.Id.ToString("N"),e.Workspace.CurrentFrame!.Id.ToString("N"),r=>saved=r,_=>{},_=>{},(_,_,_)=>Task.CompletedTask);
            UnattendedWindowing.Configure(dialog);UnattendedWindowing.ShowWithoutFocus(dialog);
            object? Call(string name,params object[] args)=>typeof(PixelRigStudioDialog).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(dialog,args);
            T Field<T>(string name)=>(T)typeof(PixelRigStudioDialog).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(dialog)!;
            void Pointer(string name,int x,int y)=>Call(name,new ImageCanvasPointerEventArgs(new(x,y),MouseButtons.Left,Keys.None,1));
            Pointer("PointerDown",4,3);Pointer("PointerMove",4,12);Pointer("PointerUp",4,12);
            Call("ProcessCmdKey",new Message(),Keys.Escape);
            HeadlessHarness.Assert(!Field<bool>("_createBones")&&!dialog.IsDisposed,"Escape must stop drawing bones before closing the wizard.");
            Call("Bind");
            var deadline=DateTime.UtcNow.AddSeconds(5);
            while(saved==null&&DateTime.UtcNow<deadline){System.Windows.Forms.Application.DoEvents();Thread.Sleep(5);}
            HeadlessHarness.Assert(saved?.Poses.Count==1&&saved.BindPixels.Length>0,"Rig button did not bind pixels and save a Default pose.");
            Pointer("PointerDown",4,12);Pointer("PointerMove",12,3);Pointer("PointerUp",12,3);
            Field<TextBox>("_poseName").Text="Raised";Call("SavePose");
            HeadlessHarness.Assert(saved!.Poses.Any(p=>p.Name=="Raised"),"Wizard did not save a named pose.");
            var before=PixelRigRasterizer.Copy(Field<List<ImagePixelBone>>("_pose"));
            Pointer("PointerDown",12,3);Pointer("PointerMove",4,12);Call("ProcessCmdKey",new Message(),Keys.Escape);
            HeadlessHarness.Assert(!dialog.IsDisposed&&!Field<bool>("_dragging")&&Field<List<ImagePixelBone>>("_pose")[0].End.X==before[0].End.X,"Escape during dragging must restore the pose and keep the wizard open.");
            Call("ProcessCmdKey",new Message(),Keys.Escape);HeadlessHarness.Assert(dialog.IsDisposed,"Idle Escape did not close the wizard.");
        });
        Check("RigCancelWorkButtonDiscardsAndCloses",e =>
        {
            int saves = 0;
            using var dialog = new PixelRigStudioDialog([],e.Workspace.CurrentLayer!.Pixels,16,16,
                e.Workspace.CurrentLayer.Id.ToString("N"),e.Workspace.CurrentFrame!.Id.ToString("N"),
                _=>saves++,_=>{},_=>{},(_,_,_)=>Task.CompletedTask);
            UnattendedWindowing.Configure(dialog); UnattendedWindowing.ShowWithoutFocus(dialog);
            var name=(TextBox)typeof(PixelRigStudioDialog).GetField("_name",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(dialog)!;
            var cancel=(Button)typeof(PixelRigStudioDialog).GetField("_cancelWork",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(dialog)!;
            name.Text="Discard me"; cancel.PerformClick(); System.Windows.Forms.Application.DoEvents();
            HeadlessHarness.Assert(dialog.IsDisposed&&saves==0,"Cancel Work did not close the rig window without saving draft changes.");
        });
        Check("RigJointsCreateSnapConstrainAndPersist",e =>
        {
            e.DrawFilledRectangle(new Rectangle(4,4,20,24),Color.Red); ImagePixelRig? saved=null;
            using var dialog = new PixelRigStudioDialog([],e.Workspace.CurrentLayer!.Pixels,16,16,
                e.Workspace.CurrentLayer.Id.ToString("N"),e.Workspace.CurrentFrame!.Id.ToString("N"),
                rig=>saved=PixelRigRasterizer.Copy(rig),_=>{},_=>{},(_,_,_)=>Task.CompletedTask);
            object? Call(string name,params object[] args)=>typeof(PixelRigStudioDialog).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(dialog,args);
            T Field<T>(string name)=>(T)typeof(PixelRigStudioDialog).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(dialog)!;
            void Pointer(string method,float x,float y,Keys modifiers=Keys.None)=>Call(method,new ImageCanvasPointerEventArgs(new(x,y),MouseButtons.Left,modifiers,4));
            typeof(PixelRigStudioDialog).GetField("_createBones",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(dialog,false);
            typeof(PixelRigStudioDialog).GetField("_createJoints",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(dialog,true);
            Pointer("PointerDown",12,8);Pointer("PointerMove",14,8);Pointer("PointerUp",14,8);
            HeadlessHarness.Assert(Field<List<ImagePixelJoint>>("_poseJoints") is [{ Radius: >= 1.9 }],"Dragging outward did not create a persistent joint circumference.");
            typeof(PixelRigStudioDialog).GetField("_createBones",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(dialog,true);
            typeof(PixelRigStudioDialog).GetField("_createJoints",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(dialog,false);
            Pointer("PointerDown",3,8);Pointer("PointerMove",6,8);Pointer("PointerUp",6,8);
            Call("ProcessCmdKey",new Message(),Keys.Escape);
            Pointer("PointerDown",6,8,Keys.Shift);Pointer("PointerMove",12,8,Keys.Shift);Pointer("PointerUp",12,8,Keys.Shift);
            var attached=Field<List<ImagePixelBone>>("_pose").Single(); var joint=Field<List<ImagePixelJoint>>("_poseJoints").Single();
            HeadlessHarness.Assert(attached.EndJointId==joint.Id&&attached.End.X==joint.Centre.X&&attached.End.Y==joint.Centre.Y,
                "Shift-dragging a bone tip did not align and attach it to the joint.");
            double fixedX=attached.End.X,fixedY=attached.End.Y; PointF middle=new((float)((attached.Start.X+attached.End.X)/2),(float)((attached.Start.Y+attached.End.Y)/2));
            Field<ComboBox>("_poseMode").SelectedIndex=2; // Explicit Rotate; middle now moves in the default mode.
            Pointer("PointerDown",middle.X,middle.Y);Pointer("PointerMove",middle.X,middle.Y-4);Pointer("PointerUp",middle.X,middle.Y-4);
            attached=Field<List<ImagePixelBone>>("_pose").Single();
            HeadlessHarness.Assert(Math.Abs(attached.End.X-fixedX)<.01&&Math.Abs(attached.End.Y-fixedY)<.01&&Math.Abs(attached.Start.Y-8)>.1,
                "Dragging an attached bone centre moved it away instead of rotating around its joint.");
            Call("SaveRig");
            HeadlessHarness.Assert(saved?.Joints.Count==1&&saved.Bones[0].EndJointId==saved.Joints[0].Id,
                "Saving a rig lost its joint or bone attachment.");
            e.SavePixelRig(saved!); e.Save();
            var reopened=ImageDocumentSerializer.LoadAtomic(e.Session.DocumentPath!).Document.PixelRigs.Single();
            HeadlessHarness.Assert(reopened.Joints.Count==1&&reopened.Bones[0].EndJointId==reopened.Joints[0].Id,
                "Reopening the image lost its saved joint attachment.");
        });
        Check("PoseManagementRefreshesDeletedSourceTransparency",e =>
        {
            e.DrawFilledRectangle(new Rectangle(3,3,2,5),Color.Red);
            byte[] staleBind = new byte[16*16*4];
            RasterOperations.Clear(staleBind,16,16,Color.White);
            for (int y=3;y<8;y++) for (int x=3;x<5;x++)
                RasterOperations.SetPixel(staleBind,16,16,x,y,Color.Blue);
            var bone = new ImagePixelBone { Name="Body",Start=new(){X=4,Y=3},End=new(){X=4,Y=8} };
            var rig = new ImagePixelRig { Name="Figure",Width=16,Height=16,
                LayerId=e.Workspace.CurrentLayer!.Id.ToString("N"),SourceFrameId=e.Workspace.CurrentFrame!.Id.ToString("N"),
                BindPixels=staleBind,Bones=[bone],Poses=[new(){Name="Default",Bones=[PixelRigRasterizer.Copy(bone)]}] };
            ImagePixelRig? saved = null;
            using var dialog = new PixelRigStudioDialog([rig],e.Workspace.CurrentLayer.Pixels,16,16,rig.LayerId,rig.SourceFrameId,
                value=>saved=PixelRigRasterizer.Copy(value),_=>{},_=>{},(_,_,_)=>Task.CompletedTask,1);
            var repaired=(ImagePixelRig)typeof(PixelRigStudioDialog).GetField("_rig",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(dialog)!;
            HeadlessHarness.Assert(RasterOperations.GetPixel(repaired.BindPixels,16,16,0,0).A==0,
                "Pose Management retained an opaque background deleted from the source cel.");
            HeadlessHarness.Assert(RasterOperations.GetPixel(repaired.BindPixels,16,16,3,3).B==255,
                "Refreshing source transparency replaced retained rig artwork.");
            HeadlessHarness.AssertPixelsEqual(new PixelRigRasterizer(repaired).Render(repaired.Bones),repaired.BindPixels,
                "The repaired rest pose differs from the repaired bind image.");
            UnattendedWindowing.Configure(dialog);UnattendedWindowing.ShowWithoutFocus(dialog);dialog.Close();
            System.Windows.Forms.Application.DoEvents();
            HeadlessHarness.Assert(saved!=null&&RasterOperations.GetPixel(saved.BindPixels,16,16,0,0).A==0,
                "Closing Pose Management did not save the repaired transparency.");
            var otherFrame=PixelRigRasterizer.Copy(rig);otherFrame.SourceFrameId=Guid.NewGuid().ToString("N");
            HeadlessHarness.Assert(!PixelRigRasterizer.RefreshSourceTransparency(otherFrame,e.Workspace.CurrentLayer.Pixels,16,16,rig.LayerId,rig.SourceFrameId)
                && RasterOperations.GetPixel(otherFrame.BindPixels,16,16,0,0).A==255,
                "Opening another frame incorrectly changed this rig's source pixels.");
        });
        Check("RigBindTransformPoseInterpolationAndPersistence",e =>
        {
            var rig=Rig(e); var raster=new PixelRigRasterizer(rig);
            HeadlessHarness.AssertPixelsEqual(raster.Render(rig.Bones),rig.BindPixels,"Rest pose must preserve exact RGBA");
            var pose=PixelRigRasterizer.Copy(rig.Bones); pose[0].Start.X+=6;pose[0].End.X+=6;
            var moved=raster.Render(pose); HeadlessHarness.Assert(!Alpha(moved,3,4)&&Alpha(moved,9,4),"Rig does not move bound pixels.");
            rig.Poses.Add(new ImagePixelPose{Name="Moved",Bones=pose});
            var anim=new ImagePoseAnimation{Keys=[new(){Frame=1,PoseId=rig.Poses[0].Id},new(){Frame=3,PoseId=rig.Poses[1].Id}]}; rig.Animations.Add(anim);
            var middle=raster.Render(PixelRigRasterizer.Interpolate(rig,anim,2)); HeadlessHarness.Assert(Alpha(middle,6,4),"Pose assignments do not interpolate.");
            e.SavePixelRig(rig); e.Save(); var loaded=ImageDocumentSerializer.LoadAtomic(e.Session.DocumentPath!).Document;
            HeadlessHarness.Assert(loaded.PixelRigs.Single().Poses.Count==2&&loaded.PixelRigs[0].Animations[0].Keys.Count==2,"Rigs/poses/animations do not persist.");
        });
        Check("GenerateButtonPublishesTimelineBeforePreviewReady",e =>
        {
            var rig = Rig(e);
            rig.Animations.Add(new ImagePoseAnimation { Name="Button motion", Keys=[new(){Frame=1,PoseId=rig.Poses[0].Id},new(){Frame=3,PoseId=rig.Poses[0].Id}] });
            e.SavePixelRig(rig);
            using var dialog = new PixelRigStudioDialog([rig],e.Workspace.CurrentLayer!.Pixels,16,16,rig.LayerId,rig.SourceFrameId,
                e.SavePixelRig,e.DeletePixelRig,e.ApplyPixelRigPose,e.GeneratePoseAnimationAsync,2);
            UnattendedWindowing.Configure(dialog); UnattendedWindowing.ShowWithoutFocus(dialog);
            ((ListBox)Find(dialog,"RigAnimations")).SelectedIndex = 0;
            typeof(PixelRigStudioDialog).GetField("_rasterizer",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(dialog,null);
            Descendants(dialog).OfType<Button>().Single(button=>button.Text=="Generate frames").PerformClick();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (e.Workspace.Frames.Count == 1 && clock.ElapsedMilliseconds < 10000)
            { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(10); }
            HeadlessHarness.Assert(e.Workspace.Frames.Count==4 && e.VisibleTimelineFrames.Count==3,"Generate button silently failed before its preview renderer was ready.");
            dialog.Close(); e.Save();
            var loaded = ImageDocumentSerializer.LoadAtomic(e.Session.DocumentPath!).Document;
            HeadlessHarness.Assert(loaded.Tags.Single().Name=="Button motion" && loaded.PixelRigs.Single().Animations.Single().GeneratedFrameIds.Count==3,
                "Closing the animation window or saving discarded generated timeline data.");
        });
        Check("TrimRigSaveWatcherAndReopen",e =>
        {
            var rig=Rig(e); rig.Animations.Add(new ImagePoseAnimation{Name="Saved",Keys=[new(){Frame=1,PoseId=rig.Poses[0].Id}]});
            e.SavePixelRig(rig); e.RemoveBlankSpace(); e.Save();
            string root = Directory.GetParent(resources.AssetsRoot)!.FullName;
            // These values reproduce the scanner's PathTooLongException without a large bitmap.
            string embedded = Path.Combine(resources.AssetsRoot,"embedded-rig-regression.json");
            File.WriteAllText(embedded,new Newtonsoft.Json.Linq.JObject {
                ["bindPixels"]=new string('A',40000), ["description"]=new string('B',40000),
                ["image"]=Path.GetRelativePath(root,e.Session.DocumentPath!) }.ToString());
            using var monitor = new Genesis.Shared.Assets.ProjectAssetMonitor(root);
            Exception? reported = null; monitor.Error += (_,error)=>reported=error;
            monitor.Notify(e.Session.DocumentPath!); monitor.FlushPending();
            HeadlessHarness.Assert(reported==null,"Saving a rig broke the asset watcher: "+reported);
            var loaded=ImageDocumentSerializer.LoadAtomic(e.Session.DocumentPath!).Document;
            HeadlessHarness.Assert(loaded.PixelRigs.Single().Width==e.Workspace.Width && loaded.PixelRigs[0].Animations.Count==1,
                "Trim/save/reopen lost rig canvas or animation data.");
            using var reopened = new Genesis.Shared.Assets.ProjectAssetMonitor(root);
            HeadlessHarness.Assert(reopened.Graph.Resources.Contains(e.Session.DocumentPath!),"Reopen omitted the saved image document.");
        });
        Check("RigGenerationUndoRegenerationAndCancel",e =>
        {
            var rig=Rig(e); var anim=new ImagePoseAnimation{Name="Idle",Keys=[new(){Frame=1,PoseId=rig.Poses[0].Id},new(){Frame=3,PoseId=rig.Poses[0].Id}]};rig.Animations.Add(anim);e.SavePixelRig(rig);
            Await(e.GeneratePoseAnimationAsync(rig,anim));
            HeadlessHarness.Assert(e.Workspace.Frames.Count==4&&e.VisibleTimelineFrames.Count==3,"Generated animation missing tagged frames.");
            e.Undo();HeadlessHarness.Assert(e.Workspace.Frames.Count==1&&e.Session.Document.Tags.Count==0,"Animation generation undo left frames/tags.");e.Redo();
            Await(e.GeneratePoseAnimationAsync(rig,anim));HeadlessHarness.Assert(e.Workspace.Frames.Count==4,"Regeneration duplicates frames.");
            int before=e.Workspace.Frames.Count;using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            try { Await(e.GeneratePoseAnimationAsync(rig,anim,cancelled.Token));throw new InvalidOperationException("Expected cancellation"); } catch(OperationCanceledException) { HeadlessHarness.Assert(cancelled.IsCancellationRequested,"Cancellation token was ignored."); }
            HeadlessHarness.Assert(e.Workspace.Frames.Count==before,"Cancelled generation changed the document.");
        });
        Check("RigRegenerationProtectsTagsSpanningGeneratedFrames",e =>
        {
            var rig = Rig(e); var animation = new ImagePoseAnimation { Name="Motion", Keys=[new(){Frame=1,PoseId=rig.Poses[0].Id},new(){Frame=3,PoseId=rig.Poses[0].Id}] };
            rig.Animations.Add(animation); e.SavePixelRig(rig); Await(e.GeneratePoseAnimationAsync(rig,animation));
            var changed = PixelRigRasterizer.Copy(animation); changed.Name = "Renamed"; changed.FramesPerSecond = 24;
            Await(e.GeneratePoseAnimationAsync(rig,changed));
            var saved = e.Session.Document.PixelRigs.Single().Animations.Single();
            HeadlessHarness.Assert(saved.Name == "Renamed" && saved.FramesPerSecond == 24,"Regeneration saved obsolete animation settings.");
            e.AddFrameForTests(true); e.SetAnimationTag("Whole range",0,4,ImagePlaybackDirection.Forward,true);
            var before = e.Workspace.Frames.Select(f=>f.Id).ToArray(); bool rejected = false;
            try { Await(e.GeneratePoseAnimationAsync(rig,changed)); } catch (InvalidOperationException error) { rejected = error.Message.Contains("another animation tag"); }
            HeadlessHarness.Assert(rejected && e.Workspace.Frames.Select(f=>f.Id).SequenceEqual(before),"Regeneration removed frames from the middle of another tag.");
        });
    }
    private static ImagePixelRig Rig(ImageEditorControl e)
    {
        e.DrawFilledRectangle(new Rectangle(3,3,2,5),Color.Red);
        var bone=new ImagePixelBone{Name="Arm",Start=new(){X=4,Y=3},End=new(){X=4,Y=8}};
        return new ImagePixelRig{Width=16,Height=16,LayerId=e.Workspace.CurrentLayer!.Id.ToString("N"),SourceFrameId=e.Workspace.CurrentFrame!.Id.ToString("N"),BindPixels=e.Workspace.CurrentLayer.Pixels.ToArray(),Bones=[bone],Poses=[new(){Name="Default",Bones=[PixelRigRasterizer.Copy(bone)]}]};
    }
    private static void Await(Task task) { while(!task.IsCompleted) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(5); } task.GetAwaiter().GetResult(); }
    private static Control Find(Control root,string name)=>Descendants(root).First(c=>c.Name==name);
    private static IEnumerable<Control> Descendants(Control root) { foreach(Control child in root.Controls) { yield return child; foreach(var nested in Descendants(child))yield return nested; } }
}
