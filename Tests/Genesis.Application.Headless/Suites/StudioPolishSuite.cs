using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Genesis.Application.Core.Diagnostics;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Settings;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Studio;
using Genesis.Application.Studio.Editing;
using Genesis.Application.Studio.Forms;
using Genesis.Runtime.Scene;

namespace Genesis.Application.Headless.Suites;

internal static class StudioPolishSuite
{
    public static void Run(HeadlessContext context)
    {
        HeadlessHarness.BeginMajor(context.Report, "Studio H19 editor polish");
        void Check(string name, Action test) => HeadlessHarness.RunCase(context.Report, "Studio.Polish." + name, test);
        StudioPolishCoreCases.Run(Check);
        Check("Picker.VirtualGridAndListConstructAndSwitch", () => WithProject(fixture =>
        {
            using AssetPickerModal picker = fixture.Picker();
            Assert(picker.ResultsControl.VirtualMode && picker.ResultsControl.View == View.LargeIcon && picker.ThumbnailCount == 0,
                "Picker construction used Tile view or synchronously decoded thumbnails.");
            GateSuite.ShowHost(picker);
            foreach (string label in new[] { "List", "Grid", "List", "Grid" })
                Descendants(picker).OfType<Button>().Single(button => button.Text == label).PerformClick();
            Assert(picker.ResultsControl.VirtualMode && picker.ResultsControl.View == View.LargeIcon, "View switching lost virtual results.");
            picker.Close();
        }));
        Check("Picker.CurrentNameSelectedAndEmittedWithoutExtension", () => WithProject(fixture =>
        {
            using AssetPickerModal picker = fixture.Picker("Night Sky"); GateSuite.ShowHost(picker);
            Assert(picker.SelectButton.Enabled && picker.ResultsControl.SelectedIndices.Count == 1, "Current image was not selected.");
            picker.SelectButton.PerformClick();
            Assert(picker.SelectedAsset?.Reference == "Night Sky", "Picker returned a storage filename instead of a name.");
        }));
        Check("Picker.SearchClearsStaleVirtualSelection", () => WithProject(fixture =>
        {
            using AssetPickerModal picker = fixture.Picker("Night Sky"); GateSuite.ShowHost(picker);
            picker.SetFilter("NothingMatchesXYZ987");
            Assert(picker.ResultsControl.VirtualListSize == 0 && picker.ResultsControl.SelectedIndices.Count == 0
                && !picker.SelectButton.Enabled, "Filtering retained a now-invalid virtual item index.");
            picker.SetFilter("Morning");
            Assert(picker.FilteredAssets.Count == 1 && picker.SelectButton.Enabled, "Search did not select its valid result.");
            picker.SelectButton.PerformClick(); Assert(picker.SelectedAsset?.Reference == "Morning Sky", "Search accepted the stale prior resource.");
        }));
        Check("Picker.BackgroundUsageOnlyShowsFlaggedImages", () => WithProject(fixture =>
        {
            string spriteOnly = fixture.Resources.CreateResource(Path.Combine(fixture.Project.AssetsPath, "Sprites"), ResourceKind.Image, "Gameplay Icon");
            ImageDocument icon = ImageDocumentSerializer.LoadAtomic(spriteOnly).Document;
            icon.Usage.Allowed = ImageUsage.Sprite;
            ImageDocumentSerializer.SaveAtomic(spriteOnly, icon);
            using AssetPickerModal picker = new(new(fixture.Project.RootPath, ResourceKind.Image, RequiredImageUsage: ImageUsage.Background),
                ProjectAssetIndex.Enumerate(fixture.Project.RootPath, ResourceKind.Image));
            string[] names = picker.FilteredAssets.Select(entry => entry.Reference).OrderBy(name => name).ToArray();
            Assert(names.SequenceEqual(new[] { "Morning Sky", "Night Sky" }),
                "Background picker exposed an Image without the Background usage flag.");
        }));
        Check("Picker.EmptyProjectIsSafe", () => WithProject(fixture =>
        {
            using AssetPickerModal picker = new(new(fixture.Project.RootPath, ResourceKind.Image), []);
            GateSuite.ShowHost(picker); picker.SetFilter("Anything");
            Assert(picker.FilteredAssets.Count == 0 && !picker.SelectButton.Enabled && !picker.FavouriteButton.Enabled,
                "Empty picker exposed an invalid selection."); picker.Close();
            // Supplying other resource types must not turn an empty image picker into an
            // untyped reference editor, including its folder/favourite/recent views.
            using AssetPickerModal typed = new(new(fixture.Project.RootPath, ResourceKind.Image),
                ProjectAssetIndex.Enumerate(fixture.Project.RootPath, ResourceKind.Room));
            Assert(typed.FilteredAssets.Count == 0, "The picker admitted resources of the wrong kind.");
        }));
        Check("Picker.FavouriteButtonScopeAndReopen", () => WithProject(fixture =>
        {
            using (AssetPickerModal picker = fixture.Picker("Morning Sky"))
            {
                GateSuite.ShowHost(picker); picker.FavouriteButton.PerformClick();
                picker.FolderTree.SelectedNode = picker.FolderTree.Nodes["$favourites"];
                Assert(picker.FilteredAssets.Single().Reference == "Morning Sky", "Favourite scope did not show the bookmarked name."); picker.Close();
            }
            using AssetPickerModal reopened = fixture.Picker(); GateSuite.ShowHost(reopened);
            reopened.FolderTree.SelectedNode = reopened.FolderTree.Nodes["$favourites"];
            Assert(reopened.FilteredAssets.Single().Reference == "Morning Sky", "Favourite was only kept by the old dialog."); reopened.Close();
        }));
        Check("Picker.SelectionCreatesPersistentRecent", () => WithProject(fixture =>
        {
            using (AssetPickerModal picker = fixture.Picker("Night Sky"))
            { GateSuite.ShowHost(picker); picker.SelectButton.PerformClick(); }
            using AssetPickerModal reopened = fixture.Picker(); GateSuite.ShowHost(reopened);
            reopened.FolderTree.SelectedNode = reopened.FolderTree.Nodes["$recent"];
            Assert(reopened.FilteredAssets.Single().Reference == "Night Sky", "Recent scope depends on process-only service history."); reopened.Close();
        }));
        Check("Picker.CatalogGuidFlowsIntoPreferences", () => WithProject(fixture =>
        {
            ProjectAssetEntry entry = ProjectAssetIndex.Enumerate(fixture.Project.RootPath, ResourceKind.Image).First();
            Assert(entry.AssetId != Guid.Empty, "Resource metadata GUID was dropped by the picker index.");
            ResourcePickerPreferences prefs = new(fixture.Project.RootPath);
            Assert(prefs.SetFavourite(entry.AssetId, entry.Reference, true) && prefs.IsFavourite(entry.AssetId, "Renamed Public Resource"),
                "Picker bookmark identity was not stable.");
        }));
        Check("Picker.RapidSelectionAndCloseDisposesPendingWork", () => WithProject(fixture =>
        {
            AssetPickerModal picker = fixture.Picker();
            try
            {
                GateSuite.ShowHost(picker);
                for (int i = 0; i < 40; i++) picker.SetFilter(i % 2 == 0 ? "Morning" : "Night");
                picker.Close(); picker.Dispose(); GateSuite.Pump(5, 10);
                Assert(picker.IsDisposed, "Picker stayed alive while waiting for a preview.");
            }
            finally { picker.Dispose(); }
        }));
        Check("Hierarchy.SingleClickDisclosureDoesNotSelectOrRebuild", () => WithRoom(fixture =>
        {
            RoomObjectsPanel panel = fixture.Editor!.Navigation.ObjectsPanel; fixture.Editor.Navigation.SetSection(RoomNavSection.Instances);
            TreeView tree = panel.InstanceHierarchy; TreeNode layer = tree.Nodes[0].Nodes[0]; layer.Expand();
            int refreshes = panel.HierarchyRefreshCount, structures = panel.HierarchyStructureUpdateCount;
            TreeNode? selection = tree.SelectedNode; IntPtr handle = layer.Handle;
            SendTreeMouse(tree, 0x0201, Arrow(layer)); SendTreeMouse(tree, 0x0202, Arrow(layer));
            Assert(!layer.IsExpanded && ReferenceEquals(selection, tree.SelectedNode) && layer.Handle == handle,
                "Single disclosure click selected a row or did not collapse.");
            Assert(panel.HierarchyRefreshCount == refreshes && panel.HierarchyStructureUpdateCount == structures,
                "Disclosure rebuilt the room hierarchy.");
            SendTreeMouse(tree, 0x0201, Arrow(layer)); SendTreeMouse(tree, 0x0202, Arrow(layer));
            Assert(layer.IsExpanded, "A second single click did not expand.");
        }));
        Check("Hierarchy.DoubleClickGlyphDoesNotToggleTwice", () => WithRoom(fixture =>
        {
            RoomObjectsPanel panel = fixture.Editor!.Navigation.ObjectsPanel; fixture.Editor.Navigation.SetSection(RoomNavSection.Instances);
            TreeView tree = panel.InstanceHierarchy; TreeNode layer = tree.Nodes[0].Nodes[0]; layer.Expand(); Point point = Arrow(layer);
            foreach (int message in new[] { 0x0201, 0x0202, 0x0203, 0x0202 }) SendTreeMouse(tree, message, point);
            Assert(!layer.IsExpanded, "Native double-click handling undid the first disclosure action.");
        }));
        Check("Hierarchy.ValueAndSelectionRetainNativeNodes", () => WithRoom(fixture =>
        {
            RoomEditorControl editor = fixture.Editor!; RoomObjectsPanel panel = editor.Navigation.ObjectsPanel;
            panel.SetInstancesMode(true); TreeNode layer = panel.InstanceHierarchy.Nodes[0].Nodes[0]; layer.Expand();
            RoomNode item = editor.Room.Nodes.Single(node => node.Name == "Actor"); TreeNode row = Find(panel.InstanceHierarchy, item.Id);
            int structures = panel.HierarchyStructureUpdateCount, refreshes = panel.HierarchyRefreshCount; IntPtr native = row.Handle;
            editor.Select(item); editor.Select(null); editor.Select(item);
            Assert(panel.HierarchyRefreshCount == refreshes, "Selecting an object rebuilt the hierarchy.");
            item.Transform.Position[0] += 40; panel.RefreshRoomInstances();
            Assert(ReferenceEquals(row, Find(panel.InstanceHierarchy, item.Id)) && row.Handle == native
                && panel.HierarchyStructureUpdateCount == structures, "Value refresh replaced stable native nodes.");
        }));
        Check("Hierarchy.SearchPreservesDisclosureState", () => WithRoom(fixture =>
        {
            RoomObjectsPanel panel = fixture.Editor!.Navigation.ObjectsPanel; fixture.Editor.Navigation.SetSection(RoomNavSection.Instances);
            TreeNode layer = panel.InstanceHierarchy.Nodes[0].Nodes[0]; layer.Collapse(true);
            panel.SetSearch("Actor"); Assert(layer.IsExpanded, "Filtered results are not reachable.");
            panel.SetSearch(""); Assert(!layer.IsExpanded, "Search destroyed the user's collapsed-layer state.");
        }));
        Check("Hierarchy.ReadOnlyInstancesCannotStealObjectSelection", () => WithRoom(fixture =>
        {
            RoomEditorControl editor = fixture.Editor!; editor.Navigation.SetSection(RoomNavSection.Instances);
            TreeView tree = editor.Navigation.ObjectsPanel.InstanceHierarchy; TreeNode actor = Find(tree, editor.Room.Nodes.Single(n => n.Name == "Actor").Id);
            tree.SelectedNode = actor;
            Assert(editor.SelectedNode is null && tree.SelectedNode != actor, "Instances navigation bypassed the Objects-tab edit boundary.");
        }));
        Check("Hierarchy.TreeMouseCoordinatesRemainSigned", () =>
        {
            nint packed = (nint)((-7 & 0xffff) | ((-11 & 0xffff) << 16));
            Assert(RoomInstanceTreeView.ClientPoint(packed) == new Point(-7, -11), "Mouse coordinate decoding became unsigned.");
        });
        Check("Background.ImageAssignmentUndoRedoAndSave", () => WithRoom(fixture =>
        {
            RoomEditorControl editor = fixture.Editor!; editor.Navigation.SetSection(RoomNavSection.Backgrounds);
            RoomBackgroundsPanel panel = editor.Navigation.BackgroundsPanel; RoomNode original = panel.ActiveBackgroundLayer!;
            Assert(panel.AssignBackgroundImage("Night Sky") && original.Background!.Asset == "Night Sky", "Image assignment failed.");
            editor.Undo(); Assert(original.Background!.Asset == "Morning Sky", "Background assignment did not undo.");
            editor.Redo(); editor.Save();
            Assert(RoomAssetLoader.Parse(editor.ResourcePath).Nodes.Single(n => n.Id == original.Id).Background!.Asset == "Night Sky",
                "Background redo/save changed its public reference.");
        }));
        Check("Background.RepeatedAssignmentDoesNotAddUndo", () => WithRoom(fixture =>
        {
            RoomEditorControl editor = fixture.Editor!; editor.Navigation.SetSection(RoomNavSection.Backgrounds);
            RoomBackgroundsPanel panel = editor.Navigation.BackgroundsPanel;
            Assert(panel.AssignBackgroundImage("Night Sky") && !panel.AssignBackgroundImage("Night Sky"), "No-op assignment created an edit.");
            editor.Undo(); Assert(panel.ActiveBackgroundLayer!.Background!.Asset == "Morning Sky", "No-op obscured the actual undo step.");
        }));
        Check("Background.NewSlotIsOneUndoAndWrongTabCannotAssign", () => WithRoom(fixture =>
        {
            RoomEditorControl editor = fixture.Editor!; RoomBackgroundsPanel panel = editor.Navigation.BackgroundsPanel;
            Assert(!panel.AssignBackgroundImage("Night Sky"), "Objects tab assigned a background.");
            editor.Room.Nodes.RemoveAll(node => node.Kind == RoomNodeKind.Background); editor.Navigation.SetSection(RoomNavSection.Backgrounds);
            int before = editor.Room.Nodes.Count;
            Assert(panel.AssignBackgroundImage("Night Sky") && editor.Room.Nodes.Count == before + 1, "Empty slot was not created.");
            Assert(panel.ActiveBackgroundLayer!.Background!.Layout == RoomBackgroundLayout.StretchView,
                "First image assignment did not choose a visible fixed-view background layout.");
            editor.Undo(); Assert(editor.Room.Nodes.Count == before && panel.ActiveBackgroundLayer is null, "Undo left a phantom empty slot.");
            editor.Redo(); Assert(panel.ActiveBackgroundLayer?.Background?.Asset == "Night Sky", "Redo did not recreate the assigned slot.");
        }));
        Check("Background.NumericEditsUndoIndividuallyWithoutReplacingControls", () => WithRoom(fixture =>
        {
            RoomEditorControl editor = fixture.Editor!; editor.Navigation.SetSection(RoomNavSection.Backgrounds);
            RoomBackgroundsPanel panel = editor.Navigation.BackgroundsPanel;
            NumericUpDown depth = Descendants(panel).OfType<NumericUpDown>().Single(control => control.Maximum == 100000);
            int before = panel.ActiveBackgroundLayer!.Background!.Depth;
            depth.Value = before + 1; depth.Value = before + 2;
            editor.Undo(); Assert(panel.ActiveBackgroundLayer!.Background!.Depth == before + 1 && depth.Value == before + 1,
                "Background numeric undo lost its control or previous detent.");
            editor.Undo(); Assert(panel.ActiveBackgroundLayer!.Background!.Depth == before, "Second numeric undo did not restore the initial depth.");
            editor.Redo(); Assert(depth.Value == before + 1 && !depth.IsDisposed, "Numeric redo replaced the Inspector field.");
        }));
        Check("Background.LockedLayersRejectAssignment", () => WithRoom(fixture =>
        {
            RoomEditorControl editor = fixture.Editor!; editor.Navigation.SetSection(RoomNavSection.Backgrounds);
            foreach (RoomLayer layer in editor.Room.Layers) layer.Locked = true;
            Assert(!editor.Navigation.BackgroundsPanel.AssignBackgroundImage("Night Sky"), "Locked background was changed.");
            editor.Room.Nodes.RemoveAll(node => node.Kind == RoomNodeKind.Background);
            Assert(!editor.Navigation.BackgroundsPanel.AssignBackgroundImage("Night Sky"), "New slot fell back to a locked layer.");
        }));
        Check("Shortcut.ReservedChordPrecedesChildRoutingAndKeepsTarget", () =>
        {
            using Form form = new(); using TextBox child = new(); form.Controls.Add(child); GateSuite.ShowHost(form);
            Control? received = null; int calls = 0;
            using StudioShortcutFilter filter = new(control => control.FindForm() == form, control => { received = control; calls++; },
                () => Keys.Control | Keys.Shift);
            Message press = Message.Create(child.Handle, 0x0100, (IntPtr)(int)Keys.P, IntPtr.Zero);
            Assert(filter.PreFilterMessage(ref press) && ReferenceEquals(received, child) && calls == 1,
                "Native child ate the reserved chord or changed its original context."); form.Close();
        });
        Check("Shortcut.AutoRepeatDoesNotQueueAnotherPalette", () =>
        {
            using Form form = new(); using TextBox child = new(); form.Controls.Add(child); GateSuite.ShowHost(form); int calls = 0;
            using StudioShortcutFilter filter = new(control => control.FindForm() == form, _ => calls++, () => Keys.Control | Keys.Shift);
            Message repeat = Message.Create(child.Handle, 0x0100, (IntPtr)(int)Keys.P, (IntPtr)(1L << 30));
            Assert(filter.PreFilterMessage(ref repeat) && calls == 0, "Held shortcut opened repeated modal palettes."); form.Close();
        });
        Check("Shortcut.ForeignWindowsOtherKeysAndDisposalPassThrough", () =>
        {
            using Form owner = new(), foreign = new(); using TextBox child = new(); foreign.Controls.Add(child); GateSuite.ShowHost(foreign); int calls = 0;
            StudioShortcutFilter filter = new(control => control.FindForm() == owner, _ => calls++, () => Keys.Control | Keys.Shift);
            Message press = Message.Create(child.Handle, 0x0100, (IntPtr)(int)Keys.P, IntPtr.Zero);
            Assert(!filter.PreFilterMessage(ref press) && calls == 0, "Another project's shortcut was intercepted.");
            filter.Dispose(); filter.Dispose(); Assert(!filter.PreFilterMessage(ref press), "Disposed filter still routed keys.");
            using StudioShortcutFilter all = new(_ => true, _ => calls++, () => Keys.Control | Keys.Shift);
            Message save = Message.Create(child.Handle, 0x0100, (IntPtr)(int)Keys.S, IntPtr.Zero);
            Assert(!all.PreFilterMessage(ref save) && calls == 0, "Palette filter changed unrelated editor shortcuts."); foreign.Close();
        });
        Check("Shortcut.ModalDialogDoesNotOpenProjectPalette", () =>
        {
            using Form modal = new(); using TextBox child = new(); modal.Controls.Add(child); int calls = 0; bool handled = true;
            using StudioShortcutFilter filter = new(_ => true, _ => calls++, () => Keys.Control | Keys.Shift);
            modal.Shown += (_, _) =>
            {
                Message press = Message.Create(child.Handle, 0x0100, (IntPtr)(int)Keys.P, IntPtr.Zero);
                handled = filter.PreFilterMessage(ref press); modal.BeginInvoke(new Action(modal.Close));
            };
            modal.ShowDialog(); Assert(!handled && calls == 0, "Project palette escaped into a modal resource picker.");
        });
        Check("Shell.ReservedFilterOpensActualPaletteFromTextInput", () => WithProject(fixture =>
        {
            StudioServices services = new(new SettingsService(Path.Combine(fixture.Root, "shortcut-settings.json")), new ProjectService(), new ProjectValidator(), new StudioLog());
            services.Settings.Current.Editing.AutoSave = false;
            using StudioShellForm shell = new(services, fixture.Project, persistLayout: false);
            using TextBox original = new() { Text = "Original text", Dock = DockStyle.Bottom };
            shell.Controls.Add(original); GateSuite.ShowHost(shell); original.Focus();
            // Exercise the real shell queue and modal palette, not a replica of its dispatch.
            MethodInfo queue = typeof(StudioShellForm).GetMethod("QueueCommandPalette", BindingFlags.Instance | BindingFlags.NonPublic)!;
            using StudioShortcutFilter filter = new(control => control.FindForm() == shell,
                control => queue.Invoke(shell, [control]), () => Keys.Control | Keys.Shift);
            bool opened = false; int ticks = 0;
            using System.Windows.Forms.Timer close = new() { Interval = 15 };
            close.Tick += (_, _) =>
            {
                CommandPaletteForm? palette = System.Windows.Forms.Application.OpenForms.OfType<CommandPaletteForm>().FirstOrDefault();
                if (palette is not null)
                {
                    opened = true; palette.DialogResult = DialogResult.Cancel; palette.Close(); close.Stop();
                }
                else if (++ticks > 100) close.Stop();
            };
            close.Start();
            Message press = Message.Create(original.Handle, 0x0100, (IntPtr)(int)Keys.P, IntPtr.Zero);
            Assert(filter.PreFilterMessage(ref press), "Reserved chord was not claimed by its project.");
            GateSuite.Pump(4, 20);
            Assert(opened && original.Text == "Original text", "The real shell did not open its palette or cancelled into an edit.");
        }));
        Check("Build.IdentityComesFromLoadedStudioAssembly", () =>
        {
            Assert(int.TryParse(StudioBuildInfo.Revision, out int revision) && revision >= 13 && StudioBuildInfo.BuildId.Length > 0
                && StudioBuildInfo.DiagnosticText.Contains(typeof(StudioBuildInfo).Assembly.ManifestModule.ModuleVersionId.ToString("D"))
                && StudioBuildInfo.DiagnosticText.Contains(typeof(StudioBuildInfo).Assembly.Location), "Assembly build identity is missing.");
        });
        Check("Shell.PaletteMenusAndBuildDiagnosticsAreDiscoverable", () => WithProject(fixture =>
        {
            StudioServices services = new(new SettingsService(Path.Combine(fixture.Root, "settings.json")), new ProjectService(), new ProjectValidator(), new StudioLog());
            services.Settings.Current.Editing.AutoSave = false;
            using StudioShellForm shell = new(services, fixture.Project, persistLayout: false); GateSuite.ShowHost(shell);
            Assert(shell.Text.Contains(StudioBuildInfo.BuildId) && shell.Text.Contains($"H{StudioBuildInfo.Revision}"), "Workspace lacks loaded build identity.");
            ToolStripMenuItem[] entries = shell.MainMenuStrip!.Items.OfType<ToolStripMenuItem>()
                .SelectMany(menu => menu.DropDownItems.OfType<ToolStripMenuItem>()).ToArray();
            Assert(entries.Count(item => item.Name == "studio.commands") >= 2
                && shell.CommandCatalog.FindShortcut((int)(Keys.Control | Keys.Shift | Keys.P)) == "studio.commands"
                && entries.Any(item => item.Name == "help.copyBuildInfo"), "Palette/build commands are hidden or not registered.");
            Assert(entries.Any(item => (item.Text ?? string.Empty).Contains("PGSL Command Reference")), "PGSL help is still confused with the action palette.");
        }));
    }

