using System.IO;
using System.Numerics;
using System.ComponentModel;
using Genesis.Rendering.Viewport;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Image.Controls;

public enum ImageOverlayShapeKind
{
    None,
    Line,
    Polyline,
    Rectangle,
    Ellipse,
    Capsule,
    Polygon,
}

public sealed class ImageViewportOverlay
{
    public RectangleF BrushCursor { get; set; }
    public IReadOnlyList<(Point A, Point B)> SelectionEdges { get; set; } = [];
    public bool ShowGrid { get; set; }
    public bool ShowPixelGrid { get; set; } = true;
    public bool ShowOrigin { get; set; } = true;
    public RenderColor GridColor { get; set; } = new(0.35f, 0.55f, 1f, 0.72f);
    public bool ShowCollision { get; set; } = true;
    public bool ShowTileRepeat { get; set; }
    public bool TileRepeatX { get; set; } = true;
    public bool TileRepeatY { get; set; } = true;
    public bool ShowNineSlice { get; set; }
    public bool ShowSymmetryX { get; set; }
    public bool ShowSymmetryY { get; set; }
    public bool ShowShapePreview { get; set; }
    public ImageOverlayShapeKind ShapePreviewKind { get; set; }
    public PointF ShapePreviewStart { get; set; }
    public PointF ShapePreviewEnd { get; set; }
    public float ShapePreviewThickness { get; set; } = 1f;
    public RenderColor ShapePreviewColor { get; set; } = new(1f, 0.85f, 0.2f, 0.92f);
    public IReadOnlyList<Vector2> ShapePreviewPath { get; set; } = [];
    public int GridWidth { get; set; } = 16;
    public int GridHeight { get; set; } = 16;
    public int GridMargin { get; set; }
    public int GridSpacing { get; set; }

    /// <summary>
    /// Tile indices flagged solid, shaded over the tile grid. Tile-set collision is authored in the
    /// Image *Viewer* because that is where the sheet is inspected — you pick tiles against the grid
    /// you can see, not in a separate editor with its own copy of the geometry.
    /// </summary>
    public IReadOnlyCollection<int> SolidTiles { get; set; } = [];
    public RenderColor SolidTileColor { get; set; } = new(1f, 0.38f, 0.43f, 0.42f);
    public Vector2 Origin { get; set; }
    public ImageOverlayShapeKind CollisionKind { get; set; }
    public RectangleF CollisionBounds { get; set; }
    public IReadOnlyList<Vector2> CollisionPoints { get; set; } = [];
    public Padding NineSlice { get; set; }
    public bool ShowBones { get; set; } = true;
    public bool ShowDeformMesh { get; set; } = true;
    public IReadOnlyList<ImageBoneOverlaySegment> BoneSegments { get; set; } = [];
    public IReadOnlyList<ImageJointOverlayCircle> JointCircles { get; set; } = [];
    public IReadOnlyList<Vector2> MeshVertices { get; set; } = [];
    public IReadOnlyList<int> MeshIndices { get; set; } = [];
}

public sealed class ImageBoneOverlaySegment
{
    public Vector2 Start { get; init; }
    public Vector2 End { get; init; }
    public bool Selected { get; init; }
    public bool Affected { get; init; }
}

public sealed class ImageJointOverlayCircle
{
    public Vector2 Centre { get; init; }
    public float Radius { get; init; }
    public bool Selected { get; init; }
    public bool Preview { get; init; }
    public bool Pinned { get; init; }
}

/// <summary>
/// Ember-backed image canvas with a persistent texture, pan/zoom and authoring overlays.
/// Pixel operations live in document/session services; this control only displays and maps input.
/// </summary>
public sealed class ImageViewportControl : D3DViewportControl
{
    private byte[]? _rgbaData;
    private string? _resolvedImagePath;
    private Vector4 _frameUv;
    private TextureHandle _spriteTexture;
    private long _loadedVersion = long.MinValue;
    private int _loadedWidth;
    private int _loadedHeight;
    private string? _loadedDiskPath;

    private Point _lastMouse;
    private bool _panning;
    private float _zoom = 1f;
    private float _fitScale = 1f;
    private Vector2 _camera;
    private int _imageWidth;
    private int _imageHeight;
    private long _contentVersion;
    private bool _showCheckerboard = true;
    private bool _integerZoomOnly = true;
    private bool _pixelPerfectFiltering = true;

    public ImageViewportControl()
    {
        DoubleBuffered = false;
        BackColor = Color.FromArgb(20, 22, 29);
        Dock = DockStyle.Fill;
        DriveMode = ViewportDriveMode.Timer;
        TargetFps = 60;
        TabStop = true;

        OnRender += Render;
        HandleCreated += (_, _) => FitToView();
        MouseDown += OnViewportMouseDown;
        MouseMove += OnViewportMouseMove;
        MouseUp += OnViewportMouseUp;
        MouseWheel += OnViewportMouseWheel;
    }

