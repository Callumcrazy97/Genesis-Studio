using System;
using System.Collections.Generic;
using Genesis.Rendering.Abstractions;

namespace Genesis.Rendering.Primitives
{
    public sealed record EngineShaderJob(string Label, string Source, string Entry, GpuShaderStage Stage);

    /// <summary>Authoritative built-in shader jobs, shared by the runtime and measured boot warmup.</summary>
    public static class EngineShaderCatalog
    {
        public static IReadOnlyList<EngineShaderJob> Jobs { get; } = new EngineShaderJob[]
        {
            new("SpriteShaders.hlsl", SpriteShaders.Source, "VS", GpuShaderStage.Vertex),
            new("SpriteShaders.hlsl", SpriteShaders.Source, "PS", GpuShaderStage.Pixel),
            new("ForwardShaders.hlsl", ForwardShaders.Source, "VS", GpuShaderStage.Vertex),
            new("ForwardShaders.hlsl", ForwardShaders.Source, "VS_Skinned", GpuShaderStage.Vertex),
            new("ForwardShaders.hlsl", ForwardShaders.Source, "VS_Shadow", GpuShaderStage.Vertex),
            new("ForwardShaders.hlsl", ForwardShaders.Source, "VS_ShadowMid", GpuShaderStage.Vertex),
            new("ForwardShaders.hlsl", ForwardShaders.Source, "VS_ShadowNear", GpuShaderStage.Vertex),
            new("ForwardShaders.hlsl", ForwardShaders.Source, "VS_ShadowSkinned", GpuShaderStage.Vertex),
            new("ForwardShaders.hlsl", ForwardShaders.Source, "VS_ShadowSkinnedMid", GpuShaderStage.Vertex),
            new("ForwardShaders.hlsl", ForwardShaders.Source, "VS_ShadowSkinnedNear", GpuShaderStage.Vertex),
            new("ForwardShaders.hlsl", ForwardShaders.Source, "VS_ShadowTileClear", GpuShaderStage.Vertex),
            new("ForwardShaders.hlsl", ForwardShaders.Source, "PS", GpuShaderStage.Pixel),
            new("TerrainShader.hlsl", TerrainShader.Source, "VS_Terrain", GpuShaderStage.Vertex),
            new("TerrainShader.hlsl", TerrainShader.Source, "PS_Terrain", GpuShaderStage.Pixel),
            new("WaterShaders.hlsl", WaterShaders.Source, "VS_Water", GpuShaderStage.Vertex),
            new("WaterShaders.hlsl", WaterShaders.Source, "PS_Water", GpuShaderStage.Pixel),
            new("FroxelFogShaders.hlsl", FroxelFogShaders.Source, "VS", GpuShaderStage.Vertex),
            new("FroxelFogShaders.hlsl", FroxelFogShaders.Source, "PS_FroxelInject", GpuShaderStage.Pixel),
            new("FroxelFogShaders.hlsl", FroxelFogShaders.Source, "PS_FroxelTemporal", GpuShaderStage.Pixel),
            new("FroxelFogShaders.hlsl", FroxelFogShaders.Source, "PS_FroxelIntegrate", GpuShaderStage.Pixel),
            new("FogPostShaders.hlsl", FogPostShaders.Source, "VS", GpuShaderStage.Vertex),
            new("FogPostShaders.hlsl", FogPostShaders.Source, "PS", GpuShaderStage.Pixel),
            new("GtaoShaders.hlsl", GtaoShaders.Source, "VS", GpuShaderStage.Vertex),
            new("GtaoShaders.hlsl", GtaoShaders.Source, "PS_Gtao", GpuShaderStage.Pixel),
            new("GtaoShaders.hlsl", GtaoShaders.Source, "PS_Blur", GpuShaderStage.Pixel),
            new("ContactShadowShaders.hlsl", ContactShadowShaders.Source, "VS", GpuShaderStage.Vertex),
            new("ContactShadowShaders.hlsl", ContactShadowShaders.Source, "PS_Contact", GpuShaderStage.Pixel),
            new("LocalVolumetricShaders.hlsl", LocalVolumetricShaders.Source, "VS", GpuShaderStage.Vertex),
            new("LocalVolumetricShaders.hlsl", LocalVolumetricShaders.Source, "PS_LocalVol", GpuShaderStage.Pixel),
            new("BloomShaders.hlsl", BloomShaders.Source, "VS", GpuShaderStage.Vertex),
            new("BloomShaders.hlsl", BloomShaders.Source, "PS_ThresholdExtract", GpuShaderStage.Pixel),
            new("BloomShaders.hlsl", BloomShaders.Source, "PS_Downsample", GpuShaderStage.Pixel),
            new("BloomShaders.hlsl", BloomShaders.Source, "PS_Upsample", GpuShaderStage.Pixel),
            new("RaymarchedCloudsShaders.hlsl", RaymarchedCloudsShaders.Source, "VS", GpuShaderStage.Vertex),
            new("RaymarchedCloudsShaders.hlsl", RaymarchedCloudsShaders.Source, "PS_Clouds", GpuShaderStage.Pixel),
            new("CloudTemporalShaders.hlsl", CloudTemporalShaders.Source, "VS", GpuShaderStage.Vertex),
            new("CloudTemporalShaders.hlsl", CloudTemporalShaders.Source, "PS_TemporalResolve", GpuShaderStage.Pixel),
            new("CloudTemporalShaders.hlsl", CloudTemporalShaders.Source, "PS_BilateralUpsample", GpuShaderStage.Pixel),
            new("ShaderPreviewFullscreenShaders.hlsl", ShaderPreviewFullscreenShaders.Source, "PreviewVS", GpuShaderStage.Vertex),
            new("AutoExposureShaders.hlsl", AutoExposureShaders.Source, "VS", GpuShaderStage.Vertex),
            new("AutoExposureShaders.hlsl", AutoExposureShaders.Source, "PS_Meter", GpuShaderStage.Pixel),
            new("AutoExposureShaders.hlsl", AutoExposureShaders.Source, "PS_Reduce", GpuShaderStage.Pixel),
            new("AutoExposureShaders.hlsl", AutoExposureShaders.Source, "PS_Adapt", GpuShaderStage.Pixel),
        };

