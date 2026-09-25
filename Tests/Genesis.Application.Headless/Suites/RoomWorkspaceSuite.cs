using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Climate;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Scene;
using Genesis.World.Terrain;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless.Suites;

internal static class RoomWorkspaceSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.Room.Workspace");
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Workspace.InstanceLockUndoPersistence", () => CheckLocks(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Workspace.AdaptiveNavigationAndAuthoring", () => CheckWorkspace(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Workspace.TerrainActivationAndLiveInstances", () => CheckTerrainWorkspace(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Camera.ThreeDDeadZoneMatchesRuntime", () => CheckDeadZone(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Palette.ReadOnlyModelDoesNotReimport", () => CheckReadOnlyModel(ctx));
    }

    private static void CheckDeadZone(HeadlessContext ctx)
    {
        string directory = Path.Combine(ctx.Workspace, "RoomCameraDeadZone"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "Follow camera.room.json");
        RoomAsset room = RoomAsset.Create("Follow camera", RoomDimension.ThreeD);
        room.Environment.DynamicSky = false;
        room.Nodes.Add(new RoomNode { Name = "Follow marker", Kind = RoomNodeKind.GameObject,
            LayerId = room.Layers[0].Id, GameObject = new(), Transform = new RoomTransform { X = 6, Y = 4, Z = 8 } });
        foreach (RoomViewport camera in room.Viewports) { camera.Enabled = false; camera.EditorVisible = false; }
        RoomViewport slot = room.Viewports[0];
        slot.Enabled = slot.EditorVisible = true; slot.EditorFillStyle = RoomViewportFillStyle.Both;
        slot.SourceX = 2; slot.SourceY = 1; slot.SourceZ = 3;
        slot.SourceWidth = 12; slot.SourceHeight = 8; slot.SourceDepth = 10;
        slot.FollowMarginX = 2; slot.FollowMarginY = 1; slot.FollowMarginZ = 3;
        slot.FollowTarget = "Follow marker"; slot.FrustumFar = 18;
        RoomAssetLoader.Save(room, path);
        using var editor = new RoomEditorControl(path, directory);
        using var host = UnattendedWindowing.NewHost(1440, 900);
        editor.Dock = DockStyle.Fill; host.Controls.Add(editor); ThemeService.Apply(host);
        editor.Viewport.Camera.Target = new Vector3(7, 4, 8); editor.Viewport.Camera.Distance = 28;
        editor.Navigation.SetSection(RoomNavSection.Views); editor.SetViewportOutlinesVisible(true);
        UnattendedWindowing.ShowWithoutFocus(host); Warm(editor);
        Assert(editor.TryGetViewportDeadZoneBounds3D(0, out Vector3 min, out Vector3 max)
            && min == new Vector3(4, 2, 6) && max == new Vector3(12, 8, 10),
            "3D dead-zone overlay does not use the source origin and individual XYZ margins.");
        float[] origins = [2, 1, 3], sizes = [12, 8, 10], margins = [2, 1, 3];
        float[] lower = [min.X, min.Y, min.Z], upper = [max.X, max.Y, max.Z];
        for (int axis = 0; axis < 3; axis++)
        {
            Assert(RoomViewportTracker.Advance(origins[axis], lower[axis], sizes[axis], margins[axis], -1) == origins[axis]
                && RoomViewportTracker.Advance(origins[axis], upper[axis], sizes[axis], margins[axis], -1) == origins[axis]
                && RoomViewportTracker.Advance(origins[axis], upper[axis] + .25f, sizes[axis], margins[axis], -1) == origins[axis] + .25f,
                "Runtime follow movement differs from the visible 3D margin boundary on axis " + axis);
        }
        InspectorSection follow = Section(editor.Navigation, "Follow and dead zone"); follow.Expanded = true;
        Field<NumericUpDown>(follow, "Margin Z").Value = 5;
        Assert(editor.TryGetViewportDeadZoneBounds3D(0, out min, out max) && min.Z == 8 && max.Z == 8,
            "Editing the visible Z margin did not collapse the dead zone to the source centre.");
        editor.Undo(); Warm(editor);
        Assert(editor.TryGetViewportDeadZoneBounds3D(0, out min, out max) && min.Z == 6 && max.Z == 10,
            "Margin undo did not restore the 3D guide.");
        using (Bitmap guideFrame = editor.Viewport.CaptureFrame(3) ?? throw new InvalidOperationException("Camera guide capture failed."))
        {
            Vector3 edge = editor.Viewport.WorldToSurface(new Vector3((min.X + max.X) * .5f, min.Y, min.Z));
            bool goldenGuide = false;
            for (int y = Math.Max(0, (int)edge.Y - 4); y <= Math.Min(guideFrame.Height - 1, (int)edge.Y + 4); y++)
                for (int x = Math.Max(0, (int)edge.X - 4); x <= Math.Min(guideFrame.Width - 1, (int)edge.X + 4); x++)
                {
                    Color pixel = guideFrame.GetPixel(x, y);
                    goldenGuide |= pixel.R > pixel.G + 12 && pixel.G > pixel.B + 22;
                }
            Assert(goldenGuide, "The rendered camera dead zone is offset from its projected world-space boundary.");
        }
        using Bitmap capture = VisualCapture.CaptureWindowPixels(host);
        capture.Save(Path.Combine(ctx.Captures, "room-camera-dead-zone.png"));
        var metrics = Genesis.Application.Runtime.ImageMetrics.Measure(capture);
        ctx.Report.Images.Add(new ImageResult("Room camera", "room-camera-dead-zone.png", metrics.Width, metrics.Height,
            metrics.UniqueSampledColors, metrics.AverageLuminance));
        editor.Save();
        using var reopened = new RoomEditorControl(path, directory);
        Assert(reopened.TryGetViewportDeadZoneBounds3D(0, out min, out max) && min == new Vector3(4, 2, 6)
            && max == new Vector3(12, 8, 10), "Camera margins changed after save/reopen.");
        editor.SetViewportEnabled(false);
        Assert(!editor.TryGetViewportDeadZoneBounds3D(0, out _, out _), "Disabled camera still exposes an active follow guide.");
    }

    private static void CheckReadOnlyModel(HeadlessContext ctx)
    {
        string directory = Path.Combine(ctx.Workspace, "RoomReadOnlyModels"); Directory.CreateDirectory(directory);
        string descriptor = Path.Combine(directory, "Saved model.model.json");
        string source = Path.Combine(directory, "Changed source.obj");
        File.WriteAllText(source, "v 0 0 0\nv 70 0 0\nv 0 70 0\nf 1 2 3\n");
        File.WriteAllText(descriptor, new JObject { ["source"] = Path.GetFileName(source), ["scale"] = 1 }.ToString());
        var baked = ModelPartBuilder.Bake([new ModelPart { Name = "Saved cube", Scale = [2, 3, 4] }]);
        var saved = ModelRigBridge.BuildAsset("Last saved geometry", baked.Vertices, baked.Indices);
        StudioModelResourceLoader.SaveCanonical(descriptor, saved);
        string canonical = StudioModelResourceLoader.CanonicalPath(descriptor);
        DateTime old = DateTime.UtcNow.AddMinutes(-10);
        File.SetLastWriteTimeUtc(descriptor, old); File.SetLastWriteTimeUtc(canonical, old);
        File.SetLastWriteTimeUtc(source, old.AddMinutes(5));
        var snapshots = Directory.EnumerateFiles(directory).ToDictionary(file => file,
            file => (Bytes: File.ReadAllBytes(file), Modified: File.GetLastWriteTimeUtc(file)));
        Assert(File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(canonical), "Fixture source must be newer than saved geometry.");
        for (int index = 0; index < 3; index++)
        {
            GModelAsset read = StudioModelResourceLoader.LoadReadOnly(descriptor);
            Assert(!read.ImportRequired && read.HasRenderableMeshes && read.Name == saved.Name
                && read.Bounds.Min == saved.Bounds.Min && read.Bounds.Max == saved.Bounds.Max,
                "Read-only thumbnail loading replaced saved geometry with the newer external source.");
        }
        Assert(Directory.EnumerateFiles(directory).OrderBy(file => file).SequenceEqual(snapshots.Keys.OrderBy(file => file)),
            "Read-only preview created a model sidecar or imported dependency.");
        foreach (var pair in snapshots)
            Assert(File.GetLastWriteTimeUtc(pair.Key) == pair.Value.Modified
                && File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value.Bytes),
                "Read-only thumbnail loading changed asset bytes or modification time: " + Path.GetFileName(pair.Key));

        string pending = Path.Combine(directory, "Not imported.model.json");
        File.WriteAllText(pending, new JObject { ["source"] = Path.GetFileName(source) }.ToString());
        byte[] pendingBefore = File.ReadAllBytes(pending); DateTime pendingTime = File.GetLastWriteTimeUtc(pending);
        GModelAsset unavailable = StudioModelResourceLoader.LoadReadOnly(pending);
        Assert(unavailable.ImportRequired && !unavailable.HasRenderableMeshes
            && !File.Exists(StudioModelResourceLoader.CanonicalPath(pending)) && !File.Exists(pending + ".mesh")
            && File.GetLastWriteTimeUtc(pending) == pendingTime && File.ReadAllBytes(pending).SequenceEqual(pendingBefore),
            "A preview request imported or rewrote a model that has never been imported.");

        string procedural = Path.Combine(directory, "Saved primitive.model.json");
        File.WriteAllText(procedural, """{"scale":1,"parts":[{"name":"Cube","primitive":"Cube","scale":[2,3,4]}]}""");
        GModelAsset primitive = StudioModelResourceLoader.LoadReadOnly(procedural);
        Assert(!primitive.ImportRequired && primitive.HasRenderableMeshes && !File.Exists(StudioModelResourceLoader.CanonicalPath(procedural)),
            "Read-only loading broke existing procedural descriptors or saved a generated canonical asset.");
    }

    private static void CheckTerrainWorkspace(HeadlessContext ctx)
    {
        var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "RoomTerrainWorkspace"), "Terrain workspace");
        var resources = new ResourceService(project);
        string roomPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Room, "Terrain navigation");
        string terrainPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Terrain, "Terrain asset");
        _ = resources.CreateResource(resources.AssetsRoot, ResourceKind.GameObject, "Object excluded from terrain palette");
        new TerrainAsset(9, 9, .5f, -2, -2, -1, 2).Save(terrainPath + ".gterrain");
        RoomAsset room = RoomAsset.Create("Terrain navigation", RoomDimension.TwoD);
        for (int index = 0; index < 12; index++)
            room.Nodes.Add(new RoomNode { Name = "Terrain patch " + (index + 1), Kind = RoomNodeKind.Terrain,
                LayerId = room.Layers[0].Id, EnabledIn2D = false,
                Terrain = new RoomTerrainData { Asset = Path.GetRelativePath(project.RootPath, terrainPath).Replace('\\', '/') },
                Transform = new RoomTransform { X = index * 5 } });
        RoomAssetLoader.Save(room, roomPath);
        using var editor = new RoomEditorControl(roomPath, project.RootPath);
        using var host = UnattendedWindowing.NewHost(1440, 900);
        editor.Dock = DockStyle.Fill; host.Controls.Add(editor); ThemeService.Apply(host);
        UnattendedWindowing.ShowWithoutFocus(host);
        editor.Navigation.SetSection(RoomNavSection.Tilesets);
        ToolStripButton threeD = editor.EditorToolbar.Strip.Items.OfType<ToolStripButton>().Single(button => button.Text == "3D");
        threeD.PerformClick(); Warm(editor);
        AssertTerrainPalette();
        editor.Undo(); Warm(editor);
        Assert(!editor.ViewMode3D && editor.Navigation.CurrentSection == RoomNavSection.Tilesets
            && editor.Navigation.TilesetsPanel.Visible, "Dimension undo did not restore the active 2D Tilesets panel.");
        editor.Redo(); Warm(editor); AssertTerrainPalette();

        ListBox instances = Descendants(editor.Navigation).OfType<ListBox>().Single(list => list.Name == "RoomTerrainInstances");
        Assert(instances.Items.Count == 12, "Entering 3D while on Tilesets did not populate placed terrain instances.");
        instances.SelectedIndex = 8; instances.TopIndex = 6;
        string selectedId = ((RoomNode)instances.SelectedItem!).Id;
        int top = instances.TopIndex;
        RoomNode renamed = editor.Room.Nodes[0]; string previousName = renamed.Name;
        Assert(editor.SetNodeName(renamed, "Renamed terrain patch"), "Terrain rename did not apply.");
        Assert(instances.GetItemText(instances.Items.Cast<RoomNode>().Single(node => node.Id == renamed.Id)) == "Renamed terrain patch"
            && ((RoomNode)instances.SelectedItem!).Id == selectedId && instances.TopIndex == top,
            "Terrain rename left stale text or reset list selection/scroll.");
        editor.Undo();
        Assert(instances.GetItemText(instances.Items.Cast<RoomNode>().Single(node => node.Id == renamed.Id)) == previousName,
            "Terrain rename undo left stale text in the active list.");
        editor.Redo();
        Assert(instances.GetItemText(instances.Items.Cast<RoomNode>().Single(node => node.Id == renamed.Id)) == "Renamed terrain patch",
            "Terrain rename redo did not refresh the active list.");

        editor.Viewport.Camera.Target = new Vector3(0, 1, 0); editor.Viewport.Camera.Distance = 24;
        Warm(editor);
        editor.BeginPlacementTerrain(terrainPath);
        Point drop = editor.ClientFromWorld3D(new Vector3(0, 0, 5));
        editor.EditorPointerDown(drop, MouseButtons.Left, Keys.None);
        editor.EditorPointerUp(drop, MouseButtons.Left, Keys.None);
        Assert(editor.Room.Nodes.Count == 13 && instances.Items.Count == 13,
            "Placing terrain did not update its visible instance list immediately.");
        RoomNode placed = editor.Room.Nodes[^1];
        Assert(instances.Items.Cast<RoomNode>().Any(node => ReferenceEquals(node, placed)),
            "Terrain list retained detached instances instead of current room nodes.");
        editor.Undo(); Assert(instances.Items.Count == 12 && !instances.Items.Cast<RoomNode>().Any(node => node.Id == placed.Id),
            "Placement undo left a deleted terrain row.");
        editor.Redo(); Assert(instances.Items.Count == 13, "Placement redo did not restore the terrain row.");
        editor.Select(placed); editor.DeleteSelected();
        Assert(instances.Items.Count == 12 && !instances.Items.Cast<RoomNode>().Any(node => node.Id == placed.Id),
            "Delete left the removed terrain selectable in the active list.");
        editor.Undo(); Assert(instances.Items.Count == 13 && instances.Items.Cast<RoomNode>().All(editor.Room.Nodes.Contains),
            "Delete undo restored stale terrain references.");
        editor.Save();
        RoomAsset saved = RoomAssetLoader.Parse(roomPath);
        Assert(saved.Nodes.Count == 13 && saved.Nodes.Any(node => node.Name == "Renamed terrain patch"),
            "Terrain list edits did not persist.");

        void AssertTerrainPalette()
        {
            Assert(editor.ViewMode3D && editor.Navigation.CurrentSection == RoomNavSection.Tilesets
                && !editor.Navigation.TilesetsPanel.Visible, "3D dimension change did not activate Terrain in the existing navigation slot.");
            ListView palette = Descendants(editor.Navigation).OfType<ListView>().Single(list => list.Visible);
            ProjectAssetEntry[] entries = palette.Items.Cast<ListViewItem>().Select(item => item.Tag).OfType<ProjectAssetEntry>().ToArray();
            Assert(entries.Length == 1 && entries[0].Kind == ResourceKind.Terrain && entries[0].FullPath == terrainPath,
                "Switching the active Tilesets tab to 3D displayed an object/tileset palette under Terrain.");
        }
    }

    private static void CheckLocks(HeadlessContext ctx)
    {
        string root = Path.Combine(ctx.Workspace, "RoomInstanceLock"); Directory.CreateDirectory(root);
        string file = Path.Combine(root, "Locks.room.json");
        RoomAsset room = RoomAsset.Create("Locks", RoomDimension.ThreeD);
        RoomNode node = new() { Name = "Protected prop", Kind = RoomNodeKind.GameObject, GameObject = new(), LayerId = room.Layers[0].Id };
        room.Nodes.Add(node); RoomAssetLoader.Save(room, file);
        using var editor = new RoomEditorControl(file, root);
        node = editor.Room.Nodes[0];
        editor.Select(node);
        Assert(editor.SetNodeLocked(node, true) && node.Locked && editor.IsNodeLocked(node), "Instance lock did not become active.");
        Assert(!editor.MoveSelectionBy(Vector3.UnitX) && node.Transform.X == 0, "Locked node moved through the domain edit path.");
        editor.DeleteSelected(); Assert(editor.Room.Nodes.Contains(node), "Locked instance was deleted.");
        editor.Undo(); Assert(!node.Locked && !editor.IsNodeLocked(node), "Undo did not clear the local lock.");
        editor.Redo(); Assert(node.Locked, "Redo did not restore local lock.");
        editor.Save();
        Assert(RoomAssetLoader.Parse(file).Nodes[0].Locked, "Room save discarded local lock.");
        editor.Room.Layers[0].Locked = true;
        Assert(editor.SetNodeLocked(node, false) && !node.Locked && editor.IsNodeLocked(node), "Local unlock accidentally cleared inherited layer protection.");
        editor.Room.Layers[0].Locked = false;
        Assert(!editor.IsNodeLocked(node), "Cleared node/layer locks retained stale protection.");
        editor.SetNodeLocked(node, true);
        RoomAsset clone = editor.Room.DeepClone();
        Assert(clone.Nodes[0].Locked, "Snapshot cloning lost local lock.");
    }

    private static void CheckWorkspace(HeadlessContext ctx)
    {
        var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "RoomWorkspace"), "Room workspace");
        var resources = new ResourceService(project);
        string roomPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Room, "Hillside camp");
        string modelPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Model, "Camp marker");
        var baked = ModelPartBuilder.Bake([
            new ModelPart { Name = "Plinth", Position = [0, .25f, 0], Scale = [1.4f, .5f, 1.4f], Color = [.25f, .35f, .45f] },
            new ModelPart { Name = "Marker", Position = [0, 1, 0], Scale = [.7f, 1, .7f], Color = [.9f, .45f, .13f] },
        ]);
        StudioModelResourceLoader.SaveCanonical(modelPath, ModelRigBridge.BuildAsset("Camp marker", baked.Vertices, baked.Indices));
        string objectPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.GameObject, "Camp marker");
        File.WriteAllText(objectPath, new JObject { ["name"] = "Camp marker", ["dimension"] = "ThreeD", ["model"] = Relative(modelPath) }.ToString());
        string terrainPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Terrain, "Hillside");
        var terrain = new TerrainAsset(33, 33, .5f, -8, -8, -3, 5);
        for (int z = 0; z < 33; z++)
            for (int x = 0; x < 33; x++)
            {
                float height = .5f + MathF.Sin(x * .14f) * .8f + MathF.Cos(z * .12f) * .5f;
                terrain.SetHeight(x, z, height);
                if (x is > 13 and < 18) terrain.SetSplat(x, z, 20, 215, 20, 0);
            }
        terrain.Save(terrainPath + ".gterrain");
        RoomAsset room = RoomAsset.Create("Hillside camp", RoomDimension.TwoD);
        room.Settings.MetricGridSize = .5f;
        room.Nodes.Add(new RoomNode { Name = "Hillside", Kind = RoomNodeKind.Terrain, LayerId = room.Layers[0].Id,
            EnabledIn2D = false, Terrain = new RoomTerrainData { Asset = Relative(terrainPath) } });
        room.Nodes.Add(new RoomNode { Name = "Camp marker", Kind = RoomNodeKind.GameObject, LayerId = room.Layers[0].Id,
            GameObject = new RoomGameObjectData { Prefab = Relative(objectPath) }, Transform = new RoomTransform { Y = 1 } });
        RoomAssetLoader.Save(room, roomPath);
        using var editor = new RoomEditorControl(roomPath, project.RootPath);
        using var host = UnattendedWindowing.NewHost(1440, 900);
        editor.Dock = DockStyle.Fill; host.Controls.Add(editor); ThemeService.Apply(host);
        UnattendedWindowing.ShowWithoutFocus(host); Warm(editor);
        ValidateShell(editor);
        Assert(editor.Inspector.Width >= 300, "The default wide layout shrank the Inspector below its readable width.");
        Assert(editor.Navigation.SectionTitles.SequenceEqual(new[] { "Objects", "Instances", "Settings", "Views", "Backgrounds", "Tilesets" }), "2D rail is not the required six-section navigation.");
        Capture("room-workspace-2d");
        ToolStrip strip = editor.EditorToolbar.Strip;
        ToolStripButton dimension = strip.Items.OfType<ToolStripButton>().Single(button => button.Text == "3D");
        dimension.PerformClick(); Warm(editor);
        Assert(editor.ViewMode3D && editor.Navigation.SectionTitles.SequenceEqual(new[] { "Objects", "Instances", "Settings", "Views", "Skybox", "Terrain" }), "Dimension button did not adapt all six navigation entries.");
        editor.Undo(); Warm(editor);
        Assert(!editor.ViewMode3D && editor.Navigation.SectionTitles.Contains("Backgrounds") && !editor.Navigation.SectionTitles.Contains("Skybox"), "Dimension undo left the rail in 3D.");
        editor.Redo(); Warm(editor);
        Assert(editor.ViewMode3D && editor.Navigation.SectionTitles.Contains("Terrain"), "Dimension redo failed.");
        editor.EditorToolbar.SnapSizePicker.SelectedItem = "0.1 m";
        Assert(MathF.Abs(editor.MetricGridSize - .1f) < .0001f, "Metric dropdown did not route its value to 3D grid settings.");
        editor.EditorToolbar.SnapSizePicker.SelectedItem = "0.5 m";

        editor.Navigation.SetSection(RoomNavSection.Backgrounds); Warm(editor);
        InspectorSection sky = Section(editor.Navigation, "Sky and time");
        Assert(sky.Visible && sky.Expanded && !editor.Navigation.BackgroundsPanel.Visible, "Skybox selection left the old parallax panel visible.");
        Field<NumericUpDown>(sky, "Clock (hours)").Value = 8.5m;
        Assert(MathF.Abs(editor.Room.Environment.TimeOfDayHours - 8.5f) < .001f, "Sky clock did not edit the canonical room environment.");
        var clock = Descendants(editor).OfType<TrackBar>().Single(control => control.Name == "RoomEnvironmentClock");
        Assert(clock.Visible && clock.Value == 85, "Bottom environment readout did not follow the sky clock.");
        editor.SetEnvironmentFields(true, WeatherKind.Clear, 8.5f, 0, false);
        editor.Viewport.Camera.Target = new Vector3(0, 1, 0); editor.Viewport.Camera.Distance = 17;
        editor.Viewport.Camera.Yaw = .65f; editor.Viewport.Camera.Pitch = -.5f;
        Warm(editor); Capture("room-workspace-skybox");

        editor.Navigation.SetSection(RoomNavSection.Views); Warm(editor);
        InspectorSection source = Section(editor.Navigation, "Source region");
        Assert(source.Visible && Section(editor.Navigation, "Projection").Visible, "3D camera authoring is inaccessible.");
        editor.SelectViewport(0); editor.SetViewportEnabled(true);
        Field<NumericUpDown>(source, "Z").Value = 4;
        Assert(editor.Room.Viewports[0].SourceZ == 4, "Visible 3D camera Z did not reach room data.");
        InspectorSection projection = Section(editor.Navigation, "Projection"); projection.Expanded = true;
        Field<NumericUpDown>(projection, "Field of view").Value = 68;
        Assert(editor.Room.Viewports[0].FieldOfView == 68, "Visible FOV did not reach the camera viewport.");
        Warm(editor); Capture("room-workspace-cameras");
        editor.ViewMode3D = false; editor.SetViewportEnabled(false); editor.SetViewportEnabled(true);
        Assert(!Field<NumericUpDown>(source, "Z").Enabled || !Field<NumericUpDown>(source, "Z").Visible,
            "Enabling a 2D camera exposed editable 3D depth.");
        editor.ViewMode3D = true; editor.SetViewportEnabled(false);

        editor.Navigation.SetSection(RoomNavSection.Tilesets); Warm(editor);
        Assert(!editor.Navigation.TilesetsPanel.Visible && Descendants(editor.Navigation).Any(control => control.Name == "RoomTerrainInstances" && control.Visible),
            "Terrain navigation did not display the terrain asset/instance panel.");
        Capture("room-workspace-terrain");
        editor.Navigation.SetSection(RoomNavSection.Objects);
        RoomNode marker = editor.Room.Nodes.Single(node => node.Name == "Camp marker");
        editor.Select(marker); editor.SnapSelectionToTerrain(); Warm(editor); Capture("room-workspace-3d");
        editor.Navigation.SetSection(RoomNavSection.Settings);
        Assert(editor.SelectedNode == marker && editor.Inspector.Visible
            && Descendants(editor.Navigation).Any(control => control.Name == "RoomSettingsFields" && control.Visible),
            "Settings should edit the room in its own panel while preserving the selected instance.");

        SplitContainer outer = Descendants(editor).OfType<SplitContainer>().Single(split => split.Panel1.Controls.Contains(editor.Navigation));
        int initial = outer.SplitterDistance;
        Assert(!outer.IsSplitterFixed, "Left navigation cannot be resized.");
        outer.OnSplitterMoving(new SplitterCancelEventArgs(initial, 0, initial + 32, 0));
        outer.SplitterDistance = initial + 32;
        host.ClientSize = new Size(1460, 900); Warm(editor);
        Assert(Math.Abs(outer.SplitterDistance - (initial + 32)) <= 4, "Window resize discarded the user's panel width.");
        foreach (Size size in new[] { new Size(1440, 900), new Size(1024, 740), new Size(800, 650), new Size(1440, 540) })
        {
            host.ClientSize = size; editor.Navigation.SetSection(RoomNavSection.Objects); Warm(editor);
            ValidateShell(editor);
            Assert(editor.Viewport.Width >= 310 && editor.Viewport.Height >= 300, "Panels crowded out the usable room viewport at " + size);
            if (!editor.Inspector.Visible)
            {
                int viewportRight = editor.PointToClient(editor.Viewport.PointToScreen(Point.Empty)).X + editor.Viewport.Width;
                Assert(Math.Abs(viewportRight - editor.ClientSize.Width) <= 4,
                    "Collapsing the Inspector left unused space beside the viewport: " + viewportRight + " / " + editor.ClientSize.Width
                    + "; parents " + string.Join(" / ", Parents(editor.Viewport).Select(control => control.GetType().Name + " " + control.Bounds)));
            }
            Capture("room-workspace-" + size.Width + "x" + size.Height);
        }
        editor.Save();
        RoomAsset saved = RoomAssetLoader.Parse(roomPath);
        Assert(saved.Dimension == RoomDimension.ThreeD && MathF.Abs(saved.Environment.TimeOfDayHours - 8.5f) < .001f
            && saved.Viewports[0].FieldOfView == 68, "Workspace edits did not survive serialization.");

        string Relative(string path) => Path.GetRelativePath(project.RootPath, path).Replace('\\', '/');
        void Capture(string name)
        {
            using Bitmap bitmap = VisualCapture.CaptureWindowPixels(host);
            bitmap.Save(Path.Combine(ctx.Captures, name + ".png"));
            var metrics = Genesis.Application.Runtime.ImageMetrics.Measure(bitmap);
            ctx.Report.Images.Add(new ImageResult("Room workspace", name + ".png", metrics.Width, metrics.Height,
                metrics.UniqueSampledColors, metrics.AverageLuminance));
        }
    }

    private static void ValidateShell(RoomEditorControl editor)
    {
        ToolStrip[] strips = Descendants(editor).OfType<ToolStrip>().Where(strip => strip.Visible && strip.Height > 0).ToArray();
        Assert(strips.Length == 1 && ReferenceEquals(strips[0], editor.EditorToolbar.Strip), "Room workspace still has duplicate command bars.");
        Assert(!Descendants(editor).Any(control => control.Visible && control is RichTextBox), "Room workspace contains a code editor.");
        Assert(!Descendants(editor).Any(control => control.Visible && (control.Text == "Code" || control.Text == "Creation Code")), "Room navigation contains a code workflow.");
        ToolStrip strip = strips[0];
        ToolStripSplitButton play = strip.Items.OfType<ToolStripSplitButton>().Single(button => button.Text?.Contains("Play", StringComparison.Ordinal) == true);
        ToolStripButton stop = strip.Items.OfType<ToolStripButton>().Single(button => button.Text?.Contains("Stop", StringComparison.Ordinal) == true);
        Assert(play.Available && play.Enabled && !stop.Enabled, "Room simulation did not open stopped and ready to Play.");
        Assert(!strip.Items.OfType<ToolStripButton>().Any(button => button.Text?.Contains("Pause", StringComparison.Ordinal) == true && button.Available),
            "Room opened with Pause instead of Play.");
        Assert(editor.Navigation.SectionTitles.Count == 6, "Room rail added extra navigation modes.");
        Button[] nav = Descendants(editor.Navigation).OfType<Button>()
            .Where(button => button.Visible && button.Text.Contains('\n') && editor.Navigation.SectionTitles.Contains(button.Text.Split('\n')[^1])).ToArray();
        Assert(nav.Length == 6, "One of the six navigation buttons is not accessible.");
        Assert(nav.OrderBy(button => button.Top).Select(button => button.Text.Split('\n')[^1]).SequenceEqual(editor.Navigation.SectionTitles),
            "Visible navigation order differs from Objects / Instances / Settings / Views / Skybox / Terrain.");
        Assert(nav.All(button => button.Parent!.ClientRectangle.Contains(button.Bounds)), "Navigation labels extend outside their rail.");
    }

    private static T Field<T>(InspectorSection section, string label) where T : Control
    {
        TableLayoutPanel table = Descendants(section.Body).OfType<TableLayoutPanel>().First();
        Label caption = table.Controls.OfType<Label>().Single(control => control.Text == label);
        return (T)table.GetControlFromPosition(1, table.GetCellPosition(caption).Row)!;
    }
    private static InspectorSection Section(Control root, string title) => Descendants(root).OfType<InspectorSection>().Single(section => section.Title == title);
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }
    private static IEnumerable<Control> Parents(Control control)
    {
        while (control.Parent is { } parent) { yield return parent; control = parent; }
    }
    private static void Warm(RoomEditorControl editor) { GateSuite.Pump(3, 25); using var frame = editor.Viewport.CaptureFrame(3); }
    private static void Assert(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
