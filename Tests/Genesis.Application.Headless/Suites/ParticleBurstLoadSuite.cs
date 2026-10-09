using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using Genesis.Rendering.Core;
using Genesis.Runtime;
using Genesis.Runtime.Particles;
using Genesis.Runtime.Rendering;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// What 300 live one-shot particle bursts cost a frame on each GPU renderer (falling leaves in a
/// jungle: ParticleBurstAt of a one-particle effect that lives several seconds): the game thread's
/// time for the composition update, the particle submission and the frame, and the managed bytes
/// allocated a frame, with all 300 standing and with bursts coming and going every frame.
/// Numbers go to the log and particle-bursts.txt.
/// </summary>
internal static class ParticleBurstLoadSuite
{
    private const int Emitters = 300;
    private const int WarmUpFrames = 60;
    private const int MeasuredFrames = 180;
    private const string Leaf = "Assets/Particles/Leaf.particle.json";

    public static void Run(HeadlessContext ctx)
    {
        string root = Path.Combine(ctx.Workspace, "ParticleBurstLoad");
        WriteProject(root);

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Particles.Bursts.LimitRemovesTheOldest", () =>
        {
            using var scene = new RuntimeScene("Burst limit");
            try
            {
                ParticleBursts.ResetLimit();
                HeadlessHarness.Assert(ParticleBursts.Limit == 256, $"The burst limit should be 256 unless a game sets it ({ParticleBursts.Limit}).");
                ParticleBursts.Limit = 50;
                var made = new List<Entity>();
                for (int i = 0; i < 80; i++) made.Add(ParticleBursts.Play(scene.World, Leaf, new Vector3(i, 0, 0)));
                scene.World.FlushDeferred();
                HeadlessHarness.Assert(made.Take(30).All(entity => !scene.World.IsAlive(entity)) && made.Skip(30).All(scene.World.IsAlive),
                    "Past the limit, the oldest bursts (and only they) should have been removed.");
                HeadlessHarness.Assert(ParticleBursts.LiveCount(scene.World) == 50, $"50 bursts should be alive ({ParticleBursts.LiveCount(scene.World)}).");
                ParticleBursts.Limit = 0;
                for (int i = 0; i < 20; i++) ParticleBursts.Play(scene.World, Leaf, Vector3.Zero);
                scene.World.FlushDeferred();
                HeadlessHarness.Assert(ParticleBursts.LiveCount(scene.World) == 70, $"With no limit nothing should be removed ({ParticleBursts.LiveCount(scene.World)}).");
            }
            finally
            {
                ParticleBursts.ResetLimit();
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Runtime.Particles.Bursts.BatchedPlayAsBurstsWithoutGpuParticles", () =>
        {
            using var scene = new RuntimeScene("Batched fallback");
            using var composition = new ObjectCompositionSubsystem(root);
            for (int i = 0; i < 3; i++)
                HeadlessHarness.Assert(ParticleBursts.PlayBatched(scene.World, Leaf, new Vector3(i, 2, 0)), "A batched burst was refused.");
            HeadlessHarness.Assert(!ParticleBursts.PlayBatched(scene.World, "", Vector3.Zero), "A batched burst with no effect was accepted.");
            scene.GameTime.Advance(1f / 60f);
            composition.Update(scene, scene.GameTime);
            HeadlessHarness.Assert(composition.ParticleEmitterCount == 3 && ParticleBursts.LiveCount(scene.World) == 3 && composition.BatchedBurstEmitterCount == 0,
                $"With no GPU renderer drawing yet, batched bursts should play as ordinary bursts ({composition.ParticleEmitterCount} emitters, {ParticleBursts.LiveCount(scene.World)} bursts).");
        });

        var lines = new List<string>();
        foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All.Where(item => item.Backend != RenderBackendOption.Software))
            HeadlessHarness.RunCase(ctx.Report, "Runtime.Particles.BurstLoad." + backend.ShortName, () => lines.Add(Measure(root, backend)));
        Directory.CreateDirectory(ctx.Captures);
        File.WriteAllLines(Path.Combine(ctx.Captures, "particle-bursts.txt"), lines);
        foreach (string line in lines) Console.WriteLine(line);
    }

    private readonly record struct Sample(double MedianMs, double P90Ms, double BytesPerFrame);

