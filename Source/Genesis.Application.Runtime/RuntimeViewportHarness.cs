using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Rendering.Meshes;
using Genesis.Rendering.Viewport;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Scripting;
using Genesis.Shared.Interfaces;
using Genesis.Shared;
using WinFormsApplication = System.Windows.Forms.Application;

namespace Genesis.Application.Runtime;

/// <summary>
/// Off-screen D3D viewport host for deterministic 2D/3D frame capture.
/// Must run on an STA thread with a message pump.
/// </summary>
public sealed partial class RuntimeViewportHarness : IDisposable
{
    private readonly Form _host;
    private readonly D3DViewportControl _viewport;
    private MeshHandle _cube;
    private MeshHandle _floor;
    private MeshHandle _sparkQuad;
    private MeshHandle _reflectionWater;
    private bool _sceneMeshesRegistered;
    private CaptureMode _mode = CaptureMode.TwoD;
    private string? _lastRenderError;
    private RenderStats _lastStats;
    private RuntimeShaderHandle _authoredShader;
    private TextureHandle _authoredSprite;
    private TextureHandle _authoredMask;
    private string? _authoredHlsl;
    private string? _compiledHlsl;
    private string? _captureBadge;
    private string? _captureBadgeDetail;

    private enum CaptureMode
    {
        TwoD,
        TwoDFog,
        ThreeD,
        PgslThreeD,
        AuthoredShader,
        FloorBatchCollapse,
        WaterReflection,
        FaceWinding,
    }

    public RuntimeViewportHarness(int width = 640, int height = 360)
    {
        _host = new Form
        {
            Text = "Genesis Runtime Viewport Harness",
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(40, 40),
            ShowInTaskbar = false,
            ClientSize = new Size(width, height),
        };

        _viewport = new D3DViewportControl
        {
            Dock = DockStyle.Fill,
            DriveMode = ViewportDriveMode.External,
            VSync = false,
        };
        _viewport.OnRender += OnRender;
        _viewport.OnPostFrame += OnPostFrame;
        _host.Controls.Add(_viewport);
    }

    /// <summary>Client size of the hosted viewport control, in pixels.</summary>
    public (int Width, int Height) ViewportClientSize
    {
        get
        {
            EnsureReady();
            return (_viewport.ClientWidth, _viewport.ClientHeight);
        }
    }

    /// <summary>
    /// Swap-chain size the backend is actually rendering at. Should equal
    /// <see cref="ViewportClientSize"/> once a resize has settled — when it does not, the viewport
    /// is being resampled to fit, which is what the fixed-1280x720 policy used to do permanently.
    /// </summary>
    public (int Width, int Height) BackendRenderSize
    {
        get
        {
            EnsureReady();
            return (_viewport.RenderWidth, _viewport.RenderHeight);
        }
    }

    /// <summary>Active render controller after the host has created a swap chain (R7.1 / F6 probes).</summary>
    public IRenderController Renderer
    {
        get
        {
            EnsureReady();
            return _viewport.Renderer
                ?? throw new InvalidOperationException("Viewport has no render controller.");
        }
    }

    /// <summary>Stats from the most recent capture or benchmark frame.</summary>
    public RenderStats LastStats => _lastStats;

