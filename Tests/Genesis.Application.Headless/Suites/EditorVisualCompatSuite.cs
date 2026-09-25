using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Settings;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Editors.Suite.Scripts;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Runtime.Climate;
using Genesis.Runtime.Particles;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Genesis.Shared.Audio;
using Genesis.World.Terrain;
using Newtonsoft.Json.Linq;
using RuntimeImageMetrics = Genesis.Application.Runtime.ImageMetrics;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// One visual preset test per specialised editor, plus cross-editor chains that exercise
/// composition previews, object sandboxes, and room viewports together.
/// </summary>
internal static class EditorVisualCompatSuite
{
    public static void Run(HeadlessContext ctx)
    {
        SettingsService settings = new();
        bool previousStoredVSync = settings.Current.Runtime.VSyncInPreview;
        bool previousEffectiveVSync = Genesis.Rendering.Core.EditorPreviewSettings.VSync;
        settings.Current.Runtime.VSyncInPreview = false;
        Genesis.Rendering.Core.EditorPreviewSettings.Configure(vsync: false);
        try
        {
            HeadlessHarness.BeginMajor(ctx.Report, "Editor.Compat");
            GateSuite.GateFixture fixture = new(ctx, "EditorCompat");

            RunImageVisual(ctx, fixture);
            RunRoomVisual(ctx, fixture);
            RunObjectVisual(ctx, fixture);
            RunScriptVisual(ctx, fixture);
            RunTerrainVisual(ctx, fixture);
            RunModelVisual(ctx, fixture);
            RunAudioVisual(ctx, fixture);
            RunShaderVisual(ctx, fixture);
            RunParticleVisual(ctx, fixture);
            RunPhysicsVisual(ctx, fixture);
            RunNoteVisual(ctx, fixture);

            RunImageObjectRoomChain(ctx, fixture);
            RunShaderObjectCompositionChain(ctx, fixture);
            RunParticleAudioObjectChain(ctx, fixture);
            RunScriptObjectSandboxChain(ctx, fixture);
            RunModelObjectRoomChain(ctx, fixture);
            RunTerrainRoomChain(ctx, fixture);
        }
        finally
        {
            settings.Current.Runtime.VSyncInPreview = previousStoredVSync;
            Genesis.Rendering.Core.EditorPreviewSettings.Configure(previousEffectiveVSync);
        }
    }

    // ── Per-editor visual preset tests ─────────────────────────────────────────

