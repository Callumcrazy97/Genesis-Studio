using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Rendering.Meshes;
using Genesis.Rendering.Viewport;
using Genesis.Shared.Interfaces;
using WinFormsApplication = System.Windows.Forms.Application;

namespace Genesis.Application.Runtime;

/// <summary>
/// Renders one deterministic scene that exercises every path in the forward renderer, so a change
/// to it can be compared against a recorded baseline instead of eyeballed.
/// </summary>
/// <remarks>
/// This exists because the pre-existing visual gate — <c>UniqueSampledColors &gt;= 4</c> — only
/// proves a capture is not blank. It cannot detect that shadows stopped rendering, that fog is
/// being applied twice, that water went flat grey or that a material map silently stopped binding:
/// all of those keep the colour count high and can leave mean luminance almost unchanged. Porting
/// <c>ForwardRenderer</c> onto a backend abstraction is a 3,300-line refactor, and doing that
/// against a gate that weak would be reckless, so the golden scene and its tile digest come first.
///
/// Determinism is the whole point, and it is bought by: a fixed viewport size, a fixed camera, a
/// fixed number of explicit <see cref="IRenderController.Advance3DTime"/> steps taken in
/// <see cref="Capture"/> rather than per rendered frame (readback renders several frames to let
/// the swap chain settle, so advancing time inside the render callback would make the result
/// depend on how many), procedurally generated textures rather than files on disk, and no use of
/// wall-clock time or unseeded randomness anywhere.
/// </remarks>
public sealed class RenderParityHarness : IDisposable
{
    /// <summary>Fixed capture size. Small enough to be quick, large enough for a 16×9 digest.</summary>
    public const int CaptureWidth = 640;

    public const int CaptureHeight = 360;

    /// <summary>Simulation steps taken before a capture, at a fixed 60 Hz.</summary>
    private const int TimeSteps = 30;

    private const float StepSeconds = 1f / 60f;

    private readonly Form _host;
    private readonly D3DViewportControl _viewport;
    private string? _lastRenderError;
    private RenderStats _lastStats;
    private bool _resourcesReady;
    private bool _clockWound;
    private bool _viewportSettled;
    private bool _frozenTimeWarmed;

    // Geometry
    private MeshHandle _cube;
    private MeshHandle _sphere;
    private MeshHandle _cylinder;
    private MeshHandle _quad;
    private MeshHandle _waterPlane;
    private MeshHandle _ground;
    private MeshHandle _skinnedStrip;
    private SkinPaletteHandle _skinPalette;

    // Material maps
    private TextureHandle _albedo;
    private TextureHandle _normal;
    private TextureHandle _orm;
    private TextureHandle _height;
    private TextureHandle _emission;

    public RenderParityHarness()
        : this(backend: null)
    {
    }

