using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Physics;
using Genesis.Runtime;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Assets;
using Genesis.Shared.Diagnostics;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Scripting;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Engine settings (the standard API's values a resource gives what uses it): added, changed and
/// removed in the Inspector and saved with the Object, applied to a room's instance (with its own
/// changes) and to the room's world; and the live values of the F6 debug screen, with its Script list.
/// </summary>
internal static class EngineSettingsChecks
{
    /// <summary>
    /// The Room editor's view shows the room's engine settings as the game will (fog from 12 m), with
    /// nothing changed for a room without settings, and values Studio shares (depth of field) put back.
    /// </summary>
    public static void RoomPreview(ProjectSession project, ResourceService resources)
    {
        string roomPath = resources.CreateResource(Path.Combine(resources.AssetsRoot, "Rooms"), ResourceKind.Room, "Settings Preview Room");
        RoomAsset room = RoomAssetLoader.Parse(roomPath);
        room.Dimension = RoomDimension.ThreeD;
        room.EngineSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Engine.Rendering.Fog.Enabled"] = "true",
            ["Engine.Rendering.Fog.Start"] = "12",
            ["Engine.Camera.Focus.Enabled"] = "true",
        };
        RoomAssetLoader.Save(room, roomPath);
        ResourceCatalog.Invalidate(project.RootPath);
        bool focusBefore = Genesis.Shared.Rendering.DepthOfField.Enabled;
        using Form host = new() { Width = 1280, Height = 800, ShowInTaskbar = false };
        var editor = new Genesis.Application.Editors.Suite.Rooms.RoomEditorControl(roomPath, project.RootPath) { Dock = DockStyle.Fill };
        host.Controls.Add(editor);
        GateSuite.ShowHost(host);
        System.Windows.Forms.Application.DoEvents();
        var changes = editor.EngineSettingsPreviewChanges();
        object? Changed(string field) => changes.FirstOrDefault(change => change.Field.Name == field)?.Value;
        HeadlessHarness.Assert(Changed("FogStart") is float start && Math.Abs(start - 12f) < 1e-4,
            $"The room view should show the room's fog from 12 m as the game will; it changes {string.Join(", ", changes.Select(change => change.Field.Name + "=" + change.Value))}.");
        HeadlessHarness.Assert(Genesis.Shared.Rendering.DepthOfField.Enabled == focusBefore,
            "Previewing the room's settings left depth of field changed for the whole of Studio.");

        // The Object editor's view shows an Object's own engine settings on its instance.
        var preview = new Genesis.Application.Editors.Suite.Objects.ObjectCompositionPreviewControl(project.RootPath, compact: true) { Dock = DockStyle.Fill };
        host.Controls.Add(preview);
        System.Windows.Forms.Application.DoEvents();
        var previousGame = PgslCommands.ActiveGameContext;
        preview.Reload(JObject.Parse("{\"schemaVersion\":2,\"dimension\":\"ThreeD\",\"components\":[{\"type\":\"" + EngineSettings.ComponentType
            + "\",\"props\":{\"Engine.Instance.ScaleX\":\"2\"}}]}"));
        float scaleX = preview.PreviewWorld is { } world && world.IsAlive(preview.PreviewEntity) ? world.GetRef<TransformComponent>(preview.PreviewEntity).ScaleX : float.NaN;
        HeadlessHarness.Assert(Math.Abs(scaleX - 2f) < 1e-4 && ReferenceEquals(PgslCommands.ActiveGameContext, previousGame),
            $"The Object view should show the Object's Engine.Instance.ScaleX of 2 (it shows {scaleX}) and leave Studio's game as it was.");
    }

    public static void InspectorAndRooms(ProjectSession project, ResourceService resources)
    {
        string objectPath = resources.CreateResource(Path.Combine(resources.AssetsRoot, "Objects"), ResourceKind.GameObject, "SettingsProbe");
        // A 3D Object whatever the project's own dimension, so its 3D values (Physics, Model) are offered.
        JObject probeObject = JObject.Parse(File.ReadAllText(objectPath));
        probeObject["dimension"] = "ThreeD";
        File.WriteAllText(objectPath, probeObject.ToString());
        const string Prefix = EngineSettingsInspector.Prefix;
        using (Form host = new() { Width = 1280, Height = 800, ShowInTaskbar = false })
        {
            var editor = new ObjectEditorControl(objectPath, project.RootPath) { Dock = DockStyle.Fill };
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            System.Windows.Forms.Application.DoEvents();
            ResourceInspectorLiveValue? Row(string path) => editor.GetLiveInspectorValues().FirstOrDefault(value => value.PropertyPath == path);

            IReadOnlyList<string> choices = Row(EngineSettingsInspector.AddPath)?.Choices ?? [];
            // Listed without "Engine." and with each default, so typing an area's first letters finds it
            // and the list says what it starts at; the listed form, a short name or a full name can be chosen.
            bool Listed(string name) => choices.Any(choice => choice.StartsWith(name + " (", StringComparison.Ordinal) && choice.EndsWith(')'));
            HeadlessHarness.Assert(Listed("Physics.Mass") && Listed("Instance.Yaw") && Listed("Model.Glow")
                && !Listed("Physics.AirDrag") && !choices.Any(choice => choice.StartsWith("Engine.", StringComparison.Ordinal)),
                $"The Object's Add a setting list is not the instance values with their defaults ({choices.Count} offered: {string.Join(", ", choices.Take(4))}).");
            string glow = choices.First(choice => choice.StartsWith("Model.Glow (", StringComparison.Ordinal));
            HeadlessHarness.Assert(editor.TryApplyInspectorValue(EngineSettingsInspector.AddPath, glow)
                && Row(Prefix + "Engine.Model.Glow")?.Description?.Contains("Default: ", StringComparison.Ordinal) == true
                && editor.TryApplyInspectorValue(EngineSettingsInspector.RemovePath, "Model.Glow"),
                $"Choosing '{glow}' from the list did not add the setting with its default described.");
            // A setting's row shows its default beside its name, says its default and range, and keeps to the range.
            HeadlessHarness.Assert(editor.TryApplyInspectorValue(EngineSettingsInspector.AddPath, "Model.Glow.Strength"), "Model.Glow.Strength could not be added.");
            ResourceInspectorLiveValue? glowRow = Row(Prefix + "Engine.Model.Glow.Strength");
            HeadlessHarness.Assert(glowRow is { Label: "Model · Glow · Strength (0)", Minimum: 0m, Maximum: 32m }
                && glowRow.Description?.Contains("Default: 0. From 0 to 32.", StringComparison.Ordinal) == true,
                $"The glow row reads '{glowRow?.Label}' ({glowRow?.Minimum} to {glowRow?.Maximum}): {glowRow?.Description}");
            HeadlessHarness.Assert(editor.TryApplyInspectorValue(EngineSettingsInspector.RemovePath, "Model.Glow.Strength"), "Model.Glow.Strength could not be removed.");
            HeadlessHarness.Assert(editor.TryApplyInspectorValue(EngineSettingsInspector.AddPath, "Instance.Yaw")
                && editor.TryApplyInspectorValue(Prefix + "Engine.Instance.Yaw", 45d)
                && editor.TryApplyInspectorValue(EngineSettingsInspector.AddPath, "Engine.Instance.ScaleX")
                && editor.TryApplyInspectorValue(Prefix + "Engine.Instance.ScaleX", 2d),
                "Adding a setting and changing its value in the Inspector failed.");
            HeadlessHarness.Assert(editor.TryApplyInspectorValue(EngineSettingsInspector.AddPath, "Engine.Physics.BodyType")
                && !editor.TryApplyInspectorValue(Prefix + "Engine.Physics.BodyType", "floppy")
                && editor.TryApplyInspectorValue(Prefix + "Engine.Physics.BodyType", "static")
                && editor.TryApplyInspectorValue(EngineSettingsInspector.RemovePath, "Engine.Physics.BodyType"),
                "A setting with choices took a value it does not have, or could not be removed.");
            HeadlessHarness.Assert(!editor.TryApplyInspectorValue(EngineSettingsInspector.AddPath, "Engine.Physics.AirDrag"),
                "A world value was given to an Object.");
            HeadlessHarness.Assert(Row(Prefix + "Engine.Instance.Yaw")?.Value is double yaw && yaw == 45
                && Row(Prefix + "Engine.Physics.BodyType") is null && Row(EngineSettingsInspector.RemovePath) is not null,
                "The Inspector does not show the Object's settings as they are.");
            editor.Save();
        }
        JObject saved = JObject.Parse(File.ReadAllText(objectPath));
        JObject? props = saved["components"]?.OfType<JObject>()
            .FirstOrDefault(component => (string?)component["type"] == EngineSettings.ComponentType)?["props"] as JObject;
        HeadlessHarness.Assert(props?["Engine.Instance.Yaw"]?.ToString() == "45" && props["Engine.Instance.ScaleX"]?.ToString() == "2" && props.Count == 2,
            $"The Object saved its settings as {props?.ToString(Newtonsoft.Json.Formatting.None) ?? "nothing"}.");

        // A room with a world setting of its own, and an instance that changes one of its Object's and adds one.
        RoomAsset room = RoomAsset.Create("SettingsRoom", RoomDimension.ThreeD);
        room.EngineSettings = new(StringComparer.OrdinalIgnoreCase) { ["Engine.Physics.AirDrag"] = "0.07" };
        RoomNode placed = new()
        {
            Name = "Probe", Kind = RoomNodeKind.GameObject,
            GameObject = new() { Prefab = ResourceCatalog.Name(project.RootPath, objectPath) },
            Transform = new() { X = 1, Y = 0, Z = 2 },
        };
        placed.GameObject.ComponentOverrides.Add(new RoomComponentOverride
        {
            ComponentId = EngineSettings.ComponentType,
            Properties = { ["Engine.Instance.Yaw"] = "90", ["Engine.Instance.Pitch"] = "10" },
        });
        room.Nodes.Add(placed);
        using RuntimeScene scene = new("Engine settings room");
        var game = new ProjectGameContext(project.RootPath, scene, null, null, room, null);
        PgslContext previousContext = PgslCommands.BindContext(new PgslContext());
        var previousGame = PgslCommands.ActiveGameContext;
        string previousPath = PgslCommands.ProjectPath;
        PgslCommands.ActiveGameContext = game;
        PgslCommands.ProjectPath = project.RootPath;
        try
        {
            scene.Physics = PhysicsWorld.Create(new PhysicsWorldAsset());
            RoomBuildResult built = new RoomSceneBuilder(project.RootPath).Build(scene, room);
            Entity probe = built.EntitiesByNodeId[placed.Id];
            TransformComponent t = scene.World.GetRef<TransformComponent>(probe);
            HeadlessHarness.Assert(Math.Abs(t.RotationY - 90) < 1e-3 && Math.Abs(t.RotationX - 10) < 1e-3 && Math.Abs(t.ScaleX - 2) < 1e-3 && t.X == 1 && t.Z == 2,
                $"The placed instance has yaw {t.RotationY} (90), pitch {t.RotationX} (10), scale {t.ScaleX} (2) at {t.X}, {t.Z} (1, 2).");
            HeadlessHarness.Assert(scene.Physics is { } physics && Math.Abs(physics.AirDrag - 0.07f) < 1e-6,
                $"The room's own setting did not reach its world (air drag {scene.Physics?.AirDrag}).");
        }
        finally
        {
            PgslCommands.BindContext(previousContext);
            PgslCommands.ActiveGameContext = previousGame;
            PgslCommands.ProjectPath = previousPath;
        }
    }

    public static void DebugScreen(HeadlessContext ctx)
    {
        using var scene = new RuntimeScene("Engine values on the debug screen");
        var game = new ProjectGameContext(ctx.Workspace, scene, null, null, RoomAsset.Create("Debug", RoomDimension.ThreeD), null);
        PgslContext previousContext = PgslCommands.BindContext(new PgslContext());
        var previousGame = PgslCommands.ActiveGameContext;
        string previousPath = PgslCommands.ProjectPath;
        PgslCommands.ActiveGameContext = game;
        PgslCommands.ProjectPath = ctx.Workspace;
        try
        {
            scene.Physics = PhysicsWorld.Create(new PhysicsWorldAsset());
            scene.Physics.EnableThreadDispatcher = false;
            VMEngine.Initialize();
            var host = new ScriptHostSystem();
            host.SetContext(game);
            var world = scene.World;
            Entity crate = world.CreateEntity();
            world.Set(crate, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
            world.Set(crate, new Transform3DComponent { Position = new(0, 5, 0), Rotation = System.Numerics.Quaternion.Identity, Scale = System.Numerics.Vector3.One });
            world.Set(crate, RigidBodyComponent.DynamicBox(new System.Numerics.Vector3(0.5f), 2f));
            using (host.UseEventSources(new Dictionary<string, string> { ["Create"] = "" })) host.Attach(world, crate, "DebugCrate");
            PgslBehavior behavior = host.Instances.OfType<PgslBehavior>().Single(candidate => candidate.Entity == crate);

            IReadOnlyList<(EngineSettings.Setting Setting, object? Value)> values = EngineSettings.InstanceValues(world, crate, behavior.Context);
            (EngineSettings.Setting Setting, object? Value) Value(string name) => values.FirstOrDefault(value => value.Setting.Name == name);
            var mass = Value("Engine.Physics.Mass");
            var sensor = Value("Engine.Physics.Sensor");
            var type = Value("Engine.Physics.BodyType");
            HeadlessHarness.Assert(mass.Setting != null && Convert.ToDouble(mass.Value) == 2 && sensor.Value is false && type.Value as string == "dynamic"
                && Value("Engine.Model.Glow").Setting == null,
                $"The debug screen's values for a crate are wrong: {string.Join(", ", values.Select(value => value.Setting.Name + "=" + value.Value))}.");
            behavior.ApplyEngineSettings(new Dictionary<string, string>
            {
                [mass.Setting!.Name] = EngineSettings.NudgedText(mass.Setting, mass.Value, 1)!,
                [sensor.Setting!.Name] = EngineSettings.NudgedText(sensor.Setting, sensor.Value, 1)!,
                [type.Setting!.Name] = EngineSettings.NudgedText(type.Setting, type.Value, 1)!,
            });
            RigidBodyComponent body = world.GetRef<RigidBodyComponent>(crate);
            HeadlessHarness.Assert(Math.Abs(body.Mass - 2.1f) < 1e-4 && body.IsSensor && body.Motion == Genesis.Shared.ECS.Components.PhysicsMotionType.Kinematic,
                $"Nudging the crate's values on the debug screen gave mass {body.Mass} (2.1), sensor {body.IsSensor}, {body.Motion} (Kinematic).");

            var air = EngineSettings.WorldValuesNow().FirstOrDefault(value => value.Setting.Name == "Engine.Physics.AirDrag");
            HeadlessHarness.Assert(air.Setting != null
                && EngineSettings.Apply(new[] { new KeyValuePair<string, string>(air.Setting.Name, EngineSettings.NudgedText(air.Setting, air.Value, 1)!) }, new PgslContext()) == 1
                && Math.Abs(scene.Physics.AirDrag - (Convert.ToDouble(air.Value) + 0.1)) < 1e-4,
                $"The world's air drag ({air.Value}) was not nudged to {Convert.ToDouble(air.Value ?? 0d) + 0.1} ({scene.Physics.AirDrag}).");

            ScriptAssetRegistry.Register("DebugListedScript", "function DlsTwice(n) { return n * 2; }");
            DebugResourceRow script = DebugResourceCatalog.Collect().FirstOrDefault(row => row.Kind == "Script" && row.Name == "DebugListedScript");
            HeadlessHarness.Assert(script.Name == "DebugListedScript" && script.Count == 1,
                $"The debug screen's resources do not list a loaded Script with its function ({script.Name}, {script.Count}).");
        }
        finally
        {
            ScriptAssetRegistry.ClearCache();
            PgslCommands.BindContext(previousContext);
            PgslCommands.ActiveGameContext = previousGame;
            PgslCommands.ProjectPath = previousPath;
        }
    }
}
