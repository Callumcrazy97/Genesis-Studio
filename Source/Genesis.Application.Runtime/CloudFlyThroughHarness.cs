using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Rendering.Core;
using Genesis.Rendering.Meshes;
using Genesis.Rendering.Primitives;
using Genesis.Rendering.Viewport;
using Genesis.Runtime.Climate;
using Genesis.Shared.Interfaces;
using WinFormsApplication = System.Windows.Forms.Application;

namespace Genesis.Application.Runtime;

/// <summary>
/// AF2.7 SkyForge fly-through capture harness. Minimal sky-looking scene with injectable camera
/// and a deterministic dense weather-map upload each frame — does not touch
/// <see cref="RenderParityHarness"/> / golden-dx11.json.
/// </summary>
public sealed class CloudFlyThroughHarness : IDisposable
{
    public const int CaptureWidth = 640;
    public const int CaptureHeight = 360;

    public const int WeatherMapResolution = 64;

    /// <summary>Matches WeatherMapService half-extent from DefaultMapScale (0.5 / 0.0025).</summary>
    public static float WeatherWorldHalfExtent => 0.5f / WeatherMapMath.DefaultMapScale;

    private const int TimeSteps = 30;
    private const float StepSeconds = 1f / 60f;

    private readonly Form _host;
    private readonly D3DViewportControl _viewport;
    private readonly byte[] _denseWeatherRgba;

    private string? _lastRenderError;
    private RenderStats _lastStats;
    private bool _resourcesReady;
    private bool _clockWound;
    private bool _viewportSettled;
    private bool _frozenTimeWarmed;

    private MeshHandle _ground;
    private MeshHandle _cube;

    private Vector3 _cameraPosition = new(0f, 140f, 48f);
    private Vector3 _cameraTarget = new(0f, 218f, -24f);

    public CloudFlyThroughHarness()
        : this(backend: null)
    {
    }

    public CloudFlyThroughHarness(RenderBackendOption? backend)
    {
        _denseWeatherRgba = BuildDenseWeatherRgba8(WeatherMapResolution);

        _host = new Form
        {
            Text = "Genesis Cloud Fly-Through Harness",
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(80, 80),
            ShowInTaskbar = false,
            ClientSize = new Size(CaptureWidth, CaptureHeight),
        };

        _viewport = new D3DViewportControl
        {
            Dock = DockStyle.Fill,
            DriveMode = ViewportDriveMode.External,
            VSync = false,
            BackendOverride = backend,
        };
        _viewport.OnRender += OnRender;
        _host.Controls.Add(_viewport);
    }

    public RenderStats LastStats => _lastStats;

    /// <summary>Active backend display name after the viewport has initialised.</summary>
    public string BackendName
    {
        get
        {
            EnsureHostReady();
            return _viewport.Renderer?.BackendName ?? string.Empty;
        }
    }

    /// <summary>Sets the look-at camera used by the next <see cref="Capture"/>.</summary>
    public void SetCamera(Vector3 position, Vector3 target)
    {
        _cameraPosition = position;
        _cameraTarget = target;
    }

