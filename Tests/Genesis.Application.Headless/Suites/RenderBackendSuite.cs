using System;
using System.Numerics;
using Genesis.Application.Runtime;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Settings;
using Genesis.Application.Studio;
using Genesis.Rendering.Diagnostics;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Core;
using Genesis.Rendering.Lights;
using Genesis.Rendering.SilkNet.DX11;
using Genesis.Rendering.SilkNet.Vulkan;
using Genesis.Rendering.Primitives;
using Genesis.Rendering.RenderGraph;
using Genesis.Rendering.Textures;
using Genesis.Runtime;
using Genesis.Runtime.Climate;
using Genesis.Runtime.Particles;
using Genesis.Shared.Assets;
using Genesis.Shared.Materials;
using SharedCamera = Genesis.Shared.Rendering.Camera;
using SharedCameraFrustum = Genesis.Shared.Rendering.CameraFrustum;
using SharedConventions = Genesis.Shared.Rendering.Conventions;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Overlay;
using Genesis.Shared.Scripting;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Debugger;
using Genesis.World.Water;
using System.Reflection;
using RuntimeImageMetrics = Genesis.Application.Runtime.ImageMetrics;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Backend-agnostic rendering assertions. These run before any GPU work so a layout or contract
/// regression is reported as itself rather than as a mysteriously wrong pixel later on.
/// </summary>
internal static class RenderBackendSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Render");

        // Phase 1 foundation: Shared is now the single authority for camera/projection conventions.
        // Keep this CPU-only and cheap enough for the normal fast build gate.
        HeadlessHarness.RunCase(ctx.Report, "Render.Foundation.SharedCameraConventions", () =>
        {
            SharedCamera camera = new()
            {
                Position = System.Numerics.Vector3.Zero,
                Yaw = 0f,
                Pitch = 0f,
                FieldOfView = MathF.PI / 2f,
                AspectRatio = 16f / 9f,
                NearPlane = 1f,
                FarPlane = 100f,
            };

            HeadlessHarness.Assert(!SharedConventions.ReversedZ,
                "Phase 1 must preserve Genesis's current conventional depth policy.");
            HeadlessHarness.Assert(SharedConventions.DepthClear == 1f,
                "Genesis conventional depth must continue clearing to 1.");
            HeadlessHarness.Assert(camera.Forward == -System.Numerics.Vector3.UnitZ,
                "Yaw zero no longer looks down world -Z.");
            HeadlessHarness.Assert(camera.Right == -System.Numerics.Vector3.UnitX,
                "Camera-right changed while consolidating the existing Genesis convention.");

            System.Numerics.Vector3 centre = camera.WorldToPixel(
                new System.Numerics.Vector3(0f, 0f, -5f), 1280f, 720f);
            HeadlessHarness.Assert(MathF.Abs(centre.X - 640f) < 0.01f && MathF.Abs(centre.Y - 360f) < 0.01f,
                $"Forward point no longer projects to screen centre: {centre}.");

            float nearDepth = camera.WorldToPixel(
                new System.Numerics.Vector3(0f, 0f, -camera.NearPlane), 1280f, 720f).Z;
            float farDepth = camera.WorldToPixel(
                new System.Numerics.Vector3(0f, 0f, -camera.FarPlane), 1280f, 720f).Z;
            HeadlessHarness.Assert(MathF.Abs(nearDepth - SharedConventions.DepthAtNearPlane) < 0.0001f,
                $"Near depth changed: {nearDepth}.");
            HeadlessHarness.Assert(MathF.Abs(farDepth - SharedConventions.DepthAtFarPlane) < 0.0001f,
                $"Far depth changed: {farDepth}.");

            System.Numerics.Vector3 centreRay = camera.PixelToWorldRay(640f, 360f, 1280f, 720f);
            HeadlessHarness.Assert(System.Numerics.Vector3.Dot(centreRay, camera.Forward) > 0.9999f,
                $"Centre pixel ray no longer follows camera forward: {centreRay}.");

            SharedCameraFrustum frustum = new(camera.ViewProjection);
            HeadlessHarness.Assert(frustum.ContainsSphere(new System.Numerics.Vector3(0f, 0f, -5f), 0.5f),
                "Shared frustum rejected a sphere directly in front of the camera.");
            HeadlessHarness.Assert(!frustum.ContainsSphere(new System.Numerics.Vector3(0f, 0f, 5f), 0.5f),
                "Shared frustum accepted a sphere behind the camera.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Render.Foundation.SpirVImageTypes", () =>
        {
            static SpirVBinding FindSampled(IReadOnlyList<SpirVBinding> list, int tRegister)
            {
                uint binding = (uint)VulkanShaderBindingPolicy.BindingForRegister('t', tRegister);
                foreach (SpirVBinding item in list)
                {
                    if (item.Kind == SpirVBindingKind.SampledImage && item.Binding == binding)
                        return item;
                }

                throw new InvalidOperationException($"No sampled-image binding at t{tRegister}.");
            }

            byte[] fogPs = ShaderCompiler.CompileForBackend(
                FogPostShaders.Source, "PS", GpuShaderStage.Pixel, GpuShaderBinaryFormat.SpirV).Blob;
            IReadOnlyList<SpirVBinding> fog = SpirVReflection.ReadBindings(fogPs);
            HeadlessHarness.Assert(
                !FindSampled(fog, 0).IsDepthImage, "SceneColor must not be a depth image.");
            // Vulkan SPIR-V uses SceneDepth.Load (texelFetch). SampleCmp on SceneDepth is the
            // WebGPU path only. Shadow maps are SampleCmp on every backend — those must be depth.
            HeadlessHarness.Assert(
                FindSampled(fog, 2).IsDepthImage, "ShadowMapFar must be a comparison image (NEXT-139).");
            HeadlessHarness.Assert(
                FindSampled(fog, 3).IsDepthImage, "ShadowMapNear must be a comparison image (NEXT-139).");
            HeadlessHarness.Assert(
                !FindSampled(fog, 4).IsDepthImage, "FogSkipMask must not be a depth image.");
            HeadlessHarness.Assert(
                FindSampled(fog, 4).IsTextureArray == false, "FogSkipMask must stay a 2D view.");

            byte[] forwardPs = ShaderCompiler.CompileForBackend(
                ForwardShaders.Source, "PS", GpuShaderStage.Pixel, GpuShaderBinaryFormat.SpirV).Blob;
            IReadOnlyList<SpirVBinding> forward = SpirVReflection.ReadBindings(forwardPs);
            HeadlessHarness.Assert(
                FindSampled(forward, 4).IsTextureArray,
                "AlbedoArray must be arrayed so the fallback view is Type2DArray (NEXT-140).");
            HeadlessHarness.Assert(
                FindSampled(forward, 2).IsDepthImage, "Forward ShadowMapFar must be a comparison image.");
            HeadlessHarness.Assert(
                FindSampled(forward, 5).IsDepthImage, "Forward ShadowMapNear must be a comparison image.");
            HeadlessHarness.Assert(
                !FindSampled(forward, 1).IsTextureArray, "AlbedoTex must stay a 2D view.");
        });

        // AetherForge's render graph is deliberately ported as an API-neutral planner first.
        // This proves the dependency/barrier algorithm before either DX11 or Vulkan executes it.
        HeadlessHarness.RunCase(ctx.Report, "Render.Foundation.RenderGraphPlanner", () =>
        {
            RenderGraphBuilder graph = new();
            RenderResourceHandle scene = graph.CreateResource("SceneColor");
            RenderResourceHandle lit = graph.CreateResource("LitColor");
            RenderResourceHandle swap = graph.CreateResource("SwapChain");

            graph.AddPass("Scene", pass => pass.Write(scene, RenderResourceUsage.ColorAttachment));
            graph.AddPass("Lighting", pass =>
            {
                pass.Read(scene, RenderResourceUsage.ShaderRead);
                pass.Write(lit, RenderResourceUsage.ColorAttachment);
            });
            graph.AddPass("Post", pass =>
            {
                pass.Read(lit, RenderResourceUsage.ShaderRead);
                pass.Write(swap, RenderResourceUsage.ColorAttachment);
            });
            graph.AddPass("Present", pass => pass.Read(swap, RenderResourceUsage.Present));

            CompiledRenderGraph compiled = graph.Compile();
            string[] names = compiled.Passes.Select(pass => pass.Name).ToArray();
            HeadlessHarness.Assert(names.SequenceEqual(new[] { "Scene", "Lighting", "Post", "Present" }),
                "Stable render-graph ordering changed unexpectedly.");
            HeadlessHarness.Assert(compiled.Passes[1].Dependencies.SequenceEqual(new[] { 0 }),
                "Lighting does not depend on Scene's write.");
            HeadlessHarness.Assert(compiled.Passes[2].Dependencies.SequenceEqual(new[] { 1 }),
                "Post does not depend on Lighting's write.");
            HeadlessHarness.Assert(compiled.Passes[3].Dependencies.SequenceEqual(new[] { 2 }),
                "Present does not depend on Post's write.");
            HeadlessHarness.Assert(compiled.Barriers.Count == 6,
                $"Expected 6 resource transitions/hazards, got {compiled.Barriers.Count}.");
            HeadlessHarness.Assert(
                compiled.Barriers.Any(barrier =>
                    barrier.Resource == swap &&
                    barrier.Before == RenderResourceUsage.ColorAttachment &&
                    barrier.After == RenderResourceUsage.Present &&
                    barrier.HasWriteHazard),
                "Swap-chain transition to Present was not derived with its write hazard.");
        });

        // Phase 1 texture foundation: one cache implementation is shared by DX11 now and Vulkan
        // later. The test stays CPU-only by using fake handles rather than a graphics device.
        HeadlessHarness.RunCase(ctx.Report, "Render.Foundation.TextureAssetCache", () =>
        {
            string directory = Path.Combine(ctx.OutputRoot, "TextureFoundation");
            Directory.CreateDirectory(directory);
            string source = Path.Combine(directory, "cache-source.bin");
            File.WriteAllBytes(source, new byte[] { 1, 2, 3, 4 });

            TextureAssetCache cache = new();
            int nextHandle = 0;
            int loads = 0;
            List<int> released = new();
            TextureHandle Loader(string _, TextureColorSpace __)
            {
                loads++;
                return new TextureHandle(++nextHandle);
            }
            void Release(TextureHandle handle) => released.Add(handle.Id);

            TextureHandle first = cache.Load(source, TextureColorSpace.Srgb, Loader, Release);
            TextureHandle reused = cache.Load(source, TextureColorSpace.Srgb, Loader, Release);
            HeadlessHarness.Assert(first.IsValid && reused.Id == first.Id && loads == 1,
                "Repeated sRGB texture request did not reuse the uploaded handle.");

            TextureHandle linear = cache.Load(source, TextureColorSpace.Linear, Loader, Release);
            HeadlessHarness.Assert(linear.IsValid && linear.Id != first.Id && loads == 2,
                "Linear and sRGB usage were incorrectly collapsed to one cache entry.");

            File.WriteAllBytes(source, new byte[] { 1, 2, 3, 4, 5 });
            TextureHandle refreshed = cache.Load(source, TextureColorSpace.Srgb, Loader, Release);
            HeadlessHarness.Assert(refreshed.IsValid && refreshed.Id != first.Id && loads == 3,
                "A changed source file returned its stale GPU handle.");
            HeadlessHarness.Assert(released.Contains(first.Id),
                "The stale file-backed GPU handle was not released during refresh.");
            HeadlessHarness.Assert(cache.Reused >= 1 && cache.Invalidated >= 1,
                "Texture cache diagnostics did not report reuse/invalidation.");

            cache.Remove(linear);
            TextureHandle linearReload = cache.Load(source, TextureColorSpace.Linear, Loader, Release);
            HeadlessHarness.Assert(linearReload.Id != linear.Id && loads == 4,
                "Explicitly released texture remained reachable through the cache.");
        });

        // The cook manifest is deliberately proven before the encoder/loader lands. It is the
        // safety contract that stops an edited PNG from accidentally using stale compressed bytes.
        HeadlessHarness.RunCase(ctx.Report, "Render.Foundation.CookedTextureManifest", () =>
        {
            string directory = Path.Combine(ctx.OutputRoot, "TextureFoundation");
            Directory.CreateDirectory(directory);
            string source = Path.Combine(directory, "cook-source.png");
            string cooked = Path.Combine(directory, "cook-source.bc7.dds");
            File.WriteAllBytes(source, new byte[] { 10, 20, 30, 40 });
            File.WriteAllBytes(cooked, new byte[] { 68, 68, 83, 32 });

            CookedTextureManifest manifest = CookedTextureManifestStore.Create(
                source, cooked, CookedTextureFormat.Bc7Color, mipmaps: true);
            CookedTextureManifestStore.Write(source, manifest);

            bool resolved = CookedTextureManifestStore.TryResolve(
                source, out string resolvedPath, out CookedTextureManifest loaded);
            HeadlessHarness.Assert(resolved && Path.GetFullPath(resolvedPath) == Path.GetFullPath(cooked),
                "Fresh cooked texture sidecar did not resolve to its GPU-ready file.");
            HeadlessHarness.Assert(loaded != null && loaded.Format == CookedTextureFormat.Bc7Color && loaded.Mipmaps,
                "Cooked texture format/mipmap intent did not survive manifest round-trip.");

            File.WriteAllBytes(source, new byte[] { 10, 20, 30, 40, 50 });
            HeadlessHarness.Assert(!CookedTextureManifestStore.TryResolve(source, out _, out _),
                "A cooked texture remained valid after its editable source changed.");
        });

        // Phase 1 texture cooking: exercise the real encoder and the DDS reader without needing a
        // graphics device. Small generated TGA sources keep this fast enough for the normal build.
        HeadlessHarness.RunCase(ctx.Report, "Render.Foundation.CookedTexturePipeline", () =>
        {
            string directory = Path.Combine(ctx.OutputRoot, "TextureFoundation", "CookedPipeline");
            Directory.CreateDirectory(directory);
            string colourSource = Path.Combine(directory, "pipeline-colour.tga");
            string normalSource = Path.Combine(directory, "pipeline-normal.tga");
            WriteTestTga(colourSource, 8, 8, normalLike: false);
            WriteTestTga(normalSource, 8, 8, normalLike: true);

            string colourCooked = CookedTextureCooker.Cook(
                colourSource, CookedTextureFormat.Bc7Color, mipmaps: true);
            HeadlessHarness.Assert(File.Exists(colourCooked),
                "BC7 cooker did not produce a DDS file.");
            HeadlessHarness.Assert(
                CookedTextureManifestStore.TryResolve(colourSource, out string resolvedColour, out CookedTextureManifest colourManifest) &&
                string.Equals(Path.GetFullPath(resolvedColour), Path.GetFullPath(colourCooked), StringComparison.OrdinalIgnoreCase),
                "Fresh BC7 cook did not resolve through its sidecar.");
            HeadlessHarness.Assert(colourManifest != null && colourManifest.Format == CookedTextureFormat.Bc7Color,
                "BC7 cook wrote the wrong manifest format.");
            HeadlessHarness.Assert(
                DdsTextureData.TryLoad(colourCooked, TextureColorSpace.Srgb, out DdsTextureData colourDds),
                "Genesis could not parse the BC7 DDS it just cooked.");
            HeadlessHarness.Assert(
                colourDds.Width == 8 && colourDds.Height == 8 &&
                colourDds.MipLevels == GpuTextureLayout.FullMipCount(8, 8) &&
                colourDds.Format == GpuFormat.BC7UNormSrgb,
                $"BC7 DDS metadata was wrong: {colourDds.Width}x{colourDds.Height}, " +
                $"mips={colourDds.MipLevels}, format={colourDds.Format}.");
            HeadlessHarness.Assert(
                colourDds.Payload.Length == GpuTextureLayout.GetMipChainSize(
                    colourDds.Format, colourDds.Width, colourDds.Height, colourDds.MipLevels),
                "BC7 DDS payload does not match Genesis's tightly-packed mip contract.");

            string normalCooked = CookedTextureCooker.Cook(
                normalSource, CookedTextureFormat.Bc5Normal, mipmaps: true);
            HeadlessHarness.Assert(File.Exists(normalCooked),
                "BC5 cooker did not produce a DDS file.");
            HeadlessHarness.Assert(
                DdsTextureData.TryLoad(normalCooked, TextureColorSpace.Linear, out DdsTextureData normalDds),
                "Genesis could not parse the BC5 DDS it just cooked.");
            HeadlessHarness.Assert(
                normalDds.Width == 8 && normalDds.Height == 8 &&
                normalDds.MipLevels == GpuTextureLayout.FullMipCount(8, 8) &&
                normalDds.Format == GpuFormat.BC5UNorm,
                $"BC5 DDS metadata was wrong: {normalDds.Width}x{normalDds.Height}, " +
                $"mips={normalDds.MipLevels}, format={normalDds.Format}.");
            HeadlessHarness.Assert(
                normalDds.Payload.Length == GpuTextureLayout.GetMipChainSize(
                    normalDds.Format, normalDds.Width, normalDds.Height, normalDds.MipLevels),
                "BC5 DDS payload does not match Genesis's tightly-packed mip contract.");

            // A cache entry created from the source path must notice when a fresh cook appears,
            // otherwise a live editor session would keep serving the old uncompressed GPU upload.
            string cacheSource = Path.Combine(directory, "pipeline-cache-source.tga");
            WriteTestTga(cacheSource, 4, 4, normalLike: false);
            TextureAssetCache cache = new();
            int nextHandle = 0;
            int loads = 0;
            List<int> released = new();
            TextureHandle Loader(string _, TextureColorSpace __)
            {
                loads++;
                return new TextureHandle(++nextHandle);
            }
            void Release(TextureHandle handle) => released.Add(handle.Id);

            TextureHandle beforeCook = cache.Load(cacheSource, TextureColorSpace.Srgb, Loader, Release);
            CookedTextureCooker.Cook(cacheSource, CookedTextureFormat.Bc7Color, mipmaps: true);
            TextureHandle afterCook = cache.Load(cacheSource, TextureColorSpace.Srgb, Loader, Release);
            HeadlessHarness.Assert(afterCook.IsValid && afterCook.Id != beforeCook.Id && loads == 2,
                "Creating a fresh cooked DDS did not invalidate the source-only texture cache entry.");
            HeadlessHarness.Assert(released.Contains(beforeCook.Id),
                "The source-only GPU handle was not released after a cooked replacement appeared.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Render.Preferences.GlobalLightingAndShadowsReachPlayer", () =>
        {
            string settingsFile = Path.Combine(ctx.Workspace, "lighting-preferences.json");
            SettingsService settings = new(settingsFile);
            settings.Current.Rendering.LightingEnabled = false;
            settings.Current.Rendering.ShadowsEnabled = true;
            settings.Current.Rendering.ShadowStrength = 0.42f;
            settings.Save();

            SettingsService reopened = new(settingsFile);
            HeadlessHarness.Assert(
                !reopened.Current.Rendering.LightingEnabled
                && reopened.Current.Rendering.ShadowsEnabled
                && Math.Abs(reopened.Current.Rendering.ShadowStrength - 0.42f) < 0.001f,
                "Global lighting/shadow preferences did not survive SettingsService.");

            try
            {
                RenderingPreferencesBridge.Apply(reopened.Current.Rendering);
                Mesh3DState state = Mesh3DState.Default;
                MeshLightingDefaults.Apply(ref state);
                HeadlessHarness.Assert(
                    !state.LightingEnabled && !state.ShadowsEnabled
                    && Math.Abs(state.ShadowStrength - 0.42f) < 0.001f,
                    "The renderer boundary did not apply the global lighting/shadow defaults.");

                ProjectManifest manifest = new()
                {
                    ProjectId = Guid.NewGuid().ToString("N"),
                    Name = "Lighting preference seam",
                };
                IDictionary<string, string> environment =
                    RenderingPreferencesBridge.BuildPlayerEnvironment(
                        reopened.Current.Rendering,
                        manifest);
                HeadlessHarness.Assert(
                    environment[MeshLightingDefaults.LightingEnvironmentVariable] == "0"
                    && environment[MeshLightingDefaults.ShadowsEnvironmentVariable] == "1"
                    && environment[MeshLightingDefaults.ShadowStrengthEnvironmentVariable] == "0.42",
                    "F5 would launch the Player without the authored global lighting/shadow defaults.");
            }
            finally
            {
                MeshLightingDefaults.Configure(true, true, 1f);
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Render.Preferences.NamedCapsReachPlayer", () =>
        {
            string settingsFile = Path.Combine(ctx.Workspace, "capacity-preferences.json");
            SettingsService settings = new(settingsFile);
            settings.Current.Rendering.SpriteInstanceCap = 1024;
            settings.Current.Rendering.MeshInstanceCap = 2048;
            settings.Current.Rendering.SceneLocalLightCap = 512;
            settings.Current.Rendering.DrawCallMode = RenderCapacityDefaults.DrawCallModeManual;
            settings.Current.Rendering.WorldDrawBudget = 42;
            settings.Save();

            SettingsService reopened = new(settingsFile);
            HeadlessHarness.Assert(
                reopened.Current.Rendering.SpriteInstanceCap == 1024
                && reopened.Current.Rendering.MeshInstanceCap == 2048
                && reopened.Current.Rendering.SceneLocalLightCap == 512
                && string.Equals(
                    reopened.Current.Rendering.DrawCallMode,
                    RenderCapacityDefaults.DrawCallModeManual,
                    StringComparison.OrdinalIgnoreCase)
                && reopened.Current.Rendering.WorldDrawBudget == 42,
                "Named render-capacity preferences did not survive SettingsService.");

            try
            {
                RenderingPreferencesBridge.Apply(reopened.Current.Rendering);
                HeadlessHarness.Assert(
                    RenderCapacityDefaults.SpriteInstanceCap == 1024
                    && RenderCapacityDefaults.MeshInstanceCap == 2048
                    && RenderCapacityDefaults.SceneLocalLightCap == 512
                    && RenderCapacityDefaults.EffectiveShadedLightCap == 512
                    && RenderCapacityDefaults.IsManualWorldDrawBudget
                    && RenderCapacityDefaults.EffectiveWorldDrawBudget == 42,
                    "RenderCapacityDefaults did not apply soft caps / Manual world-draw budget "
                    + "(scene local light soft cap must match preference after R7.5).");

                ProjectManifest manifest = new()
                {
                    ProjectId = Guid.NewGuid().ToString("N"),
                    Name = "Capacity preference seam",
                };
                IDictionary<string, string> environment =
                    RenderingPreferencesBridge.BuildPlayerEnvironment(
                        reopened.Current.Rendering,
                        manifest);
                HeadlessHarness.Assert(
                    environment[RenderCapacityDefaults.SpriteInstanceCapEnvironmentVariable] == "1024"
                    && environment[RenderCapacityDefaults.MeshInstanceCapEnvironmentVariable] == "2048"
                    && environment[RenderCapacityDefaults.SceneLocalLightCapEnvironmentVariable] == "512"
                    && environment[RenderCapacityDefaults.DrawCallModeEnvironmentVariable]
                        == RenderCapacityDefaults.DrawCallModeManual
                    && environment[RenderCapacityDefaults.WorldDrawBudgetEnvironmentVariable] == "42",
                    "F5 would launch the Player without the authored capacity preferences.");
            }
            finally
            {
                RenderCapacityDefaults.Configure(
                    RenderCapacityDefaults.HardwareSpriteInstanceCap,
                    RenderCapacityDefaults.HardwareMeshInstanceCap,
                    RenderCapacityDefaults.DefaultSceneLocalLightCap,
                    RenderCapacityDefaults.DrawCallModeAuto,
                    RenderCapacityDefaults.MaxWorldDrawBudget);
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Render.Lights.TiledClusterAssignsBoundedPerTile", () =>
        {
            ClusterPointLightGpu[] lights = new ClusterPointLightGpu[64];
            for (int i = 0; i < lights.Length; i++)
            {
                float x = (i % 8) * 4f - 14f;
                float z = (i / 8) * 4f - 14f;
                lights[i] = new ClusterPointLightGpu
                {
                    PosRadius = new Vector4(x, 1f, z, 6f),
                    ColorIntensity = new Vector4(1f, 1f, 1f, 1f),
                    FalloffPad = new Vector4(2f, 0f, 0f, 0f),
                };
            }

            Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0f, 20f, 40f), Vector3.Zero, Vector3.UnitY);
            Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, 16f / 9f, 0.1f, 200f);
            TiledLightGrid grid = new();
            grid.Build(lights, view * proj, 1280, 720, new Vector3(0f, 20f, 40f));

            int occupied = 0;
            int maxInTile = 0;
            ReadOnlySpan<uint> indices = grid.Indices;
            for (int tile = 0; tile < TiledLightDefaults.TileCount; tile++)
            {
                int count = 0;
                int baseIdx = tile * TiledLightDefaults.MaxLightsPerTile;
                for (int slot = 0; slot < TiledLightDefaults.MaxLightsPerTile; slot++)
                {
                    if (indices[baseIdx + slot] == TiledLightDefaults.EmptyLightIndex)
                        break;
                    count++;
                }

                if (count > 0) occupied++;
                maxInTile = Math.Max(maxInTile, count);
            }

            HeadlessHarness.Assert(occupied > 0, "Tiled light grid assigned no tiles.");
            HeadlessHarness.Assert(
                maxInTile <= TiledLightDefaults.MaxLightsPerTile,
                $"A tile evaluated {maxInTile} lights — R7.5 requires ≤{TiledLightDefaults.MaxLightsPerTile}.");

            int kept = TiledLightGrid.SelectStrongest(
                lights.AsSpan(), lights.Length, 16, new Vector3(0f, 20f, 40f));
            HeadlessHarness.Assert(kept == 16, "Over-cap selection must keep the strongest N lights.");
        });

        // R7.11: practical frustum splits + texel-scaled bias (still two cascades; AF1.1 owns a third).
        HeadlessHarness.RunCase(ctx.Report, "Render.Shadows.AutoCascadeSplitsAndBias", () =>
        {
            CascadeShadowFrame tight = CascadeShadowMath.Compute(
                cameraNear: 0.1f,
                cameraFar: 80f,
                orthoSize: 26f,
                fieldOfViewY: MathF.PI / 3f,
                aspectRatio: 16f / 9f,
                farMapSize: 1024,
                nearMapSize: 1024,
                authoredBias: 0.0015f);
            CascadeShadowFrame wide = CascadeShadowMath.Compute(
                cameraNear: 0.1f,
                cameraFar: 400f,
                orthoSize: 80f,
                fieldOfViewY: MathF.PI / 3f,
                aspectRatio: 16f / 9f,
                farMapSize: 1024,
                nearMapSize: 1024,
                authoredBias: 0.0015f);

            HeadlessHarness.Assert(
                tight.NearExtent >= 8f && tight.NearExtent <= tight.FarExtent * 0.5f + 0.01f,
                $"Near cascade extent out of range ({tight.NearExtent} vs far {tight.FarExtent}).");
            HeadlessHarness.Assert(
                tight.NearSplitDistance > 0f && tight.FarExtent >= 16f,
                "Cascade frame must publish a positive near split and far extent.");
            HeadlessHarness.Assert(
                wide.NearSplitDistance > tight.NearSplitDistance
                || wide.FarExtent > tight.FarExtent,
                "Wider camera far / ortho must grow cascade coverage.");
            HeadlessHarness.Assert(
                CascadeShadowMath.AdaptiveDepthBias(0.0015f, 0.2f)
                > CascadeShadowMath.AdaptiveDepthBias(0.0015f, 0.05f),
                "Adaptive bias must grow when world texels get coarser.");
            HeadlessHarness.Assert(
                MathF.Abs(CascadeShadowMath.AdaptiveDepthBias(0.0015f, 0.05f) - 0.0015f) < 0.0003f,
                "Typical 1024² cascade texels must stay near the authored bias floor.");
        });

        // AF1.1: third cascade extents + ForestLight light-space centre snap stability.
        HeadlessHarness.RunCase(ctx.Report, "Render.Shadows.ThreeCascadeSplitsAndTexelSnap", () =>
        {
            CascadeShadowFrame two = CascadeShadowMath.Compute(
                0.1f, 200f, 80f, MathF.PI / 3f, 16f / 9f, 1024, 1024, 0.0015f, cascadeCount: 2);
            CascadeShadowFrame three = CascadeShadowMath.Compute(
                0.1f, 200f, 80f, MathF.PI / 3f, 16f / 9f, 1024, 1024, 0.0015f, cascadeCount: 3);

            HeadlessHarness.Assert(two.CascadeCount == 2, "Default cascade count must stay 2.");
            HeadlessHarness.Assert(
                MathF.Abs(two.NearExtent - three.NearExtent) < 0.05f,
                "Near extent must stay stable when enabling the third cascade.");
            HeadlessHarness.Assert(
                three.CascadeCount == 3
                && three.NearExtent < three.MidExtent
                && three.MidExtent < three.FarExtent,
                $"Three-cascade extents must nest (n={three.NearExtent}, m={three.MidExtent}, f={three.FarExtent}).");
            HeadlessHarness.Assert(
                three.NearSplitDistance < three.MidSplitDistance,
                "Mid split must sit beyond the near split.");

            Vector3 lightDir = Vector3.Normalize(new Vector3(-0.4f, -0.85f, -0.35f));
            float texel = three.MidTexelWorld;
            Vector3 centre = new(12.3f, 4f, -7.8f);
            Vector3 snapped = CascadeShadowMath.SnapCascadeCentre(centre, lightDir, texel);
            Vector3 provisionalUp = MathF.Abs(Vector3.Dot(lightDir, Vector3.UnitY)) > 0.94f
                ? Vector3.UnitZ
                : Vector3.UnitY;
            Vector3 lightRight = Vector3.Normalize(Vector3.Cross(provisionalUp, lightDir));

            Vector3 nudged = CascadeShadowMath.SnapCascadeCentre(
                centre + lightRight * (texel * 0.25f), lightDir, texel);
            HeadlessHarness.Assert(
                Vector3.Distance(snapped, nudged) < 1e-4f,
                "Sub-texel camera crawl must not move the snapped cascade centre.");

            Vector3 stepped = CascadeShadowMath.SnapCascadeCentre(
                centre + lightRight * (texel * 1.1f), lightDir, texel);
            HeadlessHarness.Assert(
                Vector3.Distance(snapped, stepped) > texel * 0.5f,
                "A >1-texel light-space move must advance the snapped centre.");
        });

        // AF1.2: GTAO math + enable wiring (default off; Software skip).
        HeadlessHarness.RunCase(ctx.Report, "Render.Gtao.MathAndEnableWiring", () =>
        {
            HeadlessHarness.Assert(
                !Mesh3DState.Default.GtaoEnabled && !MeshLightingDefaults.GtaoEnabled,
                "GTAO must default off for low caps.");

            // Sky depth → fully lit.
            float[] sky = new float[8 * 8];
            Array.Fill(sky, 1f);
            float skyAo = GtaoMath.EvaluateHorizonContact(sky, 8, 8, 4, 4, 0.1f, 200f);
            HeadlessHarness.Assert(
                MathF.Abs(skyAo - 1f) < 1e-5f,
                $"Sky depth must return AO=1 (got {skyAo:F3}).");

            // Open mid-field depth stays near fully lit (no nearby occluders in a flat field).
            float[] flat = new float[16 * 16];
            Array.Fill(flat, 0.4f);
            float flatAo = GtaoMath.EvaluateHorizonContact(flat, 16, 16, 8, 8, 0.1f, 200f);
            HeadlessHarness.Assert(
                flatAo > 0.9f,
                $"Flat mid-field depth should stay mostly lit (got {flatAo:F3}).");

            float nearZ = GtaoMath.LinearizeDepth(0.1f, 0.1f, 200f);
            float farZ = GtaoMath.LinearizeDepth(0.9f, 0.1f, 200f);
            HeadlessHarness.Assert(
                nearZ < farZ,
                "LinearizeDepth must grow with depth buffer value.");

            float edge = GtaoMath.BilateralDepthWeight(10f, 10.05f);
            float far = GtaoMath.BilateralDepthWeight(10f, 14f);
            HeadlessHarness.Assert(
                edge > far,
                "Bilateral depth weight must prefer matching depths.");

            MeshLightingDefaults.Configure(true, true, 1f, 2, gtaoEnabled: false);
            Mesh3DState off = Mesh3DState.Default;
            MeshLightingDefaults.Apply(ref off);
            HeadlessHarness.Assert(!off.GtaoEnabled, "Prefs off must leave GTAO disabled.");

            MeshLightingDefaults.Configure(true, true, 1f, 2, gtaoEnabled: true);
            Mesh3DState on = Mesh3DState.Default;
            MeshLightingDefaults.Apply(ref on);
            HeadlessHarness.Assert(on.GtaoEnabled, "Prefs on must enable GTAO on Mesh3DState.");

            // Restore default-off so later cases are not polluted.
            MeshLightingDefaults.Configure(true, true, 1f, 2, gtaoEnabled: false);
        });

        // AF1.4: contact-shadow march + enable wiring (default off; Software skip).
        HeadlessHarness.RunCase(ctx.Report, "Render.ContactShadows.MathAndEnableWiring", () =>
        {
            HeadlessHarness.Assert(
                !Mesh3DState.Default.ContactShadowsEnabled
                && !MeshLightingDefaults.ContactShadowsEnabled,
                "Contact shadows must default off for low caps.");

            // Straight down: the march then runs straight up the screen, which keeps the fixtures
            // below readable and matches the engine's travel-direction convention for the sun.
            var sunTravel = new Vector3(0f, -1f, 0f);

            // Sky depth → nothing to occlude.
            float[] sky = new float[32 * 32];
            Array.Fill(sky, 1f);
            float skyOcclusion = ContactShadowMath.EvaluateOcclusion(
                sky, 32, 32, 16, 16, 0.1f, 200f, sunTravel);
            HeadlessHarness.Assert(
                skyOcclusion < 1e-5f,
                $"Sky depth must produce no contact shadow (got {skyOcclusion:F4}).");

            // A flat field has nothing standing between the surface and the sun.
            float[] flat = new float[32 * 32];
            Array.Fill(flat, 0.99f);
            float flatOcclusion = ContactShadowMath.EvaluateOcclusion(
                flat, 32, 32, 16, 16, 0.1f, 200f, sunTravel);
            HeadlessHarness.Assert(
                flatOcclusion < 0.02f,
                $"Flat depth field must stay essentially unoccluded (got {flatOcclusion:F4}).");

            // Same field with a nearer band toward the light (up the screen) — a raised occluder
            // the cascades would miss at this range.
            float[] occluded = new float[32 * 32];
            Array.Fill(occluded, 0.99f);
            for (int y = 8; y <= 14; y++)
            {
                for (int x = 0; x < 32; x++)
                    occluded[y * 32 + x] = 0.985f;
            }

            float occlusion = ContactShadowMath.EvaluateOcclusion(
                occluded, 32, 32, 16, 16, 0.1f, 200f, sunTravel);
            HeadlessHarness.Assert(
                occlusion > 0.05f && occlusion <= ContactShadowMath.MaxOcclusion,
                $"An occluder toward the light must cast a clamped contact shadow (got {occlusion:F4}).");

            // Marching away from the light must not find the same occluder.
            float behind = ContactShadowMath.EvaluateOcclusion(
                occluded, 32, 32, 16, 16, 0.1f, 200f, new Vector3(0f, 1f, 0f));
            HeadlessHarness.Assert(
                behind < occlusion,
                $"Reversing the sun must not reproduce the same occlusion ({behind:F4} vs {occlusion:F4}).");

            HeadlessHarness.Assert(
                ContactShadowMath.DirectionTowardLight(sunTravel).Y > 0.99f,
                "DirectionTowardLight must invert the engine's travel-direction convention.");

            HeadlessHarness.Assert(
                ContactShadowMath.EffectiveStrength(gtaoEnabled: false)
                > ContactShadowMath.EffectiveStrength(gtaoEnabled: true),
                "Contact strength must be pulled back when GTAO already darkens the same creases.");

            MeshLightingDefaults.Configure(true, true, 1f, 2, false, contactShadowsEnabled: false);
            Mesh3DState contactOff = Mesh3DState.Default;
            MeshLightingDefaults.Apply(ref contactOff);
            HeadlessHarness.Assert(
                !contactOff.ContactShadowsEnabled,
                "Prefs off must leave contact shadows disabled.");

            MeshLightingDefaults.Configure(true, true, 1f, 2, false, contactShadowsEnabled: true);
            Mesh3DState contactOn = Mesh3DState.Default;
            MeshLightingDefaults.Apply(ref contactOn);
            HeadlessHarness.Assert(
                contactOn.ContactShadowsEnabled,
                "Prefs on must enable contact shadows on Mesh3DState.");
            HeadlessHarness.Assert(
                !contactOn.GtaoEnabled,
                "Enabling contact shadows must not drag GTAO on with it.");

            // Restore default-off (contact and GTAO both) so later cases are not polluted.
            MeshLightingDefaults.Configure(true, true, 1f, 2, false, false);
        });

        // AF1.5: bounded local-light volumetric scatter (default off; Software skip; never a
        // 3-in-4 frame schedule — the budget is the cost control, not a reduced update rate).
        HeadlessHarness.RunCase(ctx.Report, "Render.LocalVolumetrics.MathAndEnableWiring", () =>
        {
            HeadlessHarness.Assert(
                !Mesh3DState.Default.LocalVolumetricsEnabled
                && !MeshLightingDefaults.LocalVolumetricsEnabled,
                "Local volumetric scatter must default off for low caps.");

            HeadlessHarness.Assert(
                LocalVolumetricMath.DefaultBudget == 1
                && RenderCapacityDefaults.DefaultLocalVolumetricLightBudget == 1,
                "Default local volumetric budget must be one torch, not one beam per light.");
            HeadlessHarness.Assert(
                LocalVolumetricMath.ClampBudget(99) == LocalVolumetricMath.MaxBudget
                && LocalVolumetricMath.ClampBudget(-3) == LocalVolumetricMath.MinBudget,
                "Local volumetric budget must clamp to 0..4.");
            HeadlessHarness.Assert(
                LocalVolumetricMath.StepCount <= LocalVolumetricMath.MaxStepCount
                && LocalVolumetricMath.MaxStepCount == 12,
                "The march must stay bounded at 12 steps — never ForestLight's 24-step atmosphere.");

            // Camera at the origin looking down -Z, the engine's yaw-zero forward.
            var origin = Vector3.Zero;
            var forward = new Vector3(0f, 0f, -1f);

            float emptyScatter = LocalVolumetricMath.EvaluateScatter(
                ReadOnlySpan<ClusterPointLightGpu>.Empty, origin, forward, 10f);
            HeadlessHarness.Assert(
                emptyScatter < 1e-5f,
                $"No local lights must produce no inscatter (got {emptyScatter:F5}).");

            // Bright torch sitting on the ray four units ahead.
            var near = new ClusterPointLightGpu[1];
            near[0] = new ClusterPointLightGpu
            {
                PosRadius = new Vector4(0f, 0f, -4f, 6f),
                ColorIntensity = new Vector4(1f, 0.6f, 0.2f, 8f),
            };
            float nearScatter = LocalVolumetricMath.EvaluateScatter(near, origin, forward, 10f);
            HeadlessHarness.Assert(
                nearScatter > 0.01f && nearScatter <= LocalVolumetricMath.MaxScatter,
                $"A strong torch on the view ray must scatter, clamped (got {nearScatter:F4}).");

            // Same torch parked far outside its own radius of every sample on the ray.
            var far = new ClusterPointLightGpu[1];
            far[0] = new ClusterPointLightGpu
            {
                PosRadius = new Vector4(0f, 0f, -400f, 6f),
                ColorIntensity = new Vector4(1f, 0.6f, 0.2f, 8f),
            };
            float farScatter = LocalVolumetricMath.EvaluateScatter(far, origin, forward, 10f);
            HeadlessHarness.Assert(
                farScatter < 1e-5f,
                $"A light outside its radius of the whole ray must not scatter (got {farScatter:F5}).");

            // A dim, distant-but-in-range light must not out-scatter the bright near one.
            var weak = new ClusterPointLightGpu[1];
            weak[0] = new ClusterPointLightGpu
            {
                PosRadius = new Vector4(0f, 0f, -11f, 3f),
                ColorIntensity = new Vector4(1f, 0.6f, 0.2f, 0.4f),
            };
            float weakScatter = LocalVolumetricMath.EvaluateScatter(weak, origin, forward, 10f);
            HeadlessHarness.Assert(
                nearScatter > weakScatter,
                $"Strong near light must out-scatter a weak far one ({nearScatter:F4} vs {weakScatter:F4}).");

            // A zero budget is the "off" position of the same dial, not a separate toggle.
            float unbudgeted = LocalVolumetricMath.EvaluateScatter(near, origin, forward, 10f, budget: 0);
            HeadlessHarness.Assert(
                unbudgeted < 1e-5f,
                $"Budget zero must produce no inscatter (got {unbudgeted:F5}).");

            // Selection is OmniShadowMath's, so the beam and the AF1.3 cubemap agree on the torch.
            var lights = new ClusterPointLightGpu[3];
            lights[0] = new ClusterPointLightGpu
            {
                PosRadius = new Vector4(60f, 0f, 0f, 4f),
                ColorIntensity = new Vector4(1f, 1f, 1f, 2f),
            };
            lights[1] = new ClusterPointLightGpu
            {
                PosRadius = new Vector4(1f, 0f, -2f, 5f),
                ColorIntensity = new Vector4(1f, 0.6f, 0.2f, 9f),
            };
            lights[2] = new ClusterPointLightGpu
            {
                PosRadius = new Vector4(-40f, 0f, 0f, 3f),
                ColorIntensity = new Vector4(1f, 1f, 1f, 1f),
            };
            Span<int> slots = stackalloc int[LocalVolumetricMath.MaxBudget];
            int selected = LocalVolumetricMath.SelectLights(lights, budget: 1, cameraPos: origin, slots);
            HeadlessHarness.Assert(
                selected == 1 && slots[0] == 1,
                $"Budget-1 selection must pick the nearest bright torch (got n={selected}, slot={slots[0]}).");

            MeshLightingDefaults.Configure(true, true, 1f, 2, false, false, localVolumetricsEnabled: false);
            Mesh3DState localVolOff = Mesh3DState.Default;
            MeshLightingDefaults.Apply(ref localVolOff);
            HeadlessHarness.Assert(
                !localVolOff.LocalVolumetricsEnabled,
                "Prefs off must leave local volumetric scatter disabled.");

            MeshLightingDefaults.Configure(true, true, 1f, 2, false, false, localVolumetricsEnabled: true);
            Mesh3DState localVolOn = Mesh3DState.Default;
            MeshLightingDefaults.Apply(ref localVolOn);
            HeadlessHarness.Assert(
                localVolOn.LocalVolumetricsEnabled,
                "Prefs on must enable local volumetric scatter on Mesh3DState.");
            HeadlessHarness.Assert(
                !localVolOn.GtaoEnabled && !localVolOn.ContactShadowsEnabled,
                "Enabling local volumetrics must not drag GTAO or contact shadows on with it.");

            // Restore default-off across AF1.2/1.4/1.5/1.6 toggles.
            MeshLightingDefaults.Configure(true, true, 1f, 2, false, false, false, false);
        });

        // AF1.6: particle smoke as one Beer-Lambert transmittance term in FogPost (default off;
        // Software skip; Alpha-blend emitters only — Additive fire must not feed extinction).
        HeadlessHarness.RunCase(ctx.Report, "Render.SmokeExtinction.MathAndEnableWiring", () =>
        {
            HeadlessHarness.Assert(
                !Mesh3DState.Default.SmokeExtinctionEnabled
                && !MeshLightingDefaults.SmokeExtinctionEnabled,
                "Smoke extinction must default off for low caps.");
            HeadlessHarness.Assert(
                SmokeExtinctionMath.MaxVolumes == 8
                && SmokeExtinctionMath.DefaultExtinctionCoefficient == 1.35f,
                "Smoke extinction must keep ForestLight's 8-volume / 1.35 coefficient ceiling.");

            var camera = Vector3.Zero;
            var surface = new Vector3(0f, 0f, -10f);

            float emptyT = SmokeExtinctionMath.Transmittance(
                surface, camera, ReadOnlySpan<SmokeExtinctionMath.SmokeVolume>.Empty);
            HeadlessHarness.Assert(
                MathF.Abs(emptyT - 1f) < 1e-5f,
                $"Empty volumes must leave transmittance at 1 (got {emptyT:F5}).");

            var dense = new SmokeExtinctionMath.SmokeVolume[1];
            dense[0] = new SmokeExtinctionMath.SmokeVolume(
                new Vector3(0f, 0f, -5f), Radius: 3f, Density: 0.5f);
            float denseT = SmokeExtinctionMath.Transmittance(surface, camera, dense);
            HeadlessHarness.Assert(
                denseT > 0f && denseT < 1f,
                $"A dense sphere on the view ray must attenuate into (0,1) (got {denseT:F4}).");

            // Same sphere, shorter chord: surface just behind the near face rather than past the
            // far side — optical depth must drop and transmittance must rise.
            var shortSurface = new Vector3(0f, 0f, -3.5f);
            float shortT = SmokeExtinctionMath.Transmittance(shortSurface, camera, dense);
            HeadlessHarness.Assert(
                shortT > denseT,
                $"A shorter chord must transmit more ({shortT:F4} vs long {denseT:F4}).");

            // Parallel ray at the same depth that only grazes the sphere: shorter chord → higher T
            // than the through-centre path. (Extending the far endpoint past a full diameter does
            // not change optical depth — only the chord inside the sphere counts.)
            var grazeSurface = new Vector3(2.6f, 0f, -10f);
            float grazeT = SmokeExtinctionMath.Transmittance(grazeSurface, camera, dense);
            HeadlessHarness.Assert(
                denseT < grazeT && denseT > 0f,
                $"A longer through-centre chord must transmit less than a graze ({denseT:F4} vs {grazeT:F4}).");

            float centreChord = SmokeExtinctionMath.SegmentSphereLength(
                surface, camera, dense[0].Position, dense[0].Radius);
            float grazeChord = SmokeExtinctionMath.SegmentSphereLength(
                grazeSurface, camera, dense[0].Position, dense[0].Radius);
            HeadlessHarness.Assert(
                centreChord > grazeChord && grazeChord > 0f,
                $"Through-centre chord must exceed graze ({centreChord:F3} vs {grazeChord:F3}).");

            // Spatial binning must emit at most MaxVolumes and never invent volumes from silence.
            Span<SmokeExtinctionMath.SmokeVolume> built = stackalloc SmokeExtinctionMath.SmokeVolume[8];
            int fromEmpty = SmokeExtinctionMath.BuildVolumesFromSamples(
                ReadOnlySpan<(Vector3, float)>.Empty, built);
            HeadlessHarness.Assert(fromEmpty == 0, "No samples must emit no smoke volumes.");

            var samples = new (Vector3 Position, float Weight)[12];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (new Vector3(i * 0.1f, 0f, -2f), 0.4f);
            int fromCluster = SmokeExtinctionMath.BuildVolumesFromSamples(samples, built);
            HeadlessHarness.Assert(
                fromCluster > 0 && fromCluster <= SmokeExtinctionMath.MaxVolumes,
                $"A live cluster must emit 1..{SmokeExtinctionMath.MaxVolumes} volumes (got {fromCluster}).");

            MeshLightingDefaults.Configure(
                true, true, 1f, 2, false, false, localVolumetricsEnabled: false, smokeExtinctionEnabled: false);
            Mesh3DState smokeOff = Mesh3DState.Default;
            MeshLightingDefaults.Apply(ref smokeOff);
            HeadlessHarness.Assert(
                !smokeOff.SmokeExtinctionEnabled,
                "Prefs off must leave smoke extinction disabled.");

            MeshLightingDefaults.Configure(
                true, true, 1f, 2, false, false, localVolumetricsEnabled: false, smokeExtinctionEnabled: true);
            Mesh3DState smokeOn = Mesh3DState.Default;
            MeshLightingDefaults.Apply(ref smokeOn);
            HeadlessHarness.Assert(
                smokeOn.SmokeExtinctionEnabled,
                "Prefs on must enable smoke extinction on Mesh3DState.");
            HeadlessHarness.Assert(
                !smokeOn.GtaoEnabled
                && !smokeOn.ContactShadowsEnabled
                && !smokeOn.LocalVolumetricsEnabled,
                "Enabling smoke extinction must not drag GTAO, contact shadows, or local volumetrics on.");

            // Restore default-off across AF1.2/1.4/1.5/1.6 toggles.
            MeshLightingDefaults.Configure(true, true, 1f, 2, false, false, false, false);
        });

        // AF1.7: HDR bloom threshold + exposure/grade/vignette identity defaults (default bloom
        // off; Software skips the pyramid; ACES remains the sole tonemap).
        HeadlessHarness.RunCase(ctx.Report, "Render.BloomGrading.MathAndEnableWiring", () =>
        {
            HeadlessHarness.Assert(
                !Mesh3DState.Default.BloomEnabled
                && !MeshLightingDefaults.BloomEnabled,
                "Bloom must default off so DX11/DX12 goldens do not churn.");
            HeadlessHarness.Assert(
                MathF.Abs(Mesh3DState.Default.Exposure - BloomGradingMath.DefaultExposure) < 1e-5f
                && MathF.Abs(Mesh3DState.Default.Contrast - BloomGradingMath.DefaultContrast) < 1e-5f
                && MathF.Abs(Mesh3DState.Default.Saturation - BloomGradingMath.DefaultSaturation) < 1e-5f
                && MathF.Abs(Mesh3DState.Default.VignetteStrength - BloomGradingMath.DefaultVignette) < 1e-5f,
                "Exposure/contrast/saturation/vignette must default to identity.");

            Vector3 dim = BloomGradingMath.ThresholdExtract(new Vector3(0.2f, 0.2f, 0.2f), 1f);
            HeadlessHarness.Assert(
                dim.LengthSquared() < 1e-8f,
                "A dim pixel below threshold must extract to zero.");
            Vector3 bright = BloomGradingMath.ThresholdExtract(new Vector3(2f, 2f, 2f), 1f);
            HeadlessHarness.Assert(
                bright.X > 0f && bright.Y > 0f && bright.Z > 0f,
                "A bright pixel above threshold must extract a positive contribution.");

            Vector3 baseColor = new(0.4f, 0.3f, 0.2f);
            Vector3 exposed = BloomGradingMath.ApplyExposure(baseColor, 2f);
            HeadlessHarness.Assert(
                MathF.Abs(exposed.X - baseColor.X * 2f) < 1e-5f
                && MathF.Abs(exposed.Y - baseColor.Y * 2f) < 1e-5f
                && MathF.Abs(exposed.Z - baseColor.Z * 2f) < 1e-5f,
                "Exposure 2 must double the colour.");

            Vector3 graded = BloomGradingMath.ApplyContrastSaturation(baseColor, 1f, 1f);
            HeadlessHarness.Assert(
                MathF.Abs(graded.X - baseColor.X) < 1e-5f
                && MathF.Abs(graded.Y - baseColor.Y) < 1e-5f
                && MathF.Abs(graded.Z - baseColor.Z) < 1e-5f,
                "Contrast/saturation at 1 must leave colour unchanged.");

            float center0 = BloomGradingMath.ApplyVignette(0.5f, 0.5f, 0f);
            float edge0 = BloomGradingMath.ApplyVignette(0f, 0f, 0f);
            HeadlessHarness.Assert(
                MathF.Abs(center0 - 1f) < 1e-5f && MathF.Abs(edge0 - 1f) < 1e-5f,
                "Vignette strength 0 must be 1 at centre and edge.");
            float centerOn = BloomGradingMath.ApplyVignette(0.5f, 0.5f, 0.75f);
            float edgeOn = BloomGradingMath.ApplyVignette(0f, 0f, 0.75f);
            HeadlessHarness.Assert(
                centerOn > edgeOn && edgeOn < 1f,
                $"Vignette strength > 0 must darken edges more than centre ({edgeOn:F3} vs {centerOn:F3}).");

            MeshLightingDefaults.Configure(
                true, true, 1f, 2, false, false, false, false, bloomEnabled: false);
            Mesh3DState bloomOff = Mesh3DState.Default;
            MeshLightingDefaults.Apply(ref bloomOff);
            HeadlessHarness.Assert(
                !bloomOff.BloomEnabled,
                "Prefs off must leave bloom disabled.");

            MeshLightingDefaults.Configure(
                true, true, 1f, 2, false, false, false, false, bloomEnabled: true);
            Mesh3DState bloomOn = Mesh3DState.Default;
            MeshLightingDefaults.Apply(ref bloomOn);
            HeadlessHarness.Assert(
                bloomOn.BloomEnabled,
                "Prefs on must enable bloom on Mesh3DState.");
            HeadlessHarness.Assert(
                !bloomOn.GtaoEnabled
                && !bloomOn.ContactShadowsEnabled
                && !bloomOn.LocalVolumetricsEnabled
                && !bloomOn.SmokeExtinctionEnabled,
                "Enabling bloom must not drag earlier AF toggles on.");

            // Restore default-off across AF1.2/1.4/1.5/1.6/1.7 toggles.
            MeshLightingDefaults.Configure(true, true, 1f, 2, false, false, false, false, false);
        });

        // AF1.8: Engine.Rendering PGSL properties ↔ MeshLightingDefaults / RenderCapacityDefaults;
        // prefs labels match F6 (AO/CS/LV/SE/BL/AL). No GPU. R7.5 over-cap still keeps strongest.
        HeadlessHarness.RunCase(ctx.Report, "Render.EngineRendering.PgslAndPrefsWiring", () =>
        {
            PgslCommands.WarmRegistry();

            string[] qualityProps =
            [
                "Engine.Rendering.GtaoEnabled",
                "Engine.Rendering.ContactShadowsEnabled",
                "Engine.Rendering.LocalVolumetricsEnabled",
                "Engine.Rendering.SmokeExtinctionEnabled",
                "Engine.Rendering.BloomEnabled",
                "Engine.Rendering.AtmosphereLutEnabled",
                "Engine.Rendering.CelestialExtrasEnabled",
                "Engine.Rendering.CascadeCount",
                "Engine.Rendering.LocalVolumetricLightBudget",
                "Engine.Rendering.OmniShadowBudget",
                "Engine.Rendering.Exposure",
            ];
            foreach (string qualified in qualityProps)
            {
                PgslCommandInfo info = PgslCommandRegistry.TryGet(qualified);
                HeadlessHarness.Assert(
                    info is { IsProperty: true, IsImplemented: true },
                    $"AF1.8 property '{qualified}' must be registered as an implemented PGSL property.");
            }

            // Round-trip AF quality toggles without dragging siblings / wiping capacity caps.
            PgslCommands.GtaoEnabled = true;
            HeadlessHarness.Assert(MeshLightingDefaults.GtaoEnabled && PgslCommands.GtaoEnabled,
                "GtaoEnabled setter must round-trip MeshLightingDefaults.");
            HeadlessHarness.Assert(
                !MeshLightingDefaults.ContactShadowsEnabled
                && !MeshLightingDefaults.LocalVolumetricsEnabled
                && !MeshLightingDefaults.SmokeExtinctionEnabled
                && !MeshLightingDefaults.BloomEnabled
                && !MeshLightingDefaults.AtmosphereLutEnabled,
                "Enabling AO must not drag CS/LV/SE/BL/AL on.");

            PgslCommands.ContactShadowsEnabled = true;
            PgslCommands.LocalVolumetricsEnabled = true;
            PgslCommands.SmokeExtinctionEnabled = true;
            PgslCommands.BloomEnabled = true;
            PgslCommands.AtmosphereLutEnabled = true;
            HeadlessHarness.Assert(
                MeshLightingDefaults.ContactShadowsEnabled
                && MeshLightingDefaults.LocalVolumetricsEnabled
                && MeshLightingDefaults.SmokeExtinctionEnabled
                && MeshLightingDefaults.BloomEnabled
                && MeshLightingDefaults.AtmosphereLutEnabled,
                "CS/LV/SE/BL/AL setters must round-trip MeshLightingDefaults.");

            PgslCommands.CascadeCount = 99;
            HeadlessHarness.Assert(
                MeshLightingDefaults.ShadowCascadeCount == 3 && PgslCommands.CascadeCount == 3,
                "CascadeCount must clamp to 3 when above the AF1.1 ceiling.");
            PgslCommands.CascadeCount = 1;
            HeadlessHarness.Assert(
                MeshLightingDefaults.ShadowCascadeCount == 2 && PgslCommands.CascadeCount == 2,
                "CascadeCount must clamp to 2 when below the AF floor.");

            PgslCommands.OmniShadowBudget = 99;
            HeadlessHarness.Assert(
                RenderCapacityDefaults.OmniShadowBudget == 4 && PgslCommands.OmniShadowBudget == 4,
                "OmniShadowBudget must clamp to 0–4.");
            PgslCommands.OmniShadowBudget = -3;
            HeadlessHarness.Assert(
                RenderCapacityDefaults.OmniShadowBudget == 0,
                "OmniShadowBudget must clamp negatives to 0.");

            PgslCommands.LocalVolumetricLightBudget = 99;
            HeadlessHarness.Assert(
                RenderCapacityDefaults.LocalVolumetricLightBudget == 4
                && PgslCommands.LocalVolumetricLightBudget == 4,
                "LocalVolumetricLightBudget must clamp to 0–4.");
            PgslCommands.LocalVolumetricLightBudget = -1;
            HeadlessHarness.Assert(
                RenderCapacityDefaults.LocalVolumetricLightBudget == 0,
                "LocalVolumetricLightBudget must clamp negatives to 0.");

            PgslCommands.Exposure = 2f;
            HeadlessHarness.Assert(
                MathF.Abs(MeshLightingDefaults.Exposure - 2f) < 1e-5f
                && MathF.Abs(PgslCommands.Exposure - 2f) < 1e-5f,
                "Exposure setter must round-trip MeshLightingDefaults.");

            // Setters must not throw when flipped again (CommandAutoTest-style smoke).
            PgslCommands.GtaoEnabled = false;
            PgslCommands.ContactShadowsEnabled = false;
            PgslCommands.LocalVolumetricsEnabled = false;
            PgslCommands.SmokeExtinctionEnabled = false;
            PgslCommands.BloomEnabled = false;
            PgslCommands.AtmosphereLutEnabled = false;
            PgslCommands.Exposure = 1f;

            // R7.5: over-cap still keeps strongest by camera weight (tiled + omni share the rule).
            var lights = new ClusterPointLightGpu[5];
            lights[0] = new ClusterPointLightGpu
            {
                PosRadius = new Vector4(100f, 0f, 0f, 2f),
                ColorIntensity = new Vector4(1f, 1f, 1f, 1f),
                FalloffPad = new Vector4(2f, 0f, 0f, 0f),
            };
            lights[1] = new ClusterPointLightGpu
            {
                PosRadius = new Vector4(1f, 0f, 0f, 3f),
                ColorIntensity = new Vector4(1f, 0.6f, 0.2f, 8f),
                FalloffPad = new Vector4(2f, 0f, 0f, 0f),
            };
            lights[2] = new ClusterPointLightGpu
            {
                PosRadius = new Vector4(50f, 0f, 0f, 10f),
                ColorIntensity = new Vector4(1f, 1f, 1f, 0.2f),
                FalloffPad = new Vector4(2f, 0f, 0f, 0f),
            };
            lights[3] = new ClusterPointLightGpu
            {
                PosRadius = new Vector4(0f, 2f, 4f, 1.5f),
                ColorIntensity = new Vector4(1f, 1f, 1f, 1f),
                FalloffPad = new Vector4(2f, 0f, 0f, 0f),
            };
            lights[4] = new ClusterPointLightGpu
            {
                PosRadius = new Vector4(-80f, 0f, 0f, 4f),
                ColorIntensity = new Vector4(1f, 1f, 1f, 2f),
                FalloffPad = new Vector4(2f, 0f, 0f, 0f),
            };

            Span<int> slots = stackalloc int[4];
            int n = OmniShadowMath.SelectSlots(lights, budget: 1, cameraPos: Vector3.Zero, slots);
            HeadlessHarness.Assert(
                n == 1 && slots[0] == 1,
                $"Over-cap omni select must keep the strongest torch (n={n}, slot={slots[0]}).");

            ClusterPointLightGpu[] tiled = (ClusterPointLightGpu[])lights.Clone();
            int kept = TiledLightGrid.SelectStrongest(tiled.AsSpan(), tiled.Length, 1, Vector3.Zero);
            HeadlessHarness.Assert(
                kept == 1
                && MathF.Abs(tiled[0].PosRadius.X - 1f) < 1e-3f
                && MathF.Abs(tiled[0].ColorIntensity.W - 8f) < 1e-3f,
                "Over-cap tiled SelectStrongest must keep the same camera-weight winner.");

            // Restore AF quality OFF, cascades 2, omni/LV budgets 1, exposure 1.
            MeshLightingDefaults.Configure(true, true, 1f, 2, false, false, false, false, false);
            RenderCapacityDefaults.Configure(
                RenderCapacityDefaults.SpriteInstanceCap,
                RenderCapacityDefaults.MeshInstanceCap,
                RenderCapacityDefaults.SceneLocalLightCap,
                RenderCapacityDefaults.DrawCallMode,
                RenderCapacityDefaults.WorldDrawBudget,
                RenderCapacityDefaults.DefaultOmniShadowBudget,
                RenderCapacityDefaults.DefaultLocalVolumetricLightBudget);
            HeadlessHarness.Assert(
                !MeshLightingDefaults.GtaoEnabled
                && !MeshLightingDefaults.ContactShadowsEnabled
                && !MeshLightingDefaults.LocalVolumetricsEnabled
                && !MeshLightingDefaults.SmokeExtinctionEnabled
                && !MeshLightingDefaults.BloomEnabled
                && !MeshLightingDefaults.AtmosphereLutEnabled
                && MeshLightingDefaults.ShadowCascadeCount == 2
                && RenderCapacityDefaults.OmniShadowBudget == 1
                && RenderCapacityDefaults.LocalVolumetricLightBudget == 1
                && MathF.Abs(MeshLightingDefaults.Exposure - 1f) < 1e-5f,
                "AF1.8 gate must restore default-off quality and default budgets.");
        });

        // AF2.1: Atmosphere LUT bake + UV helpers + ComposeSky + enable wiring (default off;
        // Software skips; no GPU frame / baseline churn). Placed after EngineRendering gate.
        HeadlessHarness.RunCase(ctx.Report, "Render.AtmosphereLut.MathAndEnableWiring", () =>
        {
            HeadlessHarness.Assert(
                !Mesh3DState.Default.AtmosphereLutEnabled
                && !MeshLightingDefaults.AtmosphereLutEnabled,
                "Atmosphere LUT must default off so DX11/DX12 goldens do not churn.");

            byte[] baked = AtmosphereLutMath.BakeRgba8();
            HeadlessHarness.Assert(
                baked.Length == AtmosphereLutMath.ByteCount,
                $"Bake must produce {AtmosphereLutMath.ByteCount} bytes.");

            // Region A transmittance (x=64,y=16) must be non-zero.
            int tx = 64, ty = 16;
            int tOff = (ty * AtmosphereLutMath.Width + tx) * 4;
            HeadlessHarness.Assert(
                baked[tOff] > 0 || baked[tOff + 1] > 0 || baked[tOff + 2] > 0,
                "Transmittance region must bake non-zero RGB.");

            // Sky-view (x=64,y=48) must differ from multi-scatter (x=144,y=16).
            int sx = 64, sy = 48;
            int mx = 144, my = 16;
            int sOff = (sy * AtmosphereLutMath.Width + sx) * 4;
            int mOff = (my * AtmosphereLutMath.Width + mx) * 4;
            HeadlessHarness.Assert(
                baked[sOff] != baked[mOff]
                || baked[sOff + 1] != baked[mOff + 1]
                || baked[sOff + 2] != baked[mOff + 2],
                "Sky-view region must differ from multi-scatter region.");

            Vector2 uvT = AtmosphereLutMath.SampleUvTransmittance(0.5f, 0.5f);
            Vector2 uvM = AtmosphereLutMath.SampleUvMultiScatter(0.5f, 0.5f);
            Vector2 uvS = AtmosphereLutMath.SampleUvSkyView(0.5f, 0.5f);
            HeadlessHarness.Assert(
                uvT.X is >= 0f and <= 1f && uvT.Y is >= 0f and <= 1f
                && uvM.X is >= 0f and <= 1f && uvM.Y is >= 0f and <= 1f
                && uvS.X is >= 0f and <= 1f && uvS.Y is >= 0f and <= 1f,
                "Sample UV helpers must stay in [0,1].");

            Vector3 baseSky = new(0.4f, 0.5f, 0.7f);
            Vector3 day = AtmosphereLutMath.ComposeSky(
                baseSky, sunHeight: 0.85f, viewUp: 0.6f, daylight: 1f, horizon: 0.4f, baked);
            Vector3 night = AtmosphereLutMath.ComposeSky(
                baseSky, sunHeight: -0.6f, viewUp: 0.6f, daylight: 0f, horizon: 0.4f, baked);
            HeadlessHarness.Assert(
                day.LengthSquared() > night.LengthSquared(),
                $"ComposeSky with high sunHeight must be brighter than night "
                + $"(day={day.LengthSquared():F4}, night={night.LengthSquared():F4}).");

            MeshLightingDefaults.Configure(
                true, true, 1f, 2, false, false, false, false, bloomEnabled: false,
                atmosphereLutEnabled: false);
            Mesh3DState lutOff = Mesh3DState.Default;
            MeshLightingDefaults.Apply(ref lutOff);
            HeadlessHarness.Assert(
                !lutOff.AtmosphereLutEnabled,
                "Prefs off must leave atmosphere LUT disabled.");

            MeshLightingDefaults.Configure(
                true, true, 1f, 2, false, false, false, false, bloomEnabled: false,
                atmosphereLutEnabled: true);
            Mesh3DState lutOn = Mesh3DState.Default;
            MeshLightingDefaults.Apply(ref lutOn);
            HeadlessHarness.Assert(
                lutOn.AtmosphereLutEnabled,
                "Prefs on must enable atmosphere LUT on Mesh3DState.");
            HeadlessHarness.Assert(
                !lutOn.GtaoEnabled
                && !lutOn.ContactShadowsEnabled
                && !lutOn.LocalVolumetricsEnabled
                && !lutOn.SmokeExtinctionEnabled
                && !lutOn.BloomEnabled,
                "Enabling atmosphere LUT must not drag earlier AF toggles on.");

            // Restore default-off across AF1/AF2.1 toggles (do not leave bloom/gtao on).
            MeshLightingDefaults.Configure(true, true, 1f, 2, false, false, false, false, false);
        });

        // AF2.2: CPU weather map math + EnvironmentFrame advection wiring (store-only; no FogVolume /
        // GPU / MeshLightingDefaults pollution). Placed after AtmosphereLut gate.
        HeadlessHarness.RunCase(ctx.Report, "Render.WeatherMap.MathAndAdvectionWiring", () =>
        {
            const int seed = 1337;
            Vector2 worldXZ = new(120f, -80f);
            Vector3 wind = new(2.5f, 0f, 1.25f);

            EnvironmentFrame frameT0 = MakeWeatherMapTestFrame(
                elapsedRealSeconds: 0.0, localWind: wind, cloudCover: 0.7f, kind: WeatherKind.Clear);
            EnvironmentFrame frameT1 = MakeWeatherMapTestFrame(
                elapsedRealSeconds: 90.0, localWind: wind, cloudCover: 0.7f, kind: WeatherKind.Clear);
            EnvironmentFrame frameClear = MakeWeatherMapTestFrame(
                elapsedRealSeconds: 12.0, localWind: wind, cloudCover: 0f, kind: WeatherKind.Clear);
            EnvironmentFrame frameCovered = MakeWeatherMapTestFrame(
                elapsedRealSeconds: 12.0, localWind: wind, cloudCover: 1f, kind: WeatherKind.Overcast);
            EnvironmentFrame frameStorm = MakeWeatherMapTestFrame(
                elapsedRealSeconds: 12.0, localWind: wind, cloudCover: 1f, kind: WeatherKind.Thunderstorm);

            WeatherMapSample a = WeatherMapMath.Sample(worldXZ, in frameT0, seed);
            WeatherMapSample aAgain = WeatherMapMath.Sample(worldXZ, in frameT0, seed);
            HeadlessHarness.Assert(
                MathF.Abs(a.Coverage - aAgain.Coverage) < 1e-6f
                && MathF.Abs(a.CloudType - aAgain.CloudType) < 1e-6f
                && MathF.Abs(a.Erosion - aAgain.Erosion) < 1e-6f
                && MathF.Abs(a.VerticalDevelopment - aAgain.VerticalDevelopment) < 1e-6f,
                "WeatherMapMath.Sample must be deterministic for fixed EnvironmentFrame inputs.");

            WeatherMapSample drifted = WeatherMapMath.Sample(worldXZ, in frameT1, seed);
            HeadlessHarness.Assert(
                MathF.Abs(a.Coverage - drifted.Coverage) > 1e-4f
                || MathF.Abs(a.CloudType - drifted.CloudType) > 1e-4f
                || MathF.Abs(a.Erosion - drifted.Erosion) > 1e-4f
                || MathF.Abs(a.VerticalDevelopment - drifted.VerticalDevelopment) > 1e-4f,
                "Wind + ElapsedRealSeconds must advect the sample at fixed worldXZ.");

            WeatherMapSample zeroCover = WeatherMapMath.Sample(worldXZ, in frameClear, seed);
            WeatherMapSample fullCover = WeatherMapMath.Sample(worldXZ, in frameCovered, seed);
            HeadlessHarness.Assert(
                zeroCover.Coverage < 0.02f,
                $"CloudCover=0 must drive Coverage near 0 (got {zeroCover.Coverage}).");
            HeadlessHarness.Assert(
                fullCover.Coverage > zeroCover.Coverage + 0.05f,
                $"CloudCover=1 must raise Coverage above CloudCover=0 "
                + $"(zero={zeroCover.Coverage}, full={fullCover.Coverage}).");

            AssertWeatherChannelsInUnitInterval(a);
            AssertWeatherChannelsInUnitInterval(drifted);
            AssertWeatherChannelsInUnitInterval(zeroCover);
            AssertWeatherChannelsInUnitInterval(fullCover);
            AssertWeatherChannelsInUnitInterval(WeatherMapMath.Sample(worldXZ, in frameStorm, seed));

            Vector2 uv0 = WeatherMapMath.AdvectUv(
                worldXZ, wind, 10.0, WeatherMapMath.DefaultMapScale, WeatherMapMath.DefaultWindScale);
            Vector2 uv1 = WeatherMapMath.AdvectUv(
                worldXZ, wind, 20.0, WeatherMapMath.DefaultMapScale, WeatherMapMath.DefaultWindScale);
            HeadlessHarness.Assert(
                Vector2.DistanceSquared(uv0, uv1) > 1e-10f,
                "AdvectUv must move when ElapsedRealSeconds doubles with non-zero wind "
                + "(documents Sample time source = EnvironmentFrame.ElapsedRealSeconds).");

            HeadlessHarness.Assert(
                !Mesh3DState.Default.AtmosphereLutEnabled
                && !MeshLightingDefaults.AtmosphereLutEnabled
                && !MeshLightingDefaults.GtaoEnabled
                && !MeshLightingDefaults.BloomEnabled,
                "AF2.2 weather-map gate must not pollute MeshLightingDefaults / GPU toggles.");

            using RuntimeScene scene = new();
            scene.WeatherMap.Update(frameT1, seed);
            HeadlessHarness.Assert(
                Math.Abs(scene.WeatherMap.LastUpdateElapsedSeconds - 90.0) < 1e-9,
                "WeatherMapService.Update must record frame.ElapsedRealSeconds.");
            HeadlessHarness.Assert(
                scene.WeatherMap.CurrentMeanCoverage is >= 0f and <= 1f,
                "CurrentMeanCoverage must stay in [0,1].");
            WeatherMapSample fromService = scene.WeatherMap.Sample(worldXZ);
            AssertWeatherChannelsInUnitInterval(fromService);
        });

        // AF2.3: raymarched cloud density math + enable wiring (default off; every frame;
        // Software keeps FogVolumes). No GPU frame / baseline churn.
        HeadlessHarness.RunCase(ctx.Report, "Render.RaymarchedClouds.MathAndEnableWiring", () =>
        {
            HeadlessHarness.Assert(
                !Mesh3DState.Default.RaymarchedCloudsEnabled
                && !MeshLightingDefaults.RaymarchedCloudsEnabled,
                "Raymarched clouds must default off so DX11/DX12 goldens do not churn.");

            HeadlessHarness.Assert(
                RaymarchedCloudsMath.DefaultStepCount <= RaymarchedCloudsMath.MaxStepCount
                && RaymarchedCloudsMath.MaxStepCount <= 48
                && RaymarchedCloudsMath.ClampStepCount(99) == RaymarchedCloudsMath.MaxStepCount,
                "StepCount must be ≤ MaxStepCount ≤ 48.");
            HeadlessHarness.Assert(
                RaymarchedCloudsMath.UpdateEveryFrame,
                "UpdateEveryFrame must be true — never skip the pass 3-in-4.");

            Vector4 denseWeather = new(0.95f, 0.5f, 0.1f, 0.6f);
            Vector4 emptyWeather = new(0.05f, 0.5f, 0.4f, 0.4f);
            float midY = RaymarchedCloudsMath.DefaultCloudBase
                + RaymarchedCloudsMath.DefaultThickness * 0.45f;
            Vector3 inside = new(10f, midY, -20f);
            Vector3 below = new(10f, RaymarchedCloudsMath.DefaultCloudBase - 40f, -20f);

            float densInside = RaymarchedCloudsMath.SampleDensity(inside, denseWeather);
            float densBelow = RaymarchedCloudsMath.SampleDensity(below, denseWeather);
            float densEmpty = RaymarchedCloudsMath.SampleDensity(inside, emptyWeather);
            HeadlessHarness.Assert(
                densBelow < 1e-5f,
                $"Density outside slab must be ~0 (got {densBelow}).");
            HeadlessHarness.Assert(
                densInside > 0f,
                $"Density inside slab with high coverage must be > 0 (got {densInside}).");
            HeadlessHarness.Assert(
                densEmpty < 1e-5f,
                $"Coverage < {RaymarchedCloudsMath.EmptyCoverageThreshold} must empty-skip to 0 "
                + $"(got {densEmpty}).");

            Vector3 origin = new(0f, midY, 0f);
            Vector3 dir = Vector3.UnitX;
            float shortOd = RaymarchedCloudsMath.IntegrateOpticalDepth(
                origin, dir, 8f, denseWeather);
            float longOd = RaymarchedCloudsMath.IntegrateOpticalDepth(
                origin, dir, 40f, denseWeather);
            HeadlessHarness.Assert(
                longOd > shortOd + 1e-4f,
                $"Optical depth must increase with path length in dense cloud "
                + $"(short={shortOd}, long={longOd}).");

            MeshLightingDefaults.Configure(
                true, true, 1f, 2, false, false, false, false, bloomEnabled: false,
                atmosphereLutEnabled: false, raymarchedCloudsEnabled: false);
            Mesh3DState cloudsOff = Mesh3DState.Default;
            MeshLightingDefaults.Apply(ref cloudsOff);
            HeadlessHarness.Assert(
                !cloudsOff.RaymarchedCloudsEnabled,
                "Prefs off must leave raymarched clouds disabled.");

            MeshLightingDefaults.Configure(
                true, true, 1f, 2, false, false, false, false, bloomEnabled: false,
                atmosphereLutEnabled: false, raymarchedCloudsEnabled: true);
            Mesh3DState cloudsOn = Mesh3DState.Default;
            MeshLightingDefaults.Apply(ref cloudsOn);
            HeadlessHarness.Assert(
                cloudsOn.RaymarchedCloudsEnabled,
                "Prefs on must enable raymarched clouds on Mesh3DState.");
            HeadlessHarness.Assert(
                !cloudsOn.GtaoEnabled
                && !cloudsOn.ContactShadowsEnabled
                && !cloudsOn.LocalVolumetricsEnabled
                && !cloudsOn.SmokeExtinctionEnabled
                && !cloudsOn.BloomEnabled
                && !cloudsOn.AtmosphereLutEnabled,
                "Enabling raymarched clouds must not drag earlier AF toggles on.");

            // Restore default-off across AF1/AF2 toggles.
            MeshLightingDefaults.Configure(true, true, 1f, 2, false, false, false, false, false);
        });

        // AF2.4: temporal math + quality ladder + enable wiring (default temporal off; High=0.50).
        HeadlessHarness.RunCase(ctx.Report, "Render.CloudTemporal.MathAndEnableWiring", () =>
        {
            HeadlessHarness.Assert(
                RaymarchedCloudsMath.UpdateEveryFrame,
                "UpdateEveryFrame must stay true — never skip the pass 3-in-4.");

            HeadlessHarness.Assert(
                Math.Abs(CloudTemporalMath.ScaleFor(CloudQualityTier.Performance) - 0.25f) < 1e-6f
                && Math.Abs(CloudTemporalMath.ScaleFor(CloudQualityTier.Balanced) - 0.40f) < 1e-6f
                && Math.Abs(CloudTemporalMath.ScaleFor(CloudQualityTier.High) - 0.50f) < 1e-6f
                && Math.Abs(CloudTemporalMath.ScaleFor(CloudQualityTier.Cinematic) - 0.625f) < 1e-6f,
                "Quality scales must be 0.25 / 0.40 / 0.50 / 0.625.");

            (int iw, int ih) = CloudTemporalMath.ResolveInternalSize(
                640, 360, CloudTemporalMath.ScaleFor(CloudQualityTier.High));
            HeadlessHarness.Assert(
                iw == 320 && ih == 180,
                $"High ResolveInternalSize(640,360) must be (320,180) (got {iw}x{ih}).");

            Matrix4x4 view = Matrix4x4.CreateLookAt(
                new Vector3(0f, 0f, 10f), Vector3.Zero, Vector3.UnitY);
            Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 4f, 16f / 9f, 0.1f, 1000f);
            Matrix4x4 vp = view * proj;
            Vector3 world = new(1f, 2f, -5f);
            Vector2 uv = CloudTemporalMath.ReprojectUv(world, vp);
            HeadlessHarness.Assert(
                CloudTemporalMath.UvInBounds(uv),
                $"ReprojectUv must land in-bounds for a frustum point (uv={uv}).");
            Vector4 clip = Vector4.Transform(new Vector4(world, 1f), vp);
            float invW = 1f / MathF.Max(MathF.Abs(clip.W), 1e-5f);
            Vector2 expected = new(clip.X * invW * 0.5f + 0.5f, 0.5f - clip.Y * invW * 0.5f);
            HeadlessHarness.Assert(
                Vector2.Distance(uv, expected) < 1e-4f,
                $"ReprojectUv round-trip mismatch (got {uv}, expected {expected}).");

            Vector3 clamped = CloudTemporalMath.NeighbourhoodClamp(
                new Vector3(2f, -1f, 0.5f),
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 1f, 1f));
            HeadlessHarness.Assert(
                Math.Abs(clamped.X - 1f) < 1e-5f
                && Math.Abs(clamped.Y) < 1e-5f
                && Math.Abs(clamped.Z - 0.5f) < 1e-5f,
                $"NeighbourhoodClamp must pull history into min/max (got {clamped}).");

            HeadlessHarness.Assert(
                CloudTemporalMath.HistoryWeight(
                    new Vector2(-0.1f, 0.5f), 0f, 0f, 0f) < 1e-5f,
                "HistoryWeight must reject invalid UV.");
            HeadlessHarness.Assert(
                CloudTemporalMath.HistoryWeight(
                    new Vector2(0.5f, 0.5f),
                    CloudTemporalMath.DefaultMotionThreshold + 0.1f,
                    0f,
                    0f) < 1e-5f,
                "HistoryWeight must reject large motion.");
            float okWeight = CloudTemporalMath.HistoryWeight(
                new Vector2(0.5f, 0.5f), 0.01f, 0.001f, 0.01f);
            HeadlessHarness.Assert(
                okWeight > 0.5f,
                $"HistoryWeight should keep base blend on calm pixels (got {okWeight}).");

            HeadlessHarness.Assert(
                !Mesh3DState.Default.CloudTemporalEnabled
                && !MeshLightingDefaults.CloudTemporalEnabled,
                "CloudTemporalEnabled must default off.");
            HeadlessHarness.Assert(
                Mesh3DState.Default.CloudQuality == 2
                && MeshLightingDefaults.CloudQuality == 2,
                "CloudQuality must default to High (2).");

            MeshLightingDefaults.Configure(
                true, true, 1f, 2, false, false, false, false, bloomEnabled: false,
                atmosphereLutEnabled: false, raymarchedCloudsEnabled: false,
                cloudTemporalEnabled: true, cloudQuality: 2);
            Mesh3DState temporalOn = Mesh3DState.Default;
            MeshLightingDefaults.Apply(ref temporalOn);
            HeadlessHarness.Assert(
                temporalOn.CloudTemporalEnabled && temporalOn.CloudQuality == 2,
                "Prefs must enable temporal without changing High quality.");
            HeadlessHarness.Assert(
                !temporalOn.GtaoEnabled
                && !temporalOn.ContactShadowsEnabled
                && !temporalOn.LocalVolumetricsEnabled
                && !temporalOn.SmokeExtinctionEnabled
                && !temporalOn.BloomEnabled
                && !temporalOn.AtmosphereLutEnabled
                && !temporalOn.RaymarchedCloudsEnabled,
                "Enabling cloud temporal must not drag earlier AF toggles on.");

            MeshLightingDefaults.Configure(true, true, 1f, 2, false, false, false, false, false);
        });

        // AF2.5: celestial math + FogPost enable wiring (default off; Software skip; not on cloud budget).
        HeadlessHarness.RunCase(ctx.Report, "Render.CelestialExtras.MathAndEnableWiring", () =>
        {
            HeadlessHarness.Assert(
                Math.Abs(CelestialExtrasMath.StarIntensity(0f)) < 1e-6f,
                "StarIntensity(0) must be ~0 by day.");
            HeadlessHarness.Assert(
                CelestialExtrasMath.StarIntensity(1f) > 0.5f,
                "StarIntensity(1) must rise at night.");

            float phaseA = CelestialExtrasMath.MoonPhaseFromDayOfYear(1f);
            float phaseB = CelestialExtrasMath.MoonPhaseFromDayOfYear(15f);
            float phaseC = CelestialExtrasMath.MoonPhaseFromDayOfYear(215f);
            HeadlessHarness.Assert(
                Math.Abs(phaseA - phaseB) > 0.05f || Math.Abs(phaseA - phaseC) > 0.05f,
                "MoonPhaseFromDayOfYear must vary across the synodic cycle.");
            HeadlessHarness.Assert(
                phaseA is >= 0f and <= 1f && phaseB is >= 0f and <= 1f && phaseC is >= 0f and <= 1f,
                "Moon phase must stay in 0..1.");

            float earthshineDark = CelestialExtrasMath.EarthshineFactor(0.5f, litAmount: 0f);
            HeadlessHarness.Assert(
                earthshineDark > 0.1f,
                $"EarthshineFactor must be positive on the dark limb (got {earthshineDark}).");

            Vector3 galactic = Vector3.Normalize(new Vector3(0.12f, 0.42f, 0.9f));
            float lat = CelestialExtrasMath.LatitudeRadiansFromDegrees(0f);
            // Band is the plane perpendicular to the galactic normal — pick an in-plane direction.
            Vector3 bandDir = Vector3.Normalize(Vector3.Cross(galactic, Vector3.UnitX));
            float onBand = CelestialExtrasMath.MilkyWayBandWeight(bandDir, lat);
            float offBand = CelestialExtrasMath.MilkyWayBandWeight(galactic, lat);
            HeadlessHarness.Assert(
                onBand > offBand,
                $"MilkyWayBandWeight must peak near the galactic band (on={onBand}, off={offBand}).");

            Vector3 moonDir = Vector3.Normalize(new Vector3(0.1f, 0.7f, 0.2f));
            (float maskNew, float litNew) = CelestialExtrasMath.MoonDiscLit(moonDir, moonDir, phase: 0.05f);
            (float maskFull, float litFull) = CelestialExtrasMath.MoonDiscLit(moonDir, moonDir, phase: 0.5f);
            HeadlessHarness.Assert(
                maskNew > 0.5f && maskFull > 0.5f,
                "MoonDiscLit must hit the disc centre.");
            HeadlessHarness.Assert(
                Math.Abs(litNew - litFull) > 0.05f,
                $"Phase 0 vs ~0.5 must change lit amount (new={litNew}, full={litFull}).");

            HeadlessHarness.Assert(
                !Mesh3DState.Default.CelestialExtrasEnabled
                && !MeshLightingDefaults.CelestialExtrasEnabled,
                "CelestialExtrasEnabled must default off.");

            MeshLightingDefaults.Configure(
                true, true, 1f, 2, false, false, false, false, bloomEnabled: false,
                atmosphereLutEnabled: false, raymarchedCloudsEnabled: false,
                cloudTemporalEnabled: false, cloudQuality: 2, celestialExtrasEnabled: true);
            Mesh3DState celestialOn = Mesh3DState.Default;
            MeshLightingDefaults.Apply(ref celestialOn);
            HeadlessHarness.Assert(
                celestialOn.CelestialExtrasEnabled,
                "Prefs must enable celestial extras.");
            HeadlessHarness.Assert(
                !celestialOn.GtaoEnabled
                && !celestialOn.ContactShadowsEnabled
                && !celestialOn.LocalVolumetricsEnabled
                && !celestialOn.SmokeExtinctionEnabled
                && !celestialOn.BloomEnabled
                && !celestialOn.AtmosphereLutEnabled
                && !celestialOn.RaymarchedCloudsEnabled
                && !celestialOn.CloudTemporalEnabled,
                "Enabling celestial extras must not drag earlier AF toggles on.");

            // Restore default-off across AF1/AF2 toggles.
            MeshLightingDefaults.Configure(true, true, 1f, 2, false, false, false, false, false);
        });

        // AF2.6: Engine.Sky PGSL + Mesh3DState authoring drive raymarch / celestial packs.
        HeadlessHarness.RunCase(ctx.Report, "Render.EngineSky.PgslAndAuthoringWiring", () =>
        {
            SkyAuthoringDefaults.Reset();

            HeadlessHarness.Assert(
                PgslCommandRegistry.TryGet("Engine.Sky.Coverage")?.IsProperty == true
                && PgslCommandRegistry.TryGet("Engine.Sky.Altitude")?.IsProperty == true
                && PgslCommandRegistry.TryGet("Engine.Sky.Thickness")?.IsProperty == true
                && PgslCommandRegistry.TryGet("Engine.Sky.Latitude")?.IsProperty == true
                && PgslCommandRegistry.TryGet("Engine.Sky.DayOfYear")?.IsProperty == true,
                "Engine.Sky must register Coverage/Altitude/Thickness/Latitude/DayOfYear properties.");

            Mesh3DState defaults = Mesh3DState.Default;
            HeadlessHarness.Assert(
                MathF.Abs(defaults.CloudBaseHeight - 180f) < 1e-3f
                && MathF.Abs(defaults.CloudThickness - 85f) < 1e-3f
                && MathF.Abs(defaults.DayOfYear - 215f) < 1e-3f
                && MathF.Abs(defaults.LatitudeDegrees - 53.9f) < 1e-3f
                && MathF.Abs(defaults.CloudCoverageScale - 1f) < 1e-5f
                && MathF.Abs(defaults.CloudDensityScale - 1f) < 1e-5f
                && MathF.Abs(SkyAuthoringDefaults.CloudBaseHeight - 180f) < 1e-3f
                && MathF.Abs(SkyAuthoringDefaults.CloudThickness - 85f) < 1e-3f
                && MathF.Abs(SkyAuthoringDefaults.DayOfYear - 215f) < 1e-3f
                && MathF.Abs(SkyAuthoringDefaults.LatitudeDegrees - 53.9f) < 1e-3f
                && MathF.Abs(SkyAuthoringDefaults.CloudCoverageScale - 1f) < 1e-5f,
                "AF2.6 defaults must match 180/85/215/53.9/coverage 1.");

            Vector4 packDefault = SkyAuthoringDefaults.ResolveCloudParams(defaults);
            HeadlessHarness.Assert(
                MathF.Abs(packDefault.X - 180f) < 1e-3f
                && MathF.Abs(packDefault.Y - 85f) < 1e-3f
                && MathF.Abs(packDefault.W - SkyAuthoringDefaults.DefaultCloudIntensity) < 1e-5f
                && MathF.Abs(SkyAuthoringDefaults.ResolveCoverageScale(defaults) - 1f) < 1e-5f,
                "Resolve helpers must pack historical AF2.3 defaults when state is Default.");

            PgslCommands.SkyCoverage = 0.42f;
            PgslCommands.SkyAltitude = 220f;
            PgslCommands.SkyThickness = 120f;
            PgslCommands.SkyLatitude = 12.5f;
            PgslCommands.SkyDayOfYear = 100f;
            PgslCommands.SkyDensity = 2f;
            HeadlessHarness.Assert(
                MathF.Abs(SkyAuthoringDefaults.CloudCoverageScale - 0.42f) < 1e-5f
                && MathF.Abs(SkyAuthoringDefaults.CloudBaseHeight - 220f) < 1e-3f
                && MathF.Abs(SkyAuthoringDefaults.CloudThickness - 120f) < 1e-3f
                && MathF.Abs(SkyAuthoringDefaults.LatitudeDegrees - 12.5f) < 1e-3f
                && MathF.Abs(SkyAuthoringDefaults.DayOfYear - 100f) < 1e-3f
                && MathF.Abs(SkyAuthoringDefaults.CloudDensityScale - 2f) < 1e-5f
                && MathF.Abs(PgslCommands.SkyCoverage - 0.42f) < 1e-5f
                && MathF.Abs(PgslCommands.SkyAltitude - 220f) < 1e-3f,
                "Engine.Sky setters must round-trip SkyAuthoringDefaults.");

            Mesh3DState stamped = Mesh3DState.Default;
            SkyAuthoringDefaults.Apply(ref stamped);
            HeadlessHarness.Assert(
                MathF.Abs(stamped.CloudBaseHeight - 220f) < 1e-3f
                && MathF.Abs(stamped.CloudThickness - 120f) < 1e-3f
                && MathF.Abs(stamped.CloudCoverageScale - 0.42f) < 1e-5f
                && MathF.Abs(stamped.CloudDensityScale - 2f) < 1e-5f
                && MathF.Abs(stamped.LatitudeDegrees - 12.5f) < 1e-3f
                && MathF.Abs(stamped.DayOfYear - 100f) < 1e-3f,
                "SkyAuthoringDefaults.Apply must stamp Mesh3DState from PGSL values.");

            Mesh3DState overridden = Mesh3DState.Default;
            overridden.CloudBaseHeight = 300f;
            overridden.CloudThickness = 40f;
            overridden.CloudDensityScale = 1.5f;
            overridden.CloudCoverageScale = 0.25f;
            Vector4 packOverride = SkyAuthoringDefaults.ResolveCloudParams(overridden);
            HeadlessHarness.Assert(
                MathF.Abs(packOverride.X - 300f) < 1e-3f
                && MathF.Abs(packOverride.Y - 40f) < 1e-3f
                && MathF.Abs(packOverride.W - SkyAuthoringDefaults.DefaultCloudIntensity * 1.5f) < 1e-5f
                && MathF.Abs(SkyAuthoringDefaults.ResolveCoverageScale(overridden) - 0.25f) < 1e-5f,
                "Resolve helpers must honour Mesh3DState overrides.");

            HeadlessHarness.Assert(
                MathF.Abs(Mesh3DState.Default.CloudBaseHeight - 180f) < 1e-3f
                && MathF.Abs(Mesh3DState.Default.CloudThickness - 85f) < 1e-3f
                && MathF.Abs(Mesh3DState.Default.DayOfYear - 215f) < 1e-3f
                && MathF.Abs(Mesh3DState.Default.LatitudeDegrees - 53.9f) < 1e-3f
                && MathF.Abs(Mesh3DState.Default.CloudCoverageScale - 1f) < 1e-5f,
                "Mesh3DState.Default must stay at historical constants after override resolve.");

            PgslCommands.SetCloudCoverage(0.75f);
            PgslCommands.SetLatitude(40f);
            HeadlessHarness.Assert(
                MathF.Abs(SkyAuthoringDefaults.CloudCoverageScale - 0.75f) < 1e-5f
                && MathF.Abs(SkyAuthoringDefaults.LatitudeDegrees - 40f) < 1e-3f,
                "SetCloudCoverage / SetLatitude method aliases must write SkyAuthoringDefaults.");

            // Quality / RaymarchedCloudsEnabled alias Rendering without flipping default on.
            PgslCommands.SkyQuality = 1;
            HeadlessHarness.Assert(
                MeshLightingDefaults.CloudQuality == 1 && PgslCommands.CloudQuality == 1,
                "Engine.Sky.Quality must alias Engine.Rendering.CloudQuality.");
            HeadlessHarness.Assert(
                !PgslCommands.SkyRaymarchedCloudsEnabled && !MeshLightingDefaults.RaymarchedCloudsEnabled,
                "RaymarchedClouds must stay default-off through Engine.Sky alias.");

            SkyAuthoringDefaults.Reset();
            MeshLightingDefaults.Configure(true, true, 1f, 2, false, false, false, false, false);
        });

        // AF2.7: SkyForge fly-through structural golden — below / inside / above + pan proves
        // volume, not a sticky screen-space sheet. Software labels FogVolume fallback on F6.
        // Does NOT retake golden-dx11.json.
        HeadlessHarness.RunCase(ctx.Report, "Render.Sky.CloudFlyThrough", () =>
        {
            const double slabEpsilon = 3.0;
            const double panEpsilon = 2.0;

            float cloudBase = RaymarchedCloudsMath.DefaultCloudBase;
            float thickness = RaymarchedCloudsMath.DefaultThickness;
            float midY = cloudBase + thickness * 0.45f;

            Vector3 belowPos = new(0f, cloudBase - 40f, 48f);
            Vector3 insidePos = new(0f, midY, 48f);
            Vector3 abovePos = new(0f, cloudBase + thickness + 40f, 48f);
            Vector3 lookTarget = new(0f, midY, -24f);
            Vector3 panOffset = new(10f, 0f, -4f);

            try
            {
                MeshLightingDefaults.Configure(
                    lightingEnabled: true,
                    shadowsEnabled: true,
                    shadowStrength: 1f,
                    shadowCascadeCount: 2,
                    raymarchedCloudsEnabled: true,
                    cloudTemporalEnabled: false,
                    cloudQuality: 2);

                // ── DX11 primary structural proof ───────────────────────────────
                RuntimeImageMetrics below;
                RuntimeImageMetrics inside;
                RuntimeImageMetrics above;
                RuntimeImageMetrics panA;
                RuntimeImageMetrics panB;
                RenderStats dx11Stats;
                using (CloudFlyThroughHarness dx11 = new())
                {
                    dx11.SetCamera(belowPos, lookTarget);
                    below = dx11.Capture(Path.Combine(ctx.Captures, "cloud-flythrough-below.png"));

                    dx11.SetCamera(insidePos, lookTarget);
                    inside = dx11.Capture(Path.Combine(ctx.Captures, "cloud-flythrough-inside.png"));

                    dx11.SetCamera(abovePos, lookTarget);
                    above = dx11.Capture(Path.Combine(ctx.Captures, "cloud-flythrough-above.png"));

                    dx11.SetCamera(insidePos, lookTarget);
                    panA = dx11.Capture(Path.Combine(ctx.Captures, "cloud-flythrough-pan-a.png"));
                    dx11.SetCamera(insidePos + panOffset, lookTarget + panOffset);
                    panB = dx11.Capture(Path.Combine(ctx.Captures, "cloud-flythrough-pan-b.png"));

                    dx11Stats = dx11.LastStats;
                }

                HeadlessHarness.Assert(
                    below.UniqueSampledColors >= 8
                    && inside.UniqueSampledColors >= 8
                    && above.UniqueSampledColors >= 8,
                    $"DX11 fly-through captures look blank "
                    + $"(below={below.UniqueSampledColors}, inside={inside.UniqueSampledColors}, "
                    + $"above={above.UniqueSampledColors} colours).");
                HeadlessHarness.Assert(
                    dx11Stats.RaymarchedCloudsMs > 0.001,
                    $"DX11 raymarched clouds must run (RaymarchedCloudsMs={dx11Stats.RaymarchedCloudsMs:F4}).");

                double belowInside = RuntimeImageMetrics.MaxTileDelta(below, inside);
                double insideAbove = RuntimeImageMetrics.MaxTileDelta(inside, above);
                    HeadlessHarness.Assert(
                    belowInside > slabEpsilon,
                    $"Below vs inside must differ (Δ={belowInside:F2}/255, ε={slabEpsilon:F0}/255) "
                    + "— clouds must not be a flat clear.");
                HeadlessHarness.Assert(
                    insideAbove > slabEpsilon,
                    $"Inside vs above must differ (Δ={insideAbove:F2}/255, ε={slabEpsilon:F0}/255).");

                double panDelta = RuntimeImageMetrics.MaxTileDelta(panA, panB);
                HeadlessHarness.Assert(
                    panDelta > panEpsilon,
                    $"Camera pan must change tiles (Δ={panDelta:F2}/255, ε={panEpsilon:F0}/255) "
                    + "— not a sticky screen-space sheet.");

                Console.WriteLine(
                    $"      DX11 fly-through: CL={dx11Stats.RaymarchedCloudsMs:F2} ms, "
                    + $"below↔inside Δ={belowInside:F2}/255, inside↔above Δ={insideAbove:F2}/255, "
                    + $"pan Δ={panDelta:F2}/255.");

                // ── Optional GPU backend smoke (enable + non-blank) ─────────────
                SmokeCloudFlyThroughIfAvailable(
                    ctx, RenderBackendOption.Direct3D12, "Dx12",
                    RenderBackendSelection.IsDirect3D12Available, insidePos, lookTarget);
                SmokeCloudFlyThroughIfAvailable(
                    ctx, RenderBackendOption.Vulkan, "Vulkan",
                    RenderBackendSelection.IsVulkanAvailable, insidePos, lookTarget);
                SmokeCloudFlyThroughIfAvailable(
                    ctx, RenderBackendOption.OpenGL, "OpenGL",
                    RenderBackendSelection.IsOpenGLAvailable, insidePos, lookTarget);

                // ── Software: FogVolumes fallback + F6 label ────────────────────
                using (CloudFlyThroughHarness software = new(RenderBackendOption.Software))
                {
                    // Lower camera so ground + markers stay in frame (Software skips raymarch;
                    // FogVolumes alone at slab height can leave a sky-only clear).
                    software.SetCamera(new Vector3(0f, 28f, 72f), new Vector3(0f, 10f, 0f));
                    RuntimeImageMetrics soft = software.Capture(
                        Path.Combine(ctx.Captures, "cloud-flythrough-software.png"));

                    HeadlessHarness.Assert(
                        software.LastStats.RaymarchedCloudsMs == 0,
                        $"Software must not run raymarched clouds "
                        + $"(RaymarchedCloudsMs={software.LastStats.RaymarchedCloudsMs:F4}).");

                    string label = DebugOverlay.CloudsStatusLabel(
                        software.LastStats,
                        software.BackendName,
                        MeshLightingDefaults.RaymarchedCloudsEnabled);
                    HeadlessHarness.Assert(
                        label.Contains("FogVolumes", StringComparison.Ordinal)
                        && label.Contains("Software", StringComparison.Ordinal),
                        $"Software F6 cloud label must name FogVolumes fallback (got '{label}').");

                    HeadlessHarness.Assert(
                        soft.UniqueSampledColors >= 4,
                        $"Software FogVolume fallback capture looks blank "
                        + $"({soft.UniqueSampledColors} colours).");

                    Console.WriteLine($"      Software fly-through: label '{label}', colours={soft.UniqueSampledColors}.");
                }
            }
            finally
            {
                MeshLightingDefaults.Configure(true, true, 1f, 2, false, false, false, false, false);
                SkyAuthoringDefaults.Reset();
            }
        });

        // AF3.1: conserved reservoir fill — cup level rises on pour; bath overflows at rim;
        // volume + SurfaceY persist on WaterBody JSON. No voxel MC.
        HeadlessHarness.RunCase(ctx.Report, "Render.Fluids.Reservoir.ConservedFill", () =>
        {
            // ── Cup: pour raises level and persists ─────────────────────────────
            WaterBody cup = WaterBody.CreateCupPreset("AF31 Cup", Vector3.Zero, initialLevel: 0.055f);
            WaterBodyReservoir cupFill = WaterBodyReservoir.Attach(cup);
            float cupLevel0 = cupFill.Level;
            float cupVolume0 = cupFill.Volume;
            HeadlessHarness.Assert(
                cup.Kind == WaterBodyKind.Reservoir
                && cupLevel0 > cup.ReservoirBaseY
                && cupLevel0 < cupFill.MaxLevel
                && cupVolume0 > 0f,
                $"Cup preset must start partially filled (level={cupLevel0:F4}, vol={cupVolume0:F6}).");

            cupFill.AddVolume(0.00008f); // ~80 mL pour
            float cupLevel1 = cupFill.Level;
            HeadlessHarness.Assert(
                cupLevel1 > cupLevel0 + 1e-4f
                && MathF.Abs(cup.SurfaceY - cupLevel1) < 1e-5f
                && cup.VolumeCubicMetres > cupVolume0,
                $"Pour must raise cup level (before={cupLevel0:F4}, after={cupLevel1:F4}).");

            string persistDir = Path.Combine(ctx.Workspace, "af31-reservoir");
            WaterBodyReservoir cupReload = WaterBodyReservoir.RoundTrip(cup, persistDir);
            HeadlessHarness.Assert(
                cupReload.Body.Kind == WaterBodyKind.Reservoir
                && MathF.Abs(cupReload.Level - cupLevel1) < 1e-4f
                && MathF.Abs(cupReload.Volume - cup.VolumeCubicMetres) < 1e-7f
                && MathF.Abs(cupReload.Body.ReservoirAreaAtBase - cup.ReservoirAreaAtBase) < 1e-8f,
                "Cup volume/level/cross-section must survive WaterBody JSON round-trip.");

            // ── Bath: fill past rim → overflow; RemoveOverflow clamps to MaxLevel ─
            WaterBody bath = WaterBody.CreateBathPreset("AF31 Bath", Vector3.Zero, initialLevel: 0.42f);
            WaterBodyReservoir bathFill = WaterBodyReservoir.Attach(bath);
            float bathMax = bathFill.MaxLevel;
            HeadlessHarness.Assert(
                bathFill.Level < bathMax - 0.05f,
                $"Bath must start below rim (level={bathFill.Level:F3}, max={bathMax:F3}).");

            // Overfill: add more than remaining capacity.
            bathFill.AddVolume(2.0f); // cubic metres — enough to blow past rim
            float overflowBefore = bathFill.OverflowVolume;
            HeadlessHarness.Assert(
                overflowBefore > 0.01f && bathFill.Level > bathMax - 1e-3f,
                $"Bath overfill must report overflow (overflow={overflowBefore:F4} m³, level={bathFill.Level:F4}).");

            float spilled = bathFill.RemoveOverflow();
            HeadlessHarness.Assert(
                spilled > 0.01f
                && bathFill.OverflowVolume < 1e-5f
                && MathF.Abs(bathFill.Level - bathMax) < 1e-3f
                && MathF.Abs(bath.SurfaceY - bathMax) < 1e-3f,
                $"RemoveOverflow must clamp to rim (spilled={spilled:F4}, level={bathFill.Level:F4}, max={bathMax:F4}).");

            // Empty drain leaves floor.
            bathFill.RemoveVolume(bathFill.Volume + 1f);
            HeadlessHarness.Assert(
                bathFill.Volume < 1e-6f
                && MathF.Abs(bathFill.Level - bath.ReservoirBaseY) < 1e-3f,
                "Draining must leave the reservoir at BaseY with ~zero volume.");

            Console.WriteLine(
                $"      Cup pour Δlevel={(cupLevel1 - cupLevel0) * 1000f:F1} mm; "
                + $"bath rim overflow {spilled:F3} m³; JSON round-trip OK.");
        });

        // AF3.2: hydrostatic leaks — Torricelli sqrt(2gh); discharge only above the hole;
        // aperture/obstruction scale flow; Engine.Terrain.Water.PunchHole PGSL; holes persist.
        HeadlessHarness.RunCase(ctx.Report, "Render.Fluids.Reservoir.HydrostaticLeaks", () =>
        {
            WaterAuthoringDefaults.Reset();
            PgslCommands.WarmRegistry();

            HeadlessHarness.Assert(
                PgslCommandRegistry.TryGet("Engine.Terrain.Water.PunchHole")?.IsProperty == false
                && PgslCommandRegistry.TryGet("Engine.Terrain.Water.HoleRadius")?.IsProperty == true
                && PgslCommandRegistry.TryGet("Engine.Terrain.Water.ClearHoles")?.IsProperty == false
                && PgslCommandRegistry.TryGet("Engine.Terrain.Water.HoleCount")?.IsProperty == true,
                "Engine.Terrain.Water must register PunchHole/ClearHoles methods and HoleRadius/HoleCount.");

            // Math: Torricelli speed and orifice flow.
            float head = 0.20f;
            float expectedSpeed = MathF.Sqrt(2f * FluidPhysics.Gravity * head);
            HeadlessHarness.Assert(
                MathF.Abs(FluidPhysics.HoleJetSpeed(head) - expectedSpeed) < 1e-5f
                && FluidPhysics.HoleJetSpeed(0f) == 0f
                && FluidPhysics.HoleJetSpeed(-1f) == 0f,
                "HoleJetSpeed must be sqrt(2gh) and zero for non-positive head.");

            ReservoirHole probe = new()
            {
                Position = new Vector3(0.72f, 0.30f, 0f),
                SurfaceNormal = Vector3.UnitX,
                Radius = 0.008f,
                Shape = ReservoirHoleShape.Round,
                DischargeCoefficient = 0.62f,
            };
            float qClear = FluidPhysics.HoleFlowRate(probe, head, obstructionFactor: 1f);
            float qBlocked = FluidPhysics.HoleFlowRate(probe, head, obstructionFactor: 0.4f);
            float qDry = FluidPhysics.HoleFlowRate(probe, headMetres: 0f);
            HeadlessHarness.Assert(
                qClear > 0f && qBlocked > 0f && qBlocked < qClear * 0.5f && qDry == 0f,
                $"Flow must scale with obstruction and shut off at zero head (q={qClear:E3}, blocked={qBlocked:E3}).");

            // Bath: hole below surface drains; hole above surface does not.
            WaterBody bath = WaterBody.CreateBathPreset("AF32 Bath", Vector3.Zero, initialLevel: 0.55f);
            WaterBodyReservoir fill = WaterBodyReservoir.Attach(bath);
            float level0 = fill.Level;
            float volume0 = fill.Volume;

            // Dry hole above the free surface — no discharge.
            fill.PunchHole(new Vector3(0.68f, level0 + 0.05f, 0f), Vector3.UnitX, radius: 0.01f);
            float dryRemoved = fill.DrainHoles(dt: 0.5f);
            HeadlessHarness.Assert(
                dryRemoved == 0f
                && MathF.Abs(fill.Volume - volume0) < 1e-9f
                && MathF.Abs(fill.Level - level0) < 1e-6f,
                "Hole above the surface must not discharge.");

            bath.Holes.Clear();
            ReservoirHole wetHole = fill.PunchHole(
                new Vector3(0.68f, 0.30f, 0f), Vector3.UnitX, radius: 0.01f);
            HeadlessHarness.Assert(
                fill.Solver.HeadAbove(wetHole.Position.Y) > 0.1f,
                "Wet hole must sit below the free surface.");

            float wetRemoved = 0f;
            for (int i = 0; i < 30; i++)
                wetRemoved += fill.DrainHoles(1f / 60f);
            HeadlessHarness.Assert(
                wetRemoved > 1e-5f
                && fill.Volume < volume0
                && fill.Level < level0
                && fill.LastOutflowRate > 0f,
                $"Wet hole must drain volume (removed={wetRemoved:E3}, level {level0:F3}→{fill.Level:F3}).");

            // Larger aperture drains faster (fresh baths, same head, one step).
            WaterBody small = WaterBody.CreateBathPreset("AF32 Small", Vector3.Zero, initialLevel: 0.55f);
            WaterBody large = WaterBody.CreateBathPreset("AF32 Large", Vector3.Zero, initialLevel: 0.55f);
            WaterBodyReservoir smallFill = WaterBodyReservoir.Attach(small);
            WaterBodyReservoir largeFill = WaterBodyReservoir.Attach(large);
            smallFill.PunchHole(new Vector3(0.68f, 0.30f, 0f), Vector3.UnitX, radius: 0.005f);
            largeFill.PunchHole(new Vector3(0.68f, 0.30f, 0f), Vector3.UnitX, radius: 0.012f);
            float smallQ = smallFill.DrainHoles(1f / 60f);
            float largeQ = largeFill.DrainHoles(1f / 60f);
            HeadlessHarness.Assert(
                largeQ > smallQ * 2f,
                $"Larger aperture must drain more per step (small={smallQ:E3}, large={largeQ:E3}).");

            // Surface at the hole → Torricelli head is zero → no further discharge.
            fill.Reset(wetHole.Position.Y);
            float afterEmpty = fill.DrainHoles(1f);
            HeadlessHarness.Assert(
                afterEmpty == 0f && fill.Solver.HeadAbove(wetHole.Position.Y) <= 0f,
                $"Discharge must be zero when surface is at the hole (removed={afterEmpty}).");

            // PGSL punch → apply → JSON round-trip keeps holes.
            WaterAuthoringDefaults.Reset();
            PgslCommands.WaterHoleRadius = 0.007f;
            PgslCommands.WaterHoleShape = (int)ReservoirHoleShape.Slit;
            PgslCommands.PunchWaterHole(0.70f, 0.35f, 0.05f, 1f, 0f, 0f);
            HeadlessHarness.Assert(
                PgslCommands.WaterHoleCount == 1
                && MathF.Abs(PgslCommands.WaterHoleRadius - 0.007f) < 1e-6f,
                "PunchHole PGSL must increment HoleCount and honour HoleRadius.");

            WaterBody persistBody = WaterBody.CreateBathPreset("AF32 Persist", Vector3.Zero, initialLevel: 0.50f);
            WaterAuthoringDefaults.ApplyHolesTo(persistBody);
            HeadlessHarness.Assert(persistBody.Holes.Count == 1, "ApplyHolesTo must copy scratchpad holes.");

            string persistDir = Path.Combine(ctx.Workspace, "af32-holes");
            WaterBodyReservoir reloaded = WaterBodyReservoir.RoundTrip(persistBody, persistDir);
            HeadlessHarness.Assert(
                reloaded.Body.Holes.Count == 1
                && MathF.Abs(reloaded.Body.Holes[0].Radius - 0.007f) < 1e-6f
                && reloaded.Body.Holes[0].Shape == ReservoirHoleShape.Slit
                && MathF.Abs(reloaded.Body.Holes[0].Position.Y - 0.35f) < 1e-5f,
                "Holes must survive WaterBody JSON round-trip.");

            PgslCommands.ClearWaterHoles();
            HeadlessHarness.Assert(PgslCommands.WaterHoleCount == 0, "ClearHoles must empty the scratchpad.");
            WaterAuthoringDefaults.Reset();

            Console.WriteLine(
                $"      Wet drain {wetRemoved * 1000f:F2} L over 0.5 s; "
                + $"aperture ratio large/small={largeQ / MathF.Max(smallQ, 1e-12f):F1}; "
                + "PGSL PunchHole + JSON OK.");
        });

        // AF1.3: omni-shadow budget + ForestLight face schedule + FalloffPad packing for GPU slot 0.
        HeadlessHarness.RunCase(ctx.Report, "Render.OmniShadows.BudgetAndFaceSchedule", () =>
        {
            HeadlessHarness.Assert(
                RenderCapacityDefaults.DefaultOmniShadowBudget == 1
                && OmniShadowMath.DefaultBudget == 1,
                "Default omni-shadow budget must be one campfire-class cubemap.");
            HeadlessHarness.Assert(
                OmniShadowMath.ClampBudget(99) == OmniShadowMath.MaxBudget
                && OmniShadowMath.ClampBudget(-3) == OmniShadowMath.MinBudget,
                "Omni-shadow budget must clamp to 0..4.");

            // Five locals; budget 1 → strongest by camera weight (nearest bright torch).
            var lights = new ClusterPointLightGpu[5];
            lights[0] = new ClusterPointLightGpu
            {
                PosRadius = new Vector4(100f, 0f, 0f, 2f),
                ColorIntensity = new Vector4(1f, 1f, 1f, 1f),
                // FalloffPad.Y = 1 marks GPU omni slot 0 (AF1.3); cleared unless selected.
                FalloffPad = new Vector4(2f, 0f, 0f, 0f),
            };
            lights[1] = new ClusterPointLightGpu
            {
                PosRadius = new Vector4(1f, 0f, 0f, 3f),
                ColorIntensity = new Vector4(1f, 0.6f, 0.2f, 8f),
                FalloffPad = new Vector4(2f, 0f, 0f, 0f),
            };
            lights[2] = new ClusterPointLightGpu
            {
                PosRadius = new Vector4(50f, 0f, 0f, 10f),
                ColorIntensity = new Vector4(1f, 1f, 1f, 0.2f),
                FalloffPad = new Vector4(2f, 0f, 0f, 0f),
            };
            lights[3] = new ClusterPointLightGpu
            {
                PosRadius = new Vector4(0f, 2f, 4f, 1.5f),
                ColorIntensity = new Vector4(1f, 1f, 1f, 1f),
                FalloffPad = new Vector4(2f, 0f, 0f, 0f),
            };
            lights[4] = new ClusterPointLightGpu
            {
                PosRadius = new Vector4(-80f, 0f, 0f, 4f),
                ColorIntensity = new Vector4(1f, 1f, 1f, 2f),
                FalloffPad = new Vector4(2f, 0f, 0f, 0f),
            };
            Span<int> slots = stackalloc int[4];
            int n = OmniShadowMath.SelectSlots(lights, budget: 1, cameraPos: Vector3.Zero, slots);
            HeadlessHarness.Assert(n == 1 && slots[0] == 1,
                $"Budget-1 must pick the campfire at index 1 (got n={n}, slot={slots[0]}).");

            // Packing convention: selected slot gets FalloffPad.Y=1 (shader _pad0) for omni visibility.
            lights[slots[0]].FalloffPad = new Vector4(lights[slots[0]].FalloffPad.X, 1f, OmniShadowMath.DefaultFarPlane, 0f);
            HeadlessHarness.Assert(
                lights[slots[0]].FalloffPad.Y == 1f && lights[0].FalloffPad.Y == 0f,
                "FalloffPad.Y=1 marks omni slot 0; other lights stay 0.");

            n = OmniShadowMath.SelectSlots(lights, budget: 2, cameraPos: Vector3.Zero, slots);
            HeadlessHarness.Assert(n == 2 && slots[0] == 1,
                "Budget-2 must still rank the campfire first.");

            // Face schedule: cold start refreshes all six; then three per frame.
            int cursor = 0;
            Span<int> faces = stackalloc int[6];
            int first = OmniShadowMath.NextFaceBatch(initialised: false, ref cursor, faces);
            HeadlessHarness.Assert(
                first == 6 && cursor == 0,
                $"Initial batch must draw 6 faces and wrap cursor (first={first}, cursor={cursor}).");
            for (int i = 0; i < 6; i++)
                HeadlessHarness.Assert(faces[i] == i, $"Initial face[{i}] must be {i}.");

            int second = OmniShadowMath.NextFaceBatch(initialised: true, ref cursor, faces);
            HeadlessHarness.Assert(
                second == 3 && faces[0] == 0 && faces[1] == 1 && faces[2] == 2 && cursor == 3,
                $"Steady batch must advance 3 faces (second={second}, cursor={cursor}).");

            HeadlessHarness.Assert(
                OmniShadowMath.FaceDirection(0) == Vector3.UnitX
                && OmniShadowMath.FaceDirection(1) == -Vector3.UnitX
                && OmniShadowMath.FaceUp(2) == Vector3.UnitZ,
                "Cube face axes must match ForestLight campfire maps.");
        });

        // R7.8: canned example-scene estimate from authored caps — never benches the host PC.
        HeadlessHarness.RunCase(ctx.Report, "Render.Preferences.SystemRequirementsEstimate", () =>
        {
            SystemRequirementsInput baseline = new()
            {
                Backend = RenderBackendOption.SilkNetDx11,
                LightingEnabled = true,
                ShadowsEnabled = true,
                ShadowStrength = 1f,
                SpriteInstanceCap = 4096,
                MeshInstanceCap = 4096,
                SceneLocalLightCap = 64,
                DrawCallMode = RenderCapacityDefaults.DrawCallModeAuto,
                WorldDrawBudget = 1000,
                TextureGroups =
                [
                    new SystemRequirementsAtlasGroup(TextureGroupCatalog.DefaultName, 2048),
                ],
            };
            SystemRequirementsReport light = SystemRequirementsEstimator.Estimate(baseline);

            SystemRequirementsInput heavyInput = new()
            {
                Backend = RenderBackendOption.SilkNetDx11,
                LightingEnabled = true,
                ShadowsEnabled = true,
                ShadowStrength = 1f,
                SpriteInstanceCap = 32768,
                MeshInstanceCap = 32768,
                SceneLocalLightCap = 1000,
                DrawCallMode = RenderCapacityDefaults.DrawCallModeAuto,
                WorldDrawBudget = 1000,
                TextureGroups =
                [
                    new SystemRequirementsAtlasGroup(TextureGroupCatalog.DefaultName, 4096),
                    new SystemRequirementsAtlasGroup("Props", 4096),
                ],
            };
            SystemRequirementsReport heavy = SystemRequirementsEstimator.Estimate(heavyInput);

            SystemRequirementsReport software = SystemRequirementsEstimator.Estimate(new SystemRequirementsInput
            {
                Backend = RenderBackendOption.Software,
                LightingEnabled = true,
                ShadowsEnabled = false,
                SpriteInstanceCap = 4096,
                MeshInstanceCap = 4096,
                SceneLocalLightCap = 64,
                TextureGroups =
                [
                    new SystemRequirementsAtlasGroup(TextureGroupCatalog.DefaultName, 2048),
                ],
            });

            HeadlessHarness.Assert(
                light.ExampleSceneName == SystemRequirementsEstimator.ExampleSceneName
                && light.SummaryText.Contains(
                    SystemRequirementsEstimator.MidRangeGpuModel,
                    StringComparison.Ordinal),
                "Estimate must label FPS against the fixed mid-range GPU model (not the author PC).");
            HeadlessHarness.Assert(
                light.LightingPath.Contains("Clustered", StringComparison.OrdinalIgnoreCase),
                $"DX11 estimate must report clustered lighting ({light.LightingPath}).");
            HeadlessHarness.Assert(
                software.LightingPath.Contains("Forward", StringComparison.OrdinalIgnoreCase)
                && software.LightingPath.Contains("Software", StringComparison.OrdinalIgnoreCase),
                $"Software estimate must report forward/Software path ({software.LightingPath}).");
            HeadlessHarness.Assert(
                heavy.EstimatedRamMb > light.EstimatedRamMb
                && heavy.EstimatedBuiltDiskMb > light.EstimatedBuiltDiskMb
                && heavy.ExpectedFps < light.ExpectedFps,
                "Heavier caps/atlases must raise RAM/disk and lower expected FPS "
                + $"(light: {light.EstimatedRamMb}MB/{light.ExpectedFps}fps, "
                + $"heavy: {heavy.EstimatedRamMb}MB/{heavy.ExpectedFps}fps).");
            HeadlessHarness.Assert(
                software.ExpectedFps < light.ExpectedFps,
                "Software backend must estimate lower FPS than DX11 on the same scene knobs.");
        });

        // R7.6: particles fill MeshInstanceData (+ one template) instead of boxing MeshDrawCall per particle.
        // TransBatch flags must stay on the template so ForwardRenderer parks them off WorldMeshes.
        HeadlessHarness.RunCase(ctx.Report, "Render.Particles.InstanceBufferSkipsMeshDrawCall", () =>
        {
            ParticleSimulation simulation = new();
            simulation.LoadConfig(new ParticleConfig
            {
                MaxParticles = 64,
                BurstCount = 32,
                Loop = false,
                StartSize = 0.5,
                EndSize = 0.25,
                BlendMode = ParticleBlendMode.Additive,
                Emissive = 0.4,
            });
            HeadlessHarness.Assert(
                simulation.ActiveCount > 0,
                "Burst particle config produced no active particles for the instance-buffer gate.");

            simulation.RenderInstances3D(
                [new MeshHandle(1)],
                default,
                Vector3.Zero,
                -Vector3.UnitZ,
                out MeshInstanceData[] instances,
                out int count,
                out MeshDrawCall template);

            HeadlessHarness.Assert(
                count == simulation.ActiveCount && instances is { Length: > 0 } && count <= instances.Length,
                $"Instance fill count ({count}) must match active particles ({simulation.ActiveCount}).");
            HeadlessHarness.Assert(
                (template.Flags & MeshDrawFlags.Transparent) != 0
                && (template.Flags & MeshDrawFlags.NoShadow) != 0
                && (template.Flags & MeshDrawFlags.NoDepthWrite) != 0
                && (template.Flags & MeshDrawFlags.Additive) != 0,
                $"Particle instance template lost TransBatch flags ({template.Flags}).");
            HeadlessHarness.Assert(
                template.Emissive > 0f,
                "Particle instance template dropped emissive.");
        });

        // Pure CPU — no device, no window. Guards the hand-maintained agreement between the
        // managed constant-buffer structs and every HLSL cbuffer that shader code blits them into.
        HeadlessHarness.RunCase(ctx.Report, "Render.Shader.ConstantBufferLayoutMatchesHlsl", () =>
        {
            IReadOnlyList<ConstantBufferComparison> comparisons = ShaderLayoutAudit.CompareAll();
            HeadlessHarness.Assert(
                comparisons.Count > 0,
                "ShaderLayoutAudit compared nothing — the pairing table or shader sources are empty.");

            // Durable artefact: the full layout of every buffer, so a future drift can be diffed
            // rather than re-derived by hand.
            File.WriteAllLines(
                Path.Combine(ctx.Logs, "constant-buffer-layout.txt"),
                comparisons.Select(c => c.Describe()));

            List<ConstantBufferComparison> broken = comparisons.Where(c => !c.IsConsistent).ToList();
            if (broken.Count == 0)
            {
                return;
            }

            string detail = string.Join(
                Environment.NewLine + Environment.NewLine,
                broken.Select(c => c.Describe()));
            throw new InvalidOperationException(
                $"{broken.Count} of {comparisons.Count} constant-buffer declarations disagree with the " +
                $"managed struct blitted into them. Every field after the first mismatch is read from the " +
                $"wrong offset by the shader:{Environment.NewLine}{Environment.NewLine}{detail}");
        });

        HeadlessHarness.RunCase(ctx.Report, "Render.Software.PointLightLayoutAndFalloff", () =>
        {
            // The software path decodes the same raw EngineCB uploaded to the GPU paths. Exercise
            // the private decoder with two lights: a stale 32-byte stride reads light 0's falloff
            // row as light 1's position, which this deliberately non-overlapping fixture catches.
            Type rasterizer = typeof(GpuRenderController).Assembly.GetType(
                "Genesis.Rendering.Software.SoftwareRasterizerCore",
                throwOnError: true)!;
            MethodInfo readLighting = rasterizer.GetMethod(
                "ReadLighting",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException(rasterizer.FullName, "ReadLighting");
            byte[] constants = new byte[208 + (8 * 48)];
            static void PutFloat(byte[] bytes, int offset, float value) =>
                BitConverter.GetBytes(value).CopyTo(bytes, offset);

            PutFloat(constants, 12, 1f);   // global lighting weight
            PutFloat(constants, 192, 2f);  // clustered light total (x)
            PutFloat(constants, 204, 2f);  // EngineCB light slots used (w) — Software reads this
            PutFloat(constants, 208 + 12, 9f);
            PutFloat(constants, 208 + 28, 2f);
            PutFloat(constants, 208 + 32, 1.5f);
            int second = 208 + 48;
            PutFloat(constants, second, 17f);
            PutFloat(constants, second + 12, 20f);
            PutFloat(constants, second + 16, 0.25f);
            PutFloat(constants, second + 20, 0.5f);
            PutFloat(constants, second + 24, 1f);
            PutFloat(constants, second + 28, 3f);
            PutFloat(constants, second + 32, 5.5f);

            object decoded = readLighting.Invoke(null, [constants])
                ?? throw new InvalidOperationException("The software light decoder returned null.");
            Array lights = (Array)(decoded.GetType().GetField("PointLights")?.GetValue(decoded)
                ?? throw new MissingFieldException(decoded.GetType().FullName, "PointLights"));
            object decodedSecond = lights.GetValue(1)
                ?? throw new InvalidOperationException("The second software point light was absent.");
            Vector3 position = (Vector3)(decodedSecond.GetType().GetField("Position")?.GetValue(decodedSecond)
                ?? throw new MissingFieldException(decodedSecond.GetType().FullName, "Position"));
            float falloff = (float)(decodedSecond.GetType().GetField("Falloff")?.GetValue(decodedSecond)
                ?? throw new MissingFieldException(decodedSecond.GetType().FullName, "Falloff"));
            HeadlessHarness.Assert(
                Math.Abs(position.X - 17f) < 0.001f && Math.Abs(falloff - 5.5f) < 0.001f,
                $"Software light decoding drifted from the 48-byte renderer contract "
                + $"(second.x={position.X:0.###}, falloff={falloff:0.###}).");
        });

        SoftwareRasterizerClipPerspectiveCases.Register(ctx);

        HeadlessHarness.RunCase(ctx.Report, "Render.Shader.IntegerParametersPackAsHlslIntBits", () =>
        {
            var document = new ShaderAssetDocument
            {
                Source = """
                    cbuffer GenesisParameters : register(b5) { int Steps; bool Enabled; };
                    float4 MainPS() : SV_Target { return 0; }
                    """,
                Parameters =
                [
                    new ShaderParameterValue { Name = "Steps", Type = "int", Value = [7f] },
                    new ShaderParameterValue { Name = "Enabled", Type = "bool", Value = [1f] },
                ],
            };
            ShaderParameterReflection.Pack(document, null, out Vector4 row0, out _, out _, out _);
            HeadlessHarness.Assert(
                BitConverter.SingleToInt32Bits(row0.X) == 7,
                "Integer shader parameters must upload HLSL int bit patterns, not float semantics.");
            HeadlessHarness.Assert(
                BitConverter.SingleToInt32Bits(row0.Y) == 1,
                "Boolean shader parameters must upload HLSL bool/int bit patterns.");
        });

        // A viewport must render at its control's real pixel size. This used to be a fixed
        // 1280x720 chain that DXGI stretched to fit, so every viewport at any other size was
        // resampled — visibly soft, and a capture never matched the control it came from.
        //
        // Scope note: this asserts the size a swap chain is *created* at. Resizing a live chain is
        // tracked separately as NEXT-068 — ResizeBuffers has never succeeded in this codebase, and
        // the fixed-size-plus-stretch policy was the workaround for it. That has to be solved
        // before the Vulkan backend, which has no equivalent free stretch, but it does not block a
        // parity baseline: the parity harness renders at one fixed size throughout.
        HeadlessHarness.RunCase(ctx.Report, "Render.Viewport.MatchesClientSizeAtCreation", () =>
        {
            foreach ((int width, int height) in new[] { (640, 360), (400, 300), (820, 500) })
            {
                using RuntimeViewportHarness harness = new(width, height);

                (int Width, int Height) client = harness.ViewportClientSize;
                (int Width, int Height) backend = harness.BackendRenderSize;
                HeadlessHarness.Assert(
                    backend.Width == client.Width && backend.Height == client.Height,
                    $"Hosted at {width}x{height}, the backend renders at {backend.Width}x{backend.Height} "
                    + $"but the control is {client.Width}x{client.Height} — frames would be resampled. "
                    + harness.DescribeSizes());

                // The readback must agree, because every visual baseline is measured from it.
                RuntimeImageMetrics metrics = harness.Capture3D(
                    Path.Combine(ctx.Captures, $"60-viewport-{width}x{height}.png"));
                HeadlessHarness.Assert(
                    metrics.Width == client.Width && metrics.Height == client.Height,
                    $"Readback returned {metrics.Width}x{metrics.Height}, expected "
                    + $"{client.Width}x{client.Height}.");
                HeadlessHarness.Assert(
                    metrics.Tiles.Count == RuntimeImageMetrics.TileColumns * RuntimeImageMetrics.TileRows,
                    $"Tile digest has {metrics.Tiles.Count} cells, expected "
                    + $"{RuntimeImageMetrics.TileColumns * RuntimeImageMetrics.TileRows}.");
                HeadlessHarness.Assert(
                    RuntimeImageMetrics.MaxTileDelta(metrics, metrics) == 0,
                    "A digest compared against itself must report zero drift.");
            }

            // Live resize is NEXT-068 and not asserted here, but the attempt is still exercised so
            // the swap chain's diagnostics (outstanding back-buffer references, flags) land in
            // genesis_render.log on every run rather than only when someone goes looking.
            using RuntimeViewportHarness resizeProbe = new(640, 360);
            bool followed = resizeProbe.ResizeTo(820, 500);
            Console.WriteLine(
                $"      live resize {(followed ? "succeeded" : "declined (NEXT-068)")}: {resizeProbe.DescribeSizes()}");
        });

        // Offscreen render targets were stubs — CreateRenderTarget returned Invalid and the setters
        // were empty — so nothing could ever have depended on them. The abstraction layer needs
        // them, and so does anything doing post-processing, so they are real now.
        HeadlessHarness.RunCase(ctx.Report, "Render.Rhi.RenderTargetRoundTrip", () =>
        {
            using RenderParityHarness harness = new();

            // Clear the offscreen target to a strongly-tinted colour, draw into it, then sample it
            // back onto the swap chain. If the target were not really being rendered into, the
            // sampled quad would come back black or empty rather than carrying this tint.
            RuntimeImageMetrics green = harness.CaptureThroughRenderTarget(
                Path.Combine(ctx.Captures, "62-render-target-green.png"),
                new RenderColor(0.10f, 0.65f, 0.20f));

            RuntimeImageMetrics magenta = harness.CaptureThroughRenderTarget(
                Path.Combine(ctx.Captures, "62-render-target-magenta.png"),
                new RenderColor(0.70f, 0.10f, 0.60f));

            // The probe scene is deliberately flat — clear colour plus a lit cube — so three
            // distinct colours is the correct result, not a weak one. Fewer than that would mean
            // the geometry never reached the target and only the clear survived.
            HeadlessHarness.Assert(
                green.UniqueSampledColors >= 3 && magenta.UniqueSampledColors >= 3,
                $"Only the clear colour came back, so nothing was drawn into the render target "
                + $"(green={green.UniqueSampledColors} colours, magenta={magenta.UniqueSampledColors}).");

            // Changing only the offscreen clear colour must change the final image. This is what
            // separates "the target really was the render destination" from "something else drew".
            double delta = RuntimeImageMetrics.MaxTileDelta(green, magenta);
            HeadlessHarness.Assert(
                delta > 20,
                $"Two different offscreen clear colours produced near-identical frames "
                + $"({delta:F2}/255). The render target is not actually being drawn into, or its "
                + "colour attachment is not what gets sampled back.");
        });

        // R1.4: exercise the new backend through IGpuDevice only. This is intentionally offscreen
        // so the gate proves the device/resource contract without conflating it with NEXT-068's
        // independent HWND swap-chain resize defect.
        HeadlessHarness.RunCase(ctx.Report, "Render.Rhi.Dx11DeviceSmoke", () =>
        {
            using IGpuDevice device = new Dx11GpuDevice();
            HeadlessHarness.Assert(device.BackendName == "Direct3D 11", "DX11 device identity is wrong.");
            HeadlessHarness.Assert(
                device.Capabilities.SupportsStructuredBuffers && device.Capabilities.MaxColorAttachments >= 8,
                "DX11 capabilities do not describe the feature-level-11 contract.");

            GpuBufferHandle constants = device.CreateBuffer(new GpuBufferDesc
            {
                SizeBytes = 64,
                Usage = GpuBufferUsage.Dynamic,
                BindFlags = GpuBindFlags.ConstantBuffer,
            }, ReadOnlySpan<byte>.Empty);
            device.UpdateConstantBuffer(constants, new System.Numerics.Vector4(1, 2, 3, 4));
            HeadlessHarness.Assert(device.TryMapDiscard(constants, out Span<byte> mapped),
                "A dynamic DX11 buffer could not be mapped with WriteDiscard.");
            mapped.Fill(0x5A);
            device.Unmap(constants);

            byte[] rgba =
            {
                255, 0, 0, 255,  0, 255, 0, 255,
                0, 0, 255, 255,  255, 255, 255, 255,
            };
            GpuTextureHandle texture = device.CreateTexture(new GpuTextureDesc
            {
                Width = 2, Height = 2, MipLevels = 1, ArrayLayers = 1,
                Format = GpuFormat.R8G8B8A8UNorm,
                Usage = GpuBufferUsage.Immutable,
                BindFlags = GpuBindFlags.ShaderResource,
            }, rgba);
            HeadlessHarness.Assert(device.TryReadTexture(texture, out int tw, out int th, out byte[] pixels),
                "A DX11 texture could not be copied through the abstraction's readback path.");
            HeadlessHarness.Assert(tw == 2 && th == 2 && pixels.Length == 16,
                $"Texture readback shape was {tw}x{th}/{pixels.Length} bytes, expected 2x2/16.");
            HeadlessHarness.Assert(pixels[0] == 0 && pixels[1] == 0 && pixels[2] == 255 && pixels[3] == 255,
                "RGBA texture readback was not normalized to the contract's BGRA byte order.");

            GpuSamplerHandle sampler = device.CreateSampler(new GpuSamplerDesc
            {
                Filter = GpuFilter.Linear,
                AddressU = GpuAddressMode.Clamp,
                AddressV = GpuAddressMode.Clamp,
                AddressW = GpuAddressMode.Clamp,
                MaxAnisotropy = 1,
                CompareOp = GpuCompare.Never,
            });

            GpuRenderTargetHandle target = device.CreateRenderTarget(new GpuRenderTargetDesc
            {
                Width = 16,
                Height = 16,
                ColorFormats = new[] { GpuFormat.B8G8R8A8UNorm },
                DepthFormat = GpuFormat.D24UNormS8UInt,
                DepthSampleable = true,
            });
            device.BeginRenderPass(new GpuRenderPassDesc
            {
                Target = target,
                ColorActions = new[] { GpuAttachmentAction.Clear(0.15f, 0.55f, 0.25f) },
                HasDepth = true,
                DepthAction = GpuAttachmentAction.Clear(1, 0, 0),
            });
            device.SetViewport(0, 0, 16, 16);
            device.EndRenderPass();

            GpuTextureHandle color = device.GetRenderTargetTexture(target);
            HeadlessHarness.Assert(device.GetRenderTargetDepthTexture(target).IsValid,
                "DepthSampleable did not produce a depth texture handle.");
            HeadlessHarness.Assert(device.TryReadTexture(color, out int rw, out int rh, out byte[] clear),
                "The offscreen colour attachment could not be read through IGpuDevice.");
            int center = (((rh / 2) * rw) + (rw / 2)) * 4;
            HeadlessHarness.Assert(
                clear[center + 0] is >= 62 and <= 66 &&
                clear[center + 1] is >= 138 and <= 142 &&
                clear[center + 2] is >= 36 and <= 40,
                $"Render-pass clear did not survive readback (BGRA={clear[center]},{clear[center + 1]},{clear[center + 2]})." );

            device.ReleaseRenderTarget(target);
            device.ReleaseSampler(sampler);
            device.ReleaseTexture(texture);
            device.ReleaseBuffer(constants);
        });

        // R2 source ratchet: visual output alone cannot prove the migration boundary. Keep this
        // check so a convenient native pointer cannot silently creep back into SpriteRenderer.
        HeadlessHarness.RunCase(ctx.Report, "Render.Rhi.SpriteRendererIsBackendNeutral", () =>
        {
            string sourceFile = Path.Combine(
                ResolveRepositoryRoot(),
                "Source", "Runtime", "Genesis.Rendering", "Primitives", "SpriteRenderer.cs");
            string source = File.ReadAllText(sourceFile);
            string[] forbidden =
            {
                "Silk.NET",
                "ID3D11",
                "ComPtr<",
                "_ctx->",
                "_dev->",
            };
            string[] violations = forbidden
                .Where(token => source.Contains(token, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            HeadlessHarness.Assert(
                violations.Length == 0,
                $"SpriteRenderer crossed the backend boundary again: {string.Join(", ", violations)}.");
            HeadlessHarness.Assert(
                source.Contains("IGpuDevice", StringComparison.Ordinal),
                "SpriteRenderer no longer declares the IGpuDevice boundary.");
        });

        // R3 boundary ratchet. The golden scene proves pixels; this proves the large renderer can
        // actually be reused by another backend instead of reaching around the abstraction.
        HeadlessHarness.RunCase(ctx.Report, "Render.Rhi.ForwardRendererIsBackendNeutral", () =>
        {
            string sourceFile = Path.Combine(
                ResolveRepositoryRoot(),
                "Source", "Runtime", "Genesis.Rendering", "Primitives", "ForwardRenderer.cs");
            string source = File.ReadAllText(sourceFile);
            string[] forbidden =
            {
                "Silk.NET",
                "ID3D11",
                "ComPtr<",
                "_ctx",
                "_dev",
                "unsafe",
                "GetNative",
            };
            List<string> violations = forbidden
                .Where(token => source.Contains(token, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (System.Text.RegularExpressions.Regex.IsMatch(
                    source, @"\bnint\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                violations.Add("nint");
            HeadlessHarness.Assert(
                violations.Count == 0,
                $"ForwardRenderer crossed the backend boundary: {string.Join(", ", violations)}.");
            HeadlessHarness.Assert(
                source.Contains("IGpuDevice", StringComparison.Ordinal) &&
                source.Contains("GpuRenderPassDesc", StringComparison.Ordinal) &&
                source.Contains("GpuQueryHandle", StringComparison.Ordinal),
                "ForwardRenderer is missing one or more required abstraction surfaces.");
        });

        // A separate scaffold ratchet catches the exact transitional bridges R2 left in the
        // controller. Otherwise ForwardRenderer could remain neutral while its caller silently
        // converted every texture back into a native pointer before submitting it.
        HeadlessHarness.RunCase(ctx.Report, "Render.Rhi.NoScaffoldLeft", () =>
        {
            string root = ResolveRepositoryRoot();
            string renderer = File.ReadAllText(Path.Combine(
                root, "Source", "Runtime", "Genesis.Rendering", "Primitives", "ForwardRenderer.cs"));
            string controller = File.ReadAllText(Path.Combine(
                root, "Source", "Runtime", "Genesis.Rendering", "Core", "GpuRenderController.cs"));
            string combined = renderer + "\n" + controller;
            string[] obsolete =
            {
                "_whiteSrv",
                "ResolveTexSrv",
                "GetSrv(TextureHandle",
                "new ForwardRenderer(dev",
                "_fwd.OverridePixelShader",
                "ForwardRenderer until R3",
            };
            string[] violations = obsolete
                .Where(token => combined.Contains(token, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            HeadlessHarness.Assert(
                violations.Length == 0,
                $"R3 native scaffold remains: {string.Join(", ", violations)}.");
        });

        // R4b: the overlay's text stack is provable without a device — which matters because it is
        // the part Vulkan and OpenGL will reuse verbatim. The property under test is not "text
        // rasterises" but "text rasterises ONCE": the previous implementation re-rasterised the whole
        // overlay on the CPU whenever any character changed, which measured 2.5 ms/frame and 198 MB/s
        // of upload for an ordinary HUD with a live score.
        HeadlessHarness.RunCase(ctx.Report, "Render.Overlay.GlyphAtlasRasterisesOnce", () =>
        {
            using GlyphAtlas atlas = new();
            List<GlyphQuad> quads = [];

            atlas.LayoutRun("SCORE 0", "Segoe UI", 26f, bold: true, 32f, 28f, quads);
            HeadlessHarness.Assert(
                quads.Count == 6,
                $"'SCORE 0' produced {quads.Count} glyph quads, expected 6 (the space has no pixels). "
                + "Text is not being laid out glyph by glyph.");
            int firstUploads = atlas.DrainUploads().Count;
            HeadlessHarness.Assert(
                atlas.RasterCount > 0 && firstUploads > 0,
                $"The first layout rasterised {atlas.RasterCount} glyphs and queued {firstUploads} "
                + "uploads; it must do both, or the atlas texture would stay empty.");

            long afterFirst = atlas.RasterCount;

            // The same run again must cost no rasterisation and produce no upload at all.
            quads.Clear();
            atlas.LayoutRun("SCORE 0", "Segoe UI", 26f, bold: true, 32f, 28f, quads);
            HeadlessHarness.Assert(
                atlas.RasterCount == afterFirst && atlas.DrainUploads().Count == 0,
                $"Repeating an identical run rasterised {atlas.RasterCount - afterFirst} more glyphs. "
                + "A static HUD must cost nothing.");

            // The live-score case: 300 frames of changing digits. Only the ten digits are new, and
            // once seen they are never rasterised again — this is the whole point of the atlas.
            for (int frame = 0; frame < 300; frame++)
            {
                quads.Clear();
                atlas.LayoutRun($"SCORE {frame}", "Segoe UI", 26f, bold: true, 32f, 28f, quads);
            }

            long digitsAndDigitsOnly = atlas.RasterCount - afterFirst;
            HeadlessHarness.Assert(
                digitsAndDigitsOnly <= 10,
                $"300 frames of a changing score rasterised {digitsAndDigitsOnly} extra glyphs. At most "
                + "the ten digits can be new; anything more means glyphs are being re-rasterised per "
                + "frame, which is exactly the CPU cost this atlas replaced.");

            atlas.DrainUploads();
            long beforeSteady = atlas.RasterCount;
            for (int frame = 0; frame < 120; frame++)
            {
                quads.Clear();
                atlas.LayoutRun($"SCORE {frame % 1000}", "Segoe UI", 26f, bold: true, 32f, 28f, quads);
            }

            HeadlessHarness.Assert(
                atlas.RasterCount == beforeSteady && atlas.DrainUploads().Count == 0,
                "Steady state still uploads to the GPU every frame; the glyph cache is not holding.");

            // Layout must reflect the actual string, not a per-character box. A rasteriser that
            // ignored its input would pass a quad-count check but not this.
            quads.Clear();
            atlas.LayoutRun("I", "Segoe UI", 40f, bold: true, 0f, 0f, quads);
            float narrow = quads.Count > 0 ? quads[0].Width : 0f;
            quads.Clear();
            atlas.LayoutRun("W", "Segoe UI", 40f, bold: true, 0f, 0f, quads);
            float wide = quads.Count > 0 ? quads[0].Width : 0f;
            HeadlessHarness.Assert(
                narrow > 0f && wide > narrow * 1.5f,
                $"'I' is {narrow:F1}px and 'W' is {wide:F1}px — glyph geometry is not coming from the "
                + "font, so text would render as uniform boxes.");

            // Atlas texels must be white with coverage in alpha, because the sprite shader colours
            // text purely by tint (tex * tint). Coloured texels would tint twice and come out wrong.
            using GlyphAtlas fresh = new();
            List<GlyphQuad> probe = [];
            fresh.LayoutRun("O", "Segoe UI", 48f, bold: true, 0f, 0f, probe);
            IReadOnlyList<GlyphUpload> uploads = fresh.DrainUploads();
            HeadlessHarness.Assert(uploads.Count > 0, "A fresh atlas queued no glyph upload.");

            byte[] pixels = uploads[0].Pixels;
            bool anyCoverage = false;
            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                HeadlessHarness.Assert(
                    pixels[i] == 255 && pixels[i + 1] == 255 && pixels[i + 2] == 255,
                    "A glyph texel is not white. The sprite shader multiplies by tint, so anything "
                    + "other than white would tint the text twice.");
                if (pixels[i + 3] > 128) anyCoverage = true;
            }

            HeadlessHarness.Assert(anyCoverage, "The glyph bitmap is fully transparent — nothing was drawn.");
        });

        // R4 boundary ratchet. The overlay and readback used to be reachable only by downcasting to
        // the concrete DX11 controller, which quietly made HUD text and screenshots Direct3D-only.
        HeadlessHarness.RunCase(ctx.Report, "Render.Rhi.OverlayAndReadbackAreBackendNeutral", () =>
        {
            string root = ResolveRepositoryRoot();

            // Only the factory may name the concrete controller. Anything else is a downcast, and a
            // downcast is how a second backend silently loses a feature.
            List<string> downcasts = [];
            foreach (string file in Directory.EnumerateFiles(
                         Path.Combine(root, "Source"), "*.cs", SearchOption.AllDirectories))
            {
                // The factory is allowed to name it, and so is its own definition.
                string name = Path.GetFileName(file);
                if (name is "RenderControllerFactory.cs" or "GpuRenderController.cs") continue;
                string text = File.ReadAllText(file);
                if (!text.Contains("SilkNetDx11RenderController", StringComparison.Ordinal)
                    && !text.Contains("GpuRenderController", StringComparison.Ordinal))
                    continue;
                downcasts.Add(Path.GetRelativePath(root, file));
            }

            HeadlessHarness.Assert(
                downcasts.Count == 0,
                $"{downcasts.Count} file(s) still name the concrete controller instead of "
                + $"IRenderController: {string.Join(", ", downcasts)}.");

            // P6.1.0: the controller itself must hold no backend types. It reached past IGpuDevice
            // for native render-target views in six places, which is unimplementable on an explicit
            // API — a pass has to be declared up front for its resource states to be known. Until
            // this held, a second backend meant a second copy of the whole frame orchestration.
            string controllerSource = File.ReadAllText(Path.Combine(
                root, "Source", "Runtime", "Genesis.Rendering", "Core", "GpuRenderController.cs"));
            string[] backendTypes =
            {
                "ID3D11", "Silk.NET.Direct3D11", "ID3D12", "Silk.NET.Direct3D12",
                "GetNativeRenderTargetView", "GetNativeDepthStencilView", "GetNativeSwapChain",
            };
            string[] leaks = backendTypes
                .Where(token => controllerSource.Contains(token, StringComparison.Ordinal))
                .ToArray();
            HeadlessHarness.Assert(
                leaks.Length == 0,
                $"GpuRenderController reaches past IGpuDevice for backend types ({string.Join(", ", leaks)}); "
                + "one renderer can no longer drive every backend.");

            // Direct2D/DirectWrite are gone from the shared path entirely — they were the reason
            // overlay text could not exist on another backend.
            List<string> direct2D = [];
            foreach (string file in Directory.EnumerateFiles(
                         Path.Combine(root, "Source"), "*.cs", SearchOption.AllDirectories))
            {
                string source = File.ReadAllText(file);
                if (source.Contains("Vortice.Direct2D1", StringComparison.Ordinal) ||
                    source.Contains("Vortice.DirectWrite", StringComparison.Ordinal) ||
                    source.Contains("D2dOverlay", StringComparison.Ordinal))
                {
                    direct2D.Add(Path.GetRelativePath(root, file));
                }
            }

            HeadlessHarness.Assert(
                direct2D.Count == 0,
                $"Direct2D/DirectWrite overlay code returned in: {string.Join(", ", direct2D)}.");

            string contract = File.ReadAllText(Path.Combine(
                root, "Source", "Runtime", "Genesis.Shared", "Interfaces", "IRenderController.cs"));
            HeadlessHarness.Assert(
                contract.Contains("ComposeOverlay", StringComparison.Ordinal) &&
                contract.Contains("TryReadFramePixels", StringComparison.Ordinal) &&
                contract.Contains("LastGpuMilliseconds", StringComparison.Ordinal),
                "IRenderController no longer declares the overlay/readback/timing contract every "
                + "backend must implement.");

            // DrawText was a stub for the entire life of the controller. Prove the body is real.
            string controller = File.ReadAllText(Path.Combine(
                root, "Source", "Runtime", "Genesis.Rendering", "Core", "GpuRenderController.cs"));
            HeadlessHarness.Assert(
                !controller.Contains("DrawText(string text, float x, float y, float size, RenderColor color) { ",
                    StringComparison.Ordinal),
                "IRenderController.DrawText is an empty stub again.");
        });

        // The GPU half of R4: overlay text really lands on the presented frame, through the shared
        // sprite path, and can be read back by the neutral readback contract.
        HeadlessHarness.RunCase(ctx.Report, "Render.Overlay.ComposeOnDx11", () =>
        {
            using RenderParityHarness harness = new();

            RuntimeImageMetrics control = harness.CaptureWithOverlay(
                Path.Combine(ctx.Captures, "64-overlay-control.png"), draw: null);

            HeadlessHarness.Assert(
                control.UniqueSampledColors <= 3,
                $"The overlay control frame is not a flat clear ({control.UniqueSampledColors} "
                + "colours); anything drawn over it could not be attributed to the overlay.");

            RuntimeImageMetrics composed = harness.CaptureWithOverlay(
                Path.Combine(ctx.Captures, "64-overlay-skia-text.png"),
                canvas =>
                {
                    canvas.DrawRect(24f, 24f, 380f, 96f,
                        new System.Numerics.Vector4(0.10f, 0.13f, 0.20f, 0.85f), filled: true);
                    canvas.DrawRect(24f, 24f, 380f, 96f,
                        new System.Numerics.Vector4(0.35f, 0.62f, 0.95f, 1f), strokeWidth: 2f);
                    canvas.DrawText("GENESIS OVERLAY", new System.Numerics.Vector2(40f, 40f), 30f,
                        new System.Numerics.Vector4(0.95f, 0.96f, 1f, 1f), bold: true);
                    canvas.DrawTextCentered("glyph quads from the shared GPU atlas",
                        RenderParityHarness.CaptureWidth / 2f, 84f, 520f, 15f,
                        new System.Numerics.Vector4(0.70f, 0.78f, 0.90f, 1f));
                    canvas.DrawLine(
                        new System.Numerics.Vector2(40f, 200f),
                        new System.Numerics.Vector2(600f, 260f),
                        new System.Numerics.Vector4(1f, 0.55f, 0.25f, 1f), 3f);
                });

            HeadlessHarness.Assert(
                composed.UniqueSampledColors >= 12,
                $"The composed overlay contributed only {composed.UniqueSampledColors} colours "
                + $"(control had {control.UniqueSampledColors}). Antialiased text and a translucent "
                + "panel cannot produce that few — the overlay texture is not reaching the frame.");

            double delta = RuntimeImageMetrics.MaxTileDelta(control, composed);
            HeadlessHarness.Assert(
                delta > 15,
                $"Composing the overlay moved the frame by only {delta:F2}/255 at digest "
                + $"resolution (worst tile {RuntimeImageMetrics.WorstTileIndex(control, composed)}). "
                + "The draw is being submitted but not composited.");

            // NEXT-091. The overlay deliberately draws a translucent panel, which is exactly what
            // used to survive into the capture's alpha channel and make the saved PNG show the
            // viewer's background through the game's own HUD.
            HeadlessHarness.Assert(
                harness.OverlayCaptureMinimumAlpha == 255,
                $"The overlay capture contains alpha as low as {harness.OverlayCaptureMinimumAlpha}. "
                + "Presentation ignores the back buffer's alpha, so a capture that keeps it renders "
                + "unlike the frame the player saw.");

            // Registered so the overlay lands in the capture set and gets looked at — NEXT-042 is
            // the standing reminder that screenshots catch what passing assertions do not.
            ctx.Report.Images.Add(new ImageResult(
                "Overlay — Skia text on DX11",
                "64-overlay-skia-text.png",
                composed.Width,
                composed.Height,
                composed.UniqueSampledColors,
                composed.AverageLuminance));
        });

        // Small visual proof of the migrated R2 path. Build.bat also invokes this in isolation so
        // each completed backend has a visible, non-skippable gate rather than relying on the full
        // suite happening to exercise it indirectly.
        // P6.1: Direct3D 12. The device is exercised end to end rather than merely constructed —
        // a backend that creates a device and draws nothing passes any check that stops at "no
        // exception was thrown".
        HeadlessHarness.RunCase(ctx.Report, "Render.Dx12.RendersThroughTheSharedRenderer", () =>
        {
            HeadlessHarness.Assert(
                RenderBackendCatalog.Describe(RenderBackendOption.Direct3D12).ShaderBinaryFormat
                    == GpuShaderBinaryFormat.Dxil,
                "Direct3D 12 drifted off the DXIL shader contract.");

            // Deliberately does not stand up a second device in this process: it has already driven
            // DX11 through the whole regression, and two live backends at once is not a
            // configuration Genesis ever ships. DX12's render gate is Build.bat's
            // `--backend-smoke dx12`, which runs clean; this asserts that gate is still wired in.
            HeadlessHarness.Assert(
                BackendSmokeRunner.Normalize("dx12") == "dx12",
                "The DX12 backend smoke entry point was removed or renamed.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Render.Vulkan.SmokeRequestsValidation", () =>
        {
            HeadlessHarness.Assert(
                BackendSmokeRunner.Normalize("vulkan") == "vulkan",
                "The Vulkan backend smoke entry point was removed or renamed.");

            if (!RenderBackendSelection.IsVulkanAvailable())
            {
                Console.WriteLine("      SKIPPED: no Vulkan adapter on this machine.");
                return;
            }

            // NEXT-122: the layer must attach in this process when GENESIS_VULKAN_DEBUG is set,
            // including a Release headless build. Do not create the device first — the env var
            // is read at instance construction.
            Environment.SetEnvironmentVariable("GENESIS_VULKAN_DEBUG", "1");
            VulkanRuntime.EnsureKhronosValidationSearchPath();
            VulkanRuntime.ResetValidationCapture();
            using IGpuDevice device = RenderControllerFactory.CreateDevice(RenderBackendOption.Vulkan);
            if (!VulkanRuntime.LastValidationEnabled)
            {
                // The Release smoke (BackendSmokeRunner) still fails hard — that is the P6.2 gate.
                // This suite case only proves the env-var request path; a missing SDK is not a
                // code regression.
                Console.WriteLine(
                    "      SKIPPED: VK_LAYER_KHRONOS_validation is not installed (NEXT-122). "
                    + "Install KhronosGroup.VulkanSDK via DeveloperRequirementsInstaller.ps1.");
                return;
            }

            HeadlessHarness.Assert(
                string.Equals(device.BackendName, "Vulkan", StringComparison.Ordinal),
                $"Expected a Vulkan device, got {device.BackendName}.");
        });

        // A leak that no pixel test can see: GPU timing dies silently after a few seconds of play.
        HeadlessHarness.RunCase(ctx.Report, "Render.Dx12.TimestampSlotsAreRecycled", () =>
        {
            if (!RenderBackendSelection.IsDirect3D12Available())
            {
                Console.WriteLine("      SKIPPED: no Direct3D 12 adapter on this machine.");
                return;
            }

            // The query heap holds 256 scopes. ForwardRenderer opens one per frame, so a monotonic
            // allocator exhausts it in about four seconds at 60fps and then returns Invalid forever,
            // freezing RenderStats.GpuMs at its last value with nothing to indicate why.
            using IGpuDevice device = RenderControllerFactory.CreateDevice(RenderBackendOption.Direct3D12);
            const int frames = 600;
            int issued = 0;

            for (int frame = 0; frame < frames; frame++)
            {
                device.BeginFrame();
                GpuQueryHandle scope = device.BeginTimestampScope();
                if (scope.IsValid)
                {
                    issued++;
                    device.EndTimestampScope(scope);
                }

                device.EndFrame();
                device.WaitIdle();

                // Resolve as the renderer does, which is what returns the slot to the free list.
                if (scope.IsValid)
                {
                    device.TryResolveTimestamp(scope, out _);
                }
            }

            HeadlessHarness.Assert(
                issued == frames,
                $"Only {issued} of {frames} frames could open a timestamp scope. The query heap is "
                + "not being recycled, so GPU timing stops permanently a few seconds into play.");
        });

        // Promotion and a working render path must move together, which is what
        // Dx12RenderPathVerified exists to enforce. Validate() throws if they disagree.
        HeadlessHarness.RunCase(ctx.Report, "Render.Dx12.ReadinessMatchesReality", () =>
        {
            Genesis.Rendering.Core.Dx12BackendReadiness.Validate();
            HeadlessHarness.Assert(
                Genesis.Rendering.Core.Dx12BackendReadiness.FramesInFlight >= 2,
                "Frames in flight dropped below double buffering.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Render.Parity.Sprite2D", () =>
        {
            BackendSmokeRunner.Result smoke = BackendSmokeRunner.RunOrThrow("dx11", ctx.Captures);
            HeadlessHarness.Assert(
                smoke.Metrics.UniqueSampledColors >= 4,
                $"DX11 sprite parity capture is blank ({smoke.Metrics.UniqueSampledColors} colours)." );
            HeadlessHarness.Assert(
                smoke.Metrics.AverageLuminance is > 10 and < 245,
                $"DX11 sprite parity luminance is implausible ({smoke.Metrics.AverageLuminance:F1}).");
        });

        HeadlessHarness.RunCase(ctx.Report, "Render.Batching.CompatibleInstancesCollapse", () =>
        {
            // R7.9 Auto-Instancing Draw Queue: repeated DrawMesh with the same mesh + material
            // fingerprint must collapse into BatchKey → DrawIndexedInstanced (no manual batching).
            using RenderParityHarness harness = new();
            _ = harness.Capture(Path.Combine(ctx.Captures, "60-compatible-batching.png"));
            RenderStats stats = harness.LastStats;
            HeadlessHarness.Assert(
                stats.ItemsSubmitted >= 12 && stats.InstancesDrawn >= 12
                && stats.Batches > 0 && stats.Batches < stats.ItemsSubmitted,
                $"Compatible scene submissions did not collapse into larger instanced batches "
                + $"(submitted={stats.ItemsSubmitted}, instances={stats.InstancesDrawn}, "
                + $"batches={stats.Batches}, drawCalls3D={stats.DrawCalls3D}).");
        });

        HeadlessHarness.RunCase(ctx.Report, "Render.Batching.AutoInstancingDrawQueue", () =>
        {
            // Checklist name for R7.9 — same Capture / BatchKey motorway as CompatibleInstancesCollapse.
            using RenderParityHarness harness = new();
            _ = harness.Capture(Path.Combine(ctx.Captures, "60-auto-instancing-draw-queue.png"));
            RenderStats stats = harness.LastStats;
            HeadlessHarness.Assert(
                stats.ItemsSubmitted >= 12 && stats.InstancesDrawn >= 12
                && stats.Batches > 0 && stats.Batches < stats.ItemsSubmitted,
                $"Auto-instancing draw queue did not bucket identical opaque draws "
                + $"(submitted={stats.ItemsSubmitted}, instances={stats.InstancesDrawn}, "
                + $"batches={stats.Batches}, drawCalls3D={stats.DrawCalls3D}).");
        });

        HeadlessHarness.RunCase(ctx.Report, "Render.Batching.IsFloorInstancesCollapse", () =>
        {
            // R7.7: opaque MeshDrawFlags.IsFloor used to park one WorldMesh DrawIndexed each.
            // Play path: PGSL DrawFloor3D / floor-flagged blocks (obstacle course). Same mesh +
            // flags must instance. ItemsSubmitted is zero after Flush (cleared accumulator); use
            // WorldMeshes / Batches / InstancesDrawn from the completed frame instead.
            using RuntimeViewportHarness harness = new();
            (_, RenderStats stats) = harness.CaptureFloorBatchCollapse(
                Path.Combine(ctx.Captures, "60-isfloor-batching.png"));
            HeadlessHarness.Assert(
                stats.WorldMeshes == 0,
                $"Opaque IsFloor draws still parked on WorldMeshes ({stats.WorldMeshes}); "
                + "they must join the instanced batch path.");
            HeadlessHarness.Assert(
                stats.InstancesDrawn >= 24,
                $"Floor-batch probe drew too few instances ({stats.InstancesDrawn}); expected ≥24 cubes.");
            HeadlessHarness.Assert(
                stats.Batches > 0 && stats.Batches < 24,
                $"IsFloor cubes did not collapse into instanced batches "
                + $"(batches={stats.Batches}, drawCalls3D={stats.DrawCalls3D}, "
                + $"worldMeshes={stats.WorldMeshes}).");
        });

        // The gate the whole ForwardRenderer port depends on. Renders one scene that touches every
        // path in the renderer and compares its digest against a recorded baseline.
        HeadlessHarness.RunCase(ctx.Report, "Render.Parity.GoldenScene.Dx11", () =>
        {
            using RenderParityHarness harness = new();

            RuntimeImageMetrics first = harness.Capture(
                Path.Combine(ctx.Captures, "61-golden-scene-dx11.png"));
            RuntimeImageMetrics second = harness.Capture(
                Path.Combine(ctx.Captures, "61-golden-scene-dx11-repeat.png"));

            // Determinism first: a baseline recorded from a scene that varies run to run is worse
            // than no baseline, because it fails at random and trains people to re-record it.
            double selfDelta = RuntimeImageMetrics.MaxTileDelta(first, second);
            HeadlessHarness.Assert(
                selfDelta == 0,
                $"The golden scene is not deterministic: two captures in one run differ by "
                + $"{selfDelta:F2}/255 at tile {RuntimeImageMetrics.WorstTileIndex(first, second)}. "
                + "Something in the scene depends on wall-clock time, frame count or unseeded randomness.");

            // Sanity: the scene must actually be drawing the batches it claims to.
            HeadlessHarness.Assert(
                harness.LastStats.DrawCalls3D > 0 && harness.LastStats.InstancesDrawn >= 12,
                $"The golden scene submitted too little to be a meaningful baseline "
                + $"(draws3D={harness.LastStats.DrawCalls3D}, instances={harness.LastStats.InstancesDrawn}, "
                + $"batches={harness.LastStats.Batches}).");

            string baselineFile = Path.Combine(ResolveBaselineDirectory(ctx), "golden-dx11.json");

            if (!File.Exists(baselineFile))
            {
                WriteBaseline(baselineFile, first);
                Console.WriteLine($"      BASELINE RECORDED (none existed): {baselineFile}");
                return;
            }

            GoldenBaseline recorded = ReadBaseline(baselineFile);
            RuntimeImageMetrics expected = recorded.ToMetrics();

            if (ctx.UpdateBaselines)
            {
                double replaced = RuntimeImageMetrics.MaxTileDelta(expected, first);
                WriteBaseline(baselineFile, first);
                Console.WriteLine(
                    $"      BASELINE UPDATED on request (was drifting {replaced:F2}/255): {baselineFile}");
                return;
            }

            HeadlessHarness.Assert(
                first.Width == expected.Width && first.Height == expected.Height,
                $"Capture is {first.Width}x{first.Height} but the baseline is "
                + $"{expected.Width}x{expected.Height}; re-record with --update-baselines.");

            // Same backend, so this is a near-exact comparison: 2/255 absorbs driver float jitter
            // and nothing else. Cross-backend comparisons will need a looser bound, because
            // filtering, fill rules and shader optimisation legitimately differ between APIs.
            double delta = RuntimeImageMetrics.MaxTileDelta(expected, first);
            int worst = RuntimeImageMetrics.WorstTileIndex(expected, first);
            double histogram = RuntimeImageMetrics.HistogramDistance(expected, first);

            HeadlessHarness.Assert(
                delta <= 2.0,
                $"The golden scene drifted {delta:F2}/255 from its baseline at tile {worst} "
                + $"(column {worst % RuntimeImageMetrics.TileColumns}, row {worst / RuntimeImageMetrics.TileColumns} "
                + $"of {RuntimeImageMetrics.TileColumns}x{RuntimeImageMetrics.TileRows}); "
                + $"luminance distribution moved {histogram:P1}. Compare "
                + $"'61-golden-scene-dx11.png' against the previous run. If the change is intended, "
                + $"re-record with --update-baselines.");

            HeadlessHarness.Assert(
                histogram <= 0.02,
                $"Tile means match but the luminance distribution moved {histogram:P1} — a global "
                + "tone change (a tonemap applied twice, a missing gamma step) shifts the histogram "
                + "while leaving per-region means close.");
        });

        // R7.0 first cross-backend golden: DX12 vs a same-run DX11 capture, plus a DX12 self-baseline.
        // ≤8/255 is the P6.1 aspirational bound; measured same-run residual is ~0.64/255 after
        // NEXT-158 (ddx/ddy taken outside the divergent flat-normal branch).
        HeadlessHarness.RunCase(ctx.Report, "Render.Parity.GoldenScene.Dx12", () =>
        {
            if (!RenderBackendSelection.IsDirect3D12Available())
            {
                Console.WriteLine("      SKIPPED: no Direct3D 12 adapter on this machine.");
                return;
            }

            const double crossBackendTileTolerance = 8.0;
            const double crossBackendHistogramTolerance = 0.05;

            RuntimeImageMetrics dx11Reference;
            using (RenderParityHarness dx11Harness = new())
            {
                dx11Reference = dx11Harness.Capture(
                    Path.Combine(ctx.Captures, "61-golden-scene-dx12-vs-dx11.png"));
            }

            using RenderParityHarness harness = new(RenderBackendOption.Direct3D12);

            RuntimeImageMetrics first = harness.Capture(
                Path.Combine(ctx.Captures, "61-golden-scene-dx12.png"));
            RuntimeImageMetrics second = harness.Capture(
                Path.Combine(ctx.Captures, "61-golden-scene-dx12-repeat.png"));

            double selfDelta = RuntimeImageMetrics.MaxTileDelta(first, second);
            HeadlessHarness.Assert(
                selfDelta == 0,
                $"The DX12 golden scene is not deterministic: two captures differ by "
                + $"{selfDelta:F2}/255 at tile {RuntimeImageMetrics.WorstTileIndex(first, second)}.");

            HeadlessHarness.Assert(
                harness.LastStats.DrawCalls3D > 0 && harness.LastStats.InstancesDrawn >= 12,
                $"The DX12 golden scene submitted too little "
                + $"(draws3D={harness.LastStats.DrawCalls3D}, instances={harness.LastStats.InstancesDrawn}).");

            string baselineDir = ResolveBaselineDirectory(ctx);
            string dx12BaselineFile = Path.Combine(baselineDir, "golden-dx12.json");

            if (!File.Exists(dx12BaselineFile))
            {
                WriteBaseline(dx12BaselineFile, first);
                Console.WriteLine($"      BASELINE RECORDED (none existed): {dx12BaselineFile}");
            }
            else if (ctx.UpdateBaselines)
            {
                GoldenBaseline previous = ReadBaseline(dx12BaselineFile);
                double replaced = RuntimeImageMetrics.MaxTileDelta(previous.ToMetrics(), first);
                WriteBaseline(dx12BaselineFile, first);
                Console.WriteLine(
                    $"      BASELINE UPDATED on request (was drifting {replaced:F2}/255): {dx12BaselineFile}");
            }
            else
            {
                GoldenBaseline recorded = ReadBaseline(dx12BaselineFile);
                RuntimeImageMetrics expected = recorded.ToMetrics();
                HeadlessHarness.Assert(
                    first.Width == expected.Width && first.Height == expected.Height,
                    $"DX12 capture is {first.Width}x{first.Height} but golden-dx12.json is "
                    + $"{expected.Width}x{expected.Height}; re-record with --update-baselines.");

                double dx12Self = RuntimeImageMetrics.MaxTileDelta(expected, first);
                int worstSelf = RuntimeImageMetrics.WorstTileIndex(expected, first);
                HeadlessHarness.Assert(
                    dx12Self <= 2.0,
                    $"DX12 golden drifted {dx12Self:F2}/255 from golden-dx12.json at tile {worstSelf}. "
                    + "Re-record with --update-baselines if intentional.");
            }

            HeadlessHarness.Assert(
                first.Width == dx11Reference.Width && first.Height == dx11Reference.Height,
                $"DX12 capture is {first.Width}x{first.Height} but the same-run DX11 reference is "
                + $"{dx11Reference.Width}x{dx11Reference.Height}.");

            double crossDelta = RuntimeImageMetrics.MaxTileDelta(dx11Reference, first);
            int worstCross = RuntimeImageMetrics.WorstTileIndex(dx11Reference, first);
            double crossHistogram = RuntimeImageMetrics.HistogramDistance(dx11Reference, first);

            HeadlessHarness.Assert(
                crossDelta <= crossBackendTileTolerance,
                $"DX12 golden drifted {crossDelta:F2}/255 from same-run DX11 at tile {worstCross} "
                + $"(limit {crossBackendTileTolerance}/255). Compare '61-golden-scene-dx12.png' to "
                + $"'61-golden-scene-dx12-vs-dx11.png'.");

            HeadlessHarness.Assert(
                crossHistogram <= crossBackendHistogramTolerance,
                $"DX12 vs DX11 luminance distribution moved {crossHistogram:P1} "
                + $"(limit {crossBackendHistogramTolerance:P0}).");

            Console.WriteLine(
                $"      DX12 vs DX11 golden (same run): tile Δ {crossDelta:F2}/255, "
                + $"histogram {crossHistogram:P2}.");
        });

        // R7.0 next cross-backend golden: Vulkan vs same-run DX11 + Vulkan self-baseline.
        HeadlessHarness.RunCase(ctx.Report, "Render.Parity.GoldenScene.Vulkan", () =>
        {
            if (!RenderBackendSelection.IsVulkanAvailable())
            {
                Console.WriteLine("      SKIPPED: no Vulkan adapter on this machine.");
                return;
            }

            const double crossBackendTileTolerance = 8.0;
            const double crossBackendHistogramTolerance = 0.05;

            RuntimeImageMetrics dx11Reference;
            using (RenderParityHarness dx11Harness = new())
            {
                dx11Reference = dx11Harness.Capture(
                    Path.Combine(ctx.Captures, "61-golden-scene-vulkan-vs-dx11.png"));
            }

            using RenderParityHarness harness = new(RenderBackendOption.Vulkan);

            RuntimeImageMetrics first = harness.Capture(
                Path.Combine(ctx.Captures, "61-golden-scene-vulkan.png"));
            RuntimeImageMetrics second = harness.Capture(
                Path.Combine(ctx.Captures, "61-golden-scene-vulkan-repeat.png"));

            double selfDelta = RuntimeImageMetrics.MaxTileDelta(first, second);
            HeadlessHarness.Assert(
                selfDelta == 0,
                $"The Vulkan golden scene is not deterministic: two captures differ by "
                + $"{selfDelta:F2}/255 at tile {RuntimeImageMetrics.WorstTileIndex(first, second)}.");

            HeadlessHarness.Assert(
                harness.LastStats.DrawCalls3D > 0 && harness.LastStats.InstancesDrawn >= 12,
                $"The Vulkan golden scene submitted too little "
                + $"(draws3D={harness.LastStats.DrawCalls3D}, instances={harness.LastStats.InstancesDrawn}).");

            string baselineDir = ResolveBaselineDirectory(ctx);
            string vulkanBaselineFile = Path.Combine(baselineDir, "golden-vulkan.json");

            if (!File.Exists(vulkanBaselineFile))
            {
                WriteBaseline(vulkanBaselineFile, first);
                Console.WriteLine($"      BASELINE RECORDED (none existed): {vulkanBaselineFile}");
            }
            else if (ctx.UpdateBaselines)
            {
                GoldenBaseline previous = ReadBaseline(vulkanBaselineFile);
                double replaced = RuntimeImageMetrics.MaxTileDelta(previous.ToMetrics(), first);
                WriteBaseline(vulkanBaselineFile, first);
                Console.WriteLine(
                    $"      BASELINE UPDATED on request (was drifting {replaced:F2}/255): {vulkanBaselineFile}");
            }
            else
            {
                GoldenBaseline recorded = ReadBaseline(vulkanBaselineFile);
                RuntimeImageMetrics expected = recorded.ToMetrics();
                HeadlessHarness.Assert(
                    first.Width == expected.Width && first.Height == expected.Height,
                    $"Vulkan capture is {first.Width}x{first.Height} but golden-vulkan.json is "
                    + $"{expected.Width}x{expected.Height}; re-record with --update-baselines.");

                double vulkanSelf = RuntimeImageMetrics.MaxTileDelta(expected, first);
                int worstSelf = RuntimeImageMetrics.WorstTileIndex(expected, first);
                HeadlessHarness.Assert(
                    vulkanSelf <= 2.0,
                    $"Vulkan golden drifted {vulkanSelf:F2}/255 from golden-vulkan.json at tile {worstSelf}. "
                    + "Re-record with --update-baselines if intentional.");
            }

            HeadlessHarness.Assert(
                first.Width == dx11Reference.Width && first.Height == dx11Reference.Height,
                $"Vulkan capture is {first.Width}x{first.Height} but the same-run DX11 reference is "
                + $"{dx11Reference.Width}x{dx11Reference.Height}.");

            double crossDelta = RuntimeImageMetrics.MaxTileDelta(dx11Reference, first);
            int worstCross = RuntimeImageMetrics.WorstTileIndex(dx11Reference, first);
            double crossHistogram = RuntimeImageMetrics.HistogramDistance(dx11Reference, first);

            HeadlessHarness.Assert(
                crossDelta <= crossBackendTileTolerance,
                $"Vulkan golden drifted {crossDelta:F2}/255 from same-run DX11 at tile {worstCross} "
                + $"(limit {crossBackendTileTolerance}/255). Compare '61-golden-scene-vulkan.png' to "
                + $"'61-golden-scene-vulkan-vs-dx11.png'.");

            HeadlessHarness.Assert(
                crossHistogram <= crossBackendHistogramTolerance,
                $"Vulkan vs DX11 luminance distribution moved {crossHistogram:P1} "
                + $"(limit {crossBackendHistogramTolerance:P0}).");

            Console.WriteLine(
                $"      Vulkan vs DX11 golden (same run): tile Δ {crossDelta:F2}/255, "
                + $"histogram {crossHistogram:P2}.");
        });

        // R7.0 remaining backends — same-run DX11 cross check + per-backend self-baseline.
        RunCrossBackendGoldenCase(
            ctx,
            RenderBackendOption.OpenGL,
            "OpenGL",
            "opengl",
            RenderBackendSelection.IsOpenGLAvailable,
            tileTolerance: 8.0,
            histogramTolerance: 0.05,
            requireCrossBackendParity: true);



        // Software is a CPU rasteriser — gate determinism + self-baseline; cross-DX11 residual is
        // informational (docs cite large MAE). Do not fail R7.0 on DX11-band tile Δ.
        RunCrossBackendGoldenCase(
            ctx,
            RenderBackendOption.Software,
            "Software",
            "software",
            () => true,
            tileTolerance: 40.0,
            histogramTolerance: 0.20,
            requireCrossBackendParity: false);
    }

    /// <summary>
    /// AF2.7 optional smoke: enable raymarch path + non-blank capture, or skip when unavailable.
    /// </summary>
    private static void SmokeCloudFlyThroughIfAvailable(
        HeadlessContext ctx,
        RenderBackendOption backend,
        string label,
        Func<bool> isAvailable,
        Vector3 cameraPos,
        Vector3 lookTarget)
    {
        if (!isAvailable())
        {
            Console.WriteLine($"      SKIPPED: {label} cloud fly-through (adapter unavailable).");
            return;
        }

        try
        {
            using CloudFlyThroughHarness harness = new(backend);
            harness.SetCamera(cameraPos, lookTarget);
            RuntimeImageMetrics metrics = harness.Capture(
                Path.Combine(ctx.Captures, $"cloud-flythrough-smoke-{label.ToLowerInvariant()}.png"));

            HeadlessHarness.Assert(
                metrics.UniqueSampledColors >= 4,
                $"{label} cloud fly-through smoke looks blank ({metrics.UniqueSampledColors} colours).");
            HeadlessHarness.Assert(
                harness.LastStats.RaymarchedCloudsMs > 0.001,
                $"{label} cloud fly-through must run raymarch "
                + $"(RaymarchedCloudsMs={harness.LastStats.RaymarchedCloudsMs:F4}).");

            Console.WriteLine(
                $"      {label} smoke: CL={harness.LastStats.RaymarchedCloudsMs:F2} ms, "
                + $"colours={metrics.UniqueSampledColors}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      SKIPPED: {label} cloud fly-through not ready ({ex.Message}).");
        }
    }

    /// <summary>
    /// R7.0 helper: same-run DX11 reference + backend self-baseline + optional cross-backend gates.
    /// </summary>
    private static void RunCrossBackendGoldenCase(
        HeadlessContext ctx,
        RenderBackendOption backend,
        string displayName,
        string slug,
        Func<bool> isAvailable,
        double tileTolerance,
        double histogramTolerance,
        bool requireCrossBackendParity,
        bool skipWhenCaptureUnready = false)
    {
        HeadlessHarness.RunCase(ctx.Report, $"Render.Parity.GoldenScene.{displayName.Replace(" ", string.Empty)}", () =>
        {
            if (!isAvailable())
            {
                Console.WriteLine($"      SKIPPED: no {displayName} adapter on this machine.");
                return;
            }

            EnsureShaderCatalogForBackend(backend);

            RuntimeImageMetrics dx11Reference;
            using (RenderParityHarness dx11Harness = new())
            {
                dx11Reference = dx11Harness.Capture(
                    Path.Combine(ctx.Captures, $"61-golden-scene-{slug}-vs-dx11.png"));
            }

            RuntimeImageMetrics first;
            RuntimeImageMetrics second;
            RenderStats lastStats;
            try
            {
                using RenderParityHarness harness = new(backend);
                first = harness.Capture(Path.Combine(ctx.Captures, $"61-golden-scene-{slug}.png"));
                second = harness.Capture(Path.Combine(ctx.Captures, $"61-golden-scene-{slug}-repeat.png"));
                lastStats = harness.LastStats;
            }
            catch (Exception ex) when (skipWhenCaptureUnready)
            {
                Console.WriteLine(
                    $"      SKIPPED: {displayName} golden capture not ready yet ({ex.Message}).");
                return;
            }

            double selfDelta = RuntimeImageMetrics.MaxTileDelta(first, second);
            HeadlessHarness.Assert(
                selfDelta == 0,
                $"The {displayName} golden scene is not deterministic: two captures differ by "
                + $"{selfDelta:F2}/255 at tile {RuntimeImageMetrics.WorstTileIndex(first, second)}.");

            HeadlessHarness.Assert(
                lastStats.DrawCalls3D > 0 && lastStats.InstancesDrawn >= 12,
                $"The {displayName} golden scene submitted too little "
                + $"(draws3D={lastStats.DrawCalls3D}, instances={lastStats.InstancesDrawn}).");

            string baselineDir = ResolveBaselineDirectory(ctx);
            string baselineFile = Path.Combine(baselineDir, $"golden-{slug}.json");

            if (!File.Exists(baselineFile))
            {
                WriteBaseline(baselineFile, first);
                Console.WriteLine($"      BASELINE RECORDED (none existed): {baselineFile}");
            }
            else if (ctx.UpdateBaselines)
            {
                GoldenBaseline previous = ReadBaseline(baselineFile);
                double replaced = RuntimeImageMetrics.MaxTileDelta(previous.ToMetrics(), first);
                WriteBaseline(baselineFile, first);
                Console.WriteLine(
                    $"      BASELINE UPDATED on request (was drifting {replaced:F2}/255): {baselineFile}");
            }
            else
            {
                GoldenBaseline recorded = ReadBaseline(baselineFile);
                RuntimeImageMetrics expected = recorded.ToMetrics();
                HeadlessHarness.Assert(
                    first.Width == expected.Width && first.Height == expected.Height,
                    $"{displayName} capture is {first.Width}x{first.Height} but golden-{slug}.json is "
                    + $"{expected.Width}x{expected.Height}; re-record with --update-baselines.");

                double selfBaseline = RuntimeImageMetrics.MaxTileDelta(expected, first);
                int worstSelf = RuntimeImageMetrics.WorstTileIndex(expected, first);
                HeadlessHarness.Assert(
                    selfBaseline <= 2.0,
                    $"{displayName} golden drifted {selfBaseline:F2}/255 from golden-{slug}.json at tile {worstSelf}. "
                    + "Re-record with --update-baselines if intentional.");
            }

            HeadlessHarness.Assert(
                first.Width == dx11Reference.Width && first.Height == dx11Reference.Height,
                $"{displayName} capture is {first.Width}x{first.Height} but the same-run DX11 reference is "
                + $"{dx11Reference.Width}x{dx11Reference.Height}.");

            double crossDelta = RuntimeImageMetrics.MaxTileDelta(dx11Reference, first);
            int worstCross = RuntimeImageMetrics.WorstTileIndex(dx11Reference, first);
            double crossHistogram = RuntimeImageMetrics.HistogramDistance(dx11Reference, first);

            Console.WriteLine(
                $"      {displayName} vs DX11 golden (same run): tile Δ {crossDelta:F2}/255, "
                + $"histogram {crossHistogram:P2}"
                + (requireCrossBackendParity ? "." : " (informational — cross-parity not required)."));

            if (!requireCrossBackendParity)
                return;

            HeadlessHarness.Assert(
                crossDelta <= tileTolerance,
                $"{displayName} golden drifted {crossDelta:F2}/255 from same-run DX11 at tile {worstCross} "
                + $"(limit {tileTolerance}/255). Compare '61-golden-scene-{slug}.png' to "
                + $"'61-golden-scene-{slug}-vs-dx11.png'.");

            HeadlessHarness.Assert(
                crossHistogram <= histogramTolerance,
                $"{displayName} vs DX11 luminance distribution moved {crossHistogram:P1} "
                + $"(limit {histogramTolerance:P0}).");
        });
    }

    private static void EnsureShaderCatalogForBackend(RenderBackendOption backend)
    {
        RenderBackendDescriptor descriptor = RenderBackendCatalog.Describe(backend);
        if (descriptor.ShaderBinaryFormat == GpuShaderBinaryFormat.GlslUtf8)
            EngineShaderCatalog.CompileAll(GpuShaderBinaryFormat.GlslUtf8);
    }

    private static void WriteTestTga(string path, int width, int height, bool normalLike)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));

        byte[] bytes = new byte[checked(18 + (width * height * 4))];
        bytes[2] = 2; // uncompressed true-colour image
        bytes[12] = (byte)(width & 0xFF);
        bytes[13] = (byte)((width >> 8) & 0xFF);
        bytes[14] = (byte)(height & 0xFF);
        bytes[15] = (byte)((height >> 8) & 0xFF);
        bytes[16] = 32;
        bytes[17] = 0x28; // top-left origin + 8 alpha bits

        int offset = 18;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte r;
                byte g;
                byte b;
                if (normalLike)
                {
                    r = (byte)(112 + ((x * 31) % 32));
                    g = (byte)(112 + ((y * 29) % 32));
                    b = 255;
                }
                else
                {
                    r = (byte)(30 + ((x * 23) % 200));
                    g = (byte)(40 + ((y * 19) % 190));
                    b = (byte)(50 + (((x + y) * 17) % 180));
                }

                // TGA stores true-colour pixels as BGRA.
                bytes[offset++] = b;
                bytes[offset++] = g;
                bytes[offset++] = r;
                bytes[offset++] = 255;
            }
        }

        File.WriteAllBytes(path, bytes);
    }

    // ── Baseline storage ────────────────────────────────────────────────────────
    /// <summary>
    /// Baselines live at <c>&lt;repo&gt;/TestResults/Baselines</c>, resolved by walking up from the
    /// assembly rather than from the working directory. Build.bat runs the suite from the repo root
    /// while a direct run of the exe has its own bin folder as the working directory, and a baseline
    /// that lands in two different places depending on how it was launched is not a baseline — each
    /// location would silently bootstrap its own and neither would ever catch a drift.
    /// <c>HeadlessTestRunner.PrepareOutput</c> only clears <c>TestResults/latest</c>, so this
    /// sibling directory survives a run.
    /// </summary>
    private static string ResolveBaselineDirectory(HeadlessContext ctx)
    {
        DirectoryInfo? probe = new(AppContext.BaseDirectory);
        while (probe is not null)
        {
            if (File.Exists(Path.Combine(probe.FullName, "Build.bat")))
            {
                return Path.Combine(probe.FullName, "TestResults", "Baselines");
            }

            probe = probe.Parent;
        }

        // No repo marker (an installed or relocated copy) — keep them beside this run's output.
        return Path.Combine(Path.GetDirectoryName(ctx.OutputRoot) ?? ctx.OutputRoot, "Baselines");
    }

    private static string ResolveRepositoryRoot()
    {
        DirectoryInfo? probe = new(AppContext.BaseDirectory);
        while (probe is not null)
        {
            if (File.Exists(Path.Combine(probe.FullName, "Build.bat"))) return probe.FullName;
            probe = probe.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the Genesis repository above '{AppContext.BaseDirectory}'.");
    }

    /// <summary>Flattened digest, kept as a purpose-built DTO so the on-disk shape is stable.</summary>
    private sealed record GoldenBaseline(
        int Width,
        int Height,
        int UniqueSampledColors,
        double AverageLuminance,
        double[] Tiles,
        int[] Histogram)
    {
        public static GoldenBaseline From(RuntimeImageMetrics metrics)
        {
            double[] flat = new double[metrics.Tiles.Count * 3];
            for (int i = 0; i < metrics.Tiles.Count; i++)
            {
                flat[(i * 3) + 0] = metrics.Tiles[i].R;
                flat[(i * 3) + 1] = metrics.Tiles[i].G;
                flat[(i * 3) + 2] = metrics.Tiles[i].B;
            }

            return new GoldenBaseline(
                metrics.Width,
                metrics.Height,
                metrics.UniqueSampledColors,
                metrics.AverageLuminance,
                flat,
                [.. metrics.Histogram]);
        }

        public RuntimeImageMetrics ToMetrics()
        {
            TileColor[] tiles = new TileColor[Tiles.Length / 3];
            for (int i = 0; i < tiles.Length; i++)
            {
                tiles[i] = new TileColor(Tiles[(i * 3) + 0], Tiles[(i * 3) + 1], Tiles[(i * 3) + 2]);
            }

            return new RuntimeImageMetrics(Width, Height, UniqueSampledColors, AverageLuminance)
            {
                Tiles = tiles,
                Histogram = Histogram,
            };
        }
    }

    private static void WriteBaseline(string file, RuntimeImageMetrics metrics)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(
            file,
            System.Text.Json.JsonSerializer.Serialize(
                GoldenBaseline.From(metrics), HeadlessHarness.JsonOptions));
    }

    private static GoldenBaseline ReadBaseline(string file) =>
        System.Text.Json.JsonSerializer.Deserialize<GoldenBaseline>(File.ReadAllText(file))
        ?? throw new InvalidOperationException($"Baseline '{file}' is empty or unreadable.");

    private static EnvironmentFrame MakeWeatherMapTestFrame(
        double elapsedRealSeconds,
        Vector3 localWind,
        float cloudCover,
        WeatherKind kind)
    {
        WeatherState weather = new(
            kind,
            cloudCover,
            Rain: 0f,
            Snow: 0f,
            Hail: 0f,
            WindDirection: localWind.LengthSquared() > 1e-8f ? Vector3.Normalize(localWind) : Vector3.UnitX,
            WindSpeed: localWind.Length(),
            TemperatureC: 15f,
            FogDensity: 0f,
            Lightning: kind == WeatherKind.Thunderstorm ? 0.4f : 0f);

        return new EnvironmentFrame(
            elapsedRealSeconds,
            DayIndex: 0,
            TimeOfDayHours: 12f,
            SunDirection: new Vector3(0.2f, -0.9f, 0.3f),
            MoonDirection: new Vector3(-0.2f, 0.9f, -0.3f),
            NightFactor: 0f,
            MoonPhase: 0.5f,
            weather,
            localWind,
            LocalRain: 0f,
            LocalSnow: 0f,
            LocalHail: 0f,
            LocalTemperatureC: 15f,
            GroundWetness: 0f,
            SnowAccumulation: 0f,
            EnvironmentLocalSample.Open(Vector3.Zero),
            BackgroundColor: new Vector4(0.4f, 0.55f, 0.8f, 1f),
            FogColor: new Vector4(0.6f, 0.65f, 0.7f, 1f),
            AmbientSky: new Vector3(0.3f, 0.35f, 0.5f),
            AmbientGround: new Vector3(0.15f, 0.12f, 0.1f));
    }

    private static void AssertWeatherChannelsInUnitInterval(WeatherMapSample sample)
    {
        HeadlessHarness.Assert(
            sample.Coverage is >= 0f and <= 1f
            && sample.CloudType is >= 0f and <= 1f
            && sample.Erosion is >= 0f and <= 1f
            && sample.VerticalDevelopment is >= 0f and <= 1f,
            $"Weather map channels must stay in [0,1] "
            + $"(C={sample.Coverage}, T={sample.CloudType}, E={sample.Erosion}, V={sample.VerticalDevelopment}).");
    }
}
