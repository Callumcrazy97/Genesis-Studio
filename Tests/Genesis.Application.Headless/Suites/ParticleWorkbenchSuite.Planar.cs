using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Runtime;
using Genesis.Runtime.Particles;
using Genesis.Shared.Interfaces;
using Genesis.Rendering.Core;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless.Suites;

internal static partial class ParticleWorkbenchSuite
{
    private static void RunPlanarCases(HeadlessContext context, Action<string, Action> check)
    {
        check("TwoD.LinkedEmittersSpawnAtSourcePositionOnAllBackends", () => WithEditor(original =>
        {
            ParticleConfig linked = new()
            {
                Preview2D = true, MaxParticles = 1, Loop = false, BurstCount = 1, Shape = ParticleEmitShape.Cone,
                SpreadDegrees = 0, Speed = 0, Gravity = 0, Drag = 0, Lifetime = 4, LifetimeVariance = 0,
                StartSize = 1, EndSize = 1, StartColor = new ParticleColor(.1f, .9f, .2f, 1),
                EndColor = new ParticleColor(.1f, .9f, .2f, 1),
            };
            linked.Emitters.Add(new ParticleEmitterLayer
            {
                Id = "child", Name = "Red sparks", Config = new ParticleConfig
                {
                    MaxParticles = 8, Loop = false, BurstCount = 0, Shape = ParticleEmitShape.Cone,
                    SpreadDegrees = 0, Speed = 0, Gravity = 0, Drag = 0, Lifetime = 4, LifetimeVariance = 0,
                    StartSize = 2, EndSize = 2, StartColor = new ParticleColor(1, .1f, .1f, 1),
                    EndColor = new ParticleColor(1, .1f, .1f, 1),
                },
            });
            linked.EventLinks.Add(new ParticleEventLink
            {
                SourceEmitterId = "primary", TargetEmitterId = "child", Trigger = ParticleEventTrigger.Birth,
                Count = 3, Probability = 1,
            });
            File.WriteAllText(original.ResourcePath, Text(linked));
            using ParticleEditorControl authoring = new(original.ResourcePath, original.ProjectRoot);
            JObject document = JObject.Parse(File.ReadAllText(authoring.CreateEffectObject("Linked effect")));
            JObject transform = document["components"]!.OfType<JObject>().Single(component => (string?)component["type"] == "TransformComponent");
            transform["props"]!["Position"] = new JArray(100, 150, 0);
            RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
            try
            {
                foreach (RenderBackendOption backend in new[] { RenderBackendOption.SilkNetDx11, RenderBackendOption.Direct3D12,
                    RenderBackendOption.Vulkan, RenderBackendOption.OpenGL, RenderBackendOption.Software })
                {
                    RenderBackendSelection.Configure(backend);
                    using Form host = GateSuite.NewHost(800, 600);
                    using ObjectCompositionPreviewControl preview = new(original.ProjectRoot, compact: true) { Playing = false };
                    host.Controls.Add(preview); GateSuite.ShowHost(host);
                    foreach (ParticleSimulationSpace space in Enum.GetValues<ParticleSimulationSpace>())
                    {
                        linked.Emitters[0].Config.SimulationSpace = space;
                        File.WriteAllText(original.ResourcePath, Text(linked));
                        ParticleAssetLoader.ClearCache();
                        preview.Reload(document);
                        preview.Viewport.Camera2DX = 0; preview.Viewport.Camera2DY = 0; preview.Viewport.Zoom2D = 1;
                        using Bitmap frame = preview.CaptureFrame(4)!;
                        Color child = frame.GetPixel(frame.Width / 2 + 100, frame.Height / 2 + 150);
                        Assert(child.R >= 170 && child.G <= 100 && preview.Viewport.Host.RenderFaultCount == 0,
                            $"{backend} {space} did not spawn the linked red emitter at the source's world position: {child}.");
                        string image = $"particle-linked-{backend}-{space}.png";
                        frame.Save(Path.Combine(context.Captures, image));
                        context.Report.Images.Add(ImageResult.From(backend + " linked " + space, image, VisualCapture.Measure(frame)));
                    }
                    host.Controls.Remove(preview);
                }
            }
            finally { RenderBackendSelection.Configure(previous); }
        }));
        check("TwoD.SoftwareEditorPreviewFollowsSavedEmitterLinks", () => WithEditor(original =>
        {
            ParticleConfig linked = new()
            {
                Preview2D = true, MaxParticles = 1, Loop = false, BurstCount = 1,
                Shape = ParticleEmitShape.Cone, SpreadDegrees = 0, Speed = 0,
                Gravity = 0, Lifetime = 4, LifetimeVariance = 0,
                StartSize = 1, EndSize = 1, StartColor = new ParticleColor(.1f, .9f, .2f, 1),
            };
            linked.Emitters.Add(new ParticleEmitterLayer
            {
                Id = "child", Name = "Red sparks", Config = new ParticleConfig
                {
                    MaxParticles = 8, Loop = false, BurstCount = 0,
                    Shape = ParticleEmitShape.Cone, SpreadDegrees = 0, Speed = 0,
                    Gravity = 0, Lifetime = 4, LifetimeVariance = 0,
                    StartSize = 2, EndSize = 2, StartColor = new ParticleColor(1, .1f, .1f, 1),
                },
            });
            linked.EventLinks.Add(new ParticleEventLink
            {
                SourceEmitterId = "primary", TargetEmitterId = "child",
                Trigger = ParticleEventTrigger.Birth, Count = 3,
            });
            File.WriteAllText(original.ResourcePath, Text(linked));
            RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
            try
            {
                RenderBackendSelection.Configure(RenderBackendOption.Software);
                using ParticleEditorControl editor = new(original.ResourcePath, original.ProjectRoot);
                using Form host = GateSuite.NewHost(1280, 800);
                host.Controls.Add(editor); GateSuite.ShowHost(host);
                using (editor.Viewport.CaptureFrame(2)) { }
                editor.StopPreview();
                editor.StepForTest(1f / 60f);
                List<ParticleSimulation> simulations = Field<List<ParticleSimulation>>(editor, "_previewSimulations");
                Assert(simulations.Count == 2 && simulations[0].ActiveCount == 1
                    && simulations[1].ActiveCount == 3 && editor.LiveParticleCount == 4,
                    $"Saved software preview ignored the birth link: {string.Join(",", simulations.Select(sim => sim.ActiveCount))}.");
                editor.Burst(); editor.StepForTest(1f / 60f);
                Assert(simulations[1].ActiveCount == 3,
                    "Preview burst seeded a linked child independently of its source event.");
                using Bitmap frame = editor.Viewport.CaptureFrame(2)
                    ?? throw new InvalidOperationException("Linked editor preview produced no frame.");
                Assert(editor.Viewport.Host.RenderFaultCount == 0, "Linked preview caused a render fault.");
                string image = "particle-editor-linked-Software.png";
                frame.Save(Path.Combine(context.Captures, image));
                context.Report.Images.Add(ImageResult.From("Editor linked Software", image, VisualCapture.Measure(frame)));
                host.Controls.Remove(editor);
            }
            finally { RenderBackendSelection.Configure(previous); }
        }));
        check("TwoD.SoftwarePreviewSeedRepeatsProbabilisticLinks", () => WithEditor(original =>
        {
            ParticleConfig linked = new()
            {
                Preview2D = true, MaxParticles = 64, Loop = false, BurstCount = 64,
                Speed = 0, Gravity = 0, Lifetime = 4, LifetimeVariance = 0,
            };
            linked.Emitters.Add(new ParticleEmitterLayer
            {
                Id = "child", Name = "Sparks", Config = new ParticleConfig
                {
                    MaxParticles = 64, Loop = false, BurstCount = 0,
                    Speed = 0, Gravity = 0, Lifetime = 4, LifetimeVariance = 0,
                },
            });
            linked.EventLinks.Add(new ParticleEventLink
            {
                SourceEmitterId = "primary", TargetEmitterId = "child",
                Trigger = ParticleEventTrigger.Birth, Count = 1, Probability = .5,
            });
            File.WriteAllText(original.ResourcePath, Text(linked));
            RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
            try
            {
                RenderBackendSelection.Configure(RenderBackendOption.Software);
                using ParticleEditorControl editor = new(original.ResourcePath, original.ProjectRoot);
                using Form host = GateSuite.NewHost(1280, 800);
                host.Controls.Add(editor); GateSuite.ShowHost(host);
                using (editor.Viewport.CaptureFrame(2)) { }
                int Run(int seed)
                {
                    editor.SetPreviewSeed(seed); editor.StopPreview(); editor.StepForTest(1f / 60f);
                    return Field<List<ParticleSimulation>>(editor, "_previewSimulations")[1].ActiveCount;
                }
                int first = Run(1337);
                Assert(first is > 0 and < 64 && Run(1337) == first && Run(1337) == first,
                    "Restart with the same seed changed probabilistic child emission.");
                Assert(Enumerable.Range(2000, 8).Any(seed => Run(seed) != first),
                    "Changing the preview seed did not affect probabilistic links.");
                host.Controls.Remove(editor);
            }
            finally { RenderBackendSelection.Configure(previous); }
        }));
        check("TwoD.TrailRibbonAndBeamChangeTheRenderedShapeOnAllBackends", () => WithEditor(original =>
        {
            ResourceService resources = ProjectAssetIndex.OpenResourceService(original.ProjectRoot);
            string imagePath = resources.CreateResource(ResourceFolderPolicy.RootFor(resources.Project, ResourceKind.Image), ResourceKind.Image, "Particle strip marker");
            string texturePath = Path.ChangeExtension(imagePath, ".png");
            using (Bitmap texture = new(8, 8))
            {
                using Graphics graphics = Graphics.FromImage(texture);
                graphics.Clear(Color.White); texture.Save(texturePath);
            }
            ImageDocument imageDocument = ImageDocument.CreateDefault(8, 8);
            imageDocument.Frames.Add(new ImageFrame { Source = Path.GetFileName(texturePath) });
            File.WriteAllText(imagePath, ImageDocumentSerializer.Serialize(imageDocument));
            ParticleConfig effect = new()
            {
                Preview2D = true, Shape = ParticleEmitShape.Cone, SpreadDegrees = 0, Speed = 10,
                SpeedVariance = 0, Lifetime = 2, LifetimeVariance = 0, Gravity = 0, Drag = 0,
                StartSize = .8, EndSize = .8, MaxParticles = 16, Loop = false, BurstCount = 1,
                RendererKind = ParticleRendererKind.Trail, TrailDuration = .45, TrailWidth = 1,
                TexturePath = "Particle strip marker", StartColor = new ParticleColor(.1f, .95f, .2f, 1),
                EndColor = new ParticleColor(.1f, .95f, .2f, 1),
            };
            File.WriteAllText(original.ResourcePath, Text(effect));
            using ParticleEditorControl authoring = new(original.ResourcePath, original.ProjectRoot);
            JObject document = JObject.Parse(File.ReadAllText(authoring.CreateEffectObject("Strip preview")));
            RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
            try
            {
                foreach (RenderBackendOption backend in new[] { RenderBackendOption.SilkNetDx11, RenderBackendOption.Direct3D12,
                    RenderBackendOption.Vulkan, RenderBackendOption.OpenGL, RenderBackendOption.Software })
                {
                    RenderBackendSelection.Configure(backend);
                    using Form host = GateSuite.NewHost(800, 600);
                    using ObjectCompositionPreviewControl preview = new(original.ProjectRoot, compact: true) { Playing = false };
                    host.Controls.Add(preview); GateSuite.ShowHost(host);
                    foreach (ParticleRendererKind kind in new[] { ParticleRendererKind.Trail, ParticleRendererKind.Ribbon, ParticleRendererKind.Beam })
                    {
                        ParticleConfig current = effect.Clone(); current.RendererKind = kind;
                        current.Loop = kind == ParticleRendererKind.Ribbon;
                        current.EmitRate = 10; current.BurstCount = current.Loop ? 0 : 1;
                        current.BeamEndY = 8; current.BeamNoise = 0;
                        File.WriteAllText(original.ResourcePath, Text(current));
                        preview.Reload(document);
                        preview.Viewport.Camera2DX = 0; preview.Viewport.Camera2DY = 0; preview.Viewport.Zoom2D = 1;
                        RuntimeScene scene = Field<RuntimeScene>(preview, "_scene");
                        AdvanceHalfSecond(scene);
                        using Bitmap frame = preview.CaptureFrame(3)!;
                        MarkerPixels pixels = MeasureMarker(frame, backend + " " + kind);
                        Assert(pixels.Height >= 25 && pixels.Width is >= 4 and <= 18,
                            $"{backend} {kind} rendered an ordinary billboard instead of its authored strip: {pixels}.");
                        Assert(preview.Viewport.Host.Renderer.BackendName == (backend switch
                        {
                            RenderBackendOption.SilkNetDx11 => "Direct3D 11", RenderBackendOption.Direct3D12 => "Direct3D 12",
                            RenderBackendOption.Vulkan => "Vulkan", RenderBackendOption.OpenGL => "OpenGL", _ => "Software",
                        }) && preview.Viewport.Host.RenderFaultCount == 0, backend + " did not render a healthy " + kind + " strip.");
                        string image = $"particle-strip-{backend}-{kind}.png";
                        frame.Save(Path.Combine(context.Captures, image));
                        context.Report.Images.Add(ImageResult.From(backend + " " + kind, image, VisualCapture.Measure(frame)));
                    }
                    host.Controls.Remove(preview);
                }
            }
            finally { RenderBackendSelection.Configure(previous); }
        }));
        check("TwoD.FireRainAndPortalPlayOnAllBackends", () => WithEditor(authoring =>
        {
            authoring.SetPreview2D(true);
            string path = authoring.CreateEffectObject("Effect preview");
            JObject document = JObject.Parse(File.ReadAllText(path));
            JObject empty = (JObject)document.DeepClone();
            foreach (JObject particle in empty["components"]!.OfType<JObject>()
                .Where(component => (string?)component["type"] == "ParticleComponent").ToArray()) particle.Remove();
            RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
            try
            {
                foreach (RenderBackendOption backend in new[] { RenderBackendOption.SilkNetDx11, RenderBackendOption.Direct3D12,
                    RenderBackendOption.Vulkan, RenderBackendOption.OpenGL, RenderBackendOption.Software })
                {
                    RenderBackendSelection.Configure(backend);
                    using Form host = GateSuite.NewHost(960, 640);
                    using ObjectCompositionPreviewControl preview = new(authoring.ProjectRoot, compact: true) { Playing = false };
                    host.Controls.Add(preview); GateSuite.ShowHost(host);
                    foreach (string preset in new[] { "Fire", "Rain", "Portal" })
                    {
                        authoring.ApplyPreset(preset); authoring.Save();
                        preview.Reload(document);
                        RuntimeScene scene = Field<RuntimeScene>(preview, "_scene");
                        AdvanceHalfSecond(scene);
                        using (preview.CaptureFrame(3)) { }
                        AdvanceHalfSecond(scene);
                        using Bitmap frame = preview.CaptureFrame(3)!;
                        string image = $"particle-preset-{preset}-{backend}.png";
                        frame.Save(Path.Combine(context.Captures, image));
                        context.Report.Images.Add(ImageResult.From(preset + " — " + backend, image, VisualCapture.Measure(frame)));
                        int live = preview.ActiveParticleCount;
                        float zoom = preview.Viewport.Zoom2D;
                        preview.Reload(empty); preview.Viewport.Zoom2D = zoom;
                        using Bitmap baseline = preview.CaptureFrame(3)!;
                        int changed = 0, readable = 0, coloured = 0;
                        for (int y = 40; y < frame.Height - 30; y++)
                        for (int x = 0; x < frame.Width; x++)
                        {
                            Color effect = frame.GetPixel(x, y), background = baseline.GetPixel(x, y);
                            if (Math.Abs(effect.R - background.R) + Math.Abs(effect.G - background.G)
                                + Math.Abs(effect.B - background.B) > 12)
                            {
                                changed++;
                                int peak = Math.Max(effect.R, Math.Max(effect.G, effect.B));
                                int low = Math.Min(effect.R, Math.Min(effect.G, effect.B));
                                if (peak >= 45) readable++;
                                if (peak >= 45 && peak - low >= 30) coloured++;
                            }
                        }
                        string expected = backend switch
                        {
                            RenderBackendOption.SilkNetDx11 => "Direct3D 11", RenderBackendOption.Direct3D12 => "Direct3D 12",
                            RenderBackendOption.Vulkan => "Vulkan", RenderBackendOption.OpenGL => "OpenGL", _ => "Software",
                        };
                        Assert(preview.Viewport.Host.Renderer.BackendName == expected && preview.Viewport.Host.RenderFaultCount == 0,
                            preset + " did not use the requested healthy " + expected + " renderer.");
                        Assert(live > 0 && changed > 40, $"{preset} did not visibly play on {backend}: live={live}, changed={changed}.");
                        Assert(preset == "Rain" ? readable >= 100 : coloured >= 30,
                            $"{preset} lost readable contrast or its authored colour on {backend}: readable={readable}, coloured={coloured}.");
                    }
                    host.Controls.Remove(preview);
                }
            }
            finally { RenderBackendSelection.Configure(previous); }
        }));
        check("TwoD.PixelSizeDirectionPlacementAndLiveReloadOnAllBackends", () => WithEditor(original =>
        {
            ResourceService resources = ProjectAssetIndex.OpenResourceService(original.ProjectRoot);
            string imagePath = resources.CreateResource(ResourceFolderPolicy.RootFor(resources.Project, ResourceKind.Image), ResourceKind.Image, "Particle marker");
            string texturePath = Path.ChangeExtension(imagePath, ".png");
            using (Bitmap texture = new(8, 8))
            {
                using Graphics graphics = Graphics.FromImage(texture);
                graphics.Clear(Color.White);
                texture.Save(texturePath);
            }
            ImageDocument imageDocument = ImageDocument.CreateDefault(8, 8);
            imageDocument.Frames.Add(new ImageFrame { Source = Path.GetFileName(texturePath) });
            File.WriteAllText(imagePath, ImageDocumentSerializer.Serialize(imageDocument));
            ParticleConfig marker = new()
            {
                Preview2D = true, MaxParticles = 16, Loop = false, BurstCount = 1,
                Shape = ParticleEmitShape.Cone, SpreadDegrees = 0, Speed = 10,
                SpeedVariance = 0, Lifetime = 4, LifetimeVariance = 0,
                StartSize = .8, EndSize = .8, Gravity = 0, Drag = 0,
                TurbulenceStrength = 0, Emissive = 1, ColorJitter = 0,
                RotationSpeed = 0, RotationVariance = 0,
                StartColor = new ParticleColor(.1f, .95f, .2f, 1),
                EndColor = new ParticleColor(.1f, .95f, .2f, 1),
                TexturePath = "Particle marker",
            };
            File.WriteAllText(original.ResourcePath, Text(marker));
            using ParticleEditorControl authoring = new(original.ResourcePath, original.ProjectRoot);
            string objectPath = authoring.CreateEffectObject("Planar marker");
            JObject document = JObject.Parse(File.ReadAllText(objectPath));
            JObject transform = document["components"]!.OfType<JObject>().Single(component => (string?)component["type"] == "TransformComponent");
            transform["props"]!["Position"] = new JArray(100, 150, 0);
            string source = File.ReadAllText(original.ResourcePath);
            RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
            try
            {
                foreach ((RenderBackendOption backend, string expected) in new[]
                {
                    (RenderBackendOption.SilkNetDx11, "Direct3D 11"), (RenderBackendOption.Direct3D12, "Direct3D 12"),
                    (RenderBackendOption.Vulkan, "Vulkan"), (RenderBackendOption.OpenGL, "OpenGL"),
                    (RenderBackendOption.Software, "Software"),
                })
                {
                    File.WriteAllText(original.ResourcePath, source);
                    RenderBackendSelection.Configure(backend);
                    using Form host = GateSuite.NewHost(800, 600);
                    using ObjectCompositionPreviewControl preview = new(original.ProjectRoot, compact: true) { Playing = false };
                    host.Controls.Add(preview); GateSuite.ShowHost(host);
                    preview.Reload(document);
                    preview.Viewport.Camera2DX = 0; preview.Viewport.Camera2DY = 0; preview.Viewport.Zoom2D = 1;
                    RuntimeScene scene = Field<RuntimeScene>(preview, "_scene");
                    using Bitmap first = preview.CaptureFrame(3)!;
                    MarkerPixels born = MeasureMarker(first, expected + " birth");
                    Assert(Math.Abs(born.X - first.Width * .5 - 100) <= 2
                        && Math.Abs(born.Y - first.Height * .5 - 150) <= 3,
                        $"{expected} placed a fresh particle away from its Object: {born}.");
                    AdvanceHalfSecond(scene);
                    using Bitmap travelled = preview.CaptureFrame(3)!;
                    MarkerPixels moved = MeasureMarker(travelled, expected + " travel");
                    Assert(Math.Abs(moved.X - born.X) <= 1 && born.Y - moved.Y is >= 57 and <= 63,
                        $"{expected} did not move the authored upward 10-unit/s particle by 60 pixels in 0.5s: {born} -> {moved}.");
                    Assert(moved.Width is >= 9 and <= 11 && moved.Height is >= 9 and <= 11,
                        $"{expected} scaled particle size differently from its travel: {moved}.");
                    Assert(preview.Viewport.Host.Renderer.BackendName == expected && preview.Viewport.Host.RenderFaultCount == 0,
                        "Planar particle readback did not use the requested healthy " + expected + " renderer.");
                    string image = "particle-planar-" + backend + ".png";
                    travelled.Save(Path.Combine(context.Captures, image));
                    context.Report.Images.Add(ImageResult.From("Planar particle placement — " + expected, image, VisualCapture.Measure(travelled)));
                    Assert(File.ReadAllText(original.ResourcePath) == source,
                        "Rendering a planar effect rewrote its authored values into pixel units.");
                    Assert(authoring.TryApplyInspectorValue("speed", 20), "The live authoring field rejected the new speed.");
                    authoring.Save();
                    scene.UpdateVariable(1f / 60f);
                    using Bitmap liveFirst = preview.CaptureFrame(3)!;
                    MarkerPixels liveBorn = MeasureMarker(liveFirst, expected + " live saved birth");
                    AdvanceHalfSecond(scene);
                    using Bitmap liveFrame = preview.CaptureFrame(3)!;
                    MarkerPixels liveMoved = MeasureMarker(liveFrame, expected + " live saved travel");
                    Assert(liveBorn.Y - liveMoved.Y is >= 117 and <= 123,
                        $"{expected} retained the old speed after a live editor save: {liveBorn} -> {liveMoved}.");
                    Assert(authoring.TryApplyInspectorValue("speed", 10), "The live authoring speed could not be restored.");
                    authoring.Save();
                    JObject empty = (JObject)document.DeepClone();
                    foreach (JObject particle in empty["components"]!.OfType<JObject>()
                        .Where(component => (string?)component["type"] == "ParticleComponent").ToArray()) particle.Remove();
                    preview.Reload(empty);
                    preview.Viewport.Camera2DX = 0; preview.Viewport.Camera2DY = 0; preview.Viewport.Zoom2D = 1;
                    using Bitmap backgroundFrame = preview.CaptureFrame(3)!;
                    Color backgroundPixel = backgroundFrame.GetPixel(backgroundFrame.Width / 2 + 100, backgroundFrame.Height / 2 + 150);
                    foreach (ParticleBlendMode blend in Enum.GetValues<ParticleBlendMode>())
                    {
                        ParticleConfig translucent = marker.Clone();
                        translucent.Speed = 0; translucent.BlendMode = blend;
                        translucent.StartColor.A = translucent.EndColor.A = .5f;
                        File.WriteAllText(original.ResourcePath, Text(translucent));
                        preview.Reload(document);
                        preview.Viewport.Camera2DX = 0; preview.Viewport.Camera2DY = 0; preview.Viewport.Zoom2D = 1;
                        using Bitmap blended = preview.CaptureFrame(3)!;
                        Color actual = blended.GetPixel(blended.Width / 2 + 100, blended.Height / 2 + 150);
                        double Channel(byte background, float colour) => blend switch
                        {
                            ParticleBlendMode.Additive => background + 255 * colour * .5,
                            ParticleBlendMode.Multiply => background * (colour * .5 + .5),
                            _ => background * .5 + 255 * colour * .5,
                        };
                        Assert(Math.Abs(actual.R - Channel(backgroundPixel.R, .1f)) <= 2
                            && Math.Abs(actual.G - Channel(backgroundPixel.G, .95f)) <= 2
                            && Math.Abs(actual.B - Channel(backgroundPixel.B, .2f)) <= 2,
                            $"{expected} did not physically apply {blend} compositing: background={backgroundPixel}, particle={actual}.");
                        string blendImage = $"particle-blend-{backend}-{blend}.png";
                        blended.Save(Path.Combine(context.Captures, blendImage));
                        context.Report.Images.Add(ImageResult.From(expected + " blend " + blend, blendImage, VisualCapture.Measure(blended)));
                    }
                    foreach (ParticleSimulationSpace space in Enum.GetValues<ParticleSimulationSpace>())
                    {
                        ParticleConfig collision = marker.Clone();
                        collision.SimulationSpace = space;
                        collision.CollisionMode = ParticleCollisionMode.Stick;
                        collision.CollisionPlaneHeight = -4;
                        collision.DownwardEmit = true;
                        File.WriteAllText(original.ResourcePath, Text(collision));
                        preview.Reload(document);
                        preview.Viewport.Camera2DX = 0; preview.Viewport.Camera2DY = 0; preview.Viewport.Zoom2D = 1;
                        scene = Field<RuntimeScene>(preview, "_scene");
                        using (preview.CaptureFrame(3)) { }
                        AdvanceHalfSecond(scene);
                        using Bitmap stopped = preview.CaptureFrame(3)!;
                        string collisionImage = $"particle-collision-{backend}-{space}.png";
                        stopped.Save(Path.Combine(context.Captures, collisionImage));
                        context.Report.Images.Add(ImageResult.From(expected + " collision " + space, collisionImage, VisualCapture.Measure(stopped)));
                        MarkerPixels contact = MeasureMarker(stopped, expected + " collision " + space);
                        double expectedFloor = stopped.Height * .5 + 150 + 48 - 2.4;
                        Assert(Math.Abs(contact.Y - expectedFloor) <= 1.5,
                            $"{expected} {space} particles did not stop at the Object's collision plane: {contact}, expected Y {expectedFloor}.");
                    }

                    File.WriteAllText(original.ResourcePath, source);
                    transform["props"]!["Scale"] = "2,3,1";
                    transform["props"]!["Rotation"] = "0,0,90";
                    preview.Reload(document);
                    preview.Viewport.Camera2DX = 0; preview.Viewport.Camera2DY = 0; preview.Viewport.Zoom2D = 1;
                    scene = Field<RuntimeScene>(preview, "_scene");
                    using Bitmap rotatedFirst = preview.CaptureFrame(3)!;
                    MarkerPixels rotatedBorn = MeasureMarker(rotatedFirst, expected + " rotated birth");
                    AdvanceHalfSecond(scene);
                    using Bitmap rotatedFrame = preview.CaptureFrame(3)!;
                    string transformedImage = "particle-transformed-" + backend + ".png";
                    rotatedFrame.Save(Path.Combine(context.Captures, transformedImage));
                    context.Report.Images.Add(ImageResult.From(expected + " transformed particle", transformedImage, VisualCapture.Measure(rotatedFrame)));
                    MarkerPixels rotatedMoved = MeasureMarker(rotatedFrame, expected + " rotated travel");
                    Assert(rotatedMoved.X - rotatedBorn.X is >= 177 and <= 183
                        && Math.Abs(rotatedMoved.Y - rotatedBorn.Y) <= 1.5
                        && rotatedMoved.Width is >= 18 and <= 21 && rotatedMoved.Height is >= 27 and <= 31,
                        $"{expected} did not apply Object rotation/nonuniform scale to actual particle travel and size: {rotatedBorn} -> {rotatedMoved}.");
                    transform["props"]!["Scale"] = "1,1,1";
                    ParticleConfig aligned = marker.Clone();
                    aligned.Alignment = ParticleAlignment.Velocity; aligned.VelocityStretch = .1;
                    File.WriteAllText(original.ResourcePath, Text(aligned));
                    preview.Reload(document);
                    preview.Viewport.Camera2DX = 0; preview.Viewport.Camera2DY = 0; preview.Viewport.Zoom2D = 1;
                    using Bitmap alignedFrame = preview.CaptureFrame(3)!;
                    MarkerPixels alignment = MeasureMarker(alignedFrame, expected + " velocity alignment");
                    Assert(alignment.Width is >= 18 and <= 21 && alignment.Height is >= 9 and <= 11,
                        $"{expected} did not orient and stretch the actual billboard along its rightward velocity: {alignment}.");
                    string alignedImage = "particle-alignment-" + backend + ".png";
                    alignedFrame.Save(Path.Combine(context.Captures, alignedImage));
                    context.Report.Images.Add(ImageResult.From(expected + " velocity alignment", alignedImage, VisualCapture.Measure(alignedFrame)));
                    transform["props"]!["Rotation"] = "0,0,0";
                    Assert(preview.Viewport.Host.RenderFaultCount == 0, expected + " faulted during the live/collision/transformed readbacks.");
                    host.Controls.Remove(preview);
                }
            }
            finally { RenderBackendSelection.Configure(previous); }
        }));
        check("TwoD.AnimatedTextureChangesPixelsOnAllBackends", () => WithEditor(original =>
        {
            ResourceService resources = ProjectAssetIndex.OpenResourceService(original.ProjectRoot);
            string imagePath = resources.CreateResource(ResourceFolderPolicy.RootFor(resources.Project, ResourceKind.Image), ResourceKind.Image, "Particle atlas");
            string texturePath = Path.ChangeExtension(imagePath, ".png");
            using (Bitmap atlas = new(8, 4))
            {
                using Graphics graphics = Graphics.FromImage(atlas);
                graphics.Clear(Color.Red); graphics.FillRectangle(Brushes.Blue, 4, 0, 4, 4);
                atlas.Save(texturePath);
            }
            ImageDocument imageDocument = ImageDocument.CreateDefault(8, 4);
            imageDocument.Frames.Add(new ImageFrame { Source = Path.GetFileName(texturePath) });
            File.WriteAllText(imagePath, ImageDocumentSerializer.Serialize(imageDocument));
            ParticleConfig config = new()
            {
                Preview2D = true, Shape = ParticleEmitShape.Cone, SpreadDegrees = 0, Speed = 0,
                Gravity = 0, Drag = 0, Loop = false, BurstCount = 1, MaxParticles = 1,
                Lifetime = 4, LifetimeVariance = 0, StartSize = 4, EndSize = 4,
                RotationSpeed = 0, RotationVariance = 0, TexturePath = "Particle atlas",
                StartColor = new ParticleColor(1, 1, 1, 1), EndColor = new ParticleColor(1, 1, 1, 1),
                UseFlipbook = true, FlipbookColumns = 2, FlipbookRows = 1, FlipbookFps = 2,
            };
            File.WriteAllText(original.ResourcePath, Text(config));
            using ParticleEditorControl authoring = new(original.ResourcePath, original.ProjectRoot);
            JObject document = JObject.Parse(File.ReadAllText(authoring.CreateEffectObject("Animated effect")));
            RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
            try
            {
                foreach (RenderBackendOption backend in new[] { RenderBackendOption.SilkNetDx11, RenderBackendOption.Direct3D12,
                    RenderBackendOption.Vulkan, RenderBackendOption.OpenGL, RenderBackendOption.Software })
                {
                    RenderBackendSelection.Configure(backend);
                    using Form host = GateSuite.NewHost(800, 600);
                    using ObjectCompositionPreviewControl preview = new(original.ProjectRoot, compact: true) { Playing = false };
                    host.Controls.Add(preview); GateSuite.ShowHost(host); preview.Reload(document);
                    preview.Viewport.Camera2DX = 0; preview.Viewport.Camera2DY = 0; preview.Viewport.Zoom2D = 1;
                    RuntimeScene scene = Field<RuntimeScene>(preview, "_scene");
                    using Bitmap first = preview.CaptureFrame(3)!;
                    Color red = first.GetPixel(first.Width / 2, first.Height / 2);
                    Assert(red.R >= 250 && red.B <= 3, backend + " did not render the first red texture cell: " + red);
                    for (int frame = 0; frame < 31; frame++) scene.UpdateVariable(1f / 60f);
                    using Bitmap second = preview.CaptureFrame(3)!;
                    Color blue = second.GetPixel(second.Width / 2, second.Height / 2);
                    Assert(blue.B >= 250 && blue.R <= 3 && preview.Viewport.Host.RenderFaultCount == 0,
                        backend + " did not visibly advance the particle's animated texture: " + blue);
                    foreach ((Bitmap frame, string label) in new[] { (first, "red"), (second, "blue") })
                    {
                        string image = $"particle-atlas-{backend}-{label}.png";
                        frame.Save(Path.Combine(context.Captures, image));
                        context.Report.Images.Add(ImageResult.From(backend + " texture " + label, image, VisualCapture.Measure(frame)));
                    }
                    host.Controls.Remove(preview);
                }
            }
            finally { RenderBackendSelection.Configure(previous); }
        }));
        check("TwoD.SoftwareRingLivesInXYAndLocalPlacementMovesItsParticles", () =>
        {
            ParticleConfig ring = new()
            {
                Shape = ParticleEmitShape.Ring, EmitRadius = 2, Loop = false, BurstCount = 128,
                MaxParticles = 128, Speed = 0, StartSize = .5, EndSize = .5,
                Lifetime = 4, LifetimeVariance = 0, Gravity = 0, Drag = 0,
                TurbulenceStrength = 0, SimulationSpace = ParticleSimulationSpace.Local,
            };
            ParticleSimulation simulation = new();
            simulation.SetPlanarTransform(Matrix4x4.CreateTranslation(100, 150, 0));
            simulation.LoadConfig(Particle2DLayout.ForSimulation(ring));
            SpriteDrawCall[] calls = new SpriteDrawCall[128];
            int count = simulation.FillSpriteDrawCalls2D(calls, 0, 0, 1, default);
            float width = calls.Take(count).Max(call => call.X) - calls.Take(count).Min(call => call.X);
            float height = calls.Take(count).Max(call => call.Y) - calls.Take(count).Min(call => call.Y);
            Assert(count == 128 && width > 45 && height > 45,
                $"A 2D ring was flattened into an XZ line: {width} x {height}.");
            float x = calls[0].X, y = calls[0].Y;
            simulation.SetPlanarTransform(Matrix4x4.CreateTranslation(180, 220, 0));
            simulation.FillSpriteDrawCalls2D(calls, 0, 0, 1, default);
            Assert(Math.Abs(calls[0].X - x - 80) < .01 && Math.Abs(calls[0].Y - y - 70) < .01,
                "Local-space particles did not follow the Object's live XY placement.");
            simulation.SetPlanarTransform(Matrix4x4.CreateScale(2, 3, 1) * Matrix4x4.CreateTranslation(180, 220, 0));
            simulation.FillSpriteDrawCalls2D(calls, 0, 0, 1, default);
            Assert(Math.Abs(calls[0].Width - 12) < .01 && Math.Abs(calls[0].Height - 18) < .01,
                "Nonuniform Object scale did not resize the actual planar billboards.");
        });
        check("TwoD.SoftwareVelocityAlignmentStretchAndFlipbookAdvance", () =>
        {
            ParticleConfig config = Particle2DLayout.ForSimulation(new ParticleConfig
            {
                Shape = ParticleEmitShape.Cone, SpreadDegrees = 0, Speed = 10, SpeedVariance = 0,
                Loop = false, BurstCount = 1, MaxParticles = 1, Lifetime = 4, LifetimeVariance = 0,
                Gravity = 0, Drag = 0, Alignment = ParticleAlignment.Velocity, VelocityStretch = .1,
                RotationSpeed = 0, RotationVariance = 0, StartSize = 1, EndSize = 1,
                UseFlipbook = true, FlipbookColumns = 2, FlipbookRows = 1, FlipbookFps = 2,
                BlendMode = ParticleBlendMode.Additive,
            });
            ParticleSimulation simulation = new();
            simulation.SetPlanarTransform(Matrix4x4.CreateRotationZ(MathF.PI / 2));
            simulation.LoadConfig(config);
            SpriteDrawCall[] calls = new SpriteDrawCall[1];
            simulation.FillSpriteDrawCalls2D(calls, 0, 0, 1, default);
            Assert(Math.Abs(calls[0].Rotation - 90) < .01 && Math.Abs(calls[0].Height - 24) < .01
                && Math.Abs(calls[0].Width - 12) < .01 && calls[0].UvRect == new Vector4(0, 0, .5f, 1)
                && calls[0].Blend == BlendMode.Additive,
                "The live sprite command omitted velocity alignment, world-unit stretch, the initial texture frame or blend.");
            for (int frame = 0; frame < 31; frame++) simulation.Step(1f / 60f);
            simulation.FillSpriteDrawCalls2D(calls, 0, 0, 1, default);
            Assert(calls[0].UvRect == new Vector4(.5f, 0, 1, 1), "The software particle texture did not advance to the second authored frame.");
        });
        check("TwoD.CollisionModesUsePlacedPlaneInBothSimulationSpaces", () =>
        {
            foreach (ParticleSimulationSpace space in Enum.GetValues<ParticleSimulationSpace>())
            foreach (ParticleCollisionMode mode in new[] { ParticleCollisionMode.Bounce, ParticleCollisionMode.Stick, ParticleCollisionMode.Die })
            {
                ParticleConfig config = Particle2DLayout.ForSimulation(new ParticleConfig
                {
                    Shape = ParticleEmitShape.Cone, SpreadDegrees = 0, Speed = 10, SpeedVariance = 0,
                    Lifetime = 4, LifetimeVariance = 0, Loop = false, BurstCount = 1, MaxParticles = 1,
                    Gravity = 0, Drag = 0, DownwardEmit = true, StartSize = .8, EndSize = .8,
                    CollisionMode = mode, CollisionPlaneHeight = -4, CollisionBounce = 1, SimulationSpace = space,
                });
                Assert(config.Clone().IsPlanar2D && Particle2DLayout.ForSimulation(config.Clone()).Speed == config.Speed,
                    "Cloning a simulation applied the planar unit conversion twice.");
                ParticleSimulation simulation = new();
                List<ParticleSimulation.ParticleEvent> events = [];
                simulation.Occurred += events.Add;
                simulation.SetPlanarTransform(Matrix4x4.CreateTranslation(100, 150, 0));
                simulation.LoadConfig(config);
                for (int frame = 0; frame < 30; frame++) simulation.Step(1f / 60f);
                Assert(events.Count(e => (e.Trigger & ParticleEventTrigger.Birth) != 0) == 1
                    && events.Count(e => (e.Trigger & ParticleEventTrigger.Collision) != 0) == 1
                    && events.Count(e => (e.Trigger & ParticleEventTrigger.Death) != 0) == (mode == ParticleCollisionMode.Die ? 1 : 0),
                    $"{space} {mode} emitted repeated or missing birth/collision/death events: {string.Join(",", events.Select(e => e.Trigger))}.");
                SpriteDrawCall[] sprites = new SpriteDrawCall[1];
                int count = simulation.FillSpriteDrawCalls2D(sprites, 0, 0, 1, default);
                if (mode == ParticleCollisionMode.Die) Assert(count == 0, space + " collision did not kill the particle.");
                else
                {
                    float expectedY = mode == ParticleCollisionMode.Stick ? 195.6f : 181.2f;
                    Assert(count == 1 && Math.Abs(sprites[0].Y - expectedY) <= .05,
                        $"{space} {mode} collision produced Y {sprites[0].Y} instead of {expectedY}.");
                }
            }
        });
        check("TwoD.SoftwareTrailRetainsAndFadesItsTailAfterParticleDeath", () =>
        {
            ParticleConfig config = Particle2DLayout.ForSimulation(new ParticleConfig
            {
                Shape = ParticleEmitShape.Cone, SpreadDegrees = 0, Speed = 10, SpeedVariance = 0,
                Lifetime = .2, LifetimeVariance = 0, Loop = false, BurstCount = 1, MaxParticles = 1,
                Gravity = 0, Drag = 0, StartSize = .8, EndSize = .8,
                RendererKind = ParticleRendererKind.Trail, TrailDuration = .45, TrailWidth = 1,
                StartColor = new ParticleColor(.1f, .95f, .2f, 1), EndColor = new ParticleColor(.1f, .95f, .2f, 1),
            });
            ParticleSimulation simulation = new(); simulation.LoadConfig(config);
            SpriteDrawCall[] calls = new SpriteDrawCall[simulation.SpriteCapacity2D];
            for (int frame = 0; frame < 18; frame++) simulation.Step(1f / 60f);
            int tail = simulation.FillSpriteDrawCalls2D(calls, 0, 0, 1, default);
            Assert(tail > 0 && simulation.ActiveCount == 1,
                "The software trail disappeared as soon as its particle lifetime ended.");
            float tailAlpha = calls.Take(tail).Max(call => call.Alpha);
            for (int frame = 0; frame < 9; frame++) simulation.Step(1f / 60f);
            int fading = simulation.FillSpriteDrawCalls2D(calls, 0, 0, 1, default);
            Assert(fading > 0 && calls.Take(fading).Max(call => call.Alpha) < tailAlpha,
                "The software trail tail did not fade while it remained visible.");
            for (int frame = 0; frame < 30; frame++) simulation.Step(1f / 60f);
            Assert(simulation.ActiveCount == 0 && simulation.FillSpriteDrawCalls2D(calls, 0, 0, 1, default) == 0,
                "The software trail persisted past its authored duration.");
        });
    }

    private readonly record struct MarkerPixels(double X, double Y, int Width, int Height);

    private static void AdvanceHalfSecond(RuntimeScene scene)
    {
        // Use ordinary game frames: GameTime deliberately caps a stalled frame at 0.05 seconds.
        for (int frame = 0; frame < 30; frame++) scene.UpdateVariable(1f / 60f);
    }

    private static MarkerPixels MeasureMarker(Bitmap frame, string stage)
    {
        int count = 0, left = frame.Width, right = -1, top = frame.Height, bottom = -1;
        double sumX = 0, sumY = 0;
        for (int y = 40; y < frame.Height - 30; y++)
        for (int x = 0; x < frame.Width; x++)
        {
            Color pixel = frame.GetPixel(x, y);
            if (pixel.G < 70 || pixel.G <= pixel.R * 1.5 || pixel.G <= pixel.B * 1.5) continue;
            count++; sumX += x; sumY += y;
            left = Math.Min(left, x); right = Math.Max(right, x);
            top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        Assert(count >= 4, stage + ": the actual frame did not contain the authored green particle.");
        return new(sumX / count, sumY / count, right - left + 1, bottom - top + 1);
    }
}
