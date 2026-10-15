using System.Drawing;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Runtime;

/// <summary>
/// A long view for comparing renderers far from the camera: boxes from 40 m to 900 m in front of a
/// camera whose far plane is a kilometre, each drawn three ways side by side. Left, the engine's own
/// lit material; middle, a project mesh shader that fades into the engine's fog colour with distance
/// (as a voxel game's chunk shader does); right, a project mesh shader of one flat colour. Fog and a
/// sky are optional. Tests project <see cref="FarViewPoint"/> to pixels and compare the renderers.
/// </summary>
public sealed partial class RuntimeViewportHarness
{
    /// <summary>How far in front of the camera each row of boxes stands.</summary>
    public static readonly float[] FarViewDistances = [40f, 150f, 350f, 600f, 900f];

    public const float FarViewFarPlane = 1000f;

    /// <summary>Where the middle column's shader starts to fade into the fog colour, and where it has.</summary>
    public const float FarViewShaderFogStart = 120f;

    public const float FarViewShaderFogEnd = 450f;

    /// <summary>The engine's fog colour in this scene (also what the middle column fades into).</summary>
    public static readonly Vector3 FarViewFogColor = new(0.62f, 0.74f, 0.90f);

    /// <summary>The right column's flat colour.</summary>
    public static readonly Vector3 FarViewFlatColor = new(0.85f, 0.35f, 0.15f);

    private static readonly Vector3 FarViewEye = new(0f, 2f, 0f);

    private bool _farFog;
    private bool _farSky;
    private bool _farShadows;
    private RuntimeShaderHandle _farFadeShader;
    private RuntimeShaderHandle _farFlatShader;
    private TextureHandle _farTexture;

    // Like a voxel game's chunk shader: its own pixel shader, the draw's image read with Load, the
    // engine's constants for the camera and the fog colour, and its own fog distances (b5).
    private const string FarViewFadeHlsl = """
        cbuffer PerFrameConstants : register(b0) {
            row_major float4x4 ViewProjection; row_major float4x4 LightViewProjection;
            row_major float4x4 LightViewProjectionNear; float4 CameraPosTime;
        };
        cbuffer EngineConstants : register(b1) {
            float4 LightDirEnabled; float4 FogParams; float4 FogColor; float4 AmbientColor;
            float4 AmbientGroundColor; float4 SunColorIntensity;
        };
        cbuffer GenesisParameters : register(b5) { float4 P; };
        Texture2D Atlas : register(t1);
        struct VSOut { float4 SvPos : SV_Position; float3 WorldPos : TEXCOORD1; float3 Normal : TEXCOORD2; float4 Color : TEXCOORD3; float2 UV : TEXCOORD4; };
        float4 MainPS(VSOut IN) : SV_Target
        {
            float3 c = Atlas.Load(int3(0, 0, 0)).rgb * IN.Color.rgb;
            float fog = saturate((distance(IN.WorldPos, CameraPosTime.xyz) - P.x) / max(P.y - P.x, 1.0));
            return float4(lerp(c, FogColor.rgb, fog), 1.0);
        }
        """;

    private const string FarViewFlatHlsl = """
        cbuffer GenesisParameters : register(b5) { float4 P; };
        struct VSOut { float4 SvPos : SV_Position; float3 WorldPos : TEXCOORD1; float3 Normal : TEXCOORD2; float4 Color : TEXCOORD3; float2 UV : TEXCOORD4; };
        float4 MainPS(VSOut IN) : SV_Target { return float4(P.rgb, 1.0); }
        """;

    /// <param name="fog">The engine's distance fog on, in <see cref="FarViewFogColor"/>.</param>
    /// <param name="sky">The engine's sky (atmosphere and sky colours) behind the boxes.</param>
    /// <param name="shadows">Sun shadows on, cast by the nearest boxes (with fog, the volumetric fog runs).</param>
    public ImageMetrics CaptureFarView(string outputFile, bool fog, bool sky, bool shadows = false)
    {
        EnsureReady();
        EnsureCube();
        EnsureFarViewResources();
        _farFog = fog;
        _farSky = sky;
        _farShadows = shadows;
        _mode = CaptureMode.FarView;
        return Capture(outputFile, minimumUniqueColors: 2);
    }

    /// <summary>The centre of a box: <paramref name="row"/> indexes <see cref="FarViewDistances"/>, column 0 lit, 1 fading shader, 2 flat shader.</summary>
    public static Vector3 FarViewPoint(int row, int column)
    {
        float d = FarViewDistances[row];
        // Each row sits on its own line across the picture and each column at its own place along
        // it, whatever the distance: x and y grow with z.
        float x = (column - 1) * 0.32f * d;
        float y = (row - 2) * 0.13f * d;
        return FarViewEye + new Vector3(x, y, d);
    }

