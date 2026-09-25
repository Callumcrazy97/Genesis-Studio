using System.Diagnostics;
using System.Drawing;
using Genesis.Shared.Interfaces;
using Genesis.Application.Runtime;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Core;
using Genesis.Rendering.SilkNet.DX11;
using Genesis.Rendering.SilkNet.Vulkan;
using RuntimeImageMetrics = Genesis.Application.Runtime.ImageMetrics;

namespace Genesis.Application.Headless;

/// <summary>
/// Fast, explicit rendering-backend gates used directly by Build.bat. A backend is added to that
/// script only when it is production-ready; unknown or unavailable names fail instead of skipping.
/// </summary>
internal static class BackendSmokeRunner
{
    internal readonly record struct Result(
        string Backend,
        string CaptureFile,
        RuntimeImageMetrics Metrics,
        TimeSpan Elapsed);

    public static int Run(string backend, string outputDirectory)
    {
        if (string.Equals(backend, "all", StringComparison.OrdinalIgnoreCase))
        {
            string[] allBackends = ["dx11", "dx12", "vulkan", "opengl", "software"];
            int failed = 0;
            foreach (var b in allBackends)
            {
                int code = Run(b, outputDirectory);
                if (code != 0) failed++;
            }
            if (failed == 0)
            {
                Console.WriteLine("\n==========================================");
                Console.WriteLine("ALL 5 RENDERING BACKENDS PASSED SMOKE TEST!");
                Console.WriteLine("==========================================");
                return 0;
            }
            Console.Error.WriteLine($"\n{failed} backends failed the smoke test.");
            return 1;
        }

        try
        {
            Result result = RunOrThrow(backend, outputDirectory);
            Console.WriteLine(
                $"BACKEND SMOKE PASSED — {result.Backend}: "
                + $"{result.Metrics.Width}x{result.Metrics.Height}, "
                + $"{result.Metrics.UniqueSampledColors} sampled colours, "
                + $"{result.Elapsed.TotalMilliseconds:F0} ms");
            Console.WriteLine($"Capture: {result.CaptureFile}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"BACKEND SMOKE FAILED — {backend}");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    public static Result RunOrThrow(string backend, string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backend);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        return Normalize(backend) switch
        {
            "dx11" => RunBackend(RenderBackendOption.SilkNetDx11, "Direct3D 11", "dx11", outputDirectory),
            "dx12" => RunBackend(RenderBackendOption.Direct3D12, "Direct3D 12", "dx12", outputDirectory),
            "vulkan" => RunBackend(RenderBackendOption.Vulkan, "Vulkan", "vulkan", outputDirectory),
            "opengl" => RunBackend(RenderBackendOption.OpenGL, "OpenGL 4.6", "opengl", outputDirectory),
            "software" => RunBackend(RenderBackendOption.Software, "Software", "software", outputDirectory),
            _ => throw new ArgumentException(
                $"Unknown rendering backend '{backend}'. Expected dx11, dx12, vulkan, opengl, software, or all.", nameof(backend)),
        };
    }

    /// <summary>Runs the identical smoke against whichever backend is named.</summary>
    /// <remarks>
    /// Parameterised rather than copied per backend. The point of the smoke is that every backend
    /// passes the <i>same</i> checks — a per-backend copy drifts, and the first thing to drift is
    /// always the assertion the new backend happens to fail.
    /// </remarks>
    private static Result RunBackend(
        RenderBackendOption option, string displayName, string slug, string outputDirectory)
    {
        // RuntimeViewportHarness' deterministic sprite scene spans x=60..580 and y=70..320.
        // Keep the probe at its native 640x360 design size so all three primitives are visible.
        const int width = 640;
        const int height = 360;
        string capture = Path.Combine(outputDirectory, $"sprite-{slug}-smoke.png");

        if (slug == "vulkan")
        {
            // NEXT-122: Release smokes used to compile the layer out and log "validation off".
            // The env var is read at instance creation, so it must be set before Configure()
            // probes IsVulkanAvailable (that probe constructs a runtime). A copy_only SDK
            // lives under LocalAppData and is not in HKLM — search that Bin first.
            Environment.SetEnvironmentVariable("GENESIS_VULKAN_DEBUG", "1");
            VulkanRuntime.EnsureKhronosValidationSearchPath();
            VulkanRuntime.ResetValidationCapture();
        }

        RenderBackendSelection.Configure(option);
        Stopwatch stopwatch = Stopwatch.StartNew();

        Console.WriteLine($"[Smoke] Starting {displayName} smoke test...");
        using (IGpuDevice device = RenderControllerFactory.CreateDevice(option))
        {
            Console.WriteLine($"[Smoke] Device created: {device.BackendName} ({device.AdapterName})");
            foreach (GpuFormat format in new[] { GpuFormat.BC5UNorm, GpuFormat.BC7UNormSrgb })
            {
                Console.WriteLine($"[Smoke] Testing {format} texture upload...");
                const int textureWidth = 8;
                const int textureHeight = 8;
                int mipLevels = GpuTextureLayout.FullMipCount(textureWidth, textureHeight);
                byte[] payload = new byte[GpuTextureLayout.GetMipChainSize(
                    format, textureWidth, textureHeight, mipLevels)];
                GpuTextureHandle compressed = device.CreateTexture(new GpuTextureDesc
                {
                    Width = textureWidth,
                    Height = textureHeight,
                    MipLevels = mipLevels,
                    ArrayLayers = 1,
                    Format = format,
                    Usage = GpuBufferUsage.Immutable,
                    BindFlags = GpuBindFlags.ShaderResource,
                    DebugName = "BackendSmoke.CompressedTexture",
                }, payload);
                if (!compressed.IsValid)
                    throw new InvalidOperationException($"{displayName} rejected a {format} cooked-texture upload.");
                device.ReleaseTexture(compressed);
                Console.WriteLine($"[Smoke] {format} texture upload succeeded!");
            }
        }

        Console.WriteLine("[Smoke] Initializing RuntimeViewportHarness...");
        RuntimeImageMetrics metrics;
        using (RuntimeViewportHarness harness = new(width, height))
        {
            Console.WriteLine("[Smoke] RuntimeViewportHarness initialized.");

            (int Width, int Height) client = harness.ViewportClientSize;
            (int Width, int Height) backend = harness.BackendRenderSize;
            Console.WriteLine($"[Smoke] ViewportClientSize: {client.Width}x{client.Height}, BackendRenderSize: {backend.Width}x{backend.Height}");
            if (client != backend)
            {
                throw new InvalidOperationException(
                    $"{displayName} backend size {backend.Width}x{backend.Height} does not match "
                    + $"the viewport {client.Width}x{client.Height}. {harness.DescribeSizes()}");
            }

            metrics = harness.Capture2D(capture);
        }

        string shaderCapture = Path.Combine(outputDirectory, $"shader-{slug}-smoke.png");
        Console.WriteLine($"[Smoke] Capturing authored shader frame for {displayName}...");
        RuntimeImageMetrics shaderMetrics;
        using (RuntimeViewportHarness shaderHarness = new(width, height))
        {
            shaderMetrics = shaderHarness.CaptureAuthoredShader(shaderCapture, swapVariant: false);
        }
        if (shaderMetrics.UniqueSampledColors < 2)
        {
            throw new InvalidOperationException(
                $"{displayName} authored-shader frame appears blank ({shaderMetrics.UniqueSampledColors} sampled colours).");
        }

        // Phase 5 fog parity regression: prove the production DX11 sprite pixel shader blends
        // toward the configured fog colour. A prior implementation calculated a fog amount but
        // multiplied RGB down, which made red fog turn sprites black.
        string fogCapture = Path.Combine(outputDirectory, $"sprite-{slug}-fog-smoke.png");
        using (RuntimeViewportHarness fogHarness = new(width, height))
        {
            fogHarness.Capture2DFog(fogCapture);
        }
        using (Bitmap fogBitmap = new(fogCapture))
        {
            Color far = fogBitmap.GetPixel(150, 120);
            if (far.R < 180 || far.G > 90 || far.B > 90)
                throw new InvalidOperationException(
                    $"{displayName} 2D fog did not blend a far white sprite toward red (sample={far.R},{far.G},{far.B}).");

            Color near = fogBitmap.GetPixel(450, 180);
            if (near.G < 160 || near.R > 100 || near.B > 100)
                throw new InvalidOperationException(
                    $"{displayName} 2D fog incorrectly affected a sprite in front of fog depth (sample={near.R},{near.G},{near.B}).");
        }

        // ── 3D ──────────────────────────────────────────────────────────────────
        // The smoke was 2D-only for its whole life, which is precisely how "3D renders on Direct3D
        // 11 and on nothing else" survived undetected: every backend passed a suite that never
        // submitted a mesh. This draws a real lit cube through ForwardRenderer — the path that
        // needs a swap-chain depth texture, an offscreen HDR target and a vertexless composite
        // blit, all three of which are where the other backends actually failed.
        string meshCapture = Path.Combine(outputDirectory, $"mesh-{slug}-smoke.png");
        Console.WriteLine($"[Smoke] Capturing 3D mesh frame for {displayName}...");
        RuntimeImageMetrics meshMetrics;
        using (RuntimeViewportHarness meshHarness = new(width, height))
        {
            meshMetrics = meshHarness.Capture3D(meshCapture);
            string updatedCapture = Path.Combine(outputDirectory, $"mesh-{slug}-updated-smoke.png");
            meshHarness.CaptureUpdatedMesh(updatedCapture);
            using Bitmap original = new(meshCapture);
            using Bitmap updated = new(updatedCapture);
            int changed = 0;
            for (int y = 0; y < original.Height; y += 3)
            for (int x = 0; x < original.Width; x += 3)
            {
                Color a = original.GetPixel(x, y), b = updated.GetPixel(x, y);
                if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 25) changed++;
            }
            if (changed < 100)
                throw new InvalidOperationException($"{displayName} did not display the persistent mesh update ({changed} changed sampled pixels).");
            Console.WriteLine($"[Smoke] Persistent mesh update OK for {displayName} ({changed} changed samples).");
        }

        using (RuntimeViewportHarness faceHarness = new(width, height))
        foreach (int camera in new[] { 0, 1, 2, 3 })
        foreach (bool reflected in new[] { false, true })
        foreach (var winding in new[] { FrontFaceWindingOverride.Default, FrontFaceWindingOverride.Clockwise, FrontFaceWindingOverride.CounterClockwise })
        {
            string cameraName = camera == 3 ? "runtime-ortho" : camera == 2 ? "runtime-camera" : camera == 1 ? "perspective" : "ortho";
            string facePath = Path.Combine(outputDirectory, $"winding-{slug}-{winding}-{cameraName}-{(reflected ? "mirrored" : "normal")}.png");
            faceHarness.CaptureFaceWinding(facePath, winding, reflected, camera is 1 or 2, camera >= 2);
            using Bitmap face = new(facePath);
            Color center = face.GetPixel(width / 2, height / 2);
            bool outside = winding != FrontFaceWindingOverride.Clockwise;
            if (outside ? center.G < 150 || center.R > 50 : center.R < 150 || center.G > 50)
                throw new InvalidOperationException($"{displayName} front-face/depth mismatch: {Path.GetFileName(facePath)} RGB {center.R},{center.G},{center.B}.");
        }
        Console.WriteLine($"[Winding] {displayName}: exterior, explicit override, mirrored transform, editor orthographic/perspective and runtime-camera probes passed.");
        using (RuntimeViewportHarness inspection = new(width, height))
        {
            string CaptureInspection(string name, RenderDebugView debug, bool wire = false, float distance = 4)
            {
                string path = Path.Combine(outputDirectory, $"view-{slug}-{name}.png");
                inspection.CaptureFaceWinding(path, FrontFaceWindingOverride.Default, debug: debug, wireframe: wire, distance: distance);
                return path;
            }
            using Bitmap normals = new(CaptureInspection("normals", RenderDebugView.Normals));
            var normal = normals.GetPixel(width / 2, height / 2);
            if (normal.B < normal.R + 20 || Math.Abs(normal.R - normal.G) > 5)
                throw new InvalidOperationException($"{displayName} normals did not replace material colour: {normal}.");
            using Bitmap nearDepth = new(CaptureInspection("depth-near", RenderDebugView.SceneDepth));
            using Bitmap farDepth = new(CaptureInspection("depth-far", RenderDebugView.SceneDepth, distance: 12));
            var nearPixel = nearDepth.GetPixel(width / 2, height / 2); var farPixel = farDepth.GetPixel(width / 2, height / 2);
            if (Math.Abs(nearPixel.R - nearPixel.G) > 3 || Math.Abs(nearPixel.G - nearPixel.B) > 3 || nearPixel.R < farPixel.R + 10)
                throw new InvalidOperationException($"{displayName} depth did not follow the camera: near={nearPixel}, far={farPixel}.");
            using Bitmap solid = new(CaptureInspection("solid", RenderDebugView.Shaded));
            using Bitmap wire = new(CaptureInspection("wireframe", RenderDebugView.Shaded, true));
            int solidCount = 0, wireCount = 0;
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                var a = solid.GetPixel(x, y); var b = wire.GetPixel(x, y);
                if (a.G > 100 && a.G > a.R + 40) solidCount++;
                if (b.G > 100 && b.G > b.R + 40) wireCount++;
            }
            if (wireCount < 50 || wireCount > solidCount / 4)
                throw new InvalidOperationException($"{displayName} wireframe coverage incorrect: wire={wireCount}, solid={solidCount}.");
            Console.WriteLine($"[View] {displayName}: normals, camera depth and wireframe pixel probes passed.");
        }
        stopwatch.Stop();
        if (metrics.Width != width || metrics.Height != height)
        {
            throw new InvalidOperationException(
                $"{displayName} sprite readback was {metrics.Width}x{metrics.Height}; expected {width}x{height}.");
        }
        if (metrics.UniqueSampledColors < 4)
        {
            throw new InvalidOperationException(
                $"{displayName} sprite frame appears blank ({metrics.UniqueSampledColors} sampled colours)." );
        }

        // Background + unlit particle quads can reach seven sampled colours even when every
        // opaque mesh is missing. Require enough tonal variation to prove the lit floor/cubes
        // actually reached the frame; the deterministic reference scene produces well over 100.
        if (meshMetrics.UniqueSampledColors < 12)
        {
            throw new InvalidOperationException(
                $"{displayName} rendered an incomplete 3D scene "
                + $"({meshMetrics.UniqueSampledColors} sampled colours; expected at least 12). "
                + "The floor and lit cubes must be visible in addition to the sky/particles.");
        }

        // A successful first frame does not prove swap-chain ownership is correct. Several
        // explicit backends historically resized only the native surface while retaining stale
        // colour/depth handles at the old dimensions. Exercise the same public resize path the
        // editors use, then read a freshly rendered frame back at the new size.
        const int resizedWidth = 704;
        const int resizedHeight = 396;
        using RuntimeViewportHarness resizeHarness = new(width, height);
        if (!resizeHarness.ResizeTo(resizedWidth, resizedHeight))
        {
            throw new InvalidOperationException(
                $"{displayName} did not settle its swap chain after resize: {resizeHarness.DescribeSizes()}.");
        }
        string resizedCapture = Path.Combine(outputDirectory, $"mesh-{slug}-resized-smoke.png");
        RuntimeImageMetrics resizedMetrics = resizeHarness.Capture3D(resizedCapture);
        if (resizedMetrics.Width != resizedWidth || resizedMetrics.Height != resizedHeight)
        {
            throw new InvalidOperationException(
                $"{displayName} resize readback was {resizedMetrics.Width}x{resizedMetrics.Height}; "
                + $"expected {resizedWidth}x{resizedHeight}.");
        }

        Console.WriteLine(
            $"[Smoke] 3D mesh frame OK for {displayName} "
            + $"({meshMetrics.UniqueSampledColors} sampled colours).");

        if (slug == "vulkan")
        {
            AssertVulkanValidationClean(displayName);
        }

        return new Result(displayName, capture, metrics, stopwatch.Elapsed);
    }

