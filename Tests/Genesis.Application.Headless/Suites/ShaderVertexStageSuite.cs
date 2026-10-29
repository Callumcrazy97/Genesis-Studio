using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;
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
/// A surface shader's vertex stage (<c>void Vertex(GenesisVertexInput i, inout GenesisVertex v)</c>):
/// it compiles into the engine's vertex shaders (static, skinned, shadow) for DXBC and SPIR-V;
/// without it the engine's vertex shaders compile to the same bytes as before; the node view's
/// Vertex offset writes one; and the Shader editor's preview of a mesh moves with it.
/// </summary>
internal static class ShaderVertexStageSuite
{
    private static readonly GpuShaderBinaryFormat[] Formats = [GpuShaderBinaryFormat.Dxbc, GpuShaderBinaryFormat.SpirV];

    /// <summary>Every input the vertex stage offers, and one of the shader's own parameters.</summary>
    private const string EveryInputSource = """
        #include "GenesisSurface.hlsl"
        cbuffer GenesisParameters : register(b5) { float Lift; float4 Tint; };

        void Vertex(GenesisVertexInput i, inout GenesisVertex v)
        {
            float wave = sin(i.Time + i.WorldPosition.x + i.ObjectPosition.z + length(i.CameraPosition - i.WorldPosition));
            v.Position += v.Normal * wave * Lift;
            v.Position.xz += i.Wind * 0.01 + i.ObjectWind * 0.01;
            v.UV += float2(0.1, 0.0) * wave;
            v.Color *= Tint;
        }

        void Surface(GenesisSurfaceInput i, inout GenesisSurface s) { s.Albedo *= GenesisColor(Tint.rgb); }
        """;

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.Shader.VertexStage");
        string logs = Path.Combine(ctx.Logs, "shader-vertex-stage");
        Directory.CreateDirectory(logs);

