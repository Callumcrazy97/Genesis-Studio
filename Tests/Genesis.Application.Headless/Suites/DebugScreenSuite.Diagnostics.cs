using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using Genesis.Runtime;
using Genesis.Runtime.Debugger;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Scripting;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// The debug screen's diagnostics (C9, 29 Oct 2026): its settings through the standard API
/// (Engine.Debug.*), the collider overlay drawing a body (checked in the pixels of a capture), the
/// object report saying why an instance is or is not drawn, and the GUI page counting each layer's
/// draw calls.
/// </summary>
internal static partial class DebugScreenSuite
{
    private static void RunDiagnosticsCases(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Runtime.DebugScreen.Diagnostics.SettingsRoundTripThroughApi", () =>
        {
            DebugScreenSettings.Reset();
            try
            {
                using var overlay = new DebugOverlay();
                HeadlessHarness.Assert(!overlay.IsVisible && !DebugScreenSettings.Colliders && !DebugScreenSettings.CollidersShown
                    && DebugScreenSettings.ActiveView == DebugViewMode.Off && DebugScreenSettings.ModelView == DebugViewMode.Off,
                    "Every debug drawing must be off unless set.");

                string[] names =
                [
                    "Engine.Debug.Enabled", "Engine.Debug.Overlay.Enabled", "Engine.Debug.Overlay.Colliders.Enabled",
                    "Engine.Debug.Overlay.Colliders.Bodies", "Engine.Debug.Overlay.Colliders.Characters", "Engine.Debug.Overlay.Colliders.Triggers",
                    "Engine.Debug.Overlay.Colliders.Tiles", "Engine.Debug.Overlay.Colliders.Hitboxes", "Engine.Debug.Overlay.Gui.Enabled",
                    "Engine.Debug.Overlay.Wireframe.Enabled", "Engine.Debug.Inspect.Enabled", "Engine.Debug.Inspect.Instance",
                    "Engine.Debug.View.Enabled", "Engine.Debug.View.Mode",
                ];
                foreach (string name in names)
                    HeadlessHarness.Assert(EngineSettings.TryFind(name, out _), $"{name} must be a value the Inspector can set.");

                var context = new PgslContext();
                object? Read(string name) => EngineSettings.TryRead(name, context, out object? value) ? value : null;
                void Set(string name, string text)
                {
                    var problems = new List<string>();
                    int applied = EngineSettings.Apply([new KeyValuePair<string, string>(name, text)], context, problems);
                    HeadlessHarness.Assert(applied == 1, $"{name} = {text} was not set: {string.Join("; ", problems)}");
                }

                HeadlessHarness.Assert(Read("Engine.Debug.Overlay.Colliders.Enabled") is false && Read("Engine.Debug.Overlay.Colliders.Triggers") is true,
                    "Colliders must read off, and each kind on, unless set.");

                // A part turned on turns on what it belongs to, up to the screen.
                Set("Engine.Debug.Overlay.Colliders.Enabled", "true");
                HeadlessHarness.Assert(DebugScreenSettings.Colliders && DebugScreenSettings.Overlay && overlay.IsVisible && DebugScreenSettings.CollidersShown,
                    "Turning the collider overlay on must open the overlays and the screen.");
                HeadlessHarness.Assert(Read("Engine.Debug.Overlay.Colliders.Enabled") is true && Read("Engine.Debug.Overlay.Enabled") is true
                    && Read("Engine.Debug.Enabled") is true, "The collider overlay, the overlays and the screen must read back on.");
                Set("Engine.Debug.Overlay.Colliders.Triggers", "false");
                HeadlessHarness.Assert(Read("Engine.Debug.Overlay.Colliders.Triggers") is false && !DebugScreenSettings.ColliderTriggers,
                    "A collider kind must read back as set.");

                Set("Engine.Debug.View.Mode", "overdraw");
                HeadlessHarness.Assert(Read("Engine.Debug.View.Mode") as string == "overdraw" && Read("Engine.Debug.View.Enabled") is true
                    && DebugScreenSettings.ActiveView == DebugViewMode.Overdraw && DebugScreenSettings.ModelView == DebugViewMode.Overdraw,
                    "Setting a view mode must turn the view on and read back by name.");
                Set("Engine.Debug.Inspect.Instance", "7");
                HeadlessHarness.Assert(Read("Engine.Debug.Inspect.Instance") is double instance && instance == 7, "The inspected instance must read back.");
                Set("Engine.Debug.Overlay.Gui.Enabled", "true");
                HeadlessHarness.Assert(Read("Engine.Debug.Overlay.Gui.Enabled") is true && DebugScreenSettings.GuiBoundsShown, "GUI outlines must read back on.");

                // Closing the screen hides everything it draws; its parts come back as they were.
                overlay.IsVisible = false;
                HeadlessHarness.Assert(!DebugScreenSettings.CollidersShown && !DebugScreenSettings.GuiBoundsShown
                    && DebugScreenSettings.ActiveView == DebugViewMode.Off && DebugScreenSettings.ModelView == DebugViewMode.Off
                    && DebugScreenSettings.Colliders && DebugScreenSettings.View, "A closed screen must draw nothing and keep its parts.");
                overlay.IsVisible = true;
                HeadlessHarness.Assert(DebugScreenSettings.CollidersShown && DebugScreenSettings.ActiveView == DebugViewMode.Overdraw,
                    "Reopening the screen must bring its parts back.");

                // A script sets them as code does.
                RunScript("Engine.Debug.View.Mode = \"depth\";");
                HeadlessHarness.Assert(DebugScreenSettings.ViewMode == DebugViewMode.Depth
                    && DebugScreenSettings.RendererView(DebugScreenSettings.ActiveView) == RenderDebugView.SceneDepth,
                    "A script's Engine.Debug.View.Mode = \"depth\" must ask the renderer for its depth view.");
                RunScript("Engine.Debug.View.Mode = \"off\";");
                HeadlessHarness.Assert(!DebugScreenSettings.View && DebugScreenSettings.ActiveView == DebugViewMode.Off, "\"off\" must turn the view off.");
                Set("Engine.Debug.Overlay.Enabled", "false");
                HeadlessHarness.Assert(!DebugScreenSettings.CollidersShown && DebugScreenSettings.Colliders, "Turning the overlays off must hide the colliders and keep them on.");
            }
            finally
            {
                DebugScreenSettings.Reset();
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.DebugScreen.Diagnostics.ColliderOverlayDrawsBodyPixels", () =>
        {
            DebugScreenSettings.Reset();
            try
            {
                using var scene = new RuntimeScene("ColliderProbe");
                scene.Camera3D.AspectRatio = 1280f / 720f;
                Vector3 centre = scene.Camera3D.Position + scene.Camera3D.Forward * 6f;
                Entity box = scene.World.CreateEntity();
                scene.World.Set(box, new Transform3DComponent { Position = centre, Rotation = Quaternion.Identity, Scale = Vector3.One });
                scene.World.Set(box, RigidBodyComponent.StaticBox(new Vector3(1f)));
                Entity trigger = scene.World.CreateEntity();
                RigidBodyComponent sensor = RigidBodyComponent.StaticBox(new Vector3(0.4f));
                sensor.Flags |= RigidBodyFlags.Sensor;
                scene.World.Set(trigger, new Transform3DComponent { Position = centre + scene.Camera3D.Right * 2.5f, Rotation = Quaternion.Identity, Scale = Vector3.One });
                scene.World.Set(trigger, sensor);

                using var overlay = new DebugOverlay();
                overlay.BindScene(scene, "ColliderProbe");
                overlay.IsVisible = true;
                int Pixels(string fileName, out int triggerPixels)
                {
                    using Bitmap bitmap = new(1280, 720, PixelFormat.Format32bppArgb);
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.Clear(Color.Black);
                        using var hud = new GdiHudCanvas(graphics, bitmap.Width, bitmap.Height);
                        overlay.Draw(hud, renderer: null, bitmap.Width, bitmap.Height);
                    }
                    Directory.CreateDirectory(ctx.Captures);
                    bitmap.Save(Path.Combine(ctx.Captures, fileName), ImageFormat.Png);
                    ctx.Report.Images.Add(new ImageResult(fileName, fileName, bitmap.Width, bitmap.Height, 0, 0));
                    Vector4 body = DebugColliderOverlay.Colour(DebugColliderKind.StaticBody), sensorColour = DebugColliderOverlay.Colour(DebugColliderKind.Trigger);
                    int bodyPixels = 0;
                    triggerPixels = 0;
                    // Below the strip, where only the world's drawings are.
                    for (int y = 80; y < bitmap.Height; y++)
                        for (int x = 0; x < bitmap.Width; x++)
                        {
                            Color pixel = bitmap.GetPixel(x, y);
                            if (Near(pixel, body)) bodyPixels++;
                            else if (Near(pixel, sensorColour)) triggerPixels++;
                        }
                    return bodyPixels;
                }

                int before = Pixels("debug-colliders-off.png", out int triggersBefore);
                HeadlessHarness.Assert(before == 0 && triggersBefore == 0, $"With colliders off nothing must be drawn over the world ({before} body pixels).");
                DebugScreenSettings.Colliders = true;
                int after = Pixels("debug-colliders-on.png", out int triggersAfter);
                HeadlessHarness.Assert(overlay.CollidersDrawn(DebugColliderKind.StaticBody) == 1 && overlay.CollidersDrawn(DebugColliderKind.Trigger) == 1,
                    $"The overlay must draw the static body and the trigger once each (drew {overlay.CollidersDrawnTotal}).");
                // Its twelve one-pixel, smoothed edges: about 165 pixels keep the colour closely at 1280x720.
                HeadlessHarness.Assert(after > 100, $"The static box's wireframe must be in the capture in its colour ({after} pixels).");
                HeadlessHarness.Assert(triggersAfter > 50, $"The trigger's wireframe must be in the capture in orange ({triggersAfter} pixels).");
                DebugScreenSettings.ColliderTriggers = false;
                _ = Pixels("debug-colliders-no-triggers.png", out int triggersHidden);
                HeadlessHarness.Assert(triggersHidden == 0 && overlay.CollidersDrawn(DebugColliderKind.Trigger) == 0,
                    "With triggers left out, no trigger must be drawn.");

                // With a physics world the overlay draws what the physics engine holds: a registered
                // body and a static surface no component describes (a model's or terrain's mesh).
                DebugScreenSettings.ColliderTriggers = true;
                scene.Physics = Genesis.Physics.PhysicsWorld.Create(new Genesis.Shared.Assets.PhysicsWorldAsset());
                scene.Physics.RegisterEntity(scene.World, box, ref scene.World.GetRef<RigidBodyComponent>(box), ref scene.World.GetRef<Transform3DComponent>(box));
                Vector3 floor = centre - scene.Camera3D.Right * 2.5f - new Vector3(0, 1.5f, 0);
                scene.Physics.RegisterStaticTriangleMesh(
                    [new(-1, 0, -1), new(1, 0, -1), new(-1, 0, 1), new(1, 0, 1)], [0, 1, 2, 1, 3, 2],
                    Vector3.One, floor, Quaternion.Identity, "Probe floor");
                int physicsPixels = Pixels("debug-colliders-physics.png", out _);
                HeadlessHarness.Assert(overlay.CollidersDrawn(DebugColliderKind.StaticBody) >= 2 && physicsPixels > 100,
                    $"The physics engine's static body and static surface must both be drawn ({overlay.CollidersDrawn(DebugColliderKind.StaticBody)} static, {physicsPixels} pixels).");
            }
            finally
            {
                DebugScreenSettings.Reset();
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.DebugScreen.Diagnostics.ObjectReportSaysWhy", () =>
        {
            DebugScreenSettings.Reset();
            try
            {
                using var scene = new RuntimeScene("ReportProbe");
                Entity hidden = scene.World.CreateEntity();
                scene.World.Set(hidden, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
                scene.World.Set(hidden, new Draw3DComponent { Visible = false, CastShadows = true, ReceiveShadows = true });
                Entity shown = scene.World.CreateEntity();
                scene.World.Set(shown, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
                scene.World.Set(shown, new Draw3DComponent { Visible = true, CastShadows = true, ReceiveShadows = true });
                Entity culled = scene.World.CreateEntity();
                scene.World.Set(culled, new Transform3DComponent { Position = new Vector3(0, 0, -500), Rotation = Quaternion.Identity, Scale = Vector3.One });
                scene.World.Set(culled, new MeshDrawComponent());
                scene.World.Set(culled, new VisibilityComponent { Enabled = true, LastResult = VisibilityResult.FrustumCulled });

                using var overlay = new DebugOverlay();
                overlay.BindScene(scene, "ReportProbe");
                overlay.IsVisible = true;
                string Report(Entity entity)
                {
                    DebugScreenSettings.InspectInstance = entity.Id;
                    return overlay.InspectReportText();
                }
                string hiddenReport = Report(hidden), shownReport = Report(shown), culledReport = Report(culled);
                HeadlessHarness.Assert(hiddenReport.Contains("Drawn: no: invisible", StringComparison.Ordinal),
                    "An invisible instance's report must say it is not drawn because it is invisible:\n" + hiddenReport);
                HeadlessHarness.Assert(shownReport.Contains("Drawn: yes", StringComparison.Ordinal), "A drawn instance's report must say so:\n" + shownReport);
                HeadlessHarness.Assert(culledReport.Contains("Drawn: no: outside the camera's view (frustum culled)", StringComparison.Ordinal),
                    "A culled engine mesh's report must say the frustum culled it:\n" + culledReport);
                List<int> listed = overlay.InspectableInstances();
                HeadlessHarness.Assert(listed.Contains(hidden.Id) && listed.Contains(shown.Id) && listed.Contains(culled.Id),
                    "All three instances must be in the report's list: " + string.Join(", ", listed));
                DebugScreenSettings.InspectInstance = 0;
                overlay.StepInspect(1);
                HeadlessHarness.Assert(DebugScreenSettings.InspectInstance == listed[0] && DebugScreenSettings.Inspect, "Next must start at the first instance and turn the report on.");
                overlay.StepInspect(-1);
                HeadlessHarness.Assert(DebugScreenSettings.InspectInstance == listed[^1], "Previous from the first must go round to the last.");

                // The Object tab draws the report.
                var hud = new RecordingHudCanvas();
                OpenPanel(overlay, DebugHudPanel.Object);
                DebugScreenSettings.InspectInstance = culled.Id;
                overlay.Draw(hud, renderer: null, hud.Width, hud.Height);
                HeadlessHarness.Assert(hud.Texts.Any(text => text.Contains("NOT DRAWN, outside the camera's view", StringComparison.Ordinal)),
                    "The Object tab must show why the instance is not drawn:\n" + string.Join('\n', hud.Texts));
            }
            finally
            {
                DebugScreenSettings.Reset();
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.DebugScreen.Diagnostics.GuiLayersCountDrawCalls", () =>
        {
            DebugScreenSettings.Reset();
            Genesis.Runtime.Scripting.VM.VMEngine.Initialize();
            ScriptAssetRegistry.ClearCache();
            var host = new ScriptHostSystem();
            var world = new Genesis.Runtime.ECS.World();
            var events = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DrawGui"] = "DrawRectangle(10, 10, 110, 40);\nDrawRectangle(20, 50, 220, 90);\nDrawText(30, 60, \"hp\");\n",
            };
            using (host.UseEventSources(events)) host.Attach(world, world.CreateEntity(), "HudProbe");
            host.Update(1f / 60f);
            try
            {
                using var overlay = new DebugOverlay();
                OpenPanel(overlay, DebugHudPanel.Gui);
                var hud = new RecordingHudCanvas();
                // The page asks for the trace as it draws; the next frame's GUI is counted.
                overlay.Draw(hud, renderer: null, hud.Width, hud.Height);
                HeadlessHarness.Assert(GuiLayerTrace.Enabled, "The GUI page must turn the trace on.");
                var gui = new RecordingHudCanvas();
                host.DispatchPgslGuiDraw(renderer: null, gui);
                int expected = gui.Texts.Count + gui.RectCount + gui.LineCount;
                overlay.Draw(hud, renderer: null, hud.Width, hud.Height);
                IReadOnlyList<GuiLayerTrace.Layer> layers = GuiLayerTrace.Last;
                HeadlessHarness.Assert(layers.Count == 1 && layers[0].Owner == "HudProbe" && layers[0].Pass == "Draw GUI",
                    "The GUI page must list the instance's Draw GUI layer: " + DebugOverlay.GuiLayersText());
                HeadlessHarness.Assert(expected >= 1 && layers[0].DrawCalls == expected && !layers[0].Bounds.IsEmpty,
                    $"The layer must count every draw call it made and its area ({layers[0].DrawCalls} counted, {expected} drawn, {layers[0].Bounds}).");
                HeadlessHarness.Assert(hud.Texts.Any(text => text.Contains("HudProbe", StringComparison.Ordinal)), "The GUI tab must name the layer's owner.");
                overlay.IsVisible = false;
                HeadlessHarness.Assert(!GuiLayerTrace.Enabled, "Closing the screen must stop the trace.");
            }
            finally
            {
                DebugScreenSettings.Reset();
            }
        });
    }

    private static bool Near(Color pixel, Vector4 colour) =>
        Math.Abs(pixel.R - colour.X * 255f) < 40 && Math.Abs(pixel.G - colour.Y * 255f) < 40 && Math.Abs(pixel.B - colour.Z * 255f) < 40;
}
