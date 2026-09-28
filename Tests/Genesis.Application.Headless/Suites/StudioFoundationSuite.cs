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
using Genesis.Application.Editors.Suite.Objects.VisualActions;
using Genesis.Application.Studio;
using Genesis.Application.Studio.Docking;
using Genesis.Application.Studio.Forms;
using WeifenLuo.WinFormsUI.Docking;
using Genesis.Rendering.Core;
using Genesis.Rendering.Viewport;

namespace Genesis.Application.Headless.Suites;

internal static class StudioFoundationSuite
{
    public static void Run(HeadlessContext context)
    {
        HeadlessHarness.BeginMajor(context.Report, "Studio foundation");
        int firstFoundationCase = context.Report.Tests.Count;
        void Check(string name, Action test) => HeadlessHarness.RunCase(context.Report, "Studio.Foundation." + name, test);
        StudioFoundationCoreCases.Run(Check);
        Check("Authoring.ObjectActionsHaveOneSpawnChoiceAndReadLegacyBlocks", () =>
        {
            ProjectSession project = new ProjectService().CreateProject(context.Workspace, "Object terminology", "Blank");
            new ResourceService(project).CreateResource(project.AssetsPath, ResourceKind.GameObject, "Marker");
            VisualActionTemplate legacy = new("Spawn Prefab", "CreateInstance", "Instances", "Spawn an Object resource",
                [new("obj", "Marker", VisualActionValueKind.Asset, ResourceKind.GameObject), new("x", "10"), new("y", "20"), new("z", "0")]);
            string source = "// Keep this handwritten comment\n" + VisualActionSyntax.CreateBlock(legacy, "legacy_spawn");
            string presetFile = Path.Combine(project.RootPath, ".genesis", "Editor", "ActionPresets.json");
            new VisualActionPresetStore(project.RootPath).Save(legacy);
            string savedPresets = File.ReadAllText(presetFile);
            using Form host = GateSuite.NewHost(1280, 780);
            using VisualActionBuilderControl builder = new(project.RootPath);
            int changes = 0; builder.SourceChanged += (_, _) => changes++;
            builder.LoadSource(source, groupName: "Create"); host.Controls.Add(builder); GateSuite.ShowHost(host); GateSuite.Pump(3, 15);
            VisualActionBlock oldBlock = builder.Blocks.Single();
            Assert(oldBlock.Name == "Create Instance" && oldBlock.CommandName == "CreateInstance"
                && oldBlock.Parameters.Single(parameter => parameter.Name == "obj").Value == "Marker"
                && builder.Source == source && !builder.CanUndo && changes == 0 && File.ReadAllText(presetFile) == savedPresets,
                "Opening a legacy spawn action changed its code, reference, history or preset file.");
            RichTextBox preview = (RichTextBox)typeof(VisualActionBuilderControl).GetField("_preview",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(builder)!;
            Assert(preview.Text.Contains("Keep this handwritten comment", StringComparison.Ordinal)
                && preview.Text.Contains("CreateInstance(\"Marker\", 10, 20, 0);", StringComparison.Ordinal)
                && !preview.Text.Contains("<action", StringComparison.Ordinal) && !preview.Text.Contains("Prefab", StringComparison.Ordinal),
                "The readable code preview leaks editor markers or hides handwritten PGSL.");
            TreeView palette = (TreeView)typeof(VisualActionBuilderControl).GetField("_palette",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(builder)!;
            VisualActionPaletteItem[] items = Nodes(palette.Nodes).Select(node => node.Tag).OfType<VisualActionPaletteItem>().ToArray();
            Assert(items.All(item => !item.Name.Contains("Prefab", StringComparison.OrdinalIgnoreCase))
                && items.Count(item => !item.IsUserPreset && item.Template.CommandName == "CreateInstance") == 1,
                "The Object action palette still exposes Prefabs or duplicate spawn commands.");
            Assert(!builder.InsertBlueprintAction("Spawn Prefab") && builder.InsertBlueprintAction("Create Instance"),
                "The single Create Instance action is unavailable or the removed alias remains insertable.");
            Assert(builder.Blocks.Count == 2 && builder.Blocks.All(block => block.CommandName == "CreateInstance"),
                "Terminology cleanup changed the executable spawn command.");
            builder.Undo(); Assert(builder.Source == source, "Undo did not restore the unchanged legacy source.");
            foreach (TreeNode category in palette.Nodes)
            { if (category.Text == "INSTANCES") category.Expand(); else category.Collapse(); }
            palette.TopNode = palette.Nodes[0]; GateSuite.Pump(2, 15);
            string imageFile = "object-actions-create-instance.png";
            context.Report.Images.Add(ImageResult.From("Object actions use Create Instance", imageFile,
                VisualCapture.CaptureOpenForm(host, Path.Combine(context.Captures, imageFile))));
            host.Close();

            static IEnumerable<TreeNode> Nodes(TreeNodeCollection collection)
            {
                foreach (TreeNode node in collection)
                { yield return node; foreach (TreeNode child in Nodes(node.Nodes)) yield return child; }
            }
        });
        Check("Inspector.MixedReadOnlyAxesKeepIndependentEditRoutes", () =>
        {
            using Genesis.Application.Editors.Suite.Inspector.ResourceInspectorPropertySurface surface = new();
            surface.ShowIdentityGroup = false;
            surface.InspectLive(new ResourceItem
            {
                Name = "Live fixture", FullPath = Path.Combine(Path.GetTempPath(), "Live fixture.object.json"),
                RelativePath = "Live fixture.object.json", Kind = ResourceKind.GameObject, IsFolder = false,
            },
            [
                new("Instance", "Runtime.Instance.x", "X", 10f),
                new("Instance", "Runtime.Instance.y", "Y", 20f, ReadOnly: true),
                new("Instance", "Runtime.Instance.z", "Z", 30f),
            ]);
            Assert(surface.SetValue("Runtime.Instance.x", 15f) && surface.SetValue("Runtime.Instance.z", 35f),
                "Editable axes lost their individual setters.");
            Assert(!surface.SetValue("Runtime.Instance.y", 25f)
                && !surface.EditablePropertyPaths.Contains("Runtime.Instance.y"),
                "A read-only live axis registered an edit route.");
        });
        Check("Theme.InterfaceScaleKeepsRowsLegibleAndRestoresGeometry", () =>
        {
            GenesisSettings appearance = new();
            try
            {
                using Form host = GateSuite.NewHost(600, 400);
                Panel rail = new() { Dock = DockStyle.Left, Width = 96 };
                Label footer = new() { Dock = DockStyle.Bottom, Height = 22, Text = "Saved resource" };
                ToolStrip commands = new() { Dock = DockStyle.Top, AutoSize = false, Height = 44 };
                commands.Items.Add("Save");
                host.Controls.Add(rail);
                host.Controls.Add(footer);
                host.Controls.Add(commands);
                appearance.Appearance.InterfaceScale = 2;
                Genesis.Application.Studio.Theme.ThemeService.ApplySettings(appearance);
                Genesis.Application.Studio.Theme.ThemeService.Apply(host);
                GateSuite.ShowHost(host);
                Assert(footer.Height >= TextRenderer.MeasureText(footer.Text, footer.Font).Height
                    && commands.Height >= TextRenderer.MeasureText("Save", commands.Font).Height + commands.Padding.Vertical,
                    "Enlarged application fonts are clipped by fixed-height rows.");
                int height = commands.Height;
                Genesis.Application.Studio.Theme.ThemeService.Apply(host);
                Assert(commands.Height == height && rail.Width == 192, "Repeated theme application compounds scaling.");
                appearance.Appearance.InterfaceScale = 1;
                Genesis.Application.Studio.Theme.ThemeService.ApplySettings(appearance);
                Genesis.Application.Studio.Theme.ThemeService.Apply(host);
                Assert(commands.Height == 44 && rail.Width == 96 && footer.Height == 22,
                    "Restoring interface scale did not restore row and mode-rail dimensions.");
            }
            finally
            {
                Genesis.Application.Studio.Theme.ThemeService.ApplySettings(new GenesisSettings());
            }
        });
        Check("Shell.RendererDropdownPersistsAndRecreatesLiveViewport", () =>
        {
            RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
            try
            {
                using StudioFixture fixture = new();
                StatusStrip status = fixture.Shell.Controls.OfType<StatusStrip>().Single();
                ToolStripDropDownButton selector = status.Items.OfType<ToolStripDropDownButton>().Single();
                Assert(selector.Name == "RendererSelector" && ReferenceEquals(status.Items[status.Items.Count - 1], selector),
                    "Renderer dropdown is not at the bottom right.");
                Assert(selector.DropDownItems.Count == RenderBackendCatalog.All.Count,
                    "Renderer selector does not cover the active backend registry.");
                using Form host = new() { ClientSize = new System.Drawing.Size(320, 240) };
                using D3DViewportControl viewport = new() { Dock = DockStyle.Fill };
                host.Controls.Add(viewport);
                GateSuite.ShowHost(host);
                viewport.RenderFrame();
                object originalRenderer = viewport.Renderer;
                ToolStripMenuItem software = selector.DropDownItems.OfType<ToolStripMenuItem>()
                    .Single(item => item.Name == "renderer.Software");
                software.PerformClick();
                viewport.RenderFrame();
                Assert(RenderBackendSelection.RequestedBackend == RenderBackendOption.Software
                    && viewport.Renderer.BackendName.Contains("Software", StringComparison.OrdinalIgnoreCase)
                    && !ReferenceEquals(originalRenderer, viewport.Renderer), "Selecting Software did not recreate the live viewport.");
                SettingsService reopened = new(Path.Combine(Path.GetDirectoryName(fixture.Project.RootPath)!, "settings.json"));
                Assert(reopened.Current.Rendering.Backend == RenderBackendCatalog.Describe(RenderBackendOption.Software).SettingsValue,
                    "Renderer choice was not persisted.");
                selector.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Name == "renderer.DX11").PerformClick();
                viewport.RenderFrame();
                Assert(RenderBackendSelection.RequestedBackend == RenderBackendOption.SilkNetDx11
                    && !viewport.Renderer.BackendName.Contains("Software", StringComparison.OrdinalIgnoreCase),
                    "Switching back to DX11 left the old renderer active.");
            }
            finally { RenderBackendSelection.Configure(previous); }
        });
        Check("Theme.OpenSuiteEditorsApplyInterfaceGeometry", () =>
        {
            try
            {
                using StudioFixture fixture = new();
                GenesisSettings appearance = new();
                appearance.Appearance.InterfaceScale = 2;
                Genesis.Application.Studio.Theme.ThemeService.ApplySettings(appearance);
                SuiteChromeBridge.Push();
                GateSuite.ShowHost(fixture.Shell);
                ResourceItem resource = fixture.Create(ResourceKind.Room, "Rooms", "Scaled Room");
                SuiteEditorDocument document = (SuiteEditorDocument)fixture.Shell.OpenStudioResource(resource);
                GateSuite.Pump(3, 10);
                Control editor = document.Surface.AsControl;
                Label footer = Descendants(editor).OfType<Label>().FirstOrDefault(label => label.Dock == DockStyle.Bottom)
                    ?? throw new InvalidOperationException("No status row in " + editor.GetType().Name + ": "
                        + string.Join(", ", editor.Controls.Cast<Control>().Select(control => control.GetType().Name + " " + control.Dock)));
                Assert(footer.Height >= TextRenderer.MeasureText(footer.Text, footer.Font).Height,
                    "A newly opened Suite editor bypassed scaled row geometry.");
                Assert(((Genesis.Application.Editors.Suite.Rooms.RoomEditorControl)editor).IsNarrowLayout,
                    "The room retained a crowded wide layout at an enlarged interface scale.");
            }
            finally
            {
                Genesis.Application.Studio.Theme.ThemeService.ApplySettings(new GenesisSettings());
                SuiteChromeBridge.Push();
            }
        });
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
            Assert(buttons.Length >= 6 && buttons.All(button => fixture.Shell.CommandCatalog.Find(button.Name ?? string.Empty) is not null),
                "Shell toolbar lost its shared command bindings.");
        });
        Check("Shell.CommandReferenceUsesFourRealScopesAndOnePalette", () =>
        {
            using StudioFixture fixture = new();
            ToolStripMenuItem[] menuEntries = fixture.Shell.MainMenuStrip!.Items.OfType<ToolStripMenuItem>()
                .SelectMany(menu => menu.DropDownItems.OfType<ToolStripMenuItem>()).ToArray();
            Assert(menuEntries.Count(item => item.Name == "studio.commands") == 1
                && !Descendants(fixture.Shell).OfType<ToolStrip>().SelectMany(strip => strip.Items.OfType<ToolStripButton>())
                    .Any(button => button.Name == "studio.commands"), "The shell advertises duplicate command palettes.");
            fixture.Shell.OpenStudioResource(fixture.Create(ResourceKind.PgslScript, "Scripts", "Behavior"));
            using PgslCommandReferenceForm reference = fixture.Shell.CreateCommandReference();
            GateSuite.ShowHost(reference);
            Assert(reference.CommandScopes.SequenceEqual(new[] { "PGSL Game Code", "Engine API", "Editor", "Shell" }),
                "Help does not distinguish all four command scopes.");
            reference.SelectEditorTab();
            Assert(reference.VisibleCommandCount > 8 && !reference.AutoTestEnabled,
                "Editor help is missing the actual open editor commands or can execute destructive auto-tests.");
            reference.SelectShellTab();
            Genesis.Application.Studio.Theme.ThemeService.Apply(reference);
            GateSuite.Pump(4, 10);
            Assert(reference.VisibleCommandCount == fixture.Shell.CommandCatalog.Commands.Count && !reference.AutoTestEnabled,
                "Shell help diverges from the real command palette catalog.");
            reference.SelectPgslTab();
            Assert(reference.VisibleCommandCount == reference.CatalogueCount && reference.AutoTestEnabled,
                "Returning from Shell help lost PGSL command diagnostics.");
            reference.SelectEngineTab(); GateSuite.Pump(4, 10);
            Assert(reference.ActiveCommandPathCaption == "ENGINE BACKEND / API"
                && reference.VisibleCommandCount == reference.EngineCatalogueCount,
                "Engine scope selection did not survive painting and message processing.");
        });
        Check("Shell.ProjectHubNavigationRemainsUsableAcrossHiddenPagesAndScaleChanges", () =>
        {
            try
            {
                using StudioFixture fixture = new();
                using ProjectHubForm hub = new(fixture.Services);
                GateSuite.ShowHost(hub);
                foreach (float scale in new[] { 1f, 2f, 1f })
                {
                    GenesisSettings appearance = new(); appearance.Appearance.InterfaceScale = scale;
                    Genesis.Application.Studio.Theme.ThemeService.ApplySettings(appearance);
                    foreach (HubSection section in new[] { HubSection.Templates, HubSection.Projects, HubSection.Templates })
                    {
                        Assert(hub.ClickNavigation(section) && hub.CurrentSection == section,
                            "The real Project Hub navigation did not open " + section + " at interface scale " + scale);
                        GateSuite.Pump(2, 15);
                        hub.ApplyResponsiveLayoutForTest();
                    }
                }
                hub.Close();
            }
            finally { Genesis.Application.Studio.Theme.ThemeService.ApplySettings(new GenesisSettings()); }
        });
        Check("Shell.StartPageOpensAtTopAndKeepsEnlargedProjectHeadingVisible", () =>
        {
            try
            {
                foreach (float scale in new[] { 1f, 2f })
                {
                    GenesisSettings appearance = new(); appearance.Appearance.InterfaceScale = scale;
                    Genesis.Application.Studio.Theme.ThemeService.ApplySettings(appearance);
                    using StudioFixture fixture = new();
                    fixture.Shell.ClientSize = new System.Drawing.Size(1480, 900);
                    GateSuite.Pump(6, 20);
                    WelcomeDocument start = fixture.Shell.DockPanel.Contents.OfType<WelcomeDocument>().Single();
                    Panel scroll = start.Controls.OfType<Panel>().Single();
                    Label eyebrow = Descendants(start).OfType<Label>().Single(label => label.Text == "CURRENT PROJECT");
                    System.Drawing.Rectangle heading = scroll.RectangleToClient(eyebrow.RectangleToScreen(eyebrow.ClientRectangle));
                    Assert(scroll.AutoScrollPosition.Y == 0 && scroll.ClientRectangle.Contains(heading),
                        "The Start page opens part way down its project heading at interface scale " + scale);
                    if (scale == 2)
                    {
                        scroll.AutoScrollPosition = new System.Drawing.Point(0, 140);
                        int position = scroll.AutoScrollPosition.Y;
                        Genesis.Application.Studio.Theme.ThemeService.Apply(start); GateSuite.Pump(4, 10);
                        Assert(position < 0 && scroll.AutoScrollPosition.Y == position,
                            "Refreshing the theme discards the Start page's user scroll position.");
                    }
                }
            }
            finally { Genesis.Application.Studio.Theme.ThemeService.ApplySettings(new GenesisSettings()); }
        });
        Check("Shell.DockCaptionsAndTabsFollowLiveInterfaceScale", () =>
        {
            try
            {
                Genesis.Application.Studio.Theme.ThemeService.ApplySettings(new GenesisSettings());
                using StudioFixture fixture = new();
                fixture.Shell.OpenStudioResource(fixture.Create(ResourceKind.Note, "Notes", "Game notes"));
                GateSuite.Pump(4, 15);
                DockPane documentPane = fixture.Shell.DockPanel.Panes.Single(pane => pane.DockState == DockState.Document);
                Control tabs = documentPane.Controls.OfType<DockPaneStripBase>().Single();
                Control caption = fixture.Shell.AssetBrowser.DockHandler.Pane.Controls.OfType<DockPaneCaptionBase>().Single();
                int normalTabs = tabs.Height, normalCaption = caption.Height;
                GenesisSettings enlarged = new(); enlarged.Appearance.InterfaceScale = 2f;
                Genesis.Application.Studio.Theme.ThemeService.ApplySettings(enlarged);
                GateSuite.Pump(4, 15);
                var skin = fixture.Shell.DockPanel.Theme.Skin;
                Assert(skin.DockPaneStripSkin.TextFont.SizeInPoints >= 18
                    && skin.AutoHideStripSkin.TextFont.SizeInPoints >= 18,
                    "Dock and auto-hide captions ignored the enlarged interface font.");
                Assert(tabs.Height > normalTabs && caption.Height > normalCaption
                    && tabs.Height >= skin.DockPaneStripSkin.TextFont.Height
                    && caption.Height >= skin.DockPaneStripSkin.TextFont.Height,
                    "Live dock chrome did not remeasure enough space for enlarged text.");
                Genesis.Application.Studio.Theme.ThemeService.ApplySettings(new GenesisSettings());
                GateSuite.Pump(4, 15);
                Assert(tabs.Height == normalTabs && caption.Height == normalCaption,
                    "Returning to normal scale retained enlarged dock geometry.");
            }
            finally { Genesis.Application.Studio.Theme.ThemeService.ApplySettings(new GenesisSettings()); }
        });
        Check("Shell.DialogActionsRemainReadableAndReachableAtEnlargedScale", () =>
        {
            try
            {
                GenesisSettings enlarged = new(); enlarged.Appearance.InterfaceScale = 2f;
                Genesis.Application.Studio.Theme.ThemeService.ApplySettings(enlarged);
                using StudioFixture fixture = new();
                using NewProjectDialog create = new("2DShowcase");
                using ExportGameDialog export = new(fixture.Project);
                using PreferencesForm preferences = new(fixture.Services.Settings, fixture.Project);
                foreach (Form dialog in new Form[] { create, export, preferences })
                {
                    GateSuite.ShowHost(dialog);
                    GateSuite.Pump(3, 15);
                    ListBox? categories = dialog is PreferencesForm
                        ? Descendants(dialog).OfType<ListBox>().Single(list => list.Items.Contains("Appearance")) : null;
                    foreach (object category in categories?.Items.Cast<object>().ToArray() ?? [string.Empty])
                    {
                        if (categories is not null) { categories.SelectedItem = category; GateSuite.Pump(2, 15); }
                        foreach (Button button in Descendants(dialog).OfType<Button>().Where(button => button.Visible))
                        {
                            string text = button is Genesis.Application.Studio.Controls.ModernButton modern && modern.Glyph.Length > 0
                                ? modern.Glyph + "  " + button.Text : button.Text;
                            int needed = TextRenderer.MeasureText(text, button.Font).Width;
                            Assert(button.Width >= needed && button.Height >= button.Font.Height,
                                dialog.Text + ": " + text + " needs " + needed + "px at " + button.Font.SizeInPoints
                                + "pt; button is " + button.Bounds + ".");
                        }
                    }
                    Button[] actions = Descendants(dialog).OfType<Button>().Where(button => button.Text is "Create" or "Export Game" or "Cancel" or "Apply" or "OK").ToArray();
                    Assert(actions.Length > 0 && actions.All(button => dialog.ClientRectangle.Contains(
                        dialog.RectangleToClient(button.RectangleToScreen(button.ClientRectangle)))),
                        "A primary dialog action cannot be reached without scrolling.");
                    dialog.Close();
                }
            }
            finally { Genesis.Application.Studio.Theme.ThemeService.ApplySettings(new GenesisSettings()); }
        });
        Check("Shell.EditorCommandHelpIncludesNativeActionsAndAvailability", () =>
        {
            using StudioFixture fixture = new();
            fixture.Shell.OpenStudioResource(fixture.Create(ResourceKind.Shader, "Shaders", "Sprite Colour"));
            GateSuite.Pump(3, 30);
            SuiteEditorDocument document = fixture.Shell.DockPanel.Contents.OfType<SuiteEditorDocument>().Single();
            Button[] actions = Descendants(document).OfType<Button>().Where(button =>
                (button.AccessibleName ?? string.Empty).Contains("shader pass", StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert(actions.Length >= 5, "The fixture did not open the shader's native pass actions.");
            using PgslCommandReferenceForm reference = fixture.Shell.CreateCommandReference();
            foreach (Button action in actions)
            {
                CommandReferenceEntry entry = reference.EditorCommands.Single(command => command.Title == action.AccessibleName);
                Assert(entry.Enabled == action.Enabled && (entry.Enabled || entry.DisabledReason.Length > 0),
                    "Editor help changed the native action's availability: " + action.AccessibleName);
            }
            Assert(reference.EditorCommands.Select(command => command.Id).Distinct().Count() == reference.EditorCommands.Count,
                "The same editor action appears more than once in command help.");
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
        Check("Shell.PasteValidatesResourceTypeAndPreservesContent", () =>
        {
            using StudioFixture fixture = new();
            ResourceItem note = fixture.Create(ResourceKind.Note, "Notes", "Paste example");
            fixture.Shell.AssetBrowser.RefreshTree();
            Assert(fixture.Shell.AssetBrowser.SelectPath(note.FullPath)
                && fixture.Shell.AssetBrowser.ExecuteShortcut(Keys.Control | Keys.C), "Could not copy a real Note.");
            Assert(fixture.Shell.AssetBrowser.SelectPath(Path.Combine(fixture.Project.AssetsPath, "Objects")), "Objects root is missing.");
            Assert(!fixture.Shell.AssetBrowser.CanExecute(ResourceBrowserCommand.Paste), "Paste was available for the wrong resource type.");
            int originalFiles = Directory.GetFiles(fixture.Project.AssetsPath, "*", SearchOption.AllDirectories).Length;
            fixture.Shell.AssetBrowser.ExecuteShortcut(Keys.Control | Keys.V);
            Assert(Directory.GetFiles(fixture.Project.AssetsPath, "*", SearchOption.AllDirectories).Length == originalFiles,
                "A consumed shortcut still pasted a Note into Objects.");
            Assert(fixture.Shell.AssetBrowser.SelectPath(Path.Combine(fixture.Project.AssetsPath, "Notes"))
                && fixture.Shell.AssetBrowser.ExecuteShortcut(Keys.Control | Keys.V), "Paste was unavailable in the matching resource folder.");
            ResourceItem copy = fixture.Shell.AssetBrowser.SelectedResource ?? throw new InvalidOperationException("Paste lost the new selection.");
            Assert(copy.FullPath != note.FullPath && File.Exists(copy.FullPath)
                && File.ReadAllText(copy.FullPath) == File.ReadAllText(note.FullPath), "Paste lost content or overwrote the original Note.");
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
        Check("Shell", () =>
        {
            TestCaseResult[] cases = context.Report.Tests.Skip(firstFoundationCase).ToArray();
            string[] required = ["Shell.RendererDropdownPersistsAndRecreatesLiveViewport",
                "Shell.AllMenuCommandsShareTheCatalog", "Shell.ToolbarCommandsShareCatalogIdentities",
                "Shell.CommandReferenceUsesFourRealScopesAndOnePalette",
                "Shell.StartPageOpensAtTopAndKeepsEnlargedProjectHeadingVisible",
                "Shell.EditorCommandHelpIncludesNativeActionsAndAvailability", "Palette.LayoutRemainsUsableWhenResized"];
            Assert(required.All(name => cases.Any(test => test.Name == "Studio.Foundation." + name)),
                "The complete shell workflow is missing a required shell check.");
            Assert(cases.All(test => test.Passed), "Shell foundation failed: "
                + string.Join("; ", cases.Where(test => !test.Passed).Select(test => test.Name + ": " + test.Error)));
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