        /// <summary>
        /// Every program the engine itself may compile while a game or an editor viewport starts:
        /// <see cref="Jobs"/>, plus the GPU particle kernels and the tiled-terrain programs that
        /// are made when a room first needs them. This is what ships precompiled beside Studio and
        /// the Player (<see cref="PrecompiledShaders"/>) and what the start-up warm-up makes.
        /// </summary>
        public static IReadOnlyList<EngineShaderJob> PrecompiledJobs { get; } = BuildPrecompiledJobs();

        private static EngineShaderJob[] BuildPrecompiledJobs()
        {
            var jobs = new List<EngineShaderJob>(Jobs);
            foreach (string entry in Genesis.Rendering.Particles.GpuParticleShaders.ComputeEntries)
                jobs.Add(new("GpuParticleShaders.Compute.hlsl", Genesis.Rendering.Particles.GpuParticleShaders.Compute, entry, GpuShaderStage.Compute));
            jobs.Add(new("GpuParticleShaders.Draw.hlsl", Genesis.Rendering.Particles.GpuParticleShaders.Draw, "VS", GpuShaderStage.Vertex));
            jobs.Add(new("GpuParticleShaders.Draw.hlsl", Genesis.Rendering.Particles.GpuParticleShaders.Draw, "PS", GpuShaderStage.Pixel));
            jobs.Add(new("TerrainSurfaceShaders.hlsl", TerrainSurfaceShaders.Source, "PS", GpuShaderStage.Pixel));
            jobs.Add(new("TerrainSurfaceShaders.LayerAtlas.hlsl", TerrainSurfaceShaders.LayerAtlasSource, "PS", GpuShaderStage.Pixel));
            return jobs.ToArray();
        }

        public static IReadOnlyList<ShaderCompileResult> CompileAll(GpuShaderBinaryFormat binaryFormat, string cacheRoot = null)
        {
            var results = new List<ShaderCompileResult>(Jobs.Count);
            foreach (EngineShaderJob job in Jobs) results.Add(Compile(job, binaryFormat, cacheRoot));
            return results;
        }

        public static ShaderCompileResult Compile(EngineShaderJob job, GpuShaderBinaryFormat binaryFormat, string cacheRoot = null)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            try
            {
                return ShaderCompiler.CompileForBackend(job.Source, job.Entry, job.Stage, binaryFormat,
                    sourcePath: job.Label, cacheRoot: cacheRoot);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Built-in shader {job.Label}:{job.Entry} failed to compile as {binaryFormat}.", ex);
            }
        }
    }
}
