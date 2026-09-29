using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Settings;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Scene;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless.Suites;

internal static class RoomPhysicsOverlaySuite
{
    public static void Run(HeadlessContext ctx)
    {
        var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "RoomPhysicsOverlay"), "Room physics overlay");
        ResourceService resources = new(project);
        string ModelObject(string name, float size, GModelColliderShape shape, GModelColliderMotion motion)
        {
            string model = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, name + " Model");
            GModelAsset asset = GModelPrimitiveFactory.CreateCube(name, size);
            GModelCollider collider = GModelProductionTools.FitCollider(asset, shape); collider.Motion = motion; asset.Colliders.Add(collider);
            using ModelEditorControl editor = new(model, project.RootPath); editor.ApplyAnimationWorkspace(asset, ""); editor.Save();
            return editor.CreateModelObject(name + " Object");
        }
        string floor = ModelObject("Floor", 1, GModelColliderShape.Box, GModelColliderMotion.Static);
        string cube = ModelObject("Falling cube", .6f, GModelColliderShape.Box, GModelColliderMotion.Dynamic);
        string hull = ModelObject("Hull", .5f, GModelColliderShape.ConvexHull, GModelColliderMotion.Static);
        string mesh = ModelObject("Mesh", .5f, GModelColliderShape.Mesh, GModelColliderMotion.Static);
        string Fixture(string name)
        {
            RoomAsset room = RoomAsset.Create(name, RoomDimension.ThreeD);
            room.Nodes.Add(Node("Floor", floor, 0, -.25f, 0, 6, .5f, 6));
            room.Nodes.Add(Node("Falling cube", cube, 0, 2, 0));
            room.Nodes.Add(Node("Hull", hull, -1.5f, .25f, 0));
            room.Nodes.Add(Node("Mesh", mesh, 1.5f, .25f, 0));
            foreach (RoomNode node in room.Nodes) node.LayerId = room.Layers[0].Id;
            string path = resources.CreateResource(resources.AssetsRoot, ResourceKind.Room, name); RoomAssetLoader.Save(room, path); return path;
        }
        RoomNode Node(string name, string resource, float x, float y, float z, float sx = 1, float sy = 1, float sz = 1)
            => new() { Name = name, Kind = RoomNodeKind.GameObject, GameObject = new() { Prefab = ResourceNames.Name(project.RootPath, resource) },
                Transform = new() { X = x, Y = y, Z = z, ScaleX = sx, ScaleY = sy, ScaleZ = sz } };
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.PhysicsOverlay.AuthoredCollidersContactsRayAndIsolation", () =>
        {
            string path = Fixture("Inspection"); byte[] saved = File.ReadAllBytes(path);
            using RoomEditorControl editor = new(path, project.RootPath);
            editor.SetPhysicsOverlayVisible(true);
            Check(editor.PhysicsOverlayColliders.Count == 4, "The overlay did not register all saved Model colliders.");
            var dynamic = editor.PhysicsOverlayColliders.Single(c => !c.IsStatic);
            Check(dynamic.Lines.Count == 12 && Math.Abs(dynamic.Lines.Max(l => l.Start.Y) - 2.3f) < .001, "Box wires do not match the registered shape and authored pose.");
            Check(editor.PhysicsOverlayColliders.Any(c => c.Lines.Count == 36), "The mesh's actual triangles are absent.");
            for (int i = 0; i < 120; i++) editor.StepPhysicsPreview();
            Check(editor.PhysicsOverlayContacts.Count > 0 && editor.PhysicsOverlayContacts.All(c => float.IsFinite(c.Position.LengthSquared())
                && c.Normal.LengthSquared() > .9f), "Actual solver contacts and normals are absent.");
            Check(editor.PhysicsOverlayColliders.Single(c => !c.IsStatic).Lines.Max(l => l.Start.Y) < .7f, "Stepping did not move the real temporary body.");
            Check(editor.ProbePhysicsRay(new(0, 5, 0), -Vector3.UnitY) && editor.LastPhysicsProbe is { Distance: > 4 and < 5 }, "Ray probe did not hit the registered resting collider.");
            Check(!editor.ProbePhysicsRay(new(20, 5, 20), Vector3.UnitY) && editor.LastPhysicsProbe is null, "A missed ray retained an old hit.");
            Check(editor.Room.Nodes[1].Transform.Y == 2 && !editor.IsDirty && File.ReadAllBytes(path).SequenceEqual(saved), "Physics inspection moved or saved authored Objects.");
            editor.RestartPhysicsPreview(); Check(editor.PhysicsOverlayContacts.Count == 0
                && Math.Abs(editor.PhysicsOverlayColliders.Single(c => !c.IsStatic).Lines.Max(l => l.Start.Y) - 2.3f) < .001, "Restart retained simulated positions or contacts.");
            editor.SetPhysicsOverlayVisible(false); Check(editor.PhysicsOverlayColliders.Count == 0 && !editor.PhysicsOverlayVisible, "Hiding the overlay retained its world.");
        });
        foreach ((int width, float scale) in new[] { (1440, 1f), (1000, 1f), (1440, 2f) })
            HeadlessHarness.RunCase(ctx.Report, $"Editor.Room.PhysicsOverlay.Layout.{width}.Scale{scale}", () =>
            {
                GenesisSettings settings = new(); settings.Appearance.InterfaceScale = scale;
                try
                {
                    ThemeService.ApplySettings(settings); string path = Fixture($"Layout {width} {scale}");
                    using RoomEditorControl editor = new(path, project.RootPath); using Form host = UnattendedWindowing.NewHost(width, 900);
                    host.Controls.Add(editor); ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host); GateSuite.Pump(3, 20);
                    editor.SetInspectorVisible(false); editor.SetPhysicsOverlayVisible(true);
                    editor.Viewport.Camera.Target = new(0, .6f, 0); editor.Viewport.Camera.Distance = 9;
                    editor.Viewport.Camera.Yaw = MathF.PI - .5f; editor.Viewport.Camera.Pitch = -.3f;
                    for (int i = 0; i < 120; i++) editor.StepPhysicsPreview();
                    using (var frame = editor.Viewport.CaptureFrame(3)) { }
                    Button probe = (Button)editor.Controls.Find("RoomPhysicsProbe", true).Single(); probe.PerformClick();
                    Vector3 projected = editor.Viewport.WorldToSurface(new(0, .3f, 0));
                    editor.EditorPointerDown(new((int)projected.X, (int)projected.Y), MouseButtons.Left, Keys.None);
                    Check(editor.LastPhysicsProbe is not null, "The actual Probe ray button and Room pointer path did not hit a collider.");
                    Control bar = editor.Controls.Find("RoomPhysicsDebugBar", true).Single();
                    Check(bar.Visible && bar.Controls.Cast<Control>().All(c => bar.ClientRectangle.Contains(c.Bounds))
                        && editor.Viewport.Height > 300, "The inspection controls do not fit or crowd out the Room.");
                    string name = $"room-physics-overlay-{width}-scale{scale}";
                    var metrics = VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, name + ".png"), true);
                    ctx.Report.Images.Add(ImageResult.From(name, name + ".png", metrics));
                }
                finally { settings.Appearance.InterfaceScale = 1; ThemeService.ApplySettings(settings); }
            });
    }
    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
