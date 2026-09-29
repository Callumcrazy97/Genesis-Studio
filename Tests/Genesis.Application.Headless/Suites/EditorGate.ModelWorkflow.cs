using System.Drawing;
using System.Numerics;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Settings;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Modeling;
using Genesis.Rendering.Core;
using Genesis.Shared.Assets;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless.Suites;

internal static partial class EditorGate
{
    private static void ModelNoviceWorkflow(HeadlessContext ctx, ProjectSession project, string modelPath)
    {
        HeadlessHarness.Step("Model's real workflow edits the pivot and animation, preserves history and creates a gameplay Object", () =>
        {
            using var editor = new ModelEditorControl(modelPath, project.RootPath);
            using var host = GateSuite.NewHost(1280, 840); host.Controls.Add(editor); GateSuite.ShowHost(host);
            var commands = SurfaceControls(editor).OfType<EditorCommandBar>().Single();
            foreach ((string caption, string mode) in new[] { ("Texture", "Texture"), ("Rig", "RigAnimate"), ("Outliner", "Outliner"), ("Create", "Create") })
            {
                SurfaceControls(editor).OfType<ToolStrip>().SelectMany(strip => strip.Items.OfType<ToolStripButton>())
                    .Single(button => button.Text!.EndsWith("\n" + caption, StringComparison.Ordinal)).PerformClick();
                var toolModes = SurfaceControls(editor).OfType<EditorModeHost>().Single();
                HeadlessHarness.Assert(toolModes.ActiveMode == mode && toolModes.Controls.Cast<Control>().Count(control => control.Visible) == 1,
                    "The actual Model " + caption + " button did not open its tools.");
            }
            HeadlessHarness.Assert(commands.IsSaveVisible && commands.IsDocumentStateVisible,
                "Model Save and its document state are not pinned on the actual toolbar.");
            var before = editor.PreviewAsset.Pivot.Position;
            string canonical = editor.CanonicalModelPath; string disk = File.ReadAllText(canonical);
            HeadlessHarness.Assert(editor.TryApplyInspectorValue("Model.Pivot.X", .25f) && editor.IsDirty
                && File.ReadAllText(canonical) == disk, "The Model Inspector did not retain its explicit Save boundary.");
            editor.Undo(); HeadlessHarness.Assert(editor.PreviewAsset.Pivot.Position == before, "Model pivot Undo failed.");
            editor.Redo(); HeadlessHarness.Assert(editor.PreviewAsset.Pivot.Position.X == .25f, "Model pivot Redo failed.");
            GModelAsset authored = editor.CaptureAnimationAsset();
            authored.Animations[0].Name = "Authored motion";
            editor.ApplyAnimationWorkspace(authored, "Authored motion");
            editor.Undo(); HeadlessHarness.Assert(!editor.ClipNames.Contains("Authored motion"), "Animation workspace Undo failed.");
            editor.Redo(); editor.Save();
            using var reopened = new ModelViewerControl(modelPath, project.RootPath);
            HeadlessHarness.Assert(reopened.PreviewAsset.Pivot.Position.X == .25f && reopened.ClipNames.Contains("Authored motion"),
                "Pivot and authored animation did not survive Model save/reopen.");
            GModelAsset runtime = new RuntimeModelAssetRegistry().Load(project.RootPath, ResourceNames.Name(project.RootPath, modelPath));
            var mesh = runtime.Meshes.First(part => part.IsSkinned);
            var first = new Genesis.Shared.Interfaces.MeshVertex[mesh.SkinnedVertices.Length];
            var later = new Genesis.Shared.Interfaces.MeshVertex[first.Length];
            HeadlessHarness.Assert(ModelRigBridge.SkinInto(runtime, "Authored motion", 0, first)
                && ModelRigBridge.SkinInto(runtime, "Authored motion", .5f, later)
                && first.Zip(later, (a, b) => Vector3.Distance(a.Position, b.Position)).Max() > .05f,
                "The saved animation name exists without actual changing mesh poses.");
            editor.SelectClip("Authored motion");
            commands.Items.OfType<ToolStripButton>().Single(item => item.Text == "Use in game").PerformClick();
            commands.Items.OfType<ToolStripButton>().Single(item => item.Text == "Use in game").PerformClick();
            GateSuite.Pump(3, 20);
            SurfaceControls(editor).OfType<TextBox>().Single(control => control.Name == "ModelObjectName").Text = "Guided Model Object";
            SurfaceControls(editor).OfType<Button>().Single(control => control.Name == "ModelCreateObject").PerformClick();
            string objectPath = ProjectAssetIndex.ResolveReference(project.RootPath, "Guided Model Object", ResourceKind.GameObject)!;
            HeadlessHarness.Assert(File.Exists(objectPath), "The actual Model guide did not create its Object.");
            using var obj = new ObjectEditorControl(objectPath, project.RootPath);
            HeadlessHarness.Assert(obj.IsThreeD && obj.PgslEvents["Create"].Contains("ModelAnimationPlay(\"Authored motion\"", StringComparison.Ordinal),
                "The created Object omitted its selected animation's gameplay code.");
            string capture = "model-guided-object.png";
            ctx.Report.Images.Add(ImageResult.From("Model gameplay guide", capture,
                VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, capture), includeViewports: true)));
            CheckGuidedModelGameplay(ctx, project.RootPath, obj, editor);
            Button back = SurfaceControls(editor).OfType<Button>().Single(control => control.Name == "ModelBackToView");
            HeadlessHarness.Assert(back.Visible && back.Enabled, "Live Model save/undo hid the gameplay guide's return action.");
            back.PerformClick();
            HeadlessHarness.Assert(editor.Viewport.Visible, $"Returning from Model guidance lost the viewer: host={host.Visible}, parent={editor.Viewport.Parent?.Visible}, guide={back.Parent?.Visible}.");
            host.Close();
        });
    }

    private static void CheckGuidedModelGameplay(HeadlessContext ctx, string projectRoot, ObjectEditorControl obj, ModelEditorControl editor)
    {
        foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
        {
            using var preview = new ObjectCompositionPreviewControl(projectRoot);
            preview.Viewport.BackendOverride = backend.Backend;
            using var host = GateSuite.NewHost(700, 520); host.Controls.Add(preview); GateSuite.ShowHost(host);
            JObject document = JObject.Parse(File.ReadAllText(obj.ResourcePath));
            var events = new Dictionary<string, string>(obj.PgslEvents, StringComparer.OrdinalIgnoreCase) { ["Step"] = "ModelAnimationSetTime(0);" };
            preview.EventSources = events; preview.Reload(document);
            GateSuite.Pump(5, 20);
            using Bitmap first = preview.CaptureFrame(3) ?? throw new InvalidOperationException("Model gameplay frame unavailable.");
            HeadlessHarness.Assert(first.GetPixel(first.Width / 2, first.Height / 2).A == 255,
                "The opaque gameplay viewport was captured as transparent.");
            string expected = backend.Backend switch { RenderBackendOption.OpenGL => "OpenGL", RenderBackendOption.Software => "Software", _ => backend.DisplayName };
            HeadlessHarness.Assert(preview.Viewport.Host.Renderer?.BackendName == expected && preview.ScriptError is null,
                "Model gameplay used another backend or failed its real Create event: " + preview.ScriptError);
            HeadlessHarness.Assert(preview.AnimatorTimeSeconds < .05f, "PGSL could not seek the actual Model animator to its first pose.");
            events["Step"] = "ModelAnimationSetTime(0.5);";
            preview.Reload(document); GateSuite.Pump(5, 20);
            using Bitmap later = preview.CaptureFrame(3) ?? throw new InvalidOperationException("Later Model gameplay frame unavailable.");
            int changed = CountDifferingPixels(first, later);
            foreach ((string phase, Bitmap frame) in new[] { ("first", first), ("later", later) })
            {
                string file = "model-guided-gameplay-" + backend.ShortName.ToLowerInvariant() + "-" + phase + ".png";
                frame.Save(Path.Combine(ctx.Captures, file));
                using Bitmap savedFrame = new(Path.Combine(ctx.Captures, file));
                HeadlessHarness.Assert(savedFrame.GetPixel(0, 0).A == 255
                    && savedFrame.GetPixel(savedFrame.Width / 2, savedFrame.Height / 2).A == 255,
                    "The saved " + expected + " gameplay PNG lost its opaque viewport pixels.");
                ctx.Report.Images.Add(ImageResult.From("Saved Model gameplay · " + expected + " · " + phase, file, VisualCapture.Measure(frame)));
            }
            HeadlessHarness.Assert(Math.Abs(preview.AnimatorTimeSeconds - .5f) < .03f && changed > 10 && preview.ScriptError is null,
                $"Saved Model animation did not visibly move through PGSL on {expected}: time={preview.AnimatorTimeSeconds}, changed={changed}, error={preview.ScriptError}.");
            float pivot = editor.PreviewAsset.Pivot.Position.X;
            HeadlessHarness.Assert(editor.TryApplyInspectorValue("Model.Pivot.X", pivot + .6f), "Live Model edit was rejected.");
            editor.Save(); GateSuite.Pump(8, 30);
            using Bitmap edited = preview.CaptureFrame(3) ?? throw new InvalidOperationException("Live Model gameplay frame unavailable.");
            string editedFile = "model-guided-gameplay-" + backend.ShortName.ToLowerInvariant() + "-live-edit.png";
            edited.Save(Path.Combine(ctx.Captures, editedFile));
            ctx.Report.Images.Add(ImageResult.From("Live saved Model edit · " + expected, editedFile, VisualCapture.Measure(edited)));
            int liveChanged = CountDifferingPixels(later, edited);
            HeadlessHarness.Assert(liveChanged > 10 && preview.ScriptError is null,
                $"The existing {expected} Object preview did not pick up a saved Model edit: changed={liveChanged}.");
            editor.Undo(); editor.Save();
            HeadlessHarness.Assert(editor.PreviewAsset.Pivot.Position.X == pivot, "Live Model edit Undo/save failed.");
            host.Close();
        }
    }
}
