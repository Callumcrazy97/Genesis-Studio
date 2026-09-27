using System.Windows.Forms;
using Genesis.Application.Core.Commands;
using Genesis.Application.Core.Diagnostics;
using Genesis.Application.Core.Editing;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Settings;
using Genesis.Application.Editors;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Studio;
using Genesis.Application.Studio.Docking;
using Genesis.Application.Studio.Forms;
using WeifenLuo.WinFormsUI.Docking;

namespace Genesis.Application.Headless.Suites;

internal static class StudioFoundationSuite
{
    public static void Run(HeadlessContext context)
    {
        HeadlessHarness.BeginMajor(context.Report, "Studio foundation");
        void Check(string name, Action test) => HeadlessHarness.RunCase(context.Report, "Studio.Foundation." + name, test);
        StudioFoundationCoreCases.Run(Check);
        Check("Shell.AllMenuCommandsShareTheCatalog", () =>
        {
            using StudioFixture fixture = new();
            foreach (ToolStripMenuItem menu in fixture.Shell.MainMenuStrip!.Items.OfType<ToolStripMenuItem>())
            foreach (ToolStripMenuItem item in menu.DropDownItems.OfType<ToolStripMenuItem>())
            {
                StudioCommand<ShellCommandContext>? command = fixture.Shell.CommandCatalog.Find(item.Name ?? string.Empty);
                Assert(command is not null, "Menu has an unregistered action: " + item.Text);
                Assert(item.ShortcutKeys == Keys.None, "Menu accelerator bypasses focus routing: " + item.Text);
                Assert(item.ShortcutKeyDisplayString == (command!.Shortcuts.FirstOrDefault()?.DisplayText ?? string.Empty),
                    "Shortcut documentation differs from the registered shortcut.");
            }
            Assert(fixture.Shell.CommandCatalog.FindShortcut((int)(Keys.Control | Keys.Shift | Keys.P)) == "studio.commands",
                "Command palette shortcut is missing.");
        });
        Check("Shell.ToolbarCommandsShareCatalogIdentities", () =>
        {
            using StudioFixture fixture = new();
            ToolStripButton[] buttons = Descendants(fixture.Shell).OfType<ToolStrip>()
                .SelectMany(strip => strip.Items.OfType<ToolStripButton>()).Where(button => (button.Name ?? string.Empty).StartsWith("project.")
                    || (button.Name ?? string.Empty).StartsWith("edit.") || button.Name == "studio.commands").ToArray();
            Assert(buttons.Length >= 7 && buttons.All(button => fixture.Shell.CommandCatalog.Find(button.Name ?? string.Empty) is not null),
                "Shell toolbar lost its shared command bindings.");
        });
        Check("Shell.ResourceCommandsRequireBrowserAndMutableSelection", () =>
        {
            using StudioFixture fixture = new();
            ResourceItem note = fixture.Create(ResourceKind.Note, "Notes", "Test Note");
            fixture.Shell.AssetBrowser.RefreshTree(); fixture.Shell.AssetBrowser.SelectPath(note.FullPath);
            using Panel unrelated = new();
            ShellCommandContext outside = new(unrelated, null, note, false);
            foreach (string id in new[] { "edit.delete", "resource.rename", "resource.duplicate" })
                Assert(!fixture.Shell.CommandCatalog.GetAvailability(id, outside).Enabled, "Resource action escaped the browser: " + id);
            fixture.Shell.AssetBrowser.SelectPath(Path.Combine(fixture.Project.AssetsPath, "Notes"));
            ShellCommandContext root = new(fixture.Shell.AssetBrowser, null, fixture.Shell.AssetBrowser.SelectedResource, true);
            foreach (string id in new[] { "edit.delete", "resource.rename", "resource.duplicate" })
                Assert(!fixture.Shell.CommandCatalog.GetAvailability(id, root).Enabled, "Protected root became editable: " + id);
            Assert(File.Exists(note.FullPath), "Availability checking modified a resource.");
        });
        Check("Shell.EditCommandsRespectChangingEditorScope", () =>
        {
            using StudioFixture fixture = new(); using EditProbe probe = new();
            ShellCommandContext target = new(probe, null, null, false);
            Assert(fixture.Shell.CommandCatalog.GetAvailability("edit.delete", target).Enabled, "Probe was not reachable.");
            probe.Allowed = false;
            Assert(!fixture.Shell.CommandCatalog.TryExecute("edit.delete", target).Succeeded && probe.Edits == 0,
                "Changed layer/tab capability was ignored.");
            probe.Allowed = true;
            Assert(fixture.Shell.CommandCatalog.TryExecute("edit.delete", target).Succeeded && probe.Edits == 1, "Allowed edit was lost.");
            probe.Dispose();
            Assert(!fixture.Shell.CommandCatalog.GetAvailability("edit.delete", target).Enabled, "Closed editor remained editable.");
        });
        Check("Shell.ExplicitTextActionTargetsOriginalField", () =>
        {
            using StudioFixture fixture = new(); using TextBox text = new() { Text = "Genesis" };
            text.Select(0, 3);
            ShellCommandContext original = new(text, null, null, false);
            using CommandPaletteForm palette = new(fixture.Shell.CommandCatalog, original);
            GateSuite.ShowHost(palette); palette.SearchBox.Text = "delete"; System.Windows.Forms.Application.DoEvents();
            Assert(palette.Results.Items.Count == 1 && palette.RunButton.Enabled, "Delete command was not discoverable for selected text.");
            palette.ChooseSelection();
            Assert(palette.SelectedCommandId == "edit.delete" && text.Text == "Genesis", "Palette executed instead of returning a selection.");
            Assert(fixture.Shell.CommandCatalog.TryExecute(palette.SelectedCommandId!, original).Succeeded
                && text.Text == "esis", "Palette search stole the edit target.");
        });
        Check("Shell.DisabledPaletteSelectionExplainsWhy", () =>
        {
            using StudioFixture fixture = new(); using Panel panel = new();
            ShellCommandContext original = new(panel, null, null, false);
            using CommandPaletteForm palette = new(fixture.Shell.CommandCatalog, original);
            GateSuite.ShowHost(palette); palette.SearchBox.Text = "Rename Resource"; System.Windows.Forms.Application.DoEvents();
            Assert(palette.Results.Items.Count == 1 && !palette.RunButton.Enabled
                && palette.Results.Items[0].SubItems[2].Text.Contains("Browser"), "Disabled command lacks its reason.");
            palette.ChooseSelection(); Assert(palette.SelectedCommandId is null, "Disabled action was accepted.");
            palette.Close();
        });
        Check("Shell.PaletteKeepsViewportCapabilitiesWhileSearchHasFocus", () =>
        {
            using StudioFixture fixture = new(); using EditProbe probe = new();
            ShellCommandContext original = new(probe, null, null, false);
            using CommandPaletteForm palette = new(fixture.Shell.CommandCatalog, original);
            GateSuite.ShowHost(palette);
            using IDisposable searchFocus = EditorInputGuard.UseCommandFocus(palette.SearchBox);
            palette.SearchBox.Text = "delete";
            Assert(EditorInputGuard.IsTextEntryFocused() && palette.RunButton.Enabled,
                "Palette's search field disabled the original viewport command.");
            Assert(fixture.Shell.CommandCatalog.TryExecute("edit.delete", original).Succeeded && probe.Edits == 1,
                "Explicit command did not use the original focus context.");
            Assert(EditorInputGuard.IsTextEntryFocused(), "Command focus override leaked into normal input routing.");
            palette.Close();
        });
        Check("Shell.CommandFocusScopesRestoreAfterExceptionAndNesting", () =>
        {
            using TextBox text = new(); using Panel panel = new();
            using (EditorInputGuard.UseCommandFocus(text))
            {
                Assert(EditorInputGuard.IsTextEntryFocused(), "Outer scope lost its text target.");
                try
                {
                    using IDisposable inner = EditorInputGuard.UseCommandFocus(panel);
                    Assert(!EditorInputGuard.IsTextEntryFocused(), "Inner scope did not replace its target.");
                    throw new InvalidOperationException("Fixture");
                }
                catch (InvalidOperationException error) when (error.Message == "Fixture") { }
                Assert(EditorInputGuard.IsTextEntryFocused(), "Nested exception leaked its focus override.");
            }
        });
        Check("Shell.FloatingDocksKeepSharedShortcutRouter", () =>
        {
            using StudioFixture fixture = new();
            ResourceItem note = fixture.Create(ResourceKind.Note, "Notes", "Draft");
            GenesisDockContent document = (GenesisDockContent)fixture.Shell.OpenStudioResource(note);
            document.Show(fixture.Shell.DockPanel, DockState.Float);
            Assert(HasShortcutRouter(document) && HasShortcutRouter(fixture.Shell.AssetBrowser),
                "Floating document/tool windows lost their project shortcuts.");
        });
        Check("Save.FloatingDocumentsAreFoundSavedAndNotOpenedTwice", () =>
        {
            using StudioFixture fixture = new();
            ResourceItem image = fixture.Create(ResourceKind.Image, "Sprites", "Floating Portrait");
            ImageViewerDocument document = (ImageViewerDocument)fixture.Shell.OpenStudioResource(image);
            document.Show(fixture.Shell.DockPanel, DockState.Float);
            Assert(document.TryApplyInspectorValue("origin.x", 7.5), "Could not edit a floating image.");
            using (EditorInputGuard.UseCommandFocus(document.Viewer))
                Assert(ReferenceEquals(fixture.Shell.CaptureCommandContext().Document, document), "Save targeted a different docked document.");
            Assert(ReferenceEquals(fixture.Shell.OpenStudioResource(image), document), "Reopening created a second writer for a floating document.");
            DocumentSaveResult result = fixture.Shell.SaveOpenDocuments();
            Assert(result.Success && result.SavedNames.Contains("Floating Portrait") && !document.IsDirty,
                "Save Project ignored a floating document.");
        });
        Check("Save.SharedImageWindowsPersistOnce", () =>
        {
            using StudioFixture fixture = new(); ResourceItem image = fixture.Create(ResourceKind.Image, "Sprites", "Portrait");
            ImageViewerDocument viewer = (ImageViewerDocument)fixture.Shell.OpenStudioResource(image);
            fixture.Shell.OpenImageEditor(new(image, viewer.Session, viewer.Workspace));
            Assert(viewer.TryApplyInspectorValue("origin.x", 13.25), "Could not make a real authored image edit.");
            DocumentSaveTarget[] targets = fixture.Shell.CaptureSaveTargets().Where(target => target.Name == "Portrait").ToArray();
            Assert(targets.Length == 2 && ReferenceEquals(targets[0].SessionIdentity, targets[1].SessionIdentity), "Image views do not share a save token.");
            DocumentSaveResult result = fixture.Shell.SaveOpenDocuments();
            Assert(result.Success && result.SavedNames.Count(name => name == "Portrait") == 1
                && Math.Abs(ImageDocumentSerializer.LoadAtomic(image.FullPath).Document.Origin.X - 13.25) < .001,
                "Shared image save duplicated writes or lost the authored origin.");
        });
        Check("Save.StandaloneImageWithoutDockedViewerIsIncluded", () =>
        {
            using StudioFixture fixture = new(); ResourceItem image = fixture.Create(ResourceKind.Image, "Sprites", "Detached Portrait");
            ImageDocumentSession session = new(ImageDocumentSerializer.LoadAtomic(image.FullPath).Document, image.FullPath);
            ImageWorkspace workspace = ImageWorkspaceStorage.Load(session);
            fixture.Shell.OpenImageEditor(new(image, session, workspace));
            session.Execute(new OriginEdit());
            ImageEditorWindow window = fixture.Shell.OwnedForms.OfType<ImageEditorWindow>().Single();
            Assert(HasShortcutRouter(window) && window.IsDirty, "Separate authoring window was not routed/dirty.");
            using (EditorInputGuard.UseCommandFocus(window.Editor))
                Assert(ReferenceEquals(fixture.Shell.CaptureCommandContext().Document, window), "Detached Save selected a docked document instead.");
            DocumentSaveResult result = fixture.Shell.SaveOpenDocuments();
            Assert(result.Success && result.SavedNames.Contains("Detached Portrait") && !window.IsDirty
                && Math.Abs(ImageDocumentSerializer.LoadAtomic(image.FullPath).Document.Origin.X - 19) < .001,
                "Save Project omitted a separate Image Editor.");
        });
        Check("Save.RunDebugAndExportStopOnSaveFailure", () =>
        {
            using StudioFixture fixture = new(); ResourceItem note = fixture.Create(ResourceKind.Note, "Notes", "Broken Note");
            using FailingSurface surface = new(note.FullPath, fixture.Project.RootPath);
            using SuiteEditorDocument document = new(note, fixture.Services.Log, surface, "Save fixture");
            document.Show(fixture.Shell.DockPanel, DockState.Document);
            try
            {
                fixture.Shell.RunProject(false); fixture.Shell.RunProject(true);
                fixture.Shell.CommandCatalog.TryExecute("project.export", fixture.Shell.CaptureCommandContext());
                Assert(surface.IsDirty && fixture.Services.Log.Entries.Any(entry => entry.Message.Contains("Run stopped"))
                    && fixture.Services.Log.Entries.Any(entry => entry.Message.Contains("Debug stopped"))
                    && fixture.Services.Log.Entries.Any(entry => entry.Message.Contains("Export stopped"))
                    && !fixture.Services.Log.Entries.Any(entry => entry.Source == "Runner" && entry.Message.Contains("Launched")),
                    "Run/Debug/Export crossed a failed save boundary.");
            }
            finally { surface.Fail = false; document.Save(); }
        });
        Check("Documentation.PublishedAndSourcePathsFindMaster", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "Genesis-doc-path-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Documentation"));
                string master = Path.Combine(root, "Documentation", "README.md");
                File.WriteAllText(master, "Master");
                Assert(StudioShellForm.FindMasterDocumentation(root) == master
                    && StudioShellForm.FindMasterDocumentation(Path.Combine(root, "Source", "Studio", "bin", "Release", "net10.0-windows")) == master,
                    "Published or source/debug lookup missed the master.");
                string published = Path.Combine(root, "Published"); Directory.CreateDirectory(Path.Combine(published, "Documentation"));
                string packaged = Path.Combine(published, "Documentation", "README.md"); File.WriteAllText(packaged, "Packaged");
                Assert(StudioShellForm.FindMasterDocumentation(published) == packaged, "Lookup preferred a stale ancestor master over the packaged one.");
            }
            finally { Directory.Delete(root, recursive: true); }
        });
        Check("Palette.LayoutRemainsUsableWhenResized", () =>
        {
            using StudioFixture fixture = new();
            using CommandPaletteForm palette = new(fixture.Shell.CommandCatalog, new(null, null, null, false));
            GateSuite.ShowHost(palette);
            foreach (Size size in new[] { new Size(600, 370), new Size(820, 500) })
            {
                palette.ClientSize = size; palette.PerformLayout(); System.Windows.Forms.Application.DoEvents();
                Assert(palette.SearchBox.ClientSize.Width > 200 && palette.Results.ClientSize.Height > 70
                    && palette.Results.Columns.Cast<ColumnHeader>().All(column => column.Width > 30),
                    "Palette lost its search/results when resized.");
            }
            palette.Close();
        });
    }

    private static void Assert(bool condition, string message) => HeadlessHarness.Assert(condition, message);
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }
    private static bool HasShortcutRouter(Control window)
    {
        // H16 intentionally removed the designer-visible property. Inspect the runtime field
        // declared by the actual host rather than compiling against the obsolete property.
        for (Type? type = window.GetType(); type is not null; type = type.BaseType)
        {
            System.Reflection.FieldInfo? field = type.GetField("_projectShortcutRouter",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.DeclaredOnly);
            if (field is not null) return field.GetValue(window) is Func<Keys, bool>;
        }
        return false;
    }

    private sealed class EditProbe : UserControl, IEditCommandTarget
    {
        public bool Allowed = true;
        public int Edits;
        public bool CanEdit(EditCommand command) => Allowed && command == EditCommand.Delete && !EditorInputGuard.IsTextEntryFocused();
        public bool TryEdit(EditCommand command) { if (!CanEdit(command)) return false; Edits++; return true; }
    }
    private sealed class OriginEdit : IImageDocumentCommand
    {
        public string Description => "Move origin";
        public long EstimatedByteSize => 16;
        public void Execute(ImageDocumentSession session) => session.Document.Origin.X = 19;
        public void Undo(ImageDocumentSession session) => session.Document.Origin.X = 0;
    }
    private sealed class FailingSurface(string path, string root) : EditorSurfaceControl(path, root)
    {
        public bool Fail = true;
        public override void Save() { if (Fail) throw new IOException("Simulated write failure"); AcceptSave(); }
        protected override void OnCreateControl() { base.OnCreateControl(); MarkDirty(); }
    }
    private sealed class StudioFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "Genesis-H12-" + Guid.NewGuid().ToString("N"));
        public StudioFixture()
        {
            Directory.CreateDirectory(_directory);
            Services = new(new SettingsService(Path.Combine(_directory, "settings.json")), new ProjectService(), new ProjectValidator(), new StudioLog());
            Services.Settings.Current.Editing.AutoSave = false;
            Project = Services.Projects.CreateProject(_directory, "Foundation");
            Resources = new(Project);
            Shell = new(Services, Project, persistLayout: false);
            GateSuite.ShowHost(Shell);
        }
        public StudioServices Services { get; }
        public ProjectSession Project { get; }
        public ResourceService Resources { get; }
        public StudioShellForm Shell { get; }
        public ResourceItem Create(ResourceKind kind, string folder, string name)
        {
            string path = Resources.CreateResource(Path.Combine(Project.AssetsPath, folder), kind, name);
            return Flatten(Resources.BuildTree()).Single(item => string.Equals(item.FullPath, path, StringComparison.OrdinalIgnoreCase));
        }
        private static IEnumerable<ResourceItem> Flatten(ResourceItem resource)
        {
            yield return resource;
            foreach (ResourceItem child in resource.Children)
            foreach (ResourceItem nested in Flatten(child)) yield return nested;
        }
        public void Dispose()
        {
            foreach (Form window in Shell.OwnedForms.ToArray()) window.Dispose();
            Shell.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