    public Point ProjectFarViewPoint(Vector3 world)
    {
        (Matrix4x4 view, Matrix4x4 projection) = FarViewCamera();
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), view * projection);
        return new Point(
            (int)MathF.Round((clip.X / clip.W * 0.5f + 0.5f) * _viewport.ClientWidth),
            (int)MathF.Round((0.5f - clip.Y / clip.W * 0.5f) * _viewport.ClientHeight));
    }

    private (Matrix4x4 View, Matrix4x4 Projection) FarViewCamera() =>
        (Genesis.Rendering.D3dMath.D3dMatrixHelper.CreateLookAtLh(FarViewEye, FarViewEye + Vector3.UnitZ, Vector3.UnitY),
         Genesis.Rendering.D3dMath.D3dMatrixHelper.CreatePerspectiveLh(
             MathF.PI / 3f,
             _viewport.ClientWidth / (float)Math.Max(1, _viewport.ClientHeight),
             0.1f,
             FarViewFarPlane));

    private void EnsureFarViewResources()
    {
        IRenderController renderer = _viewport.Renderer
            ?? throw new InvalidOperationException("Renderer is unavailable.");
        if (!_farTexture.IsValid)
            _farTexture = renderer.CreateTexture(8, 8, SolidRgba(200, 210, 190));
        if (!_farFadeShader.IsValid)
        {
            _farFadeShader = renderer.RegisterRuntimeShader(FarViewFadeHlsl, "MainPS", ShaderPreviewProfile.MeshPipeline);
            if (!_farFadeShader.IsValid) throw new InvalidOperationException("The far view's fading shader did not compile.");
        }
        if (!_farFlatShader.IsValid)
        {
            _farFlatShader = renderer.RegisterRuntimeShader(FarViewFlatHlsl, "MainPS", ShaderPreviewProfile.MeshPipeline);
            if (!_farFlatShader.IsValid) throw new InvalidOperationException("The far view's flat shader did not compile.");
        }
    }

    private void ReleaseFarViewResources()
    {
        if (_viewport.Renderer is not IRenderController renderer) return;
        if (_farFadeShader.IsValid) renderer.ReleaseRuntimeShader(_farFadeShader, ShaderPreviewProfile.MeshPipeline);
        if (_farFlatShader.IsValid) renderer.ReleaseRuntimeShader(_farFlatShader, ShaderPreviewProfile.MeshPipeline);
        _farFadeShader = RuntimeShaderHandle.Invalid;
        _farFlatShader = RuntimeShaderHandle.Invalid;
    }

    private void RenderFarView(IRenderController renderer)
    {
        EnsureCube();
        renderer.SetRoomFog(RoomFogState.Disabled);
        renderer.Set3DFrameActive(true);
        Vector3 background = new(0.32f, 0.42f, 0.58f);
        renderer.Clear(background.X, background.Y, background.Z, 1f);
        (Matrix4x4 view, Matrix4x4 projection) = FarViewCamera();
        renderer.SetCamera3D(view, projection);

        Mesh3DState state = Mesh3DState.Default;
        state.LightingEnabled = true;
        state.LightingWeight = 1f;
        state.LightDirection = Vector3.Normalize(new Vector3(0.3f, -0.8f, 0.5f));
        state.SunColor = new Vector3(1f, 0.97f, 0.92f);
        state.SunIntensity = 1.2f;
        state.AmbientColor = new Vector3(0.32f, 0.35f, 0.40f);
        state.AmbientGroundColor = new Vector3(0.14f, 0.14f, 0.15f);
        state.FrustumCullingEnabled = false;
        state.ShadowsEnabled = _farShadows;
        state.ShadowOrthoSize = 80f;
        state.ShowSunVisual = false;
        state.ShowFloor = false;
        state.CameraFarPlane = FarViewFarPlane;
        state.BackgroundColor = background;
        state.FogColor = new Vector4(FarViewFogColor, 1f);
        state.FogEnabled = _farFog;
        state.FogStart = 100f;
        state.FogEnd = 800f;
        state.FogDensity = 0.0025f;
        state.FogNoiseStrength = 0f;
        state.FogHeightBase = 0f;
        state.FogHeightFalloff = 0.002f;
        state.FogAerialBlend = 1f;
        state.VolumetricTemporalBlend = 1f; // a steady volume: captures repeat exactly
        if (_farSky)
        {
            state.AuthoredSkyEnabled = true;
            state.AtmosphereLutEnabled = true;
            state.SkyZenithColor = new Vector3(0.20f, 0.38f, 0.72f);
            state.SkyHorizonColor = new Vector3(0.66f, 0.76f, 0.88f);
            state.SkySunDirection = Vector3.Normalize(new Vector3(-0.3f, 0.8f, -0.5f));
            state.SkyTimeOfDayHours = 12f;
        }
        renderer.SetMesh3DState(state);
        renderer.ClearPointLights();

        for (int row = 0; row < FarViewDistances.Length; row++)
        {
            float size = FarViewDistances[row] * 0.08f;
            Matrix4x4 Box(int column) =>
                Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation(FarViewPoint(row, column));

            // The nearest lit box casts the sun's shadow (onto nothing in view), so a capture with
            // shadows on runs the shadow maps and, with fog, the volumetric fog.
            renderer.DrawMesh(new MeshDrawCall
            {
                Mesh = _cube, World = Box(0), Tint = new RenderColor(0.30f, 0.62f, 0.28f), Alpha = 1f,
                Flags = row == 0 ? MeshDrawFlags.None : MeshDrawFlags.NoShadow,
            });
            renderer.DrawMesh(new MeshDrawCall
            {
                Mesh = _cube, World = Box(1), Tint = RenderColor.White, Alpha = 1f, Texture = _farTexture,
                Flags = MeshDrawFlags.NoShadow | MeshDrawFlags.NoFog,
                Shader = _farFadeShader,
                ShaderParams0 = new Vector4(FarViewShaderFogStart, FarViewShaderFogEnd, 0f, 0f),
            });
            renderer.DrawMesh(new MeshDrawCall
            {
                Mesh = _cube, World = Box(2), Tint = RenderColor.White, Alpha = 1f,
                Flags = MeshDrawFlags.NoShadow | MeshDrawFlags.NoFog,
                Shader = _farFlatShader,
                ShaderParams0 = new Vector4(FarViewFlatColor, 1f),
            });
        }

        SetCaptureBadge("ENGINE FAR VIEW", "Boxes from 40 m to 900 m: engine-lit, a fading mesh shader, a flat mesh shader.");
    }
}
