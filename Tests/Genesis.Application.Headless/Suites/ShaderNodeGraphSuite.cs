using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Genesis.Application.Core.Automation;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Shaders;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Studio.Theme;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Core;
using Genesis.Rendering.Primitives;
using Genesis.Shared.Assets;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// The Shader editor's node graph: every node kind writes HLSL that compiles for DX11 (DXBC) and
/// for the SPIR-V path Vulkan uses, on Mesh (surface shader), Sprite and Fullscreen targets; a
/// graph survives save and reopen in the resource; a shader without a graph saves as before.
/// </summary>
internal static class ShaderNodeGraphSuite
{
    private static readonly JsonSerializerOptions ResourceJson = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.Shader.Nodes");
        string logs = Path.Combine(ctx.Logs, "shader-nodes");
        Directory.CreateDirectory(logs);

        HeadlessHarness.RunCase(ctx.Report, "Editor.Shader.Nodes.EveryNodeCompilesOnEachTarget", () =>
        {
            ShaderGraphDocument graph = EveryNodeGraph(out int expectedVariables);
            foreach (ShaderAssetPipeline pipeline in new[] { ShaderAssetPipeline.Mesh, ShaderAssetPipeline.Sprite, ShaderAssetPipeline.Fullscreen })
            {
                ShaderGraphGenerationResult result = ShaderGraphGenerator.Generate(graph, pipeline);
                File.WriteAllText(Path.Combine(logs, "every-node-" + pipeline + ".hlsl"), result.Source);
                Assert(result.Succeeded, pipeline + " graph did not generate: " + string.Join(" | ", result.Errors));
                int variables = Regex.Matches(result.Source, @"\bg(\d+)_").Select(match => match.Groups[1].Value).Distinct().Count();
                Assert(variables == expectedVariables, $"{pipeline}: {variables} nodes wrote code, expected {expectedVariables} (every node but the texture parameter and the Output).");
                Assert(ShaderGraphGenerator.Generate(graph, pipeline).Source == result.Source, pipeline + ": the same graph made different code twice.");
                Assert(pipeline != ShaderAssetPipeline.Mesh || MeshSurfaceShaders.IsSurfaceSource(result.Source), "A Mesh graph did not make a surface shader.");
                Compile(result.Source, pipeline.ToString());
                IReadOnlyList<ReflectedShaderParameter> parameters = ShaderParameterReflection.Reflect(result.Source);
                Assert(parameters.Select(parameter => parameter.Name).OrderBy(name => name)
                        .SequenceEqual(new[] { "Offset", "Strength", "Tint" }),
                    pipeline + ": parameters were " + string.Join(", ", parameters.Select(parameter => parameter.Name)));
                Assert(ShaderResourceReflection.Reflect(result.Source).Any(resource => resource.Name == "Pattern"),
                    pipeline + ": the texture parameter was not declared.");
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Shader.Nodes.GraphErrorsAreReportedNotThrown", () =>
        {
            ShaderGraphDocument noOutput = new();
            ShaderGraphDocuments.AddNode(noOutput, "Time", 0, 0);
            Assert(!ShaderGraphGenerator.Generate(noOutput, ShaderAssetPipeline.Mesh).Succeeded, "A graph without an Output generated code.");

            ShaderGraphDocument loop = ShaderGraphDocuments.CreateStarter();
            ShaderGraphNode add = ShaderGraphDocuments.AddNode(loop, "Add", 0, 0);
            ShaderGraphNode sine = ShaderGraphDocuments.AddNode(loop, "Sin", 0, 0);
            Assert(ShaderGraphDocuments.Connect(loop, add.Id, "Out", sine.Id, "In"), "Add to Sine was refused.");
            Assert(!ShaderGraphDocuments.Connect(loop, sine.Id, "Out", add.Id, "A"), "A wire making a loop was accepted.");
            // A loop written into the file by hand is reported by the generator.
            loop.Connections.Add(new ShaderGraphConnection { FromNode = sine.Id, FromPort = "Out", ToNode = add.Id, ToPort = "A" });
            loop.Connections.Add(new ShaderGraphConnection { FromNode = add.Id, FromPort = "Out", ToNode = loop.Nodes.Single(node => node.Type == "Output").Id, ToPort = "Emission" });
            ShaderGraphGenerationResult looped = ShaderGraphGenerator.Generate(loop, ShaderAssetPipeline.Mesh);
            Assert(!looped.Succeeded && looped.Errors.Any(error => error.Contains("loops", StringComparison.Ordinal)), "A loop was not reported: " + string.Join(" | ", looped.Errors));

            ShaderGraphDocument badName = ShaderGraphDocuments.CreateStarter();
            badName.Nodes.Single(node => node.Type == "ColorParameter").Name = "texture";
            Assert(!ShaderGraphGenerator.Generate(badName, ShaderAssetPipeline.Sprite).Succeeded, "A reserved parameter name was accepted.");

            ShaderGraphDocument tooMany = ShaderGraphDocuments.CreateStarter();
            for (int index = 0; index < 5; index++) ShaderGraphDocuments.AddNode(tooMany, "VectorParameter", 0, 0);
            ShaderGraphGenerationResult full = ShaderGraphGenerator.Generate(tooMany, ShaderAssetPipeline.Mesh);
            Assert(!full.Succeeded && full.Errors.Any(error => error.Contains("16 numbers", StringComparison.Ordinal)), "Too many parameters were not reported: " + string.Join(" | ", full.Errors));

            ShaderGraphDocument unknown = ShaderGraphDocuments.CreateStarter();
            unknown.Nodes.Add(new ShaderGraphNode { Id = "x1", Type = "NoSuchNode" });
            Assert(!ShaderGraphGenerator.Generate(unknown, ShaderAssetPipeline.Mesh).Succeeded, "An unknown node kind was accepted.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Shader.Nodes.TextureTintFresnelSavesAndReopens", () =>
        {
            var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "ShaderNodesDocument"), "Shader nodes document");
            var resources = new ResourceService(project);
            string image = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, "Node pattern");
            string shader = resources.CreateResource(resources.AssetsRoot, ResourceKind.Shader, "Node shader");
            string imageName = ResourceNames.Name(project.RootPath, image);

            ShaderAssetDocument document = ShaderAssetDocument.ParseFlexible(File.ReadAllText(shader));
            document.TargetType = ShaderTargetType.Model;
            document.Pipeline = ShaderAssetPipeline.Mesh;
            document.Graph = TextureTintFresnel(imageName);
            ShaderGraphGenerationResult applied = ShaderGraphDocuments.Apply(document);
            Assert(applied.Succeeded, "Texture × tint + fresnel did not generate: " + string.Join(" | ", applied.Errors));
            Assert(document.Parameters.Single(parameter => parameter.Name == "Tint").Value.SequenceEqual(new[] { 1f, .55f, .3f, 1f }),
                "The Tint parameter did not take the node's colour.");
            Assert(document.Resources.Single(resource => resource.Name == "Pattern").Binding == imageName,
                "The texture parameter's Image was not bound.");
            File.WriteAllText(shader, JsonSerializer.Serialize(document, ResourceJson));
            File.WriteAllText(Path.Combine(logs, "texture-tint-fresnel.shader.json"), File.ReadAllText(shader));

            ShaderAssetDocument reopened = ShaderAssetDocument.Load(shader);
            Assert(reopened.Graph is not null, "The graph was not kept in the resource.");
            Assert(reopened.Graph!.Nodes.Count == document.Graph.Nodes.Count && reopened.Graph.Connections.Count == document.Graph.Connections.Count,
                "The reopened graph lost nodes or wires.");
            Assert(reopened.Graph.Nodes.Single(node => node.Name == "Pattern").Texture == imageName, "The texture node lost its Image.");
            Assert(reopened.Graph.Nodes.Single(node => node.Name == "RimColour").Value.SequenceEqual(new[] { .2f, .7f, 1f, 1f }), "A colour value changed on reopening.");
            Assert(reopened.Source == document.Source, "The saved code differs from the generated code.");
            Assert(ShaderGraphGenerator.Generate(reopened.Graph, reopened.Pipeline).Source == reopened.Source,
                "The reopened graph makes different code from what was saved.");
            Compile(reopened.Source, "texture × tint + fresnel");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Shader.Nodes.CodeShaderSavesWithoutGraph", () =>
        {
            ShaderAssetDocument code = new() { Source = "float4 MainPS() : SV_Target { return 1; }" };
            string json = JsonSerializer.Serialize(code, ResourceJson);
            Assert(!json.Contains("\"graph\"", StringComparison.Ordinal), "A shader written as code saved a graph field.");
            Assert(ShaderAssetDocument.ParseFlexible(json).Graph is null, "A shader written as code reopened with a graph.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Shader.Nodes.AutomationBuildsAShader", () =>
        {
            ProjectSession project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "ShaderNodesAutomation"), "Shader nodes automation");
            AutomationContext context = AutomationContext.For(project);
            context.Caller = "test";
            AutomationRegistry registry = AutomationRegistry.CreateDefault();
            AutomationResult Call(string name, string json) => registry.Invoke(context, name, JsonNode.Parse(json) as JsonObject);
            var resources = new ResourceService(project);
            string image = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, "Checker");
            WriteChecker(ResourceAssociates.FindPrimaryImage(image)
                ?? Path.Combine(Path.GetDirectoryName(image)!, ResourceAssociates.GetStem(image) + ".png"));

            AutomationResult nodes = Call("shader.nodes", "{}");
            Assert(nodes.Ok && nodes.Data?["nodes"] is JsonArray { Count: > 30 } listed
                && listed.OfType<JsonObject>().Any(node => node["type"]?.GetValue<string>() == "Fresnel"), "shader.nodes did not list the node types: " + nodes.Message);
            Assert(Call("resource.create", """{"kind":"Shader","name":"Rim"}""").Ok, "The Shader resource was not made.");
            AutomationResult built = Call("shader.graph", CookbookRimGraph);
            Assert(built.Ok, "shader.graph failed: " + built.Message);
            string path = Path.Combine(project.RootPath, built.Data!["path"]!.GetValue<string>());
            ShaderAssetDocument saved = ShaderAssetDocument.Load(path);
            Assert(saved.TargetType == ShaderTargetType.Model && saved.Pipeline == ShaderAssetPipeline.Mesh && saved.Graph?.Nodes.Count == 8,
                "shader.graph did not keep the target and the graph.");
            Assert(saved.Source == built.Data["source"]!.GetValue<string>() && saved.Source.Contains("GsgFresnel", StringComparison.Ordinal)
                && ShaderGraphGenerator.Generate(saved.Graph, saved.Pipeline).Source == saved.Source, "The saved code is not what the graph makes.");
            Assert(saved.Resources.Single(resource => resource.Name == "Pattern").Binding == ResourceNames.Name(project.RootPath, image),
                "The texture parameter's Image was not bound.");
            Assert(saved.Parameters.Single(parameter => parameter.Name == "RimColour").Value.SequenceEqual(new[] { .2f, .7f, 1f, 1f }),
                "The rim colour was not kept.");
            File.WriteAllText(Path.Combine(logs, "automation-rim.shader.json"), File.ReadAllText(path));

            AutomationResult loop = Call("shader.graph", """
                {"shader":"Rim","graph":{"nodes":[{"id":"o","type":"Output"},{"id":"a","type":"Add"},{"id":"b","type":"Sin"}],
                 "connections":[{"fromNode":"a","fromPort":"Out","toNode":"b","toPort":"In"},{"fromNode":"b","fromPort":"Out","toNode":"a","toPort":"A"}]}}
                """);
            Assert(!loop.Ok && loop.Message.Contains("loop", StringComparison.Ordinal), "A looped graph was accepted: " + loop.Message);
            Assert(!Call("shader.graph", """{"shader":"Rim","graph":{"nodes":[{"type":"NoSuchNode"}]}}""").Ok, "An unknown node type was accepted.");
            Assert(ShaderAssetDocument.Load(path).Source == saved.Source, "A refused graph changed the resource.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Shader.Nodes.ViewBuildsSavesReopensAndPreviews", () => RunEditorCase(ctx, logs));
    }

    /// <summary>The Cookbook's example (Documentation/Cookbook.md, "Make a shader from nodes"), word for word.</summary>
    private const string CookbookRimGraph = """
        {
          "shader": "Rim",
          "target": "Model",
          "graph": {
            "nodes": [
              { "id": "out", "type": "Output", "x": 720, "y": 60 },
              { "id": "pattern", "type": "TextureParameter", "name": "Pattern", "texture": "Checker", "x": 0, "y": 0 },
              { "id": "sample", "type": "SampleTexture", "x": 220, "y": 0 },
              { "id": "tint", "type": "ColorParameter", "name": "Tint", "value": [1, 0.55, 0.3, 1], "x": 220, "y": 200 },
              { "id": "colour", "type": "Multiply", "x": 480, "y": 40 },
              { "id": "rim", "type": "Fresnel", "inputs": { "Power": [2.5] }, "x": 220, "y": 340 },
              { "id": "rimColour", "type": "ColorParameter", "name": "RimColour", "value": [0.2, 0.7, 1, 1], "x": 220, "y": 480 },
              { "id": "glow", "type": "Multiply", "x": 480, "y": 380 }
            ],
            "connections": [
              { "fromNode": "pattern", "fromPort": "Texture", "toNode": "sample", "toPort": "Texture" },
              { "fromNode": "sample", "fromPort": "RGB", "toNode": "colour", "toPort": "A" },
              { "fromNode": "tint", "fromPort": "RGB", "toNode": "colour", "toPort": "B" },
              { "fromNode": "colour", "fromPort": "Out", "toNode": "out", "toPort": "Colour" },
              { "fromNode": "rim", "fromPort": "Out", "toNode": "glow", "toPort": "A" },
              { "fromNode": "rimColour", "fromPort": "RGB", "toNode": "glow", "toPort": "B" },
              { "fromNode": "glow", "fromPort": "Out", "toNode": "out", "toPort": "Emission" }
            ]
          }
        }
        """;

    /// <summary>
    /// The node view in the Shader editor on DX11: a code shader shows its code and an empty Nodes
    /// page; starting a graph replaces the code; texture × tint + fresnel emission is built through
    /// the editor's graph edits, undone and redone, previewed on a model, saved and reopened on its
    /// graph; a graph that cannot make a shader is a diagnostic; detaching keeps the code.
    /// </summary>
    private static void RunEditorCase(HeadlessContext ctx, string logs)
    {
        var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "ShaderNodesEditor"), "Shader nodes editor");
        var resources = new ResourceService(project);
        string shader = resources.CreateResource(resources.AssetsRoot, ResourceKind.Shader, "Rim shader");
        string codeShader = resources.CreateResource(resources.AssetsRoot, ResourceKind.Shader, "Code shader");
        string model = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Node ball");
        File.WriteAllText(model, """{"scale":1,"parts":[{"name":"Ball","primitive":"Sphere","position":[0,0.5,0],"scale":[1,1,1],"color":[1,1,1]}]}""");
        string image = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, "Node checker");
        WriteChecker(ResourceAssociates.FindPrimaryImage(image)
            ?? Path.Combine(Path.GetDirectoryName(image)!, ResourceAssociates.GetStem(image) + ".png"));
        string imageName = ResourceNames.Name(project.RootPath, image);

        RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
        try
        {
            RenderBackendSelection.Configure(RenderBackendOption.SilkNetDx11);
            string savedSource;
            int savedNodes;
            using (var editor = new ShaderEditorControl(shader, project.RootPath))
            using (var host = GateSuite.NewHost(1440, 900))
            {
                host.Controls.Add(editor);
                ThemeService.Apply(host);
                GateSuite.ShowHost(host);
                Warm(editor);
                editor.SetTargetType(ShaderTargetType.Model);
                Assert(editor.ChoosePreviewAsset(model), "The model was not offered as a preview target.");
                editor.ShowNodes();
                Warm(editor);
                Assert(editor.WorkspaceMode == "Nodes" && !editor.HasNodeGraph, "Nodes did not open on a shader written as code.");
                Assert(Find(editor, "ShaderStartNodeGraph") is { Visible: true }, "A code shader's Nodes page did not offer to start a graph.");
                Capture(ctx, host, "shader-nodes-empty");

                string codeBefore = editor.SourceText;
                editor.StartNodeGraph();
                Warm(editor);
                Assert(editor.HasNodeGraph && editor.LastCompileSucceeded, "Starting a graph did not compile: " + editor.DiagnosticText);
                Assert(editor.SourceText != codeBefore && editor.SourceText.Contains("GenesisSurface.hlsl", StringComparison.Ordinal),
                    "Starting a graph did not replace the code with a surface shader.");
                Assert(editor.NodesPreviewShown && editor.PreviewVisible, "The preview is not shown beside the graph.");

                // Texture × tint + fresnel emission, through the editor's own graph edits.
                ShaderGraphDocument graph = editor.NodeGraph!;
                string output = graph.Nodes.Single(node => node.Type == "Output").Id;
                string multiply = graph.Nodes.Single(node => node.Type == "Multiply").Id;
                string tint = graph.Nodes.Single(node => node.Type == "ColorParameter").Id;
                Assert(editor.RemoveGraphNodes(graph.Nodes.Single(node => node.Type == "MainTexture").Id), "The starter's picture node could not be removed.");
                string texture = Require(editor.AddGraphNode("TextureParameter", -220, -60), "texture parameter");
                string sample = Require(editor.AddGraphNode("SampleTexture", 20, -110), "sample texture");
                Assert(editor.SetGraphNodeName(texture, "Pattern"), "The texture parameter could not be named.");
                Assert(editor.SetGraphNodeTexture(texture, imageName), "The texture parameter did not take the Image.");
                Assert(editor.ConnectGraphNodes(texture, "Texture", sample, "Texture"), "Texture to Sample texture was refused.");
                Assert(editor.ConnectGraphNodes(sample, "RGB", multiply, "A"), "Sample to Multiply was refused.");
                Assert(editor.SetGraphNodeValue(tint, 1f, .55f, .3f, 1f), "The tint colour was not set.");
                Assert(editor.ParameterValue("Tint").SequenceEqual(new[] { 1f, .55f, .3f, 1f }), "The tint node and the Inspector's Tint disagree.");
                string fresnel = Require(editor.AddGraphNode("Fresnel", 40, 330), "fresnel");
                Assert(editor.SetGraphNodeInput(fresnel, "Power", [2.5f]), "Fresnel power was not set.");
                string rim = Require(editor.AddGraphNode("ColorParameter", 40, 460), "rim colour");
                Assert(editor.SetGraphNodeName(rim, "RimColour") && editor.SetGraphNodeValue(rim, .2f, .7f, 1f, 1f), "The rim colour was not set.");
                string glow = Require(editor.AddGraphNode("Multiply", 290, 380), "glow multiply");
                Assert(editor.ConnectGraphNodes(fresnel, "Out", glow, "A") && editor.ConnectGraphNodes(rim, "RGB", glow, "B"), "The rim wires were refused.");
                Assert(!editor.ConnectGraphNodes(texture, "Texture", glow, "B"), "A texture was wired into a number input.");
                Assert(!editor.SetGraphNodeName(rim, "Pattern"), "Two parameters were given one name.");
                Assert(editor.ConnectGraphNodes(glow, "Out", output, "Emission"), "Emission wire refused.");
                editor.NodeGraphView!.FrameAll();
                editor.CompileNow();
                Warm(editor);
                Assert(editor.LastCompileSucceeded && editor.LivePreviewApplied, "Texture × tint + fresnel did not compile and preview: " + editor.DiagnosticText);
                Assert(editor.SourceText.Contains("GsgFresnel", StringComparison.Ordinal) && editor.SourceText.Contains("Pattern.Sample(GenesisLinearWrap", StringComparison.Ordinal),
                    "The code view does not show what the graph makes.");
                Assert(editor.ResourceBinding("Pattern") == imageName, "The Inspector's Pattern texture is not the node's Image.");
                File.WriteAllText(Path.Combine(logs, "editor-graph.hlsl"), editor.SourceText);
                Capture(ctx, host, "shader-nodes-graph");

                // Undo takes the emission wire away (and its code); redo brings it back.
                int wires = editor.NodeGraph!.Connections.Count;
                string withEmission = editor.SourceText;
                editor.Undo();
                Warm(editor);
                Assert(editor.NodeGraph!.Connections.Count == wires - 1 && editor.SourceText != withEmission && editor.LastCompileSucceeded,
                    "Undo did not take back the last wire and its code.");
                editor.Redo();
                Warm(editor);
                Assert(editor.NodeGraph!.Connections.Count == wires && editor.SourceText == withEmission, "Redo did not bring the wire back.");
                Assert(editor.WorkspaceMode == "Nodes", "Undo or redo left the node view.");

                // A graph that cannot make a shader is a diagnostic, not an exception, and the code stays.
                // Tint and RimColour take two of GenesisParameters' four rows: two more vectors fit, a third does not.
                List<string> extra = [];
                for (int index = 0; index < 2; index++) extra.Add(Require(editor.AddGraphNode("VectorParameter", 760, 420 + index * 90), "vector"));
                editor.CompileNow();
                string fits = editor.SourceText;
                Assert(editor.LastCompileSucceeded && fits.Contains("Offset2", StringComparison.Ordinal), "Four parameter rows did not compile: " + editor.DiagnosticText);
                extra.Add(Require(editor.AddGraphNode("VectorParameter", 760, 600), "vector"));
                editor.CompileNow();
                Assert(!editor.LastCompileSucceeded && editor.NodeGraphErrors.Count > 0 && editor.DiagnosticText.Contains("Node graph", StringComparison.Ordinal),
                    "Too many parameters was not reported as a graph error: " + editor.DiagnosticText);
                Assert(editor.SourceText == fits, "A graph that cannot make a shader changed the code.");
                Assert(Find(editor, "ShaderNodesErrors") is { Visible: true }, "The node view did not show the graph's error.");
                Capture(ctx, host, "shader-nodes-error");
                Assert(editor.RemoveGraphNodes([.. extra]), "The extra parameters could not be removed.");
                editor.CompileNow();
                Warm(editor);
                Assert(editor.LastCompileSucceeded && editor.SourceText == withEmission, "Removing the extra parameters did not recover: " + editor.DiagnosticText);

                editor.Save();
                savedSource = editor.SourceText;
                savedNodes = editor.NodeGraph!.Nodes.Count;
                Assert(!editor.IsDirty, "Save left the shader marked changed.");
            }

            string json = File.ReadAllText(shader);
            File.WriteAllText(Path.Combine(logs, "editor-saved.shader.json"), json);
            ShaderAssetDocument saved = ShaderAssetDocument.ParseFlexible(json);
            Assert(saved.Graph is { } savedGraph && savedGraph.Nodes.Count == savedNodes && saved.Source == savedSource,
                "The saved resource does not hold the graph and its code.");
            Assert(ShaderGraphGenerator.Generate(saved.Graph, saved.Pipeline).Source == saved.Source, "The saved code is not what the saved graph makes.");
            Compile(saved.Source, "saved node shader");

            using (var reopened = new ShaderEditorControl(shader, project.RootPath))
            using (var host = GateSuite.NewHost(1440, 900))
            {
                host.Controls.Add(reopened);
                ThemeService.Apply(host);
                GateSuite.ShowHost(host);
                Warm(reopened);
                Assert(reopened.WorkspaceMode == "Nodes" && reopened.HasNodeGraph && reopened.NodeGraph!.Nodes.Count == savedNodes,
                    "The shader made from nodes did not reopen on its graph.");
                Assert(reopened.LastCompileSucceeded && reopened.SourceText == savedSource && !reopened.IsDirty,
                    "Reopening changed or failed the graph's code: " + reopened.DiagnosticText);
                Assert(reopened.ParameterValue("RimColour").SequenceEqual(new[] { .2f, .7f, 1f, 1f }), "The rim colour changed on reopening.");
                reopened.SetPreviewVisible(true);
                Warm(reopened);
                Assert(reopened.LivePreviewApplied, "The reopened shader's preview was not applied: " + reopened.DiagnosticText);
                Capture(ctx, host, "shader-nodes-preview");

                reopened.ShowNodes();
                reopened.DetachNodeGraph();
                Warm(reopened);
                Assert(!reopened.HasNodeGraph && reopened.WorkspaceMode == "Code" && reopened.SourceText == savedSource,
                    "Detaching did not keep the code and open it.");
                reopened.SetSource(savedSource.Replace("s.Emission =", "s.Emission = 0.5 *", StringComparison.Ordinal));
                reopened.CompileNow();
                Assert(reopened.LastCompileSucceeded, "Hand-edited code after detaching did not compile: " + reopened.DiagnosticText);
            }

            // A shader written as code opens as before and saves without a graph.
            using (var code = new ShaderEditorControl(codeShader, project.RootPath))
            using (var host = GateSuite.NewHost(1200, 800))
            {
                host.Controls.Add(code);
                ThemeService.Apply(host);
                GateSuite.ShowHost(host);
                Warm(code);
                Assert(!code.HasNodeGraph && code.WorkspaceMode != "Nodes" && code.LastCompileSucceeded, "A code shader opened differently: " + code.DiagnosticText);
                code.SetSource(code.SourceText + "\n// edited\n");
                code.Save();
            }
            Assert(!File.ReadAllText(codeShader).Contains("\"graph\"", StringComparison.Ordinal), "A code shader saved a graph.");
        }
        finally { RenderBackendSelection.Configure(previous); }
    }

    private static string Require(string? id, string what) => id ?? throw new InvalidOperationException($"The {what} node was not added.");

    private static Control? Find(Control root, string name) => root.Controls.Find(name, true).FirstOrDefault();

    private static void Warm(ShaderEditorControl editor)
    {
        GateSuite.Pump(3, 30);
        using var frame = editor.Viewport.CaptureFrame(5);
    }

    private static void Capture(HeadlessContext ctx, Form form, string name)
    {
        using var capture = VisualCapture.CaptureWindowPixels(form);
        capture.Save(Path.Combine(ctx.Captures, name + ".png"));
        var metrics = Genesis.Application.Runtime.ImageMetrics.Measure(capture);
        ctx.Report.Images.Add(new ImageResult("Shader nodes", name + ".png", metrics.Width, metrics.Height, metrics.UniqueSampledColors, metrics.AverageLuminance));
    }

    /// <summary>An 8 × 8 checker of white and dark blue squares, so a texture is plain to see on the ball.</summary>
    private static void WriteChecker(string path)
    {
        using var bitmap = new System.Drawing.Bitmap(128, 128);
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                bitmap.SetPixel(x, y, ((x / 16) + (y / 16)) % 2 == 0 ? System.Drawing.Color.White : System.Drawing.Color.FromArgb(40, 60, 120));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>The acceptance graph: a texture times a tint colour, with a fresnel rim as emission.</summary>
    internal static ShaderGraphDocument TextureTintFresnel(string image)
    {
        ShaderGraphDocument graph = new();
        ShaderGraphNode output = ShaderGraphDocuments.AddNode(graph, "Output", 760, 80);
        ShaderGraphNode texture = ShaderGraphDocuments.AddNode(graph, "TextureParameter", 20, 0);
        texture.Name = "Pattern";
        texture.Texture = image;
        ShaderGraphNode sample = ShaderGraphDocuments.AddNode(graph, "SampleTexture", 240, 0);
        ShaderGraphNode tint = ShaderGraphDocuments.AddNode(graph, "ColorParameter", 240, 220);
        tint.Value = [1f, .55f, .3f, 1f];
        ShaderGraphNode multiply = ShaderGraphDocuments.AddNode(graph, "Multiply", 500, 60);
        ShaderGraphNode fresnel = ShaderGraphDocuments.AddNode(graph, "Fresnel", 240, 360);
        fresnel.Inputs["Power"] = [2.5f];
        ShaderGraphNode rim = ShaderGraphDocuments.AddNode(graph, "ColorParameter", 240, 500);
        rim.Name = "RimColour";
        rim.Value = [.2f, .7f, 1f, 1f];
        ShaderGraphNode glow = ShaderGraphDocuments.AddNode(graph, "Multiply", 500, 400);
        Wire(graph, texture, "Texture", sample, "Texture");
        Wire(graph, sample, "RGB", multiply, "A");
        Wire(graph, tint, "RGB", multiply, "B");
        Wire(graph, multiply, "Out", output, "Colour");
        Wire(graph, fresnel, "Out", glow, "A");
        Wire(graph, rim, "RGB", glow, "B");
        Wire(graph, glow, "Out", output, "Emission");
        return graph;
    }

    /// <summary>One node of every kind, all reaching the Output, with a few wires that change types.</summary>
    private static ShaderGraphDocument EveryNodeGraph(out int expectedVariables)
    {
        ShaderGraphDocument graph = new();
        ShaderGraphNode output = ShaderGraphDocuments.AddNode(graph, "Output", 2000, 0);
        Dictionary<string, ShaderGraphNode> nodes = [];
        float y = 0;
        foreach (ShaderGraphNodeDefinition definition in ShaderGraphCatalog.Nodes)
        {
            if (definition.Role == ShaderGraphNodeRole.Output) continue;
            nodes[definition.Type] = ShaderGraphDocuments.AddNode(graph, definition.Type, 0, y += 120);
        }
        // Every texture mode, and a texture parameter feeding one of them.
        ShaderGraphNode data = ShaderGraphDocuments.AddNode(graph, "SampleTexture", 200, 0);
        data.Mode = "Data";
        ShaderGraphNode normalMap = ShaderGraphDocuments.AddNode(graph, "SampleTexture", 200, 120);
        normalMap.Mode = "Normal map";
        Wire(graph, nodes["TextureParameter"], "Texture", nodes["SampleTexture"], "Texture");
        Wire(graph, nodes["TextureParameter"], "Texture", normalMap, "Texture");
        Wire(graph, nodes["VectorParameter"], "Out", nodes["Split"], "In");
        Wire(graph, nodes["Normal"], "Out", nodes["Cross"], "A");
        Wire(graph, nodes["Time"], "Out", nodes["Combine"], "R");
        Wire(graph, nodes["UV"], "Out", nodes["Dot"], "A");
        Wire(graph, nodes["WorldPosition"], "Out", nodes["Dot"], "B");
        Wire(graph, nodes["ScreenUV"], "Out", data, "UV");
        Wire(graph, nodes["ObjectPosition"], "Out", nodes["Normalize"], "In");
        Wire(graph, nodes["ViewDirection"], "Out", nodes["Fresnel"], "View");
        Wire(graph, nodes["VertexColor"], "RGBA", nodes["Lerp"], "A");
        Wire(graph, nodes["MainTexture"], "RGB", nodes["Lerp"], "B");
        Wire(graph, nodes["Float"], "Out", nodes["Lerp"], "T");

        // Sum every node's first number or vector output, so each one is reached from the Output.
        string? sumNode = null, sumPort = null;
        int adds = 0;
        foreach (ShaderGraphNode node in graph.Nodes.ToList())
        {
            ShaderGraphNodeDefinition definition = ShaderGraphCatalog.Find(node.Type)!;
            if (definition.Role == ShaderGraphNodeRole.Output || definition.Outputs.All(port => port.Type == ShaderGraphValueType.Texture)) continue;
            string port = definition.Outputs.First(candidate => candidate.Type != ShaderGraphValueType.Texture).Name;
            if (sumNode is null) { sumNode = node.Id; sumPort = port; continue; }
            ShaderGraphNode add = ShaderGraphDocuments.AddNode(graph, "Add", 1000, adds++ * 60);
            Assert(ShaderGraphDocuments.Connect(graph, sumNode, sumPort!, add.Id, "A"), "Sum wire refused.");
            Assert(ShaderGraphDocuments.Connect(graph, node.Id, port, add.Id, "B"), $"Wire from {node.Type} refused.");
            sumNode = add.Id; sumPort = "Out";
        }
        Assert(ShaderGraphDocuments.Connect(graph, sumNode!, sumPort!, output.Id, "Colour"), "Colour wire refused.");
        Wire(graph, nodes["Normalize"], "Out", output, "Normal");
        Wire(graph, nodes["Fresnel"], "Out", output, "Emission");
        Wire(graph, nodes["Split"], "R", output, "Roughness");
        Wire(graph, nodes["Length"], "Out", output, "Metalness");
        Wire(graph, nodes["Saturate"], "Out", output, "Alpha");
        expectedVariables = graph.Nodes.Count - 2;
        return graph;
    }

    private static void Wire(ShaderGraphDocument graph, ShaderGraphNode from, string fromPort, ShaderGraphNode to, string toPort) =>
        Assert(ShaderGraphDocuments.Connect(graph, from.Id, fromPort, to.Id, toPort),
            $"{from.Type}.{fromPort} → {to.Type}.{toPort} was refused: {ShaderGraphDocuments.ConnectionProblem(graph, from.Id, fromPort, to.Id, toPort)}");

    /// <summary>Compiles a pixel shader for DX11 and for the SPIR-V path, as the Shader editor's compile does.</summary>
    internal static void Compile(string source, string label)
    {
        byte[] dxbc, spirv;
        try
        {
            dxbc = ShaderCompiler.Compile(source, "MainPS", "ps_5_0");
            spirv = ShaderCompiler.CompileForBackend(source, "MainPS", GpuShaderStage.Pixel, GpuShaderBinaryFormat.SpirV).Blob;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(label + " did not compile: " + exception.Message, exception);
        }
        Assert(dxbc.Length > 0 && spirv.Length > 0, label + " compiled to nothing.");
        Console.WriteLine($"  {label}: DXBC {dxbc.Length} B, SPIR-V {spirv.Length} B");
    }

    private static void Assert(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