        HeadlessHarness.RunCase(ctx.Report, "Editor.Shader.VertexStage.CompilesForDxbcAndSpirV", () =>
        {
            Assert(MeshSurfaceShaders.HasVertexStage(EveryInputSource), "The Vertex function was not seen.");
            string composed = MeshSurfaceShaders.Compose(EveryInputSource, "every-input.shader.json");
            File.WriteAllText(Path.Combine(logs, "every-input.composed.hlsl"), composed);
            Assert(composed.Contains("#define GENESIS_SURFACE_VERTEX 1", StringComparison.Ordinal)
                && composed.IndexOf("struct GenesisVertexInput", StringComparison.Ordinal) < composed.IndexOf("VSOut VS(VSIn IN", StringComparison.Ordinal),
                "The vertex contract is not placed before the engine's vertex shaders.");
            CompileStages(EveryInputSource, "every input");

            List<ShaderPresetDefinition> stagePresets = ShaderPresetLibrary.BuiltIns.Where(preset => MeshSurfaceShaders.HasVertexStage(preset.Source)).ToList();
            Assert(stagePresets.Select(preset => preset.Name).OrderBy(name => name).SequenceEqual(new[] { "Wave", "Wind Sway" }),
                "The vertex stage presets are " + string.Join(", ", stagePresets.Select(preset => preset.Name)));
            foreach (ShaderPresetDefinition preset in stagePresets) CompileStages(preset.Source, preset.Name);

            // The Code page's template compiles as it is added.
            string template = "#include \"GenesisSurface.hlsl\"\nvoid Surface(GenesisSurfaceInput i, inout GenesisSurface s) { }\n\n"
                + ShaderEditorControl.VertexFunctionTemplate.Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert(MeshSurfaceShaders.HasVertexStage(template), "The Code page's template is not a vertex stage.");
            CompileStages(template, "Code page template");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Shader.VertexStage.WithoutItTheCodeIsUnchanged", () =>
        {
            // The engine's vertex shaders with the hooks taken out are the engine's vertex shaders
            // before 29 October; without GENESIS_SURFACE_VERTEX they compile to the same bytes.
            string engine = ForwardShaders.Source;
            Regex hook = new(@"#if defined\(GENESIS_SURFACE_VERTEX\)[\s\S]*?#endif\r?\n");
            Assert(hook.Matches(engine).Count == 6, $"Expected six vertex hooks in the forward shader, found {hook.Matches(engine).Count}.");
            string before = hook.Replace(engine, string.Empty);
            foreach (GpuShaderBinaryFormat format in Formats)
            {
                foreach (string entry in new[] { "VS", "VS_Skinned", "VS_Shadow", "VS_ShadowSkinned" })
                {
                    byte[] now = ShaderCompiler.CompileForBackend(engine, entry, GpuShaderStage.Vertex, format).Blob;
                    byte[] then = ShaderCompiler.CompileForBackend(before, entry, GpuShaderStage.Vertex, format).Blob;
                    Assert(now.AsSpan().SequenceEqual(then), $"{format} {entry} differs from the engine's vertex shader without the hooks ({now.Length} and {then.Length} bytes).");
                }
            }

            const string plain = "#include \"GenesisSurface.hlsl\"\nvoid Surface(GenesisSurfaceInput i, inout GenesisSurface s) { s.Roughness = 0.2; }\n";
            string composed = MeshSurfaceShaders.Compose(plain);
            Assert(!MeshSurfaceShaders.HasVertexStage(plain) && !composed.Contains("GENESIS_SURFACE_VERTEX", StringComparison.Ordinal)
                && !composed.Contains("GenesisVertexInput", StringComparison.Ordinal), "A surface shader without Vertex was given the vertex stage.");
            Assert(!MeshSurfaceShaders.HasVertexStage("// void Vertex(\n" + plain), "A Vertex function in a comment counted.");
            ShaderGraphGenerationResult graph = ShaderGraphGenerator.Generate(ShaderNodeGraphSuite.TextureTintFresnel("Checker"), ShaderAssetPipeline.Mesh);
            Assert(graph.Succeeded && !graph.Source.Contains("void Vertex", StringComparison.Ordinal), "A graph without a Vertex offset wrote a Vertex function.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Shader.VertexStage.NodeVertexOffsetWritesTheFunction", () =>
        {
            ShaderGraphDocument graph = ShaderGraphDocuments.CreateStarter();
            string output = graph.Nodes.Single(node => node.Type == ShaderGraphCatalog.OutputType).Id;
            ShaderGraphNode normal = ShaderGraphDocuments.AddNode(graph, "Normal", 0, 300);
            ShaderGraphNode time = ShaderGraphDocuments.AddNode(graph, "Time", 0, 400);
            ShaderGraphNode sine = ShaderGraphDocuments.AddNode(graph, "Sin", 200, 400);
            ShaderGraphNode lift = ShaderGraphDocuments.AddNode(graph, "FloatParameter", 200, 500);
            lift.Name = "Lift";
            ShaderGraphNode scale = ShaderGraphDocuments.AddNode(graph, "Multiply", 400, 400);
            ShaderGraphNode offset = ShaderGraphDocuments.AddNode(graph, "Multiply", 600, 350);
            Assert(ShaderGraphDocuments.Connect(graph, time.Id, "Out", sine.Id, "In")
                && ShaderGraphDocuments.Connect(graph, sine.Id, "Out", scale.Id, "A")
                && ShaderGraphDocuments.Connect(graph, lift.Id, "Out", scale.Id, "B")
                && ShaderGraphDocuments.Connect(graph, normal.Id, "Out", offset.Id, "A")
                && ShaderGraphDocuments.Connect(graph, scale.Id, "Out", offset.Id, "B")
                && ShaderGraphDocuments.Connect(graph, offset.Id, "Out", output, ShaderGraphCatalog.VertexOffsetPort), "A vertex offset wire was refused.");

            ShaderGraphGenerationResult mesh = ShaderGraphGenerator.Generate(graph, ShaderAssetPipeline.Mesh);
            File.WriteAllText(Path.Combine(logs, "node-vertex-offset.hlsl"), mesh.Source);
            Assert(mesh.Succeeded, "The vertex offset graph did not generate: " + string.Join(" | ", mesh.Errors));
            Assert(MeshSurfaceShaders.HasVertexStage(mesh.Source) && mesh.Source.Contains("v.Position +=", StringComparison.Ordinal)
                && mesh.Source.Contains("= v.Normal;", StringComparison.Ordinal) && mesh.Source.Contains("= i.Time;", StringComparison.Ordinal),
                "The Vertex offset did not become a Vertex function reading the vertex's own normal and the time.");
            Assert(ShaderGraphGenerator.Generate(graph, ShaderAssetPipeline.Mesh).Source == mesh.Source, "The same graph made different code twice.");
            CompileStages(mesh.Source, "node vertex offset");

            ShaderGraphGenerationResult sprite = ShaderGraphGenerator.Generate(graph, ShaderAssetPipeline.Sprite);
            Assert(sprite.Succeeded && !sprite.Source.Contains("Vertex(", StringComparison.Ordinal)
                && sprite.Warnings.Any(warning => warning.Contains("Vertex offset", StringComparison.Ordinal)), "A sprite graph did not leave the Vertex offset out with a warning.");

            ShaderGraphNode texture = ShaderGraphDocuments.AddNode(graph, "SampleTexture", 400, 600);
            Assert(ShaderGraphDocuments.Connect(graph, texture.Id, "RGB", offset.Id, "B"), "Texture to offset wire refused.");
            ShaderGraphGenerationResult textured = ShaderGraphGenerator.Generate(graph, ShaderAssetPipeline.Mesh);
            Assert(!textured.Succeeded && textured.Errors.Any(error => error.Contains("Vertex offset cannot read a texture", StringComparison.Ordinal)),
                "A texture feeding the Vertex offset was not reported: " + string.Join(" | ", textured.Errors));
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Shader.VertexStage.PreviewMovesTheMesh", () => RunPreviewCase(ctx));
    }

    /// <summary>
    /// DX11: the Wave preset on a ball against the same shader with its Vertex function taken out: the
    /// two previews differ, and the shader's parameters reach the stage.
    /// </summary>
    private static void RunPreviewCase(HeadlessContext ctx)
    {
        var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "ShaderVertexStage"), "Shader vertex stage");
        var resources = new ResourceService(project);
        string shader = resources.CreateResource(resources.AssetsRoot, ResourceKind.Shader, "Wave shader");
        string model = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Wave ball");
        File.WriteAllText(model, """{"scale":1,"parts":[{"name":"Ball","primitive":"Sphere","position":[0,0.5,0],"scale":[1,1,1],"color":[0.85,0.85,0.9]}]}""");

        RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
        try
        {
            RenderBackendSelection.Configure(RenderBackendOption.SilkNetDx11);
            using var editor = new ShaderEditorControl(shader, project.RootPath);
            using var host = GateSuite.NewHost(1200, 800);
            host.Controls.Add(editor);
            ThemeService.Apply(host);
            GateSuite.ShowHost(host);
            Warm(editor);
            Assert(editor.SelectPreset("Wave") && editor.ChoosePreviewAsset(model), "The Wave preset or the ball could not be chosen.");
            Assert(editor.SetParameterValue("Amplitude", 0.3f) && editor.SetParameterValue("Wavelength", 0.7f), "The Wave parameters are not exposed.");
            editor.CompileNow();
            Warm(editor);
            Assert(editor.LastCompileSucceeded && editor.LivePreviewApplied && editor.HasVertexStage
                && editor.DiagnosticText.Contains("engine VS with Vertex()", StringComparison.Ordinal),
                "The Wave preset did not compile its vertex stage and preview: " + editor.DiagnosticText);
            using Bitmap moved = Require(editor.Viewport.CaptureFrame(6));
            moved.Save(Path.Combine(ctx.Captures, "shader-vertex-wave.png"));

            // The same shader without its Vertex function: no vertex stage, same surface and parameters.
            string withoutStage = Regex.Replace(editor.SourceText, @"void Vertex\(GenesisVertexInput i, inout GenesisVertex v\)\s*\{.*?\r?\n\}", string.Empty, RegexOptions.Singleline);
            Assert(withoutStage != editor.SourceText, "The Wave preset's Vertex function was not found to take out.");
            editor.SetSource(withoutStage);
            editor.CompileNow();
            Warm(editor);
            Assert(editor.LastCompileSucceeded && !editor.HasVertexStage, "The shader without its vertex stage did not compile: " + editor.DiagnosticText);
            using Bitmap still = Require(editor.Viewport.CaptureFrame(6));
            still.Save(Path.Combine(ctx.Captures, "shader-vertex-none.png"));

            int changed = 0, total = 0;
            int width = Math.Min(moved.Width, still.Width), height = Math.Min(moved.Height, still.Height);
            for (int y = 0; y < height; y += 2)
            {
                for (int x = 0; x < width; x += 2)
                {
                    Color a = moved.GetPixel(x, y), b = still.GetPixel(x, y);
                    total++;
                    if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 36) changed++;
                }
            }
            double share = total == 0 ? 0 : changed / (double)total;
            Console.WriteLine($"  vertex stage preview: {share:P1} of pixels differ");
            Assert(share > 0.01, $"The preview with a vertex stage looks like the one without ({share:P2} of pixels differ).");

            // The Code page's Vertex button adds an empty function to a surface shader without one.
            Assert(editor.AddVertexFunction() && editor.HasVertexStage && !editor.AddVertexFunction(), "The Code page could not add a Vertex function once.");
            editor.CompileNow();
            Assert(editor.LastCompileSucceeded, "The added Vertex function did not compile: " + editor.DiagnosticText);
            editor.Undo();
            Assert(!editor.HasVertexStage, "Undo did not take the added Vertex function away.");
        }
        finally { RenderBackendSelection.Configure(previous); }
    }

    /// <summary>The engine's vertex shaders (static, skinned, sun shadow, skinned shadow) and the pixel shader, for DXBC and SPIR-V.</summary>
    private static void CompileStages(string source, string label)
    {
        foreach (GpuShaderBinaryFormat format in Formats)
        {
            int bytes = 0;
            foreach (string entry in new[] { MeshSurfaceShaders.VertexEntry, MeshSurfaceShaders.SkinnedVertexEntry, "VS_Shadow", "VS_ShadowSkinned" })
            {
                try { bytes += ShaderCompiler.CompileForBackend(source, entry, GpuShaderStage.Vertex, format).Blob.Length; }
                catch (Exception exception) { throw new InvalidOperationException($"{label}: {format} {entry} did not compile: {exception.Message}", exception); }
            }
            try { bytes += ShaderCompiler.CompileForBackend(source, "MainPS", GpuShaderStage.Pixel, format).Blob.Length; }
            catch (Exception exception) { throw new InvalidOperationException($"{label}: {format} pixel shader did not compile: {exception.Message}", exception); }
            Console.WriteLine($"  {label}: {format} {bytes} B (VS, VS_Skinned, VS_Shadow, VS_ShadowSkinned, PS)");
        }
    }

    private static Bitmap Require(Bitmap? bitmap) => bitmap ?? throw new InvalidOperationException("The preview could not be read back.");

    private static void Warm(ShaderEditorControl editor)
    {
        GateSuite.Pump(3, 30);
        using var frame = editor.Viewport.CaptureFrame(5);
    }

    private static void Assert(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
