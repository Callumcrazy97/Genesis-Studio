using System;
using System.IO;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Editor;

/// <summary>What an editor viewport is previewing.</summary>
public enum PreviewKind
{
    Empty,
    Sprite,
    Model,
    Room2D,
    Room3D,
    ObjectSandbox,
    Terrain,
    ShaderLog,
}

/// <summary>Configuration passed to <see cref="EditorPreviewSession"/> each frame.</summary>
public sealed class PreviewContext
{
    public PreviewKind Kind = PreviewKind.Empty;
    public string ProjectPath;
    public string ResourceFolder;
    public string ResourceName;
    public string ResolvedImagePath;
    /// <summary>Optional live RGBA8 surface supplied by an editor document.</summary>
    public byte[] RgbaData;
    public int RgbaWidth;
    public int RgbaHeight;
    /// <summary>Increment after mutating <see cref="RgbaData"/> so the GPU texture is updated once.</summary>
    public long ContentVersion;
    /// <summary>Normalized UV rectangle for the selected frame; zero means the whole texture.</summary>
    public Vector4 FrameUv;
    /// <summary>Pivot in source pixel space; negative values use texture center.</summary>
    public float OriginPixelX = -1f;
    public float OriginPixelY = -1f;
    public bool ShowCheckerboard = true;
    public float CamX, CamY, CamZoom = 1f;
    public bool UseExplicitContentPosition;
    public float ContentX, ContentY;
    /// <summary>When true, sprite/checkerboard are drawn in swap-chain pixel space (legacy image viewer layout).</summary>
    public bool UseScreenSpaceLayout;
    public float ScreenX, ScreenY, ScreenWidth, ScreenHeight;
    public Vector3 Eye = new(0f, 6f, 18f);
    public float YawRad, PitchRad = -0.2f;
    public ShaderPreviewProfile ShaderProfile = ShaderPreviewProfile.SpritePipeline;
    public bool ShaderFullscreenOverlay;
}

/// <summary>
/// Shared preview draw path for all editors. Builds the same draw lists export/run will use;
/// editor gizmos belong in a separate overlay pass (not implemented here yet).
/// </summary>
public sealed class EditorPreviewSession : IDisposable
{
    private readonly MeshDrawCall[] _meshBuffer = new MeshDrawCall[4096];
    private TextureHandle _spriteTexture;
    private string _loadedSpriteKey;
    private long _loadedContentVersion = long.MinValue;
    private int _loadedMemoryWidth;
    private int _loadedMemoryHeight;

    public void UpdateAndRender(IRenderController renderer, float dt, PreviewContext ctx, bool advanceTime = true)
    {
        if (renderer == null || ctx == null) return;

        if (advanceTime)
            renderer.Advance3DTime(dt);

        switch (ctx.Kind)
        {
            case PreviewKind.Sprite:
                RenderSpritePreview(renderer, ctx);
                break;
            case PreviewKind.Room2D:
                RenderRoom2DPreview(renderer, ctx);
                break;
            case PreviewKind.Terrain:
            case PreviewKind.Room3D:
            case PreviewKind.Model:
                Render3DPreview(renderer, dt, ctx, ctx.Kind);
                break;
            case PreviewKind.ObjectSandbox:
                renderer.Clear(0.06f, 0.07f, 0.10f);
                renderer.SetCamera2D(ctx.CamX, ctx.CamY, ctx.CamZoom, 0f);
                break;
            default:
                if (ctx.Kind != PreviewKind.ShaderLog)
                    renderer.Clear(0.08f, 0.09f, 0.12f);
                break;
        }
    }

