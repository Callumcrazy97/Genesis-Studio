using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Scene;
using Genesis.World.Terrain;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless.Suites;

internal static class RoomSurfacePlacementSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.Room.SurfacePlacement");
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.SurfacePlacement.TransformedTriangleRaycast", () =>
        {
            TerrainAsset terrain = Slope();
            foreach (Matrix4x4 world in new[]
            {
                Matrix4x4.Identity,
                Matrix4x4.CreateScale(2, .7f, 1.4f) * Matrix4x4.CreateFromYawPitchRoll(.55f, -.3f, .18f) * Matrix4x4.CreateTranslation(4, 3, -7),
                Matrix4x4.CreateScale(-2, 1.2f, .6f) * Matrix4x4.CreateRotationY(-.8f) * Matrix4x4.CreateTranslation(-7, -4, 8),
            })
            {
                Vector3 local = new(.35f, Height(.35f, -.6f), -.6f);
                Vector3 expected = Vector3.Transform(local, world);
                Assert(Matrix4x4.Invert(world, out Matrix4x4 inverse), "Fixture transform was singular.");
                Vector3 normal = Vector3.Normalize(Vector3.TransformNormal(Vector3.Normalize(new Vector3(-.25f, 1, -.125f)), Matrix4x4.Transpose(inverse)));
                Assert(RoomSurfacePlacement.Raycast(terrain, world, expected + Vector3.UnitY * 12, -Vector3.UnitY, out RoomSurfaceHit hit), "Vertical world ray missed transformed terrain.");
                Assert(Vector3.Distance(hit.Position, expected) < .002f, "Ray used an untransformed or bilinear height: " + hit.Position);
                Assert(Vector3.Dot(hit.Normal, normal) > .9999f, "Terrain normal was not transformed by inverse transpose.");
                Assert(MathF.Abs(hit.Distance - 12) < .002f, "Ray distance changed under nonuniform scale.");
                Assert(RoomSurfacePlacement.Raycast(terrain, world, expected + normal * 9, -normal, out hit), "Oblique ray missed transformed terrain.");
                Assert(Vector3.Distance(hit.Position, expected) < .002f, "Oblique ray hit the wrong cell.");
                Assert(!RoomSurfacePlacement.Raycast(terrain, world, expected + normal * 9, -normal, out _, 2), "Maximum distance was ignored.");
            }
            Assert(!RoomSurfacePlacement.Raycast(terrain, Matrix4x4.Identity, new Vector3(10, 20, 10), -Vector3.UnitY, out _), "Out-of-bounds ray used clamped edge heights.");
            Assert(!RoomSurfacePlacement.Raycast(terrain, Matrix4x4.CreateScale(1, 0, 1), new Vector3(0, 20, 0), -Vector3.UnitY, out _), "Singular terrain transform was accepted.");
            var saddle = new TerrainAsset(2, 2, 1, 0, 0, 0, 4);
            saddle.SetHeight(0, 0, 0); saddle.SetHeight(1, 0, 0); saddle.SetHeight(0, 1, 0); saddle.SetHeight(1, 1, 4);
            Assert(RoomSurfacePlacement.Raycast(saddle, Matrix4x4.Identity, new Vector3(.75f, 10, .25f), -Vector3.UnitY, out RoomSurfaceHit triangle), "Saddle fixture ray missed.");
            Assert(MathF.Abs(triangle.Position.Y - 1) < .001f, "Picking must match the rendered triangles, not bilinear sampling.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.SurfacePlacement.SupportBoundsAndHeading", () =>
        {
            Vector3 normal = Vector3.Normalize(new Vector3(-.3f, 1, .2f));
            foreach (float yaw in new[] { -170f, -80f, 0f, 65f, 170f })
            {
                Matrix4x4 rotation = RoomSurfacePlacement.SurfaceRotation(normal, yaw);
                Assert(Vector3.Dot(Vector3.TransformNormal(Vector3.UnitY, rotation), normal) > .99999f, "Local up missed slope normal.");
                Vector3 forward = Vector3.TransformNormal(Vector3.UnitZ, rotation);
                float heading = MathF.Atan2(forward.X, forward.Z) * 180 / MathF.PI;
                Assert(MathF.Abs(heading - yaw) < .001f, "Alignment changed horizontal heading.");
                Vector3 min = new(-1.3f, -3.5f, -.3f), max = new(.7f, -.5f, .8f);
                Matrix4x4 basis = Matrix4x4.CreateScale(-1.5f, 2, .7f) * rotation;
                float offset = RoomSurfacePlacement.ContactOffset(min, max, basis, normal);
                Assert(MathF.Abs(LowestPlaneDistance(min, max, basis * Matrix4x4.CreateTranslation(0, offset, 0), Vector3.Zero, normal)) < .001f,
                    "Off-centre, mirrored model support did not touch the surface.");
            }
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.SurfacePlacement.ObjectSupportUsesGeometryBoundsAndUndo", () =>
            CheckObjectSupport(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.SurfacePlacement.MetricPreferencesUndoRoundTrip", () =>
        {
            string folder = Path.Combine(ctx.Workspace, "RoomSurfaceSettings"); Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "Legacy.room.json");
            RoomAsset asset = RoomAsset.Create("Legacy", RoomDimension.ThreeD); asset.Settings.GridSize = 64;
            RoomAssetLoader.Save(asset, file);
            using var editor = new RoomEditorControl(file, folder);
            Assert(editor.MetricGridSize == 2 && editor.Room.Settings.MetricGridSize is null, "Legacy pixel-backed 3D grid changed before editing.");
            foreach (float increment in new[] { .1f, .5f, 1f, 2f })
            {
                editor.SetMetricGridSize(increment);
                Assert(editor.MetricGridSize == increment && editor.Room.Settings.GridSize == 64, "Metric setting rewrote 2D pixel increment.");
            }
            editor.SetMetricGridSize(.1f); editor.Undo();
            Assert(editor.MetricGridSize == 2, "Metric undo failed."); editor.Redo();
            editor.SetSnapToTerrain(false); editor.SetAlignToTerrainNormal(true);
            editor.Save();
            using var reopened = new RoomEditorControl(file, folder);
            Assert(reopened.MetricGridSize == .1f && !reopened.SnapToTerrain && reopened.AlignToTerrainNormal, "Surface preferences were lost on save/reopen.");
            reopened.SetRoomSettingsFields(30, 800, 600);
            reopened.Undo();
            Assert(reopened.MetricGridSize == .1f && !reopened.SnapToTerrain && reopened.AlignToTerrainNormal,
                "An unrelated settings undo discarded surface preferences.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.SurfacePlacement.PointerPlacementDragPersistence", () => CheckEditor(ctx));
    }

    private static void CheckObjectSupport(HeadlessContext ctx)
    {
        var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "RoomObjectSupport"), "Object support");
        var resources = new ResourceService(project);
        string platformModelPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Support platform");
        var platformMesh = ModelPartBuilder.Bake(
            [new ModelPart { Name = "Platform", Position = [0, .5f, 0], Scale = [4, 1, 4] }]);
        GModelAsset platformModel = ModelRigBridge.BuildAsset("Support platform", platformMesh.Vertices, platformMesh.Indices);
        platformModel.Pivot.Position = new Vector3(.2f, -.25f, .1f);
        StudioModelResourceLoader.SaveCanonical(platformModelPath, platformModel);

        string itemModelPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Supported item");
        var itemMesh = ModelPartBuilder.Bake(
            [new ModelPart { Name = "Item", Position = [0, 1, 0], Scale = [1, 2, 1] }]);
        GModelAsset itemModel = ModelRigBridge.BuildAsset("Supported item", itemMesh.Vertices, itemMesh.Indices);
        itemModel.Pivot.Position = new Vector3(.1f, .25f, -.2f);
        StudioModelResourceLoader.SaveCanonical(itemModelPath, itemModel);

        string platformObjectPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.GameObject, "Support platform object");
        string itemObjectPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.GameObject, "Supported item object");
        File.WriteAllText(platformObjectPath,
            new JObject { ["name"] = "Support platform object", ["model"] = Relative(platformModelPath) }.ToString());
        File.WriteAllText(itemObjectPath,
            new JObject { ["name"] = "Supported item object", ["model"] = Relative(itemModelPath) }.ToString());

        string roomPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Room, "Object support room");
        RoomAsset room = RoomAsset.Create("Object support room", RoomDimension.ThreeD);
        RoomNode parent = new()
        {
            Name = "Rotated parent", Kind = RoomNodeKind.GameObject, LayerId = room.Layers[0].Id,
            GameObject = new RoomGameObjectData(),
            Transform = new RoomTransform { X = -6, Y = 2, Z = 5, RotationY = 30, ScaleX = 1.5f, ScaleY = 1.5f, ScaleZ = 1.5f },
        };
        RoomNode item = new()
        {
            Name = "Supported item", Kind = RoomNodeKind.GameObject, LayerId = room.Layers[0].Id, ParentId = parent.Id,
            GameObject = new RoomGameObjectData { Prefab = Relative(itemObjectPath) },
            Transform = new RoomTransform { X = 4, Y = 6, Z = 1, RotationY = 17, ScaleX = 1.2f, ScaleY = .8f, ScaleZ = 1.1f },
        };
        room.Nodes.Add(parent);
        room.Nodes.Add(item);
        RoomTransform initialItemWorld = RoomHierarchyTransforms.World(room, item);
        RoomNode platform = new()
        {
            Name = "Platform", Kind = RoomNodeKind.GameObject, LayerId = room.Layers[0].Id,
            GameObject = new RoomGameObjectData { Prefab = Relative(platformObjectPath) },
            Transform = new RoomTransform
            {
                X = initialItemWorld.X, Y = 1, Z = initialItemWorld.Z,
                RotationY = -25, ScaleX = 1.3f, ScaleY = 2, ScaleZ = .8f,
            },
        };
        room.Nodes.Add(platform);
        room.Nodes.Add(new RoomNode
        {
            Name = "Non-supporting background", Kind = RoomNodeKind.Background, LayerId = room.Layers[0].Id,
            Background = new RoomBackgroundData(),
            Transform = new RoomTransform
            {
                X = initialItemWorld.X, Y = 4, Z = initialItemWorld.Z,
                ScaleX = 4, ScaleY = 1, ScaleZ = 4,
            },
        });
        RoomAssetLoader.Save(room, roomPath);

        using var editor = new RoomEditorControl(roomPath, project.RootPath);
        RoomNode loadedItem = editor.Room.Nodes.Single(node => node.Name == "Supported item");
        RoomNode loadedPlatform = editor.Room.Nodes.Single(node => node.Name == "Platform");
        RoomTransform beforeLocal = JObject.FromObject(loadedItem.Transform).ToObject<RoomTransform>()!;
        RoomTransform beforeWorld = editor.GetNodeWorldTransform(loadedItem);
        Vector3 platformMin = platformModel.Bounds.Min - platformModel.Pivot.Position;
        Vector3 platformMax = platformModel.Bounds.Max - platformModel.Pivot.Position;
        Vector3 platformWorldMax = Bounds(platformMin, platformMax, editor.GetNodeWorldMatrix(loadedPlatform)).Max;
        Vector3 itemMin = itemModel.Bounds.Min - itemModel.Pivot.Position;
        Vector3 itemMax = itemModel.Bounds.Max - itemModel.Pivot.Position;
        Matrix4x4 itemBasis = Matrix4x4.CreateScale(beforeWorld.ScaleX, beforeWorld.ScaleY, beforeWorld.ScaleZ)
            * Matrix4x4.CreateFromYawPitchRoll(beforeWorld.RotationY * MathF.PI / 180,
                beforeWorld.RotationX * MathF.PI / 180, beforeWorld.RotationZ * MathF.PI / 180);
        float expectedY = platformWorldMax.Y
            + RoomSurfacePlacement.ContactOffset(itemMin, itemMax, itemBasis, Vector3.UnitY);

        editor.Select(loadedItem);
        Assert(editor.SnapSelectionToFloor(), "An elevated parented model did not snap to the object surface.");
        RoomTransform snappedWorld = editor.GetNodeWorldTransform(loadedItem);
        Assert(MathF.Abs(snappedWorld.Y - expectedY) < .001f,
            $"Object contact used selection padding or local coordinates ({snappedWorld.Y} instead of {expectedY}).");
        Assert(snappedWorld.Y < 4,
            "A background was treated as a unit-box support surface.");
        Assert(MathF.Abs(LowestPlaneDistance(itemMin, itemMax, editor.GetNodeWorldMatrix(loadedItem),
            new Vector3(snappedWorld.X, platformWorldMax.Y, snappedWorld.Z), Vector3.UnitY)) < .001f,
            "The imported model support point did not touch the transformed platform bounds.");

        editor.Undo();
        RoomTransform undoneWorld = editor.GetNodeWorldTransform(loadedItem);
        Assert(MathF.Abs(loadedItem.Transform.Y - beforeLocal.Y) < .001f
            && Vector3.Distance(new Vector3(undoneWorld.X, undoneWorld.Y, undoneWorld.Z),
                new Vector3(beforeWorld.X, beforeWorld.Y, beforeWorld.Z)) < .001f,
            "Object-surface snap did not restore the parent-relative transform on undo.");

        string Relative(string file) => Path.GetRelativePath(project.RootPath, file).Replace('\\', '/');
    }

    private static void CheckEditor(HeadlessContext ctx)
    {
        var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "RoomSurfacePlacement"), "Surface placement");
        var resources = new ResourceService(project);
        string modelPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Offset crate");
        var baked = ModelPartBuilder.Bake([new ModelPart { Name = "Crate", Position = [0, -2, 0], Scale = [1, 2, 1] }]);
        GModelAsset model = ModelRigBridge.BuildAsset("Offset crate", baked.Vertices, baked.Indices);
        model.Pivot.Position = new Vector3(.3f, .5f, -.2f);
        StudioModelResourceLoader.SaveCanonical(modelPath, model);
        string objectPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.GameObject, "Crate object");
        File.WriteAllText(objectPath, new JObject { ["name"] = "Crate object", ["model"] = Relative(modelPath) }.ToString());
        string terrainPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Terrain, "Slope");
        Slope().Save(terrainPath + ".gterrain");
        string roomPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Room, "Terrain staging");
        RoomAsset room = RoomAsset.Create("Terrain staging", RoomDimension.ThreeD);
        room.Settings.MetricGridSize = .5f; room.Settings.AlignToTerrainNormal = true;
        RoomNode terrain = new()
        {
            Kind = RoomNodeKind.Terrain, Name = "Slope", LayerId = room.Layers[0].Id,
            Terrain = new RoomTerrainData { Asset = Relative(terrainPath) },
            Transform = new RoomTransform { ScaleX = 1.5f, ScaleY = .8f, ScaleZ = 1.3f, RotationY = 18, RotationZ = 8, Y = 1 },
        };
        room.Nodes.Add(terrain); RoomAssetLoader.Save(room, roomPath);
        using var editor = new RoomEditorControl(roomPath, project.RootPath);
        using var host = UnattendedWindowing.NewHost(1400, 900);
        host.Controls.Add(editor); UnattendedWindowing.ShowWithoutFocus(host); GateSuite.Pump(4, 20);
        editor.Viewport.Camera.Target = new Vector3(0, 3, 0);
        editor.Viewport.Camera.Distance = 15; editor.Viewport.Camera.Yaw = .5f; editor.Viewport.Camera.Pitch = -.55f;
        using (editor.Viewport.CaptureFrame(3)) { }
        Assert(editor.TryGetTerrainSurface(0, 0, out RoomSurfaceHit surface), "Placed terrain cannot be sampled.");
        editor.BeginPlacement(objectPath);
        Point client = editor.ClientFromWorld3D(surface.Position);
        editor.EditorPointerMove(client, MouseButtons.None, Keys.None);
        RoomTransform ghost = editor.PlacementGhostTransform3D ?? throw new InvalidOperationException("3D placement preview missing.");
        Vector3 ghostPosition = new(ghost.X, ghost.Y, ghost.Z);
        editor.EditorPointerDown(client, MouseButtons.Left, Keys.None); editor.EditorPointerUp(client, MouseButtons.Left, Keys.None);
        RoomNode node = editor.Room.Nodes.Single(candidate => candidate.Kind == RoomNodeKind.GameObject);
        Assert(Vector3.Distance(ghostPosition, new Vector3(node.Transform.X, node.Transform.Y, node.Transform.Z)) < .001f,
            "Pointer preview and placed model disagreed.");
        CheckContact(node);
        editor.Undo(); Assert(!editor.Room.Nodes.Contains(node), "Placement undo left a partial model.");
        editor.Redo(); Assert(editor.Room.Nodes.Contains(node), "Placement redo did not restore the same node.");
        editor.Select(node); editor.SetGizmo(RoomEditorControl.GizmoKind.Move);
        RoomTransform before = JObject.FromObject(node.Transform).ToObject<RoomTransform>()!;
        float length = MathF.Max(.6f, editor.Viewport.Camera.Distance * .14f);
        Point axis = editor.ClientFromWorld3D(new Vector3(node.Transform.X + length * .7f, node.Transform.Y, node.Transform.Z));
        Point end = editor.ClientFromWorld3D(new Vector3(node.Transform.X + length * .7f + 1, node.Transform.Y, node.Transform.Z));
        editor.EditorPointerDown(axis, MouseButtons.Left, Keys.None);
        editor.EditorPointerMove(end, MouseButtons.Left, Keys.None); editor.EditorPointerUp(end, MouseButtons.Left, Keys.None);
        Assert(MathF.Abs(node.Transform.X - before.X) > .2f, "Horizontal gizmo did not move the instance.");
        CheckContact(node);
        editor.Undo(); Assert(MathF.Abs(node.Transform.X - before.X) < .001f && MathF.Abs(node.Transform.Y - before.Y) < .001f,
            "Drag undo did not restore pre-contact transform."); editor.Redo();
        editor.Select(node); node.Transform.Y += 7;
        Assert(editor.SnapSelectionToFloor(), "End did not seat an elevated model on terrain."); CheckContact(node);
        editor.Save();
        RoomAsset saved = RoomAssetLoader.Parse(roomPath);
        RoomNode savedNode = saved.Nodes.Single(candidate => candidate.Kind == RoomNodeKind.GameObject);
        Assert(Vector3.Distance(new(savedNode.Transform.X, savedNode.Transform.Y, savedNode.Transform.Z),
            new(node.Transform.X, node.Transform.Y, node.Transform.Z)) < .001f, "Save/reopen changed contact position.");
        Assert(MathF.Abs(savedNode.Transform.RotationZ - node.Transform.RotationZ) < .001f, "Save/reopen changed normal alignment.");
        editor.SetKindVisible(RoomNodeKind.Terrain, false);
        Assert(!editor.TryGetTerrainSurface(0, 0, out _), "Hidden terrain remained a placement target.");

        void CheckContact(RoomNode placed)
        {
            Assert(editor.TryGetTerrainSurface(placed.Transform.X, placed.Transform.Z, out RoomSurfaceHit hit), "Moved instance left the fixture terrain.");
            Vector3 normal = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY,
                Matrix4x4.CreateFromYawPitchRoll(placed.Transform.RotationY * MathF.PI / 180,
                    placed.Transform.RotationX * MathF.PI / 180, placed.Transform.RotationZ * MathF.PI / 180)));
            Assert(Vector3.Dot(normal, hit.Normal) > .9999f, "Placed model local up does not match terrain slope.");
            Vector3 min = model.Bounds.Min - model.Pivot.Position, max = model.Bounds.Max - model.Pivot.Position;
            Assert(MathF.Abs(LowestPlaneDistance(min, max, RoomSurfacePlacement.Transform(placed.Transform), hit.Position, hit.Normal)) < .003f,
                "The model pivot, not its base, was placed on terrain.");
        }
        string Relative(string file) => Path.GetRelativePath(project.RootPath, file).Replace('\\', '/');
    }

    private static TerrainAsset Slope()
    {
        var terrain = new TerrainAsset(17, 17, .5f, -4, -4, -2, 6);
        for (int z = 0; z < 17; z++)
            for (int x = 0; x < 17; x++) terrain.SetHeight(x, z, Height(-4 + x * .5f, -4 + z * .5f));
        return terrain;
    }
    private static float Height(float x, float z) => 2 + x * .25f + z * .125f;
    private static float LowestPlaneDistance(Vector3 min, Vector3 max, Matrix4x4 world, Vector3 point, Vector3 normal)
    {
        float result = float.PositiveInfinity;
        for (int mask = 0; mask < 8; mask++)
        {
            Vector3 corner = new((mask & 1) == 0 ? min.X : max.X, (mask & 2) == 0 ? min.Y : max.Y, (mask & 4) == 0 ? min.Z : max.Z);
            result = MathF.Min(result, Vector3.Dot(Vector3.Transform(corner, world) - point, normal));
        }
        return result;
    }
    private static (Vector3 Min, Vector3 Max) Bounds(Vector3 min, Vector3 max, Matrix4x4 world)
    {
        Vector3 resultMin = new(float.MaxValue), resultMax = new(float.MinValue);
        for (int mask = 0; mask < 8; mask++)
        {
            Vector3 corner = Vector3.Transform(new Vector3(
                (mask & 1) == 0 ? min.X : max.X,
                (mask & 2) == 0 ? min.Y : max.Y,
                (mask & 4) == 0 ? min.Z : max.Z), world);
            resultMin = Vector3.Min(resultMin, corner);
            resultMax = Vector3.Max(resultMax, corner);
        }
        return (resultMin, resultMax);
    }
    private static void Assert(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
