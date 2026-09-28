using System.Drawing;
using System.Numerics;
using System.Reflection;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Core.Settings;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Navigation;
using Genesis.Runtime.Debugger;
using Genesis.Runtime.Rendering;
using Genesis.Rendering.Core;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

internal static partial class EditorGate
{
    private static void Pathing(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        ProjectSession project = fixture.TwoD;
        string path = fixture.Resources(project).CreateResource(
            ResourceFolderPolicy.RootFor(project, ResourceKind.Pathing), ResourceKind.Pathing, "Patrol");
        using Form host = GateSuite.NewHost(1280, 780);
        using PathingEditorControl editor = new(path, project.RootPath);
        host.Controls.Add(editor);
        GateSuite.ShowHost(host);
        HeadlessHarness.Step("opening and resizing retain access to route tools and code", () =>
        {
            foreach (Size size in new[] { new Size(700, 480), new Size(380, 260), new Size(1280, 780) })
            {
                host.ClientSize = size;
                GateSuite.Pump(2, 15);
                foreach (SplitContainer split in SurfaceControls(editor).OfType<SplitContainer>())
                    HeadlessHarness.Assert(!split.Panel1Collapsed || !split.Panel2Collapsed,
                        "Both Pathing surfaces were hidden at " + size);
            }
            ToolStripButton code = SurfaceControls(editor).OfType<ToolStrip>()
                .SelectMany(strip => strip.Items.OfType<ToolStripButton>()).Single(button => button.Text == "Code");
            host.ClientSize = new Size(700, 480);
            GateSuite.Pump(2, 15);
            code.PerformClick();
            HeadlessHarness.Assert(editor.Code.Visible && editor.Code.Width > 200,
                "The narrow layout cannot expose usable route code.");
            host.ClientSize = new Size(1280, 780);
        });
        HeadlessHarness.Step("visual parameters undo and redo without losing the route", () =>
        {
            float original = Convert.ToSingle(editor.GetLiveInspectorValues().Single(value => value.PropertyPath == "Pathing.Speed").Value);
            HeadlessHarness.Assert(editor.TryApplyInspectorValue("Pathing.Speed", 6f) && editor.CanUndo,
                "Visual parameter editing was not journaled.");
            editor.Undo();
            HeadlessHarness.Assert(Convert.ToSingle(editor.GetLiveInspectorValues().Single(value => value.PropertyPath == "Pathing.Speed").Value) == original,
                "Undo did not restore the route speed.");
            editor.Redo();
            HeadlessHarness.Assert(editor.Code.CodeText.Contains("speed: 6", StringComparison.Ordinal), "Redo did not synchronize route code.");
        });
        HeadlessHarness.Step("author a declaration, save and reopen using the runtime serializer", () =>
        {
            editor.Code.CodeText = """
                pathing "Gate Patrol" {
                    dimension: ThreeD
                    mode: WaypointPatrol
                    loop: PingPong
                    speed: 6
                    stopping_distance: 0.1
                    waypoint "Start" (0.5, 0, 0.5) wait 0 curve false
                    waypoint "End" (8.5, 0, 0.5) wait 0.5 curve false
                    speed_key 0 1
                    speed_key 2 0.5
                }
                """;
            editor.Save();
            PathingAsset saved = PathingAssetSerializer.Load(path);
            HeadlessHarness.Assert(saved.Name == "Gate Patrol" && saved.Route.Waypoints.Count == 2
                && saved.Route.LoopMode == PathingLoopMode.PingPong && saved.Route.SpeedAt(2) == 3,
                "Runtime route data did not retain authored waypoints, loop and speed curve.");
            using PathingEditorControl reopened = new(path, project.RootPath);
            HeadlessHarness.Assert(reopened.Code.CodeText.Contains("\"End\" (8.5, 0, 0.5)", StringComparison.Ordinal),
                "The reopened editor lost route geometry.");
            byte[] before = File.ReadAllBytes(path);
            editor.Code.CodeText += "\nunknown: garbage";
            bool rejected = false;
            try { editor.Save(); } catch (InvalidDataException) { rejected = true; }
            HeadlessHarness.Assert(rejected && File.ReadAllBytes(path).SequenceEqual(before),
                "Invalid route code silently overwrote the last valid resource.");
        });
        HeadlessHarness.Step("gameplay code loads the authored route into its navigation agent", () =>
        {
            using RuntimeScene scene = new("Pathing gate");
            ProjectGameContext game = new(project.RootPath, scene, null, null, RoomAsset.Create("Patrol", RoomDimension.ThreeD), null);
            PgslContext context = new();
            PgslContext? previousContext = PgslCommands.BindContext(context);
            IGameContext? previousGame = PgslCommands.ActiveGameContext;
            string previousPath = PgslCommands.ProjectPath;
            PgslCommands.ActiveGameContext = game;
            PgslCommands.ProjectPath = project.RootPath;
            try
            {
                var entity = scene.World.CreateEntity();
                context.InstanceId = entity.Id;
                context.X = context.Z = .5;
                scene.World.Set(entity, new TransformComponent { X = .5f, Z = .5f });
                PgslCommands.NavMeshBakeGrid(0, 0, 12, 12, 1);
                PgslCommands.PathFollow(path, "");
                HeadlessHarness.Assert(scene.World.Has<NavMeshAgentComponent>(entity)
                    && scene.World.GetRef<NavMeshAgentComponent>(entity).Agent.Speed == 6
                    && Math.Abs(scene.World.GetRef<NavMeshAgentComponent>(entity).Agent.StoppingDistance - .1f) < .001f,
                    "PathFollow did not consume the saved route's movement settings.");
            }
            finally
            {
                PgslCommands.BindContext(previousContext);
                PgslCommands.ActiveGameContext = previousGame;
                PgslCommands.ProjectPath = previousPath;
            }
        });
        HeadlessHarness.Step("saved XY routes render the bound Image and move real gameplay transforms along curves", () =>
        {
            editor.Code.CodeText = """
                pathing "Sprite patrol" {
                    dimension: TwoD
                    object: "Assets/Objects/Coin.object.json"
                    preview_agents: 1
                    mode: WaypointPatrol
                    loop: Once
                    speed: 60
                    stopping_distance: 0.05
                    waypoint "Start" (8, 12, 7) wait 0 curve false
                    waypoint "Bend" (24, 96, 7) wait 0 curve true
                    waypoint "End" (104, 96, 7) wait 0 curve false
                    speed_key 0 1
                    speed_key 2 0.5
                }
                """;
            editor.Save();
            using (PathingEditorControl reopened = new(path, project.RootPath))
                HeadlessHarness.Assert(reopened.Dimension == PathingDimension.TwoD,
                    "Saved pathing plane did not survive reopening.");
            editor.CommandBar.Items.OfType<ToolStripButton>().Single(button => button.Text == "Quick setup").PerformClick();
            SurfaceControls(editor).OfType<Button>().Single(button => button.Text == "Add point").PerformClick();
            editor.Save();
            PathingAsset extended = PathingAssetSerializer.Load(path);
            HeadlessHarness.Assert(extended.Route.Waypoints.Count == 4 && extended.Route.Waypoints[3].X == 136
                && extended.Route.Waypoints[3].Y == 128 && extended.Route.Waypoints[3].Z == 7,
                "Adding an XY point did not retain depth or usable sprite-sized spacing: " + string.Join("; ", extended.Route.Waypoints.Select(point => $"{point.Name} ({point.X},{point.Y},{point.Z})")));
            editor.Undo(); editor.Save();
            editor.SetDimension(PathingDimension.ThreeD);
            editor.Undo();
            HeadlessHarness.Assert(editor.Dimension == PathingDimension.TwoD, "Undo lost the XY plane.");
            ToolStripButton code = SurfaceControls(editor).OfType<ToolStrip>().SelectMany(strip => strip.Items.OfType<ToolStripButton>())
                .Single(button => button.Text == "Code");
            if (editor.Code.Visible) code.PerformClick();
            GateSuite.Pump(3, 20);
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            object preview = typeof(PathingEditorControl).GetField("_viewport", flags)!.GetValue(editor)!;
            EditorViewport3D viewport = (EditorViewport3D)preview.GetType().GetField("_viewport", flags)!.GetValue(preview)!;
            preview.GetType().GetMethod("FrameContent")!.Invoke(preview, null);
            using Bitmap? frame = viewport.CaptureFrame(3);
            int gold = 0;
            if (frame is not null)
                for (int y = 0; y < frame.Height; y += 2)
                for (int x = 0; x < frame.Width; x += 2)
                {
                    Color color = frame.GetPixel(x, y);
                    if (color.R > 180 && color.G > 130 && color.B < 100) gold++;
                }
            HeadlessHarness.Assert(frame is not null && gold > 50,
                "The actual 2D readback did not contain the bound Coin Image pixels.");
            Vector2 pin = viewport.World2DToSurface(new Vector2(24, 96));
            Point start = Point.Round(viewport.SurfaceToControl(new PointF(pin.X, pin.Y)));
            preview.GetType().GetMethod("PointerDown", flags)!.Invoke(preview, [null, new MouseEventArgs(MouseButtons.Left, 1, start.X, start.Y, 0)]);
            for (int step = 1; step <= 120; step++)
                preview.GetType().GetMethod("PointerMove", flags)!.Invoke(preview, [null, new MouseEventArgs(MouseButtons.Left, 0, start.X + step / 3, start.Y - step / 6, 0)]);
            preview.GetType().GetMethod("PointerUp", flags)!.Invoke(preview, [null, new MouseEventArgs(MouseButtons.Left, 0, start.X + 40, start.Y - 20, 0)]);
            editor.Save();
            PathingAsset dragged = PathingAssetSerializer.Load(path);
            HeadlessHarness.Assert(dragged.Route.Waypoints[1].X > 24 && dragged.Route.Waypoints[1].Y < 96 && dragged.Route.Waypoints[1].Z == 7,
                "Dragging the rendered XY pin did not edit X/Y while retaining depth.");
            editor.Undo(); editor.Save();
            PathingAsset restored = PathingAssetSerializer.Load(path);
            HeadlessHarness.Assert(restored.Route.Waypoints[1].X == 24 && restored.Route.Waypoints[1].Y == 96 && restored.Route.Waypoints[1].Curve,
                "Saving a dragged waypoint added an extra edit or changed the authored curve: " + editor.Code.CodeText);
            typeof(PathingEditorControl).GetMethod("ScrubTo", flags)!.Invoke(editor, [1f]);
            object simulation = typeof(PathingEditorControl).GetField("_simulation", flags)!.GetValue(editor)!;
            System.Collections.IList agents = (System.Collections.IList)simulation.GetType().GetProperty("Agents")!.GetValue(simulation)!;
            Vector3 previewPosition = (Vector3)agents[0]!.GetType().GetField("Position")!.GetValue(agents[0])!;
            HeadlessHarness.Assert(previewPosition.Z > 30 && previewPosition.Y == 0,
                "The preview did not simulate movement in the saved XY plane.");
            ctx.Report.Images.Add(ImageResult.From("Editor.Pathing", "pathing-xy-preview.png",
                VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, "pathing-xy-preview.png"), includeViewports: true)));

            using RuntimeScene scene = new("XY pathing gameplay");
            ProjectGameContext game = new(project.RootPath, scene, null, null, RoomAsset.Create("Patrol XY", RoomDimension.TwoD), null);
            PgslContext context = new();
            PgslContext? previousContext = PgslCommands.BindContext(context);
            IGameContext? previousGame = PgslCommands.ActiveGameContext;
            string previousPath = PgslCommands.ProjectPath;
            PgslCommands.ActiveGameContext = game; PgslCommands.ProjectPath = project.RootPath;
            try
            {
                var entity = scene.World.CreateEntity();
                context.InstanceId = entity.Id; context.X = 8; context.Y = 12; context.Z = 7;
                scene.World.Set(entity, new TransformComponent { X = 8, Y = 12, Z = 7, ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
                MethodInfo tick = typeof(PgslCommands).GetMethod("UpdateNavigation", BindingFlags.NonPublic | BindingFlags.Static)!;
                PgslCommands.PathFollow(path, "");
                float curveDeviation = 0;
                for (int frameIndex = 0; frameIndex < 60; frameIndex++)
                {
                    tick.Invoke(null, [scene, 1f / 60]);
                    TransformComponent transform = scene.World.GetRef<TransformComponent>(entity);
                    curveDeviation = Math.Max(curveDeviation, Math.Abs((transform.X - 8) * 84 - (transform.Y - 12) * 16) / MathF.Sqrt(84 * 84 + 16 * 16));
                }
                TransformComponent moved = scene.World.GetRef<TransformComponent>(entity);
                NavMeshAgent agent = scene.World.GetRef<NavMeshAgentComponent>(entity).Agent;
                HeadlessHarness.Assert(agent.PlanarXY && moved.Y > 40 && moved.Z == 7 && curveDeviation > 2
                    && Math.Abs(agent.Speed - 45) < .01f,
                    $"Gameplay did not follow the XY curve, preserve depth and apply the speed curve during the segment: planar={agent.PlanarXY}, position=({moved.X},{moved.Y},{moved.Z}), deviation={curveDeviation}, speed={agent.Speed}.");
                HeadlessHarness.Assert(Vector2.Distance(new Vector2(moved.X, moved.Y), new Vector2(previewPosition.X, previewPosition.Z)) < 1.5f,
                    "The preview and gameplay route moved differently over the same second.");
                PgslCommands.PathStop();
                tick.Invoke(null, [scene, .5f]);
                TransformComponent stopped = scene.World.GetRef<TransformComponent>(entity);
                HeadlessHarness.Assert(stopped.X == moved.X && stopped.Y == moved.Y, "PathStop continued moving the actor.");

                var obstacle = scene.World.CreateEntity();
                scene.World.Set(obstacle, RigidBodyComponent.StaticBox(new Vector3(3, 20, 1)));
                scene.World.Set(obstacle, new Transform3DComponent { Position = new Vector3(52, 48, 7), Rotation = Quaternion.Identity });
                PgslCommands.NavMeshBakeGrid(0, 0, 128, 128, 1);
                double navigationPath = PgslCommands.NavMeshPathFind(8, 12, 7, 104, 96, 7);
                int count = (int)PgslCommands.NavMeshPathGetWaypointCount(navigationPath);
                HeadlessHarness.Assert(navigationPath > 0 && count >= 3 && PgslCommands.NavMeshPathGetWaypointY(navigationPath, count - 1) > 90
                    && PgslCommands.NavMeshPathGetWaypointZ(navigationPath, count - 1) == 7,
                    "2D navigation did not route around the actual XY static collider or return world-space XY waypoints.");
                scene.World.GetRef<TransformComponent>(entity).X = 8; scene.World.GetRef<TransformComponent>(entity).Y = 12;
                PgslCommands.PathSeek(104, 96, 999);
                for (int frameIndex = 0; frameIndex < 300; frameIndex++) tick.Invoke(null, [scene, 1f / 60]);
                TransformComponent arrived = scene.World.GetRef<TransformComponent>(entity);
                HeadlessHarness.Assert(arrived.X > 103 && arrived.Y > 95 && arrived.Z == 7,
                    "The gameplay actor did not reach its baked XY destination at its retained depth.");
                var previewEntity = scene.World.CreateEntity();
                scene.World.Set(previewEntity, new TransformComponent { X = 8, Y = 12, Z = 7, ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
                ObjectDrawAssetRegistry.Set(previewEntity, new ObjectDrawAssetEntry { Prefab = restored.TargetObject });
                string? previousDebugPath = Environment.GetEnvironmentVariable(NavigationDebugTelemetry.DebugPathingEnvironmentVariable);
                bool debugEnabled = NavigationDebugTelemetry.Enabled;
                try
                {
                    Environment.SetEnvironmentVariable(NavigationDebugTelemetry.DebugPathingEnvironmentVariable, path);
                    NavigationDebugTelemetry.Enabled = true;
                    MethodInfo debugTick = typeof(NavigationDebugTelemetry).GetMethod("AdvanceDebugPreview", BindingFlags.NonPublic | BindingFlags.Static)!;
                    for (int frameIndex = 0; frameIndex < 60; frameIndex++)
                    { tick.Invoke(null, [scene, 1f / 60]); debugTick.Invoke(null, [scene, 1f / 60]); }
                    TransformComponent debugMoved = scene.World.GetRef<TransformComponent>(previewEntity);
                    HeadlessHarness.Assert(debugMoved.Y > 40 && debugMoved.Z == 7 && scene.World.GetRef<NavMeshAgentComponent>(previewEntity).Agent.PlanarXY,
                        "F3 route preview did not move its real XY actor at the retained depth.");
                    tick.Invoke(null, [scene, 1f / 60]);
                    TransformComponent afterOrdinaryTick = scene.World.GetRef<TransformComponent>(previewEntity);
                    HeadlessHarness.Assert(afterOrdinaryTick.X == debugMoved.X && afterOrdinaryTick.Y == debugMoved.Y,
                        "The ordinary navigation tick moved the debug preview a second time.");
                }
                finally
                {
                    NavigationDebugTelemetry.Enabled = debugEnabled;
                    Environment.SetEnvironmentVariable(NavigationDebugTelemetry.DebugPathingEnvironmentVariable, previousDebugPath);
                    ObjectDrawAssetRegistry.Remove(previewEntity);
                }
            }
            finally
            {
                PgslCommands.BindContext(previousContext); PgslCommands.ActiveGameContext = previousGame; PgslCommands.ProjectPath = previousPath;
            }
        });
        HeadlessHarness.Step("the 2D route preview recreates real sprites on each requested backend", () =>
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            object preview = typeof(PathingEditorControl).GetField("_viewport", flags)!.GetValue(editor)!;
            EditorViewport3D viewport = (EditorViewport3D)preview.GetType().GetField("_viewport", flags)!.GetValue(preview)!;
            try
            {
                foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
                {
                    viewport.Host.BackendOverride = backend.Backend;
                    GateSuite.Pump(3, 15);
                    using Bitmap? frame = viewport.CaptureFrame(3);
                    string actualName = backend.Backend switch
                    { RenderBackendOption.OpenGL => "OpenGL", RenderBackendOption.Software => "Software", _ => backend.DisplayName };
                    HeadlessHarness.Assert(frame is not null && viewport.Host.Renderer?.BackendName == actualName,
                        "Pathing did not use the exact requested renderer: " + backend.DisplayName
                        + "; actual=" + viewport.Host.Renderer?.BackendName + "; frame=" + (frame is not null));
                    int gold = 0;
                    for (int y = 0; y < frame!.Height; y += 2)
                    for (int x = 0; x < frame.Width; x += 2)
                    {
                        Color color = frame.GetPixel(x, y);
                        if (color.R > 180 && color.G > 130 && color.B < 100) gold++;
                    }
                    HeadlessHarness.Assert(gold > 50, "The recreated " + backend.DisplayName + " route preview lost its actual Image pixels.");
                    HeadlessHarness.Assert(viewport.Host.RenderFaultCount == 0,
                        "Recreating " + backend.DisplayName + " recorded a renderer fault: " + viewport.Host.LastRenderException);
                    string capture = "pathing-xy-" + backend.ShortName.ToLowerInvariant() + ".png";
                    string imageFile = Path.Combine(ctx.Captures, capture);
                    frame.Save(imageFile, System.Drawing.Imaging.ImageFormat.Png);
                    ctx.Report.Images.Add(ImageResult.From("Editor.Pathing." + backend.ShortName, capture, VisualCapture.Measure(frame)));
                }
            }
            finally { viewport.Host.BackendOverride = null; }
        });
        HeadlessHarness.Step("enlarged pathing controls keep readable fields and restore toggled panels", () =>
        {
            try
            {
                ThemeService.ApplySettings(new GenesisSettings { Appearance = { InterfaceScale = 2f } });
                Genesis.Application.Studio.Docking.SuiteChromeBridge.Push();
                ThemeService.Apply(host); GateSuite.Pump(4, 20);
                ToolStrip toolbar = editor.CommandBar;
                Control fields = (Control)typeof(PathingEditorControl).GetField("_toolsFields", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;
                toolbar.Items.OfType<ToolStripButton>().Single(button => button.Text == "Quick setup").PerformClick();
                GateSuite.Pump(4, 20);
                HeadlessHarness.Assert(fields.Visible && fields.Controls.Cast<Control>().Where(control => control.Tag as string == "PathingField")
                    .All(field => field.Height > 50 && field.Controls.OfType<NumericUpDown>().All(input => input.Width > 140)),
                    "Enlarged route parameters were clipped or inaccessible: " + fields.Visible + ", " + string.Join(", ", fields.Controls.Cast<Control>()
                        .Where(control => control.Tag as string == "PathingField").Select(field => $"{field.Width}x{field.Height}")));
                host.ClientSize = new Size(1380, 800); GateSuite.Pump(3, 20);
                HeadlessHarness.Assert(fields.Visible, "Resizing hid the primary route controls.");
            }
            finally { ThemeService.ApplySettings(new GenesisSettings()); Genesis.Application.Studio.Docking.SuiteChromeBridge.Push(); ThemeService.Apply(host); }
        });
        CheckPathingNoviceWorkflow(ctx, fixture, editor, host);
    }
}
