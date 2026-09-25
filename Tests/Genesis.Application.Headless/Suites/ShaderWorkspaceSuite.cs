using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Studio.Theme;
using Genesis.Rendering.Core;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Assets;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless.Suites;

internal static class ShaderWorkspaceSuite
{
    public static void Run(HeadlessContext ctx)
    {
        var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "ShaderWorkspace"), "Shader workspace");
        var resources = new ResourceService(project);
        string shader = resources.CreateResource(resources.AssetsRoot, ResourceKind.Shader, "Workspace shader");
        string model = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Offset model");
        File.WriteAllText(model, """{"scale":1,"parts":[{"name":"Offset box","primitive":"Cube","position":[4,-3,2],"scale":[1,3,1],"color":[0.7,0.8,1]}]}""");
        string picture = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, "Preview image");
        string terrain = resources.CreateResource(resources.AssetsRoot, ResourceKind.Terrain, "Preview terrain");
        string object2D = resources.CreateResource(resources.AssetsRoot, ResourceKind.GameObject, "2D object");
        string object3D = resources.CreateResource(resources.AssetsRoot, ResourceKind.GameObject, "3D object");
        WriteObject(object2D, "TwoD", "SpriteComponent", new JObject { ["Sprite"] = Relative(picture) });
        WriteObject(object3D, "ThreeD", "ModelRendererComponent", new JObject { ["ModelAsset"] = Relative(model), ["ScaleX"] = 2, ["ScaleY"] = 1, ["ScaleZ"] = 1 });
        string terrainEntity = Path.Combine(resources.AssetsRoot, "Terrain model.terrainentity.json");
        WriteObject(terrainEntity, "ThreeD", "Model", new JObject { ["Model"] = Relative(model) });
        JObject terrainDocument = JObject.Parse(File.ReadAllText(terrain));
        terrainDocument["entities"] = new JArray(Relative(terrainEntity));
        File.WriteAllText(terrain, terrainDocument.ToString());
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.Shader.Workspace");
        RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
        try
        {
            foreach (var backend in new[] { RenderBackendOption.SilkNetDx11, RenderBackendOption.Direct3D12, RenderBackendOption.Vulkan, RenderBackendOption.OpenGL, RenderBackendOption.Software })
            {
                HeadlessHarness.RunCase(ctx.Report, "Editor.Shader.Workspace." + backend, () =>
                {
                    RenderBackendSelection.Configure(backend);
                    using var editor = new ShaderEditorControl(shader, project.RootPath);
                    using var host = backend == RenderBackendOption.Software ? GateSuite.NewHost(640, 480) : GateSuite.NewHost(1440, 900);
                    // Exercise readback allocation padding rather than only aligned viewport sizes.
                    if (backend == RenderBackendOption.Vulkan) host.ClientSize = new Size(1441, 901);
                    host.Controls.Add(editor);
                    ThemeService.Apply(host);
                    GateSuite.ShowHost(host);
                    Warm(editor);
                    Assert(!editor.IsPreviewPlaying && editor.PreviewVisible, "Visual preview must open paused.");
                    foreach (string preset in editor.PresetNames)
                    {
                        Console.WriteLine("  Preview: " + preset);
                        Assert(editor.SelectPreset(preset), "Preset missing: " + preset);
                        editor.CompileNow();
                        Warm(editor);
                        Assert(editor.LastCompileSucceeded, preset + ": " + editor.DiagnosticText);
                        if (backend == RenderBackendOption.Software)
                            Assert(!editor.LivePreviewApplied && !editor.SupportsAuthoredShaderPreview
                                && editor.DiagnosticText.Contains("Software previews geometry only", StringComparison.Ordinal),
                                "Software must explain its shader execution limit instead of claiming an applied effect.");
                        else
                            Assert(editor.LivePreviewApplied && editor.SupportsAuthoredShaderPreview, preset + ": " + editor.DiagnosticText);
                    }
                    if (backend == RenderBackendOption.Vulkan)
                    {
                        editor.SelectPreset("Model Rainbow");
                        editor.SetTargetType(ShaderTargetType.Image);
                        editor.CompileNow(); Warm(editor);
                        Assert(!editor.LastCompileSucceeded && editor.DiagnosticsVisible && editor.DiagnosticText.Contains("incompatible between stages"), "An incompatible shader reached the Vulkan driver instead of becoming an in-app diagnostic.");
                    }
                    editor.SetTargetType(ShaderTargetType.Model);
                    editor.SelectPreset("Model Rainbow");
                    Assert(editor.ChoosePreviewAsset(model), "Cannot select model asset.");
                    editor.SetFloorStyle(EditorFloorStyle.Plain);
                    Warm(editor);
                    {
                        var renderer = new RuntimeModelRenderSystem();
                        Assert(renderer.TryGetBounds(project.RootPath, Relative(model), out Vector3 min, out Vector3 max), "Fixture bounds unavailable.");
                        Vector3 ground = Vector3.Transform(new Vector3((min.X + max.X) / 2, min.Y, (min.Z + max.Z) / 2), editor.PreviewModelTransform);
                        Assert(ground.Length() < .001f, "Model was not centered with its base on the floor: " + ground);
                    }
                    editor.SelectPreset("Terrain Tint");
                    Assert(editor.ChoosePreviewAsset(terrain), "Cannot choose a terrain resource.");
                    Assert(editor.TryApplyInspectorValue("TargetComponent", "entity:" + Relative(terrainEntity)), "Cannot choose the terrain's model entity.");
                    Warm(editor);
                    Assert(editor.PreviewObjectModel == Relative(model) && editor.LastCompileSucceeded, "Terrain entity did not preview its model.");
                    editor.SelectPreset("Model Rainbow");
                    editor.SetTargetType(ShaderTargetType.Object);
                    Console.WriteLine("  Preview: 2D Object");
                    Assert(editor.ChoosePreviewAsset(object2D), "2D object missing from picker.");
                    Warm(editor);
                    Assert(editor.Pipeline == ShaderAssetPipeline.Sprite && editor.Viewport.Mode2D, "2D Object did not choose a sprite preview.");
                    Console.WriteLine("  Preview: 3D Object");
                    Assert(editor.ChoosePreviewAsset(object3D), "3D object missing from picker.");
                    editor.CompileNow();
                    Warm(editor);
                    Assert(editor.Pipeline == ShaderAssetPipeline.Mesh && !editor.Viewport.Mode2D && editor.LastCompileSucceeded, "3D Object did not choose a working model preview: " + editor.DiagnosticText);
                    Assert(editor.PreviewObjectModel == Relative(model), "The Object model component was not resolved.");
                    Assert(!editor.IsPreviewPlaying, "Changing targets unexpectedly started playback.");
                    editor.SetPlayback(true); GateSuite.Pump(3, 30);
                    Assert(editor.IsPreviewPlaying && editor.PreviewFrame > 0, "Play did not advance shader time.");
                    editor.SetPlayback(false);
                    int pausedFrame = editor.PreviewFrame;
                    GateSuite.Pump(3, 30);
                    Assert(editor.PreviewFrame == pausedFrame, "Pause advanced shader time.");
                    editor.ResetPlayback();
                    Assert(editor.PreviewFrame == 0 && !editor.IsPreviewPlaying, "Stop did not rewind and pause.");
                    editor.Save();
                    Console.WriteLine("  Save/reopen Object target");
                    using (var reopened = new ShaderEditorControl(shader, project.RootPath))
                        Assert(reopened.TargetType == ShaderTargetType.Object && reopened.PreviewAsset == Relative(object3D) && reopened.Pipeline == ShaderAssetPipeline.Mesh, "Object preview did not survive save/reopen.");
                    ValidateLayout(editor, compact: backend == RenderBackendOption.Software);
                    Capture(host, "shader-object-" + backend);
                    if (backend != RenderBackendOption.OpenGL) return;
                    foreach (Size size in new[] { new Size(1440, 900), new Size(1024, 740), new Size(800, 650) })
                    {
                        host.ClientSize = size;
                        editor.SelectPreset("Terrain Tint"); Assert(editor.ChoosePreviewAsset(terrain), "Terrain selection failed.");
                        editor.TryApplyInspectorValue("TargetComponent", "all");
                        Warm(editor); ValidateLayout(editor);
                        Capture(host, "shader-terrain-" + size.Width);
                    }
                    host.ClientSize = new Size(1440, 900);
                    editor.SetTargetType(ShaderTargetType.Model);
                    editor.SelectPreset("Model Rainbow"); Assert(editor.ChoosePreviewAsset(model), "Model selection failed.");
                    const string realModel = @"F:\Development\Game Development\Shared\Models\Vulipet.glb";
                    if (File.Exists(realModel))
                    {
                        StudioModelResourceLoader.SaveCanonical(model, ExternalModelImporter.Import(realModel, project.RootPath, model));
                        editor.ChoosePreviewAsset(string.Empty); editor.ChoosePreviewAsset(model);
                    }
                    editor.SetFloorStyle(EditorFloorStyle.GridOnly);
                    Warm(editor); ValidateLayout(editor); Capture(host, "shader-visual-model");
                    editor.SetFloorStyle(EditorFloorStyle.Plain);
                    Warm(editor); Capture(host, "shader-model-grounded");
                    editor.SetFloorStyle(EditorFloorStyle.GridOnly);
                    editor.Controls.Find("ShaderPresetsToggle", true).OfType<Button>().Single().PerformClick();
                    Capture(host, "shader-presets");
                    editor.SetAuthoringMode(ShaderAuthoringMode.Code);
                    Warm(editor); Capture(host, "shader-code-model");
                    foreach (Size size in new[] { new Size(1024, 740), new Size(800, 650) })
                    {
                        host.ClientSize = size;
                        Warm(editor);
                        ValidateLayout(editor, stackedCode: true);
                        Control source = editor.Controls.Find("ShaderCodeSurface", true).Single();
                        Assert(source.Visible && source.Width >= 600 && source.Height >= 160,
                            "Narrow code workspace hid or crowded out the source editor.");
                        Assert(editor.PreviewVisible && editor.Viewport.Visible,
                            "Narrow code workspace hid the shader's live preview.");
                        Capture(host, "shader-code-" + size.Width);
                    }
                    host.ClientSize = new Size(1440, 900);
                    Warm(editor);
                    string valid = editor.SourceText;
                    editor.SetSource(valid.Replace("float4 MainPS(", "float4 BrokenPS(", StringComparison.Ordinal));
                    editor.CompileNow();
                    Assert(!editor.LastCompileSucceeded && editor.DiagnosticsVisible && editor.LivePreviewApplied, "Bad source did not show diagnostics while retaining the last working preview.");
                    Capture(host, "shader-diagnostics");
                    editor.Undo(); Warm(editor);
                    Assert(editor.LastCompileSucceeded, "Undo did not recover from compile failure.");
                    editor.SetSource(valid
                        .Replace("float Speed; float Saturation;", "float Speed; float Saturation; float4 Tint;", StringComparison.Ordinal)
                        .Replace("tex.rgb * HsvToRgb", "tex.rgb * Tint.rgb * HsvToRgb", StringComparison.Ordinal));
                    editor.CompileNow();
                    Assert(editor.SetParameterValue("Tint", 1f, .8f, .6f, 1f), "Vector parameter was not exposed.");
                    editor.SetAuthoringMode(ShaderAuthoringMode.Preset);
                    editor.Controls.Find("ShaderPresetsToggle", true).OfType<Button>().Single().PerformClick();
                    Warm(editor);
                    Control tintCard = editor.Controls.Find("ShaderParameterCard", true).Single(card =>
                        card.Controls.OfType<Label>().Any(label => label.Text == "Tint"));
                    TableLayoutPanel vectorRows = tintCard.Controls.OfType<TableLayoutPanel>().Single();
                    NumericUpDown[] vectorInputs = vectorRows.Controls.OfType<NumericUpDown>().ToArray();
                    Assert(vectorInputs.Length == 4 && vectorInputs.All(input => vectorRows.ClientRectangle.Contains(input.Bounds)),
                        "A vector parameter field is clipped by its card.");
                    vectorInputs[3].Value = .5m;
                    Assert(editor.ParameterValue("Tint")[3] == .5f, "The fourth vector field did not update the shader.");
                    Capture(host, "shader-controls-vector");
                    editor.Undo();
                    Assert(editor.ParameterValue("Tint")[3] == 1f, "Undo did not restore the edited vector field.");
                });
            }
        }
        finally { RenderBackendSelection.Configure(previous); }

        string Relative(string path) => Path.GetRelativePath(project.RootPath, path).Replace('\\', '/');
        void Capture(Form form, string name)
        {
            using var capture = VisualCapture.CaptureWindowPixels(form);
            capture.Save(Path.Combine(ctx.Captures, name + ".png"));
            var metrics = Genesis.Application.Runtime.ImageMetrics.Measure(capture);
            ctx.Report.Images.Add(new ImageResult("Shader workspace", name + ".png", metrics.Width, metrics.Height, metrics.UniqueSampledColors, metrics.AverageLuminance));
        }
    }

    private static void WriteObject(string path, string dimension, string component, JObject props) =>
        File.WriteAllText(path, new JObject { ["dimension"] = dimension, ["components"] = new JArray(new JObject { ["type"] = component, ["enabled"] = true, ["props"] = props }) }.ToString());

    private static void Warm(ShaderEditorControl editor)
    {
        GateSuite.Pump(3, 30);
        using var frame = editor.Viewport.CaptureFrame(5);
    }

    private static void ValidateLayout(ShaderEditorControl editor, bool compact = false, bool stackedCode = false)
    {
        var menus = (ToolStrip)editor.Controls.Find("ShaderMenus", true).Single();
        Assert(menus.Items.Cast<ToolStripItem>().All(item => item.Height >= item.Font.Height + 4), "File/Edit menu text is clipped vertically.");
        Control setup = editor.Controls.Find("ShaderPreviewSetup", true).Single();
        foreach (Control control in setup.Controls.Cast<Control>().Where(c => c.Visible))
            Assert(setup.ClientRectangle.Contains(control.Bounds), "Target control is clipped: " + control.Name);
        Control transport = editor.Controls.Find("ShaderTransport", true).Single();
        Assert(editor.Viewport.Width >= (compact ? 320 : 420) && editor.Viewport.Height >= (compact || stackedCode ? 160 : 280), "Controls crowded out the shader preview.");
        Assert(transport.Visible && transport.Height >= 40, "Playback controls are clipped.");
        Assert(transport.Controls.OfType<Button>().Select(b => b.Text).Order().SequenceEqual(new[] { "Play", "Stop" }), "Transport contains unexpected controls.");
    }

    private static void Assert(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
