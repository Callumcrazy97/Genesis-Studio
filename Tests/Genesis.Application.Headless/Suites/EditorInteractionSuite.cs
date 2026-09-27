using System.Drawing;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Runtime.ECS;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Imaging;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;
using EcsWorld = Genesis.Runtime.ECS.World;
using EditorRasterizer = Genesis.Application.Editors.Image.Rigging.PixelRigRasterizer;
using EditorPose = Genesis.Application.Editors.Image.Rigging.PixelRigPoseEditor;

namespace Genesis.Application.Headless.Suites;

/// <summary>Exercises production drawers, room event routes and the real shared CPU rig core.
/// The recording render controller verifies texture ownership/submission, not native GPU output.</summary>
internal static class EditorInteractionSuite
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run(HeadlessContext ctx)
    {
        void Check(string name, Action test) => HeadlessHarness.RunCase(ctx.Report, "Editor.Interaction." + name, test);
        Check("Inspector.ScalarChangesRetainControls", () =>
        {
            using ResourceInspectorPropertySurface surface = new();
            ResourceItem item = Item(ctx, "live.room.json", ResourceKind.Room);
            double value = 0; int commits = 0;
            IReadOnlyList<ResourceInspectorLiveValue> Values() => [new("Tile", "Tile.Offset", "Offset", value)];
            surface.EditRouter = request => { value = Convert.ToDouble(request.Value); commits++; return true; };
            surface.InspectLive(item, Values());
            NumericUpDown number = All(surface).OfType<NumericUpDown>().Single();
            int builds = surface.LayoutBuildCount;
            for (int i = 1; i <= 120; i++) { number.Value = i; surface.InspectLive(item, Values()); }
            Assert(value == 120 && commits == 120, "A scalar input was lost or routed recursively.");
            Assert(builds == surface.LayoutBuildCount && ReferenceEquals(number, All(surface).OfType<NumericUpDown>().Single()), "Every wheel step rebuilt the Inspector.");
        });
        Check("Inspector.ExternalVectorRefreshIsSilentAndNextEditUsesNewAxes", () =>
        {
            using ResourceInspectorPropertySurface surface = new();
            ResourceItem item = Item(ctx,"vector.room.json",ResourceKind.Room);
            float x=1,y=2,z=3; List<string> commits=[];
            IReadOnlyList<ResourceInspectorLiveValue> Values() => [new("Transform","Position.X","Position X",x),new("Transform","Position.Y","Position Y",y),new("Transform","Position.Z","Position Z",z)];
            surface.EditRouter = request =>
            { commits.Add(request.PropertyPath); float v=Convert.ToSingle(request.Value); if(request.PropertyPath.EndsWith(".X",StringComparison.Ordinal)) x=v; else if(request.PropertyPath.EndsWith(".Y",StringComparison.Ordinal)) y=v; else z=v; return true; };
            surface.InspectLive(item,Values()); x=7;y=8;z=9;surface.InspectLive(item,Values());
            Assert(commits.Count==0,"A value refresh was treated as a user edit.");
            NumericUpDown[] axes=All(surface).OfType<NumericUpDown>().ToArray();axes[0].Value=10;
            Assert(x==10&&y==8&&z==9&&commits.SequenceEqual(["Position.X"]),"Changing X wrote stale Y/Z values or extra undo entries.");
        });
        Check("Inspector.SchemaChangesStillRebuild",()=>
        {
            using ResourceInspectorPropertySurface surface=new();var item=Item(ctx,"schema.room.json",ResourceKind.Room);
            surface.InspectLive(item,[new("One","A","A",1)]);int builds=surface.LayoutBuildCount;
            surface.InspectLive(item,[new("One","A","A",1),new("Two","B","B",true)]);
            Assert(surface.LayoutBuildCount==builds+1&&surface.EditablePropertyPaths.Contains("B"),"Changed Inspector schemas were hidden by the value cache.");
        });
        Check("Inspector.JsonScalarWritesReuseDrawer",()=>
        {
            using ResourceInspectorPropertySurface surface=new();var item=Item(ctx,"scalar.note.json",ResourceKind.Note);
            Directory.CreateDirectory(Path.GetDirectoryName(item.FullPath)!);File.WriteAllText(item.FullPath,"{\"name\":\"Scalar\",\"probe\":1}");
            surface.Inspect(item);NumericUpDown number=All(surface).OfType<NumericUpDown>().Single();int builds=surface.LayoutBuildCount;
            number.Value=7;surface.Inspect(item);
            Assert(builds==surface.LayoutBuildCount&&ReferenceEquals(number,All(surface).OfType<NumericUpDown>().Single()),"Own JSON writes recreated the scalar drawer.");
            using JsonDocument saved=JsonDocument.Parse(File.ReadAllText(item.FullPath));Assert(saved.RootElement.GetProperty("probe").GetInt32()==7,"The retained drawer failed to persist its edit.");
        });
        Check("Room.TabAndExactLayerCapabilities",()=>WithRoom(ctx,"contexts",editor=>
        {
            var room=editor.Room;RoomNode first=room.Nodes.Single(n=>n.Name=="First"),second=room.Nodes.Single(n=>n.Name=="Second");
            Assert(editor.CanEditNodeInActiveContext(first)&&!editor.CanInspectNodeInActiveContext(second),"Objects on an inactive object layer are editable.");
            editor.Select(first);editor.Navigation.SetSection(RoomNavSection.Backgrounds);
            Assert(!editor.CanInspectNodeInActiveContext(first)&&editor.SelectedNode is null,"Changing tabs retained an object Inspector/selection.");
            RoomNode background=room.Nodes.Single(n=>n.Name=="Background 1");Assert(editor.CanEditNodeInActiveContext(background),"Selected background slot is not editable.");
            editor.Navigation.SetSection(RoomNavSection.Tilesets);var tiles=room.Nodes.Where(n=>n.Kind==RoomNodeKind.TileLayer).ToArray();
            editor.Navigation.TilesetsPanel.SelectLayer(tiles[1]);
            Assert(editor.CanEditNodeInActiveContext(tiles[1])&&!editor.CanInspectNodeInActiveContext(tiles[0])&&!editor.CanInspectNodeInActiveContext(background),"Tile editing fell back to another layer or resource type.");
            editor.Navigation.SetSection(RoomNavSection.Views);Assert(!room.Nodes.Any(editor.CanInspectNodeInActiveContext),"Views tab exposes editable room resources.");
        }));
        Check("Room.WrongTabCannotPlaceDeleteOrApplyStaleInspector",()=>WithRoom(ctx,"stale",editor=>
        {
            RoomNode node=editor.Room.Nodes.Single(n=>n.Name=="First");editor.Select(node);editor.Navigation.SetSection(RoomNavSection.Tilesets);
            int count=editor.Room.Nodes.Count;editor.Select(node);editor.DeleteSelected();
            Assert(editor.Room.Nodes.Count==count&&!editor.TryApplyInspectorValue("Selection.Position.X",42f),"Wrong-tab or stale Inspector edit mutated an object.");
            Assert(!editor.SetNodeName(node,"Wrong tab"),"A hierarchy rename bypassed the resource tab.");
        }));
        Check("Room.ActiveTileAndGridSurviveValueRefresh",()=>WithRoom(ctx,"tilevalues",editor=>
        {
            RoomNode tile=editor.Room.Nodes.Last(n=>n.Kind==RoomNodeKind.TileLayer);editor.Navigation.SetSection(RoomNavSection.Tilesets);editor.Navigation.TilesetsPanel.SelectLayer(tile);
            Call(editor,"SelectTileCell",tile,tile.TileLayer!.Cells[0]);editor.FlushPendingRoomUiRefresh();int trees=editor.RoomStructureRefreshCount;
            int width=tile.TileLayer.CellWidth,height=tile.TileLayer.CellHeight,margin=tile.TileLayer.Margin;
            for(int i=1;i<=120;i++) Assert(editor.TryApplyInspectorValue("Selection.TileCell.OffsetX",(float)i),"Tile scalar was rejected.");
            editor.FlushPendingRoomUiRefresh();
            Assert(ReferenceEquals(editor.Navigation.TilesetsPanel.ActiveTileLayer,tile)&&ReferenceEquals(editor.SelectedTileCell,tile.TileLayer.Cells[0]),"Tile/layer selection was reset by a scalar edit.");
            Assert(editor.RoomStructureRefreshCount==trees&&tile.TileLayer.CellWidth==width&&tile.TileLayer.CellHeight==height&&tile.TileLayer.Margin==margin,"A value refresh rebuilt lists or rewrote authored tile dimensions.");
            editor.Undo();Assert(tile.TileLayer.Cells[0].OffsetX==119,"Tile numeric undo did not restore the previous detent.");editor.Redo();editor.Save();
            RoomAsset reopened=RoomAssetLoader.Parse(editor.ResourcePath);Assert(reopened.Nodes.Single(n=>n.Id==tile.Id).TileLayer!.Cells[0].OffsetX==120,"Tile edit failed save/reopen.");
        }));
        Check("Room.TileDragCoalescesAndIsOneUndo",()=>WithRoom(ctx,"tiledrag",editor=>
        {
            RoomNode tile=editor.Room.Nodes.First(n=>n.Kind==RoomNodeKind.TileLayer);editor.Navigation.SetSection(RoomNavSection.Tilesets);editor.Navigation.TilesetsPanel.SelectLayer(tile);
            RoomTileCell cell=tile.TileLayer!.Cells[0];Call(editor,"SelectTileCell",tile,cell);editor.FlushPendingRoomUiRefresh();int trees=editor.RoomStructureRefreshCount,values=editor.RoomInspectorValueRefreshCount;
            Type drag=typeof(RoomEditorControl).GetNestedType("TileDragKind",BindingFlags.NonPublic)!;
            Point origin=(Point)Call(editor,"ClientFromWorld2D",new Vector2(16,16))!;
            Call(editor,"BeginTileDrag",Enum.Parse(drag,"Move"),origin,-1);
            for(int i=1;i<=240;i++) Call(editor,"UpdateTileDrag",new Point(origin.X+i,origin.Y),Keys.Shift);
            Assert(editor.RoomStructureRefreshCount==trees&&editor.RoomInspectorValueRefreshCount==values,"Pointer events synchronously refreshed navigation/Inspector.");
            Assert(cell.X!=0||cell.OffsetX!=0,"Dragging did not update the actual document immediately.");
            Call(editor,"CommitTileDrag");editor.FlushPendingRoomUiRefresh();editor.Undo();
            Assert(cell.X==0&&cell.Y==0&&cell.OffsetX==0&&cell.OffsetY==0,"One undo did not restore the whole tile drag.");
        }));
        Check("Room.TabSwitchCancelsActiveTileGesture",()=>WithRoom(ctx,"cancel",editor=>
        {
            RoomNode tile=editor.Room.Nodes.First(n=>n.Kind==RoomNodeKind.TileLayer);editor.Navigation.SetSection(RoomNavSection.Tilesets);editor.Navigation.TilesetsPanel.SelectLayer(tile);
            RoomTileCell cell=tile.TileLayer!.Cells[0];Call(editor,"SelectTileCell",tile,cell);
            Type drag=typeof(RoomEditorControl).GetNestedType("TileDragKind",BindingFlags.NonPublic)!;Point origin=(Point)Call(editor,"ClientFromWorld2D",new Vector2(16,16))!;
            Call(editor,"BeginTileDrag",Enum.Parse(drag,"Move"),origin,-1);Call(editor,"UpdateTileDrag",new Point(origin.X+100,origin.Y),Keys.Shift);
            editor.Navigation.SetSection(RoomNavSection.Objects);
            Assert(cell.X==0&&cell.OffsetX==0&&editor.SelectedTileCell is null,"Tab switch kept an uncommitted drag or stale tile capability.");
        }));
        Check("Rig.NeutralPixelsAndInstanceIsolation",()=>
        {
            PixelRigDefinition rig=Rig();PixelRigPlayer first=new(rig),second=new(rig);
            Assert(first.GetPixels().Span.SequenceEqual(rig.BindPixels),"Neutral live rig differs from authored source pixels.");
            first.RotateBone("Head",30,false);byte[] posed=first.GetPixels().ToArray();
            Assert(!posed.AsSpan().SequenceEqual(rig.BindPixels)&&second.GetPixels().Span.SequenceEqual(rig.BindPixels),"Pose did not deform or contaminated a second instance.");
            long count=first.RenderCount;first.GetPixels();first.RotateBone("Head",30,false);first.GetPixels();Assert(first.RenderCount==count,"An unchanged pose rasterized again.");
        });
        Check("Rig.SharedEditorRasterAndConnectedSolverParity",()=>
        {
            ImagePixelRig authored=RigPoseControlSuite.Fixture();var bones=EditorRasterizer.Copy(authored.Bones);var joints=EditorRasterizer.Copy(authored.Joints);
            EditorPose.Transform(bones,joints,bones[1].Id,Matrix3x2.CreateRotation(.5f,new Vector2(32,32)),false,joints[1].Id);
            PixelRigDefinition engine=JsonSerializer.Deserialize<PixelRigDefinition>(JsonSerializer.Serialize(authored))!;
            var engineBones=JsonSerializer.Deserialize<List<PixelRigBone>>(JsonSerializer.Serialize(bones))!;var engineJoints=JsonSerializer.Deserialize<List<PixelRigJoint>>(JsonSerializer.Serialize(joints))!;
            byte[] fromEditor=new EditorRasterizer(authored).Render(bones,poseJoints:joints);
            byte[] fromEngine=new PixelRigRasterizer(engine).Render(engineBones,poseJoints:engineJoints);
            Assert(fromEditor.AsSpan().SequenceEqual(fromEngine),"Editor/runtime deformation or joint-gap infill diverged.");
        });
        Check("Rig.PinnedJointsAndInvalidData",()=>
        {
            PixelRigDefinition rig=Rig();rig.Joints[0].Pinned=true;PixelRigPlayer player=new(rig);
            Assert(!player.MoveJoint(rig.Joints[0].Id,10,10),"Runtime moved an authored pinned joint.");
            rig.Bones[0].ParentId=rig.Bones[1].Id;bool rejected=false;try{_ = new PixelRigPlayer(rig);}catch(ArgumentException){rejected=true;}Assert(rejected,"Parent cycle reached the rasterizer.");
        });
        Check("Rig.AnimationSamplingAndRepeatedPlay",()=>
        {
            PixelRigDefinition rig=AnimatedRig();PixelRigPlayer player=new(rig);Assert(player.Play("Look"),"Authored animation was not found.");
            player.GetPixels();long revision=player.Revision;player.Advance(.001);player.GetPixels();Assert(player.Revision==revision,"Sub-frame playback resampled a pose.");
            player.Advance(.1);int frame=player.Frame;player.Play("Look");Assert(player.Frame==frame&&frame>1,"Repeated Play restarted the animation.");
            player.Stop();player.Advance(.5);Assert(player.Frame==frame,"Stopped rig kept advancing.");player.Seek(999);Assert(player.Frame==10,"Animation seek exceeded its final key.");
        });
        Check("Rig.CachedUploadsAndRelease",()=>
        {
            using EditorInteractionRenderProbe renderer=new();using PixelRigSprite sprite=new(new PixelRigPlayer(Rig()),"memory","",32,64);
            TextureHandle texture=sprite.GetTexture(renderer);for(int i=0;i<100;i++)sprite.GetTexture(renderer);
            Assert(renderer.Created==1&&renderer.Updated==0&&sprite.UploadCount==1,"Unchanged sprite reallocated/uploaded its texture.");
            sprite.Player.RotateBone("Head",30,false);Assert(sprite.GetTexture(renderer).Id==texture.Id&&renderer.Updated==1,"Changed pose did not update its existing texture.");
            PixelRigSprite.InvalidateRenderer(renderer);Assert(renderer.Released==1,"Asset/backend invalidation leaked a texture.");
            sprite.GetTexture(renderer);Assert(renderer.Created==2,"Invalidated texture was not recreated.");sprite.Dispose();Assert(renderer.Released==2&&renderer.Textures.Count==0,"Binding disposal leaked or double-freed a texture.");
        });
        Check("Rig.LiveEntityDrawingAndDestroyCleanup",()=>
        {
            using EcsWorld world=new(1);Entity entity=world.CreateEntity();using EditorInteractionRenderProbe renderer=new();
            var binding=new PixelRigSprite(new PixelRigPlayer(Rig()),"memory","",32,64);world.Set(entity,new PixelRigSpriteComponent{Binding=binding});
            world.Set(entity,new TransformComponent{X=80,Y=100,ScaleX=-2,ScaleY=2,ScaleZ=1});world.Set(entity,new Draw2DComponent{Visible=true});
            ObjectDrawAssetRegistry.Set(entity,new ObjectDrawAssetEntry{Is3D=false,Image="probe",SpriteAlpha=1});
            try
            {
                ObjectDrawPass.EnqueueEntity2D(world,entity,"",renderer,0,0,1,renderer);
                Assert(renderer.Sprites.Count==1&&renderer.Sprites[0].Texture.IsValid&&renderer.Sprites[0].Width==-128,"Bound entity did not submit its deformed/mirrored sprite through ObjectDrawPass.");
                world.DestroyEntity(entity);world.FlushDeferred();Assert(binding.IsDisposed&&renderer.Textures.Count==0,"Destroying the entity leaked its live rig texture.");
            }
            finally{ObjectDrawAssetRegistry.Remove(entity);}
        });
        Check("Rig.PgslBindingControlsErrorsAndClear",()=>
        {
            string directory=Path.Combine(ctx.Workspace,"h10-rig-pgsl");Directory.CreateDirectory(directory);string file=Path.Combine(directory,"Rig.image.json");PixelRigDefinition rig=AnimatedRig();
            File.WriteAllText(file,JsonSerializer.Serialize(new{canvas=new{width=64,height=64},origin=new{space="pixels",x=32,y=64},frames=Array.Empty<object>(),pixelRigs=new[]{rig}}));
            using EcsWorld world=new(1);Entity entity=world.CreateEntity();world.Set(entity,new TransformComponent{ScaleX=1,ScaleY=1,ScaleZ=1});
            IGameContext previous=PgslCommands.ActiveGameContext;string previousPath=PgslCommands.ProjectPath;PgslContext old=PgslCommands.BindContext(new PgslContext{InstanceId=entity.Id,ImageXScale=1,ImageYScale=1});
            try
            {
                PgslCommands.ActiveGameContext=new NullGameContext{World=world,ProjectPath=directory};PgslCommands.ProjectPath=directory;
                Assert(PgslCommands.SpriteRigBind(file)&&PgslCommands.SpriteRigIsBound(),PgslCommands.SpriteRigError());
                Assert(PgslCommands.SpriteRigBoneRotate("Head",20,false)&&PgslCommands.SpriteRigAim("Head",60,0,35,false),"PGSL rig controls did not reach the live player.");
                Assert(PgslCommands.SpriteRigInfill(false)&&!SpriteRigRuntime.Get(world,entity)!.FillJointGaps,"PGSL infill did not change the real rasterizer setting.");
                Assert(PgslCommands.SpriteRigPlay("Look"),"PGSL animation was not started.");ComponentLifecycle.OnUpdate(world,entity,.1f);Assert(PgslCommands.SpriteRigFrame()>1,"Rig did not advance through the actual component update path.");
                Assert(!PgslCommands.SpriteRigBind("missing.image.json")&&PgslCommands.SpriteRigIsBound()&&PgslCommands.SpriteRigError().Length>0,"Bad replacement erased the good rig or hid its error.");
                PgslCommands.SpriteRigClear();Assert(!PgslCommands.SpriteRigIsBound(),"Clear did not resume ordinary sprite rendering.");
            }
            finally {PgslCommands.BindContext(old);PgslCommands.ActiveGameContext=previous;PgslCommands.ProjectPath=previousPath;ObjectDrawAssetRegistry.Remove(entity);}
        });
        Check("Rig.SharedLayerBlendKeepsAlphaAndHiddenArt",()=>
        {
            byte[] canvas=[0,0,255,255];PixelLayerCompositor.Composite(canvas,new byte[]{255,0,0,255},.5f,PixelBlendMode.Normal);
            Assert(canvas[0]>100&&canvas[2]>100&&canvas[3]==255,"Shared layer compositor lost normal blend/alpha.");
            PixelRigDefinition definition=Rig();definition.LayerId="rig-layer";definition.SourceFrameId="frame";PixelRigPlayer player=new(definition);
            using JsonDocument doc=JsonDocument.Parse("{\"layers\":[{\"id\":\"rig-layer\",\"kind\":\"Raster\",\"visible\":false}]}");
            PixelRigLayerStack? stack=PixelRigLayerStack.Load(doc.RootElement,Path.Combine(ctx.Workspace,"hidden.image.json"),player);
            Assert(stack is not null&&stack.GetPixels().ToArray().All(value=>value==0),"Hidden rig layer was drawn at runtime.");
        });
    }

    private static PixelRigDefinition Rig()=>JsonSerializer.Deserialize<PixelRigDefinition>(JsonSerializer.Serialize(RigPoseControlSuite.Fixture()))!;
    private static PixelRigDefinition AnimatedRig()
    {
        PixelRigDefinition rig=Rig();PixelRigPose pose=PixelRigData.Copy(rig.Poses[0]);pose.Id=Guid.NewGuid().ToString("N");pose.Name="Look up";
        PixelRigPoseEditor.Transform(pose.Bones,pose.Joints,pose.Bones[1].Id,Matrix3x2.CreateRotation(.4f,new Vector2(32,32)),false,pose.Joints[1].Id);
        rig.Poses.Add(pose);rig.Animations.Add(new PixelRigAnimation{Name="Look",FramesPerSecond=30,Keys=[new(){Frame=1,PoseId=rig.Poses[0].Id},new(){Frame=10,PoseId=pose.Id}]});return rig;
    }
    private static ResourceItem Item(HeadlessContext ctx,string name,ResourceKind kind)=>new(){Name=name,FullPath=Path.Combine(ctx.Workspace,"H10Inspector",name),RelativePath=name,Kind=kind,IsFolder=false};
    private static IEnumerable<Control> All(Control parent){foreach(Control child in parent.Controls){yield return child;foreach(Control descendant in All(child))yield return descendant;}}
    private static object? Call(RoomEditorControl editor,string name,params object[] args)=>typeof(RoomEditorControl).GetMethod(name,Private)!.Invoke(editor,args);
    private static void Assert(bool condition,string message)=>HeadlessHarness.Assert(condition,message);
    private static void WithRoom(HeadlessContext ctx,string name,Action<RoomEditorControl> action)
    {
        var project=new ProjectService().CreateProject(Path.Combine(ctx.Workspace,"H10Room-"+name),"Interaction regression");var resources=new ResourceService(project);
        string path=resources.CreateResource(resources.AssetsRoot,ResourceKind.Room,"Interaction");RoomAsset room=RoomAsset.Create("Interaction",RoomDimension.TwoD);
        RoomLayer second=new(){Name="Other objects",Order=1};room.Layers.Add(second);
        room.Nodes.Add(new RoomNode{Name="First",Kind=RoomNodeKind.GameObject,LayerId=room.Layers[0].Id,GameObject=new()});
        room.Nodes.Add(new RoomNode{Name="Second",Kind=RoomNodeKind.GameObject,LayerId=second.Id,GameObject=new()});
        room.Nodes.Add(new RoomNode{Name="Background 1",Kind=RoomNodeKind.Background,LayerId=room.Layers[0].Id,Background=new()});
        for(int i=0;i<2;i++)room.Nodes.Add(new RoomNode{Name="Tiles "+i,Kind=RoomNodeKind.TileLayer,LayerId=room.Layers[0].Id,TileLayer=new(){CellWidth=32+i*16,CellHeight=32,Margin=i,Cells=[new(){X=0,Y=0}]}});
        RoomAssetLoader.Save(room,path);using RoomEditorControl editor=new(path,project.RootPath);using Form host=UnattendedWindowing.NewHost(1380,900);
        editor.Dock=DockStyle.Fill;host.Controls.Add(editor);UnattendedWindowing.ShowWithoutFocus(host);GateSuite.Pump(2,20);editor.FlushPendingRoomUiRefresh();action(editor);
    }
}
