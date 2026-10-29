using System.ComponentModel;
using System.Drawing;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Genesis.Application.Core.Automation;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Runtime;
using Genesis.Application.Studio.Theme;
using Genesis.Rendering.Core;
using Genesis.Rendering.Meshes;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Rendering;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Light Probes and Reflection Probes placed in a room (Engine.Lighting.Probes). A red wall stands
/// behind and to the left of the stage, a blue one to the right, both towards the camera, with a gap
/// the camera looks through. A Reflection Probe box beside each wall sees its own wall where the
/// camera's probe sees only the sky behind it, so a mirror sphere moved from one box through their
/// overlap into the other turns from red through both to blue; a Light Probe by the red wall reddens
/// a white box's shaded face. With the probes off, or out of reach, the picture is as it was.
/// </summary>
internal static class PlacedProbeSuite
{
    private static readonly RenderBackendOption[] GpuBackends =
    {
        RenderBackendOption.SilkNetDx11, RenderBackendOption.Direct3D12,
        RenderBackendOption.Vulkan, RenderBackendOption.OpenGL,
    };

    // The sunlight travels away from the stage, towards the walls, so the faces the probes see are lit.
    private static readonly Vector3 SunTowardsWalls = Vector3.Normalize(new Vector3(0.2f, -0.6f, -1f));
    private static readonly Vector3 LeftProbe = new(-3f, 2f, 0f), RightProbe = new(3f, 2f, 0f);
    private const float SphereRadius = 0.6f, SphereHeight = 1f;

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Engine.Api.Lighting.Probes.ValuesByName", ApiByName);
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.LightProbe.PlacedProbesBlendByPositionOnAllBackends", () => BlendByPosition(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.LightProbe.PlacedProbesOffOrOutOfReachChangeNothing", () => OffOrOutOfReach(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.LightProbe.PlacedProbeCaptureCost", () => CaptureCost(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Probes.PlaceInspectUndoSaveReopenAndHandToTheGame", () => RoomEditorProbes(ctx));
    }

    private static PlacedProbe ReflectionBox(string name, Vector3 at) => new()
    {
        Name = name, Kind = PlacedProbeKind.Reflection, Shape = PlacedProbeShape.Box,
        Position = at, Size = new Vector3(8f, 6f, 6f), BlendDistance = 2f,
    };

    // Each box reaches 1 m past the stage's middle (x -7..1 and -1..7), so they overlap from x -1
    // to 1 and each fades out over its last 2 m: halves at x 0, all one or the other at |x| >= 1.
    private static PlacedProbe[] TwoReflectionBoxes() => new[] { ReflectionBox("Left", LeftProbe), ReflectionBox("Right", RightProbe) };

    private static void Walls(IRenderController r, MeshHandle cube)
    {
        void Box(Vector3 centre, Vector3 size, RenderColor tint) => r.DrawMesh(new MeshDrawCall
        {
            Mesh = cube, Tint = tint, Alpha = 1f,
            World = Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation(centre),
        });
        Box(new Vector3(-4.1f, 2.5f, -3f), new Vector3(5.8f, 5f, 0.3f), new RenderColor(0.95f, 0.08f, 0.06f));
        Box(new Vector3(4.1f, 2.5f, -3f), new Vector3(5.8f, 5f, 0.3f), new RenderColor(0.06f, 0.12f, 0.95f));
    }

    private static void ApiByName()
    {
        try
        {
            PlacedProbes.SetRoom(new[]
            {
                ReflectionBox("Hall", new Vector3(1f, 2f, 3f)),
                new PlacedProbe { Name = "Hearth", Kind = PlacedProbeKind.Light, Position = new Vector3(-2f, 1f, 0f), Radius = 4f, BlendDistance = 1f },
            });
            bool defaults = PgslCommands.LightingApiProbesEnabled && PgslCommands.LightingApiProbesCount == 2
                && PgslCommands.LightingApiProbesCaptured == 0 && PgslCommands.LightingApiProbesName(1) == "Hearth"
                && PgslCommands.LightingApiProbesName(2) == "" && PgslCommands.LightingApiProbesExists("hall")
                && !PgslCommands.LightingApiProbesExists("Cellar") && PgslCommands.LightingApiProbesKind("Hall") == "reflection"
                && PgslCommands.LightingApiProbesKind("Hearth") == "light" && PgslCommands.LightingApiProbesRadius("Hearth") == 4
                && PgslCommands.LightingApiProbesRadius("Hall") == 4 && PgslCommands.LightingApiProbesBlend("Hall") == 2;
            HeadlessHarness.Assert(defaults, "The placed probes' values by name are not what the room gave them.");

            PlacedProbe hearth = PlacedProbes.Find("Hearth");
            int revision = hearth.Revision;
            bool moved = PgslCommands.LightingApiProbesSetPosition("Hearth", 5, 6, 7) && hearth.Revision == revision + 1
                && PgslCommands.LightingApiProbesX("Hearth") == 5 && PgslCommands.LightingApiProbesY("Hearth") == 6 && PgslCommands.LightingApiProbesZ("Hearth") == 7
                && !PgslCommands.LightingApiProbesSetPosition("Cellar", 0, 0, 0);
            bool sized = PgslCommands.LightingApiProbesSetRadius("Hearth", 5000) && hearth.Radius == 1000
                && !PgslCommands.LightingApiProbesSetRadius("Hall", 3) && PgslCommands.LightingApiProbesSetBlend("Hall", -1) && PlacedProbes.Find("Hall").BlendDistance == 0;
            bool switched = PgslCommands.LightingApiProbesSetActive("Hall", false) && !PgslCommands.LightingApiProbesActive("Hall")
                && PgslCommands.LightingApiProbesActive("Hearth") && !PgslCommands.LightingApiProbesSetActive("Cellar", true);
            HeadlessHarness.Assert(moved && sized && switched,
                $"Setting placed probes by name: moved {moved} (recaptured: revision {revision} -> {hearth.Revision}), sized {sized}, switched {switched}.");

            // Shares by position, as the forward shader weighs them.
            var sphere = new PlacedProbe { Shape = PlacedProbeShape.Sphere, Position = Vector3.Zero, Radius = 4f, BlendDistance = 2f };
            var box = new PlacedProbe { Shape = PlacedProbeShape.Box, Position = Vector3.Zero, Size = new Vector3(4f), BlendDistance = 1f };
            bool shares = PlacedProbes.Weight(sphere, Vector3.Zero) == 1f && Math.Abs(PlacedProbes.Weight(sphere, new Vector3(3f, 0f, 0f)) - 0.5f) < 1e-5f
                && PlacedProbes.Weight(sphere, new Vector3(0f, 4.5f, 0f)) == 0f && Math.Abs(PlacedProbes.Weight(box, new Vector3(1.5f, 0f, 0f)) - 0.5f) < 1e-5f
                && PlacedProbes.Weight(box, new Vector3(0.5f, -0.9f, 0.2f)) == 1f && PlacedProbes.Weight(box, new Vector3(0f, 0f, 2.1f)) == 0f;
            HeadlessHarness.Assert(shares, "A placed probe's share by position is wrong (whole inside, half halfway through its blend, none outside).");

            hearth.Captured = true;
            hearth.Position = Vector3.Zero;
            hearth.Radius = 4f;
            hearth.BlendDistance = 1f;
            bool blending = PlacedProbes.BlendingAt(new Vector3(1f, 0f, 0f)) == 1 && PlacedProbes.BlendingAt(new Vector3(9f, 0f, 0f)) == 0;
            PgslCommands.LightingApiProbesEnabled = false;
            blending &= PlacedProbes.BlendingAt(new Vector3(1f, 0f, 0f)) == 0;
            HeadlessHarness.Assert(blending, "Engine.Lighting.Probes.Blending does not count the captured probes reaching a point, or counts them while turned off.");
            PlacedProbes.SetRoom(Array.Empty<PlacedProbe>());
            HeadlessHarness.Assert(PgslCommands.LightingApiProbesEnabled && PgslCommands.LightingApiProbesCount == 0, "A room's start must turn placed probes back on, with none placed.");
        }
        finally
        {
            PlacedProbes.Clear();
        }
    }

    private static void BlendByPosition(HeadlessContext ctx)
    {
        RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
        var report = new List<string>();
        LightProbes.InGame = true;
        try
        {
            foreach (RenderBackendOption backend in GpuBackends)
            {
                RenderBackendSelection.Configure(backend);
                string name = backend.ToString().ToLowerInvariant();
                string File(string what) => Path.Combine(ctx.Captures, $"placed-probes-{name}-{what}.png");
                using RuntimeViewportHarness harness = new();
                MeshHandle cube = harness.LitSceneCube;
                MeshHandle sphere = default;
                float sphereX = 0f;
                bool showSphere = true, showMatteBox = false;
                Action<IRenderController> scene = r =>
                {
                    Walls(r, cube);
                    if (showSphere)
                    {
                        if (!sphere.IsValid) sphere = MeshGeometry.RegisterSphere(r, RenderColor.White, SphereRadius);
                        r.DrawMesh(new MeshDrawCall
                        {
                            Mesh = sphere, Tint = new RenderColor(0.85f, 0.85f, 0.85f), Alpha = 1f, MaterialFactors = new Vector4(1f, 0.04f, 0f, 1f),
                            World = Matrix4x4.CreateTranslation(sphereX, SphereHeight, 0f),
                        });
                    }
                    if (showMatteBox)
                        r.DrawMesh(new MeshDrawCall
                        {
                            Mesh = cube, Tint = new RenderColor(0.9f, 0.9f, 0.9f), Alpha = 1f,
                            World = Matrix4x4.CreateScale(1f) * Matrix4x4.CreateTranslation(-1.5f, 0.6f, 0f),
                        });
                };

                // A room's start: the camera's probe and the two boxes, captured one a frame.
                LightProbes.ResetForRoom();
                PlacedProbes.SetRoom(TwoReflectionBoxes());
                harness.MeasureLitScene(6, SunTowardsWalls, shadows: false, scene);
                int captured = PlacedProbes.Captured, frames = PlacedProbes.FramesToCaptureAll;

                var samples = new List<(float X, double Red, double Blue)>();
                foreach (float x in new[] { -1.5f, -0.5f, 0f, 0.5f, 1.5f })
                {
                    sphereX = x;
                    harness.CaptureLitScene(File($"sphere-{x:+0.0;-0.0;0}"), SunTowardsWalls, shadows: false, scene);
                    using Bitmap image = new(File($"sphere-{x:+0.0;-0.0;0}"));
                    Point at = harness.ProjectLitScenePoint(new Vector3(x, SphereHeight, -SphereRadius));
                    samples.Add((x, Hue(image, at, red: true), Hue(image, at, red: false)));
                }
                PlacedProbes.Enabled = false;
                sphereX = 0f;
                harness.CaptureLitScene(File("sphere-0-off"), SunTowardsWalls, shadows: false, scene);
                PlacedProbes.Enabled = true;
                using Bitmap offImage = new(File("sphere-0-off"));
                Point middle = harness.ProjectLitScenePoint(new Vector3(0f, SphereHeight, -SphereRadius));
                double offRed = Hue(offImage, middle, red: true), offBlue = Hue(offImage, middle, red: false);

                // Bounced light: a Light Probe by the red wall reddens the white box's shaded face.
                showSphere = false;
                showMatteBox = true;
                PlacedProbes.SetRoom(new[]
                {
                    new PlacedProbe { Name = "By the red wall", Kind = PlacedProbeKind.Light, Position = LeftProbe, Radius = 4f, BlendDistance = 1f },
                });
                harness.MeasureLitScene(4, SunTowardsWalls, shadows: false, scene);
                harness.CaptureLitScene(File("bounce-on"), SunTowardsWalls, shadows: false, scene);
                PlacedProbes.Enabled = false;
                harness.CaptureLitScene(File("bounce-off"), SunTowardsWalls, shadows: false, scene);
                PlacedProbes.Enabled = true;
                Point face = harness.ProjectLitScenePoint(new Vector3(-1.5f, 0.6f, -0.5f));
                using Bitmap bounceOn = new(File("bounce-on")), bounceOff = new(File("bounce-off"));
                double bounceRedOn = Hue(bounceOn, face, red: true), bounceRedOff = Hue(bounceOff, face, red: true);

                report.Add($"{backend}: {captured} captured in {frames} frames; sphere red/blue " +
                    string.Join(", ", samples.Select(s => $"x {s.X:+0.0;-0.0;0}: {s.Red:F0}/{s.Blue:F0}")) +
                    $"; at x 0 off {offRed:F0}/{offBlue:F0}; white box redness {bounceRedOff:F1} -> {bounceRedOn:F1} with the light probe");
                HeadlessHarness.Assert(captured == 2 && frames is > 0 and <= 4,
                    $"{backend}: the two placed probes were not captured one a frame from the room's start ({captured} captured, all by frame {frames}).");
                if (backend != RenderBackendOption.SilkNetDx11) continue;

                var (left, nearLeft, centre, nearRight, right) = (samples[0], samples[1], samples[2], samples[3], samples[4]);
                HeadlessHarness.Assert(left.Red > left.Blue + 40 && right.Blue > right.Red + 40,
                    $"{backend}: inside one box only the sphere should mirror that box's wall (left red {left.Red:F0} blue {left.Blue:F0}, right red {right.Red:F0} blue {right.Blue:F0}).");
                HeadlessHarness.Assert(centre.Red > offRed + 25 && centre.Red < left.Red - 15 && centre.Blue > left.Blue + 15,
                    $"{backend}: where the boxes overlap the sphere should mirror both walls, which the camera's probe alone does not see (red {centre.Red:F0}, " +
                    $"{offRed:F0} with the probes off, {left.Red:F0} in the left box alone; blue {centre.Blue:F0}, {offBlue:F0} off, {left.Blue:F0} in the left box).");
                HeadlessHarness.Assert(left.Red > nearLeft.Red && nearLeft.Red > centre.Red && centre.Red > nearRight.Red && nearRight.Red > right.Red
                    && left.Blue < nearLeft.Blue && nearLeft.Blue < centre.Blue && centre.Blue < nearRight.Blue && nearRight.Blue < right.Blue,
                    $"{backend}: moving across the blend the reflection should go steadily from red to blue: " +
                    string.Join(", ", samples.Select(s => $"{s.X:+0.0;-0.0;0} {s.Red:F0}/{s.Blue:F0}")));
                HeadlessHarness.Assert(bounceRedOn > bounceRedOff + 6,
                    $"{backend}: the Light Probe by the red wall does not redden the white box's shaded face (redness {bounceRedOff:F1} off, {bounceRedOn:F1} on).");
            }
        }
        finally
        {
            RenderBackendSelection.Configure(previous);
            PlacedProbes.Clear();
            LightProbes.ResetForRoom();
            LightProbes.InGame = false;
            File.WriteAllLines(Path.Combine(ctx.Captures, "placed-probes-backends.txt"), report);
        }
    }

    // A room with no placed probes draws as before: so do probes turned off, and probes placed where
    // nothing in view is inside them (the forward shader's placed path then changes no pixel).
    private static void OffOrOutOfReach(HeadlessContext ctx)
    {
        RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
        LightProbes.InGame = true;
        try
        {
            RenderBackendSelection.Configure(RenderBackendOption.SilkNetDx11);
            string File(string what) => Path.Combine(ctx.Captures, $"placed-probes-unchanged-{what}.png");
            using RuntimeViewportHarness harness = new();
            MeshHandle cube = harness.LitSceneCube;
            MeshHandle sphere = default;
            Action<IRenderController> scene = r =>
            {
                Walls(r, cube);
                if (!sphere.IsValid) sphere = MeshGeometry.RegisterSphere(r, RenderColor.White, SphereRadius);
                r.DrawMesh(new MeshDrawCall
                {
                    Mesh = sphere, Tint = new RenderColor(0.85f, 0.85f, 0.85f), Alpha = 1f, MaterialFactors = new Vector4(1f, 0.04f, 0f, 1f),
                    World = Matrix4x4.CreateTranslation(0f, SphereHeight, 0f),
                });
            };
            LightProbes.ResetForRoom();
            PlacedProbes.Clear();
            harness.MeasureLitScene(3, SunTowardsWalls, shadows: false, scene);
            harness.CaptureLitScene(File("none"), SunTowardsWalls, shadows: false, scene);

            PlacedProbes.SetRoom(new[] { ReflectionBox("Far away", new Vector3(0f, 2f, 200f)),
                new PlacedProbe { Name = "Far light", Kind = PlacedProbeKind.Light, Position = new Vector3(0f, 2f, -200f), Radius = 5f } });
            harness.MeasureLitScene(4, SunTowardsWalls, shadows: false, scene);
            int captured = PlacedProbes.Captured;
            harness.CaptureLitScene(File("out-of-reach"), SunTowardsWalls, shadows: false, scene);

            PlacedProbes.SetRoom(TwoReflectionBoxes());
            harness.MeasureLitScene(4, SunTowardsWalls, shadows: false, scene);
            PlacedProbes.Enabled = false;
            harness.CaptureLitScene(File("off"), SunTowardsWalls, shadows: false, scene);

            using Bitmap none = new(File("none")), far = new(File("out-of-reach")), off = new(File("off"));
            int farDiff = MaxDifference(none, far), offDiff = MaxDifference(none, off);
            HeadlessHarness.Assert(captured == 2, $"The probes out of reach were not captured ({captured} of 2).");
            HeadlessHarness.Assert(farDiff == 0 && offDiff == 0,
                $"Placed probes that reach nothing in view, or are turned off, changed the picture (largest channel difference {farDiff} out of reach, {offDiff} off; 0 expected).");
        }
        finally
        {
            RenderBackendSelection.Configure(previous);
            PlacedProbes.Clear();
            LightProbes.ResetForRoom();
            LightProbes.InGame = false;
        }
    }

    // What capturing 4 and 16 probes costs at a room's start on DX11: one capture a frame, the CPU
    // time of each and the frames until the last is ready.
    private static void CaptureCost(HeadlessContext ctx)
    {
        RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
        var report = new List<string>();
        LightProbes.InGame = true;
        try
        {
            RenderBackendSelection.Configure(RenderBackendOption.SilkNetDx11);
            using RuntimeViewportHarness harness = new();
            MeshHandle cube = harness.LitSceneCube;
            Action<IRenderController> scene = r =>
            {
                Walls(r, cube);
                for (int i = 0; i < 24; i++)
                    r.DrawMesh(new MeshDrawCall
                    {
                        Mesh = cube, Tint = new RenderColor(0.5f + i % 3 * 0.2f, 0.6f, 0.5f), Alpha = 1f,
                        World = Matrix4x4.CreateScale(0.6f) * Matrix4x4.CreateTranslation(-5f + i % 6 * 2f, 0.3f, 1f + i / 6 * 1.5f),
                    });
            };
            LightProbes.ResetForRoom();
            PlacedProbes.Clear();
            List<double> baseline = harness.MeasureLitScene(20, SunTowardsWalls, shadows: false, scene);
            foreach (int count in new[] { 4, 16 })
            {
                var probes = new List<PlacedProbe>();
                for (int i = 0; i < count; i++)
                    probes.Add(new PlacedProbe
                    {
                        Name = "Probe " + (i + 1), Kind = i % 2 == 0 ? PlacedProbeKind.Reflection : PlacedProbeKind.Light,
                        Shape = i % 2 == 0 ? PlacedProbeShape.Box : PlacedProbeShape.Sphere,
                        Position = new Vector3(-6f + i % 4 * 4f, 1.5f, -2f + i / 4 * 2.5f), Radius = 3f, Size = new Vector3(4f, 3f, 4f),
                    });
                PlacedProbes.SetRoom(probes);
                double longest = 0;
                List<double> gpu = new();
                for (int frame = 0; frame < count + 4; frame++)
                {
                    gpu.AddRange(harness.MeasureLitScene(1, SunTowardsWalls, shadows: false, scene));
                    longest = Math.Max(longest, PlacedProbes.LastCaptureMs);
                }
                double mean = PlacedProbes.CapturesMade > 0 ? PlacedProbes.CaptureMsTotal / PlacedProbes.CapturesMade : 0;
                string line = $"{count} probes: {PlacedProbes.Captured} captured by frame {PlacedProbes.FramesToCaptureAll}, " +
                    $"{PlacedProbes.CaptureMsTotal:F1} ms CPU in all ({mean:F2} ms each, longest {longest:F2} ms), " +
                    $"GPU frame {Median(gpu):F2} ms median / {(gpu.Count > 0 ? gpu.Max() : 0):F2} ms longest while capturing, {Median(baseline):F2} ms without probes";
                report.Add(line);
                Console.WriteLine("      " + line);
                HeadlessHarness.Assert(PlacedProbes.Captured == count && PlacedProbes.FramesToCaptureAll <= count + 2,
                    $"{count} probes were not all captured one a frame: {line}.");
                HeadlessHarness.Assert(longest < 50,
                    $"One placed probe's capture took {longest:F1} ms of a frame; the F5 bar allows no long frames: {line}.");
            }
        }
        finally
        {
            RenderBackendSelection.Configure(previous);
            PlacedProbes.Clear();
            LightProbes.ResetForRoom();
            LightProbes.InGame = false;
            File.WriteAllLines(Path.Combine(ctx.Captures, "placed-probes-cost.txt"), report);
        }
    }

    // The Room editor: Edit > Add light probe / Add reflection probe, the Inspector's fields with
    // undo, the volumes drawn in the 3D view (a capture to inspect), save and reopen, and the room
    // as the game reads it.
    private static void RoomEditorProbes(HeadlessContext ctx)
    {
        var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, "PlacedProbes"), "Placed probes");
        var resources = new ResourceService(project);
        string roomPath = resources.CreateResource(resources.AssetsRoot, ResourceKind.Room, "Probe room");
        RoomAsset room = RoomAssetLoader.Parse(roomPath);
        room.Dimension = RoomDimension.ThreeD;
        RoomAssetLoader.Save(room, roomPath);

        using var host = GateSuite.NewHost(1400, 880);
        using var editor = new RoomEditorControl(roomPath, project.RootPath) { Dock = DockStyle.Fill };
        host.Controls.Add(editor);
        ThemeService.Apply(host);
        GateSuite.ShowHost(host);
        System.Windows.Forms.Application.DoEvents();
        editor.ViewMode3D = true;
        editor.Navigation.SetSection(RoomNavSection.Objects);

        RoomNode light = editor.AddProbe(RoomEditorControl.ProbeType.Light, LeftProbe)!;
        RoomNode reflection = editor.AddProbe(RoomEditorControl.ProbeType.Reflection, RightProbe)!;
        GateSuite.Pump(2, 20);
        HeadlessHarness.Assert(light is { Kind: RoomNodeKind.Probe, Probe.IsReflection: false } && reflection is { Kind: RoomNodeKind.Probe, Probe.IsBox: true }
            && light.Name == "Light Probe 1" && reflection.Name == "Reflection Probe 1" && editor.SelectedNode == reflection && !light.EnabledIn2D,
            $"Adding probes made '{light.Name}' and '{reflection.Name}', selected '{editor.SelectedNode?.Name}'.");

        // The Inspector's own fields, as a designer types them.
        PropertyGrid grid = Find<PropertyGrid>(editor, "RoomNodePropertyGrid");
        object inspector = grid.SelectedObject ?? throw new InvalidOperationException("The Inspector shows nothing for the selected probe.");
        PropertyDescriptorCollection fields = TypeDescriptor.GetProperties(inspector);
        string[] wanted = { "Type", "Shape", "Radius", "SizeX", "SizeY", "SizeZ", "BlendDistance", "BoxProjection" };
        HeadlessHarness.Assert(wanted.All(field => fields[field] is not null),
            "The Inspector lacks a probe field: " + string.Join(", ", wanted.Where(field => fields[field] is null)));
        // The Inspector's Probe group (the Room editor's own panel and Studio's) and its node details.
        string[] probeGroup = editor.GetLiveInspectorValues().Select(value => value.PropertyPath)
            .Where(path => path.StartsWith("Selection.Probe.", StringComparison.Ordinal)).ToArray();
        HeadlessHarness.Assert(probeGroup.Contains("Selection.Probe.SizeX") && probeGroup.Contains("Selection.Probe.BoxProjection")
            && probeGroup.Contains("Selection.Probe.BlendDistance") && !probeGroup.Contains("Selection.Probe.Radius"),
            "The Inspector's Probe group for a reflection box is not its size, projection and blend: " + string.Join(", ", probeGroup));
        bool applied = editor.TryApplyInspectorValue("Selection.Probe.SizeX", 7f)
            && editor.TryApplyInspectorValue("Selection.Probe.BlendDistance", 1.25f);
        HeadlessHarness.Assert(applied, "The Inspector's Probe fields refused a size or blend distance.");
        fields["BoxProjection"]!.SetValue(inspector, true);
        bool set = reflection.Probe!.SizeAxis(0) == 7f && reflection.Probe.BlendDistance == 1.25f && reflection.Probe.BoxProjection;
        editor.Undo();
        bool undone = !reflection.Probe.BoxProjection && reflection.Probe.BlendDistance == 1.25f;
        editor.Redo();
        bool redone = reflection.Probe.BoxProjection;
        HeadlessHarness.Assert(set && undone && redone,
            $"Inspector edits of a probe: set {set}, undo took back only the last {undone}, redo {redone}.");
        editor.Select(light);
        GateSuite.Pump(2, 20);
        object lightInspector = Find<PropertyGrid>(editor, "RoomNodePropertyGrid").SelectedObject
            ?? throw new InvalidOperationException("The Inspector shows nothing for the Light Probe.");
        TypeDescriptor.GetProperties(lightInspector)["Radius"]!.SetValue(lightInspector, 3.5f);
        HeadlessHarness.Assert(light.Probe!.Radius == 3.5f, "The Light Probe's radius was not set from the Inspector.");

        // The volumes in the 3D view.
        editor.SetGridVisible(false);
        editor.Viewport.Camera.Target = new Vector3(0f, 1.5f, 0f);
        editor.Viewport.Camera.Distance = 22f;
        editor.Viewport.Camera.Pitch = -0.45f;
        editor.Viewport.Camera.Yaw = 0.35f;
        GateSuite.Pump(2, 20);
        using (Bitmap? frame = editor.Viewport.CaptureFrame(3))
            HeadlessHarness.Assert(frame is not null, "The Room view did not draw.");
        HeadlessHarness.Assert(editor.LastProbeGizmosDrawn == 2, $"The view drew {editor.LastProbeGizmosDrawn} probe volumes (2 placed).");
        Editor3DInspectionSuite.Capture(ctx, host, "room-placed-probes-gizmos");
        editor.SetKindVisible(RoomNodeKind.Probe, false);
        GateSuite.Pump(2, 20);
        using (editor.Viewport.CaptureFrame(2)) { }
        HeadlessHarness.Assert(editor.LastProbeGizmosDrawn == 0, "View > Types > Probes off still drew probe volumes.");
        editor.SetKindVisible(RoomNodeKind.Probe, true);

        // Delete and undo, then save and reopen.
        editor.Select(light);
        editor.DeleteSelected();
        bool deleted = !editor.Room.Nodes.Contains(light);
        editor.Undo();
        HeadlessHarness.Assert(deleted && editor.Room.Nodes.Contains(light), "Deleting a probe and undoing it did not bring it back.");
        editor.Save();
        using var reopened = new RoomEditorControl(roomPath, project.RootPath);
        RoomNode[] probes = reopened.Room.Nodes.Where(node => node.Kind == RoomNodeKind.Probe).ToArray();
        RoomNode? savedLight = probes.FirstOrDefault(node => node.Name == "Light Probe 1"), savedBox = probes.FirstOrDefault(node => node.Name == "Reflection Probe 1");
        HeadlessHarness.Assert(probes.Length == 2 && savedLight?.Probe is { IsReflection: false, Radius: 3.5f } && savedBox?.Probe is { IsBox: true, BoxProjection: true, BlendDistance: 1.25f }
            && savedBox.Probe.SizeAxis(0) == 7f && savedBox.Transform.X == RightProbe.X && savedLight.Transform.Z == LeftProbe.Z,
            $"The probes did not survive save and reopen: {probes.Length} probe node(s).");

        // As the game reads the room.
        List<PlacedProbe> forGame = RoomSceneBuilder.PlacedProbesOf(RoomAssetLoader.Parse(roomPath));
        HeadlessHarness.Assert(forGame.Count == 2
            && forGame.Any(probe => probe is { Kind: PlacedProbeKind.Light, Shape: PlacedProbeShape.Sphere, Radius: 3.5f } && probe.Position == LeftProbe)
            && forGame.Any(probe => probe is { Kind: PlacedProbeKind.Reflection, Shape: PlacedProbeShape.Box, BoxProjection: true } && probe.Size.X == 7f && probe.Position == RightProbe),
            $"The game reads {forGame.Count} probe(s) from the saved room, not the two placed.");
        room = RoomAssetLoader.Parse(roomPath);
        room.Dimension = RoomDimension.TwoD;
        HeadlessHarness.Assert(RoomSceneBuilder.PlacedProbesOf(room).Count == 0, "A 2D room must not hand probes to the renderer.");

        // And with automation, as an assistant or a script places them.
        AutomationContext automation = AutomationContext.For(project);
        automation.Caller = "test";
        AutomationRegistry registry = AutomationRegistry.CreateDefault();
        string relativeRoom = Path.GetRelativePath(project.RootPath, roomPath).Replace('\\', '/');
        AutomationResult added = registry.Invoke(automation, "room.addProbe", JsonNode.Parse($$"""
            { "room": "{{relativeRoom}}", "type": "reflection", "x": 0, "y": 2, "z": 8, "size": [6, 3, 4], "boxProjection": true, "blend": 0.5, "name": "Hall" }
            """) as JsonObject);
        AutomationResult refused = registry.Invoke(automation, "room.addProbe", JsonNode.Parse($$"""
            { "room": "{{relativeRoom}}", "type": "light", "shape": "box" }
            """) as JsonObject);
        RoomNode? hall = RoomAssetLoader.Parse(roomPath).Nodes.FirstOrDefault(node => node.Name == "Hall");
        HeadlessHarness.Assert(added.Ok && !refused.Ok && hall?.Probe is { IsBox: true, BoxProjection: true, BlendDistance: 0.5f }
            && hall.Probe.SizeAxis(0) == 6f && hall.Transform.Z == 8f,
            $"room.addProbe: {added.Message} / a light box: {refused.Message}.");
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        root.Controls.Find(name, searchAllChildren: true).OfType<T>().FirstOrDefault()
            ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}.");

    // How much one colour stands out of a 5 x 5 patch: red (or blue) less the mean of the other two.
    private static double Hue(Bitmap image, Point centre, bool red)
    {
        double total = 0;
        int count = 0;
        for (int y = centre.Y - 2; y <= centre.Y + 2; y++)
            for (int x = centre.X - 2; x <= centre.X + 2; x++)
            {
                if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) continue;
                Color c = image.GetPixel(x, y);
                total += red ? c.R - (c.G + c.B) / 2.0 : c.B - (c.R + c.G) / 2.0;
                count++;
            }
        return count == 0 ? 0 : total / count;
    }

    private static int MaxDifference(Bitmap a, Bitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return 255;
        int largest = 0;
        for (int y = 0; y < a.Height; y += 2)
            for (int x = 0; x < a.Width; x += 2)
            {
                Color p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                largest = Math.Max(largest, Math.Max(Math.Abs(p.R - q.R), Math.Max(Math.Abs(p.G - q.G), Math.Abs(p.B - q.B))));
            }
        return largest;
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(value => value).ToList();
        return sorted[sorted.Count / 2];
    }
}