    /// <summary>
    /// Renders with the current camera, uploading dense weather RGBA8 every frame, and writes PNG
    /// to <paramref name="outputFile"/>. Freezes the scene clock like <see cref="RenderParityHarness.Capture"/>.
    /// </summary>
    public ImageMetrics Capture(string outputFile)
    {
        EnsureReady();
        _lastRenderError = null;

        WindSceneClock();
        WarmFrozenTime();

        using Bitmap? bitmap = _viewport.ReadbackFrameToBitmap(settleFrames: 2);
        if (bitmap is null)
        {
            throw new InvalidOperationException(
                "Cloud fly-through harness could not read back a frame — no readback support.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
        bitmap.Save(outputFile, ImageFormat.Png);

        if (!string.IsNullOrEmpty(_lastRenderError))
        {
            throw new InvalidOperationException(
                $"Cloud fly-through scene failed to render:{Environment.NewLine}{_lastRenderError}");
        }

        return ImageMetrics.Measure(bitmap);
    }

    public void Dispose()
    {
        _viewport.OnRender -= OnRender;
        _host.Close();
        _host.Dispose();
        WinFormsApplication.DoEvents();
    }

    private void OnRender(IRenderController renderer)
    {
        try
        {
            EnsureResources(renderer);
            RenderScene(renderer);
            _lastStats = renderer.GetStats();
        }
        catch (Exception ex)
        {
            _lastRenderError = ex.ToString();
        }
    }

    private void RenderScene(IRenderController renderer)
    {
        renderer.Set3DFrameActive(true);
        // Clear toward a sky-ish colour so cloud composite has contrast against empty space.
        renderer.Clear(0.42f, 0.58f, 0.78f, 1f);

        Matrix4x4 view = Matrix4x4.CreateLookAt(_cameraPosition, _cameraTarget, Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3.2f,
            CaptureWidth / (float)CaptureHeight,
            0.5f,
            520f);
        renderer.SetCamera3D(view, projection);

        Mesh3DState state = Mesh3DState.Default;
        state.LightingEnabled = true;
        state.LightingWeight = 1f;
        state.LightDirection = Vector3.Normalize(new Vector3(-0.35f, -1f, -0.25f));
        state.SunColor = new Vector3(1f, 0.96f, 0.88f);
        state.SunIntensity = 1.15f;
        state.AmbientColor = new Vector3(0.32f, 0.38f, 0.48f);
        state.AmbientGroundColor = new Vector3(0.10f, 0.11f, 0.13f);
        state.CullBackFaces = true;
        state.FrustumCullingEnabled = true;
        state.ShadowsEnabled = false;
        state.FogEnabled = true;
        state.FogScreenSpace = true;
        state.FogStart = 40f;
        state.FogEnd = 420f;
        state.ShowFloor = false;
        state.ShowSunVisual = true;
        state.CameraFarPlane = 520f;
        state.CameraForward = Vector3.Normalize(_cameraTarget - _cameraPosition);
        // Cloud slab authoring (AF2.6 defaults) — MeshLightingDefaults OR-applies raymarch prefs.
        state.CloudBaseHeight = RaymarchedCloudsMath.DefaultCloudBase;
        state.CloudThickness = RaymarchedCloudsMath.DefaultThickness;
        state.CloudCoverageScale = 1f;
        state.CloudDensityScale = 1f;
        renderer.SetMesh3DState(state);

        // Dense weather before draw so BeginSubmitFrame→upload→flush sees a valid map this frame.
        renderer.SetWeatherMapRgba8(
            _denseWeatherRgba.AsSpan(),
            WeatherMapResolution,
            WeatherMapResolution,
            WeatherWorldHalfExtent);

        // FogVolumes remain for Software / disabled-raymarch fallback (AF2.3 contract).
        float baseY = RaymarchedCloudsMath.DefaultCloudBase;
        float thick = RaymarchedCloudsMath.DefaultThickness;
        renderer.AddFogVolume(new FogVolume
        {
            Center = new Vector3(0f, baseY + thick * 0.35f, -10f),
            Extents = new Vector3(48f, thick * 0.4f, 48f),
            Color = new Vector3(0.78f, 0.82f, 0.90f),
            Density = 0.35f,
            FalloffCurve = 1.4f,
            Shape = FogVolumeShape.Box,
            Kind = FogVolumeKind.Cloud,
        });
        renderer.AddFogVolume(new FogVolume
        {
            Center = new Vector3(18f, baseY + thick * 0.55f, 8f),
            Extents = new Vector3(22f, thick * 0.35f, 22f),
            Color = new Vector3(0.88f, 0.86f, 0.80f),
            Density = 0.28f,
            FalloffCurve = 1.8f,
            Shape = FogVolumeShape.Sphere,
            Kind = FogVolumeKind.Cloud,
        });

        // Ground + a couple of markers so Software FogVolume-only captures stay non-blank.
        renderer.DrawMesh(new MeshDrawCall
        {
            Mesh = _ground,
            World = Matrix4x4.CreateTranslation(0f, 0f, 0f),
            Tint = new RenderColor(0.28f, 0.36f, 0.22f),
            Alpha = 1f,
            Flags = MeshDrawFlags.IsFloor,
        });
        renderer.DrawMesh(new MeshDrawCall
        {
            Mesh = _cube,
            World = Matrix4x4.CreateScale(6f, 12f, 6f) * Matrix4x4.CreateTranslation(-20f, 6f, -30f),
            Tint = new RenderColor(0.55f, 0.42f, 0.35f),
            Alpha = 1f,
        });
        renderer.DrawMesh(new MeshDrawCall
        {
            Mesh = _cube,
            World = Matrix4x4.CreateScale(4f, 8f, 4f) * Matrix4x4.CreateTranslation(24f, 4f, -18f),
            Tint = new RenderColor(0.40f, 0.48f, 0.55f),
            Alpha = 1f,
        });
    }

    private void EnsureResources(IRenderController renderer)
    {
        if (_resourcesReady)
            return;

        _cube = MeshGeometry.RegisterCube(renderer, RenderColor.White, 1f);
        var (groundVerts, groundIndices) = MeshGeometry.BuildFloor(RenderColor.White, size: 200f, uvTile: 24f);
        _ground = renderer.RegisterMesh(groundVerts, groundIndices);
        _resourcesReady = true;
    }

    private void WindSceneClock()
    {
        if (_clockWound || _viewport.Renderer is null)
            return;

        for (int i = 0; i < TimeSteps; i++)
            _viewport.Renderer.Advance3DTime(StepSeconds);

        _clockWound = true;
    }

    private void WarmFrozenTime()
    {
        if (_frozenTimeWarmed)
            return;

        for (int i = 0; i < 8; i++)
            _viewport.RenderFrame();

        _frozenTimeWarmed = true;
    }

    private void EnsureHostReady()
    {
        if (!_host.Visible)
        {
            _host.Show();
            _host.Activate();
        }

        for (int i = 0; i < 12 && (_viewport.Renderer is null || !_viewport.Renderer.IsInitialized); i++)
        {
            WinFormsApplication.DoEvents();
            Thread.Sleep(16);
        }

        if (_viewport.Renderer is null || !_viewport.Renderer.IsInitialized)
        {
            string detail = _viewport.LastRenderException == null
                ? "no renderer fault recorded"
                : _viewport.LastRenderException.ToString();
            throw new InvalidOperationException(
                "Cloud fly-through viewport failed to initialize. " + detail);
        }
    }

    private void EnsureReady()
    {
        EnsureHostReady();
        if (_viewportSettled)
            return;

        for (int i = 0; i < 12; i++)
        {
            WinFormsApplication.DoEvents();
            Thread.Sleep(16);
            _viewport.RenderFrame();
        }

        _viewportSettled = true;
    }

    /// <summary>
    /// High-coverage RGBA8: R=Coverage≈0.95, G=Type 0.5, B=Erosion 0.1, A=Vertical 0.6.
    /// </summary>
    public static byte[] BuildDenseWeatherRgba8(int resolution)
    {
        int n = Math.Max(8, resolution);
        byte[] rgba = new byte[n * n * 4];
        for (int i = 0; i < n * n; i++)
        {
            int o = i * 4;
            rgba[o] = 242;     // coverage
            rgba[o + 1] = 128; // type
            rgba[o + 2] = 26;  // low erosion
            rgba[o + 3] = 153; // vertical development
        }

        return rgba;
    }
}
