using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Scene;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless.Suites;

internal static class RoomObjectsPanelSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Objects.ParentHierarchyLayerControlsRefreshAndPalette", () =>
        {
            var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "RoomObjectsPanel"), "Room objects");
            var resources = new ResourceService(project);
            string roomPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Room, "Hierarchy");
            string imagePath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, "Marker");
            string objectPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.GameObject, "Marker object");
            File.WriteAllText(objectPath, new JObject { ["name"] = "Marker object", ["sprite"] = Relative(imagePath) }.ToString());
            RoomAsset room = RoomAsset.Create("Hierarchy", RoomDimension.TwoD);
            RoomLayer scenery = new() { Name = "Scenery", Order = 100 }; room.Layers.Add(scenery);
            RoomNode parent = new() { Name = "Platform", LayerId = room.Layers[0].Id, Kind = RoomNodeKind.GameObject,
                GameObject = new RoomGameObjectData { Prefab = Relative(objectPath) } };
            RoomNode child = new() { Name = "Child marker", LayerId = parent.LayerId, ParentId = parent.Id, Kind = RoomNodeKind.GameObject,
                GameObject = new RoomGameObjectData { Prefab = Relative(objectPath) } };
            RoomNode sibling = new() { Name = "Other marker", LayerId = parent.LayerId, Kind = RoomNodeKind.GameObject,
                GameObject = new RoomGameObjectData { Prefab = Relative(objectPath) } };
            room.Nodes.AddRange([parent, child, sibling]);
            foreach (RoomNodeKind kind in new[] { RoomNodeKind.Background, RoomNodeKind.TileLayer, RoomNodeKind.Terrain })
                room.Nodes.Add(new RoomNode { Name = kind + " node", Kind = kind, LayerId = scenery.Id });
            RoomAssetLoader.Save(room, roomPath);
            using var editor = new RoomEditorControl(roomPath, project.RootPath);
            RoomObjectsPanel panel = editor.Navigation.ObjectsPanel;
            using var host = UnattendedWindowing.NewHost(370, 760);
            host.Controls.Add(panel); ThemeService.Apply(host); UnattendedWindowing.ShowWithoutFocus(host);
            panel.RefreshObjects([new ProjectAssetEntry("Marker object", objectPath, Relative(objectPath), ResourceKind.GameObject)]);
            panel.SetInstancesMode(true);
            GateSuite.Pump(3, 20);
            parent = editor.Room.Nodes.Single(node => node.Id == parent.Id);
            child = editor.Room.Nodes.Single(node => node.Id == child.Id);
            sibling = editor.Room.Nodes.Single(node => node.Id == sibling.Id);
            scenery = editor.Room.Layers.Single(layer => layer.Id == scenery.Id);
            Assert(Items().Count(item => item.Tag is RoomNode) == editor.Room.Nodes.Count,
                "Hierarchy omitted a node kind or grouped duplicate object instances into one row.");
            Assert(Node(child).Parent?.Tag == parent, "Child instance does not appear beneath its actual ParentId.");
            Assert(!panel.PlacementOptionsExpanded && panel.InstanceHierarchy.Height >= 100 && !panel.ObjectAssetShelf.Visible,
                "The Instances section did not dedicate its space to the hierarchy.");
            int selections = 0; panel.InstanceSelected += _ => selections++;
            editor.Select(child); panel.RefreshRoomInstances(); Node(parent).Expand();
            int before = selections;
            for (int i = 0; i < 8; i++) panel.RefreshRoomInstances();
            Assert(selections == before && panel.InstanceHierarchy.SelectedNode?.Tag == child && Node(parent).IsExpanded,
                "Refresh changed selection, emitted spurious selection events or collapsed the parent.");
            editor.Select(null); panel.RefreshRoomInstances();
            Assert(panel.InstanceHierarchy.SelectedNode is null, "Deselecting in the editor left a stale instance highlight.");
            panel.SetSearch("Child marker");
            Assert(Items().Count(item => item.Tag is RoomNode) == 2 && Node(child).Parent?.Tag == parent,
                "Search lost the matching instance's ancestry or kept unrelated instances.");
            panel.SetSearch("");
            parent.Transform.X = 12; parent.Transform.Y = 8; parent.Transform.ScaleX = parent.Transform.ScaleY = 2;
            child.Transform.X = 3; child.Transform.Y = 4;
            child.Order = 71; sibling.Order = -19;
            Assert(panel.MoveInstanceToLayer(child.Id, scenery.Id) && child.ParentId.Length == 0
                && child.Transform.X == 18 && child.Transform.Y == 16 && Node(child).Parent?.Tag == scenery,
                "A parented layer drop failed to detach and preserve its world pose.");
            editor.Undo(); panel.RefreshRoomInstances();
            Assert(child.ParentId == parent.Id && child.Transform.X == 3 && child.LayerId == parent.LayerId,
                "Layer drop was not one complete undo operation.");
            TreeNode parentRow = Node(parent); parentRow.EnsureVisible();
            Assert(panel.DropHierarchyAt(sibling.Id, new Point(parentRow.Bounds.Left + 4, parentRow.Bounds.Top + 1))
                && sibling.ParentId.Length == 0 && Node(sibling).Index < Node(parent).Index
                && sibling.Order == -19 && child.Order == 71, "Between-row drop reparented a sibling or changed authored depth.");
            editor.Save();
            RoomAsset savedOrder = RoomAssetLoader.Parse(roomPath);
            Assert(savedOrder.Nodes.FindIndex(node => node.Id == sibling.Id) < savedOrder.Nodes.FindIndex(node => node.Id == parent.Id),
                "Hierarchy order did not survive save/reopen.");
            editor.Undo(); panel.RefreshRoomInstances();
            Assert(Node(sibling).Index > Node(parent).Index, "Hierarchy order undo did not restore the original order.");
            Assert(!panel.ReparentInstance(parent.Id, child.Id), "Hierarchy accepted a parent cycle.");
            Assert(panel.ReparentInstance(sibling.Id, parent.Id) && sibling.ParentId == parent.Id,
                "Drag reparent command did not change the actual room instance.");
            editor.Undo(); panel.RefreshRoomInstances();
            Assert(sibling.ParentId.Length == 0, "Reparent undo lost the original parent.");
            Assert(panel.MoveInstanceToLayer(sibling.Id, scenery.Id) && sibling.LayerId == scenery.Id
                && Node(sibling).Parent?.Tag == scenery, "Dropping an unparented instance on a layer did not move its visible row.");
            editor.Undo(); panel.RefreshRoomInstances();
            ClickRow(Node(sibling), false); Assert(!sibling.Enabled, "Instance eye did not change actual visibility.");
            editor.Undo(); panel.RefreshRoomInstances(); Assert(sibling.Enabled, "Instance visibility was not undoable.");
            ClickRow(Node(sibling), true); Assert(sibling.Locked, "Instance padlock did not persist a local lock.");
            Assert(!panel.ReparentInstance(sibling.Id, parent.Id), "Locked instance accepted a hierarchy edit.");
            ClickRow(Node(sibling), true); Assert(!sibling.Locked, "Instance padlock could not unlock the instance.");
            TreeNode layerNode = Items().Single(item => item.Tag == scenery);
            ClickRow(layerNode, false); Assert(!scenery.Enabled, "Layer eye did not change layer visibility.");
            editor.Undo(); panel.RefreshRoomInstances();
            ClickRow(Items().Single(item => item.Tag == scenery), true);
            Assert(scenery.Locked && !panel.MoveInstanceToLayer(sibling.Id, scenery.Id), "Locked layer accepted a dragged instance.");
            editor.Undo(); panel.RefreshRoomInstances();
            editor.Placement.TargetLayerId = scenery.Id; panel.RefreshLayers(); panel.RefreshLayers();
            Assert(editor.Placement.TargetLayerId == scenery.Id, "Layer refresh reset the placement target.");
            int armed = 0; panel.ObjectArmed += _ => armed++;
            panel.ObjectAssetShelf.SelectedIndex = 0;
            panel.RefreshRoomInstances(); panel.RefreshObjects([new ProjectAssetEntry("Marker object", objectPath, Relative(objectPath), ResourceKind.GameObject)]);
            Assert(armed == 1 && panel.ObjectAssetShelf.SelectedIndex == 0, "Refreshing the asset shelf rearmed placement or lost the selected asset.");
            Editor3DInspectionSuite.Capture(ctx, host, "room-objects-hierarchy-and-assets");
            host.ClientSize = new Size(320, 550); panel.Dock = DockStyle.Left; panel.Width = 280; GateSuite.Pump(3, 20);
            Assert(panel.InstanceHierarchy.Height >= 80 && !panel.ObjectAssetShelf.Visible,
                "Narrow hierarchy layout collapsed its primary content.");
            Editor3DInspectionSuite.Capture(ctx, host, "room-objects-narrow-layout");
            panel.SetInstancesMode(false); panel.SetPlacementOptionsExpanded(true); GateSuite.Pump(3, 20);
            Assert(!panel.InstanceHierarchy.Visible && panel.ObjectAssetShelf.Height >= 100,
                "Expanded placement controls squeezed the object lists away.");
            Editor3DInspectionSuite.Capture(ctx, host, "room-objects-expanded-placement");

            string Relative(string path) => Path.GetRelativePath(project.RootPath, path).Replace('\\', '/');
            IEnumerable<TreeNode> Items() => Descendants(panel.InstanceHierarchy.Nodes);
            TreeNode Node(RoomNode node) => Items().Single(item => item.Tag == node);
            void ClickRow(TreeNode item, bool padlock)
            {
                item.EnsureVisible(); GateSuite.Pump(1, 10);
                int x = panel.InstanceHierarchy.ClientSize.Width - (padlock ? 14 : 42);
                var args = new TreeNodeMouseClickEventArgs(item, MouseButtons.Left, 1, x, item.Bounds.Top + 16);
                typeof(TreeView).GetMethod("OnNodeMouseClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(panel.InstanceHierarchy, [args]);
            }
        });
    }

    private static IEnumerable<TreeNode> Descendants(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        { yield return node; foreach (TreeNode child in Descendants(node.Nodes)) yield return child; }
    }
    private static void Assert(bool value, string message) => HeadlessHarness.Assert(value, message);
}
