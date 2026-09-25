#nullable enable annotations

using System;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Particles;

/// <summary>
/// The single backend-selection rule for particle execution.
/// Hardware rendering may never silently fall back to CPU particle simulation.
/// </summary>
public enum ParticleExecutionTarget
{
    Pending = 0,
    Gpu = 1,
    CpuSoftware = 2,
    UnsupportedHardware = 3,
}

public readonly record struct ParticleExecutionDecision(
    ParticleExecutionTarget Target,
    string BackendName,
    bool ComputeAvailable,
    bool IndirectDrawAvailable)
{
    public bool UsesGpu => Target == ParticleExecutionTarget.Gpu;
    public bool UsesCpu => Target == ParticleExecutionTarget.CpuSoftware;
    public bool CanExecute => Target is ParticleExecutionTarget.Gpu or ParticleExecutionTarget.CpuSoftware;

    public string StatusText => Target switch
    {
        ParticleExecutionTarget.Gpu => "GPU target",
        ParticleExecutionTarget.CpuSoftware => "CPU target (Software backend)",
        ParticleExecutionTarget.UnsupportedHardware => "GPU unavailable — CPU fallback disabled",
        _ => "particle backend pending",
    };
}

/// <summary>
/// Resolves the execution target from the renderer actually selected by Studio/Player.
/// </summary>
public static class ParticleExecutionPolicy
{
    public static ParticleExecutionDecision Resolve(IRenderController? renderer)
    {
        if (renderer is null)
            return new ParticleExecutionDecision(
                ParticleExecutionTarget.Pending, string.Empty, false, false);

        return Resolve(
            renderer.BackendName,
            renderer.SupportsComputeShaders,
            renderer.SupportsIndirectDraw);
    }

    /// <summary>
    /// Pure overload used by regression tests and diagnostics.
    /// </summary>
    public static ParticleExecutionDecision Resolve(
        string? backendName,
        bool computeAvailable,
        bool indirectDrawAvailable)
    {
        string backend = string.IsNullOrWhiteSpace(backendName) ? "Unknown" : backendName.Trim();
        if (string.Equals(backend, "Software", StringComparison.OrdinalIgnoreCase))
        {
            return new ParticleExecutionDecision(
                ParticleExecutionTarget.CpuSoftware,
                backend,
                computeAvailable,
                indirectDrawAvailable);
        }

        // Do not infer "GPU" just because a renderer is not called Software. Both capabilities
        // are required by the production particle path planned for Phase 2.
        if (computeAvailable && indirectDrawAvailable)
        {
            return new ParticleExecutionDecision(
                ParticleExecutionTarget.Gpu,
                backend,
                true,
                true);
        }

        return new ParticleExecutionDecision(
            ParticleExecutionTarget.UnsupportedHardware,
            backend,
            computeAvailable,
            indirectDrawAvailable);
    }

    public static void ThrowIfHardwareWouldFallbackToCpu(IRenderController? renderer)
    {
        ParticleExecutionDecision decision = Resolve(renderer);
        if (decision.Target != ParticleExecutionTarget.UnsupportedHardware)
            return;

        throw new NotSupportedException(
            $"Particle GPU execution is unavailable on '{decision.BackendName}'. "
            + "Genesis will not silently fall back to CPU particles on a hardware renderer. "
            + "Select the Software backend explicitly to use CPU particles.");
    }
}
