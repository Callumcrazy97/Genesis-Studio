using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Diagnostics;
using Genesis.Application.Core.Editing;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Settings;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Dialogs;
using Genesis.Application.Editors;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Application.Editors.Suite.Objects.VisualActions;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Scripts;
using Genesis.Application.Studio.Docking;
using Genesis.Application.Studio.Editing;
using Genesis.Application.Studio.Resources;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Scripting;
using Genesis.Runtime.Scene;
using Genesis.Shared.Assets;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Regressions for the editor quality-of-life pass: viewport presentation, object art bindings,
/// honest sandbox reporting, focus-aware shortcuts and canvas-growing paste.
/// </summary>
/// <remarks>
/// Each case here failed before its fix and is written to fail again if the fix regresses, rather
/// than to assert that some code path merely runs.
/// </remarks>
internal static class QualityOfLifeSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.QoL");

        // One 2D-template project: the platformer is the fixture whose objects, images and scripts
        // these features are actually used against.
        string root = Path.Combine(ctx.Workspace, "QoL");
        Directory.CreateDirectory(root);
        ProjectSession? project = null;

        HeadlessHarness.RunCase(ctx.Report, "Editor.QoL.Fixture.PlatformerProject", () =>
        {
            project = new ProjectService().CreateProject(root, "QoL Platformer", "2D");
            HeadlessHarness.Assert(
                File.Exists(Path.Combine(project.AssetsPath, "Objects", "Coin.object.json")),
                "The 2D template did not produce the Coin object this suite tests against.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.QoL.TextureGroups.DefaultBackfillAndViewer", () =>
        {
            ProjectService projects = new();
            ProjectSession session = project
                ?? projects.CreateProject(Path.Combine(root, "groups"), "Texture Groups", "Blank");

            HeadlessHarness.Assert(
                TextureGroupCatalog.Names(session.Manifest).Contains(TextureGroupCatalog.DefaultName),
                "New projects must seed Default Texture Group.");

            string images = Path.Combine(session.AssetsPath, "Images");
            Directory.CreateDirectory(images);
            string orphanPath = Path.Combine(images, "Orphan.image.json");
            // Intentionally omit TextureGroup so open/backfill must write Default into the file.
            File.WriteAllText(orphanPath,
                """
                {
                  "schemaVersion": 2,
                  "canvas": { "width": 8, "height": 8, "pixelFormat": "Rgba8", "colorSpace": "Srgb" },
                  "import": { "mode": "Single", "preserveSource": true },
                  "usage": { "allowed": "Sprite" },
                  "origin": { "x": 0.5, "y": 0.5, "space": "Normalized" },
                  "frames": [],
                  "layers": []
                }
                """);

            ProjectSession reopened = projects.OpenProject(session.ProjectFile);
            string raw = File.ReadAllText(orphanPath);
            HeadlessHarness.Assert(
                raw.Contains("TextureGroup", StringComparison.OrdinalIgnoreCase)
                && raw.Contains(TextureGroupCatalog.DefaultName, StringComparison.Ordinal),
                "Opening a project did not write Default Texture Group into images missing the field.");

            HeadlessHarness.Assert(
                TextureGroupCatalog.TryAdd(reopened.Manifest, "UI", 4096, out string? addError) && addError == string.Empty,
                $"Could not add a custom Texture Group: {addError}");
            projects.Save(reopened);

            TextureGroupDeleteResult deleteDefault = TextureGroupCatalog.TryDelete(
                reopened, projects, TextureGroupCatalog.DefaultName);
            HeadlessHarness.Assert(
                !deleteDefault.Succeeded,
                "Default Texture Group must not be deletable.");

            ImageDocument document = ImageDocument.CreateDefault(8, 8);
            document.TextureGroup = "UI";
            ImageDocumentSession imageSession = new(document, orphanPath, ImageDocumentAccess.Editor);
            using ImageViewerControl viewer = new(imageSession);
            viewer.ListTextureGroups = () => TextureGroupCatalog.Names(reopened.Manifest);
            viewer.TryAddTextureGroup = (name, size) =>
                TextureGroupCatalog.TryAdd(reopened.Manifest, name, size, out string err) ? null : err;
            viewer.TryDeleteTextureGroup = name =>
            {
                TextureGroupDeleteResult result = TextureGroupCatalog.TryDelete(reopened, projects, name);
                return result.Succeeded ? null : result.Error;
            };
            viewer.RefreshFromDocument();
            HeadlessHarness.Assert(
                viewer.HasTextureGroupChrome,
                "Texture Group chrome was not built into the Image Viewer.");

            // Packer overflow: a second sheet is created instead of silent failure.
            Genesis.Rendering.Textures.TextureAtlasPacker packer = new();
            packer.Allocate("UI", "a", 2000, 2000, 2048, out _);
            Genesis.Rendering.Textures.TextureGroupAtlas sheet2 = packer.Allocate("UI", "b", 2000, 2000, 2048, out _);
            HeadlessHarness.Assert(
                packer.AtlasSheets["UI"].Count >= 2 && sheet2.GroupName.Contains('#'),
                "Atlas overflow did not create a second sheet for the Texture Group.");
        });

        RunViewportCases(ctx.Report);
        RunImageMetadataCases(ctx.Report, project);
        RunObjectArtCases(ctx.Report, project);
        RunObjectActionCases(ctx.Report, project);
        RunObjectLayoutCases(ctx.Report, project);
        RunInspectorCases(ctx.Report, project);
        RunSandboxCases(ctx.Report, project);
        RunShortcutCases(ctx.Report);
        RunTypingCase(ctx.Report, project);
        RunMenuOwnershipCase(ctx.Report, ctx, project);
        RunCanvasPasteCases(ctx.Report);
    }

    // ── Resource inspector ───────────────────────────────────────────────────

    private static void RunInspectorCases(TestReport report, ProjectSession? project)
    {
        HeadlessHarness.RunCase(report, "Editor.QoL.Inspector.NarrowActionsReflow", () =>
        {
            ResourceItem room = InspectorRoom(project);
            using Form host = GateSuite.NewHost();
            host.ClientSize = new Size(236, 760);
            using InspectorDock inspector = new()
            {
                Dock = DockStyle.Fill,
                FormBorderStyle = FormBorderStyle.None,
                TopLevel = false,
            };
            host.Controls.Add(inspector);
            GateSuite.ShowHost(host);
            inspector.Show();
            inspector.Inspect(room);
            GateSuite.Pump(6, 20);

            CheckInspectorGeometry(host, inspector, width: 223, expectedActionColumns: 1);
            CheckActionLayout(host, inspector, width: 340, expectedColumns: 2);
            host.Close();
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Inspector.GuidIsReadableAndCopyable", () =>
        {
            ResourceItem room = InspectorRoom(project);
            ProjectSession session = HeadlessHarness.Require(project, "QoL project");
            using Form host = GateSuite.NewHost();
            using InspectorDock inspector = new()
            {
                Dock = DockStyle.Fill,
                FormBorderStyle = FormBorderStyle.None,
                TopLevel = false,
            };
            host.Controls.Add(inspector);
            GateSuite.ShowHost(host);
            inspector.Show();
            inspector.Inspect(room);
            GateSuite.Pump(3, 20);

            TextBox guid = Descendants(inspector)
                .OfType<TextBox>()
                .Single(control => control.Name == "InspectorGuidValue");
            string expected = room.AssetId.ToString("N");
            HeadlessHarness.Assert(
                guid.ReadOnly && guid.Text == expected,
                "The full asset GUID is not exposed in a selectable, read-only value field.");

            ClipboardTestScope priorClipboard = ClipboardTestScope.Capture();
            try
            {
                string copiedGuid = WaitForClipboardText(
                    expected, () => FindButton(inspector, "Copy GUID").PerformClick());
                HeadlessHarness.Assert(
                    copiedGuid == expected,
                    "Copy GUID did not place the complete GUID on the clipboard; "
                    + $"clipboard contained '{copiedGuid}'.");

                ResourceItem assetsRoot = new ResourceService(session).BuildTree();
                inspector.Inspect(assetsRoot);
                string copiedRootPath = WaitForClipboardText(
                    "Assets", () => FindButton(inspector, "Copy path").PerformClick());
                HeadlessHarness.Assert(
                    copiedRootPath == "Assets",
                    "Copy path did not use the Inspector's displayed 'Assets' value for the root; "
                    + $"clipboard contained '{copiedRootPath}'.");
                HeadlessHarness.Assert(
                    !FindButton(inspector, "Copy GUID").Enabled,
                    "The Assets root offers a GUID copy action despite having no asset GUID.");
            }
            finally
            {
                priorClipboard.Dispose();
            }

            host.Close();
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Inspector.DensityControlsWholeLayout", () =>
        {
            ResourceItem room = InspectorRoom(project);
            try
            {
                using Form host = GateSuite.NewHost();
                host.ClientSize = new Size(340, 760);
                using InspectorDock inspector = new()
                {
                    Dock = DockStyle.Fill,
                    FormBorderStyle = FormBorderStyle.None,
                    TopLevel = false,
                };
                host.Controls.Add(inspector);
                GateSuite.ShowHost(host);
                inspector.Show();
                inspector.Inspect(room);

                List<(int Header, int Field, int Action, int Outer, int Inner, int Section)> metrics = [];
                foreach (string density in new[] { "Compact", "Comfortable", "Spacious" })
                {
                    GenesisSettings settings = new();
                    settings.Appearance.Density = density;
                    ThemeService.ApplySettings(settings);
                    GateSuite.Pump(3, 20);

                    Panel header = FindNamed<Panel>(inspector, "InspectorHeader");
                    Panel details = FindNamed<Panel>(inspector, "InspectorDetailsViewport");
                    TableLayoutPanel identity = FindNamed<TableLayoutPanel>(
                        inspector, "InspectorIdentityGrid");
                    Label heading = FindNamed<Label>(inspector, "InspectorResourceHeading");
                    int[] fields = identity.GetRowHeights();
                    Button[] actions = InspectorButtons(inspector);
                    HeadlessHarness.Assert(
                        fields.Length == 4
                        && fields[0] == fields[1]
                        && fields[2] == fields[3]
                        && fields[0] > fields[2],
                        $"{density} wide identity layout did not reserve taller rows for the "
                        + "selectable fields than for plain metadata. Rows="
                        + string.Join(",", fields));
                    HeadlessHarness.Assert(
                        actions.All(button => button.Height == actions[0].Height),
                        $"{density} applies different heights to Inspector actions.");
                    metrics.Add((header.Height, fields[0], actions[0].Height,
                        details.Padding.Left, identity.Padding.Left, heading.Margin.Top));
                }

                HeadlessHarness.Assert(
                    StrictlyIncreases(metrics.Select(value => value.Header))
                    && StrictlyIncreases(metrics.Select(value => value.Field))
                    && StrictlyIncreases(metrics.Select(value => value.Action))
                    && StrictlyIncreases(metrics.Select(value => value.Outer))
                    && StrictlyIncreases(metrics.Select(value => value.Inner))
                    && StrictlyIncreases(metrics.Select(value => value.Section)),
                    "Compact/Comfortable/Spacious does not progressively change the Inspector's "
                    + "header, fields, actions, outer/inner padding and section spacing: "
                    + string.Join("; ", metrics.Select((value, index) =>
                        $"{new[] { "Compact", "Comfortable", "Spacious" }[index]}="
                        + $"({value.Header},{value.Field},{value.Action},{value.Outer},"
                        + $"{value.Inner},{value.Section})")));
                host.Close();
            }
            finally
            {
                ThemeService.ApplySettings(new GenesisSettings());
            }
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Inspector.KindSummariesAreUseful", () =>
        {
            ProjectSession session = HeadlessHarness.Require(project, "QoL project");
            string fixtureDirectory = Path.Combine(session.AssetsPath, "InspectorFixtures");
            Directory.CreateDirectory(fixtureDirectory);

            const string shaderBody = "float4 SecretBodyMarker() : SV_Target {\n    return 1;\n}";
            string shaderPath = Path.Combine(fixtureDirectory, "Summary.shader.json");
            File.WriteAllText(shaderPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                pipeline = "Forward",
                entry = "MainPS",
                profile = "ps_5_0",
                source = shaderBody,
                parameters = new[] { "Tint" },
            }));

            string modelPath = Path.Combine(fixtureDirectory, "Summary.model.json");
            File.WriteAllText(modelPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                parts = new[] { new { name = "Body" }, new { name = "Head" } },
                animations = new[] { "Walk" },
                rigTemplate = "Humanoid",
            }));

            string malformedPath = Path.Combine(fixtureDirectory, "Broken.physics.json");
            File.WriteAllText(malformedPath, "{ this is not JSON");

            using InspectorDock inspector = new();
            Label summary = FindNamed<Label>(inspector, "InspectorKindSummary");
            var cases = new[]
            {
                new
                {
                    Item = InspectorRoom(project),
                    Includes = new[] { "1280", "720", "layer", "node" },
                    Excludes = Array.Empty<string>(),
                },
                new
                {
                    Item = InspectorResource(session, "Audio/Pickup.audio.json", ResourceKind.Audio),
                    Includes = new[] { "Assets/Audio/Pickup.wav", "Volume 0.7", "One shot", "Non-spatial" },
                    Excludes = Array.Empty<string>(),
                },
                new
                {
                    Item = InspectorResource(session, "Images/Player Idle.image.json", ResourceKind.Image),
                    Includes = new[] { "32", "px", "frame", "layer" },
                    Excludes = Array.Empty<string>(),
                },
                new
                {
                    Item = InspectorResource(session, "Objects/Player.object.json", ResourceKind.GameObject),
                    Includes = new[] { "TwoD", "3 components", "6 events", "Player Idle.image.json" },
                    Excludes = Array.Empty<string>(),
                },
                new
                {
                    Item = LooseResource(
                        session,
                        Path.Combine(session.AssetsPath, "Objects", "Player", "Create.pgsl"),
                        ResourceKind.PgslScript),
                    Includes = new[] { "line" },
                    Excludes = new[] { "Starting state" },
                },
                new
                {
                    Item = LooseResource(session, shaderPath, ResourceKind.Shader),
                    Includes = new[] { "Forward pipeline", "Entry MainPS", "Profile ps_5_0", "3 lines", "1 parameter" },
                    Excludes = new[] { "SecretBodyMarker", "return 1" },
                },
                new
                {
                    Item = LooseResource(session, modelPath, ResourceKind.Model),
                    Includes = new[] { "2 parts", "1 animation", "Rig Humanoid" },
                    Excludes = Array.Empty<string>(),
                },
                new
                {
                    Item = LooseResource(session, malformedPath, ResourceKind.Physics),
                    Includes = new[] { "Details unavailable" },
                    Excludes = Array.Empty<string>(),
                },
            };

            foreach (var testCase in cases)
            {
                inspector.Inspect(testCase.Item);
                foreach (string expected in testCase.Includes)
                {
                    HeadlessHarness.Assert(
                        summary.Text.Contains(expected, StringComparison.OrdinalIgnoreCase),
                        $"{testCase.Item.Kind} summary omitted '{expected}': {summary.Text}");
                }

                foreach (string forbidden in testCase.Excludes)
                {
                    HeadlessHarness.Assert(
                        !summary.Text.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                        $"{testCase.Item.Kind} summary exposed '{forbidden}': {summary.Text}");
                }
            }
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Inspector.TypedPropertiesAndCodeReflection", () =>
        {
            ProjectSession session = HeadlessHarness.Require(project, "QoL project");
            string fixtureDirectory = Path.Combine(session.AssetsPath, "InspectorProperties");
            Directory.CreateDirectory(fixtureDirectory);
            using InspectorDock inspector = new();

            string physicsPath = Path.Combine(fixtureDirectory, "Editable.physics.json");
            File.WriteAllText(physicsPath,
                """{"schemaVersion":1,"friction":0.5,"restitution":0.1,"density":1.0}""");
            inspector.Inspect(LooseResource(session, physicsPath, ResourceKind.Physics));
            HeadlessHarness.Assert(
                inspector.EditablePropertyGroups.Contains("Physics material", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyPaths.Contains("friction", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyPaths.Contains("restitution", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyPaths.Contains("density", StringComparer.OrdinalIgnoreCase),
                "A Physics resource did not expose its appropriate values in a logical material group.");
            HeadlessHarness.Assert(inspector.SetEditableValue("friction", 0.82),
                "The typed Physics friction field rejected an Inspector edit.");
            using (System.Text.Json.JsonDocument saved =
                   System.Text.Json.JsonDocument.Parse(File.ReadAllText(physicsPath)))
            {
                HeadlessHarness.Assert(
                    Math.Abs(saved.RootElement.GetProperty("friction").GetDouble() - 0.82) < 0.001,
                    "A generic JSON Inspector edit did not persist to the resource.");
            }

            string roomPath = InspectorRoom(project).FullPath;
            inspector.Inspect(LooseResource(session, roomPath, ResourceKind.Room));
            HeadlessHarness.Assert(
                inspector.EditablePropertyGroups.Contains("Room settings", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyPaths.Contains("settings.width", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyPaths.Any(path =>
                    path.StartsWith("layers[", StringComparison.OrdinalIgnoreCase)),
                "Nested Room settings and authored arrays are not available through collapsible Inspector groups.");

            const string shaderSource = """
                cbuffer GenesisParameters : register(b5) { float Speed; float3 Tint; int Steps; bool Enabled; };
                struct VSOut { float4 SvPos : SV_Position; float2 UV : TEXCOORD0; float4 Color : COLOR; float FogDepth : TEXCOORD1; };
                float4 MainPS(VSOut IN) : SV_Target { return float4(Tint * Speed, 1.0); }
                """;
            string shaderPath = Path.Combine(fixtureDirectory, "Live.shader.json");
            File.WriteAllText(shaderPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                schemaVersion = 3,
                pipeline = "Sprite",
                authoringMode = "Code",
                targetType = "Image",
                preset = "",
                targetComponent = "",
                entry = "MainPS",
                profile = "ps_5_0",
                source = shaderSource,
                previewAsset = "",
                parameters = new object[]
                {
                    new { name = "Speed", type = "float", value = new[] { 1f } },
                    new { name = "Tint", type = "float3", value = new[] { 1f, 1f, 1f } },
                    new { name = "Steps", type = "int", value = new[] { 3f } },
                    new { name = "Enabled", type = "bool", value = new[] { 1f } },
                },
            }));
            using ShaderEditorControl shaderEditor = new(shaderPath, session.RootPath);
            HeadlessHarness.Assert(
                Descendants(shaderEditor).OfType<CheckBox>().Any(toggle =>
                    toggle.Text is "Enabled" or "Disabled")
                && Descendants(shaderEditor).OfType<NumericUpDown>().Any(input =>
                    input.DecimalPlaces == 0),
                "The Shader Editor did not render bool and integer parameters with typed controls.");
            bool shaderRouted = false;
            int shaderInspectorRefreshes = 0;
            shaderEditor.InspectorStateChanged += (_, _) => shaderInspectorRefreshes++;
            inspector.EditRouter = request =>
            {
                bool handled = shaderEditor.TryApplyInspectorValue(request.PropertyPath, request.Value);
                shaderRouted |= handled;
                return handled;
            };
            inspector.LiveValueProvider = _ => shaderEditor.GetLiveInspectorValues();
            ResourceItem shaderResource = LooseResource(session, shaderPath, ResourceKind.Shader);
            inspector.Inspect(shaderResource);
            HeadlessHarness.Assert(
                inspector.EditablePropertyGroups.Contains("Authoring", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyGroups.Contains("Preview target", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyGroups.Contains("Resources", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyGroups.Contains("Compile status", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyGroups.Contains("Parameters", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyPaths.Contains("AuthoringMode", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyPaths.Contains("TargetType", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyPaths.Contains("Entry", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyPaths.Contains("Parameters.Speed", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyPaths.Contains("Parameters.Tint.X", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyPaths.Contains("Parameters.Tint.Z", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyPaths.Contains("Parameters.Steps", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyPaths.Contains("Parameters.Enabled", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyPaths.Count
                   == inspector.EditablePropertyPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                "The live Shader Inspector omitted authoring/target fields, duplicated them, or failed to reflect GenesisParameters b5.");
            HeadlessHarness.Assert(
                inspector.SetEditableValue("AuthoringMode", "Preset")
                && shaderEditor.AuthoringMode == ShaderAuthoringMode.Preset
                && inspector.SetEditableValue("AuthoringMode", "Code")
                && shaderEditor.AuthoringMode == ShaderAuthoringMode.Code,
                "Typed Shader authoring-mode edits did not route bidirectionally through the Inspector.");
            HeadlessHarness.Assert(
                inspector.SetEditableValue("TargetType", "Model")
                && shaderEditor.TargetType == ShaderTargetType.Model
                && shaderEditor.Pipeline == ShaderAssetPipeline.Mesh
                && inspector.SetEditableValue("TargetType", "Image")
                && shaderEditor.TargetType == ShaderTargetType.Image,
                "Typed Shader target edits did not synchronize pipeline and editor state.");
            HeadlessHarness.Assert(
                inspector.SetEditableValue("Parameters.Speed", 2.25f)
                && inspector.SetEditableValue("Parameters.Steps", 7)
                && inspector.SetEditableValue("Parameters.Enabled", false)
                && shaderRouted && shaderEditor.IsDirty && shaderInspectorRefreshes > 0,
                "Changing a Shader Inspector value did not live-route into the open Shader Editor.");
            ShaderAssetDocument diskBeforeShaderSave = ShaderAssetDocument.Load(shaderPath);
            HeadlessHarness.Assert(
                Math.Abs(diskBeforeShaderSave.Parameters.Single(value => value.Name == "Speed").Value[0] - 1f) < 0.001f,
                "The live Inspector route overwrote disk instead of preserving the open editor's save boundary.");
            shaderEditor.Save();
            ShaderAssetDocument diskAfterShaderSave = ShaderAssetDocument.Load(shaderPath);
            HeadlessHarness.Assert(
                Math.Abs(diskAfterShaderSave.Parameters.Single(value => value.Name == "Speed").Value[0] - 2.25f) < 0.001f
                && diskAfterShaderSave.Parameters.Single(value => value.Name == "Steps").Value[0] == 7f
                && diskAfterShaderSave.Parameters.Single(value => value.Name == "Enabled").Value[0] == 0f,
                "The Shader Editor did not retain and save typed Inspector-routed values.");

            string shaderConsumerPath = new ResourceService(session).CreateResource(
                fixtureDirectory,
                ResourceKind.GameObject,
                "Shader Consumer");
            string relativeShader = Path.GetRelativePath(session.RootPath, shaderPath).Replace('\\', '/');
            JObject objectDocument = JObject.Parse(File.ReadAllText(shaderConsumerPath));
            objectDocument["shader"] = relativeShader;
            objectDocument["shaderParameters"] = new JObject();
            objectDocument["components"] = new JArray(new JObject
            {
                ["type"] = "ShaderComponent",
                ["enabled"] = true,
                ["props"] = new JObject { ["Asset"] = relativeShader },
            });
            File.WriteAllText(shaderConsumerPath, objectDocument.ToString());
            using ObjectEditorControl shaderConsumerEditor = new(shaderConsumerPath, session.RootPath);
            inspector.EditRouter = request =>
                shaderConsumerEditor.TryApplyInspectorValue(request.PropertyPath, request.Value);
            inspector.LiveValueProvider = _ => shaderConsumerEditor.GetLiveInspectorValues();
            inspector.Inspect(LooseResource(session, shaderConsumerPath, ResourceKind.GameObject));
            HeadlessHarness.Assert(
                inspector.EditablePropertyGroups.Contains("Shader parameters", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyPaths.Contains(
                    "shaderParameters.Speed[0]",
                    StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyPaths.Contains(
                    "shaderParameters.Tint[2]",
                    StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyPaths.Contains(
                    "shaderParameters.Enabled[0]",
                    StringComparer.OrdinalIgnoreCase),
                "Object Inspector did not reflect typed overrides from its bound ShaderComponent.");
            HeadlessHarness.Assert(
                inspector.SetEditableValue("shaderParameters.Tint[1]", 0.35f)
                && inspector.SetEditableValue("shaderParameters.Enabled[0]", true)
                && shaderConsumerEditor.IsDirty,
                "Reflected ShaderComponent overrides did not route through the open Object Editor.");
            HeadlessHarness.Assert(
                shaderConsumerEditor.Document["shaderParameters"]?.HasValues == true,
                "The open Object Editor accepted Shader override edits but retained no values.");
            HeadlessHarness.Assert(
                !JObject.Parse(File.ReadAllText(shaderConsumerPath))["shaderParameters"]!.HasValues,
                "Object Inspector wrote Shader overrides to disk before the open editor saved.");
            shaderConsumerEditor.Save();
            JObject savedShaderConsumer = JObject.Parse(File.ReadAllText(shaderConsumerPath));
            HeadlessHarness.Assert(
                Math.Abs(((float?)savedShaderConsumer["shaderParameters"]?["Tint"]?[1] ?? 0f) - 0.35f) < 0.001f
                && (float?)savedShaderConsumer["shaderParameters"]?["Enabled"]?[0] == 1f,
                "Typed ShaderComponent overrides did not survive Object save.");

            inspector.LiveValueProvider = null;
            string scriptPath = Path.Combine(fixtureDirectory, "Inspectable.pgsl");
            File.WriteAllText(scriptPath,
                "var moveSpeed = 3.5;\nvar godMode = false;\nevent Step {\n    var privateCounter = 4;\n}\n");
            using PgslScriptEditorControl scriptEditor = new(scriptPath, session.RootPath);
            bool scriptRouted = false;
            inspector.EditRouter = request =>
            {
                bool handled = scriptEditor.TryApplyInspectorValue(request.PropertyPath, request.Value);
                scriptRouted |= handled;
                return handled;
            };
            inspector.Inspect(LooseResource(session, scriptPath, ResourceKind.PgslScript));
            HeadlessHarness.Assert(
                inspector.EditablePropertyGroups.Contains(
                    "Inspectable · SCRIPT VARIABLES", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyPaths.Contains("Variables.moveSpeed", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyPaths.Contains("Variables.godMode", StringComparer.OrdinalIgnoreCase)
                && !inspector.EditablePropertyPaths.Contains("Variables.privateCounter", StringComparer.OrdinalIgnoreCase),
                "PGSL did not group file-scope variables under their script while keeping event-local variables private.");
            HeadlessHarness.Assert(
                inspector.SetEditableValue("Variables.moveSpeed", 7.75)
                && scriptRouted && scriptEditor.IsDirty
                && scriptEditor.ScriptText.Contains("var moveSpeed = 7.75;", StringComparison.Ordinal),
                "Changing a PGSL Inspector variable did not update the open Script Editor live.");
            scriptEditor.Save();
            HeadlessHarness.Assert(
                File.ReadAllText(scriptPath).Contains("var moveSpeed = 7.75;", StringComparison.Ordinal),
                "The live PGSL Inspector value did not survive the Script Editor save.");

            string objectPath = Path.Combine(fixtureDirectory, "Inspectable.object.json");
            File.WriteAllText(objectPath,
                """
                {
                  "schemaVersion": 3,
                  "name": "Inspectable",
                  "dimension": "TwoD",
                  "depth": 0,
                  "sprite": "Assets/Images/Hero.image.json",
                  "physics": "Dynamic",
                  "events": ["Create"],
                  "components": [
                    {
                      "id": "cmp-image",
                      "type": "SpriteComponent",
                      "enabled": true,
                      "props": {
                        "Sprite": "Assets/Images/Hero.image.json",
                        "ImageSpeed": 1.0,
                        "Alpha": 0.75,
                        "Depth": 0
                      }
                    },
                    {
                      "id": "cmp-physics",
                      "type": "PhysicsComponent",
                      "enabled": true,
                      "props": {
                        "Preset": "Dynamic",
                        "Gravity": 0.0,
                        "GravityDirection": 270.0,
                        "Friction": 0.4,
                        "Solid": true
                      }
                    }
                  ]
                }
                """);
            ObjectEventStore.Save(objectPath, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Create"] = "var spawnCount = 2;\n",
            });
            using ObjectEditorControl objectEditor = new(objectPath, session.RootPath);
            bool objectRouted = false;
            inspector.EditRouter = request =>
            {
                bool handled = objectEditor.TryApplyInspectorValue(request.PropertyPath, request.Value);
                objectRouted |= handled;
                return handled;
            };
            inspector.LiveValueProvider = _ => objectEditor.GetLiveInspectorValues();
            inspector.Inspect(LooseResource(session, objectPath, ResourceKind.GameObject));
            HeadlessHarness.Assert(
                inspector.EditablePropertyGroups.Contains("Image / Animated Sprite", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyGroups.Contains("Physics", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyGroups.Contains("CREATE EVENT", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyPaths.Contains("Events.Create.Variables.spawnCount", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyPaths.Contains("components[0].props.Alpha", StringComparer.OrdinalIgnoreCase)
                && !inspector.EditablePropertyPaths.Contains("components[0].id", StringComparer.OrdinalIgnoreCase)
                && !inspector.EditablePropertyPaths.Contains("sprite", StringComparer.OrdinalIgnoreCase),
                "Object components were not separated into authored sections, or implementation mirrors leaked into the Inspector.");
            ComboBox physicsPreset = FindNamed<ComboBox>(
                inspector,
                "InspectorProperty_components_1__props_Preset");
            HeadlessHarness.Assert(
                physicsPreset.Items.Cast<object>().Any(item => item.ToString() == "PlatformerCharacter")
                && physicsPreset.Items.Cast<object>().Any(item => item.ToString() == "StaticSolid"),
                "The Object Physics preset was exposed as raw text instead of a constrained preset choice.");
            HeadlessHarness.Assert(
                inspector.SetEditableValue("Events.Create.Variables.spawnCount", 5)
                && objectRouted
                && objectEditor.PgslEvents["Create"].Contains("var spawnCount = 5;", StringComparison.Ordinal),
                "An authored Create-event variable did not live-route into the open Object Editor.");
            objectRouted = false;
            HeadlessHarness.Assert(
                inspector.SetEditableValue("components[0].props.Alpha", 0.25)
                && objectRouted && objectEditor.IsDirty
                && Math.Abs(objectEditor.Document.SelectToken("components[0].props.Alpha")!.Value<double>() - 0.25) < 0.001,
                "Changing an Object component value did not live-route into the open Object Editor.");
            HeadlessHarness.Assert(
                File.ReadAllText(objectPath).Contains("\"Alpha\": 0.75", StringComparison.Ordinal),
                "The live Object Inspector route overwrote disk before the Object Editor was saved.");
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Inspector.RoomAndImageStayLive", () =>
        {
            ProjectSession session = HeadlessHarness.Require(project, "QoL project");
            string fixtureDirectory = Path.Combine(session.AssetsPath, "InspectorLiveEditors");
            Directory.CreateDirectory(fixtureDirectory);

            string roomPath = Path.Combine(fixtureDirectory, "Live.room.json");
            RoomAsset room = RoomAsset.Create("Live Inspector Room", RoomDimension.ThreeD);
            RoomNode campfire = new()
            {
                Name = "Campfire",
                Kind = RoomNodeKind.GameObject,
                LayerId = room.Layers[0].Id,
                GameObject = new RoomGameObjectData
                {
                    Prefab = "Assets/Objects/Campfire.object.json",
                    ComponentOverrides =
                    [
                        new RoomComponentOverride
                        {
                            ComponentId = "LightComponent",
                            Properties = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["Intensity"] = 1.5f,
                            },
                        },
                    ],
                },
            };
            room.Nodes.Add(campfire);
            RoomAssetLoader.Save(room, roomPath);

            using RoomEditorControl roomEditor = new(roomPath, session.RootPath);
            roomEditor.Select(roomEditor.Room.Nodes.Single());
            using Form roomHost = GateSuite.NewHost();
            using InspectorDock roomInspector = new()
            {
                Dock = DockStyle.Fill,
                FormBorderStyle = FormBorderStyle.None,
                TopLevel = false,
                LiveValueProvider = _ => roomEditor.GetLiveInspectorValues(),
                EditRouter = request => roomEditor.TryApplyInspectorValue(request.PropertyPath, request.Value),
            };
            roomHost.Controls.Add(roomInspector);
            GateSuite.ShowHost(roomHost);
            roomInspector.Show();
            ResourceItem roomResource = LooseResource(session, roomPath, ResourceKind.Room);
            roomInspector.Inspect(roomResource);
            GateSuite.Pump(3, 20);
            HeadlessHarness.Assert(
                roomInspector.EditablePropertyGroups.Contains("Room settings", StringComparer.OrdinalIgnoreCase)
                && roomInspector.EditablePropertyGroups.Contains("Environment audio", StringComparer.OrdinalIgnoreCase)
                && roomInspector.EditablePropertyGroups.Contains("Viewport 1", StringComparer.OrdinalIgnoreCase)
                && roomInspector.EditablePropertyGroups.Contains("Campfire · Instance", StringComparer.OrdinalIgnoreCase)
                && roomInspector.EditablePropertyGroups.Contains("Campfire · Transform", StringComparer.OrdinalIgnoreCase)
                && roomInspector.EditablePropertyGroups.Contains("Campfire · Light Component", StringComparer.OrdinalIgnoreCase)
                && !roomInspector.EditablePropertyPaths.Any(path =>
                    path.StartsWith("nodes[", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("layers[", StringComparison.OrdinalIgnoreCase)),
                "The live Room Inspector omitted authored groups or leaked raw Room arrays.");

            roomInspector.PropertyFilterText = "follow target";
            GateSuite.Pump(2, 20);
            HeadlessHarness.Assert(
                FindNamed<TableLayoutPanel>(roomInspector, "InspectorGroupViewport1").Visible
                && !FindNamed<TableLayoutPanel>(roomInspector, "InspectorGroupRoomsettings").Visible,
                "Inspector filtering did not isolate a matching property inside a long Room stack.");
            roomInspector.PropertyFilterText = string.Empty;

            HeadlessHarness.Assert(
                roomInspector.SetEditableValue("settings.width", 1920)
                && roomInspector.SetEditableValue("Selection.Transform.Position.X", 42.5f)
                && roomEditor.IsDirty
                && roomEditor.Room.Settings.Width == 1920
                && Math.Abs(roomEditor.Room.Nodes[0].Transform.X - 42.5f) < 0.001f,
                "Room Inspector values did not route into the open Room Editor.");
            RoomAsset roomOnDiskBeforeSave = RoomAssetLoader.Parse(roomPath);
            HeadlessHarness.Assert(
                roomOnDiskBeforeSave.Settings.Width != 1920
                && Math.Abs(roomOnDiskBeforeSave.Nodes[0].Transform.X - 42.5f) > 0.001f,
                "Room Inspector bypassed the Room Editor save boundary.");
            roomEditor.Save();
            RoomAsset savedRoom = RoomAssetLoader.Parse(roomPath);
            HeadlessHarness.Assert(
                savedRoom.Settings.Width == 1920
                && Math.Abs(savedRoom.Nodes[0].Transform.X - 42.5f) < 0.001f,
                "Room Inspector edits did not survive the Room Editor save.");
            roomHost.Close();

            string imagePath = Path.Combine(fixtureDirectory, "Live.image.json");
            ImageDocument image = ImageDocument.CreateDefault(32, 24);
            ImageDocumentSerializer.SaveAtomic(imagePath, image);
            ResourceItem imageResource = LooseResource(session, imagePath, ResourceKind.Image);
            using ImageViewerDocument imageViewer = new(imageResource, new StudioLog());
            using InspectorDock imageInspector = new()
            {
                LiveValueProvider = _ => imageViewer.GetLiveInspectorValues(),
                EditRouter = request => imageViewer.TryApplyInspectorValue(request.PropertyPath, request.Value),
            };
            imageInspector.Inspect(imageResource);
            HeadlessHarness.Assert(
                imageInspector.EditablePropertyGroups.Contains("Image usage", StringComparer.OrdinalIgnoreCase)
                && imageInspector.EditablePropertyGroups.Contains("Source & import", StringComparer.OrdinalIgnoreCase)
                && imageInspector.EditablePropertyGroups.Contains("Sprite rendering", StringComparer.OrdinalIgnoreCase)
                && imageInspector.EditablePropertyGroups.Any(group => group.EndsWith("· Frame", StringComparison.OrdinalIgnoreCase))
                && imageInspector.EditablePropertyGroups.Any(group => group.EndsWith("· Layer", StringComparison.OrdinalIgnoreCase))
                && imageInspector.EditablePropertyGroups.Contains("Canvas", StringComparer.OrdinalIgnoreCase)
                && !imageInspector.EditablePropertyPaths.Any(path =>
                    path.StartsWith("attachments[", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("tracks[", StringComparison.OrdinalIgnoreCase)),
                "The live Image Inspector omitted its authored groups or leaked implementation arrays.");
            HeadlessHarness.Assert(
                imageInspector.SetEditableValue("Usage.Texture", true)
                && imageInspector.SetEditableValue("origin.x", 12.25)
                && imageViewer.IsDirty
                && imageViewer.Session.Document.Usage.Supports(ImageUsage.Texture)
                && Math.Abs(imageViewer.Session.Document.Origin.X - 12.25) < 0.001,
                "Image Inspector values did not route into the shared Image session.");
            ImageDocument imageOnDiskBeforeSave = ImageDocumentSerializer.LoadAtomic(imagePath).Document;
            HeadlessHarness.Assert(
                !imageOnDiskBeforeSave.Usage.Supports(ImageUsage.Texture)
                && Math.Abs(imageOnDiskBeforeSave.Origin.X - 12.25) > 0.001,
                "Image Inspector bypassed the Image Viewer save boundary.");
            imageViewer.Save();
            ImageDocument savedImage = ImageDocumentSerializer.LoadAtomic(imagePath).Document;
            HeadlessHarness.Assert(
                savedImage.Usage.Supports(ImageUsage.Texture)
                && Math.Abs(savedImage.Origin.X - 12.25) < 0.001,
                "Image Inspector edits did not survive the shared Image save.");
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Inspector.SharedDrawersAndAssetPicker", () =>
        {
            object? changed = null;
            using Control ranged = PropertyDrawerRegistry.CreateControl(new PropertyDrawerContext(
                "Opacity",
                typeof(float),
                0.5f,
                value => changed = value,
                Minimum: 0m,
                Maximum: 1m,
                Increment: 0.01m,
                DecimalPlaces: 2));
            HeadlessHarness.Assert(
                ranged is TableLayoutPanel
                && Descendants(ranged).OfType<TrackBar>().Count() == 1
                && Descendants(ranged).OfType<NumericUpDown>().Count() == 1,
                "A ranged value did not resolve through the shared slider/input drawer.");

            using Control choices = PropertyDrawerRegistry.CreateControl(new PropertyDrawerContext(
                "Blend mode",
                typeof(string),
                "Alpha",
                value => changed = value,
                Choices: ["Alpha", "Additive", "Multiply"]));
            HeadlessHarness.Assert(
                choices is ComboBox combo && combo.Items.Count == 3,
                "A declared choice list did not resolve through the shared dropdown drawer.");

            string projectRoot = HeadlessHarness.Require(project, "QoL project").RootPath;
            ProjectAssetEntry[] assets =
            [
                new("Oak Tree", Path.Combine(projectRoot, "Assets", "Models", "Oak.model.json"),
                    "Assets/Models/Oak.model.json", ResourceKind.Model),
                new("Pine Tree", Path.Combine(projectRoot, "Assets", "Nature", "Pine.model.json"),
                    "Assets/Nature/Pine.model.json", ResourceKind.Model),
                new("Oak Table", Path.Combine(projectRoot, "Assets", "Furniture", "Oak Table.model.json"),
                    "Assets/Furniture/Oak Table.model.json", ResourceKind.Model),
            ];
            using AssetPickerModal picker = new(
                new AssetPickerRequest(projectRoot, ResourceKind.Model),
                assets,
                ["Assets/Nature/Pine.model.json"]);
            picker.SetFilter("furn oak");
            HeadlessHarness.Assert(
                picker.FilteredAssets.Count == 1
                && picker.FilteredAssets[0].DisplayName == "Oak Table",
                "The shared picker did not fuzzy-filter across both folder and asset name.");
            GC.KeepAlive(changed);
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Inspector.StaleAndOversizedFilesStayBounded", () =>
        {
            ProjectSession session = HeadlessHarness.Require(project, "QoL project");
            string fixtureDirectory = Path.Combine(session.AssetsPath, "InspectorBounds");
            Directory.CreateDirectory(fixtureDirectory);

            using InspectorDock inspector = new();
            Label summary = FindNamed<Label>(inspector, "InspectorKindSummary");
            Label modified = FindNamed<Label>(inspector, "InspectorModifiedValue");
            Label size = FindNamed<Label>(inspector, "InspectorSizeValue");

            string missingPath = Path.Combine(fixtureDirectory, "Missing.md");
            inspector.Inspect(LooseResource(session, missingPath, ResourceKind.Note));
            HeadlessHarness.Assert(
                modified.Text == "Unavailable" && size.Text == "Unavailable",
                $"A stale row reported Modified='{modified.Text}', Size='{size.Text}' instead of Unavailable.");
            HeadlessHarness.Assert(
                summary.Text.Contains("Details unavailable", StringComparison.OrdinalIgnoreCase),
                $"A stale row did not degrade to an unavailable summary: {summary.Text}");

            string largeNote = Path.Combine(fixtureDirectory, "Large.md");
            SetSparseLength(largeNote, (1024 * 1024) + 1);
            inspector.Inspect(LooseResource(session, largeNote, ResourceKind.Note));
            HeadlessHarness.Assert(
                summary.Text.Contains("1 MB text summary limit", StringComparison.OrdinalIgnoreCase),
                $"Oversized text was read or reported unclearly: {summary.Text}");

            string largeShader = Path.Combine(fixtureDirectory, "Large.shader.json");
            SetSparseLength(largeShader, (4 * 1024 * 1024) + 1);
            inspector.Inspect(LooseResource(session, largeShader, ResourceKind.Shader));
            HeadlessHarness.Assert(
                summary.Text.Contains("4 MB JSON summary limit", StringComparison.OrdinalIgnoreCase),
                $"Oversized JSON was read or reported unclearly: {summary.Text}");

            string imagePath = Path.Combine(fixtureDirectory, "Large.image.json");
            string sourcePath = Path.Combine(fixtureDirectory, "Large.png");
            // RoomEditor discovers every image resource when it builds the tile palette. Keep this
            // oversized associate attached to a valid document so a robustness fixture cannot
            // poison later editors merely by living under Assets.
            ImageDocumentSerializer.SaveAtomic(imagePath, ImageDocument.CreateDefault(1, 1));
            SetSparseLength(sourcePath, (32 * 1024 * 1024) + 1);
            inspector.Inspect(LooseResource(session, imagePath, ResourceKind.Image));
            PictureBox thumbnail = FindNamed<PictureBox>(inspector, "InspectorThumbnail");
            HeadlessHarness.Assert(
                !thumbnail.Visible && thumbnail.Image is null,
                "An oversized thumbnail source was synchronously decoded instead of staying hidden.");
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Inspector.ObjectPreviewRejectsTraversal", () =>
        {
            ProjectSession session = HeadlessHarness.Require(project, "QoL project");
            string outsideDirectory = Directory.GetParent(session.RootPath)?.FullName
                ?? throw new InvalidOperationException("The QoL project has no parent directory.");
            string outsideDocument = Path.Combine(outsideDirectory, "InspectorOutside.image.json");
            string outsideImage = Path.Combine(outsideDirectory, "InspectorOutside.png");
            using (Bitmap bitmap = new(2, 2))
            {
                bitmap.Save(outsideImage, System.Drawing.Imaging.ImageFormat.Png);
            }
            File.WriteAllText(outsideDocument, "{\"source\":\"InspectorOutside.png\"}");

            string objectPath = Path.Combine(session.AssetsPath, "Objects", "Traversal.object.json");
            string escaped = Path.GetRelativePath(session.RootPath, outsideDocument)
                .Replace(Path.DirectorySeparatorChar, '/');
            File.WriteAllText(
                objectPath,
                System.Text.Json.JsonSerializer.Serialize(new { sprite = escaped }));

            HeadlessHarness.Assert(
                ObjectResourceReader.ResolveImageDocument(objectPath, session.RootPath) is null,
                "An object sprite binding escaped the project's Assets root.");
            string player = Path.Combine(session.AssetsPath, "Objects", "Player.object.json");
            HeadlessHarness.Assert(
                ObjectResourceReader.ResolveImageDocument(player, session.RootPath) is not null,
                "Traversal containment also rejected a valid in-Assets object binding.");

            using InspectorDock inspector = new();
            inspector.Inspect(LooseResource(session, objectPath, ResourceKind.GameObject));
            PictureBox thumbnail = FindNamed<PictureBox>(inspector, "InspectorThumbnail");
            HeadlessHarness.Assert(
                !thumbnail.Visible && thumbnail.Image is null,
                "Inspector preview loaded an image through an Assets traversal binding.");
        });
    }

    private static ResourceItem InspectorRoom(ProjectSession? project)
    {
        ProjectSession session = HeadlessHarness.Require(project, "QoL project");
        string path = Path.Combine(session.AssetsPath, "Rooms", "Level 1.room.json");
        return FindResource(new ResourceService(session).BuildTree(), path)
            ?? throw new InvalidOperationException("The platformer fixture has no Level 1 room.");
    }

    private static void CheckActionLayout(
        Form host,
        InspectorDock inspector,
        int width,
        int expectedColumns)
    {
        host.ClientSize = new Size(width, 760);
        GateSuite.Pump(4, 20);

        Panel viewport = FindNamed<Panel>(inspector, "InspectorDetailsViewport");
        TableLayoutPanel actions = FindNamed<TableLayoutPanel>(inspector, "InspectorActions");
        Button[] buttons = InspectorButtons(inspector);
        Rectangle[] bounds = buttons
            .Select(button => viewport.RectangleToClient(
                button.RectangleToScreen(button.ClientRectangle)))
            .ToArray();

        HeadlessHarness.Assert(
            actions.ColumnCount == expectedColumns,
            $"A {viewport.ClientSize.Width}px details viewport used {actions.ColumnCount} "
            + $"action columns instead of {expectedColumns}.");

        Rectangle client = viewport.ClientRectangle;
        for (int index = 0; index < buttons.Length; index++)
        {
            Button button = buttons[index];
            Rectangle rectangle = bounds[index];
            HeadlessHarness.Assert(
                rectangle.Left >= client.Left && rectangle.Right <= client.Right,
                $"'{button.Text}' escaped the Inspector horizontally: button={rectangle}, "
                + $"viewport={client}. The longer editable-property document may scroll vertically.");

            Size caption = TextRenderer.MeasureText(
                button.Text,
                button.Font,
                Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            HeadlessHarness.Assert(
                caption.Width <= button.ClientSize.Width - 4
                && caption.Height <= button.ClientSize.Height - 2,
                $"'{button.Text}' is clipped at {button.ClientSize.Width}x{button.ClientSize.Height}; "
                + $"its caption requires {caption.Width}x{caption.Height}.");
        }

        for (int first = 0; first < bounds.Length; first++)
        {
            for (int second = first + 1; second < bounds.Length; second++)
            {
                HeadlessHarness.Assert(
                    !bounds[first].IntersectsWith(bounds[second]),
                    $"'{buttons[first].Text}' overlaps '{buttons[second].Text}' at width {width}.");
            }
        }

        if (expectedColumns == 1)
        {
            HeadlessHarness.Assert(
                bounds.Select(rectangle => rectangle.Left).Distinct().Count() == 1
                && bounds[0].Top < bounds[1].Top
                && bounds[1].Top < bounds[2].Top,
                "The narrow action layout did not stack all three buttons in one column.");
        }
        else
        {
            HeadlessHarness.Assert(
                bounds[0].Top == bounds[1].Top
                && bounds[0].Left < bounds[1].Left
                && bounds[2].Top > bounds[0].Top,
                "The wide action layout did not place two actions above the spanning GUID action.");
        }
    }

    private static void CheckInspectorGeometry(
        Form host,
        InspectorDock inspector,
        int width,
        int expectedActionColumns)
    {
        host.ClientSize = new Size(width, 500);
        GateSuite.Pump(4, 20);

        Panel viewport = FindNamed<Panel>(inspector, "InspectorDetailsViewport");
        TableLayoutPanel content = FindNamed<TableLayoutPanel>(inspector, "InspectorContent");
        Label propertiesHeading = FindNamed<Label>(inspector, "InspectorPropertiesHeading");
        Panel propertySurface = FindNamed<Panel>(inspector, "InspectorPropertySurface");
        TableLayoutPanel identity = FindNamed<TableLayoutPanel>(inspector, "InspectorIdentityGrid");
        Label identityHeading = FindNamed<Label>(inspector, "InspectorIdentityHeading");
        Label resourceHeading = FindNamed<Label>(inspector, "InspectorResourceHeading");
        Label actionsHeading = FindNamed<Label>(inspector, "InspectorActionsHeading");
        Label modifiedCaption = FindNamed<Label>(inspector, "InspectorModifiedCaption");
        TextBox path = FindNamed<TextBox>(inspector, "InspectorPathValue");
        TextBox guid = FindNamed<TextBox>(inspector, "InspectorGuidValue");
        int[] rows = identity.GetRowHeights();

        HeadlessHarness.Assert(
            viewport.VerticalScroll.Visible && !viewport.HorizontalScroll.Visible,
            "A short narrow Inspector did not stay inside its actual vertical-scroll viewport.");
        HeadlessHarness.Assert(
            content.Width == viewport.ClientSize.Width - viewport.Padding.Horizontal,
            $"Inspector content is {content.Width}px wide inside a "
            + $"{viewport.ClientSize.Width}px viewport with {viewport.Padding.Horizontal}px padding.");
        HeadlessHarness.Assert(
            propertiesHeading.Top == 0,
            $"The first Inspector heading starts at y={propertiesHeading.Top}; inter-section spacing "
            + "was incorrectly applied above the first section.");

        int expectedSectionGap = resourceHeading.Margin.Top;
        HeadlessHarness.Assert(
            expectedSectionGap > 0
            && identityHeading.Margin.Top == expectedSectionGap
            && actionsHeading.Margin.Top == expectedSectionGap,
            "Inspector section headings do not use one consistent inter-section gap.");
        HeadlessHarness.Assert(
            identityHeading.Top - propertySurface.Bottom == expectedSectionGap,
            "Editable properties and Identity contain an unexpected blank band between sections.");
        HeadlessHarness.Assert(
            resourceHeading.Top - identity.Bottom == expectedSectionGap,
            "Identity and Resource details contain an unexpected blank band between sections.");

        Size captionSize = TextRenderer.MeasureText(
            modifiedCaption.Text,
            modifiedCaption.Font,
            Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        HeadlessHarness.Assert(
            modifiedCaption.ClientSize.Width >= captionSize.Width,
            $"'Modified' is clipped at {modifiedCaption.ClientSize.Width}px; it requires "
            + $"{captionSize.Width}px at the active font/DPI.");
        HeadlessHarness.Assert(
            path.Width >= content.ClientSize.Width * 0.75
            && guid.Width == path.Width,
            $"Selectable identity fields are narrow or misaligned: path={path.Width}, "
            + $"guid={guid.Width}, content={content.ClientSize.Width}.");
        HeadlessHarness.Assert(
            rows.Length is 6 or 8
            && rows[0] < rows[1]
            && rows[2] < rows[3]
            && rows[4] < rows[5],
            "Narrow identity captions and values do not use compact, aligned rows.");

        CheckActionLayout(host, inspector, width, expectedActionColumns);
    }

    private static Button FindButton(Control root, string text) =>
        Descendants(root)
            .OfType<Button>()
            .Single(button => button.Text.Equals(text, StringComparison.Ordinal));

    private static Button[] InspectorButtons(Control root) =>
    [
        FindButton(root, "Reveal"),
        FindButton(root, "Copy path"),
        FindButton(root, "Copy GUID"),
    ];

    private static T FindNamed<T>(Control root, string name) where T : Control =>
        Descendants(root)
            .Prepend(root)
            .OfType<T>()
            .Single(control => control.Name.Equals(name, StringComparison.Ordinal));

    private static bool StrictlyIncreases(IEnumerable<int> values)
    {
        int[] sequence = values.ToArray();
        return sequence.Length > 1
               && sequence.Zip(sequence.Skip(1), (first, second) => first < second).All(result => result);
    }

    private static ResourceItem InspectorResource(
        ProjectSession session,
        string relativePath,
        ResourceKind expectedKind)
    {
        string path = Path.Combine(
            session.AssetsPath,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        ResourceItem item = FindResource(new ResourceService(session).BuildTree(), path)
            ?? throw new InvalidOperationException($"Inspector fixture is absent: {relativePath}");
        HeadlessHarness.Assert(
            item.Kind == expectedKind,
            $"Inspector fixture {relativePath} is {item.Kind}, expected {expectedKind}.");
        return item;
    }

    private static ResourceItem LooseResource(
        ProjectSession session,
        string path,
        ResourceKind kind) =>
        new()
        {
            Name = Path.GetFileName(path),
            FullPath = path,
            RelativePath = Path.GetRelativePath(session.AssetsPath, path).Replace('\\', '/'),
            Kind = kind,
            IsFolder = false,
            AssetId = Guid.NewGuid(),
        };

    private static void SetSparseLength(string path, long length)
    {
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.SetLength(length);
    }

    private static void WithClipboardRetry(Action action)
    {
        // Windows shares the clipboard with every desktop process. A brief external lock
        // should delay the real copy/read action, without changing its success assertion.
        // Match the gate's bounded backoff instead of treating a single desktop ownership
        // transition as a failed copy/paste. The final exception still fails the case; this
        // helper never replaces the system clipboard with an in-process test clipboard.
        for (int attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (System.Runtime.InteropServices.ExternalException) when (attempt < 11)
            {
                GateSuite.Pump(1, 50 + (attempt * 25));
            }
        }
    }

    private static string WaitForClipboardText(string expected, Action copy)
    {
        string actual = string.Empty;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            WithClipboardRetry(() =>
            {
                copy();
                actual = Clipboard.GetText();
            });
            if (string.Equals(actual, expected, StringComparison.Ordinal))
            {
                return actual;
            }

            Thread.Sleep(20 + (attempt * 5));
        }

        return actual;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    // ── Viewport presentation ───────────────────────────────────────────────────

    private static void RunViewportCases(TestReport report)
    {
        HeadlessHarness.RunCase(report, "Editor.QoL.Viewport.SwapChainFollowsClientSize", () =>
        {
            // The bug: Silk.NET's ComPtr AddRefs what it wraps, so the swap chain's render-target
            // view survived Dispose and held back buffer 0. Every ResizeBuffers then returned
            // DXGI_ERROR_INVALID_CALL for the life of the control, and DXGI stretched a stale back
            // buffer over the pane — which is why a docked Image Viewer showed a smeared band of
            // one pixel column instead of the sheet.
            using Form host = new()
            {
                ClientSize = new Size(420, 320),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(20, 20),
                ShowInTaskbar = false,
            };
            ImageViewportControl viewport = new();
            host.Controls.Add(viewport);
            GateSuite.ShowHost(host);
            Pump(6, 30);

            HeadlessHarness.Assert(
                viewport.RenderWidth == viewport.ClientWidth && viewport.RenderHeight == viewport.ClientHeight,
                $"Swap chain started at {viewport.RenderWidth}x{viewport.RenderHeight} for a "
                + $"{viewport.ClientWidth}x{viewport.ClientHeight} control.");

            host.ClientSize = new Size(300, 240);
            Pump(10, 30);

            HeadlessHarness.Assert(
                viewport.RenderWidth == viewport.ClientWidth && viewport.RenderHeight == viewport.ClientHeight,
                $"Swap chain stayed {viewport.RenderWidth}x{viewport.RenderHeight} after the control "
                + $"became {viewport.ClientWidth}x{viewport.ClientHeight}; the presented frame is "
                + "being stretched, not rendered.");
            host.Close();
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Viewport.FitUsesLiveClientSize", () =>
        {
            // FitToView read the swap chain's size, which lags the control. A viewport whose first
            // layout pass was degenerate (a docked document mid-layout) stayed fitted for that
            // degenerate size and rendered the image at 1:1 in a large pane.
            using Form host = new()
            {
                ClientSize = new Size(200, 200),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(20, 20),
                ShowInTaskbar = false,
            };
            ImageViewportControl viewport = new();
            host.Controls.Add(viewport);
            GateSuite.ShowHost(host);
            viewport.SetSurface(new byte[32 * 32 * 4], 32, 32, 1);
            Pump(6, 30);
            viewport.FitToView();
            float small = viewport.Zoom;

            host.ClientSize = new Size(700, 700);
            Pump(8, 30);
            viewport.FitToView();

            HeadlessHarness.Assert(
                viewport.Zoom > small,
                $"Fit produced zoom {viewport.Zoom} in a 700px pane after {small} in a 200px pane — "
                + "the fit is being computed against a stale size.");
            host.Close();
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Viewport.HighZoomKeepsArtworkVisible", () =>
        {
            // Unclipped transparency checkers used one DrawRect per 16px cell across the entire
            // zoomed destination. Past ~300% on large sheets that exceeded the 32k sprite instance
            // cap and the artwork sprite was dropped from the flush, leaving only the checkerboard.
            const int width = 512;
            const int height = 512;
            byte[] pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;     // R
                pixels[i + 1] = 32;  // G
                pixels[i + 2] = 200; // B
                pixels[i + 3] = 255; // A
            }

            using Form host = new()
            {
                ClientSize = new Size(420, 320),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(20, 20),
                ShowInTaskbar = false,
            };
            ImageViewportControl viewport = new();
            host.Controls.Add(viewport);
            GateSuite.ShowHost(host);
            viewport.SetSurface(pixels, width, height, 1);
            viewport.ActualPixels();
            Pump(4, 30);

            Point anchor = new(viewport.ClientWidth / 2, viewport.ClientHeight / 2);
            while (viewport.Zoom < 8f)
                viewport.ZoomAt(anchor, zoomIn: true);
            Pump(6, 30);

            using Bitmap frame = viewport.ReadbackFrameToBitmap(settleFrames: 4);
            Color sample = frame.GetPixel(frame.Width / 2, frame.Height / 2);
            HeadlessHarness.Assert(
                sample.R > 180 && sample.B > 140,
                $"High zoom ({viewport.Zoom:0}×) centre pixel was {sample} — artwork should remain "
                + "visible instead of vanishing into the transparency checkerboard.");
            host.Close();
        });
    }

    // ── Image metadata inspector ────────────────────────────────────────────────

    private static void RunImageMetadataCases(TestReport report, ProjectSession? project)
    {
        HeadlessHarness.RunCase(report, "Editor.QoL.Image.UsageProfileIsFourRoles", () =>
        {
            IReadOnlyList<ImageUsage> flags = ImageViewerControl.UsageProfileFlags;
            HeadlessHarness.Assert(flags.Count == 4, $"Expected four usage roles, found {flags.Count}.");
            HeadlessHarness.Assert(
                !flags.Contains(ImageUsage.NineSlice),
                "Nine-slice is a way of drawing an image, not a kind of image; it must not be a usage role.");
            foreach (ImageUsage required in new[]
                { ImageUsage.Sprite, ImageUsage.Background, ImageUsage.Texture, ImageUsage.Tileset })
            {
                HeadlessHarness.Assert(flags.Contains(required), $"Usage role {required} is missing.");
            }
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Image.SectionsFollowUsage", () =>
        {
            ProjectSession session = HeadlessHarness.Require(project, "QoL project");
            string tiles = Path.Combine(session.AssetsPath, "Sprites", "Tiles.image.json");
            ImageDocumentSession document = new(
                ImageDocumentSerializer.LoadAtomic(tiles).Document, tiles, ImageDocumentAccess.Editor);
            using ImageViewerControl viewer = new(document);

            HeadlessHarness.Assert(
                viewer.IsSectionVisible(ImageUsage.Tileset),
                "A tile sheet must show its TILESET settings.");
            HeadlessHarness.Assert(
                !viewer.IsSectionVisible(ImageUsage.Background),
                "A tile sheet is not a background; its parallax settings must stay out of the way.");
            HeadlessHarness.Assert(
                !viewer.IsSectionVisible(ImageUsage.Texture),
                "A tile sheet is not a model texture; its surface settings must stay out of the way.");

            document.Document.Usage.Allowed |= ImageUsage.Background;
            viewer.RefreshFromDocument();
            HeadlessHarness.Assert(
                viewer.IsSectionVisible(ImageUsage.Background),
                "Ticking Background must reveal the background settings.");
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Image.ResponsiveLayoutCollapsesSidePanels", () =>
        {
            ProjectSession session = HeadlessHarness.Require(project, "QoL project");
            string tiles = Path.Combine(session.AssetsPath, "Sprites", "Tiles.image.json");
            ImageDocumentSession document = new(
                ImageDocumentSerializer.LoadAtomic(tiles).Document, tiles, ImageDocumentAccess.Editor);
            using ImageViewerControl viewer = new(document) { Size = new Size(960, 720) };
            GateSuite.Pump(4, 20);
            HeadlessHarness.Assert(viewer.IsNarrowLayout, "A 960px-wide viewer should enter narrow layout.");
            viewer.Size = new Size(1280, 720);
            GateSuite.Pump(4, 20);
            HeadlessHarness.Assert(!viewer.IsNarrowLayout, "A 1280px-wide viewer should use the wide layout.");
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.ImageEditor.ResponsiveLayoutCollapsesSidePanels", () =>
        {
            ProjectSession session = HeadlessHarness.Require(project, "QoL project");
            string tiles = Path.Combine(session.AssetsPath, "Sprites", "Tiles.image.json");
            ImageDocumentSession document = new(
                ImageDocumentSerializer.LoadAtomic(tiles).Document, tiles, ImageDocumentAccess.Editor);
            ImageWorkspace workspace = ImageWorkspaceStorage.Load(document);
            using ImageEditorControl editor = new(document, workspace) { Size = new Size(960, 720) };
            GateSuite.Pump(4, 20);
            HeadlessHarness.Assert(editor.IsNarrowLayout, "A 960px-wide pixel editor should enter narrow layout.");
            editor.Size = new Size(1280, 720);
            GateSuite.Pump(4, 20);
            HeadlessHarness.Assert(!editor.IsNarrowLayout, "A 1280px-wide pixel editor should use the wide layout.");
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Room.ResponsiveLayoutCollapsesSidePanels", () =>
        {
            ProjectSession session = HeadlessHarness.Require(project, "QoL project");
            string room = Path.Combine(session.AssetsPath, "Rooms", "Level1.room.json");
            using Form host = GateSuite.NewHost(960, 720);
            RoomEditorControl editor = new(room, session.RootPath) { Dock = DockStyle.Fill };
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(4, 20);
            HeadlessHarness.Assert(editor.IsNarrowLayout, "A 960px-wide Room Editor should enter narrow layout.");
            host.ClientSize = new Size(1280, 720);
            GateSuite.Pump(4, 20);
            HeadlessHarness.Assert(!editor.IsNarrowLayout, "A 1280px-wide Room Editor should use the wide layout.");
        });
    }

    // ── Object art bindings ─────────────────────────────────────────────────────

    private static void RunObjectArtCases(TestReport report, ProjectSession? project)
    {
        HeadlessHarness.RunCase(report, "Editor.QoL.Object.TreeIconFollowsImageBinding", () =>
        {
            ProjectSession session = HeadlessHarness.Require(project, "QoL project");
            string coin = Path.Combine(session.AssetsPath, "Objects", "Coin.object.json");
            string? bound = ObjectResourceReader.ResolveImageFile(coin, session.RootPath);
            HeadlessHarness.Assert(
                bound is not null && File.Exists(bound),
                "The Coin object's image binding did not resolve to a file on disk.");

            ResourceService resources = new(session);
            ResourceItem item = FindResource(resources.BuildTree(), coin)
                ?? throw new InvalidOperationException("Coin object missing from the resource tree.");
            using ResourceThumbnailCache cache = new(session.RootPath);
            cache.Warm(item);

            HeadlessHarness.Assert(
                cache.ResolvedImageFor(item) is not null,
                "The Assets tree resolved no thumbnail for an object that plainly has an image.");
            HeadlessHarness.Assert(
                !ReferenceEquals(cache.GetCachedIcon(item), cache.GetCachedIcon(new ResourceItem
                {
                    Name = "Nothing",
                    FullPath = Path.Combine(session.AssetsPath, "Objects", "Absent.object.json"),
                    RelativePath = "Objects/Absent.object.json",
                    Kind = ResourceKind.GameObject,
                    IsFolder = false,
                })),
                "A bound object drew the same placeholder icon as an unbound one.");
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Object.EditorShowsBoundImage", () =>
        {
            ProjectSession session = HeadlessHarness.Require(project, "QoL project");
            string coin = Path.Combine(session.AssetsPath, "Objects", "Coin.object.json");
            using ObjectEditorControl editor = new(coin, session.RootPath);

            HeadlessHarness.Assert(
                editor.SpritePreview.HasImage,
                "The Object Editor showed no image for an object with an image binding.");
            HeadlessHarness.Assert(
                editor.SpritePreview.FrameSize is { Width: > 0, Height: > 0 },
                "The Object Editor's preview reported no frame size.");
        });
    }

    // ── Object visual actions ──────────────────────────────────────────────────

    private static void RunObjectActionCases(TestReport report, ProjectSession? project)
    {
        HeadlessHarness.RunCase(report, "Editor.QoL.Object.VisualActionsRoundTrip", () =>
        {
            ProjectSession session = HeadlessHarness.Require(project, "QoL project");
            VisualActionPresetStore presets = new(session.RootPath);
            VisualActionTemplate template = new(
                "Move carefully",
                "Custom",
                "Movement",
                "A reusable authored action.",
                [],
                "x = x + speed;");
            string original = "var speed = Choose(1, RandomRange(2, 4));" + Environment.NewLine;
            string source = VisualActionSyntax.Insert(
                original,
                template,
                VisualActionPlacement.Bottom);
            IReadOnlyList<VisualActionBlock> parsed = VisualActionSyntax.Parse(source);
            VisualActionBlock? managed = parsed.SingleOrDefault(block => block.Managed);

            HeadlessHarness.Assert(
                managed is { Name: "Move carefully" }
                && parsed.Any(block => !block.Managed
                                       && block.CommandName == "Choose"
                                       && block.ResultVariable == "speed"),
                "A generated action did not round-trip through its explicit PGSL region: "
                + string.Join(" | ", parsed.Select(block =>
                    $"{block.Name}/{block.CommandName}/managed={block.Managed}/body={block.Body.Replace(Environment.NewLine, " ")}")));
            HeadlessHarness.Assert(
                source.StartsWith(original, StringComparison.Ordinal),
                "Adding a visual action rewrote pre-existing hand-written PGSL.");
            HeadlessHarness.Assert(
                VisualActionSyntax.SplitArguments("StringJoin2(\"a,b\", StringOf(x)), 0.5").Count == 2,
                "Nested calls or quoted commas were split into invalid action arguments.");

            using VisualActionBuilderControl builder = new(session.RootPath);
            builder.LoadSource(source, groupName: "Create");
            HeadlessHarness.Assert(
                builder.GroupName == "Create Event"
                && builder.Blocks.Count == 2
                && builder.Blocks.Count(block => block.Managed) == 1,
                "The visual builder did not retain its logical event grouping.");
            HeadlessHarness.Assert(
                builder.SaveActionAsPreset(builder.Blocks.Single(block => block.Managed).Id, "Careful movement")
                && File.Exists(Path.Combine(session.RootPath, ".genesis", "Editor", "ActionPresets.json")),
                "A reusable visual action preset was not stored in project editor metadata.");
            HeadlessHarness.Assert(
                builder.InsertPreset("builtin_draw_self") && builder.Blocks.Count == 3,
                "A built-in preset could not be dragged/inserted into the event action sequence.");
            VisualActionCommand? spriteSet = VisualActionCatalog.Find("SpriteSet");
            HeadlessHarness.Assert(
                spriteSet?.Parameters.FirstOrDefault()?.AssetKind == ResourceKind.Image
                && presets.All.Any(preset => preset.Id == "builtin_set_model")
                && presets.All.Any(preset => preset.Id == "builtin_play_sound")
                && presets.All.Any(preset => preset.Id == "builtin_set_variable")
                && presets.All.Any(preset => preset.Id == "builtin_animation_play")
                && builder.InsertPreset("builtin_animation_play")
                && builder.Source.Contains("AnimationStatePlay(\"Idle\", true, 0.15);", StringComparison.Ordinal),
                "Common resource/audio/animation actions are not exposed as typed Object Builder blocks.");

            string coin = Path.Combine(session.AssetsPath, "Objects", "Coin.object.json");
            using ObjectEditorControl objectEditor = new(coin, session.RootPath);
            objectEditor.SetEventBody("Create", builder.Source);
            objectEditor.ShowVisualActions();
            int before = objectEditor.VisualActions.Blocks.Count;
            HeadlessHarness.Assert(
                objectEditor.IsVisualActionMode
                && objectEditor.VisualActions.GroupName == "Create Event"
                && objectEditor.VisualActions.InsertPreset("builtin_destroy_self")
                && objectEditor.VisualActions.Blocks.Count == before + 1
                && objectEditor.PgslEvents["Create"].Contains("InstanceDestroy(id);", StringComparison.Ordinal),
                "Object Editor visual actions did not live-route into the selected event source.");
            objectEditor.ShowCodeEditor();
            HeadlessHarness.Assert(
                !objectEditor.IsVisualActionMode,
                "Object Editor could not return from action blocks to the PGSL code editor.");
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Object.VisualActionsWizardAndEdits", () =>
        {
            ProjectSession session = HeadlessHarness.Require(project, "QoL project");
            VisualActionPresetStore presets = new(session.RootPath);
            using VisualActionWizardDialog wizard = new(session.RootPath, presets, "DrawSelf");
            HeadlessHarness.Assert(
                wizard.SelectCommand("DrawSelf") && wizard.SetActionName("Draw in Step")
                && wizard.CreateSelection() && wizard.SelectedTemplate is not null,
                "The visual action wizard did not produce a DrawSelf template.");
            string source = VisualActionSyntax.Insert(
                string.Empty,
                wizard.SelectedTemplate!,
                VisualActionPlacement.Bottom);
            VisualActionTemplate second = new(
                "Destroy later",
                "InstanceDestroy",
                "Instances",
                "Remove the instance.",
                [new VisualActionParameter("id", "id")]);
            source = VisualActionSyntax.Insert(source, second, VisualActionPlacement.Bottom);
            using VisualActionBuilderControl builder = new(session.RootPath);
            builder.LoadSource(source, groupName: "Step");
            HeadlessHarness.Assert(builder.Blocks.Count == 2, "Expected two managed visual actions.");
            HeadlessHarness.Assert(builder.InsertCondition("speed > 0", includeElse: true)
                && builder.Flows.Count == 1,
                "The visual builder could not create a structured If/Else package.");
            VisualActionFlowGroup flow = builder.Flows[0];
            HeadlessHarness.Assert(
                builder.InsertCommandIntoCondition(flow.Id, "then", "PlaySound")
                && builder.InsertCommandIntoCondition(flow.Id, "else", "Engine.Rendering.DrawModel3D")
                && builder.SetCondition(flow.Id, "speed >= runSpeed")
                && builder.Source.Contains("if (speed >= runSpeed)", StringComparison.Ordinal)
                && builder.Blocks.Any(block => block.FlowId == flow.Id && block.FlowBranch == "then")
                && builder.Blocks.Any(block => block.FlowId == flow.Id && block.FlowBranch == "else"),
                "Then/Else drop zones did not generate brace-wrapped PGSL with ordered branch actions.");
            HeadlessHarness.Assert(
                builder.InsertConditionIntoCondition(flow.Id, "else", "hasFallback")
                && builder.Flows.Count == 2,
                "The visual builder could not nest a condition package inside an outer branch.");
            VisualActionFlowGroup nested = builder.Flows.Single(candidate => candidate.ParentFlowId == flow.Id);
            HeadlessHarness.Assert(
                nested.ParentBranch == "else"
                && builder.InsertCommandIntoCondition(nested.Id, "then", "SpriteSet")
                && builder.InsertCommandIntoCondition(nested.Id, "else", "DrawCircle")
                && builder.Source.Contains("if (hasFallback)", StringComparison.Ordinal)
                && builder.Blocks.Any(block => block.FlowId == nested.Id && block.FlowBranch == "then")
                && builder.Blocks.Any(block => block.FlowId == nested.Id && block.FlowBranch == "else"),
                "Nested Then/Else packages did not preserve their parent branch or inner action drop zones.");
            HeadlessHarness.Assert(
                builder.InsertConditionIntoCondition(nested.Id, "then", "canContinue")
                && builder.Flows.Count == 3,
                "The visual builder stopped nesting conditions after one inner level.");
            VisualActionFlowGroup deepest = builder.Flows.Single(candidate =>
                candidate.ParentFlowId == nested.Id);
            HeadlessHarness.Assert(
                deepest.ParentBranch == "then"
                && builder.InsertCommandIntoCondition(deepest.Id, "then", "DrawSelf")
                && builder.InsertConditionIntoCondition(deepest.Id, "else", "useFallback")
                && builder.Flows.Count == 4,
                "Arbitrary-depth conditions did not retain their parent branch or accept actions and child flows.");
            using (VisualActionBuilderControl reopenedBuilder = new(session.RootPath))
            {
                reopenedBuilder.LoadSource(builder.Source, groupName: "Step");
                VisualActionFlowGroup fourth = reopenedBuilder.Flows.Single(candidate =>
                    candidate.ParentFlowId == deepest.Id);
                HeadlessHarness.Assert(
                    reopenedBuilder.Flows.Count == 4
                    && fourth.ParentBranch == "else"
                    && reopenedBuilder.Blocks.Any(block =>
                        block.FlowId == deepest.Id && block.FlowBranch == "then"
                        && block.CommandName == "DrawSelf"),
                    "Deep visual flow structure did not survive a source round trip.");
            }
            string blockId = builder.Blocks[0].Id;
            VisualActionBlock customBody = new(
                "custom",
                "Custom step",
                "Custom",
                "Movement",
                string.Empty,
                [],
                "x = x + 1;",
                0,
                0,
                Managed: true);
            HeadlessHarness.Assert(
                !builder.SetArgument(customBody.Id, "x", "x + 1"),
                "Parameter edits must not rewrite custom-body blocks.");
            HeadlessHarness.Assert(
                builder.MoveAction(blockId, 2) && builder.Blocks[0].Name == "Destroy later",
                "A managed visual action could not be reordered.");
            int beforeRemove = builder.Blocks.Count;
            HeadlessHarness.Assert(
                builder.RemoveAction(blockId) && builder.Blocks.Count == beforeRemove - 1,
                "A managed visual action could not be removed.");
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Object.VisualActionsImplicitDetection", () =>
        {
            string source = "DrawSelf();" + Environment.NewLine + "x = x + 1;" + Environment.NewLine;
            IReadOnlyList<VisualActionBlock> blocks = VisualActionSyntax.Parse(source);
            HeadlessHarness.Assert(
                blocks.Any(block => !block.Managed && block.CommandName == "DrawSelf")
                && blocks.Any(block => block.CommandName == "PGSL"
                                       && block.Body.Contains("x = x + 1", StringComparison.Ordinal)),
                "Standalone catalog commands were not surfaced as implicit visual actions.");
            HeadlessHarness.Assert(
                source.Contains("x = x + 1;", StringComparison.Ordinal),
                "Implicit detection rewrote neighbouring hand-written PGSL.");
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Object.BlueprintConnectionsRoundTrip", () =>
        {
            ProjectSession session = HeadlessHarness.Require(project, "QoL project");
            using VisualActionBuilderControl builder = new(session.RootPath);
            builder.LoadSource(string.Empty, groupName: "Step");
            HeadlessHarness.Assert(
                builder.InsertBlueprintAction("Set Position")
                && builder.InsertBlueprintAction("Random Range"),
                "The generic position and value nodes could not be added to an empty graph.");

            VisualActionBlock position = builder.Blocks.Single(block => block.CommandName == "Editor.Position");
            VisualActionBlock random = builder.Blocks.Single(block => block.CommandName == "RandomRange");
            HeadlessHarness.Assert(
                builder.SetArgument(position.Id, "X", "7")
                && builder.ConnectData(random.Id, position.Id, "X"),
                "A value output could not be connected to an inline Object property.");
            position = builder.Blocks.Single(block => block.Id == position.Id);
            random = builder.Blocks.Single(block => block.Id == random.Id);
            string linkedX = position.Parameters.Single(parameter => parameter.Name == "X").Value;
            int randomIndex = builder.Blocks.ToList().FindIndex(block => block.Id == random.Id);
            int positionIndex = builder.Blocks.ToList().FindIndex(block => block.Id == position.Id);
            HeadlessHarness.Assert(
                linkedX == random.ResultVariable && randomIndex >= 0 && randomIndex < positionIndex,
                $"The data connection did not emit a variable assignment before its consumer "
                + $"(X='{linkedX}', result='{random.ResultVariable}', indexes={randomIndex}/{positionIndex}).");
            HeadlessHarness.Assert(
                builder.DisconnectData(position.Id, "X")
                && builder.Blocks.Single(block => block.Id == position.Id).Parameters
                    .Single(parameter => parameter.Name == "X").Value == "7",
                "Deleting a data connector did not restore the field's authored literal.");

            HeadlessHarness.Assert(
                builder.ConnectData(random.Id, position.Id, "X")
                && builder.DisconnectExecution(position.Id),
                "The graph could not disconnect an execution chain without deleting its nodes.");
            string disconnected = builder.Source;
            using VisualActionBuilderControl reopened = new(session.RootPath);
            reopened.LoadSource(disconnected, groupName: "Step");
            HeadlessHarness.Assert(
                reopened.Blocks.Single(block => block.Id == position.Id).DetachedChain.Length > 0
                && reopened.ConnectExecution("$event", position.Id)
                && reopened.Blocks.Single(block => block.Id == position.Id).DetachedChain.Length == 0
                && reopened.Blocks.ToList().FindIndex(block => block.Id == random.Id)
                    < reopened.Blocks.ToList().FindIndex(block => block.Id == position.Id),
                "Execution connectors or their data dependencies did not survive source round trip and reconnection.");
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Object.VisualAnimationGraph", () =>
        {
            ProjectSession session = HeadlessHarness.Require(project, "QoL project");
            AnimationGraphDefinition graph = new()
            {
                Name = "Character locomotion",
                ModelAsset = "Assets/Models/Hero.model.json",
                KeepPreviousTransform = true,
                States =
                [
                    new AnimationGraphState { Id = "idle", Name = "Idle", Clip = "Idle", Speed = 1, Loop = true, IsDefault = true },
                    new AnimationGraphState { Id = "run", Name = "Run", Clip = "Run", Speed = 1.5, Loop = true },
                    new AnimationGraphState { Id = "jump", Name = "Jump", Clip = "Jump", Speed = 0.8, Loop = false },
                ],
                Transitions =
                [
                    new AnimationGraphTransition { FromStateId = "idle", ToStateId = "run", Condition = "vspeed > 0", BlendSeconds = 0.2 },
                    new AnimationGraphTransition { FromStateId = "run", ToStateId = "jump", Condition = "jumping", BlendSeconds = 0.1 },
                    new AnimationGraphTransition { FromStateId = "jump", ToStateId = "idle", OnAnimationEnd = true, BlendSeconds = 0.25 },
                ],
            };
            string body = AnimationGraphSyntax.GenerateBody(graph);
            HeadlessHarness.Assert(
                AnimationGraphSyntax.TryRead(body, out AnimationGraphDefinition parsed)
                && parsed.States.Count == 3 && parsed.Transitions.Count == 3
                && body.Contains("Engine.Rendering.Models.KeepPreviousTransform = true;", StringComparison.Ordinal)
                && body.Contains("ModelSet(\"Assets/Models/Hero.model.json\");", StringComparison.Ordinal)
                && body.Contains("if (vspeed > 0)", StringComparison.Ordinal)
                && body.Contains("AnimationStateHasFinished()", StringComparison.Ordinal)
                && body.Contains("AnimationStateSetSpeed(1.5);", StringComparison.Ordinal),
                "The visual animation graph did not round-trip its states or emit executable PGSL transitions.");

            VMEngine.Initialize();
            PgslContext runtimeContext = new()
            {
                VSpeed = 2,
            };
            PgslContext? previousContext = VMEngine.Bridge.GetContext();
            try
            {
                VMEngine.Bridge.SetContext(runtimeContext);
                VMEngine.ClearCompileCache();
                CompileResult compiled = VMEngine.Compile(body)
                    ?? throw new InvalidOperationException("Animation graph PGSL did not compile.");
                PgslVm vm = VMEngine.CreateVm();
                vm.Execute(compiled.Instructions, compiled.Constants, clearVariables: true);
                HeadlessHarness.Assert(
                    runtimeContext.ModelKeepPreviousTransform
                    && runtimeContext.ModelAsset.EndsWith("Hero.model.json", StringComparison.Ordinal)
                    && runtimeContext.ModelAnimationClip == "Run"
                    && Math.Abs(runtimeContext.ModelAnimationSpeed - 1.5) < 0.001,
                    $"Compiled animation graph PGSL did not select and play the condition-matched state: "
                    + $"keep={runtimeContext.ModelKeepPreviousTransform}, clip='{runtimeContext.ModelAnimationClip}', "
                    + $"speed={runtimeContext.ModelAnimationSpeed:0.###}, "
                    + $"state='{runtimeContext.AnimationParameters.FirstOrDefault().Value}'.");
            }
            finally
            {
                VMEngine.Bridge.SetContext(previousContext ?? new PgslContext());
            }

            using VisualActionBuilderControl builder = new(session.RootPath);
            builder.LoadSource(string.Empty, groupName: "Step");
            AnimationGraphDefinition authored = new();
            HeadlessHarness.Assert(
                builder.Presets.Any(preset => preset.Id == "builtin_animation_graph")
                && builder.InsertAnimationGraph(graph)
                && builder.Blocks.Count == 1
                && AnimationGraphSyntax.TryRead(builder.Blocks[0].Body, out authored)
                && authored.Name == "Character locomotion",
                "Universal Builder did not expose or insert the optional animation graph action.");

            using AnimationGraphDesignerDialog designer = new(authored);
            AnimationGraphState land = designer.AddState("Land", "Land");
            HeadlessHarness.Assert(
                designer.AddTransition("jump", land.Id, "grounded", blendSeconds: 0.08) is not null
                && designer.AcceptDefinition()
                && designer.ResultDefinition is { States.Count: 4, Transitions.Count: 4 }
                && builder.UpdateAnimationGraph(builder.Blocks[0].Id, designer.ResultDefinition)
                && builder.Source.Contains("if (grounded)", StringComparison.Ordinal),
                "The animation graph designer could not add and update state/transition PGSL.");

            JObject model = ObjectCompositionModel.Props(
                new ObjectCompositionModel(new JObject()).Ensure("ModelRendererComponent"));
            HeadlessHarness.Assert(
                model["KeepPreviousTransform"]?.Type == JTokenType.Boolean,
                "The Object Model component does not expose transform preservation as a boolean option.");
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Object.VisualGraphInspectorAndHistory", () =>
        {
            ProjectSession session = HeadlessHarness.Require(project, "QoL project");
            using VisualActionBuilderControl builder = new(session.RootPath);
            builder.LoadSource(string.Empty, groupName: "Step");
            HeadlessHarness.Assert(
                builder.Graph.HasStartNode && builder.Graph.HasEndNode && builder.Graph.NodeCount == 0,
                "The visual event graph did not expose its Start/End flow terminals.");
            HeadlessHarness.Assert(
                builder.InsertCommand("InstanceDestroy") && builder.Blocks.Count == 1 && builder.CanUndo,
                "A catalogued PGSL command could not be inserted directly from Universal Builder.");

            string blockId = builder.Blocks[0].Id;
            HeadlessHarness.Assert(
                builder.SelectBlock(blockId)
                && builder.GetSelectedInspectorValues().Any(value =>
                    value.PropertyPath == $"VisualActions.{blockId}.Parameters.id"),
                "Selecting a graph action did not expose its typed parameters to Inspector.");
            HeadlessHarness.Assert(
                builder.TryApplyInspectorValue($"VisualActions.{blockId}.Parameters.id", "other")
                && builder.Source.Contains("InstanceDestroy(other);", StringComparison.Ordinal),
                "Inspector did not live-update the selected action and its PGSL preview source.");
            HeadlessHarness.Assert(
                builder.CopySelected() && builder.Paste() && builder.Blocks.Count == 2,
                "Visual action copy/paste did not duplicate the selected block.");
            builder.Undo();
            HeadlessHarness.Assert(
                builder.Blocks.Count == 1 && builder.CanRedo,
                "Visual action Undo did not restore the previous event source.");
            builder.Redo();
            HeadlessHarness.Assert(
                builder.Blocks.Count == 2,
                "Visual action Redo did not reapply the event edit.");

            using EventListPanel events = new();
            events.SetEvents(["Create", "Step", "Draw"], "Create", ["Create"]);
            events.SetFilter("draw");
            HeadlessHarness.Assert(
                events.EventIds.SequenceEqual(["Draw"]),
                "Object event search did not filter against event descriptions/categories.");
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Object.CodeModeSyncsActionsAndInspector", () =>
        {
            ProjectSession session = HeadlessHarness.Require(project, "QoL project");
            string coin = Path.Combine(session.AssetsPath, "Objects", "Coin.object.json");
            using ObjectEditorControl objectEditor = new(coin, session.RootPath);
            objectEditor.AddEventViaWizard("Create");
            objectEditor.SelectEvent("Create");
            objectEditor.SetEventBody("Create", "var coins = 1;" + Environment.NewLine);
            objectEditor.ShowCodeEditor();
            objectEditor.ReplaceActiveEventSource("var coins = 2;" + Environment.NewLine);
            HeadlessHarness.Assert(
                objectEditor.VisualActions.Blocks.Count == 1
                && objectEditor.VisualActions.Blocks[0].CommandName == "PGSL"
                && objectEditor.VisualActions.Blocks[0].Body.Contains("var coins = 2", StringComparison.Ordinal)
                && objectEditor.PgslEvents["Create"].Contains("var coins = 2;", StringComparison.Ordinal),
                "Code edits did not refresh the visual action builder source.");
            HeadlessHarness.Assert(
                objectEditor.GetLiveInspectorValues()
                    .Any(value => value.PropertyPath == "Events.Create.Variables.coins"
                                  && Convert.ToInt32(value.Value, System.Globalization.CultureInfo.InvariantCulture) == 2),
                "Code edits did not refresh authored Inspector variables.");
        });
    }

    private static void RunObjectLayoutCases(TestReport report, ProjectSession? project)
    {
        HeadlessHarness.RunCase(report, "Editor.QoL.Object.NewEventsAreInert", () =>
        {
            ProjectSession session = HeadlessHarness.Require(project, "QoL project");
            string path = Path.Combine(session.AssetsPath, "Objects", "Neutral Starter.object.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, ResourceDefinitions.Get(ResourceKind.GameObject).DefaultContent);
            using ObjectEditorControl editor = new(path, session.RootPath);
            HeadlessHarness.Assert(
                editor.AddEventViaWizard("Create")
                && PgslAstBuilder.Parse(editor.PgslEvents["Create"]).Body.Count == 0,
                "Adding a Create event imposed movement, drawing, or project-specific behavior.");
            HeadlessHarness.Assert(
                editor.AddEventViaWizard("Draw")
                && PgslAstBuilder.Parse(editor.PgslEvents["Draw"]).Body.Count == 0,
                "Adding a Draw event imposed a sample game's visuals on the Object.");
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Object.LeftPanelCompactLayout", () =>
        {
            ProjectSession session = HeadlessHarness.Require(project, "QoL project");
            string coin = Path.Combine(session.AssetsPath, "Objects", "Coin.object.json");
            using ObjectEditorControl editor = new(coin, session.RootPath);

            IEnumerable<string> labels = Descendants(editor)
                .OfType<Label>()
                .Select(label => label.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text));
            HeadlessHarness.Assert(
                Descendants(editor).OfType<Button>().Any(button =>
                    button.Text.Equals("Choose Sprite / Model…", StringComparison.Ordinal))
                && labels.Any(text => text.Equals("Depth", StringComparison.Ordinal))
                && labels.Any(text => text.Equals("Parent", StringComparison.Ordinal)),
                "The Object Editor left panel did not expose visual selection, Depth and Parent.");
            HeadlessHarness.Assert(
                !labels.Any(text => text is "Model" or "Shader" or "Wildlife"),
                "Model, Shader and Wildlife still appear on the compact Object Editor left panel.");
            HeadlessHarness.Assert(
                Descendants(editor).OfType<CheckBox>().All(box => box.Text is not "Uses physics" and not "3D object"),
                "Physics and 3D toggles still appear on the compact Object Editor left panel.");
            HeadlessHarness.Assert(
                Descendants(editor).OfType<Button>().Any(button => button.Text.Contains("Add Event", StringComparison.Ordinal)),
                "The Object Editor left panel is missing Add Event.");
            HeadlessHarness.Assert(
                !Descendants(editor).OfType<Button>().Any(button => button.Text.Contains("Remove selected event", StringComparison.Ordinal)),
                "Remove selected event should not live on the compact left panel.");

            using ObjectCompositionDialog composition = new(editor.Document, session.RootPath);
            HeadlessHarness.Assert(
                ObjectCompositionModel.Definitions.Any(definition =>
                    definition.Type.Contains("Model", StringComparison.OrdinalIgnoreCase))
                && ObjectCompositionModel.Definitions.Any(definition =>
                    definition.Type.Contains("Shader", StringComparison.OrdinalIgnoreCase))
                && ObjectCompositionModel.Definitions.All(definition =>
                    !definition.Type.Contains("Wildlife", StringComparison.OrdinalIgnoreCase)),
                "Model and Shader must remain reachable through Components, without Wildlife authoring.");
            HeadlessHarness.Assert(
                composition.SelectComponent("SpriteComponent"),
                "The component stack could not select a component by its stable type.");
            composition.Composition.SetProperty("SpriteComponent", "Alpha", 0.25f);
            HeadlessHarness.Assert(
                composition.CopySelectedComponent()
                && composition.ResetSelectedComponent()
                && Math.Abs(ObjectCompositionModel.Props(composition.Composition.Find("SpriteComponent")!).Value<float>("Alpha") - 1f) < 0.001f
                && composition.PasteComponent()
                && Math.Abs(ObjectCompositionModel.Props(composition.Composition.Find("SpriteComponent")!).Value<float>("Alpha") - 0.25f) < 0.001f,
                "Component copy/paste/reset did not preserve and restore typed component settings.");
        });
    }

    // ── Sandbox honesty ─────────────────────────────────────────────────────────

    private static void RunSandboxCases(TestReport report, ProjectSession? project)
    {
        HeadlessHarness.RunCase(report, "Editor.QoL.Sandbox.ReportsWhatItSwallowed", () =>
        {
            ProjectSession session = HeadlessHarness.Require(project, "QoL project");
            string player = Path.Combine(session.AssetsPath, "Objects", "Player.object.json");
            IReadOnlyDictionary<string, string> events = ObjectEventStore.Load(player);
            HeadlessHarness.Assert(events.Count > 0, "The Player object has no events to run.");

            ObjectSandboxResult result = ObjectSandbox.Run(events, frames: 12);
            HeadlessHarness.Assert(result.Ok, "The Player object should still run without errors.");
            HeadlessHarness.Assert(
                result.Warnings.Count > 0,
                "The sandbox reported a clean run for a script whose input and collision calls it "
                + "silently neutralised.");
            HeadlessHarness.Assert(
                result.Warnings.Any(warning =>
                    warning.Subject.Equals("KeyCheck", StringComparison.OrdinalIgnoreCase)),
                "Input commands returned their neutral value without saying so: "
                + string.Join(" | ", result.Warnings.Select(w => w.Subject)));
            HeadlessHarness.Assert(!result.Clean, "A run with warnings must not report itself as clean.");
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Sandbox.CleanScriptWarnsAboutNothing", () =>
        {
            // The counterpart that stops the warnings becoming noise: a script that only does what
            // the sandbox genuinely supports must report nothing at all.
            Dictionary<string, string> events = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Create"] = "count = 0;\nx = 32;\ny = 48;\n",
                ["Step"] = "count = count + 1;\nx = x + 1;\n",
                ["Draw"] = "DrawSetColorRgb(255, 0, 0);\nDrawCircle(x, y, 8, true);\n",
            };

            ObjectSandboxResult result = ObjectSandbox.Run(events, frames: 5);
            HeadlessHarness.Assert(result.Ok, $"Plain script failed: {FirstError(result)}");
            HeadlessHarness.Assert(
                result.Warnings.Count == 0,
                "A script using only supported commands must produce no warnings, or the warnings "
                + "are noise: " + string.Join(" | ", result.Warnings.Select(w => w.Message)));
            HeadlessHarness.Assert(result.Clean, "A run with no errors and no warnings is clean.");
            HeadlessHarness.Assert(
                Math.Abs(result.Numbers["count"] - 5) < 0.001,
                $"Step ran {result.Numbers.GetValueOrDefault("count")} times instead of 5.");
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Sandbox.SurfacesUnsetVariableReads", () =>
        {
            Dictionary<string, string> events = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Step"] = "total = total + neverSet;\n",
            };

            ObjectSandboxResult result = ObjectSandbox.Run(events, frames: 3);
            HeadlessHarness.Assert(
                result.Warnings.Any(warning =>
                    warning.Subject.Equals("neverSet", StringComparison.OrdinalIgnoreCase)),
                "Reading a name nothing ever set counted as 0 without a word about it.");
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Sandbox.DrawsBoundSpriteWithoutDrawEvent", () =>
        {
            // The Coin case, reported from a screenshot: Create and Step, no Draw event, an Image
            // bound. That object renders perfectly well in a room, because a sprite-backed object is
            // drawn by its sprite component rather than by script — but the sandbox recorded only
            // PGSL draw calls, so it showed an empty canvas and reported "0 draw call(s)". Both
            // facts were true and together they read as a broken sandbox.
            //
            // The assertion is deliberately in two halves: the script must issue zero draw calls
            // (proving this is the no-Draw-event case, not a script accidentally covering for the
            // sprite), and the sprite must still have been drawn.
            if (project is null)
            {
                HeadlessHarness.Assert(false, "No project workspace for the sandbox sprite fixture.");
                return;
            }

            string fixtureDir = Path.Combine(project.RootPath, "SandboxSpriteFixture");
            Directory.CreateDirectory(fixtureDir);
            string framePath = Path.Combine(fixtureDir, "coin.png");
            using (Bitmap frame = new(16, 16))
            {
                using (Graphics graphics = Graphics.FromImage(frame))
                {
                    graphics.Clear(Color.Gold);
                }

                frame.Save(framePath, System.Drawing.Imaging.ImageFormat.Png);
            }

            using ObjectSandboxPanel panel = new();
            panel.EventSource = () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Create"] = "homeY = y;\n",
                ["Step"] = "y = y + 1;\n",
            };
            panel.SpriteSource = () =>
                new ObjectSandboxPanel.SandboxSprite(framePath, 0.5, 0.5, NormalizedOrigin: true);

            ObjectSandboxResult? result = panel.Run();
            HeadlessHarness.Assert(
                result is not null && result.Ok,
                $"Sandbox run failed: {(result is null ? "no result" : FirstError(result))}");
            HeadlessHarness.Assert(
                panel.LastDrawCallCount == 0,
                "This fixture must issue no script draw calls, or it is not testing the "
                + $"sprite-only path (saw {panel.LastDrawCallCount}).");
            HeadlessHarness.Assert(
                panel.LastSpriteDrawn,
                "The bound sprite was not drawn. A sprite-backed object with no Draw event renders "
                + "in a room but showed an empty sandbox — the defect this guard exists for.");
        });
    }

    // ── Focus-aware shortcuts ───────────────────────────────────────────────────

    private static void RunShortcutCases(TestReport report)
    {
        HeadlessHarness.RunCase(report, "Editor.QoL.Shortcuts.MapAndFocusClassification", () =>
        {
            HeadlessHarness.Assert(
                EditCommandRouter.Map(Keys.Control | Keys.C) == EditCommand.Copy
                && EditCommandRouter.Map(Keys.Control | Keys.V) == EditCommand.Paste
                && EditCommandRouter.Map(Keys.Control | Keys.X) == EditCommand.Cut
                && EditCommandRouter.Map(Keys.Control | Keys.Z) == EditCommand.Undo
                && EditCommandRouter.Map(Keys.Control | Keys.Y) == EditCommand.Redo
                && EditCommandRouter.Map(Keys.Delete) == EditCommand.Delete,
                "The editing shortcut map lost one of its bindings.");
            HeadlessHarness.Assert(
                EditCommandRouter.Map(Keys.F5) is null,
                "Run must not be treated as an editing command.");

            using TextBox text = new();
            using TreeView tree = new();
            HeadlessHarness.Assert(
                EditCommandRouter.IsTextEntry(text),
                "A text box must keep its own Ctrl+C/V/X — the shell has to leave those alone.");
            HeadlessHarness.Assert(
                !EditCommandRouter.IsTextEntry(tree),
                "A tree view is not text entry; the shell routes its keys.");
        });
    }

    /// <summary>
    /// Typing inside an editor must not trigger that editor's own single-key shortcuts.
    /// </summary>
    /// <remarks>
    /// The shell's routing was already focus-aware, but an editor that overrides
    /// <c>ProcessCmdKey</c> is asked first — the override bubbles up from the focused control, so it
    /// runs before the shell and before the text box itself. The Room Editor bound bare letters to
    /// tools and Delete to "delete the selected instances", which meant renaming a node or typing a
    /// grid size switched tools per keystroke and could destroy the selection outright.
    ///
    /// Driven through <see cref="Control.PreProcessMessage"/> with a real WM_KEYDOWN, because that
    /// is the path Windows uses and the one where the ordering bug lived; calling the editor's
    /// methods directly would prove nothing about who sees the key first.
    /// </remarks>
    private static void RunTypingCase(TestReport report, ProjectSession? project)
    {
        HeadlessHarness.RunCase(report, "Editor.QoL.Shortcuts.TypingDoesNotTriggerEditorKeys", () =>
        {
            ProjectSession session = HeadlessHarness.Require(project, "QoL project");
            string roomPath = Path.Combine(session.AssetsPath, "Rooms", "Level 1.room.json");

            using Form host = GateSuite.NewHost();
            RoomEditorControl editor = new(roomPath, session.RootPath);
            // Inside the editor, because that is where its real text fields live (the grid-size box,
            // the inspector's spinners, the outliner's rename). A sibling text box would prove
            // nothing: ProcessCmdKey walks the parent chain, so only a child reaches the editor.
            TextBox typing = new() { Dock = DockStyle.Bottom, Height = 24 };
            editor.Controls.Add(typing);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(8, 25);

            typing.Focus();
            GateSuite.Pump(4, 20);
            HeadlessHarness.Assert(typing.Focused, "The text box never took the focus.");

            // R selects the scale gizmo, G toggles snap, Delete removes instances, Ctrl+V pastes.
            // With the caret in a text box every one of them belongs to the text box.
            RoomEditorControl.RoomTool toolBefore = editor.ActiveTool;
            RoomEditorControl.GizmoKind gizmoBefore = editor.Gizmo;
            int nodesBefore = editor.Room.Nodes.Count;
            foreach (Keys key in new[] { Keys.R, Keys.G, Keys.Delete, Keys.Control | Keys.V })
            {
                HeadlessHarness.Assert(
                    !SendKeyDown(typing, key),
                    $"The Room Editor consumed {key} while the caret was in a text box.");
            }

            HeadlessHarness.Assert(
                editor.ActiveTool == toolBefore && editor.Gizmo == gizmoBefore,
                $"Typing changed the tool to {editor.ActiveTool}/{editor.Gizmo}.");
            HeadlessHarness.Assert(
                editor.Room.Nodes.Count == nodesBefore,
                $"Typing changed the room from {nodesBefore} instances to {editor.Room.Nodes.Count}.");
            HeadlessHarness.Assert(
                !editor.CanEdit(EditCommand.Delete) && !editor.CanEdit(EditCommand.Paste),
                "The Room Editor offers the Edit menu its verbs while the caret is in a text box.");

            // The other half of the guard: away from the text box the shortcut still works, so this
            // suppresses the wrong context rather than the feature. The stand-in text box goes away
            // first, otherwise the editor just hands the focus straight back to it.
            editor.Controls.Remove(typing);
            typing.Dispose();
            GateSuite.Pump(4, 20);
            ((Control)editor).Focus();
            GateSuite.Pump(4, 20);
            HeadlessHarness.Assert(
                editor.Viewport.Host.Focused && !EditorInputGuard.IsTextEntryFocused(),
                "Focusing the Room Editor did not target its viewport: " + EditorInputGuard.FocusedControl()?.Name);
            HeadlessHarness.Assert(
                SendKeyDown(EditorInputGuard.FocusedControl() ?? editor, Keys.R),
                "The Room Editor stopped handling its own tool shortcut.");
            HeadlessHarness.Assert(
                editor.Gizmo == RoomEditorControl.GizmoKind.Scale,
                $"R did not select the scale gizmo (got {editor.Gizmo}).");

            // Exercise the actual new search field, not only the stand-in. A direct child focus
            // must survive repaint and keys, while clicking the viewport must restore shortcuts.
            TextBox search = editor.Controls.Find("RoomObjectSearch", true).OfType<TextBox>().Single();
            search.Focus();
            GateSuite.Pump(3, 20);
            HeadlessHarness.Assert(search.Focused && EditorInputGuard.IsTextEntryFocused(),
                "Direct focus on the Objects search was redirected into the viewport.");
            bool snapBefore = editor.Room.Settings.SnapEnabled;
            search.Clear();
            foreach ((Keys key, char character) in new[]
                     { (Keys.Q, 'q'), (Keys.W, 'w'), (Keys.E, 'e'), (Keys.R, 'r'), (Keys.G, 'g') })
            {
                HeadlessHarness.Assert(!SendKeyDown(search, key), "A search keystroke was consumed as a Room shortcut: " + key);
                SendTypingCharacter(search.Handle, 0x0102, (IntPtr)(int)character, IntPtr.Zero);
            }
            GateSuite.Pump(3, 20);
            HeadlessHarness.Assert(search.Focused && search.Text == "qwerg"
                && editor.ActiveTool == RoomEditorControl.RoomTool.Select && editor.Gizmo == RoomEditorControl.GizmoKind.Scale
                && editor.Room.Settings.SnapEnabled == snapBefore && editor.Room.Nodes.Count == nodesBefore,
                "Actual search typing lost focus, changed tools/snap, or modified scene instances.");
            HeadlessHarness.Assert(!SendKeyDown(search, Keys.Delete) && !SendKeyDown(search, Keys.Control | Keys.V),
                "Delete or paste escaped the actual Objects search field.");
            search.Clear();
            editor.EditorPointerDown(new Point(5, 5), MouseButtons.Left, Keys.None);
            editor.EditorPointerUp(new Point(5, 5), MouseButtons.Left, Keys.None);
            GateSuite.Pump(3, 20);
            HeadlessHarness.Assert(editor.Viewport.Host.Focused && !EditorInputGuard.IsTextEntryFocused(),
                "Clicking the canvas left keyboard focus in the Objects search field.");
            HeadlessHarness.Assert(SendKeyDown(editor.Viewport.Host, Keys.W)
                && editor.Gizmo == RoomEditorControl.GizmoKind.Move,
                "Canvas click did not restore the Room move shortcut.");
            search.Focus();
            ((Control)editor).Focus();
            GateSuite.Pump(3, 20);
            HeadlessHarness.Assert(editor.Viewport.Host.Focused,
                "Explicit root focus restored its remembered search child instead of the canvas.");
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendTypingCharacter(IntPtr window, uint message, IntPtr character, IntPtr data);

    /// <summary>Presses a key at a control the way Windows does, and reports who took it.</summary>
    /// <remarks>
    /// <see cref="Control.PreProcessMessage"/> is the real entry point: it runs
    /// <c>ProcessCmdKey</c> up the parent chain first, then the control's own handling. Sending the
    /// message to the focused control is the whole point — the defect was about ordering, and
    /// invoking the editor's methods directly would sail straight past it.
    /// </remarks>
    private static bool SendKeyDown(Control target, Keys key)
    {
        Message message = Message.Create(target.Handle, WmKeyDown, (IntPtr)(int)key, IntPtr.Zero);
        return target.PreProcessMessage(ref message);
    }

    private const int WmKeyDown = 0x0100;

    private static void RunMenuOwnershipCase(TestReport report, HeadlessContext ctx, ProjectSession? project)
    {
        HeadlessHarness.RunCase(report, "Editor.QoL.Shortcuts.MenuDoesNotClaimEditingKeys", () =>
        {
            // The whole defect in one assertion: a menu accelerator is consumed before the focused
            // control is offered the key, so any Edit item that *registers* Ctrl+C or Delete takes
            // it away from every code editor and image canvas in the application. The items must
            // display their shortcut and claim nothing.
            Genesis.Application.Studio.StudioServices services =
                HeadlessHarness.Require(ctx.StudioServices, "Studio services");
            ProjectSession session = HeadlessHarness.Require(project, "QoL project");
            using Genesis.Application.Studio.Forms.StudioShellForm shell =
                new(services, session, persistLayout: false);

            ToolStripMenuItem edit = shell.MainMenuStrip!.Items
                .OfType<ToolStripMenuItem>()
                .First(item => item.Text!.Contains("Edit", StringComparison.OrdinalIgnoreCase));

            string[] focusAware = ["Undo", "Redo", "Cut", "Copy", "Paste", "Rename", "Delete"];
            foreach (string name in focusAware)
            {
                ToolStripMenuItem item = edit.DropDownItems
                    .OfType<ToolStripMenuItem>()
                    .First(candidate => string.Equals(candidate.Text, name, StringComparison.Ordinal));

                HeadlessHarness.Assert(
                    item.ShortcutKeys == Keys.None,
                    $"Edit ▸ {name} registers {item.ShortcutKeys}, which the menu will consume before "
                    + "the focused editor ever sees it.");
                HeadlessHarness.Assert(
                    !string.IsNullOrWhiteSpace(item.ShortcutKeyDisplayString),
                    $"Edit ▸ {name} no longer tells anyone what its shortcut is.");
            }
        });
    }

    // ── Canvas-growing paste ────────────────────────────────────────────────────

    private static void RunCanvasPasteCases(TestReport report)
    {
        HeadlessHarness.RunCase(report, "Editor.QoL.Paste.AnchorOffsets", () =>
        {
            Size content = new(2, 2);
            Size canvas = new(6, 4);
            Check(CanvasAnchor.Stay, 0, 0);
            Check(CanvasAnchor.TopLeft, 0, 0);
            Check(CanvasAnchor.Top, 2, 0);
            Check(CanvasAnchor.TopRight, 4, 0);
            Check(CanvasAnchor.Left, 0, 1);
            Check(CanvasAnchor.Right, 4, 1);
            Check(CanvasAnchor.BottomLeft, 0, 2);
            Check(CanvasAnchor.Bottom, 2, 2);
            Check(CanvasAnchor.BottomRight, 4, 2);

            void Check(CanvasAnchor anchor, int x, int y)
            {
                Point offset = CanvasResizeDialog.OffsetFor(anchor, content, canvas);
                HeadlessHarness.Assert(
                    offset == new Point(x, y),
                    $"Anchor {anchor} placed content at {offset.X},{offset.Y} rather than {x},{y}.");
            }
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Paste.GrowingKeepsPixelsAtTheAnchor", () =>
        {
            ImageWorkspace workspace = ImageWorkspace.CreateBlank(2, 2, Color.FromArgb(255, 10, 200, 40));
            workspace.ResizeCanvas(6, 4, new Point(4, 2));

            HeadlessHarness.Assert(
                workspace.Width == 6 && workspace.Height == 4,
                $"Canvas is {workspace.Width}x{workspace.Height} after growing to 6x4.");
            byte[] pixels = workspace.CompositeCurrentFrame();
            HeadlessHarness.AssertPixel(
                pixels, 6, 4, 4, 2, Color.FromArgb(255, 10, 200, 40), "Moved content");
            HeadlessHarness.AssertPixel(
                pixels, 6, 4, 0, 0, Color.FromArgb(0, 0, 0, 0), "Vacated area");
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Paste.ScalingDownAveragesRatherThanDrops", () =>
        {
            // Four 2x2 blocks of distinct colours halved: each target pixel is one whole block, so
            // a correct box filter reproduces the block colours exactly.
            byte[] source = new byte[4 * 4 * 4];
            Fill(source, 4, 0, 0, 2, 2, Color.FromArgb(255, 200, 0, 0));
            Fill(source, 4, 2, 0, 2, 2, Color.FromArgb(255, 0, 200, 0));
            Fill(source, 4, 0, 2, 2, 2, Color.FromArgb(255, 0, 0, 200));
            Fill(source, 4, 2, 2, 2, 2, Color.FromArgb(255, 200, 200, 0));

            byte[] scaled = RasterOperations.Resample(source, 4, 4, 2, 2);
            HeadlessHarness.AssertPixel(scaled, 2, 2, 0, 0, Color.FromArgb(255, 200, 0, 0), "Top-left block");
            HeadlessHarness.AssertPixel(scaled, 2, 2, 1, 0, Color.FromArgb(255, 0, 200, 0), "Top-right block");
            HeadlessHarness.AssertPixel(scaled, 2, 2, 0, 1, Color.FromArgb(255, 0, 0, 200), "Bottom-left block");
            HeadlessHarness.AssertPixel(scaled, 2, 2, 1, 1, Color.FromArgb(255, 200, 200, 0), "Bottom-right block");
        });

        HeadlessHarness.RunCase(report, "Editor.QoL.Paste.ExternalClipboardImageLands", () =>
        {
            ClipboardTestScope priorClipboard = ClipboardTestScope.Capture();
            try
            {
                ImageDocument document = ImageDocument.CreateDefault(16, 16);
                ImageDocumentSession session = new(document, null);
                ImageWorkspace workspace = ImageWorkspace.CreateBlank(16, 16, Color.Transparent);
                using ImageEditorControl editor = new(session, workspace);

                using Bitmap source = new(4, 4);
                using (Graphics graphics = Graphics.FromImage(source))
                {
                    graphics.Clear(Color.FromArgb(255, 12, 180, 240));
                }

                WithClipboardRetry(() => Clipboard.SetImage(source));
                HeadlessHarness.Assert(
                    editor.TryPasteFromSystemClipboard(),
                    "Ctrl+V with an image on the system clipboard did nothing — external art could not "
                    + "be pasted at all.");

                // Preview includes floating pixels; the saved composite must stay unchanged until placement.
                byte[] pixels = editor.GetPreviewPixels();
                Color landed = RasterOperations.GetPixel(pixels, 16, 16, 0, 0);
                HeadlessHarness.Assert(
                    landed.A > 0,
                    $"The pasted pixels are not on the canvas (got {landed}).");
                HeadlessHarness.Assert(workspace.CompositeCurrentFrame()[3] == 0,
                    "A floating paste polluted the saved composite cache before placement.");
                HeadlessHarness.Assert(editor.IsDirty, "A floating paste must trigger unsaved-change protection.");
                editor.CommitFloatingSelection();
                HeadlessHarness.AssertPixel(workspace.CompositeCurrentFrame(),16,16,0,0,Color.FromArgb(255,12,180,240),"Committed external paste");
                editor.Undo();
                HeadlessHarness.Assert(workspace.CompositeCurrentFrame()[3] == 0,"Undo did not remove placed clipboard pixels.");
            }
            finally
            {
                priorClipboard.Dispose();
            }
        });
    }

    private static string FirstError(ObjectSandboxResult result) =>
        result.Errors.Count == 0 ? "none" : result.Errors[0].Message;

    private static ResourceItem? FindResource(ResourceItem item, string path)
    {
        if (!item.IsFolder && string.Equals(
                Path.GetFullPath(item.FullPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        {
            return item;
        }

        foreach (ResourceItem child in item.Children)
        {
            ResourceItem? found = FindResource(child, path);
            if (found is not null) return found;
        }

        return null;
    }

    private static void Fill(byte[] rgba, int width, int x, int y, int w, int h, Color color)
    {
        for (int row = y; row < y + h; row++)
        {
            for (int column = x; column < x + w; column++)
            {
                int index = ((row * width) + column) * 4;
                rgba[index] = color.R;
                rgba[index + 1] = color.G;
                rgba[index + 2] = color.B;
                rgba[index + 3] = color.A;
            }
        }
    }

    private static void Pump(int iterations, int delay)
    {
        for (int index = 0; index < iterations; index++)
        {
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(delay);
        }
    }
}
