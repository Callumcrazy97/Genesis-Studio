using System.Collections;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Genesis.Rendering.Core;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Streaming chunks through script meshes for a long time (PGSL MeshCreate, build, draw,
/// MeshDestroy, as a game streams land round its player) on every renderer: thousands of
/// chunk-sized meshes come and go, and the renderer's live buffers, the managed heap and the
/// process's private memory must level off rather than climb. A mesh destroyed while its rebuild
/// still waits for an upload turn must free its old GPU mesh too. Numbers go to mesh-soak.txt.
/// </summary>
internal static class ScriptMeshSoakSuite
{
    private const int Rounds = 72;
    private const int MeshesPerRound = 32;
    private const int QuadsPerMesh = 3000; // 12,000 vertices, about a busy voxel chunk

    public static void Run(HeadlessContext ctx)
    {
        var lines = new List<string>();
        foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
            HeadlessHarness.RunCase(ctx.Report, "Runtime.ScriptMeshes.StreamingSoak." + backend.ShortName, () => lines.Add(Soak(backend)));
        Directory.CreateDirectory(ctx.Captures);
        File.WriteAllLines(Path.Combine(ctx.Captures, "mesh-soak.txt"), lines);
        foreach (string line in lines) Console.WriteLine(line);
    }

    private static string Soak(RenderBackendDescriptor backend)
    {
        using Form host = UnattendedWindowing.NewHost(320, 240); UnattendedWindowing.ShowWithoutFocus(host);
        using IRenderController renderer = RenderControllerFactory.Create(backend.Backend); renderer.Initialize(host.Handle, 320, 240);
        renderer.SetVSync(false);
        ScriptMeshes.Reset();
        bool software = backend.Backend == RenderBackendOption.Software;
        // The software rasteriser draws every triangle on the CPU: fewer, smaller meshes there.
        int perRound = software ? 8 : MeshesPerRound;
        int quads = software ? 600 : QuadsPerMesh;
        var live = new Queue<int>();
        var lines = new List<string>();
        try
        {
            void Frame(IEnumerable<int> draw)
            {
                Mesh3DState state = Mesh3DState.Default;
                state.ShowFloor = false; state.ShowSunVisual = false;
                renderer.SetMesh3DState(state); renderer.Set3DFrameActive(true);
                renderer.SetCamera3D(Matrix4x4.CreateLookAt(new Vector3(0, 40, -60), Vector3.Zero, Vector3.UnitY),
                    Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 4f / 3f, .1f, 1000));
                renderer.BeginFrame(); renderer.Clear(.1f, .1f, .1f);
                ScriptMeshes.BeginFrame();
                int n = 0;
                foreach (int id in draw)
                {
                    MeshHandle handle = ScriptMeshes.Resolve(id, renderer);
                    if (!handle.IsValid) continue;
                    renderer.DrawMesh(new MeshDrawCall
                    {
                        Mesh = handle, World = Matrix4x4.CreateTranslation((n % 8) * 18 - 70, 0, (n / 8) * 18),
                        Tint = new RenderColor(1, 1, 1, 1), Alpha = 1,
                    });
                    n++;
                }
                renderer.EndFrame();
                renderer.Present();
            }

            // A round: a new ring of chunks is built and drawn, the ring before it is let go.
            void Round(int round)
            {
                var made = new List<int>(perRound);
                for (int m = 0; m < perRound; m++)
                {
                    int id = ScriptMeshes.Create();
                    Build(id, round * perRound + m, quads);
                    made.Add(id);
                }
                ScriptMeshes.UploadBudgetMilliseconds = 0;
                Frame(made.Concat(live));
                while (live.Count > 0) ScriptMeshes.Destroy(live.Dequeue());
                foreach (int id in made) live.Enqueue(id);
            }

            int baseBuffers = LiveBuffers(renderer);
            for (int round = 0; round < 6; round++) Round(round); // warm up
            Settle(renderer);
            int warmBuffers = LiveBuffers(renderer);
            long warmManaged = GC.GetTotalMemory(true);
            long warmPrivate = PrivateBytes();
            int warmPages = FieldCount(renderer, "_uploadPages");

            long allocated = GC.GetTotalAllocatedBytes(precise: true);
            var clock = Stopwatch.StartNew();
            int checkpoints = 0;
            long peakPrivate = warmPrivate;
            for (int round = 6; round < Rounds; round++)
            {
                Round(round);
                if ((round + 1) % 12 == 0)
                {
                    Settle(renderer);
                    checkpoints++;
                    long managed = GC.GetTotalMemory(true);
                    long privateBytes = PrivateBytes();
                    peakPrivate = Math.Max(peakPrivate, privateBytes);
                    lines.Add($"  round {round + 1}: live buffers {LiveBuffers(renderer)}, managed {managed / 1048576.0:F0} MB, private {privateBytes / 1048576.0:F0} MB, upload pages {FieldCount(renderer, "_uploadPages")}");
                }
            }
            double seconds = clock.Elapsed.TotalSeconds;
            long allocatedPerRound = (GC.GetTotalAllocatedBytes(precise: true) - allocated) / (Rounds - 6);

            // A mesh changed and destroyed before its rebuild's upload turn frees its old GPU mesh.
            ScriptMeshes.UploadBudgetMilliseconds = ScriptMeshes.DefaultUploadBudgetMilliseconds;
            int[] pending = live.ToArray();
            foreach (int id in pending) { ScriptMeshes.Clear(id); Build(id, id, quads); }
            Frame(Array.Empty<int>()); // nothing resolved: every rebuild still waits
            foreach (int id in pending)
                HeadlessHarness.Assert(!ScriptMeshes.IsUploaded(id), $"{backend.ShortName}: a rebuilt mesh claimed to be uploaded before its turn.");
            while (live.Count > 0) ScriptMeshes.Destroy(live.Dequeue());
            Settle(renderer);
            int endBuffers = LiveBuffers(renderer);
            long endManaged = GC.GetTotalMemory(true);
            long endPrivate = PrivateBytes();

            string summary = $"{backend.ShortName}: {Rounds * perRound} meshes of {quads * 4:N0} vertices made, drawn and destroyed in {seconds:F1} s; "
                + $"{allocatedPerRound / 1048576.0:F1} MB allocated a round of {perRound}; "
                + $"live buffers {baseBuffers} at start, {warmBuffers} warm, {endBuffers} after every mesh was destroyed; "
                + $"managed {warmManaged / 1048576.0:F0} -> {endManaged / 1048576.0:F0} MB; private {warmPrivate / 1048576.0:F0} -> {endPrivate / 1048576.0:F0} MB (peak {peakPrivate / 1048576.0:F0}); "
                + $"upload pages {warmPages} -> {FieldCount(renderer, "_uploadPages")}";
            lines.Insert(0, summary);
            Console.WriteLine(string.Join(Environment.NewLine, lines));

            if (baseBuffers >= 0)
                HeadlessHarness.Assert(endBuffers <= baseBuffers + 8,
                    $"{backend.ShortName}: destroyed script meshes left GPU buffers behind ({baseBuffers} before, {endBuffers} after). " + summary);
            HeadlessHarness.Assert(endManaged - warmManaged < 64L * 1048576,
                $"{backend.ShortName}: the managed heap kept growing while meshes streamed. " + summary);
            HeadlessHarness.Assert(endPrivate - warmPrivate < 256L * 1048576,
                $"{backend.ShortName}: private memory kept growing while meshes streamed. " + summary);
            // Destroyed meshes' storage is reused by the next ones: building a round of chunks
            // allocated 52 MB before (each 12,000-vertex list grown from nothing). The software
            // renderer keeps its own copy of every mesh, so it is not held to this.
            if (!software)
                HeadlessHarness.Assert(allocatedPerRound < 4L * 1048576,
                    $"{backend.ShortName}: streaming chunk meshes allocates too much. " + summary);
            HeadlessHarness.Assert(ScriptMeshes.SpareBytes <= ScriptMeshes.MaxSpareBytes,
                $"{backend.ShortName}: more mesh storage was kept for reuse than allowed ({ScriptMeshes.SpareBytes / 1048576.0:F0} MB).");
            return string.Join(Environment.NewLine, lines);
        }
        finally
        {
            ScriptMeshes.Reset();
        }
    }

    // Lets the GPU finish the frames in flight so deferred releases happen.
    private static void Settle(IRenderController renderer)
    {
        for (int i = 0; i < 4; i++)
        {
            renderer.BeginFrame(); renderer.Clear(0, 0, 0); renderer.EndFrame(); renderer.Present();
        }
    }

    private static long PrivateBytes()
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return process.PrivateMemorySize64;
    }

    private static int LiveBuffers(IRenderController renderer) => FieldCount(renderer, "_buffers");

    // The count of a collection field on the renderer's GPU device (-1 when there is none).
    private static int FieldCount(IRenderController renderer, string field)
    {
        object? device = renderer.GetType().GetField("_gpu", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(renderer);
        for (int depth = 0; device is not null && depth < 3; depth++)
        {
            for (Type? type = device.GetType(); type is not null; type = type.BaseType)
            {
                if (type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(device) is ICollection collection)
                    return collection.Count;
            }
            // A wrapping device: look inside it.
            device = device.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .FirstOrDefault(info => typeof(Genesis.Rendering.Abstractions.IGpuDevice).IsAssignableFrom(info.FieldType))?.GetValue(device);
        }
        return -1;
    }

    // A grid of quads with a little height so every mesh differs.
    private static void Build(int id, int seed, int quads)
    {
        int side = (int)MathF.Ceiling(MathF.Sqrt(quads));
        int made = 0;
        for (int z = 0; z < side && made < quads; z++)
            for (int x = 0; x < side && made < quads; x++, made++)
            {
                float y = ((x * 7 + z * 13 + seed) % 5) * 0.1f;
                var colour = new Vector4(0.4f + (x % 3) * 0.2f, 0.6f, 0.3f, 1);
                ScriptMeshes.AddQuad(id, new Vector3(x * .25f, y, z * .25f), new Vector3(x * .25f + .25f, y, z * .25f),
                    new Vector3(x * .25f + .25f, y, z * .25f + .25f), new Vector3(x * .25f, y, z * .25f + .25f),
                    Vector3.UnitY, new Vector4(0, 0, 1, 1), colour);
            }
    }
}
