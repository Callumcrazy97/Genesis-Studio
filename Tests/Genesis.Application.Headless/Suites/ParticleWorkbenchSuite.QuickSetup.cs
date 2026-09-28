using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Application.Editors.Suite.Scripts;
using Genesis.Rendering.Core;
using Genesis.Shared.Interfaces;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless.Suites;

internal static partial class ParticleWorkbenchSuite
{
    private static void RunQuickSetupCases(HeadlessContext context, Action<string, Action> check)
    {
        check("QuickSetup.PreviewResizesWithinWorkspace", () => WithPreview(RenderBackendOption.Software, editor =>
        {
            Form host = editor.FindForm()!;
            host.ClientSize = new Size(833, 550);
            GateSuite.Pump(4, 20);
            using Bitmap frame = editor.Viewport.CaptureFrame(2)
                ?? throw new InvalidOperationException("Narrow particle preview produced no frame.");
            SplitContainer split = Field<SplitContainer>(editor, "_referenceAuthoringSplit");
            Assert(split.Right <= split.Parent!.ClientSize.Width && editor.Viewport.Width <= split.Panel2.ClientSize.Width,
                $"The preview is clipped by its workspace after resizing: split={split.Bounds}, parent={split.Parent.ClientSize}, viewport={editor.Viewport.Size}.");
            Assert(frame.Width == editor.Viewport.Host.ClientWidth && frame.Height == editor.Viewport.Host.ClientHeight,
                "The narrow preview buffer no longer matches the visible viewport.");
        }));
        check("QuickSetup.VisibleControlsCreateSavedRuntimeObject", () => WithPreview(RenderBackendOption.Software, editor =>
        {
            Form host = editor.FindForm()!;
            FlowLayoutPanel quick = Field<FlowLayoutPanel>(editor, "_particleQuickSetup");
            TabControl advanced = Field<TabControl>(editor, "_inspectorTabs");
            EditorCommandBar commands = editor.Controls.OfType<EditorCommandBar>().Single();
            Assert(quick.Visible && !advanced.Visible && !editor.IsDirty,
                "Opening the effect did not give an unchanged Quick setup document.");
            Assert(!Field<TabControl>(editor, "_particleLibraryTabs").Visible
                && !Field<Panel>(editor, "_particleTimelinePanel").Visible,
                "Opening the effect exposed advanced panels before the user asked for them.");
            Assert(!commands.Items.Cast<ToolStripItem>().Any(item => item.Text is "Step" or "Stop" or "Burst" or "Emit continuously"),
                "The primary toolbar still contains the old detailed preview commands.");
            ToolStripDropDownButton preset = commands.Items.OfType<ToolStripDropDownButton>().Single(button => button.Text == "Start with…");
            preset.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == "Explosion").PerformClick();
            Assert(!editor.Config.Loop && editor.Config.BurstCount > 0 && quick.Visible,
                "Selecting a one-shot preset did not create a playable effect in Quick setup.");
            editor.Undo();
            Assert(!editor.IsDirty, "Undoing the preset did not restore the opened document.");
            NumericUpDown rate = Field<Dictionary<string, NumericUpDown>>(editor, "_numericControls")["rate"];
            Assert(ReferenceEquals(rate.Parent!.Parent, quick), "Quick setup is a second copy of the rate control.");
            rate.Value = 360;
            Assert(editor.Config.EmitRate == 360 && editor.IsDirty, "The visible Quick setup field did not edit the real effect.");
            editor.Undo();
            Assert(editor.Config.EmitRate != 360 && !editor.IsDirty, "Quick setup edits did not support normal document Undo.");

            ToolStripDropDownButton options = commands.Items.OfType<ToolStripDropDownButton>().Single(button => button.Text == "Options");
            ToolStripMenuItem detailed = options.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == "Advanced properties");
            detailed.PerformClick();
            Assert(advanced.Visible && !quick.Visible, "Advanced properties did not replace Quick setup.");
            Field<Button>(editor, "_particleInspectorBack").PerformClick();
            Assert(quick.Visible && !advanced.Visible && !detailed.Checked,
                "Returning to Quick setup did not update the actual advanced mode choice.");
            options.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Text == "Edit emitter definition").PerformClick();
            CodeEditor code = Field<CodeEditor>(editor, "_code");
            code.CodeText = "particle \"Bad\" {\n unknown.setting: 7\n}";
            string before = Text(editor.Config);
            commands.Items.OfType<ToolStripButton>().Single(button => button.Text == "Use in game").PerformClick();
            Assert(editor.AuthoringMode == ParticleAuthoringMode.Code && Text(editor.Config) == before,
                "Use in game discarded an invalid emitter draft.");
            Invoke(editor, "RevertParticleDraft");
            commands.Items.OfType<ToolStripButton>().Single(button => button.Text == "Use in game").PerformClick();
            FlowLayoutPanel guide = Field<FlowLayoutPanel>(editor, "_particleGameGuide");
            Assert(guide.Visible && !quick.Visible && !advanced.Visible,
                "Use in game did not expose the concrete Object and PGSL routes.");
            editor.ApplyPreset("Fire");
            editor.SetPreview2D(true);
            rate.Value = 300;
            string? opened = null;
            editor.OpenLinkedResourceRequested += (_, path) => opened = path;
            guide.Controls.OfType<Button>().Single(button => button.Name == "ParticleCreateEffectObject").PerformClick();
            Assert(opened is not null && File.Exists(opened) && !editor.IsDirty,
                "Create effect Object did not save the effect and open a real Object resource.");
            JObject document = JObject.Parse(File.ReadAllText(opened!));
            Assert((string?)document["dimension"] == "TwoD" && document["components"]!.OfType<JObject>()
                .Any(component => (string?)component["type"] == "ParticleComponent"
                    && (string?)component["props"]?["Asset"] == "Campfire"),
                "The created Object lost its 2D dimension or named particle reference.");
            using ParticleEditorControl reopened = new(editor.ResourcePath, editor.ProjectRoot);
            Assert(reopened.Config.EmitRate == 300 && reopened.Config.Preview2D,
                "Creating an Object linked to stale, unsaved effect settings.");
            string image = "particle-use-in-game.png";
            context.Report.Images.Add(ImageResult.From("Particle — create an effect Object", image,
                VisualCapture.CaptureOpenForm(host, Path.Combine(context.Captures, image))));
            CheckParticleObjectPixels(context, editor.ProjectRoot, document);
        }));
    }

    private static void CheckParticleObjectPixels(HeadlessContext context, string projectRoot, JObject document)
    {
        RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
        try
        {
            foreach ((RenderBackendOption backend, string expected) in new[]
            {
                (RenderBackendOption.SilkNetDx11, "Direct3D 11"), (RenderBackendOption.Direct3D12, "Direct3D 12"),
                (RenderBackendOption.Vulkan, "Vulkan"), (RenderBackendOption.OpenGL, "OpenGL"),
                (RenderBackendOption.Software, "Software"),
            })
            {
                RenderBackendSelection.Configure(backend);
                using Form host = GateSuite.NewHost(960, 640);
                using ObjectCompositionPreviewControl preview = new(projectRoot, compact: true);
                host.Controls.Add(preview);
                GateSuite.ShowHost(host);
                preview.Reload(document);
                using (preview.CaptureFrame(3)) { }
                GateSuite.Pump(12, 20);
                using Bitmap frame = preview.CaptureFrame(4) ?? throw new InvalidOperationException("The particle Object rendered no frame.");
                Assert(preview.Viewport.Host.Renderer.BackendName == expected && preview.Viewport.Host.RenderFaultCount == 0,
                    "The particle Object preview did not use the requested " + expected + " renderer.");
                int gold = 0;
                for (int y = 60; y < frame.Height - 50; y++)
                for (int x = 0; x < frame.Width; x++)
                {
                    Color pixel = frame.GetPixel(x, y);
                    if (pixel.R > 120 && pixel.G > 50 && pixel.B < pixel.R * .65f) gold++;
                }
                int emitters = preview.ParticleEmitterCount, live = preview.ActiveParticleCount;
                string image = "particle-object-" + backend + ".png";
                string path = Path.Combine(context.Captures, image);
                frame.Save(path);
                context.Report.Images.Add(ImageResult.From("Particle Object — " + expected, image, VisualCapture.Measure(frame)));
                JObject empty = (JObject)document.DeepClone();
                JArray components = (JArray)empty["components"]!;
                foreach (JObject particle in components.OfType<JObject>().Where(component =>
                    (string?)component["type"] == "ParticleComponent").ToArray()) particle.Remove();
                preview.Reload(empty);
                using Bitmap baseline = preview.CaptureFrame(3)!;
                int changed = 0;
                for (int y = 60; y < frame.Height - 50; y++)
                for (int x = 0; x < frame.Width; x++)
                    if (frame.GetPixel(x, y).ToArgb() != baseline.GetPixel(x, y).ToArgb()) changed++;
                Assert(emitters == 1 && live > 0 && changed > 20,
                    $"The generated Object did not visibly play its saved Fire effect on {expected}: emitters={emitters}, live={live}, changed={changed}, gold={gold}.");
            }
        }
        finally { RenderBackendSelection.Configure(previous); }
    }
}
