using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Application.Editors.Suite.Objects.VisualActions;
using Genesis.Application.Editors.Suite.Scripts;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless.Suites;

internal static partial class EditorGate
{
    private static void ObjectNoviceWorkflow(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        var project = fixture.TwoD; ResourceService resources = fixture.Resources(project);
        string path = resources.CreateResource(fixture.Folder(project, "Objects"), ResourceKind.GameObject, "Gate Object workflow");
        using ObjectEditorControl editor = new(path, project.RootPath);
        using Form host = GateSuite.NewHost(1280, 800); host.Controls.Add(editor); GateSuite.ShowHost(host);
        foreach (string id in editor.PgslEvents.Keys.ToArray()) editor.RemoveEvent(id);
        editor.Save();
        HeadlessHarness.Step("Object has a short primary workflow and its guide opens the real saved Room", () =>
        {
            EditorCommandBar bar = editor.Controls.OfType<EditorCommandBar>().Single();
            string[] primary = bar.Items.Cast<ToolStripItem>().Where(item => item.Available && item.Alignment != ToolStripItemAlignment.Right)
                .Select(item => item.Text ?? string.Empty).ToArray();
            HeadlessHarness.Assert(primary.SequenceEqual(new[] { "Builder", "Code", "Add event…", "Add action…", "Use in game", "Options" }),
                "Object keeps a crowded or repeated primary toolbar: " + string.Join(',', primary));
            HeadlessHarness.Assert(!bar.Items.OfType<ToolStripButton>().Single(item => item.Text == "Add action…").Enabled,
                "Add action should explain the missing event rather than silently edit unattached code.");
            string? opened = null; editor.OpenLinkedResourceRequested += (_, value) => opened = value;
            bar.Items.OfType<ToolStripButton>().Single(item => item.Text == "Use in game").PerformClick();
            FlowLayoutPanel guide = SurfaceControls(editor).OfType<FlowLayoutPanel>().Single(control => control.Name == "ObjectUseInGame");
            HeadlessHarness.Assert(guide.Visible && guide.Parent!.Top >= bar.Bottom && bar.IsSaveVisible, "The Object guide covered the primary toolbar.");
            SurfaceControls(editor).OfType<Button>().Single(button => button.Name == "ObjectOpenRoom").PerformClick();
            HeadlessHarness.Assert(opened is not null && opened.EndsWith(".room.json", StringComparison.Ordinal) && File.Exists(opened) && !editor.IsDirty,
                "The actual guide button did not save and open its selected Room.");
            string capture = "object-use-in-game.png";
            ctx.Report.Images.Add(ImageResult.From("Object Use in game", capture, VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, capture))));
            bar.Items.OfType<ToolStripButton>().Single(item => item.Text == "Builder").PerformClick();
            VisualActionCommand rig = VisualActionCatalog.Find("SpriteRigPlay")!;
            HeadlessHarness.Assert(rig.Parameters.Single(parameter => parameter.Name == "animation").Value == "\"\""
                && rig.Parameters.Single(parameter => parameter.Name == "speed").Value == "1"
                && rig.Parameters.Single(parameter => parameter.Name == "loop").Value == "-1"
                && rig.Parameters.Single(parameter => parameter.Name == "loop").DataType == BlueprintValueType.Int,
                "The action picker has invalid string/numeric defaults for the real rig playback command.");
            using var components = editor.CreateCompositionDialog();
            HeadlessHarness.Assert(components.Preview.Viewport.Mode2D && SurfaceControls(components).OfType<ToolStrip>()
                .SelectMany(strip => strip.Items.OfType<ToolStripButton>()).Single(button => button.Text == "2D").Checked,
                "The component preview selected 3D while rendering a 2D Object.");
        });
        HeadlessHarness.Step("Object document history crosses event addition, Builder, Code and component edits", () =>
        {
            editor.SetEventBody("Create", "var workflowHealth = 100;\n");
            editor.SetEventBody("Step", "workflowHealth += 1;\nx += 1;\n");
            editor.Save();
            HeadlessHarness.Assert(editor.VisualActions.InsertCommand("SpriteSetSpeed"), "The Builder could not add an ordinary runtime action.");
            string builder = editor.PgslEvents["Step"];
            editor.ShowCodeEditor();
            editor.VisibleEventCode = editor.VisibleEventCode.Replace("x += 1", "x += 2", StringComparison.Ordinal);
            HeadlessHarness.Assert(editor.PgslEvents["Step"].Contains("x += 2", StringComparison.Ordinal) && editor.CanUndo,
                "Code did not update the same event document.");
            CodeEditor code = SurfaceControls(editor).OfType<CodeEditor>().Single();
            KeyEventArgs undoKey = new(Keys.Control | Keys.Z);
            typeof(Control).GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(code.TextBox, [undoKey]);
            HeadlessHarness.Assert(undoKey.Handled && editor.PgslEvents["Step"] == builder, "Ctrl+Z in Code discarded the earlier Builder action or bypassed document history.");
            editor.VisualActions.Undo(); HeadlessHarness.Assert(!editor.PgslEvents["Step"].Contains("SpriteSetSpeed", StringComparison.Ordinal) && !editor.IsDirty,
                "Undo Builder did not restore the exact saved event or saved state.");
            editor.Redo(); editor.Redo();
            HeadlessHarness.Assert(editor.PgslEvents["Step"].Contains("x += 2", StringComparison.Ordinal) && editor.PgslEvents["Step"].Contains("SpriteSetSpeed", StringComparison.Ordinal),
                "Redo across modes failed to restore both edits.");
            JObject before = (JObject)editor.Document.DeepClone(), composed = (JObject)before.DeepClone();
            new ObjectCompositionModel(composed).SetProperty("AudioComponent", "Volume", .37f);
            editor.ApplyComposition(composed); editor.Undo();
            HeadlessHarness.Assert(JToken.DeepEquals(editor.Document, before), "Component undo did not restore its complete prior document.");
            editor.Redo(); HeadlessHarness.Assert(Math.Abs(ObjectCompositionModel.Props(editor.Composition.Find("AudioComponent")!).Value<float>("Volume") - .37f) < .001f,
                "Component redo did not restore the authored value.");
            editor.SetEventBody("Alarm1", "workflowHealth = 12;\n"); editor.Undo();
            HeadlessHarness.Assert(!editor.PgslEvents.ContainsKey("Alarm1"), "Undo event addition left an attached Alarm event.");
            editor.Redo(); editor.RemoveEvent("Alarm1"); editor.Undo();
            HeadlessHarness.Assert(editor.PgslEvents["Alarm1"].Contains("workflowHealth = 12", StringComparison.Ordinal), "Undo removal did not retain the event source.");
            editor.RemoveEvent("Alarm1"); editor.Save();
            using ObjectEditorControl reopened = new(path, project.RootPath);
            HeadlessHarness.Assert(reopened.PgslEvents["Step"].Contains("x += 2", StringComparison.Ordinal) && reopened.Composition.Find("AudioComponent") is not null
                && !ObjectEventStore.Load(path).ContainsKey("Alarm1"), "Object save/reopen lost shared source/components or restored a deleted event.");
        });
        HeadlessHarness.Step("narrow Object editing keeps event selection and typed assistance while the real preview executes authored changes", () =>
        {
            host.ClientSize = new(500, 720); editor.ShowCodeEditor(); GateSuite.Pump(3, 10);
            ComboBox picker = SurfaceControls(editor).OfType<ComboBox>().Single(control => control.Name == "ObjectEventPicker");
            picker.SelectedItem = "Create"; HeadlessHarness.Assert(picker.Visible && editor.ActiveEvent == "Create", "Hidden properties left no selectable event.");
            editor.VisibleEventCode = "var workflowHealth = 100;\nSpriteRigPlay(\"Flutter\", 1, -1, false);";
            CodeEditor code = SurfaceControls(editor).OfType<CodeEditor>().Single(); code.MoveCaret(code.CodeText.IndexOf(", -1", StringComparison.Ordinal) + 2);
            HeadlessHarness.Assert(code.SignatureVisible && code.ActiveParameterIndex == 2 && code.SignatureText.Contains("SpriteRigPlay", StringComparison.Ordinal),
                "Object has no typed call/active-argument guidance.");
            editor.VisibleEventCode = "var workflowHealth = 100;\n";
            editor.RunLiveSandbox(); editor.RuntimePreview.Playing = false;
            PgslBehavior first = HeadlessHarness.Require(editor.RuntimePreview.LiveBehavior, "actual Object behavior");
            HeadlessHarness.Assert(Convert.ToDouble(first.GetVariablesSnapshot()["workflowHealth"]) == 100, "The real preview did not execute Create.");
            editor.SetEventBody("Create", "var workflowHealth = 150;\n"); editor.RunLiveSandbox(); editor.RuntimePreview.Playing = false;
            PgslBehavior updated = HeadlessHarness.Require(editor.RuntimePreview.LiveBehavior, "updated Object behavior");
            HeadlessHarness.Assert(!ReferenceEquals(first, updated) && Convert.ToDouble(updated.GetVariablesSnapshot()["workflowHealth"]) == 150,
                "Authored edits did not reach the actual preview VM.");
            editor.ResetLiveSandbox(); editor.Save();
        });
        host.Close();
    }
}