    public ImageViewportOverlay Overlay { get; } = new();

    public void SetSelection(Genesis.Application.Editors.Image.Imaging.ImageSelectionMask selection)
    {
        List<(Point, Point)> edges = [];
        if (selection.HasSelection)
        {
            Rectangle bounds = selection.GetBounds();
            bool Inside(int x, int y) => x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom && selection.Contains(x,y);
            for (int y = bounds.Top; y < bounds.Bottom; y++)
                for (int x = bounds.Left; x < bounds.Right; x++)
                {
                    if (!Inside(x,y)) continue;
                    if (!Inside(x,y-1)) edges.Add((new Point(x,y),new Point(x+1,y)));
                    if (!Inside(x,y+1)) edges.Add((new Point(x,y+1),new Point(x+1,y+1)));
                    if (!Inside(x-1,y)) edges.Add((new Point(x,y),new Point(x,y+1)));
                    if (!Inside(x+1,y)) edges.Add((new Point(x+1,y),new Point(x+1,y+1)));
                }
        }
        Overlay.SelectionEdges = edges;
    }

    public float Zoom => _zoom;

    public Vector2 Camera => _camera;

    public event EventHandler? ViewChanged;

    public event EventHandler<ImageCanvasPointerEventArgs>? CanvasPointerDown;

    public event EventHandler<ImageCanvasPointerEventArgs>? CanvasPointerMove;

    public event EventHandler<ImageCanvasPointerEventArgs>? CanvasPointerUp;

    /// <summary>
    /// Tiles across the sheet for the current grid settings. Shared by the solid-tile shading and
    /// <see cref="TileIndexAt"/> so what you click is always what gets shaded.
    /// </summary>
    public int TileColumns()
    {
        int gx = Math.Max(1, Overlay.GridWidth);
        return Math.Max(0, (_imageWidth - Overlay.GridMargin + Overlay.GridSpacing) / (gx + Overlay.GridSpacing));
    }

    public int TileRows()
    {
        int gy = Math.Max(1, Overlay.GridHeight);
        return Math.Max(0, (_imageHeight - Overlay.GridMargin + Overlay.GridSpacing) / (gy + Overlay.GridSpacing));
    }

    /// <summary>
    /// Tile index under a canvas-space point, or -1 when the point falls outside the sheet or in
    /// the margin/separation gutters between tiles (clicking a gutter must not toggle a neighbour).
    /// </summary>
    public int TileIndexAt(float canvasX, float canvasY)
    {
        int gx = Math.Max(1, Overlay.GridWidth);
        int gy = Math.Max(1, Overlay.GridHeight);
        int columns = TileColumns();
        if (columns <= 0) return -1;

        float localX = canvasX - Overlay.GridMargin;
        float localY = canvasY - Overlay.GridMargin;
        if (localX < 0 || localY < 0) return -1;

        int strideX = gx + Overlay.GridSpacing;
        int strideY = gy + Overlay.GridSpacing;
        int column = (int)(localX / strideX);
        int row = (int)(localY / strideY);
        if (localX - (column * strideX) >= gx) return -1;   // in the horizontal gutter
        if (localY - (row * strideY) >= gy) return -1;       // in the vertical gutter
        if (column >= columns || row >= TileRows()) return -1;

        return (row * columns) + column;
    }

    public void SetSurface(byte[] rgba, int width, int height, long contentVersion)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            throw new ArgumentException("RGBA surface dimensions do not match its byte buffer.", nameof(rgba));