    /// <param name="backend">
    /// When set, the viewport creates that backend instead of the process-wide preference.
    /// Used for cross-backend golden/parity without retargeting Studio's editors.
    /// </param>
    public RenderParityHarness(Genesis.Rendering.Core.RenderBackendOption? backend)
    {
        _host = new Form
        {
            Text = "Genesis Render Parity Harness",
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(60, 60),
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
        _viewport.OnPostFrame += OnPostFrame;
        _host.Controls.Add(_viewport);
    }

    /// <summary>Draw statistics from the most recent capture, for assertions about culling/batching.</summary>
    public RenderStats LastStats => _lastStats;

    /// <summary>
    /// Creates the hidden viewport used by performance sampling. Window/device startup is harness
    /// setup rather than authored scene load, so Phase 0 measures it outside the scene load timer.
    /// </summary>
    public void InitializeBenchmarkHost() => EnsureHostReady();

    /// <summary>
    /// Prepares the deterministic golden scene without taking a screenshot. Procedural resources
    /// and the fixed scene clock are established here so they are reported as scene load cost.
    /// </summary>
    public void PrepareBenchmark()
    {
        EnsureHostReady();
        WindSceneClock();
        _lastRenderError = null;

        // The first golden-scene frame performs procedural mesh/texture registration. P0 times this
        // as scene load, while hidden-window creation/settling is measured outside that timer.
        _viewport.RenderFrame();
        if (!string.IsNullOrEmpty(_lastRenderError))
        {
            throw new InvalidOperationException(
                $"The golden benchmark scene failed to load:{Environment.NewLine}{_lastRenderError}");
        }
    }

    /// <summary>
    /// Renders one presented golden-scene frame without readback instrumentation.
    /// <see cref="PrepareBenchmark"/> must be called once before sampling.
    /// </summary>
    public RenderStats RenderBenchmarkFrame()
    {
        if (!_host.Visible || _viewport.Renderer is null || !_viewport.Renderer.IsInitialized)
        {
            throw new InvalidOperationException(
                "The parity benchmark has not been prepared. Call PrepareBenchmark() first.");
        }

        _lastRenderError = null;
        _viewport.RenderFrame();
        if (!string.IsNullOrEmpty(_lastRenderError))
        {
            throw new InvalidOperationException(
                $"The golden benchmark scene failed to render:{Environment.NewLine}{_lastRenderError}");
        }

        return _lastStats;
    }

    /// <summary>
    /// Renders the golden scene and returns its digest, also writing the PNG to
    /// <paramref name="outputFile"/> so a failure can be looked at.
    /// </summary>
    public ImageMetrics Capture(string outputFile)
    {
        return WithOmniShadowsDisabledForParity(() =>
        {
            EnsureReady();
            _lastRenderError = null;

            // Wind the scene clock to one fixed simulation time and leave it there. The renderer's
            // clock only moves forward, so advancing per capture would put a second capture at a later
            // time than the first and the animated paths (water flow, UV scroll) would legitimately
            // differ — which reads as non-determinism. A fresh harness always starts at zero, so every
            // capture from every run lands on the same instant.
            WindSceneClock();
            WarmFrozenTime();

            using Bitmap? bitmap = _viewport.ReadbackFrameToBitmap(settleFrames: 2);
            if (bitmap is null)
            {
                throw new InvalidOperationException(
                    "The parity harness could not read back a frame — the backend reported no readback support.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
            bitmap.Save(outputFile, ImageFormat.Png);

            if (!string.IsNullOrEmpty(_lastRenderError))
            {
                throw new InvalidOperationException(
                    $"The golden scene failed to render:{Environment.NewLine}{_lastRenderError}");
            }

            ImageMetrics metrics = ImageMetrics.Measure(bitmap);
            if (metrics.UniqueSampledColors < 8)
            {
                throw new InvalidOperationException(
                    $"The golden scene looks blank (colors={metrics.UniqueSampledColors}, "
                    + $"lum={metrics.AverageLuminance:F1}, draws={_lastStats.DrawCalls3D}, "
                    + $"instances={_lastStats.InstancesDrawn}). A baseline must never be recorded from this.");
            }

            return metrics;
        });
    }

    /// <summary>
    /// Renders one frame through an offscreen render target and returns what the target contained,
    /// by clearing it to <paramref name="clearColor"/>, drawing into it, then sampling the result
    /// back onto the back buffer as a fullscreen quad. Sampling rather than reading the target
    /// directly is deliberate: it proves the colour attachment is both renderable *and* bindable as
    /// a texture, which is what any post-process pass actually needs of it.
    /// </summary>
    public ImageMetrics CaptureThroughRenderTarget(string outputFile, RenderColor clearColor)
    {
        return WithOmniShadowsDisabledForParity(() =>
        {
            EnsureReady();
            _lastRenderError = null;
            _renderTargetProbe = clearColor;
            try
            {
                using Bitmap? bitmap = _viewport.ReadbackFrameToBitmap(settleFrames: 2);
                if (bitmap is null)
                {
                    throw new InvalidOperationException("No frame could be read back for the render-target probe.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
                bitmap.Save(outputFile, ImageFormat.Png);

                if (!string.IsNullOrEmpty(_lastRenderError))
                {
                    throw new InvalidOperationException(
                        $"The render-target probe failed:{Environment.NewLine}{_lastRenderError}");
                }

                return ImageMetrics.Measure(bitmap);
            }
            finally
            {
                _renderTargetProbe = null;
            }
        });
    }

    /// <summary>
    /// Renders a flat, single-colour frame and composites <paramref name="draw"/> over it through
    /// <see cref="IRenderController.ComposeOverlay"/>, so every non-background pixel in the result
    /// came from the overlay and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>Pass a null <paramref name="draw"/> for the control capture. The scene is deliberately
    /// a bare clear rather than the golden scene: against a busy 3D image, "the overlay drew" and
    /// "the scene changed" are indistinguishable in a digest.</para>
    ///
    /// <para><b>The settle frames are rendered with the overlay disabled, and the capture itself
    /// asks for none.</b> That is not a detail — with the overlay composing on every settle frame,
    /// an overlay that reached the frame one frame late still appeared in the capture, so the
    /// assertion could not tell a correct composite from NEXT-074's late-by-one HUD. Composing only
    /// on the final, non-presented frame means a late overlay captures nothing at all.</para>
    /// </remarks>
    public ImageMetrics CaptureWithOverlay(string outputFile, Action<IOverlayCanvas>? draw)
    {
        return WithOmniShadowsDisabledForParity(() =>
        {
            EnsureReady();
            _lastRenderError = null;
            _overlayProbeActive = true;
            _overlayProbe = null;

            // Settle the flat backdrop first, with no overlay in play.
            for (int i = 0; i < 3; i++) _viewport.RenderFrame();

            _overlayProbe = draw;
            try
            {
                using Bitmap? bitmap = _viewport.ReadbackFrameToBitmap(settleFrames: 0);
                if (bitmap is null)
                {
                    throw new InvalidOperationException("No frame could be read back for the overlay probe.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
                bitmap.Save(outputFile, ImageFormat.Png);

                if (!string.IsNullOrEmpty(_lastRenderError))
                {
                    throw new InvalidOperationException(
                        $"The overlay probe failed:{Environment.NewLine}{_lastRenderError}");
                }

                // NEXT-091: the sprite blend writes alpha into a back buffer whose alpha presentation
                // ignores, so a translucent HUD used to read back translucent and the saved PNG showed
                // the viewer's background through the game's own panel.
                OverlayCaptureMinimumAlpha = MinimumAlpha(bitmap);

                return ImageMetrics.Measure(bitmap);
            }
            finally
            {
                _overlayProbe = null;
                _overlayProbeActive = false;
            }
        });
    }

    /// <summary>Lowest alpha byte in the most recent <see cref="CaptureWithOverlay"/> result.</summary>
    public int OverlayCaptureMinimumAlpha { get; private set; } = 255;

    /// <summary>
    /// AF1.3 golden safety: force omni-shadow budget to 0 for parity captures so DX11/DX12
    /// digests stay stable while the default game budget remains 1.
    /// </summary>
    private static T WithOmniShadowsDisabledForParity<T>(Func<T> action)
    {
        int previous = RenderCapacityDefaults.OmniShadowBudget;
        RenderCapacityDefaults.Configure(
            RenderCapacityDefaults.SpriteInstanceCap,
            RenderCapacityDefaults.MeshInstanceCap,
            RenderCapacityDefaults.SceneLocalLightCap,
            RenderCapacityDefaults.DrawCallMode,
            RenderCapacityDefaults.WorldDrawBudget,
            omniShadowBudget: 0);
        try
        {
            return action();
        }
        finally
        {
            RenderCapacityDefaults.Configure(
                RenderCapacityDefaults.SpriteInstanceCap,
                RenderCapacityDefaults.MeshInstanceCap,
                RenderCapacityDefaults.SceneLocalLightCap,
                RenderCapacityDefaults.DrawCallMode,
                RenderCapacityDefaults.WorldDrawBudget,
                omniShadowBudget: previous);
        }
    }

    private static int MinimumAlpha(Bitmap bitmap)
    {
        int lowest = 255;
        for (int y = 0; y < bitmap.Height; y += 2)
        {
            for (int x = 0; x < bitmap.Width; x += 2)
            {
                int alpha = bitmap.GetPixel(x, y).A;
                if (alpha < lowest) lowest = alpha;
            }
        }

        return lowest;
    }

    /// <summary>The flat backdrop the overlay probe composites onto.</summary>
    private static readonly RenderColor OverlayProbeBackground = new(0.06f, 0.07f, 0.10f);

    private bool _overlayProbeActive;
    private Action<IOverlayCanvas>? _overlayProbe;
    private RenderColor? _renderTargetProbe;
    private RenderTargetHandle _probeTarget;

    private bool RenderProbeFrame(IRenderController renderer, RenderColor clear)
    {
        if (!_probeTarget.IsValid)
        {
            _probeTarget = renderer.CreateRenderTarget(ProbeSize, ProbeSize);
            if (!_probeTarget.IsValid)
            {
                throw new InvalidOperationException(
                    "CreateRenderTarget returned Invalid — offscreen targets are not implemented.");
            }
        }

        // Pass 1: into the offscreen target.
        renderer.SetRenderTarget(_probeTarget);
        renderer.Clear(clear.R, clear.G, clear.B, 1f);
        renderer.Set3DFrameActive(true);
        renderer.SetCamera3D(
            Matrix4x4.CreateLookAt(new Vector3(0f, 0f, 3.2f), Vector3.Zero, Vector3.UnitY),
            Matrix4x4.CreateOrthographic(4f, 4f, 0.1f, 20f));
        renderer.SetMesh3DState(ProbeState());
        renderer.DrawMesh(new MeshDrawCall
        {
            Mesh = _cube,
            World = Matrix4x4.CreateScale(1.6f) * Matrix4x4.CreateRotationY(0.6f),
            Tint = new RenderColor(0.95f, 0.35f, 0.20f),
            Alpha = 1f,
        });
        renderer.EndFrame();

        // Pass 2: back to the swap chain, sampling the target we just filled.
        renderer.SetRenderTargetDefault();
        renderer.Clear(0f, 0f, 0f, 1f);

        TextureHandle sampled = renderer.GetRenderTargetTexture(_probeTarget);
        if (!sampled.IsValid)
        {
            throw new InvalidOperationException(
                "GetRenderTargetTexture returned Invalid — the colour attachment is not sampleable.");
        }

        // Sampled through a textured orthographic quad rather than DrawSprite: sprite pixels do not
        // survive D3D readback in this harness (NEXT-012), so a sprite here would prove nothing
        // about the render target. The mesh path is what the existing 2D visual gate uses for the
        // same reason.
        renderer.Set3DFrameActive(true);
        renderer.SetCamera3D(
            Matrix4x4.CreateLookAt(new Vector3(0f, 0f, 3f), Vector3.Zero, Vector3.UnitY),
            Matrix4x4.CreateOrthographic(2f, 2f, 0.1f, 20f));

        Mesh3DState flat = ProbeState();
        flat.LightingEnabled = false;   // show the sampled colours, not a shaded version of them
        flat.LightingWeight = 0f;
        flat.CullBackFaces = false;     // a single quad's winding must not decide whether it appears
        renderer.SetMesh3DState(flat);

        renderer.DrawMesh(new MeshDrawCall
        {
            Mesh = _quad,
            World = Matrix4x4.CreateScale(2f),
            Texture = sampled,
            Tint = RenderColor.White,
            Alpha = 1f,
            Flags = MeshDrawFlags.NoCull,
        });
        return true;
    }

    private const int ProbeSize = 256;

    private static Mesh3DState ProbeState()
    {
        Mesh3DState state = Mesh3DState.Default;
        state.LightingEnabled = true;
        state.LightingWeight = 1f;
        state.ShadowsEnabled = false;
        state.FogEnabled = false;
        state.ShowFloor = false;
        state.ShowSunVisual = false;
        state.FrustumCullingEnabled = false;
        return state;
    }

    private void WindSceneClock()
    {
        if (_clockWound) return;

        for (int step = 0; step < TimeSteps; step++)
        {
            _viewport.AdvanceSceneTime(StepSeconds);
        }

        _clockWound = true;
    }

    private void WarmFrozenTime()
    {
        if (_frozenTimeWarmed)
            return;

        // Shader compilation and GPU caches settle at the frozen clock before either digest.
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
                "Render parity viewport failed to initialize. " + detail);
        }
    }

    private void EnsureReady()
    {
        EnsureHostReady();
        if (_viewportSettled)
            return;

        // One warmup only. Repeating this on the second golden capture used to insert
        // twelve more wall-clock Sleep/DoEvents frames between the two digests.
        for (int i = 0; i < 12; i++)
        {
            WinFormsApplication.DoEvents();
            Thread.Sleep(16);
            _viewport.RenderFrame();
        }

        _viewportSettled = true;
    }

    /// <summary>
    /// The overlay is composed here rather than in <c>OnRender</c> because it belongs over the
    /// finished frame: this hook runs after <c>EndFrame</c> and before <c>Present</c>, which is
    /// exactly the window the readback also uses.
    /// </summary>
    private void OnPostFrame(IRenderController renderer)
    {
        if (!_overlayProbeActive || _overlayProbe is null) return;
        try
        {
            renderer.ComposeOverlay(_overlayProbe);
        }
        catch (Exception ex)
        {
            _lastRenderError = ex.ToString();
        }
    }

    private void OnRender(IRenderController renderer)
    {
        try
        {
            EnsureResources(renderer);
            if (_overlayProbeActive)
            {
                // A bare clear, so anything else in the capture is the overlay.
                renderer.Set3DFrameActive(false);
                renderer.Clear(
                    OverlayProbeBackground.R, OverlayProbeBackground.G, OverlayProbeBackground.B, 1f);
                _lastStats = renderer.GetStats();
                return;
            }

            if (_renderTargetProbe is RenderColor probeClear)
            {
                RenderProbeFrame(renderer, probeClear);
            }
            else
            {
                RenderGoldenScene(renderer);
            }

            _lastStats = renderer.GetStats();
        }
        catch (Exception ex)
        {
            _lastRenderError = ex.ToString();
        }
    }

    // ── Resources ───────────────────────────────────────────────────────────────

    private void EnsureResources(IRenderController renderer)
    {
        if (_resourcesReady) return;

        _cube     = MeshGeometry.RegisterCube(renderer, RenderColor.White, 1f);
        _sphere   = MeshGeometry.RegisterSphere(renderer, RenderColor.White, 0.6f);
        _cylinder = MeshGeometry.RegisterCylinder(renderer, RenderColor.White, 0.4f, 1.4f);
        _quad     = MeshGeometry.RegisterQuad(renderer, RenderColor.White);

        var (waterVerts, waterIndices) = MeshGeometry.BuildFloor(RenderColor.White, size: 14f, uvTile: 4f);
        _waterPlane = renderer.RegisterMesh(waterVerts, waterIndices);

        var (groundVerts, groundIndices) = MeshGeometry.BuildFloor(RenderColor.White, size: 40f, uvTile: 12f);
        _ground = renderer.RegisterMesh(groundVerts, groundIndices);

        BuildSkinnedStrip(renderer);

        // Procedural, so the scene has no dependency on files that could change underneath it.
        _albedo   = MakeTexture(renderer, 64, CheckerPixel);
        _normal   = MakeTexture(renderer, 64, NormalPixel);
        _orm      = MakeTexture(renderer, 64, OrmPixel);
        _height   = MakeTexture(renderer, 64, HeightPixel);
        _emission = MakeTexture(renderer, 64, EmissionPixel);

        _resourcesReady = true;
    }

    /// <summary>
    /// A four-segment vertical strip bound to two joints, so the GPU skinning path (skinned vertex
    /// layout + structured matrix palette) is exercised rather than merely compiled.
    /// </summary>
    private void BuildSkinnedStrip(IRenderController renderer)
    {
        const int Segments = 4;
        var verts = new SkinnedMeshVertex[(Segments + 1) * 2];
        var indices = new ushort[Segments * 6];

        for (int i = 0; i <= Segments; i++)
        {
            float t = i / (float)Segments;
            float y = t * 2f;
            // Blend from joint 0 at the base to joint 1 at the tip.
            float upper = t;
            float lower = 1f - t;

            for (int side = 0; side < 2; side++)
            {
                verts[(i * 2) + side] = new SkinnedMeshVertex
                {
                    Position     = new Vector3(side == 0 ? -0.3f : 0.3f, y, 0f),
                    Normal       = Vector3.UnitZ,
                    Color        = new Vector4(0.85f, 0.75f, 0.35f, 1f),
                    UV           = new Vector2(side, t),
                    JointWeights = new Vector4(lower, upper, 0f, 0f),
                    JointIndices = new Vector4(0f, 1f, 0f, 0f),
                };
            }
        }

        for (int i = 0; i < Segments; i++)
        {
            int b = i * 2;
            int o = i * 6;
            indices[o + 0] = (ushort)b;
            indices[o + 1] = (ushort)(b + 2);
            indices[o + 2] = (ushort)(b + 1);
            indices[o + 3] = (ushort)(b + 1);
            indices[o + 4] = (ushort)(b + 2);
            indices[o + 5] = (ushort)(b + 3);
        }

        _skinnedStrip = renderer.RegisterSkinnedMesh(verts, indices);
        _skinPalette = renderer.CreateSkinPalette(2);
    }

    private static TextureHandle MakeTexture(IRenderController renderer, int size, Func<int, int, uint> pixel)
    {
        var rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                uint packed = pixel(x, y);
                int i = ((y * size) + x) * 4;
                rgba[i + 0] = (byte)(packed >> 24);
                rgba[i + 1] = (byte)(packed >> 16);
                rgba[i + 2] = (byte)(packed >> 8);
                rgba[i + 3] = (byte)packed;
            }
        }

        return renderer.CreateTexture(size, size, rgba);
    }

    private static uint Pack(byte r, byte g, byte b, byte a) =>
        ((uint)r << 24) | ((uint)g << 16) | ((uint)b << 8) | a;

    private static uint CheckerPixel(int x, int y) =>
        (((x >> 3) + (y >> 3)) & 1) == 0
            ? Pack(210, 205, 190, 255)
            : Pack(90, 105, 130, 255);

    private static uint NormalPixel(int x, int y)
    {
        // A gentle ripple, so normal-map scaling has something visible to scale.
        float nx = MathF.Sin(x * 0.35f) * 0.35f;
        float ny = MathF.Sin(y * 0.35f) * 0.35f;
        float nz = MathF.Sqrt(MathF.Max(0.0001f, 1f - (nx * nx) - (ny * ny)));
        return Pack(
            (byte)((nx * 0.5f + 0.5f) * 255f),
            (byte)((ny * 0.5f + 0.5f) * 255f),
            (byte)((nz * 0.5f + 0.5f) * 255f),
            255);
    }

    private static uint OrmPixel(int x, int y) =>
        // R = occlusion, G = roughness, B = metallic.
        Pack(255, (byte)(70 + ((x * 3) & 0x7F)), (byte)(((y >> 4) & 1) == 0 ? 20 : 200), 255);

    private static uint HeightPixel(int x, int y) =>
        Pack((byte)(((x >> 2) + (y >> 2)) & 0xFF), 0, 0, 255);

    private static uint EmissionPixel(int x, int y) =>
        ((x ^ y) & 0x10) != 0 ? Pack(255, 120, 40, 255) : Pack(0, 0, 0, 255);

    // ── The scene ───────────────────────────────────────────────────────────────

    private void RenderGoldenScene(IRenderController renderer)
    {
        renderer.Set3DFrameActive(true);
        renderer.Clear(0.10f, 0.13f, 0.19f, 1f);

        Matrix4x4 view = Matrix4x4.CreateLookAt(
            new Vector3(7.5f, 4.6f, 9.5f),
            new Vector3(0f, 1.0f, 0f),
            Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 4f,
            CaptureWidth / (float)CaptureHeight,
            0.1f,
            220f);
        renderer.SetCamera3D(view, projection);

        renderer.SetMesh3DState(GoldenState());

        // Point lights and fog volumes are per-frame submissions, cleared at BeginFrame.
        SubmitPointLights(renderer);
        SubmitFogVolumes(renderer);

        DrawGround(renderer);
        DrawInstancedOpaqueBatch(renderer);
        DrawMaterialMappedMesh(renderer);
        DrawSkinned(renderer);
        DrawFoliage(renderer);
        DrawWater(renderer);
        DrawTransparentAndAdditive(renderer);
        DrawViewModel(renderer);
    }

    private static Mesh3DState GoldenState()
    {
        Mesh3DState state = Mesh3DState.Default;
        state.LightingEnabled       = true;
        state.LightingWeight        = 1f;
        state.LightDirection        = Vector3.Normalize(new Vector3(-0.45f, -1f, -0.35f));
        state.SunColor              = new Vector3(1f, 0.95f, 0.86f);
        state.SunIntensity          = 1.25f;
        state.AmbientColor          = new Vector3(0.26f, 0.30f, 0.38f);
        state.AmbientGroundColor    = new Vector3(0.09f, 0.09f, 0.11f);
        state.CullBackFaces         = true;
        state.FrustumCullingEnabled = true;
        state.ShadowsEnabled        = true;      // near + far cascade
        state.ShadowHighQuality     = true;
        state.ShadowOrthoSize       = 26f;
        state.FogEnabled            = true;
        state.FogScreenSpace        = true;      // screen-space fog post + composite
        state.FogStart              = 14f;
        state.FogEnd                = 90f;
        state.VolumetricFogEnabled  = true;
        state.VolumetricFogQuality  = 1;
        state.VolumetricTemporalBlend = 1f;      // analytic only; never a history blend
        state.ShowFloor             = true;      // built-in environment floor
        state.FloorFollowsCamera    = false;
        state.ShowSunVisual         = true;      // sun billboard
        state.StylizedLightingEnabled = true;
        state.CameraFarPlane        = 220f;
        state.CameraForward         = Vector3.Normalize(new Vector3(-7.5f, -3.6f, -9.5f));
        return state;
    }

    private static void SubmitPointLights(IRenderController renderer)
    {
        // Eight — the documented cap — laid out on a fixed ring so the count itself is covered.
        for (int i = 0; i < 8; i++)
        {
            float angle = i * MathF.Tau / 8f;
            renderer.AddPointLight(
                new Vector3(MathF.Cos(angle) * 5.5f, 1.4f, MathF.Sin(angle) * 5.5f),
                new Vector3(
                    0.5f + (0.5f * MathF.Cos(angle)),
                    0.5f + (0.5f * MathF.Sin(angle * 1.7f)),
                    0.9f),
                radius: 6f,
                intensity: 1.1f);
        }
    }

    private static void SubmitFogVolumes(IRenderController renderer)
    {
        renderer.AddFogVolume(new FogVolume
        {
            Center = new Vector3(-4f, 1.0f, -2f),
            Extents = new Vector3(3.5f, 1.4f, 3.5f),
            Color = new Vector3(0.62f, 0.68f, 0.80f),
            Density = 0.55f,
            FalloffCurve = 1.6f,
            Shape = FogVolumeShape.Box,
            Kind = FogVolumeKind.GroundMist,
        });

        renderer.AddFogVolume(new FogVolume
        {
            Center = new Vector3(4.5f, 2.0f, 1.5f),
            Extents = new Vector3(2.6f, 2.6f, 2.6f),
            Color = new Vector3(0.85f, 0.72f, 0.55f),
            Density = 0.45f,
            FalloffCurve = 2f,
            Shape = FogVolumeShape.Sphere,
            Kind = FogVolumeKind.Haze,
        });
    }

    private void DrawGround(IRenderController renderer)
    {
        // TerrainGround swaps the pixel shader onto its procedural slope/noise blend.
        renderer.DrawMesh(new MeshDrawCall
        {
            Mesh = _ground,
            World = Matrix4x4.CreateTranslation(0f, -0.01f, 0f),
            Tint = new RenderColor(0.42f, 0.46f, 0.38f),
            Alpha = 1f,
            Flags = MeshDrawFlags.TerrainGround,
            Texture = _albedo,
        });
    }

    /// <summary>Several draws sharing one mesh and material, which the renderer batches into one instanced call.</summary>
    private void DrawInstancedOpaqueBatch(IRenderController renderer)
    {
        for (int i = 0; i < 12; i++)
        {
            float angle = i * MathF.Tau / 12f;
            float radius = 3.2f + ((i % 3) * 0.9f);
            Matrix4x4 world =
                Matrix4x4.CreateScale(0.5f + ((i % 4) * 0.16f)) *
                Matrix4x4.CreateRotationY(angle) *
                Matrix4x4.CreateTranslation(
                    MathF.Cos(angle) * radius,
                    0.45f + ((i % 5) * 0.28f),
                    MathF.Sin(angle) * radius);

            renderer.DrawMesh(new MeshDrawCall
            {
                Mesh = _cube,
                World = world,
                Texture = _albedo,
                Tint = new RenderColor(0.80f, 0.82f, 0.86f),
                Alpha = 1f,
            });
        }

        // A tall caster well outside the near cascade, so the far cascade has work to do.
        renderer.DrawMesh(new MeshDrawCall
        {
            Mesh = _cylinder,
            World = Matrix4x4.CreateScale(1.6f, 4.5f, 1.6f) * Matrix4x4.CreateTranslation(-11f, 3.2f, -12f),
            Texture = _albedo,
            Tint = new RenderColor(0.7f, 0.66f, 0.6f),
            Alpha = 1f,
        });
    }

    /// <summary>The full material set: normal, ORM, height/parallax, emission — the path NEXT-066 had broken.</summary>
    private void DrawMaterialMappedMesh(IRenderController renderer)
    {
        renderer.DrawMesh(new MeshDrawCall
        {
            Mesh = _sphere,
            World = Matrix4x4.CreateScale(2.2f) * Matrix4x4.CreateTranslation(0f, 1.5f, 0f),
            Texture = _albedo,
            NormalMap = _normal,
            OrmMap = _orm,
            HeightMap = _height,
            EmissionMap = _emission,
            HeightMode = Genesis.Shared.Materials.MaterialHeightMode.Parallax,
            SurfaceParams = new Vector4(1.4f, 0.05f, 1.8f, 0.6f),      // normal, height, emission, clearcoat
            DetailParams = new Vector4(0.35f, 0.4f, 0.25f, 1f),        // subsurface, flow speed/strength, UV scale
            SubsurfaceColorSteps = new Vector4(0.9f, 0.55f, 0.45f, 16f),
            Tint = RenderColor.White,
            Alpha = 1f,
            Emissive = 0.4f,
            Flags = MeshDrawFlags.Emissive,
        });
    }

    private void DrawSkinned(IRenderController renderer)
    {
        // A fixed, non-identity two-joint pose: joint 1 leans, joint 0 stays put.
        Matrix4x4[] palette =
        [
            Matrix4x4.Identity,
            Matrix4x4.CreateRotationZ(0.45f) * Matrix4x4.CreateTranslation(0.25f, 0f, 0f),
        ];
        renderer.UpdateSkinPalette(_skinPalette, palette);

        renderer.DrawMesh(new MeshDrawCall
        {
            Mesh = _skinnedStrip,
            SkinPalette = _skinPalette,
            World = Matrix4x4.CreateTranslation(-3.4f, 0f, 3.2f),
            Texture = _albedo,
            Tint = RenderColor.White,
            Alpha = 1f,
        });
    }

    /// <summary>Alpha-cutout crossed quads: unlit foliage shading plus two-sided rasterisation.</summary>
    private void DrawFoliage(IRenderController renderer)
    {
        for (int i = 0; i < 6; i++)
        {
            float angle = i * MathF.Tau / 6f;
            renderer.DrawMesh(new MeshDrawCall
            {
                Mesh = _quad,
                World =
                    Matrix4x4.CreateScale(1.3f) *
                    Matrix4x4.CreateRotationY(angle) *
                    Matrix4x4.CreateTranslation(MathF.Cos(angle) * 6.4f, 0.65f, MathF.Sin(angle) * 6.4f),
                Texture = _albedo,
                Tint = new RenderColor(0.45f, 0.70f, 0.35f),
                Alpha = 1f,
                Flags = MeshDrawFlags.Foliage | MeshDrawFlags.NoCull,
            });
        }
    }

    private void DrawWater(IRenderController renderer)
    {
        renderer.DrawMesh(new MeshDrawCall
        {
            Mesh = _waterPlane,
            World = Matrix4x4.CreateTranslation(0f, 0.35f, 0f),
            Tint = new RenderColor(0.30f, 0.55f, 0.62f),
            DeepTint = new RenderColor(0.04f, 0.16f, 0.26f),
            WaterParams = new Vector4(0.35f, 0.6f, 0.4f, 0.12f),   // flow, fresnel, foam, amplitude
            SkyHorizon = new Vector4(0.62f, 0.72f, 0.85f, 1f),
            SkyZenith = new Vector4(0.24f, 0.42f, 0.72f, 1f),
            Alpha = 0.85f,
            Flags = MeshDrawFlags.Water,
        });
    }

    private void DrawTransparentAndAdditive(IRenderController renderer)
    {
        renderer.DrawMesh(new MeshDrawCall
        {
            Mesh = _cube,
            World = Matrix4x4.CreateScale(1.8f) * Matrix4x4.CreateTranslation(2.8f, 1.6f, 3.4f),
            Tint = new RenderColor(0.35f, 0.85f, 0.95f),
            Alpha = 0.45f,
            Flags = MeshDrawFlags.Transparent | MeshDrawFlags.NoDepthWrite,
        });

        renderer.DrawMesh(new MeshDrawCall
        {
            Mesh = _sphere,
            World = Matrix4x4.CreateScale(1.1f) * Matrix4x4.CreateTranslation(-2.6f, 2.4f, 2.2f),
            Tint = new RenderColor(1f, 0.62f, 0.22f),
            Alpha = 0.8f,
            Emissive = 1.5f,
            Flags = MeshDrawFlags.Additive | MeshDrawFlags.NoShadow | MeshDrawFlags.NoFog,
        });
    }

    /// <summary>First-person layer: its own depth range, drawn over everything.</summary>
    private void DrawViewModel(IRenderController renderer)
    {
        renderer.DrawMesh(new MeshDrawCall
        {
            Mesh = _cube,
            World =
                Matrix4x4.CreateScale(0.30f, 0.30f, 1.1f) *
                Matrix4x4.CreateRotationY(0.5f) *
                Matrix4x4.CreateTranslation(5.6f, 3.1f, 7.4f),
            Texture = _albedo,
            Tint = new RenderColor(0.72f, 0.30f, 0.28f),
            Alpha = 1f,
            Flags = MeshDrawFlags.NoDepthTest | MeshDrawFlags.NoShadow,
        });
    }

    public void Dispose()
    {
        _viewport.OnRender -= OnRender;
        _viewport.OnPostFrame -= OnPostFrame;
        _host.Close();
        _host.Dispose();

        // Let the close/destroy messages drain before the next test activates a window of its own.
        // Without this the teardown is still in flight, and the visual-capture harness has been
        // seen to sample a form mid-destruction ("too small", "excessive transparency").
        WinFormsApplication.DoEvents();
    }
}
