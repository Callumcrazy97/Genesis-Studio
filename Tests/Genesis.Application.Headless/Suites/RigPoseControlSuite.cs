using System.Drawing;
using System.Numerics;
using System.Reflection;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Dialogs;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Image.Rigging;

namespace Genesis.Application.Headless.Suites;

internal static class RigPoseControlSuite
{
    internal static ImagePixelRig Fixture()
    {
        var root=new ImagePixelJoint{Name="Body anchor",Centre=new(){X=32,Y=54},Radius=7};
        var neck=new ImagePixelJoint{Name="Neck",Centre=new(){X=32,Y=32},Radius=10};
        var body=new ImagePixelBone{Name="Body",Start=PixelRigRasterizer.Copy(root.Centre),End=PixelRigRasterizer.Copy(neck.Centre),StartJointId=root.Id,EndJointId=neck.Id};
        var head=new ImagePixelBone{Name="Head",Start=PixelRigRasterizer.Copy(neck.Centre),End=new(){X=32,Y=10},StartJointId=neck.Id,ParentId=body.Id};
        byte[] pixels=new byte[64*64*4];
        for(int y=6;y<59;y++) for(int x=22;x<42;x++)
            RasterOperations.SetPixel(pixels,64,64,x,y,x<25||x>=39 ? Color.SaddleBrown : Color.BurlyWood);
        var rig=new ImagePixelRig{Name="Joint controls",Width=64,Height=64,BindPixels=pixels,Bones=[body,head],Joints=[root,neck]};
        rig.Poses.Add(new ImagePixelPose{Name="Default",Bones=PixelRigRasterizer.Copy(rig.Bones),Joints=PixelRigRasterizer.Copy(rig.Joints)});
        return rig;
    }
    public static void Run(HeadlessContext ctx)
    {
        void Check(string name,Action test)=>HeadlessHarness.RunCase(ctx.Report,"Editor.Image.RigControls."+name,test);
        Check("JointInfillClosesSeamAndPreservesTransparency",()=>
        {
            var rig=Fixture(); var pose=PixelRigRasterizer.Copy(rig.Bones); var joints=PixelRigRasterizer.Copy(rig.Joints);
            Vector2 pivot=new(32,32);
            PixelRigPoseEditor.Transform(pose,joints,pose[1].Id,Matrix3x2.CreateTranslation(-pivot)*Matrix3x2.CreateRotation(.65f)*Matrix3x2.CreateTranslation(pivot),false,rig.Joints[1].Id);
            rig.FillJointGaps=false; byte[] torn=new PixelRigRasterizer(rig).Render(pose,poseJoints:joints);
            rig.FillJointGaps=true; var renderer=new PixelRigRasterizer(rig); byte[] filled=renderer.Render(pose,poseJoints:joints);
            int added=0;
            for(int y=0;y<64;y++) for(int x=0;x<64;x++)
            {
                int i=(y*64+x)*4;
                if(torn[i+3]!=0) HeadlessHarness.Assert(torn.AsSpan(i,4).SequenceEqual(filled.AsSpan(i,4)),"Infill overwrote posed artwork.");
                if(torn[i+3]==0 && filled[i+3]!=0)
                {
                    added++;
                    HeadlessHarness.Assert(Vector2.Distance(new(x+.5f,y+.5f),pivot)<=10.01f,"Infill escaped the neck joint circle.");
                    HeadlessHarness.Assert(rig.BindPixels[i+3]!=0,"Infill painted a transparent source/background pixel.");
                }
            }
            HeadlessHarness.Assert(added>5,"The torn neck received no source-art infill.");
            HeadlessHarness.AssertPixelsEqual(renderer.Render(rig.Bones),rig.BindPixels,"Rest-pose infill modified source pixels");
            using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
            bool stopped=false; try{renderer.Render(pose,cancelled.Token,joints);}catch(OperationCanceledException){stopped=true;}
            HeadlessHarness.Assert(stopped,"Infill ignored cancellation.");
        });
        Check("StretchKeepsLimbThickness",()=>
        {
            var rig=Fixture(); rig.Bones.RemoveAt(1); rig.Joints.Clear(); rig.Bones[0].StartJointId=rig.Bones[0].EndJointId=null;
            var pose=PixelRigRasterizer.Copy(rig.Bones); pose[0].End.Y=10;
            byte[] image=new PixelRigRasterizer(rig).Render(pose);
            HeadlessHarness.Assert(RasterOperations.GetPixel(image,64,64,15,40).A==0 && RasterOperations.GetPixel(image,64,64,23,40).A>0,
                "Lengthening a bone also widened its artwork.");
        });
        Check("OnlyBoneVersusConnectedChain",()=>
        {
            var rig=Fixture(); Vector2 pivot=new(32,54); var rotation=Matrix3x2.CreateTranslation(-pivot)*Matrix3x2.CreateRotation(.5f)*Matrix3x2.CreateTranslation(pivot);
            var only=PixelRigRasterizer.Copy(rig.Bones); var joints=PixelRigRasterizer.Copy(rig.Joints);
            PixelRigPoseEditor.Transform(only,joints,only[0].Id,rotation,false,rig.Joints[0].Id);
            HeadlessHarness.Assert(only[1].End.X==32 && only[1].End.Y==10,"Only-bone mode transformed the child's far tip.");
            HeadlessHarness.Assert(Vector2.Distance(PixelRigRasterizer.Vector(only[0].End),PixelRigRasterizer.Vector(only[1].Start))<.001f,"Only-bone mode tore a shared anchor.");
            var chain=PixelRigRasterizer.Copy(rig.Bones); var chainJoints=PixelRigRasterizer.Copy(rig.Joints);
            PixelRigPoseEditor.Transform(chain,chainJoints,chain[0].Id,rotation,true,rig.Joints[0].Id);
            HeadlessHarness.Assert(chain[1].End.X>40,"Connected-chain mode did not carry the child.");
            joints[0].Pinned=true; var pinned=PixelRigRasterizer.Vector(joints[0].Centre);
            PixelRigPoseEditor.Transform(only,joints,only[0].Id,Matrix3x2.CreateTranslation(12,4),true);
            HeadlessHarness.Assert(Vector2.Distance(PixelRigRasterizer.Vector(only[0].Start),pinned)<.001f,"A pinned joint moved with the chain.");
        });
        Check("DialogMoveResizeUndoAndPin",()=>
        {
            var rig=Fixture(); ImagePixelRig? savedRig=null;
            using var dialog=new PixelRigStudioDialog([rig],rig.BindPixels,64,64,"","",value=>savedRig=PixelRigRasterizer.Copy(value),_=>{},_=>{},(_,_,_)=>Task.CompletedTask,1);
            T Field<T>(string name)=>(T)typeof(PixelRigStudioDialog).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(dialog)!;
            void Call(string name,params object[] args)=>typeof(PixelRigStudioDialog).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(dialog,args);
            void Pointer(string method,float x,float y)=>Call(method,new ImageCanvasPointerEventArgs(new(x,y),MouseButtons.Left,Keys.None,4));
            Field<ComboBox>("_bonePicker").SelectedIndex=0;
            Pointer("PointerDown",32,43);Pointer("PointerMove",40,43);Pointer("PointerUp",40,43);
            HeadlessHarness.Assert(Math.Abs(Field<List<ImagePixelBone>>("_pose")[0].Start.X-40)<.01,"An attached bone middle did not move in the default mode.");
            HeadlessHarness.Assert(Field<List<ImagePixelBone>>("_pose")[1].End.X==32,"Default mode unexpectedly carried the child tip.");
            Call("UndoPoseEdit"); HeadlessHarness.Assert(Field<List<ImagePixelBone>>("_pose")[0].Start.X==32,"Pose undo failed.");
            Field<ComboBox>("_poseMode").SelectedIndex=3;
            Pointer("PointerDown",32,32);Pointer("PointerMove",32,22);Pointer("PointerUp",32,22);
            HeadlessHarness.Assert(Math.Abs(Field<List<ImagePixelBone>>("_pose")[0].End.Y-22)<.01,"Attached bone resize did not extend its endpoint.");
            HeadlessHarness.Assert(Math.Abs(Field<List<ImagePixelJoint>>("_poseJoints")[1].Centre.Y-22)<.01,"Resize left the snapped joint behind.");
            Call("UndoPoseEdit"); Call("RedoPoseEdit"); Call("UndoPoseEdit");
            Field<CheckBox>("_editJoints").Checked=true;
            Pointer("PointerDown",32,54);Pointer("PointerUp",32,54); Field<CheckBox>("_pinJoint").Checked=true;
            Pointer("PointerDown",32,54);Pointer("PointerMove",40,54);Pointer("PointerUp",40,54);
            HeadlessHarness.Assert(Field<List<ImagePixelJoint>>("_poseJoints")[0].Centre.X==32,"Dragging a pinned joint moved it.");
            Field<NumericUpDown>("_jointRadius").Value=11;
            HeadlessHarness.Assert(Field<List<ImagePixelJoint>>("_poseJoints")[0].Radius==11,"The infill radius did not update.");
            Call("SaveRig"); HeadlessHarness.Assert(savedRig!.Joints[0].Pinned&&savedRig.Joints[0].Radius==11,"Saving the rig lost its joint editing preferences.");
        });
        Check("ControlsStayVisibleAboveViewport",()=>
        {
            foreach(var size in new[]{new Size(1120,780),new Size(900,660)})
            {
                var rig=Fixture(); using var dialog=new PixelRigStudioDialog([rig],rig.BindPixels,64,64,"","",_=>{},_=>{},_=>{},(_,_,_)=>Task.CompletedTask,1) { ClientSize=size };
                UnattendedWindowing.Configure(dialog);UnattendedWindowing.ShowWithoutFocus(dialog);System.Windows.Forms.Application.DoEvents();
                T Field<T>(string name)=>(T)typeof(PixelRigStudioDialog).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(dialog)!;
                var controls=Field<Panel>("_poseControls");var canvas=Field<ImageViewportControl>("_canvas");
                HeadlessHarness.Assert(controls.Height>100 && !controls.Bounds.IntersectsWith(canvas.Bounds),$"Pose controls are hidden by the viewport at {size}: header={controls.Bounds}, canvas={canvas.Bounds}.");
                HeadlessHarness.Assert(controls.Height<215,$"Pose toolbar wastes canvas space at {size}: {controls.Height}px. "+
                    string.Join("; ",controls.Controls.Cast<Control>().SelectMany(c=>c.Controls.Cast<Control>()).Select(c=>$"{c.GetType().Name} {c.Bounds} preferred={c.PreferredSize}")));
                foreach(var name in new[]{"_poseMode","_bonePicker","_fillGaps","_pinJoint"})
                {
                    var control=Field<Control>(name);var bounds=controls.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                    if(!control.Visible) continue;
                    HeadlessHarness.Assert(controls.ClientRectangle.Contains(bounds),$"{name} is clipped at {size}: {bounds} in {controls.ClientRectangle}.");
                }
                dialog.Close();
            }
        });
        Check("JointAnimationInterpolationAndPersistence",()=>
        {
            var rig=Fixture(); var target=PixelRigRasterizer.Copy(rig.Poses[0]); target.Id=Guid.NewGuid().ToString("N");target.Name="Tilt";
            PixelRigPoseEditor.Transform(target.Bones,target.Joints,rig.Bones[0].Id,Matrix3x2.CreateTranslation(-32,-54)*Matrix3x2.CreateRotation(.7f)*Matrix3x2.CreateTranslation(32,54),true,rig.Joints[0].Id);
            rig.Poses.Add(target); rig.FillJointGaps=false; rig.Joints[0].Pinned=true;
            var animation=new ImagePoseAnimation{Keys=[new(){Frame=1,PoseId=rig.Poses[0].Id},new(){Frame=30,PoseId=target.Id}]};rig.Animations.Add(animation);
            var pose=PixelRigRasterizer.Interpolate(rig,animation,15); var joints=PixelRigRasterizer.InterpolateJoints(rig,animation,15);
            HeadlessHarness.Assert(Vector2.Distance(PixelRigRasterizer.Vector(pose[0].End),PixelRigRasterizer.Vector(joints[1].Centre))<.001f,"Interpolated joint drifted off its bone.");
            var resources=HeadlessHarness.Require(ctx.Resources,"Resources");string path=resources.CreateResource(resources.AssetsRoot,ResourceKind.Image,"Joint persistence");
            using var editor=new ImageEditorControl(new ImageDocumentSession(ImageDocument.CreateDefault(64,64),path,ImageDocumentAccess.Editor),ImageWorkspace.CreateBlank(64,64,Color.Transparent));
            rig.LayerId=editor.Workspace.CurrentLayer!.Id.ToString("N");rig.SourceFrameId=editor.Workspace.CurrentFrame!.Id.ToString("N");
            editor.SavePixelRig(rig);editor.Save();var loaded=ImageDocumentSerializer.LoadAtomic(editor.Session.DocumentPath!).Document.PixelRigs.Single();
            HeadlessHarness.Assert(!loaded.FillJointGaps&&loaded.Joints[0].Pinned&&loaded.Poses.Count==2,"Infill, pins or poses did not survive saving.");
        });
        Check("PinnedAndDetachedPosesInterpolateAsAuthored",()=>
        {
            var rig=Fixture(); rig.Poses[0].Joints[0].Pinned=true;
            var target=PixelRigRasterizer.Copy(rig.Poses[0]); target.Id=Guid.NewGuid().ToString("N");
            target.Bones[0].End.X+=15;target.Bones[1].Start.X+=15;target.Joints[1].Centre.X+=15;
            target.Bones[1].ParentId=target.Bones[1].StartJointId=null;
            rig.Poses.Add(target);
            var animation=new ImagePoseAnimation{Keys=[new(){Frame=1,PoseId=rig.Poses[0].Id},new(){Frame=30,PoseId=target.Id}]};
            var middle=PixelRigRasterizer.Interpolate(rig,animation,15);
            HeadlessHarness.Assert(middle[0].Start.X==32&&middle[0].Start.Y==54,"A joint pinned in both poses drifted between keys.");
            var end=PixelRigRasterizer.Interpolate(rig,animation,30);
            HeadlessHarness.Assert(end[1].ParentId==null&&end[1].StartJointId==null,"Animation restored a detached bone's bind-pose connections.");
        });
        Check("ChainRecognizesBonesDrawnTowardTheJoint",()=>
        {
            var rig=Fixture();var head=rig.Bones[1];head.ParentId=null;head.EndJointId=head.StartJointId;head.StartJointId=null;
            (head.Start,head.End)=(head.End,head.Start);
            var affected=PixelRigPoseEditor.AffectedBones(rig.Bones,rig.Bones[0].Id,true,rig.Joints[0].Id);
            HeadlessHarness.Assert(affected.Contains(head.Id),"A reversed bone sharing the moving joint was omitted from the chain.");
            affected=PixelRigPoseEditor.AffectedBones(rig.Bones,head.Id,true,rig.Joints[1].Id);
            HeadlessHarness.Assert(!affected.Contains(rig.Bones[0].Id),"Rotating the head around the neck pulled in the body across the fixed pivot.");
        });
        Check("GeneratedFramesUseJointInfillAndPoseRadius",()=>
        {
            var resources=HeadlessHarness.Require(ctx.Resources,"Resources");string path=resources.CreateResource(resources.AssetsRoot,ResourceKind.Image,"Joint generation");
            using var editor=new ImageEditorControl(new ImageDocumentSession(ImageDocument.CreateDefault(64,64),path,ImageDocumentAccess.Editor),ImageWorkspace.CreateBlank(64,64,Color.Transparent));
            var rig=Fixture(); rig.LayerId=editor.Workspace.CurrentLayer!.Id.ToString("N");rig.SourceFrameId=editor.Workspace.CurrentFrame!.Id.ToString("N");
            var posed=PixelRigRasterizer.Copy(rig.Poses[0]);posed.Id=Guid.NewGuid().ToString("N");posed.Name="Tilt";posed.Joints[1].Radius=14;
            PixelRigPoseEditor.Transform(posed.Bones,posed.Joints,rig.Bones[1].Id,Matrix3x2.CreateTranslation(-32,-32)*Matrix3x2.CreateRotation(.6f)*Matrix3x2.CreateTranslation(32,32),false,rig.Joints[1].Id);
            rig.Poses.Add(posed);var animation=new ImagePoseAnimation{Name="Neck",Keys=[new(){Frame=1,PoseId=rig.Poses[0].Id},new(){Frame=3,PoseId=posed.Id}]};
            rig.Animations.Add(animation);editor.SavePixelRig(rig);
            var expected=new PixelRigRasterizer(rig).Render(PixelRigRasterizer.Interpolate(rig,animation,3),poseJoints:PixelRigRasterizer.InterpolateJoints(rig,animation,3));
            Task task=editor.GeneratePoseAnimationAsync(rig,animation);
            while(!task.IsCompleted){System.Windows.Forms.Application.DoEvents();Thread.Sleep(5);}task.GetAwaiter().GetResult();
            HeadlessHarness.AssertPixelsEqual(editor.Workspace.Frames[^1].Layers[0].Pixels,expected,"Generated cels differ from the joint-aware preview");
            editor.Save();var saved=ImageDocumentSerializer.LoadAtomic(editor.Session.DocumentPath!).Document;
            HeadlessHarness.Assert(saved.Tags.Single().Name=="Neck"&&saved.PixelRigs.Single().Poses[1].Joints[1].Radius==14,"Generation lost its timeline tag or authored joint radius.");
        });
    }
}