    private static void RunImageVisual(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Compat.Visual.Image", () =>
        {
            ProjectSession project = fixture.Blank;
            string path = fixture.Resources(project).CreateResource(
                fixture.Folder(project, "Sprites"), ResourceKind.Image, "Compat Sprite");

            ImageDocumentSession session = new(
                ImageDocumentSerializer.LoadAtomic(path).Document, path, ImageDocumentAccess.Editor);
            ImageWorkspace workspace = ImageWorkspace.CreateBlank(64, 64, Color.Transparent);

            using Form host = GateSuite.NewHost(1280, 780);
            ImageEditorControl editor = new(session, workspace) { Dock = DockStyle.Fill };
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            editor.Canvas.SyncClientSize();
            GateSuite.Pump(6, 25);

            editor.SetActiveTool(ImageToolKind.Brush);
            editor.SetBrushSize(6);
            editor.SetForegroundColor(Color.FromArgb(255, 40, 180, 90));
            editor.DrawStroke(new Point(8, 32), new Point(56, 32), Color.FromArgb(255, 40, 180, 90), size: 6);
            editor.GeneratePbrMaterialSet(PbrMaterialSettings.FromPreset(PbrMaterialPreset.Rock));
            ImageWorkspaceStorage.Save(session, workspace);

            byte[] albedo = workspace.CompositeCurrentFrame();
            HeadlessHarness.Assert(
                albedo.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 0),
                "The brush stroke produced no visible pixels before capture.");
            HeadlessHarness.Assert(
                workspace.CurrentFrame!.Layers.Select(layer => layer.Channel).Distinct().Count() >= 4,
                "The Rock PBR preset did not materialise multiple channels.");

            CaptureForm(ctx, host, "compat-visual-image.png", "Image Editor — brush + Rock PBR preset");

            ImageDocument reloaded = ImageDocumentSerializer.LoadAtomic(path).Document;
            HeadlessHarness.Assert(
                reloaded.Layers.Count >= 2,
                "Saved image document did not retain PBR layers after reload.");
        });
    }

    private static void RunRoomVisual(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Compat.Visual.Room", () =>
        {
            ProjectSession project = fixture.Blank;
            ResourceService resources = fixture.Resources(project);
            string hero = resources.CreateResource(
                fixture.Folder(project, "Objects"), ResourceKind.GameObject, "Compat Hero");
            string roomPath = resources.CreateResource(
                fixture.Folder(project, "Rooms"), ResourceKind.Room, "Compat Arena");

            using Form host = GateSuite.NewHost();
            RoomEditorControl editor = new(roomPath, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(8, 25);

            editor.BeginPlacement(hero);
            Place(editor, 384f, 288f, Keys.Control);
            Place(editor, 512f, 320f, Keys.None);
            HeadlessHarness.Assert(editor.Room.Nodes.Count >= 2, "Room placement did not create instances.");

            editor.Save();
            CaptureViewport(ctx, editor.Viewport, "compat-visual-room.png", "Room Editor — placed instances", minColors: 5);

            using RoomEditorControl reopened = new(roomPath, project.RootPath);
            HeadlessHarness.Assert(
                reopened.Room.Nodes.Count >= 2,
                "Room instances did not survive save and reload.");
        });
    }

    private static void RunObjectVisual(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Compat.Visual.Object", () =>
        {
            ProjectSession project = fixture.TwoD;
            string coin = Path.Combine(project.AssetsPath, "Objects", "Coin.object.json");
            ResourceService resources = fixture.Resources(project);
            string particle = resources.CreateResource(
                fixture.Folder(project, "Particles"), ResourceKind.Particle, "Compat Sparkle");

            using Form host = GateSuite.NewHost(1200, 820);
            using ParticleEditorControl particleEditor = new(particle, project.RootPath);
            particleEditor.ApplyPreset("Portal");
            particleEditor.Save();
            string relativeParticle = Path.GetRelativePath(project.RootPath, particle).Replace('\\', '/');

            ObjectEditorControl editor = new(coin, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(8, 25);

            HeadlessHarness.Assert(editor.SpritePreview.HasImage, "The Object Editor showed no sprite preview.");
            editor.Composition.SetAsset("ParticleComponent", relativeParticle);
            editor.Composition.SetProperty("ParticleComponent", "Emitting", true);
            editor.Save();

            ObjectSandboxResult? sandbox = editor.RunSandbox();
            HeadlessHarness.Assert(sandbox is { Ok: true }, "The Object sandbox failed on the Coin prefab.");

            using ObjectCompositionPreviewControl preview = editor.CreateCompositionPreview();
            host.Controls.Add(preview);
            preview.Size = new Size(420, 320);
            preview.Location = new Point(host.ClientSize.Width - preview.Width - 12, 48);
            GateSuite.Pump(12, 30);
            CaptureViewport(ctx, preview, "compat-visual-object-composition.png",
                "Object Editor — composition preview with Portal particles", minColors: 2);
            HeadlessHarness.Assert(
                preview.ParticleEmitterCount >= 1,
                "The Object composition preview did not attach the particle preset.");
        });
    }

    private static void RunScriptVisual(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Compat.Visual.Script", () =>
        {
            ProjectSession project = fixture.Blank;
            string path = fixture.Resources(project).CreateResource(
                fixture.Folder(project, "Scripts"), ResourceKind.PgslScript, "Compat Script");

            using Form host = GateSuite.NewHost(1100, 720);
            PgslScriptEditorControl editor = new(path, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(6, 25);

            editor.ScriptText =
                """
                var moveSpeed = 4;
                function MovePlayer(step) {
                    x += step * moveSpeed;
                }
                DrawSprite("hero", 0, x, y);
                """;
            editor.Save();

            ImageMetrics metrics = VisualCapture.CaptureOpenForm(
                host, Path.Combine(ctx.Captures, "compat-visual-script.png"));
            ctx.Report.Images.Add(ImageResult.From("PGSL Editor", "compat-visual-script.png", metrics));
            HeadlessHarness.Assert(metrics.UniqueSampledColors >= 8, "The PGSL editor capture appears blank.");

            using PgslScriptEditorControl reopened = new(path, project.RootPath);
            HeadlessHarness.Assert(
                reopened.ScriptText.Contains("MovePlayer", StringComparison.Ordinal),
                "The PGSL script did not round-trip through save.");
        });
    }

    private static void RunTerrainVisual(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Compat.Visual.Terrain", () =>
        {
            ProjectSession project = fixture.Blank;
            string path = fixture.Resources(project).CreateResource(
                fixture.Folder(project, "Terrain"), ResourceKind.Terrain, "Compat Terrain");

            using Form host = GateSuite.NewHost(1120, 800);
            TerrainEditorControl editor = new(path, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(8, 25);

            editor.ApplyGeneration(new TerrainGenParams
            {
                Preset = TerrainPreset.Mountains,
                Seed = 4242,
                ResolutionX = 129,
                ResolutionZ = 129,
                CellSize = 1f,
                MinHeight = -24f,
                MaxHeight = 72f,
            });
            editor.Save();
            CaptureViewport(ctx, editor.Viewport, "compat-visual-terrain.png",
                "Terrain Editor — Mountains preset", minColors: 24);

            using TerrainEditorControl reopened = new(path, project.RootPath);
            HeadlessHarness.Assert(
                reopened.Terrain.ResolutionX == 129,
                "Generated terrain did not survive save and reload.");
        });
    }

    private static void RunModelVisual(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Compat.Visual.Model", () =>
        {
            ProjectSession project = fixture.Blank;
            string path = fixture.Resources(project).CreateResource(
                fixture.Folder(project, "Models"), ResourceKind.Model, "Compat Model");

            using Form host = GateSuite.NewHost(1120, 800);
            ModelEditorControl editor = new(path, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(8, 25);

            editor.AddPart(ModelPrimitiveKind.Cube);
            editor.AddPart(ModelPrimitiveKind.Sphere);
            editor.ApplyFixtureParts();
            editor.FrameModelForTest();
            GateSuite.Pump(6, 25);
            editor.Save();

            HeadlessHarness.Assert(editor.BakedTriangleCount > 0, "The kitbashed model produced no triangles.");
            CaptureViewport(ctx, editor.Viewport, "compat-visual-model.png",
                "Model Editor — cube + sphere kitbash", minColors: 8);

            using ModelEditorControl reopened = new(path, project.RootPath);
            HeadlessHarness.Assert(reopened.BakedVertexCount == editor.BakedVertexCount && reopened.BakedTriangleCount == editor.BakedTriangleCount
                && reopened.BakedVerticesForTest.SequenceEqual(editor.BakedVerticesForTest), "Authored canonical geometry did not round-trip.");
        });
    }

    private static void RunAudioVisual(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Compat.Visual.Audio", () =>
        {
            ProjectSession project = fixture.Blank;
            ResourceService resources = fixture.Resources(project);
            string path = resources.CreateResource(
                fixture.Folder(project, "Audio"), ResourceKind.Audio, "Compat Sound");
            string wav = Path.Combine(Path.GetDirectoryName(path)!, "Compat Tone.wav");
            WriteToneWave(wav, frequency: 440f, seconds: 0.4f);
            JObject document = JObject.Parse(File.ReadAllText(path));
            document["source"] = Path.GetRelativePath(project.RootPath, wav).Replace('\\', '/');
            File.WriteAllText(path, document.ToString(Newtonsoft.Json.Formatting.Indented));

            using Form host = GateSuite.NewHost(1000, 700);
            AudioEditorControl editor = new(path, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(6, 25);

            HeadlessHarness.Assert(
                editor.SelectSource((string)document["source"]!),
                "The Audio Editor did not list the generated WAV.");
            HeadlessHarness.Assert(editor.HasWaveform, "The Audio Editor decoded no waveform.");
            editor.ApplyEnvironmentPreset(EnvironmentAudioRole.Water);
            editor.SetSpatial(true);
            editor.Save();

            CaptureForm(ctx, host, "compat-visual-audio.png", "Audio Editor — Water ambience preset");

            AudioAssetSettings? settings = AudioAssetSettings.Load(path);
            HeadlessHarness.Assert(
                settings is { EnvironmentRole: "Water", Spatial: true },
                "The Water ambience preset did not persist for the runtime parser.");
        });
    }

    private static void RunShaderVisual(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Compat.Visual.Shader", () =>
        {
            ProjectSession project = fixture.Blank;
            string path = fixture.Resources(project).CreateResource(
                fixture.Folder(project, "Shaders"), ResourceKind.Shader, "Compat Shader");

            using Form host = GateSuite.NewHost(1000, 720);
            ShaderEditorControl editor = new(path, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(6, 25);

            HeadlessHarness.Assert(editor.SelectPreset("Image Rainbow"), "The Image Rainbow preset is missing.");
            editor.CompileNow();
            GateSuite.Pump(4, 25);
            HeadlessHarness.Assert(editor.LastCompileSucceeded, "The Image Rainbow preset did not compile.");
            editor.Save();

            HeadlessHarness.Assert(editor.IsNarrowLayout,
                "The 1000 px Shader Editor did not activate its stacked responsive workspace.");
            CaptureForm(ctx, host, "compat-visual-shader-editor-narrow.png",
                "Shader Editor — narrow authoring workspace", minColors: 8);
            editor.SetPreviewVisible(true);
            GateSuite.Pump(3, 20);
            HeadlessHarness.Assert(editor.IsNarrowPreviewActive && editor.PreviewVisible,
                "The narrow Shader Preview command did not switch to the full viewport.");
            CaptureForm(ctx, host, "compat-visual-shader-editor-narrow-preview.png",
                "Shader Editor — narrow preview workspace", minColors: 8);
            host.ClientSize = new Size(1280, 760);
            GateSuite.Pump(4, 20);
            HeadlessHarness.Assert(!editor.IsNarrowLayout,
                "The Shader Editor did not restore its side-by-side desktop workspace.");
            CaptureForm(ctx, host, "compat-visual-shader-editor-desktop.png",
                "Shader Editor — desktop workspace", minColors: 8);
            CaptureViewport(ctx, editor.Viewport, "compat-visual-shader.png",
                "Shader Editor — Image Rainbow preset", minColors: 8);

            ShaderAssetDocument authored = ShaderAssetDocument.Load(path);
            HeadlessHarness.Assert(
                authored.AuthoringMode == ShaderAuthoringMode.Preset,
                "The shader preset did not round-trip through save.");
        });
    }

    private static void RunParticleVisual(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Compat.Visual.Particle", () =>
        {
            ProjectSession project = fixture.Blank;
            string path = fixture.Resources(project).CreateResource(
                fixture.Folder(project, "Particles"), ResourceKind.Particle, "Compat Particle");

            using Form host = GateSuite.NewHost(1000, 700);
            ParticleEditorControl editor = new(path, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(6, 25);

            editor.ApplyPreset("Portal");
            editor.SetPreview2D(true);
            editor.StepForTest(0.35f);
            HeadlessHarness.Assert(editor.LiveParticleCount > 0, "The Portal preset emitted no particles.");
            editor.Save();

            CaptureForm(ctx, host, "compat-visual-particle.png", "Particle Editor — Portal preset");

            using ParticleEditorControl reopened = new(path, project.RootPath);
            HeadlessHarness.Assert(
                reopened.Config.Shape == ParticleEmitShape.Ring,
                "The Portal preset did not round-trip through save.");
        });
    }

    private static void RunPhysicsVisual(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Compat.Visual.Physics", () =>
        {
            ProjectSession project = fixture.Blank;
            string path = fixture.Resources(project).CreateResource(
                fixture.Folder(project, "Physics"), ResourceKind.Physics, "Compat Physics");

            JObject document = JObject.Parse(File.ReadAllText(path));
            document["restitution"] = 0.85;
            document["friction"] = 0.15;
            File.WriteAllText(path, document.ToString(Newtonsoft.Json.Formatting.Indented));

            using Form host = GateSuite.NewHost(1000, 700);
            PhysicsEditorControl editor = new(path, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(30, 20);

            editor.Save();
            CaptureForm(ctx, host, "compat-visual-physics.png", "Physics Editor — bouncing sandbox");

            using PhysicsEditorControl reopened = new(path, project.RootPath);
            JObject reloaded = JObject.Parse(File.ReadAllText(path));
            HeadlessHarness.Assert(
                (double?)reloaded.GetValue("restitution", StringComparison.OrdinalIgnoreCase) == 0.85,
                "Physics restitution did not round-trip.");
        });
    }

    private static void RunNoteVisual(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Compat.Visual.Note", () =>
        {
            ProjectSession project = fixture.Blank;
            string path = fixture.Resources(project).CreateResource(
                fixture.Folder(project, "Notes"), ResourceKind.Note, "Compat Note");

            using Form host = GateSuite.NewHost(900, 640);
            NoteEditorControl editor = new(path, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(4, 25);

            editor.NoteText =
                """
                # Compat Note

                This note documents the **visual compat** pass for editors.
                """;
            editor.SetViewMode(NoteEditorViewMode.Preview);
            editor.Save();

            CaptureForm(ctx, host, "compat-visual-note.png", "Note Editor — rendered preview", minColors: 4);

            using NoteEditorControl reopened = new(path, project.RootPath);
            HeadlessHarness.Assert(
                reopened.NoteText.Contains("visual compat", StringComparison.OrdinalIgnoreCase),
                "Note text did not round-trip.");
        });
    }

    // ── Cross-editor compatibility chains ───────────────────────────────────────

    private static void RunImageObjectRoomChain(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Compat.Chain.ImageObjectRoom", () =>
        {
            ProjectSession project = fixture.Blank;
            ResourceService resources = fixture.Resources(project);
            string imagePath = resources.CreateResource(
                fixture.Folder(project, "Sprites"), ResourceKind.Image, "Compat Chain Sprite");
            string objectPath = resources.CreateResource(
                fixture.Folder(project, "Objects"), ResourceKind.GameObject, "Compat Chain Hero");
            string roomPath = resources.CreateResource(
                fixture.Folder(project, "Rooms"), ResourceKind.Room, "Compat Chain Room");

            ImageDocumentSession session = new(
                ImageDocumentSerializer.LoadAtomic(imagePath).Document, imagePath, ImageDocumentAccess.Editor);
            ImageWorkspace workspace = ImageWorkspace.CreateBlank(32, 32, Color.Transparent);
            using (Form imageHost = GateSuite.NewHost(900, 640))
            {
                ImageEditorControl imageEditor = new(session, workspace) { Dock = DockStyle.Fill };
                imageHost.Controls.Add(imageEditor);
                GateSuite.ShowHost(imageHost);
                imageEditor.SetActiveTool(ImageToolKind.Brush);
                imageEditor.SetForegroundColor(Color.FromArgb(255, 255, 90, 30));
                imageEditor.DrawStroke(new Point(4, 16), new Point(28, 16), Color.FromArgb(255, 255, 90, 30), size: 4);
                ImageWorkspaceStorage.Save(session, workspace);
            }

            string relativeImage = Path.GetRelativePath(project.RootPath, imagePath).Replace('\\', '/');
            JObject objectDocument = JObject.Parse(File.ReadAllText(objectPath));
            objectDocument["sprite"] = relativeImage;
            File.WriteAllText(objectPath, objectDocument.ToString(Newtonsoft.Json.Formatting.Indented));

            using (Form objectHost = GateSuite.NewHost(1100, 760))
            {
                ObjectEditorControl objectEditor = new(objectPath, project.RootPath);
                objectHost.Controls.Add(objectEditor);
                GateSuite.ShowHost(objectHost);
                GateSuite.Pump(6, 25);
                HeadlessHarness.Assert(objectEditor.SpritePreview.HasImage, "The object did not bind the authored image.");
                objectEditor.SetEventBody("Create", "chainReady = 1;\n");
                ObjectSandboxResult? sandbox = objectEditor.RunSandbox();
                HeadlessHarness.Assert(
                    sandbox is { Ok: true },
                    sandbox?.Errors.Count > 0
                        ? "The object sandbox failed after image binding: " + sandbox.Errors[0].Message
                        : "The object sandbox failed after image binding.");
                objectEditor.Save();
            }

            using Form roomHost = GateSuite.NewHost();
            RoomEditorControl room = new(roomPath, project.RootPath);
            roomHost.Controls.Add(room);
            GateSuite.ShowHost(roomHost);
            GateSuite.Pump(8, 25);
            room.BeginPlacement(objectPath);
            Place(room, 448f, 320f, Keys.None);
            HeadlessHarness.Assert(room.Room.Nodes.Count == 1, "The room did not accept the image-bound object.");
            room.Save();
            CaptureViewport(ctx, room.Viewport, "compat-chain-image-object-room.png",
                "Chain — Image → Object → Room", minColors: 5);
        });
    }

    private static void RunShaderObjectCompositionChain(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Compat.Chain.ShaderObjectComposition", () =>
        {
            ProjectSession project = fixture.TwoD;
            ResourceService resources = fixture.Resources(project);
            string shaderPath = resources.CreateResource(
                fixture.Folder(project, "Shaders"), ResourceKind.Shader, "Compat Chain Rainbow");
            string objectPath = Path.Combine(project.AssetsPath, "Objects", "Coin.object.json");
            string relativeShader = Path.GetRelativePath(project.RootPath, shaderPath).Replace('\\', '/');

            using (Form shaderHost = GateSuite.NewHost(1000, 720))
            {
                ShaderEditorControl shader = new(shaderPath, project.RootPath);
                shaderHost.Controls.Add(shader);
                GateSuite.ShowHost(shaderHost);
                GateSuite.Pump(6, 25);
                HeadlessHarness.Assert(shader.SelectPreset("Image Rainbow"), "Shader preset selection failed.");
                HeadlessHarness.Assert(shader.SetParameterValue("Speed", 1.4f)
                    && shader.SetParameterValue("Saturation", 0.72f),
                    "Shader parameters could not be authored before cross-editor binding.");
                shader.CompileNow();
                GateSuite.Pump(4, 25);
                shader.Save();
            }

            using Form host = GateSuite.NewHost(1200, 820);
            ObjectEditorControl editor = new(objectPath, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(6, 25);
            HeadlessHarness.Assert(editor.SetShaderBinding(relativeShader), "The object rejected the shader binding.");
            editor.Document["shaderParameters"] = new JObject
            {
                ["Speed"] = new JArray(1.4f),
                ["Saturation"] = new JArray(0.72f),
            };
            editor.Save();
            JObject savedObject = JObject.Parse(File.ReadAllText(objectPath));
            HeadlessHarness.Assert(
                Math.Abs(((float?)savedObject["shaderParameters"]?["Speed"]?[0] ?? 0f) - 1.4f) < 0.001f
                && Math.Abs(((float?)savedObject["shaderParameters"]?["Saturation"]?[0] ?? 0f) - 0.72f) < 0.001f,
                "Shader parameter overrides did not survive the Shader → Object handoff.");

            using ObjectCompositionPreviewControl preview = editor.CreateCompositionPreview();
            host.Controls.Add(preview);
            preview.Size = new Size(480, 360);
            preview.Location = new Point(12, 48);
            GateSuite.Pump(12, 30);
            CaptureViewport(ctx, preview, "compat-chain-shader-object.png",
                "Chain — Shader preset → Object composition preview", minColors: 2);

            ObjectSandboxResult? sandbox = editor.RunSandbox();
            HeadlessHarness.Assert(sandbox is { Ok: true }, "The object sandbox failed with a shader binding.");
        });
    }

    private static void RunParticleAudioObjectChain(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Compat.Chain.ParticleAudioObject", () =>
        {
            ProjectSession project = fixture.TwoD;
            ResourceService resources = fixture.Resources(project);
            string particlePath = resources.CreateResource(
                fixture.Folder(project, "Particles"), ResourceKind.Particle, "Compat Chain Burst");
            string audioPath = resources.CreateResource(
                fixture.Folder(project, "Audio"), ResourceKind.Audio, "Compat Chain Hum");
            string wave = Path.Combine(Path.GetDirectoryName(audioPath)!, "Compat Chain Hum.wav");
            WriteToneWave(wave, frequency: 220f, seconds: 0.25f);
            JObject audioDocument = JObject.Parse(File.ReadAllText(audioPath));
            audioDocument["source"] = Path.GetRelativePath(project.RootPath, wave).Replace('\\', '/');
            File.WriteAllText(audioPath, audioDocument.ToString(Newtonsoft.Json.Formatting.Indented));

            using (Form particleHost = GateSuite.NewHost(900, 640))
            {
                ParticleEditorControl particle = new(particlePath, project.RootPath);
                particleHost.Controls.Add(particle);
                GateSuite.ShowHost(particleHost);
                particle.ApplyPreset("Portal");
                particle.Save();
            }

            using (Form audioHost = GateSuite.NewHost(900, 640))
            {
                AudioEditorControl audio = new(audioPath, project.RootPath);
                audioHost.Controls.Add(audio);
                GateSuite.ShowHost(audioHost);
                GateSuite.Pump(4, 20);
                audio.SelectSource((string)audioDocument["source"]!);
                audio.SetSpatial(true);
                audio.SetLoop(true);
                audio.Save();
            }

            string objectPath = Path.Combine(project.AssetsPath, "Objects", "Player.object.json");
            string relativeParticle = Path.GetRelativePath(project.RootPath, particlePath).Replace('\\', '/');
            string relativeAudio = Path.GetRelativePath(project.RootPath, audioPath).Replace('\\', '/');

            using Form host = GateSuite.NewHost(1200, 820);
            ObjectEditorControl editor = new(objectPath, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(6, 25);

            ObjectCompositionModel composition = editor.Composition;
            composition.SetAsset("ParticleComponent", relativeParticle);
            composition.SetProperty("ParticleComponent", "Emitting", true);
            composition.SetAsset("AudioComponent", relativeAudio);
            composition.SetProperty("AudioComponent", "AutoPlay", true);
            composition.SetProperty("AudioComponent", "Spatial", true);
            editor.Save();

            ObjectSandboxResult? sandbox = editor.RunSandbox();
            HeadlessHarness.Assert(sandbox is { Ok: true }, "The Player sandbox failed with composed audio/particles.");

            using ObjectCompositionPreviewControl preview = editor.CreateCompositionPreview();
            host.Controls.Add(preview);
            preview.Size = new Size(480, 360);
            preview.Location = new Point(host.ClientSize.Width - preview.Width - 12, 48);
            GateSuite.Pump(14, 30);
            HeadlessHarness.Assert(
                preview.ParticleEmitterCount >= 1 && preview.ActiveAudioCount >= 1,
                "The composition preview did not simulate particle and audio components together.");
            CaptureViewport(ctx, preview, "compat-chain-particle-audio-object.png",
                "Chain — Particle + Audio → Object preview", minColors: 2);
        });
    }

    private static void RunScriptObjectSandboxChain(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Compat.Chain.ScriptObjectSandbox", () =>
        {
            ProjectSession project = fixture.TwoD;
            ResourceService resources = fixture.Resources(project);
            string scriptPath = resources.CreateResource(
                fixture.Folder(project, "Scripts"), ResourceKind.PgslScript, "Compat Chain Logic");
            string objectPath = Path.Combine(project.AssetsPath, "Objects", "Coin.object.json");

            using (Form scriptHost = GateSuite.NewHost(1000, 680))
            {
                PgslScriptEditorControl script = new(scriptPath, project.RootPath);
                scriptHost.Controls.Add(script);
                GateSuite.ShowHost(scriptHost);
                script.ScriptText =
                    """
                    function PulseAlarm() {
                        gateFlag = 1;
                        SetAlarm(1, 30);
                    }
                    """;
                script.Save();
            }

            using Form host = GateSuite.NewHost(1100, 760);
            ObjectEditorControl editor = new(objectPath, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(6, 25);
            HeadlessHarness.Assert(editor.AddEventViaWizard("Alarm1"), "Alarm1 could not be added to the object.");
            editor.SetEventBody("Alarm1", "gateFlag = 1;\nSetAlarm(1, 30);\n");
            editor.Save();

            ObjectSandboxResult? sandbox = editor.RunSandbox();
            HeadlessHarness.Assert(sandbox is { Ok: true }, "The object sandbox failed after PGSL event authoring.");
            HeadlessHarness.Assert(
                editor.PgslEvents.ContainsKey("Alarm1"),
                "The object's event tree did not retain Alarm1 authored alongside the script resource.");
        });
    }

    private static void RunModelObjectRoomChain(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Compat.Chain.ModelObjectRoom", () =>
        {
            ProjectSession project = fixture.Blank;
            ResourceService resources = fixture.Resources(project);
            string modelPath = resources.CreateResource(
                fixture.Folder(project, "Models"), ResourceKind.Model, "Compat Chain Prop");
            string objectPath = resources.CreateResource(
                fixture.Folder(project, "Objects"), ResourceKind.GameObject, "Compat Chain Prop Object");
            string roomPath = resources.CreateResource(
                fixture.Folder(project, "Rooms"), ResourceKind.Room, "Compat Chain 3D Room");

            using (Form modelHost = GateSuite.NewHost(1120, 800))
            {
                ModelEditorControl model = new(modelPath, project.RootPath);
                modelHost.Controls.Add(model);
                GateSuite.ShowHost(modelHost);
                model.AddPart(ModelPrimitiveKind.Cylinder);
                model.AddPart(ModelPrimitiveKind.Sphere);
                model.ApplyFixtureParts();
                model.Save();
            }

            JObject objectDocument = JObject.Parse(File.ReadAllText(objectPath));
            objectDocument["dimension"] = "ThreeD";
            objectDocument["sprite"] = string.Empty;
            File.WriteAllText(objectPath, objectDocument.ToString(Newtonsoft.Json.Formatting.Indented));

            string relativeModel = Path.GetRelativePath(project.RootPath, modelPath).Replace('\\', '/');
            using (Form objectHost = GateSuite.NewHost(1100, 760))
            {
                ObjectEditorControl objectEditor = new(objectPath, project.RootPath);
                objectHost.Controls.Add(objectEditor);
                GateSuite.ShowHost(objectHost);
                GateSuite.Pump(6, 25);
                objectEditor.Composition.SetAsset("ModelRendererComponent", relativeModel);
                objectEditor.SetPhysicsPreset("Dynamic");
                objectEditor.Save();

                using ObjectCompositionPreviewControl preview = objectEditor.CreateCompositionPreview();
                objectHost.Controls.Add(preview);
                preview.Size = new Size(480, 360);
                preview.Location = new Point(12, 48);
                GateSuite.Pump(12, 30);
                CaptureViewport(ctx, preview, "compat-chain-model-object.png",
                    "Chain — Model kitbash → Object 3D preview", minColors: 2);
            }

            using Form roomHost = GateSuite.NewHost();
            RoomEditorControl room = new(roomPath, project.RootPath);
            roomHost.Controls.Add(room);
            GateSuite.ShowHost(roomHost);
            GateSuite.Pump(8, 25);
            room.ViewMode3D = true;
            GateSuite.Pump(4, 25);
            room.BeginPlacement(objectPath);
            Point centre = new(room.Viewport.Width / 2, (int)(room.Viewport.Height * 0.62f));
            room.EditorPointerDown(centre, MouseButtons.Left, Keys.None);
            room.EditorPointerUp(centre, MouseButtons.Left, Keys.None);
            HeadlessHarness.Assert(room.Room.Nodes.Count == 1, "The 3D room did not accept the model-bound object.");
            room.Save();
            CaptureViewport(ctx, room.Viewport, "compat-chain-model-object-room.png",
                "Chain — Model → Object → Room 3D", minColors: 8);
        });
    }

    private static void RunTerrainRoomChain(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.Compat.Chain.TerrainRoom", () =>
        {
            ProjectSession project = fixture.Blank;
            ResourceService resources = fixture.Resources(project);
            string terrainPath = resources.CreateResource(
                fixture.Folder(project, "Terrain"), ResourceKind.Terrain, "Compat Chain Ground");
            string roomPath = resources.CreateResource(
                fixture.Folder(project, "Rooms"), ResourceKind.Room, "Compat Chain Terrain Room");

            using (Form terrainHost = GateSuite.NewHost(1120, 800))
            {
                TerrainEditorControl terrain = new(terrainPath, project.RootPath);
                terrainHost.Controls.Add(terrain);
                GateSuite.ShowHost(terrainHost);
                GateSuite.Pump(6, 25);
                terrain.ApplyGeneration(new TerrainGenParams
                {
                    Preset = TerrainPreset.Hills,
                    Seed = 808,
                    ResolutionX = 129,
                    ResolutionZ = 129,
                    CellSize = 1f,
                    MinHeight = -16f,
                    MaxHeight = 48f,
                });
                terrain.Save();
            }

            using Form roomHost = GateSuite.NewHost();
            RoomEditorControl room = new(roomPath, project.RootPath);
            roomHost.Controls.Add(room);
            GateSuite.ShowHost(roomHost);
            GateSuite.Pump(8, 25);
            room.ViewMode3D = true;
            GateSuite.Pump(4, 25);
            room.BeginPlacementTerrain(terrainPath);
            Point centre = new(room.Viewport.Width / 2, (int)(room.Viewport.Height * 0.62f));
            room.EditorPointerDown(centre, MouseButtons.Left, Keys.None);
            room.EditorPointerUp(centre, MouseButtons.Left, Keys.None);
            HeadlessHarness.Assert(
                room.Room.Nodes.Any(node => node.Kind == RoomNodeKind.Terrain),
                "The room did not accept terrain placement.");
            room.Save();
            CaptureViewport(ctx, room.Viewport, "compat-chain-terrain-room.png",
                "Chain — Terrain preset → Room 3D", minColors: 12);
        });
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static void Place(RoomEditorControl editor, float x, float y, Keys modifiers)
    {
        Point client = editor.ClientFromWorld2D(new Vector2(x, y));
        editor.EditorPointerDown(client, MouseButtons.Left, modifiers);
        editor.EditorPointerUp(client, MouseButtons.Left, modifiers);
    }

    private static void CaptureViewport(
        HeadlessContext ctx,
        EditorViewport3D viewport,
        string fileName,
        string label,
        int minColors)
    {
        using Bitmap? frame = viewport.CaptureFrame(settleFrames: 4);
        HeadlessHarness.Assert(frame is not null, $"Viewport capture returned no frame for '{fileName}'.");
        SaveCapture(ctx, frame!, fileName, label, minColors);
    }

    private static void CaptureViewport(
        HeadlessContext ctx,
        ObjectCompositionPreviewControl preview,
        string fileName,
        string label,
        int minColors)
    {
        using Bitmap? frame = preview.CaptureFrame(settleFrames: 4);
        HeadlessHarness.Assert(frame is not null, $"Composition preview returned no frame for '{fileName}'.");
        SaveCapture(ctx, frame!, fileName, label, minColors);
    }

    private static void CaptureForm(HeadlessContext ctx, Form host, string fileName, string label, int minColors = 8)
    {
        
        host.BringToFront();
        host.TopMost = true;
        GateSuite.Pump(8, 25);
        using Bitmap bitmap = VisualCapture.CaptureWindowPixels(host);
        host.TopMost = false;
        SaveCapture(ctx, bitmap, fileName, label, minColors: minColors);
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
        ctx.Report.Images.Add(new ImageResult(
            label, fileName, metrics.Width, metrics.Height, metrics.UniqueSampledColors, metrics.AverageLuminance));
        HeadlessHarness.Assert(
            metrics.UniqueSampledColors >= minColors,
            $"'{fileName}' looks blank/flat ({metrics.UniqueSampledColors} colours < {minColors}).");
        return metrics;
    }

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
}
