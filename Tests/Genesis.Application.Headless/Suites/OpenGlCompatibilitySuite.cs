using System.Text;
using System.Text.RegularExpressions;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Particles;
using Genesis.Rendering.Primitives;
using Genesis.Rendering.SilkNet.OpenGL;

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
            Link(WaterShaders.Source, "VS_Water", "PS_Water", "GL45.Water");
            using GpuParticleLibrary particles = new(device);
            Check(particles.DrawProgram.IsValid && GpuParticleShaders.ComputeEntries.All(entry => particles.Kernel(entry).IsValid),
                "GPU particle compute or indirect draw rejected the GLSL baseline.");
            File.WriteAllText(Path.Combine(ctx.Logs, "glsl45-native.txt"), device.BackendName + Environment.NewLine + device.AdapterName
                + Environment.NewLine + "GLSL 450 programs linked on the available native context. This does not simulate a separate 4.5-only driver.");
        });
    }
    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
