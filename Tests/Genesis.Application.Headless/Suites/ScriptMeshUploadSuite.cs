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
        HeadlessHarness.RunCase(ctx.Report, "Runtime.ScriptMeshes.ShaderMeshDrawsAllocateNothing", () => lines.Add(MeasureShaderDrawAllocations(ctx)));
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

    private const int ShaderDraws = 400;

    /// <summary>
    /// What a Draw event that draws many script meshes through a mesh Shader resource allocates, as a
    /// game drawing its chunks and creatures does: each draw sets the instance's shader vector
    /// (ShaderSetVector) and draws (DrawMeshShader3D), 400 a frame on DX11. Reports the bytes a draw
    /// and a frame allocate and the types allocated most; a draw must allocate nothing.
    /// </summary>
    private static string MeasureShaderDrawAllocations(HeadlessContext ctx)
    {
        string project = Path.Combine(ctx.Workspace, "ShaderDrawAllocations");
        string shaders = Path.Combine(project, "Assets", "Shaders");
        Directory.CreateDirectory(shaders);
        File.WriteAllText(Path.Combine(shaders, "Tinted.shader.json"), System.Text.Json.JsonSerializer.Serialize(
            new Genesis.Shared.Assets.ShaderAssetDocument
            {
                Pipeline = Genesis.Shared.Assets.ShaderAssetPipeline.Mesh,
                Entry = "MainPS",
                Source = """
                    cbuffer GenesisParameters : register(b5) { float4 Tint; float Glow; };
                    struct VSOut { float4 SvPos : SV_Position; float3 WorldPos : TEXCOORD1; float3 Normal : TEXCOORD2; float4 Color : TEXCOORD3; float2 UV : TEXCOORD4; };
                    float4 MainPS(VSOut input) : SV_Target { return float4(Tint.rgb * input.Color.rgb + Glow, 1); }
                    """,
            },
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
        Genesis.Shared.Assets.ResourceCatalog.Invalidate(project);

        using Form host = UnattendedWindowing.NewHost(480, 320); UnattendedWindowing.ShowWithoutFocus(host);
        using IRenderController renderer = RenderControllerFactory.Create(RenderBackendOption.SilkNetDx11); renderer.Initialize(host.Handle, 480, 320);
        using var scene = new Genesis.Runtime.RuntimeScene("Shader draws");
        var game = new Genesis.Runtime.Project.ProjectGameContext(project, scene, null, null,
            Genesis.Runtime.Scene.RoomAsset.Create("Probe", Genesis.Runtime.Scene.RoomDimension.ThreeD), null);
        var previousGame = Genesis.Runtime.Scripting.PgslCommands.ActiveGameContext;
        Genesis.Runtime.Scripting.PgslCommands.ActiveGameContext = game;
        var instance = scene.World.CreateEntity();
        var context = new Genesis.Shared.Scripting.PgslContext
        {
            InstanceId = instance.Id,
            DrawSurface = new Genesis.Runtime.Scripting.PgslRenderDrawSurface(renderer, null, 480, 320, null, project, is3DActive: true),
        };
        var previousContext = Genesis.Runtime.Scripting.PgslCommands.BindContext(context);
        ScriptMeshes.Reset();
        using var types = new AllocationTypes();
        try
        {
            int mesh = ScriptMeshes.Create();
            ScriptMeshes.AddQuad(mesh, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0),
                -Vector3.UnitZ, new Vector4(0, 0, 1, 1), new Vector4(1, 1, 1, 1));
            long drawBytes = 0, frameBytes = 0;
            int measured = 0;
            void Frame(bool measure)
            {
                long frameStart = GC.GetAllocatedBytesForCurrentThread();
                Mesh3DState state = Mesh3DState.Default;
                state.ShowFloor = false; state.ShowSunVisual = false;
                renderer.SetMesh3DState(state); renderer.Set3DFrameActive(true);
                renderer.SetCamera3D(Matrix4x4.CreateLookAt(new Vector3(10, 10, -30), new Vector3(10, 10, 0), Vector3.UnitY),
                    Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 1.5f, .1f, 1000));
                renderer.BeginFrame(); renderer.Clear(.1f, .1f, .1f);
                ScriptMeshes.BeginFrame();
                long drawStart = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < ShaderDraws; i++)
                {
                    Genesis.Runtime.Scripting.PgslCommands.ShaderSetVector("Tint", (i % 20) / 20.0, 0.5, 1 - (i % 7) / 7.0, 1);
                    Genesis.Runtime.Scripting.PgslCommands.ShaderSetParameter("Glow", (i % 3) * 0.1);
                    Genesis.Runtime.Scripting.PgslCommands.DrawMeshShader3D(mesh, "Tinted", i % 20, i / 20, 0, 0.9, 0.9, 0.9, 0, "");
                }
                long drawn = GC.GetAllocatedBytesForCurrentThread() - drawStart;
                renderer.EndFrame();
                if (!measure) return;
                drawBytes += drawn;
                frameBytes += GC.GetAllocatedBytesForCurrentThread() - frameStart;
                measured++;
            }
            for (int i = 0; i < 10; i++) Frame(measure: false);
            types.Start();
            for (int i = 0; i < 30; i++) Frame(measure: true);
            string top = types.Summary();
            double perDraw = drawBytes / (double)(measured * ShaderDraws);
            string line = $"DX11: {ShaderDraws} script-mesh draws a frame through a mesh Shader resource, each after ShaderSetVector and ShaderSetParameter: "
                + $"{perDraw:F1} bytes a draw, {frameBytes / (double)measured / 1024:F1} KB a frame in all (over {measured} frames); most allocated: {(top.Length > 0 ? top : "nothing sampled")}";
            Console.WriteLine(line);
            HeadlessHarness.Assert(Genesis.Runtime.Rendering.ObjectDrawAssetRegistry.TryGet(instance, out var assets) && assets.ShaderParameters["Tint"][1] == 0.5f,
                "The instance's shader vector did not reach its draw values.");
            HeadlessHarness.Assert(perDraw < 8, "Drawing a script mesh through a shader allocates: " + line);
            return line;
        }
        finally
        {
            ScriptMeshes.Reset();
            Genesis.Runtime.Scripting.PgslCommands.BindContext(previousContext);
            Genesis.Runtime.Scripting.PgslCommands.ActiveGameContext = previousGame;
        }
    }

    /// <summary>The types the runtime's allocation samples name (one sample about every 100 KB), while started.</summary>
    private sealed class AllocationTypes : System.Diagnostics.Tracing.EventListener
    {
        private readonly Dictionary<string, long> _bytes = new(StringComparer.Ordinal);
        private volatile bool _on;

        public void Start() => _on = true;

        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1);
        }

        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs data)
        {
            if (!_on || data.EventName == null || !data.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal) || data.Payload == null) return;
            int typeIndex = data.PayloadNames?.IndexOf("TypeName") ?? -1;
            int amountIndex = data.PayloadNames?.IndexOf("AllocationAmount64") ?? -1;
            if (typeIndex < 0) return;
            string type = data.Payload[typeIndex] as string ?? "?";
            long amount = amountIndex >= 0 && data.Payload[amountIndex] is ulong big ? (long)big : 100_000;
            lock (_bytes) { _bytes[type] = _bytes.GetValueOrDefault(type) + amount; }
        }

        public string Summary()
        {
            lock (_bytes)
                return string.Join(", ", _bytes.OrderByDescending(pair => pair.Value).Take(8)
                    .Select(pair => $"{pair.Key} {pair.Value / 1024.0:F0} KB"));
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
