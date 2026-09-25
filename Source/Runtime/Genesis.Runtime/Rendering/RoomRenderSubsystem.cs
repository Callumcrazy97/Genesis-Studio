using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Genesis.Rendering.D3dMath;
using Genesis.Rendering.Diagnostics;
using Genesis.Rendering.Meshes;
using Genesis.Runtime.Core;
using Genesis.Runtime.Culling;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Textures;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Rendering;

namespace Genesis.Runtime.Rendering;

/// <summary>Shared room presentation pass used by the player and editor Game view.</summary>
public sealed partial class RoomRenderSubsystem : ISceneSubsystem
{
    private readonly string _projectPath;
    private readonly RoomAsset _room;
    private readonly RoomSceneBuilder _cameraResolver;
    private readonly Dictionary<string, TextureHandle> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _resolvedImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int Width, int Height)> _imageSizes = new(StringComparer.OrdinalIgnoreCase);
    private int _drawAreaWidth, _drawAreaHeight;
    private IRenderController _renderer;
    private MeshHandle _quad;
    private float _roomTime;
    public bool IsTwoD => _room.Dimension == RoomDimension.TwoD;

    public RoomRenderSubsystem(string projectPath, RoomAsset room) { _projectPath = projectPath; _room = room; _cameraResolver = new RoomSceneBuilder(projectPath); }
    public void FixedUpdate(RuntimeScene scene, float fixedDelta) { }
    public void Update(RuntimeScene scene, GameTime time)
    {
        _roomTime = time?.Total ?? _roomTime;
        if (IsTwoD) RoomEffects2D.For(_room)?.Advance(time?.Delta ?? 0);
        if (!IsTwoD && _room.UsesViewports)
            for (int index = 0; index < _room.Viewports.Count; index++)
                if (_room.Viewports[index].Enabled) AdvanceViewport(scene, index, _room.Viewports[index]);
    }

    /// <summary>Live scroll position of each configured viewport. Never written back to the room.</summary>
    private readonly RoomViewportTracker _viewportTracker = new();

    public Vector3 GetViewportPosition(int index)
    {
        (float x, float y, float z) = _viewportTracker.PositionOf(_room, index);
        return new Vector3(x, y, z);
    }

    public void SetViewportPosition(int index, Vector3 position) =>
        _viewportTracker.SetPosition(_room, index, position.X, position.Y, position.Z);

    public void StartViewportShake(int index, float magnitude, float durationSeconds) =>
        _viewportTracker.StartShake(_room, index, magnitude, durationSeconds, _roomTime);

    public Vector2 GetViewportShakeOffset(int index) =>
        _viewportTracker.ShakeOffset(_room, index, _roomTime);

    public void Render2D(RuntimeScene scene, IRenderController renderer, IRenderCommandSink commands)
    {
        if (!IsTwoD || commands == null) return;
        renderer.SetSamplerState(_room.Settings.PixelArtSampling ? SamplerFilter.Point : SamplerFilter.Linear);

        // A room with viewports configured draws once per enabled viewport, into that viewport's
        // rectangle of the window. A room with none keeps the original single-camera path exactly,
        // so nothing that worked before depends on the new feature being correct.
        if (_room.UsesViewports)
        {
            RenderViewports2D(scene, renderer, commands);
            return;
        }

        RenderSingleCamera2D(scene, renderer, commands);
    }

    private void RenderViewports2D(RuntimeScene scene, IRenderController renderer, IRenderCommandSink commands)
    {
        _renderer = renderer;
        renderer.Set3DFrameActive(false);

        for (int index = 0; index < _room.Viewports.Count; index++)
        {
            RoomViewport viewport = _room.Viewports[index];
            if (!viewport.Enabled) continue;

            AdvanceViewport(scene, index, viewport);
            (float sourceX, float sourceY, _) = _viewportTracker.PositionOf(_room, index);
            Vector2 shake = _viewportTracker.ShakeOffset(_room, index, _roomTime);
            sourceX += shake.X;
            sourceY += shake.Y;

            // The port is the region of the window this viewport owns; everything it draws is
            // clipped and scaled into it, which is what makes split screen work.
            System.Drawing.Rectangle port = RoomDisplayLayout.Port(_room, viewport, renderer.PixelWidth, renderer.PixelHeight);
            _drawAreaWidth = port.Width; _drawAreaHeight = port.Height;
            IRenderCommandSink viewportCommands = new ViewportSpriteSink(commands, port);

            float zoom = viewport.SourceWidth <= 0f ? 1f : port.Width / viewport.SourceWidth;
            float offsetX = -sourceX * zoom;
            float offsetY = -sourceY * zoom;

            // The final sprite pass uses one full-target orthographic camera. Each command owns
            // its destination offset and clip; changing GPU state while queuing does not work.
            renderer.SetCamera2D(renderer.PixelWidth * .5f, renderer.PixelHeight * .5f, 1f, 0f);
            DrawBackgrounds2D(renderer, viewportCommands, offsetX, offsetY, zoom,
                sourceX + (viewport.SourceWidth * 0.5f), sourceY + (viewport.SourceHeight * 0.5f));
            DrawTiles2D(renderer, viewportCommands, offsetX, offsetY, zoom);
            ObjectDrawPass.Render2D(scene.World, _projectPath, viewportCommands, renderer, offsetX, offsetY, zoom);
            RenderObjectComposition2D(scene, viewportCommands, offsetX, offsetY, zoom);
            RoomEffects2D.For(_room)?.Render(renderer, viewportCommands, _projectPath, index, offsetX, offsetY, zoom, _drawAreaWidth, _drawAreaHeight);
        }

        renderer.SetViewport(0, 0, renderer.PixelWidth, renderer.PixelHeight);
    }

    /// <summary>Moves a viewport toward its follow target, if it has one that still exists.</summary>
    private void AdvanceViewport(RuntimeScene scene, int index, RoomViewport viewport)
    {
        if (string.IsNullOrWhiteSpace(viewport.FollowTarget))
        {
            _viewportTracker.Follow(_room, index, hasTarget: false, 0f, 0f, 0f);
            return;
        }

        bool found = ObjectDrawPass.TryFindInstancePosition(
            scene?.World, viewport.FollowTarget, out float x, out float y, out float z);
        _viewportTracker.Follow(_room, index, found, x, y, z);
    }

    private void RenderSingleCamera2D(RuntimeScene scene, IRenderController renderer, IRenderCommandSink commands)
    {
        _renderer = renderer;
        _drawAreaWidth = renderer.PixelWidth; _drawAreaHeight = renderer.PixelHeight;
        renderer.Set3DFrameActive(false);
        renderer.SetViewport(0, 0, renderer.PixelWidth, renderer.PixelHeight);
        RoomNode camera = _room.Nodes.FirstOrDefault(n => n.Id == _room.ActiveGameCameraId);
        RoomCameraState cameraState = _cameraResolver.ResolveCameraState(_room, camera);
        float cx = cameraState?.Position.X ?? _room.Settings.Width * .5f;
        float cy = cameraState?.Position.Y ?? _room.Settings.Height * .5f;
        float zoom = cameraState?.Zoom2D ?? 1f;
        float offsetX = renderer.PixelWidth * .5f - cx * zoom, offsetY = renderer.PixelHeight * .5f - cy * zoom;
        // The 2D camera is derived from several values that are individually plausible and only
        // wrong in combination, so state them once. A room that draws in the wrong place is
        // otherwise pure guesswork from a screenshot.
        if (!_loggedCamera)
        {
            _loggedCamera = true;
            RenderLog.Line(
                $"[Room] 2D camera: buffer={renderer.PixelWidth}x{renderer.PixelHeight} " +
                $"room={_room.Settings.Width}x{_room.Settings.Height} " +
                $"camera={(camera?.Name ?? "(none)")} centre=({cx:0.#},{cy:0.#}) " +
                $"zoom={zoom:0.###} offset=({offsetX:0.#},{offsetY:0.#})");
        }

        // SetCamera2D takes the camera *centre in world space*, not a screen offset: the sprite
        // renderer computes screen = world - camera + halfScreen (see FlushOverlaySprites, which
        // passes width/2, height/2, 1 to mean "identity"). Every draw below already bakes the
        // offset and zoom into its own position, so the camera has to be the identity value —
        // passing offsetX/offsetY here made the renderer add half a screen on top, which is exactly
        // the room appearing shifted down-right by (640,360) in a 1280x720 window.
        renderer.SetCamera2D(renderer.PixelWidth * 0.5f, renderer.PixelHeight * 0.5f, 1f, 0f);
        DrawBackgrounds2D(renderer, commands, offsetX, offsetY, zoom, cx, cy);
        DrawTiles2D(renderer, commands, offsetX, offsetY, zoom);
        ObjectDrawPass.Render2D(scene.World, _projectPath, commands, renderer, offsetX, offsetY, zoom);
        RenderObjectComposition2D(scene, commands, offsetX, offsetY, zoom);
        RoomEffects2D.For(_room)?.Render(renderer, commands, _projectPath, -1, offsetX, offsetY, zoom, _drawAreaWidth, _drawAreaHeight);
    }

    private static void RenderObjectComposition2D(
        RuntimeScene scene,
        IRenderCommandSink commands,
        float offsetX,
        float offsetY,
        float zoom)
    {
        if (scene == null) return;
        foreach (ISceneSubsystem subsystem in scene.Subsystems)
            if (subsystem is ObjectCompositionSubsystem composition)
                composition.Render2D(commands, offsetX, offsetY, zoom);
    }

    public void SubmitMeshes(RuntimeScene scene, MeshDrawCall[] buffer, ref int count, IRenderController renderer)
    {
        if (IsTwoD) return;
        _renderer = renderer;
        if (!_quad.IsValid) _quad = MeshGeometry.RegisterQuad(renderer, RenderColor.White);

        Matrix4x4 viewProjection = scene.Camera3D.ViewMatrix * scene.Camera3D.ProjectionMatrix;
        Frustum frustum = new(viewProjection);
        bool cull = RenderAutoState.FrustumCulling;
        bool occlude = RenderAutoState.OcclusionCulling;

        foreach (RoomNode node in Active(RoomNodeKind.Background))
        {
            RoomTransform nodeWorld = RoomHierarchyTransforms.World(_room, node); RoomBackgroundData bg = node.Background;
            if (bg.Mode == RoomBackgroundMode.TwoD || count >= buffer.Length || !Texture(renderer, bg.Asset, out TextureHandle tex, out _, out _)) continue;
            Vector3 pos = new(nodeWorld.X, nodeWorld.Y, nodeWorld.Z); Matrix4x4 world;
            MeshDrawFlags flags = MeshDrawFlags.NoCull | MeshDrawFlags.NoShadow | MeshDrawFlags.NoDepthWrite;
            if (!bg.DepthTest && bg.Mode != RoomBackgroundMode.Sky) flags |= MeshDrawFlags.NoDepthTest;
            if (bg.Mode == RoomBackgroundMode.Sky)
            {
                pos = scene.Camera3D.Position + scene.Camera3D.Forward * 160f;
                world = Matrix4x4.CreateScale(420, 240, 1) * Matrix4x4.CreateBillboard(pos, scene.Camera3D.Position, Vector3.UnitY, scene.Camera3D.Forward);
            }
            else if (bg.Mode == RoomBackgroundMode.Billboard)
                world = Matrix4x4.CreateScale(nodeWorld.ScaleX, nodeWorld.ScaleY, 1) * Matrix4x4.CreateBillboard(pos, scene.Camera3D.Position, Vector3.UnitY, scene.Camera3D.Forward);
            else world = Matrix4x4.CreateScale(nodeWorld.ScaleX, nodeWorld.ScaleY, 1) * Matrix4x4.CreateFromYawPitchRoll(Deg(nodeWorld.RotationY), Deg(nodeWorld.RotationX), Deg(nodeWorld.RotationZ)) * Matrix4x4.CreateTranslation(pos);

            // Sky is always submitted; other backgrounds respect frustum/occlusion.
            if (bg.Mode != RoomBackgroundMode.Sky && cull)
            {
                float radius = BoundsHelper.BoundingRadiusFromScale(
                    nodeWorld.ScaleX, nodeWorld.ScaleY, nodeWorld.ScaleZ, baseRadius: 1f);
                if (!Visibility.IsVisible(frustum, viewProjection, pos, radius, occlude))
                    continue;
            }

            buffer[count++] = new MeshDrawCall { Mesh = _quad, Texture = tex, World = world, Tint = Tint(bg.TintArgb), Alpha = bg.Opacity, Flags = flags };
            RenderAutoState.SubmittedMeshes++;
        }
    }

    private void DrawBackgrounds2D(
        IRenderController renderer,
        IRenderCommandSink commands,
        float offsetX,
        float offsetY,
        float zoom,
        float cameraX,
        float cameraY)
    {
        foreach (RoomNode node in Active(RoomNodeKind.Background)
            .OrderByDescending(n => LayerDepth(n) + n.Background.Depth))
        {
            RoomTransform nodeWorld = RoomHierarchyTransforms.World(_room, node); RoomBackgroundData bg = node.Background; if (bg.Mode != RoomBackgroundMode.TwoD || !Texture(renderer, bg.Asset, out TextureHandle tex, out int iw, out int ih)) continue;
            float w = iw * nodeWorld.ScaleX, h = ih * nodeWorld.ScaleY;
            float scrollX = bg.Scroll is { Length: > 0 } ? bg.Scroll[0] * _roomTime : 0f;
            float scrollY = bg.Scroll is { Length: > 1 } ? bg.Scroll[1] * _roomTime : 0f;
            float baseX = nodeWorld.X + scrollX;
            float baseY = nodeWorld.Y + scrollY;
            if (bg.Layout == RoomBackgroundLayout.StretchRoom) { w = _room.Settings.Width; h = _room.Settings.Height; }
            else if (bg.Layout == RoomBackgroundLayout.StretchView)
            {
                w = _drawAreaWidth / Math.Max(.0001f, zoom);
                h = _drawAreaHeight / Math.Max(.0001f, zoom);
                baseX = cameraX - w * .5f;
                baseY = cameraY - h * .5f;
            }
            int rx = bg.Layout == RoomBackgroundLayout.Tile || bg.RepeatX ? Math.Max(1, (int)Math.Ceiling(_room.Settings.Width / Math.Max(1, w))) : 1;
            int ry = bg.Layout == RoomBackgroundLayout.Tile || bg.RepeatY ? Math.Max(1, (int)Math.Ceiling(_room.Settings.Height / Math.Max(1, h))) : 1;
            for (int y = 0; y < ry; y++) for (int x = 0; x < rx; x++)
            {
                SpriteDrawCall call = new()
                {
                    Texture = tex, X = offsetX + (baseX + x * w) * zoom, Y = offsetY + (baseY + y * h) * zoom,
                    Width = w * zoom, Height = h * zoom, ScaleX = 1, ScaleY = 1,
                    Rotation = nodeWorld.RotationZ, Alpha = bg.Opacity, Tint = Tint(bg.TintArgb),
                    Depth = LayerDepth(node) + bg.Depth, UvRect = new Vector4(0f, 0f, 1f, 1f),
                };
                RemapAtlas(bg.Asset, ref call);
                commands.DrawSprite(call);
            }
        }
    }

    private void DrawTiles2D(IRenderController renderer, IRenderCommandSink commands, float offsetX, float offsetY, float zoom)
    {
        foreach (RoomNode node in Active(RoomNodeKind.TileLayer))
        {
            RoomTransform nodeWorld = RoomHierarchyTransforms.World(_room, node); RoomTileLayerData layer = node.TileLayer; if (!Texture(renderer, layer.Tileset, out TextureHandle tex, out int iw, out int ih)) continue;
            Matrix4x4 matrix = RoomHierarchyTransforms.Matrix(nodeWorld);
            foreach (RoomTileCell cell in layer.Cells)
            {
                float cellScaleX = MathF.Max(0.001f, MathF.Abs(cell.ScaleX));
                float cellScaleY = MathF.Max(0.001f, MathF.Abs(cell.ScaleY));
                Vector3 position = Vector3.Transform(new Vector3(
                    (cell.X + .5f) * layer.CellWidth + cell.OffsetX,
                    (cell.Y + .5f) * layer.CellHeight + cell.OffsetY, 0), matrix);
                float px = offsetX + position.X * zoom, py = offsetY + position.Y * zoom;
                float signedWidth = layer.CellWidth * zoom * nodeWorld.ScaleX * cellScaleX;
                float signedHeight = layer.CellHeight * zoom * nodeWorld.ScaleY * cellScaleY;
                float radius = MathF.Sqrt(signedWidth * signedWidth + signedHeight * signedHeight) * .5f;
                if (px + radius < 0 || py + radius < 0 || px - radius > _drawAreaWidth || py - radius > _drawAreaHeight) continue;
                float width = signedWidth * (cell.FlipX ? -1 : 1), height = signedHeight * (cell.FlipY ? -1 : 1);
                float u0 = (layer.Margin + cell.TileX * (layer.CellWidth + layer.Separation)) / (float)iw, v0 = (layer.Margin + cell.TileY * (layer.CellHeight + layer.Separation)) / (float)ih;
                SpriteDrawCall call = new()
                {
                    Texture = tex,
                    X = px, Y = py,
                    Width = width, Height = height, OriginX = width * .5f, OriginY = height * .5f,
                    ScaleX = 1, ScaleY = 1,
                    Rotation = nodeWorld.RotationZ + cell.Rotation,
                    Alpha = 1, Tint = RenderColor.White, Depth = LayerDepth(node) + layer.Depth,
                    UvRect = new Vector4(u0, v0, Math.Min(1, u0 + layer.CellWidth / (float)iw), Math.Min(1, v0 + layer.CellHeight / (float)ih)),
                };
                RemapAtlas(layer.Tileset, ref call);
                commands.DrawSprite(call);
            }
        }
    }

    private IEnumerable<RoomNode> Active(RoomNodeKind kind) => _room.Nodes.Where(n => n.Kind == kind && RoomHierarchyTransforms.IsActive(_room, n));
    private int LayerDepth(RoomNode node) => _room.Layers.FirstOrDefault(l => l.Id == node.LayerId)?.Order ?? 0;
    /// <summary>Names already reported as unresolvable, so a per-frame failure logs once, not 60×/s.</summary>
    private readonly HashSet<string> _reportedMissing = new(StringComparer.OrdinalIgnoreCase);
    private bool _loggedCamera;

    private bool Texture(IRenderController renderer, string name, out TextureHandle handle, out int width, out int height)
    {
        width = height = 32; handle = TextureHandle.Invalid;
        string path = ResolveImage(name);
        if (path == null)
        {
            ReportMissing(name, "no image file could be resolved for it");
            return false;
        }

        if (!_textures.TryGetValue(path, out handle) || !handle.IsValid) { handle = renderer.LoadTexture(path); _textures[path] = handle; }
        if (!_imageSizes.TryGetValue(path, out var dimensions))
        {
            TryPngSize(path, out width, out height);
            dimensions = (width, height); _imageSizes[path] = dimensions;
        }
        width = dimensions.Width; height = dimensions.Height;

        if (!handle.IsValid)
        {
            ReportMissing(name, $"'{path}' resolved but the backend could not load it as a texture");
        }

        return handle.IsValid;
    }

    private void RemapAtlas(string imageName, ref SpriteDrawCall call)
    {
        string path = ResolveImage(imageName);
        if (path is null) return;
        if (RuntimeTextureAtlas.TryRemap(path, call.UvRect, out TextureHandle atlas, out Vector4 uv))
        {
            call.Texture = atlas;
            call.UvRect = uv;
        }
    }

    /// <summary>
    /// Says, once per asset, why nothing was drawn for it.
    /// </summary>
    /// <remarks>
    /// Every draw path here gates on <see cref="Texture"/> and skips the node when it fails. That
    /// used to be silent, so a room whose art did not resolve rendered as an empty background with
    /// nothing anywhere reporting a problem — which is exactly how NEXT-012 stayed open: the game
    /// looked broken and said nothing. A skipped draw is a real failure and must name itself.
    /// </remarks>
    private void ReportMissing(string name, string reason)
    {
        string key = name ?? "(null)";
        if (!_reportedMissing.Add(key)) return;
        RenderLog.Line($"[Room] Nothing will draw for image '{key}': {reason}.");
    }

    private string ResolveImage(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_resolvedImages.TryGetValue(name, out string cached)) return cached;
        string path = ResolveImageUncached(name);
        _resolvedImages[name] = path;
        return path;
    }

    private string ResolveImageUncached(string name)
    {
        if (Path.IsPathRooted(name) && File.Exists(name) && !IsDescriptor(name)) return name;

        // An Image resource is a `.image.json` descriptor, not a bitmap: its pixels live in a
        // sibling `<name>.spritedata/frames/<guid>.png`. Combining the project path with the
        // reference finds the descriptor, which exists — so this used to hand a JSON file to
        // LoadTexture, get Invalid back, and silently draw nothing. Resolve through the same
        // sprite loader the rest of the runtime uses so the editor and the game agree about what
        // an image reference means (the NEXT-041/NEXT-046 lesson: one reference, one resolver).
        string frame = TryResolveImageDescriptor(name);
        if (frame != null) return frame;

        string direct = Path.Combine(_projectPath, name.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(direct) && !IsDescriptor(direct)) return direct;

        foreach (string folder in new[] { "Backgrounds", "Sprites", "Textures" })
        {
            foreach (string ext in new[] { ".png", ".jpg", ".jpeg", ".bmp" })
            {
                string p = Path.Combine(_projectPath, folder, name + ext);
                if (File.Exists(p)) return p;
            }
        }

        return null;
    }

    private static bool IsDescriptor(string path) =>
        path.EndsWith(".image.json", StringComparison.OrdinalIgnoreCase);

    private string TryResolveImageDescriptor(string name)
    {
        try
        {
            string frame = Genesis.Runtime.Assets.SpriteAssetLoader.ResolveFrameTexturePath(_projectPath, name, 0);
            return !string.IsNullOrWhiteSpace(frame) && File.Exists(frame) ? frame : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            // A malformed or half-written descriptor should not take the frame down, but it must
            // not vanish either — the caller reports the asset as unresolvable.
            RenderLog.Line($"[Room] Image descriptor '{name}' could not be read: {exception.Message}");
            return null;
        }
    }

    private static void TryPngSize(string path, out int w, out int h)
    {
        w = h = 32;
        try
        {
            byte[] b = new byte[24];
            using FileStream stream = File.OpenRead(path);
            stream.ReadExactly(b);
            if (b[0] == 137 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G')
            {
                int actualW = ReadBe(b, 16), actualH = ReadBe(b, 20);
                if (actualW > 0 && actualH > 0) { w = actualW; h = actualH; }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Falls back to 32×32 rather than failing the frame, but says so: a wrong size silently
            // mis-scales every tile drawn from this sheet.
            RenderLog.Line($"[Room] Could not read the pixel size of '{path}' ({exception.Message}); assuming 32x32.");
        }
    }
    private static int ReadBe(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
    private static RenderColor Tint(int argb) => new(((argb >> 16) & 255) / 255f, ((argb >> 8) & 255) / 255f, (argb & 255) / 255f, Math.Clamp(((argb >> 24) & 255) / 255f, 0, 1));
    private static float Deg(float value) => value * MathF.PI / 180f;
    public void Dispose() { RoomEffects2D.For(_room)?.Dispose(); ReleaseViewportTargets(); if (_renderer != null) { if (_quad.IsValid) _renderer.ReleaseMesh(_quad); foreach (TextureHandle t in _textures.Values) if (t.IsValid) _renderer.ReleaseTexture(t); } _textures.Clear(); _resolvedImages.Clear(); _imageSizes.Clear(); }
}