    /// <summary>
    /// Resizes the host and pumps frames until the swap chain has followed, or until
    /// <paramref name="maxFrames"/> have been rendered. Returns true if it followed.
    /// </summary>
    public bool ResizeTo(int width, int height, int maxFrames = 12)
    {
        EnsureReady();
        _host.ClientSize = new Size(width, height);

        for (int frame = 0; frame < maxFrames; frame++)
        {
            // Re-sync every iteration: the control's client rect is only correct once WinForms has
            // actually processed the layout, which can take more than one pump.
            WinFormsApplication.DoEvents();
            _viewport.SyncClientSize();
            _viewport.RenderFrame();

            if (_viewport.RenderWidth == _viewport.ClientWidth
                && _viewport.RenderHeight == _viewport.ClientHeight)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Human-readable size state, for assertion messages when <see cref="ResizeTo"/> fails.</summary>
    public string DescribeSizes() =>
        $"host client {_host.ClientSize.Width}x{_host.ClientSize.Height}, "
        + $"viewport client {_viewport.ClientWidth}x{_viewport.ClientHeight}, "
        + $"backend {_viewport.RenderWidth}x{_viewport.RenderHeight}";

    /// <summary>
    /// Creates and settles the hidden viewport used by performance sampling. This is harness startup,
    /// not scene load, so Phase 0 measures it outside the scene load timer.
    /// </summary>
    public void InitializeBenchmarkHost() => EnsureReady();

    /// <summary>Applies the 2D benchmark scene state. It has no authored asset load today.</summary>
    public void PrepareBenchmark2D()
    {
        EnsureReady();
        _mode = CaptureMode.TwoD;
    }

    /// <summary>Registers the current 3D benchmark geometry as the scene's measured load step.</summary>
    public void PrepareBenchmark3D()
    {
        EnsureReady();
        EnsureCube();
        _mode = CaptureMode.ThreeD;
    }

    /// <summary>
    /// Renders one presented 2D benchmark frame without performing a readback. The caller owns
    /// timing so screenshot/readback instrumentation never contaminates frame-time statistics.
    /// </summary>
    public RenderStats RenderBenchmarkFrame2D()
    {
        EnsureReady();
        _mode = CaptureMode.TwoD;
        return RenderBenchmarkFrame();
    }

    /// <summary>
    /// Renders one presented 3D benchmark frame without performing a readback. Geometry is created
    /// before the measured frame so asset registration can be reported separately as load cost.
    /// </summary>
    public RenderStats RenderBenchmarkFrame3D()
    {
        EnsureReady();
        EnsureCube();
        _mode = CaptureMode.ThreeD;
        return RenderBenchmarkFrame();
    }

    private RenderStats RenderBenchmarkFrame()
    {
        _lastRenderError = null;
        _viewport.RenderFrame();

        if (!string.IsNullOrEmpty(_lastRenderError))
        {
            throw new InvalidOperationException(
                $"Benchmark render callback failed:{Environment.NewLine}{_lastRenderError}");
        }

        _lastStats = _viewport.Renderer?.GetStats() ?? default;
        return _lastStats;
    }

    public ImageMetrics Capture2D(string outputFile)
    {
        EnsureReady();
        _mode = CaptureMode.TwoD;
        ImageMetrics metrics = Capture(outputFile);
        // This capture is sprite-only: a rising draw count is not proof when clip-space errors can
        // discard every pixel after submission.
        if (_lastStats.InstancesDrawn < 3)
        {
            throw new InvalidOperationException(
                $"2D sprite submit path failed (instances={_lastStats.InstancesDrawn}).");
        }

        return metrics;
    }

    /// <summary>DX11 sprite-fog visual proof: a white world sprite at far 2D depth must become red, not black.</summary>
    public ImageMetrics Capture2DFog(string outputFile)
    {
        EnsureReady();
        _mode = CaptureMode.TwoDFog;
        return Capture(outputFile);
    }

    public ImageMetrics Capture3D(string outputFile)
    {
        EnsureReady();
        EnsureCube();
        _mode = CaptureMode.ThreeD;
        return Capture(outputFile);
    }

    /// <summary>Exercise persistent mesh updates, including frames after the upload completes.</summary>
    public ImageMetrics CaptureUpdatedMesh(string outputFile)
    {
        EnsureReady();
        EnsureCube();
        (MeshVertex[] vertices, _) = MeshGeometry.BuildCube(new RenderColor(1f, 0.02f, 0.02f), 1f);
        Renderer.UpdateMesh(_cube, vertices);
        _mode = CaptureMode.ThreeD;
        return Capture(outputFile);
    }

    public ImageMetrics CaptureWaterReflection(string outputFile)
    {
        EnsureReady(); EnsureCube();
        if (!_reflectionWater.IsValid)
        {
            var (vertices, indices) = MeshGeometry.BuildCheckerFloor(RenderColor.White, RenderColor.White, tiles: 2, tileSize: 10);
            _reflectionWater = Renderer.RegisterMesh(vertices, indices);
        }
        _mode = CaptureMode.WaterReflection;
        return Capture(outputFile);
    }

    /// <summary>
    /// R7.7 probe: many opaque <see cref="MeshDrawFlags.IsFloor"/> submits with one mesh must
    /// collapse into instanced batches instead of parking one WorldMesh draw each.
    /// </summary>
    public (ImageMetrics Metrics, RenderStats Stats) CaptureFloorBatchCollapse(string outputFile)
    {
        EnsureReady();
        EnsureCube();
        _mode = CaptureMode.FloorBatchCollapse;
        ImageMetrics metrics = Capture(outputFile, minimumUniqueColors: 2);
        return (metrics, _lastStats);
    }

    public ImageMetrics CapturePgsl3D(string outputFile)
    {
        EnsureReady();
        EnsureCube();
        _mode = CaptureMode.PgslThreeD;
        return Capture(outputFile);
    }

    /// <summary>
    /// Draws a deterministic authored mesh shader inside the same small 3D world used by the engine
    /// capture. Engine tests prove renderer-owned scene setup; authored/PGSL-adjacent tests prove
    /// code-selected draw state and shader logic on top of that scene.
    /// </summary>
    public ImageMetrics CaptureAuthoredShader(string outputFile, bool swapVariant)
    {
        EnsureReady();
        _authoredHlsl = AuthoredParityHlsl(swapVariant);
        _mode = CaptureMode.AuthoredShader;
        try
        {
            ImageMetrics metrics = Capture(outputFile, minimumUniqueColors: 2);
            if (_lastStats.InstancesDrawn < 1)
            {
                throw new InvalidOperationException(
                    $"Authored shader submit path failed (instances={_lastStats.InstancesDrawn}).");
            }

            return metrics;
        }
        finally
        {
            ReleaseAuthoredShader();
        }
    }

    private static string AuthoredParityHlsl(bool swap) => swap
        ? """
            cbuffer GenesisParameters : register(b5) { float4 Row0; float4 Row1; float4 Row2; float4 Row3; };
            struct VSOut { float4 SvPos : SV_Position; float3 WorldPos : TEXCOORD1; float3 Normal : TEXCOORD2; float4 Color : TEXCOORD3; float2 UV : TEXCOORD4; float4 ShadowPos : TEXCOORD5; float4 ShadowPosNr : TEXCOORD6; float AtlasLayer : TEXCOORD7; float4 ShadowPosMd : TEXCOORD8; };
            struct PSOut { float4 Color : SV_Target0; float SkipPostFog : SV_Target1; };
            PSOut MainPS(VSOut IN, bool isFront : SV_IsFrontFace)
            {
                float3 n = normalize(IN.Normal);
                if (!isFront) n = -n;
                float rim = pow(1.0 - saturate(abs(n.z)), 2.0);
                float stripes = step(0.52, frac((IN.WorldPos.x + IN.WorldPos.z) * 2.7));
                float checker = step(0.5, frac(IN.UV.x * 4.0)) * step(0.5, frac(IN.UV.y * 4.0));
                float3 baseColor = lerp(float3(0.08, 0.22, 0.95), float3(0.95, 0.72, 0.18), stripes);
                baseColor = lerp(baseColor, IN.Color.rgb, checker * 0.2);
                float keepMeshContract = (dot(IN.ShadowPos, float4(0.01, 0.02, 0.03, 0.04))
                    + dot(IN.ShadowPosNr, float4(0.04, 0.03, 0.02, 0.01))
                    + dot(IN.ShadowPosMd, float4(0.03, 0.01, 0.04, 0.02))
                    + IN.AtlasLayer) * 0.000001;
                PSOut OUT;
                OUT.Color = float4(baseColor + rim * Row0.xyz * 0.35 + keepMeshContract.xxx, 1.0);
                OUT.SkipPostFog = 0.0;
                return OUT;
            }
            """
        : """
            cbuffer GenesisParameters : register(b5) { float4 Row0; float4 Row1; float4 Row2; float4 Row3; };
            struct VSOut { float4 SvPos : SV_Position; float3 WorldPos : TEXCOORD1; float3 Normal : TEXCOORD2; float4 Color : TEXCOORD3; float2 UV : TEXCOORD4; float4 ShadowPos : TEXCOORD5; float4 ShadowPosNr : TEXCOORD6; float AtlasLayer : TEXCOORD7; float4 ShadowPosMd : TEXCOORD8; };
            struct PSOut { float4 Color : SV_Target0; float SkipPostFog : SV_Target1; };
            PSOut MainPS(VSOut IN, bool isFront : SV_IsFrontFace)
            {
                float3 n = normalize(IN.Normal);
                if (!isFront) n = -n;
                float vertical = saturate(n.y * 0.5 + 0.5);
                float pulse = 0.5 + 0.5 * sin((IN.WorldPos.x - IN.WorldPos.z) * 3.1);
                float checker = step(0.5, frac(IN.UV.x * 4.0)) * step(0.5, frac(IN.UV.y * 4.0));
                float3 baseColor = lerp(float3(0.96, 0.28, 0.12), float3(0.14, 0.78, 0.95), pulse);
                baseColor = lerp(baseColor, IN.Color.rgb, checker * 0.2);
                float keepMeshContract = (dot(IN.ShadowPos, float4(0.01, 0.02, 0.03, 0.04))
                    + dot(IN.ShadowPosNr, float4(0.04, 0.03, 0.02, 0.01))
                    + dot(IN.ShadowPosMd, float4(0.03, 0.01, 0.04, 0.02))
                    + IN.AtlasLayer) * 0.000001;
                PSOut OUT;
                OUT.Color = float4(baseColor * (0.55 + vertical * 0.45) + Row0.xyz * 0.12 + keepMeshContract.xxx, 1.0);
                OUT.SkipPostFog = 0.0;
                return OUT;
            }
            """;

    private const string PgslVisual3D = """
        DrawSetColorRgb(150, 155, 165);
        DrawFloor3D(0, 0, 0, 18, 18);

        DrawSetColorRgb(80, 190, 255);
        DrawCube3D(-1.6, 0.55, 0.0, 1.1);

        DrawSetColorRgb(255, 115, 55);
        DrawCube3D(0.2, 0.8, -1.1, 0.8);

        DrawSetColorRgb(110, 245, 130);
        DrawBox3D(1.5, 0.45, 0.7, 0.7, 0.9, 0.7);

        DrawSetColorRgb(240, 205, 70);
        DrawSphere3D(-0.4, 1.5, 0.9, 0.22);
        DrawSphere3D(0.1, 1.8, 1.2, 0.16);
        DrawSphere3D(0.6, 1.45, 0.8, 0.14);

        DrawSetColorRgb(120, 170, 255);
        DrawWall3D(-2.8, 0, 2.2, 2.8, 0, 2.2, 1.0, 0.12);

        DrawSetColorRgb(220, 225, 235);
        DrawGrid3D(-3.0, 0.04, -3.0, 1.0, 6, 6);
        """;

    private void EnsureReady()
    {
        if (!_host.IsHandleCreated)
        {
            _host.Show();
            Pump(8, 25);
        }

        if (_viewport.Renderer is null || !_viewport.Renderer.IsInitialized)
        {
            throw new InvalidOperationException("D3D viewport renderer failed to initialize.");
        }
    }

    private void EnsureCube()
    {
        if (_sceneMeshesRegistered)
        {
            return;
        }

        IRenderController renderer = _viewport.Renderer
            ?? throw new InvalidOperationException("Renderer is unavailable.");
        _cube = MeshGeometry.RegisterCube(renderer, RenderColor.White, size: 1f);
        (MeshVertex[] floorVertices, ushort[] floorIndices) = MeshGeometry.BuildCheckerFloor(
            new RenderColor(0.006f, 0.007f, 0.009f),
            new RenderColor(0.82f, 0.84f, 0.88f));
        _floor = renderer.RegisterMesh(floorVertices, floorIndices);
        _sparkQuad = MeshGeometry.RegisterQuad(renderer, RenderColor.White);
        _sceneMeshesRegistered = _cube.IsValid && _floor.IsValid && _sparkQuad.IsValid;
    }

    private ImageMetrics Capture(string outputFile, int minimumUniqueColors = 4)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);
        string? directory = Path.GetDirectoryName(outputFile);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _lastRenderError = null;
        using Bitmap? bitmap = ReadbackWithOneRetry();
        if (bitmap is null)
        {
            throw new InvalidOperationException(
                $"D3D readback failed for '{Path.GetFileName(outputFile)}'."
                + (_viewport.LastRenderException is { } fault ? " Render fault: " + fault : string.Empty));
        }

        if (_viewport.Renderer is not null)
        {
            _lastStats = _viewport.Renderer.GetStats();
        }

        bitmap.Save(outputFile, ImageFormat.Png);
        ImageMetrics metrics = ImageMetrics.Measure(bitmap);
        if (!string.IsNullOrEmpty(_lastRenderError))
        {
            throw new InvalidOperationException(
                $"Render callback failed during '{Path.GetFileName(outputFile)}':{_lastRenderError}");
        }

        if (metrics.UniqueSampledColors < minimumUniqueColors)
        {
            throw new InvalidOperationException(
                $"Capture '{Path.GetFileName(outputFile)}' appears visually blank " +
                $"(colors={metrics.UniqueSampledColors}, lum={metrics.AverageLuminance:F1}, " +
                $"draws={_lastStats.DrawCalls}, instances={_lastStats.InstancesDrawn}).");
        }

        return metrics;
    }

