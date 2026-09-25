using System;
using System.Numerics;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Core;

namespace Genesis.Rendering.Primitives;

internal sealed partial class ForwardRenderer
{
    private GpuRenderTargetHandle _reflectionTarget;
    private GpuTextureHandle _reflectionTexture, _reflectionDepth;
    private int _reflectionWidth, _reflectionHeight;
    private bool _reflectionPassActive, _reflectionReady;
    private float _reflectionPlaneHeight;
    private Matrix4x4 _reflectionViewProjection;

    private void RenderPlanarReflection(GpuTextureHandle whiteTexture, int width, int height)
    {
        _reflectionReady = false;
        if (!EngineRenderingDefaults.WaterReflections || _waterMeshes.Count == 0 || width <= 0 || height <= 0
            || _gpu.BackendName.Contains("Software", StringComparison.OrdinalIgnoreCase)) return;
        float best = float.MinValue, planeHeight = 0;
        foreach (WaterMesh water in _waterMeshes)
        {
            if (!TryGetMesh(water.MeshId, out var mesh)) continue;
            Vector3 up = Vector3.TransformNormal(Vector3.UnitY, water.World);
            if (up.LengthSquared() < 1e-6f || Math.Abs(Vector3.Normalize(up).Y) < .999f) continue;
            Vector3 center = Vector3.Transform(mesh.BoundsCenter, water.World);
            if (_cameraPos.Y <= center.Y + .05f) continue;
            float scale = MathF.Max(new Vector3(water.World.M11, water.World.M12, water.World.M13).Length(),
                new Vector3(water.World.M31, water.World.M32, water.World.M33).Length());
            float radius = mesh.BoundsRadius * scale;
            float score = radius * radius / MathF.Max(1, Vector3.DistanceSquared(center, _cameraPos));
            if (score <= best) continue;
            best = score; planeHeight = center.Y;
        }
        if (best == float.MinValue || !PlanarReflectionMath.TryCreate(_view, _proj, _cameraPos, planeHeight,
            out var view, out var projection, out var camera)) return;
        int w = Math.Clamp(width / 2, 1, 1024), h = Math.Clamp(height / 2, 1, 1024);
        if (!_reflectionTarget.IsValid || w != _reflectionWidth || h != _reflectionHeight)
        {
            if (_reflectionTarget.IsValid) _gpu.ReleaseRenderTarget(_reflectionTarget);
            _reflectionTarget = _gpu.CreateRenderTarget(new GpuRenderTargetDesc
            {
                Width = w, Height = h, ColorFormats = new[] { GpuFormat.R16G16B16A16Float, GpuFormat.R8UNorm },
                DepthFormat = GpuFormat.D32Float, DepthSampleable = true, DebugName = "Water planar reflection",
            });
            _reflectionTexture = _gpu.GetRenderTargetTexture(_reflectionTarget, 0);
            _reflectionDepth = _gpu.GetRenderTargetDepthTexture(_reflectionTarget);
            _reflectionWidth = w; _reflectionHeight = h;
        }
        if (!_reflectionTarget.IsValid || !_reflectionTexture.IsValid || !_reflectionDepth.IsValid) return;
        Matrix4x4 savedView = _view, savedProjection = _proj;
        Vector3 savedCamera = _cameraPos;
        int savedWidth = _rtWidth, savedHeight = _rtHeight;
        bool savedFog = _screenFogActiveThisFrame, savedShadows = _shadowsActiveThisFrame;
        try
        {
            _gpu.ClearTexture(GpuShaderStage.Pixel, 7);
            _view = view; _proj = projection; _cameraPos = camera;
            _rtWidth = w; _rtHeight = h; _reflectionPassActive = true;
            _screenFogActiveThisFrame = false; _shadowsActiveThisFrame = false;
            _reflectionViewProjection = view * projection; _reflectionPlaneHeight = planeHeight;
            // Reuse the complete submitted scene, including GPU-skinned and instanced geometry.
            // Water and view-models are excluded to avoid recursion and reflected first-person hands.
            MainPass(_reflectionTarget, _reflectionDepth, whiteTexture, postProcessTarget: true);
            _reflectionReady = true;
        }
        finally
        {
            _view = savedView; _proj = savedProjection; _cameraPos = savedCamera;
            _rtWidth = savedWidth; _rtHeight = savedHeight;
            _screenFogActiveThisFrame = savedFog; _shadowsActiveThisFrame = savedShadows;
            _reflectionPassActive = false;
        }
    }
}