    private static Point Arrow(TreeNode node) => new(node.Bounds.Left - 10, node.Bounds.Top + node.TreeView!.ItemHeight / 2);
    private static TreeNode Find(TreeView tree, string id) => tree.Nodes.Find(id, true).Single();
    private static void SendTreeMouse(TreeView tree, int kind, Point point)
    {
        Message message = Message.Create(tree.Handle, kind, (IntPtr)1, (IntPtr)((point.X & 0xffff) | ((point.Y & 0xffff) << 16)));
        object?[] args = [message];
        typeof(RoomInstanceTreeView).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tree, args);
    }
    private static IEnumerable<Control> Descendants(Control root)
    { foreach (Control child in root.Controls) { yield return child; foreach (Control nested in Descendants(child)) yield return nested; } }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void WithProject(Action<Fixture> action) { using Fixture fixture = new(); action(fixture); }
    private static void WithRoom(Action<Fixture> action) { using Fixture fixture = new(); fixture.OpenRoom(); action(fixture); }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Genesis-H13-" + Guid.NewGuid().ToString("N"));
        public ProjectSession Project { get; }
        public ResourceService Resources { get; }
        public RoomEditorControl? Editor { get; private set; }
        private Form? _host;
        public Fixture()
        {
            Directory.CreateDirectory(Root); Project = new ProjectService().CreateProject(Root, "Polish"); Resources = new(Project);
            foreach (string name in new[] { "Morning Sky", "Night Sky" })
            {
                string imagePath = Resources.CreateResource(Path.Combine(Project.AssetsPath, "Sprites"), ResourceKind.Image, name);
                ImageDocument image = ImageDocumentSerializer.LoadAtomic(imagePath).Document;
                image.Usage.Allowed = ImageUsage.Background;
                ImageDocumentSerializer.SaveAtomic(imagePath, image);
            }
        }
        public AssetPickerModal Picker(string? current = null) => new(new(Project.RootPath, ResourceKind.Image, current),
            ProjectAssetIndex.Enumerate(Project.RootPath, ResourceKind.Image));
        public void OpenRoom()
        {
            string path = Resources.CreateResource(Path.Combine(Project.AssetsPath, "Rooms"), ResourceKind.Room, "Room Check");
            RoomAsset room = RoomAsset.Create("Room Check", RoomDimension.TwoD);
            room.Nodes.Add(new RoomNode { Name = "Actor", LayerId = room.Layers[0].Id, Kind = RoomNodeKind.GameObject });
            room.Nodes.Add(new RoomNode { Name = "Background 1", LayerId = room.Layers[0].Id, Kind = RoomNodeKind.Background,
                Background = new RoomBackgroundData { Asset = "Morning Sky", Layout = RoomBackgroundLayout.StretchRoom } });
            RoomAssetLoader.Save(room, path); Editor = new(path, Project.RootPath) { Dock = DockStyle.Fill };
            _host = new Form { ClientSize = new Size(1200, 800) }; _host.Controls.Add(Editor); GateSuite.ShowHost(_host);
            Editor.FlushPendingRoomUiRefresh();
        }
        public void Dispose()
        {
            _host?.Dispose(); Editor?.Dispose(); GateSuite.Pump(2, 10);
            Directory.Delete(Root, true);
        }
    }
}
