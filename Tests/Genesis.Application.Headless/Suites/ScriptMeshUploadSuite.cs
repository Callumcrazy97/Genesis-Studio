using System.Diagnostics;
using System.Numerics;
using Genesis.Rendering.Core;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// What it costs to bring many script-built meshes (PGSL MeshCreate) to the GPU: the first draw of
/// 40 chunk-sized meshes in one frame, rebuilding them (MeshClear and build again, as a game that
/// pools its meshes does), and an ordinary frame drawing them. Timings are reported in the log and
/// in mesh-upload.txt; only a generous ceiling is asserted.
/// </summary>
internal static class ScriptMeshUploadSuite
{
    private const int MeshCount = 40;
    private const int QuadsPerMesh = 3000; // 12,000 vertices, about a busy voxel chunk

    public static void Run(HeadlessContext ctx)
    {
        var lines = new List<string>();
        foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All.Where(item => item.Backend != RenderBackendOption.Software))
            HeadlessHarness.RunCase(ctx.Report, "Runtime.ScriptMeshes.UploadCost." + backend.ShortName, () => lines.Add(Measure(backend)));
        Directory.CreateDirectory(ctx.Captures);
        File.WriteAllLines(Path.Combine(ctx.Captures, "mesh-upload.txt"), lines);
        foreach (string line in lines) Console.WriteLine(line);
    }

    private static string Measure(RenderBackendDescriptor backend)
    {
        using Form host = UnattendedWindowing.NewHost(480, 320); UnattendedWindowing.ShowWithoutFocus(host);
        using IRenderController renderer = RenderControllerFactory.Create(backend.Backend); renderer.Initialize(host.Handle, 480, 320);
        ScriptMeshes.Reset();
        int[] ids = new int[MeshCount];
        try
        {
            for (int m = 0; m < MeshCount; m++)
            {
                ids[m] = ScriptMeshes.Create();
                Build(ids[m], m, 0f);
            }

            double lastResolve = 0;
            double Frame(Action perMesh)
            {
                Mesh3DState state = Mesh3DState.Default;
                state.ShowFloor = false; state.ShowSunVisual = false;
                renderer.SetMesh3DState(state); renderer.Set3DFrameActive(true);
                renderer.SetCamera3D(Matrix4x4.CreateLookAt(new Vector3(0, 40, -60), Vector3.Zero, Vector3.UnitY),
                    Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 1.5f, .1f, 1000));
                renderer.BeginFrame(); renderer.Clear(.1f, .1f, .1f);
                ScriptMeshes.BeginFrame();
                var clock = Stopwatch.StartNew();
                perMesh();
                var resolve = Stopwatch.StartNew();
                var handles = new MeshHandle[MeshCount];
                for (int m = 0; m < MeshCount; m++) handles[m] = ScriptMeshes.Resolve(ids[m], renderer);
                lastResolve = resolve.Elapsed.TotalMilliseconds;
                for (int m = 0; m < MeshCount; m++)
                {
                    renderer.DrawMesh(new MeshDrawCall
                    {
                        Mesh = handles[m], World = Matrix4x4.CreateTranslation((m % 8) * 18 - 70, 0, (m / 8) * 18),
                        Tint = new RenderColor(1, 1, 1, 1), Alpha = 1,
                    });
                }
                renderer.EndFrame();
                clock.Stop();
                return clock.Elapsed.TotalMilliseconds;
            }

            // The old behaviour first: every mesh in the frame it is drawn.
            ScriptMeshes.UploadBudgetMilliseconds = 0;
            double first = Frame(() => { });
            double firstUpload = lastResolve;
            double steady = Frame(() => { });
            double rebuildBuild = 0;
            double rebuild = Frame(() =>
            {
                var build = Stopwatch.StartNew();
                for (int m = 0; m < MeshCount; m++) { ScriptMeshes.Clear(ids[m]); Build(ids[m], m, 0.5f); }
                rebuildBuild = build.Elapsed.TotalMilliseconds;
            }) - rebuildBuild;
            double rebuildUpload = lastResolve;
            double steadyAfter = Frame(() => { });

            // With the frame budget: rebuild all of them at once, then count the frames until every
            // build is on the GPU and the longest upload in any one frame.
            ScriptMeshes.UploadBudgetMilliseconds = ScriptMeshes.DefaultUploadBudgetMilliseconds;
            for (int m = 0; m < MeshCount; m++) { ScriptMeshes.Clear(ids[m]); Build(ids[m], m, 0.25f); }
            int frames = 0;
            double worstUpload = 0;
            while (frames < 200 && !ids.All(ScriptMeshes.IsUploaded))
            {
                Frame(() => { });
                worstUpload = Math.Max(worstUpload, lastResolve);
                frames++;
            }
            HeadlessHarness.Assert(ids.All(ScriptMeshes.IsUploaded), $"{backend.ShortName}: the budget never finished uploading the meshes.");
            string line = $"{backend.ShortName}: first draw of {MeshCount} new meshes {first:F1} ms (upload {firstUpload:F1}); rebuilt and drawn {rebuild:F1} ms (upload {rebuildUpload:F1}) "
                + $"(building them on the CPU {rebuildBuild:F1} ms more); ordinary frame {steady:F1} / {steadyAfter:F1} ms; "
                + $"with the {ScriptMeshes.DefaultUploadBudgetMilliseconds:F0} ms budget: all {MeshCount} rebuilt in {frames} frames, at most {worstUpload:F1} ms of upload in one frame";
            HeadlessHarness.Assert(first < 5000 && rebuild < 5000, "Script mesh upload took over five seconds: " + line);
            // One mesh may start just under the budget; allow it plus room for a busy machine.
            HeadlessHarness.Assert(worstUpload < 20, "One frame spent too long uploading with the budget: " + line);
            return line;
        }
        finally
        {
            ScriptMeshes.Reset();
        }
    }

    // A grid of quads with a little height so every mesh differs; the same vertex count each build.
    private static void Build(int id, int seed, float lift)
    {
        int side = (int)MathF.Ceiling(MathF.Sqrt(QuadsPerMesh));
        int made = 0;
        for (int z = 0; z < side && made < QuadsPerMesh; z++)
            for (int x = 0; x < side && made < QuadsPerMesh; x++, made++)
            {
                float y = ((x * 7 + z * 13 + seed) % 5) * 0.1f + lift;
                var colour = new Vector4(0.4f + (x % 3) * 0.2f, 0.6f, 0.3f, 1);
                ScriptMeshes.AddQuad(id, new Vector3(x * .25f, y, z * .25f), new Vector3(x * .25f + .25f, y, z * .25f),
                    new Vector3(x * .25f + .25f, y, z * .25f + .25f), new Vector3(x * .25f, y, z * .25f + .25f),
                    Vector3.UnitY, new Vector4(0, 0, 1, 1), colour);
            }
    }
}