    private Bitmap? ReadbackWithOneRetry()
    {
        Bitmap? bitmap = _viewport.ReadbackFrameToBitmap(settleFrames: 3);
        if (bitmap is not null) return bitmap;
        // DX12 can report its staging fence one frame late when several backend fixtures are
        // created back-to-back. A bounded retry keeps the test strict while allowing that fence
        // to complete; a second null is still surfaced as a real readback failure.
        System.Windows.Forms.Application.DoEvents();
        return _viewport.ReadbackFrameToBitmap(settleFrames: 6);
    }

    private void OnRender(IRenderController renderer)
    {
        try
        {
            _captureBadge = null;
            _captureBadgeDetail = null;
            RenderScene(renderer);
        }
        catch (Exception ex)
        {
            _lastRenderError = ex.ToString();
        }
    }

    private void OnPostFrame(IRenderController renderer)
    {
        if (string.IsNullOrWhiteSpace(_captureBadge)) return;

        string badge = _captureBadge;
        string detail = _captureBadgeDetail ?? string.Empty;
        bool composed = renderer.ComposeOverlay(canvas =>
        {
            canvas.DrawRect(12f, 12f, 520f, 72f, new Vector4(0.04f, 0.05f, 0.08f, 0.84f), filled: true);
            canvas.DrawRect(20f, 20f, 190f, 22f, new Vector4(0.28f, 0.86f, 1.0f, 1f), filled: true);
            canvas.DrawText(badge, new Vector2(28f, 24f), 11f, new Vector4(0.02f, 0.03f, 0.05f, 1f), bold: true);
            canvas.DrawText(detail, new Vector2(20f, 50f), 11f, new Vector4(0.88f, 0.92f, 0.98f, 0.98f));
            canvas.DrawText(
                "Screenshots are labelled so Engine and PGSL visual tests cannot be confused.",
                new Vector2(20f, 66f),
                9f,
                new Vector4(0.72f, 0.78f, 0.86f, 1f));
        });
        if (!composed)
        {
            _lastRenderError = "The backend rejected the capture identification overlay.";
        }
    }