    private void RenderSpritePreview(IRenderController renderer, PreviewContext ctx)
    {
        renderer.Set3DFrameActive(false);
        int viewW = Math.Max(1, renderer.PixelWidth);
        int viewH = Math.Max(1, renderer.PixelHeight);
        renderer.SetViewport(0, 0, viewW, viewH);
        renderer.Clear(0.12f, 0.12f, 0.14f);

        bool hasMemoryImage = ctx.RgbaData != null
            && ctx.RgbaWidth > 0
            && ctx.RgbaHeight > 0
            && ctx.RgbaData.Length >= ctx.RgbaWidth * ctx.RgbaHeight * 4;

        if (hasMemoryImage)
        {
            bool dimensionsChanged = _loadedMemoryWidth != ctx.RgbaWidth
                || _loadedMemoryHeight != ctx.RgbaHeight;
            if (!_spriteTexture.IsValid || dimensionsChanged)
            {
                if (_spriteTexture.IsValid) renderer.ReleaseTexture(_spriteTexture);
                _spriteTexture = renderer.CreateTexture(ctx.RgbaWidth, ctx.RgbaHeight, ctx.RgbaData);
                if (!_spriteTexture.IsValid
                    && !string.IsNullOrWhiteSpace(ctx.ResolvedImagePath)
                    && File.Exists(ctx.ResolvedImagePath))
                {
                    _spriteTexture = renderer.LoadTexture(ctx.ResolvedImagePath);
                }

                if (_spriteTexture.IsValid)
                {
                    _loadedMemoryWidth = ctx.RgbaWidth;
                    _loadedMemoryHeight = ctx.RgbaHeight;
                    _loadedContentVersion = ctx.ContentVersion;
                    _loadedSpriteKey = null;
                }
            }
            else if (_spriteTexture.IsValid && _loadedContentVersion != ctx.ContentVersion)
            {
                renderer.UpdateTexture(_spriteTexture, ctx.RgbaWidth, ctx.RgbaHeight, ctx.RgbaData);
                _loadedContentVersion = ctx.ContentVersion;
            }
            else if (!_spriteTexture.IsValid
                && !string.IsNullOrWhiteSpace(ctx.ResolvedImagePath)
                && File.Exists(ctx.ResolvedImagePath))
            {
                _spriteTexture = renderer.LoadTexture(ctx.ResolvedImagePath);
            }
        }
        else
        {
            string key = ctx.ProjectPath + "|" + ctx.ResourceFolder + "|" + ctx.ResourceName;
            if (_loadedSpriteKey != key)
            {
                if (_spriteTexture.IsValid) renderer.ReleaseTexture(_spriteTexture);
                _spriteTexture = TextureHandle.Invalid;
                _loadedSpriteKey = key;
                _loadedContentVersion = long.MinValue;
                _loadedMemoryWidth = 0;
                _loadedMemoryHeight = 0;

                string path = !string.IsNullOrEmpty(ctx.ResolvedImagePath)
                    ? ctx.ResolvedImagePath
                    : Genesis.Shared.Assets.ImageAssetDecoder.ResolveImagePath(
                        ctx.ProjectPath, ctx.ResourceFolder, ctx.ResourceName);
                if (path != null)
                    _spriteTexture = renderer.LoadTexture(path);
            }
        }

        bool useScreenLayout = ctx.UseScreenSpaceLayout && ctx.ScreenWidth > 0f && ctx.ScreenHeight > 0f;
        if (useScreenLayout)
        {
            renderer.SetCamera2D(viewW * 0.5f, viewH * 0.5f, 1f, 0f);
        }
        else
        {
            renderer.SetCamera2D(ctx.CamX, ctx.CamY, ctx.CamZoom, 0f);
        }

        if (_spriteTexture.IsValid)
        {
            float contentX;
            float contentY;
            float w;
            float h;
            float originX;
            float originY;
            if (useScreenLayout)
            {
                contentX = ctx.ScreenX;
                contentY = ctx.ScreenY;
                w = ctx.ScreenWidth;
                h = ctx.ScreenHeight;
                originX = 0f;
                originY = 0f;
            }
            else
            {
                contentX = ctx.UseExplicitContentPosition ? ctx.ContentX : ctx.CamX;
                contentY = ctx.UseExplicitContentPosition ? ctx.ContentY : ctx.CamY;
                w = hasMemoryImage ? ctx.RgbaWidth : 256f;
                h = hasMemoryImage ? ctx.RgbaHeight : 256f;
                if (!hasMemoryImage && !string.IsNullOrEmpty(ctx.ResolvedImagePath))
                {
                    Genesis.Shared.Assets.ImageAssetDecoder.DecodeToRgba(ctx.ResolvedImagePath, out int iw, out int ih);
                    if (iw > 0 && ih > 0) { w = iw; h = ih; }
                }

                originX = w * 0.5f;
                originY = h * 0.5f;
                int sourceWidth = hasMemoryImage ? ctx.RgbaWidth : (int)w;
                int sourceHeight = hasMemoryImage ? ctx.RgbaHeight : (int)h;
                if (ctx.OriginPixelX >= 0f && ctx.OriginPixelY >= 0f && sourceWidth > 0 && sourceHeight > 0)
                {
                    originX = ctx.OriginPixelX / sourceWidth * w;
                    originY = ctx.OriginPixelY / sourceHeight * h;
                }
            }

            if (ctx.ShowCheckerboard)
                DrawCheckerboard(renderer, contentX, contentY, w, h, topLeft: useScreenLayout);

            var call = new SpriteDrawCall
            {
                Texture = _spriteTexture,
                X = useScreenLayout ? contentX : contentX,
                Y = useScreenLayout ? contentY : contentY,
                Width = w,
                Height = h,
                OriginX = originX,
                OriginY = originY,
                Tint = RenderColor.White,
                Alpha = 1f,
                UvRect = ctx.FrameUv,
            };
            renderer.DrawSpriteBatch(new[] { call });
        }
        else if (hasMemoryImage && ctx.ShowCheckerboard)
        {
            if (useScreenLayout)
            {
                DrawCheckerboard(renderer, ctx.ScreenX, ctx.ScreenY, ctx.ScreenWidth, ctx.ScreenHeight, topLeft: true);
            }
            else
            {
                float contentX = ctx.UseExplicitContentPosition ? ctx.ContentX : ctx.CamX;
                float contentY = ctx.UseExplicitContentPosition ? ctx.ContentY : ctx.CamY;
                DrawCheckerboard(renderer, contentX, contentY, ctx.RgbaWidth, ctx.RgbaHeight);
            }
        }
    }