    /// <summary>
    /// NEXT-122. Pixel output is not the Phase 6.2 gate — the Khronos layer catching
    /// NEXT-123/124-class bugs is. Fail if the layer never attached, or if it reported errors.
    /// </summary>
    private static void AssertVulkanValidationClean(string displayName)
    {
        if (!VulkanRuntime.LastValidationEnabled)
        {
            throw new InvalidOperationException(
                $"{displayName} smoke ran without VK_LAYER_KHRONOS_validation (NEXT-122). "
                + "Run DeveloperRequirementsInstaller.ps1 or BuildTools\\EnsureVulkanSdk.ps1 "
                + "(user-local copy is enough; System32 vulkaninfo from a GPU driver is not).");
        }

        string[] errors = VulkanRuntime.CopyValidationErrors();
        if (errors.Length == 0)
        {
            Console.WriteLine($"[Smoke] {displayName} validation layer on, 0 errors.");
            return;
        }

        throw new InvalidOperationException(
            $"{displayName} reported {errors.Length} validation error(s):{Environment.NewLine}"
            + string.Join(Environment.NewLine, errors));
    }

    internal static string Normalize(string backend) =>
        backend.Trim().Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "direct3d11" or "d3d11" or "dx11" => "dx11",
            "direct3d12" or "d3d12" or "dx12" => "dx12",
            "vk" or "vulkan" => "vulkan",
            "gl" or "opengl" => "opengl",
            "wgpu" or "webgpu" => "webgpu",
            "sdl" or "sdl3" or "sdl3gpu" => "sdl3",
            "sw" or "cpu" or "software" => "software",
            string value => value,
        };
}
