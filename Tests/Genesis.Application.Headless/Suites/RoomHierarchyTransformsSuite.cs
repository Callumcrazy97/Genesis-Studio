using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Shared.ECS.Components;
using Genesis.World.Terrain;
using Newtonsoft.Json.Linq;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Application.Headless.Suites;

internal static class RoomHierarchyTransformsSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.Room.HierarchyTransforms");
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Hierarchy.WorldPoseReparentUndoAndRuntime", () => WorldAndReparent(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Hierarchy.PointerDragAndPickingUseWorldSpace", () => PointerDrag(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Hierarchy.TerrainParentContactAndRuntimeSample", () => TerrainContact(ctx));
    }

    private static void WorldAndReparent(HeadlessContext ctx)
    {
        Fixture fixture = MakeFixture(ctx, "RoomHierarchy3D", RoomDimension.ThreeD);
        using var editor = new RoomEditorControl(fixture.RoomPath, fixture.Root);
        RoomNode parent = editor.Room.Nodes[0], child = editor.Room.Nodes[1], target = editor.Room.Nodes[2];
        Matrix4x4 expected = RoomHierarchyTransforms.Matrix(child.Transform) * RoomHierarchyTransforms.Matrix(parent.Transform);
        Same(editor.GetNodeWorldMatrix(child), expected, "Rotated/scaled parent did not transform the child's local pose.");
        EcsWorld world = new();
        RoomBuildResult built = new RoomSceneBuilder(fixture.Root).Build(world, editor.Room);
        Transform3DComponent runtime = world.GetRef<Transform3DComponent>(built.EntitiesByNodeId[child.Id]);
        Same(runtime.WorldMatrix, expected, "F5 initial placement differs from the Room viewport.");

        string oldParent = child.ParentId;
        Matrix4x4 oldLocal = RoomHierarchyTransforms.Matrix(child.Transform);
        Assert(editor.SetNodeParent(child, target), "A representable reparent was refused.");
        Same(editor.GetNodeWorldMatrix(child), expected, "Reparent moved the visible child.");
        editor.Undo();
        Assert(child.ParentId == oldParent, "Reparent undo did not restore parent identity.");
        Same(RoomHierarchyTransforms.Matrix(child.Transform), oldLocal, "Reparent undo did not restore local TRS.");
        editor.Redo(); Same(editor.GetNodeWorldMatrix(child), expected, "Reparent redo changed world pose.");
        editor.Undo();
        Assert(!editor.SetNodeParent(parent, child), "A hierarchy cycle was accepted.");
        RoomTransform targetBefore = new() { Position = [.. target.Transform.Position], Rotation = [.. target.Transform.Rotation], Scale = [.. target.Transform.Scale] };
        target.Transform.ScaleX = 4; target.Transform.ScaleY = 1; target.Transform.ScaleZ = .5f;
        target.Transform.RotationY = 37;
        Assert(!editor.SetNodeParent(child, target), "A shear-producing reparent silently approximated the pose.");
        Assert(child.ParentId == oldParent, "Rejected reparent partially changed the parent.");
        Same(RoomHierarchyTransforms.Matrix(child.Transform), oldLocal, "Rejected reparent partially changed local TRS.");
        target.Transform.ScaleX = 0;
        Assert(!editor.SetNodeParent(child, target), "A singular parent was accepted.");
        target.Transform = targetBefore;

        editor.Select(child);
        Vector3 delta = new(2, 3, -4);
        Assert(editor.MoveSelectionBy(delta), "Moving a parented child failed.");
        Near(editor.GetNodeWorldMatrix(child).Translation, expected.Translation + delta, "Child movement used local axes instead of requested world axes.");
        editor.Undo(); Same(editor.GetNodeWorldMatrix(child), expected, "Child move undo changed the world pose.");
        editor.Select(parent); editor.Select(child, additive: true);
        Assert(editor.MoveSelectionBy(delta), "Moving a parent/child selection failed.");
        Near(editor.GetNodeWorldMatrix(child).Translation, expected.Translation + delta, "Selected child received the parent's movement twice.");
        Same(RoomHierarchyTransforms.Matrix(child.Transform), oldLocal, "Moving both parent and child rewrote the child's local pose.");
        editor.Undo(); Same(editor.GetNodeWorldMatrix(child), expected, "Group move undo changed the child.");

        editor.Select(parent); editor.Select(child, additive: true);
        IReadOnlyList<RoomNode> copies = editor.DuplicateSelection();
        RoomNode childCopy = copies.Single(node => node.Name == child.Name + " copy");
        Assert(copies.Any(node => node.Id == childCopy.ParentId), "Duplicate child retained the original parent.");
        Near(editor.GetNodeWorldMatrix(childCopy).Translation, expected.Translation + new Vector3(editor.MetricGridSize, 0, editor.MetricGridSize),
            "Duplicating parent and child applied the placement offset twice.");
        Same(RoomHierarchyTransforms.Matrix(childCopy.Transform), oldLocal, "Duplicate child lost its local pose.");
        editor.Undo(); Assert(copies.All(copy => !editor.Room.Nodes.Contains(copy)), "Duplicate undo retained hierarchy copies.");
        using (ClipboardTestScope clipboard = ClipboardTestScope.Capture())
        {
            editor.Select(parent); editor.Select(child, additive: true);
            Assert(editor.CopySelected(), "Copying the hierarchy failed.");
            IReadOnlyList<RoomNode> pasted = editor.PasteCopied();
            RoomNode pastedChild = pasted.Single(node => node.Name == child.Name + " copy");
            Assert(pasted.Any(node => node.Id == pastedChild.ParentId), "Pasted child retained the original parent.");
            Near(editor.GetNodeWorldMatrix(pastedChild).Translation, expected.Translation + new Vector3(editor.MetricGridSize, 0, editor.MetricGridSize),
                "Clipboard paste applied its offset twice to a parented child.");
            Same(RoomHierarchyTransforms.Matrix(pastedChild.Transform), oldLocal, "Pasted child lost its local pose.");
            editor.Undo();
        }

        editor.Select(parent); editor.DeleteSelected();
        Assert(child.ParentId.Length == 0 && editor.Room.Nodes.Contains(child), "Deleting a parent removed or retained a dangling child.");
        Same(editor.GetNodeWorldMatrix(child), expected, "Deleting a parent shifted its surviving child.");
        editor.Undo(); Assert(child.ParentId == parent.Id, "Delete undo lost the hierarchy.");
        Same(editor.GetNodeWorldMatrix(child), expected, "Delete undo shifted the restored child.");
        editor.Save();
        using var reopened = new RoomEditorControl(fixture.RoomPath, fixture.Root);
        Same(reopened.GetNodeWorldMatrix(reopened.Room.Nodes.Single(node => node.Id == child.Id)), expected,
            "Saving/reopening changed the local hierarchy's world pose.");

        using var host = UnattendedWindowing.NewHost(1380, 880);
        host.Controls.Add(editor); ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host);
        editor.Viewport.Camera.Target = expected.Translation; editor.Viewport.Camera.Distance = 26;
        GateSuite.Pump(3, 20); using (editor.Viewport.CaptureFrame(2)) { }
        Editor3DInspectionSuite.Capture(ctx, host, "room-hierarchy-rotated-scaled-parent");
    }

    private static void PointerDrag(HeadlessContext ctx)
    {
        Fixture fixture = MakeFixture(ctx, "RoomHierarchy2D", RoomDimension.TwoD);
        using var editor = new RoomEditorControl(fixture.RoomPath, fixture.Root);
        RoomNode parent = editor.Room.Nodes[0], child = editor.Room.Nodes[1];
        parent.Transform = new RoomTransform { X = 220, Y = 200, RotationZ = 90, ScaleX = 2, ScaleY = 2, ScaleZ = 2 };
        child.Transform = new RoomTransform { X = 70, Y = 0, ScaleX = 1, ScaleY = 1, ScaleZ = 1 };
        using var host = UnattendedWindowing.NewHost(1380, 880);
        host.Controls.Add(editor); ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host);
        editor.SetSnapEnabled(false); editor.Viewport.Camera2DX = 300; editor.Viewport.Camera2DY = 280;
        editor.Viewport.Zoom2D = 1; editor.SetTool(RoomEditorControl.RoomTool.Select);
        GateSuite.Pump(3, 20); using (editor.Viewport.CaptureFrame(2)) { }
        Vector3 before = editor.GetNodeWorldMatrix(child).Translation;
        Point from = editor.ClientFromWorld2D(new(before.X, before.Y));
        Assert(editor.HitTestNodes(from).Contains(child), "World-space pointer pick missed the parented child.");
        editor.Select(child);
        Point to = editor.ClientFromWorld2D(new(before.X + 42, before.Y + 26));
        editor.EditorPointerDown(from, MouseButtons.Left, Keys.None);
        editor.EditorPointerMove(to, MouseButtons.Left, Keys.None);
        editor.EditorPointerUp(to, MouseButtons.Left, Keys.None);
        Near(editor.GetNodeWorldMatrix(child).Translation, before + new Vector3(42, 26, 0),
            "Pointer drag did not convert the moved world position back to child-local coordinates.", 2f);
        editor.Undo(); Near(editor.GetNodeWorldMatrix(child).Translation, before, "Pointer drag undo stored a world transform as local.");
        Editor3DInspectionSuite.Capture(ctx, host, "room-hierarchy-2d-world-picking");
    }

    private static void TerrainContact(HeadlessContext ctx)
    {
        Fixture fixture = MakeFixture(ctx, "RoomHierarchyTerrain", RoomDimension.ThreeD);
        string terrainPath = Path.Combine(fixture.Root, "Assets", "Parented.gterrain");
        TerrainAsset terrain = new(17, 17, .5f, -4, -4, -2, 4);
        for (int z = 0; z < terrain.ResolutionZ; z++)
        for (int x = 0; x < terrain.ResolutionX; x++) terrain.SetHeight(x, z, .8f + x * .02f);
        terrain.Save(terrainPath);
        using var editor = new RoomEditorControl(fixture.RoomPath, fixture.Root);
        RoomNode parent = editor.Room.Nodes[0], child = editor.Room.Nodes[1];
        parent.Transform = new RoomTransform { X = 10, Y = 2, Z = -5, RotationY = 35, ScaleX = 2, ScaleY = 2, ScaleZ = 2 };
        RoomNode ground = new() { Name = "Parented slope", Kind = RoomNodeKind.Terrain, ParentId = parent.Id,
            LayerId = parent.LayerId, Terrain = new RoomTerrainData { Asset = Path.GetRelativePath(fixture.Root, terrainPath).Replace('\\', '/') },
            Transform = new RoomTransform { X = 3, Y = 1, Z = 4, RotationY = 12 } };
        editor.Room.Nodes.Add(ground);
        Matrix4x4 groundWorld = editor.GetNodeWorldMatrix(ground);
        Vector3 contact = Vector3.Transform(new Vector3(0, terrain.GetHeight(8, 8), 0), groundWorld);
        using var runtime = new RoomTerrainSubsystem(fixture.Root, editor.Room, null!);
        Assert(editor.TryGetTerrainSurface(contact.X, contact.Z, out RoomSurfaceHit picked), "Parented terrain could not be picked at its world position.");
        Near(picked.Position, contact, "Editor parented terrain contact differs from transformed geometry.");
        Assert(MathF.Abs(runtime.SampleHeight(contact.X, contact.Z) - contact.Y) < .002f,
            "Runtime terrain height ignored parent placement or rotation.");
        Assert(Matrix4x4.Invert(editor.GetNodeWorldMatrix(parent), out Matrix4x4 inverse), "Fixture parent became singular.");
        Vector3 localPosition = Vector3.Transform(contact + Vector3.UnitY * 6, inverse);
        child.Transform = new RoomTransform { X = localPosition.X, Y = localPosition.Y, Z = localPosition.Z };
        editor.Select(child);
        RoomTransform localBefore = new() { Position = [.. child.Transform.Position], Rotation = [.. child.Transform.Rotation], Scale = [.. child.Transform.Scale] };
        Assert(editor.SnapSelectionToTerrain(), "Parented instance did not snap to terrain.");
        Matrix4x4 snappedWorld = editor.GetNodeWorldMatrix(child);
        Assert(MathF.Abs(snappedWorld.Translation.X - contact.X) < .002f && MathF.Abs(snappedWorld.Translation.Z - contact.Z) < .002f,
            "Surface contact wrote world coordinates into a local transform.");
        float minimumClearance = float.MaxValue;
        foreach (float x in new[] { -.5f, .5f })
        foreach (float z in new[] { -.5f, .5f })
        {
            Vector3 foot = Vector3.Transform(new Vector3(x, 0, z), snappedWorld);
            float clearance = foot.Y - runtime.SampleHeight(foot.X, foot.Z);
            Assert(clearance >= -.003f, "A bottom corner penetrates the parented terrain after snapping.");
            minimumClearance = MathF.Min(minimumClearance, clearance);
        }
        Assert(minimumClearance < .003f, "Snapped object floats above every terrain contact point.");
        editor.Undo(); Same(RoomHierarchyTransforms.Matrix(child.Transform), RoomHierarchyTransforms.Matrix(localBefore), "Terrain snap undo did not restore local TRS.");
    }

    private sealed record Fixture(string Root, string RoomPath);
    private static Fixture MakeFixture(HeadlessContext ctx, string folder, RoomDimension dimension)
    {
        var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, folder), folder);
        var resources = new ResourceService(project);
        string roomPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Room, "Hierarchy");
        string model = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Hierarchy pillar");
        var baked = ModelPartBuilder.Bake([new ModelPart { Name = "Pillar", Position = [0, .5f, 0], Scale = [1, 1, 1] }]);
        StudioModelResourceLoader.SaveCanonical(model, ModelRigBridge.BuildAsset("Hierarchy pillar", baked.Vertices, baked.Indices));
        string prefab = resources.CreateResource(resources.AssetsRoot, ResourceKind.GameObject, "Hierarchy marker");
        File.WriteAllText(prefab, new JObject { ["name"] = "Hierarchy marker", ["model"] = Relative(model) }.ToString());
        RoomAsset room = RoomAsset.Create("Hierarchy", dimension);
        RoomNode parent = Node("Parent", new RoomTransform { X = 5, Y = 2, Z = -3,
            RotationX = 15, RotationY = 65, RotationZ = -10, ScaleX = 2, ScaleY = 2, ScaleZ = 2 });
        RoomNode child = Node("Child", new RoomTransform { X = 1, Y = 2, Z = 3,
            RotationX = -10, RotationY = 20, RotationZ = 25, ScaleX = .5f, ScaleY = 1, ScaleZ = 1.5f });
        child.ParentId = parent.Id;
        room.Nodes.AddRange([parent, child, Node("Other parent", new RoomTransform { X = -4, Y = 1, Z = 2,
            RotationY = -25, ScaleX = 1.5f, ScaleY = 1.5f, ScaleZ = 1.5f })]);
        RoomAssetLoader.Save(room, roomPath);
        return new Fixture(project.RootPath, roomPath);
        RoomNode Node(string name, RoomTransform transform) => new() { Name = name, Kind = RoomNodeKind.GameObject,
            LayerId = room.Layers[0].Id, GameObject = new RoomGameObjectData { Prefab = Relative(prefab) }, Transform = transform };
        string Relative(string path) => Path.GetRelativePath(project.RootPath, path).Replace('\\', '/');
    }

    private static void Same(Matrix4x4 actual, Matrix4x4 expected, string message) =>
        Assert(RoomHierarchyTransforms.NearlyEqual(actual, expected, .0004f), message);
    private static void Near(Vector3 actual, Vector3 expected, string message, float tolerance = .002f) =>
        Assert(Vector3.Distance(actual, expected) < tolerance, $"{message} Expected {expected}, got {actual}.");
    private static void Assert(bool value, string message) => HeadlessHarness.Assert(value, message);
}