    private static string Measure(string root, RenderBackendDescriptor backend)
    {
        using Form host = UnattendedWindowing.NewHost(640, 360); UnattendedWindowing.ShowWithoutFocus(host);
        using IRenderController renderer = RenderControllerFactory.Create(backend.Backend); renderer.Initialize(host.Handle, 640, 360);
        renderer.SetVSync(false);
        using var scene = new RuntimeScene("Leaves");
        var composition = new ObjectCompositionSubsystem(root);
        var draws = new MeshDrawCall[64];
        var random = new Random(7);
        var live = new Queue<Entity>();
        Process process = Process.GetCurrentProcess();
        IntPtr affinity = process.ProcessorAffinity;
        try
        {
            // P-cores only on the development PC (logical CPUs 0-15); harmless elsewhere.
            try { process.ProcessorAffinity = (IntPtr)(affinity.ToInt64() & 0xFFFF); } catch (Exception) { }

            Vector3 eye = new(0, 12, -30);
            Matrix4x4 view = Matrix4x4.CreateLookAt(eye, new Vector3(0, 8, 10), Vector3.UnitY);
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 16f / 9f, .1f, 500);
            void Frame()
            {
                scene.GameTime.Advance(1f / 60f);
                composition.Update(scene, scene.GameTime);
                scene.World.FlushDeferred();
                Mesh3DState state = Mesh3DState.Default;
                state.ShowFloor = false; state.ShowSunVisual = false;
                renderer.SetMesh3DState(state); renderer.Set3DFrameActive(true);
                renderer.SetCamera3D(view, projection);
                renderer.BeginFrame(); renderer.Clear(.2f, .3f, .4f);
                int count = 0;
                composition.SubmitMeshes(scene, draws, ref count, renderer);
                renderer.EndFrame();
                renderer.Present();
            }

            Vector3 Somewhere() => new(random.NextSingle() * 40 - 20, 6 + random.NextSingle() * 10, random.NextSingle() * 40 - 10);
            Entity Spawn()
            {
                Entity burst = ParticleBursts.Play(scene.World, Leaf, Somewhere());
                HeadlessHarness.Assert(!burst.IsNull, "A leaf burst could not be played.");
                live.Enqueue(burst);
                return burst;
            }

            Sample Run(Action? each)
            {
                for (int i = 0; i < WarmUpFrames; i++) { each?.Invoke(); Frame(); }
                var times = new double[MeasuredFrames];
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < MeasuredFrames; i++)
                {
                    long started = Stopwatch.GetTimestamp();
                    each?.Invoke();
                    Frame();
                    times[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                Array.Sort(times);
                return new Sample(times[times.Length / 2], times[(int)(times.Length * 0.9)], bytes / (double)MeasuredFrames);
            }

            // The frame with nothing in it, for comparison.
            Sample empty = Run(null);

            // The engine's burst limit (default 256) would remove the oldest of 300: lift it here.
            ParticleBursts.Limit = 0;
            var spawnClock = Stopwatch.StartNew();
            long spawnBytes = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Emitters; i++) Spawn();
            Frame();
            double spawnMs = spawnClock.Elapsed.TotalMilliseconds;
            spawnBytes = GC.GetAllocatedBytesForCurrentThread() - spawnBytes;
            HeadlessHarness.Assert(composition.ParticleEmitterCount == Emitters,
                $"{backend.ShortName}: {Emitters} bursts should be {Emitters} emitters ({composition.ParticleEmitterCount}).");

            // All 300 standing (their particles live for several seconds).
            Sample standing = Run(null);

            // Bursts coming and going: one new leaf a frame, the oldest taken away.
            Sample churn = Run(() =>
            {
                scene.World.DestroyEntity(live.Dequeue());
                scene.World.FlushDeferred();
                Spawn();
            });

            int emittersAfterChurn = composition.ParticleEmitterCount;

            // The same leaves as batched bursts: every one shares the effect's one emitter.
            while (live.Count > 0) scene.World.DestroyEntity(live.Dequeue());
            scene.World.FlushDeferred();
            Frame();
            for (int i = 0; i < Emitters; i++)
                HeadlessHarness.Assert(ParticleBursts.PlayBatched(scene.World, Leaf, Somewhere()), "A batched leaf burst was refused.");
            // Three quarters of a second on (the live count is read back twice a second); a leaf
            // lives from almost nothing to 12 s, so a few have already gone.
            for (int i = 0; i < 45; i++) Frame();
            int batchedAlive = composition.ActiveParticleCount;
            Sample batched = Run(null);
            int batchedEmitters = composition.BatchedBurstEmitterCount, batchedEntities = composition.ParticleEmitterCount;
            Sample batchedChurn = Run(() => ParticleBursts.PlayBatched(scene.World, Leaf, Somewhere()));

            string line = $"{backend.ShortName}: empty frame {empty.MedianMs:F2} ms (p90 {empty.P90Ms:F2}, {empty.BytesPerFrame / 1024:F1} KB/frame); "
                + $"{Emitters} bursts made in {spawnMs:F0} ms ({spawnBytes / 1024.0 / 1024.0:F1} MB); "
                + $"{Emitters} standing {standing.MedianMs:F2} ms (p90 {standing.P90Ms:F2}, {standing.BytesPerFrame / 1024:F1} KB/frame); "
                + $"one in, one out each frame {churn.MedianMs:F2} ms (p90 {churn.P90Ms:F2}, {churn.BytesPerFrame / 1024:F1} KB/frame); "
                + $"{Emitters} batched {batched.MedianMs:F2} ms (p90 {batched.P90Ms:F2}, {batched.BytesPerFrame / 1024:F1} KB/frame, {batchedEmitters} emitter, {batchedAlive} alive); "
                + $"one more batched each frame {batchedChurn.MedianMs:F2} ms (p90 {batchedChurn.P90Ms:F2}, {batchedChurn.BytesPerFrame / 1024:F1} KB/frame)";
            Console.WriteLine(line);
            HeadlessHarness.Assert(emittersAfterChurn == Emitters,
                $"{backend.ShortName}: the churn should keep {Emitters} emitters ({emittersAfterChurn}). " + line);
            HeadlessHarness.Assert(batchedEmitters == 1 && batchedEntities == 0,
                $"{backend.ShortName}: batched bursts of one effect should share one emitter and make no entities ({batchedEmitters} emitters, {batchedEntities} entities). " + line);
            HeadlessHarness.Assert(batchedAlive >= Emitters * 8 / 10 && batchedAlive <= Emitters,
                $"{backend.ShortName}: the shared emitter should hold every batched leaf ({batchedAlive} of {Emitters} alive). " + line);
            // A frame of 300 standing emitters allocated about 340 KB before (two definitions, a
            // draw and a diagnostics string for each emitter, every frame). The Vulkan device
            // itself allocates about half a kilobyte for each emitter's dispatches (its descriptor
            // writes, 9 Oct 2026), so it is held only to what it did before.
            double standingBudget = backend.Backend == RenderBackendOption.Vulkan ? 256 * 1024 : 24 * 1024;
            HeadlessHarness.Assert(standing.BytesPerFrame < standingBudget,
                $"{backend.ShortName}: {Emitters} standing bursts allocate too much a frame. " + line);
            HeadlessHarness.Assert(batched.BytesPerFrame < 8 * 1024,
                $"{backend.ShortName}: {Emitters} batched bursts allocate too much a frame. " + line);
            HeadlessHarness.Assert(batched.MedianMs < standing.MedianMs,
                $"{backend.ShortName}: batched bursts should cost less than separate emitters. " + line);
            return line;
        }
        finally
        {
            ParticleBursts.ResetLimit();
            try { process.ProcessorAffinity = affinity; } catch (Exception) { }
            composition.Dispose();
        }
    }

    // A falling leaf like GenesisCraft's: one particle a burst, alive for several seconds, a
    // five-frame flipbook texture.
    private static void WriteProject(string root)
    {
        string folder = Path.Combine(root, "Assets", "Particles");
        Directory.CreateDirectory(Path.Combine(folder, "Tex"));
        using (var strip = new Bitmap(80, 16, PixelFormat.Format32bppArgb))
        {
            for (int frame = 0; frame < 5; frame++)
                for (int y = 2; y < 14; y++)
                    for (int x = 2 + frame; x < 14; x++)
                        strip.SetPixel(frame * 16 + x, y, Color.FromArgb(255, 90 + frame * 20, 170, 50));
            strip.Save(Path.Combine(folder, "Tex", "leaf.png"), ImageFormat.Png);
        }
        File.WriteAllText(Path.Combine(folder, "Leaf.particle.json"), """
            {
                "effectName": "Leaf",
                "maxParticles": 60,
                "emitRate": 0,
                "burstCount": 1,
                "loop": false,
                "shape": "Sphere",
                "spreadDegrees": 90,
                "emitRadius": 0.3,
                "speed": 0.25,
                "speedVariance": 0.15,
                "gravity": -0.9,
                "drag": 1.6,
                "lifetime": 6,
                "lifetimeVariance": 2,
                "startSize": 0.16,
                "endSize": 0.16,
                "emissive": 1,
                "blendMode": "Alpha",
                "startColor": { "r": 0.37, "g": 0.66, "b": 0.2, "a": 1 },
                "endColor": { "r": 0.37, "g": 0.66, "b": 0.2, "a": 1 },
                "colorJitter": 0.06,
                "flipbookColumns": 5,
                "flipbookRandomStart": true,
                "useFlipbook": true,
                "flipbookRows": 1,
                "flipbookFps": 0.001,
                "texturePath": "Assets/Particles/Tex/leaf.png"
            }
            """);
    }
}
