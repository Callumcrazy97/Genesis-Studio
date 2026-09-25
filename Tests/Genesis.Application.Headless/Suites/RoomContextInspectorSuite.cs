using System.Drawing;
using System.Numerics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Application.Studio.Theme;
using Genesis.Rendering.Meshes;
using Genesis.Runtime;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Interfaces;
using Newtonsoft.Json.Linq;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Application.Headless.Suites;

internal static class RoomContextInspectorSuite
{
    private const string ModelPath = "Context.Selection.Components[0].Properties.ModelAsset";
    private const string DamagePath = "Context.Selection.Components[1].Properties.field:Damage";
    private const string LowerDamagePath = "Context.Selection.Components[1].Properties.field:damage";
    private const string ModelScaleXPath = "Context.Selection.Components[0].Properties.ScaleX";

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.Room.ContextInspector");
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Inspector.ProportionalScalePreservesRatiosMirrorsAndUndo", () =>
        {
            Fixture fixture = CreateFixture(ctx, "RoomProportionalScale");
            using var editor = new RoomEditorControl(fixture.RoomPath, fixture.Root);
            using var host = UnattendedWindowing.NewHost(1380, 900);
            editor.Dock = DockStyle.Fill; host.Controls.Add(editor); ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host);
            RoomNode node = editor.Room.Nodes[0];
            node.Transform.ScaleX = 2; node.Transform.ScaleY = -3; node.Transform.ScaleZ = 4;
            editor.Select(node); GateSuite.Pump(3, 20);
            ((CheckBox)Field(editor.Inspector, "Context.Selection.Transform.UniformScale")).Checked = true;
            Numeric(Field(editor.Inspector, "Context.Selection.Transform.Scale.X")).Value = 4;
            GateSuite.Pump(3, 20);
            Assert(node.Transform.ScaleX == 4 && node.Transform.ScaleY == -6 && node.Transform.ScaleZ == 8,
                "Locked scale flattened proportions or lost a mirrored axis.");
            editor.Undo(); Assert(node.Transform.ScaleX == 2 && node.Transform.ScaleY == -3 && node.Transform.ScaleZ == 4,
                "Scale undo did not restore all axes.");
            editor.Redo(); editor.Save();
            RoomNode reopened = RoomAssetLoader.Parse(fixture.RoomPath).Nodes[0];
            Assert(reopened.Transform.ScaleX == 4 && reopened.Transform.ScaleY == -6 && reopened.Transform.ScaleZ == 8,
                "Scale proportions did not survive save/reopen.");
            Editor3DInspectionSuite.Capture(ctx, host, "room-inspector-proportional-scale");
            editor.ViewMode3D = false; GateSuite.Pump(3, 20);
            Numeric(Field(editor.Inspector, "Context.Selection.Transform.Scale.X")).Value = 2;
            Assert(node.Transform.ScaleX == 2 && node.Transform.ScaleY == -3 && node.Transform.ScaleZ == 8,
                "2D proportional scaling changed the hidden Z axis.");
            editor.Undo();
        });
        HeadlessHarness.RunCase(ctx.Report,
            "Editor.Room.Inspector.PgslDeclarationSpecializationIsSurgical",
            CheckPgslDeclarationSpecialization);
        HeadlessHarness.RunCase(ctx.Report,
            "Editor.Room.Inspector.TypedOverridesFocusUndoRuntimeAndPersistence",
            () => CheckInstanceContext(ctx));
        HeadlessHarness.RunCase(ctx.Report,
            "Editor.Room.Inspector.GlobalGravityControlsRuntimeAndPersistence",
            () => CheckRoomContext(ctx));
    }

    private static void CheckPgslDeclarationSpecialization()
    {
        const string source = """
            /*
            var Commented = 91;
            */
            var Damage = 4; var damage = 5; var Enabled = true; var Tiny = 0.000001;
            if (true) {
                var Damage = 77;
            }
            for (var i = 0; i < 2; i++) { }
            var Formula = 2 + 3;
            var Caption = "line one
            var Embedded = 88;
            line three";
            """;
        IReadOnlyList<PgslExposedVariables.Variable> variables = PgslExposedVariables.Reflect(source);
        Assert(variables.Select(variable => variable.Name).SequenceEqual(
                   new[] { "Damage", "damage", "Enabled", "Tiny", "Caption" }),
            "PGSL exposure included a comment, nested local, expression initializer or missed an adjacent declaration.");
        Assert(PgslExposedVariables.ApplyOverrides(source,
                   new Dictionary<string, string>(StringComparer.Ordinal)) == source,
            "An absent PGSL instance override changed the source bytes.");

        string specialized = PgslExposedVariables.ApplyOverrides(source,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Damage"] = "9",
                ["damage"] = "10",
                ["Enabled"] = "false",
                ["Tiny"] = "0.0000001",
                ["Commented"] = "1",
                ["Embedded"] = "1",
            });
        Assert(specialized.Contains("var Damage = 9; var damage = 10; var Enabled = false;", StringComparison.Ordinal)
               && specialized.Contains("var Commented = 91;", StringComparison.Ordinal)
               && specialized.Contains("var Damage = 77;", StringComparison.Ordinal)
               && specialized.Contains("for (var i = 0; i < 2; i++)", StringComparison.Ordinal)
               && specialized.Contains("var Formula = 2 + 3;", StringComparison.Ordinal)
               && specialized.Contains("var Embedded = 88;", StringComparison.Ordinal),
            "PGSL instance specialization replaced text outside exact top-level literal spans.");
        CompiledScriptAsset compiled = ScriptAssetCompiler.Compile("InspectorExponentProbe", specialized);
        Assert(compiled.CompileResult is not null,
            "An Inspector numeric override formatted with exponent notation did not compile as PGSL.");
    }

    private static void CheckInstanceContext(HeadlessContext ctx)
    {
        Fixture fixture = CreateFixture(ctx, "RoomContextInstances");
        using var editor = new RoomEditorControl(fixture.RoomPath, fixture.Root);
        using var host = UnattendedWindowing.NewHost(1380, 900);
        editor.Dock = DockStyle.Fill;
        host.Controls.Add(editor);
        ThemeService.Apply(host);
        UnattendedWindowing.ShowWithoutFocus(host);
        GateSuite.Pump(4, 20);

        RoomNode first = editor.Room.Nodes.Single(node => node.Id == fixture.FirstNodeId);
        RoomNode second = editor.Room.Nodes.Single(node => node.Id == fixture.SecondNodeId);
        editor.SetInspectorVisible(true);
        editor.Select(first);
        GateSuite.Pump(3, 20);
        RoomInspectorPanel inspector = editor.Inspector;

        InspectorSection transform = Descendants(inspector).OfType<InspectorSection>()
            .Single(section => section.Title == "Transform");
        TableLayoutPanel transformTable = transform.Body.Controls.OfType<TableLayoutPanel>().Single();
        Assert(transformTable.RowCount == 4,
            "The 3D Transform Inspector did not group position, rotation and scale into three vector rows plus Uniform.");
        foreach (string axisPath in new[]
                 {
                     "Context.Selection.Transform.Position.X",
                     "Context.Selection.Transform.Position.Y",
                     "Context.Selection.Transform.Position.Z",
                 })
        {
            Control[] labels = inspector.Controls.Find("RoomInspectorAxis_" + SafeName(axisPath), true);
            Assert(labels.Length == 1 && labels[0].Visible && labels[0].Width >= 18 && labels[0].Height >= 20
                   && labels[0].Text == axisPath[^1].ToString(),
                "A vector axis label is missing, clipped or hidden for " + axisPath + ".");
        }

        Control modelField = Field(inspector, ModelPath);
        Control damageField = Field(inspector, DamagePath);
        Assert(AssetText(modelField).Text == fixture.BaseModelReference,
            "The selected instance did not show its inherited model reference.");
        Assert(Numeric(damageField).Value == 4m,
            "The Room Inspector did not discover the inherited top-level PGSL variable.");
        Assert(Numeric(Field(inspector, LowerDamagePath)).Value == 6m,
            "Case-distinct PGSL field paths collapsed in the Room Inspector.");
        Assert(inspector.Controls.Find(
                   "RoomInspectorProperty_" + SafeName("Context.Selection.Components[1].Properties.field:ExpressionValue"),
                   true).Length == 0,
            "An expression initializer was presented as an editable literal PGSL field.");
        Assert(WithinVisibleInspector(inspector, modelField) && WithinVisibleInspector(inspector, damageField),
            "The selected object's model reference or instance variables are below the initial 900px Inspector view.");
        Assert(Field(inspector, "Context.Selection.Locked").Visible,
            "The selected instance lock control is not visible.");
        Editor3DInspectionSuite.Capture(ctx, host, "room-context-inspector-selected");

        ApplyContext(editor, first, ModelPath, fixture.OverrideModelReference);
        inspector.RefreshInspector();
        Assert(AssetText(Field(inspector, ModelPath)).Text == fixture.OverrideModelReference,
            "A model reference override did not refresh in the selected Inspector.");
        (Vector3 firstMin, Vector3 firstMax) = NodeBounds(editor, first);
        (Vector3 secondMin, Vector3 secondMax) = NodeBounds(editor, second);
        Vector3 firstSize = firstMax - firstMin;
        Vector3 secondSize = secondMax - secondMin;
        Assert(firstSize.Y > secondSize.Y * 2.5f && firstSize.X > secondSize.X * 1.8f,
            "The editor bounds did not resolve distinct baked model geometry per prefab instance.");
        editor.FrameContentForTest();
        GateSuite.Pump(3, 20);
        Editor3DInspectionSuite.Capture(ctx, host, "room-context-inspector-instance-overrides");

        Control damageRoot = Field(inspector, DamagePath);
        NumericUpDown damage = Numeric(damageRoot);
        damage.Focus();
        GateSuite.Pump(2, 15);
        Assert(damage.Focused, "The inherited numeric instance variable never accepted edit focus.");
        damage.Value = 9m;
        ApplyContext(editor, first, LowerDamagePath, 12d);
        GateSuite.Pump(3, 15);
        inspector.RefreshInspector();
        Assert(ReferenceEquals(Field(inspector, DamagePath), damageRoot) && damage.Focused,
            "Refreshing an edited value rebuilt the Inspector field or moved its focus.");
        Assert(Override(first, "cmp-script").Properties["field:Damage"].ToString() == "9"
               && Override(first, "cmp-script").Properties["field:damage"].ToString() == "12",
            "Editing an inherited typed variable did not create an instance override.");

        first.Transform.Y = 13.25f;
        inspector.RefreshInspector();
        Assert(ReferenceEquals(Field(inspector, DamagePath), damageRoot) && damage.Focused,
            "Synchronizing another live value replaced the active variable editor.");
        Assert(Numeric(Field(inspector, "Context.Selection.Transform.Position.Y")).Value == 13.25m,
            "A non-focused vector axis did not synchronize its live room value.");

        ApplyContext(editor, first, ModelScaleXPath, 2f);
        Assert(Override(first, "cmp-model").Properties.ContainsKey("ScaleX"),
            "The model scale edit did not enter the per-instance override snapshot.");
        editor.Undo();
        RoomComponentOverride modelAfterUndo = Override(first, "cmp-model");
        RoomComponentOverride scriptAfterUndo = Override(first, "cmp-script");
        Assert(!modelAfterUndo.Properties.ContainsKey("ScaleX")
               && modelAfterUndo.Properties["ModelAsset"].ToString() == fixture.OverrideModelReference
               && scriptAfterUndo.Properties["field:Damage"].ToString() == "9"
               && scriptAfterUndo.Properties["field:damage"].ToString() == "12",
            "One undo removed unrelated instance overrides or retained the last scale edit.");
        editor.Redo();

        editor.Select(second);
        GateSuite.Pump(2, 15);
        Assert(AssetText(Field(inspector, ModelPath)).Text == fixture.BaseModelReference
               && Numeric(Field(inspector, DamagePath)).Value == 4m,
            "Selecting the second shared-prefab instance leaked the first instance's overrides.");
        editor.Select(first);
        GateSuite.Pump(2, 15);
        Assert(AssetText(Field(inspector, ModelPath)).Text == fixture.OverrideModelReference
               && Numeric(Field(inspector, DamagePath)).Value == 9m,
            "Returning to the overridden instance lost its effective values.");

        JObject source = JObject.Parse(File.ReadAllText(fixture.ObjectPath));
        JObject effectiveFirst = RoomSceneBuilder.ApplyOverrides(source, first.GameObject.ComponentOverrides);
        JObject effectiveSecond = RoomSceneBuilder.ApplyOverrides(source, second.GameObject.ComponentOverrides);
        Assert(ModelProperties(effectiveFirst)["ModelAsset"]?.ToString() == fixture.OverrideModelReference
               && ModelProperties(effectiveSecond)["ModelAsset"]?.ToString() == fixture.BaseModelReference
               && ScriptProperties(effectiveFirst)["field:Damage"]?.ToString() == "9"
               && ScriptProperties(effectiveFirst)["field:damage"]?.ToString() == "12"
               && ScriptProperties(effectiveSecond)["field:Damage"] is null,
            "The runtime override document did not keep shared-prefab instances independent.");

        EcsWorld world = new();
        ScriptHostSystem scriptHost = new();
        scriptHost.SetContext(new NullGameContext
        {
            World = world,
            Room = editor.Room,
            ProjectPath = fixture.Root,
        });
        RoomBuildResult built = new RoomSceneBuilder(fixture.Root, scriptHost).Build(world, editor.Room);
        ModelRendererComponent firstRuntime = world.GetRef<ModelRendererComponent>(built.EntitiesByNodeId[first.Id]);
        ModelRendererComponent secondRuntime = world.GetRef<ModelRendererComponent>(built.EntitiesByNodeId[second.Id]);
        Assert(firstRuntime.ModelAsset == fixture.OverrideModelReference && firstRuntime.ScaleX == 2f
               && secondRuntime.ModelAsset == fixture.BaseModelReference && secondRuntime.ScaleX == 1f,
            "F5 runtime components did not receive each instance's effective model reference and scale.");
        PgslBehavior firstBehavior = scriptHost.FindBehaviorForEntity(built.EntitiesByNodeId[first.Id]) as PgslBehavior
            ?? throw new InvalidDataException("The first instance did not attach its PGSL behaviour.");
        PgslBehavior secondBehavior = scriptHost.FindBehaviorForEntity(built.EntitiesByNodeId[second.Id]) as PgslBehavior
            ?? throw new InvalidDataException("The second instance did not attach its PGSL behaviour.");
        Assert(PgslNumber(firstBehavior, "Damage") == 9d && PgslNumber(secondBehavior, "Damage") == 4d
               && PgslNumber(firstBehavior, "damage") == 12d && PgslNumber(secondBehavior, "damage") == 6d
               && PgslNumber(firstBehavior, "ExpressionValue") == 5d
               && PgslNumber(secondBehavior, "ExpressionValue") == 5d,
            "ScriptHostSystem did not initialize independent PGSL VM variables from the effective instances.");
        Assert(world.GetRef<TransformComponent>(built.EntitiesByNodeId[first.Id]).X == 9f
               && world.GetRef<TransformComponent>(built.EntitiesByNodeId[second.Id]).X == 4f
               && world.GetRef<TransformComponent>(built.EntitiesByNodeId[first.Id]).Z == 12f
               && world.GetRef<TransformComponent>(built.EntitiesByNodeId[second.Id]).Z == 6f,
            "The PGSL override was not visible to genuine Create event game logic.");
        scriptHost.Update(1f / 60f);
        Assert(world.GetRef<TransformComponent>(built.EntitiesByNodeId[first.Id]).Y == 9f
               && world.GetRef<TransformComponent>(built.EntitiesByNodeId[second.Id]).Y == 4f,
            "The two instance values did not remain independent in genuine Step event game logic.");

        editor.Save();
        Assert(File.ReadAllText(fixture.CreateEventPath) == fixture.CreateEventSource,
            "Applying a Room instance field rewrote the inherited PGSL source on disk.");
        using var reopened = new RoomEditorControl(fixture.RoomPath, fixture.Root);
        RoomNode savedFirst = reopened.Room.Nodes.Single(node => node.Id == first.Id);
        RoomNode savedSecond = reopened.Room.Nodes.Single(node => node.Id == second.Id);
        RoomComponentOverride savedModel = Override(savedFirst, "cmp-model");
        RoomComponentOverride savedScript = Override(savedFirst, "cmp-script");
        Assert(savedModel.Properties["ModelAsset"].ToString() == fixture.OverrideModelReference
               && savedScript.Properties["field:Damage"].ToString() == "9"
               && savedScript.Properties["field:damage"].ToString() == "12"
               && savedModel.Properties["ScaleX"].ToString() == "2"
               && savedSecond.GameObject.ComponentOverrides.Count == 0,
            "Save/reopen lost the typed override or copied it to the other prefab instance.");
        (Vector3 reopenedMin, Vector3 reopenedMax) = NodeBounds(reopened, savedFirst);
        (Vector3 inheritedMin, Vector3 inheritedMax) = NodeBounds(reopened, savedSecond);
        Assert((reopenedMax - reopenedMin).Y > (inheritedMax - inheritedMin).Y * 2.5f,
            "Save/reopen lost the overridden instance's baked editor bounds.");
    }

    private static void CheckRoomContext(HeadlessContext ctx)
    {
        Fixture fixture = CreateFixture(ctx, "RoomContextGlobals");
        using var editor = new RoomEditorControl(fixture.RoomPath, fixture.Root);
        using var host = UnattendedWindowing.NewHost(1380, 900);
        editor.Dock = DockStyle.Fill;
        host.Controls.Add(editor);
        ThemeService.Apply(host);
        UnattendedWindowing.ShowWithoutFocus(host);
        editor.Select(null);
        editor.Inspector.ShowRoomSettings();
        GateSuite.Pump(3, 20);

        InspectorSection physics = Descendants(editor.Inspector).OfType<InspectorSection>()
            .Single(section => section.Title == "Physics");
        physics.Expanded = true;
        Numeric(Field(editor.Inspector, "Context.Room.GravityX")).Value = 3m;
        Numeric(Field(editor.Inspector, "Context.Room.GravityY")).Value = -4m;
        Numeric(Field(editor.Inspector, "Context.Room.GravityZ")).Value = 0m;
        GateSuite.Pump(3, 15);
        Assert(editor.Inspector.Controls.Find("RoomInspectorProperty_Context_Room_ClearBackground", true).Length == 0,
            "The contextual Room settings exposed a fake clear-background control.");
        Assert(editor.Room.Environment.Gravity.SequenceEqual(new[] { 3f, -4f, 0f }),
            "Gravity controls did not update the persisted Room environment vector.");

        using var runtime = new RuntimeScene();
        RoomSceneBuilder.ApplySceneSettings(runtime, editor.Room);
        Vector3 acceleration = runtime.Physics.GetGravityAcceleration();
        Assert(Vector3.Distance(acceleration, new Vector3(3f, -4f, 0f)) < .0001f,
            "The runtime PhysicsWorld did not consume the Room Inspector gravity vector.");

        editor.Undo();
        Assert(editor.Room.Environment.Gravity.SequenceEqual(new[] { 3f, -9.81f, 0f }),
            "Gravity undo did not revert exactly the last changed axis.");
        editor.Redo();
        Assert(editor.Room.Environment.Gravity.SequenceEqual(new[] { 3f, -4f, 0f }),
            "Gravity redo did not restore exactly the last changed axis.");
        editor.Save();
        using var reopened = new RoomEditorControl(fixture.RoomPath, fixture.Root);
        Assert(reopened.Room.Environment.Gravity.SequenceEqual(new[] { 3f, -4f, 0f }),
            "Room gravity did not survive save/reopen.");
        using var reopenedRuntime = new RuntimeScene();
        RoomSceneBuilder.ApplySceneSettings(reopenedRuntime, reopened.Room);
        Assert(Vector3.Distance(reopenedRuntime.Physics.GetGravityAcceleration(), new Vector3(3f, -4f, 0f)) < .0001f,
            "Reopened Room gravity diverged from the F5 runtime physics state.");
    }

    private static Fixture CreateFixture(HeadlessContext ctx, string folder)
    {
        var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, folder), "Room context inspector");
        var resources = new ResourceService(project);
        string roomPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Room, "Inspector room");
        string objectPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.GameObject, "Shared model object");
        string baseModel = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Base model");
        string overrideModel = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Override model");
        WriteModel(baseModel, "Base cube", new Vector3(.5f, .5f, .5f), new Vector4(.25f, .55f, 1f, 1f));
        WriteModel(overrideModel, "Override tower", new Vector3(1.25f, 2f, .5f), new Vector4(1f, .4f, .15f, 1f));
        string BaseRef() => Relative(project.RootPath, baseModel);
        JObject objectDocument = new()
        {
            ["name"] = "Shared model object",
            ["model"] = BaseRef(),
            ["components"] = new JArray
            {
                new JObject
                {
                    ["id"] = "cmp-model",
                    ["type"] = "ModelRendererComponent",
                    ["enabled"] = true,
                    ["props"] = new JObject
                    {
                        ["ModelAsset"] = BaseRef(),
                        ["ScaleX"] = 1f,
                        ["ScaleY"] = 1f,
                        ["ScaleZ"] = 1f,
                        ["CastShadows"] = true,
                        ["ReceiveShadows"] = true,
                        ["KeepPreviousTransform"] = false,
                    },
                },
                new JObject
                {
                    ["id"] = "cmp-script",
                    ["type"] = "ScriptComponent",
                    ["enabled"] = true,
                    ["props"] = new JObject { ["ScriptClass"] = "Shared model object" },
                },
            },
        };
        File.WriteAllText(objectPath, objectDocument.ToString());
        const string createSource = "var Damage = 4;\nvar damage = 6;\nvar ExpressionValue = 2 + 3;\nx = Damage;\nz = damage;\n";
        ObjectEventStore.Save(objectPath, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Create"] = createSource,
            ["Step"] = "y = Damage;\n",
        });

        RoomAsset room = RoomAsset.Create("Inspector room", RoomDimension.ThreeD);
        RoomNode first = new()
        {
            Name = "First shared instance",
            Kind = RoomNodeKind.GameObject,
            LayerId = room.Layers[0].Id,
            Transform = new RoomTransform { X = -2.5f, ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f },
            GameObject = new RoomGameObjectData { Prefab = Relative(project.RootPath, objectPath) },
        };
        RoomNode second = new()
        {
            Name = "Second shared instance",
            Kind = RoomNodeKind.GameObject,
            LayerId = room.Layers[0].Id,
            Transform = new RoomTransform { X = 2.5f, ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f },
            GameObject = new RoomGameObjectData { Prefab = Relative(project.RootPath, objectPath) },
        };
        room.Nodes.AddRange([first, second]);
        RoomAssetLoader.Save(room, roomPath);
        return new Fixture(
            project.RootPath,
            roomPath,
            objectPath,
            ObjectEventStore.PathFor(objectPath, "Create"),
            createSource,
            BaseRef(),
            Relative(project.RootPath, overrideModel),
            first.Id,
            second.Id);
    }

    private static void WriteModel(string resourcePath, string name, Vector3 halfExtents, Vector4 color)
    {
        var (vertices, indices) = MeshGeometry.BuildCube(RenderColor.White, 2f);
        for (int index = 0; index < vertices.Length; index++)
        {
            MeshVertex vertex = vertices[index];
            vertex.Position *= halfExtents;
            vertex.Color = color;
            vertices[index] = vertex;
        }
        GModelAsset asset = new()
        {
            Name = name,
            Meshes = [new GModelMesh { Name = name, Vertices = vertices, Indices = indices }],
            Materials = [new GModelMaterial { Name = name + " material", EmissiveFactor = new Vector3(.12f) }],
        };
        asset.RecalculateBounds();
        StudioModelResourceLoader.SaveCanonical(resourcePath, asset);
    }

    private static void ApplyContext(RoomEditorControl editor, RoomNode? node, string propertyPath, object value)
    {
        MethodInfo method = typeof(RoomEditorControl).GetMethod(
            "TryApplyContextInspectorValue", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(RoomEditorControl), "TryApplyContextInspectorValue");
        Assert(method.Invoke(editor, [node, propertyPath, value]) is true,
            "The contextual Inspector rejected " + propertyPath + ".");
    }

    private static Control Field(RoomInspectorPanel inspector, string propertyPath)
    {
        string name = "RoomInspectorProperty_" + SafeName(propertyPath);
        Control[] controls = Descendants(inspector)
            .Where(control => control.Name.Equals(name, StringComparison.Ordinal))
            .ToArray();
        Assert(controls.Length == 1, $"Expected exactly one live Inspector control for {propertyPath}; found {controls.Length}.");
        return controls[0];
    }

    private static NumericUpDown Numeric(Control root) => root as NumericUpDown
        ?? Descendants(root).OfType<NumericUpDown>().Single();

    private static TextBox AssetText(Control root) => Descendants(root).OfType<TextBox>().Single();

    private static JObject ModelProperties(JObject document)
    {
        JObject component = ((JArray?)document["components"])?.OfType<JObject>().Single(candidate =>
            string.Equals((string?)candidate["id"], "cmp-model", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Fixture model component was lost.");
        return component["props"] as JObject
            ?? throw new InvalidDataException("Fixture model properties were lost.");
    }

    private static JObject ScriptProperties(JObject document)
    {
        JObject component = ((JArray?)document["components"])?.OfType<JObject>().Single(candidate =>
            string.Equals((string?)candidate["id"], "cmp-script", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Fixture script component was lost.");
        return component["props"] as JObject
            ?? throw new InvalidDataException("Fixture script properties were lost.");
    }

    private static RoomComponentOverride Override(RoomNode node, string componentId) =>
        node.GameObject.ComponentOverrides.Single(component =>
            component.ComponentId.Equals(componentId, StringComparison.OrdinalIgnoreCase));

    private static (Vector3 Min, Vector3 Max) NodeBounds(RoomEditorControl editor, RoomNode node)
    {
        MethodInfo method = typeof(RoomEditorControl).GetMethod(
            "NodeBounds3D", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(RoomEditorControl), "NodeBounds3D");
        object result = method.Invoke(editor, [node])
            ?? throw new InvalidOperationException("Room node bounds returned no value.");
        var bounds = ((Vector3 Min, Vector3 Max))result;
        return bounds;
    }

    private static double PgslNumber(PgslBehavior behavior, string name)
    {
        if (!behavior.GetVariablesSnapshot().TryGetValue(name, out object? value) || value is null)
            throw new InvalidDataException("The PGSL VM has no persistent variable named " + name + ".");
        return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool WithinVisibleInspector(RoomInspectorPanel inspector, Control control)
    {
        Rectangle bounds = inspector.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
        return control.Visible && bounds.Bottom > 0 && bounds.Top < inspector.ClientSize.Height;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child)) yield return descendant;
        }
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string SafeName(string value) => Regex.Replace(value, @"[^A-Za-z0-9_]", "_");

    private static void Assert(bool value, string message) => HeadlessHarness.Assert(value, message);

    private sealed record Fixture(
        string Root,
        string RoomPath,
        string ObjectPath,
        string CreateEventPath,
        string CreateEventSource,
        string BaseModelReference,
        string OverrideModelReference,
        string FirstNodeId,
        string SecondNodeId);
}
