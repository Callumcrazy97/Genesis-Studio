using System.Drawing;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Core;
using Genesis.Rendering.Meshes;
using Genesis.Rendering.Particles;
using Genesis.Rendering.Primitives;
using Genesis.Rendering.SilkNet.OpenGL;
using Genesis.Rendering.Viewport;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

internal static class OpenGlCompatibilitySuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Render.OpenGL.Compatibility.Glsl45CatalogAndCache", () =>
        {
            string root = Path.Combine(ctx.Workspace, "OpenGL45Cache");
            List<EngineShaderJob> jobs = new(EngineShaderCatalog.Jobs);
            jobs.AddRange(GpuParticleShaders.ComputeEntries.Select(entry => new EngineShaderJob("ParticleCompute", GpuParticleShaders.Compute, entry, GpuShaderStage.Compute)));
            jobs.Add(new("ParticleDraw", GpuParticleShaders.Draw, "VS", GpuShaderStage.Vertex));
            jobs.Add(new("ParticleDraw", GpuParticleShaders.Draw, "PS", GpuShaderStage.Pixel));
            jobs.Add(new("TerrainImages", TerrainSurfaceShaders.Source, "VS", GpuShaderStage.Vertex));
            jobs.Add(new("TerrainImages", TerrainSurfaceShaders.Source, "PS", GpuShaderStage.Pixel));
            jobs.Add(new("TerrainLayerAtlas", TerrainSurfaceShaders.LayerAtlasSource, "PS", GpuShaderStage.Pixel));
            foreach (EngineShaderJob job in jobs)
            {
                ShaderCompileResult first = EngineShaderCatalog.Compile(job, GpuShaderBinaryFormat.GlslUtf8, root);
                Check(Regex.Match(Encoding.UTF8.GetString(first.Blob), @"(?m)^\s*#version\s+(\d+)").Groups[1].Value == "450",
                    job.Label + ":" + job.Entry + " still requires GLSL 4.6.");
                ShaderCompileResult cached = EngineShaderCatalog.Compile(job, GpuShaderBinaryFormat.GlslUtf8, root);
                Check(cached.CacheHit && first.Blob.SequenceEqual(cached.Blob), "The GLSL baseline did not survive a warm cache read.");
            }
            File.WriteAllText(Path.Combine(ctx.Logs, "glsl45-catalog.txt"), string.Join(Environment.NewLine, jobs.Select(job => job.Label + ":" + job.Entry)));
        });
        HeadlessHarness.RunCase(ctx.Report, "Render.OpenGL.Compatibility.Glsl45NativeProgramsAndCompute", () =>
        {
            using OpenGLGpuDevice device = new();
            void Link(string source, string vertex, string pixel, string label)
            {
                byte[] vs = ShaderCompiler.CompileForBackend(source, vertex, GpuShaderStage.Vertex, device.ShaderBinaryFormat).Blob;
                byte[] ps = ShaderCompiler.CompileForBackend(source, pixel, GpuShaderStage.Pixel, device.ShaderBinaryFormat).Blob;
                GpuShaderProgramHandle program = device.CreateShaderProgram(new() { BinaryFormat = device.ShaderBinaryFormat,
                    VertexShader = vs, PixelShader = ps, DebugName = label });
                Check(program.IsValid, "Native driver rejected the GLSL 4.5 program: " + label);
                device.ReleaseShaderProgram(program);
            }
            Link(SpriteShaders.Source, "VS", "PS", "GL45.Sprite");
            Link(ForwardShaders.Source, "VS", "PS", "GL45.Model");
            Link(ForwardShaders.Source, "VS_Skinned", "PS", "GL45.AnimatedModel");
            Link(TerrainSurfaceShaders.Source, "VS", "PS", "GL45.TiledTerrain");
            Link(TerrainSurfaceShaders.LayerAtlasSource, "VS", "PS", "GL45.TerrainLayerAtlas");
            Link(WaterShaders.Source, "VS_Water", "PS_Water", "GL45.Water");
            using GpuParticleLibrary particles = new(device);
            Check(particles.DrawProgram.IsValid && GpuParticleShaders.ComputeEntries.All(entry => particles.Kernel(entry).IsValid),
                "GPU particle compute or indirect draw rejected the GLSL baseline.");
            File.WriteAllText(Path.Combine(ctx.Logs, "glsl45-native.txt"), device.BackendName + Environment.NewLine + device.AdapterName
                + Environment.NewLine + "GLSL 450 programs linked on the available native context. This does not simulate a separate 4.5-only driver.");
        });
        HeadlessHarness.RunCase(ctx.Report, "Render.OpenGL.ProjectMeshShaderWithItsOwnEngineBuffersDrawsOnEveryBackend",
            () => ProjectMeshShaderWithItsOwnEngineBuffers(ctx));
    }

    /// <summary>
    /// A mesh Shader resource's pixel shader is drawn with the engine's vertex shader, and declares
    /// its own copy of the engine's constant buffers: fewer members, other names. Direct3D and
    /// Vulkan read each stage's declaration against the bound buffer by offset. GLSL refused to link
    /// the pair ("struct type mismatch between shaders for uniform (named type_PerFrameConstants)"),
    /// so a game drawing with such a shader stopped on its first frame on OpenGL. Each channel of the
    /// drawn colour is lit only when its buffer was read at the engine's offsets, each to its own
    /// level, so the engine's own shading of a white quad cannot pass for it.
    /// </summary>
    /// <remarks>
    /// The shader also carries a game-sized <c>static const int3</c> table (2,048 entries, like a
    /// block table), read at runtime indices in its first and its fifteenth hundred-and-twenty-eight:
    /// as one GLSL constant NVIDIA's compiler refused it ("too much data in initialization"). Red is
    /// lit only when the table read the right entries too.
    /// </remarks>
    private static readonly string OwnBuffersShader = BuildOwnBuffersShader();

    private static string BuildOwnBuffersShader()
    {
        static string Entry(int i) => $"int3({i * 7}, {i ^ 0x55}, {2048 - i})";
        string table = string.Join(", ", Enumerable.Range(0, 2048).Select(Entry));
        // Index 1800 comes from the camera height (3 x 600), index 7 from the fog start.
        return $$"""
            cbuffer PerFrameConstants : register(b0) { row_major float4x4 Vp; row_major float4x4 FarLight; row_major float4x4 NearLight; float4 EyeAndClock; };
            cbuffer EngineConstants : register(b1) { float4 SunDirOn; float4 FogA; };
            cbuffer DrawConstants : register(b2) { row_major float4x4 ObjectToWorld; float4 Tint; };
            static const int3 BigTable[2048] = { {{table}} };
            struct VSOut { float4 SvPos : SV_Position; float3 WorldPos : TEXCOORD1; float3 Normal : TEXCOORD2; float4 Color : TEXCOORD3; float2 UV : TEXCOORD4; };
            float4 MainPS(VSOut input) : SV_Target
            {
                int far = (int)round(EyeAndClock.y * 600.0);
                int near = (int)round(FogA.y);
                bool table = all(BigTable[far] == {{Entry(1800)}}) && all(BigTable[near] == {{Entry(7)}});
                float frame = abs(EyeAndClock.y - 3.0) < 0.01 && table ? 1 : 0;
                float engine = abs(FogA.y - 7.0) < 0.001 && abs(FogA.z - 31.0) < 0.001
                    && abs(SunDirOn.x - 0.6) < 0.001 && abs(SunDirOn.y + 0.8) < 0.001 ? 1 : 0;
                float draw = all(abs(Tint - 1.0) < 0.001) ? 1 : 0;
                return float4(frame, engine * 0.3, draw * 0.08, 1);
            }
            """;
    }

    private static void ProjectMeshShaderWithItsOwnEngineBuffers(HeadlessContext ctx)
    {
        IRenderController? preparedFor = null;
        MeshHandle quad = MeshHandle.Invalid;
        RuntimeShaderHandle shader = RuntimeShaderHandle.Invalid;
        string? error = null;

        using Form host = GateSuite.NewHost(320, 240);
        using D3DViewportControl viewport = new() { Dock = DockStyle.Fill, DriveMode = ViewportDriveMode.External, VSync = false };
        viewport.OnRender += renderer =>
        {
            try
            {
                if (!ReferenceEquals(preparedFor, renderer))
                {
                    preparedFor = renderer;
                    quad = MeshGeometry.RegisterQuad(renderer, RenderColor.White);
                    shader = renderer.RegisterRuntimeMeshPass(OwnBuffersShader,
                        new ShaderPassDefinition { Name = "Surface", Entry = "MainPS", Source = OwnBuffersShader });
                }

                renderer.Set3DFrameActive(true);
                renderer.Clear(0.1f, 0.1f, 0.1f, 1f);
                renderer.SetCamera3D(
                    Matrix4x4.CreateLookAt(new Vector3(0f, 3f, 3f), new Vector3(0f, 3f, 0f), Vector3.UnitY),
                    Matrix4x4.CreateOrthographic(2f, 2f, 0.1f, 20f));
                Mesh3DState state = Mesh3DState.Default;
                state.LightingEnabled = true; state.LightingWeight = 1f;
                state.LightDirection = new Vector3(0.6f, -0.8f, 0f);
                state.FogEnabled = false; state.FogStart = 7f; state.FogEnd = 31f;
                state.ShadowsEnabled = false; state.ShowFloor = false; state.ShowSunVisual = false;
                state.FrustumCullingEnabled = false;
                renderer.SetMesh3DState(state);
                renderer.ClearPointLights();
                renderer.DrawMesh(new MeshDrawCall
                {
                    Mesh = quad, Shader = shader, Tint = RenderColor.White, Alpha = 1f,
                    World = Matrix4x4.CreateScale(1.2f) * Matrix4x4.CreateTranslation(0f, 3f, 0f),
                    Flags = MeshDrawFlags.NoCull | MeshDrawFlags.NoShadow | MeshDrawFlags.NoFog,
                });
            }
            catch (Exception exception)
            {
                error ??= exception.ToString();
            }
        };
        host.Controls.Add(viewport);
        GateSuite.ShowHost(host);

        List<string> failures = [];
        List<string> readings = [];
        Color? reference = null;
        foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
        {
            // The Software rasteriser runs no HLSL: it shades every mesh itself.
            if (backend.Backend == RenderBackendOption.Software)
            {
                readings.Add("Software skipped (runs no project shaders)");
                continue;
            }
            if (!RenderBackendSelection.IsAvailable(backend.Backend))
            {
                readings.Add(backend.ShortName + " skipped (unavailable here)");
                continue;
            }

            error = null;
            viewport.BackendOverride = backend.Backend;
            GateSuite.Pump(3, 15);
            using Bitmap? frame = viewport.ReadbackFrameToBitmap(3);
            string active = viewport.Renderer?.BackendName ?? "none";
            if (frame is null || error != null || !shader.IsValid)
            {
                failures.Add($"{backend.ShortName} ({active}): no frame or no shader. {error ?? viewport.LastRenderException?.ToString()}");
                continue;
            }

            string name = backend.ShortName.ToLowerInvariant();
            frame.Save(Path.Combine(ctx.Captures, "project-mesh-shader-own-buffers-" + name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            Color centre = frame.GetPixel(frame.Width / 2, frame.Height / 2);
            readings.Add($"{backend.ShortName} ({active}) centre={centre.R},{centre.G},{centre.B}");
            string expected = backend.Backend switch
            {
                RenderBackendOption.SilkNetDx11 => "Direct3D 11",
                RenderBackendOption.Direct3D12 => "Direct3D 12",
                RenderBackendOption.Vulkan => "Vulkan",
                _ => "OpenGL",
            };
            if (!active.StartsWith(expected, StringComparison.OrdinalIgnoreCase))
                failures.Add($"{backend.ShortName}: the viewport drew with '{active}' instead.");
            // Lit to three distinct levels: red (PerFrameConstants) above green (EngineConstants)
            // above blue (DrawConstants), none of them dark. A channel whose buffer was misread is black.
            if (!(centre.R > centre.G + 15 && centre.G > centre.B + 15 && centre.B > 10))
                failures.Add($"{backend.ShortName}: the shader read the engine's buffers wrongly (R = PerFrameConstants and the "
                    + $"2,048-entry table, G = EngineConstants, B = DrawConstants; each should be lit, at falling levels): {centre}.");
            // Direct3D 11 is the reference: every backend must draw the colour it drew.
            reference ??= centre;
            Color expectedColour = reference.Value;
            if (Math.Abs(centre.R - expectedColour.R) > 6 || Math.Abs(centre.G - expectedColour.G) > 6 || Math.Abs(centre.B - expectedColour.B) > 6)
                failures.Add($"{backend.ShortName}: drew {centre} where Direct3D 11 drew {expectedColour}.");
        }

        File.WriteAllLines(Path.Combine(ctx.Logs, "project-mesh-shader-own-buffers.txt"), readings);
        Console.WriteLine("Project mesh shader with its own engine buffers: " + string.Join("; ", readings));
        Check(failures.Count == 0, string.Join(" | ", failures));
    }
    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
