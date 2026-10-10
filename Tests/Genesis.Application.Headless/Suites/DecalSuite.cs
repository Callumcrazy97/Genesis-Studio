using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Rendering.Core;
using Genesis.Runtime.Project;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using Genesis.World.Terrain;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Projected decals (PGSL DecalAdd): on a wall and on real terrain, from a project Image and from
/// a texture a script painted; fading out at the end of their life; the oldest removed past the
/// limit; nothing on a surface facing away; see-through decals in the order they were added; on
/// every renderer. And what hundreds of bullet holes cost at 1080p on DX11.
/// </summary>
internal static class DecalSuite
{
    private const int Width = 480, Height = 320;

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Decals");
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Decals.CommandsKeepLimitLifeAndOrder", Commands);
        HeadlessHarness.RunCase(ctx.Report, "Render.Decals.WallTerrainFadeLimitAndFacingOnEveryBackend", () => Pixels(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Render.Decals.HundredsOfBulletHolesAt1080pOnDx11", () => Cost(ctx));
    }

    private static void Commands()
    {
        WorldDecals.Reset();
        try
        {
            double first = PgslCommands.DecalAdd("x", 0, 1, 2, 0, 0, -1, 0.5, 1.0);
            Check(first > 0 && PgslCommands.DecalCount() == 1 && PgslCommands.DecalExists(first), "DecalAdd did not place a decal.");
            Check(PgslCommands.DecalSetColor(first, 255, 0, 0, 0.5) && PgslCommands.DecalSetRotation(first, 45)
                && PgslCommands.DecalSetSize(first, 1, 2, 0.3) && PgslCommands.DecalSetBlend(first, "alpha")
                && PgslCommands.DecalSetFade(first, 0.25) && PgslCommands.DecalMove(first, 1, 1, 1, 0, 1, 0),
                "A live decal refused a change.");
            Check(!PgslCommands.DecalSetBlend(first, "sideways"), "An unknown blend was accepted.");
            Check(PgslCommands.DecalAdd("x", double.NaN, 0, 0, 0, 1, 0, 1, 1) == 0 && PgslCommands.DecalAdd("x", 0, 0, 0, 0, 0, 0, 1, 1) == 0
                && PgslCommands.DecalAdd("x", 0, 0, 0, 0, 1, 0, 0, 1) == 0, "A decal with no position, normal or size was placed.");
            Check(!PgslCommands.DecalSetColor(999, 1, 1, 1, 1) && !PgslCommands.DecalRemove(-3), "A missing decal was changed.");

            // Life and fade: one second, fading over its last quarter.
            Check(WorldDecals.Opacity(0.5f, 1f, -1f) == 1f && MathF.Abs(WorldDecals.Opacity(0.9f, 1f, -1f) - 0.4f) < 1e-4f
                && WorldDecals.Opacity(1f, 1f, -1f) == 0f && WorldDecals.Opacity(100f, 0f, -1f) == 1f, "Decal opacity over a life is wrong.");
            double forever = PgslCommands.DecalAdd("x", 0, 0, 0, 0, 1, 0, 1, 0);
            WorldDecals.Advance(0.6f);
            Check(PgslCommands.DecalExists(first) && PgslCommands.DecalExists(forever), "A decal went before its life ended.");
            WorldDecals.Advance(0.5f);
            Check(!PgslCommands.DecalExists(first) && PgslCommands.DecalExists(forever), "A decal outlived its life, or one without a life went.");
            Check(PgslCommands.DecalSetLife(forever, 2) && PgslCommands.DecalRemove(forever) && PgslCommands.DecalCount() == 0, "DecalRemove left the decal.");

            // The limit removes the oldest.
            PgslCommands.DecalSetLimit(3);
            double[] ids = Enumerable.Range(0, 5).Select(i => PgslCommands.DecalAdd("x", i, 0, 0, 0, 1, 0, 1, 0)).ToArray();
            Check(PgslCommands.DecalCount() == 3 && !PgslCommands.DecalExists(ids[0]) && !PgslCommands.DecalExists(ids[1])
                && PgslCommands.DecalExists(ids[2]) && PgslCommands.DecalExists(ids[4]), "Past the limit the oldest decals were not the ones removed.");
            PgslCommands.DecalSetLimit(1);
            Check(PgslCommands.DecalCount() == 1 && PgslCommands.DecalExists(ids[4]), "Lowering the limit did not keep the newest.");
            PgslCommands.DecalClear();
            Check(PgslCommands.DecalCount() == 0, "DecalClear left decals.");

            // On a wall facing -z the picture runs to the viewer's right (-x, the camera looking +z) with its top up.
            Vector3 tangent = WorldDecals.Tangent(-Vector3.UnitZ, 0f);
            Check(Vector3.Distance(tangent, -Vector3.UnitX) < 1e-4f, $"A wall decal's picture runs along {tangent}.");
            Vector3 turned = WorldDecals.Tangent(-Vector3.UnitZ, 90f);
            Check(Vector3.Distance(turned, Vector3.UnitY) < 1e-4f, $"A quarter turn anticlockwise leaves the picture along {turned}.");
        }
        finally { WorldDecals.Reset(); }
    }

    private sealed record Scene(string Root, RoomAsset Room, TerrainAsset Terrain);

    private static Scene Build(HeadlessContext ctx, string folder)
    {
        var project = new ProjectService().CreateProject(Path.Combine(ctx.Workspace, folder), "Decals");
        ResourceService resources = new(project);
        void Picture(string name, Color color)
        {
            string file = resources.CreateResource(resources.AssetsRoot, ResourceKind.Image, name);
            ImageDocumentSession session = new(ImageDocumentSerializer.LoadAtomic(file).Document, file, ImageDocumentAccess.Editor);
            ImageWorkspaceStorage.Save(session, ImageWorkspace.CreateBlank(16, 16, color));
        }
        Picture("Decal stain", Color.FromArgb(255, 200, 40, 40));
        Picture("Decal paint red", Color.FromArgb(255, 230, 30, 30));
        Picture("Decal paint blue", Color.FromArgb(255, 30, 60, 230));
        Genesis.Shared.Assets.ResourceCatalog.Invalidate(project.RootPath);

        // 32 m of gently rolling ground.
        var terrain = new TerrainAsset(65, 65, 0.5f, -16f, -16f, -2f, 2f);
        for (int z = 0; z < 65; z++)
            for (int x = 0; x < 65; x++)
                terrain.SetHeight(x, z, 0.12f * MathF.Sin(x * 0.35f) + 0.1f * MathF.Cos(z * 0.3f));
        string terrains = Path.Combine(project.RootPath, "Terrains");
        Directory.CreateDirectory(terrains);
        terrain.Save(Path.Combine(terrains, "Decal ground.gterrain"));
        RoomAsset room = RoomAsset.Create("Decal yard", RoomDimension.ThreeD);
        room.Environment.DynamicSky = false;
        room.Nodes.Add(new RoomNode
        {
            Name = "Decal ground",
            Kind = RoomNodeKind.Terrain,
            LayerId = room.Layers[0].Id,
            Terrain = new RoomTerrainData { Asset = "Terrains/Decal ground.gterrain" },
            Transform = new RoomTransform { ScaleX = 1, ScaleY = 1, ScaleZ = 1 },
        });
        return new Scene(project.RootPath, room, terrain);
    }

    private static void Pixels(HeadlessContext ctx)
    {
        Scene scene = Build(ctx, "DecalPixels");
        Vector3 eye = new(0, 3.2f, -3.5f), target = new(0, 1.2f, 8f);
        Matrix4x4 view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, Width / (float)Height, 0.1f, 200f);
        Point Screen(Vector3 world)
        {
            Vector4 clip = Vector4.Transform(new Vector4(world, 1f), view * projection);
            return new Point((int)((clip.X / clip.W * 0.5f + 0.5f) * Width), (int)((0.5f - clip.Y / clip.W * 0.5f) * Height));
        }

        // Wall decals on the wall at z = 8 (facing -z, towards the camera), one on the ground.
        Vector3 wallFade = new(-4.5f, 2.6f, 8f), wallLimitOld = new(-1.5f, 2.6f, 8f), wallLimitNew = new(1.5f, 2.6f, 8f);
        Vector3 wallAway = new(4.5f, 2.6f, 8f), wallOrder = new(-3f, 4.4f, 8f);
        Vector3 groundSpot = new(-1.5f, scene.Terrain.SampleHeight(-1.5f, 3f), 3f);

        List<string> failures = [];
        List<string> readings = [];
        foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
        {
            WorldDecals.Reset();
            ScriptTextures.Reset();
            using Form host = UnattendedWindowing.NewHost(Width, Height); UnattendedWindowing.ShowWithoutFocus(host);
            using IRenderController renderer = RenderControllerFactory.Create(backend.Backend); renderer.Initialize(host.Handle, Width, Height);
            using var terrainRuntime = new RoomTerrainSubsystem(scene.Root, scene.Room, null!);
            MeshDrawCall[] draws = new MeshDrawCall[terrainRuntime.GetMeshDrawCapacity(renderer) + 64];
            MeshHandle wall = renderer.RegisterMesh(WallVertices(16f, 6f, 8f, 32, 12, new Vector4(0.8f, 0.8f, 0.8f, 1f)), WallIndices(32, 12));
            // A black picture the script paints (the number path of DecalAdd's image).
            int painted = ScriptTextures.Create(4, 4);
            byte[] ink = ScriptTextures.BeginWrite(painted, 0, 0, 4, 4, out _, out _)!;
            for (int i = 0; i < ink.Length; i += 4) { ink[i] = 0; ink[i + 1] = 0; ink[i + 2] = 0; ink[i + 3] = 255; }
            string name = backend.ShortName.ToLowerInvariant();
            try
            {
                byte[] Frame(string label)
                {
                    Mesh3DState state = Mesh3DState.Default;
                    state.ShowFloor = false; state.ShowSunVisual = false; state.FogEnabled = false; state.ShadowsEnabled = false;
                    state.CameraFarPlane = 200f;
                    renderer.SetMesh3DState(state); renderer.Set3DFrameActive(true);
                    renderer.SetCamera3D(view, projection);
                    renderer.BeginFrame(); renderer.Clear(0.35f, 0.5f, 0.75f);
                    int count = 0;
                    terrainRuntime.SubmitPreviewMeshes(eye, view * projection, draws, ref count, renderer);
                    for (int i = 0; i < count; i++) renderer.DrawMesh(draws[i]);
                    renderer.DrawMesh(new MeshDrawCall { Mesh = wall, World = Matrix4x4.Identity, Tint = RenderColor.White, Alpha = 1, Flags = MeshDrawFlags.NoShadow });
                    WorldDecals.Submit(renderer, scene.Root);
                    renderer.EndFrame();
                    HeadlessHarness.Assert(renderer.TryReadSubmittedFramePixels(out int width, out int height, out byte[] pixels)
                        && width == Width && height == Height, backend.ShortName + ": no frame could be read back.");
                    FlashLightSuite.Save(ctx, pixels, width, height, "decals-" + name + "-" + label);
                    renderer.Present();
                    return pixels;
                }

                byte[] bare = Frame("bare");
                PgslCommands.DecalSetLimit(16);
                double fading = PgslCommands.DecalAdd("Decal stain", wallFade.X, wallFade.Y, wallFade.Z, 0, 0, -1, 1.2, 2.0);
                double oldest = PgslCommands.DecalAdd("Decal stain", wallLimitOld.X, wallLimitOld.Y, wallLimitOld.Z, 0, 0, -1, 1.2, 0);
                // Placed as if on the far side of the wall: its box covers the front face too, which faces away from it.
                double away = PgslCommands.DecalAdd("Decal stain", wallAway.X, wallAway.Y, wallAway.Z, 0, 0, 1, 1.2, 0);
                double ground = PgslCommands.DecalAdd(painted.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    groundSpot.X, groundSpot.Y, groundSpot.Z, 0, 1, 0, 1.6, 0);
                // Two see-through decals on one spot: the one added later is the one on top.
                double red = PgslCommands.DecalAdd("Decal paint red", wallOrder.X, wallOrder.Y, wallOrder.Z, 0, 0, -1, 1.0, 0);
                double blue = PgslCommands.DecalAdd("Decal paint blue", wallOrder.X, wallOrder.Y, wallOrder.Z, 0, 0, -1, 1.0, 0);
                PgslCommands.DecalSetBlend(red, "alpha"); PgslCommands.DecalSetBlend(blue, "alpha");
                byte[] placed = Frame("placed");
                int drawn = renderer.GetStats().DecalsDrawn;

                WorldDecals.Advance(1.75f);   // 0.25 s left of a 0.5 s fade: half strength
                byte[] fadingFrame = Frame("fading");
                WorldDecals.Advance(0.5f);    // over: five decals are left
                // At a limit of five the next one removes the oldest left, the stain at the limit spot.
                PgslCommands.DecalSetLimit(5);
                double newest = PgslCommands.DecalAdd("Decal stain", wallLimitNew.X, wallLimitNew.Y, wallLimitNew.Z, 0, 0, -1, 1.2, 0);
                byte[] limited = Frame("limit");

                Point fadeAt = Screen(wallFade), oldAt = Screen(wallLimitOld), newAt = Screen(wallLimitNew);
                Point awayAt = Screen(wallAway), orderAt = Screen(wallOrder), groundAt = Screen(groundSpot);
                Color Px(byte[] frame, Point at) => Mean(frame, at.X, at.Y, 3);
                double L(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                bool Stained(Color c, Color plain) => L(c) < L(plain) * 0.85 && c.R > c.G * 2.2 && c.R > c.B * 2.2;
                bool Same(Color c, Color plain) => Math.Abs(c.R - plain.R) <= 6 && Math.Abs(c.G - plain.G) <= 6 && Math.Abs(c.B - plain.B) <= 6;

                Color plainFade = Px(bare, fadeAt), plainOld = Px(bare, oldAt), plainNew = Px(bare, newAt), plainAway = Px(bare, awayAt);
                Color plainGround = Px(bare, groundAt), plainOrder = Px(bare, orderAt);
                Color fadeOn = Px(placed, fadeAt), fadeHalf = Px(fadingFrame, fadeAt), fadeGone = Px(limited, fadeAt);
                Color oldOn = Px(placed, oldAt), oldGone = Px(limited, oldAt), newOn = Px(limited, newAt);
                Color awayOn = Px(placed, awayAt), groundOn = Px(placed, groundAt), orderOn = Px(placed, orderAt);
                readings.Add($"{backend.ShortName}: wall {plainFade} -> {fadeOn} -> half {fadeHalf} -> gone {fadeGone}; ground {plainGround} -> {groundOn}; "
                    + $"facing away {plainAway} -> {awayOn}; alpha order {plainOrder} -> {orderOn}; limit old {oldOn} -> {oldGone}, new {plainNew} -> {newOn}; drawn {drawn}");

                if (!(fading > 0 && oldest > 0 && away > 0 && ground > 0 && red > 0 && blue > 0 && newest > 0))
                    failures.Add(backend.ShortName + ": a decal could not be placed");
                if (drawn != 6)
                    failures.Add($"{backend.ShortName}: {drawn} decals drawn of 6");
                if (!Stained(fadeOn, plainFade)) failures.Add($"{backend.ShortName}: no stain on the wall ({plainFade} -> {fadeOn})");
                if (!(L(fadeHalf) > L(fadeOn) + 4 && L(fadeHalf) < L(plainFade) - 4))
                    failures.Add($"{backend.ShortName}: the fading stain is not between full and gone ({fadeOn} / {fadeHalf} / {plainFade})");
                if (!Same(fadeGone, plainFade)) failures.Add($"{backend.ShortName}: the stain outlived its life ({fadeGone} against {plainFade})");
                if (!(L(groundOn) < L(plainGround) * 0.45)) failures.Add($"{backend.ShortName}: no mark on the terrain ({plainGround} -> {groundOn})");
                if (!Same(awayOn, plainAway)) failures.Add($"{backend.ShortName}: a decal facing away from the surface shows on it ({plainAway} -> {awayOn})");
                if (!(orderOn.B > orderOn.R * 2 && orderOn.B > 120)) failures.Add($"{backend.ShortName}: the later see-through decal is not on top ({orderOn})");
                if (!Stained(oldOn, plainOld) || !Same(oldGone, plainOld)) failures.Add($"{backend.ShortName}: past the limit the oldest stain stayed ({oldOn} -> {oldGone})");
                if (!Stained(newOn, plainNew)) failures.Add($"{backend.ShortName}: the newest stain is missing ({plainNew} -> {newOn})");
            }
            finally
            {
                renderer.ReleaseMesh(wall);
                WorldDecals.Reset();
                ScriptTextures.Reset();
            }
        }
        Directory.CreateDirectory(ctx.Logs);
        File.WriteAllLines(Path.Combine(ctx.Logs, "decal-readings.txt"), readings);
        Console.WriteLine("Decal readings: " + string.Join(" | ", readings));
        HeadlessHarness.Assert(failures.Count == 0, string.Join(" | ", failures));
    }

    /// <summary>
    /// A walled yard at 1920 x 1080 on DX11: no decals, 500 and 2,000 bullet holes (one picture,
    /// one draw each) on the walls and ground in view, three rounds each.
    /// </summary>
    private static void Cost(HeadlessContext ctx)
    {
        const int W = 1920, H = 1080, WarmUp = 45, Measured = 120;
        Scene scene = Build(ctx, "DecalCost");
        WorldDecals.Reset();
        using Form host = UnattendedWindowing.NewHost(W, H); UnattendedWindowing.ShowWithoutFocus(host);
        using IRenderController renderer = RenderControllerFactory.Create(RenderBackendOption.SilkNetDx11); renderer.Initialize(host.Handle, W, H);
        renderer.SetVSync(false);
        using var terrainRuntime = new RoomTerrainSubsystem(scene.Root, scene.Room, null!);
        MeshDrawCall[] draws = new MeshDrawCall[terrainRuntime.GetMeshDrawCapacity(renderer) + 64];
        MeshHandle wall = renderer.RegisterMesh(WallVertices(16f, 6f, 8f, 32, 12, new Vector4(0.7f, 0.68f, 0.64f, 1f)), WallIndices(32, 12));
        Vector3 eye = new(0, 1.7f, -4f);
        Matrix4x4 view = Matrix4x4.CreateLookAt(eye, new Vector3(0, 1.4f, 8f), Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, W / (float)H, 0.1f, 200f);
        Process process = Process.GetCurrentProcess();
        IntPtr affinity = process.ProcessorAffinity;
        var random = new Random(5);
        void Spray(int count)
        {
            WorldDecals.Reset();
            WorldDecals.SetLimit(count);
            for (int i = 0; i < count; i++)
            {
                double id;
                if (i % 3 == 2)
                {
                    float x = random.NextSingle() * 12 - 6, z = random.NextSingle() * 9 - 1;
                    id = PgslCommands.DecalAdd("Decal stain", x, scene.Terrain.SampleHeight(x, z), z, 0, 1, 0, 0.12 + random.NextDouble() * 0.1, 0);
                }
                else id = PgslCommands.DecalAdd("Decal stain", random.NextSingle() * 14 - 7, 0.3 + random.NextSingle() * 5, 8, 0, 0, -1, 0.12 + random.NextDouble() * 0.1, 0);
                PgslCommands.DecalSetRotation(id, random.Next(360));
            }
        }
        try
        {
            try { process.ProcessorAffinity = (IntPtr)(affinity.ToInt64() & 0xFFFF); } catch (Exception) { }
            (double gpu, double frame, double submit, int drawn, int calls) Run(int count)
            {
                Spray(count);
                double gpuTotal = 0, frameTotal = 0, submitTotal = 0;
                int drawn = 0, calls = 0;
                for (int i = 0; i < WarmUp + Measured; i++)
                {
                    long started = Stopwatch.GetTimestamp();
                    Mesh3DState state = Mesh3DState.Default;
                    state.ShowFloor = false; state.ShowSunVisual = false; state.CameraFarPlane = 200f;
                    FlashLightSuite.ModernLook(ref state);
                    renderer.SetMesh3DState(state); renderer.Set3DFrameActive(true);
                    renderer.SetCamera3D(view, projection);
                    renderer.BeginFrame(); renderer.Clear(0.4f, 0.5f, 0.7f);
                    int n = 0;
                    terrainRuntime.SubmitPreviewMeshes(eye, view * projection, draws, ref n, renderer);
                    for (int d = 0; d < n; d++) renderer.DrawMesh(draws[d]);
                    renderer.DrawMesh(new MeshDrawCall { Mesh = wall, World = Matrix4x4.Identity, Tint = RenderColor.White, Alpha = 1 });
                    long submitStarted = Stopwatch.GetTimestamp();
                    WorldDecals.Submit(renderer, scene.Root);
                    double submit = Stopwatch.GetElapsedTime(submitStarted).TotalMilliseconds;
                    renderer.EndFrame();
                    renderer.Present();
                    if (i < WarmUp) continue;
                    frameTotal += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    submitTotal += submit;
                    gpuTotal += renderer.LastGpuMilliseconds;
                    RenderStats stats = renderer.GetStats();
                    drawn = stats.DecalsDrawn;
                    calls = stats.DecalDrawCalls;
                }
                return (gpuTotal / Measured, frameTotal / Measured, submitTotal / Measured, drawn, calls);
            }

            var gpu = new Dictionary<int, List<double>> { [0] = [], [500] = [], [2000] = [] };
            var frames = new Dictionary<int, List<double>> { [0] = [], [500] = [], [2000] = [] };
            var submits = new Dictionary<int, List<double>> { [0] = [], [500] = [], [2000] = [] };
            int drawn500 = 0, calls500 = 0, drawn2000 = 0;
            for (int round = 0; round < 3; round++)
                foreach (int count in new[] { 0, 500, 2000 })
                {
                    (double g, double f, double s, int drawn, int calls) = Run(count);
                    gpu[count].Add(g); frames[count].Add(f); submits[count].Add(s);
                    if (count == 500) { drawn500 = drawn; calls500 = calls; }
                    if (count == 2000) drawn2000 = drawn;
                }
            string Range(List<double> values) => $"{values.Min():F2}-{values.Max():F2} ms";
            string report = string.Join(Environment.NewLine,
                $"Decals at {W} x {H} on DX11 ({renderer.AdapterName}): a 16 m wall over rolling terrain, GTAO, contact shadows, bloom and volumetric fog on, bullet holes 12-22 cm across with random turns, two thirds on the wall and a third on the ground; {Measured} frames after {WarmUp}, 3 rounds; P-cores.",
                $"GPU per frame: none {Range(gpu[0])}, 500 {Range(gpu[500])}, 2000 {Range(gpu[2000])}.",
                $"Whole frame (submit, draw, present): none {Range(frames[0])}, 500 {Range(frames[500])}, 2000 {Range(frames[2000])}.",
                $"Giving the renderer the decals (WorldDecals.Submit): 500 {Range(submits[500])}, 2000 {Range(submits[2000])}.",
                $"500 decals drawn in {calls500} draw call(s) ({drawn500} drawn); 2000 drawn: {drawn2000}.");
            Console.WriteLine(report);
            Directory.CreateDirectory(ctx.Logs);
            File.WriteAllText(Path.Combine(ctx.Logs, "decal-cost.txt"), report + Environment.NewLine);
            Check(drawn500 == 500 && calls500 == 1 && drawn2000 == 2000, $"Bullet holes were not one draw ({drawn500} in {calls500}; {drawn2000}).");
            Check(gpu[500].Min() - gpu[0].Max() < 3.0, "500 bullet holes cost more than 3 ms of GPU time at 1080p.");
        }
        finally
        {
            try { process.ProcessorAffinity = affinity; } catch (Exception) { }
            renderer.ReleaseMesh(wall);
            WorldDecals.Reset();
        }
    }

    /// <summary>A wall <paramref name="width"/> wide and <paramref name="height"/> tall standing on y = 0 at z, facing -z.</summary>
    private static MeshVertex[] WallVertices(float width, float height, float z, int columns, int rows, Vector4 color)
    {
        var vertices = new MeshVertex[(columns + 1) * (rows + 1)];
        for (int r = 0; r <= rows; r++)
            for (int c = 0; c <= columns; c++)
                vertices[r * (columns + 1) + c] = new MeshVertex
                {
                    // Columns run towards -x (the camera's right) so the grid winds like the floor's.
                    Position = new Vector3(width / 2 - width * c / columns, height * r / rows, z),
                    Normal = -Vector3.UnitZ,
                    Color = color,
                    UV = new Vector2(c / (float)columns, r / (float)rows),
                };
        return vertices;
    }

    /// <summary>Two triangles a cell, wound so the wall's face is towards -z.</summary>
    private static ushort[] WallIndices(int columns, int rows)
    {
        var indices = new ushort[columns * rows * 6];
        int at = 0;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
            {
                ushort a = (ushort)(r * (columns + 1) + c), b = (ushort)(a + 1), up = (ushort)(a + columns + 1), d = (ushort)(up + 1);
                indices[at++] = a; indices[at++] = b; indices[at++] = up;
                indices[at++] = b; indices[at++] = d; indices[at++] = up;
            }
        return indices;
    }

    private static Color Mean(byte[] bgra, int cx, int cy, int half)
    {
        int r = 0, g = 0, b = 0, n = 0;
        for (int y = cy - half; y <= cy + half; y++)
            for (int x = cx - half; x <= cx + half; x++)
            {
                if (x < 0 || y < 0 || x >= Width || y >= Height) continue;
                int i = (y * Width + x) * 4;
                b += bgra[i]; g += bgra[i + 1]; r += bgra[i + 2]; n++;
            }
        return n == 0 ? Color.Black : Color.FromArgb(r / n, g / n, b / n);
    }

    private static void Check(bool value, string message) => HeadlessHarness.Assert(value, message);
}
