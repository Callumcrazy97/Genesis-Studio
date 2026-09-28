using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Genesis.Application.Core.Diagnostics;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Settings;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Application.Editors.Suite.Scripts;
using Genesis.Application.Studio;
using Genesis.Application.Studio.Docking;
using Genesis.Application.Studio.Forms;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using WeifenLuo.WinFormsUI.Docking;

namespace Genesis.Application.Headless.Suites;

internal static class ResourceInspectorSuite
{
    public static void Run(HeadlessContext ctx)
    {
        int start = ctx.Report.Tests.Count;
        (ResourceKind Kind, string Field, object First, object Second)[] cases =
        [
            (ResourceKind.Image, "origin.x", 12d, 18d),
            (ResourceKind.Audio, "volume", .6f, .4f),
            (ResourceKind.Shader, "Parameters.Speed", 2f, 3f),
            (ResourceKind.PgslScript, "Variables.Damage", 5d, 7d),
            (ResourceKind.GameObject, "Events.Create.Variables.Damage", 5d, 7d),
            (ResourceKind.Room, "settings.width", 1600f, 1920f),
            (ResourceKind.Particle, "emitRate", 33f, 44f),
            (ResourceKind.Physics, "friction", .7m, .9m),
            (ResourceKind.Pathing, "Pathing.Speed", 55f, 77f),
            (ResourceKind.UserInterface, "Ui.Text", "First label", "Saved label"),
            (ResourceKind.Note, "Note.Title", "First heading", "Saved heading"),
        ];
        foreach (var item in cases)
            HeadlessHarness.RunCase(ctx.Report, "Editor.ResourceInspector." + item.Kind + ".ShellHistoryPersistence", () =>
            {
                var project = new ProjectService().CreateProject(ctx.Workspace, "Inspector " + item.Kind, "Blank");
                var resources = new ResourceService(project);
                string path = resources.CreateResource(ResourceFolderPolicy.RootFor(project, item.Kind), item.Kind, "Editable " + item.Kind);
                if (item.Kind == ResourceKind.PgslScript) File.WriteAllText(path, "var Damage = 2; var damage = 3;\n");
                var resource = GateSuite.Flatten(resources.BuildTree()).Single(asset => asset.FullPath == path);
                var services = new StudioServices(new SettingsService(Path.Combine(project.RootPath, "inspector.settings.json")),
                    new ProjectService(), new ProjectValidator(), new StudioLog(Path.Combine(ctx.Logs, "inspector-" + item.Kind + ".log")));
                services.Settings.Current.Runtime.VSyncInPreview = false;
                using var shell = new StudioShellForm(services, project, persistLayout: false);
                GateSuite.ShowHost(shell);
                IStudioDocument document = shell.OpenStudioResource(resource);
                object owner = document is SuiteEditorDocument suite ? suite.Surface : document;
                if (owner is ObjectEditorControl obj) { obj.SetEventBody("Create", "var Damage = 2; var damage = 3;\n"); document.Save(); }
                if (owner is UiEditorControl ui) { ui.AddElement(UiElementType.Text); document.Save(); }
                var live = owner as ILiveResourceInspectorTarget ?? throw new InvalidOperationException("Resource has no live Inspector owner.");
                var target = owner as IResourceInspectorTarget ?? throw new InvalidOperationException("Resource cannot accept authored values.");
                shell.AssetBrowser.SelectPath(path);
                shell.Inspector.Show(shell.Inspector.DockPanel, DockState.DockRight); shell.Inspector.Inspect(resource);
                GateSuite.Pump(8, 20);
                object initial = Value(live, item.Field);
                string disk = ResourceText(path, owner);
                HeadlessHarness.Assert(shell.Inspector.SetEditableValue(item.Field, item.First)
                    && Same(Value(live, item.Field), item.First) && document.IsDirty,
                    "The actual Studio Inspector did not edit its retained " + item.Kind + " document.");
                HeadlessHarness.Assert(ResourceText(path, owner) == disk, "An open editor's Inspector bypassed Save.");
                document.Execute(StudioDocumentCommand.Undo);
                HeadlessHarness.Assert(Same(Value(live, item.Field), initial), "Inspector edit did not enter editor Undo.");
                document.Execute(StudioDocumentCommand.Redo);
                HeadlessHarness.Assert(Same(Value(live, item.Field), item.First), "Inspector edit did not survive Redo.");
                HeadlessHarness.Assert(target.TryApplyInspectorValue(item.Field, item.Second), "Editor could not update its authored value.");
                GateSuite.Pump(10, 20);
                string drawerName = "InspectorProperty_" + Regex.Replace(item.Field, "[^A-Za-z0-9_]", "_");
                Control drawer = All(shell.Inspector).FirstOrDefault(control => control.Name == drawerName)
                    ?? All(shell.Inspector).Single(control => control.Name.Equals(drawerName, StringComparison.OrdinalIgnoreCase));
                string displayed = drawer is TextBoxBase text ? text.Text : All(drawer).Prepend(drawer).OfType<NumericUpDown>().First().Value.ToString(CultureInfo.InvariantCulture);
                HeadlessHarness.Assert(Same(displayed, item.Second), "Editor changes did not propagate back to the existing Inspector drawer: " + displayed);
                document.Save();
                HeadlessHarness.Assert(!document.IsDirty && ResourceText(path, owner) != disk, "Inspector changes did not persist at Save.");
                ((GenesisDockContent)document).Close(); GateSuite.Pump(3, 20);
                IStudioDocument reopened = shell.OpenStudioResource(resource);
                object reopenedOwner = reopened is SuiteEditorDocument reopenedSuite ? reopenedSuite.Surface : reopened;
                if (reopenedOwner is UiEditorControl reopenedUi) reopenedUi.SelectElement("Text1");
                HeadlessHarness.Assert(Same(Value((ILiveResourceInspectorTarget)reopenedOwner, item.Field), item.Second), "Save/reopen lost the edited resource value.");
                shell.Inspector.Show(shell.Inspector.DockPanel, DockState.DockRight);
                shell.Inspector.Inspect(resource); GateSuite.Pump(3, 20);
                foreach (int scale in new[] { 100, 200 })
                {
                    var settings = new GenesisSettings(); settings.Appearance.InterfaceScale = scale / 100f;
                    ThemeService.ApplySettings(settings); GateSuite.Pump(4, 20);
                    shell.Inspector.DockPanel.DockRightPortion = scale == 100 ? 440 : 560;
                    GateSuite.Pump(3, 20);
                    string capture = "inspector-" + item.Kind.ToString().ToLowerInvariant() + "-" + scale + ".png";
                    ctx.Report.Images.Add(ImageResult.From(item.Kind + " Inspector at " + scale + "%", capture,
                        VisualCapture.CaptureOpenForm(shell, Path.Combine(ctx.Captures, capture), includeViewports: true)));
                    foreach (Label caption in All(shell.Inspector).OfType<Label>().Where(control =>
                                 control.Name is "InspectorPathCaption" or "InspectorGuidCaption" or "InspectorModifiedCaption" or "InspectorSizeCaption"))
                        HeadlessHarness.Assert(caption.ClientSize.Width >= TextRenderer.MeasureText(caption.Text, caption.Font,
                            Size.Empty, TextFormatFlags.SingleLine).Width,
                            $"{scale}% Inspector caption {caption.Text} is clipped: width={caption.ClientSize.Width}, font={caption.Font.SizeInPoints}.");
                    foreach (Control value in All(shell.Inspector).Where(control => control.Name is "InspectorModifiedValue" or "InspectorGuidValue"))
                        HeadlessHarness.Assert(value.ClientSize.Height >= TextRenderer.MeasureText("Ag", value.Font).Height,
                            $"{scale}% Inspector metadata {value.Name} is clipped vertically.");
                    if (reopenedOwner is Control editor)
                        foreach (Control page in All(editor).Where(control => control.Name is "ParticleQuickSetup" or "ShaderParameterCard"))
                        foreach (NumericUpDown field in All(page).OfType<NumericUpDown>().Where(control => control.Visible))
                            HeadlessHarness.Assert(field.ClientSize.Width >= TextRenderer.MeasureText("0.00", field.Font).Width + 18,
                                $"{scale}% {item.Kind} field is unreadable beside the pinned Inspector: {field.ClientSize.Width}px.");
                    if (scale == 200 && reopenedOwner is ShaderEditorControl shader)
                    {
                        Panel quick = All(shader).OfType<Panel>().Single(control => control.Name == "ShaderQuickSetup");
                        FlowLayoutPanel parameters = quick.Controls.OfType<FlowLayoutPanel>().Single(control => control.Controls.Cast<Control>().Any(card => card.Name == "ShaderParameterCard"));
                        FlowLayoutPanel header = quick.Controls.OfType<FlowLayoutPanel>().Single(control => control != parameters);
                        HeadlessHarness.Assert(quick.AutoScroll && parameters.Top >= header.Bottom,
                            "The Shader's parameter fields overlap its quick setup instructions beside the Inspector.");
                        quick.AutoScrollPosition = new Point(0, 10000); GateSuite.Pump(4, 20);
                        string lowerCapture = "inspector-shader-200-parameters.png";
                        ctx.Report.Images.Add(ImageResult.From("Shader parameters beside Inspector", lowerCapture,
                            VisualCapture.CaptureOpenForm(shell, Path.Combine(ctx.Captures, lowerCapture), includeViewports: true)));
                    }
                }
                ThemeService.ApplySettings(new GenesisSettings());
                shell.Close();
            });

        HeadlessHarness.RunCase(ctx.Report, "Editor.ResourceInspector.PgslTypedReferencesAndLiteralContract", () =>
        {
            var project = new ProjectService().CreateProject(ctx.Workspace, "Typed Inspector references", "Blank");
            var resources = new ResourceService(project);
            string firstImage = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Image), ResourceKind.Image, "First sprite");
            string secondImage = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Image), ResourceKind.Image, "Second sprite");
            string script = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.PgslScript), ResourceKind.PgslScript, "Typed values");
            const string source = "// var ghost = 90;\n/*\nvar hidden = 20;\n*/\nvar Damage = 2; var damage = 3;\nvar hero = \"First sprite\"; // @resource Image\nvar optional = \"\"; // @resource Audio\nfunction Nested() { var privateValue = 4; }\n";
            File.WriteAllText(script, source); ResourceNames.Invalidate(project.RootPath);
            using var editor = new PgslScriptEditorControl(script, project.RootPath);
            using var host = GateSuite.NewHost(520, 780);
            using var inspector = new InspectorDock { Dock = DockStyle.Fill, TopLevel = false, FormBorderStyle = FormBorderStyle.None };
            var resource = GateSuite.Flatten(resources.BuildTree()).Single(asset => asset.FullPath == script);
            inspector.LiveValueProvider = _ => editor.GetLiveInspectorValues();
            inspector.EditRouter = request => editor.TryApplyInspectorValue(request.PropertyPath, request.Value);
            editor.InspectorStateChanged += (_, _) => inspector.RefreshLiveValues(resource);
            host.Controls.Add(inspector); GateSuite.ShowHost(host); inspector.Show(); inspector.Inspect(resource);
            var values = editor.GetLiveInspectorValues();
            HeadlessHarness.Assert(values.Any(value => value.PropertyPath == "Variables.Damage")
                && values.Any(value => value.PropertyPath == "Variables.damage")
                && !values.Any(value => value.PropertyPath.EndsWith("ghost") || value.PropertyPath.EndsWith("hidden") || value.PropertyPath.EndsWith("privateValue")),
                "Inspector and runtime disagree on comments, adjacent declarations or scope.");
            Control field = All(inspector).Single(control => control.Name == "InspectorProperty_Variables_hero");
            ComboBox choice = All(field).OfType<ComboBox>().Single();
            HeadlessHarness.Assert(choice.DropDownStyle == ComboBoxStyle.DropDownList && choice.Items.Count == 3
                && choice.Items.Cast<object>().Any(item => item.ToString() == "Second sprite"), "Image reference has no typed dropdown/None choice.");
            choice.SelectedItem = choice.Items.Cast<object>().Single(item => item.ToString() == "Second sprite");
            HeadlessHarness.Assert(editor.ScriptText.Contains("var hero = \"Second sprite\";", StringComparison.Ordinal)
                && File.ReadAllText(script) == source, "Actual dropdown did not edit the retained source at its save boundary.");
            HeadlessHarness.Assert(inspector.SetEditableValue("Variables.damage", 9d)
                && Value(editor, "Variables.Damage").Equals(2d) && Value(editor, "Variables.damage").Equals(9d), "Case-sensitive variables overwrite each other.");
            editor.Undo(); HeadlessHarness.Assert(Value(editor, "Variables.damage").Equals(3d), "Case-sensitive edit is not undoable."); editor.Redo(); editor.Save();
            using var reopened = new PgslScriptEditorControl(script, project.RootPath);
            HeadlessHarness.Assert(Value(reopened, "Variables.hero").Equals("Second sprite")
                && reopened.GetLiveInspectorValues().Single(value => value.PropertyPath == "Variables.optional").AssetKind == ResourceKind.Audio,
                "Typed references or initially empty optional references were lost on reopen.");
            choice.SelectedIndex = 0; HeadlessHarness.Assert(Value(editor, "Variables.hero").Equals(string.Empty), "None did not clear the reference."); editor.Undo();
            var runtimeVariables = PgslExposedVariables.Reflect(editor.ScriptText);
            HeadlessHarness.Assert(runtimeVariables.Single(value => value.Name == "hero").Value.Equals("Second sprite"), "Saved Inspector reference is not the runtime literal.");
            foreach (int scale in new[] { 100, 200 })
            {
                var settings = new GenesisSettings(); settings.Appearance.InterfaceScale = scale / 100f;
                ThemeService.ApplySettings(settings); GateSuite.Pump(4, 20);
                string capture = "inspector-typed-references-" + scale + ".png";
                ctx.Report.Images.Add(ImageResult.From("Typed Inspector references", capture, VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, capture))));
            }
            editor.Save(); host.Close(); ThemeService.ApplySettings(new GenesisSettings());
            GC.KeepAlive(firstImage); GC.KeepAlive(secondImage);
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.ResourceInspector", () =>
            HeadlessHarness.Assert(ctx.Report.Tests.Skip(start).All(result => result.Passed), "One or more full resource Inspector workflows failed."));
    }

    private static object Value(ILiveResourceInspectorTarget live, string field) => live.GetLiveInspectorValues().Single(value => value.PropertyPath == field).Value;
    private static string ResourceText(string path, object owner) => File.ReadAllText(path)
        + (owner is ObjectEditorControl obj ? string.Concat(Directory.EnumerateFiles(obj.EventFolder, "*.pgsl")
            .OrderBy(file => file, StringComparer.Ordinal).Select(File.ReadAllText)) : string.Empty);
    private static bool Same(object actual, object expected) => expected is string text ? actual.ToString() == text
        : double.TryParse(Convert.ToString(actual, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
          && Math.Abs(value - Convert.ToDouble(expected, CultureInfo.InvariantCulture)) < .001;
    private static IEnumerable<Control> All(Control control)
    {
        foreach (Control child in control.Controls) { yield return child; foreach (Control nested in All(child)) yield return nested; }
    }
}