    private void RenderScene(IRenderController renderer)
    {
        if (_mode == CaptureMode.FaceWinding) { RenderFaceWinding(renderer); return; }
        if (_mode == CaptureMode.WaterReflection) { RenderWaterReflection(renderer); return; }
        if (_mode == CaptureMode.TwoD)
        {
            RenderTwoD(renderer);
            return;
        }
        if (_mode == CaptureMode.TwoDFog)
        {
            RenderTwoDFog(renderer);
            return;
        }
        if (_mode == CaptureMode.AuthoredShader)
        {
            RenderAuthoredShader(renderer);
            return;
        }
        if (_mode == CaptureMode.PgslThreeD)
        {
            RenderPgslThreeD(renderer);
            return;
        }
        if (_mode == CaptureMode.FloorBatchCollapse)
        {
            RenderFloorBatchCollapse(renderer);
            return;
        }

        RenderThreeD(renderer);
    }

    private void RenderWaterReflection(IRenderController renderer)
    {
        BeginThreeDScene(renderer);
        renderer.SetCamera3D(Matrix4x4.CreateLookAt(new Vector3(0, 1, 9), new Vector3(0, .3f, 0), Vector3.UnitY),
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, _viewport.ClientWidth / (float)_viewport.ClientHeight, .1f, 100));
        var state = Mesh3DState.Default;
        state.LightingEnabled = true; state.LightingWeight = 1; state.SunIntensity = 1;
        state.LightDirection = Vector3.Normalize(new Vector3(-1, -1, -1));
        state.AmbientColor = new Vector3(.7f); state.SunColor = Vector3.One;
        state.BackgroundColor = new Vector3(.18f, .25f, .4f); state.FrustumCullingEnabled = false;
        state.ShadowsEnabled = false; state.ShowFloor = false; state.ShowSunVisual = false;
        renderer.SetMesh3DState(state); renderer.ClearPointLights();
        renderer.DrawMesh(new MeshDrawCall { Mesh = _cube, World = Matrix4x4.CreateScale(1.5f, 3, 1.5f) * Matrix4x4.CreateTranslation(0, 1.5f, 0),
            Tint = new RenderColor(1, .18f, .03f), Alpha = 1, Flags = MeshDrawFlags.NoCull | MeshDrawFlags.NoShadow });
        renderer.DrawMesh(new MeshDrawCall { Mesh = _cube, World = Matrix4x4.CreateScale(3) * Matrix4x4.CreateTranslation(-3, -3, 0),
            Tint = new RenderColor(.02f, 1, .03f), Alpha = 1, Flags = MeshDrawFlags.NoCull | MeshDrawFlags.NoShadow });
        renderer.DrawMesh(new MeshDrawCall { Mesh = _reflectionWater, World = Matrix4x4.Identity,
            Tint = new RenderColor(.15f, .35f, .4f), DeepTint = new RenderColor(.01f, .06f, .1f, 5), Alpha = 1,
            WaterParams = new Vector4(0, 5, .1f, 0), SkyHorizon = new Vector4(.4f, .5f, 0, 1), SkyZenith = new Vector4(.15f, .3f, .5f, 0),
            Flags = MeshDrawFlags.Water | MeshDrawFlags.NoShadow | MeshDrawFlags.NoCull });
    }

    private void RenderFloorBatchCollapse(IRenderController renderer)
    {
        BeginThreeDScene(renderer);
        // Rebuild state from Default with the same lighting as BeginThreeDScene, but without sun
        // so draw-call noise is only the floor batch under test. IRenderController has no getter.
        Mesh3DState state = Mesh3DState.Default;
        state.LightingEnabled = true;
        state.LightingWeight = 1f;
        state.LightDirection = Vector3.Normalize(new Vector3(-0.35f, -1f, -0.45f));
        state.SunColor = new Vector3(1f, 0.95f, 0.9f);
        state.SunIntensity = 1.35f;
        state.AmbientColor = new Vector3(0.36f, 0.42f, 0.54f);
        state.AmbientGroundColor = new Vector3(0.12f, 0.13f, 0.16f);
        state.FrustumCullingEnabled = false;
        state.ShadowsEnabled = false;
        state.ShowSunVisual = false;
        state.BackgroundColor = new Vector3(0.50f, 0.62f, 0.78f);
        renderer.SetMesh3DState(state);

        const int count = 24;
        for (int i = 0; i < count; i++)
        {
            float x = (i % 6) * 1.1f - 2.75f;
            float z = (i / 6) * 1.1f - 1.65f;
            renderer.DrawMesh(new MeshDrawCall
            {
                Mesh = _cube,
                World = Matrix4x4.CreateScale(0.9f) * Matrix4x4.CreateTranslation(x, 0.45f, z),
                Tint = new RenderColor(0.56f, 0.58f, 0.62f),
                Alpha = 1f,
                Flags = MeshDrawFlags.IsFloor | MeshDrawFlags.NoShadow | MeshDrawFlags.NoCull,
            });
        }

        SetCaptureBadge(
            "FLOOR BATCH COLLAPSE",
            "24 IsFloor cubes must instance, not park on WorldMeshes.");
    }

    private void RenderAuthoredShader(IRenderController renderer)
    {
        EnsureAuthoredResources(renderer);
        RenderThreeD(
            renderer,
            _authoredShader,
            "ENGINE AUTHORED SHADER TEST",
            "Engine mesh scene with authored shader override.");
    }

    private void EnsureAuthoredResources(IRenderController renderer)
    {
        if (!_authoredSprite.IsValid)
        {
            _authoredSprite = renderer.CreateTexture(8, 8, SolidRgba(220, 40, 40));
        }

        if (!_authoredMask.IsValid)
        {
            _authoredMask = renderer.CreateTexture(8, 8, SolidRgba(40, 80, 220));
        }

        if (_authoredShader.IsValid && string.Equals(_compiledHlsl, _authoredHlsl, StringComparison.Ordinal))
            return;
        ReleaseAuthoredShader();

        if (string.IsNullOrWhiteSpace(_authoredHlsl)) return;
        _authoredShader = renderer.RegisterRuntimeShader(
            _authoredHlsl,
            "MainPS",
            ShaderPreviewProfile.MeshPipeline);
        _compiledHlsl = _authoredHlsl;
        if (!_authoredShader.IsValid)
        {
            throw new InvalidOperationException("The authored shader parity scene failed to compile.");
        }
    }

    private void ReleaseAuthoredShader()
    {
        if (!_authoredShader.IsValid || _viewport.Renderer is not IRenderController renderer)
            return;
        renderer.ReleaseRuntimeShader(_authoredShader, ShaderPreviewProfile.MeshPipeline);
        _authoredShader = RuntimeShaderHandle.Invalid;
        _compiledHlsl = null;
    }

    private static byte[] SolidRgba(byte r, byte g, byte b)
    {
        byte[] pixels = new byte[8 * 8 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    private void RenderTwoD(IRenderController renderer)
    {
        int rw = Math.Max(1, renderer.PixelWidth);
        int rh = Math.Max(1, renderer.PixelHeight);

        // Sprite-only visual proof (SetCamera2D + DrawRect/DrawLine). Keeping the 3D pass disabled
        // means the image cannot accidentally pass because an orthographic mesh drew over it.
        renderer.Set3DFrameActive(false);
        renderer.SetRoomFog(RoomFogState.Disabled);
        renderer.SetViewport(0, 0, rw, rh);
        renderer.Clear(0.08f, 0.10f, 0.16f, 1f);
        renderer.SetCamera2D(rw * 0.5f, rh * 0.5f, 1f, 0f);
        renderer.DrawRect(80f, 70f, 220f, 140f, new RenderColor(0.95f, 0.35f, 0.25f), filled: true);
        renderer.DrawRect(360f, 120f, 280f, 180f, new RenderColor(0.25f, 0.85f, 0.45f), filled: true);
        renderer.DrawLine(60f, 320f, 580f, 320f, new RenderColor(1f, 1f, 1f), thickness: 4f);
    }

    private void RenderTwoDFog(IRenderController renderer)
    {
        int rw = Math.Max(1, renderer.PixelWidth);
        int rh = Math.Max(1, renderer.PixelHeight);
        renderer.Set3DFrameActive(false);
        renderer.SetViewport(0, 0, rw, rh);
        renderer.Clear(0.03f, 0.04f, 0.06f, 1f);
        renderer.SetCamera2D(rw * 0.5f, rh * 0.5f, 1f, 0f);

        // Far world sprite: full opaque red fog must replace WHITE RGB with RED RGB. The regression
        // that prompted this test multiplied the sprite toward black instead.
        renderer.SetRoomFog(RoomFogState.CreateDepthThickness(
            true, new Vector4(1f, 0f, 0f, 1f), depth: 10f, thickness: 10f, alpha: 1f));
        renderer.DrawRect(80f, 70f, 220f, 140f, RenderColor.White, filled: true, depth: 100);

        // Near world sprite remains green, proving the depth transition itself still works.
        renderer.DrawRect(360f, 120f, 220f, 140f, new RenderColor(0f, 1f, 0f), filled: true, depth: 0);

        // A second near colour keeps the generic capture sanity check meaningful (background +
        // red-fogged far sprite + green + blue = at least four sampled colours).
        renderer.DrawRect(500f, 285f, 80f, 35f, new RenderColor(0.15f, 0.35f, 1f), filled: true, depth: 0);
    }

    private void RenderThreeD(IRenderController renderer) =>
        RenderThreeD(
            renderer,
            RuntimeShaderHandle.Invalid,
            "ENGINE 3D TEST",
            "Engine submits temporary floor, cubes, lights, sky, and particles.");

    private void RenderPgslThreeD(IRenderController renderer)
    {
        BeginThreeDScene(renderer);

        PgslRenderDrawSurface surface = new(
            renderer,
            hud: null,
            Math.Max(1, renderer.PixelWidth),
            Math.Max(1, renderer.PixelHeight),
            is3DActive: true);
        PgslContext ctx = new()
        {
            RoomWidth = Math.Max(1, renderer.PixelWidth),
            RoomHeight = Math.Max(1, renderer.PixelHeight),
            DrawSurface = surface,
            DrawColor = Color.White,
            DrawAlpha = 1.0,
        };

        PgslContext previous = PgslCommands.BindContext(ctx);
        try
        {
            VMEngine.Initialize();
            CompileResult compiled = VMEngine.Compile(PgslVisual3D)
                ?? throw new InvalidOperationException("PGSL visual 3D script failed to compile.");
            PgslVm vm = VMEngine.CreateVm(debug: false);
            vm.Execute(compiled.Instructions, compiled.Constants, clearVariables: true);
            SetCaptureBadge(
                "PGSL GAME CODE 3D TEST",
                "PGSL VM executes DrawFloor3D / DrawCube3D / DrawSphere3D commands.");
        }
        finally
        {
            PgslCommands.BindContext(previous);
        }
    }

    private void RenderThreeD(
        IRenderController renderer,
        RuntimeShaderHandle authoredShader,
        string badge,
        string detail)
    {
        BeginThreeDScene(renderer);

        DrawFloor(renderer);
        DrawTintedCube(renderer, new Vector3(-1.4f, 0.55f, 0.0f), new RenderColor(0.24f, 0.74f, 1f), 1.1f, rotate: true, authoredShader);
        DrawTintedCube(renderer, new Vector3(0.25f, 0.85f, -0.9f), new RenderColor(1f, 0.42f, 0.22f), 0.85f, rotate: true);
        DrawTintedCube(renderer, new Vector3(1.45f, 0.45f, 0.65f), new RenderColor(0.45f, 0.96f, 0.45f), 0.65f, rotate: false);
        DrawSparkParticles(renderer);
        SetCaptureBadge(badge, detail);
    }

    private void SetCaptureBadge(string badge, string detail)
    {
        _captureBadge = badge;
        _captureBadgeDetail = detail;
    }

    private void BeginThreeDScene(IRenderController renderer)
    {
        EnsureCube();
        renderer.SetRoomFog(RoomFogState.Disabled);
        renderer.Set3DFrameActive(true);
        renderer.Clear(0.50f, 0.62f, 0.78f, 1f);
        Matrix4x4 view = Matrix4x4.CreateLookAt(
            new Vector3(4.8f, 3.0f, 5.2f),
            new Vector3(0.1f, 0.45f, 0.0f),
            Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 4f,
            _viewport.ClientWidth / (float)Math.Max(1, _viewport.ClientHeight),
            0.1f,
            100f);
        renderer.SetCamera3D(view, projection);
        Mesh3DState state = Mesh3DState.Default;
        state.LightingEnabled = true;
        state.LightingWeight = 1f;
        state.LightDirection = Vector3.Normalize(new Vector3(-0.35f, -1f, -0.45f));
        state.SunColor = new Vector3(1f, 0.95f, 0.9f);
        state.SunIntensity = 1.35f;
        state.AmbientColor = new Vector3(0.36f, 0.42f, 0.54f);
        state.AmbientGroundColor = new Vector3(0.12f, 0.13f, 0.16f);
        state.FrustumCullingEnabled = false;
        state.ShadowsEnabled = true;
        state.ShadowOrthoSize = 16f;
        state.ShowSunVisual = true;
        state.BackgroundColor = new Vector3(0.50f, 0.62f, 0.78f);
        renderer.SetMesh3DState(state);
        renderer.ClearPointLights();
        renderer.AddPointLight(new Vector3(-1.8f, 1.7f, 1.2f), new Vector3(0.35f, 0.65f, 1f), 5.0f, 1.3f);
        renderer.AddPointLight(new Vector3(2.0f, 1.2f, -1.4f), new Vector3(1f, 0.55f, 0.25f), 4.2f, 0.9f);
    }

    private void DrawFloor(IRenderController renderer)
    {
        renderer.DrawMesh(new MeshDrawCall
        {
            Mesh = _floor,
            World = Matrix4x4.Identity,
            Tint = new RenderColor(0.56f, 0.58f, 0.62f),
            Alpha = 1f,
            // A floor is intentionally double-sided: editor/runtime cameras can orbit below the
            // authored plane, and the renderer's own environment floor follows the same policy.
            // Keeping this explicit also prevents a backend-specific front-face convention from
            // making the shared visual-test floor disappear while the cubes remain visible.
            Flags = MeshDrawFlags.IsFloor | MeshDrawFlags.NoShadow | MeshDrawFlags.NoCull,
        });
    }

    private void DrawTintedCube(
        IRenderController renderer,
        Vector3 position,
        RenderColor tint,
        float scale,
        bool rotate = false,
        RuntimeShaderHandle shader = default)
    {
        Matrix4x4 world = Matrix4x4.CreateScale(scale);
        if (rotate)
        {
            world *= Matrix4x4.CreateRotationY(0.55f) * Matrix4x4.CreateRotationX(0.25f);
        }

        world *= Matrix4x4.CreateTranslation(position);
        renderer.DrawMesh(new MeshDrawCall
        {
            Mesh = _cube,
            World = world,
            Tint = tint,
            Alpha = 1f,
            Shader = shader,
            ShaderParams0 = new Vector4(0.8f, 0.95f, 1f, 1f),
        });
    }

    private void DrawSparkParticles(IRenderController renderer)
    {
        for (int i = 0; i < 10; i++)
        {
            float t = i / 9f;
            float angle = t * MathF.Tau;
            Vector3 pos = new(MathF.Cos(angle) * 1.55f, 0.7f + MathF.Sin(t * MathF.PI) * 0.75f, MathF.Sin(angle) * 1.25f);
            float scale = 0.10f + (i % 3) * 0.025f;
            Vector3 forward = Vector3.Normalize(new Vector3(4.8f, 3.0f, 5.2f) - pos);
            Matrix4x4 world =
                Matrix4x4.CreateScale(scale, scale, 1f)
                * Matrix4x4.CreateBillboard(pos, new Vector3(4.8f, 3.0f, 5.2f), Vector3.UnitY, forward);
            renderer.DrawMesh(new MeshDrawCall
            {
                Mesh = _sparkQuad,
                World = world,
                Tint = new RenderColor(1f, 0.72f + t * 0.2f, 0.18f, 0.78f),
                Alpha = 0.78f,
                Flags = MeshDrawFlags.Additive | MeshDrawFlags.NoDepthWrite | MeshDrawFlags.NoShadow | MeshDrawFlags.NoFog | MeshDrawFlags.NoCull,
                Emissive = 1.5f,
            });
        }
    }

    private static void Pump(int iterations, int delayMilliseconds)
    {
        for (int i = 0; i < iterations; i++)
        {
            WinFormsApplication.DoEvents();
            Thread.Sleep(delayMilliseconds);
        }
    }

    public void Dispose()
    {
        _viewport.OnRender -= OnRender;
        _viewport.OnPostFrame -= OnPostFrame;
        if (_viewport.Renderer is IRenderController renderer)
        {
            if (_reflectionWater.IsValid) renderer.ReleaseMesh(_reflectionWater);
            if (_faceProbe.IsValid) renderer.ReleaseMesh(_faceProbe);
            if (_authoredShader.IsValid)
                renderer.ReleaseRuntimeShader(_authoredShader, ShaderPreviewProfile.MeshPipeline);
            if (_authoredSprite.IsValid) renderer.ReleaseTexture(_authoredSprite);
            if (_authoredMask.IsValid) renderer.ReleaseTexture(_authoredMask);
        }

        _viewport.Dispose();
        _host.Close();
        _host.Dispose();

        // Drain the close/destroy messages so the window is really gone before the next test
        // activates one of its own — a half-torn-down form is what a capture sees as "too small".
        WinFormsApplication.DoEvents();
    }
}

/// <summary>Mean colour of one cell of the <see cref="ImageMetrics.TileColumns"/>×<see cref="ImageMetrics.TileRows"/> digest grid.</summary>
public readonly record struct TileColor(double R, double G, double B);

public sealed record ImageMetrics(
    int Width,
    int Height,
    int UniqueSampledColors,
    double AverageLuminance)
{
    /// <summary>Digest grid shape. 16×9 keeps a 16:9 capture's cells roughly square.</summary>
    public const int TileColumns = 16;

    public const int TileRows = 9;

    public const int HistogramBuckets = 32;

    /// <summary>
    /// Mean RGB per cell, row-major, <see cref="TileColumns"/>×<see cref="TileRows"/> entries.
    /// This is the render-parity gate. <see cref="UniqueSampledColors"/> and
    /// <see cref="AverageLuminance"/> only prove a capture is not blank — they cannot tell that
    /// shadows vanished, that fog was applied twice or that water turned flat grey, because all
    /// three keep the colour count high and can leave mean luminance almost unchanged. Comparing
    /// per-region means against a recorded baseline does detect exactly those.
    /// </summary>
    public IReadOnlyList<TileColor> Tiles { get; init; } = [];

    /// <summary>Luminance distribution in <see cref="HistogramBuckets"/> buckets over the same samples.</summary>
    public IReadOnlyList<int> Histogram { get; init; } = [];

    public static ImageMetrics Measure(Bitmap bitmap)
    {
        HashSet<int> colors = [];
        int samples = 0;
        long luminance = 0;
        int stepX = Math.Max(1, bitmap.Width / 120);
        int stepY = Math.Max(1, bitmap.Height / 80);

        // Tile sums and the histogram accumulate in the same sampling pass — GetPixel is slow
        // enough that a second traversal would be the dominant cost of a capture.
        int tileCount = TileColumns * TileRows;
        double[] sumR = new double[tileCount];
        double[] sumG = new double[tileCount];
        double[] sumB = new double[tileCount];
        int[] tileSamples = new int[tileCount];
        int[] histogram = new int[HistogramBuckets];

        for (int y = 0; y < bitmap.Height; y += stepY)
        {
            int tileY = Math.Clamp(y * TileRows / Math.Max(1, bitmap.Height), 0, TileRows - 1);

            for (int x = 0; x < bitmap.Width; x += stepX)
            {
                Color color = bitmap.GetPixel(x, y);
                colors.Add(color.ToArgb());
                samples++;

                long pixelLuminance = (color.R * 299L + color.G * 587L + color.B * 114L) / 1000L;
                luminance += pixelLuminance;

                int tileX = Math.Clamp(x * TileColumns / Math.Max(1, bitmap.Width), 0, TileColumns - 1);
                int tile = (tileY * TileColumns) + tileX;
                sumR[tile] += color.R;
                sumG[tile] += color.G;
                sumB[tile] += color.B;
                tileSamples[tile]++;

                histogram[Math.Clamp((int)(pixelLuminance * HistogramBuckets / 256), 0, HistogramBuckets - 1)]++;
            }
        }

        TileColor[] tiles = new TileColor[tileCount];
        for (int i = 0; i < tileCount; i++)
        {
            // A cell the sampling stride never landed in stays at zero rather than dividing by it;
            // that is stable across runs, so it compares cleanly against a baseline.
            int n = tileSamples[i];
            tiles[i] = n == 0
                ? new TileColor(0, 0, 0)
                : new TileColor(sumR[i] / n, sumG[i] / n, sumB[i] / n);
        }

        return new ImageMetrics(
            bitmap.Width,
            bitmap.Height,
            colors.Count,
            samples == 0 ? 0 : luminance / (double)samples)
        {
            Tiles = tiles,
            Histogram = histogram,
        };
    }

    /// <summary>
    /// Largest per-channel difference between two digests, in 0..255 units. Zero means the two
    /// captures are identical at digest resolution.
    /// </summary>
    /// <remarks>
    /// Same-backend refactors are held to ≤2 (driver float jitter only). Cross-backend comparison
    /// uses a looser bound: filtering LOD rounding, rasterisation fill rules and driver shader
    /// optimisation all differ legitimately between D3D, Vulkan and OpenGL, so exact identity is
    /// not achievable and chasing it would be wasted effort.
    /// </remarks>
    public static double MaxTileDelta(ImageMetrics a, ImageMetrics b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Tiles.Count != b.Tiles.Count)
        {
            throw new InvalidOperationException(
                $"Tile digests are not comparable: {a.Tiles.Count} tiles vs {b.Tiles.Count}. "
                + "A baseline recorded at a different digest shape must be regenerated.");
        }

        double worst = 0;
        for (int i = 0; i < a.Tiles.Count; i++)
        {
            TileColor left = a.Tiles[i];
            TileColor right = b.Tiles[i];
            worst = Math.Max(worst, Math.Abs(left.R - right.R));
            worst = Math.Max(worst, Math.Abs(left.G - right.G));
            worst = Math.Max(worst, Math.Abs(left.B - right.B));
        }

        return worst;
    }

    /// <summary>
    /// Index of the tile with the largest per-channel difference, or -1 when the digests match.
    /// Lets a failure name the region that changed instead of only the magnitude.
    /// </summary>
    public static int WorstTileIndex(ImageMetrics a, ImageMetrics b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        int worstIndex = -1;
        double worst = 0;
        for (int i = 0; i < Math.Min(a.Tiles.Count, b.Tiles.Count); i++)
        {
            double delta = Math.Max(
                Math.Abs(a.Tiles[i].R - b.Tiles[i].R),
                Math.Max(Math.Abs(a.Tiles[i].G - b.Tiles[i].G), Math.Abs(a.Tiles[i].B - b.Tiles[i].B)));
            if (delta > worst)
            {
                worst = delta;
                worstIndex = i;
            }
        }

        return worstIndex;
    }

    /// <summary>
    /// Luminance-distribution distance in 0..1, as the share of samples that would have to move
    /// buckets to make the two histograms agree. Catches global tone shifts (a tonemap applied
    /// twice, a missing gamma step) that leave per-region means close.
    /// </summary>
    public static double HistogramDistance(ImageMetrics a, ImageMetrics b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Histogram.Count != b.Histogram.Count)
        {
            throw new InvalidOperationException(
                $"Histograms are not comparable: {a.Histogram.Count} buckets vs {b.Histogram.Count}.");
        }

        long totalA = a.Histogram.Sum(v => (long)v);
        long totalB = b.Histogram.Sum(v => (long)v);
        if (totalA == 0 || totalB == 0)
        {
            return totalA == totalB ? 0 : 1;
        }

        double drift = 0;
        for (int i = 0; i < a.Histogram.Count; i++)
        {
            drift += Math.Abs((a.Histogram[i] / (double)totalA) - (b.Histogram[i] / (double)totalB));
        }

        return drift / 2;   // halve: every sample that leaves one bucket arrives in another
    }
}
