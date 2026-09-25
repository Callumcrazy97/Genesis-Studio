using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Runtime.Scene;

namespace Genesis.Application.Headless.Suites;

internal static class RoomNavigationSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Navigation.FreeAndOrbit", () => Cameras(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Navigation.SelectionWheelUndoAndParents", () => Wheel(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Navigation.ConstrainedDrags", () => Drags(ctx));
    }

    private static void WithEditor(HeadlessContext ctx, string name, Action<RoomEditorControl, Form, string> action)
    {
        string directory = Path.Combine(ctx.Workspace, name); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "Navigation.room.json");
        var room = RoomAsset.Create("Navigation", RoomDimension.ThreeD);
        room.Settings.SnapEnabled = false;
        room.Nodes.Add(new RoomNode { Name = "Selected object", Kind = RoomNodeKind.GameObject,
            LayerId = room.Layers[0].Id, GameObject = new RoomGameObjectData(), Transform = new RoomTransform { Y = 1 } });
        room.Nodes.Add(new RoomNode { Name = "Terrain", Kind = RoomNodeKind.Terrain,
            LayerId = room.Layers[0].Id, Terrain = new RoomTerrainData(), Transform = new RoomTransform { X = 12 } });
        RoomAssetLoader.Save(room, path);
        using var editor = new RoomEditorControl(path, directory);
        using var host = UnattendedWindowing.NewHost(1440, 900);
        editor.Dock = DockStyle.Fill; host.Controls.Add(editor);
        editor.Viewport.Host.TimerEnabled = false;
        UnattendedWindowing.ShowWithoutFocus(host);
        editor.Viewport.Camera.Target = new(0, 1, 0); editor.Viewport.Camera.Yaw = .6f;
        editor.Viewport.Camera.Pitch = -.35f; editor.Viewport.Camera.Distance = 16;
        Warm(editor); action(editor, host, path);
    }

    private static void Cameras(HeadlessContext ctx) => WithEditor(ctx, "CameraControls", (editor, host, _) =>
    {
        var viewport = editor.Viewport; var camera = viewport.Camera;
        Check(editor.CameraControlMethod == EditorCameraControlMethod.Free, "Room did not default to Free camera.");
        Vector3 eye = camera.Eye, forward = camera.Forward;
        viewport.BeginNavigationDrag(new(100, 100), MouseButtons.Right, Keys.None);
        viewport.MoveNavigationDrag(new(140, 115));
        Close(camera.Eye, eye, "Free look moved the eye around an orbit pivot.");
        Check(Vector3.Distance(forward, camera.Forward) > .1f, "Free look did not turn.");
        editor.CameraControlMethod = EditorCameraControlMethod.Orbit;
        Close(camera.Eye, eye, "Switching controls changed the view.");
        Vector3 target = camera.Target;
        viewport.BeginNavigationDrag(new(100, 100), MouseButtons.Right, Keys.None);
        viewport.MoveNavigationDrag(new(140, 115));
        Close(camera.Target, target, "Orbit changed its target.");
        Check(Vector3.Distance(camera.Eye, eye) > .1f, "Orbit did not move around the target.");
        eye = camera.Eye; target = camera.Target; forward = camera.Forward;
        viewport.BeginNavigationDrag(new(100, 100), MouseButtons.Middle, Keys.None);
        viewport.MoveNavigationDrag(new(130, 120));
        Close(camera.Eye - eye, camera.Target - target, "Pan changed eye and target differently.");
        Close(camera.Forward, forward, "Middle drag rotated the camera.");
        Check(Vector3.Distance(camera.Eye, eye) > .1f, "Unmodified middle drag did not pan.");
        editor.CameraControlMethod = EditorCameraControlMethod.Free;
        float distance = camera.Distance; eye = camera.Eye; forward = camera.Forward;
        viewport.NavigateWheel(new(100, 100), 120, Keys.None);
        Check(Vector3.Dot(camera.Eye - eye, forward) > 0 && camera.Distance == distance, "Free wheel did not travel forward.");
        editor.CameraControlMethod = EditorCameraControlMethod.Orbit;
        target = camera.Target; viewport.NavigateWheel(new(100, 100), 120, Keys.None);
        Check(camera.Distance < distance, "Orbit wheel did not zoom."); Close(target, camera.Target, "Orbit zoom moved the target.");
        eye = camera.Eye; viewport.NavigationEnabled = false; viewport.NavigateWheel(new(100, 100), 120, Keys.None);
        Close(camera.Eye, eye, "Suspended camera still navigates."); viewport.NavigationEnabled = true;
        editor.EditorToolbar.CameraDropdown.ShowDropDown();
        ToolStripMenuItem controls = editor.EditorToolbar.CameraDropdown.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Name == "RoomCameraControlMethod");
        controls.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Name == "RoomCameraFree").PerformClick();
        Check(editor.CameraControlMethod == EditorCameraControlMethod.Free, "Camera menu did not change the method.");
        editor.EditorToolbar.CameraDropdown.HideDropDown();
        Warm(editor); Editor3DInspectionSuite.Capture(ctx, host, "room-free-camera-controls");
    });

    private static void Wheel(HeadlessContext ctx) => WithEditor(ctx, "SelectionWheel", (editor, _, path) =>
    {
        var selected = editor.Room.Nodes[0]; var terrain = editor.Room.Nodes[1];
        editor.Select(selected); Vector3 before = Position(editor, selected), eye = editor.Viewport.Camera.Eye;
        editor.Viewport.NavigateWheel(Point.Empty, 120, Keys.Control);
        Vector3 closer = Position(editor, selected);
        Check(Vector3.Dot(closer - before, editor.Viewport.Camera.Forward) < 0, "Ctrl-wheel did not bring selection closer.");
        Close(eye, editor.Viewport.Camera.Eye, "Selection wheel also moved the camera.");
        editor.Undo(); Close(before, Position(editor, selected), "Selection wheel undo failed.");
        editor.Redo(); Close(closer, Position(editor, selected), "Selection wheel redo failed.");
        editor.SetNodeParent(selected, terrain);
        terrain.Transform.RotationZ = 25; terrain.Transform.ScaleX = 2;
        editor.Select(selected); before = Position(editor, selected);
        editor.Viewport.NavigateWheel(Point.Empty, 120, Keys.Shift);
        Vector3 after = Position(editor, selected);
        Check(after.Y > before.Y, "Shift-wheel did not move up.");
        Check(MathF.Abs(after.X - before.X) < .001f && MathF.Abs(after.Z - before.Z) < .001f, "Parent transform changed the world movement axis.");
        editor.Select(terrain); before = Position(editor, terrain);
        editor.Viewport.NavigateWheel(Point.Empty, -120, Keys.Shift);
        Check(Position(editor, terrain).Y < before.Y, "Terrain cannot be moved down with Shift-wheel.");
        editor.Save(); var saved = RoomAssetLoader.Parse(path);
        Check(saved.Nodes[1].Transform.Y == terrain.Transform.Y, "Wheel movement did not survive save/reopen.");
        editor.SetNodeLocked(terrain, true); before = Position(editor, terrain); eye = editor.Viewport.Camera.Eye;
        editor.Viewport.NavigateWheel(Point.Empty, 120, Keys.Control);
        editor.Viewport.NavigateWheel(Point.Empty, 120, Keys.Shift);
        Close(before, Position(editor, terrain), "Wheel moved a locked terrain.");
        Close(eye, editor.Viewport.Camera.Eye, "Locked selection wheel unexpectedly navigated the camera.");
    });

    private static void Drags(HeadlessContext ctx) => WithEditor(ctx, "ConstraintDrags", (editor, host, _) =>
    {
        var selected = editor.Room.Nodes[0]; editor.Select(selected); editor.SetSnapToTerrain(false); Warm(editor);
        Vector3 before = Position(editor, selected); Point start = editor.ClientFromWorld3D(before);
        editor.EditorPointerDown(start, MouseButtons.Left, Keys.Control);
        editor.EditorPointerMove(new(start.X + 45, start.Y - 4), MouseButtons.Left, Keys.Control);
        editor.EditorPointerMove(new(start.X + 75, start.Y - 30), MouseButtons.Left, Keys.Control);
        Vector3 delta = Position(editor, selected) - before;
        Check(new[] { delta.X, delta.Y, delta.Z }.Count(value => MathF.Abs(value) > .001f) == 1, "Ctrl-drag did not retain exactly one world axis.");
        editor.EditorPointerUp(new(start.X + 75, start.Y - 30), MouseButtons.Left, Keys.Control);
        editor.Undo(); Close(before, Position(editor, selected), "Ctrl-drag was not one undoable operation.");
        Warm(editor); start = editor.ClientFromWorld3D(before);
        editor.EditorPointerDown(start, MouseButtons.Left, Keys.Shift);
        editor.EditorPointerMove(new(start.X + 30, start.Y - 20), MouseButtons.Left, Keys.Shift);
        Vector3 first = Position(editor, selected) - before;
        editor.EditorPointerMove(new(start.X + 85, start.Y + 40), MouseButtons.Left, Keys.Shift);
        Vector3 second = Position(editor, selected) - before;
        Check(first.Length() > .01f && second.Length() > .01f, "Shift-drag did not move the selection.");
        Check(Vector3.Cross(Vector3.Normalize(first), Vector3.Normalize(second)).Length() < .001f, "Shift-drag changed the initial direction.");
        editor.EditorPointerUp(new(start.X + 85, start.Y + 40), MouseButtons.Left, Keys.Shift);
        editor.Undo(); Close(before, Position(editor, selected), "Shift-drag was not one undoable operation.");
        editor.Redo(); Warm(editor); Editor3DInspectionSuite.Capture(ctx, host, "room-direction-constrained-drag");
        before = Position(editor, selected); start = editor.ClientFromWorld3D(before);
        editor.EditorPointerDown(start, MouseButtons.Left, Keys.Shift);
        editor.EditorPointerMove(new(start.X + 45, start.Y - 25), MouseButtons.Left, Keys.Shift);
        object[] arguments = { new Message(), Keys.Escape };
        bool handled = (bool)typeof(RoomEditorControl).GetMethod("ProcessCmdKey",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(editor, arguments)!;
        Check(handled, "Escape was not handled during movement.");
        Close(before, Position(editor, selected), "Escape left a partially moved object.");
        editor.EditorPointerUp(new(start.X + 45, start.Y - 25), MouseButtons.Left, Keys.Shift);
        Close(before, Position(editor, selected), "Mouse release recommitted a cancelled movement.");
    });

    private static Vector3 Position(RoomEditorControl editor, RoomNode node)
    { RoomTransform transform = editor.GetNodeWorldTransform(node); return new(transform.X, transform.Y, transform.Z); }
    private static void Warm(RoomEditorControl editor) { GateSuite.Pump(2, 15); using var capture = editor.Viewport.CaptureFrame(2); }
    private static void Close(Vector3 a, Vector3 b, string message) => Check(Vector3.Distance(a, b) < .001f, message);
    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
