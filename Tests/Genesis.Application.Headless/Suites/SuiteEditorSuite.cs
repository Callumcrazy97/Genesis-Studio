using System.Drawing;
using System.Drawing.Imaging;
using System.ComponentModel;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Editors.Suite.Scripts;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Application.Core.Diagnostics;
using Genesis.Application.Studio;
using Genesis.Application.Studio.Docking;
using Genesis.Application.Studio.Forms;
using Genesis.Application.Studio.Theme;
using Genesis.Rendering.Meshes;
using Genesis.Runtime.ECS.Components;
using Genesis.Shared.ECS;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Audio;
using Genesis.Shared.Interfaces;
using Genesis.World.Terrain;
using Newtonsoft.Json.Linq;
using WinFormsApplication = System.Windows.Forms.Application;
using RuntimeImageMetrics = Genesis.Application.Runtime.ImageMetrics;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Headless coverage for the specialised editor suite: Room (2D/3D, placement, selection,
/// gizmos, save→runtime spawn), Terrain (sculpt/paint/undo + lit capture), PGSL diagnostics,
/// Object events, every remaining editor's open+capture, live theme, and F5 resolution.
/// </summary>
internal static class SuiteEditorSuite
{
    public static void Run(HeadlessContext ctx)
    {
        Editor3DInspectionSuite.Run(ctx);
        ModelBranchSidebarSuite.Run(ctx);
        ModelRigEditingSuite.Run(ctx);
        ModelRigWizardSuite.Run(ctx);
        ModelMotionImportSuite.Run(ctx);
        ModelArcherAnimationSuite.Run(ctx);
        ModelPoseWorkflowSuite.Run(ctx);
        ModelIntakeSuite.Run(ctx);
        ModelViewerSuite.Run(ctx);
        ModelEditorResetSuite.Run(ctx);
        ModelRefinementSuite.Run(ctx);
        ModelRigPlacementSuite.Run(ctx);
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.Suite");
        ProjectSession project = HeadlessHarness.Require(ctx.Project, "Project fixture");
        ResourceService resources = HeadlessHarness.Require(ctx.Resources, "Resource service");
        string captures = ctx.Captures;

        string objectsFolder = Path.Combine(resources.AssetsRoot, "Objects");
        string roomsFolder = Path.Combine(resources.AssetsRoot, "Rooms");
        Directory.CreateDirectory(objectsFolder);
        Directory.CreateDirectory(roomsFolder);

        string heroObject = string.Empty;
        string arenaRoom = string.Empty;

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.Room.PlaceSelectMoveUndo2D", () =>
        {
            heroObject = resources.CreateResource(objectsFolder, ResourceKind.GameObject, "SuiteHero");
            arenaRoom = resources.CreateResource(roomsFolder, ResourceKind.Room, "SuiteArena");

            using Form host = NewHost();
            RoomEditorControl editor = new(arenaRoom, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            Pump(10, 30);

            HeadlessHarness.Assert(!editor.ViewMode3D, "New room should start in 2D mode.");
            HeadlessHarness.Assert(editor.Room.Nodes.Count == 0, "New room should start empty.");

            // Palette → place two with Ctrl held, third without (returns to Select).
            editor.BeginPlacement(heroObject);
            HeadlessHarness.Assert(editor.ActiveTool == RoomEditorControl.RoomTool.Place, "Placement should arm the Place tool.");
            editor.EditorPointerDown(editor.ClientFromWorld2D(new Vector2(320f, 256f)), MouseButtons.Left, Keys.Control);
            editor.EditorPointerUp(editor.ClientFromWorld2D(new Vector2(320f, 256f)), MouseButtons.Left, Keys.Control);
            editor.EditorPointerDown(editor.ClientFromWorld2D(new Vector2(576f, 256f)), MouseButtons.Left, Keys.Control);
            editor.EditorPointerUp(editor.ClientFromWorld2D(new Vector2(576f, 256f)), MouseButtons.Left, Keys.Control);
            HeadlessHarness.Assert(
                editor.ActiveTool == RoomEditorControl.RoomTool.Place,
                "Ctrl-held placement must stay in Place mode for multi-place.");
            editor.EditorPointerDown(editor.ClientFromWorld2D(new Vector2(448f, 384f)), MouseButtons.Left, Keys.None);
            editor.EditorPointerUp(editor.ClientFromWorld2D(new Vector2(448f, 384f)), MouseButtons.Left, Keys.None);
            HeadlessHarness.Assert(editor.Room.Nodes.Count == 3, $"Expected 3 placed instances, got {editor.Room.Nodes.Count}.");
            HeadlessHarness.Assert(
                editor.ActiveTool == RoomEditorControl.RoomTool.Select,
                "Placing without Ctrl must return to the Select tool.");

            // Click-select shows a selection with a bounding box.
            editor.EditorPointerDown(editor.ClientFromWorld2D(new Vector2(320f, 256f)), MouseButtons.Left, Keys.None);
            editor.EditorPointerUp(editor.ClientFromWorld2D(new Vector2(320f, 256f)), MouseButtons.Left, Keys.None);
            HeadlessHarness.Assert(editor.SelectedNode is not null, "Click selection failed to hit the placed instance.");
            RoomNode selected = editor.SelectedNode!;
            HeadlessHarness.Assert(MathF.Abs(selected.Transform.X - 320f) < 0.6f, "Selected the wrong instance.");

            // Body drag with grid snap (32) moves the instance.
            Point from = editor.ClientFromWorld2D(new Vector2(320f, 256f));
            Point to = editor.ClientFromWorld2D(new Vector2(416f, 320f));
            editor.EditorPointerDown(from, MouseButtons.Left, Keys.None);
            editor.EditorPointerMove(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2), MouseButtons.Left, Keys.None);
            editor.EditorPointerMove(to, MouseButtons.Left, Keys.None);
            editor.EditorPointerUp(to, MouseButtons.Left, Keys.None);
            HeadlessHarness.Assert(
                MathF.Abs(selected.Transform.X - 416f) < 0.6f && MathF.Abs(selected.Transform.Y - 320f) < 0.6f,
                $"Gizmo move landed at ({selected.Transform.X},{selected.Transform.Y}) instead of (416,320).");

            HeadlessHarness.Assert(editor.CanUndo, "Move must be journaled for undo (NEXT-007).");
            editor.Undo();
            HeadlessHarness.Assert(
                MathF.Abs(selected.Transform.X - 320f) < 0.6f,
                $"Undo did not restore X (got {selected.Transform.X}).");
            editor.Redo();
            HeadlessHarness.Assert(
                MathF.Abs(selected.Transform.X - 416f) < 0.6f,
                $"Redo did not re-apply X (got {selected.Transform.X}).");

            // Scale via the SE corner handle.
            editor.SetGizmo(RoomEditorControl.GizmoKind.Scale);
            Vector2 corner = new(selected.Transform.X + 16f, selected.Transform.Y + 16f);
            Point cornerClient = editor.ClientFromWorld2D(corner);
            editor.EditorPointerDown(cornerClient, MouseButtons.Left, Keys.None);
            Point stretched = editor.ClientFromWorld2D(new Vector2(selected.Transform.X + 32f, selected.Transform.Y + 32f));
            editor.EditorPointerMove(stretched, MouseButtons.Left, Keys.None);
            editor.EditorPointerUp(stretched, MouseButtons.Left, Keys.None);
            HeadlessHarness.Assert(
                selected.Transform.ScaleX > 1.5f,
                $"Corner drag should scale up (scaleX={selected.Transform.ScaleX:0.00}).");

            // Rotate handle.
            editor.SetGizmo(RoomEditorControl.GizmoKind.Rotate);
            Point rotStart = editor.ClientFromWorld2D(new Vector2(selected.Transform.X + 40f, selected.Transform.Y));
            Point rotEnd = editor.ClientFromWorld2D(new Vector2(selected.Transform.X, selected.Transform.Y + 40f));
            editor.EditorPointerDown(rotStart, MouseButtons.Left, Keys.None);
            editor.EditorPointerMove(rotEnd, MouseButtons.Left, Keys.None);
            editor.EditorPointerUp(rotEnd, MouseButtons.Left, Keys.None);
            HeadlessHarness.Assert(
                MathF.Abs(NormalizeDegrees(selected.Transform.RotationZ)) > 30f,
                $"Rotate drag should change rotation (rotZ={selected.Transform.RotationZ:0.#}).");

            editor.Save();
            HeadlessHarness.Assert(!editor.IsDirty, "Room stayed dirty after save.");

            Bitmap? frame = editor.Viewport.CaptureFrame(settleFrames: 3);
            HeadlessHarness.Assert(frame is not null, "Room 2D viewport readback failed.");
            SaveCapture(ctx, frame!, "20-room-editor-2d.png", "Room Editor 2D", minColors: 5);
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.Room.Toggle3DPlaceAxisGizmo", () =>
        {
            // Independent room fixture: arenaRoom's 2D-placed nodes carry pixel-scale
            // coordinates (hundreds of units) that would dwarf a 3D scene at meter scale.
            string arena3DRoom = resources.CreateResource(roomsFolder, ResourceKind.Room, "SuiteArena3D");

            using Form host = NewHost();
            RoomEditorControl editor = new(arena3DRoom, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            Pump(10, 30);

            editor.ViewMode3D = true;
            HeadlessHarness.Assert(editor.Room.Dimension == RoomDimension.ThreeD, "3D toggle must set the room dimension.");
            Pump(4, 25);

            int before = editor.Room.Nodes.Count;
            editor.BeginPlacement(heroObject);
            Point center = new(editor.Viewport.Width / 2, (int)(editor.Viewport.Height * 0.62f));
            Point viewportCenter = TranslateToViewport(editor, center);
            editor.EditorPointerDown(viewportCenter, MouseButtons.Left, Keys.None);
            editor.EditorPointerUp(viewportCenter, MouseButtons.Left, Keys.None);
            HeadlessHarness.Assert(editor.Room.Nodes.Count == before + 1, "3D ground placement did not add an instance.");
            RoomNode node = editor.Room.Nodes[^1];

            // Select it by clicking its projected position.
            Vector3 position = new(node.Transform.X, node.Transform.Y + 0.4f, node.Transform.Z);
            Point client = editor.ClientFromWorld3D(position);
            editor.EditorPointerDown(client, MouseButtons.Left, Keys.None);
            editor.EditorPointerUp(client, MouseButtons.Left, Keys.None);
            HeadlessHarness.Assert(ReferenceEquals(editor.SelectedNode, node), "3D ray pick selected the wrong node.");

            // Drag the X axis handle: X must change, Z stay (within snap tolerance).
            editor.SetGizmo(RoomEditorControl.GizmoKind.Move);
            float beforeX = node.Transform.X;
            float beforeZ = node.Transform.Z;
            Point axisTip = editor.ClientFromWorld3D(new Vector3(
                node.Transform.X + AxisLength(editor), node.Transform.Y, node.Transform.Z));
            Point axisMid = editor.ClientFromWorld3D(new Vector3(
                node.Transform.X + AxisLength(editor) * 0.7f, node.Transform.Y, node.Transform.Z));
            editor.EditorPointerDown(axisMid, MouseButtons.Left, Keys.None);
            Point dragTarget = new(axisTip.X + (axisTip.X - axisMid.X), axisTip.Y + (axisTip.Y - axisMid.Y));
            editor.EditorPointerMove(dragTarget, MouseButtons.Left, Keys.Shift);
            editor.EditorPointerUp(dragTarget, MouseButtons.Left, Keys.Shift);
            HeadlessHarness.Assert(
                MathF.Abs(node.Transform.X - beforeX) > 0.2f,
                $"X-axis gizmo drag did not move the node (dx={node.Transform.X - beforeX:0.###}).");
            HeadlessHarness.Assert(
                MathF.Abs(node.Transform.Z - beforeZ) < 0.75f,
                $"X-axis drag must stay on the X axis (dz={node.Transform.Z - beforeZ:0.###}).");

            editor.Save();
            Bitmap? frame = editor.Viewport.CaptureFrame(settleFrames: 4);
            HeadlessHarness.Assert(frame is not null, "Room 3D viewport readback failed.");
            SaveCapture(ctx, frame!, "21-room-editor-3d.png", "Room Editor 3D", minColors: 24);
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.Room.SaveSpawnsInRuntime", () =>
        {
            RoomAsset parsed = RoomAssetLoader.Parse(arenaRoom);
            int gameObjectNodes = parsed.Nodes.Count(n => n.Kind == RoomNodeKind.GameObject);
            HeadlessHarness.Assert(gameObjectNodes >= 3, $"Saved room lost instances (found {gameObjectNodes}).");

            RoomSceneBuilder builder = new(project.RootPath);
            EcsWorld world = new();
            RoomBuildResult build = builder.Build(world, parsed);
            HeadlessHarness.Assert(
                build.SpawnedEntities.Count == gameObjectNodes,
                $"Runtime spawned {build.SpawnedEntities.Count}/{gameObjectNodes} room instances — prefab resolution broke.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.Room.PlaceTerrain3DOnly", () =>
        {
            string terrainFolder = Path.Combine(resources.AssetsRoot, "Terrain");
            Directory.CreateDirectory(terrainFolder);
            string terrainAsset = resources.CreateResource(terrainFolder, ResourceKind.Terrain, "SuiteRoomTerrain");

            // A freshly-created terrain only has heightmap data in memory — Save() is what
            // writes the .gterrain binary sidecar RoomTerrainSubsystem.ResolveTerrainFile (and
            // this Room Editor's own preview loader) actually reads.
            using (Form terrainHost = NewHost())
            {
                TerrainEditorControl terrainEditor = new(terrainAsset, project.RootPath);
                terrainHost.Controls.Add(terrainEditor);
                GateSuite.ShowHost(terrainHost);
                Pump(6, 25);
                terrainEditor.Save();
                HeadlessHarness.Assert(File.Exists(terrainAsset + ".gterrain"), "Terrain fixture did not save its binary sidecar.");
            }

            string terrainRoom = resources.CreateResource(roomsFolder, ResourceKind.Room, "SuiteTerrainRoom");
            using Form host = NewHost();
            RoomEditorControl editor = new(terrainRoom, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            Pump(10, 30);

            // Terrain has no 2D projection — placement must be refused while in 2D.
            editor.BeginPlacementTerrain(terrainAsset);
            HeadlessHarness.Assert(
                editor.ActiveTool != RoomEditorControl.RoomTool.Place,
                "Terrain placement must be refused while the room is in 2D mode.");

            editor.ViewMode3D = true;
            Pump(4, 25);

            int before = editor.Room.Nodes.Count;
            editor.BeginPlacementTerrain(terrainAsset);
            HeadlessHarness.Assert(editor.ActiveTool == RoomEditorControl.RoomTool.Place, "3D Terrain placement should arm the Place tool.");

            Point center = new(editor.Viewport.Width / 2, (int)(editor.Viewport.Height * 0.62f));
            Point viewportCenter = TranslateToViewport(editor, center);
            editor.EditorPointerDown(viewportCenter, MouseButtons.Left, Keys.None);
            editor.EditorPointerUp(viewportCenter, MouseButtons.Left, Keys.None);
            HeadlessHarness.Assert(editor.Room.Nodes.Count == before + 1, "3D ground click did not place a Terrain node.");

            RoomNode terrainNode = editor.Room.Nodes[^1];
            HeadlessHarness.Assert(terrainNode.Kind == RoomNodeKind.Terrain, "Placed node has the wrong kind.");
            HeadlessHarness.Assert(!string.IsNullOrWhiteSpace(terrainNode.Terrain?.Asset), "Placed Terrain node lost its asset reference.");
            HeadlessHarness.Assert(!terrainNode.EnabledIn2D, "A placed Terrain node should be excluded from 2D (no projection to show it in).");

            // Ray-pick selects it (same 3D hit-test as GameObjects), and the Move gizmo drags
            // it along an axis using the exact generic transform-drag code GameObjects use —
            // Terrain nodes get gizmos for free since RoomTerrainSubsystem places them the same
            // Scale*Rotate*Translate way at runtime.
            editor.EditorPointerDown(viewportCenter, MouseButtons.Left, Keys.None);
            editor.EditorPointerUp(viewportCenter, MouseButtons.Left, Keys.None);
            HeadlessHarness.Assert(ReferenceEquals(editor.SelectedNode, terrainNode), "3D ray pick did not select the placed Terrain node.");

            editor.SetGizmo(RoomEditorControl.GizmoKind.Move);
            float beforeX = terrainNode.Transform.X;
            float beforeZ = terrainNode.Transform.Z;
            Point axisTip = editor.ClientFromWorld3D(new Vector3(
                terrainNode.Transform.X + AxisLength(editor), terrainNode.Transform.Y, terrainNode.Transform.Z));
            Point axisMid = editor.ClientFromWorld3D(new Vector3(
                terrainNode.Transform.X + AxisLength(editor) * 0.7f, terrainNode.Transform.Y, terrainNode.Transform.Z));
            editor.EditorPointerDown(axisMid, MouseButtons.Left, Keys.None);
            Point dragTarget = new(axisTip.X + (axisTip.X - axisMid.X), axisTip.Y + (axisTip.Y - axisMid.Y));
            editor.EditorPointerMove(dragTarget, MouseButtons.Left, Keys.Shift);
            editor.EditorPointerUp(dragTarget, MouseButtons.Left, Keys.Shift);
            HeadlessHarness.Assert(
                MathF.Abs(terrainNode.Transform.X - beforeX) > 0.2f,
                $"X-axis gizmo drag did not move the placed Terrain node (dx={terrainNode.Transform.X - beforeX:0.###}).");
            HeadlessHarness.Assert(
                MathF.Abs(terrainNode.Transform.Z - beforeZ) < 0.75f,
                $"X-axis drag must stay on the X axis (dz={terrainNode.Transform.Z - beforeZ:0.###}).");

            // Switching to 2D must deselect a Terrain-kind selection (no 2D gizmo/outline for it).
            editor.ViewMode3D = false;
            HeadlessHarness.Assert(editor.SelectedNode is null, "Switching to 2D should deselect a Terrain-kind selection.");
            editor.ViewMode3D = true;
            Pump(2, 20);

            editor.Save();
            Bitmap? frame = editor.Viewport.CaptureFrame(settleFrames: 5);
            HeadlessHarness.Assert(frame is not null, "Room+Terrain 3D viewport readback failed.");
            SaveCapture(ctx, frame!, "36-room-editor-terrain.png", "Room Editor Terrain Placement", minColors: 40);
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.Room.RunRequiresRoom", () =>
        {
            // A separate project so this doesn't disturb the shared fixture. CreateProject
            // always seeds one starter Room ("Start") — trash it so the project is genuinely
            // roomless, which is the only way to exercise this guard.
            StudioServices studioServices = HeadlessHarness.Require(ctx.StudioServices, "Studio services");
            ProjectService emptyProjectService = new();
            ProjectSession emptyProject = emptyProjectService.CreateProject(
                Path.Combine(ctx.Workspace, "EmptyRoomGuard"), "Empty Room Guard Project");
            ResourceService emptyProjectResources = new(emptyProject);
            string starterRoomPath = Path.Combine(emptyProject.AssetsPath, "Rooms", "Start.room.json");
            HeadlessHarness.Assert(File.Exists(starterRoomPath), "CreateProject's seeded starter Room was not found where expected.");
            emptyProjectResources.MoveToTrash(starterRoomPath);

            using StudioShellForm studio = new(studioServices, emptyProject, persistLayout: false);
            int entriesBefore = studioServices.Log.Entries.Count;
            studio.RunProject(false);
            HeadlessHarness.Assert(
                studioServices.Log.Entries.Skip(entriesBefore).Any(entry =>
                    entry.Level == StudioLogLevel.Error && entry.Message.Contains("no Room", StringComparison.OrdinalIgnoreCase)),
                "Running a project with zero Rooms must log a clear 'no Room' error instead of falling through to launch.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.Terrain.SculptPaintUndoCapture", () =>
        {
            string terrain = resources.CreateResource(
                Path.Combine(resources.AssetsRoot, "Terrain"), ResourceKind.Terrain, "SuiteTerrain");

            using Form host = NewHost();
            TerrainEditorControl editor = new(terrain, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            Pump(12, 30);

            HeadlessHarness.Assert(!editor.FogEnabled, "Fog must default to off on a new terrain.");
            editor.FogEnabled = true;
            HeadlessHarness.Assert(editor.IsDirty, "Toggling fog must mark the terrain dirty.");

            ushort[] beforeHeights = (ushort[])editor.Terrain.HeightsData.Clone();
            editor.BeginStroke(0f, 0f);
            for (int i = 0; i < 6; i++)
            {
                editor.ApplyBrushAt(i * 2f, 0f, TerrainEditorControl.TerrainBrush.Raise);
            }

            editor.EndStroke();
            HeadlessHarness.Assert(
                !beforeHeights.AsSpan().SequenceEqual(editor.Terrain.HeightsData),
                "Raise brush did not change any heights.");

            HeadlessHarness.Assert(editor.CanUndo, "Terrain stroke must journal for undo.");
            editor.Undo();
            HeadlessHarness.Assert(
                beforeHeights.AsSpan().SequenceEqual(editor.Terrain.HeightsData),
                "Undo did not restore the pre-stroke heights.");
            editor.Redo();
            HeadlessHarness.Assert(
                !beforeHeights.AsSpan().SequenceEqual(editor.Terrain.HeightsData),
                "Redo did not re-apply the stroke.");

            // Layer 3 (Snow) only appears above height 14 in the seed generator, so a flat,
            // low starting point is guaranteed near-zero on that channel before painting —
            // unlike layer 0 (Grass), which the generator can already saturate to 100% at a
            // flat low spot, making "any byte changed" flaky depending on the noise seed.
            editor.SelectPaintLayer(3);
            byte[] beforeSplat = (byte[])editor.Terrain.SplatmapData.Clone();
            editor.BeginStroke(4f, 4f);
            editor.ApplyBrushAt(4f, 4f, TerrainEditorControl.TerrainBrush.Paint);
            editor.EndStroke();
            HeadlessHarness.Assert(
                !beforeSplat.AsSpan().SequenceEqual(editor.Terrain.SplatmapData),
                "Paint brush did not change the splat map.");

            editor.Save();
            HeadlessHarness.Assert(File.Exists(terrain + ".gterrain"), "Terrain save did not write the binary heights sidecar.");
            HeadlessHarness.Assert(!editor.IsDirty, "Save must always succeed and clear dirty, even right after a prior save.");
            editor.Save();
            HeadlessHarness.Assert(!editor.IsDirty, "A second Save (nothing dirty) must not throw or misbehave.");

            using (Form reloadHost = NewHost())
            {
                TerrainEditorControl reloaded = new(terrain, project.RootPath);
                reloadHost.Controls.Add(reloaded);
                GateSuite.ShowHost(reloadHost);
                Pump(4, 25);
                HeadlessHarness.Assert(reloaded.FogEnabled, "Fog toggle did not persist across save/reload.");
            }

            Bitmap? frame = editor.Viewport.CaptureFrame(settleFrames: 5);
            HeadlessHarness.Assert(frame is not null, "Terrain viewport readback failed.");
            HeadlessHarness.Assert(
                editor.TerrainPreviewChunkCount >= 1,
                "The Terrain Editor must submit chunked TerrainGround meshes so paint layers are visible.");
            RuntimeImageMetrics metrics = SaveCapture(ctx, frame!, "22-terrain-editor.png", "Terrain Editor", minColors: 48);

            // Lighting overhaul guard: a flat-Lambert untextured sheet reads as a handful of
            // colours; the shaded rig (sun+shadows+hemisphere+specular+haze) must produce a
            // rich distribution.
            HeadlessHarness.Assert(
                metrics.UniqueSampledColors >= 48,
                $"Terrain shading looks flat ({metrics.UniqueSampledColors} colours — expected the lit, non-Lambert rig).");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.Room.TilePainting2D", () =>
        {
            // Authored tile cells remain room data; this exercises the tile painting UI.
            string tileSetsFolder = Path.Combine(resources.AssetsRoot, "Sprites", "TileSets");
            Directory.CreateDirectory(tileSetsFolder);
            string tileSet = resources.CreateResource(tileSetsFolder, ResourceKind.Image, "SuiteTiles");
            File.WriteAllText(tileSet,
                """{"schemaVersion":1,"source":"","tileWidth":32,"tileHeight":32,"margin":0,"separation":0,"collision":[]}""");

            string tileRoom = resources.CreateResource(roomsFolder, ResourceKind.Room, "SuiteTileRoom");
            using Form host = NewHost();
            RoomEditorControl editor = new(tileRoom, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            Pump(8, 25);

            HeadlessHarness.Assert(!editor.ViewMode3D, "Tile painting needs the room in 2D mode.");
            EnableTileSet(tileSet);
            editor.BeginTilePainting(tileSet);
            HeadlessHarness.Assert(editor.IsTilePainting, "Selecting a tile set did not arm tile painting.");
            HeadlessHarness.Assert(editor.ActiveTileLayer is not null, "No TileLayer node was created for the tile set.");

            // Paint a floor run plus a small step — a recognisable platformer ground.
            for (int x = 0; x < 8; x++)
            {
                HeadlessHarness.Assert(
                    editor.PaintTileAtWorld(x * 32f + 4f, 256f),
                    $"Painting floor tile {x} did nothing.");
            }

            for (int x = 5; x < 8; x++)
            {
                editor.PaintTileAtWorld(x * 32f + 4f, 224f);
            }

            RoomTileLayerData layerData = editor.ActiveTileLayer!.TileLayer!;
            HeadlessHarness.Assert(layerData.Cells.Count == 11, $"Expected 11 painted cells, got {layerData.Cells.Count}.");

            // Painting the same cell with the same tile must not duplicate it.
            editor.PaintTileAtWorld(4f, 256f);
            HeadlessHarness.Assert(layerData.Cells.Count == 11, "Repainting an identical tile duplicated the cell.");

            // Erase, then undo the erase.
            HeadlessHarness.Assert(editor.PaintTileAtWorld(4f, 256f, erase: true), "Erasing a tile did nothing.");
            HeadlessHarness.Assert(layerData.Cells.Count == 10, "Erase did not remove the cell.");
            editor.Undo();
            HeadlessHarness.Assert(layerData.Cells.Count == 11, "Undo did not restore the erased tile.");

            // Select-mode tile editing: when the layer owns the click, a painted tile behaves like
            // a normal room element rather than becoming trapped in the paint-only workflow.
            editor.SetTool(RoomEditorControl.RoomTool.Select);
            RoomNode editableLayer = editor.ActiveTileLayer!;
            editor.Select(editableLayer);
            Point tileFrom = editor.ClientFromWorld2D(new Vector2(16f, 272f));
            Point tileTo = editor.ClientFromWorld2D(new Vector2(16f, 240f));
            editor.EditorPointerDown(tileFrom, MouseButtons.Left, Keys.None);
            editor.EditorPointerMove(tileTo, MouseButtons.Left, Keys.None);
            editor.EditorPointerUp(tileTo, MouseButtons.Left, Keys.None);
            HeadlessHarness.Assert(editor.SelectedTileCell is { Y: 7 }, "Click-drag on the selected tile layer did not move the individual tile.");
            HeadlessHarness.Assert(layerData.Cells.Count == 11, "Moving a tile duplicated or deleted a painted cell.");

            // The Inspector edits the tile itself, including dimensions/rotation, not the whole layer.
            HeadlessHarness.Assert(editor.TryApplyInspectorValue("Selection.TileCell.Width", 40f), "Tile width was not editable in the Inspector.");
            HeadlessHarness.Assert(editor.TryApplyInspectorValue("Selection.TileCell.Height", 24f), "Tile height was not editable in the Inspector.");
            HeadlessHarness.Assert(editor.TryApplyInspectorValue("Selection.TileCell.Rotation", 12f), "Tile rotation was not editable in the Inspector.");
            RoomTileCell editedTile = editor.SelectedTileCell ?? throw new InvalidOperationException("Tile selection disappeared while editing it.");
            HeadlessHarness.Assert(MathF.Abs(editedTile.ScaleX - 1.25f) < .001f
                && MathF.Abs(editedTile.ScaleY - .75f) < .001f
                && MathF.Abs(editedTile.Rotation - 12f) < .001f,
                "Inspector changes did not reach the selected tile transform.");

            // Saved tile transforms must survive reload and world-space tile queries.
            editor.Save();
            RoomAsset reloaded = RoomAssetLoader.Parse(tileRoom);
            RoomNode? savedLayer = reloaded.Nodes.FirstOrDefault(n => n.Kind == RoomNodeKind.TileLayer);
            HeadlessHarness.Assert(savedLayer is not null, "Saved room lost its tile layer.");
            HeadlessHarness.Assert(
                savedLayer!.TileLayer!.Cells.Count == 11,
                $"Saved tile layer has {savedLayer.TileLayer.Cells.Count} cells instead of 11.");
            RoomTileCell movedTile = savedLayer.TileLayer.Cells.Single(cell => cell.X == 0 && cell.Y == 7);
            HeadlessHarness.Assert(MathF.Abs(movedTile.ScaleX - 1.25f) < .001f
                && MathF.Abs(movedTile.ScaleY - .75f) < .001f
                && MathF.Abs(movedTile.Rotation - 12f) < .001f,
                "Per-tile resize/rotation did not persist through room JSON.");
            RoomTileCollisionMap tileMap = new(reloaded, project.RootPath, _ => null);
            HeadlessHarness.Assert(tileMap.TryCell(16f, 240f, savedLayer.Name, out _, out RoomTileCell hitTile, out _)
                && ReferenceEquals(hitTile, movedTile),
                "Runtime tile lookup does not follow an individually moved/resized tile.");

            RoomSceneBuilder builder = new(project.RootPath);
            EcsWorld world = new();
            RoomBuildResult build = builder.Build(world, reloaded);
            HeadlessHarness.Assert(
                build.SpawnedEntities.Count == 0,
                $"Tile cells must stay batched room data instead of spawning {build.SpawnedEntities.Count} phantom ECS entities.");

            // ── Parallax backgrounds ────────────────────────────────────────────
            // The runtime already draws Background nodes (RoomRenderSubsystem); this covers the
            // authoring UI. Each added layer must stack behind the previous one.
            string bgSprite = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, "SuiteSkyLayer");
            string bgSprite2 = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, "SuiteHillsLayer");

            RoomNode sky = editor.AddBackground(bgSprite);
            RoomNode hills = editor.AddBackground(bgSprite2);
            HeadlessHarness.Assert(editor.Backgrounds.Count == 2, $"Expected 2 background layers, got {editor.Backgrounds.Count}.");
            HeadlessHarness.Assert(
                hills.Background!.Depth > sky.Background!.Depth,
                $"Each added background should stack behind the last (sky {sky.Background.Depth}, hills {hills.Background.Depth}).");
            HeadlessHarness.Assert(
                sky.Background.Layout == RoomBackgroundLayout.StretchRoom,
                "Backgrounds should default to stretching over the room.");

            // Parallax scroll rate is what makes layers move at different speeds.
            editor.SetBackgroundScroll(hills, 12f, 0f);
            HeadlessHarness.Assert(
                Math.Abs(hills.Background.Scroll[0] - 12f) < 0.001f,
                "Background scroll rate was not applied.");
            HeadlessHarness.Assert(editor.CanUndo, "Background scroll must be undoable.");
            editor.Undo();
            HeadlessHarness.Assert(
                Math.Abs(hills.Background.Scroll[0]) < 0.001f,
                "Undo did not restore the background scroll rate.");

            editor.Save();
            RoomAsset withBackgrounds = RoomAssetLoader.Parse(tileRoom);
            HeadlessHarness.Assert(
                withBackgrounds.Nodes.Count(n => n.Kind == RoomNodeKind.Background) == 2,
                "Saved room lost its background layers.");

            Bitmap? frame = editor.Viewport.CaptureFrame(settleFrames: 4);
            HeadlessHarness.Assert(frame is not null, "Tile room viewport readback failed.");
            SaveCapture(ctx, frame!, "48-room-tile-painting.png", "Room Editor — tiles + backgrounds", minColors: 5);
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.Room.SettingsAndViewports", () =>
        {
            // Room settings and viewports had no authoring surface at all. Everything below drives
            // the panels' own fields rather than the room model, because writing to the model would
            // prove the document works and nothing about whether the panel is wired to it.
            string room = resources.CreateResource(roomsFolder, ResourceKind.Room, "SuiteViewportRoom");
            using Form host = NewHost();
            RoomEditorControl editor = new(room, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            Pump(8, 25);

            // ── Room settings ────────────────────────────────────────────────────
            editor.SetRoomSettingsFields(frameRate: 30, width: 1600, height: 900);

            // A room is named by its file, exactly as every other resource is, so the Name field
            // reports rather than edits. An editable box here would revert on the next save.
            HeadlessHarness.Assert(
                !editor.RoomNameEditable,
                "The room Name field is editable, but Save derives the name from the file path — "
                + "anything typed here would silently revert.");
            HeadlessHarness.Assert(
                editor.Room.Settings.TargetFps == 30,
                $"Frame rate did not reach the document (got {editor.Room.Settings.TargetFps}).");
            HeadlessHarness.Assert(
                editor.Room.Settings.Width == 1600 && editor.Room.Settings.Height == 900,
                "Room dimensions did not reach the document.");
            HeadlessHarness.Assert(
                !editor.RoomDepthEditable,
                "Depth is editable in a 2D room; it has no meaning there and would save a value the game ignores.");

            // Undo has to cover settings too, or a mistyped room size is unrecoverable.
            editor.Undo();
            HeadlessHarness.Assert(
                editor.Room.Settings.Width != 1600 || editor.Room.Settings.Height != 900,
                "Undo did not revert the room settings edit.");
            editor.Redo();
            HeadlessHarness.Assert(
                editor.Room.Settings.Width == 1600, "Redo did not restore the room settings edit.");

            // ── Viewports ────────────────────────────────────────────────────────
            HeadlessHarness.Assert(
                editor.Room.Viewports.Count == RoomAsset.MaxViewports,
                $"The room has {editor.Room.Viewports.Count} viewport slots, expected {RoomAsset.MaxViewports}.");

            // A disabled slot must grey its fields, or a designer edits settings that do nothing.
            editor.SelectViewport(1);
            HeadlessHarness.Assert(
                editor.ViewportFieldsGreyed,
                "A disabled viewport's fields are editable; its settings would have no effect in game.");

            editor.SetViewportEnabled(true);
            HeadlessHarness.Assert(
                !editor.ViewportFieldsGreyed, "Enabling a viewport did not un-grey its fields.");

            editor.SetViewportSource(0f, 0f, 800f, 900f);
            editor.SetViewportPort(0, 0, 800, 900);
            editor.SetViewportFollowTarget("Hero");

            RoomViewport authored = editor.Room.Viewports[1];
            HeadlessHarness.Assert(
                authored.Enabled && Math.Abs(authored.SourceWidth - 800f) < 0.01f
                && authored.PortWidth == 800 && authored.FollowTarget == "Hero",
                "Viewport panel edits did not reach the document.");

            // Slot identity: configuring 1 must not disturb 2, or split-screen layouts are unusable.
            editor.SelectViewport(2);
            HeadlessHarness.Assert(
                !editor.Room.Viewports[2].Enabled,
                "Enabling viewport 1 also enabled viewport 2; the slots are not independent.");
            editor.SetViewportEnabled(true);
            editor.SetViewportSource(800f, 0f, 800f, 900f);
            editor.SetViewportPort(800, 0, 800, 900);

            HeadlessHarness.Assert(
                editor.Room.Viewports[1].PortX == 0 && editor.Room.Viewports[2].PortX == 800,
                "Two viewports could not be placed side by side, which is the split-screen case.");
            HeadlessHarness.Assert(
                editor.Room.UsesViewports,
                "The room does not report using viewports, so the runtime would take the single-camera path.");

            // Returning to slot 1 must show slot 1's values, not slot 2's.
            editor.SelectViewport(1);
            HeadlessHarness.Assert(
                Math.Abs(editor.Room.Viewports[1].SourceX) < 0.01f,
                "Switching slots did not reload the selected viewport's own settings.");

            // ── Visibility toolbar ───────────────────────────────────────────────
            editor.BeginPlacement(heroObject);
            editor.PlaceObjectAtWorld2D(200f, 200f);
            RoomNode placed = editor.Room.Nodes[^1];

            editor.SetKindVisible(RoomNodeKind.GameObject, false);
            HeadlessHarness.Assert(
                editor.HitTestNode(editor.ClientFromWorld2D(new Vector2(200f, 200f))) is null,
                "A hidden object can still be picked. Draw and hit testing must share one filter, "
                + "or you can select something you cannot see.");
            editor.SetKindVisible(RoomNodeKind.GameObject, true);
            HeadlessHarness.Assert(
                editor.HitTestNode(editor.ClientFromWorld2D(new Vector2(200f, 200f))) is not null,
                "Re-showing objects did not restore hit testing.");

            editor.SetGridVisible(false);
            HeadlessHarness.Assert(!editor.ShowGrid, "The grid toggle did not take.");
            editor.SetGridVisible(true);

            editor.SetViewportOutlineFilter(1);
            HeadlessHarness.Assert(editor.ViewportOutlineFilter == 1, "The viewport outline filter did not take.");
            editor.SetViewportOutlineFilter(-1);

            editor.Save();

            // The whole point is that this survives a reload — a viewport that only exists in the
            // live editor is the drift bug this feature was built to avoid.
            RoomEditorControl reopened = new(room, project.RootPath);
            HeadlessHarness.Assert(
                reopened.Room.Viewports[1].Enabled && reopened.Room.Viewports[2].PortX == 800
                && reopened.Room.Settings.TargetFps == 30,
                "Room settings or viewports did not survive save and reload.");

            // Frame both viewports, so the capture shows the split-screen layout rather than a
            // corner of one of them — the picture is the point of drawing the regions at all.
            editor.Viewport.Camera2DX = editor.Room.Settings.Width * 0.5f;
            editor.Viewport.Camera2DY = editor.Room.Settings.Height * 0.5f;
            editor.Viewport.Zoom2D = 0.42f;
            Pump(6, 20);

            Bitmap? frame = editor.Viewport.CaptureFrame(settleFrames: 4);
            HeadlessHarness.Assert(frame is not null, "Viewport room readback failed.");
            SaveCapture(ctx, frame!, "49-room-viewports.png", "Room Editor — viewport regions", minColors: 4);
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.Room.CameraFrustumOverlay", () =>
        {
            // CAM-2: Room 3D frustum wires face play-camera forward (−Z / follow / Engine yaw),
            // and a marker click drives the existing second-camera inset.
            string roomPath = resources.CreateResource(roomsFolder, ResourceKind.Room, "SuiteFrustumOverlay");
            using Form host = NewHost();
            RoomEditorControl editor = new(roomPath, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            Pump(8, 25);

            editor.ViewMode3D = true;
            Pump(4, 25);

            editor.BeginPlacement(heroObject);
            editor.PlaceObjectAtWorld2D(0f, 0f);
            RoomNode hero = editor.Room.Nodes[^1];
            hero.Transform.X = 8f;
            hero.Transform.Y = 0f;
            hero.Transform.Z = -4f;

            editor.SelectViewport(6);
            editor.SetViewportEnabled(true);
            editor.SetViewportSource3D(0f, 1f, 0f, 16f, 9f, 16f);
            editor.SetViewportFrustum(0.5f, 40f, 70f);
            editor.SetViewportFollowTarget("SuiteHero");

            HeadlessHarness.Assert(
                editor.TryGetViewportFrustumPose(6, out Vector3 eye, out Vector3 forward, out float fov, out float near, out float far, out _),
                "Viewport frustum pose was not resolved.");
            HeadlessHarness.Assert(
                MathF.Abs(eye.X) < 0.01f && MathF.Abs(eye.Y - 1f) < 0.01f && MathF.Abs(eye.Z) < 0.01f,
                $"Frustum eye should be the authored Source XYZ (got {eye}).");
            HeadlessHarness.Assert(
                MathF.Abs(fov - 70f) < 0.01f && MathF.Abs(near - 0.5f) < 0.01f && MathF.Abs(far - 40f) < 0.01f,
                "Frustum near/far/FOV did not match the Viewports panel.");

            Vector3 expectedFollow = Vector3.Normalize(new Vector3(8f, -1f, -4f));
            HeadlessHarness.Assert(
                Vector3.Dot(Vector3.Normalize(forward), expectedFollow) > 0.99f,
                $"Follow-target frustum should look at the hero (forward={forward}, expected≈{expectedFollow}).");

            // Engine registry orientation wins when the slot exists (CAM-1 ↔ CAM-2).
            Genesis.Shared.Commands.Engine.Camera3DCreate(6);
            Genesis.Shared.Commands.Engine.Camera3DSetPos(6, 0f, 1f, 0f);
            Genesis.Shared.Commands.Engine.Camera3DSetYaw(6, 90f);
            Genesis.Shared.Commands.Engine.Camera3DSetPitch(6, 0f);
            Genesis.Shared.Commands.Engine.Camera3DSetFrustum(6, 70f, 16f / 9f, 0.5f, 40f);

            HeadlessHarness.Assert(
                editor.TryGetViewportFrustumPose(6, out _, out Vector3 registryForward, out _, out _, out _, out _),
                "Registry frustum pose was not resolved.");
            Vector3 expectedYaw = Genesis.Runtime.Core.MathUtil.DirectionFromYawPitch(90f * MathF.PI / 180f, 0f);
            HeadlessHarness.Assert(
                Vector3.Dot(Vector3.Normalize(registryForward), expectedYaw) > 0.99f,
                $"Engine yaw should orient the overlay (forward={registryForward}, expected≈{expectedYaw}).");

            editor.Viewport.Camera.Target = eye;
            editor.Viewport.Camera.Distance = 12f;
            Pump(4, 20);

            Point marker = editor.ClientFromWorld3D(eye);
            HeadlessHarness.Assert(
                editor.TryHitCameraMarker(marker, out int hitIndex, out bool hitGame) && hitIndex == 6 && !hitGame,
                "Camera marker was not pickable at the frustum eye.");

            editor.EditorPointerDown(marker, MouseButtons.Left, Keys.None);
            editor.EditorPointerUp(marker, MouseButtons.Left, Keys.None);
            HeadlessHarness.Assert(
                editor.Viewport.HasSecondaryCamera,
                "Clicking the frustum marker did not open the second-camera inset.");

            Bitmap? frame = editor.Viewport.CaptureFrame(settleFrames: 4);
            HeadlessHarness.Assert(frame is not null, "Frustum overlay readback failed.");
            SaveCapture(ctx, frame!, "49b-room-camera-frustum.png", "Room Editor — CAM-2 frustum overlay", minColors: 4);
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.Room.ViewsHierarchyAndOverrides", () =>
        {
            // The last three Track D gaps. All were runtime-ready (ApplyActiveGameCamera,
            // ResolveWorldTransform parent chains, ApplyOverrides) but had no authoring surface.
            string room = resources.CreateResource(roomsFolder, ResourceKind.Room, "SuiteHierarchyRoom");
            using Form host = NewHost();
            RoomEditorControl editor = new(room, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            Pump(8, 25);

            editor.BeginPlacement(heroObject);
            editor.EditorPointerDown(editor.ClientFromWorld2D(new Vector2(160f, 160f)), MouseButtons.Left, Keys.Control);
            editor.EditorPointerUp(editor.ClientFromWorld2D(new Vector2(160f, 160f)), MouseButtons.Left, Keys.Control);
            editor.EditorPointerDown(editor.ClientFromWorld2D(new Vector2(320f, 160f)), MouseButtons.Left, Keys.None);
            editor.EditorPointerUp(editor.ClientFromWorld2D(new Vector2(320f, 160f)), MouseButtons.Left, Keys.None);
            HeadlessHarness.Assert(editor.Room.Nodes.Count == 2, $"Expected 2 placed nodes, got {editor.Room.Nodes.Count}.");

            RoomNode parent = editor.Room.Nodes[0];
            RoomNode child = editor.Room.Nodes[1];

            // ── Hierarchy: the child's transform must compose onto the parent's ──
            HeadlessHarness.Assert(editor.SetNodeParent(child, parent), "Re-parenting failed.");
            HeadlessHarness.Assert(child.ParentId == parent.Id, "Child did not record its parent.");
            HeadlessHarness.Assert(!editor.SetNodeParent(parent, child), "A parent loop should be refused.");
            HeadlessHarness.Assert(!editor.SetNodeParent(child, child), "Self-parenting should be refused.");

            // ── Views: designate the game camera ─────────────────────────────────
            editor.SetActiveGameCamera(parent);
            HeadlessHarness.Assert(ReferenceEquals(editor.ActiveGameCamera, parent), "Active game camera was not set.");
            editor.SetActiveGameCamera(null);
            HeadlessHarness.Assert(editor.ActiveGameCamera is null, "Active game camera was not cleared.");
            editor.SetActiveGameCamera(parent);

            // ── Per-instance override: change one instance without touching the prefab ──
            editor.SetComponentOverride(child, "SpriteComponent", "Alpha", "0.25");
            HeadlessHarness.Assert(
                child.GameObject!.ComponentOverrides.Count == 1,
                "Component override was not recorded on the instance.");
            HeadlessHarness.Assert(editor.CanUndo, "Overrides must be undoable.");
            editor.Undo();
            HeadlessHarness.Assert(
                child.GameObject.ComponentOverrides.Count == 0
                    || child.GameObject.ComponentOverrides[0].Properties.Count == 0,
                "Undo did not remove the override.");
            editor.Redo();

            // ── Everything must survive save/reload and reach the runtime ────────
            editor.Save();
            RoomAsset reloaded = RoomAssetLoader.Parse(room);
            RoomNode savedChild = reloaded.Nodes.First(n => n.Id == child.Id);
            HeadlessHarness.Assert(savedChild.ParentId == parent.Id, "Saved room lost the parent link.");
            HeadlessHarness.Assert(reloaded.ActiveGameCameraId == parent.Id, "Saved room lost the active game camera.");
            HeadlessHarness.Assert(
                savedChild.GameObject!.ComponentOverrides.Count == 1,
                "Saved room lost the per-instance override.");

            // The runtime composes the parent chain: the child's world position is parent + local.
            RoomSceneBuilder builder = new(project.RootPath);
            RoomTransform childWorld = builder.ResolveWorldTransform(reloaded, savedChild);
            RoomNode savedParent = reloaded.Nodes.First(n => n.Id == parent.Id);
            HeadlessHarness.Assert(
                Math.Abs(childWorld.X - (savedParent.Transform.X + savedChild.Transform.X)) < 0.01f,
                $"Parented child world X should be parent+local, got {childWorld.X}.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.Room.DenseSceneClipboardLayers", () =>
        {
            string roomPath = resources.CreateResource(roomsFolder, ResourceKind.Room, "SuiteDenseWorkflow");
            using Form host = NewHost();
            RoomEditorControl editor = new(roomPath, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            Pump(8, 25);

            // Twelve instances, including three exactly overlapped, exercise the case where
            // viewport clicking alone is ambiguous and the outliner/Alt-cycle must stay usable.
            for (int index = 0; index < 12; index++)
            {
                Vector2 position = index < 3
                    ? new Vector2(256f, 224f)
                    : new Vector2(128f + (index % 5) * 96f, 352f + (index / 5) * 64f);
                editor.BeginPlacement(heroObject);
                editor.PlaceObjectAtWorld2D(position.X, position.Y);
            }

            List<RoomNode> objects = editor.Room.Nodes.Where(node => node.Kind == RoomNodeKind.GameObject).ToList();
            HeadlessHarness.Assert(objects.Count == 12, $"Dense fixture has {objects.Count}/12 objects.");
            editor.SetNodeDepth(objects[0], -30);
            editor.SetNodeDepth(objects[1], -20);
            editor.SetNodeDepth(objects[2], -10);
            Point overlap = editor.ClientFromWorld2D(new Vector2(256f, 224f));
            IReadOnlyList<RoomNode> overlapHits = editor.HitTestNodes(overlap);
            HeadlessHarness.Assert(overlapHits.Count == 3, $"Expected 3 overlap hits, got {overlapHits.Count}.");
            HeadlessHarness.Assert(ReferenceEquals(overlapHits[0], objects[0]), "Dense hit list is not front-to-back by authored depth.");
            editor.Select(overlapHits[0]);
            editor.EditorPointerDown(overlap, MouseButtons.Left, Keys.Alt);
            editor.EditorPointerUp(overlap, MouseButtons.Left, Keys.Alt);
            HeadlessHarness.Assert(ReferenceEquals(editor.SelectedNode, overlapHits[1]), "Alt-click did not cycle beneath the front overlap.");

            // Ctrl/Shift-style selection is represented as one set and transformed atomically.
            editor.Select(objects[0]);
            editor.Select(objects[1], additive: true);
            editor.Select(objects[2], additive: true);
            HeadlessHarness.Assert(editor.SelectedNodes.Count == 3, "Multi-selection did not retain all three objects.");
            float[] beforeX = editor.SelectedNodes.Select(node => node.Transform.X).ToArray();
            HeadlessHarness.Assert(editor.MoveSelectionBy(new Vector3(64f, 32f, 0f)), "Grouped move was refused on an unlocked layer.");
            for (int index = 0; index < 3; index++)
            {
                HeadlessHarness.Assert(
                    MathF.Abs(editor.SelectedNodes[index].Transform.X - beforeX[index] - 64f) < .01f,
                    "Grouped move did not apply the same delta to every selection member.");
            }
            editor.Undo();
            HeadlessHarness.Assert(MathF.Abs(objects[0].Transform.X - beforeX[0]) < .01f, "Grouped move did not undo atomically.");
            editor.Redo();

            // Copy/paste preserves the selected parent graph while generating unique ids.
            HeadlessHarness.Assert(editor.SetNodeParent(objects[1], objects[0]), "Dense fixture parent link failed.");
            editor.Select(objects[0]);
            editor.Select(objects[1], additive: true);
            editor.Select(objects[2], additive: true);
            HeadlessHarness.Assert(editor.CopySelected(), "Copy did not accept a non-empty selection.");
            IReadOnlyList<RoomNode> pasted = editor.PasteCopied();
            HeadlessHarness.Assert(pasted.Count == 3, $"Paste produced {pasted.Count}/3 objects.");
            HeadlessHarness.Assert(pasted.Select(node => node.Id).Distinct().Count() == 3, "Paste reused a room-node id.");
            HeadlessHarness.Assert(pasted[1].ParentId == pasted[0].Id, "Paste did not remap the copied parent graph.");

            // Deleting only a parent safely detaches its surviving child; undo restores both.
            editor.Select(pasted[0]);
            HeadlessHarness.Assert(editor.DeleteSelection() == 1, "Single selected pasted parent was not deleted.");
            HeadlessHarness.Assert(string.IsNullOrEmpty(pasted[1].ParentId), "Deleting a parent left a dangling child id.");
            editor.Undo();
            HeadlessHarness.Assert(editor.Room.Nodes.Contains(pasted[0]), "Undo did not restore the deleted parent.");
            HeadlessHarness.Assert(pasted[1].ParentId == pasted[0].Id, "Undo did not restore the detached parent link.");

            // Layer order is an additive runtime depth; lock protects viewport, inspector and delete.
            RoomLayer gameplay = editor.AddRoomLayer("Gameplay Front");
            editor.SetLayerOrder(gameplay, -400);
            editor.Select(objects[0]);
            editor.Select(objects[1], additive: true);
            editor.Select(objects[2], additive: true);
            HeadlessHarness.Assert(editor.SetSelectionLayer(gameplay), "Multi-selection did not move to the chosen layer.");
            editor.SetLayerLocked(gameplay, true);
            HeadlessHarness.Assert(!editor.MoveSelectionBy(new Vector3(32f, 0f, 0f)), "Locked layer allowed a grouped transform.");
            HeadlessHarness.Assert(editor.DeleteSelection() == 0, "Locked layer allowed deletion.");
            editor.SetLayerVisibility(gameplay, false);
            HeadlessHarness.Assert(editor.HitTestNodes(editor.ClientFromWorld2D(new Vector2(objects[0].Transform.X, objects[0].Transform.Y))).Count == 0,
                "Hidden layer remained viewport-selectable.");
            editor.SetLayerVisibility(gameplay, true);

            HeadlessHarness.Assert(editor.SceneOutliner.Items.Count == editor.Room.Nodes.Count,
                $"Outliner shows {editor.SceneOutliner.Items.Count}/{editor.Room.Nodes.Count} nodes.");
            editor.Save();
            RoomAsset saved = RoomAssetLoader.Parse(roomPath);
            RoomLayer savedLayer = saved.Layers.First(layer => layer.Id == gameplay.Id);
            HeadlessHarness.Assert(savedLayer.Locked && savedLayer.Order == -400, "Layer lock/order did not round-trip.");
            RoomNode savedFront = saved.Nodes.First(node => node.Id == objects[0].Id);
            HeadlessHarness.Assert(savedFront.LayerId == gameplay.Id && savedFront.Order == -30,
                "Per-instance layer/depth did not round-trip.");

            EcsWorld world = new();
            RoomBuildResult build = new RoomSceneBuilder(project.RootPath).Build(world, saved);
            Entity frontEntity = build.EntitiesByNodeId[savedFront.Id];
            HeadlessHarness.Assert(world.Has<SpriteComponent>(frontEntity), "Runtime object lost its sprite-depth component.");
            float runtimeDepth = world.Has<Draw2DComponent>(frontEntity)
                ? world.GetRef<Draw2DComponent>(frontEntity).Depth
                : world.GetRef<SpriteComponent>(frontEntity).Depth;
            HeadlessHarness.Assert(
                MathF.Abs(runtimeDepth - (-430f)) < .01f,
                $"Runtime depth did not include layer+node offsets (got {runtimeDepth}).");

            Pump(5, 25);
            Bitmap outlinerCapture = new(host.ClientSize.Width, host.ClientSize.Height, PixelFormat.Format32bppArgb);
            host.DrawToBitmap(outlinerCapture, new Rectangle(Point.Empty, host.ClientSize));
            SaveCapture(ctx, outlinerCapture, "62-room-phase4-dense-outliner.png",
                "Room Phase 4 — dense scene outliner and inspector", minColors: 8);
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.Room.BackgroundTileInspectorRoundTrip", () =>
        {
            string tileSet = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, "Phase4Tiles");
            EnableTileSet(tileSet);
            string roomPath = resources.CreateResource(roomsFolder, ResourceKind.Room, "SuitePhase4ArtWorkflow");
            using Form host = NewHost();
            RoomEditorControl editor = new(roomPath, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            Pump(8, 25);

            editor.BeginTilePainting(tileSet, createNewLayer: true);
            RoomNode firstTiles = HeadlessHarness.Require(editor.ActiveTileLayer, "First Phase 4 tile layer");
            Point strokeStart = editor.ClientFromWorld2D(new Vector2(4f, 132f));
            Point strokeEnd = editor.ClientFromWorld2D(new Vector2(164f, 132f));
            editor.EditorPointerDown(strokeStart, MouseButtons.Left, Keys.None);
            editor.EditorPointerMove(strokeEnd, MouseButtons.Left, Keys.None);
            editor.EditorPointerUp(strokeEnd, MouseButtons.Left, Keys.None);
            HeadlessHarness.Assert(firstTiles.TileLayer!.Cells.Count == 6,
                $"Interpolated tile stroke painted {firstTiles.TileLayer.Cells.Count}/6 cells.");
            HeadlessHarness.Assert(editor.LastEditLabel == "Paint tile stroke", "A drag was not journaled as one tile stroke.");
            editor.Undo();
            HeadlessHarness.Assert(firstTiles.TileLayer.Cells.Count == 0, "One undo did not remove the whole tile stroke.");
            editor.Redo();
            HeadlessHarness.Assert(firstTiles.TileLayer.Cells.Count == 6, "Redo did not restore the whole tile stroke.");

            editor.BeginTilePainting(tileSet, createNewLayer: true);
            RoomNode secondTiles = HeadlessHarness.Require(editor.ActiveTileLayer, "Second Phase 4 tile layer");
            HeadlessHarness.Assert(
                editor.Room.Nodes.Count(node => node.Kind == RoomNodeKind.TileLayer) == 2,
                "Ctrl/new-layer tile workflow reused the first layer.");

            // Drive the real PropertyGrid model, not just direct data APIs, then verify its setters
            // reach the document and undo journal.
            PropertyGrid grid = (PropertyGrid)editor.Controls.Find("RoomNodePropertyGrid", true).Single();
            object tileInspector = HeadlessHarness.Require(grid.SelectedObject, "Tile inspector model");
            PropertyDescriptorCollection tileProperties = TypeDescriptor.GetProperties(tileInspector);
            HeadlessHarness.Require(tileProperties["CellWidth"], "Tile CellWidth inspector property").SetValue(tileInspector, 48);
            HeadlessHarness.Require(tileProperties["CellHeight"], "Tile CellHeight inspector property").SetValue(tileInspector, 24);
            HeadlessHarness.Require(tileProperties["CollisionEnabled"], "Tile collision inspector property").SetValue(tileInspector, true);
            editor.SetNodeDepth(secondTiles, -350);
            HeadlessHarness.Assert(
                secondTiles.TileLayer!.CellWidth == 48 && secondTiles.TileLayer.CellHeight == 24 && secondTiles.TileLayer.CollisionEnabled,
                "Tile inspector setters did not update the selected layer.");

            string skyAsset = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, "Phase4Sky");
            string cloudAsset = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, "Phase4Clouds");
            RoomNode sky = editor.AddBackground(skyAsset);
            PropertyGrid backgroundGrid = (PropertyGrid)editor.Controls.Find("RoomNodePropertyGrid", true).Single();
            object backgroundInspector = HeadlessHarness.Require(backgroundGrid.SelectedObject, "Background inspector model");
            PropertyDescriptorCollection backgroundProperties = TypeDescriptor.GetProperties(backgroundInspector);
            HeadlessHarness.Require(backgroundProperties["Layout"], "Background Layout inspector property")
                .SetValue(backgroundInspector, RoomBackgroundLayout.StretchView);
            HeadlessHarness.Require(backgroundProperties["Opacity"], "Background Opacity inspector property")
                .SetValue(backgroundInspector, .55f);
            editor.SetBackgroundRepeat(sky, true, false);
            editor.SetBackgroundScroll(sky, 8f, -2f);
            editor.SetBackgroundTint(sky, unchecked((int)0xffb8d8ff));
            editor.SetNodeDepth(sky, 8_000);
            RoomNode clouds = editor.AddBackground(cloudAsset, RoomBackgroundLayout.Tile);
            editor.SetBackgroundOpacity(clouds, .35f);
            editor.SetNodeDepth(clouds, 9_000);
            HeadlessHarness.Assert(editor.Backgrounds.Count == 2, "Multiple background workflow lost a layer.");

            RoomLayer lockedDefault = editor.Room.Layers[0];
            editor.SetLayerLocked(lockedDefault, true);
            int cellsBeforeLockAttempt = secondTiles.TileLayer.Cells.Count;
            HeadlessHarness.Assert(!editor.PaintTileAtWorld(4f, 4f), "Locked tile layer accepted paint.");
            HeadlessHarness.Assert(secondTiles.TileLayer.Cells.Count == cellsBeforeLockAttempt, "Locked paint changed tile data.");

            editor.Save();
            RoomAsset saved = RoomAssetLoader.Parse(roomPath);
            RoomNode savedTiles = saved.Nodes.First(node => node.Id == secondTiles.Id);
            HeadlessHarness.Assert(
                savedTiles.TileLayer!.CellWidth == 48
                && savedTiles.TileLayer.CellHeight == 24
                && savedTiles.TileLayer.CollisionEnabled
                && savedTiles.TileLayer.Depth == -350,
                "Tile inspector values did not survive save/reload.");
            RoomNode savedSky = saved.Nodes.First(node => node.Id == sky.Id);
            HeadlessHarness.Assert(
                savedSky.Background!.Layout == RoomBackgroundLayout.StretchView
                && MathF.Abs(savedSky.Background.Opacity - .55f) < .001f
                && savedSky.Background.RepeatX
                && MathF.Abs(savedSky.Background.Scroll[0] - 8f) < .001f
                && savedSky.Background.TintArgb == unchecked((int)0xffb8d8ff),
                "Background inspector values did not survive save/reload.");
            HeadlessHarness.Assert(saved.Layers[0].Locked, "Layer lock did not survive the inspector round-trip save.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.Room.GameCameraPreviewParity", () =>
        {
            static bool MatrixClose(Matrix4x4 left, Matrix4x4 right)
            {
                ReadOnlySpan<float> a =
                [left.M11, left.M12, left.M13, left.M14, left.M21, left.M22, left.M23, left.M24,
                 left.M31, left.M32, left.M33, left.M34, left.M41, left.M42, left.M43, left.M44];
                ReadOnlySpan<float> b =
                [right.M11, right.M12, right.M13, right.M14, right.M21, right.M22, right.M23, right.M24,
                 right.M31, right.M32, right.M33, right.M34, right.M41, right.M42, right.M43, right.M44];
                for (int index = 0; index < a.Length; index++) if (MathF.Abs(a[index] - b[index]) > 0.0001f) return false;
                return true;
            }

            string cameraObject = resources.CreateResource(objectsFolder, ResourceKind.GameObject, "SuiteCameraRig");
            JObject cameraPrefab = JObject.Parse(File.ReadAllText(cameraObject));
            JArray components = (JArray)cameraPrefab["components"]!;
            components.Add(new JObject
            {
                ["id"] = "Camera2D",
                ["type"] = "Camera2DComponent",
                ["enabled"] = true,
                ["props"] = new JObject { ["Zoom"] = "1.75" },
            });
            components.Add(new JObject
            {
                ["id"] = "Camera3D",
                ["type"] = "Camera3DComponent",
                ["enabled"] = true,
                ["props"] = new JObject { ["FOV"] = "72", ["Near"] = "0.25", ["Far"] = "900" },
            });
            File.WriteAllText(cameraObject, cameraPrefab.ToString());

            string roomPath = resources.CreateResource(roomsFolder, ResourceKind.Room, "SuiteCameraPreview");
            using Form host = NewHost();
            RoomEditorControl editor = new(roomPath, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            Pump(8, 25);

            editor.BeginPlacement(heroObject);
            editor.PlaceObjectAtWorld2D(40f, 20f);
            RoomNode parent = editor.Room.Nodes[^1];
            editor.BeginPlacement(cameraObject);
            editor.PlaceObjectAtWorld2D(400f, 300f);
            RoomNode camera = editor.Room.Nodes[^1];
            editor.SetNodeParent(camera, parent);
            editor.SetActiveGameCamera(camera);

            float editorX = editor.Viewport.Camera2DX;
            float editorY = editor.Viewport.Camera2DY;
            float editorZoom = editor.Viewport.Zoom2D;
            HeadlessHarness.Assert(editor.PreviewGameCamera(), "2D Game Camera preview did not activate.");
            RoomCameraState preview2D = HeadlessHarness.Require(editor.GameCameraPreviewState, "2D preview state");
            HeadlessHarness.Assert(preview2D.HasCameraComponent, "2D camera component was not resolved.");
            float expectedCameraX = parent.Transform.X + camera.Transform.X;
            float expectedCameraY = parent.Transform.Y + camera.Transform.Y;
            HeadlessHarness.Assert(
                MathF.Abs(preview2D.Position.X - expectedCameraX) < .01f
                && MathF.Abs(preview2D.Position.Y - expectedCameraY) < .01f
                && MathF.Abs(preview2D.Zoom2D - 1.75f) < .001f,
                $"2D preview ignored parent transform/zoom: ({preview2D.Position.X},{preview2D.Position.Y}) z={preview2D.Zoom2D}.");
            HeadlessHarness.Assert(
                MathF.Abs(editor.Viewport.Camera2DX - preview2D.Position.X) < .01f
                && MathF.Abs(editor.Viewport.Camera2DY - preview2D.Position.Y) < .01f
                && MathF.Abs(editor.Viewport.Zoom2D - preview2D.Zoom2D) < .001f,
                "2D viewport did not apply the resolved runtime camera state.");
            HeadlessHarness.Assert(!editor.Viewport.NavigationEnabled, "Camera preview still allowed accidental editor navigation.");
            editor.ExitGameCameraPreview();
            HeadlessHarness.Assert(
                MathF.Abs(editor.Viewport.Camera2DX - editorX) < .01f
                && MathF.Abs(editor.Viewport.Camera2DY - editorY) < .01f
                && MathF.Abs(editor.Viewport.Zoom2D - editorZoom) < .001f,
                "Leaving Game Camera preview did not restore the editor camera.");

            editor.SetNodeParent(camera, null);
            camera.Transform.X = 5f;
            camera.Transform.Y = 3f;
            camera.Transform.Z = 12f;
            camera.Transform.RotationX = -15f;
            camera.Transform.RotationY = 35f;
            Camera3D authoredCamera = new()
            {
                Position = new Vector3(camera.Transform.X, camera.Transform.Y, camera.Transform.Z),
                Yaw = camera.Transform.RotationY * MathF.PI / 180f,
                Pitch = camera.Transform.RotationX * MathF.PI / 180f,
            };
            // Put a real room instance in the authored frustum so the preview capture proves the
            // camera view, rather than merely proving that a dark clear colour can be read back.
            parent.Transform.X = camera.Transform.X + authoredCamera.Forward.X * 8f;
            parent.Transform.Y = camera.Transform.Y + authoredCamera.Forward.Y * 8f;
            parent.Transform.Z = camera.Transform.Z + authoredCamera.Forward.Z * 8f;
            editor.ViewMode3D = true;
            Pump(4, 25);
            HeadlessHarness.Assert(editor.PreviewGameCamera(), "3D Game Camera preview did not activate.");
            RoomCameraState preview3D = HeadlessHarness.Require(editor.GameCameraPreviewState, "3D preview state");
            HeadlessHarness.Assert(
                MathF.Abs(preview3D.FieldOfViewDegrees - 72f) < .001f
                && MathF.Abs(preview3D.NearPlane - .25f) < .001f
                && MathF.Abs(preview3D.FarPlane - 900f) < .001f,
                "3D preview did not resolve authored FOV/clip planes.");
            EditorCameraOverride actual = HeadlessHarness.Require(editor.Viewport.CameraOverrideFactory, "3D camera override")();
            Camera3D runtimeCamera = new()
            {
                Position = preview3D.Position,
                Yaw = preview3D.YawRadians,
                Pitch = preview3D.PitchRadians,
                FieldOfView = preview3D.FieldOfViewDegrees * MathF.PI / 180f,
                NearPlane = preview3D.NearPlane,
                FarPlane = preview3D.FarPlane,
                AspectRatio = editor.Viewport.SurfaceWidth / (float)editor.Viewport.SurfaceHeight,
            };
            HeadlessHarness.Assert(MatrixClose(actual.View, runtimeCamera.ViewMatrix), "Studio preview view matrix differs from runtime Camera3D.");
            HeadlessHarness.Assert(MatrixClose(actual.Projection, runtimeCamera.ProjectionMatrix), "Studio preview projection differs from runtime Camera3D.");

            editor.Select(camera);
            editor.MoveSelectionBy(new Vector3(2f, 1f, -3f));
            RoomCameraState movedPreview = HeadlessHarness.Require(editor.GameCameraPreviewState, "Updated 3D preview state");
            HeadlessHarness.Assert(
                MathF.Abs(movedPreview.Position.X - 7f) < .01f
                && MathF.Abs(movedPreview.Position.Y - 4f) < .01f
                && MathF.Abs(movedPreview.Position.Z - 9f) < .01f,
                "Live camera preview did not follow an inspector/gizmo transform.");
            parent.Transform.X = movedPreview.Position.X + authoredCamera.Forward.X * 8f;
            parent.Transform.Y = movedPreview.Position.Y + authoredCamera.Forward.Y * 8f;
            parent.Transform.Z = movedPreview.Position.Z + authoredCamera.Forward.Z * 8f;

            editor.Save();
            RoomAsset saved = RoomAssetLoader.Parse(roomPath);
            HeadlessHarness.Assert(saved.ActiveGameCameraId == camera.Id, "Active game camera did not round-trip.");
            Pump(5, 25);
            Bitmap? previewFrame = editor.Viewport.CaptureFrame(settleFrames: 5);
            HeadlessHarness.Assert(previewFrame is not null, "Game Camera preview readback failed.");
            SaveCapture(ctx, previewFrame!, "63-room-phase4-game-camera-preview.png",
                "Room Phase 4 — exact Game Camera preview", minColors: 5);
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.Terrain.CreationWizard", () =>
        {
            // Presets are distinct landscapes, not just re-seeds: Mountains must be far more rugged
            // than Flatlands at the same seed.
            float flatRelief = Relief(TerrainGenerator.Generate(new TerrainGenParams { Preset = TerrainPreset.Flatlands, Seed = 42 }));
            float mtnRelief = Relief(TerrainGenerator.Generate(new TerrainGenParams { Preset = TerrainPreset.Mountains, Seed = 42 }));
            HeadlessHarness.Assert(
                mtnRelief > flatRelief * 3f,
                $"Mountains relief ({mtnRelief:0.#}) should dwarf Flatlands ({flatRelief:0.#}).");

            // Same preset, different seed → different terrain.
            TerrainAsset s1 = TerrainGenerator.Generate(new TerrainGenParams { Preset = TerrainPreset.Hills, Seed = 1 });
            TerrainAsset s2 = TerrainGenerator.Generate(new TerrainGenParams { Preset = TerrainPreset.Hills, Seed = 2 });
            HeadlessHarness.Assert(
                !s1.HeightsData.AsSpan().SequenceEqual(s2.HeightsData),
                "Different seeds must produce different terrain.");

            // The Aetherforge geological slice is exposed as reproducible Genesis authoring, not
            // a visual-only filter: every new preset generates, erosion lowers local roughness,
            // and full-strength terraces have a bounded number of strata.
            foreach (TerrainPreset preset in Enum.GetValues<TerrainPreset>())
            {
                TerrainAsset generated = TerrainGenerator.Generate(new TerrainGenParams
                {
                    Preset = preset,
                    Seed = 314,
                    ResolutionX = 65,
                    ResolutionZ = 65,
                });
                HeadlessHarness.Assert(Relief(generated) > 0.01f, $"Terrain preset {preset} is flat/empty.");
            }

            TerrainAsset rugged = TerrainGenerator.Generate(new TerrainGenParams
            {
                Preset = TerrainPreset.Mountains,
                Seed = 51,
                ResolutionX = 65,
                ResolutionZ = 65,
            });
            float roughnessBefore = TerrainRoughness(rugged);
            TerrainGenerator.ApplyThermalErosion(rugged, 12, 0.5f);
            float roughnessAfter = TerrainRoughness(rugged);
            HeadlessHarness.Assert(
                roughnessAfter < roughnessBefore,
                $"Thermal erosion increased local roughness ({roughnessBefore:0.###} → {roughnessAfter:0.###}).");

            TerrainAsset terraced = TerrainGenerator.Generate(new TerrainGenParams
            {
                Preset = TerrainPreset.Hills,
                Seed = 51,
                ResolutionX = 65,
                ResolutionZ = 65,
            });
            TerrainGenerator.ApplyTerracing(terraced, 1f, 8);
            HeadlessHarness.Assert(
                terraced.HeightsData.Distinct().Count() <= 9,
                "Full-strength eight-step terracing produced too many height bands.");
            ushort[] beforeRivers = (ushort[])terraced.HeightsData.Clone();
            TerrainGenerator.CarveRivers(terraced, 51, 2, 6f);
            HeadlessHarness.Assert(
                !beforeRivers.AsSpan().SequenceEqual(terraced.HeightsData),
                "River carving did not alter the terrain.");

            // The wizard drives generation + a live thumbnail.
            string terrain = resources.CreateResource(
                Path.Combine(resources.AssetsRoot, "Terrain"), ResourceKind.Terrain, "SuiteWizardTerrain");
            using (TerrainCreationWizardDialog wizard = new("SuiteWizardTerrain"))
            {
                wizard.SetPreset(TerrainPreset.Islands);
                wizard.SetSeed(7);
                wizard.SetProcesses(6, 0.4f, 0.2f, 2, 5f);
                TerrainAsset preview = wizard.Regenerate();
                HeadlessHarness.Assert(wizard.Params.Preset == TerrainPreset.Islands, "Wizard preset selection did not stick.");
                HeadlessHarness.Assert(preview.ResolutionX == wizard.Params.ResolutionX, "Wizard preview resolution mismatch.");
                HeadlessHarness.Assert(wizard.PreviewAsset is not null, "Wizard did not build a preview asset.");
                HeadlessHarness.Assert(
                    wizard.Params.ErosionIterations == 6 && wizard.Params.RiverCount == 2,
                    "Wizard geological process options did not stick.");
            }

            // The editor applies a wizard generation — including a resolution change — undoably.
            using Form host = NewHost();
            TerrainEditorControl editor = new(terrain, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            Pump(6, 25);

            int beforeRes = editor.Terrain.ResolutionX;
            editor.ApplyGeneration(new TerrainGenParams
            {
                Preset = TerrainPreset.Mountains, Seed = 99,
                ResolutionX = 257, ResolutionZ = 257, CellSize = 1f, MinHeight = -24f, MaxHeight = 72f,
            });
            HeadlessHarness.Assert(editor.Terrain.ResolutionX == 257, "ApplyGeneration did not change the resolution.");
            HeadlessHarness.Assert(editor.IsDirty, "ApplyGeneration must mark the document dirty.");
            HeadlessHarness.Assert(editor.CanUndo, "ApplyGeneration must be undoable.");
            editor.Undo();
            HeadlessHarness.Assert(editor.Terrain.ResolutionX == beforeRes, "Undo did not restore the prior terrain resolution.");
            editor.Redo();
            HeadlessHarness.Assert(editor.Terrain.ResolutionX == 257, "Redo did not re-apply the generated terrain.");

            ushort[] beforeProcess = (ushort[])editor.Terrain.HeightsData.Clone();
            editor.ApplyErosion(4, 0.4f);
            HeadlessHarness.Assert(
                !beforeProcess.AsSpan().SequenceEqual(editor.Terrain.HeightsData),
                "The Terrain Editor Erode action changed no heights.");
            editor.Undo();
            HeadlessHarness.Assert(
                beforeProcess.AsSpan().SequenceEqual(editor.Terrain.HeightsData),
                "Undo did not restore the terrain before erosion.");
            editor.Redo();

            editor.Save();
            HeadlessHarness.Assert(File.Exists(terrain + ".gterrain"), "Wizard terrain did not write its binary sidecar.");

            using (Form reloadHost = NewHost())
            {
                TerrainEditorControl reloaded = new(terrain, project.RootPath);
                reloadHost.Controls.Add(reloaded);
                GateSuite.ShowHost(reloadHost);
                Pump(4, 25);
                HeadlessHarness.Assert(reloaded.Terrain.ResolutionX == 257, "Saved wizard terrain lost its resolution on reload.");
            }

            Bitmap? frame = editor.Viewport.CaptureFrame(settleFrames: 4);
            HeadlessHarness.Assert(frame is not null, "Terrain wizard viewport readback failed.");
            SaveCapture(ctx, frame!, "37-terrain-wizard.png", "Terrain Creation Wizard", minColors: 40);
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.Model.PrimitiveWindingConsistency", () =>
        {
            // Renderer-independent guard for the NEXT-024 / NEXT-031 bug class: every horizontal
            // face must wind consistently across primitives, or it renders solid black. The cube's
            // +Y face is the reference (verified correct on screen); the cylinder's caps must agree.
            (MeshVertex[] cubeV, ushort[] cubeI) = ModelPartBuilder.BuildPrimitive(ModelPrimitiveKind.Cube, RenderColor.White);
            (MeshVertex[] cylV, ushort[] cylI) = ModelPartBuilder.BuildPrimitive(ModelPrimitiveKind.Cylinder, RenderColor.White);

            int cubeUp = HorizontalWindingSign(cubeV, cubeI, up: true);
            int cubeDown = HorizontalWindingSign(cubeV, cubeI, up: false);
            int cylUp = HorizontalWindingSign(cylV, cylI, up: true);
            int cylDown = HorizontalWindingSign(cylV, cylI, up: false);

            HeadlessHarness.Assert(cubeUp != 0, "Cube has no consistent upward-facing winding.");
            HeadlessHarness.Assert(cylUp != 0, "Cylinder top cap winding is inconsistent within itself.");
            HeadlessHarness.Assert(
                cylUp == cubeUp,
                $"Cylinder top cap winds opposite the cube's +Y face (cyl {cylUp}, cube {cubeUp}) — it will render black (NEXT-031).");
            HeadlessHarness.Assert(
                cylDown == cubeDown,
                $"Cylinder bottom cap winds opposite the cube's -Y face (cyl {cylDown}, cube {cubeDown}).");
            HeadlessHarness.Assert(cubeUp == -cubeDown, "A cube's +Y and -Y faces must wind oppositely.");

            // Horizontal caps alone did not catch sphere/torus/side-strip regressions. Every
            // non-degenerate triangle must agree with the normals carried by its three vertices;
            // otherwise one backend culls the face while another happens to display it.
            AssertWindingMatchesNormals("cube", MeshGeometry.BuildCube(RenderColor.White));
            AssertWindingMatchesNormals("sphere", MeshGeometry.BuildSphere(RenderColor.White));
            AssertWindingMatchesNormals("cylinder", MeshGeometry.BuildCylinder(RenderColor.White));
            AssertWindingMatchesNormals("cone", MeshGeometry.BuildCone(RenderColor.White));
            AssertWindingMatchesNormals("pyramid", MeshGeometry.BuildPyramid(RenderColor.White));
            AssertWindingMatchesNormals("torus", MeshGeometry.BuildTorus(RenderColor.White));
            AssertWindingMatchesNormals("floor", MeshGeometry.BuildFloor(RenderColor.White));
            AssertWindingMatchesNormals(
                "checker floor",
                MeshGeometry.BuildCheckerFloor(RenderColor.Black, RenderColor.White));
        });

        // The removed model shell's kitbash/topology/flow controls are intentionally retired.
        // Replacement authoring behaviour is covered by ModelEditorResetSuite.

        HeadlessHarness.RunCase(ctx.Report, "Editor.Shell.PgslCommandReference", () =>
        {
            // Help → Commands. Drives the dialog's own Auto-Test button rather than the engine
            // underneath it, so the button wiring is covered too — the engine already has its own
            // gate in Runtime.Pgsl.CommandAutoTest. The two visual preview paths are also captured
            // independently so a regression cannot make Engine API and PGSL Game Code ambiguous.
            using PgslCommandReferenceForm reference = new();
            GateSuite.ShowHost(reference);
            Pump(6, 25);

            HeadlessHarness.Assert(
                reference.Text == "Commands — PGSL Game Code + Engine API",
                $"Help → Commands window title is '{reference.Text}'.");
            HeadlessHarness.Assert(
                reference.PgslTabCaption == "PGSL Game Code"
                && reference.EngineTabCaption == "Engine API",
                $"Help → Commands tabs are not explicit: '{reference.PgslTabCaption}' / '{reference.EngineTabCaption}'.");
            HeadlessHarness.Assert(
                reference.CatalogueCount > 250,
                $"The PGSL reference lists only {reference.CatalogueCount} commands.");
            HeadlessHarness.Assert(
                reference.EngineCatalogueCount > 50,
                $"The Engine tab lists only {reference.EngineCatalogueCount} commands.");
            HeadlessHarness.Assert(
                reference.ContainsCommand("Engine.Rendering.Models.KeepPreviousTransform")
                && reference.ContainsCommand("AnimationStateSetSpeed")
                && reference.ContainsCommand("AnimationStateHasFinished")
                && reference.ContainsCommand("Engine.Rendering.DrawModelTransform3D")
                && reference.ContainsCommand("Engine.Rendering.DrawModelShader3D")
                && reference.ContainsCommand("Engine.Rendering.DrawPointLight3D"),
                "Help → Commands does not expose the animation/resource-rendering API.");
            HeadlessHarness.Assert(
                reference.VisibleCommandCount == reference.CatalogueCount,
                $"With no filter applied the list shows {reference.VisibleCommandCount} of {reference.CatalogueCount} commands.");

            reference.RunAutoTest();
            Pump(6, 25);

            CaptureForm(ctx, reference, "49-help-pgsl-commands.png", "Help — Commands with auto-test results");

            reference.SelectPgslTab();
            reference.ShowVisualScene();
            Pump(8, 25);
            HeadlessHarness.Assert(
                reference.ActiveCommandPathCaption == "PGSL GAME CODE",
                $"PGSL visual preview badge is '{reference.ActiveCommandPathCaption}'.");
            HeadlessHarness.Assert(
                reference.VisualModeHudLabel == "PGSL GAME CODE"
                && !string.IsNullOrWhiteSpace(reference.VisualRendererLabel),
                $"PGSL visual HUD labels are incomplete: mode '{reference.VisualModeHudLabel}', renderer '{reference.VisualRendererLabel}'.");
            HeadlessHarness.Assert(
                reference.VisualCancelEnabled == false,
                "Visual-test Cancel should be disabled while the preview is idle.");
            HeadlessHarness.Assert(
                reference.VisualCancelButtonCaption == "Cancel",
                $"Visual-test cancellation control caption is '{reference.VisualCancelButtonCaption}'.");
            HeadlessHarness.Assert(
                reference.VisualDemoSubjectCount == 5,
                $"Visual demo reports {reference.VisualDemoSubjectCount} subjects instead of five.");
            HeadlessHarness.Assert(
                reference.VisualDemoResourceCount >= 14,
                $"PGSL visual demo found only {reference.VisualDemoResourceCount} packaged resources.");
            HeadlessHarness.Assert(
                reference.PgslVisualBaselineReady,
                "The real-resource PGSL visual baseline did not compile and execute.");
            Bitmap? pgslVisual = reference.CaptureVisualFrame(settleFrames: 3);
            HeadlessHarness.Assert(pgslVisual is not null, "PGSL Game Code visual preview readback failed.");
            SaveCapture(
                ctx,
                pgslVisual!,
                "49b-help-commands-visual-pgsl.png",
                "Help — Commands PGSL Game Code visual preview",
                minColors: 16);

            reference.SelectEngineTab();
            reference.ShowVisualScene();
            Pump(8, 25);
            HeadlessHarness.Assert(
                reference.ActiveCommandPathCaption == "ENGINE BACKEND / API",
                $"Engine visual preview badge is '{reference.ActiveCommandPathCaption}'.");
            HeadlessHarness.Assert(
                reference.VisualModeHudLabel == "ENGINE API"
                && !string.IsNullOrWhiteSpace(reference.VisualRendererLabel),
                $"Engine visual HUD labels are incomplete: mode '{reference.VisualModeHudLabel}', renderer '{reference.VisualRendererLabel}'.");
            Bitmap? engineVisual = reference.CaptureVisualFrame(settleFrames: 3);
            HeadlessHarness.Assert(engineVisual is not null, "Engine API visual preview readback failed.");
            SaveCapture(
                ctx,
                engineVisual!,
                "49c-help-commands-visual-engine.png",
                "Help — Commands Engine API visual preview",
                minColors: 16);

            // The visual sweep is deliberately cancellable: exercise the same state transition as
            // the toolbar button without waiting for the normal per-backend budget to expire.
            Task cancellation = reference.RunVisualTestAsync(budgetMs: 5_000);
            Pump(4, 20);
            HeadlessHarness.Assert(
                reference.VisualTestRunning && reference.VisualCancelEnabled,
                "Visual-test Cancel did not become enabled while a sweep was running.");
            reference.CancelVisualTest();
            for (int wait = 0; wait < 120 && !cancellation.IsCompleted; wait++)
                Pump(1, 10);
            HeadlessHarness.Assert(cancellation.IsCompleted, "Visual-test cancellation did not complete the sweep.");
            cancellation.GetAwaiter().GetResult();
            HeadlessHarness.Assert(
                !reference.VisualTestRunning && !reference.VisualCancelEnabled,
                "Visual-test controls were not restored after cancellation.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.Acceptance.TwoDGameEndToEnd", () =>
        {
            // ACCEPTANCE TARGET #1: author a whole 2D game in the editors, then run it through the
            // runtime's OWN scene builder and script host — not a simulation of them — and assert it
            // actually plays. Everything here is loaded from the files the editors saved, because
            // that is the seam where things break (see NEXT-041, NEXT-044): an editor can look
            // perfect while the game reads its output differently, or not at all.
            string audioFolder = Path.Combine(resources.AssetsRoot, "Audio");
            string objectsFolder3 = Path.Combine(resources.AssetsRoot, "Objects");
            Directory.CreateDirectory(audioFolder);

            // ── 1. A sound effect, authored in the Audio Editor ──────────────────────
            string pickupWav = Path.Combine(audioFolder, "GamePickup.wav");
            WriteSineWav(pickupWav, seconds: 0.2, frequency: 880.0);
            string pickupSound = resources.CreateResource(audioFolder, ResourceKind.Audio, "GamePickup");
            using (Form audioHost = NewHost())
            {
                AudioEditorControl audioEditor = new(pickupSound, project.RootPath);
                audioHost.Controls.Add(audioEditor);
                GateSuite.ShowHost(audioHost);
                Pump(4, 20);
                HeadlessHarness.Assert(
                    audioEditor.SelectSource("Assets/Audio/GamePickup.wav"),
                    "Audio Editor could not bind the pickup WAV.");
                audioEditor.SetVolume(0.8f);
                audioEditor.Save();
            }

            // ── 2. The player object, scripted in the Object Editor ──────────────────
            // Create runs ONCE and parks the player at x=100; Update advances it every frame.
            // That makes the two failure modes observable purely from the transform:
            //   correct      → x == 100 + frames
            //   Create-as-Update → x pinned near 101 (Create resets x every frame)
            string playerObject = resources.CreateResource(objectsFolder3, ResourceKind.GameObject, "GamePlayer");
            using (Form objectHost = NewHost())
            {
                ObjectEditorControl objectEditor = new(playerObject, project.RootPath);
                objectHost.Controls.Add(objectEditor);
                GateSuite.ShowHost(objectHost);
                Pump(4, 20);
                objectEditor.SetEventBody("Create", "x = 100; y = 200; PlaySound(\"Assets/Audio/GamePickup.wav\", 0.8, 1, false);");
                objectEditor.SetEventBody("Step", "x = x + 1;");
                objectEditor.SetEventBody("DrawGui", "DrawText(16, 12, \"SCORE\");");
                objectEditor.Save();
            }

            // ── 3. The level, assembled in the Room Editor ───────────────────────────
            string gameTiles = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, "GameTiles");
            string gameRoom = resources.CreateResource(roomsFolder, ResourceKind.Room, "AcceptTwoDGame");
            using (Form roomHost = NewHost())
            {
                RoomEditorControl gameLevel = new(gameRoom, project.RootPath);
                roomHost.Controls.Add(gameLevel);
                GateSuite.ShowHost(roomHost);
                Pump(8, 25);

                gameLevel.ViewMode3D = false;
                Pump(2, 25);

                EnableTileSet(gameTiles);
                gameLevel.BeginTilePainting(gameTiles);
                for (int column = 0; column < 8; column++)
                {
                    gameLevel.PaintTileAtWorld(column * 32f, 320f);
                }

                gameLevel.BeginPlacement(playerObject);
                Point spawn = TranslateToViewport(gameLevel, new Point(gameLevel.Viewport.Width / 3, gameLevel.Viewport.Height / 2));
                gameLevel.EditorPointerDown(spawn, MouseButtons.Left, Keys.None);
                gameLevel.EditorPointerUp(spawn, MouseButtons.Left, Keys.None);

                gameLevel.Save();

                HeadlessHarness.Assert(
                    gameLevel.Room.Nodes.Any(n => n.Kind == RoomNodeKind.TileLayer),
                    "The authored level has no tile layer.");
                HeadlessHarness.Assert(
                    gameLevel.Room.Nodes.Any(n => n.Kind == RoomNodeKind.GameObject),
                    "The player was not placed in the level.");

                CaptureForm(ctx, roomHost, "48-acceptance-2d-level.png", "Acceptance — the authored 2D level");
            }

            // ── 4. Run it, exactly as the shipped game would ─────────────────────────
            RecordingHudCanvas hud = new();
            AcceptanceAudioRecorder audio = new();
            NullGameContext gameContext = new() { Audio = audio };
            IGameContext previousContext = PgslCommands.ActiveGameContext;
            string? previousProject = PgslCommands.ProjectPath;

            PgslCommands.ActiveGameContext = gameContext;
            PgslCommands.ProjectPath = project.RootPath;
            try
            {
                VMEngine.Initialize();
                ScriptAssetRegistry.ClearCache();
                ScriptAssetRegistry.LoadFromProject(project.RootPath);

                // T2: event code now travels with the object rather than through the global script
                // registry, so the seam to check is the object's own folder. There is no shared name
                // left to mis-resolve, which is what made NEXT-044/046 possible.
                IReadOnlyDictionary<string, string> playerEvents = ObjectEventStore.Load(playerObject);
                foreach (string id in new[] { "Create", "Step", "DrawGui" })
                {
                    HeadlessHarness.Assert(
                        playerEvents.ContainsKey(id),
                        $"The object's '{id}' event is missing from {ObjectEventStore.FolderFor(playerObject)}.");
                }

                HeadlessHarness.Assert(
                    !ScriptAssetRegistry.TryGet("Create", out _),
                    "Event scripts must stay out of the global script library.");

                ScriptHostSystem scriptHost = new();
                scriptHost.SetContext(gameContext);

                EcsWorld world = new();
                RoomAsset level = RoomAssetLoader.Parse(gameRoom);
                RoomSceneBuilder builder = new(project.RootPath, scriptHost);
                RoomBuildResult built = builder.Build(world, level);

                HeadlessHarness.Assert(
                    built.SpawnedEntities.Count > 8,
                    $"The level spawned only {built.SpawnedEntities.Count} entities — the tilemap did not become game objects.");

                Entity player = built.EntitiesByNodeId.Values.First();
                HeadlessHarness.Assert(
                    world.Has<TransformComponent>(player),
                    "The spawned player has no transform.");

                // Create should already have fired during Build (PrefabSpawner → Attach → OnCreate).
                float spawnX = world.GetRef<TransformComponent>(player).X;
                HeadlessHarness.Assert(
                    Math.Abs(spawnX - 100f) < 0.01f,
                    $"The Create event did not run: player sits at x={spawnX:0.##}, expected the scripted x=100.");
                HeadlessHarness.Assert(
                    audio.Plays.Count == 1,
                    $"Create played the pickup sound {audio.Plays.Count} times, expected exactly 1.");

                const int frames = 60;
                for (int frame = 0; frame < frames; frame++)
                {
                    scriptHost.Update(1f / 60f);
                }

                float playedX = world.GetRef<TransformComponent>(player).X;
                HeadlessHarness.Assert(
                    Math.Abs(playedX - (100f + frames)) < 0.51f,
                    $"After {frames} frames the player is at x={playedX:0.##}, expected {100 + frames}. "
                    + "If it is pinned near 101 the Create event is being run every frame as Update (NEXT-044).");
                HeadlessHarness.Assert(
                    audio.Plays.Count == 1,
                    $"The one-shot Create sound fired {audio.Plays.Count} times across {frames} frames — Create is re-running.");

                // …and the HUD the designer authored in DrawGui must reach the screen. World Draw
                // and screen-space DrawGui are deliberately separate runtime passes.
                scriptHost.DispatchPgslDraw(renderer: null, hud);
                HeadlessHarness.Assert(
                    hud.Texts.Count == 1,
                    $"The DrawGui event produced {hud.Texts.Count} HUD text calls, expected 1 — "
                    + "a DrawGui script bound under the wrong name never registers for the GUI pass.");
                HeadlessHarness.Assert(
                    hud.Texts[0].Text == "SCORE" && hud.Texts[0].X == 16f && hud.Texts[0].Y == 12f,
                    $"HUD drew '{hud.Texts[0].Text}' at ({hud.Texts[0].X},{hud.Texts[0].Y}), expected 'SCORE' at (16,12).");
            }
            finally
            {
                PgslCommands.ActiveGameContext = previousContext;
                PgslCommands.ProjectPath = previousProject;
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.Acceptance.WaterfallModelInTerrainScene", () =>
        {
            // ACCEPTANCE TARGET #2: a 3D terrain scene containing a waterfall that was modelled in
            // the Model Editor. Proves the whole chain: author a model → place it in a 3D room
            // alongside terrain → the room renders its REAL geometry (not a placeholder box).
            string modelsFolder = Path.Combine(resources.AssetsRoot, "Models");
            string objectsFolder2 = Path.Combine(resources.AssetsRoot, "Objects");
            string terrainFolder = Path.Combine(resources.AssetsRoot, "Terrain");
            Directory.CreateDirectory(modelsFolder);
            Directory.CreateDirectory(terrainFolder);

            // 1. Model the waterfall in the Model Editor and save it.
            string waterfallModel = resources.CreateResource(modelsFolder, ResourceKind.Model, "AcceptWaterfall");
            using (Form modelHost = NewHost())
            {
                ModelEditorControl modelEditor = new(waterfallModel, project.RootPath);
                modelHost.Controls.Add(modelEditor);
                GateSuite.ShowHost(modelHost);
                Pump(6, 25);
                while (modelEditor.Parts.Count > 0)
                {
                    modelEditor.SelectPart(0);
                    modelEditor.DeleteSelectedPartForTest();
                }

                ModelPart fallSheet = modelEditor.AddPart(ModelPrimitiveKind.Cube);
                fallSheet.Name = "Falling sheet";
                fallSheet.Position = [0f, 4f, 0f];
                fallSheet.Scale = [3f, 8f, 0.4f];
                fallSheet.Color = [0.35f, 0.62f, 0.86f];

                ModelPart lip = modelEditor.AddPart(ModelPrimitiveKind.Cube);
                lip.Name = "Rock lip";
                lip.Position = [0f, 8.2f, -0.5f];
                lip.Scale = [4f, 0.8f, 1.6f];
                lip.Color = [0.42f, 0.40f, 0.38f];

                ModelPart pool = modelEditor.AddPart(ModelPrimitiveKind.Cylinder);
                pool.Name = "Plunge pool";
                pool.Position = [0f, 0.25f, 0.8f];
                pool.Scale = [6f, 0.5f, 6f];
                pool.Color = [0.28f, 0.55f, 0.75f];

                modelEditor.ApplyFixtureParts();
                // The waterfall must FLOW: an animated-UV material saved with the model, so it
                // keeps flowing wherever the model is placed — not just in this editor's preview.
                modelEditor.ApplyFixtureFlow(new Vector2(0f, -0.7f));
                modelEditor.Save();
                HeadlessHarness.Assert(modelEditor.BakedVertexCount > 0, "Waterfall model baked no geometry.");
                HeadlessHarness.Assert(
                    File.ReadAllText(waterfallModel).Contains("flowEnabled", StringComparison.OrdinalIgnoreCase),
                    "Flow material was not persisted with the model.");
            }

            // 2. An Object that renders that model.
            string waterfallObject = resources.CreateResource(objectsFolder2, ResourceKind.GameObject, "AcceptWaterfallObject");
            string modelReference = Path.GetRelativePath(project.RootPath, waterfallModel).Replace('\\', '/');
            using (ObjectEditorControl objectEditor = new(waterfallObject, project.RootPath))
            {
                using ObjectCompositionDialog composition = new(objectEditor.Document, project.RootPath);
                composition.Composition.SetAsset("ModelRendererComponent", modelReference);
                objectEditor.ApplyComposition(composition.Document);
                objectEditor.Save();
            }

            // 3. A terrain for the scene to stand on.
            string sceneTerrain = resources.CreateResource(terrainFolder, ResourceKind.Terrain, "AcceptTerrain");
            using (Form terrainHost = NewHost())
            {
                TerrainEditorControl terrainEditor = new(sceneTerrain, project.RootPath);
                terrainHost.Controls.Add(terrainEditor);
                GateSuite.ShowHost(terrainHost);
                Pump(6, 25);
                terrainEditor.ApplyGeneration(new TerrainGenParams
                {
                    Preset = TerrainPreset.Hills, Seed = 2024,
                    ResolutionX = 129, ResolutionZ = 129, CellSize = 1f, MinHeight = -24f, MaxHeight = 72f,
                });
                terrainEditor.Save();
            }

            // 4. Build the 3D scene: terrain + the waterfall model placed on it.
            string scene = resources.CreateResource(roomsFolder, ResourceKind.Room, "AcceptWaterfallScene");
            using Form host = NewHost();
            RoomEditorControl room = new(scene, project.RootPath);
            host.Controls.Add(room);
            GateSuite.ShowHost(host);
            Pump(10, 30);

            room.ViewMode3D = true;
            Pump(4, 25);
            room.BeginPlacementTerrain(sceneTerrain);
            Point centre = TranslateToViewport(room, new Point(room.Viewport.Width / 2, (int)(room.Viewport.Height * 0.62f)));
            room.EditorPointerDown(centre, MouseButtons.Left, Keys.None);
            room.EditorPointerUp(centre, MouseButtons.Left, Keys.None);

            room.BeginPlacement(waterfallObject);
            room.EditorPointerDown(centre, MouseButtons.Left, Keys.None);
            room.EditorPointerUp(centre, MouseButtons.Left, Keys.None);

            HeadlessHarness.Assert(
                room.Room.Nodes.Count(n => n.Kind == RoomNodeKind.Terrain) == 1,
                "Scene is missing its terrain.");
            RoomNode? placedModel = room.Room.Nodes.LastOrDefault(n => n.Kind == RoomNodeKind.GameObject);
            HeadlessHarness.Assert(placedModel is not null, "Waterfall object was not placed in the scene.");

            // The model must load as real geometry — a placeholder box would mean the scene shows a
            // grey cube instead of the authored waterfall.
            (MeshVertex[] loadedVerts, ushort[] loadedIndices) = ModelAssetLoader.LoadBaked(waterfallModel);
            HeadlessHarness.Assert(
                loadedVerts.Length > 24 && loadedIndices.Length > 36,
                $"Placed model resolved to placeholder-sized geometry ({loadedVerts.Length} verts) — the Room Editor would draw a box.");

            // Seat the waterfall on the terrain surface and frame the shot on it — placement drops
            // the node at the ray/ground-plane hit, which isn't the terrain's actual height.
            // Terrain is itself a placed/transformed node. Sampling world X/Z directly from its
            // local heightmap can bury the model; use the same End command a designer uses.
            placedModel!.Transform.Y = 100f;
            room.Select(placedModel);
            HeadlessHarness.Assert(room.SnapSelectionToFloor(), "The waterfall could not be seated on the scene terrain.");

            room.Save();
            room.SetGizmo(RoomEditorControl.GizmoKind.Move);
            room.Viewport.Camera.Target = new Vector3(
                placedModel.Transform.X, placedModel.Transform.Y + 4.5f, placedModel.Transform.Z);
            // Frame the authored subject independently of the editor sidebar widths.
            room.Viewport.Camera.Distance = 18f;
            Pump(6, 25);
            room.SetModelPreviewTime(0f);
            Pump(3, 25);
            Bitmap? sceneFrame = room.Viewport.CaptureFrame(settleFrames: 6);
            HeadlessHarness.Assert(sceneFrame is not null, "Terrain scene readback failed.");

            // …and it must actually FLOW in the scene: same geometry, later time, different pixels.
            room.SetModelPreviewTime(1.1f);
            Pump(3, 25);
            Bitmap? flowedFrame = room.Viewport.CaptureFrame(settleFrames: 6);
            HeadlessHarness.Assert(flowedFrame is not null, "Flowed scene readback failed.");

            // Compare before saving — SaveCapture disposes the bitmap it is handed.
            int sceneFlowDelta = CountDifferingPixels(sceneFrame!, flowedFrame!);
            sceneFrame!.Save(Path.Combine(captures, "waterfall-flow-before.png"), ImageFormat.Png);
            flowedFrame!.Save(Path.Combine(captures, "waterfall-flow-after.png"), ImageFormat.Png);
            HeadlessHarness.Assert(
                sceneFlowDelta > 250,
                $"The placed waterfall is not flowing in the scene (only {sceneFlowDelta} pixels changed).");

            SaveCapture(ctx, sceneFrame!, "46-acceptance-waterfall-scene.png",
                "Acceptance — waterfall model in a 3D terrain scene", minColors: 40);
            SaveCapture(ctx, flowedFrame!, "47-acceptance-waterfall-flowing.png",
                "Acceptance — the placed waterfall flowing", minColors: 40);
        });

        string terrainEntitiesFolder = Path.Combine(resources.AssetsRoot, "TerrainEntities");
        Directory.CreateDirectory(terrainEntitiesFolder);

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.TerrainEntity.WizardCreateComponentsAndGenerateAsset", () =>
        {
            string entityPath = Path.Combine(terrainEntitiesFolder, "SuiteFern.terrainentity.json");
            File.WriteAllText(entityPath, ResourceDefinitions.Get(ResourceKind.TerrainEntity).DefaultContent);

            using TerrainEntityWizardDialog wizard = new(entityPath, project.RootPath, TerrainEntityType.Foliage);
            GateSuite.ShowHost(wizard);
            Pump(4, 20);

            wizard.SetName("Suite Fern");
            wizard.SetType(TerrainEntityType.Foliage);
            HeadlessHarness.Assert(wizard.EntityType == TerrainEntityType.Foliage, "Type card selection did not stick.");

            wizard.GoToPage(2);
            Pump(2, 20);
            wizard.AddComponent(TerrainEntityComponentKinds.Physics);
            HeadlessHarness.Assert(wizard.Components.Count == 1, "Add Component did not add a Physics component.");
            HeadlessHarness.Assert(wizard.Components[0].Get("Shape") == "Box", "Physics component defaults were not applied.");

            wizard.AddComponent(TerrainEntityComponentKinds.Texture);
            HeadlessHarness.Assert(wizard.Components.Count == 2, "Add Component did not add a Texture component.");
            Pump(2, 20);

            wizard.AddComponent(TerrainEntityComponentKinds.Model);
            wizard.SetComponentProperty(wizard.Components.Count - 1, "AnimationClip", "Spin");
            wizard.AddComponent(TerrainEntityComponentKinds.Condition);
            int condition = wizard.Components.Count - 1;
            wizard.SetComponentProperty(condition, "If", "Wind > 0.2");
            wizard.SetComponentProperty(condition, "ThenSource", TerrainVisualCondition.PlayAnimationSource("Spin"));
            wizard.SetComponentProperty(condition, "ElseSource", TerrainVisualCondition.PlayAnimationSource("Idle"));
            HeadlessHarness.Assert(
                wizard.Components[^1].Type == TerrainEntityComponentKinds.Condition
                && wizard.Components[^1].Get("ThenClip") == "Spin"
                && wizard.Components[^1].Get("ThenSource").Contains("AnimationPlay", StringComparison.Ordinal)
                && wizard.Components.Any(component =>
                    component.Type == TerrainEntityComponentKinds.Model
                    && component.Get("AnimationClip") == "Spin"),
                "Model animation clip and Condition visual If/Then/Else did not stick on the wizard.");
            Pump(2, 20);

            // In-wizard live asset generation: click the Texture component's real "New…" button,
            // confirm the wizard body swapped to a live Image Editor, "Done" it, and confirm the
            // component captured the newly generated sprite's reference — all without the wizard
            // ever closing (per spec: "should not require leaving it").
            Button? newTextureButton = FindDescendant<Button>(
                wizard.ContentHost,
                b => b.Name == "TerrainEntityTextureNewButton");
            HeadlessHarness.Assert(newTextureButton is not null, "Texture component's New… button not found.");
            newTextureButton!.PerformClick();
            Pump(4, 25);
            HeadlessHarness.Assert(
                FindDescendant<ImageEditorControl>(wizard.ContentHost) is not null,
                "New… did not swap the wizard body to a live Image Editor.");

            Button? doneButton = FindDescendant<Button>(wizard.ContentHost, b => b.Text == "Done");
            HeadlessHarness.Assert(doneButton is not null, "Done button missing from the in-wizard asset editor.");
            doneButton!.PerformClick();
            Pump(2, 20);
            HeadlessHarness.Assert(
                !string.IsNullOrWhiteSpace(wizard.Components[1].Get("Texture")),
                "Texture component did not capture the newly generated sprite's reference.");
            HeadlessHarness.Assert(
                wizard.Components.Any(component =>
                    component.Type == TerrainEntityComponentKinds.Model
                    && component.Get("AnimationClip") == "Spin"),
                "Saving did not keep the Model AnimationClip.");

            wizard.SaveAndCloseForTest();
            HeadlessHarness.Assert(File.Exists(entityPath), "Wizard save did not write the terrain entity JSON.");
            HeadlessHarness.Assert(File.ReadAllText(entityPath).Contains("Suite Fern"), "Saved terrain entity JSON lost the name.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.TerrainEntity.ListPanelGroupingEditDelete", () =>
        {
            string rockPath = Path.Combine(terrainEntitiesFolder, "SuiteRock.terrainentity.json");
            File.WriteAllText(rockPath, ResourceDefinitions.Get(ResourceKind.TerrainEntity).DefaultContent);
            File.WriteAllText(
                rockPath,
                """{"schemaVersion":1,"name":"Suite Rock","type":"Object","icon":"","components":[]}""");

            using Form host = NewHost();
            TerrainEntityListPanel panel = new(project.RootPath) { Dock = DockStyle.Fill };
            host.Controls.Add(panel);
            GateSuite.ShowHost(host);
            Pump(8, 25);

            TerrainEntityType? createRequested = null;
            panel.CreateRequested += (_, type) => createRequested = type;
            string? editRequested = null;
            panel.EditRequested += (_, path) => editRequested = path;

            // Every one of the 5 typed sections rendered, including the Foliage/Object entries
            // created by the fixtures above (grouping is the panel's whole reason to exist).
            // Counts aren't asserted exactly — the shared project fixture already seeds one
            // "Sample Terrain Entity" (Foliage) elsewhere, so only each row's presence is checked.
            HeadlessHarness.Assert(
                FindDescendant<Label>(panel, l => l.Text.StartsWith("FOLIAGE", StringComparison.Ordinal)) is not null,
                "Foliage group header not found.");
            HeadlessHarness.Assert(
                FindDescendant<Label>(panel, l => l.Text == "Suite Fern") is not null,
                "Foliage group should list the wizard-created 'Suite Fern'.");
            HeadlessHarness.Assert(
                FindDescendant<Label>(panel, l => l.Text.StartsWith("OBJECT", StringComparison.Ordinal)) is not null,
                "Object group header not found.");
            HeadlessHarness.Assert(
                FindDescendant<Label>(panel, l => l.Text == "Suite Rock") is not null,
                "Object group should list 'Suite Rock'.");
            HeadlessHarness.Assert(
                FindDescendant<Label>(panel, l => l.Text.StartsWith("FLUID", StringComparison.Ordinal)) is not null,
                "Fluid group header should render even with zero entries.");

            // Group header [+] raises CreateRequested with that group's type.
            Panel? objectGroupHeader = FindDescendant<Panel>(panel, p => p.Tag is TerrainEntityType.Object);
            HeadlessHarness.Assert(objectGroupHeader is not null, "Object group header panel not found.");
            Button? objectPlus = FindDescendant<Button>(objectGroupHeader!, b => b.Text == "+");
            HeadlessHarness.Assert(objectPlus is not null, "Object group [+] button not found.");
            objectPlus!.PerformClick();
            HeadlessHarness.Assert(createRequested == TerrainEntityType.Object, "Group [+] did not raise CreateRequested(Object).");

            // Row Edit raises EditRequested with that row's resource path.
            Label? rockLabel = FindDescendant<Label>(panel, l => l.Text == "Suite Rock");
            HeadlessHarness.Assert(rockLabel is not null, "Suite Rock row label not found.");
            Button? editButton = FindDescendant<Button>(rockLabel!.Parent!, b => b.Text == "✎");
            HeadlessHarness.Assert(editButton is not null, "Edit button for Suite Rock row not found.");
            editButton!.PerformClick();
            HeadlessHarness.Assert(
                string.Equals(editRequested, rockPath, StringComparison.OrdinalIgnoreCase),
                "Edit did not raise EditRequested with the row's resource path.");

            GateSuite.ShowHost(host);
            WinFormsApplication.DoEvents();
            ImageMetrics metrics = VisualCapture.Capture(host, Path.Combine(captures, "35-terrain-entity-list.png"));
            ctx.Report.Images.Add(ImageResult.From("Terrain Entity Library", "35-terrain-entity-list.png", metrics));
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.Script.DiagnosticsLive", () =>
        {
            string script = resources.CreateResource(
                Path.Combine(resources.AssetsRoot, "Scripts"), ResourceKind.PgslScript, "SuiteScript");

            using Form host = NewHost();
            PgslScriptEditorControl editor = new(script, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            Pump(6, 25);

            editor.ValidateNow();
            HeadlessHarness.Assert(editor.ErrorCount == 0, "Template PGSL script should validate cleanly.");

            editor.ScriptText = "event Create { this is not pgsl ((((";
            editor.ValidateNow();
            HeadlessHarness.Assert(editor.ErrorCount > 0, "Broken PGSL must produce diagnostics.");

            editor.ScriptText = "event Create {\n    x = 32\n    y = 48\n}\n";
            editor.ValidateNow();
            HeadlessHarness.Assert(editor.ErrorCount == 0, $"Valid PGSL flagged {editor.ErrorCount} error(s).");
            editor.Save();

            CaptureForm(ctx, host, "23-pgsl-editor.png", "PGSL Editor");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.Object.EventsExportForPlay", () =>
        {
            using Form host = NewHost();
            ObjectEditorControl editor = new(heroObject, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            Pump(6, 25);

            // T2: the GMS-style event model. ~35 events across 8 categories, code stored per-event
            // inside the object's own folder, and the folder is the source of truth.
            HeadlessHarness.Assert(
                ObjectEventCatalog.All.Count >= 35 && ObjectEventCatalog.Categories.Count == 8,
                $"Expected ~35 events over 8 categories, got {ObjectEventCatalog.All.Count} over "
                + $"{ObjectEventCatalog.Categories.Count}.");
            HeadlessHarness.Assert(
                ObjectEventCatalog.Exists("DrawGui") && ObjectEventCatalog.Exists("Alarm0")
                && ObjectEventCatalog.Exists("UserEvent0") && ObjectEventCatalog.Exists("RoomStart"),
                "The catalogue is missing events the GMS model requires (Draw GUI, Alarms, User Events, Room Start).");

            editor.SetEventBody("Create", "x = 64;\ny = 64;\nSetAlarm(0, 5);\n");
            editor.SetEventBody("Step", "x = x + 1;\n");
            editor.SetEventBody("DrawGui", "DrawTextScaled(16, 12, \"HUD\", 18);\n");
            editor.Save();

            string stem = editor.ObjectStem;
            string folder = editor.EventFolder;
            HeadlessHarness.Assert(
                Directory.Exists(folder) && Path.GetFileName(folder) == stem,
                $"Event scripts must live in a folder named after the object; got '{folder}'.");
            foreach (string id in new[] { "Create", "Step", "DrawGui" })
            {
                HeadlessHarness.Assert(
                    File.Exists(Path.Combine(folder, id + ".pgsl")),
                    $"Event '{id}' was not written to {stem}/{id}.pgsl.");
            }

            // Removing an event deletes its file — the folder cannot drift from the editor.
            editor.SetEventBody("Destroy", "// bye\n");
            editor.Save();
            HeadlessHarness.Assert(File.Exists(Path.Combine(folder, "Destroy.pgsl")), "Destroy event was not written.");
            editor.RemoveEvent("Destroy");
            editor.Save();
            HeadlessHarness.Assert(
                !File.Exists(Path.Combine(folder, "Destroy.pgsl")),
                "Removing an event must delete its script file, or the folder drifts from the editor.");

            // Reloading reads the folder, not the document — proving the folder really is authoritative.
            using (Form reopenHost = NewHost())
            {
                ObjectEditorControl reopened = new(heroObject, project.RootPath);
                reopenHost.Controls.Add(reopened);
                GateSuite.ShowHost(reopenHost);
                Pump(4, 20);
                HeadlessHarness.Assert(
                    reopened.ActiveEvents.Count == 3,
                    $"Reopened object shows {reopened.ActiveEvents.Count} events, expected 3 from the folder.");
                HeadlessHarness.Assert(
                    reopened.PgslEvents["Step"].Contains("x + 1", StringComparison.Ordinal),
                    "Reopened Step event lost its code.");
            }

            // Event files must NOT enter the global script registry: they are named by event, so two
            // objects both handling Create would collide on the shared name — and the C# transpiler,
            // which derives a class per file, would emit two identically-named classes.
            ScriptAssetRegistry.ClearCache();
            ScriptAssetRegistry.LoadFromProject(project.RootPath);
            HeadlessHarness.Assert(
                !ScriptAssetRegistry.TryGet("Create", out _) && !ScriptAssetRegistry.TryGet("Step", out _),
                "Object event scripts leaked into the global script library — two objects with a Create "
                + "event would collide, and the transpiler would emit duplicate class names.");

            // One ScriptComponent, naming the object, purely so the runtime attaches a behaviour.
            JObject saved = JObject.Parse(File.ReadAllText(heroObject));
            JArray components = (JArray)saved["components"]!;
            List<JObject> scriptComponents = components.OfType<JObject>()
                .Where(component => string.Equals((string?)component["type"], "ScriptComponent", StringComparison.OrdinalIgnoreCase))
                .ToList();
            HeadlessHarness.Assert(
                scriptComponents.Count == 1,
                $"Expected exactly one ScriptComponent trigger, found {scriptComponents.Count}.");
            HeadlessHarness.Assert(
                string.Equals((string?)(scriptComponents[0]["props"] as JObject)?["ScriptClass"], stem, StringComparison.OrdinalIgnoreCase),
                "The ScriptComponent must name the object, not an event.");

            // The built-in sandbox: run the object's events on a real VM, no room and no player.
            ObjectSandboxResult? sandbox = editor.RunSandbox();
            HeadlessHarness.Assert(sandbox is not null, "The sandbox produced no result.");
            HeadlessHarness.Assert(sandbox!.Ok, $"Sandbox reported errors: {string.Join("; ", sandbox.Errors.Select(e => e.Message))}");
            HeadlessHarness.Assert(
                Math.Abs(sandbox.X - (64 + sandbox.FramesRun)) < 0.51,
                $"Sandbox ran Create (x=64) then {sandbox.FramesRun} Step frames, so x should be "
                + $"{64 + sandbox.FramesRun}; got {sandbox.X:0.##}.");
            HeadlessHarness.Assert(
                sandbox.EventsFired.Count(id => id == "Create") == 1,
                "Create must fire exactly once in the sandbox, not every frame.");
            HeadlessHarness.Assert(
                editor.Sandbox.LastDrawCallCount >= sandbox.FramesRun,
                $"The DrawGui event should emit a call per frame; recorded {editor.Sandbox.LastDrawCallCount}.");

            // ── Object properties: what code cannot infer ────────────────────────
            editor.SetPhysicsPreset("PlatformerCharacter");
            HeadlessHarness.Assert(
                editor.PhysicsPreset == "PlatformerCharacter",
                "Ticking 'uses physics' and choosing a preset did not stick.");
            editor.Document["depth"] = -25;
            editor.Document["dimension"] = "TwoD";
            editor.Save();

            JObject withProperties = JObject.Parse(File.ReadAllText(heroObject));
            HeadlessHarness.Assert(
                (string?)withProperties["physics"] == "PlatformerCharacter" && (int?)withProperties["depth"] == -25,
                "Object properties (physics preset, depth) did not persist.");
            HeadlessHarness.Assert(!editor.IsThreeD, "A TwoD object reported itself as 3D.");

            // ── Add Event wizard ─────────────────────────────────────────────────
            HeadlessHarness.Assert(
                editor.AddEventViaWizard("RoomStart"),
                "The Add Event wizard did not add the chosen event.");
            HeadlessHarness.Assert(
                editor.ActiveEvents.Contains("RoomStart") && editor.ActiveEvent == "RoomStart",
                "Adding an event should create it and select it.");
            HeadlessHarness.Assert(
                !editor.AddEventViaWizard("RoomStart"),
                "The wizard offered an event the object already has.");
            editor.RemoveEvent("RoomStart");

            // ── Test Event / Test Object ─────────────────────────────────────────
            editor.SelectEvent("Step");
            HeadlessHarness.Assert(editor.TestEvent(), "Test Event failed on a valid event.");

            // A reference the project cannot satisfy must be caught, not just bad syntax — a clean
            // event that plays a missing sound still breaks at run time.
            editor.SetEventBody("Step", "x = x + 1;\nPlaySound(\"Assets/Audio/DoesNotExist.wav\", 1, 1, false);\n");
            HeadlessHarness.Assert(
                !editor.TestEvent(),
                "Test Event passed an event referencing an asset that does not exist.");
            editor.SetEventBody("Step", "x = x + 1;\n");
            HeadlessHarness.Assert(editor.TestEvent(), "Test Event still fails after removing the bad reference.");

            ObjectSandboxResult? tested = editor.TestObject();
            HeadlessHarness.Assert(tested is not null && tested.Ok, "Test Object failed on a valid object.");

            // Leave a populated event on screen: the capture should show the editor doing its job,
            // not the blank pane left behind by the remove-event check above.
            editor.SelectEvent("Create");
            HeadlessHarness.Assert(editor.ActiveEvent == "Create", "Selecting an event did not take effect.");
            Pump(4, 20);
            CaptureForm(ctx, host, "24-object-editor.png", "Object Editor — events, code and sandbox");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.Assets.TileSetAudioShaderPhysics", () =>
        {
            string tileset = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, "SuiteTiles");
            string audio = resources.CreateResource(Path.Combine(resources.AssetsRoot, "Audio"), ResourceKind.Audio, "SuiteAudio");
            string shader = resources.CreateResource(Path.Combine(resources.AssetsRoot, "Shaders"), ResourceKind.Shader, "SuiteShader");
            string physics = resources.CreateResource(Path.Combine(resources.AssetsRoot, "Physics"), ResourceKind.Physics, "SuitePhysics");

            // T1: there is no Tile Set resource or editor any more. Tile geometry and collision live
            // in the Image document's usage profile, and TileSetInfo is the single reader the Room
            // Editor and the runtime both go through. This asserts that contract directly.
            {
                ImageDocument tileDocument = ImageDocumentSerializer.Deserialize(File.ReadAllText(tileset)).Document;

                HeadlessHarness.Assert(
                    TileSetInfo.Load(tileset) is null,
                    "An Image not enabled for Tileset use must not resolve as a tile set — the usage flags would be decoration.");

                tileDocument.Usage.Allowed |= ImageUsage.Tileset;
                tileDocument.Usage.Tileset.TileWidth = 32;
                tileDocument.Usage.Tileset.TileHeight = 32;
                tileDocument.Usage.Tileset.Collision.Add(3);
                File.WriteAllText(tileset, ImageDocumentSerializer.Serialize(tileDocument));

                TileSetInfo? tiles = TileSetInfo.Load(tileset);
                HeadlessHarness.Assert(tiles is not null, "An Image enabled for Tileset use did not resolve as a tile set.");
                HeadlessHarness.Assert(
                    tiles!.TileWidth == 32 && tiles.TileHeight == 32,
                    $"Tile geometry did not round-trip: got {tiles.TileWidth}x{tiles.TileHeight}, expected 32x32.");
                HeadlessHarness.Assert(tiles.IsSolid(3) && !tiles.IsSolid(4), "Tile collision flags did not round-trip.");
                HeadlessHarness.Assert(
                    tiles.ColumnsFor(128) == 4 && tiles.TileRect(5, 128) == new Rectangle(32, 32, 32, 32),
                    "Tile grid arithmetic is wrong for a 128px sheet of 32px tiles.");

                // The same image can be a model texture at the same time — the whole point of T1.
                tileDocument.Usage.Allowed |= ImageUsage.Texture;
                tileDocument.Usage.Material.Roughness = 0.25;
                File.WriteAllText(tileset, ImageDocumentSerializer.Serialize(tileDocument));
                ImageDocument reloaded = ImageDocumentSerializer.Deserialize(File.ReadAllText(tileset)).Document;
                HeadlessHarness.Assert(
                    reloaded.Usage.Supports(ImageUsage.Tileset) && reloaded.Usage.Supports(ImageUsage.Texture),
                    "An image must be able to be a tile set AND a model texture at once.");
                HeadlessHarness.Assert(
                    Math.Abs(reloaded.Usage.Material.Roughness - 0.25) < 0.001,
                    "Material settings folded into the Image document did not round-trip.");
            }

            using (Form host = NewHost())
            {
                AudioEditorControl audioEditor = new(audio, project.RootPath);
                host.Controls.Add(audioEditor);
                GateSuite.ShowHost(host);
                Pump(4, 20);
                audioEditor.Save();
                CaptureForm(ctx, host, "26-audio-editor.png", "Audio Editor");
            }

            using (Form host = NewHost())
            {
                ShaderEditorControl shaderEditor = new(shader, project.RootPath);
                host.Controls.Add(shaderEditor);
                GateSuite.ShowHost(host);
                Pump(4, 20);
                HeadlessHarness.Assert(shaderEditor.LastCompileSucceeded, "Default shader template failed D3DCompile.");
                shaderEditor.Save();
                CaptureForm(ctx, host, "27-shader-editor.png", "Shader Editor");
            }

            using (Form host = NewHost())
            {
                PhysicsEditorControl physicsEditor = new(physics, project.RootPath);
                host.Controls.Add(physicsEditor);
                GateSuite.ShowHost(host);
                Pump(10, 25);
                physicsEditor.Save();
                CaptureForm(ctx, host, "28-physics-editor.png", "Physics Editor");
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.Assets.AudioPlaybackSettings", () =>
        {
            // Track M regression: the editor wrote "Volume"/"Loop"/"Spatial" while XAudioSystem
            // read "gain"/"loop"/"spatial", and JsonElement.TryGetProperty is case-sensitive — so
            // every knob in the Audio Editor was silently discarded by the shipped mixer. Both
            // sides now go through AudioAssetSettings; this case asserts they agree on disk.
            string audioDir = Path.Combine(resources.AssetsRoot, "Audio");
            Directory.CreateDirectory(audioDir);
            string wavPath = Path.Combine(audioDir, "SuiteTone.wav");
            WriteSineWav(wavPath, seconds: 0.5, frequency: 440.0);

            string doc = resources.CreateResource(audioDir, ResourceKind.Audio, "SuiteSpatial");

            using Form host = NewHost();
            AudioEditorControl editor = new(doc, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            Pump(4, 20);

            HeadlessHarness.Assert(
                editor.SelectSource("Assets/Audio/SuiteTone.wav"),
                "Audio Editor did not list the generated WAV as a selectable source.");
            Pump(2, 20);
            HeadlessHarness.Assert(editor.HasWaveform, "Audio Editor decoded no waveform for a 16-bit PCM WAV.");

            editor.SetVolume(0.40f);
            editor.SetLoop(true);
            editor.SetSpatial(true);
            editor.SetFalloff(minDistance: 2f, maxDistance: 20f, curve: 1.5f);
            Pump(2, 20);
            editor.Save();

            // Re-read through the *runtime's* parser, not the editor's own document type.
            AudioAssetSettings? saved = AudioAssetSettings.Load(doc);
            HeadlessHarness.Assert(saved is not null, "Saved .audio.json could not be parsed by the runtime reader.");
            HeadlessHarness.Assert(
                MathF.Abs(saved!.Volume - 0.40f) < 0.01f,
                $"Runtime read volume {saved.Volume:0.00}, expected 0.40 — editor/runtime schema drift.");
            HeadlessHarness.Assert(saved.Loop, "Runtime did not see the authored Loop flag.");
            HeadlessHarness.Assert(saved.Spatial, "Runtime did not see the authored Spatial flag.");
            HeadlessHarness.Assert(
                MathF.Abs(saved.MinDistance - 2f) < 0.01f && MathF.Abs(saved.MaxDistance - 20f) < 0.01f,
                $"Runtime read falloff range {saved.MinDistance}..{saved.MaxDistance}, expected 2..20.");
            HeadlessHarness.Assert(
                saved.Source == "Assets/Audio/SuiteTone.wav",
                "Saved document lost its Source reference.");

            // The curve must actually attenuate: full inside the inner radius, silent past the
            // outer one, and strictly decreasing in between.
            HeadlessHarness.Assert(saved.AttenuationAt(0f) == 1f, "Attenuation at zero distance was not full gain.");
            HeadlessHarness.Assert(saved.AttenuationAt(2f) == 1f, "Attenuation inside MinDistance was not full gain.");
            HeadlessHarness.Assert(saved.AttenuationAt(20f) == 0f, "Sound was still audible at MaxDistance.");
            float near = saved.AttenuationAt(6f);
            float far = saved.AttenuationAt(15f);
            HeadlessHarness.Assert(
                near > far && far > 0f && near < 1f,
                $"Falloff is not monotonic between min and max (6u={near:0.000}, 15u={far:0.000}).");
            HeadlessHarness.Assert(
                MathF.Abs(editor.GainAtDistance(6f) - 0.40f * near) < 0.001f,
                "Editor's displayed gain disagrees with the runtime attenuation curve.");

            // The inspector readout must track the settings, not sit on its construction-time
            // text — it kept saying "Non-spatial" after Spatial was ticked because the refresh
            // hung off the audition path, which no-ops when nothing is playing.
            HeadlessHarness.Assert(
                !editor.ListenerSummary.Contains("Non-spatial", StringComparison.OrdinalIgnoreCase),
                $"Listener readout is stale: '{editor.ListenerSummary}' while Spatial is enabled.");
            HeadlessHarness.Assert(
                editor.ListenerSummary.Contains("gain", StringComparison.OrdinalIgnoreCase),
                $"Listener readout should show the computed gain, got '{editor.ListenerSummary}'.");

            // Legacy lowercase sidecars written before this change must keep working.
            AudioAssetSettings? legacy = AudioAssetSettings.Parse("{\"gain\":0.25,\"loop\":true,\"spatial\":true,\"bus\":\"music\"}");
            HeadlessHarness.Assert(
                legacy is not null && MathF.Abs(legacy.Volume - 0.25f) < 0.001f && legacy.Loop && legacy.Bus == "music",
                "Legacy lowercase .audio.json sidecar no longer parses.");

            CaptureForm(ctx, host, "26b-audio-falloff.png", "Audio Editor — spatial falloff");
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.Assets.ModelParticleNote", () =>
        {
            string model = resources.CreateResource(Path.Combine(resources.AssetsRoot, "Models"), ResourceKind.Model, "SuiteModel");            string particle = resources.CreateResource(Path.Combine(resources.AssetsRoot, "Particles"), ResourceKind.Particle, "SuiteParticle");            string note = resources.CreateResource(resources.AssetsRoot, ResourceKind.Note, "SuiteNote");
            using (Form host = NewHost())
            {
                ModelEditorControl modelEditor = new(model, project.RootPath);
                // A plain cube legitimately has few colours on the new neutral grid. Add a
                // curved, coloured surface so the unchanged colour budget measures model lighting,
                // rather than depending on the old checkerboard floor to make a blank scene pass.
                var sphere = modelEditor.AddPart(ModelPrimitiveKind.Sphere);
                sphere.Position = [2f, 1f, 0f];
                sphere.Color = [0.2f, 0.65f, 0.85f];
                modelEditor.ApplyFixtureParts();
                host.Controls.Add(modelEditor);
                GateSuite.ShowHost(host);
                Pump(8, 25);
                modelEditor.Save();
                Bitmap? frame = modelEditor.Viewport.CaptureFrame(settleFrames: 4);
                HeadlessHarness.Assert(frame is not null, "Model viewport readback failed.");
                SaveCapture(ctx, frame!, "29-model-editor.png", "Model Editor", minColors: 24);
            }


            using (Form host = NewHost())
            {
                ParticleEditorControl particleEditor = new(particle, project.RootPath);
                host.Controls.Add(particleEditor);
                GateSuite.ShowHost(host);
                Pump(16, 30);
                HeadlessHarness.Assert(particleEditor.LiveParticleCount > 0, "Particle preview simulation is not spawning.");
                particleEditor.Save();
                CaptureForm(ctx, host, "31-particle-editor.png", "Particle Editor");
            }


            using (Form host = NewHost())
            {
                NoteEditorControl noteEditor = new(note, project.RootPath);
                host.Controls.Add(noteEditor);
                noteEditor.NoteText = "# Suite Note\n\nThis note has **bold**, *italic*, and `code`.\n- bullet one\n- bullet two\n";
                GateSuite.ShowHost(host);
                Pump(6, 25);
                noteEditor.Save();
                CaptureForm(ctx, host, "33-note-editor.png", "Note Editor");
            }

        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.ThemeReachesOpenEditors", () =>
        {
            SuiteChromeBridge.Push();
            using Form host = NewHost();
            PhysicsEditorControl editor = new(
                resources.CreateResource(resources.AssetsRoot, ResourceKind.Physics, "SuiteThemeProbe"),
                project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            Pump(3, 20);

            Color darkCanvas = editor.BackColor;
            ThemePalette light = ThemePalette.Light;
            EditorChrome.Update(
                light.Canvas, light.Surface, light.SurfaceRaised, light.SurfaceHover, light.Border,
                light.Text, light.TextMuted, light.Accent, light.Success, light.Warning, light.Error,
                light.IsDark, ThemeService.InterfaceFont, ThemeService.CodeFont);
            Pump(4, 25);
            HeadlessHarness.Assert(
                editor.BackColor.ToArgb() == light.Canvas.ToArgb(),
                "Open editor did not restyle live when the theme changed.");
            HeadlessHarness.Assert(editor.BackColor.ToArgb() != darkCanvas.ToArgb(), "Theme change was a no-op.");

            ThemePalette dark = ThemePalette.Dark;
            EditorChrome.Update(
                dark.Canvas, dark.Surface, dark.SurfaceRaised, dark.SurfaceHover, dark.Border,
                dark.Text, dark.TextMuted, dark.Accent, dark.Success, dark.Warning, dark.Error,
                dark.IsDark, ThemeService.InterfaceFont, ThemeService.CodeFont);
            Pump(2, 20);
        });

        HeadlessHarness.RunCase(ctx.Report, "Editor.Suite.PlayerLauncherResolves", () =>
        {
            string? runtimeDir = Genesis.Runtime.RuntimePaths.ResolveRuntimeDir();
            HeadlessHarness.Assert(
                runtimeDir is not null,
                "GenesisEngine.exe was not found — the Ember player must build before the headless gate.");
            HeadlessHarness.Assert(
                File.Exists(Path.Combine(runtimeDir!, Genesis.Runtime.RuntimePaths.RuntimeExeName)),
                "Resolved runtime dir does not actually contain GenesisEngine.exe.");

            // The Studio start-room reference must resolve through the Assets tree and parse
            // as a runtime room (this is the F5 preflight, without launching a window).
            string? startRoom = Genesis.Runtime.Project.ProjectRoomResolver.ResolveRoomFile(
                project.RootPath, project.Manifest.StartRoom);
            HeadlessHarness.Assert(startRoom is not null, $"Start room '{project.Manifest.StartRoom}' did not resolve.");
            RoomAsset parsed = RoomAssetLoader.Parse(startRoom!);
            HeadlessHarness.Assert(parsed.Layers.Count > 0, "Parsed start room has no layers.");

            Genesis.Runtime.Project.ProjectRunLauncher.CompileOutcome compile =
                Genesis.Runtime.Project.ProjectRunLauncher.CompileScripts(project.RootPath, runtimeDir);
            HeadlessHarness.Assert(
                compile.Success,
                "Project script compile failed: " + (compile.ErrorMessage ?? "unknown"));
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static Form NewHost() => new()
    {
        ClientSize = new Size(1360, 860),
        FormBorderStyle = FormBorderStyle.FixedSingle,
        Location = new Point(40, 40),
        ShowInTaskbar = false,
        StartPosition = FormStartPosition.Manual,
    };

    private static void Pump(int iterations, int delayMilliseconds)
    {
        for (int i = 0; i < iterations; i++)
        {
            WinFormsApplication.DoEvents();
            Thread.Sleep(delayMilliseconds);
        }
    }

    /// <summary>
    /// Sign of the shared XZ winding of every horizontal (±Y-normal) triangle in a mesh:
    /// +1 / -1 if they all agree, 0 if they disagree (which is itself the bug). Used to assert
    /// that horizontal faces wind identically across primitives — see NEXT-024 / NEXT-031.
    /// </summary>
    private static int HorizontalWindingSign(MeshVertex[] vertices, ushort[] indices, bool up)
    {
        int sign = 0;
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            MeshVertex a = vertices[indices[i]];
            MeshVertex b = vertices[indices[i + 1]];
            MeshVertex c = vertices[indices[i + 2]];
            float wanted = up ? 1f : -1f;
            if (a.Normal.Y * wanted < 0.9f || b.Normal.Y * wanted < 0.9f || c.Normal.Y * wanted < 0.9f)
            {
                continue; // not a horizontal face pointing the way we're inspecting
            }

            // Signed area in the XZ plane; degenerate slivers are skipped.
            float area = (b.Position.X - a.Position.X) * (c.Position.Z - a.Position.Z)
                       - (c.Position.X - a.Position.X) * (b.Position.Z - a.Position.Z);
            if (MathF.Abs(area) < 1e-6f)
            {
                continue;
            }

            int s = area > 0f ? 1 : -1;
            if (sign == 0)
            {
                sign = s;
            }
            else if (sign != s)
            {
                return 0; // inconsistent within this mesh
            }
        }

        return sign;
    }

    private static void AssertWindingMatchesNormals(
        string primitive,
        (MeshVertex[] verts, ushort[] indices) geometry)
    {
        int checkedTriangles = 0;
        for (int i = 0; i + 2 < geometry.indices.Length; i += 3)
        {
            MeshVertex a = geometry.verts[geometry.indices[i]];
            MeshVertex b = geometry.verts[geometry.indices[i + 1]];
            MeshVertex c = geometry.verts[geometry.indices[i + 2]];
            Vector3 face = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
            if (face.LengthSquared() < 1e-10f) continue;

            Vector3 expected = a.Normal + b.Normal + c.Normal;
            if (expected.LengthSquared() < 1e-10f) continue;
            checkedTriangles++;
            float agreement = Vector3.Dot(Vector3.Normalize(face), Vector3.Normalize(expected));
            HeadlessHarness.Assert(
                agreement > 0.001f,
                $"{primitive} triangle {i / 3} winds against its vertex normals (dot {agreement:0.###}).");
        }

        HeadlessHarness.Assert(checkedTriangles > 0, $"{primitive} has no non-degenerate triangles to validate.");
    }

    /// <summary>
    /// The CPU-skinned pose at a time. Comparing whole poses (not just a bounding extent) is what
    /// actually proves the walk cycle animates: a symmetric limb swing can leave the bounding box
    /// unchanged while every leg vertex moves.
    /// </summary>
    private static MeshVertex[] PosedVertices(ModelEditorControl editor, float time)
    {
        if (editor.RiggedAsset is null) return [];
        MeshVertex[] posed = new MeshVertex[editor.RiggedAsset.Meshes[0].SkinnedVertices.Length];
        return ModelRigBridge.SkinInto(editor.RiggedAsset, editor.ActiveClip, time, posed) ? posed : [];
    }

    /// <summary>How many vertices are dominantly weighted to a bone whose name contains "Leg".</summary>
    private static int LegBoundVertexCount(Genesis.Runtime.Modeling.GModelAsset asset)
    {
        if (asset.Meshes.Count == 0) return 0;
        var skinned = asset.Meshes[0].SkinnedVertices;
        if (skinned is not { Length: > 0 }) return 0;

        int count = 0;
        foreach (var v in skinned)
        {
            Span<float> weights = [v.JointWeights.X, v.JointWeights.Y, v.JointWeights.Z, v.JointWeights.W];
            Span<float> joints = [v.JointIndices.X, v.JointIndices.Y, v.JointIndices.Z, v.JointIndices.W];
            int best = 0;
            float bestWeight = -1f;
            for (int k = 0; k < 4; k++)
            {
                if (weights[k] > bestWeight) { bestWeight = weights[k]; best = (int)joints[k]; }
            }

            if (best >= 0 && best < asset.Rig.Bones.Count
                && asset.Rig.Bones[best].Name.Contains("Leg", StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Sampled pixels that differ noticeably between two captures. Used to assert that an
    /// animation is actually *visible*, not merely present in the data — the check that would have
    /// caught the near-idle walk preset (NEXT-036).
    /// </summary>
    private static int CountDifferingPixels(Bitmap a, Bitmap b, int step = 3, int threshold = 12)
    {
        if (a.Width != b.Width || a.Height != b.Height) return int.MaxValue;
        int differing = 0;
        for (int y = 0; y < a.Height; y += step)
        {
            for (int x = 0; x < a.Width; x += step)
            {
                Color pa = a.GetPixel(x, y);
                Color pb = b.GetPixel(x, y);
                int delta = Math.Abs(pa.R - pb.R) + Math.Abs(pa.G - pb.G) + Math.Abs(pa.B - pb.B);
                if (delta > threshold)
                {
                    differing++;
                }
            }
        }

        return differing;
    }

    /// <summary>Largest single-vertex displacement between two poses (how big the stride reads).</summary>
    private static float MaxDisplacement(MeshVertex[] a, MeshVertex[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0f;
        float max = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            max = MathF.Max(max, Vector3.Distance(a[i].Position, b[i].Position));
        }

        return max;
    }

    /// <summary>Number of vertices that moved more than a hair between two poses.</summary>
    private static int PoseDelta(MeshVertex[] a, MeshVertex[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0;
        int moved = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (Vector3.Distance(a[i].Position, b[i].Position) > 0.005f)
            {
                moved++;
            }
        }

        return moved;
    }

    /// <summary>Peak-to-trough height range of a generated terrain (how rugged it is).</summary>
    private static float Relief(TerrainAsset terrain)
    {
        float min = float.MaxValue, max = float.MinValue;
        for (int z = 0; z < terrain.ResolutionZ; z++)
        {
            for (int x = 0; x < terrain.ResolutionX; x++)
            {
                float h = terrain.GetHeight(x, z);
                if (h < min) { min = h; }
                if (h > max) { max = h; }
            }
        }

        return max - min;
    }

    /// <summary>Mean cardinal-neighbour height delta; thermal erosion should reduce it.</summary>
    private static float TerrainRoughness(TerrainAsset terrain)
    {
        double total = 0;
        int samples = 0;
        for (int z = 0; z < terrain.ResolutionZ - 1; z++)
        {
            for (int x = 0; x < terrain.ResolutionX - 1; x++)
            {
                float height = terrain.GetHeight(x, z);
                total += Math.Abs(height - terrain.GetHeight(x + 1, z));
                total += Math.Abs(height - terrain.GetHeight(x, z + 1));
                samples += 2;
            }
        }

        return samples > 0 ? (float)(total / samples) : 0f;
    }

    private static float AxisLength(RoomEditorControl editor) =>
        MathF.Max(0.6f, editor.Viewport.Camera.Distance * 0.14f);

    private static Point TranslateToViewport(RoomEditorControl editor, Point editorPoint)
    {
        Point screen = editor.PointToScreen(editorPoint);
        return editor.Viewport.Host.PointToClient(screen);
    }

    private static float NormalizeDegrees(float degrees)
    {
        degrees %= 360f;
        if (degrees > 180f)
        {
            degrees -= 360f;
        }
        else if (degrees < -180f)
        {
            degrees += 360f;
        }

        return degrees;
    }

    /// <summary>Recursively finds the first descendant of type <typeparamref name="T"/> matching
    /// <paramref name="predicate"/> — drives real dialog/panel controls in headless tests the
    /// same way a click would, instead of re-deriving their behaviour from private state.</summary>
    private static T? FindDescendant<T>(Control root, Func<T, bool>? predicate = null) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T typed && (predicate is null || predicate(typed)))
            {
                return typed;
            }

            T? found = FindDescendant(child, predicate);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static RuntimeImageMetrics SaveCapture(
        HeadlessContext ctx,
        Bitmap bitmap,
        string fileName,
        string label,
        int minColors)
    {
        string path = Path.Combine(ctx.Captures, fileName);
        bitmap.Save(path, ImageFormat.Png);
        RuntimeImageMetrics metrics = RuntimeImageMetrics.Measure(bitmap);
        bitmap.Dispose();
        ctx.Report.Images.Add(new ImageResult(
            label, fileName, metrics.Width, metrics.Height, metrics.UniqueSampledColors, metrics.AverageLuminance));
        HeadlessHarness.Assert(
            metrics.UniqueSampledColors >= minColors,
            $"'{fileName}' looks blank/flat ({metrics.UniqueSampledColors} colours < {minColors}).");
        return metrics;
    }

    /// <summary>
    /// Enable an Image resource for tile-set use. T1 made this a prerequisite: an image is only
    /// offered as a tile set once it has been flagged for it, so tests must opt in exactly as a
    /// designer would tick the box in the Image Editor.
    /// </summary>
    private static void EnableTileSet(string imageResourcePath, int tileWidth = 32, int tileHeight = 32)
    {
        ImageDocument document = ImageDocumentSerializer.Deserialize(File.ReadAllText(imageResourcePath)).Document;
        document.Usage.Allowed |= ImageUsage.Tileset;
        document.Usage.Tileset.TileWidth = tileWidth;
        document.Usage.Tileset.TileHeight = tileHeight;
        File.WriteAllText(imageResourcePath, ImageDocumentSerializer.Serialize(document));
    }

    /// <summary>Captures HUD text a running game's PGSL Draw events emit.</summary>
    private sealed class RecordingHudCanvas : IHudCanvas
    {
        public List<(string Text, float X, float Y)> Texts { get; } = [];
        public int Width => 1280;
        public int Height => 720;
        public void Text(string text, float x, float y, float size, Vector4 color) => Texts.Add((text, x, y));
        public void TextCentered(string text, float cx, float y, float w, float size, Vector4 color) => Texts.Add((text, cx, y));
        public void Rect(float x, float y, float w, float h, Vector4 color, bool filled = true) { }
        public void Line(float x1, float y1, float x2, float y2, Vector4 color, float thickness = 1.5f) { }
    }

    /// <summary>Captures what a running game asked the mixer to play.</summary>
    private sealed class AcceptanceAudioRecorder : IAudioSystem
    {
        private int _nextChannel = 1;

        public List<string> Plays { get; } = [];
        public float MasterVolume { get; set; } = 1f;

        public int LoadSound(string projectRelativePath) => 1;

        public AudioChannel Play(int soundId, float volume = 1f, float pitch = 1f, bool loop = false)
        {
            Plays.Add($"sound{soundId}@{volume:0.00}");
            return new AudioChannel(_nextChannel++);
        }

        public void Stop(AudioChannel channel) { }
        public void StopAll() { }
        public bool IsPlaying(AudioChannel channel) => channel.IsValid;
        public void SetChannelVolume(AudioChannel channel, float volume) { }
        public void SetChannelPosition(AudioChannel channel, Vector3 position) { }
        public void SetListener(Vector3 position, Vector3 forward) { }
        public void Update() { }
    }

    /// <summary>
    /// Write a 16-bit mono PCM sine WAV so the Audio Editor has a real, decodable source to
    /// draw and audition without shipping a binary fixture into the repository.
    /// </summary>
    private static void WriteSineWav(string path, double seconds, double frequency, int sampleRate = 22050)
    {
        int sampleCount = (int)(seconds * sampleRate);
        int dataBytes = sampleCount * 2;

        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);                       // PCM chunk size
        writer.Write((short)1);                 // format = PCM
        writer.Write((short)1);                 // channels
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);           // byte rate
        writer.Write((short)2);                 // block align
        writer.Write((short)16);                // bits per sample
        writer.Write("data"u8);
        writer.Write(dataBytes);

        for (int sample = 0; sample < sampleCount; sample++)
        {
            // Fade the tail so the drawn envelope has visible shape rather than a flat block.
            double envelope = 1.0 - (sample / (double)sampleCount);
            double value = Math.Sin(2.0 * Math.PI * frequency * sample / sampleRate) * envelope;
            writer.Write((short)(value * short.MaxValue * 0.8));
        }
    }

    private static void CaptureForm(HeadlessContext ctx, Form host, string fileName, string label)
    {
        
        host.BringToFront();
        host.TopMost = true;
        Pump(8, 30);
        using Bitmap bitmap = VisualCapture.CaptureWindowPixels(host);

        host.TopMost = false;
        string path = Path.Combine(ctx.Captures, fileName);
        bitmap.Save(path, ImageFormat.Png);
        RuntimeImageMetrics metrics = RuntimeImageMetrics.Measure(bitmap);
        ctx.Report.Images.Add(new ImageResult(
            label, fileName, metrics.Width, metrics.Height, metrics.UniqueSampledColors, metrics.AverageLuminance));
        HeadlessHarness.Assert(
            metrics.UniqueSampledColors >= 8,
            $"'{fileName}' capture appears blank ({metrics.UniqueSampledColors} colours).");
    }
}
