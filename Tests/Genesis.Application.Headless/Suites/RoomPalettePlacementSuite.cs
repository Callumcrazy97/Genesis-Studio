using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless.Suites;

internal static class RoomPalettePlacementSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Palette.NeutralSkinThumbnailDoesNotWrite", () => CheckNeutralThumbnail(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Palette.DropTransformLayerUndoPersistence", () =>
        {
            var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "RoomPalettePlacement"), "Palette placement");
            var resources = new ResourceService(project);
            string model = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Offset pillar");
            File.WriteAllText(model, """{"scale":1,"parts":[{"name":"Pillar","primitive":"Cube","position":[0,-3,0],"scale":[1,2,1]}]}""");
            string prefab = resources.CreateResource(resources.AssetsRoot, ResourceKind.GameObject, "Pillar object");
            File.WriteAllText(prefab, new JObject { ["name"] = "Pillar object", ["model"] = Path.GetRelativePath(project.RootPath, model).Replace('\\', '/') }.ToString());
            string roomPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Room, "Palette room");
            RoomAssetLoader.Save(RoomAsset.Create("Palette room", RoomDimension.ThreeD), roomPath);
            using var editor = new RoomEditorControl(roomPath, project.RootPath);
            using var host = UnattendedWindowing.NewHost(1400, 900);
            host.Controls.Add(editor); UnattendedWindowing.ShowWithoutFocus(host); GateSuite.Pump(3, 20);
            editor.Viewport.Camera.Target = new Vector3(0, 1, 0); editor.Viewport.Camera.Distance = 20;
            using (editor.Viewport.CaptureFrame(2)) { }
            var thumbnailDeadline = DateTime.UtcNow.AddSeconds(8);
            while (editor.Navigation.ObjectsPanel.ModelThumbnailCount == 0 && DateTime.UtcNow < thumbnailDeadline) GateSuite.Pump(1, 50);
            Assert(editor.Navigation.ObjectsPanel.ModelThumbnailCount == 1,
                "The 3D palette kept a generic icon: " + editor.Navigation.ObjectsPanel.ThumbnailError);
            RoomLayer layer = editor.AddRoomLayer("Props");
            editor.Placement.TargetLayerId = layer.Id;
            editor.Placement.PlacementRotation = 30;
            editor.Placement.PlacementScale = new Vector2(1.5f, 2);
            editor.Placement.PlacementScaleZ = .75f;
            Point point = editor.ClientFromWorld3D(Vector3.Zero);
            editor.BeginPlacement(prefab); editor.EditorPointerMove(point, MouseButtons.None, Keys.None);
            RoomTransform ghost = editor.PlacementGhostTransform3D ?? throw new InvalidOperationException("Missing placement ghost.");
            Assert(editor.DropObjectAt(prefab, point), "Palette drop did not create an instance.");
            RoomNode node = editor.Room.Nodes.Single();
            Assert(node.LayerId == layer.Id && MathF.Abs(node.Transform.RotationY - 30) < .001f
                && MathF.Abs(node.Transform.ScaleY - 2) < .001f && MathF.Abs(node.Transform.ScaleZ - .75f) < .001f,
                $"Drop ignored placement fields: layer {node.LayerId}/{layer.Id}, yaw {node.Transform.RotationY}, scale Y {node.Transform.ScaleY}, Z {node.Transform.ScaleZ}.");
            Assert(Vector3.Distance(new(node.Transform.X, node.Transform.Y, node.Transform.Z), new(ghost.X, ghost.Y, ghost.Z)) < .001f,
                "Configured ghost disagrees with configured drop.");
            editor.Undo(); Assert(editor.Room.Nodes.Count == 0, "Drop undo left an instance behind.");
            editor.Redo(); Assert(editor.Room.Nodes.Count == 1, "Drop redo lost the instance.");
            Assert(editor.RenameRoomLayer(layer, "Scenery"), "Layer rename failed.");
            editor.Undo(); Assert(layer.Name == "Props", "Layer rename undo failed."); editor.Redo();
            Assert(editor.RemoveRoomLayer(layer), "Layer removal failed.");
            Assert(node.LayerId != layer.Id && editor.Room.Nodes.Contains(node), "Layer removal discarded its instances.");
            editor.Undo(); Assert(editor.Room.Layers.Contains(layer) && node.LayerId == layer.Id, "Layer undo did not restore ownership.");
            editor.SetLayerLocked(layer, true);
            Assert(!editor.DropObjectAt(prefab, point) && editor.Room.Nodes.Count == 1, "Drop placed into a locked layer.");
            editor.Save();
            RoomAsset saved = RoomAssetLoader.Parse(roomPath);
            Assert(saved.Nodes.Single().Transform.ScaleZ == .75f && saved.Nodes.Single().LayerId == layer.Id && saved.Layers.Single(item => item.Id == layer.Id).Locked,
                "Placement or layer changes did not survive save/reopen.");
        });
    }

    private static void CheckNeutralThumbnail(HeadlessContext ctx)
    {
        var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "RoomNeutralSkinThumbnail"), "Neutral thumbnail");
        var resources = new ResourceService(project);
        string modelPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Translated bind pose");
        var baked = ModelPartBuilder.Bake([new ModelPart { Name = "Orange marker", Scale = [2, 3, 2] }]);
        GModelAsset asset = ModelRigBridge.BuildAsset("Translated bind pose", baked.Vertices, baked.Indices);
        foreach (GModelMesh mesh in asset.Meshes)
        {
            mesh.SkinnedVertices = mesh.Vertices.Select(vertex => new SkinnedMeshVertex
            {
                Position = vertex.Position, Normal = vertex.Normal, UV = vertex.UV, Color = Vector4.One,
                JointIndices = Vector4.Zero, JointWeights = new Vector4(1, 0, 0, 0),
            }).ToArray();
            mesh.Vertices = []; mesh.IsSkinned = true;
        }
        asset.Materials.Add(new GModelMaterial { BaseColor = new Vector4(1, .3f, .04f, 1) });
        asset.Rig = new GModelRig
        {
            Bones = [new GModelBone { Name = "Root", BindLocal = Matrix4x4.CreateTranslation(20, 0, 0) }],
            InverseBindMatrices = [Matrix4x4.Identity],
        };
        asset.RecalculateBounds();
        Assert(asset.Bounds.Min.X > 15, "Fixture must frame the neutral skin, far away from the raw vertices.");
        StudioModelResourceLoader.SaveCanonical(modelPath, asset);
        string canonical = StudioModelResourceLoader.CanonicalPath(modelPath);
        var saved = new[] { modelPath, canonical }.ToDictionary(path => path,
            path => (Bytes: File.ReadAllBytes(path), Modified: File.GetLastWriteTimeUtc(path)));
        string objectPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.GameObject, "Orange marker object");
        File.WriteAllText(objectPath, new JObject { ["name"] = "Orange marker object", ["dimension"] = "ThreeD",
            ["model"] = Path.GetRelativePath(project.RootPath, modelPath).Replace('\\', '/') }.ToString());
        string roomPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Room, "Thumbnail room");
        RoomAssetLoader.Save(RoomAsset.Create("Thumbnail room", RoomDimension.ThreeD), roomPath);
        using var editor = new RoomEditorControl(roomPath, project.RootPath);
        using var host = UnattendedWindowing.NewHost(1400, 900);
        editor.Dock = DockStyle.Fill; host.Controls.Add(editor); UnattendedWindowing.ShowWithoutFocus(host);
        RoomObjectsPanel objects = editor.Navigation.ObjectsPanel;
        DateTime deadline = DateTime.UtcNow.AddSeconds(8);
        while (objects.ModelThumbnailCount == 0 && DateTime.UtcNow < deadline) GateSuite.Pump(1, 50);
        Assert(objects.ModelThumbnailCount == 1, "Neutral model thumbnail failed: " + objects.ThumbnailError);
        ListBox shelf = objects.ObjectAssetShelf;
        Assert(shelf.Visible && shelf.Items.Count == 1, "Neutral thumbnail fixture is not visible in the object shelf.");
        using Bitmap capture = VisualCapture.CaptureWindowPixels(host);
        Point origin = host.PointToClient(shelf.PointToScreen(new Point(5, 5)));
        Rectangle thumbnail = Rectangle.Intersect(new Rectangle(origin, new Size(56, 56)), new Rectangle(Point.Empty, capture.Size));
        int orange = 0;
        for (int y = thumbnail.Top; y < thumbnail.Bottom; y++)
            for (int x = thumbnail.Left; x < thumbnail.Right; x++)
            {
                Color pixel = capture.GetPixel(x, y);
                if (pixel.R > pixel.G + 20 && pixel.R > pixel.B + 30) orange++;
            }
        capture.Save(Path.Combine(ctx.Captures, "room-neutral-skin-thumbnail.png"));
        Assert(orange > 100, $"Neutral skin thumbnail does not contain the orange model ({orange} pixels); raw vertices were rendered outside the bind-pose bounds.");
        foreach (var pair in saved)
            Assert(File.GetLastWriteTimeUtc(pair.Key) == pair.Value.Modified && File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value.Bytes),
                "Baking a thumbnail rewrote the original model: " + Path.GetFileName(pair.Key));
    }

    private static void Assert(bool value, string message) => HeadlessHarness.Assert(value, message);
}
