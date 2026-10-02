using System;
using System.Numerics;
using Genesis.Rendering.Abstractions;

namespace Genesis.Rendering.Primitives;

/// <summary>Which GPU particle draws a <see cref="ForwardRenderer.ExternalParticles"/> call wants.</summary>
internal enum ParticleDrawPhase
{
    /// <summary>Every 3D emitter, straight into the scene target (no froxel layer this frame).</summary>
    All,

    /// <summary>Multiply-blended emitters only: they darken the scene, which the layer cannot express.</summary>
    MultiplyOnly,

    /// <summary>Alpha and additive emitters, premultiplied and self-fogged, into the particle layer.</summary>
    Layer,
}

/// <summary>
/// Separate translucency for GPU compute particles. Particles write no depth, so fogging them in
/// the scene target let the composite fog them at the depth of whatever stood behind them: a
/// sprite in front of a distant wall came out almost pure fog colour. With the froxel volume
/// active they instead draw into their own premultiplied HDR layer, each fogged at its own depth,
/// and the composite lays that layer over the fogged scene: <c>scene · (1 − a) + layer</c>.
/// </summary>
/// <remarks>
/// That is exact for alpha blending (a particle over a fogged background) and for additive
/// blending (its light only loses transmittance). The layer has no depth attachment — only the
/// DX11 backend honours a borrowed depth attachment — so the particle shader tests itself
/// against the scene depth. Bloom extracts from the scene with the layer composited on, so
/// bright sparks still glow.
/// </remarks>
internal sealed partial class ForwardRenderer
{
    private GpuRenderTargetHandle _particleLayerTarget;
    private GpuTextureHandle _particleLayerTexture;
    private int _particleLayerWidth, _particleLayerHeight;
    private bool _particleLayerThisFrame;

    /// <summary>
    /// True when the host has 3D particles that the layer would draw this frame; lets a frame
    /// without them skip clearing a full-resolution target.
    /// </summary>
    internal Func<bool> ExternalParticlesPending;

    /// <summary>The GPU particle layer ran this frame (for tests and diagnostics).</summary>
    public bool LastFrameParticleLayer => _particleLayerThisFrame;

    private bool ShouldRunParticleLayer(bool canPost) =>
        canPost
        && _froxelsActiveThisFrame
        && ExternalParticles != null
        && ExternalParticlesPending?.Invoke() == true
        && !string.Equals(_gpu.BackendName, "Software", StringComparison.OrdinalIgnoreCase);

    private void EnsureParticleLayerTarget(int width, int height)
    {
        if (_particleLayerTarget.IsValid && width == _particleLayerWidth && height == _particleLayerHeight)
            return;
        ReleaseParticleLayerTarget();
        _particleLayerWidth = width;
        _particleLayerHeight = height;
        _particleLayerTarget = _gpu.CreateRenderTarget(new GpuRenderTargetDesc
        {
            Width = width,
            Height = height,
            ColorFormats = new[] { GpuFormat.R16G16B16A16Float },
            DepthFormat = GpuFormat.Unknown,
            DepthSampleable = false,
            DebugName = "GPU particle layer",
        });
        _particleLayerTexture = _gpu.GetRenderTargetTexture(_particleLayerTarget, 0);
    }

    private void ReleaseParticleLayerTarget()
    {
        if (_particleLayerTarget.IsValid)
            _gpu.ReleaseRenderTarget(_particleLayerTarget);
        _particleLayerTarget = GpuRenderTargetHandle.Invalid;
        _particleLayerTexture = GpuTextureHandle.Invalid;
        _particleLayerWidth = _particleLayerHeight = 0;
    }

    /// <summary>
    /// Draws the alpha and additive GPU particles into the layer after the main pass. The main-view
    /// EngineCB is still current, the froxel volume is bound through <see cref="BindFroxelApply"/>,
    /// and the scene depth is sampled (t12) for the depth test.
    /// </summary>
    private void ParticleLayerPass(GpuTextureHandle sceneDepth, int width, int height)
    {
        EnsureParticleLayerTarget(width, height);
        if (!_particleLayerTexture.IsValid)
        {
            _particleLayerThisFrame = false;
            return;
        }

        _gpu.BeginRenderPass(new GpuRenderPassDesc
        {
            Target = _particleLayerTarget,
            ColorActions = new[] { GpuAttachmentAction.Clear(0f, 0f, 0f, 0f) },
            HasDepth = false,
            DebugName = "GPU particle layer",
        });
        _gpu.SetViewport(0, 0, width, height);
        _gpu.SetConstantBuffer(GpuShaderStage.Pixel, ParticleEngineConstantsSlot, _cbEngine);
        BindFroxelApply();
        _gpu.SetTexture(GpuShaderStage.Pixel, ParticleSceneDepthSlot, sceneDepth);
        ExternalParticles(_view, SceneProj, ParticleDrawPhase.Layer, width, height);
        _gpu.ClearTexture(GpuShaderStage.Pixel, ParticleSceneDepthSlot);
        _gpu.ClearTexture(GpuShaderStage.Pixel, 16);
        _gpu.EndRenderPass();
    }

    /// <summary>GpuParticleShaders.Draw declares the EngineConstants prefix here (b1/b2 are its own).</summary>
    internal const int ParticleEngineConstantsSlot = 3;

    /// <summary>GpuParticleShaders.Draw samples the scene depth here for its own depth test.</summary>
    internal const int ParticleSceneDepthSlot = 12;

    private Vector4 ParticleLayerParams() =>
        _particleLayerThisFrame && _particleLayerTexture.IsValid ? new Vector4(1f, 0f, 0f, 0f) : Vector4.Zero;
}