        _rgbaData = (byte[])rgba.Clone();
        _imageWidth = width;
        _imageHeight = height;
        _contentVersion = contentVersion;
    }

    public void SetOriginPixels(float pixelX, float pixelY)
    {
        Overlay.Origin = new Vector2(pixelX, pixelY);
    }

    public void SetFrameUv(Vector4 uv) => _frameUv = uv;

    public void SetCheckerboard(bool enabled) => _showCheckerboard = enabled;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IntegerZoomOnly
    {
        get => _integerZoomOnly;
        set
        {
            if (_integerZoomOnly == value) return;
            _integerZoomOnly = value;
            if (value)
                _zoom = MathF.Max(1f, MathF.Round(_zoom));
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool PixelPerfectFiltering
    {
        get => _pixelPerfectFiltering;
        set
        {
            if (_pixelPerfectFiltering == value) return;
            _pixelPerfectFiltering = value;
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetSourceImagePath(string? absolutePath) => _resolvedImagePath = absolutePath;

    public void FitToView()
    {
        if (_imageWidth <= 0 || _imageHeight <= 0) return;

        // Client size, not RenderWidth: the swap chain trails the control by up to a couple of
        // frames while a resize settles, so fitting against it computed the zoom for the pane's
        // *previous* size — and a pane that was created tiny (a docked document mid-layout) stayed
        // fitted for that tiny size afterwards.
        int viewW = Math.Max(1, ClientWidth);
        int viewH = Math.Max(1, ClientHeight);
        float fit = MathF.Min((viewW - 32f) / _imageWidth, (viewH - 32f) / _imageHeight);
        if (_integerZoomOnly)
        {
            _fitScale = 1f;
            _zoom = fit >= 1f ? MathF.Floor(fit) : Math.Clamp(fit, 0.01f, 1f);
        }
        else
        {
            _fitScale = Math.Clamp(fit, 0.01f, 64f);
            _zoom = 1f;
        }
        _camera = Vector2.Zero;
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ActualPixels()
    {
        _fitScale = 1f;
        _zoom = 1f;
        _camera = Vector2.Zero;
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public PointF ScreenToImage(Point screen)
    {
        Point renderPoint = ClientToRender(screen);
        RectangleF dest = GetScreenDestination();
        if (dest.Width <= 0f || dest.Height <= 0f)
            return PointF.Empty;
        float u = (renderPoint.X - dest.Left) / dest.Width;
        float v = (renderPoint.Y - dest.Top) / dest.Height;
        return new PointF(u * _imageWidth, v * _imageHeight);
    }

    public Point ImageToClient(PointF imagePoint)
    {
        RectangleF dest = GetScreenDestination();
        if (dest.Width <= 0f || dest.Height <= 0f || _imageWidth <= 0 || _imageHeight <= 0)
            return Point.Empty;
        float u = imagePoint.X / _imageWidth;
        float v = imagePoint.Y / _imageHeight;
        float renderX = dest.Left + u * dest.Width;
        float renderY = dest.Top + v * dest.Height;
        return RenderToClient(new PointF(renderX, renderY));
    }

    public void MarkSurfaceDirty() => _loadedVersion = long.MinValue;

    private Point ClientToRender(Point client)
    {
        float sx = RenderWidth / (float)Math.Max(1, ClientWidth);
        float sy = RenderHeight / (float)Math.Max(1, ClientHeight);
        return new Point(
            (int)MathF.Round(client.X * sx),
            (int)MathF.Round(client.Y * sy));
    }

    private Point RenderToClient(PointF render)
    {
        float sx = ClientWidth / (float)Math.Max(1, RenderWidth);
        float sy = ClientHeight / (float)Math.Max(1, RenderHeight);
        return new Point(
            (int)MathF.Round(render.X * sx),
            (int)MathF.Round(render.Y * sy));
    }

    private RectangleF GetScreenDestination()
    {
        if (_imageWidth <= 0 || _imageHeight <= 0) return RectangleF.Empty;
        int viewW = Math.Max(1, RenderWidth);
        int viewH = Math.Max(1, RenderHeight);
        float scale = _fitScale * _zoom;
        float width = _imageWidth * scale;
        float height = _imageHeight * scale;
        float x = (viewW - width) * 0.5f + _camera.X;
        float y = (viewH - height) * 0.5f + _camera.Y;
        return new RectangleF(x, y, width, height);
    }

    private void Render(IRenderController renderer)
    {
        if (!renderer.IsInitialized || _imageWidth <= 0 || _imageHeight <= 0)
            return;

        int viewW = Math.Max(1, RenderWidth);
        int viewH = Math.Max(1, RenderHeight);
        renderer.Set3DFrameActive(false);
        renderer.SetViewport(0, 0, viewW, viewH);
        renderer.Clear(0.12f, 0.12f, 0.14f);
        renderer.SetCamera2D(viewW * 0.5f, viewH * 0.5f, 1f, 0f);
        renderer.SetSamplerState(_pixelPerfectFiltering ? SamplerFilter.Point : SamplerFilter.Linear);

        RectangleF dest = GetScreenDestination();
        if (dest.Width <= 0f || dest.Height <= 0f)
            return;

        if (_showCheckerboard)
            DrawCheckerboard(renderer, dest, viewW, viewH);

        TextureHandle texture = EnsureTexture(renderer);
        if (texture.IsValid)
        {
            Vector4 uv = _frameUv;
            if (uv.Z <= uv.X || uv.W <= uv.Y)
                uv = new Vector4(0f, 0f, 1f, 1f);

            renderer.DrawSpriteBatch(new[]
            {
                new SpriteDrawCall
                {
                    Texture = texture,
                    X = dest.X,
                    Y = dest.Y,
                    Width = dest.Width,
                    Height = dest.Height,
                    OriginX = 0f,
                    OriginY = 0f,
                    Tint = RenderColor.White,
                    Alpha = 1f,
                    UvRect = uv,
                },
            });
        }

        RenderOverlays(renderer, dest);
    }

    private TextureHandle EnsureTexture(IRenderController renderer)
    {
        bool hasMemory = _rgbaData != null
            && _imageWidth > 0
            && _imageHeight > 0
            && _rgbaData.Length >= _imageWidth * _imageHeight * 4;

        if (!hasMemory)
            return EnsureDiskTexture(renderer);

        bool dimensionsChanged = _loadedWidth != _imageWidth || _loadedHeight != _imageHeight;
        if (!_spriteTexture.IsValid || dimensionsChanged)
        {
            if (_spriteTexture.IsValid)
                renderer.ReleaseTexture(_spriteTexture);

            _spriteTexture = renderer.CreateTexture(_imageWidth, _imageHeight, _rgbaData);
            if (!_spriteTexture.IsValid)
                _spriteTexture = EnsureDiskTexture(renderer);

            if (_spriteTexture.IsValid)
            {
                _loadedWidth = _imageWidth;
                _loadedHeight = _imageHeight;
                _loadedVersion = _contentVersion;
            }

            return _spriteTexture;
        }

        if (_spriteTexture.IsValid && _loadedVersion != _contentVersion)
        {
            renderer.UpdateTexture(_spriteTexture, _imageWidth, _imageHeight, _rgbaData);
            _loadedVersion = _contentVersion;
        }

        if (!_spriteTexture.IsValid)
            _spriteTexture = EnsureDiskTexture(renderer);

        return _spriteTexture;
    }

    private TextureHandle EnsureDiskTexture(IRenderController renderer)
    {
        if (string.IsNullOrWhiteSpace(_resolvedImagePath) || !File.Exists(_resolvedImagePath))
            return TextureHandle.Invalid;

        if (_loadedDiskPath != _resolvedImagePath || !_spriteTexture.IsValid)
        {
            if (_spriteTexture.IsValid)
                renderer.ReleaseTexture(_spriteTexture);
            _spriteTexture = renderer.LoadTexture(_resolvedImagePath);
            _loadedDiskPath = _resolvedImagePath;
        }

        return _spriteTexture;
    }

    /// <summary>
    /// Fills the image destination with a transparency checker. Cells are limited to the visible
    /// viewport intersection — drawing every cell of a zoomed-past-screen dest exhausts the shared
    /// sprite instance cap (~32k) and the actual image sprite is dropped from the flush.
    /// </summary>
    private static void DrawCheckerboard(IRenderController renderer, RectangleF dest, int viewW, int viewH)
    {
        const float cell = 16f;
        float visibleLeft = MathF.Max(dest.X, 0f);
        float visibleTop = MathF.Max(dest.Y, 0f);
        float visibleRight = MathF.Min(dest.Right, viewW);
        float visibleBottom = MathF.Min(dest.Bottom, viewH);
        if (visibleRight <= visibleLeft || visibleBottom <= visibleTop)
            return;

        int colStart = Math.Max(0, (int)MathF.Floor((visibleLeft - dest.X) / cell));
        int rowStart = Math.Max(0, (int)MathF.Floor((visibleTop - dest.Y) / cell));
        int colEnd = (int)MathF.Ceiling((visibleRight - dest.X) / cell);
        int rowEnd = (int)MathF.Ceiling((visibleBottom - dest.Y) / cell);

        for (int row = rowStart; row < rowEnd; row++)
        {
            for (int col = colStart; col < colEnd; col++)
            {
                float x = dest.X + col * cell;
                float y = dest.Y + row * cell;
                float width = MathF.Min(cell, dest.Right - x);
                float height = MathF.Min(cell, dest.Bottom - y);
                RenderColor color = ((col + row) & 1) == 0
                    ? new RenderColor(0.22f, 0.23f, 0.27f, 1f)
                    : new RenderColor(0.15f, 0.16f, 0.19f, 1f);
                renderer.DrawRect(x, y, width, height, color, filled: true, depth: 1000);
            }
        }
    }

    private void RenderOverlays(IRenderController renderer, RectangleF dest)
    {
        if (_imageWidth <= 0 || _imageHeight <= 0 || dest.Width <= 0f || dest.Height <= 0f) return;
        float scaleX = dest.Width / _imageWidth;
        float scaleY = dest.Height / _imageHeight;
        float left = dest.X;
        float top = dest.Y;
        RenderColor guide = Overlay.GridColor;
        RenderColor origin = new(1f, 0.35f, 0.3f, 0.95f);
        RenderColor collision = new(0.3f, 1f, 0.55f, 0.95f);

        void Ants(float x1, float y1, float x2, float y2)
        {
            float length = MathF.Max(1, MathF.Sqrt((x2-x1)*(x2-x1)+(y2-y1)*(y2-y1)));
            int phase = (int)(Environment.TickCount64 / 120 % 8);
            for (float offset = 0; offset < length; offset += 4)
            {
                float a = offset / length, b = Math.Min(offset + 4, length) / length;
                bool white = ((int)(offset + phase + x1 + y1) / 4 & 1) == 0;
                renderer.DrawLine(x1+(x2-x1)*a, y1+(y2-y1)*a, x1+(x2-x1)*b, y1+(y2-y1)*b,
                    white ? new RenderColor(1,1,1,1) : new RenderColor(0,0,0,1), 1.5f, -300);
            }
        }
        foreach (var edge in Overlay.SelectionEdges)
            Ants(left+edge.A.X*scaleX, top+edge.A.Y*scaleY, left+edge.B.X*scaleX, top+edge.B.Y*scaleY);
        RectangleF cursor = Overlay.BrushCursor;
        if (!cursor.IsEmpty)
        {
            float x = left+cursor.X*scaleX, y = top+cursor.Y*scaleY, r = x+cursor.Width*scaleX, b = y+cursor.Height*scaleY;
            Ants(x,y,r,y); Ants(r,y,r,b); Ants(r,b,x,b); Ants(x,b,x,y);
        }

        if (Overlay.ShowGrid)
        {
            int gx = Math.Max(1, Overlay.GridWidth);
            int gy = Math.Max(1, Overlay.GridHeight);

            // Shade solid tiles under the grid lines so the lines stay readable on top of them.
            if (Overlay.SolidTiles.Count > 0)
            {
                int columns = TileColumns();
                foreach (int index in Overlay.SolidTiles)
                {
                    if (index < 0 || columns <= 0)
                    {
                        continue;
                    }

                    int column = index % columns;
                    int row = index / columns;
                    float tx = Overlay.GridMargin + column * (gx + Overlay.GridSpacing);
                    float ty = Overlay.GridMargin + row * (gy + Overlay.GridSpacing);
                    if (tx >= _imageWidth || ty >= _imageHeight)
                    {
                        continue;
                    }

                    renderer.DrawRect(
                        left + tx * scaleX,
                        top + ty * scaleY,
                        gx * scaleX,
                        gy * scaleY,
                        Overlay.SolidTileColor,
                        filled: true,
                        depth: -99);
                }
            }

            for (int x = Overlay.GridMargin; x <= _imageWidth; x += gx + Overlay.GridSpacing)
                renderer.DrawLine(left + x * scaleX, top, left + x * scaleX, top + dest.Height, guide, 1f, -100);
            for (int y = Overlay.GridMargin; y <= _imageHeight; y += gy + Overlay.GridSpacing)
                renderer.DrawLine(left, top + y * scaleY, left + dest.Width, top + y * scaleY, guide, 1f, -100);
        }

        if (Overlay.ShowPixelGrid && _fitScale * _zoom >= 8f)
        {
            // Same cap risk as the checkerboard: only emit lines that cross the live viewport.
            RenderColor pixel = new(0.65f, 0.68f, 0.75f, 0.26f);
            int viewW = Math.Max(1, RenderWidth);
            int viewH = Math.Max(1, RenderHeight);
            float clipLeft = MathF.Max(left, 0f);
            float clipTop = MathF.Max(top, 0f);
            float clipRight = MathF.Min(left + dest.Width, viewW);
            float clipBottom = MathF.Min(top + dest.Height, viewH);
            if (clipRight > clipLeft && clipBottom > clipTop && scaleX > 0f && scaleY > 0f)
            {
                int x0 = Math.Max(0, (int)MathF.Floor((clipLeft - left) / scaleX));
                int x1 = Math.Min(_imageWidth, (int)MathF.Ceiling((clipRight - left) / scaleX));
                int y0 = Math.Max(0, (int)MathF.Floor((clipTop - top) / scaleY));
                int y1 = Math.Min(_imageHeight, (int)MathF.Ceiling((clipBottom - top) / scaleY));
                for (int x = x0; x <= x1; x++)
                {
                    float sx = left + x * scaleX;
                    renderer.DrawLine(sx, clipTop, sx, clipBottom, pixel, 0.5f, -90);
                }

                for (int y = y0; y <= y1; y++)
                {
                    float sy = top + y * scaleY;
                    renderer.DrawLine(clipLeft, sy, clipRight, sy, pixel, 0.5f, -90);
                }
            }
        }

        if (Overlay.ShowOrigin)
        {
            float ox = left + Overlay.Origin.X * scaleX;
            float oy = top + Overlay.Origin.Y * scaleY;
            renderer.DrawLine(ox - 8f, oy, ox + 8f, oy, origin, 2f, -200);
            renderer.DrawLine(ox, oy - 8f, ox, oy + 8f, origin, 2f, -200);
        }

        if (Overlay.ShowCollision && Overlay.CollisionKind != ImageOverlayShapeKind.None)
        {
            RectangleF b = Overlay.CollisionBounds;
            float x = left + b.X * scaleX;
            float y = top + b.Y * scaleY;
            if (Overlay.CollisionKind == ImageOverlayShapeKind.Polygon && Overlay.CollisionPoints.Count > 1)
            {
                for (int i = 0; i < Overlay.CollisionPoints.Count; i++)
                {
                    Vector2 a = Overlay.CollisionPoints[i];
                    Vector2 c = Overlay.CollisionPoints[(i + 1) % Overlay.CollisionPoints.Count];
                    renderer.DrawLine(left + a.X * scaleX, top + a.Y * scaleY, left + c.X * scaleX, top + c.Y * scaleY, collision, 2f, -200);
                }
            }
            else
            {
                renderer.DrawRect(x, y, b.Width * scaleX, b.Height * scaleY, collision, filled: false, depth: -200);
            }
        }

        if (Overlay.ShowSymmetryX)
            renderer.DrawLine(left + dest.Width * 0.5f, top, left + dest.Width * 0.5f, top + dest.Height, guide, 1f, -150);
        if (Overlay.ShowSymmetryY)
            renderer.DrawLine(left, top + dest.Height * 0.5f, left + dest.Width, top + dest.Height * 0.5f, guide, 1f, -150);

        if (Overlay.ShowTileRepeat)
        {
            RenderColor repeat = new(0.45f, 0.62f, 1f, 0.55f);
            if (Overlay.TileRepeatX)
            {
                renderer.DrawRect(left - dest.Width, top, dest.Width, dest.Height, repeat, filled: false, depth: -80);
                renderer.DrawRect(left + dest.Width, top, dest.Width, dest.Height, repeat, filled: false, depth: -80);
            }
            if (Overlay.TileRepeatY)
            {
                renderer.DrawRect(left, top - dest.Height, dest.Width, dest.Height, repeat, filled: false, depth: -80);
                renderer.DrawRect(left, top + dest.Height, dest.Width, dest.Height, repeat, filled: false, depth: -80);
            }
            if (Overlay.TileRepeatX && Overlay.TileRepeatY)
            {
                renderer.DrawRect(left - dest.Width, top - dest.Height, dest.Width, dest.Height, repeat, filled: false, depth: -80);
                renderer.DrawRect(left + dest.Width, top - dest.Height, dest.Width, dest.Height, repeat, filled: false, depth: -80);
                renderer.DrawRect(left - dest.Width, top + dest.Height, dest.Width, dest.Height, repeat, filled: false, depth: -80);
                renderer.DrawRect(left + dest.Width, top + dest.Height, dest.Width, dest.Height, repeat, filled: false, depth: -80);
            }
        }

        if (Overlay.ShowShapePreview)
        {
            RenderColor preview = Overlay.ShapePreviewColor;
            float previewX0 = left + (Overlay.ShapePreviewStart.X + 0.5f) * scaleX;
            float previewY0 = top + (Overlay.ShapePreviewStart.Y + 0.5f) * scaleY;
            float previewX1 = left + (Overlay.ShapePreviewEnd.X + 0.5f) * scaleX;
            float previewY1 = top + (Overlay.ShapePreviewEnd.Y + 0.5f) * scaleY;
            float thickness = MathF.Max(1f, Overlay.ShapePreviewThickness * MathF.Max(scaleX, scaleY));
            switch (Overlay.ShapePreviewKind)
            {
                case ImageOverlayShapeKind.Rectangle:
                {
                    float rx = MathF.Min(previewX0, previewX1) - scaleX * 0.5f;
                    float ry = MathF.Min(previewY0, previewY1) - scaleY * 0.5f;
                    float rw = MathF.Abs(previewX1 - previewX0) + scaleX;
                    float rh = MathF.Abs(previewY1 - previewY0) + scaleY;
                    renderer.DrawRect(rx, ry, rw, rh, preview, filled: false, depth: -210);
                    break;
                }
                case ImageOverlayShapeKind.Ellipse:
                {
                    float rx = MathF.Min(previewX0, previewX1) - scaleX * 0.5f;
                    float ry = MathF.Min(previewY0, previewY1) - scaleY * 0.5f;
                    float rw = MathF.Abs(previewX1 - previewX0) + scaleX;
                    float rh = MathF.Abs(previewY1 - previewY0) + scaleY;
                    renderer.DrawRect(rx, ry, rw, rh, preview, filled: false, depth: -210);
                    break;
                }
                case ImageOverlayShapeKind.Polyline when Overlay.ShapePreviewPath.Count > 1:
                {
                    for (int i = 0; i < Overlay.ShapePreviewPath.Count - 1; i++)
                    {
                        Vector2 a = Overlay.ShapePreviewPath[i];
                        Vector2 b = Overlay.ShapePreviewPath[i + 1];
                        renderer.DrawLine(
                            left + (a.X + 0.5f) * scaleX, top + (a.Y + 0.5f) * scaleY,
                            left + (b.X + 0.5f) * scaleX, top + (b.Y + 0.5f) * scaleY,
                            preview, thickness, -210);
                    }
                    break;
                }
                case ImageOverlayShapeKind.Line:
                default:
                    renderer.DrawLine(previewX0, previewY0, previewX1, previewY1, preview, thickness, -210);
                    break;
            }
        }

        if (Overlay.ShowNineSlice)
        {
            RenderColor sliceGuide = new(0.2f,0.65f,1f,1f);
            foreach (float x in new[] { left+Overlay.NineSlice.Left*scaleX, left+(_imageWidth-Overlay.NineSlice.Right)*scaleX })
                renderer.DrawLine(x,top,x,top+_imageHeight*scaleY,sliceGuide,2f,-218);
            foreach (float y in new[] { top+Overlay.NineSlice.Top*scaleY, top+(_imageHeight-Overlay.NineSlice.Bottom)*scaleY })
                renderer.DrawLine(left,y,left+_imageWidth*scaleX,y,sliceGuide,2f,-218);
        }

        if (Overlay.ShowBones)
        {
            RenderColor boneColor = new(0.2f, 1f, 0.45f, 0.95f);
            RenderColor selectedColor = new(1f, 0.85f, 0.2f, 0.98f);
            foreach (ImageBoneOverlaySegment segment in Overlay.BoneSegments)
            {
                RenderColor color = segment.Selected ? selectedColor : segment.Affected ? new RenderColor(.3f,.8f,1f,1f) : boneColor;
                float x0 = left + (segment.Start.X + 0.5f) * scaleX;
                float y0 = top + (segment.Start.Y + 0.5f) * scaleY;
                float x1 = left + (segment.End.X + 0.5f) * scaleX;
                float y1 = top + (segment.End.Y + 0.5f) * scaleY;
                renderer.DrawLine(x0, y0, x1, y1, color, 2.5f, -220);
                renderer.DrawRect(x0 - 3f, y0 - 3f, 6f, 6f, color, filled: true, depth: -221);
                renderer.DrawRect(x1 - 3f, y1 - 3f, 6f, 6f, color, filled: true, depth: -220);
                renderer.DrawRect((x0+x1)/2-4f, (y0+y1)/2-4f, 8f, 8f, color, filled: false, depth: -221);
            }
            foreach (ImageJointOverlayCircle joint in Overlay.JointCircles)
            {
                RenderColor color = joint.Selected ? selectedColor : joint.Pinned ? new RenderColor(1f,.5f,.25f,1f) : new RenderColor(0.25f, 0.72f, 1f, 0.98f);
                if (joint.Preview) color = new RenderColor(color.R, color.G, color.B, 0.72f);
                float cx = left + (joint.Centre.X + 0.5f) * scaleX;
                float cy = top + (joint.Centre.Y + 0.5f) * scaleY;
                float rx = Math.Max(2f, joint.Radius * scaleX);
                float ry = Math.Max(2f, joint.Radius * scaleY);
                const int steps = 32;
                for (int index = 0; index < steps; index++)
                {
                    float a = index * MathF.Tau / steps, b = (index + 1) * MathF.Tau / steps;
                    renderer.DrawLine(cx + MathF.Cos(a) * rx, cy + MathF.Sin(a) * ry,
                        cx + MathF.Cos(b) * rx, cy + MathF.Sin(b) * ry, color, 2f, -223);
                }
                renderer.DrawLine(cx - 4f, cy, cx + 4f, cy, color, 1.5f, -224);
                renderer.DrawLine(cx, cy - 4f, cx, cy + 4f, color, 1.5f, -224);
                if(joint.Pinned) renderer.DrawRect(cx-5,cy-5,10,10,color,filled:false,depth:-225);
            }
        }

        if (Overlay.ShowDeformMesh && Overlay.MeshVertices.Count > 0)
        {
            RenderColor meshColor = new(0.55f, 0.75f, 1f, 0.8f);
            IReadOnlyList<int> indices = Overlay.MeshIndices;
            if (indices.Count >= 3)
            {
                for (int i = 0; i + 2 < indices.Count; i += 3)
                {
                    Vector2 a = Overlay.MeshVertices[indices[i]];
                    Vector2 b = Overlay.MeshVertices[indices[i + 1]];
                    Vector2 c = Overlay.MeshVertices[indices[i + 2]];
                    renderer.DrawLine(left + a.X * scaleX, top + a.Y * scaleY, left + b.X * scaleX, top + b.Y * scaleY, meshColor, 1f, -215);
                    renderer.DrawLine(left + b.X * scaleX, top + b.Y * scaleY, left + c.X * scaleX, top + c.Y * scaleY, meshColor, 1f, -215);
                    renderer.DrawLine(left + c.X * scaleX, top + c.Y * scaleY, left + a.X * scaleX, top + a.Y * scaleY, meshColor, 1f, -215);
                }
            }
            else
            {
                foreach (Vector2 vertex in Overlay.MeshVertices)
                    renderer.DrawRect(left + vertex.X * scaleX - 1f, top + vertex.Y * scaleY - 1f, 2f, 2f, meshColor, filled: true, depth: -215);
            }
        }
    }

    private void OnViewportMouseDown(object? sender, MouseEventArgs e)
    {
        Focus();
        Capture = true;
        _lastMouse = e.Location;
        _panning = e.Button == MouseButtons.Middle;
        if (_panning)
            Cursor = Cursors.NoMove2D;
        if (!_panning)
            CanvasPointerDown?.Invoke(this, CreatePointerEvent(e));
    }

    private void OnViewportMouseMove(object? sender, MouseEventArgs e)
    {
        if (_panning)
        {
            PanByClientDelta(e.X - _lastMouse.X, e.Y - _lastMouse.Y);
            _lastMouse = e.Location;
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        CanvasPointerMove?.Invoke(this, CreatePointerEvent(e));
    }

    private void OnViewportMouseUp(object? sender, MouseEventArgs e)
    {
        Capture = false;
        if (_panning)
        {
            _panning = false;
            Cursor = Cursors.Default;
            return;
        }
        CanvasPointerUp?.Invoke(this, CreatePointerEvent(e));
    }

    private void OnViewportMouseWheel(object? sender, MouseEventArgs e)
    {
        ZoomAt(e.Location, e.Delta > 0);
    }

    public void PanByClientDelta(float x, float y)
    {
        _camera.X += x * RenderWidth / Math.Max(1, ClientWidth);
        _camera.Y += y * RenderHeight / Math.Max(1, ClientHeight);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ZoomAt(Point clientPoint, bool zoomIn)
    {
        PointF anchor = ScreenToImage(clientPoint);
        Point renderPoint = ClientToRender(clientPoint);
        if (_integerZoomOnly)
        {
            _zoom = _zoom < 1f
                ? Math.Clamp(zoomIn ? _zoom * 2f : _zoom / 2f, 0.01f, 1f)
                : Math.Clamp(zoomIn ? MathF.Floor(_zoom) + 1 : (_zoom <= 1 ? 0.5f : MathF.Ceiling(_zoom) - 1), 0.01f, 64f);
        }
        else
        {
            float factor = zoomIn ? 1.2f : 1f / 1.2f;
            _zoom = Math.Clamp(_zoom * factor, 0.05f, 64f);
        }
        RectangleF destination = GetScreenDestination();
        if (_imageWidth > 0 && _imageHeight > 0)
        {
            _camera.X += renderPoint.X - (destination.Left + anchor.X / _imageWidth * destination.Width);
            _camera.Y += renderPoint.Y - (destination.Top + anchor.Y / _imageHeight * destination.Height);
        }
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private ImageCanvasPointerEventArgs CreatePointerEvent(MouseEventArgs e) =>
        new(ScreenToImage(e.Location), e.Button, ModifierKeys, _zoom);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            OnRender -= Render;
            IRenderController? renderer = Renderer;
            if (renderer != null && _spriteTexture.IsValid)
                renderer.ReleaseTexture(_spriteTexture);
        }
        base.Dispose(disposing);
    }
}

public sealed class ImageCanvasPointerEventArgs(
    PointF imagePoint,
    MouseButtons button,
    Keys modifiers,
    float zoom) : EventArgs
{
    public PointF ImagePoint { get; } = imagePoint;
    public MouseButtons Button { get; } = button;
    public Keys Modifiers { get; } = modifiers;
    public float Zoom { get; } = zoom;
}
