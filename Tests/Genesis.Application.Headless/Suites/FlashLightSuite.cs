using System.Diagnostics;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Core;
using Genesis.Rendering.Lights;
using Genesis.Rendering.Primitives;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Flash lights (PGSL LightFlash): a short point light that is bright at once, fades to nothing over
/// its life and removes itself; lit on every renderer; never given a local shadow slot; and cheap in
/// dozens at 1080p on DX11.
/// </summary>
internal static class FlashLightSuite
{
    private const int Width = 480, Height = 320;

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "FlashLights");

        HeadlessHarness.RunCase(ctx.Report, "Runtime.FlashLights.FadeOverTheirLifeAndRemoveThemselves", () =>
        {
            FlashLights.Reset();
            try
            {
                double id = PgslCommands.LightFlash(1, 2, 3, 255, 200, 120, 4, 6, 0.5);
                Check(id > 0 && FlashLights.Count == 1 && PgslCommands.LightFlashExists(id), "LightFlash did not light a flash.");
                Check(FlashLights.Brightness(0f, 0.5f) == 1f, "A new flash is not at full brightness.");
                Check(MathF.Abs(FlashLights.Brightness(0.25f, 0.5f) - 0.25f) < 1e-5f, "A flash half-way through its life is not a quarter as bright.");
                FlashLights.Advance(0.3f);
                Check(FlashLights.Count == 1, "A flash went out before the end of its life.");
                FlashLights.Advance(0.25f);
                Check(FlashLights.Count == 0 && !PgslCommands.LightFlashExists(id), "A flash outlived its life.");

                // Nothing to light, numbers that are not numbers: nothing added, nothing thrown.
                Check(PgslCommands.LightFlash(0, 0, 0, 255, 255, 255, 0, 5, 1) == 0, "A flash of no brightness was added.");
                Check(PgslCommands.LightFlash(0, 0, 0, 255, 255, 255, 1, 5, 0) == 0, "A flash of no life was added.");
                Check(PgslCommands.LightFlash(double.NaN, 0, 0, 255, 255, 255, 1, 5, 1) == 0, "A flash at NaN was added.");
                Check(!PgslCommands.LightFlashMove(12345, 0, 0, 0) && !PgslCommands.LightFlashRemove(-1), "A missing flash was moved or removed.");

                double moving = PgslCommands.LightFlash(0, 0, 0, 255, 255, 255, 1, 5, 2);
                Check(PgslCommands.LightFlashMove(moving, 4, 5, 6), "A live flash could not be moved.");
                Check(PgslCommands.LightFlashRemove(moving) && FlashLights.Count == 0, "LightFlashRemove left the flash lit.");

                // Past the limit a new flash replaces the one nearest its end.
                for (int i = 0; i < FlashLights.MaxFlashes; i++) FlashLights.Add(Vector3.Zero, Vector3.One, 1f, 2f, 1f + i * 0.01f);
                FlashLights.Advance(0.05f);
                int newest = FlashLights.Add(Vector3.Zero, Vector3.One, 1f, 2f, 3f);
                Check(newest > 0 && FlashLights.Count == FlashLights.MaxFlashes && FlashLights.Exists(newest), "A flash past the limit was not taken.");
                FlashLights.Advance(1.0f);
                Check(FlashLights.Count < FlashLights.MaxFlashes && FlashLights.Exists(newest), "The newest flash was the one replaced.");
                PgslCommands.LightFlashClear();
                Check(PgslCommands.LightFlashCount() == 0, "LightFlashClear left flashes lit.");
            }
            finally { FlashLights.Reset(); }
        });

        HeadlessHarness.RunCase(ctx.Report, "Render.FlashLights.NeverTakeALocalShadowSlot", () =>
        {
            Vector3 camera = new(0, 1.6f, 0);
            ClusterPointLightGpu lamp = new()
            {
                PosRadius = new Vector4(6, 3, 0, 10), ColorIntensity = new Vector4(1, 0.9f, 0.7f, 1), FalloffPad = new Vector4(2, 0, 0, 0),
            };
            // Far brighter and right at the camera: an ordinary light this strong would win the slot.
            ClusterPointLightGpu flash = new()
            {
                PosRadius = new Vector4(0.3f, 1.5f, 0.5f, 8), ColorIntensity = new Vector4(1, 0.8f, 0.5f, 20), FalloffPad = new Vector4(2, 0, 0, 0),
                SpotDirCos = new Vector4(0, 0, 0, OmniShadowMath.FlashLightMarker),
            };
            ClusterPointLightGpu ordinary = flash;
            ordinary.SpotDirCos = Vector4.Zero;
            Check(OmniShadowMath.IsFlashLight(flash) && !OmniShadowMath.IsFlashLight(ordinary) && !OmniShadowMath.IsFlashLight(lamp),
                "The flash marker is not recognised.");

            Span<int> assignment = stackalloc int[1];
            Span<Vector4> none = stackalloc Vector4[1];
            OmniShadowMath.AssignSlots(new[] { lamp, ordinary }, 1, camera, none, assignment);
            Check(assignment[0] == 1, "The comparison is wrong: a strong ordinary light at the camera should win the slot.");
            OmniShadowMath.AssignSlots(new[] { lamp, flash }, 1, camera, none, assignment);
            Check(assignment[0] == 0, $"The flash took the lamp's shadow slot (slot holds light {assignment[0]}).");
            OmniShadowMath.AssignSlots(new[] { flash }, 1, camera, none, assignment);
            Check(assignment[0] == -1, "A flash alone was given a shadow slot.");
            // A flash where last frame's shadowed lamp stood does not inherit its slot.
            Span<Vector4> held = stackalloc Vector4[] { new Vector4(0.3f, 1.5f, 0.5f, 8) };
            OmniShadowMath.AssignSlots(new[] { flash, lamp }, 1, camera, held, assignment);
            Check(assignment[0] == 1, "A flash inherited a shadow slot held at its position.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Render.FlashLights.BrightenThenFadeOnEveryBackend", () => BrightenThenFade(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Render.FlashLights.DozensAt1080pOnDx11", () => Cost(ctx));
    }

    private static void BrightenThenFade(HeadlessContext ctx)
    {
        List<string> failures = [];
        List<string> readings = [];
        foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
        {
            FlashLights.Reset();
            using Form host = UnattendedWindowing.NewHost(Width, Height); UnattendedWindowing.ShowWithoutFocus(host);
            using IRenderController renderer = RenderControllerFactory.Create(backend.Backend); renderer.Initialize(host.Handle, Width, Height);
            // A floor of small squares: the software renderer lights at the corners of triangles.
            MeshHandle floor = renderer.RegisterMesh(GridVertices(20f, 40, new Vector4(0.8f, 0.8f, 0.8f, 1f)), GridIndices(40));
            try
            {
                string name = backend.ShortName.ToLowerInvariant();
                (double centre, int lights) Frame(string label)
                {
                    Mesh3DState state = Mesh3DState.Default;
                    state.ShowFloor = false; state.ShowSunVisual = false; state.FogEnabled = false; state.ShadowsEnabled = false;
                    state.LightingEnabled = true; state.SunIntensity = 0f;
                    state.AmbientColor = new Vector3(0.04f); state.AmbientGroundColor = new Vector3(0.04f);
                    state.CameraFarPlane = 100f;
                    renderer.SetMesh3DState(state); renderer.Set3DFrameActive(true);
                    renderer.SetCamera3D(Matrix4x4.CreateLookAt(new Vector3(0, 7, -7), Vector3.Zero, Vector3.UnitY),
                        Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, Width / (float)Height, 0.1f, 100f));
                    renderer.BeginFrame(); renderer.Clear(0f, 0f, 0f);
                    FlashLights.Submit(renderer);
                    renderer.DrawMesh(new MeshDrawCall { Mesh = floor, World = Matrix4x4.Identity, Tint = RenderColor.White, Alpha = 1, Flags = MeshDrawFlags.NoShadow });
                    renderer.EndFrame();
                    int used = renderer.GetStats().LightsUsed;
                    HeadlessHarness.Assert(renderer.TryReadSubmittedFramePixels(out int width, out int height, out byte[] pixels)
                        && width == Width && height == Height, backend.ShortName + ": no frame could be read back.");
                    Save(ctx, pixels, width, height, "flash-light-" + name + "-" + label);
                    renderer.Present();
                    return (Luminance(pixels, width, Width / 2, Height / 2, 6), used);
                }

                (double before, _) = Frame("before");
                double id = PgslCommands.LightFlash(0, 1, 0, 255, 210, 150, 3, 6, 1.0);
                (double peak, int peakLights) = Frame("peak");
                FlashLights.Advance(0.5f);
                (double half, _) = Frame("half");
                FlashLights.Advance(0.6f);
                (double after, int afterLights) = Frame("after");
                readings.Add($"{backend.ShortName}: before {before:F0}, peak {peak:F0} ({peakLights} light), half-life {half:F0}, after {after:F0} ({afterLights} lights)");

                if (id <= 0) failures.Add(backend.ShortName + ": LightFlash returned no flash");
                if (peakLights != 1) failures.Add($"{backend.ShortName}: {peakLights} lights in the flash frame");
                if (peak < before + 60) failures.Add($"{backend.ShortName}: the flash did not light the floor ({before:F0} -> {peak:F0})");
                if (!(half < peak - 15 && half > before + 4)) failures.Add($"{backend.ShortName}: half-way through its life the flash is not dimmer but still lit ({before:F0}/{half:F0}/{peak:F0})");
                if (Math.Abs(after - before) > 6 || afterLights != 0 || FlashLights.Count != 0)
                    failures.Add($"{backend.ShortName}: the burnt-out flash still lights ({after:F0} against {before:F0}, {afterLights} lights)");
            }
            finally
            {
                renderer.ReleaseMesh(floor);
                FlashLights.Reset();
            }
        }
        File.WriteAllLines(Path.Combine(ctx.Logs, "flash-light-readings.txt"), readings);
        Console.WriteLine("Flash light readings: " + string.Join(" | ", readings));
        HeadlessHarness.Assert(failures.Count == 0, string.Join(" | ", failures));
    }

    /// <summary>
    /// A shadowed, sunlit yard with crates at 1920 x 1080 on DX11: no flashes, 48 flashes, and the
    /// same 48 as ordinary point lights (which compete for the shadow slot), three rounds each.
    /// </summary>
    private static void Cost(HeadlessContext ctx)
    {
        const int W = 1920, H = 1080, Count = 48, WarmUp = 45, Measured = 120;
        FlashLights.Reset();
        using Form host = UnattendedWindowing.NewHost(W, H); UnattendedWindowing.ShowWithoutFocus(host);
        using IRenderController renderer = RenderControllerFactory.Create(RenderBackendOption.SilkNetDx11); renderer.Initialize(host.Handle, W, H);
        renderer.SetVSync(false);
        MeshHandle floor = renderer.RegisterMesh(GridVertices(80f, 40, new Vector4(0.55f, 0.55f, 0.5f, 1f)), GridIndices(40));
        MeshHandle cube = renderer.GetBuiltinMesh(BuiltinMeshKind.Cube);
        Process process = Process.GetCurrentProcess();
        IntPtr affinity = process.ProcessorAffinity;
        var random = new Random(11);
        var crates = new Matrix4x4[24];
        for (int i = 0; i < crates.Length; i++)
            crates[i] = Matrix4x4.CreateScale(1.2f + random.NextSingle()) * Matrix4x4.CreateTranslation(random.NextSingle() * 24 - 12, 0.8f, 4 + random.NextSingle() * 22);
        var spots = new Vector3[Count];
        for (int i = 0; i < Count; i++)
            spots[i] = new Vector3(random.NextSingle() * 20 - 10, 0.6f + random.NextSingle() * 1.5f, 3 + random.NextSingle() * 20);
        try
        {
            try { process.ProcessorAffinity = (IntPtr)(affinity.ToInt64() & 0xFFFF); } catch (Exception) { }
            (double gpu, double frame, int lights, int shadowLights) Run(int mode)
            {
                double gpuTotal = 0, frameTotal = 0;
                int lightsSeen = 0, shadowSeen = 0;
                for (int i = 0; i < WarmUp + Measured; i++)
                {
                    long started = Stopwatch.GetTimestamp();
                    Mesh3DState state = Mesh3DState.Default;
                    state.ShowFloor = false; state.ShowSunVisual = false; state.CameraFarPlane = 200f;
                    renderer.SetMesh3DState(state); renderer.Set3DFrameActive(true);
                    renderer.SetCamera3D(Matrix4x4.CreateLookAt(new Vector3(0, 1.7f, -4), new Vector3(0, 1.2f, 10), Vector3.UnitY),
                        Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, W / (float)H, 0.1f, 200f));
                    renderer.BeginFrame(); renderer.Clear(0.4f, 0.5f, 0.7f);
                    // Flashes come and go as in a fire fight: all 48 re-fired every 9 frames, each
                    // living 0.15 s. Mode 2 adds the same lights, as bright, as ordinary point lights.
                    if (mode > 0)
                    {
                        if (i % 9 == 0)
                        {
                            FlashLights.Reset();
                            for (int f = 0; f < Count; f++) FlashLights.Add(spots[f], new Vector3(1f, 0.75f, 0.45f), 4f, 6f, 0.15f);
                        }
                        if (mode == 1) FlashLights.Submit(renderer);
                        else
                            for (int f = 0; f < Count; f++)
                                renderer.AddPointLight(spots[f], new Vector3(1f, 0.75f, 0.45f), 6f,
                                    4f * FlashLights.Brightness((i % 9) / 60f, 0.15f));
                        FlashLights.Advance(1f / 60f);
                    }
                    renderer.DrawMesh(new MeshDrawCall { Mesh = floor, World = Matrix4x4.Identity, Tint = RenderColor.White, Alpha = 1, Flags = MeshDrawFlags.NoShadow });
                    foreach (Matrix4x4 crate in crates)
                        renderer.DrawMesh(new MeshDrawCall { Mesh = cube, World = crate, Tint = new RenderColor(0.6f, 0.45f, 0.3f, 1f), Alpha = 1 });
                    renderer.EndFrame();
                    renderer.Present();
                    if (i < WarmUp) continue;
                    frameTotal += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    gpuTotal += renderer.LastGpuMilliseconds;
                    RenderStats stats = renderer.GetStats();
                    lightsSeen = Math.Max(lightsSeen, stats.LightsUsed);
                    shadowSeen = Math.Max(shadowSeen, stats.LocalShadowLights);
                }
                FlashLights.Reset();
                return (gpuTotal / Measured, frameTotal / Measured, lightsSeen, shadowSeen);
            }

            var none = new List<double>(); var flashes = new List<double>(); var ordinary = new List<double>();
            var noneFrame = new List<double>(); var flashFrame = new List<double>(); var ordinaryFrame = new List<double>();
            int flashLights = 0, flashShadows = 0, ordinaryShadows = 0;
            for (int round = 0; round < 3; round++)
            {
                (double g0, double f0, _, _) = Run(0); none.Add(g0); noneFrame.Add(f0);
                (double g1, double f1, int l1, int s1) = Run(1); flashes.Add(g1); flashFrame.Add(f1); flashLights = l1; flashShadows = s1;
                (double g2, double f2, _, int s2) = Run(2); ordinary.Add(g2); ordinaryFrame.Add(f2); ordinaryShadows = s2;
            }
            string Range(List<double> values) => $"{values.Min():F2}-{values.Max():F2} ms";
            string report = string.Join(Environment.NewLine,
                $"Flash lights at {W} x {H} on DX11 ({renderer.AdapterName}): a sunlit, shadowed yard with 24 crates; {Count} lights radius 6 re-fired every 9 frames (0.15 s life); {Measured} frames measured after {WarmUp}, 3 rounds; P-cores.",
                $"GPU per frame: none {Range(none)}, {Count} flashes {Range(flashes)}, the same {Count} as ordinary point lights {Range(ordinary)}.",
                $"Whole frame (submit, draw, present): none {Range(noneFrame)}, flashes {Range(flashFrame)}, ordinary lights {Range(ordinaryFrame)}.",
                $"Lights in the frame with flashes: {flashLights}; local shadow lights with flashes {flashShadows}, with ordinary lights {ordinaryShadows}.");
            Console.WriteLine(report);
            Directory.CreateDirectory(ctx.Logs);
            File.WriteAllText(Path.Combine(ctx.Logs, "flash-light-cost.txt"), report + Environment.NewLine);
            Check(flashLights >= Count, $"Only {flashLights} of the {Count} flashes reached the frame.");
            Check(flashShadows == 0, "Flash lights were given a local shadow slot.");
            Check(flashes.Min() - none.Max() < 4.0, $"{Count} flash lights cost more than 4 ms of GPU time at 1080p.");
        }
        finally
        {
            try { process.ProcessorAffinity = affinity; } catch (Exception) { }
            renderer.ReleaseMesh(floor);
            FlashLights.Reset();
        }
    }

    internal static MeshVertex[] GridVertices(float size, int cells, Vector4 color)
    {
        var vertices = new MeshVertex[(cells + 1) * (cells + 1)];
        for (int z = 0; z <= cells; z++)
            for (int x = 0; x <= cells; x++)
                vertices[z * (cells + 1) + x] = new MeshVertex
                {
                    Position = new Vector3(-size / 2 + size * x / cells, 0, -size / 2 + size * z / cells),
                    Normal = Vector3.UnitY,
                    Color = color,
                    UV = new Vector2(x / (float)cells, z / (float)cells),
                };
        return vertices;
    }

    internal static ushort[] GridIndices(int cells)
    {
        var indices = new ushort[cells * cells * 6];
        int at = 0;
        for (int z = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
            {
                ushort a = (ushort)(z * (cells + 1) + x), b = (ushort)(a + 1), c = (ushort)(a + cells + 1), d = (ushort)(c + 1);
                // Clockwise seen from above (+Y), the engine's front face.
                indices[at++] = a; indices[at++] = c; indices[at++] = b;
                indices[at++] = b; indices[at++] = c; indices[at++] = d;
            }
        return indices;
    }

    /// <summary>Mean luminance (0-255) of a square of BGRA pixels.</summary>
    internal static double Luminance(byte[] bgra, int width, int cx, int cy, int half)
    {
        double sum = 0; int count = 0;
        for (int y = cy - half; y <= cy + half; y++)
            for (int x = cx - half; x <= cx + half; x++)
            {
                int i = (y * width + x) * 4;
                if (i < 0 || i + 3 >= bgra.Length) continue;
                sum += 0.114 * bgra[i] + 0.587 * bgra[i + 1] + 0.299 * bgra[i + 2];
                count++;
            }
        return count == 0 ? 0 : sum / count;
    }

    internal static void Save(HeadlessContext ctx, byte[] bgra, int width, int height, string name)
    {
        using Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try { Marshal.Copy(bgra, 0, data.Scan0, Math.Min(bgra.Length, width * height * 4)); } finally { bitmap.UnlockBits(data); }
        bitmap.Save(Path.Combine(ctx.Captures, name + ".png"));
        ctx.Report.Images.Add(ImageResult.From(name, name + ".png", VisualCapture.Measure(bitmap)));
    }

    private static void Check(bool value, string message) => HeadlessHarness.Assert(value, message);
}
