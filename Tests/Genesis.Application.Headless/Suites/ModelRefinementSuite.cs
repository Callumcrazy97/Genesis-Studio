using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Headless.Suites;

internal static class ModelRefinementSuite
{
    public static void Run(HeadlessContext ctx)
    {
        var resources=ctx.Resources!;string project=ctx.Project!.RootPath;
        HeadlessHarness.BeginMajor(ctx.Report,"Editor.Model.Refinement");
        HeadlessHarness.RunCase(ctx.Report,"Editor.Model.Refinement.ImportKeepsIdentityAndRollsBack",()=>
        {
            string path=resources.CreateResource(resources.AssetsRoot,ResourceKind.Model,"Current model");
            string source=AnimatedGlbFixture.Write(Path.Combine(ctx.Workspace,"Replacement source"));
            var files=Directory.GetFiles(resources.AssetsRoot,"*.model.json");
            using var editor=new ModelEditorControl(path,project);editor.AddPart(ModelPrimitiveKind.Cube);editor.Save();
            Assert(editor.ImportExternalModel(source)==path,"Import changed resource path.");
            Assert(files.Order().SequenceEqual(Directory.GetFiles(resources.AssetsRoot,"*.model.json").Order()),"Import created another model resource.");
            Assert(editor.AnimationClipCount>0&&editor.CanonicalMeshCount==2,"Import did not refresh the current editor.");
            string original=File.ReadAllText(path),canonical=File.ReadAllText(StudioModelResourceLoader.CanonicalPath(path));
            string broken=Path.Combine(ctx.Workspace,"broken.glb");File.WriteAllText(broken,"invalid glb");bool rejected=false;
            try{editor.ImportExternalModel(broken);}catch(Exception){rejected=true;}
            Assert(rejected&&File.ReadAllText(path)==original&&File.ReadAllText(StudioModelResourceLoader.CanonicalPath(path))==canonical,"Failed replacement changed the current resource.");
            using var reopened=new ModelViewerControl(path,project);Assert(reopened.PreviewAsset.Meshes.Count==2,"Replacement lost geometry on reopening.");
        });
        HeadlessHarness.RunCase(ctx.Report,"Editor.Model.Refinement.SculptKeepsChunkAndUvSeamsConnected",()=>
        {
            string path=resources.CreateResource(resources.AssetsRoot,ResourceKind.Model,"Shared vertex cube");
            using var editor=new ModelEditorControl(path,project);editor.AddPart(ModelPrimitiveKind.Cube);
            // Cube face normals differ at coincident positions. Duplicate the mesh as another chunk.
            var asset=editor.CaptureAnimationAsset();asset.Meshes.Add(ModelPoseWorkflow.Copy(asset.Meshes[0]));editor.ApplyAnimationWorkspace(asset,"");
            var original=editor.BakedVerticesForTest;editor.SetMode(ModelEditorMode.Sculpt);editor.SetBrushRadius(3);editor.SetBrushStrength(.4f);
            foreach(var kind in new[]{ModelBrushKind.Draw,ModelBrushKind.Carve,ModelBrushKind.Smooth})
            {
                editor.SetBrush(kind);Assert(editor.BrushAt(new Vector3(.5f,.5f,.5f))>0,"Brush did not edit vertices.");
                var edited=editor.BakedVerticesForTest;
                foreach(var group in original.Select((v,i)=>(v.Position,i)).GroupBy(v=>v.Position))
                    Assert(group.Select(v=>edited[v.i].Position).Distinct().Count()==1,"A shared position tore across normals or mesh chunks.");
                Assert(edited.All(v=>float.IsFinite(v.Position.LengthSquared())&&float.IsFinite(v.Normal.LengthSquared())),"Brush wrote invalid geometry.");
                editor.Undo();Assert(original.SequenceEqual(editor.BakedVerticesForTest),"Sculpt undo did not restore every chunk.");
            }
        });
        HeadlessHarness.RunCase(ctx.Report,"Editor.Model.Refinement.DrawFacesSelectAndColour",()=>
        {
            string path=resources.CreateResource(resources.AssetsRoot,ResourceKind.Model,"Drawn geometry");using var editor=new ModelEditorControl(path,project);
            using var host=UnattendedWindowing.NewHost(1440,920);host.Controls.Add(editor);ThemeService.Apply(host);UnattendedWindowing.ShowWithoutFocus(host);System.Windows.Forms.Application.DoEvents();
            editor.SetCameraView("Front");editor.FrameModel();using(var frame=editor.Viewport.CaptureFrame(3)){}
            int w=editor.Viewport.Width,h=editor.Viewport.Height;editor.SelectTool(ModelAuthoringTool.SquareFace);
            editor.PointerDown(new(w/3,h/3),MouseButtons.Left);editor.PointerMove(new(2*w/3,2*h/3),MouseButtons.Left);
            Assert(editor.CanonicalMeshCount==0,"Placement committed before release.");editor.PointerUp(new(2*w/3,2*h/3),MouseButtons.Left);
            Assert(editor.BakedTriangleCount==2,"Square drag must create two triangles.");editor.Undo();Assert(editor.CanonicalMeshCount==0,"Place face cannot be undone.");editor.Redo();
            foreach(var tool in new[]{ModelAuthoringTool.CircleFace,ModelAuthoringTool.TriangleFace,ModelAuthoringTool.Sphere,ModelAuthoringTool.Cylinder,ModelAuthoringTool.Cube})
            {int previous=editor.CanonicalMeshCount;editor.PlaceShape(tool,new Vector3(-2,-1,0),new Vector3(-1,0,0));Assert(editor.CanonicalMeshCount==previous+1,"Placement failed: "+tool);editor.Undo();}
            editor.SetCameraView("Front");editor.FrameModel();using(var frame=editor.Viewport.CaptureFrame(3)){}
            editor.SelectTool(ModelAuthoringTool.Region);editor.SelectRegion(Point.Empty,new Point(editor.Viewport.Width/2,editor.Viewport.Height));
            Assert(editor.SelectedVertexCount==2,"Region did not select the left two vertices.");
            editor.SetMode(ModelEditorMode.Paint);editor.SetBrushRadius(20);editor.SetBrushStrength(1);editor.SetPaintColor(new Vector4(1,0,0,1));var before=editor.BakedVerticesForTest;editor.BrushAt(editor.PreviewAsset.Bounds.Center);var after=editor.BakedVerticesForTest;
            Assert(before.Where((v,i)=>v.Color!=after[i].Color).Count()==2,"Colouring ignored the selection.");
            editor.SelectTool(ModelAuthoringTool.Wand);editor.SelectConnected(new Point(editor.Viewport.Width/2,editor.Viewport.Height/2));Assert(editor.SelectedVertexCount==4,"Wand failed to follow connected faces.");
            editor.SelectRegion(Point.Empty,new Point(editor.Viewport.Width,editor.Viewport.Height),modifiers:Keys.Shift);Assert(editor.SelectedVertexCount==0,"Subtract selection failed.");
            editor.Save();using var reopened=new ModelViewerControl(path,project);Assert(reopened.PreviewAsset.Meshes[0].Vertices.SequenceEqual(after),"Painted face was not saved.");
        });
        HeadlessHarness.RunCase(ctx.Report,"Editor.Model.Refinement.DrawBonesJointsDirectPoseAndCancel",()=>
        {
            string path=resources.CreateResource(resources.AssetsRoot,ResourceKind.Model,"Drawn rig");StudioModelResourceLoader.SaveCanonical(path,ModelPoseWorkflowSuite.Fixture());
            using var editor=new ModelEditorControl(path,project);using var dialog=editor.CreateAnimationStudio();UnattendedWindowing.Configure(dialog);UnattendedWindowing.ShowWithoutFocus(dialog);
            dialog.NewSkeleton();Assert(dialog.Result.Rig.Bones.Count==0,"New rig inserted template bones.");
            dialog.PlaceJoint(Vector3.Zero,.08f);dialog.PlaceJoint(new Vector3(0,1,0),.08f);dialog.DrawBone(Vector3.Zero,new Vector3(0,1,0));dialog.DrawBone(new Vector3(0,1,0),new Vector3(1,1,0));dialog.BindMesh();dialog.GoToPage(1);
            var preview=dialog.Preview;preview.Viewport.Camera.Yaw=MathF.PI;preview.Viewport.Camera.Pitch=0;preview.FrameModelForTest();using(var frame=preview.Viewport.CaptureFrame(4)){}
            var before=preview.CaptureWorkingPose();preview.SelectAnimationNode(dialog.Result.Rig.Bones[2].Name);
            Point end=PointAt(new Vector3(1,1,0));Assert(preview.DirectPointerDown(end),"Bone tip could not be selected.");preview.DirectPointerMove(new Point(end.X-20,end.Y-60));preview.DirectPointerUp(end);
            var posed=preview.CaptureWorkingPose();Assert(!before.SequenceEqual(posed),"Dragging the tip did not rotate.");
            var world=GModelPrimitiveFactory.ComputeWorldTransforms(dialog.Result.Rig.Bones,posed);Assert(Vector3.Distance(world[1].Translation,new Vector3(0,1,0))<.001f,"Tip rotation moved its pivot.");
            preview.Undo();Assert(preview.CaptureWorkingPose().SequenceEqual(before),"Direct pose undo failed.");
            Point middle=PointAt(new Vector3(.5f,1,0));preview.DirectPointerDown(middle);preview.DirectPointerMove(new Point(middle.X+30,middle.Y));Assert(preview.CancelAnimationGesture(),"Escape did not cancel the bone drag.");Assert(preview.CaptureWorkingPose().SequenceEqual(before),"Cancel left a changed pose.");
            var pose=dialog.SaveCurrentPose("Default");Assert(dialog.Result.Rig.Bones.Count(b=>b.JointRadius>0)==2,"Authored joint circles were lost.");
            dialog.ApplyToModel();editor.ApplyAnimationWorkspace(dialog.Result,dialog.SelectedClip);editor.Save();using var reopened=new ModelViewerControl(path,project);Assert(reopened.PreviewAsset.Rig.Bones.Count(b=>b.JointRadius>0)==2,"Joint radius did not persist.");
            Point PointAt(Vector3 p){var q=preview.Viewport.WorldToSurface(p-preview.RiggedAsset!.Pivot.Position);return new((int)(q.X*preview.Viewport.Host.Width/preview.Viewport.SurfaceWidth),(int)(q.Y*preview.Viewport.Host.Height/preview.Viewport.SurfaceHeight));}
        });
        string? fox=Environment.GetEnvironmentVariable("GENESIS_MODEL_REVIEW_SOURCE");
        if(!string.IsNullOrWhiteSpace(fox)&&File.Exists(fox))HeadlessHarness.RunCase(ctx.Report,"Editor.Model.Refinement.ProductionFoxSculptAndColour",()=>
        {
            string path=resources.CreateResource(resources.AssetsRoot,ResourceKind.Model,"Fox tools");using var editor=new ModelEditorControl(path,project);editor.ImportExternalModel(fox);
            using var host=UnattendedWindowing.NewHost(1440,920);host.Controls.Add(editor);ThemeService.Apply(host);UnattendedWindowing.ShowWithoutFocus(host);System.Windows.Forms.Application.DoEvents();editor.SetCameraView("Right");editor.FrameModel();using(var frame=editor.Viewport.CaptureFrame(4)){}
            var original=editor.BakedVerticesForTest;var point=new Point(editor.Viewport.Width/2,editor.Viewport.Height/2);
            if (!editor.TryPickSurface(point,out _))
            {
                bool found=false;
                for(int distance=10;distance<180&&!found;distance+=10)
                foreach(var offset in new[]{new Point(distance,0),new Point(-distance,0),new Point(0,distance),new Point(0,-distance)})
                {var candidate=new Point(editor.Viewport.Width/2+offset.X,editor.Viewport.Height/2+offset.Y);if(editor.TryPickSurface(candidate,out _)){point=candidate;found=true;break;}}
            }
            Assert(editor.TryPickSurface(point,out var hit),"Fox surface is not pickable.");editor.SelectTool(ModelAuthoringTool.Brush);editor.SetBrushRadius(.35f);editor.SetBrushStrength(.4f);
            editor.PointerDown(point,MouseButtons.Left);editor.PointerMove(new(point.X+20,point.Y+10),MouseButtons.Left);editor.PointerUp(new(point.X+20,point.Y+10),MouseButtons.Left);
            var sculpted=editor.BakedVerticesForTest;Assert(original.Where((v,i)=>v.Position!=sculpted[i].Position).Any(),"Fox was not sculpted.");
            foreach(var group in original.Select((v,i)=>(v.Position,i)).GroupBy(v=>v.Position))Assert(group.Select(v=>sculpted[v.i].Position).Distinct().Count()==1,"Fox seams tore.");
            Capture("model-fox-sculpted");editor.Undo();Assert(original.SequenceEqual(editor.BakedVerticesForTest),"Fox stroke undo lost geometry.");editor.SelectTool(ModelAuthoringTool.Colouring);editor.SetPaintColor(new Vector4(.2f,.8f,.9f,1));editor.BrushAt(hit);
            Assert(original.Select(v=>v.Position).SequenceEqual(editor.BakedVerticesForTest.Select(v=>v.Position)),"Colouring changed vertex positions.");Capture("model-fox-colouring");
            void Capture(string name){using(var frame=editor.Viewport.CaptureFrame(5)){}var metrics=VisualCapture.CaptureOpenForm(host,Path.Combine(ctx.Captures,name+".png"),true);ctx.Report.Images.Add(ImageResult.From(name,name+".png",metrics));}
        });
    }
    private static void Assert(bool condition,string message)=>HeadlessHarness.Assert(condition,message);
}
