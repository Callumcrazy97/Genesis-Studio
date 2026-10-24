using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Runtime;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS;
using Genesis.Shared.Scripting;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Objects inside Objects: a part added in the Object's Inspector is saved with it, made with it in a
/// room at its offset, kept on it as it moves and turns, found from PGSL (Engine.Instance.ChildCount,
/// Child, Parent, Attach, Detach) and destroyed with it.
/// </summary>
internal static class ObjectPartsChecks
{
    public static void PartsInRooms(ProjectSession project, ResourceService resources)
    {
        string objects = Path.Combine(resources.AssetsRoot, "Objects");
        string wheel = resources.CreateResource(objects, ResourceKind.GameObject, "PartWheel");
        string cart = resources.CreateResource(objects, ResourceKind.GameObject, "PartCart");
        string box = resources.CreateResource(objects, ResourceKind.GameObject, "PartBox");
        foreach (string path in new[] { wheel, cart, box })
            ObjectEventStore.Save(path, new Dictionary<string, string> { ["Create"] = "made = 1;" });

        using (Form host = new() { Width = 1280, Height = 800, ShowInTaskbar = false })
        {
            // Saved through the editor, as a designer would, so each lists its events and runs them.
            foreach (string path in new[] { wheel, box })
            {
                using var other = new ObjectEditorControl(path, project.RootPath) { Dock = DockStyle.Fill };
                host.Controls.Add(other);
                GateSuite.ShowHost(host);
                System.Windows.Forms.Application.DoEvents();
                other.Save();
                host.Controls.Remove(other);
            }
            var editor = new ObjectEditorControl(cart, project.RootPath) { Dock = DockStyle.Fill };
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            System.Windows.Forms.Application.DoEvents();
            ResourceInspectorLiveValue? Row(string path) => editor.GetLiveInspectorValues().FirstOrDefault(value => value.PropertyPath == path);
            IReadOnlyList<string> choices = Row("Parts.+Add")?.Choices ?? [];
            HeadlessHarness.Assert(choices.Contains("PartWheel") && choices.Contains("PartBox") && !choices.Contains("PartCart"),
                $"Add a part does not offer the other Objects only ({string.Join(", ", choices)}).");
            HeadlessHarness.Assert(editor.TryApplyInspectorValue("Parts.+Add", "PartWheel") && editor.TryApplyInspectorValue("Parts.0.X", 20d)
                && editor.TryApplyInspectorValue("Parts.+Add", "PartBox") && editor.TryApplyInspectorValue("Parts.+Remove", "2 · PartBox")
                && !editor.TryApplyInspectorValue("Parts.+Add", "PartCart"),
                "Adding, changing or removing a part in the Inspector failed (or an Object was made its own part).");
            HeadlessHarness.Assert(Row("Parts.0.Object")?.Value as string == "PartWheel" && Row("Parts.0.X")?.Value is double x && x == 20
                && Row("Parts.1.Object") is null, "The Inspector does not show the Object's parts as they are.");
            editor.Save();
        }
        JArray? saved = JObject.Parse(File.ReadAllText(cart))["parts"] as JArray;
        HeadlessHarness.Assert(saved?.Count == 1 && (string?)saved[0]["object"] == "PartWheel" && (double?)saved[0]["x"] == 20,
            $"The cart saved its parts as {saved?.ToString(Newtonsoft.Json.Formatting.None) ?? "nothing"}.");

        // A 2D room: the cart turned a quarter, so its wheel 20 to the right is 20 below; a box beside it.
        RoomAsset room = RoomAsset.Create("PartsRoom", RoomDimension.TwoD);
        RoomNode Place(string path, float x, float y, float angle)
        {
            var node = new RoomNode
            {
                Name = Path.GetFileNameWithoutExtension(path), Kind = RoomNodeKind.GameObject,
                GameObject = new() { Prefab = ResourceCatalog.Name(project.RootPath, path) },
                Transform = new() { X = x, Y = y, RotationZ = angle },
            };
            room.Nodes.Add(node);
            return node;
        }
        RoomNode cartNode = Place(cart, 100, 50, 90), boxNode = Place(box, 300, 300, 0);
        using RuntimeScene scene = new("Parts room");
        var game = new ProjectGameContext(project.RootPath, scene, null, null, room, null);
        PgslContext previousContext = PgslCommands.BindContext(new PgslContext());
        var previousGame = PgslCommands.ActiveGameContext;
        string previousPath = PgslCommands.ProjectPath;
        PgslCommands.ActiveGameContext = game;
        PgslCommands.ProjectPath = project.RootPath;
        try
        {
            VMEngine.Initialize();
            var scripts = new ScriptHostSystem();
            scripts.SetContext(game);
            RoomBuildResult built = new RoomSceneBuilder(project.RootPath, scripts).Build(scene, room);
            var world = scene.World;
            Entity cartEntity = built.EntitiesByNodeId[cartNode.Id], boxEntity = built.EntitiesByNodeId[boxNode.Id];
            HeadlessHarness.Assert(built.EntitiesByNodeId.TryGetValue(cartNode.Id + "/part1", out Entity wheelEntity)
                && world.Has<ModelSocketAttachmentComponent>(wheelEntity)
                && world.GetRef<ModelSocketAttachmentComponent>(wheelEntity) is { Enabled: true, Part: true } link && link.ParentEntityId == cartEntity.Id,
                "The cart's wheel was not made as a part kept on it.");
            TransformComponent At(Entity entity) => world.GetRef<TransformComponent>(entity);
            HeadlessHarness.Assert(Math.Abs(At(wheelEntity).X - 100) < 0.01 && Math.Abs(At(wheelEntity).Y - 70) < 0.01 && Math.Abs(At(wheelEntity).Rotation - 90) < 0.01,
                $"The wheel is at {At(wheelEntity).X}, {At(wheelEntity).Y} turned {At(wheelEntity).Rotation} (100, 70, 90).");

            // PGSL: the cart's parts, the wheel's parent, and a box held and let go.
            Entity controller = world.CreateEntity();
            world.Set(controller, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
            string code = $"var cart = {cartEntity.Id}; var n = 0; var first = 0; var up = 0; var let = 0;\n"
                + "with (cart) { n = Engine.Instance.ChildCount; first = Engine.Instance.Child(0); }\n"
                + "with (first) { up = Engine.Instance.Parent; }\n"
                + $"count = n; wheel = first; wheelParent = up;\n"
                + $"held = Engine.Instance.Attach({boxEntity.Id}, 5, 0, 0); holding = Engine.Instance.ChildCount;\n"
                + $"with ({boxEntity.Id}) {{ let = Engine.Instance.Detach(); }}\n"
                + "released = let; after = Engine.Instance.ChildCount;\n"
                + "liveCarts = InstanceNumber(\"PartCart\"); liveWheels = InstanceNumber(\"PartWheel\"); liveBoxes = InstanceNumber(\"PartBox\");\n";
            using (scripts.UseEventSources(new Dictionary<string, string> { ["Create"] = code })) scripts.Attach(world, controller, "PartController");
            var vars = scripts.Instances.OfType<PgslBehavior>().Single(behavior => behavior.Entity == controller).GetVariablesSnapshot();
            double Number(string name) => vars.TryGetValue(name, out object? value) && value is not string ? Convert.ToDouble(value) : double.NaN;
            string diagnostics = string.Join("; ", scripts.RecentDiagnostics.Select(d => d.Message));
            HeadlessHarness.Assert(Number("count") == 1 && Number("wheel") == wheelEntity.Id && Number("wheelParent") == cartEntity.Id
                && Number("held") == 1 && Number("holding") == 1 && Number("released") == 1 && Number("after") == 0,
                $"PGSL saw count {Number("count")} (1), wheel {Number("wheel")} ({wheelEntity.Id}), its parent {Number("wheelParent")} ({cartEntity.Id}), "
                + $"held {Number("held")}, holding {Number("holding")}, released {Number("released")}, after {Number("after")}; live carts {Number("liveCarts")}, wheels {Number("liveWheels")}, boxes {Number("liveBoxes")}; behaviours {string.Join(", ", scripts.Instances.OfType<PgslBehavior>().Select(b => b.ScriptName + "#" + b.Entity.Id))}. {diagnostics}");

            // The cart moves and the wheel goes with it; the cart is destroyed and so is the wheel.
            world.GetRef<TransformComponent>(cartEntity).X += 10;
            ModelSocketRuntime.Update(world, project.RootPath, RuntimeModelAssetRegistry.Shared);
            HeadlessHarness.Assert(Math.Abs(At(wheelEntity).X - 110) < 0.01 && Math.Abs(At(wheelEntity).Y - 70) < 0.01,
                $"The wheel stayed at {At(wheelEntity).X}, {At(wheelEntity).Y} when the cart moved (110, 70).");
            PgslCommands.InstanceDestroy(cartEntity.Id);
            world.FlushDeferred();
            ModelSocketRuntime.Update(world, project.RootPath, RuntimeModelAssetRegistry.Shared);
            world.FlushDeferred();
            HeadlessHarness.Assert(!world.IsAlive(wheelEntity) && world.IsAlive(boxEntity), "The wheel was not destroyed with its cart (or the box was).");
        }
        finally
        {
            PgslCommands.BindContext(previousContext);
            PgslCommands.ActiveGameContext = previousGame;
            PgslCommands.ProjectPath = previousPath;
        }
    }
}
