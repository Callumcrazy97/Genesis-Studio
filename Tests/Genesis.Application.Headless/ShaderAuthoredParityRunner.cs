using Genesis.Application.Runtime;
using Genesis.Rendering.Core;
using RuntimeImageMetrics = Genesis.Application.Runtime.ImageMetrics;

namespace Genesis.Application.Headless;

/// <summary>
/// Live authored-shader golden/parity across the seven production backends. Each backend compiles
/// the same sprite shader, samples two bound textures, and is compared against Direct3D 11.
/// </summary>
internal static class ShaderAuthoredParityRunner
{
    private static readonly (RenderBackendOption Option, string Slug)[] Backends =
    [
        (RenderBackendOption.SilkNetDx11, "dx11"),
        (RenderBackendOption.Direct3D12, "dx12"),
        (RenderBackendOption.Vulkan, "vulkan"),
        (RenderBackendOption.OpenGL, "opengl"),
        (RenderBackendOption.Software, "software"),
    ];

    /// <summary>Tile mean RGB delta allowed against the DX11 golden for a custom authored shader.</summary>
    public const double CrossBackendTileTolerance = 32.0;

    public readonly record struct Capture(string Slug, string File, RuntimeImageMetrics Metrics);

    public static IReadOnlyList<Capture> RunAll(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var captures = new List<Capture>(Backends.Length);
        RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
        try
        {
            foreach ((RenderBackendOption option, string slug) in Backends)
            {
                if (!RenderBackendCatalog.Describe(option).IsImplemented)
                {
                    throw new InvalidOperationException(
                        $"{slug} is no longer registered as an implemented shader backend.");
                }

                if (!RenderBackendSelection.IsAvailable(option))
                {
                    Console.WriteLine($"      SKIPPED authored-shader parity on {slug}: backend is not available on this machine.");
                    continue;
                }

                RenderBackendSelection.Configure(option);
                string captureFile = Path.Combine(outputDirectory, $"shader-authored-{slug}.png");
                using RuntimeViewportHarness harness = new(640, 360);
                RuntimeImageMetrics metrics = harness.CaptureAuthoredShader(captureFile, swapVariant: false);
                captures.Add(new Capture(slug, captureFile, metrics));
            }
        }
        finally
        {
            RenderBackendSelection.Configure(previous);
        }

        return captures;
    }

    public static Capture RunCurrentBackend(string outputDirectory, string slug, bool swapVariant)
    {
        Directory.CreateDirectory(outputDirectory);
        string captureFile = Path.Combine(outputDirectory, $"shader-authored-{slug}{(swapVariant ? "-swap" : "")}.png");
        using RuntimeViewportHarness harness = new(640, 360);
        RuntimeImageMetrics metrics = harness.CaptureAuthoredShader(captureFile, swapVariant);
        return new Capture(slug, captureFile, metrics);
    }
}
