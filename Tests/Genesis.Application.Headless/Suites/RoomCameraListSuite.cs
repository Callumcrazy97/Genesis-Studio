using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Settings;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Scene;
using Genesis.Shared.Commands;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless.Suites;

internal static class RoomCameraListSuite
{
    public static void Run(HeadlessContext ctx)
    {
        var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "CameraList"), "Camera list");
        ResourceService resources = new(project);
        string Fixture(string name, RoomDimension dimension)
        {
            string cameraPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.GameObject, name + " Camera Object");
            JObject source = JObject.Parse(File.ReadAllText(cameraPath)); source["dimension"] = dimension.ToString();
            ((JArray)source["components"]!).Add(new JObject { ["type"] = dimension == RoomDimension.ThreeD ? "Camera3DComponent" : "Camera2DComponent",
                ["enabled"] = true, ["props"] = new JObject { ["FOV"] = 65, ["Near"] = .2, ["Far"] = 200, ["Zoom"] = 1.5 } });
            File.WriteAllText(cameraPath, source.ToString());
            RoomAsset room = RoomAsset.Create(name, dimension);
            for (int i = 0; i < 2; i++) room.Nodes.Add(new() { Name = i == 0 ? "Game camera" : "Alternate camera", LayerId = room.Layers[0].Id,
                Kind = RoomNodeKind.GameObject, Transform = new() { X = i * 4, Y = 3, Z = 12 },
                GameObject = new() { Prefab = ResourceNames.Name(project.RootPath, cameraPath) } });
            room.ActiveGameCameraId = room.Nodes[0].Id;
            room.Viewports[0].Enabled = true; room.Viewports[0].SourceX = 0; room.Viewports[0].SourceY = 2;
            room.Viewports[0].SourceZ = 12; room.Viewports[0].SourceWidth = 320; room.Viewports[0].SourceHeight = 180;
            string roomPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Room, name); RoomAssetLoader.Save(room, roomPath); return roomPath;
        }
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.CameraList.AllAuthoredPinnedAndLiveViewsPreserveGameCamera", () =>
        {
            using EngineCameraRegistryScope cameras = new();
            string path = Fixture("3D Cameras", RoomDimension.ThreeD);
            using RoomEditorControl editor = new(path, project.RootPath); using Form host = UnattendedWindowing.NewHost(1440, 900);
            host.Controls.Add(editor); ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host); GateSuite.Pump(3, 20);
            RoomNode alternate = editor.Room.Nodes[1]; string game = editor.Room.ActiveGameCameraId; byte[] saved = File.ReadAllBytes(path);
            editor.Viewport.Camera.Target = new(3, 2, 1); editor.Viewport.Camera.Distance = 15; Vector3 freeEye = editor.Viewport.Camera.Eye;
            editor.Viewport.PinSecondaryFromCurrentView(); editor.RefreshSceneViews();
            Check(editor.CameraNodes().Count() == 2 && editor.CameraListKeys.Contains(alternate.Id) && editor.CameraListKeys.Contains("@pinned")
                && editor.CameraListKeys.Contains("@view:7") && editor.CameraListKeys.Distinct().Count() == editor.CameraListKeys.Count, "The list omitted cameras or duplicated IDs.");
            Check(editor.LookThroughCamera(alternate.Id) && editor.SceneViewNode == alternate && editor.GameCameraPreviewState?.Position.X == 4, "The alternate authored camera did not resolve its real pose.");
            editor.SelectListedCamera(); Check(editor.SelectedNode == alternate, "Select camera did not select the authored Object.");
            editor.PreviewListedCamera(); Check(editor.Viewport.SecondaryCamera?.Kind == EditorCameraSlotKind.AuthoredGame, "Authored preview was mislabeled as a pinned editor camera.");
            alternate.Transform.X = 9;
            Check(editor.Viewport.SecondaryViewport!.CameraOverrideFactory!().Eye.X == 9, "The 3D authored inset retained a stale camera pose.");
            alternate.Transform.X = 4;
            editor.ExitGameCameraPreview(); Check(editor.Viewport.Camera.Eye == freeEye && editor.Viewport.NavigationEnabled, "Returning to Scene lost the editor view.");
            Engine.Camera3DCreate(6); Engine.Camera3DSetPos(6, 7, 4, 18); Engine.Camera3DSetYaw(6, 15); Engine.Camera3DSetFrustum(6, 70, 1.6f, .3f, 250);
            Check(editor.LookThroughCamera("@view:6") && editor.Viewport.CameraOverrideFactory!().Eye == new Vector3(7, 4, 18), "A listed live camera did not use the registry pose.");
            Engine.Camera3DSetPos(6, 8, 5, 19);
            Check(editor.Viewport.CameraOverrideFactory!().Eye == new Vector3(8, 5, 19), "Live camera edits did not update look-through.");
            editor.SelectListedCamera(); Check(editor.ActiveViewportIndex == 6, "Selecting a listed View did not open its authored settings.");
            editor.ExitGameCameraPreview(); editor.Viewport.PinSecondaryFromCurrentView(); editor.RefreshSceneViews();
            Check(editor.LookThroughCamera("@pinned") && editor.Viewport.CameraOverrideFactory!().Eye == freeEye, "The pinned view could not be looked through.");
            editor.ExitGameCameraPreview();
            Check(editor.Room.ActiveGameCameraId == game && !editor.IsDirty && File.ReadAllBytes(path).SequenceEqual(saved), "Previewing or selecting cameras changed the saved game camera or Room.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.CameraList.TwoDInsetTracksAuthoredAndViewportEdits", () =>
        {
            string path = Fixture("2D Cameras", RoomDimension.TwoD);
            using RoomEditorControl editor = new(path, project.RootPath); using Form host = UnattendedWindowing.NewHost(1440, 900);
            host.Controls.Add(editor); ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host); GateSuite.Pump(3, 20);
            editor.LookThroughCamera(editor.Room.Nodes[1].Id); editor.PreviewListedCamera();
            using (var frame = editor.Viewport.SecondaryViewport!.CaptureFrame(3)) { }
            Check(editor.Viewport.SecondaryViewport.Camera2DX == 4 && editor.Viewport.SecondaryCamera?.Kind == EditorCameraSlotKind.AuthoredGame
                && !editor.CameraListKeys.Contains("@pinned"), "The 2D authored camera was duplicated as a pinned view.");
            editor.Room.Nodes[1].Transform.X = 75;
            using (var frame = editor.Viewport.SecondaryViewport.CaptureFrame(3)) { }
            Check(editor.Viewport.SecondaryViewport.Camera2DX == 75, "The inset retained a stale authored camera pose.");
            editor.ExitGameCameraPreview(); editor.LookThroughCamera("@view:0"); editor.PreviewListedCamera();
            editor.Room.Viewports[0].SourceX = 40;
            using (var frame = editor.Viewport.SecondaryViewport.CaptureFrame(3)) { }
            Check(editor.Viewport.SecondaryViewport.Camera2DX == 200 && !editor.Viewport.SecondaryViewport.NavigationEnabled, "The 2D View inset did not track live source edits or remain an authored view.");
            editor.ExitGameCameraPreview(); Check(editor.Viewport.NavigationEnabled, "Scene remained locked after a 2D View preview.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.CameraList.UtilityPlaceholderToggleAffectsSceneAndInset", () =>
        {
            string path = Fixture("Utility placeholder toggle", RoomDimension.ThreeD);
            using RoomEditorControl editor = new(path, project.RootPath); using Form host = UnattendedWindowing.NewHost(1440, 900);
            host.Controls.Add(editor); ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host); GateSuite.Pump(3, 20);
            editor.RefreshSceneViews();
            Check(editor.LookThroughCamera(editor.Room.ActiveGameCameraId), "The authored camera could not be selected for the utility-placeholder test.");
            editor.PreviewListedCamera();
            Check(editor.Viewport.SecondaryViewport is not null, "The secondary camera was not created for the utility-placeholder test.");
            ToolStripDropDownItem viewMenu = editor.EditorToolbar.OptionsDropdown.DropDownItems
                .OfType<ToolStripDropDownItem>().Single(item => item.Text == "View");
            ToolStripMenuItem toggle = viewMenu.DropDownItems.OfType<ToolStripMenuItem>()
                .Single(item => item.Name == "RoomViewUtilityPlaceholders");
            Check(toggle.Checked && editor.ShowUtilityPlaceholders, "Utility placeholders did not start visible or the menu was out of sync.");
            toggle.PerformClick();
            Check(!toggle.Checked && !editor.ShowUtilityPlaceholders, "View/Utility placeholder cubes did not hide editor placeholders.");
            using (var scene = editor.Viewport.CaptureFrame(2)) { }
            using (var inset = editor.Viewport.SecondaryViewport!.CaptureFrame(2)) { }
            toggle.PerformClick();
            Check(toggle.Checked && editor.ShowUtilityPlaceholders, "View/Utility placeholder cubes did not restore editor placeholders.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.CameraList.HiddenUtilityObjectsDoNotOccludeGameView", () =>
        {
            using EngineCameraRegistryScope cameras = new();
            string path=Fixture("Hidden utilities",RoomDimension.ThreeD);
            RoomAsset authored=RoomAssetLoader.Parse(path);
            string objectPath=RoomSceneBuilder.ResolvePrefabPath(project.RootPath,authored.Nodes[0].GameObject!.Prefab);
            JObject source=JObject.Parse(File.ReadAllText(objectPath));source["visible"]=false;File.WriteAllText(objectPath,source.ToString());
            using RoomEditorControl editor=new(path,project.RootPath);using Form host=UnattendedWindowing.NewHost(1440,900);
            host.Controls.Add(editor);UnattendedWindowing.ShowWithoutFocus(host);GateSuite.Pump(3,20);
            Engine.Camera3DCreate(6);Engine.Camera3DSetPos(6,0,3.5f,15);Engine.Camera3DSetYaw(6,0);
            Engine.Camera3DSetFrustum(6,60,1.6f,.1f,100);
            Check(editor.LookThroughCamera("@view:6"),"Live game view did not open");
            using var hidden=editor.Viewport.CaptureFrame(3) ?? throw new InvalidOperationException("Missing hidden-object frame");
            editor.Room.Nodes.Clear();editor.FlushPendingRoomUiRefresh();
            using var empty=editor.Viewport.CaptureFrame(3) ?? throw new InvalidOperationException("Missing empty-room frame");
            int changed=0,samples=0;
            for(int y=hidden.Height/3;y<hidden.Height*2/3;y+=4)
                for(int x=hidden.Width/3;x<hidden.Width*2/3;x+=4)
                {samples++;Color a=hidden.GetPixel(x,y),b=empty.GetPixel(x,y);if(Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B)>12)changed++;}
            hidden.Save(Path.Combine(ctx.Captures,"room-camera-hidden-objects.png"));empty.Save(Path.Combine(ctx.Captures,"room-camera-empty-room.png"));
            // The hidden object's thin selection outline is still drawn and is not occlusion. On a 4 px
            // sample grid its share swings between about 1.5% and 2.5% with the viewport's exact height
            // (which rows the horizontal lines land on), so 2% was a coin toss. A filled placeholder
            // covers the whole box: about 22% of these samples.
            Check(changed<samples*.05,$"Invisible utility placeholders changed the game camera image ({changed}/{samples} samples; frames {hidden.Width}x{hidden.Height} and {empty.Width}x{empty.Height})");
        });
        foreach ((int width, float scale) in new[] { (1440, 1f), (1000, 1f), (1440, 2f) })
            HeadlessHarness.RunCase(ctx.Report, $"Editor.Room.CameraList.Layout.{width}.Scale{scale}", () =>
            {
                GenesisSettings settings = new(); settings.Appearance.InterfaceScale = scale;
                try
                {
                    ThemeService.ApplySettings(settings); string path = Fixture($"Layout {width} {scale}", RoomDimension.ThreeD);
                    using RoomEditorControl editor = new(path, project.RootPath); using Form host = UnattendedWindowing.NewHost(width, 900);
                    host.Controls.Add(editor); ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host); GateSuite.Pump(3, 20);
                    editor.Navigation.SetSection(RoomNavSection.Views); editor.RefreshSceneViews();
                    ComboBox cameras = (ComboBox)editor.Controls.Find("RoomSceneCamera", true).Single();
                    Check(cameras.Visible && cameras.Items.Count >= 11 && cameras.Width > 180, "The camera list is absent or too cramped.");
                    foreach (string actionName in new[] { "RoomPreviewListedCamera", "RoomSelectListedCamera" })
                    {
                        Control button = editor.Controls.Find(actionName, true).Single();
                        Check(button.Visible && button.Parent!.ClientRectangle.Contains(button.Bounds), "A camera action does not fit its panel.");
                    }
                    using (var frame = editor.Viewport.CaptureFrame(3)) { }
                    string name = $"room-camera-list-{width}-scale{scale}";
                    var metrics = VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, name + ".png"), true);
                    ctx.Report.Images.Add(ImageResult.From(name, name + ".png", metrics));
                }
                finally { settings.Appearance.InterfaceScale = 1; ThemeService.ApplySettings(settings); }
            });
    }
    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
