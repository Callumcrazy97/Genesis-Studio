using System.Drawing;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Scene;
using Genesis.World.Terrain;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless.Suites;

internal static class RoomSurfaceAlignmentSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.SurfaceAlignment.RandomSettingsUndoAndReopen", () => CheckSettings(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.SurfaceAlignment.GhostDropAndSelectedActions", () => CheckPlacementAndActions(ctx));
    }

    private static void CheckSettings(HeadlessContext ctx)
    {
        string folder = Path.Combine(ctx.Workspace, "RoomYawSettings"); Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "Yaw.room.json");
        RoomAssetLoader.Save(RoomAsset.Create("Yaw", RoomDimension.ThreeD), file);
        using var editor = new RoomEditorControl(file, folder);
        Assert(!editor.RandomYaw && editor.NextPlacementYawOffset == 0, "Legacy placement acquired random rotation by default.");
        editor.SetRandomYaw(true); editor.SetRandomYawDegrees(65); editor.SetPlacementRandomSeed(90210);
        float expected = editor.NextPlacementYawOffset;
        Assert(MathF.Abs(expected) <= 65, "Yaw sample escaped its authored range.");
        editor.SetPlacementRandomSeed(123); editor.Undo();
        Assert(editor.PlacementRandomSeed == 90210 && editor.NextPlacementYawOffset == expected, "Seed undo did not restore the exact next sample.");
        editor.SetRandomYawDegrees(23); editor.Undo();
        Assert(editor.RandomYawDegrees == 65, "Yaw range undo failed.");
        editor.SetRandomYaw(false); editor.Undo();
        Assert(editor.RandomYaw, "Random-yaw undo failed.");
        editor.Room.Settings.PlacementRandomSequence = 7;
        expected = editor.NextPlacementYawOffset;
        editor.SetRoomSettingsFields(30, 800, 600); editor.Undo();
        Assert(editor.RandomYaw && editor.RandomYawDegrees == 65 && editor.PlacementRandomSeed == 90210
            && editor.Room.Settings.PlacementRandomSequence == 7 && editor.NextPlacementYawOffset == expected,
            "Unrelated room-settings undo discarded placement preferences or sequence.");
        editor.Save();
        using var reopened = new RoomEditorControl(file, folder);
        Assert(reopened.RandomYaw && reopened.RandomYawDegrees == 65 && reopened.PlacementRandomSeed == 90210
            && reopened.Room.Settings.PlacementRandomSequence == 7 && reopened.NextPlacementYawOffset == expected,
            "Save/reopen changed the deterministic placement sequence.");
    }

    private static void CheckPlacementAndActions(HeadlessContext ctx)
    {
        var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "RoomSurfaceActions"), "Surface actions");
        var resources = new ResourceService(project);
        string model = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Pillar");
        File.WriteAllText(model, """{"scale":1,"parts":[{"name":"Pillar","primitive":"Cube","position":[0,1,0],"scale":[1,2,1]}]}""");
        string prefab = resources.CreateResource(resources.AssetsRoot, ResourceKind.GameObject, "Pillar object");
        File.WriteAllText(prefab, new JObject { ["name"] = "Pillar object", ["model"] = Relative(model) }.ToString());
        string terrainPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Terrain, "Slope");
        var terrain = new TerrainAsset(17, 17, .5f, -4, -4, -2, 6);
        for (int z = 0; z < 17; z++)
            for (int x = 0; x < 17; x++) terrain.SetHeight(x, z, 2 + (-4 + x * .5f) * .25f + (-4 + z * .5f) * .125f);
        terrain.Save(terrainPath + ".gterrain");
        string roomPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Room, "Surface actions");
        RoomAsset room = RoomAsset.Create("Surface actions", RoomDimension.ThreeD);
        room.Settings.AlignToTerrainNormal = true;
        room.Nodes.Add(new RoomNode { Kind = RoomNodeKind.Terrain, Name = "Slope", LayerId = room.Layers[0].Id,
            Terrain = new RoomTerrainData { Asset = Relative(terrainPath) } });
        RoomAssetLoader.Save(room, roomPath);
        using var editor = new RoomEditorControl(roomPath, project.RootPath);
        using var host = UnattendedWindowing.NewHost(1400, 900);
        editor.Dock = DockStyle.Fill; host.Controls.Add(editor); ThemeService.Apply(host);
        UnattendedWindowing.ShowWithoutFocus(host); GateSuite.Pump(3, 20);
        editor.Viewport.Camera.Target = new Vector3(0, 2, 0); editor.Viewport.Camera.Distance = 16;
        editor.Viewport.Camera.Yaw = .5f; editor.Viewport.Camera.Pitch = -.6f;
        using (editor.Viewport.CaptureFrame(2)) { }
        editor.SetRandomYaw(true); editor.SetRandomYawDegrees(85); editor.SetPlacementRandomSeed(923);
        editor.Placement.PlacementRotation = 30;
        float firstOffset = editor.NextPlacementYawOffset;
        Assert(editor.TryGetTerrainSurface(0, 0, out RoomSurfaceHit surface), "Fixture terrain cannot be sampled.");
        Point point = editor.ClientFromWorld3D(surface.Position);
        editor.BeginPlacement(prefab); editor.EditorPointerMove(point, MouseButtons.None, Keys.None);
        RoomTransform ghost = editor.PlacementGhostTransform3D ?? throw new InvalidOperationException("Randomized ghost is missing.");
        Vector3 ghostForward = Vector3.Transform(Vector3.UnitZ, RoomHierarchyTransforms.Rotation(ghost));
        float heading = MathF.Atan2(ghostForward.X, ghostForward.Z) * 180 / MathF.PI;
        Assert(MathF.Abs(MathF.IEEERemainder(heading - (30 + firstOffset), 360)) < .002f,
            "Arming placement or slope alignment discarded the base yaw plus random offset.");
        editor.EditorPointerMove(new Point(point.X + 10, point.Y), MouseButtons.None, Keys.None);
        editor.EditorPointerMove(point, MouseButtons.None, Keys.None);
        Assert(editor.NextPlacementYawOffset == firstOffset && editor.Room.Settings.PlacementRandomSequence == 0,
            "Pointer movement consumed a random angle.");
        editor.CancelPlacement(); editor.BeginPlacement(prefab); editor.EditorPointerMove(point, MouseButtons.None, Keys.None);
        Assert(MatrixNear(RoomHierarchyTransforms.Matrix(ghost), RoomHierarchyTransforms.Matrix(editor.PlacementGhostTransform3D!)),
            "Cancelling and rearming changed the pending preview angle.");
        Assert(editor.DropObjectAt(prefab, point), "Palette drop did not place the randomized object.");
        RoomNode node = editor.Room.Nodes.Single(item => item.Kind == RoomNodeKind.GameObject);
        Assert(MatrixNear(RoomHierarchyTransforms.Matrix(ghost), editor.GetNodeWorldMatrix(node)),
            "Placed transform did not match the randomized and slope-aligned ghost.");
        Assert(editor.Room.Settings.PlacementRandomSequence == 1 && editor.NextPlacementYawOffset != firstOffset,
            "Committed placement did not advance to a new angle.");
        RoomTransform placed = Clone(node.Transform);
        float secondOffset = editor.NextPlacementYawOffset;
        editor.Undo();
        Assert(editor.Room.Settings.PlacementRandomSequence == 0 && editor.NextPlacementYawOffset == firstOffset,
            "Placement undo consumed or lost the preview sample.");
        editor.Redo();
        Assert(editor.Room.Settings.PlacementRandomSequence == 1 && editor.NextPlacementYawOffset == secondOffset
            && MatrixNear(RoomHierarchyTransforms.Matrix(placed), editor.GetNodeWorldMatrix(node)),
            "Placement redo rerolled the existing object.");

        // Explicit actions must be independent of future-placement toggles and work in parent-local storage.
        RoomNode parent = new() { Kind = RoomNodeKind.GameObject, Name = "Parent", LayerId = editor.Room.Layers[0].Id,
            GameObject = new RoomGameObjectData { Prefab = Relative(prefab) },
            Transform = new RoomTransform { X = 2, Y = 1, Z = -1, RotationY = 35, ScaleX = 1.5f, ScaleY = 1.5f, ScaleZ = 1.5f } };
        editor.Room.Nodes.Add(parent);
        Assert(editor.SetNodeParent(node, parent), "Fixture reparent was rejected.");
        node.Transform.Y += 4;
        node.Transform.RotationX += 13;
        editor.SetAlignToTerrainNormal(true);
        editor.Select(node); editor.SetInspectorVisible(true); GateSuite.Pump(2, 20);
        ShowSurfaceGroups(editor.Inspector);
        Control randomField = Field(editor.Inspector, "Context.Placement.RandomYaw");
        CheckBox randomToggle = randomField as CheckBox ?? Descendants(randomField).OfType<CheckBox>().Single();
        randomToggle.Checked = false;
        Assert(!editor.RandomYaw && editor.NextPlacementYawOffset == 0,
            $"Inspector random-yaw toggle did not update placement behavior: enabled={editor.RandomYaw}, offset={editor.NextPlacementYawOffset}.");
        // Inspector commits defer dependent-control refresh until the active change callback returns.
        GateSuite.Pump(2, 15);
        Assert(!Field(editor.Inspector, "Context.Placement.RandomYawDegrees").Enabled,
            "Inspector yaw range stayed enabled after the queued preference refresh.");
        editor.Undo();
        Assert(editor.RandomYaw && editor.NextPlacementYawOffset == secondOffset,
            "Inspector preference undo lost the pending placement angle.");
        Button snap = Field(editor.Inspector, "Context.Placement.SnapSelected") as Button
            ?? throw new InvalidOperationException("Snap is not a real Inspector command button.");
        RoomTransform before = Clone(node.Transform);
        Quaternion beforeRotation = RoomHierarchyTransforms.Rotation(editor.GetNodeWorldTransform(node));
        snap.PerformClick(); GateSuite.Pump(2, 15);
        RoomTransform snapped = editor.GetNodeWorldTransform(node);
        Assert(MathF.Abs(Quaternion.Dot(beforeRotation, RoomHierarchyTransforms.Rotation(snapped))) > .99999f,
            "Snap-only action inherited the alignment preference and rotated the selection.");
        Assert(snapped.Y < RoomHierarchyTransforms.World(editor.Room, new RoomNode
            { ParentId = parent.Id, Transform = before }).Y - 1, "Snap-only button did not seat the elevated object.");
        editor.Undo(); Assert(MatrixNear(RoomHierarchyTransforms.Matrix(node.Transform), RoomHierarchyTransforms.Matrix(before)),
            "Selected snap undo did not restore the complete local transform.");
        editor.SetAlignToTerrainNormal(false);
        Button align = (Button)Field(editor.Inspector, "Context.Placement.AlignSelected");
        align.PerformClick(); GateSuite.Pump(2, 15);
        RoomTransform aligned = editor.GetNodeWorldTransform(node);
        Assert(editor.TryGetTerrainSurface(aligned.X, aligned.Z, out surface), "Aligned object left terrain.");
        Vector3 up = Vector3.Transform(Vector3.UnitY, RoomHierarchyTransforms.Rotation(aligned));
        Assert(Vector3.Dot(up, surface.Normal) > .99999f,
            "Align-selected button did not align world up independently of the future-placement toggle.");
        Assert(editor.Room.Settings.PlacementRandomSequence == 1, "Selected alignment consumed a random placement angle.");
        Editor3DInspectionSuite.Capture(ctx, host, "room-selected-surface-alignment");
        editor.Undo(); Assert(MatrixNear(RoomHierarchyTransforms.Matrix(node.Transform), RoomHierarchyTransforms.Matrix(before)),
            "Selected alignment undo did not restore parent-local pose."); editor.Redo();
        RoomTransform lockedPose = Clone(node.Transform);
        editor.SetNodeLocked(node, true);
        Assert(!Field(editor.Inspector, "Context.Placement.AlignSelected").Enabled && !editor.AlignSelectionToTerrain()
            && MatrixNear(RoomHierarchyTransforms.Matrix(node.Transform), RoomHierarchyTransforms.Matrix(lockedPose)),
            "Surface alignment changed a locked instance.");
        editor.Save();
        RoomAsset saved = RoomAssetLoader.Parse(roomPath);
        Assert(saved.Settings.PlacementRandomSequence == 1 && saved.Settings.RandomYaw
            && MatrixNear(RoomHierarchyTransforms.Matrix(saved.Nodes.Single(item => item.Id == node.Id).Transform), RoomHierarchyTransforms.Matrix(node.Transform)),
            "Save/reopen lost selected alignment or the random placement sequence.");

        string Relative(string path) => Path.GetRelativePath(project.RootPath, path).Replace('\\', '/');
    }

    private static void ShowSurfaceGroups(RoomInspectorPanel panel)
    {
        foreach (InspectorSection section in Descendants(panel).OfType<InspectorSection>())
            section.Expanded = section.Title is "Surface alignment" or "Future placement";
        GateSuite.Pump(2, 15);
    }

    private static Control Field(RoomInspectorPanel panel, string path) => panel.Controls.Find(
        "RoomInspectorProperty_" + Regex.Replace(path, "[^A-Za-z0-9_]", "_"), true).Single();
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child)) yield return descendant;
        }
    }
    private static RoomTransform Clone(RoomTransform transform) => JObject.FromObject(transform).ToObject<RoomTransform>()!;
    private static bool MatrixNear(Matrix4x4 a, Matrix4x4 b) =>
        Vector3.Distance(Vector3.Transform(Vector3.Zero, a), Vector3.Transform(Vector3.Zero, b)) < .002f
        && Vector3.Distance(Vector3.TransformNormal(Vector3.UnitX, a), Vector3.TransformNormal(Vector3.UnitX, b)) < .002f
        && Vector3.Distance(Vector3.TransformNormal(Vector3.UnitY, a), Vector3.TransformNormal(Vector3.UnitY, b)) < .002f
        && Vector3.Distance(Vector3.TransformNormal(Vector3.UnitZ, a), Vector3.TransformNormal(Vector3.UnitZ, b)) < .002f;
    private static void Assert(bool value, string message) => HeadlessHarness.Assert(value, message);
}