    private static void DrawCheckerboard(
        IRenderController renderer,
        float x,
        float y,
        float width,
        float height,
        bool topLeft = false)
    {
        const float cell = 16f;
        float left = topLeft ? x : x - width * 0.5f;
        float top = topLeft ? y : y - height * 0.5f;
        int columns = Math.Max(1, (int)MathF.Ceiling(width / cell));
        int rows = Math.Max(1, (int)MathF.Ceiling(height / cell));
        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < columns; col++)
            {
                float cw = MathF.Min(cell, width - col * cell);
                float ch = MathF.Min(cell, height - row * cell);
                RenderColor color = ((col + row) & 1) == 0
                    ? new RenderColor(0.22f, 0.23f, 0.27f, 1f)
                    : new RenderColor(0.15f, 0.16f, 0.19f, 1f);
                renderer.DrawRect(left + col * cell, top + row * cell, cw, ch, color, filled: true, depth: 1000);
            }
        }
    }

    private static void RenderRoom2DPreview(IRenderController renderer, PreviewContext ctx)
    {
        renderer.Clear(0.05f, 0.06f, 0.10f);
        renderer.SetCamera2D(ctx.CamX, ctx.CamY, ctx.CamZoom, 0f);
        renderer.DrawRect(-2000, -2000, 4000, 4000, new RenderColor(0.15f, 0.16f, 0.2f), filled: true);
    }

    private void Render3DPreview(IRenderController renderer, float dt, PreviewContext ctx, PreviewKind kind)
    {
        renderer.Clear(0.09f, 0.10f, 0.14f);

        float aspect = renderer.PixelWidth > 0 && renderer.PixelHeight > 0
            ? (float)renderer.PixelWidth / renderer.PixelHeight : 16f / 9f;

        renderer.SetViewport(0, 0, renderer.PixelWidth, renderer.PixelHeight);

        Vector3 fwd = ForwardFromYawPitch(ctx.YawRad, ctx.PitchRad);
        var view = Matrix4x4.CreateLookAt(ctx.Eye, ctx.Eye + fwd, Vector3.UnitY);
        float fov = 75f * MathF.PI / 180f;
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, 0.1f, 1000f);
        renderer.SetCamera3D(view, proj);

        // Editor lighting overhaul: full state (warm sun + shadows, hemisphere sky ambient,
        // wrapped diffuse + specular + rim via the stylized pipeline) instead of the old flat
        // "LightingEnabled and nothing else" Lambert look.
        Mesh3DState state = Mesh3DState.Default;
        state.SunIntensity = 1.25f;
        state.ShadowsEnabled = true;
        state.StylizedLightingEnabled = true;
        state.StylizedToonSteps = 1f;
        state.StylizedDiffuseWrap = 0.42f;
        state.StylizedSpecularStrength = 0.85f;
        state.StylizedRimStrength = 0.30f;
        state.StylizedSaturation = 1.06f;
        state.ShowFloor = kind != PreviewKind.Terrain;
        state.ShowSunVisual = false;
        renderer.SetMesh3DState(state);

        // The forward renderer draws the reference floor itself when ShowFloor is set;
        // submitting the builtin floor mesh through the batch path renders black and
        // occludes the real environment floor.
        int drawCount = 0;

        if (kind == PreviewKind.Model)
        {
            MeshHandle sun = renderer.GetBuiltinMesh(BuiltinMeshKind.Sun);
            if (sun.IsValid && drawCount < _meshBuffer.Length)
            {
                _meshBuffer[drawCount++] = new MeshDrawCall
                {
                    Mesh = sun,
                    World = Matrix4x4.CreateScale(4f),
                    Tint = new RenderColor(0.4f, 0.7f, 1f),
                    Flags = MeshDrawFlags.NoShadow,
                };
            }
        }

        if (drawCount > 0)
            renderer.DrawMeshBatch(_meshBuffer.AsSpan(0, drawCount));
    }

    private static Vector3 ForwardFromYawPitch(float yaw, float pitch)
    {
        float cp = MathF.Cos(pitch);
        return Vector3.Normalize(new Vector3(
            cp * MathF.Sin(yaw),
            MathF.Sin(pitch),
            cp * MathF.Cos(yaw)));
    }

    public void Dispose()
    {
        _spriteTexture = TextureHandle.Invalid;
        _loadedSpriteKey = null;
        _loadedContentVersion = long.MinValue;
        _loadedMemoryWidth = 0;
        _loadedMemoryHeight = 0;
    }
}
