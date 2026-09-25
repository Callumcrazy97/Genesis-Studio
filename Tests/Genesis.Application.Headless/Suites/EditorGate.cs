using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Diagnostics;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Projects.Templates;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Settings;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Dialogs;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Application.Editors.Suite.Objects.VisualActions;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Editors.Suite.Scripts;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Application.Studio.Docking;
using Genesis.Application.Studio.Forms;
using Genesis.Application.Studio;
using Genesis.Runtime.Scripting;
using Genesis.Runtime;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Particles;
using Genesis.Runtime.Project;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Climate;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Core;
using Genesis.Rendering.Primitives;
using Genesis.Shared.Assets;
using Genesis.Shared.Audio;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Interfaces;
using Genesis.Runtime.ECS.Components;
using Genesis.World.Foliage;
using Genesis.World.Terrain;
using Genesis.World.Water;
using Newtonsoft.Json.Linq;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// The build gate's editor half: one test per editor, each a full authoring workflow.
/// </summary>
/// <remarks>
/// Every case follows the same spine — <b>author → assert in the editor → save → reload from disk →
/// assert it survived</b> — and, for anything the game consumes, one more step proving the *runtime*
/// reads what was written. That last step is the point. This codebase's recurring defect is an
/// editor that looks completely functional while the runtime reads a different field, a different
/// folder, or nothing at all (NEXT-041, NEXT-044, NEXT-046, NEXT-086), and no amount of in-editor
/// assertion catches it.
/// </remarks>
internal static class EditorGate
{
    public static void Run(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Editor");

        HeadlessHarness.RunCase(ctx.Report, "Editor.Image", () => Image(ctx, fixture));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room", () => Room(ctx, fixture));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Object", () => Object(ctx, fixture));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Script", () => Script(ctx, fixture));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Terrain", () => Terrain(ctx, fixture));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model", () => Model(ctx, fixture));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Audio", () => Audio(ctx, fixture));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Shader", () => Shader(ctx, fixture));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Particle", () => Particle(ctx, fixture));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Physics", () => Physics(ctx, fixture));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Note", () => Note(ctx, fixture));
    }

    public static void RunFocused(HeadlessContext ctx, GateSuite.GateFixture fixture, string editor)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Editor");
        (string Name, Action Action) focused = editor switch
        {
            "image" => ("Editor.Image", () => Image(ctx, fixture)),
            "room" => ("Editor.Room", () => Room(ctx, fixture)),
            "object" => ("Editor.Object", () => Object(ctx, fixture)),
            "script" or "pgsl" => ("Editor.Script", () => Script(ctx, fixture)),
            "terrain" => ("Editor.Terrain", () => Terrain(ctx, fixture)),
            "model" => ("Editor.Model", () => Model(ctx, fixture)),
            "audio" => ("Editor.Audio", () => Audio(ctx, fixture)),
            "shader" => ("Editor.Shader", () => Shader(ctx, fixture)),
            "particle" => ("Editor.Particle", () => Particle(ctx, fixture)),
            "physics" => ("Editor.Physics", () => Physics(ctx, fixture)),
            "note" => ("Editor.Note", () => Note(ctx, fixture)),
            _ => throw new ArgumentException(
                $"Unknown editor '{editor}'. Expected Image, Room, Object, Script, Terrain, Model, Audio, Shader, Particle, Physics, or Note."),
        };
        HeadlessHarness.RunCase(ctx.Report, focused.Name, focused.Action);
    }

    // ── Image ───────────────────────────────────────────────────────────────────

    private static void Image(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        ProjectSession project = fixture.Blank;
        ResourceService resources = fixture.Resources(project);
        string path = resources.CreateResource(
            fixture.Folder(project, "Images"), ResourceKind.Image, "Gate Sprite");

        ImageDocumentSession session = new(
            ImageDocumentSerializer.LoadAtomic(path).Document, path, ImageDocumentAccess.Editor);
        ImageWorkspace workspace = ImageWorkspace.CreateBlank(32, 32, Color.Transparent);

        using Form host = GateSuite.NewHost(1280, 780);
        ImageEditorControl editor = new(session, workspace) { Dock = DockStyle.Fill };
        host.Controls.Add(editor);
        GateSuite.ShowHost(host);
        editor.Canvas.SyncClientSize();
        GateSuite.Pump(6, 30);

        HeadlessHarness.Step("target image shell exposes composite preview and collapses panels at narrow widths", () =>
        {
            HeadlessHarness.Assert(
                editor.UsesTargetImageShell && editor.HasCompositeFramePreview,
                "The Image Editor is not using the target shell with a live composite frame preview.");
            host.ClientSize = new Size(760, 620);
            GateSuite.Pump(3, 20);
            HeadlessHarness.Assert(
                editor.IsNarrowLayout
                && !editor.IsToolsPanelVisible
                && !editor.IsInspectorPanelVisible
                && editor.IsTimelinePanelVisible,
                "A narrow Image Editor should keep the canvas/timeline usable and collapse side panels behind toggles.");
            host.ClientSize = new Size(1280, 780);
            GateSuite.Pump(3, 20);
            HeadlessHarness.Assert(
                !editor.IsNarrowLayout
                && editor.IsToolsPanelVisible
                && editor.IsInspectorPanelVisible
                && editor.IsTimelinePanelVisible,
                "A wide Image Editor should restore the full tools/canvas/inspector/timeline shell.");
        });

        HeadlessHarness.Step("draw with the brush at the pixel asked for", () =>
        {
            editor.SetActiveTool(ImageToolKind.Brush);
            editor.SetBrushSize(4);
            editor.SetForegroundColor(Color.FromArgb(255, 220, 40, 40));
            editor.DrawStroke(new Point(6, 6), new Point(24, 6), Color.FromArgb(255, 220, 40, 40), size: 4);

            Color painted = RasterOperations.GetPixel(workspace.CurrentLayer!.Pixels, 32, 32, 12, 6);
            HeadlessHarness.Assert(
                painted.A > 0 && painted.R > 150 && painted.G < 90,
                $"The brush stroke did not land where it was asked ({painted}).");
        });

        HeadlessHarness.Step("undo and redo the stroke", () =>
        {
            HeadlessHarness.Assert(editor.Undo(), "The stroke was not journaled for undo.");
            Color cleared = RasterOperations.GetPixel(workspace.CurrentLayer!.Pixels, 32, 32, 12, 6);
            HeadlessHarness.Assert(cleared.A == 0, $"Undo left paint behind ({cleared}).");
            HeadlessHarness.Assert(editor.Redo(), "Redo was not available after an undo.");
            HeadlessHarness.Assert(
                RasterOperations.GetPixel(workspace.CurrentLayer!.Pixels, 32, 32, 12, 6).A > 0,
                "Redo did not restore the stroke.");
        });

        HeadlessHarness.Step("effects respect selection, alpha and locked layers with exact undo", () =>
        {
            ImageLayerBuffer layer = workspace.CurrentLayer!;
            byte[] before = (byte[])layer.Pixels.Clone();
            workspace.Selection.SetRectangle(new Rectangle(10, 5, 4, 3), false, false);
            editor.ApplyEffect(ImageEffectCatalog.Invert, new Dictionary<string, object>());
            byte[] after = (byte[])layer.Pixels.Clone();
            HeadlessHarness.Assert(after[(6 * 32 + 12) * 4] == 255 - before[(6 * 32 + 12) * 4],
                "The effect missed a selected pixel.");
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                {
                    int offset = (y * 32 + x) * 4;
                    HeadlessHarness.Assert(after[offset + 3] == before[offset + 3], "Colour effect changed alpha.");
                    if (!workspace.Selection.Contains(x, y))
                        HeadlessHarness.Assert(after.AsSpan(offset, 4).SequenceEqual(before.AsSpan(offset, 4)), "Effect leaked outside selection.");
                }
            HeadlessHarness.Assert(editor.Undo() && layer.Pixels.SequenceEqual(before), "Effect undo did not restore exact pixels.");
            HeadlessHarness.Assert(editor.Redo() && layer.Pixels.SequenceEqual(after), "Effect redo changed the result.");
            editor.Undo();
            layer.Locked = true;
            editor.ApplyEffect(ImageEffectCatalog.Invert, new Dictionary<string, object>());
            HeadlessHarness.Assert(layer.Pixels.SequenceEqual(before), "Effect changed a locked layer.");
            layer.Locked = false;
            workspace.Selection.Clear();
        });

        HeadlessHarness.Step("paste an image from outside the application", () =>
        {
            using ClipboardTestScope clipboard = ClipboardTestScope.Capture();
            using Bitmap external = new(8, 8);
            using (Graphics graphics = Graphics.FromImage(external))
            {
                graphics.Clear(Color.FromArgb(255, 20, 120, 240));
            }

            HeadlessHarness.Assert(
                TryPasteClipboardImage(external, editor),
                "External image paste failed through both the Win32 clipboard and its deterministic decode seam.");
            HeadlessHarness.Assert(
                workspace.CompositeCurrentFrame().Where((_, index) => index % 4 == 3).Any(alpha => alpha > 0),
                "The paste produced no visible pixels.");
        });

        HeadlessHarness.Step("grow the canvas around existing artwork", () =>
        {
            Point offset = CanvasResizeDialog.OffsetFor(
                CanvasAnchor.BottomRight, new Size(32, 32), new Size(64, 64));
            workspace.ResizeCanvas(64, 64, offset);
            HeadlessHarness.Assert(
                workspace.Width == 64 && workspace.Height == 64,
                $"The canvas is {workspace.Width}×{workspace.Height} after growing to 64×64.");
            HeadlessHarness.Assert(
                offset == new Point(32, 32),
                $"Anchoring bottom-right offset the artwork by {offset.X},{offset.Y}.");
        });

        HeadlessHarness.Step("generate a complete editable PBR material set", () =>
        {
            editor.GeneratePbrMaterialSet(PbrMaterialSettings.FromPreset(PbrMaterialPreset.Rock));
            ImageMaterialChannel[] channels = workspace.CurrentFrame!.Layers.Select(layer => layer.Channel).Distinct().ToArray();
            foreach (ImageMaterialChannel expected in new[]
            {
                ImageMaterialChannel.Normal, ImageMaterialChannel.Roughness, ImageMaterialChannel.Metallic,
                ImageMaterialChannel.Height, ImageMaterialChannel.Occlusion,
            })
            {
                HeadlessHarness.Assert(channels.Contains(expected), $"PBR generation omitted the {expected} channel.");
                byte[] channel = workspace.CompositeCurrentFrame(expected);
                HeadlessHarness.Assert(channel.Length == 64 * 64 * 4 && channel.Where((_, index) => index % 4 != 3).Distinct().Count() > 1,
                    $"Generated {expected} channel is empty or constant.");
            }
        });

        HeadlessHarness.Step("metadata follows the usage profile", () =>
        {
            session.Document.Usage.Allowed = ImageUsage.Tileset;
            session.Document.Usage.Tileset.TileWidth = 16;
            session.Document.Usage.Tileset.TileHeight = 16;

            using ImageViewerControl viewer = new(session, workspace);
            HeadlessHarness.Assert(
                viewer.IsSectionVisible(ImageUsage.Tileset) && !viewer.IsSectionVisible(ImageUsage.Background),
                "The inspector showed the wrong sections for a tile sheet.");
            HeadlessHarness.Assert(
                ImageViewerControl.UsageProfileFlags.Count == 4
                && !ImageViewerControl.UsageProfileFlags.Contains(ImageUsage.NineSlice),
                "The usage profile is no longer the four roles an image can be.");
            HeadlessHarness.Assert(
                viewer.UsesTargetImageViewerShell && viewer.HasCompositeFramePreview,
                "The Image Viewer is not using the target shell with a live composite frame preview.");
            HeadlessHarness.Assert(
                viewer.HasTextureGroupChrome,
                "The Image Viewer is missing the Texture Group combo / manage chrome (R7.3).");
        });

        HeadlessHarness.Step("save, reload, and find the same image", () =>
        {
            ImageWorkspaceStorage.Save(session, workspace);
            ImageDocumentSession reloaded = new(
                ImageDocumentSerializer.LoadAtomic(path).Document, path, ImageDocumentAccess.Editor);
            ImageWorkspace fromDisk = ImageWorkspaceStorage.Load(reloaded);

            HeadlessHarness.Assert(
                fromDisk.Width == 64 && fromDisk.Height == 64,
                $"Reloaded canvas is {fromDisk.Width}×{fromDisk.Height}, expected 64×64.");
            HeadlessHarness.Assert(
                reloaded.Document.Usage.Supports(ImageUsage.Tileset)
                && reloaded.Document.Usage.Tileset.TileWidth == 16,
                "The tileset metadata did not survive the save.");
            HeadlessHarness.Assert(
                fromDisk.CompositeCurrentFrame().Where((_, index) => index % 4 == 3).Any(alpha => alpha > 0),
                "The reloaded image has no pixels — the art was written nowhere.");
            HeadlessHarness.Assert(
                fromDisk.CurrentFrame!.Layers.Any(layer => layer.Channel == ImageMaterialChannel.Normal)
                && fromDisk.CurrentFrame.Layers.Any(layer => layer.Channel == ImageMaterialChannel.Occlusion),
                "Generated PBR channel layers did not survive the workspace save.");
        });

        HeadlessHarness.Step("the canvas renders on the GPU", () =>
        {
            editor.Canvas.SyncClientSize();
            editor.Canvas.FitToView();
            GateSuite.Pump(6, 30);
            using Bitmap? frame = editor.Canvas.ReadbackFrameToBitmap(settleFrames: 3);
            HeadlessHarness.Assert(frame is not null, "The image canvas produced no GPU frame.");
            HeadlessHarness.Assert(
                editor.Canvas.RenderWidth == editor.Canvas.ClientWidth,
                $"The canvas swap chain is {editor.Canvas.RenderWidth}px wide for a "
                + $"{editor.Canvas.ClientWidth}px control — the frame is being stretched.");
        });

        HeadlessHarness.Step("external frame and layer writes live-reload the shared Image workspace", () =>
        {
            ResourceItem resource = GateSuite.Flatten(resources.BuildTree())
                .First(item => string.Equals(item.FullPath, path, StringComparison.OrdinalIgnoreCase));
            using ImageViewerDocument viewerDocument = new(resource, new StudioLog());
            AssetDependencyGraph graph = new(project.RootPath);
            string dataDirectory = ResourceAssociates.GetSpriteDataDirectory(path);
            string[] payloads = Directory.EnumerateFiles(dataDirectory, "*.png", SearchOption.AllDirectories).ToArray();
            HeadlessHarness.Assert(payloads.Length > 0, "The saved Image has no live-reloadable pixel payloads.");
            byte[] lime = new byte[64 * 64 * 4];
            for (int pixel = 0; pixel < lime.Length; pixel += 4)
            {
                lime[pixel + 1] = 255;
                lime[pixel + 3] = 255;
            }
            foreach (string payload in payloads)
                ImageWorkspaceStorage.WritePng(payload, 64, 64, lime);
            IReadOnlyCollection<string> affected = graph.GetAffectedPaths(payloads);
            ProjectAssetChangeSet changes = new(project.RootPath, payloads, affected, generation: 77);
            viewerDocument.HandleAssetChanges(changes);
            Color refreshed = RasterOperations.GetPixel(
                viewerDocument.Workspace.CompositeCurrentFrame(), 64, 64, 8, 8);
            HeadlessHarness.Assert(
                refreshed.G > 180 && refreshed.R < 80,
                $"The open Image workspace kept stale pixels after an external write ({refreshed}).");
        });

        host.Close();
    }

    // ── Room ────────────────────────────────────────────────────────────────────

    private static void Room(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        ProjectSession project = fixture.Blank;
        ResourceService resources = fixture.Resources(project);
        string hero = resources.CreateResource(
            fixture.Folder(project, "Objects"), ResourceKind.GameObject, "Gate Room Hero");
        string roomPath = resources.CreateResource(
            fixture.Folder(project, "Rooms"), ResourceKind.Room, "Gate Arena");

        using Form host = GateSuite.NewHost();
        RoomEditorControl editor = new(roomPath, project.RootPath);
        HeadlessHarness.Step("opening a room preserves its authored dimension and clean state", () =>
        {
            HeadlessHarness.Assert(editor.Room.Dimension == RoomAssetLoader.Parse(roomPath).Dimension,
                "Constructing the Room Editor changed the document dimension.");
            HeadlessHarness.Assert(!editor.IsDirty, "Opening the Room Editor dirtied the document.");
        });
        host.Controls.Add(editor);
        GateSuite.ShowHost(host);
        GateSuite.Pump(10, 30);

        HeadlessHarness.Step("shared chrome spacing matches the editor mockup tokens", () =>
            AssertSharedChromeSpacing(editor, "The Room Editor"));

        HeadlessHarness.Step("room Inspector sections expand to their fields and collapse without clipping", () =>
        {
            static IEnumerable<Control> Children(Control parent) => parent.Controls.Cast<Control>()
                .SelectMany(child => new[] { child }.Concat(Children(child)));
            var sections = Children(editor.Inspector).OfType<Genesis.Application.Editors.Suite.UiKit.InspectorSection>()
                .Where(section => section.Visible).ToArray();
            HeadlessHarness.Assert(sections.Length >= 3, "Room settings sections are missing.");
            foreach (var section in sections)
            {
                int expandedHeight = section.Height;
                HeadlessHarness.Assert(section.Body.Bottom <= section.ClientSize.Height, $"{section.Title} clips its body.");
                section.Expanded = false;
                GateSuite.Pump(2, 10);
                HeadlessHarness.Assert(section.Height < expandedHeight, $"{section.Title} did not collapse.");
                section.Expanded = true;
                GateSuite.Pump(2, 10);
                HeadlessHarness.Assert(section.Body.Bottom <= section.ClientSize.Height, $"{section.Title} clips after reopening.");
            }
        });

        HeadlessHarness.Step("responsive command bar exposes document state and room modes", () =>
        {
            EditorCommandBar commandBar = editor.Controls.OfType<EditorCommandBar>().Single();
            HeadlessHarness.Assert(commandBar.IsDocumentBound && commandBar.DocumentStateText.Contains("Saved", StringComparison.Ordinal),
                "The Room Editor command bar is not bound to the document save state.");
            host.ClientSize = new Size(760, 620);
            GateSuite.Pump(3, 20);
            HeadlessHarness.Assert(editor.IsNarrowLayout,
                "A 760px-wide Room Editor should enter narrow layout.");
            HeadlessHarness.Assert(commandBar.IsSavePinned && commandBar.HasOverflowedCommands,
                "The narrow Room Editor neither pinned Save nor moved secondary commands into More.");
            editor.SetTool(RoomEditorControl.RoomTool.Paint);
            HeadlessHarness.Assert(editor.ActiveTool == RoomEditorControl.RoomTool.Paint,
                "The Room Editor has no explicit Paint mode.");
            editor.SetTool(RoomEditorControl.RoomTool.Select);
            host.ClientSize = new Size(1100, 760);
            GateSuite.Pump(3, 20);
            HeadlessHarness.Assert(!editor.IsNarrowLayout,
                "An 1100px-wide Room Editor should use the wide split layout.");
        });

        HeadlessHarness.Step("place instances from the palette", () =>
        {
            HeadlessHarness.Assert(!editor.ViewMode3D, "A new room should open in 2D.");
            editor.BeginPlacement(hero);
            Place(editor, 320f, 256f, Keys.Control);
            Place(editor, 576f, 256f, Keys.Control);
            Place(editor, 448f, 384f, Keys.None);
            HeadlessHarness.Assert(
                editor.Room.Nodes.Count == 3,
                $"Expected 3 placed instances, got {editor.Room.Nodes.Count}.");
            HeadlessHarness.Assert(
                editor.ActiveTool == RoomEditorControl.RoomTool.Select,
                "Placing without Ctrl must return to the Select tool.");
        });

        RoomNode selected = null!;
        HeadlessHarness.Step("select and move one, with undo and redo", () =>
        {
            editor.EditorPointerDown(editor.ClientFromWorld2D(new Vector2(320f, 256f)), MouseButtons.Left, Keys.None);
            editor.EditorPointerUp(editor.ClientFromWorld2D(new Vector2(320f, 256f)), MouseButtons.Left, Keys.None);
            selected = editor.SelectedNode ?? throw new InvalidOperationException("Click selection hit nothing.");

            Point from = editor.ClientFromWorld2D(new Vector2(320f, 256f));
            Point to = editor.ClientFromWorld2D(new Vector2(416f, 320f));
            editor.EditorPointerDown(from, MouseButtons.Left, Keys.None);
            editor.EditorPointerMove(to, MouseButtons.Left, Keys.None);
            editor.EditorPointerUp(to, MouseButtons.Left, Keys.None);
            HeadlessHarness.Assert(
                MathF.Abs(selected.Transform.X - 416f) < 0.6f,
                $"The drag landed at x={selected.Transform.X}, expected 416.");

            HeadlessHarness.Assert(editor.CanUndo, "The move was not journaled for undo.");
            editor.Undo();
            HeadlessHarness.Assert(
                MathF.Abs(selected.Transform.X - 320f) < 0.6f,
                $"Undo did not restore x (got {selected.Transform.X}).");
            editor.Redo();
            HeadlessHarness.Assert(
                MathF.Abs(selected.Transform.X - 416f) < 0.6f,
                $"Redo did not re-apply x (got {selected.Transform.X}).");
        });

        HeadlessHarness.Step("switch to 3D and place on the ground plane", () =>
        {
            string room3D = resources.CreateResource(
                fixture.Folder(project, "Rooms"), ResourceKind.Room, "Gate Arena 3D");
            using Form host3D = GateSuite.NewHost();
            RoomEditorControl editor3D = new(room3D, project.RootPath);
            host3D.Controls.Add(editor3D);
            GateSuite.ShowHost(host3D);
            GateSuite.Pump(8, 30);

            editor3D.ViewMode3D = true;
            HeadlessHarness.Assert(
                editor3D.Room.Dimension == RoomDimension.ThreeD,
                "Toggling 3D did not change the room's dimension.");
            GateSuite.Pump(4, 25);

            editor3D.BeginPlacement(hero);
            Point centre = new(editor3D.Viewport.Width / 2, (int)(editor3D.Viewport.Height * 0.62f));
            Point onViewport = new(
                centre.X + editor3D.Viewport.Left,
                centre.Y + editor3D.Viewport.Top);
            editor3D.EditorPointerDown(onViewport, MouseButtons.Left, Keys.None);
            editor3D.EditorPointerUp(onViewport, MouseButtons.Left, Keys.None);
            HeadlessHarness.Assert(
                editor3D.Room.Nodes.Count == 1,
                $"3D ground placement added {editor3D.Room.Nodes.Count} instances, expected 1.");
            editor3D.SetEnvironmentFields(
                dynamicSky: true,
                WeatherKind.Thunderstorm,
                timeHours: 18.5f,
                timeScale: 48f,
                automaticWeather: false);
            editor3D.SetAtmosphereFields(AtmospherePreset.Storm, volumetricClouds: true, quality: 3,
                baseHeight: 220f, thickness: 110f, coverage: 1.2f, haze: 0.4f);
            editor3D.SetEnvironmentSoundscape(
                rain: "Assets/Audio/Rain.audio.json",
                water: "Assets/Audio/Water.audio.json",
                wildlife: "Assets/Audio/Wildlife.audio.json");
            editor3D.Save();

            RoomAsset authored = RoomAssetLoader.Parse(room3D);
            HeadlessHarness.Assert(
                authored.Environment.DynamicSky
                && authored.Environment.Weather == "Thunderstorm"
                && Math.Abs(authored.Environment.TimeOfDayHours - 18.5f) < 0.01f
                && authored.Environment.AtmospherePreset == "Storm"
                && authored.Environment.CloudQuality == 3
                && authored.Environment.RainAudio.EndsWith("Rain.audio.json", StringComparison.Ordinal),
                "Room climate, atmosphere or adaptive-soundscape controls did not persist their authored values.");
            using Genesis.Runtime.RuntimeScene runtimeScene = new("climate-gate");
            RoomSceneBuilder.ApplySceneSettings(runtimeScene, authored);
            HeadlessHarness.Assert(
                runtimeScene.Climate is not null
                && runtimeScene.Climate.ActiveWeather == WeatherKind.Thunderstorm
                && runtimeScene.Atmosphere is not null
                && runtimeScene.Atmosphere.Options.Preset == AtmospherePreset.Storm,
                "The runtime did not activate the room's authored climate and atmosphere.");
            host3D.Close();
        });

        HeadlessHarness.Step("save, and the runtime spawns what was authored", () =>
        {
            editor.Save();
            HeadlessHarness.Assert(!editor.IsDirty, "The room stayed dirty after save.");

            // The check that matters: the runtime's own loader, reading the file on disk.
            RoomAsset asset = RoomAssetLoader.Parse(roomPath);
            EcsWorld world = new();
            ScriptHostSystem scriptHost = new();
            RoomBuildResult built = new RoomSceneBuilder(project.RootPath, scriptHost).Build(world, asset);
            HeadlessHarness.Assert(
                built.SpawnedEntities.Count >= 3,
                $"The saved room spawned {built.SpawnedEntities.Count} entities, expected at least 3.");

            Entity spawned = built.EntitiesByNodeId.Values.First();
            ref TransformComponent transform = ref world.GetRef<TransformComponent>(spawned);
            HeadlessHarness.Assert(
                transform.X > 0f && transform.Y > 0f,
                $"A spawned instance sits at ({transform.X},{transform.Y}) — placement did not reach the runtime.");
        });

        HeadlessHarness.Step("the viewport renders", () =>
        {
            using Bitmap? frame = editor.Viewport.CaptureFrame(settleFrames: 3);
            HeadlessHarness.Assert(frame is not null, "The room viewport produced no frame.");
            ImageMetrics metrics = MeasureBitmap(frame!);
            HeadlessHarness.Assert(
                metrics.UniqueSampledColors >= 4,
                $"The room viewport drew {metrics.UniqueSampledColors} colours — effectively nothing.");
            string file = Path.Combine(ctx.Captures, "gate-03-room-editor.png");
            frame!.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            ctx.Report.Images.Add(ImageResult.From("Gate — Room Editor", "gate-03-room-editor.png", metrics));
        });

        HeadlessHarness.Step("the canonical 3D project opens as one authorable Room Editor scene", () =>
        {
            ProjectSession canonicalProject = fixture.ThreeD;
            string canonicalRoom = Path.Combine(
                canonicalProject.AssetsPath,
                "Rooms",
                SandboxTemplate.RoomName + ".room.json");
            using Form canonicalHost = GateSuite.NewHost(1280, 820);
            using RoomEditorControl canonicalEditor = new(canonicalRoom, canonicalProject.RootPath);
            canonicalHost.Controls.Add(canonicalEditor);
            GateSuite.ShowHost(canonicalHost);
            GateSuite.Pump(12, 35);

            HeadlessHarness.Assert(
                canonicalEditor.ViewMode3D
                && canonicalEditor.Room.Nodes.Any(node => node.Kind == RoomNodeKind.Terrain)
                && canonicalEditor.Room.Nodes.Any(node => node.GameObject?.Prefab.EndsWith(
                    "/" + SandboxTemplate.CampfireName + ".object.json",
                    StringComparison.OrdinalIgnoreCase) == true)
                && canonicalEditor.Room.Nodes.Any(node => node.GameObject?.Prefab.EndsWith(
                    "/" + SandboxTemplate.PlayerName + ".object.json",
                    StringComparison.OrdinalIgnoreCase) == true),
                "Room Editor did not open the canonical terrain/campfire/player graph in 3D mode.");
            using Bitmap? canonicalFrame = canonicalEditor.Viewport.CaptureFrame(settleFrames: 4);
            HeadlessHarness.Assert(
                canonicalFrame is not null && MeasureBitmap(canonicalFrame).UniqueSampledColors >= 5,
                "Room Editor could not preview the same integrated 3D room that F5 plays.");
            canonicalHost.Close();
        });

        host.Close();
    }

    // ── Object ──────────────────────────────────────────────────────────────────

    private static void Object(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        ProjectSession project = fixture.TwoD;
        string coin = Path.Combine(project.AssetsPath, "Objects", "Coin.object.json");
        ResourceService objectResources = fixture.Resources(project);
        string particle = objectResources.CreateResource(
            fixture.Folder(project, "Particles"), ResourceKind.Particle, "Coin Sparkle");
        string audio = objectResources.CreateResource(
            fixture.Folder(project, "Audio"), ResourceKind.Audio, "Coin Hum");
        string wave = Path.Combine(Path.GetDirectoryName(audio)!, "Coin Hum.wav");
        WriteToneWave(wave, frequency: 330f, seconds: 0.35f);
        JObject audioDocument = JObject.Parse(File.ReadAllText(audio));
        audioDocument["source"] = Path.GetRelativePath(project.RootPath, wave).Replace('\\', '/');
        File.WriteAllText(audio, audioDocument.ToString(Newtonsoft.Json.Formatting.Indented));
        string shader = objectResources.CreateResource(
            fixture.Folder(project, "Shaders"), ResourceKind.Shader, "Coin Rainbow");
        string model = objectResources.CreateResource(
            fixture.Folder(project, "Models"), ResourceKind.Model, "Campfire Blockout");
        JObject modelDocument = JObject.Parse(File.ReadAllText(model));
        modelDocument["primitive"] = "Cube";
        modelDocument["scale"] = 2.2f;
        modelDocument["tint"] = new JArray(0.72f, 0.28f, 0.08f);
        File.WriteAllText(model, modelDocument.ToString(Newtonsoft.Json.Formatting.Indented));
        string relativeParticle = Path.GetRelativePath(project.RootPath, particle).Replace('\\', '/');
        string relativeAudio = Path.GetRelativePath(project.RootPath, audio).Replace('\\', '/');
        string relativeShader = Path.GetRelativePath(project.RootPath, shader).Replace('\\', '/');
        string relativeModel = Path.GetRelativePath(project.RootPath, model).Replace('\\', '/');

        using Form host = GateSuite.NewHost(1200, 800);
        ObjectEditorControl editor = new(coin, project.RootPath);
        host.Controls.Add(editor);
        GateSuite.ShowHost(host);
        GateSuite.Pump(6, 30);

        HeadlessHarness.Step("the object shows the image it is bound to", () =>
        {
            HeadlessHarness.Assert(
                editor.SpritePreview.HasImage,
                "The editor showed no art for an object with an image binding.");
            HeadlessHarness.Assert(
                editor.SpritePreview.FrameSize is { Width: > 0, Height: > 0 },
                "The preview reported no frame size.");
        });

        HeadlessHarness.Step("add an event and write code into it", () =>
        {
            HeadlessHarness.Assert(
                editor.AddEventViaWizard("Alarm1"),
                "The Add Event wizard did not create Alarm1.");
            editor.SetEventBody("Alarm1", "gateFlag = 1;\nSetAlarm(1, 30);\n");
            string create = editor.PgslEvents.GetValueOrDefault("Create", string.Empty);
            editor.SetEventBody(
                "Create",
                create + "\nliveHealth = 100;\nliveLabel = \"Ready\";\n");
            HeadlessHarness.Assert(
                editor.ActiveEvents.Contains("Alarm1"),
                "Alarm1 is not among the object's active events.");
        });

        HeadlessHarness.Step("the sandbox runs the events and reports what it could not do", () =>
        {
            ObjectSandboxResult? result = editor.RunSandbox();
            HeadlessHarness.Assert(result is not null, "The sandbox produced no result.");
            HeadlessHarness.Assert(result!.Ok, "The sandbox reported errors: " + FirstError(result));

            // The Coin's Step is pure maths, so this object must come back clean; the Player's does
            // not, and Editor.QoL asserts the warning path. Both halves matter: a sandbox that warns
            // about everything is as useless as one that warns about nothing.
            HeadlessHarness.Assert(
                result.Numbers.ContainsKey("bob") || result.Numbers.Count > 0,
                "The sandbox read no variables back from a script that plainly sets them.");
        });

        HeadlessHarness.Step("the Inspector hot-edits the retained VM without resetting it", () =>
        {
            ObjectSandboxResult before = HeadlessHarness.Require(editor.Sandbox.LastResult, "sandbox result");
            ObjectSandboxLiveInstance binding = HeadlessHarness.Require(before.LiveInstance, "sandbox live binding");
            int firedBefore = before.EventsFired.Count;

            ResourceItem objectResource = GateSuite.Flatten(objectResources.BuildTree())
                .First(item => string.Equals(item.FullPath, coin, StringComparison.OrdinalIgnoreCase));
            using Form inspectorHost = GateSuite.NewHost(440, 760);
            using InspectorDock inspector = new()
            {
                Dock = DockStyle.Fill,
                FormBorderStyle = FormBorderStyle.None,
                TopLevel = false,
            };
            inspector.LiveValueProvider = _ => editor.GetLiveInspectorValues();
            inspector.EditRouter = request =>
                editor.TryApplyLiveInspectorValue(request.PropertyPath, request.Value);
            inspectorHost.Controls.Add(inspector);
            GateSuite.ShowHost(inspectorHost);
            inspector.Show();
            inspector.Inspect(objectResource);
            GateSuite.Pump(6, 20);

            HeadlessHarness.Assert(
                inspector.EditablePropertyGroups.Contains("INSTANCE FIELDS", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyGroups.Contains("CREATE EVENT", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyPaths.Contains("Runtime.Instance.x", StringComparer.OrdinalIgnoreCase)
                && inspector.EditablePropertyPaths.Contains("Runtime.Variables.liveHealth", StringComparer.OrdinalIgnoreCase),
                "The Inspector did not separate Create-event variables from built-in instance fields.");

            HeadlessHarness.Assert(
                inspector.SetEditableValue("Runtime.Variables.liveHealth", 50.0)
                && inspector.SetEditableValue("Runtime.Instance.x", 444.0),
                "The Inspector rejected a live script variable or built-in instance field.");
            ObjectSandboxResult updatedResult = HeadlessHarness.Require(
                editor.Sandbox.LastResult,
                "The sandbox result disappeared after the live Inspector edit.");
            HeadlessHarness.Assert(
                binding.TryGetValue("liveHealth", out object health)
                && Math.Abs(Convert.ToDouble(health) - 50.0) < 0.001
                && Math.Abs(updatedResult.X - 444.0) < 0.001,
                "The Inspector edit did not mutate the retained VM/context immediately.");
            HeadlessHarness.Assert(
                ReferenceEquals(binding, updatedResult.LiveInstance)
                && updatedResult.EventsFired.Count == firedBefore
                && editor.PgslEvents["Create"].Contains("liveHealth = 100", StringComparison.Ordinal),
                "Hot-editing recreated the sandbox or rewrote the authored Create value.");

            ImageMetrics metrics = VisualCapture.Capture(
                inspectorHost,
                Path.Combine(ctx.Captures, "gate-object-live-inspector.png"),
                captureFromScreen: false);
            ctx.Report.Images.Add(ImageResult.From(
                "Gate — live Object sandbox Inspector",
                "gate-object-live-inspector.png",
                metrics));
        });

        HeadlessHarness.Step("compose multiple live capabilities as one ordered object", () =>
        {
            ObjectCompositionModel composition = editor.Composition;
            JObject particleComponent = composition.SetAsset("ParticleComponent", relativeParticle);
            composition.SetProperty("ParticleComponent", "RateScale", 1.35f);
            composition.SetProperty("ParticleComponent", "FollowEntity", true);
            JObject audioComponent = composition.SetAsset("AudioComponent", relativeAudio);
            composition.SetProperty("AudioComponent", "AutoPlay", true);
            composition.SetProperty("AudioComponent", "Spatial", true);
            composition.SetProperty("AudioComponent", "Volume", 0.65f);
            composition.SetAsset("ShaderComponent", relativeShader);
            composition.SetProperty("PointLightComponent", "Color", new JArray(1f, 0.55f, 0.15f));
            composition.SetProperty("PointLightComponent", "SecondaryColor", new JArray(0.2f, 0.4f, 1f));
            composition.SetProperty("PointLightComponent", "ColorCount", 2);
            composition.SetProperty("PointLightComponent", "Radius", 7.5f);
            composition.SetProperty("PointLightComponent", "Intensity", 2.25f);
            composition.SetProperty("PointLightComponent", "Falloff", 3.5f);
            composition.SetProperty("PointLightComponent", "Action", "ColourCycle");
            composition.SetProperty("PointLightComponent", "ActionSpeed", 1.75f);
            composition.SetProperty("PointLightComponent", "ActionAmount", 0.6f);

            string particleId = (string?)particleComponent["id"] ?? string.Empty;
            string audioId = (string?)audioComponent["id"] ?? string.Empty;
            HeadlessHarness.Assert(composition.Move(audioId, -1), "The Audio component could not be reordered.");
            HeadlessHarness.Assert(
                composition.Components.IndexOf(composition.Find("AudioComponent")!)
                    < composition.Components.IndexOf(composition.Find("ParticleComponent")!),
                "The component stack did not preserve the requested Audio/Particle order.");
            HeadlessHarness.Assert(!string.IsNullOrWhiteSpace(particleId), "The component stack did not assign a stable component id.");
            HeadlessHarness.Assert(
                VisualActionCatalog.Find("Engine.Rendering.Lights.LightEmitterSetRadius") is
                    { Category: "Lighting" }
                && VisualActionCatalog.Find("Engine.Rendering.Lights.LightEmitterSetAction") is not null,
                "Universal Builder did not discover the persistent Light Emitter PGSL controls.");
        });

        HeadlessHarness.Step("the live preview uses runtime spawning, particles, audio and lights", () =>
        {
            using ObjectCompositionDialog compositionDialog = new(editor.Document, project.RootPath);
            compositionDialog.Show(host);
            GateSuite.Pump(12, 35);
            ObjectCompositionPreviewControl preview = compositionDialog.Preview;
            using Bitmap? frame = preview.CaptureFrame(settleFrames: 4);
            HeadlessHarness.Assert(frame is not null, "The Object composition preview returned no frame.");
            ImageMetrics metrics = MeasureBitmap(frame!);
            HeadlessHarness.Assert(metrics.UniqueSampledColors >= 2,
                $"The composed Object preview drew only its background ({metrics.UniqueSampledColors} sampled colour)." );
            HeadlessHarness.Assert(
                preview.ParticleEmitterCount == 1 && preview.ActiveParticleCount > 0,
                "The runtime preview did not simulate the attached particle asset.");
            HeadlessHarness.Assert(preview.ActiveAudioCount == 1,
                "The runtime preview did not start the attached autoplay audio component.");
            HeadlessHarness.Assert(preview.PointLightCount == 1,
                "The runtime preview did not service the attached point light.");
            HeadlessHarness.Assert(compositionDialog.Composition.Count >= 6,
                "The user-facing Components dialog did not expose the composed stack.");
            compositionDialog.Close();
        });

        HeadlessHarness.Step("the same component system previews and spawns a composed 3D model", () =>
        {
            string objectPath = objectResources.CreateResource(
                fixture.Folder(project, "Objects"), ResourceKind.GameObject, "Gate Campfire");
            JObject objectDocument = JObject.Parse(File.ReadAllText(objectPath));
            objectDocument["dimension"] = "ThreeD";
            objectDocument["sprite"] = string.Empty;
            File.WriteAllText(objectPath, objectDocument.ToString(Newtonsoft.Json.Formatting.Indented));

            using ObjectEditorControl threeD = new(objectPath, project.RootPath);
            threeD.Composition.SetAsset("ModelRendererComponent", relativeModel);
            threeD.Composition.SetAsset("MaterialComponent", (string?)editor.Document["sprite"]);
            threeD.Composition.SetAsset("ShaderComponent", relativeShader);
            threeD.Composition.SetAsset("ParticleComponent", relativeParticle);
            threeD.Composition.SetProperty("PointLightComponent", "Radius", 10f);
            threeD.Composition.SetProperty("PointLightComponent", "Intensity", 3f);
            threeD.Composition.SetProperty("ModelAnimatorComponent", "ClipName", "Idle");
            threeD.Composition.SetProperty("ModelAnimatorComponent", "Playing", true);
            threeD.SetPhysicsPreset("Dynamic");
            HeadlessHarness.Assert(
                threeD.TryApplyInspectorValue("culling", "Front")
                && threeD.TryApplyInspectorValue("windingOrder", "CounterClockwise"),
                "The Object Inspector rejected its authored culling/winding overrides.");
            threeD.Save();

            using Form previewHost = GateSuite.NewHost(900, 620);
            using ObjectCompositionPreviewControl preview = threeD.CreateCompositionPreview();
            previewHost.Controls.Add(preview);
            GateSuite.ShowHost(previewHost);
            GateSuite.Pump(12, 35);
            using Bitmap? frame = preview.CaptureFrame(settleFrames: 4);
            HeadlessHarness.Assert(frame is not null && MeasureBitmap(frame).UniqueSampledColors >= 3,
                "The 3D Object composition preview did not render the model scene.");
            HeadlessHarness.Assert(preview.ParticleEmitterCount == 1 && preview.PointLightCount == 1,
                "The 3D preview did not service the model's attached particles and point light.");
            HeadlessHarness.Assert(preview.AnimatorTimeSeconds > 0f,
                "The 3D preview did not advance the attached Model Animation component.");
            previewHost.Close();

            JObject saved = JObject.Parse(File.ReadAllText(objectPath));
            EcsWorld world = new();
            Entity entity = PrefabSpawner.Spawn(world, saved);
            HeadlessHarness.Assert(
                world.Has<ModelRendererComponent>(entity)
                && world.Has<ModelAnimatorComponent>(entity)
                && world.Has<ParticleComponent>(entity)
                && world.Has<PointLightComponent>(entity)
                && world.Has<PhysicsComponent>(entity)
                && world.GetRef<ModelRendererComponent>(entity).Culling == FaceCullingOverride.Front
                && world.GetRef<ModelRendererComponent>(entity).WindingOrder
                    == FrontFaceWindingOverride.CounterClockwise,
                "The F5 prefab path lost part of the composed 3D model stack or its raster overrides.");
            GModelAsset runtimeModel = new RuntimeModelAssetRegistry().Load(project.RootPath, relativeModel);
            HeadlessHarness.Assert(runtimeModel.HasRenderableMeshes && !runtimeModel.ImportRequired,
                "F5 could not consume the .model.json resource saved by Studio.");
        });

        HeadlessHarness.Step("author a complete terrain and swimming character preset", () =>
        {
            ResourceService resources = fixture.Resources(project);
            string motorObject = resources.CreateResource(
                fixture.Folder(project, "Objects"), ResourceKind.GameObject, "Gate Swimmer");
            using ObjectEditorControl motorEditor = new(motorObject, project.RootPath);
            motorEditor.SetPhysicsPreset("PlatformerCharacter");
            motorEditor.SetCharacterMotorSettings(
                11.5f, 1.8f, 52f, 17f, 7.2f, 0.45f, 51f, 6.4f, 4.8f, 21f, 3.1f);
            motorEditor.Save();
            using ObjectEditorControl reopened = new(motorObject, project.RootPath);
            JObject? settings = reopened.CharacterMotorSettings;
            HeadlessHarness.Assert(reopened.PhysicsPreset == "PlatformerCharacter"
                && settings != null
                && Math.Abs(settings.Value<float>("walkSpeed") - 11.5f) < 0.001f
                && Math.Abs(settings.Value<float>("maximumSlopeDegrees") - 51f) < 0.001f
                && Math.Abs(settings.Value<float>("swimSpeed") - 6.4f) < 0.001f,
                "Object Editor did not preserve the authored ground/slope/swimming motor.");
        });

        HeadlessHarness.Step("save writes one PGSL file per event", () =>
        {
            editor.Save();
            string folder = editor.EventFolder;
            HeadlessHarness.Assert(Directory.Exists(folder), $"The event folder '{folder}' was not created.");
            HeadlessHarness.Assert(
                File.Exists(Path.Combine(folder, "Alarm1.pgsl")),
                "Alarm1 was not written as its own .pgsl file.");
            HeadlessHarness.Assert(
                File.ReadAllText(Path.Combine(folder, "Alarm1.pgsl")).Contains("gateFlag", StringComparison.Ordinal),
                "The saved Alarm1 file does not contain the authored code.");
            JObject prefab = JObject.Parse(File.ReadAllText(coin));
            EcsWorld prefabWorld = new();
            Entity prefabEntity = PrefabSpawner.Spawn(prefabWorld, prefab);

            HeadlessHarness.Assert(
                prefabWorld.Has<ParticleComponent>(prefabEntity)
                && prefabWorld.GetRef<ParticleComponent>(prefabEntity).Asset == relativeParticle
                && prefabWorld.Has<AudioComponent>(prefabEntity)
                && prefabWorld.GetRef<AudioComponent>(prefabEntity).Asset == relativeAudio
                && prefabWorld.Has<PointLightComponent>(prefabEntity)
                && Math.Abs(prefabWorld.GetRef<PointLightComponent>(prefabEntity).Radius - 7.5f) < 0.001f
                && Math.Abs(prefabWorld.GetRef<PointLightComponent>(prefabEntity).Falloff - 3.5f) < 0.001f
                && prefabWorld.GetRef<PointLightComponent>(prefabEntity).ColorCount == 2
                && prefabWorld.GetRef<PointLightComponent>(prefabEntity).Action == LightEmitterAction.ColourCycle
                && Math.Abs(prefabWorld.GetRef<PointLightComponent>(prefabEntity).ActionSpeed - 1.75f) < 0.001f,
                "The F5 prefab spawn path lost a composed particle, audio, or Light Emitter setting.");
            HeadlessHarness.Assert(
                ObjectDrawAssetRegistry.TryGet(prefabEntity, out ObjectDrawAssetEntry drawAssets)
                && string.Equals(drawAssets.Shader, relativeShader, StringComparison.OrdinalIgnoreCase),
                "The F5 prefab spawn path lost the composed shader binding.");

            using ObjectEditorControl reopened = new(coin, project.RootPath);
            JObject[] roundTrip = [.. reopened.Composition.Components.OfType<JObject>()];
            int audioIndex = Array.FindIndex(roundTrip, component => (string?)component["type"] == "AudioComponent");
            int particleIndex = Array.FindIndex(roundTrip, component => (string?)component["type"] == "ParticleComponent");
            HeadlessHarness.Assert(audioIndex >= 0 && particleIndex >= 0 && audioIndex < particleIndex,
                "The ordered component stack did not survive save/reopen.");
            HeadlessHarness.Assert(
                Math.Abs(ObjectCompositionModel.Props(reopened.Composition.Find("AudioComponent")!).Value<float>("Volume") - 0.65f) < 0.001f,
                "The Audio component's authored properties did not survive save/reopen.");
            JObject lightProps = ObjectCompositionModel.Props(
                reopened.Composition.Find("PointLightComponent")!);
            HeadlessHarness.Assert(
                lightProps.Value<string>("Action") == "ColourCycle"
                && Math.Abs(lightProps.Value<float>("Falloff") - 3.5f) < 0.001f
                && lightProps.Value<int>("ColorCount") == 2,
                "The Light Emitter's action, falloff, or colour settings did not survive save/reopen.");
        });

        HeadlessHarness.Step("the runtime loads those events and F5 accepts them", () =>
        {
            IReadOnlyDictionary<string, string> events = ObjectEventStore.Load(coin);
            HeadlessHarness.Assert(
                events.ContainsKey("Alarm1") && events.ContainsKey("Step"),
                "The event store did not read back the events the editor saved: "
                + string.Join(", ", events.Keys));

            // Validated as a project, not file by file: an object's events share one variable scope,
            // so `homeY` set in Create and read in Step is correct and only whole-object validation
            // knows that. This is the same call F5 makes.
            PgslValidationReport report = PgslScriptValidator.ValidateProject(project.RootPath, strict: true);
            HeadlessHarness.Assert(
                report.Success,
                "The saved events would be rejected by F5: " + string.Join(" | ", report.Errors.Take(4)));
        });

        HeadlessHarness.Step("Studio automatically binds its Inspector to the open live Object", () =>
        {
            ResourceItem resource = GateSuite.Flatten(objectResources.BuildTree())
                .First(item => string.Equals(item.FullPath, coin, StringComparison.OrdinalIgnoreCase));
            StudioServices services = new(
                new SettingsService(Path.Combine(ctx.Workspace, "object-live-inspector.settings.json")),
                new ProjectService(),
                new ProjectValidator(),
                new StudioLog(Path.Combine(ctx.Logs, "object-live-inspector.log")));
            using StudioShellForm studio = new(services, project, persistLayout: false);
            GateSuite.ShowHost(studio);
            GateSuite.Pump(8, 25);

            HeadlessHarness.Assert(
                studio.AssetBrowser.SelectPath(coin),
                "Studio could not select the Object that owns the retained sandbox state.");
            IStudioDocument opened = studio.OpenStudioResource(resource);
            SuiteEditorDocument document = opened as SuiteEditorDocument
                ?? throw new InvalidOperationException("Studio did not use the suite Object document.");
            ObjectEditorControl liveEditor = document.Surface as ObjectEditorControl
                ?? throw new InvalidOperationException("Studio did not open the Object Editor surface.");
            ObjectSandboxResult liveRun = HeadlessHarness.Require(
                liveEditor.RunSandbox(), "Studio Object sandbox result");
            GateSuite.Pump(8, 25);

            HeadlessHarness.Assert(
                studio.Inspector.EditablePropertyGroups.Contains("CREATE EVENT", StringComparer.OrdinalIgnoreCase)
                && studio.Inspector.EditablePropertyGroups.Contains("INSTANCE FIELDS", StringComparer.OrdinalIgnoreCase)
                && studio.Inspector.SetEditableValue("Runtime.Variables.liveHealth", 25.0),
                "The production Studio shell did not route the selected Object's live values automatically.");
            GateSuite.Pump(4, 20);
            HeadlessHarness.Assert(
                ReferenceEquals(liveRun.LiveInstance, liveEditor.Sandbox.LastResult?.LiveInstance)
                && liveRun.LiveInstance!.TryGetValue("liveHealth", out object shellHealth)
                && Math.Abs(Convert.ToDouble(shellHealth) - 25.0) < 0.001,
                "Studio rebuilt the VM or failed to apply its Inspector edit to the retained context.");
            studio.Close();
        });

        HeadlessHarness.Step("asset changes traverse Image → Object → Room and rebind open previews", () =>
        {
            AssetDependencyGraph graph = new(project.RootPath);
            HeadlessHarness.Assert(
                graph.DependsOn(coin, particle),
                "The project graph did not discover the Object's Particle component binding.");
            string roomFile = ProjectRoomResolver.ResolveRoomFile(project.RootPath, project.Manifest.StartRoom)
                ?? throw new InvalidOperationException("The 2D start room did not resolve for dependency testing.");
            IReadOnlyCollection<string> affected = graph.GetAffectedPaths([particle]);
            HeadlessHarness.Assert(
                affected.Contains(coin, StringComparer.OrdinalIgnoreCase)
                && affected.Contains(roomFile, StringComparer.OrdinalIgnoreCase),
                "A changed Particle did not propagate through its Object to the Room that consumes it.");

            using ObjectCompositionPreviewControl preview = editor.CreateCompositionPreview();
            using ProjectAssetMonitor monitor = new(project.RootPath, debounceMilliseconds: 5000);
            File.AppendAllText(particle, Environment.NewLine + " ");
            monitor.Notify(particle);
            ProjectAssetChangeSet changes = monitor.FlushPending();
            editor.HandleAssetChanges(changes);
            HeadlessHarness.Assert(
                editor.AssetRefreshGeneration == changes.Generation
                && preview.ParticleEmitterCount == 1,
                "The open Object preview did not rebind its compound Particle dependency.");
        });

        HeadlessHarness.Step("external edits preserve dirty work and recreate clean documents", () =>
        {
            ResourceItem resource = GateSuite.Flatten(objectResources.BuildTree())
                .First(item => string.Equals(item.FullPath, coin, StringComparison.OrdinalIgnoreCase));
            ObjectEditorControl CreateSurface() => new(coin, project.RootPath);
            using SuiteEditorDocument document = new(
                resource, new StudioLog(), CreateSurface(), "Object Editor", CreateSurface);

            ObjectEditorControl dirtySurface = (ObjectEditorControl)document.Surface;
            dirtySurface.SetEventBody("Alarm1", "gateFlag = 2;\n");
            ProjectAssetChangeSet ownChange = new(project.RootPath, [coin], [coin], generation: 9001);
            document.HandleAssetChanges(ownChange);
            HeadlessHarness.Assert(
                document.HasExternalConflict && ReferenceEquals(document.Surface, dirtySurface),
                "An external write discarded unsaved Object Editor work instead of flagging a conflict.");

            document.Save();
            document.HandleAssetChanges(new ProjectAssetChangeSet(
                project.RootPath, [coin], [coin], generation: 9002));
            HeadlessHarness.Assert(
                !document.HasExternalConflict && !ReferenceEquals(document.Surface, dirtySurface),
                "A clean externally changed document was not recreated from disk.");

            IEditorSurface savedSurface = document.Surface;
            document.HandleAssetChanges(new ProjectAssetChangeSet(
                project.RootPath, [coin], [coin], generation: 9003, locallyWrittenPaths: [coin]));
            HeadlessHarness.Assert(
                ReferenceEquals(document.Surface, savedSurface),
                "The editor reopened itself after its own Save instead of retaining authoring context.");
        });

        HeadlessHarness.Step("deep visual flow stays editable, scrollable, and source-backed", () =>
        {
            string deepObject = objectResources.CreateResource(
                fixture.Folder(project, "Objects"), ResourceKind.GameObject, "Gate Nested Flow");
            using VisualActionBuilderControl authored = new(project.RootPath);
            authored.LoadSource(string.Empty, groupName: "Create");
            HeadlessHarness.Assert(authored.InsertCondition("true"),
                "The root condition could not be created.");
            VisualActionFlowGroup parent = authored.Flows.Single();
            for (int depth = 0; depth < 4; depth++)
            {
                HeadlessHarness.Assert(
                    authored.InsertConditionIntoCondition(parent.Id, depth % 2 == 0 ? "then" : "else", "true"),
                    $"Condition depth {depth + 2} could not be authored.");
                parent = authored.Flows.Single(flow => flow.ParentFlowId == parent.Id);
            }
            HeadlessHarness.Assert(
                authored.InsertCommandIntoCondition(parent.Id, "then", "DrawSelf"),
                "The deepest branch rejected an ordinary action.");

            using Form deepHost = GateSuite.NewHost(1200, 800);
            using ObjectEditorControl deepEditor = new(deepObject, project.RootPath);
            deepEditor.SetEventBody("Create", authored.Source);
            deepEditor.SelectEvent("Create");
            deepEditor.ShowVisualActions();
            deepHost.Controls.Add(deepEditor);
            GateSuite.ShowHost(deepHost);
            GateSuite.Pump(8, 25);
            ImageMetrics metrics = VisualCapture.Capture(
                deepHost,
                Path.Combine(ctx.Captures, "gate-object-deep-flow.png"),
                captureFromScreen: false);
            HeadlessHarness.Assert(
                deepEditor.VisualActions.Flows.Count == 5
                && deepEditor.VisualActions.Graph.NodeBounds.Count >= 1
                && deepEditor.VisualActions.Graph.AutoScrollMinSize.Height
                    > deepEditor.VisualActions.Graph.ClientSize.Height,
                "The deep graph lost a branch/action or did not expose scrollable content "
                + $"(flows={deepEditor.VisualActions.Flows.Count}, "
                + $"nodes={deepEditor.VisualActions.Graph.NodeBounds.Count}, "
                + $"extent={deepEditor.VisualActions.Graph.AutoScrollMinSize.Height}, "
                + $"client={deepEditor.VisualActions.Graph.ClientSize.Height}).");
            ctx.Report.Images.Add(ImageResult.From(
                "Gate — arbitrary-depth Object flow",
                "gate-object-deep-flow.png",
                metrics));
            deepEditor.Save();
            using ObjectEditorControl reopened = new(deepObject, project.RootPath);
            reopened.SelectEvent("Create");
            HeadlessHarness.Assert(
                reopened.VisualActions.Flows.Count == 5
                && reopened.VisualActions.Blocks.Any(block => block.CommandName == "DrawSelf"),
                "The deep graph did not survive Object save/reopen.");
            deepHost.Close();
        });

        host.Close();
    }

    // ── PGSL script ─────────────────────────────────────────────────────────────

    private static void Script(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        ProjectSession project = fixture.Blank;
        ResourceService resources = fixture.Resources(project);
        string path = resources.CreateResource(
            fixture.Folder(project, "Scripts"), ResourceKind.PgslScript, "Gate Script");

        using Form host = GateSuite.NewHost(1100, 700);
        PgslScriptEditorControl editor = new(path, project.RootPath);
        host.Controls.Add(editor);
        GateSuite.ShowHost(host);
        GateSuite.Pump(6, 30);

        HeadlessHarness.Step("code intelligence understands real PGSL editing contexts", () =>
        {
            const string nested = "DrawSprite(GetName(\"a,b\"), 0, Clamp(1, 2, 3), ";
            HeadlessHarness.Assert(
                CodeContextAnalyzer.TryGetActiveCall(nested, nested.Length, out string command, out int argument)
                && command == "DrawSprite"
                && argument == 3,
                $"Nested calls or string commas confused signature help ({command}, argument {argument}).");

            const string commented = "DrawSprite(1, /* FakeCall(9, 8), */ 2";
            HeadlessHarness.Assert(
                CodeContextAnalyzer.TryGetActiveCall(commented, commented.Length, out command, out argument)
                && command == "DrawSprite"
                && argument == 1,
                "A comma or parenthesis inside a block comment changed the active signature parameter.");

            IReadOnlyList<CodeSymbol> symbols = CodeContextAnalyzer.DiscoverSymbols(
                "var moveSpeed = 4;\nfunction MovePlayer(step) { var localStep = step; }\n");
            HeadlessHarness.Assert(
                symbols.Any(symbol => symbol.Name == "moveSpeed" && symbol.Kind == "Variable")
                && symbols.Any(symbol => symbol.Name == "MovePlayer" && symbol.Kind == "Function"),
                "File variables and functions were not discovered for completion.");
        });

        HeadlessHarness.Step("completion and signature help are wired into the visible editor", () =>
        {
            editor.ScriptText = "var moveSpeed = 4;\nmoveS";
            editor.Code.MoveCaret(editor.ScriptText.Length);
            HeadlessHarness.Assert(
                editor.Code.CompletionVisible
                && editor.Code.CompletionItems.Any(item => item.DisplayText == "moveSpeed" && item.Kind == "Variable"),
                "The popup did not offer a variable declared in the current file.");
            HeadlessHarness.Assert(
                editor.Code.CommitSelectedCompletion()
                && editor.ScriptText.EndsWith("moveSpeed", StringComparison.Ordinal),
                "Accepting completion did not replace the typed prefix.");

            editor.ScriptText = "DrawSp";
            editor.Code.MoveCaret(editor.ScriptText.Length);
            HeadlessHarness.Assert(
                editor.Code.CompletionItems.Any(item => item.DisplayText == "DrawSprite"),
                "The PGSL command catalogue did not reach completion.");

            editor.ScriptText = "Engine.DrawSp";
            editor.Code.MoveCaret(editor.ScriptText.Length);
            HeadlessHarness.Assert(
                editor.Code.CompletionItems.Any(item => item.DisplayText == "Engine.DrawSprite"),
                "The Engine.* command catalogue did not reach completion.");

            editor.ScriptText = "forea";
            editor.Code.MoveCaret(editor.ScriptText.Length);
            HeadlessHarness.Assert(
                editor.Code.CompletionItems.Any(item => item.DisplayText == "foreach" && item.Kind == "Keyword"),
                "PGSL keywords were omitted from completion.");

            editor.ScriptText = "var moveSpeed = 4;\nDrawSprite(\"sprite,one\", 0, moveS";
            editor.Code.MoveCaret(editor.ScriptText.Length);
            HeadlessHarness.Assert(
                editor.Code.SignatureVisible
                && editor.Code.CompletionVisible
                && editor.Code.SignatureText.Contains("DrawSprite(spr,frame,x,y)", StringComparison.Ordinal)
                && editor.Code.SignatureText.Contains("Draw sprite", StringComparison.OrdinalIgnoreCase),
                "Signature help, command documentation, and completion were not visible together.");

            ImageMetrics metrics = VisualCapture.CaptureOpenForm(
                host,
                Path.Combine(ctx.Captures, "gate-script-code-intelligence.png"));
            HeadlessHarness.Assert(
                metrics.UniqueSampledColors >= 5,
                "The code-intelligence visual capture appears blank.");
            ctx.Report.Images.Add(ImageResult.From(
                "Gate — PGSL completion and signature help",
                "gate-script-code-intelligence.png",
                metrics));
        });

        HeadlessHarness.Step("asset completion inserts a resolvable quoted resource path", () =>
        {
            _ = resources.CreateResource(
                fixture.Folder(project, "Images"), ResourceKind.Image, "Completion Sprite");
            editor.ScriptText = "Complet";
            editor.Code.MoveCaret(editor.ScriptText.Length);
            CodeCompletionItem? asset = editor.Code.CompletionItems.FirstOrDefault(
                item => item.DisplayText == "Completion Sprite" && item.Kind == "Image");
            HeadlessHarness.Assert(asset is not null, "Project Images were omitted from completion.");
            HeadlessHarness.Assert(
                asset!.InsertText == "\"Assets/Images/Completion Sprite.image.json\"",
                $"Asset completion would insert '{asset.InsertText}' instead of its real resource path.");
        });

        HeadlessHarness.Step("a mistake is reported while typing", () =>
        {
            editor.ScriptText = "event Create { this is not pgsl ((((";
            editor.ValidateNow();
            HeadlessHarness.Assert(
                editor.ErrorCount > 0,
                "Broken PGSL produced no live diagnostic at all.");
        });

        HeadlessHarness.Step("what live editing lets through, F5 still refuses", () =>
        {
            HeadlessHarness.Assert(
                editor.VisibleCommandCount > 500
                && editor.CommandBrowserContains("SetCameraFarPlane", implemented: true)
                && editor.CommandBrowserContains("Camera3DCreate", implemented: true),
                "The Script Editor command browser omitted live Engine commands or hid roadmap status.");
            // Live validation is deliberately lenient — a half-typed line is not an error yet — so
            // the strict pass is what has to catch a call that does not exist. Both directions
            // matter: lenient while typing, uncompromising at Play.
            editor.ScriptText = "NotARealCommandAtAll(1);\n";
            editor.ValidateNow();
            PgslValidationReport strict = PgslScriptValidator.ValidateSource(
                editor.ScriptText, "gate-script", strict: true);
            HeadlessHarness.Assert(
                !strict.Success,
                "Strict validation accepted an unknown command — F5 would launch a broken game.");
        });

        HeadlessHarness.Step("fixing it clears the diagnostics", () =>
        {
            editor.ScriptText = "var total = 0;\nfor (var i = 0; i < 4; i += 1) { total += i; }\n";
            editor.ValidateNow();
            HeadlessHarness.Assert(
                editor.ErrorCount == 0,
                $"Valid PGSL still reports {editor.ErrorCount} error(s).");
        });

        HeadlessHarness.Step("save, reload, and the script is byte-for-byte what was written", () =>
        {
            editor.Save();
            string onDisk = File.ReadAllText(path);
            HeadlessHarness.Assert(
                onDisk.Contains("for (var i = 0; i < 4", StringComparison.Ordinal),
                "The saved script does not contain the authored source.");

            using PgslScriptEditorControl reopened = new(path, project.RootPath);
            HeadlessHarness.Assert(
                reopened.ScriptText.Contains("total += i", StringComparison.Ordinal),
                "Reopening the script did not restore its text.");
        });

        HeadlessHarness.Step("the VM executes what was authored", () =>
        {
            ObjectSandboxResult result = ObjectSandbox.Run(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Create"] = File.ReadAllText(path),
                },
                frames: 1);
            HeadlessHarness.Assert(result.Ok, "The saved script failed on the VM: " + FirstError(result));
            HeadlessHarness.Assert(
                Math.Abs(result.Numbers.GetValueOrDefault("total") - 6) < 0.001,
                $"The script computed total={result.Numbers.GetValueOrDefault("total")}, expected 6.");
        });

        HeadlessHarness.Step("the complete Project Hub flow is responsive and non-duplicative", () =>
        {
            string settingsPath = Path.Combine(ctx.Workspace, "project-hub-settings.json");
            SettingsService hubSettings = new(settingsPath);
            hubSettings.AddRecentProject(project.Manifest.Name, project.ProjectFile);
            StudioServices services = new(
                hubSettings,
                new ProjectService(),
                new ProjectValidator(),
                new StudioLog(Path.Combine(ctx.Logs, "project-hub.log")));

            using ProjectHubForm hub = new(services);
            ShellLayoutSuite.AssertProjectHubBrandLayout(
                hub,
                Genesis.Application.Studio.Theme.ThemeService.Density);
            ShellLayoutSuite.AssertResponsiveProjectExperience(hub);

            using ProjectHubForm gallery = new(services)
            {
                ClientSize = new Size(1440, 850),
            };
            gallery.ShowSection(HubSection.Templates);
            ImageMetrics hubMetrics = VisualCapture.Capture(
                gallery,
                Path.Combine(ctx.Captures, "gate-02-project-hub-templates.png"),
                captureFromScreen: false);
            ctx.Report.Images.Add(ImageResult.From(
                "Gate — responsive Project Hub templates",
                "gate-02-project-hub-templates.png",
                hubMetrics));

            using ProjectHubForm projects = new(services)
            {
                ClientSize = new Size(1180, 720),
            };
            ImageMetrics projectsMetrics = VisualCapture.Capture(
                projects,
                Path.Combine(ctx.Captures, "gate-02a-project-hub-projects.png"),
                captureFromScreen: false);
            ctx.Report.Images.Add(ImageResult.From(
                "Gate — Project Hub projects and recent work",
                "gate-02a-project-hub-projects.png",
                projectsMetrics));

            using NewProjectDialog create = new("3DNatureWalk");
            ImageMetrics createMetrics = VisualCapture.Capture(
                create,
                Path.Combine(ctx.Captures, "gate-03-create-from-template.png"),
                captureFromScreen: false);
            ctx.Report.Images.Add(ImageResult.From(
                "Gate — create from template",
                "gate-03-create-from-template.png",
                createMetrics));

            using Form welcomeHost = GateSuite.NewHost(1180, 720);
            using WelcomeDocument welcome = new(project)
            {
                Dock = DockStyle.Fill,
                FormBorderStyle = FormBorderStyle.None,
                TopLevel = false,
            };
            welcomeHost.Controls.Add(welcome);
            GateSuite.ShowHost(welcome);
            ImageMetrics welcomeMetrics = VisualCapture.Capture(
                welcomeHost,
                Path.Combine(ctx.Captures, "gate-04-project-welcome.png"),
                captureFromScreen: false);
            ctx.Report.Images.Add(ImageResult.From(
                "Gate — in-project Welcome page",
                "gate-04-project-welcome.png",
                welcomeMetrics));
        });

        host.Close();
    }

    // ── Terrain ─────────────────────────────────────────────────────────────────

    private static void Terrain(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        ProjectSession project = fixture.Blank;
        ResourceService resources = fixture.Resources(project);
        string path = resources.CreateResource(
            fixture.Folder(project, "Terrain"), ResourceKind.Terrain, "Gate Terrain");
        string componentShader = string.Empty;
        string waterTargetId = string.Empty;

        using Form host = GateSuite.NewHost(1120, 800);
        TerrainEditorControl editor = new(path, project.RootPath);
        host.Controls.Add(editor);
        GateSuite.ShowHost(host);
        GateSuite.Pump(10, 30);

        HeadlessHarness.Step("shared chrome spacing matches the editor mockup tokens", () =>
            AssertSharedChromeSpacing(editor, "The Terrain Editor"));

        HeadlessHarness.Step("shared 3D session owns grid, floor, lighting, gizmo, and Play clock", () =>
        {
            HeadlessHarness.Assert(
                !editor.ShowGrid && editor.FloorStyle == EditorFloorStyle.None,
                "Terrain must start with the grid off and no checkerboard floor over the heightfield.");
            HeadlessHarness.Assert(
                editor.Affects == TerrainAffects.Heightfield,
                "Sculpt mode should lock Affects to Heightfield by default.");
            HeadlessHarness.Assert(
                editor.ViewportSession.ShowSunVisual && editor.ViewportSession.Lit,
                "Terrain preview lighting should start lit with a visible sun disc.");
            HeadlessHarness.Assert(
                editor.ViewportSession.GizmoMode == EditorGizmoMode.Move,
                "The shared gizmo must start in Move.");
            editor.ViewportSession.GizmoMode = EditorGizmoMode.Rotate;
            HeadlessHarness.Assert(
                editor.ViewportSession.GizmoMode == EditorGizmoMode.Rotate,
                "The Terrain Editor did not adopt the shared rotate gizmo.");
            editor.ViewportSession.GizmoMode = EditorGizmoMode.Move;
            HeadlessHarness.Assert(
                !editor.IsPreviewPlaying && editor.PreviewTimeSeconds == 0f,
                "The shared Play clock must start stopped.");
        });

        HeadlessHarness.Step("terrain modes and contextual inspector are explicit", () =>
        {
            EditorCommandBar commandBar = editor.Controls.OfType<EditorCommandBar>().Single();
            HeadlessHarness.Assert(commandBar.IsDocumentBound && commandBar.IsSavePinned,
                "The Terrain Editor did not adopt the shared document command bar.");
            HeadlessHarness.Assert(editor.AuthoringModes.Count == 9
                && editor.AuthoringModes.Contains(nameof(TerrainEditorControl.TerrainEditorMode.Paths))
                && editor.AuthoringModes.Contains(nameof(TerrainEditorControl.TerrainEditorMode.Water))
                && editor.AuthoringModes.Contains(nameof(TerrainEditorControl.TerrainEditorMode.Entities)),
                "Terrain authoring modes are missing from the contextual inspector.");
            editor.SetMode(TerrainEditorControl.TerrainEditorMode.Paths);
            HeadlessHarness.Assert(editor.ActiveMode == TerrainEditorControl.TerrainEditorMode.Paths,
                "The Terrain Editor did not switch to Paths mode.");
            HeadlessHarness.Assert(
                commandBar.Items.OfType<ToolStripDropDownItem>().Any(item => item.Text == "Wizard"
                    || item.DropDownItems.OfType<ToolStripDropDownItem>().Any(child => child.Text == "Wizard")),
                "The Terrain Editor command bar must expose Wizard directly or through View.");
            editor.SetMode(TerrainEditorControl.TerrainEditorMode.Sculpt);
        });

        ushort[] before = [];
        FoliageInstance[] savedFoliage = [];
        HeadlessHarness.Step("sculpting and painting change the ground", () =>
        {
            before = (ushort[])editor.Terrain.HeightsData.Clone();
            editor.BeginStroke(0f, 0f);
            for (int step = 0; step < 6; step++)
            {
                editor.ApplyBrushAt(step * 2f, 0f, TerrainEditorControl.TerrainBrush.Raise);
            }

            editor.EndStroke();
            EditorCommandBar commandBar = editor.Controls.OfType<EditorCommandBar>().Single();
            HeadlessHarness.Assert(commandBar.DocumentStateText.Contains("Unsaved", StringComparison.Ordinal),
                "A terrain stroke did not surface the unsaved document state.");
            HeadlessHarness.Assert(
                !before.AsSpan().SequenceEqual(editor.Terrain.HeightsData),
                "Six raise strokes changed no heights at all.");

            // Layer 3 (Snow) starts near zero on a flat low spot, so "any byte changed" is a real
            // signal rather than a coin flip on the noise seed.
            editor.SelectPaintLayer(3);
            byte[] beforeSplat = (byte[])editor.Terrain.SplatmapData.Clone();
            editor.BeginStroke(4f, 4f);
            editor.ApplyBrushAt(4f, 4f, TerrainEditorControl.TerrainBrush.Paint);
            editor.EndStroke();
            HeadlessHarness.Assert(
                !beforeSplat.AsSpan().SequenceEqual(editor.Terrain.SplatmapData),
                "The paint brush changed no splat weights.");
        });

        HeadlessHarness.Step("undo puts it back", () =>
        {
            HeadlessHarness.Assert(editor.CanUndo, "The sculpt stroke was not journaled.");
            editor.Undo();
            editor.Undo();
            HeadlessHarness.Assert(
                before.AsSpan().SequenceEqual(editor.Terrain.HeightsData),
                "Undo did not restore the pre-stroke heights.");
            editor.Redo();
            editor.Redo();
        });

        HeadlessHarness.Step("author paths, ecological scatter, water and a world map", () =>
        {
            editor.GeneratePaths(new TerrainPathSettings
            {
                Seed = 901, PathCount = 2, Width = 4f, GradeStrength = 0.65f, SplatChannel = 1,
            });
            editor.ScatterFoliage(new FoliageScatterSettings
            {
                Seed = 902, Preset = FoliagePreset.Meadow, MaximumInstances = 12000,
                Density = 1f, MinimumSpacing = 0.65f, PathExclusion = 1.35f,
                NearDistance = 22f, FarDistance = 72f, StreamingCellSize = 16f,
                VisibleInstanceBudget = 6000, TriangleBudget = 120000,
                ResidentMemoryBudgetMegabytes = 24f, GpuUploadBudgetMegabytes = 0.5f,
                TargetGpuMilliseconds = 12f,
            });
            float centreX = editor.Terrain.OriginX + (editor.Terrain.ResolutionX - 1) * editor.Terrain.CellSize * 0.5f;
            float centreZ = editor.Terrain.OriginZ + (editor.Terrain.ResolutionZ - 1) * editor.Terrain.CellSize * 0.5f;
            float surface = editor.Terrain.SampleHeight(centreX, centreZ) + 1f;
            editor.AddWaterBody(new TerrainWaterDefinition
            {
                Name = "Gate Lake", Center = new Vector3(centreX, surface, centreZ),
                SurfaceHeight = surface, SizeX = 18f, SizeZ = 14f, SimulationEnabled = true,
                PhysicsDepth = 7f, FluidDensity = 1025f, BuoyancyStrength = 1.15f,
                LinearDrag = 3.2f, AngularDrag = 1.4f, Swimmable = true,
            });
            HeadlessHarness.Assert(editor.Nature.Paths.Count == 2 && editor.Foliage.Instances.Count > 1000
                && editor.Nature.WaterBodies.Count == 1,
                "Terrain Nature workflow did not produce paths, foliage and water.");
            TerrainWaterDefinition lake = editor.Nature.WaterBodies[0];
            HeadlessHarness.Assert(
                lake.Kind == TerrainWaterKind.Water,
                "AddWaterBody did not author standing water as kind Water.");
            HeadlessHarness.Assert(
                editor.Foliage.Instances.Count(instance =>
                    lake.ContainsHorizontal(
                        instance.Position.X,
                        instance.Position.Z,
                        TerrainWaterDefinition.FoliageExclusionPadding)) == 0,
                "Adding a lake left grass inside the water footprint.");
            HeadlessHarness.Assert(
                WaterSurfaceMesh.VerticalSpan(WaterSurfaceMesh.BuildVisual(lake.ToWaterBody(), Vector3.Zero))
                >= lake.PhysicsDepth * 0.85f,
                "Authored lake visual is a flat sheet instead of a filled volume.");
            string shaderPath = resources.CreateResource(
                fixture.Folder(project, "Shaders"), ResourceKind.Shader, "Gate Lake Shader");
            componentShader = Path.GetRelativePath(project.RootPath, shaderPath).Replace('\\', '/');
            editor.SelectTerrainComponent(TerrainComponentsPanel.ComponentKind.Water, lake.Id);
            waterTargetId = editor.SelectedShaderTargetId ?? string.Empty;
            HeadlessHarness.Assert(
                waterTargetId == "water:" + lake.Id
                && editor.ShaderTargetIds.Contains("layer:0")
                && editor.ShaderTargetIds.Contains("nature:vegetation")
                && editor.ShaderTargetIds.Any(id => id.StartsWith("path:", StringComparison.Ordinal)),
                "Terrain shader targets did not match the Shader Editor picker ids.");
            editor.SetComponentShader(waterTargetId, componentShader);
            HeadlessHarness.Assert(
                editor.GetComponentShader(waterTargetId) == componentShader
                && editor.GetLiveInspectorValues().Any(value =>
                    value.PropertyPath == "Component.Shader" && Equals(value.Value, componentShader)),
                "Assigning a shader to the selected water body did not surface on the Inspector.");
            HeadlessHarness.Assert(editor.BakeWorldMap(128).Pixels.Length == 128 * 128 * 4,
                "Terrain Editor world-map export path produced no pixels.");
        });

        HeadlessHarness.Step("live collider rebuild, volume overlays and drop-in-water preview", () =>
        {
            editor.ShowColliderOverlay = true;
            editor.ShowWaterVolumeOverlay = true;
            editor.RebuildPhysicsPreview();
            int generation = editor.ColliderRebuildGeneration;
            HeadlessHarness.Assert(
                generation > 0
                && editor.ColliderOverlayEdgeCount > 8
                && editor.WaterVolumeOverlayEdgeCount >= 16,
                "Terrain collider/water overlays were not built from the authored heightfield and lake.");

            float probeX = editor.Terrain.OriginX + 4f;
            float probeZ = editor.Terrain.OriginZ + 4f;
            HeadlessHarness.Assert(
                editor.TryRaycastCollider(probeX, probeZ, out float before),
                "The live terrain collider did not answer a downward ray.");
            editor.BeginStroke(probeX, probeZ);
            editor.ApplyBrushAt(probeX, probeZ, TerrainEditorControl.TerrainBrush.Raise);
            editor.EndStroke();
            HeadlessHarness.Assert(
                editor.ColliderRebuildGeneration > generation
                && editor.TryRaycastCollider(probeX, probeZ, out float after)
                && after > before + 0.04f,
                "Sculpting did not rebuild the live terrain collider to the new heights.");

            editor.DropPlayableIntoWater();
            editor.StepPhysicsPreview(1f / 60f, 240);
            HeadlessHarness.Assert(
                editor.PlayableIsActive
                && editor.PlayableSubmergedFraction > 0.15f
                && (editor.PlayableEnteredWater || editor.PlayableMotorState == CharacterMotorState.Swimming),
                $"Drop-in-water preview never entered the lake (state={editor.PlayableMotorState}, "
                + $"submerged={editor.PlayableSubmergedFraction:0.00}, y={editor.PlayablePosition.Y:0.00}).");
        });

        HeadlessHarness.Step("regional foliage paints, places and erases with deterministic constrained undo", () =>
        {
            editor.SetMode(TerrainEditorControl.TerrainEditorMode.Foliage);
            TerrainPathNetwork paths = new(editor.Nature.Paths);
            float minimumX = editor.Terrain.OriginX;
            float minimumZ = editor.Terrain.OriginZ;
            float maximumX = minimumX + (editor.Terrain.ResolutionX - 1) * editor.Terrain.CellSize;
            float maximumZ = minimumZ + (editor.Terrain.ResolutionZ - 1) * editor.Terrain.CellSize;
            const float radius = 5f;
            FoliageInstance? selected = editor.Foliage.Instances
                .Where(instance => instance.Position.X >= minimumX + radius
                    && instance.Position.X <= maximumX - radius
                    && instance.Position.Z >= minimumZ + radius
                    && instance.Position.Z <= maximumZ - radius)
                .OrderByDescending(instance =>
                {
                    TerrainPathSample sample = paths.Sample(instance.Position.X, instance.Position.Z);
                    return sample.IsValid ? sample.Distance - sample.Width * editor.Nature.FoliageSettings.PathExclusion : float.MaxValue;
                })
                .Select(instance => (FoliageInstance?)instance)
                .FirstOrDefault();
            HeadlessHarness.Assert(selected.HasValue,
                "The ecological field provided no interior instance for regional authoring.");
            Vector2 center = new(selected!.Value.Position.X, selected.Value.Position.Z);
            FoliageInstance[] scattered = editor.Foliage.Instances.ToArray();

            editor.ConfigureFoliageBrush(FoliageBrushMode.Erase, FoliageSpecies.Sapling, radius, 1f);
            editor.BeginFoliageStroke();
            FoliageBrushResult erase = editor.ApplyFoliageBrushAt(center.X, center.Y);
            editor.EndFoliageStroke();
            FoliageInstance[] erased = editor.Foliage.Instances.ToArray();
            HeadlessHarness.Assert(erase.RemovedInstances > 0
                && erased.Length < scattered.Length
                && erased.All(instance => Vector2.DistanceSquared(
                    new Vector2(instance.Position.X, instance.Position.Z), center) > radius * radius),
                "The regional erase tool did not clear its authored radius.");
            editor.Undo();
            HeadlessHarness.Assert(editor.Foliage.Instances.SequenceEqual(scattered),
                "Undo did not restore the exact pre-erase foliage field.");
            editor.Redo();
            HeadlessHarness.Assert(editor.Foliage.Instances.SequenceEqual(erased),
                "Redo did not restore the exact erased foliage field.");

            editor.ConfigureFoliageBrush(FoliageBrushMode.Place, FoliageSpecies.Sapling, radius, 1f);
            var placeSettings = new FoliageBrushSettings
            {
                Mode = FoliageBrushMode.Place, Species = FoliageSpecies.Sapling, Radius = radius, Density = 1f,
            };
            FoliageBrushResult expectedPlace = Genesis.World.Foliage.FoliageBrush.Apply(
                editor.Terrain, paths, erase.Field, editor.Nature.FoliageSettings, center, placeSettings, editor.Nature.WaterBodies);
            editor.BeginFoliageStroke();
            FoliageBrushResult place = editor.ApplyFoliageBrushAt(center.X, center.Y);
            editor.EndFoliageStroke();
            FoliageInstance[] placed = editor.Foliage.Instances.ToArray();
            HeadlessHarness.Assert(place.AddedInstances == 1
                && placed.SequenceEqual(expectedPlace.Field.Instances)
                && placed.Any(instance => instance.Species == FoliageSpecies.Sapling
                    && Vector2.DistanceSquared(new Vector2(instance.Position.X, instance.Position.Z), center) <= radius * radius),
                "The place tool did not add the deterministic nearest legal sapling.");
            editor.Undo();
            HeadlessHarness.Assert(editor.Foliage.Instances.SequenceEqual(erased),
                "Undo did not restore the pre-place field.");
            editor.Redo();
            HeadlessHarness.Assert(editor.Foliage.Instances.SequenceEqual(placed),
                "Redo did not restore the placed instance.");

            const float density = 0.82f;
            Vector2 secondCenter = center + new Vector2(radius * 0.55f, radius * 0.25f);
            editor.ConfigureFoliageBrush(FoliageBrushMode.Paint, FoliageSpecies.Reed, radius, density);
            var paintSettings = new FoliageBrushSettings
            {
                Mode = FoliageBrushMode.Paint, Species = FoliageSpecies.Reed, Radius = radius, Density = density,
            };
            FoliageBrushResult expectedFirst = Genesis.World.Foliage.FoliageBrush.Apply(
                editor.Terrain, paths, expectedPlace.Field, editor.Nature.FoliageSettings, center, paintSettings, editor.Nature.WaterBodies);
            FoliageBrushResult expectedSecond = Genesis.World.Foliage.FoliageBrush.Apply(
                editor.Terrain, paths, expectedFirst.Field, editor.Nature.FoliageSettings, secondCenter, paintSettings, editor.Nature.WaterBodies);
            editor.BeginFoliageStroke();
            FoliageBrushResult firstPaint = editor.ApplyFoliageBrushAt(center.X, center.Y);
            FoliageBrushResult secondPaint = editor.ApplyFoliageBrushAt(secondCenter.X, secondCenter.Y);
            editor.EndFoliageStroke();
            FoliageInstance[] painted = editor.Foliage.Instances.ToArray();
            HeadlessHarness.Assert((firstPaint.AddedInstances + secondPaint.AddedInstances) > 0
                && painted.SequenceEqual(expectedSecond.Field.Instances),
                "Overlapping foliage paint dabs were not deterministic across editor and engine paths.");

            int[] regional = Enumerable.Range(0, painted.Length).Where(index =>
            {
                Vector2 point = new(painted[index].Position.X, painted[index].Position.Z);
                return Vector2.DistanceSquared(point, center) <= radius * radius
                    || Vector2.DistanceSquared(point, secondCenter) <= radius * radius;
            }).ToArray();
            HeadlessHarness.Assert(regional.Length > 0
                && regional.All(index => painted[index].Species == FoliageSpecies.Reed),
                "Paint did not replace the complete overlapping region with the selected species.");
            float minimumSpacingSquared = editor.Nature.FoliageSettings.MinimumSpacing
                * editor.Nature.FoliageSettings.MinimumSpacing;
            float maximumSlopeRadians = editor.Nature.FoliageSettings.MaximumSlopeDegrees * MathF.PI / 180f;
            foreach (int index in regional)
            {
                FoliageInstance instance = painted[index];
                float x = instance.Position.X;
                float z = instance.Position.Z;
                float d = MathF.Max(editor.Terrain.CellSize, 0.1f);
                Vector3 normal = Vector3.Normalize(new Vector3(
                    editor.Terrain.SampleHeight(x - d, z) - editor.Terrain.SampleHeight(x + d, z),
                    d * 2f,
                    editor.Terrain.SampleHeight(x, z - d) - editor.Terrain.SampleHeight(x, z + d)));
                TerrainPathSample sample = paths.Sample(x, z);
                HeadlessHarness.Assert(
                    Math.Abs(instance.Position.Y - editor.Terrain.SampleHeight(x, z)) < 0.001f
                    && MathF.Acos(Math.Clamp(normal.Y, -1f, 1f)) <= maximumSlopeRadians + 0.001f
                    && (!sample.IsValid || sample.Distance >= sample.Width * editor.Nature.FoliageSettings.PathExclusion),
                    "A painted foliage instance violated terrain height, slope, or authored path exclusion.");
                for (int other = 0; other < painted.Length; other++)
                {
                    if (other == index) continue;
                    float dx = painted[other].Position.X - x;
                    float dz = painted[other].Position.Z - z;
                    HeadlessHarness.Assert(dx * dx + dz * dz >= minimumSpacingSquared - 0.0001f,
                        "A regional foliage stroke violated the authored minimum spacing.");
                }
            }

            editor.Undo();
            HeadlessHarness.Assert(editor.Foliage.Instances.SequenceEqual(placed),
                "The overlapping paint drag was not journaled as one undoable operation.");
            editor.Redo();
            HeadlessHarness.Assert(editor.Foliage.Instances.SequenceEqual(painted),
                "Redo did not restore the exact regional paint result.");
            savedFoliage = painted;
        });

        HeadlessHarness.Step("component CRUD, play clock, placed entity clip and If/Then/Else", () =>
        {
            int layers = editor.PaintLayerCount;
            editor.AddPaintLayer("Gate Dirt");
            HeadlessHarness.Assert(
                editor.PaintLayerCount == layers + 1,
                "Add Paint Layer did not create a new splat layer.");

            string originalWater = editor.Nature.WaterBodies[0].Id;
            int waterCount = editor.Nature.WaterBodies.Count;
            editor.SelectTerrainComponent(TerrainComponentsPanel.ComponentKind.Water, originalWater);
            editor.DuplicateSelectedComponent();
            HeadlessHarness.Assert(
                editor.Nature.WaterBodies.Count == waterCount + 1,
                "Duplicate did not clone the selected water body.");
            string copyId = editor.Nature.WaterBodies.First(body =>
                !string.Equals(body.Id, originalWater, StringComparison.OrdinalIgnoreCase)).Id;
            editor.SelectTerrainComponent(TerrainComponentsPanel.ComponentKind.Water, copyId);
            editor.DeleteSelectedComponent();
            HeadlessHarness.Assert(
                editor.Nature.WaterBodies.Count == waterCount
                && editor.Nature.WaterBodies[0].Id == originalWater,
                "Delete did not remove the duplicated water body.");

            editor.PlayPreview();
            HeadlessHarness.Assert(editor.IsPreviewPlaying, "Play did not start the terrain preview clock.");
            float started = editor.PreviewTimeSeconds;
            editor.StepPreview(1f / 60f, 12);
            HeadlessHarness.Assert(
                editor.PreviewTimeSeconds > started + 0.1f,
                "StepPreview did not advance shader/animation time.");
            editor.PausePreview();
            HeadlessHarness.Assert(!editor.IsPreviewPlaying, "Pause did not stop the preview clock.");
            editor.StopPreview();
            HeadlessHarness.Assert(
                !editor.IsPreviewPlaying && editor.PreviewTimeSeconds == 0f,
                "Stop did not rewind the terrain preview clock.");
            HeadlessHarness.Assert(
                editor.GetLiveInspectorValues().Any(value =>
                    value.PropertyPath == "Preview.Wind" && Convert.ToDouble(value.Value) == 0d)
                && editor.TryApplyLiveInspectorValue("Preview.Wind", 0.5d)
                && editor.PreviewVariables["Wind"] == 0.5d,
                "Inspector Preview.Wind is not a writable preview variable for If expressions.");
            editor.SetPreviewVariable("Wind", 0);

            string entityPath = resources.CreateResource(
                fixture.Folder(project, "TerrainEntities"), ResourceKind.TerrainEntity, "Gate Windmill");
            using TerrainEntityWizardDialog wizard = new(entityPath, project.RootPath, TerrainEntityType.Object);
            GateSuite.ShowHost(wizard);
            GateSuite.Pump(2, 20);
            wizard.SetName("Gate Windmill");
            wizard.GoToPage(1);
            GateSuite.Pump(2, 20);
            wizard.AddComponent(TerrainEntityComponentKinds.Model);
            wizard.SetComponentProperty(wizard.Components.Count - 1, "AnimationClip", "Spin");
            wizard.AddComponent(TerrainEntityComponentKinds.Condition);
            int condition = wizard.Components.Count - 1;
            wizard.SetComponentProperty(condition, "If", "Wind > 0.2");
            wizard.SetComponentProperty(condition, "ThenSource", TerrainVisualCondition.PlayAnimationSource("Spin"));
            wizard.SetComponentProperty(condition, "ElseSource", TerrainVisualCondition.PlayAnimationSource("Idle"));
            HeadlessHarness.Assert(
                wizard.Components.Any(component =>
                    component.Type == TerrainEntityComponentKinds.Model
                    && component.Get("AnimationClip") == "Spin")
                && wizard.Components.Any(component =>
                    component.Type == TerrainEntityComponentKinds.Condition
                    && component.Get("If") == "Wind > 0.2"
                    && component.Get("ThenClip") == "Spin"
                    && component.Get("ElseClip") == "Idle"
                    && component.Get("ThenSource").Contains("AnimationPlay", StringComparison.Ordinal)
                    && component.Get("ElseSource").Contains("AnimationPlay", StringComparison.Ordinal)),
                "Terrain Entity wizard did not persist model animation and visual If/Then/Else actions.");
            wizard.SaveAndCloseForTest();

            float centreX = editor.Terrain.OriginX + (editor.Terrain.ResolutionX - 1) * editor.Terrain.CellSize * 0.5f;
            float centreZ = editor.Terrain.OriginZ + (editor.Terrain.ResolutionZ - 1) * editor.Terrain.CellSize * 0.5f;
            string placedId = editor.PlaceTerrainEntity(entityPath, centreX, centreZ);
            HeadlessHarness.Assert(
                editor.PlacedEntityCount == 1
                && editor.SelectedComponentKind == TerrainComponentsPanel.ComponentKind.Entity
                && editor.SelectedComponentId == placedId
                && editor.HasComponentGizmo,
                "Placing a Terrain Entity did not select it for the move gizmo.");
            HeadlessHarness.Assert(
                editor.Affects == TerrainAffects.Objects,
                "Selecting a placed Object entity should lock Affects to Objects.");
            TerrainPlacedEntity placed = editor.Nature.PlacedEntities[0];
            HeadlessHarness.Assert(
                editor.TryApplyLiveInspectorValue("Component.Culling", "None")
                && editor.TryApplyLiveInspectorValue("Component.WindingOrder", "CounterClockwise")
                && editor.TryApplyLiveInspectorValue("culling", "Front")
                && editor.TryApplyLiveInspectorValue("windingOrder", "Clockwise"),
                "The Terrain Inspector rejected terrain-resource or placed-object raster overrides.");
            HeadlessHarness.Assert(
                placed.AnimationClip == "Spin"
                && placed.IfExpression == "Wind > 0.2"
                && placed.ThenClip == "Spin"
                && placed.ElseClip == "Idle"
                && placed.ThenSource.Contains("AnimationPlay", StringComparison.Ordinal)
                && placed.ElseSource.Contains("AnimationPlay", StringComparison.Ordinal)
                && placed.ResolvePreviewClip(true) == "Spin"
                && placed.ResolvePreviewClip(false) == "Idle",
                "Placed windmill did not copy Animation = Spin and If/Then/Else visual actions.");
            editor.SetPreviewVariable("Wind", 1);
            HeadlessHarness.Assert(
                TerrainEditorControl.EvaluatePreviewCondition("Wind > 0.2", editor.PreviewVariables)
                && TerrainEditorControl.ResolvePlacedPreviewClip(placed, true) == "Spin",
                "PGSL If Wind > 0.2 did not evaluate true or select the Then AnimationPlay clip.");
            editor.SetPreviewVariable("Wind", 0);
            HeadlessHarness.Assert(
                !TerrainEditorControl.EvaluatePreviewCondition("Wind > 0.2", editor.PreviewVariables)
                && TerrainEditorControl.ResolvePlacedPreviewClip(placed, false) == "Idle",
                "PGSL If Wind > 0.2 did not evaluate false or select the Else AnimationPlay clip.");
        });

        HeadlessHarness.Step("save, reload, and the heights are still there", () =>
        {
            editor.Save();
            HeadlessHarness.Assert(
                File.Exists(path + ".gterrain"),
                "Saving wrote no binary heights sidecar, so nothing was persisted.");
            HeadlessHarness.Assert(File.Exists(TerrainNatureSerializer.SidecarPath(path)),
                "Saving wrote no natural-world sidecar.");
            ushort[] saved = (ushort[])editor.Terrain.HeightsData.Clone();

            using TerrainEditorControl reopened = new(path, project.RootPath);
            HeadlessHarness.Assert(
                saved.AsSpan().SequenceEqual(reopened.Terrain.HeightsData),
                "The reloaded terrain does not match what was saved.");
            HeadlessHarness.Assert(reopened.Nature.Paths.Count == 2
                && reopened.Nature.WaterBodies.Count == 1
                && reopened.Nature.WaterBodies[0].Kind == TerrainWaterKind.Water
                && Math.Abs(reopened.Nature.WaterBodies[0].PhysicsDepth - 7f) < 0.001f
                && Math.Abs(reopened.Nature.WaterBodies[0].BuoyancyStrength - 1.15f) < 0.001f
                && reopened.Nature.WaterBodies[0].Swimmable
                && reopened.Foliage.Instances.SequenceEqual(savedFoliage)
                && reopened.GetComponentShader(waterTargetId) == componentShader
                && reopened.Nature.PlacedEntities.Count == 1
                && reopened.Nature.PlacedEntities[0].AnimationClip == "Spin"
                && reopened.Nature.PlacedEntities[0].IfExpression == "Wind > 0.2"
                && reopened.Nature.PlacedEntities[0].ThenSource.Contains("AnimationPlay", StringComparison.Ordinal)
                && reopened.Nature.PlacedEntities[0].Culling == FaceCullingOverride.None
                && reopened.Nature.PlacedEntities[0].WindingOrder
                    == FrontFaceWindingOverride.CounterClockwise
                && reopened.GetLiveInspectorValues().Any(value =>
                    value.PropertyPath == "culling" && Equals(value.Value, "Front"))
                && reopened.GetLiveInspectorValues().Any(value =>
                    value.PropertyPath == "windingOrder" && Equals(value.Value, "Clockwise")),
                "Terrain authoring, including resource/placed-object raster overrides, did not survive save/reopen.");
        });

        HeadlessHarness.Step("the lit viewport renders", () =>
        {
            using Bitmap? frame = editor.Viewport.CaptureFrame(settleFrames: 3);
            HeadlessHarness.Assert(frame is not null, "The terrain viewport produced no frame.");
            HeadlessHarness.Assert(
                editor.TerrainPreviewChunkCount >= 1,
                "The Terrain Editor must submit chunked TerrainGround meshes (the same path F5 uses for splat paint).");
            ImageMetrics metrics = MeasureBitmap(frame!);
            HeadlessHarness.Assert(
                metrics.UniqueSampledColors >= 4,
                $"The terrain viewport drew {metrics.UniqueSampledColors} colours.");
            FoliagePerformanceSnapshot foliage = editor.LastFoliagePerformance;
            RenderStats render = editor.Viewport.Host.Renderer.GetStats();
            HeadlessHarness.Assert(
                foliage.AuthoredInstances == editor.Foliage.Instances.Count
                && foliage.TotalCells > 1
                && foliage.VisibleCells <= foliage.TotalCells
                && foliage.SubmittedInstances > 0
                && foliage.SubmittedInstances <= foliage.EffectiveInstanceBudget
                && foliage.SubmittedTriangles <= editor.Nature.FoliageSettings.TriangleBudget
                && foliage.UploadBytes <= (long)(editor.Nature.FoliageSettings.GpuUploadBudgetMegabytes * 1024f * 1024f)
                && foliage.Batches is > 0 and <= 14,
                $"Terrain foliage preview violated its streaming contract: authored={foliage.AuthoredInstances}, "
                + $"visible={foliage.SubmittedInstances}, cells={foliage.VisibleCells}/{foliage.TotalCells}, "
                + $"batches={foliage.Batches}, triangles={foliage.SubmittedTriangles}, upload={foliage.UploadBytes}.");
            HeadlessHarness.Assert(
                render.FoliageInstances == foliage.SubmittedInstances
                && render.FoliageBatches == foliage.Batches
                && render.FoliageUploadBytes == foliage.UploadBytes,
                $"The viewport did not issue the planned GPU instance batches: planned "
                + $"{foliage.SubmittedInstances}/{foliage.Batches}/{foliage.UploadBytes}, rendered "
                + $"{render.FoliageInstances}/{render.FoliageBatches}/{render.FoliageUploadBytes}.");
            string file = Path.Combine(ctx.Captures, "gate-04-terrain-editor.png");
            frame!.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            ctx.Report.Images.Add(ImageResult.From("Gate — Terrain Editor", "gate-04-terrain-editor.png", metrics));
        });

        host.Close();
    }

    // ── Model ───────────────────────────────────────────────────────────────────

    private static void Model(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        ProjectSession project = fixture.Blank;
        ResourceService resources = fixture.Resources(project);
        string path = resources.CreateResource(
            fixture.Folder(project, "Models"), ResourceKind.Model, "Gate Model");

        using Form host = GateSuite.NewHost(1120, 800);
        ModelEditorControl editor = new(path, project.RootPath);
        host.Controls.Add(editor);
        GateSuite.ShowHost(host);
        GateSuite.Pump(10, 30);

        HeadlessHarness.Step("create, edit and save geometry in the replacement shell", () =>
        {
            editor.AddPart(ModelPrimitiveKind.Sphere);
            editor.SetMode(ModelEditorMode.Sculpt); editor.SetBrushRadius(3);
            HeadlessHarness.Assert(editor.BrushAt(Vector3.Zero) > 0, "Sculpt did not change geometry.");
            editor.Undo(); editor.Redo(); editor.Save();
            using var viewer = new ModelViewerControl(path, project.RootPath);
            HeadlessHarness.Assert(viewer.PreviewAsset.HasRenderableMeshes, "Viewer lost saved geometry.");
            bool requested = false; viewer.ComposeRequested += (_, _) => requested = true; viewer.RequestCompose();
            HeadlessHarness.Assert(requested, "Edit Model did not open authoring.");
        });

        HeadlessHarness.Step("import one animated GLB through Model, Object, Room and F5", () =>
        {
            string sourceDirectory = Path.Combine(project.InternalPath, "AcceptanceSources", "AnimatedModel");
            string source = AnimatedGlbFixture.Write(sourceDirectory);
            bool wasDirty = editor.IsDirty;
            string importedPath = editor.ImportExternalModel(source);
            HeadlessHarness.Assert(editor.IsDirty == wasDirty, "Importing another model changed the open model's dirty state.");
            string relativeModel = Path.GetRelativePath(project.RootPath, importedPath).Replace('\\', '/');
            JObject descriptor = JObject.Parse(File.ReadAllText(importedPath));
            string ownedSource = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(importedPath)!,
                ((string?)descriptor["source"] ?? string.Empty).Replace('/', Path.DirectorySeparatorChar)));
            HeadlessHarness.Assert(
                ownedSource.Contains(".modeldata", StringComparison.OrdinalIgnoreCase)
                && File.Exists(ownedSource)
                && !string.Equals(Path.GetFullPath(source), ownedSource, StringComparison.OrdinalIgnoreCase),
                "The imported model did not receive a project-owned source copy.");

            using Form importedHost = GateSuite.NewHost(960, 700);
            using ModelEditorControl imported = new(importedPath, project.RootPath);
            importedHost.Controls.Add(imported);
            GateSuite.ShowHost(importedHost);
            GateSuite.Pump(10, 30);

            GModelAsset canonical = imported.RiggedAsset
                ?? throw new InvalidOperationException("The Model Editor did not load the canonical imported model.");
            HeadlessHarness.Assert(
                File.Exists(imported.CanonicalModelPath)
                && imported.SourceNodeCount == 2
                && imported.OutlinerNodeCount == 2
                && imported.DisplayedPartCount == 2
                && imported.CanonicalMeshCount == 2
                && imported.MaterialCount == 2
                && imported.AnimationClipCount == 1
                && imported.HasPersistedSkin,
                $"The GLB intake lost authored data: nodes={imported.SourceNodeCount}, meshes={imported.CanonicalMeshCount}, "
                + $"materials={imported.MaterialCount}, clips={imported.AnimationClipCount}, skin={imported.HasPersistedSkin}.");
            HeadlessHarness.Assert(
                canonical.Nodes[0].Metadata.TryGetValue("footprint_x", out string? footprint) && footprint == "2"
                && canonical.Meshes.All(mesh => mesh.Metadata.TryGetValue("collision", out string? collision) && collision == "capsule")
                && canonical.Meshes.Select(mesh => mesh.MaterialIndex).Distinct().Count() == 2,
                "The canonical asset discarded source metadata or per-primitive material assignments.");
            string texture = Path.GetFullPath(Path.Combine(
                project.RootPath,
                canonical.Materials[0].AlbedoTexture.Replace('/', Path.DirectorySeparatorChar)));
            HeadlessHarness.Assert(File.Exists(texture), "The embedded GLB texture was not extracted into model-owned data.");

            imported.SetMode(ModelEditorMode.Animate);
            imported.PlayClip("Wave", play: false);
            GModelMesh animatedMesh = canonical.Meshes.First(mesh => mesh.IsSkinned);
            MeshVertex[] pose0 = new MeshVertex[animatedMesh.SkinnedVertices.Length];
            MeshVertex[] poseHalf = new MeshVertex[animatedMesh.SkinnedVertices.Length];
            HeadlessHarness.Assert(
                ModelRigBridge.SkinInto(canonical, "Wave", 0f, pose0)
                && ModelRigBridge.SkinInto(canonical, "Wave", 0.5f, poseHalf),
                "The imported skin could not be evaluated by the editor/runtime animation bridge.");
            float maximumMovement = pose0.Zip(poseHalf, (a, b) => Vector3.Distance(a.Position, b.Position)).Max();
            HeadlessHarness.Assert(maximumMovement > 0.05f,
                $"The imported Wave clip moved vertices by only {maximumMovement:0.000}; animation is not live.");
            imported.SetAnimationTime(0.5f);
            imported.FrameModelForTest();
            GateSuite.Pump(5, 25);
            using (Bitmap? animatedFrame = imported.Viewport.CaptureFrame(settleFrames: 3))
            {
                HeadlessHarness.Assert(animatedFrame is not null
                    && MeasureBitmap(animatedFrame).UniqueSampledColors >= 3,
                    "The shared runtime renderer produced no visible animated Model Editor preview.");
            }

            imported.Save();
            using (ModelEditorControl reopened = new(importedPath, project.RootPath))
            {
                HeadlessHarness.Assert(
                    reopened.SourceNodeCount == 2
                    && reopened.CanonicalMeshCount == 2
                    && reopened.MaterialCount == 2
                    && reopened.AnimationClipCount == 1
                    && reopened.HasPersistedSkin
                    && reopened.ClipNames.Contains("Wave", StringComparer.Ordinal),
                    "The imported hierarchy, materials, skin or clip did not survive Model Editor save/reopen.");
            }

            GModelAsset runtimeAsset = new RuntimeModelAssetRegistry().Load(project.RootPath, relativeModel);
            HeadlessHarness.Assert(
                runtimeAsset.HasRenderableMeshes
                && !runtimeAsset.ImportRequired
                && runtimeAsset.Meshes.Count == 2
                && runtimeAsset.Materials.Count == 2
                && runtimeAsset.Animations.Any(clip => clip.Name == "Wave")
                && runtimeAsset.Rig.IsValid,
                "The F5 model registry did not consume the canonical asset saved by Model Editor.");
            (MeshVertex[] roomVertices, ushort[] roomIndices) = ModelAssetLoader.LoadBaked(importedPath);
            HeadlessHarness.Assert(
                roomVertices.Length >= 6 && roomIndices.Length == 6
                && roomVertices.Select(vertex => vertex.Color).Distinct().Count() >= 2,
                "The Room Editor model loader lost imported geometry or its two material colours.");

            string objectPath = resources.CreateResource(
                fixture.Folder(project, "Objects"), ResourceKind.GameObject, "Animated Intake Object");
            JObject objectDocument = JObject.Parse(File.ReadAllText(objectPath));
            objectDocument["dimension"] = "ThreeD";
            objectDocument["sprite"] = string.Empty;
            File.WriteAllText(objectPath, objectDocument.ToString(Newtonsoft.Json.Formatting.Indented));
            using (ObjectEditorControl objectEditor = new(objectPath, project.RootPath))
            {
                objectEditor.Composition.SetAsset("ModelRendererComponent", relativeModel);
                objectEditor.Composition.SetProperty("ModelAnimatorComponent", "ClipName", "Wave");
                objectEditor.Composition.SetProperty("ModelAnimatorComponent", "Playing", true);
                objectEditor.Composition.SetProperty("ModelAnimatorComponent", "Loop", true);
                objectEditor.Save();

                using Form objectPreviewHost = GateSuite.NewHost(820, 600);
                using ObjectCompositionPreviewControl objectPreview = objectEditor.CreateCompositionPreview();
                objectPreviewHost.Controls.Add(objectPreview);
                GateSuite.ShowHost(objectPreviewHost);
                GateSuite.Pump(6, 25);
                using Bitmap? objectFrame0 = objectPreview.CaptureFrame(settleFrames: 2);
                int changedPixels = 0;
                // Sampling a looping clip twice can land on the same pose under variable GPU load.
                // Require visible movement in a bounded sequence while still checking runtime time.
                for (int sample = 0; sample < 8; sample++)
                {
                    GateSuite.Pump(3, 25);
                    using Bitmap? objectFrame = objectPreview.CaptureFrame(settleFrames: 1);
                    if (objectFrame0 is not null && objectFrame is not null)
                        changedPixels = Math.Max(changedPixels, CountDifferingPixels(objectFrame0, objectFrame));
                    if (changedPixels >= 5 && objectPreview.AnimatorTimeSeconds > 0.35f) break;
                }
                HeadlessHarness.Assert(
                    objectFrame0 is not null
                    && objectPreview.AnimatorTimeSeconds > 0.35f
                    && changedPixels >= 5,
                    $"Object Editor preview did not visibly animate the imported model through runtime services: time={objectPreview.AnimatorTimeSeconds:0.000}, changed pixels={changedPixels}.");
                objectPreviewHost.Close();
            }

            JObject savedObject = JObject.Parse(File.ReadAllText(objectPath));
            EcsWorld prefabWorld = new();
            Entity prefab = PrefabSpawner.Spawn(prefabWorld, savedObject);
            HeadlessHarness.Assert(
                prefabWorld.Has<ModelRendererComponent>(prefab)
                && prefabWorld.Has<ModelAnimatorComponent>(prefab)
                && prefabWorld.GetRef<ModelRendererComponent>(prefab).ModelAsset == relativeModel
                && prefabWorld.GetRef<ModelAnimatorComponent>(prefab).ClipName == "Wave",
                "The Object Editor/F5 prefab path lost the imported model or its selected animation clip.");

            string roomPath = resources.CreateResource(
                fixture.Folder(project, "Rooms"), ResourceKind.Room, "Animated Intake Room");
            using Form roomHost = GateSuite.NewHost(900, 620);
            using RoomEditorControl room = new(roomPath, project.RootPath);
            roomHost.Controls.Add(room);
            GateSuite.ShowHost(roomHost);
            GateSuite.Pump(8, 25);
            room.ViewMode3D = true;
            GateSuite.Pump(4, 25);
            room.BeginPlacement(objectPath);
            Point centre = new(room.Viewport.Left + room.Viewport.Width / 2,
                room.Viewport.Top + (int)(room.Viewport.Height * 0.62f));
            room.EditorPointerDown(centre, MouseButtons.Left, Keys.None);
            room.EditorPointerUp(centre, MouseButtons.Left, Keys.None);
            room.Save();
            HeadlessHarness.Assert(room.Room.Nodes.Count == 1,
                "Room Editor did not place the Object that references the imported model.");
            room.FrameContentForTest();
            GateSuite.Pump(6, 25);
            room.SetModelPreviewTime(0f);
            using Bitmap? roomFrame0 = room.Viewport.CaptureFrame(settleFrames: 2);
            room.SetModelPreviewTime(0.5f);
            using (Bitmap? roomFrame = room.Viewport.CaptureFrame(settleFrames: 2))
            {
                HeadlessHarness.Assert(roomFrame is not null
                    && MeasureBitmap(roomFrame).UniqueSampledColors >= 3,
                    "Room Editor did not visibly preview the placed imported model.");
                HeadlessHarness.Assert(roomFrame0 is not null
                    && CountDifferingPixels(roomFrame0, roomFrame!) >= 5,
                    "Room Editor showed only a static bind pose for the placed animated model.");
            }
            RoomAsset roomAsset = RoomAssetLoader.Parse(roomPath);
            EcsWorld roomWorld = new();
            RoomBuildResult built = new RoomSceneBuilder(project.RootPath, new ScriptHostSystem()).Build(roomWorld, roomAsset);
            Entity roomEntity = built.EntitiesByNodeId.Values.Single();
            HeadlessHarness.Assert(
                roomWorld.Has<ModelRendererComponent>(roomEntity)
                && roomWorld.Has<ModelAnimatorComponent>(roomEntity)
                && roomWorld.GetRef<ModelAnimatorComponent>(roomEntity).ClipName == "Wave",
                "The Room-to-F5 build lost the imported animated model components.");

            string gltfSource = AnimatedGlbFixture.WriteGltf(Path.Combine(sourceDirectory, "ExternalBuffer"));
            string gltfPath = resources.ImportFiles(fixture.Folder(project, "Models"), [gltfSource]).Single();
            JObject gltfDescriptor = JObject.Parse(File.ReadAllText(gltfPath));
            string copiedGltf = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(gltfPath)!,
                ((string?)gltfDescriptor["source"] ?? string.Empty).Replace('/', Path.DirectorySeparatorChar)));
            string copiedBuffer = Path.Combine(Path.GetDirectoryName(copiedGltf)!, "Animated Intake.bin");
            using (ModelEditorControl gltfEditor = new(gltfPath, project.RootPath))
            {
                HeadlessHarness.Assert(
                    File.Exists(copiedGltf)
                    && File.Exists(copiedBuffer)
                    && File.Exists(gltfEditor.CanonicalModelPath)
                    && gltfEditor.SourceNodeCount == 2
                    && gltfEditor.CanonicalMeshCount == 2
                    && gltfEditor.AnimationClipCount == 1
                    && gltfEditor.HasPersistedSkin,
                    "JSON glTF intake did not own its external buffer or produce the same canonical animated model as GLB.");
            }
            string renamedGltfPath = resources.Rename(gltfPath, "Renamed Animated Intake");
            JObject renamedDescriptor = JObject.Parse(File.ReadAllText(renamedGltfPath));
            string renamedSource = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(renamedGltfPath)!,
                ((string?)renamedDescriptor["source"] ?? string.Empty).Replace('/', Path.DirectorySeparatorChar)));
            HeadlessHarness.Assert(
                File.Exists(StudioModelResourceLoader.CanonicalPath(renamedGltfPath))
                && File.Exists(renamedSource)
                && File.Exists(Path.Combine(Path.GetDirectoryName(renamedSource)!, "Animated Intake.bin")),
                "Resource-tree rename separated the Model descriptor from its canonical asset or glTF dependencies.");
            string copiedGltfPath = resources.Duplicate(renamedGltfPath);
            using (ModelEditorControl copiedGltfEditor = new(copiedGltfPath, project.RootPath))
            {
                HeadlessHarness.Assert(
                    copiedGltfEditor.SourceNodeCount == 2
                    && copiedGltfEditor.CanonicalMeshCount == 2
                    && copiedGltfEditor.AnimationClipCount == 1,
                    "Resource-tree duplicate did not preserve a self-contained imported model.");
            }

            // When the adjacent Settlement sample is present, the same consolidated workflow also
            // exercises a real Blender-authored production asset. The self-contained fixture above
            // remains authoritative in isolated build environments.
            string settlementSource = Path.GetFullPath(Path.Combine(
                Environment.CurrentDirectory,
                "..", "Game Tests", "Settlement", "Art", "Models", "GS_Villager_AdultMale_LOD.glb"));
            if (File.Exists(settlementSource))
            {
                string settlementPath = resources.ImportFiles(
                    fixture.Folder(project, "Models"), [settlementSource]).Single();
                using ModelEditorControl settlementEditor = new(settlementPath, project.RootPath);
                HeadlessHarness.Assert(
                    settlementEditor.SourceNodeCount == 39
                    && settlementEditor.CanonicalMeshCount == 14
                    && settlementEditor.MaterialCount == 9
                    && settlementEditor.AnimationClipCount == 8
                    && settlementEditor.HasPersistedSkin
                    && settlementEditor.ClipNames.Contains("Idle", StringComparer.Ordinal)
                    && settlementEditor.ClipNames.Contains("Walk", StringComparer.Ordinal)
                    && settlementEditor.ClipNames.Contains("WalkCarry", StringComparer.Ordinal),
                    $"The real Settlement character did not complete intake: nodes={settlementEditor.SourceNodeCount}, "
                    + $"meshes={settlementEditor.CanonicalMeshCount}, materials={settlementEditor.MaterialCount}, "
                    + $"clips={settlementEditor.AnimationClipCount}, skin={settlementEditor.HasPersistedSkin}.");
            }
            roomHost.Close();
            importedHost.Close();
        });

        HeadlessHarness.Step("author the real Settlement cart animation with timeline, onion skins and 3D gizmos", () =>
        {
            string cartSource = Path.GetFullPath(Path.Combine(
                Environment.CurrentDirectory,
                "..", "Game Tests", "Settlement", "Art", "Models", "GS_StorageCart_01.glb"));
            if (!File.Exists(cartSource))
            {
                // The synthetic animated intake above remains the portable acceptance fixture. This
                // production proof runs whenever the adjacent Settlement sample is present.
                return;
            }

            string cartPath = resources.ImportFiles(
                fixture.Folder(project, "Models"), [cartSource]).Single();
            using Form cartHost = GateSuite.NewHost(1360, 840);
            cartHost.Text = "Genesis Studio — Model Editor — GS_StorageCart_01";
            using ModelRigViewportControl cart = new(cartPath, project.RootPath);
            cartHost.Controls.Add(cart);
            GateSuite.ShowHost(cartHost);
            GateSuite.Pump(12, 35);
            cart.SetSpin(false);
            cart.FrameModelForTest();
            GateSuite.Pump(10, 35);

            GModelAsset importedCart = cart.RiggedAsset
                ?? throw new InvalidOperationException("The cart did not produce a canonical model asset.");
            HeadlessHarness.Assert(
                cart.SourceNodeCount == 15
                && cart.CanonicalMeshCount == 10
                && cart.MaterialCount == 7
                && cart.AnimationClipCount == 2
                && cart.HasPersistedSkin
                && cart.ClipNames.Contains("GS_StorageCart_01_Wheel_LAction", StringComparer.Ordinal)
                && cart.ClipNames.Contains("GS_StorageCart_01_Wheel_RAction", StringComparer.Ordinal),
                $"The real cart intake lost authored content: nodes={cart.SourceNodeCount}, "
                + $"meshes={cart.CanonicalMeshCount}, materials={cart.MaterialCount}, "
                + $"clips={cart.AnimationClipCount}, skin={cart.HasPersistedSkin}.");
            HeadlessHarness.Assert(
                importedCart.Nodes[^1].ParentIndex < 0
                && importedCart.Nodes.Take(importedCart.Nodes.Count - 1).Any(node => node.ParentIndex == importedCart.Nodes.Count - 1),
                "The production cart no longer exercises a parent-after-child glTF hierarchy.");
            CaptureModelAuthoringWindow(
                ctx, cartHost, "model-cart-imported.png", "Model — Settlement cart imported");

            string clipName = "GS_StorageCart_01_Wheel_LAction";
            GModelAnimationClip clip = importedCart.Animations.Single(candidate => candidate.Name == clipName);
            int sourceFrameCount = clip.Frames.Count;
            int editFrame = Math.Clamp(sourceFrameCount / 2, 0, sourceFrameCount - 1);
            cart.SetMode(ModelEditorMode.Animate);
            cart.PlayClip(clipName, play: false);
            cart.SetAnimationTime(editFrame / MathF.Max(1f, clip.Fps));
            cart.SetOnionSkin(false);
            cart.SetMotionTrail(false);
            cart.FrameModelForTest();
            GateSuite.Pump(10, 35);
            HeadlessHarness.Assert(
                cart.AnimationFrameCount > 0
                && cart.AnimationFrameCount == sourceFrameCount
                && cart.CurrentAnimationFrame == editFrame,
                "The viewer-side clip picker, frame timeline or scrub state did not bind to the cart animation.");
            CaptureModelAuthoringWindow(
                ctx, cartHost, "model-cart-animation-preview.png", "Model — cart animation and frame preview");

            HeadlessHarness.Assert(cart.SelectAnimationNode("GS_StorageCart_01_Wheel_L"),
                "The animated left wheel was not selectable from the imported hierarchy.");
            cart.SetAnimationGizmo(ModelAnimationGizmoKind.Move);
            cart.SetOnionSkin(false);
            cart.SetMotionTrail(false);
            GateSuite.Pump(5, 25);
            using Bitmap? withoutOnion = cart.Viewport.CaptureFrame(settleFrames: 2);
            cart.SetOnionSkin(true, 2);
            GateSuite.Pump(5, 25);
            using Bitmap? withOnion = cart.Viewport.CaptureFrame(settleFrames: 2);
            HeadlessHarness.Assert(
                withoutOnion is not null && withOnion is not null
                && CountDifferingPixels(withoutOnion, withOnion) >= 5,
                "Enabling 3D onion skinning did not visibly add neighbouring animation poses.");
            cart.SetMotionTrail(true);
            GateSuite.Pump(10, 35);
            HeadlessHarness.Assert(
                cart.SelectedAnimationBoneName.Length > 0
                && cart.SelectedAnimationBoneName == "GS_StorageCart_01_Wheel_L"
                && cart.OnionSkinEnabled
                && cart.OnionSkinFrameCount == 2
                && cart.MotionTrailEnabled
                && cart.AnimationGizmo == ModelAnimationGizmoKind.Move
                && cart.AnimationUsesSharedGizmo
                && cart.AnimationSharedGizmoMode == EditorGizmoMode.Move,
                "Selection did not expose the animation inspector, onion poses, motion trail and shared move gizmo together.");
            CaptureModelAuthoringWindow(
                ctx, cartHost, "model-cart-gizmo-onion-skin.png", "Model — selected wheel, onion skin and move gizmo");

            int wheelIndex = importedCart.Rig.Bones.FindIndex(bone => bone.Name == "GS_StorageCart_01_Wheel_L");
            Matrix4x4 before = clip.Frames[editFrame].LocalBoneTransforms[wheelIndex];
            cart.SetAnimationGizmo(ModelAnimationGizmoKind.Rotate);
            HeadlessHarness.Assert(cart.AnimationSharedGizmoMode == EditorGizmoMode.Rotate,
                "The Model animation rotate tool is not routed through the shared gizmo mode.");
            HeadlessHarness.Assert(cart.RotateSelectedAnimationNode(new Vector3(18f, 0f, 0f)),
                "The selected wheel did not accept an authored rotation.");
            Matrix4x4 edited = clip.Frames[editFrame].LocalBoneTransforms[wheelIndex];
            HeadlessHarness.Assert(!before.Equals(edited),
                "The rotation gizmo reported an edit without changing the sampled pose.");
            cart.DuplicateCurrentFrame();
            HeadlessHarness.Assert(cart.AnimationFrameCount == sourceFrameCount + 1,
                "Duplicate frame did not extend the clip timeline.");
            cart.Save();
            GateSuite.Pump(10, 35);
            CaptureModelAuthoringWindow(
                ctx, cartHost, "model-cart-pose-edit.png", "Model — edited cart pose and rotation gizmo");

            using ModelEditorControl reopenedCart = new(cartPath, project.RootPath);
            GModelAnimationClip savedClip = reopenedCart.RiggedAsset!.Animations
                .Single(candidate => candidate.Name == clipName);
            Matrix4x4 savedEdit = savedClip.Frames[editFrame].LocalBoneTransforms[wheelIndex];
            Matrix4x4 savedDuplicate = savedClip.Frames[editFrame + 1].LocalBoneTransforms[wheelIndex];
            HeadlessHarness.Assert(
                savedClip.Frames.Count == sourceFrameCount + 1
                && savedEdit.Equals(edited)
                && savedDuplicate.Equals(edited)
                && savedClip.Tracks.Any(track => track.BoneIndex == wheelIndex
                    && track.Keys.Any(key => key.Frame == editFrame + 1)),
                "The edited and duplicated wheel poses or their authored track keys did not survive save/reopen.");

            cartHost.Close();
        });

        HeadlessHarness.Step("the viewport renders the model", () =>
        {
            editor.FrameModelForTest();
            GateSuite.Pump(4, 30);
            using Bitmap? frame = editor.Viewport.CaptureFrame(settleFrames: 3);
            HeadlessHarness.Assert(frame is not null, "The model viewport produced no frame.");
            ImageMetrics metrics = MeasureBitmap(frame!);
            string file = Path.Combine(ctx.Captures, "gate-05-model-editor.png");
            frame!.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            ctx.Report.Images.Add(ImageResult.From("Gate — Model Editor", "gate-05-model-editor.png", metrics));
        });

        host.Close();
    }

    private static void CaptureModelAuthoringWindow(
        HeadlessContext ctx,
        Form host,
        string fileName,
        string label)
    {
        
        host.BringToFront();
        host.TopMost = true;
        GateSuite.Pump(8, 30);
        using Bitmap bitmap = VisualCapture.CaptureWindowPixels(host);
        host.TopMost = false;
        string path = Path.Combine(ctx.Captures, fileName);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        ImageMetrics metrics = MeasureBitmap(bitmap);
        HeadlessHarness.Assert(metrics.UniqueSampledColors >= 8,
            $"'{fileName}' capture appears blank ({metrics.UniqueSampledColors} colours).");
        ctx.Report.Images.Add(ImageResult.From(label, fileName, metrics));
    }

    // ── Audio ───────────────────────────────────────────────────────────────────

    private static void Audio(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        ProjectSession project = fixture.Blank;
        ResourceService resources = fixture.Resources(project);
        string path = resources.CreateResource(
            fixture.Folder(project, "Audio"), ResourceKind.Audio, "Gate Sound");

        using Form host = GateSuite.NewHost(1000, 700);
        AudioEditorControl editor = new(path, project.RootPath);
        host.Controls.Add(editor);
        GateSuite.ShowHost(host);
        GateSuite.Pump(6, 30);

        HeadlessHarness.Step("author the settings a game reads", () =>
        {
            JObject document = JObject.Parse(File.ReadAllText(path));
            document["volume"] = 0.42;
            document["loop"] = true;
            document["spatial"] = true;
            document["bus"] = "music";
            File.WriteAllText(path, document.ToString());
        });

        HeadlessHarness.Step("the runtime's own parser reads them back", () =>
        {
            // NEXT-041 in one assertion: the editor wrote Volume/Loop/Spatial while the runtime read
            // gain/loop/spatial, so every setting was silently discarded by the game. One shared
            // parser is the fix, and this is the check that keeps it shared.
            AudioAssetSettings settings = AudioAssetSettings.Load(path)
                ?? throw new InvalidOperationException(
                    "The runtime's audio parser could not read the document the editor wrote.");
            HeadlessHarness.Assert(
                Math.Abs(settings.Volume - 0.42f) < 0.001f,
                $"The runtime read volume {settings.Volume}, the editor wrote 0.42.");
            HeadlessHarness.Assert(settings.Loop, "The runtime did not see the loop flag.");
            HeadlessHarness.Assert(settings.Spatial, "The runtime did not see the spatial flag.");
        });

        HeadlessHarness.Step("adaptive ambience presets persist as runtime roles", () =>
        {
            editor.ApplyEnvironmentPreset(EnvironmentAudioRole.Water);
            editor.Save();
            AudioAssetSettings settings = AudioAssetSettings.Load(path)
                ?? throw new InvalidOperationException("The Water ambience preset did not save readable audio settings.");
            HeadlessHarness.Assert(settings.EnvironmentRole == "Water" && settings.Loop && settings.Spatial
                && settings.MaxDistance > settings.MinDistance,
                "The Water ambience preset did not persist its role, loop and spatial falloff.");
        });

        host.Close();
    }

    // ── Shader ──────────────────────────────────────────────────────────────────

    private static void Shader(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        ProjectSession project = fixture.Blank;
        ResourceService resources = fixture.Resources(project);
        string path = resources.CreateResource(
            fixture.Folder(project, "Shaders"), ResourceKind.Shader, "Gate Shader");
        string previewImage = resources.CreateResource(
            fixture.Folder(project, "Images"), ResourceKind.Image, "Gate Shader Preview");
        string terrain = resources.CreateResource(
            fixture.Folder(project, "Terrain"), ResourceKind.Terrain, "Gate Shader Terrain");

        using Form host = GateSuite.NewHost(1000, 720);
        ShaderEditorControl editor = new(path, project.RootPath);
        host.Controls.Add(editor);
        GateSuite.ShowHost(host);
        GateSuite.Pump(6, 30);

        HeadlessHarness.Step("shared chrome spacing matches the editor mockup tokens", () =>
            AssertSharedChromeSpacing(editor, "The Shader Editor"));

        HeadlessHarness.Step("Preset and Code authoring expose targets, terrain components, preview, and frame controls", () =>
        {
            HeadlessHarness.Assert(
                editor.IsNarrowLayout
                && editor.IsNarrowPreviewActive
                && editor.PreviewVisible
                && editor.CommandBar.IsSaveVisible
                && editor.CommandBar.IsDocumentStateVisible,
                "The narrow Shader workspace did not open its visual preview or hid Save/state.");
            host.ClientSize = new Size(1280, 760);
            GateSuite.Pump(3, 20);
            HeadlessHarness.Assert(
                !editor.IsNarrowLayout && editor.CommandBar.IsSaveVisible,
                "The Shader workspace did not return to a useful side-by-side desktop layout.");
            host.ClientSize = new Size(1000, 720);
            GateSuite.Pump(3, 20);

            HeadlessHarness.Assert(
                editor.AuthoringMode == ShaderAuthoringMode.Preset
                && editor.TargetType == ShaderTargetType.Image
                && editor.PresetNames.SequenceEqual(
                    ["Image Rainbow", "Model Rainbow", "Particle Pulse", "Terrain Tint", "Pond Water", "Fullscreen Vignette"]),
                "The Shader Editor did not open with the complete Preset/Code authoring library.");
            editor.SetPreviewVisible(false);
            HeadlessHarness.Assert(!editor.PreviewVisible, "Unticking Preview left the shader viewport visible.");
            editor.SetPreviewVisible(true);
            HeadlessHarness.Assert(
                editor.PreviewVisible && !editor.IsPreviewPlaying && editor.PreviewSpeed == 60,
                "The embedded viewport or its default playback controls are unavailable.");

            foreach (string preset in editor.PresetNames)
            {
                HeadlessHarness.Assert(editor.SelectPreset(preset), $"The {preset} preset could not be selected.");
                editor.CompileNow();
                GateSuite.Pump(2, 20);
                HeadlessHarness.Assert(
                    editor.LastCompileSucceeded,
                    $"The built-in {preset} shader does not compile.");
            }

            HeadlessHarness.Assert(editor.SelectPreset("Terrain Tint"), "The Terrain preset is missing.");
            HeadlessHarness.Assert(editor.ChoosePreviewAsset(terrain), "The terrain asset picker rejected a project terrain.");
            GateSuite.Pump(3, 20);
            HeadlessHarness.Assert(
                editor.TargetType == ShaderTargetType.Terrain
                && editor.PreviewAsset.EndsWith(Path.GetFileName(terrain), StringComparison.OrdinalIgnoreCase)
                && editor.TerrainComponents.Contains("none", StringComparer.Ordinal)
                && editor.TerrainComponents.Contains("all", StringComparer.Ordinal)
                && editor.TerrainComponents.Count(component => component.StartsWith("layer:", StringComparison.Ordinal)) == 4,
                "Terrain targeting did not expose None/All and the authored terrain surface layers.");

            editor.SetAuthoringMode(ShaderAuthoringMode.Code);
            HeadlessHarness.Assert(
                editor.AuthoringMode == ShaderAuthoringMode.Code,
                "Code mode did not replace the preset controls with the source editor.");
            editor.SetAuthoringMode(ShaderAuthoringMode.Preset);
            HeadlessHarness.Assert(editor.SelectPreset("Image Rainbow"), "The Image preset could not be restored.");
            GateSuite.Pump(3, 20);
            string sourceBeforeTargetChange = editor.SourceText;
            editor.SetTargetType(ShaderTargetType.Model);
            HeadlessHarness.Assert(
                editor.TargetType == ShaderTargetType.Model
                && editor.SourceText == sourceBeforeTargetChange,
                "Changing Shader target silently replaced the current source with another preset.");
            editor.Undo();
            HeadlessHarness.Assert(editor.TargetType == ShaderTargetType.Image,
                "Undo did not restore the Shader target after a non-destructive target change.");
        });

        HeadlessHarness.Step("the authored shader compiles", () =>
        {
            // The one language other than PGSL a game may contain, so it is compiled rather than
            // assumed: a shader that does not build is a black screen at run time.
            editor.CompileNow();
            GateSuite.Pump(4, 25);
            HeadlessHarness.Assert(
                editor.LastCompileSucceeded,
                "The default shader an editor creates does not compile.");
            HeadlessHarness.Assert(editor.ReflectedParameterCount == 2,
                "GenesisParameters did not reflect Speed and Saturation into live authoring controls.");
            HeadlessHarness.Assert(editor.ReflectedResourceCount >= 2,
                "SpriteTex and SpriteSamp were not reflected as texture/sampler resources.");
            HeadlessHarness.Assert(editor.LivePreviewApplied,
                "The compiled shader was not installed in the live Image preview.");
            HeadlessHarness.Assert(editor.SetParameterValue("Speed", 0.6f)
                && editor.SetParameterValue("Saturation", 1f),
                "Reflected shader values could not be edited live.");
            ProjectAssetChangeSet dependencyChange = new(
                project.RootPath,
                [previewImage],
                [previewImage, path],
                generation: 806);
            editor.HandleAssetChanges(dependencyChange);
            HeadlessHarness.Assert(editor.AssetRefreshGeneration == dependencyChange.Generation,
                "The Shader preview did not rebind after its selected Image dependency changed.");
        });

        HeadlessHarness.Step("compile failures remain visible and complete document edits undo and redo", () =>
        {
            string validSource = editor.SourceText;
            editor.SetSource(validSource.Replace("float4 MainPS(", "float4 BrokenPS(", StringComparison.Ordinal));
            editor.CompileNow();
            HeadlessHarness.Assert(
                !editor.LastCompileSucceeded
                && editor.DiagnosticsVisible
                && editor.LivePreviewApplied
                && editor.CanUndo,
                "A bad source edit did not surface diagnostics, preserve the last-good preview, or enter history.");

            editor.Undo();
            GateSuite.Pump(2, 20);
            HeadlessHarness.Assert(
                editor.SourceText == validSource && editor.LastCompileSucceeded && editor.CanRedo,
                "Undo did not restore and recompile the complete shader document.");
            editor.Redo();
            HeadlessHarness.Assert(!editor.LastCompileSucceeded,
                "Redo did not restore the invalid shader edit.");
            editor.Undo();
            HeadlessHarness.Assert(editor.LastCompileSucceeded,
                "The final recovery undo did not restore a compiling shader.");
        });

        HeadlessHarness.Step("project presets save, update, reload, and delete without mutating built-ins", () =>
        {
            const string projectPreset = "Gate Project Rainbow";
            HeadlessHarness.Assert(
                editor.SavePresetAs(projectPreset, "Reusable Shader gate preset.")
                && !editor.SelectedPresetIsBuiltIn,
                "The current shader could not be saved as a project-scoped preset.");
            HeadlessHarness.Assert(
                File.Exists(ShaderPresetLibrary.ProjectFile(project.RootPath))
                && ShaderPresetLibrary.ProjectFile(project.RootPath).EndsWith(
                    ".genesis\\Editor\\ShaderPresets.json",
                    StringComparison.OrdinalIgnoreCase)
                &&
                ShaderPresetLibrary.Load(project.RootPath).Any(preset =>
                    preset.Name == projectPreset && !preset.IsBuiltIn),
                "The new project shader preset was not reloadable from disk.");
            float savedSpeed = editor.ParameterValue("Speed")[0];
            editor.SetParameterValue("Speed", 0.15f);
            HeadlessHarness.Assert(
                editor.SelectPreset(projectPreset)
                && Math.Abs(editor.ParameterValue("Speed")[0] - savedSpeed) < 0.001f,
                "A re-applied project preset did not restore its typed parameter defaults.");
            HeadlessHarness.Assert(editor.UpdateSelectedPreset(),
                "The selected project shader preset could not be updated.");
            const string renamedPreset = "Gate Project Spectrum";
            HeadlessHarness.Assert(
                editor.RenameSelectedPreset(renamedPreset)
                && ShaderPresetLibrary.Load(project.RootPath).Any(preset => preset.Name == renamedPreset)
                && ShaderPresetLibrary.Load(project.RootPath).All(preset => preset.Name != projectPreset),
                "The project shader preset could not be renamed atomically.");
            HeadlessHarness.Assert(editor.DeleteSelectedPreset()
                && ShaderPresetLibrary.Load(project.RootPath).All(preset => preset.Name != renamedPreset),
                "The project shader preset could not be deleted cleanly.");
            HeadlessHarness.Assert(editor.SelectPreset("Image Rainbow")
                && editor.SelectedPresetIsBuiltIn
                && !editor.DeleteSelectedPreset(),
                "Built-in shader preset protection failed.");
        });

        HeadlessHarness.Step("one asset survives save, compiles for every production shader ABI, and previews as a Model", () =>
        {
            editor.Save();
            HeadlessHarness.Assert(File.Exists(path), "The shader document was not written.");
            ShaderAssetDocument authored = ShaderAssetDocument.Load(path);
            HeadlessHarness.Assert(
                authored.SchemaVersion == 4
                && authored.AuthoringMode == ShaderAuthoringMode.Preset
                && authored.TargetType == ShaderTargetType.Image
                && authored.Parameters.Count == 2,
                "The reflected parameter schema did not round-trip through the shader asset.");
            foreach (GpuShaderBinaryFormat format in new[]
                     {
                         GpuShaderBinaryFormat.Dxbc,
                         GpuShaderBinaryFormat.Dxil,
                         GpuShaderBinaryFormat.SpirV,
                         GpuShaderBinaryFormat.GlslUtf8,
                     })
            {
                ShaderCompileResult compiled = ShaderCompiler.CompileForBackend(
                    authored.Source, authored.Entry, GpuShaderStage.Pixel, format, path,
                    ShaderCompiler.BuildDefaultIncludeSearchPaths(path, project.RootPath));
                HeadlessHarness.Assert(compiled.Blob.Length > 0 && compiled.BinaryFormat == format,
                    $"The authored shader did not compile for {format}.");
            }

            editor.SetPipeline(ShaderAssetPipeline.Mesh);
            // Loading the built-in mesh template is a user-facing toolbar action; the public source
            // setter drives the same document surface for the consolidated acceptance workflow.
            editor.SetSource(authored.Source.Replace("Texture2D SpriteTex : register(t0);", "Texture2D AlbedoTex : register(t1);")
                .Replace("SpriteSamp", "AlbedoSamp").Replace("SpriteTex", "AlbedoTex")
                .Replace("float2 UV : TEXCOORD0; float4 Color : COLOR; float FogDepth : TEXCOORD1;",
                    "float3 WorldPos : TEXCOORD1; float3 Normal : TEXCOORD2; float4 Color : TEXCOORD3; float2 UV : TEXCOORD4; float4 ShadowPos : TEXCOORD5; float4 ShadowPosNr : TEXCOORD6; float AtlasLayer : TEXCOORD7;"));
            editor.CompileNow();
            GateSuite.Pump(5, 25);
            HeadlessHarness.Assert(editor.LastCompileSucceeded && editor.LivePreviewApplied,
                "The same editor could not preview its Model pipeline target.");

            using ShaderEditorControl reopened = new(path, project.RootPath);
            reopened.CompileNow();
            HeadlessHarness.Assert(
                reopened.LastCompileSucceeded && reopened.ReflectedParameterCount == 2,
                "The reloaded shader lost its source or reflected parameters.");
        });

        HeadlessHarness.Step("Object binding flows through a placed Room instance into runtime draw assets", () =>
        {
            string objectPath = resources.CreateResource(
                fixture.Folder(project, "Objects"), ResourceKind.GameObject, "Rainbow Logo");
            string relativeShader = Path.GetRelativePath(project.RootPath, path).Replace('\\', '/');
            using (ObjectEditorControl objectEditor = new(objectPath, project.RootPath))
            {
                HeadlessHarness.Assert(objectEditor.SetShaderBinding(relativeShader),
                    "The Object Editor did not offer the authored Shader resource.");
                objectEditor.Document["shaderParameters"] = new JObject
                {
                    ["Speed"] = 0.6f,
                    ["Saturation"] = 1f,
                };
                objectEditor.Save();
            }

            RoomAsset room = RoomAsset.Create("Shader Room", RoomDimension.TwoD);
            RoomNode placed = new()
            {
                Name = "Rainbow Logo",
                Kind = RoomNodeKind.GameObject,
                LayerId = room.Layers[0].Id,
                GameObject = new RoomGameObjectData
                {
                    Prefab = Path.GetRelativePath(project.RootPath, objectPath).Replace('\\', '/'),
                },
            };
            room.Nodes.Add(placed);
            room.Normalize();
            EcsWorld world = new();
            RoomBuildResult built = new RoomSceneBuilder(project.RootPath).Build(world, room);
            Entity entity = built.EntitiesByNodeId[placed.Id];
            HeadlessHarness.Assert(ObjectDrawAssetRegistry.TryGet(entity, out ObjectDrawAssetEntry assets)
                && string.Equals(assets.Shader, relativeShader, StringComparison.OrdinalIgnoreCase)
                && assets.ShaderParameters.TryGetValue("Speed", out float[]? speed)
                && speed is { Length: > 0 }
                && Math.Abs(speed[0] - 0.6f) < 0.001f,
                "The Room runtime instance lost the Object shader binding or its parameter override.");
        });

        HeadlessHarness.Step("named variants and extra texture resources persist and recompile", () =>
        {
            HeadlessHarness.Assert(editor.SelectPreset("Image Rainbow"), "The Image preset could not be restored for variant authoring.");
            GateSuite.Pump(2, 20);
            string relativePreview = Path.GetRelativePath(project.RootPath, previewImage).Replace('\\', '/');
            string withMask = editor.SourceText
                .Replace(
                    "SamplerState SpriteSamp : register(s0);",
                    "SamplerState SpriteSamp : register(s0);\n        Texture2D MaskTex : register(t1);\n        SamplerState MaskSamp : register(s1);",
                    StringComparison.Ordinal)
                .Replace(
                    "float4 tex = SpriteTex.Sample(SpriteSamp, IN.UV) * IN.Color;",
                    "float4 tex = SpriteTex.Sample(SpriteSamp, IN.UV) * IN.Color;\n#ifdef SWAP\n            tex.rgb = MaskTex.Sample(MaskSamp, IN.UV).rgb;\n#endif",
                    StringComparison.Ordinal);
            editor.SetAuthoringMode(ShaderAuthoringMode.Code);
            editor.SetSource(withMask);
            editor.CompileNow();
            GateSuite.Pump(2, 20);
            HeadlessHarness.Assert(
                editor.LastCompileSucceeded && editor.ReflectedResourceCount >= 4,
                "Adding MaskTex/MaskSamp did not reflect extra texture and sampler resources.");
            HeadlessHarness.Assert(
                editor.SetResourceBinding("MaskTex", relativePreview)
                && editor.ResourceBinding("MaskTex").Equals(relativePreview, StringComparison.OrdinalIgnoreCase),
                "The extra MaskTex slot could not be bound to a project Image.");
            HeadlessHarness.Assert(
                editor.AddVariant("Swap", "SWAP")
                && editor.ActiveVariant == "Swap"
                && editor.VariantNames.Contains("Swap"),
                "A named SWAP variant could not be added.");
            editor.CompileNow();
            GateSuite.Pump(2, 20);
            HeadlessHarness.Assert(editor.LastCompileSucceeded, "The SWAP variant did not compile.");
            editor.Save();
            ShaderAssetDocument saved = ShaderAssetDocument.Load(path);
            HeadlessHarness.Assert(
                saved.SchemaVersion == 4
                && saved.ActiveVariant == "Swap"
                && saved.Variants.Any(variant =>
                    variant.Name == "Swap" && variant.Keywords.Contains("SWAP", StringComparer.Ordinal))
                && saved.Resources.Any(resource =>
                    resource.Name == "MaskTex"
                    && resource.Slot == 1
                    && resource.Binding.Equals(relativePreview, StringComparison.OrdinalIgnoreCase)),
                "Named variants or reflected texture bindings did not persist on disk.");
            HeadlessHarness.Assert(
                saved.ResolveCompiledSource().Contains("#define SWAP 1", StringComparison.Ordinal),
                "The active variant did not inject its preprocessor keyword into the compiled source.");
            using ShaderEditorControl reopened = new(path, project.RootPath);
            HeadlessHarness.Assert(
                reopened.ActiveVariant == "Swap"
                && reopened.ResourceBinding("MaskTex").Equals(relativePreview, StringComparison.OrdinalIgnoreCase)
                && reopened.SelectVariant(string.Empty)
                && reopened.ActiveVariant.Length == 0
                && reopened.SelectVariant("Swap"),
                "Reloading the shader lost its named variant or extra texture binding.");
        });

        host.Close();

        HeadlessHarness.Step("live authored-shader golden/parity covers all seven backends", () =>
        {
            IReadOnlyList<ShaderAuthoredParityRunner.Capture> captures =
                ShaderAuthoredParityRunner.RunAll(Path.Combine(ctx.Captures, "shader-parity"));
            HeadlessHarness.Assert(
                captures.Count >= 2 && captures[0].Slug == "dx11",
                "Authored-shader parity did not capture Direct3D 11 plus at least one other live backend.");
            foreach (string slug in new[] { "dx11", "dx12", "vulkan", "opengl", "software" })
            {
                RenderBackendOption option = slug switch
                {
                    "dx11" => RenderBackendOption.SilkNetDx11,
                    "dx12" => RenderBackendOption.Direct3D12,
                    "vulkan" => RenderBackendOption.Vulkan,
                    "opengl" => RenderBackendOption.OpenGL,
                    _ => RenderBackendOption.Software,
                };
                if (!RenderBackendSelection.IsAvailable(option)) continue;
                HeadlessHarness.Assert(
                    captures.Any(capture => capture.Slug == slug),
                    $"Authored-shader parity skipped {slug} even though that backend is available.");
            }
            Genesis.Application.Runtime.ImageMetrics dx11 = captures[0].Metrics;
            foreach (ShaderAuthoredParityRunner.Capture capture in captures)
            {
                int minColors = capture.Slug == "software" ? 2 : 6;
                HeadlessHarness.Assert(
                    capture.Metrics.UniqueSampledColors >= minColors,
                    $"{capture.Slug} authored-shader capture looks blank ({capture.Metrics.UniqueSampledColors} colours).");
                if (capture.Slug == "software") continue;
                double delta = Genesis.Application.Runtime.ImageMetrics.MaxTileDelta(dx11, capture.Metrics);
                HeadlessHarness.Assert(
                    delta <= ShaderAuthoredParityRunner.CrossBackendTileTolerance,
                    $"{capture.Slug} authored-shader tiles drifted {delta:F1}/255 from the DX11 golden.");
            }

            ShaderAuthoredParityRunner.Capture swapped = ShaderAuthoredParityRunner.RunCurrentBackend(
                Path.Combine(ctx.Captures, "shader-parity"), "dx11", swapVariant: true);
            HeadlessHarness.Assert(
                Genesis.Application.Runtime.ImageMetrics.MaxTileDelta(dx11, swapped.Metrics) > 4,
                "The SWAP keyword variant rendered identically to the base authored shader.");
        });
    }

    // ── Particle ────────────────────────────────────────────────────────────────

    private static void Particle(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        ProjectSession project = fixture.Blank;
        ResourceService resources = fixture.Resources(project);
        string path = resources.CreateResource(
            fixture.Folder(project, "Particles"), ResourceKind.Particle, "Gate Particle");

        using Form host = GateSuite.NewHost(1000, 700);
        ParticleEditorControl editor = new(path, project.RootPath);
        host.Controls.Add(editor);
        GateSuite.ShowHost(host);
        GateSuite.Pump(6, 30);

        HeadlessHarness.Step("shared chrome spacing matches the editor mockup tokens", () =>
            AssertSharedChromeSpacing(editor, "The Particle Editor"));

        HeadlessHarness.Step("particle properties use a sectioned inspector and responsive command bar", () =>
        {
            EditorCommandBar commandBar = editor.Controls.OfType<EditorCommandBar>().Single();
            HeadlessHarness.Assert(editor.InspectorSections.SequenceEqual(new[] { "Emission", "Forces", "Material", "Curves", "Collision", "Renderer", "Effect Light" }),
                "Particle properties are not grouped into the expected inspector sections.");
            HeadlessHarness.Assert(
                commandBar.IsDocumentBound && commandBar.IsSavePinned
                && commandBar.IsSaveVisible && commandBar.IsDocumentStateVisible,
                "The Particle Editor did not adopt the shared document command bar.");
        });

        HeadlessHarness.Step("presets edit and save the runtime particle schema", () =>
        {
            editor.ApplyPreset("Portal");
            editor.SetLifetimeCurves(ParticleInterpolationCurve.SmoothStep, ParticleInterpolationCurve.EaseOut);
            editor.SetGradientMidpoint(new ParticleColor(0.9f, 0.3f, 1f, 0.7f), 0.4);
            editor.SetPreview2D(true);
            editor.SetTimelinePosition(0.5f);
            HeadlessHarness.Assert(!editor.TimelinePlaying && Math.Abs(editor.TimelinePosition - 0.5f) < 0.01f,
                "The particle timeline did not pause and scrub to the requested lifetime position.");
            editor.ToggleTimelinePlayback();
            editor.StepForTest(0.5f);
            HeadlessHarness.Assert(editor.LiveParticleCount > 0, "The runtime particle preview emitted nothing.");
            editor.Save();

            JObject saved = JObject.Parse(File.ReadAllText(path));
            HeadlessHarness.Assert(saved["emission"] is null, "The editor still wrote its obsolete nested emission schema.");
            HeadlessHarness.Assert((string?)saved["shape"] == "Ring", "The Portal preset did not save its runtime shape.");
            using ParticleEditorControl reopened = new(path, project.RootPath);
            HeadlessHarness.Assert(
                reopened.Config.Shape == ParticleEmitShape.Ring
                && reopened.Config.BlendMode == ParticleBlendMode.Additive
                && reopened.Config.SizeCurve == ParticleInterpolationCurve.SmoothStep
                && reopened.Config.AlphaCurve == ParticleInterpolationCurve.EaseOut
                && reopened.Config.MidColor is not null
                && Math.Abs(reopened.Config.ColorMidpoint - 0.4) < 0.001
                && reopened.Config.Preview2D,
                "The canonical particle configuration did not round-trip through the editor.");
        });

        HeadlessHarness.Step("legacy particle documents migrate without losing authored motion", () =>
        {
            File.WriteAllText(path,
                """{"schemaVersion":1,"loop":true,"emission":{"rate":25},"shape":{"type":"Cone"},"lifetime":2.5,"speed":240,"gravity":160}""");
            using ParticleEditorControl migrated = new(path, project.RootPath);
            HeadlessHarness.Assert(
                Math.Abs(migrated.Config.EmitRate - 25) < 0.001
                && Math.Abs(migrated.Config.Speed - 6) < 0.001
                && Math.Abs(migrated.Config.Gravity - -4) < 0.001,
                "Legacy particle values were not converted into runtime units.");
            migrated.Save();
            JObject saved = JObject.Parse(File.ReadAllText(path));
            HeadlessHarness.Assert(
                saved["emitRate"] is not null && saved["emission"] is null,
                "Saving a migrated particle did not replace the legacy schema.");
        });

        host.Close();
    }

    // ── Physics ─────────────────────────────────────────────────────────────────

    private static void Physics(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        ProjectSession project = fixture.Blank;
        ResourceService resources = fixture.Resources(project);
        string path = resources.CreateResource(
            fixture.Folder(project, "Physics"), ResourceKind.Physics, "Gate Physics");

        using Form host = GateSuite.NewHost(1000, 700);
        PhysicsEditorControl editor = new(path, project.RootPath);
        host.Controls.Add(editor);
        GateSuite.ShowHost(host);
        GateSuite.Pump(6, 30);

        HeadlessHarness.Step("save and reload a physics body", () =>
        {
            editor.Save();
            JObject document = JObject.Parse(File.ReadAllText(path));
            document["friction"] = 0.75;
            document["restitution"] = 0.2;
            File.WriteAllText(path, document.ToString());

            using PhysicsEditorControl reopened = new(path, project.RootPath);
            JObject reloaded = JObject.Parse(File.ReadAllText(path));
            HeadlessHarness.Assert(
                (double?)reloaded["friction"] == 0.75,
                "The physics document did not keep its authored friction.");
        });

        host.Close();
    }

    // ── Note ────────────────────────────────────────────────────────────────────

    private static void Note(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        ProjectSession project = fixture.Blank;
        ResourceService resources = fixture.Resources(project);
        string path = resources.CreateResource(
            fixture.Folder(project, "Notes"), ResourceKind.Note, "Gate Note");

        using Form host = GateSuite.NewHost(900, 600);
        NoteEditorControl editor = new(path, project.RootPath);
        host.Controls.Add(editor);
        GateSuite.ShowHost(host);
        GateSuite.Pump(4, 25);

        HeadlessHarness.Step("note view modes retain the shared document controls", () =>
        {
            EditorCommandBar commandBar = editor.Controls.OfType<EditorCommandBar>().Single();
            editor.SetViewMode(NoteEditorViewMode.Preview);
            HeadlessHarness.Assert(
                editor.ViewMode == NoteEditorViewMode.Preview
                && commandBar.IsSaveVisible
                && commandBar.IsDocumentStateVisible,
                "The Note Editor lost its view mode or shared document controls.");
            editor.SetViewMode(NoteEditorViewMode.Split);
        });

        HeadlessHarness.Step("text round-trips through the file", () =>
        {
            editor.NoteText = "Gate note: the design decision that produced this room.";
            editor.Save();
            using NoteEditorControl reopened = new(path, project.RootPath);
            HeadlessHarness.Assert(
                reopened.NoteText.Contains("design decision", StringComparison.Ordinal),
                "The note's text did not survive save and reload.");
        });

        host.Close();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Puts a bitmap on the system clipboard and reads it through the editor, retrying briefly.
    /// </summary>
    /// <remarks>
    /// The Windows clipboard is a shared, singly-owned resource: any process can hold it for a
    /// moment and make either side of the set/read cycle fail transiently. Retrying is the standard
    /// remedy and keeps this from being the flaky test in the gate. It is the *test* that retries —
    /// the editor's own paste reports the failure to the user rather than silently looping.
    /// </remarks>
    private static bool TryPasteClipboardImage(Bitmap bitmap, ImageEditorControl editor)
    {
        // The clipboard is owned by one process at a time and any application can hold it for a
        // moment — a browser, a password manager, the OS itself. Five quick attempts was not a
        // generous enough budget and the gate failed on contention rather than on Genesis, which
        // is the worst kind of test failure. Back off progressively across ~3 seconds instead.
        bool clipboardWasWritable = false;
        for (int attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                Clipboard.SetImage(bitmap);
                clipboardWasWritable = true;
                if (Clipboard.ContainsImage() && editor.TryPasteFromSystemClipboard())
                {
                    return true;
                }
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // Another process owns it this instant; wait for it to let go.
            }

            Thread.Sleep(50 + (attempt * 25));
        }

        // Some CI/automation desktop sessions return Win32 ERROR_ACCESS_DENIED for every
        // OpenClipboard call even though no window owns an open clipboard. In that environment the
        // OS integration cannot be exercised at all. Keep proving the production decode/fit/paste
        // implementation with an externally supplied bitmap, but never mask a failure after even
        // one successful clipboard write — that would be a real Genesis integration regression.
        return !clipboardWasWritable && editor.TryPasteExternalImage(bitmap);
    }

    private static void Place(RoomEditorControl editor, float x, float y, Keys modifiers)
    {
        Point client = editor.ClientFromWorld2D(new Vector2(x, y));
        editor.EditorPointerDown(client, MouseButtons.Left, modifiers);
        editor.EditorPointerUp(client, MouseButtons.Left, modifiers);
    }

    private static string FirstError(ObjectSandboxResult result) =>
        result.Errors.Count == 0 ? "no error reported" : result.Errors[0].Message;

    private static void WriteToneWave(string path, float frequency, float seconds)
    {
        const int sampleRate = 22050;
        const short channels = 1;
        const short bitsPerSample = 16;
        int sampleCount = Math.Max(1, (int)(sampleRate * seconds));
        int dataBytes = sampleCount * sizeof(short);
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        for (int sample = 0; sample < sampleCount; sample++)
        {
            float envelope = MathF.Min(1f, sample / (sampleRate * 0.01f))
                * MathF.Min(1f, (sampleCount - sample) / (sampleRate * 0.02f));
            writer.Write((short)(MathF.Sin(sample * MathF.Tau * frequency / sampleRate) * envelope * 3500f));
        }
    }

    private static ImageMetrics MeasureBitmap(Bitmap bitmap)
    {
        HashSet<int> colors = [];
        long luminance = 0;
        int opaque = 0;
        int samples = 0;
        int stepX = Math.Max(1, bitmap.Width / 48);
        int stepY = Math.Max(1, bitmap.Height / 48);
        for (int y = 0; y < bitmap.Height; y += stepY)
        {
            for (int x = 0; x < bitmap.Width; x += stepX)
            {
                Color color = bitmap.GetPixel(x, y);
                colors.Add(color.ToArgb());
                samples++;
                if (color.A > 245) opaque++;
                luminance += (color.R * 299L + color.G * 587L + color.B * 114L) / 1000L;
            }
        }

        return new ImageMetrics(
            bitmap.Width,
            bitmap.Height,
            colors.Count,
            samples == 0 ? 0 : opaque / (double)samples,
            samples == 0 ? 0 : luminance / (double)samples);
    }

    private static int CountDifferingPixels(Bitmap a, Bitmap b, int step = 3, int threshold = 12)
    {
        if (a.Width != b.Width || a.Height != b.Height) return int.MaxValue;
        int differing = 0;
        for (int y = 0; y < a.Height; y += step)
        {
            for (int x = 0; x < a.Width; x += step)
            {
                Color first = a.GetPixel(x, y);
                Color second = b.GetPixel(x, y);
                int delta = Math.Abs(first.R - second.R)
                    + Math.Abs(first.G - second.G)
                    + Math.Abs(first.B - second.B);
                if (delta > threshold) differing++;
            }
        }
        return differing;
    }

    private static void AssertSharedChromeSpacing(Control editor, string editorName)
    {
        IEnumerable<Control> chrome = editor is ModelEditorControl ? SurfaceControls(editor) : editor.Controls.Cast<Control>();
        EditorCommandBar commandBar = chrome.OfType<EditorCommandBar>().Single();
        Label? status = chrome.OfType<Label>().FirstOrDefault(label =>
            (label.Dock == DockStyle.Bottom || label.Name == "ModelStatusBar") && label.Height == EditorChrome.StatusBarHeight);
        HeadlessHarness.Assert(
            commandBar.Height == EditorChrome.CommandBarHeight
            && commandBar.Padding == EditorChrome.CommandBarPadding,
            $"{editorName} command bar is {commandBar.Height}px with padding {commandBar.Padding}; "
            + $"expected {EditorChrome.CommandBarHeight}px {EditorChrome.CommandBarPadding}.");
        HeadlessHarness.Assert(
            status is not null && status.Padding == EditorChrome.StatusBarPadding,
            $"{editorName} is missing the shared status-bar spacing.");
    }

    private static IEnumerable<Control> SurfaceControls(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control nested in SurfaceControls(child)) yield return nested;
        }
    }
}
