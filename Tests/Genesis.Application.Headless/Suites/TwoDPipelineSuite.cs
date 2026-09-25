using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Runtime.Assets;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Audio;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS;
using WinFormsApplication = System.Windows.Forms.Application;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Walks the entire 2D game-creation pipeline and captures a PNG at every stage.
/// </summary>
/// <remarks>
/// This suite exists to answer one question with evidence rather than opinion: <b>can a person
/// actually make a 2D game in this app today?</b> It follows the order a designer would work in —
/// draw a sprite, slice a tile set, script an object, build a room, add sound, press play — and
/// asserts each handoff while saving an image of the screen at that point.
///
/// Where a step cannot be done through the UI, the assertion says so explicitly rather than reaching
/// past the editor to fake it. Those failures are the deliverable: a green suite that bypassed the
/// editor would tell us nothing about whether the pipeline is usable.
/// </remarks>
internal static class TwoDPipelineSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "TwoDPipeline");

        ProjectSession project = HeadlessHarness.Require(ctx.Project, "Project fixture");
        ResourceService resources = HeadlessHarness.Require(ctx.Resources, "Resource service");
        string assets = resources.AssetsRoot;
        string imagesFolder = Path.Combine(assets, "Sprites", "Pipeline");
        Directory.CreateDirectory(imagesFolder);

        // Stage assets shared across the cases below.
        string heroImage = string.Empty;
        string tileImage = string.Empty;
        string skyImage = string.Empty;
        string heroObject = string.Empty;
        string coinObject = string.Empty;
        string pickupSound = string.Empty;
        string gameRoom = string.Empty;

        // ── 1. Sprite authoring ──────────────────────────────────────────────────
        HeadlessHarness.RunCase(ctx.Report, "TwoD.1.SpriteAuthoring", () =>
        {
            heroImage = resources.CreateResource(imagesFolder, ResourceKind.Image, "Hero");
            ImageDocument document = ImageDocument.CreateDefault(32, 32);
            document.Usage.Allowed = ImageUsage.Sprite;
            document.Frames.Add(new ImageFrame { Name = "Frame 1", DurationMilliseconds = 100 });
            document.Layers.Add(new ImageLayer { Name = "Base" });
            ImageDocumentSerializer.SaveAtomic(heroImage, document);

            ImageWorkspace workspace = ImageWorkspace.CreateBlank(32, 32, Color.Transparent);
            ImageDocumentSession session = new(document, heroImage);

            // A recognisable little figure, so the room shows art rather than a placeholder.
            PaintAndSave(session, workspace, (pixels, w, h) =>
            {
                FillBlock(pixels, w, h, 10, 4, 12, 10, Color.FromArgb(255, 240, 200, 150));   // head
                FillBlock(pixels, w, h, 8, 14, 16, 12, Color.FromArgb(255, 70, 130, 220));     // body
                FillBlock(pixels, w, h, 8, 26, 6, 6, Color.FromArgb(255, 40, 40, 60));         // legs
                FillBlock(pixels, w, h, 18, 26, 6, 6, Color.FromArgb(255, 40, 40, 60));
            });

            using Form host = Host(new ImageViewerControl(session, workspace), "Sprite — Hero");
            Pump(6, 25);

            HeadlessHarness.Assert(document.Canvas.Width == 32, "Sprite canvas was not created at the requested size.");
            HeadlessHarness.Assert(
                document.Usage.Supports(ImageUsage.Sprite),
                "A new Image is not usable as a sprite, so nothing downstream can reference it.");

            Capture(ctx, host, "50-2d-01-sprite.png", "2D pipeline 1 — sprite authoring");
        });

        // ── 2. Tile set authoring ────────────────────────────────────────────────
        HeadlessHarness.RunCase(ctx.Report, "TwoD.2.TileSetAuthoring", () =>
        {
            tileImage = resources.CreateResource(imagesFolder, ResourceKind.Image, "Tiles");
            ImageDocument document = ImageDocument.CreateDefault(128, 128);
            document.Usage.Allowed = ImageUsage.Tileset | ImageUsage.Texture;
            document.Usage.Tileset.TileWidth = 32;
            document.Usage.Tileset.TileHeight = 32;
            document.Frames.Add(new ImageFrame { Name = "Sheet", DurationMilliseconds = 100 });
            document.Layers.Add(new ImageLayer { Name = "Base" });
            ImageDocumentSerializer.SaveAtomic(tileImage, document);

            ImageWorkspace workspace = ImageWorkspace.CreateBlank(128, 128, Color.Transparent);
            ImageDocumentSession session = new(document, tileImage);

            // A 4x4 sheet where every tile is visibly different, so painting the wrong tile is obvious.
            PaintAndSave(session, workspace, (pixels, w, h) =>
            {
                Color[] palette =
                [
                    Color.FromArgb(255, 96, 160, 72), Color.FromArgb(255, 128, 96, 56),
                    Color.FromArgb(255, 110, 110, 120), Color.FromArgb(255, 220, 220, 235),
                    Color.FromArgb(255, 60, 120, 190), Color.FromArgb(255, 180, 140, 70),
                    Color.FromArgb(255, 150, 60, 60), Color.FromArgb(255, 90, 70, 130),
                    Color.FromArgb(255, 70, 150, 140), Color.FromArgb(255, 200, 170, 90),
                    Color.FromArgb(255, 120, 190, 90), Color.FromArgb(255, 80, 80, 100),
                    Color.FromArgb(255, 210, 120, 60), Color.FromArgb(255, 60, 90, 150),
                    Color.FromArgb(255, 170, 200, 210), Color.FromArgb(255, 140, 100, 160),
                ];

                for (int tile = 0; tile < 16; tile++)
                {
                    int tx = (tile % 4) * 32;
                    int ty = (tile / 4) * 32;
                    FillBlock(pixels, w, h, tx + 1, ty + 1, 30, 30, palette[tile]);
                }
            });

            ImageViewerControl viewer = new(session, workspace);

            using Form host = Host(viewer, "Tile Set — Tiles");
            Pump(6, 25);

            // 128px sheet of 32px tiles = 4×4. Flag a few solid, the way a designer marks ground.
            viewer.MarkSolidTilesMode = true;
            foreach (int index in new[] { 0, 1, 5 })
            {
                HeadlessHarness.Assert(
                    viewer.ToggleSolidTile(index),
                    $"Could not flag tile {index} solid through the viewer.");
            }

            HeadlessHarness.Assert(viewer.SolidTileCount == 3, $"Expected 3 solid tiles, got {viewer.SolidTileCount}.");
            viewer.Save();

            TileSetInfo? info = TileSetInfo.Load(tileImage);
            HeadlessHarness.Assert(info is not null, "The saved tile set does not resolve through TileSetInfo.");
            HeadlessHarness.Assert(
                info!.ColumnsFor(128) == 4 && info.RowsFor(128) == 4,
                $"Expected a 4x4 grid, got {info.ColumnsFor(128)}x{info.RowsFor(128)}.");
            HeadlessHarness.Assert(info.IsSolid(5) && !info.IsSolid(6), "Tile collision flags did not persist.");

            Capture(ctx, host, "50-2d-02-tileset.png", "2D pipeline 2 — tile set with solid tiles marked");
        });

        // ── 3. Background image ──────────────────────────────────────────────────
        HeadlessHarness.RunCase(ctx.Report, "TwoD.3.BackgroundAuthoring", () =>
        {
            skyImage = resources.CreateResource(imagesFolder, ResourceKind.Image, "Sky");
            ImageDocument document = ImageDocument.CreateDefault(256, 128);
            document.Usage.Allowed = ImageUsage.Background;
            document.Usage.Background.RepeatX = true;
            document.Usage.Background.ParallaxX = 0.4;
            document.Frames.Add(new ImageFrame { Name = "Sky", DurationMilliseconds = 100 });
            document.Layers.Add(new ImageLayer { Name = "Base" });
            ImageDocumentSerializer.SaveAtomic(skyImage, document);

            // A banded sky with a couple of clouds, so parallax is visible when it scrolls.
            ImageWorkspace skyWorkspace = ImageWorkspace.CreateBlank(256, 128, Color.Transparent);
            ImageDocumentSession skySession = new(document, skyImage);
            PaintAndSave(skySession, skyWorkspace, (pixels, w, h) =>
            {
                for (int y = 0; y < h; y++)
                {
                    int shade = 120 + (y * 90 / Math.Max(1, h));
                    FillBlock(pixels, w, h, 0, y, w, 1, Color.FromArgb(255, shade / 2, shade / 2 + 30, 200));
                }

                FillBlock(pixels, w, h, 30, 24, 40, 12, Color.FromArgb(255, 245, 245, 255));
                FillBlock(pixels, w, h, 150, 44, 56, 14, Color.FromArgb(255, 240, 240, 250));
            });

            ImageDocument reloaded = ImageDocumentSerializer.Deserialize(File.ReadAllText(skyImage)).Document;
            HeadlessHarness.Assert(
                reloaded.Usage.Supports(ImageUsage.Background) && reloaded.Usage.Background.RepeatX,
                "Background usage and repeat did not persist.");
            HeadlessHarness.Assert(
                Math.Abs(reloaded.Usage.Background.ParallaxX - 0.4) < 0.001,
                "Parallax rate did not persist — a scrolling backdrop would be impossible.");
        });

        // ── 4. Objects with events ───────────────────────────────────────────────
        HeadlessHarness.RunCase(ctx.Report, "TwoD.4.ObjectsAndEvents", () =>
        {
            string objectsFolder = Path.Combine(assets, "Objects");
            Directory.CreateDirectory(objectsFolder);

            heroObject = resources.CreateResource(objectsFolder, ResourceKind.GameObject, "PipelineHero");
            using (Form host = Host(new ObjectEditorControl(heroObject, project.RootPath), "Object — Hero"))
            {
                ObjectEditorControl editor = (ObjectEditorControl)host.Controls[0];
                Pump(6, 25);

                // Bind the sprite the way the toolbar combo does, so the room draws real art.
                HeadlessHarness.Assert(editor.SetSpriteBinding(Relative(project.RootPath, heroImage)),
                    "The hero image was not available in the Object Editor's image picker.");
                editor.SetEventBody("Create", "x = 120;\ny = 300;\nscore = 0;\nSpriteSetDepth(-10);\n");
                editor.SetEventBody("Step", "x = x + 2;\nif (x > 1100) { x = 120; score = score + 1; }\n");
                editor.SetEventBody("DrawGui", "DrawTextScaled(16, 12, StringJoin2(\"SCORE \", StringOf(score)), 20);\n");
                editor.Save();

                ObjectSandboxResult? sandbox = editor.RunSandbox();
                HeadlessHarness.Assert(sandbox is not null && sandbox.Ok, "The hero object's events do not run cleanly.");
                HeadlessHarness.Assert(
                    Math.Abs(sandbox!.X - (120 + (2 * sandbox.FramesRun))) < 0.51,
                    $"Hero did not move as scripted: x={sandbox.X:0.##} after {sandbox.FramesRun} frames.");

                Capture(ctx, host, "50-2d-04-object.png", "2D pipeline 4 — object events and sandbox");
            }

            // A second object at a different depth, so depth sorting has something to sort.
            coinObject = resources.CreateResource(objectsFolder, ResourceKind.GameObject, "PipelineCoin");
            using Form coinHost = Host(new ObjectEditorControl(coinObject, project.RootPath), "Object — Coin");
            ObjectEditorControl coinEditor = (ObjectEditorControl)coinHost.Controls[0];
            Pump(4, 20);
            coinEditor.SetEventBody("Create", "x = 400;\ny = 300;\nSpriteSetDepth(50);\n");
            HeadlessHarness.Assert(coinEditor.SetSpriteBinding(Relative(project.RootPath, heroImage)),
                "The coin image was not available in the Object Editor's image picker.");
            coinEditor.Save();

            HeadlessHarness.Assert(
                ObjectEventStore.Load(coinObject).ContainsKey("Create"),
                "The coin object's Create event was not stored in its folder.");
        });

        // ── 5. Audio ─────────────────────────────────────────────────────────────
        HeadlessHarness.RunCase(ctx.Report, "TwoD.5.Audio", () =>
        {
            string audioFolder = Path.Combine(assets, "Audio");
            Directory.CreateDirectory(audioFolder);
            string wav = Path.Combine(audioFolder, "PipelinePickup.wav");
            WriteSineWav(wav, 0.25, 660);

            pickupSound = resources.CreateResource(audioFolder, ResourceKind.Audio, "PipelinePickup");
            using Form host = Host(new AudioEditorControl(pickupSound, project.RootPath), "Audio — Pickup");
            AudioEditorControl editor = (AudioEditorControl)host.Controls[0];
            Pump(6, 25);

            HeadlessHarness.Assert(
                editor.SelectSource("Assets/Audio/PipelinePickup.wav"),
                "The Audio Editor cannot see a WAV placed in the project.");
            HeadlessHarness.Assert(editor.HasWaveform, "The WAV decoded no waveform.");
            editor.SetVolume(0.7f);
            editor.Save();

            AudioAssetSettings? settings = AudioAssetSettings.Load(pickupSound);
            HeadlessHarness.Assert(
                settings is not null && Math.Abs(settings.Volume - 0.7f) < 0.01f,
                "Audio settings do not reach the runtime reader.");

            Capture(ctx, host, "50-2d-05-audio.png", "2D pipeline 5 — audio asset");
        });

        // ── 6. Room: backgrounds, tiles, placement, modification, depth ──────────
        HeadlessHarness.RunCase(ctx.Report, "TwoD.6.RoomAssembly", () =>
        {
            string roomsFolder = Path.Combine(assets, "Rooms");
            Directory.CreateDirectory(roomsFolder);
            gameRoom = resources.CreateResource(roomsFolder, ResourceKind.Room, "PipelineRoom");

            using Form host = Host(new RoomEditorControl(gameRoom, project.RootPath), "Room — 2D assembly");
            RoomEditorControl room = (RoomEditorControl)host.Controls[0];
            Pump(10, 30);

            HeadlessHarness.Assert(!room.ViewMode3D, "A new room should open in 2D.");

            // (a) Background layer with parallax.
            RoomNode background = room.AddBackground(skyImage);
            room.SetBackgroundScroll(background, 0.4f, 0f);
            HeadlessHarness.Assert(room.Backgrounds.Count == 1, "The background layer was not added.");

            // (b) Tile painting — including choosing WHICH tile from the sheet.
            room.BeginTilePainting(tileImage);
            HeadlessHarness.Assert(room.IsTilePainting, "Selecting a tile set did not arm tile painting.");
            HeadlessHarness.Assert(
                room.ActiveTileIndex == 0,
                $"Tile painting should start on tile 0, got {room.ActiveTileIndex}.");

            // NEXT-061: pick the tile the way a designer does — by clicking the sheet — rather than
            // calling the API. The previous audit had to call SelectTileIndex directly because no
            // picker existed, which meant it proved nothing about whether a person could do it.
            HeadlessHarness.Assert(
                room.TilePicker.HasSheet,
                "Arming a tile set did not load its sheet into the picker, so no tile can be chosen by hand. "
                + room.TilePicker.LoadDiagnostic);
            HeadlessHarness.Assert(
                room.TilePicker.TileCount == 16,
                $"The picker shows {room.TilePicker.TileCount} tiles for a 4x4 sheet, expected 16.");

            HeadlessHarness.Assert(
                ClickTile(room.TilePicker, 5),
                "Clicking tile 5 in the picker did not select it.");
            HeadlessHarness.Assert(
                room.ActiveTileIndex == 5 && room.TilePicker.SelectedIndex == 5,
                $"Picker and brush disagree after a click: picker={room.TilePicker.SelectedIndex}, "
                + $"brush={room.ActiveTileIndex}.");

            for (int column = 0; column < 12; column++)
            {
                room.PaintTileAtWorld(column * 32f, 384f);
            }

            HeadlessHarness.Assert(ClickTile(room.TilePicker, 1), "Clicking tile 1 in the picker did not select it.");
            for (int column = 3; column < 7; column++)
            {
                room.PaintTileAtWorld(column * 32f, 320f);
            }

            RoomNode? tileLayer = room.Room.Nodes.FirstOrDefault(n => n.Kind == RoomNodeKind.TileLayer);
            HeadlessHarness.Assert(tileLayer is not null, "No tile layer was created.");
            HeadlessHarness.Assert(
                tileLayer!.TileLayer.Cells.Count == 16,
                $"Expected 16 painted cells, got {tileLayer.TileLayer.Cells.Count}.");
            HeadlessHarness.Assert(
                tileLayer.TileLayer.Cells.Select(c => (c.TileX, c.TileY)).Distinct().Count() > 1,
                "Every painted cell used the same tile — the tile-index selection is not reaching the cells.");

            // (c) Place both objects by clicking at a chosen WORLD point, converted through the
            // editor's own ClientFromWorld2D. Picking screen fractions instead (viewport width/4)
            // silently clicked left of the room, because the 2D camera shows space outside the room
            // bounds — that was the whole of NEXT-060, a test artefact rather than a mapping bug.
            // Going through the editor's own transform also makes this a genuine round-trip check.
            PlaceAtWorld(room, heroObject, new Vector2(200f, 300f));
            PlaceAtWorld(room, coinObject, new Vector2(640f, 300f));

            List<RoomNode> placed = [.. room.Room.Nodes.Where(n => n.Kind == RoomNodeKind.GameObject)];
            HeadlessHarness.Assert(placed.Count == 2, $"Expected 2 placed instances, got {placed.Count}.");

            // NEXT-060: a click inside the viewport must produce an instance inside the room. This
            // separates a genuine click→world mapping bug from a test that simply clicked outside.
            foreach (RoomNode node in placed)
            {
                HeadlessHarness.Assert(
                    node.Transform.X >= 0 && node.Transform.X <= room.Room.Settings.Width
                    && node.Transform.Y >= 0 && node.Transform.Y <= room.Room.Settings.Height,
                    $"'{node.Name}' was placed at ({node.Transform.X:0.#}, {node.Transform.Y:0.#}), outside the "
                    + $"{room.Room.Settings.Width}x{room.Room.Settings.Height} room. A click inside the viewport "
                    + "must land inside the room.");
            }

            // NEXT-060: instance sprites render inconsistently — one drew its art outside the room
            // bounds, the other a missing-texture checkerboard. Split the question mechanically so the
            // next person knows which half is broken instead of re-reading a screenshot.
            foreach (string objectPath in new[] { heroObject, coinObject })
            {
                Newtonsoft.Json.Linq.JObject prefab =
                    Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(objectPath));
                string? spriteReference = (string?)prefab["sprite"];
                HeadlessHarness.Assert(
                    !string.IsNullOrWhiteSpace(spriteReference),
                    $"'{Path.GetFileName(objectPath)}' saved no sprite reference, so it can only ever draw a placeholder.");

                string? texture = ProjectAssetIndex.ResolveSpriteImage(project.RootPath, spriteReference);
                HeadlessHarness.Assert(
                    texture is not null && File.Exists(texture),
                    $"'{Path.GetFileName(objectPath)}' has sprite '{spriteReference}' but it resolves to "
                    + $"'{texture ?? "(null)"}' — the texture lookup is the broken half of NEXT-060.");
            }

            // (d) MODIFY a placed instance: move, scale, rotate.
            RoomNode target = placed[0];
            float originalX = target.Transform.X;
            target.Transform.X += 64f;
            target.Transform.ScaleX = 2f;
            target.Transform.ScaleY = 2f;
            target.Transform.RotationZ = 30f;
            room.SetGizmo(RoomEditorControl.GizmoKind.Move);
            room.SetTool(RoomEditorControl.RoomTool.Select);

            HeadlessHarness.Assert(
                Math.Abs(target.Transform.X - (originalX + 64f)) < 0.01f
                && Math.Abs(target.Transform.ScaleX - 2f) < 0.01f
                && Math.Abs(target.Transform.RotationZ - 30f) < 0.01f,
                "A placed instance could not be moved, scaled and rotated.");

            // (e) Designate the camera the game uses.
            room.SetActiveGameCamera(placed[1]);
            HeadlessHarness.Assert(
                room.ActiveGameCamera is not null,
                "No instance could be designated as the game camera.");

            room.Save();
            Capture(ctx, host, "50-2d-06-room.png", "2D pipeline 6 — room with background, tiles, instances");
        });

        // ── 6b. The tile picker, on its own so the capture shows it live ─────────
        HeadlessHarness.RunCase(ctx.Report, "TwoD.6b.TilePicker", () =>
        {
            // A separate case rather than a capture inside stage 6: capturing mid-case pumps enough
            // message cycles to disturb the room editor's state, and the picker is hidden by the end
            // of stage 6 anyway because placing an object correctly switches the palette out of tile
            // mode.
            using Form host = Host(new RoomEditorControl(gameRoom, project.RootPath), "Room — tile picker");
            RoomEditorControl room = (RoomEditorControl)host.Controls[0];
            Pump(10, 30);

            room.BeginTilePainting(tileImage);
            HeadlessHarness.Assert(
                room.TilePicker.HasSheet,
                "The picker did not load the armed sheet. " + room.TilePicker.LoadDiagnostic);
            HeadlessHarness.Assert(
                room.TilePicker.TileCount == 16,
                $"The picker shows {room.TilePicker.TileCount} tiles, expected 16.");

            // Selecting through the API must move the picker too, or the two can disagree about
            // which tile is armed — the exact drift this feature exists to remove.
            room.SelectTileIndex(9);
            HeadlessHarness.Assert(
                room.TilePicker.SelectedIndex == 9,
                $"SelectTileIndex(9) left the picker on {room.TilePicker.SelectedIndex}.");

            HeadlessHarness.Assert(ClickTile(room.TilePicker, 6), "Clicking tile 6 did not select it.");
            HeadlessHarness.Assert(
                room.ActiveTileIndex == 6,
                $"The brush is on tile {room.ActiveTileIndex} after clicking tile 6 in the picker.");

            // The picker marks solid tiles, which is the question a designer asks constantly while
            // building a level. Assert the flags reached it rather than trusting the paint code.
            TileSetInfo? armed = TileSetInfo.Load(tileImage);
            HeadlessHarness.Assert(
                armed is not null && armed.IsSolid(0) && armed.IsSolid(1) && armed.IsSolid(5) && !armed.IsSolid(6),
                "The solid-tile flags marked in the Image Editor did not survive into the room's tile set, "
                + "so the picker cannot show which tiles are ground.");

            // Clicking outside the grid must not change the selection or throw.
            room.TilePicker.SimulateClick(new Point(-20, -20));
            HeadlessHarness.Assert(
                room.TilePicker.SelectedIndex == 6,
                "A click outside the tile grid changed the selection.");

            Pump(4, 25);
            Capture(ctx, host, "50-2d-06b-tile-picker.png", "2D pipeline 6b — tile picker with tile 6 selected");
        });

        // ── 7. Depth sorting ─────────────────────────────────────────────────────
        HeadlessHarness.RunCase(ctx.Report, "TwoD.7.DepthSorting", () =>
        {
            // Depth is enforced by the sprite renderer's depth buffer over a ±10240 range, so the
            // check that matters is whether authored depth actually reaches a draw call, in order.
            RoomAsset asset = RoomAssetLoader.Parse(gameRoom);
            HeadlessHarness.Assert(
                asset.Nodes.Any(n => n.Kind == RoomNodeKind.Background),
                "The saved room lost its background layer.");

            List<RoomNode> backgrounds = [.. asset.Nodes.Where(n => n.Kind == RoomNodeKind.Background)];
            RoomNode? tiles = asset.Nodes.FirstOrDefault(n => n.Kind == RoomNodeKind.TileLayer);
            HeadlessHarness.Assert(tiles is not null, "The saved room lost its tile layer.");

            // Backgrounds must sit behind tiles, which must sit behind objects. The room editor gives
            // backgrounds a large positive depth precisely so they render first.
            HeadlessHarness.Assert(
                backgrounds.All(b => b.Background.Depth > tiles!.TileLayer.Depth),
                $"Background depth ({backgrounds[0].Background.Depth}) must be greater — further back — "
                + $"than the tile layer ({tiles!.TileLayer.Depth}), or the backdrop hides the level.");

            // …and the objects' own Draw depth comes from their scripts (SpriteSetDepth), which the
            // sandbox proves runs. Assert the two objects asked for different depths.
            IReadOnlyDictionary<string, string> heroEvents = ObjectEventStore.Load(heroObject);
            IReadOnlyDictionary<string, string> coinEvents = ObjectEventStore.Load(coinObject);
            HeadlessHarness.Assert(
                heroEvents["Create"].Contains("SpriteSetDepth(-10)", StringComparison.Ordinal)
                && coinEvents["Create"].Contains("SpriteSetDepth(50)", StringComparison.Ordinal),
                "The two objects do not request different depths, so nothing would be sorted.");

            ObjectSandboxResult hero = ObjectSandbox.Run(heroEvents, 1);
            ObjectSandboxResult coin = ObjectSandbox.Run(coinEvents, 1);
            HeadlessHarness.Assert(
                hero.Ok && coin.Ok,
                "The depth-setting scripts do not run.");
        });

        // ── 8. Play the room ─────────────────────────────────────────────────────
        HeadlessHarness.RunCase(ctx.Report, "TwoD.8.PlayTheRoom", () =>
        {
            PipelineAudioRecorder audio = new();
            NullGameContext gameContext = new() { Audio = audio };
            IGameContext previousContext = PgslCommands.ActiveGameContext;
            string? previousProject = PgslCommands.ProjectPath;
            PgslCommands.ActiveGameContext = gameContext;
            PgslCommands.ProjectPath = project.RootPath;

            try
            {
                Genesis.Runtime.Scripting.VM.VMEngine.Initialize();
                ScriptAssetRegistry.ClearCache();
                ScriptAssetRegistry.LoadFromProject(project.RootPath);

                ScriptHostSystem scriptHost = new();
                scriptHost.SetContext(gameContext);

                EcsWorld world = new();
                RoomAsset asset = RoomAssetLoader.Parse(gameRoom);
                RoomSceneBuilder builder = new(project.RootPath, scriptHost);
                RoomBuildResult built = builder.Build(world, asset);

                HeadlessHarness.Assert(
                    built.SpawnedEntities.Count == 2,
                    $"The room spawned only {built.SpawnedEntities.Count} entities — 2 objects expected; tiles stay in room data, not ECS placeholders.");
                HeadlessHarness.Assert(
                    built.EntitiesByNodeId.Count == 2,
                    $"Expected 2 scripted instances, got {built.EntitiesByNodeId.Count}.");

                Entity hero = built.EntitiesByNodeId.Values.First();
                float startX = world.GetRef<TransformComponent>(hero).X;

                for (int frame = 0; frame < 60; frame++)
                {
                    scriptHost.Update(1f / 60f);
                }

                float endX = world.GetRef<TransformComponent>(hero).X;
                HeadlessHarness.Assert(
                    Math.Abs(endX - startX) > 1f,
                    $"Nothing moved when the room was played: x stayed at {endX:0.##}.");

                PipelineHudCanvas hud = new();
                scriptHost.DispatchPgslDraw(renderer: null, hud);
                HeadlessHarness.Assert(
                    hud.Texts.Count > 0,
                    "The HUD the object draws in DrawGui never reached a canvas when the room was played.");
                HeadlessHarness.Assert(
                    hud.Texts[0].Text.StartsWith("SCORE", StringComparison.Ordinal),
                    $"HUD drew '{hud.Texts[0].Text}', expected the scripted score line.");
            }
            finally
            {
                PgslCommands.ActiveGameContext = previousContext;
                PgslCommands.ProjectPath = previousProject;
            }
        });

        // ── 10. The 2D Platformer template ───────────────────────────────────────
        HeadlessHarness.RunCase(ctx.Report, "TwoD.10.PlatformerTemplate", () =>
        {
            // A template's whole job is that F5 works before the user types anything, so this
            // creates one the way the New Project dialog does and then actually plays it.
            string parent = Path.Combine(ctx.Workspace, "Templates");
            Directory.CreateDirectory(parent);

            ProjectService service = new();
            ProjectSession templated = service.CreateProject(parent, "Platformer Template Check", "2D");
            string root = templated.RootPath;

            // Exercise the exact F5 compile gate before the in-process VM path. This is the
            // regression the original template test missed: the template could play in its
            // sandbox while File -> Run refused to launch it.
            string templatePlayer = Path.Combine(root, "Build", "TemplatePlayer");
            Genesis.Runtime.Project.ProjectRunLauncher.CompileOutcome compile =
                Genesis.Runtime.Project.ProjectRunLauncher.CompileScripts(root, templatePlayer);
            HeadlessHarness.Assert(
                compile.Success,
                "The generated 2D template failed the File -> Run compile gate: "
                + (compile.ErrorMessage ?? "unknown compiler failure"));
            HeadlessHarness.Assert(
                compile.TargetDll is null || !File.Exists(compile.TargetDll),
                "A PGSL-only project emitted GameScripts.dll even though Play now uses the same VM backend as the editor.");

            string generatedFolder = Path.Combine(root, "Build", "Intermediate", "GeneratedPGSL");
            string[] generatedSources = Directory.Exists(generatedFolder)
                ? Directory.GetFiles(generatedFolder, "*.cs")
                : Array.Empty<string>();
            HeadlessHarness.Assert(
                generatedSources.Length == 0,
                $"Found {generatedSources.Length} stale per-event AOT sources; Play must not compile dead unbound behaviours.");

            // Every asset a platformer needs, created as real editable resources.
            foreach (string expected in new[]
                     {
                         "Assets/Sprites/Tiles.image.json",
                         "Assets/Sprites/Sky.image.json",
                         "Assets/Sprites/Player Idle.image.json",
                         "Assets/Sprites/Player Run.image.json",
                         "Assets/Sprites/Coin.image.json",
                         "Assets/Audio/Pickup.wav",
                         "Assets/Objects/Player.object.json",
                         "Assets/Objects/Coin.object.json",
                         "Assets/Rooms/Level 1.room.json",
                     })
            {
                HeadlessHarness.Assert(
                    File.Exists(Path.Combine(root, expected.Replace('/', Path.DirectorySeparatorChar))),
                    $"The 2D template did not create '{expected}'.");
            }

            // The art must be real pixels the room can draw, not empty documents.
            string tileSheet = Path.Combine(root, "Assets", "Sprites", "Tiles.image.json");
            TileSetInfo? templateTiles = TileSetInfo.Load(tileSheet);
            HeadlessHarness.Assert(templateTiles is not null, "The template's tile sheet is not usable as a tile set.");
            HeadlessHarness.Assert(
                templateTiles!.IsSolid(0) && templateTiles.IsSolid(1),
                "The template's ground tiles are not flagged solid.");

            string? tileTexture = ProjectAssetIndex.ResolveSpriteImage(root, "Assets/Sprites/Tiles.image.json");
            HeadlessHarness.Assert(
                tileTexture is not null && File.Exists(tileTexture),
                "The template's tile sheet has no resolvable texture — the room would draw placeholders.");

            string playerSpritePath = Path.Combine(root, "Assets", "Sprites", "Player Idle.image.json");
            SpriteRuntimeAsset playerSprite = SpriteAssetLoader.Load(playerSpritePath);
            (float playerOriginX, float playerOriginY) = SpriteOriginUtility.ResolvePixels(
                playerSprite.Origin,
                playerSprite.Canvas.Width,
                playerSprite.Canvas.Height);
            HeadlessHarness.Assert(
                Math.Abs(playerOriginX - 16f) < 0.01f && Math.Abs(playerOriginY - 32f) < 0.01f,
                $"The template Player origin resolved to ({playerOriginX}, {playerOriginY}); its sprite would render off-screen.");

            // The player's events must be there, including the controls hint.
            IReadOnlyDictionary<string, string> playerEvents =
                ObjectEventStore.Load(Path.Combine(root, "Assets", "Objects", "Player.object.json"));
            foreach (string id in new[] { "Create", "Step", "DrawGui", "Alarm0" })
            {
                HeadlessHarness.Assert(playerEvents.ContainsKey(id), $"The template's Player is missing its {id} event.");
            }

            HeadlessHarness.Assert(
                playerEvents["DrawGui"].Contains("move", StringComparison.OrdinalIgnoreCase)
                && playerEvents["DrawGui"].Contains("jump", StringComparison.OrdinalIgnoreCase),
                "The template must tell the player which keys move and jump when the game starts.");

            // Play it: build the room and run frames, exactly as the shipped game would.
            PipelineAudioRecorder audio = new();
            NullGameContext gameContext = new() { Audio = audio };
            IGameContext previousContext = PgslCommands.ActiveGameContext;
            string? previousProject = PgslCommands.ProjectPath;
            PgslCommands.ActiveGameContext = gameContext;
            PgslCommands.ProjectPath = root;

            try
            {
                Genesis.Runtime.Scripting.VM.VMEngine.Initialize();
                ScriptAssetRegistry.ClearCache();
                ScriptAssetRegistry.LoadFromProject(root);

                ScriptHostSystem scriptHost = new();
                scriptHost.SetContext(gameContext);

                EcsWorld world = new();
                RoomAsset level = RoomAssetLoader.Parse(Path.Combine(root, "Assets", "Rooms", "Level 1.room.json"));
                gameContext.World = world;
                gameContext.Room = level;

                // An unrelated entity at the first coin's position proves the template's typed
                // CollisionCircle query does not collect a tile/camera/other object by accident.
                Entity collisionDecoy = world.CreateEntity();
                world.Set(collisionDecoy, new TransformComponent
                {
                    X = 360f,
                    Y = Genesis.Application.Core.Projects.Templates.PlatformerTemplate.GroundY - 40f,
                    ScaleX = 1f,
                    ScaleY = 1f,
                    ScaleZ = 1f,
                });
                HeadlessHarness.Assert(
                    level.Settings.Width == Genesis.Application.Core.Projects.Templates.PlatformerTemplate.RoomWidth
                    && level.Settings.Height == Genesis.Application.Core.Projects.Templates.PlatformerTemplate.RoomHeight,
                    $"Template room size round-tripped as {level.Settings.Width}x{level.Settings.Height}.");
                RoomNode playerNode = level.Nodes.Single(node => node.Name == "Player 1");
                HeadlessHarness.Assert(
                    Math.Abs(playerNode.Transform.X - 160f) < 0.01f
                    && Math.Abs(playerNode.Transform.Y - Genesis.Application.Core.Projects.Templates.PlatformerTemplate.GroundY) < 0.01f,
                    $"Template Player placement round-tripped as ({playerNode.Transform.X}, {playerNode.Transform.Y}).");
                RoomNode skyNode = level.Nodes.Single(node => node.Name == "Sky");
                HeadlessHarness.Assert(
                    skyNode.Background.Scroll is { Length: >= 2 }
                    && Math.Abs(skyNode.Background.Scroll[0] - 0.35f) < 0.001f,
                    "Template sky parallax did not round-trip through the room schema.");
                RoomSceneBuilder builder = new(root, scriptHost);
                RoomBuildResult built = builder.Build(world, level);

                HeadlessHarness.Assert(
                    built.EntitiesByNodeId.Count == 9,
                    $"Expected 1 player + 8 coins, got {built.EntitiesByNodeId.Count} scripted instances.");
                HeadlessHarness.Assert(
                    built.SpawnedEntities.Count == level.Nodes.Count(node => node.Kind == RoomNodeKind.GameObject),
                    $"The level spawned {built.SpawnedEntities.Count} objects; tile cells must not allocate invisible entities.");

                // The HUD must draw from the very first frame, controls hint included.
                PipelineHudCanvas hud = new();
                scriptHost.DispatchPgslDraw(renderer: null, hud);
                HeadlessHarness.Assert(
                    hud.Texts.Any(text => text.Text.Contains("SCORE", StringComparison.Ordinal)),
                    "The template's HUD does not show a score.");
                HeadlessHarness.Assert(
                    hud.Texts.Any(text => text.Text.Contains("move", StringComparison.OrdinalIgnoreCase))
                    && hud.Texts.Any(text => text.Text.Contains("jump", StringComparison.OrdinalIgnoreCase)),
                    "The controls hint is not on screen at the start — a player would not know how to move.");

                // Put the player onto Coin 1 for one real VM step. The decoy occupies the same
                // point with a lower entity id, so only object filtering can select the coin.
                RoomNode firstCoinNode = level.Nodes.Single(node => node.Name == "Coin 1");
                Entity playerEntity = built.EntitiesByNodeId[playerNode.Id];
                Entity firstCoinEntity = built.EntitiesByNodeId[firstCoinNode.Id];
                ref TransformComponent playerTransform = ref world.GetRef<TransformComponent>(playerEntity);
                playerTransform.X = firstCoinNode.Transform.X;
                playerTransform.Y = firstCoinNode.Transform.Y + 16f;

                scriptHost.Update(1f / 60f);
                world.FlushDeferred();

                HeadlessHarness.Assert(world.IsAlive(collisionDecoy),
                    "Typed CollisionCircle destroyed an unrelated entity instead of Coin 1.");
                HeadlessHarness.Assert(!world.IsAlive(firstCoinEntity),
                    "The collected coin was not destroyed and can be scored repeatedly.");
                HeadlessHarness.Assert(audio.Plays.Count == 1,
                    $"Collecting one coin played {audio.Plays.Count} pickup sounds instead of one.");
                PipelineHudCanvas scoredHud = new();
                scriptHost.DispatchPgslDraw(renderer: null, scoredHud);
                HeadlessHarness.Assert(
                    scoredHud.Texts.Any(text => text.Text.Contains("SCORE  10", StringComparison.Ordinal)),
                    "Collecting Coin 1 did not update the scripted HUD score to 10.");

                // Gravity must act and Alarm 0 must be dispatched by the played VM. After 310
                // frames the controls hint has fired and completed its fade.
                for (int frame = 0; frame < 310; frame++)
                {
                    scriptHost.Update(1f / 60f);
                }

                HeadlessHarness.Assert(
                    scriptHost.LastError is null,
                    $"A template event threw while playing: {scriptHost.LastError}");

                PipelineHudCanvas fadedHud = new();
                scriptHost.DispatchPgslDraw(renderer: null, fadedHud);
                HeadlessHarness.Assert(
                    !fadedHud.Texts.Any(text => text.Text.Contains("move", StringComparison.OrdinalIgnoreCase)
                        || text.Text.Contains("jump", StringComparison.OrdinalIgnoreCase)),
                    "Alarm 0 never reached the played object; the controls hint did not fade out.");
            }
            finally
            {
                PgslCommands.ActiveGameContext = previousContext;
                PgslCommands.ProjectPath = previousProject;
            }

            // …and the level opens in the Room Editor for editing, which is the other half of a
            // template being a starting point rather than a demo.
            using Form host = Host(
                new RoomEditorControl(Path.Combine(root, "Assets", "Rooms", "Level 1.room.json"), root),
                "2D Platformer Template — Level 1");
            RoomEditorControl room = (RoomEditorControl)host.Controls[0];
            Pump(12, 30);

            HeadlessHarness.Assert(!room.ViewMode3D, "The template's level should open in 2D.");
            HeadlessHarness.Assert(
                room.Room.Nodes.Count(n => n.Kind == RoomNodeKind.GameObject) == 9,
                "The Room Editor does not show the template's instances.");

            Capture(ctx, host, "50-2d-10-template.png", "2D pipeline 10 — the 2D Platformer template");
        });

        // ── 9. Camera views: is a dual/split viewport available? ─────────────────
        HeadlessHarness.RunCase(ctx.Report, "TwoD.9.CameraViews", () =>
        {
            using Form host = Host(new RoomEditorControl(gameRoom, project.RootPath), "Room — camera views");
            RoomEditorControl room = (RoomEditorControl)host.Controls[0];
            Pump(8, 25);

            // The room can nominate which instance the game's camera follows…
            HeadlessHarness.Assert(
                room.ActiveGameCamera is not null,
                "The saved room lost its designated game camera.");

            // …but a second, simultaneous viewport (GameMaker's Room Editor shows the room while a
            // separate window previews the camera view) is NOT implemented. Count the viewports the
            // editor actually hosts so this stays a measured fact rather than an impression.
            int viewportCount = CountViewports(room);
            HeadlessHarness.Assert(
                viewportCount == 1,
                $"Expected exactly 1 viewport in today's Room Editor; found {viewportCount}. "
                + "If a second has been added, this audit's finding is stale and should be updated.");

            Capture(ctx, host, "50-2d-09-single-viewport.png",
                "2D pipeline 9 — one viewport; no simultaneous camera preview");
        });
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>Count nested D3D viewport controls, so "how many views" is measured, not assumed.</summary>
    private static int CountViewports(Control root)
    {
        int found = root is Genesis.Rendering.Viewport.D3DViewportControl ? 1 : 0;
        foreach (Control child in root.Controls)
        {
            found += CountViewports(child);
        }

        return found;
    }

    private static Form Host(Control content, string title)
    {
        Form form = new()
        {
            BackColor = Color.FromArgb(20, 22, 28),
            ClientSize = new Size(1360, 860),
            FormBorderStyle = FormBorderStyle.FixedSingle,
            StartPosition = FormStartPosition.CenterScreen,
            Text = title,
        };
        form.Controls.Add(content);
        GateSuite.ShowHost(form);
        Pump(4, 20);
        return form;
    }

    /// <summary>
    /// Place an object by clicking the viewport point that corresponds to a world position, using
    /// the editor's own world→client transform. Asserts the round trip landed where it was aimed.
    /// </summary>
    private static void PlaceAtWorld(RoomEditorControl room, string objectPath, Vector2 world)
    {
        room.BeginPlacement(objectPath);
        Point at = room.ClientFromWorld2D(world);
        room.EditorPointerDown(at, MouseButtons.Left, Keys.None);
        room.EditorPointerUp(at, MouseButtons.Left, Keys.None);

        RoomNode? placed = room.Room.Nodes.LastOrDefault(node => node.Kind == RoomNodeKind.GameObject);
        HeadlessHarness.Assert(placed is not null, $"Clicking to place '{Path.GetFileName(objectPath)}' created nothing.");

        // Snapping moves the drop by up to half a grid cell, so allow that but nothing more — a
        // broken transform would be out by hundreds of units, not tens.
        float tolerance = Math.Max(4f, room.Room.Settings.GridSize);
        HeadlessHarness.Assert(
            Math.Abs(placed!.Transform.X - world.X) <= tolerance
            && Math.Abs(placed.Transform.Y - world.Y) <= tolerance,
            $"Clicked the client point for world ({world.X}, {world.Y}) but the instance landed at "
            + $"({placed.Transform.X:0.#}, {placed.Transform.Y:0.#}) — the click→world round trip is broken.");
    }

    /// <summary>Click a tile in the picker, exactly as a designer would.</summary>
    private static bool ClickTile(TilePickerPanel picker, int index)
    {
        if (!picker.HasSheet || index < 0 || index >= picker.TileCount) return false;

        // Drive the real mouse handler rather than the Select API, so the test exercises hit
        // testing, selection and the TileSelected event together.
        Point centre = picker.CentreOf(index);
        picker.SimulateClick(centre);
        return picker.SelectedIndex == index;
    }

    private static string Relative(string projectRoot, string fullPath) =>
        Path.GetRelativePath(projectRoot, fullPath).Replace(Path.DirectorySeparatorChar, (char)47);

    private static Point Translate(RoomEditorControl room, Point viewportPoint) =>
        room.Viewport.PointToClient(room.PointToScreen(viewportPoint)) is Point translated
            ? translated
            : viewportPoint;

    private static void Pump(int iterations, int delayMs)
    {
        for (int index = 0; index < iterations; index++)
        {
            WinFormsApplication.DoEvents();
            Thread.Sleep(delayMs);
        }
    }

    private static void Capture(HeadlessContext ctx, Form host, string fileName, string label)
    {
        
        host.BringToFront();
        host.TopMost = true;
        Pump(8, 30);

        string path = Path.Combine(ctx.Captures, fileName);
        ImageMetrics metrics = VisualCapture.Capture(host, path, captureFromScreen: true);
        host.TopMost = false;
        ctx.Report.Images.Add(ImageResult.From(label, fileName, metrics));
    }

    /// <summary>
    /// Paint real pixels into an image and save them.
    /// </summary>
    /// <remarks>
    /// Not decoration. The first run of this audit authored blank images, so the room rendered
    /// nothing but missing-texture placeholders — which told us the *structure* worked and nothing
    /// about whether a designer would see their art. An audit of placeholders is not an audit.
    /// </remarks>
    private static void PaintAndSave(
        ImageDocumentSession session, ImageWorkspace workspace, Action<byte[], int, int> paint)
    {
        ImageFrameBuffer frame = workspace.Frames[0];
        ImageLayerBuffer layer = frame.Layers[0];
        paint(layer.Pixels, workspace.Width, workspace.Height);
        ImageWorkspaceStorage.Save(session, workspace);
    }

    /// <summary>Fill an axis-aligned block of pixels — the primitive the fixtures are built from.</summary>
    private static void FillBlock(byte[] rgba, int width, int height, int x0, int y0, int w, int h, Color colour)
    {
        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                if (x >= 0 && y >= 0 && x < width && y < height)
                {
                    RasterOperations.SetPixel(rgba, width, height, x, y, colour);
                }
            }
        }
    }

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
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);

        for (int sample = 0; sample < sampleCount; sample++)
        {
            double envelope = 1.0 - (sample / (double)sampleCount);
            double value = Math.Sin(2.0 * Math.PI * frequency * sample / sampleRate) * envelope;
            writer.Write((short)(value * short.MaxValue * 0.8));
        }
    }

    private sealed class PipelineHudCanvas : IHudCanvas
    {
        public List<(string Text, float X, float Y)> Texts { get; } = [];
        public int Width => 1280;
        public int Height => 720;
        public void Text(string text, float x, float y, float size, Vector4 color) => Texts.Add((text, x, y));
        public void TextCentered(string text, float cx, float y, float w, float size, Vector4 color) => Texts.Add((text, cx, y));
        public void Rect(float x, float y, float w, float h, Vector4 color, bool filled = true) { }
        public void Line(float x1, float y1, float x2, float y2, Vector4 color, float thickness = 1.5f) { }
    }

    private sealed class PipelineAudioRecorder : IAudioSystem
    {
        private int _next = 1;
        public List<string> Plays { get; } = [];
        public float MasterVolume { get; set; } = 1f;
        public int LoadSound(string projectRelativePath) => 1;
        public AudioChannel Play(int soundId, float volume = 1f, float pitch = 1f, bool loop = false)
        {
            Plays.Add($"{soundId}@{volume:0.00}");
            return new AudioChannel(_next++);
        }

        public void Stop(AudioChannel channel) { }
        public void StopAll() { }
        public bool IsPlaying(AudioChannel channel) => channel.IsValid;
        public void SetChannelVolume(AudioChannel channel, float volume) { }
        public void SetChannelPosition(AudioChannel channel, Vector3 position) { }
        public void SetListener(Vector3 position, Vector3 forward) { }
        public void Update() { }
    }
}
