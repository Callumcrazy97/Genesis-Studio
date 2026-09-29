using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Settings;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Application.Studio.Theme;
using Genesis.Physics;
using Genesis.Runtime;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Project;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Newtonsoft.Json.Linq;
using PhysicsMotionType = Genesis.Shared.ECS.Components.PhysicsMotionType;

namespace Genesis.Application.Headless.Suites;

internal static class PhysicsModelWorkflowSuite
{
    public static void Run(HeadlessContext ctx)
    {
        var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "PhysicsModelWorkflow"), "Physics Model workflow");
        ResourceService resources = new(project);
        string model = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Linked body Model");
        GModelAsset geometry = GModelPrimitiveFactory.CreateCube("Body", 1);
        geometry.Pivot.Position = new(.2f, -.5f, .1f);
        geometry.Colliders.Add(GModelProductionTools.FitCollider(geometry, GModelColliderShape.Sphere));
        geometry.Colliders[0].Motion = GModelColliderMotion.Static;
        using (ModelEditorControl editor = new(model, project.RootPath)) { editor.ApplyAnimationWorkspace(geometry, ""); editor.Save(); }
        byte[] originalModel = File.ReadAllBytes(model);
        string physics = resources.CreateResource(resources.AssetsRoot, ResourceKind.Physics, "Linked body Physics");
        PhysicsSceneConfig asset = PhysicsScenePresets.Default(); asset.Dimension = PhysicsDimension.ThreeD;
        asset.Friction = .35; asset.Restitution = .2; asset.Density = 2; asset.LockRotation = true;
        asset.CollisionLayer = 2; asset.CollisionLayerMatrix[2][4] = asset.CollisionLayerMatrix[4][2] = false;
        string objectPath = "";
        HeadlessHarness.RunCase(ctx.Report, "Editor.Physics.Model.GeneratedObjectAndEveryShapeUseSavedBoundsAndMaterial", () =>
        {
            using (PhysicsEditorControl editor = new(physics, project.RootPath))
            {
                Check(editor.SetDefinition(PhysicsCodeCodec.Serialize(asset)), "The 3D body definition was rejected.");
                Check(editor.ChooseModel(ResourceNames.Name(project.RootPath, model)), "Quick setup rejected the saved Model.");
                objectPath = editor.CreatePhysicsObject("Linked physics Object");
            }
            asset.PreviewAssetKind = "Model"; asset.PreviewAssetPath = ResourceNames.Name(project.RootPath, model);
            JObject document = JObject.Parse(File.ReadAllText(objectPath));
            Check((string?)document["dimension"] == "ThreeD" && (string?)document["physics"] == ResourceNames.Name(project.RootPath, physics)
                && ObjectEventStore.Load(objectPath)["Create"].Contains("ModelSet", StringComparison.Ordinal), "The Object lost its saved Physics, Model or real Create event.");
            foreach (PhysicsBodyShape shape in Enum.GetValues<PhysicsBodyShape>())
            {
                asset.Shape = shape; asset.SaveToFile(physics);
                using RuntimeScene scene = new();
                RoomAsset room = Fixture(); Entity entity = new RoomSceneBuilder(project.RootPath).Build(scene, room).SpawnedEntities.Single();
                scene.Physics.EnableThreadDispatcher = false;
                RigidBodyComponent body = scene.World.GetRef<RigidBodyComponent>(entity);
                Vector3 expectedSize = shape == PhysicsBodyShape.Sphere ? new Vector3(1.5f)
                    : shape is PhysicsBodyShape.Capsule or PhysicsBodyShape.Cylinder ? new(1, 1.5f, 0) : new(1, 1.5f, .5f);
                Check(body.Size == expectedSize && body.Motion == PhysicsMotionType.Dynamic && !body.PlanarTwoD && body.LockRotation
                    && Math.Abs(body.Friction - .35f) < .001 && Math.Abs(body.Restitution - .2f) < .001 && body.CollisionLayer == 2
                    && (body.CollisionMask & (1u << 4)) == 0, "Gameplay ignored linked Physics or scaled Model bounds for " + shape);
                if (shape is PhysicsBodyShape.Box or PhysicsBodyShape.Mesh) Check(Math.Abs(body.Mass - 12) < .001, "Density did not use actual scaled volume.");
                Check(shape == PhysicsBodyShape.Mesh ? scene.World.Has<MeshColliderComponent>(entity) : body.LocalOffset == new Vector3(.4f, 1.5f, -.1f), "Mirrored scale or pivot was lost.");
                scene.UpdateFixed(1f / 60);
                Check(scene.Physics.Raycast(scene.World, new(0, 8, 0), -Vector3.UnitY, 20, out var hit)
                    && hit.Entity == entity && hit.Distance is > 1.8f and < 4.1f, "The real registered shape cannot be hit: " + shape);
                var collider = scene.Physics.CaptureColliderDebug().Single();
                Check(collider.Lines.Count >= 12 && collider.Lines.All(line => float.IsFinite(line.Start.LengthSquared())), "Actual shape wires are missing: " + shape);
                for (int i = 0; i < 30; i++) scene.UpdateFixed(1f / 60);
                Check(scene.World.GetRef<Transform3DComponent>(entity).Position.Y < 2.9f, "The authored Dynamic body did not fall: " + shape);
            }
            Check(File.ReadAllBytes(model).SequenceEqual(originalModel), "Using Physics changed the Model's geometry or own collider.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Physics.Model.SavedScriptsBodyEditsAndDisabledComponent", () =>
        {
            Check(File.Exists(objectPath), "The generated saved Object is missing.");
            asset.Shape = PhysicsBodyShape.Box;
            using (ObjectEditorControl editor = new(objectPath, project.RootPath))
            {
                editor.SetEventBody("Create", "ModelSet(\"Linked body Model\"); PhysicsSetVelocity(InstanceSelf(), 3, 0, 2);"); editor.Save();
            }
            var oldGame = PgslCommands.ActiveGameContext; string oldProject = PgslCommands.ProjectPath;
            try
            {
                foreach (PhysicsBodyKind kind in new[] { PhysicsBodyKind.Static, PhysicsBodyKind.Kinematic, PhysicsBodyKind.Dynamic })
                {
                    asset.BodyType = kind; asset.Friction = .62; asset.GravityScale = 0; asset.SaveToFile(physics);
                    using RuntimeScene scene = new(); RoomAsset room = Fixture();
                    ProjectGameContext game = new(project.RootPath, scene, null, null, room, null);
                    PgslCommands.ActiveGameContext = game; PgslCommands.ProjectPath = project.RootPath;
                    VMEngine.Initialize(); ScriptHostSystem scripts = new(); scripts.SetContext(game);
                    try
                    {
                        Entity entity = ProjectRoomLoader.Build(project.RootPath, scene, room, scripts, game, beginGame: true).SpawnedEntities.Single();
                        scene.Physics.EnableThreadDispatcher = false; scene.UpdateFixed(1f / 60);
                        var body = scene.World.GetRef<RigidBodyComponent>(entity);
                        Check(body.Motion.ToString() == kind.ToString() && Math.Abs(body.Friction - .62f) < .001 && scripts.RecentDiagnostics.Count == 0,
                            "Restarting did not apply a saved body edit or the generated Object script failed: " + kind);
                        Check(body.LocalOffset == new Vector3(.4f, 1.5f, -.1f) && Math.Abs(body.Mass - 12) < .001,
                            "Editing Create lost the canonical Model pivot or scaled volume before Physics attachment.");
                        Vector3 before = scene.World.GetRef<Transform3DComponent>(entity).Position;
                        for (int i = 0; i < 30; i++) scene.UpdateFixed(1f / 60);
                        Vector3 after = scene.World.GetRef<Transform3DComponent>(entity).Position;
                        Check(kind == PhysicsBodyKind.Static ? Vector3.Distance(before, after) < .001 : after.X > before.X + .5f && after.Z > before.Z + .5f,
                            "Saved Object Create velocity did not use XYZ metres/second: " + kind);
                        Check(ObjectDrawAssetRegistry.TryGet(entity, out var draw) && draw.Model == "Linked body Model", "The actual ModelSet event lost its linked Model.");
                    }
                    finally { scripts.Shutdown(); }
                }
            }
            finally { PgslCommands.ActiveGameContext = oldGame; PgslCommands.ProjectPath = oldProject; }
            JObject disabled = JObject.Parse(File.ReadAllText(objectPath)); new ObjectCompositionModel(disabled).SetEnabled("PhysicsComponent", false);
            File.WriteAllText(objectPath, disabled.ToString());
            using RuntimeScene stopped = new(); Entity inactive = new RoomSceneBuilder(project.RootPath).Build(stopped, Fixture()).SpawnedEntities.Single();
            Check(!stopped.World.Has<RigidBodyComponent>(inactive), "Disabling Physics silently reattached the Model's own collider.");
            new ObjectCompositionModel(disabled).SetEnabled("PhysicsComponent", true); File.WriteAllText(objectPath, disabled.ToString());
        });
        foreach ((int width, float scale) in new[] { (1440, 1f), (1000, 1f), (1440, 2f) })
            HeadlessHarness.RunCase(ctx.Report, $"Editor.Physics.Model.Layout.{width}.Scale{scale}", () =>
            {
                GenesisSettings settings = new(); settings.Appearance.InterfaceScale = scale;
                try
                {
                    ThemeService.ApplySettings(settings);
                    using PhysicsEditorControl editor = new(physics, project.RootPath); using Form host = UnattendedWindowing.NewHost(width, 900);
                    host.Controls.Add(editor); ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host); GateSuite.Pump(3, 20);
                    Button choose = (Button)editor.Controls.Find("PhysicsChooseImage", true).Single();
                    Check(choose.Visible && choose.Text.Contains("Model", StringComparison.Ordinal), "3D Quick setup does not expose its Model choice.");
                    Capture("quick");
                    foreach (ToolStrip bar in Descendants(editor).OfType<ToolStrip>())
                        foreach (ToolStripButton button in bar.Items.OfType<ToolStripButton>())
                            if (button.Text == "Use in game") button.PerformClick();
                    Control guide = editor.Controls.Find("PhysicsUseInGame", true).Single();
                    Button create = (Button)editor.Controls.Find("PhysicsCreateObject", true).Single();
                    Check(guide.Visible && create.Text == "Create model Object" && create.Width > 160, "The 3D gameplay guide is missing or clipped.");
                    ((TextBox)editor.Controls.Find("PhysicsObjectName", true).Single()).Text = $"Layout body {width} {scale}"; create.PerformClick();
                    string result = editor.Controls.Find("PhysicsCreateResult", true).Single().Text;
                    Check(result.StartsWith("Created ", StringComparison.Ordinal), "The actual Create Object button failed: " + result);
                    Capture("game");
                    void Capture(string state)
                    {
                        string name = $"physics-model-{state}-{width}-scale{scale}";
                        var metrics = VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, name + ".png"), true);
                        ctx.Report.Images.Add(ImageResult.From(name, name + ".png", metrics));
                    }
                }
                finally { settings.Appearance.InterfaceScale = 1; ThemeService.ApplySettings(settings); }
            });

        RoomAsset Fixture()
        {
            RoomAsset room = RoomAsset.Create("Linked physics Room", RoomDimension.ThreeD);
            room.Nodes.Add(new RoomNode { Kind = RoomNodeKind.GameObject, LayerId = room.Layers[0].Id,
                GameObject = new() { Prefab = ResourceNames.Name(project.RootPath, objectPath) },
                Transform = new() { Y = 3, ScaleX = -2, ScaleY = 3, ScaleZ = 1 } }); return room;
        }
    }
    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control control in root.Controls) { yield return control; foreach (Control child in Descendants(control)) yield return child; }
    }
}
