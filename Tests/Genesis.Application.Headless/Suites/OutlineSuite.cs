using System.Drawing;
using System.Numerics;
using Genesis.Application.Runtime;
using Genesis.Rendering.Core;
using Genesis.Runtime;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scripting;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Outlines for readability (request 2 of the Aetherium port's FPS work): an engine outline per
/// instance, its colour and width, shown through walls or not, and the object images (marks and
/// ids) a project post effect reads. Pixel-checked on DX11, DX12, Vulkan and OpenGL; the cost is
/// measured at 1920 x 1080 on DX11.
/// </summary>
internal static class OutlineSuite
{
    private static readonly RenderBackendOption[] GpuBackends =
    {
        RenderBackendOption.SilkNetDx11, RenderBackendOption.Direct3D12,
        RenderBackendOption.Vulkan, RenderBackendOption.OpenGL,
    };

    private static readonly Vector3 SunFront = Vector3.Normalize(new Vector3(0.45f, -0.75f, 0.5f));
    private static readonly Vector3 Seen = new(-1.3f, 0.6f, 0f);
    private static readonly Vector3 Hidden = new(1.3f, 0.6f, 0.4f);
    private static readonly Vector3 WallCentre = new(1.3f, 1.1f, -1.2f);
    private static readonly Vector3 WallSize = new(2.6f, 2.6f, 0.2f);

    // Paints the pixels of instance 11 magenta and those of instance 22 hidden behind something
    // (a negative id) cyan, from the ObjectIds and ObjectMarks images.
    private const string IdEffect = """
        Texture2D SceneColor : register(t0);
        Texture2D<float4> ObjectMarks : register(t3);
        Texture2D<float> ObjectIds : register(t4);
        struct PreviewVSOut { float4 SvPos : SV_Position; float2 UV : TEXCOORD0; };
        float4 MainPS(PreviewVSOut IN) : SV_Target
        {
            int3 p = int3(int2(IN.SvPos.xy), 0);
            float4 c = SceneColor.Load(p);
            float id = ObjectIds.Load(p);
            if (abs(id - 11.0) < 0.5 && ObjectMarks.Load(p).a > 0.0) return float4(1.0, 0.0, 1.0, 1.0);
            if (abs(id + 22.0) < 0.5) return float4(0.0, 1.0, 1.0, 1.0);
            return c;
        }
        """;

    // A shader pack run after the id effect: it multiplies the frame by a texture it declares at t3
    // under another name (not ObjectMarks) and by one at t5 that nothing binds. Both must read
    // white, so the frame, outlines and id colours included, comes through unchanged.
    private const string PackEffect = """
        Texture2D SceneColor : register(t0);
        Texture2D Grain : register(t3);
        Texture2D Paper : register(t5);
        SamplerState Linear : register(s0);
        struct PreviewVSOut { float4 SvPos : SV_Position; float2 UV : TEXCOORD0; };
        float4 MainPS(PreviewVSOut IN) : SV_Target
        {
            float4 c = SceneColor.Load(int3(int2(IN.SvPos.xy), 0));
            return float4(c.rgb * Grain.Sample(Linear, IN.UV).rgb * Paper.Sample(Linear, IN.UV).rgb, c.a);
        }
        """;

    /// <summary>An effect as a game's would reach the renderer: with what its code declares (ProjectPostEffects).</summary>
    private static PostEffectRequest Effect(string name, string source)
    {
        (int textures, int samplers, int objectImages) = ProjectPostEffects.DeclaredSlots(source);
        return new PostEffectRequest(name, source, "MainPS", null!, null!, default, default, default, default)
        {
            TextureSlots = textures, SamplerSlots = samplers, ObjectImageSlots = objectImages,
        };
    }

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.Outline.ThroughWallsAndObjectIdsOnAllBackends", () => Outlines(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.Outline.InstancesOutlineTheirModels", () => Instances(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.Outline.CostAt1080p", () => Cost(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Outline.CommandsReachTheDrawEntry", () => Commands(ctx));
    }

    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);

    private static Action<IRenderController> Scene(MeshHandle cube, bool outlines, bool hiddenThroughWalls, bool idEffect = false, bool packEffect = false) => r =>
    {
        r.SetPostEffects(packEffect
            ? [Effect("Ids", IdEffect), Effect("Pack", PackEffect)]
            : idEffect
            ? [new PostEffectRequest("Ids", IdEffect, "MainPS", null!, null!, default, default, default, default)]
            : Array.Empty<PostEffectRequest>());
        void Box(Vector3 centre, Vector3 size, RenderColor tint, Vector4 outline = default, int id = 0, bool through = false) =>
            r.DrawMesh(new MeshDrawCall
            {
                Mesh = cube, Tint = tint, Alpha = 1f,
                World = Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation(centre),
                Outline = outline, OutlineId = outlines ? id : 0, OutlineThroughWalls = through,
            });
        RenderColor grey = new(0.6f, 0.6f, 0.62f);
        Box(Seen, new Vector3(1.2f), grey, new Vector4(1f, 0f, 0f, 3f), 11);
        Box(Hidden, new Vector3(1.0f), grey, new Vector4(0f, 1f, 0f, 3f), 22, hiddenThroughWalls);
        Box(WallCentre, WallSize, new RenderColor(0.5f, 0.45f, 0.4f));
    };

    private static void Outlines(HeadlessContext ctx)
    {
        RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
        var report = new List<string>();
        try
        {
            foreach (RenderBackendOption backend in GpuBackends)
            {
                RenderBackendSelection.Configure(backend);
                string name = backend.ToString().ToLowerInvariant();
                using RuntimeViewportHarness harness = new();
                MeshHandle cube = harness.LitSceneCube;
                string File(string what) => Path.Combine(ctx.Captures, $"outline-{name}-{what}.png");
                harness.CaptureLitScene(File("none"), SunFront, shadows: true, Scene(cube, outlines: false, false));
                harness.CaptureLitScene(File("through-walls"), SunFront, shadows: true, Scene(cube, outlines: true, true));
                harness.CaptureLitScene(File("not-through"), SunFront, shadows: true, Scene(cube, outlines: true, false));
                harness.CaptureLitScene(File("ids"), SunFront, shadows: true, Scene(cube, outlines: true, true, idEffect: true));
                RenderStats stats = harness.LastStats;
                harness.CaptureLitScene(File("ids-pack"), SunFront, shadows: true, Scene(cube, outlines: true, true, packEffect: true));

                Rectangle seenBox = Bounds(harness, Seen, 0.6f), hiddenBox = Bounds(harness, Hidden, 0.5f);
                using Bitmap none = new(File("none")), through = new(File("through-walls")), notThrough = new(File("not-through")), ids = new(File("ids"));
                using Bitmap idsPack = new(File("ids-pack"));
                int noneRed = Count(none, IsRed, Rectangle.Empty), noneGreen = Count(none, IsGreen, Rectangle.Empty);
                int red = Count(through, IsRed, Rectangle.Empty), redNear = Count(through, IsRed, Grow(seenBox, 5));
                int green = Count(through, IsGreen, Rectangle.Empty), greenNear = Count(through, IsGreen, Grow(hiddenBox, 5));
                int greenHidden = Count(notThrough, IsGreen, Rectangle.Empty), redStill = Count(notThrough, IsRed, Grow(seenBox, 5));
                Point centre = harness.ProjectLitScenePoint(Seen + new Vector3(0f, 0f, -0.6f));
                Color inside = through.GetPixel(centre.X, centre.Y), insidePlain = none.GetPixel(centre.X, centre.Y);
                Color marked = ids.GetPixel(centre.X, centre.Y);
                Point hiddenCentre = harness.ProjectLitScenePoint(Hidden + new Vector3(0f, 0f, -0.5f));
                Color behind = ids.GetPixel(hiddenCentre.X, hiddenCentre.Y);
                report.Add($"{backend}: red {red} ({redNear} beside the box), green through walls {green} ({greenNear} around the hidden box), "
                    + $"green not through walls {greenHidden}, inside {inside} vs {insidePlain}, id effect {marked}, draws {stats.DrawCalls3D}");

                Check(noneRed == 0 && noneGreen == 0, $"{backend}: the scene has outline colours of its own ({noneRed} red, {noneGreen} green).");
                Check(red > 200 && redNear >= red * 0.98,
                    $"{backend}: the seen box's red outline is missing or misplaced ({red} red pixels, {redNear} beside it).");
                Check(Math.Abs(Luma(inside) - Luma(insidePlain)) < 6,
                    $"{backend}: the outline painted over the box itself ({inside} against {insidePlain}).");
                Check(green > 150 && greenNear >= green * 0.98,
                    $"{backend}: the box behind the wall has no outline through it ({green} green pixels, {greenNear} around it).");
                Check(greenHidden == 0 && redStill > 200,
                    $"{backend}: an outline not shown through walls showed through one ({greenHidden} green pixels), or the seen one went ({redStill}).");
                Check(marked.R > 240 && marked.G < 20 && marked.B > 240,
                    $"{backend}: a post effect could not find the box's id in ObjectIds ({marked}).");
                Check(behind.R < 20 && behind.G > 240 && behind.B > 240,
                    $"{backend}: the box behind the wall is not marked hidden (a negative id) in ObjectIds ({behind}).");

                // The id effect declaring ObjectMarks and ObjectIds by name, as a game's reaches the
                // renderer, still reads them; the pack after it reads white at t3 (another name) and
                // at t5 (unbound), so the whole frame is as the id effect left it.
                Color packMarked = idsPack.GetPixel(centre.X, centre.Y), packBehind = idsPack.GetPixel(hiddenCentre.X, hiddenCentre.Y);
                Point wall = harness.ProjectLitScenePoint(WallCentre + new Vector3(-0.9f, 0.9f, 0.11f));
                double changed = 0;
                foreach (Point at in new[] { wall, new Point(12, 12), new Point(ids.Width - 12, ids.Height - 12), new Point(ids.Width / 2, 12) })
                    changed = Math.Max(changed, Math.Abs(Luma(idsPack.GetPixel(at.X, at.Y)) - Luma(ids.GetPixel(at.X, at.Y))));
                int packRed = Count(idsPack, IsRed, Grow(seenBox, 5));
                report.Add($"{backend}: with a pack after it: id {packMarked}, hidden {packBehind}, largest change elsewhere {changed:F1}, red outline {packRed}");
                Check(packMarked.R > 240 && packMarked.G < 20 && packMarked.B > 240 && packBehind.R < 20 && packBehind.G > 240 && packBehind.B > 240,
                    $"{backend}: an effect declaring ObjectMarks/ObjectIds by name lost the object images ({packMarked}, {packBehind}).");
                Check(changed < 6 && packRed > 200,
                    $"{backend}: a pack's textures at t3 (not named ObjectMarks) and t5 (unbound) did not read white: the frame changed by {changed:F1}, red outline {packRed}.");
            }
        }
        finally { RenderBackendSelection.Configure(previous); }
        File.WriteAllLines(Path.Combine(ctx.Captures, "outline-backends.txt"), report);
    }

    // A model placed in a room, outlined from a script, through the same draw pass a game uses.
    private static void Instances(HeadlessContext ctx)
    {
        string root = MeshSurfaceSuite.BuildGunProject(ctx, "OutlineGuns", out _);
        RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
        IGameContext? previousGame = PgslCommands.ActiveGameContext;
        string? previousProject = PgslCommands.ProjectPath;
        var registry = new ObjectDrawAssetRegistry.PreviewScope();
        try
        {
            using IDisposable scope = registry.Activate();
            using var scene = new RuntimeScene("Outlined guns");
            PgslCommands.ActiveGameContext = new Genesis.Runtime.Project.ProjectGameContext(root, scene, null, null,
                Genesis.Runtime.Scene.RoomAsset.Create("Probe", Genesis.Runtime.Scene.RoomDimension.ThreeD), null);
            PgslCommands.ProjectPath = root;
            Entity left = MeshSurfaceSuite.PlaceGun(scene, -1.5f), right = MeshSurfaceSuite.PlaceGun(scene, 1.5f);
            PgslCommands.InstanceSetOutline(right.Id, 1, 0.9, 0, 3, false);
            var buffer = new MeshDrawCall[64];
            foreach (RenderBackendOption backend in GpuBackends)
            {
                RenderBackendSelection.Configure(backend);
                using RuntimeViewportHarness harness = new();
                (Matrix4x4 view, Matrix4x4 projection) = harness.LitSceneCamera();
                Matrix4x4.Invert(view, out Matrix4x4 inverse);
                Vector3 forward = Vector3.Normalize(new Vector3(inverse.M31, inverse.M32, inverse.M33));
                string file = Path.Combine(ctx.Captures, $"outline-{backend.ToString().ToLowerInvariant()}-guns.png");
                int marked = 0;
                harness.CaptureLitScene(file, SunFront, shadows: true, r =>
                {
                    int count = 0;
                    ObjectDrawPass.SubmitMeshes3D(scene.World, root, buffer, ref count, r, RuntimeViewportHarness.LitSceneEye, forward, view * projection);
                    marked = 0;
                    for (int i = 0; i < count; i++) if (buffer[i].OutlineId == right.Id) marked++;
                    r.DrawMeshBatch(buffer.AsSpan(0, count));
                });
                ObjectDrawPass.InvalidateAssets(harness.Renderer);
                using Bitmap image = new(file);
                Rectangle rightBox = Grow(Bounds(harness, new Vector3(1.5f, 1f, 0f), new Vector3(0.5f, 1f, 0.5f)), 5);
                Rectangle leftBox = Grow(Bounds(harness, new Vector3(-1.5f, 1f, 0f), new Vector3(0.5f, 1f, 0.5f)), 5);
                int yellow = Count(image, IsYellow, Rectangle.Empty), nearRight = Count(image, IsYellow, rightBox), nearLeft = Count(image, IsYellow, leftBox);
                Check(marked == 2, $"{backend}: the outlined gun's two meshes were marked {marked} times.");
                Check(yellow > 200 && nearRight >= yellow * 0.98 && nearLeft == 0,
                    $"{backend}: the script's outline is not around its own gun alone ({yellow} yellow, {nearRight} by it, {nearLeft} by the other).");
            }
            PgslCommands.InstanceClearOutline(right.Id);
            Check(!PgslCommands.InstanceHasOutline(right.Id), "InstanceClearOutline left the outline.");
        }
        finally
        {
            RenderBackendSelection.Configure(previous);
            PgslCommands.ActiveGameContext = previousGame;
            PgslCommands.ProjectPath = previousProject;
        }
    }

    // GPU time of the whole frame with twenty outlined boxes against the same boxes without, at
    // 1920 x 1080 on DX11, over three rounds; the difference is the outlines' cost.
    private static void Cost(HeadlessContext ctx)
    {
        RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
        var lines = new List<string>();
        try
        {
            RenderBackendSelection.Configure(RenderBackendOption.SilkNetDx11);
            using RuntimeViewportHarness harness = new(1920, 1080);
            harness.ResizeTo(1920, 1080);
            MeshHandle cube = harness.LitSceneCube;
            Action<IRenderController> Boxes(bool outlined, bool throughWalls) => r =>
            {
                for (int i = 0; i < 20; i++)
                {
                    float x = -3.6f + (i % 5) * 1.8f, z = -1f + (i / 5) * 1.6f;
                    r.DrawMesh(new MeshDrawCall
                    {
                        Mesh = cube, Tint = new RenderColor(0.6f, 0.6f, 0.62f), Alpha = 1f,
                        World = Matrix4x4.CreateScale(0.8f) * Matrix4x4.CreateTranslation(x, 0.4f, z),
                        Outline = new Vector4(1f, 0.2f, 0.1f, 3f), OutlineId = outlined ? 100 + i : 0,
                        OutlineThroughWalls = throughWalls,
                    });
                }
            };
            var withoutAll = new List<double>();
            var withAll = new List<double>();
            for (int round = 0; round < 3; round++)
            {
                harness.MeasureLitScene(30, SunFront, true, Boxes(false, false)); // warm-up
                List<double> without = harness.MeasureLitScene(120, SunFront, true, Boxes(false, false));
                List<double> with = harness.MeasureLitScene(120, SunFront, true, Boxes(true, true));
                double a = Median(without), b = Median(with);
                withoutAll.Add(a);
                withAll.Add(b);
                lines.Add($"round {round + 1}: {a:F3} ms without outlines, {b:F3} ms with twenty (width 3, through walls): +{b - a:F3} ms");
            }
            double cost = Median(withAll.Zip(withoutAll, (w, o) => w - o).ToList());
            lines.Add($"median added cost: {cost:F3} ms at {harness.BackendRenderSize.Width} x {harness.BackendRenderSize.Height}");
            Check(withoutAll.All(ms => ms > 0), "The GPU timer reported nothing to measure against.");
            Check(cost < 2.0, $"Twenty outlines cost {cost:F2} ms of GPU time at 1080p.");
        }
        finally
        {
            RenderBackendSelection.Configure(previous);
            File.WriteAllLines(Path.Combine(ctx.Captures, "outline-cost-1080p.txt"), lines);
        }
    }

    private static void Commands(HeadlessContext ctx)
    {
        string folder = Path.Combine(ctx.Workspace, "OutlineCommands");
        Directory.CreateDirectory(folder);
        // Dummy and missing instances are ignored; a value out of range is limited.
        PgslCommands.InstanceSetOutline(0, 1, 0, 0, 3, true);
        PgslCommands.InstanceClearOutline(-5);
        Check(!PgslCommands.InstanceHasOutline(0), "An outline was reported on no instance.");
        IGameContext? previousGame = PgslCommands.ActiveGameContext;
        var registry = new ObjectDrawAssetRegistry.PreviewScope();
        try
        {
            using IDisposable scope = registry.Activate();
            using var scene = new RuntimeScene("Outline commands");
            PgslCommands.ActiveGameContext = new Genesis.Runtime.Project.ProjectGameContext(folder, scene, null, null,
                Genesis.Runtime.Scene.RoomAsset.Create("Probe", Genesis.Runtime.Scene.RoomDimension.ThreeD), null);
            Entity entity = scene.World.CreateEntity();
            PgslCommands.InstanceSetOutline(entity.Id, 2, -1, 0.5, 400, true);
            Check(ObjectDrawAssetRegistry.TryGet(entity, out ObjectDrawAssetEntry assets)
                && assets.Outline == new Vector4(1f, 0f, 0.5f, 16f) && assets.OutlineThroughWalls,
                $"The outline did not reach the instance limited to range: {assets?.Outline}.");
            PgslCommands.InstanceSetOutline(entity.Id, double.NaN, 0, 0, 2, false);
            Check(assets!.Outline == new Vector4(1f, 0f, 0.5f, 16f), "A colour that is not a number replaced the outline.");
        }
        finally { PgslCommands.ActiveGameContext = previousGame; }
    }

    // ── Pixels ───────────────────────────────────────────────────────────────

    private static Rectangle Bounds(RuntimeViewportHarness harness, Vector3 centre, float half) =>
        Bounds(harness, centre, new Vector3(half));

    private static Rectangle Bounds(RuntimeViewportHarness harness, Vector3 centre, Vector3 half)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 p = centre + new Vector3((corner & 1) == 0 ? -half.X : half.X, (corner & 2) == 0 ? -half.Y : half.Y, (corner & 4) == 0 ? -half.Z : half.Z);
            Point s = harness.ProjectLitScenePoint(p);
            minX = Math.Min(minX, s.X); minY = Math.Min(minY, s.Y); maxX = Math.Max(maxX, s.X); maxY = Math.Max(maxY, s.Y);
        }
        return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    private static Rectangle Grow(Rectangle box, int by) => Rectangle.Inflate(box, by, by);

    private static int Count(Bitmap image, Func<Color, bool> test, Rectangle within)
    {
        int count = 0;
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                if ((within.IsEmpty || within.Contains(x, y)) && test(image.GetPixel(x, y))) count++;
        return count;
    }

    private static bool IsRed(Color c) => c.R > c.G + 80 && c.R > c.B + 80;
    private static bool IsGreen(Color c) => c.G > c.R + 80 && c.G > c.B + 80;
    private static bool IsYellow(Color c) => c.R > c.B + 90 && c.G > c.B + 90;
    private static double Luma(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }
}
