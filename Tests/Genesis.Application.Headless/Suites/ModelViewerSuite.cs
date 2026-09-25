using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Modeling;
using Assimp;

namespace Genesis.Application.Headless.Suites;

internal static class ModelViewerSuite
{
    public static void Run(HeadlessContext ctx)
    {
        var resources = HeadlessHarness.Require(ctx.Resources, "Resources");
        var project = HeadlessHarness.Require(ctx.Project, "Project");
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.Model.Viewer");
        string original = AnimatedGlbFixture.Write(Path.Combine(ctx.Workspace, "Viewer source"));
        string assetPath = resources.ImportFiles(resources.AssetsRoot, [original]).Single();
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Viewer.LeftViewportPlaybackAndReadOnly", () =>
        {
            using var viewer = new ModelViewerControl(assetPath, project.RootPath);
            using var host = Show(viewer, 1400, 880);
            string before = File.ReadAllText(assetPath);
            viewer.SetCameraView("Front"); viewer.FrameModel();
            using (var axesReady = viewer.Viewport.CaptureFrame(8)) { }
            Assert(viewer.SelectOrientationAt(new Point(viewer.Viewport.Host.Width - 28, 59))
                && viewer.Viewport.Camera.Eye.X > viewer.Viewport.Camera.Target.X + viewer.Viewport.Camera.Distance * .9f,
                "The positive X orientation button must look from the positive X side.");
            viewer.SetCameraView("Front");
            viewer.SetGround(Genesis.Application.Editors.Suite.EditorFloorStyle.Checkerboard);
            Assert(Math.Abs(viewer.GroundHeight - viewer.ModelBounds.Min.Y) < .001f, "Ground intersects the model instead of aligning to its minimum bound.");
            var left = viewer.Controls.Find("ModelViewerLeftPanel", true).Single();
            Assert(left.Visible && left.Left == 0 && viewer.Viewport.Left >= left.Width, "Viewer must retain a left panel and viewport to its right.");
            var commands = Descendants(viewer).OfType<ToolStrip>().SelectMany(strip => strip.Items.Cast<ToolStripItem>()).ToArray();
            Assert(commands.Any(item => item.Name == "ImportModelFiles" && item.Available), "Import Model is not visible in the Viewer toolbar.");
            Assert(!commands.Any(item => item.Text is "Sculpt" or "Paint" or "Compose"), "Viewer still contains authoring modes.");
            Capture(host, viewer, ctx, "viewer-layout-ground");
            viewer.Viewport.PinSecondaryFromCurrentView();
            using (var secondary = viewer.Viewport.SecondaryViewport!.CaptureFrame(8))
            {
                Assert(secondary is not null && viewer.Viewport.SecondaryViewport.FloorHeight == viewer.GroundHeight, "Second camera has no image or its floor cuts through the model.");
            }
            Capture(host, viewer, ctx, "viewer-second-camera"); viewer.Viewport.ClearSecondaryCamera();
            viewer.SelectClip(viewer.PreviewAsset.Animations[0].Name); viewer.Loop = false; viewer.SetPlaying(true); viewer.AdvancePreview(100);
            Assert(!viewer.IsPlaying && viewer.CurrentFrame == viewer.PreviewAsset.Animations[0].Frames.Count - 1, "Non-looping preview did not stop at the last frame.");
            viewer.Loop = true; viewer.SetFrame(0); viewer.SetPlaying(true); viewer.AdvancePreview(.05f);
            Assert(viewer.IsPlaying, "Looping playback stopped."); viewer.SetPlaying(false);
            viewer.SetShading(ModelPreviewShading.Wireframe); viewer.SetLightingPreset("Unlit"); viewer.SetOrthographic(true); viewer.Save();
            Assert(!viewer.IsDirty && File.ReadAllText(assetPath) == before, "Preview controls changed the source model.");
            Capture(host, viewer, ctx, "viewer-wireframe-orthographic");
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Viewer.FbxObjDaeConversion", () =>
        {
            using var context = new AssimpContext();
            var scene = context.ImportFile(original, PostProcessSteps.Triangulate);
            // Assimp's COLLADA exporter emits broken animation references for this glTF skin.
            // Use static format fixtures here; Blender-authored animation is tested separately.
            scene.Animations.Clear();
            foreach (var format in new[] { ("obj", ".obj"), ("collada", ".dae"), ("fbxa", ".fbx") })
            {
                string source = Path.Combine(ctx.Workspace, "converted-source" + format.Item2);
                Assert(context.ExportFile(scene, source, format.Item1), "Could not create format fixture: " + format.Item2);
                string imported = resources.ImportFiles(resources.AssetsRoot, [source]).Single();
                var asset = StudioModelResourceLoader.Load(imported);
                Assert(!asset.ImportRequired && asset.HasRenderableMeshes, "Conversion produced an empty " + format.Item2 + " model: " + asset.ImportMessage);
                Assert(File.Exists(Path.Combine(ResourceAssociates.GetModelDataDirectory(imported), "original" + format.Item2)), "The original model source was not retained.");
                Assert(StudioModelResourceLoader.ResolveSource(imported).EndsWith(".glb", StringComparison.OrdinalIgnoreCase), "Runtime source is not the owned GLB.");
            }
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Viewer.EmptyAndMalformedImport", () =>
        {
            string path = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Blank viewer");
            using var viewer = new ModelViewerControl(path, project.RootPath); using var host = Show(viewer, 1100, 750);
            Assert(!viewer.PreviewAsset.HasRenderableMeshes && viewer.Controls.Find("EmptyModelHint", true).Single().Visible, "Blank model must show a useful import hint.");
            string source = Path.Combine(ctx.Workspace, "broken.fbx"); File.WriteAllText(source, "not an FBX file");
            bool rejected = false;
            try { viewer.ImportExternalModel(source); } catch (Exception) { rejected = true; }
            Assert(rejected && !viewer.IsDirty, "Invalid import should fail without changing the open model.");
            Capture(host, viewer, ctx, "viewer-empty");
        });
        if (ModelSourceConversion.FindBlender() is { } blender)
        {
            HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Viewer.BlenderAndBinaryFbxAnimation", () =>
            {
                string folder = Path.Combine(ctx.Workspace, "Blender source"); Directory.CreateDirectory(folder);
                string script = Path.Combine(folder, "fixture.py");
                File.WriteAllText(script, "import bpy, sys, os\nbpy.ops.wm.read_factory_settings(use_empty=True)\nbpy.ops.mesh.primitive_uv_sphere_add()\na=bpy.context.object\na.name='Animated sphere'\na.location.z=1\na.keyframe_insert(data_path='location',frame=1)\na.location.z=3\na.keyframe_insert(data_path='location',frame=30)\nbpy.context.scene.frame_end=30\nbpy.context.scene.frame_set(1)\nfolder=sys.argv[-1]\nbpy.ops.wm.save_as_mainfile(filepath=os.path.join(folder,'source.blend'))\nbpy.ops.export_scene.fbx(filepath=os.path.join(folder,'source.fbx'),bake_anim=True)\n");
                var start = new System.Diagnostics.ProcessStartInfo(blender) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string argument in new[] { "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1", "--python", script, "--", folder }) start.ArgumentList.Add(argument);
                using var process = System.Diagnostics.Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(60000)) { process.Kill(true); throw new InvalidOperationException("Blender fixture generation timed out."); }
                Assert(process.ExitCode == 0, "Blender fixture failed: " + error.Result + output.Result);
                foreach (string extension in new[] { ".blend", ".fbx" })
                {
                    string imported = resources.ImportFiles(resources.AssetsRoot, [Path.Combine(folder, "source" + extension)]).Single();
                    var asset = StudioModelResourceLoader.Load(imported);
                    Assert(asset.HasRenderableMeshes && asset.Animations.Any(c => c.Frames.Count > 1), extension + " lost its geometry or animation.");
                }
            });
        }
        string? reviewSource = Environment.GetEnvironmentVariable("GENESIS_MODEL_REVIEW_SOURCE");
        if (!string.IsNullOrWhiteSpace(reviewSource) && File.Exists(reviewSource))
        {
            HeadlessHarness.RunCase(ctx.Report, "Editor.Model.Viewer.ProductionAssetGround", () =>
            {
                string path = resources.ImportFiles(resources.AssetsRoot, [reviewSource]).Single();
                using var viewer = new ModelViewerControl(path, project.RootPath); using var host = Show(viewer, 1500, 940);
                viewer.FrameModel(); viewer.SetCameraView("Three-quarter");
                Capture(host, viewer, ctx, "viewer-production-grid");
                viewer.SetGround(Genesis.Application.Editors.Suite.EditorFloorStyle.Checkerboard);
                Capture(host, viewer, ctx, "viewer-production-floor");
                Assert(viewer.GroundHeight == viewer.ModelBounds.Min.Y, "Production model remains below the floor.");
            });
        }
    }
    private static Form Show(ModelViewerControl viewer, int width, int height)
    {
        var host = UnattendedWindowing.NewHost(width, height); host.Controls.Add(viewer); ThemeService.Apply(host);
        UnattendedWindowing.ShowWithoutFocus(host); System.Windows.Forms.Application.DoEvents(); return host;
    }
    private static void Capture(Form host, ModelViewerControl viewer, HeadlessContext ctx, string name)
    {
        using (var ready = viewer.Viewport.CaptureFrame(8)) { }
        var metrics = VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, name + ".png"), includeViewports: true);
        ctx.Report.Images.Add(ImageResult.From(name, name + ".png", metrics));
    }
    private static IEnumerable<Control> Descendants(Control control) { foreach (Control child in control.Controls) { yield return child; foreach (Control nested in Descendants(child)) yield return nested; } }
    private static void Assert(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
