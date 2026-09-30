using System.Numerics;
using Genesis.Application.Core.Resources;
using Genesis.Rendering.Meshes;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Assets;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Navigation;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>
/// Interactive 60 Hz pathing view. Room terrain and model-backed Objects use the same runtime
/// renderers as Room/F5; navigation guides remain an editor-only overlay.
/// </summary>
internal sealed class PathingViewportControl : UserControl
{
    private sealed record SceneVisual(
        string Model,
        string Material,
        string Animation,
        float AnimationFps,
        bool Loop,
        Matrix4x4 World,
        Vector3 Scale,
        RenderColor Tint);

    private readonly string _projectRoot;
    private readonly EditorViewport3D _viewport;
    private readonly RuntimeModelRenderSystem _models = new(
        assetFreshnessIntervalMilliseconds: 500,
        textureFreshnessIntervalMilliseconds: 1000);
    private readonly List<SceneVisual> _roomVisuals = [];
    private PathingAsset _asset = new();
    private RoomAsset? _room;
    private NavMeshData? _navMesh;
    private PathingPreviewSimulation _simulation = new();
    private RoomTerrainSubsystem? _terrain;
    private SceneVisual? _agentVisual;
    private MeshDrawCall[] _terrainDraws = [];
    private MeshHandle _unitCube;
    private IRenderController? _renderer;
    private int _dragWaypoint = -1;
    private int _selectedWaypoint = -1;
    private float _time;
    private bool _hasFramed;
    private string _boundObject = string.Empty;
    private string _agentSprite = string.Empty;
    private long _objectStamp;
    private SpriteRuntimeAsset? _spriteAsset;
    private SpriteComponent _spritePlayback;
    private float _spriteTime;
    private string _spriteState = string.Empty;
    private bool _frameQueued;
    private int _frameWidth;
    private int _frameHeight;
    private SkiaSharp.SKTypeface? _labelTypeface;
    private SkiaSharp.SKFont? _labelFont;

    public PathingViewportControl(string projectRoot)
    {
        _projectRoot = projectRoot;
        Dock = DockStyle.Fill;
        BackColor = EditorChrome.Canvas;
        TabStop = true;
        _viewport = new EditorViewport3D
        {
            Dock = DockStyle.Fill,
            FloorStyle = EditorFloorStyle.GridOnly,
            MiddleButtonPans = true,
            ControlMethod = EditorCameraControlMethod.Orbit,
            SceneStateFactory = () => EditorSceneLighting.Create(showFloor: true, farPlane: 5000f),
        };
        _viewport.Camera.Target = new Vector3(0f, 1.2f, 0f);
        _viewport.Camera.Distance = 26f;
        _viewport.Camera.MaximumDistance = 5000f;
        _viewport.FarPlane = 5000f;
        _viewport.DrawScene += DrawScene;
        _viewport.DrawScene2D += DrawScene2D;
        _viewport.DrawOverlay += DrawOverlay;
        _viewport.Host.MouseDown += PointerDown;
        _viewport.Host.MouseMove += PointerMove;
        _viewport.Host.MouseUp += PointerUp;
        _viewport.Host.KeyDown += KeyPressed;
        _viewport.Host.SizeChanged += (_, _) => QueueFrameContent();
        Controls.Add(_viewport);
        Disposed += (_, _) => ReleaseRuntimeResources();
    }

    public event Action<int, Vector3>? WaypointMoved;
    public event Action<int>? WaypointSelected;
    public event Action<int>? WaypointDeleteRequested;
    public event Action? WaypointDragStarted;
    public event Action? WaypointDragCompleted;

    public void Bind(PathingAsset asset, RoomAsset? room, NavMeshData? navMesh, PathingPreviewSimulation simulation)
    {
        bool dimensionChanged = _viewport.Mode2D != (asset.Dimension == PathingDimension.TwoD);
        bool contextChanged = dimensionChanged || !ReferenceEquals(_room, room) || !ReferenceEquals(_navMesh, navMesh)
            || !string.Equals(_boundObject, asset.TargetObject, StringComparison.OrdinalIgnoreCase);
        _asset = asset;
        _room = room;
        _navMesh = navMesh;
        _simulation = simulation;
        _boundObject = asset.TargetObject;
        _viewport.Mode2D = asset.Dimension == PathingDimension.TwoD;
        if (contextChanged)
        {
            RebuildContext();
            FrameContent();
            QueueFrameContent();
        }
        else if (!_hasFramed)
        {
            FrameContent();
        }
        _viewport.Host.Invalidate();
    }

    public void SetTime(float time)
    {
        _time = MathF.Max(0f, time);
        _viewport.Host.Invalidate();
    }

    public void RefreshContext()
    {
        RebuildContext();
        _viewport.Invalidate(true);
    }

    public void FrameContent()
    {
        List<Vector3> points = RouteSamples(_asset.Route.Waypoints).ToList();
        if (_asset.Dimension == PathingDimension.TwoD)
        {
            FrameContent2D(_viewport.SurfaceWidth, _viewport.SurfaceHeight);
            _hasFramed = true;
            _viewport.Invalidate(true);
            return;
        }
        if (_navMesh is not null)
        {
            points.Add(new Vector3(_navMesh.OriginX, 0, _navMesh.OriginZ));
            points.Add(new Vector3(_navMesh.OriginX + _navMesh.Width * _navMesh.CellSize, 0,
                _navMesh.OriginZ + _navMesh.Depth * _navMesh.CellSize));
        }
        if (_room is not null)
        {
            float width = Math.Max(1, _room.Settings.Width);
            float depth = Math.Max(1, _room.Settings.Depth > 0 ? _room.Settings.Depth : _room.Settings.Height);
            points.Add(new Vector3(-width * .5f, 0, -depth * .5f));
            points.Add(new Vector3(width * .5f, 0, depth * .5f));
        }
        if (points.Count == 0) points.AddRange([new Vector3(-8, 0, -8), new Vector3(8, 0, 8)]);
        Vector3 min = points.Aggregate(Vector3.Min);
        Vector3 max = points.Aggregate(Vector3.Max);
        Vector3 center = (min + max) * .5f;
        float span = MathF.Max(8f, MathF.Max(max.X - min.X, max.Z - min.Z));
        _viewport.Camera.Target = center;
        _viewport.Camera.Distance = Math.Clamp(span * 1.35f, 10f, 4500f);
        _hasFramed = true;
        _viewport.Host.Invalidate();
    }

    private void FrameContent2D(int width, int height)
    {
        List<Vector3> points = RouteSamples(_asset.Route.Waypoints).ToList();
        if (points.Count == 0) points.AddRange([new Vector3(-8, -8, 0), new Vector3(8, 8, 0)]);
        Vector3 lower = points.Aggregate(Vector3.Min), upper = points.Aggregate(Vector3.Max);
        if (!string.IsNullOrWhiteSpace(_agentSprite))
        {
            try
            {
                SpriteRuntimeAsset sprite = SpriteAssetLoader.Load(_projectRoot, _agentSprite);
                (float x, float y) = SpriteOriginUtility.ResolvePixels(sprite.Origin, sprite.Canvas.Width, sprite.Canvas.Height);
                lower -= new Vector3(x, y, 0);
                upper += new Vector3(sprite.Canvas.Width - x, sprite.Canvas.Height - y, 0);
            }
            catch (Exception exception) when (IsAssetFailure(exception)) { }
        }
        _viewport.Camera2DX = (lower.X + upper.X) / 2;
        _viewport.Camera2DY = (lower.Y + upper.Y) / 2;
        _viewport.Zoom2D = Math.Clamp(Math.Min(width * .7f / Math.Max(8, upper.X - lower.X),
            height * .7f / Math.Max(8, upper.Y - lower.Y)), .02f, 500);
        _frameWidth = width; _frameHeight = height;
    }

    private void QueueFrameContent()
    {
        if (_frameQueued || !IsHandleCreated || IsDisposed) return;
        _frameQueued = true;
        BeginInvoke((Action)(() => { _frameQueued = false; if (!IsDisposed) FrameContent(); }));
    }

    private void RebuildContext()
    {
        _objectStamp = 0; _agentSprite = string.Empty; _spriteAsset = null;
        RefreshSpriteReference();
        _roomVisuals.Clear();
        _terrain?.Dispose();
        _terrain = null;
        _models.InvalidateAssets(_renderer);
        if (_room is not null)
        {
            try { _terrain = new RoomTerrainSubsystem(_projectRoot, _room, null!); }
            catch (Exception exception) when (IsAssetFailure(exception)) { _terrain = null; }
            foreach (RoomNode node in _room.Nodes.Where(node => node.Enabled && node.Kind == RoomNodeKind.GameObject))
            {
                SceneVisual visual = VisualForNode(node);
                _roomVisuals.Add(visual);
            }
        }
        _agentVisual = VisualForObjectReference(_asset.TargetObject, Matrix4x4.Identity);
    }

    private void DrawScene(IRenderController renderer)
    {
        UseRenderer(renderer);
        if (!_unitCube.IsValid) _unitCube = MeshGeometry.RegisterCube(renderer, RenderColor.White, 1f);
        _models.BeginFrame();
        try
        {
            DrawTerrain(renderer);
            foreach (SceneVisual visual in _roomVisuals) DrawVisual(renderer, visual, visual.World, _time);
            foreach (PathingPreviewAgent agent in _simulation.Agents)
            {
                Matrix4x4 world = AgentWorld(agent);
                if (_agentVisual is not null)
                    DrawVisual(renderer, _agentVisual, world, _time, _asset.Route.AnimationState);
                else
                    renderer.DrawMesh(new MeshDrawCall
                    {
                        Mesh = _unitCube,
                        World = Matrix4x4.CreateScale(.45f, .9f, .45f) * Matrix4x4.CreateTranslation(0, .45f, 0) * world,
                        Tint = new RenderColor(.82f, .9f, .94f),
                        Alpha = 1f,
                        Flags = MeshDrawFlags.NoCull,
                    });
            }
        }
        finally { _models.EndFrame(); }
    }

    private void DrawTerrain(IRenderController renderer)
    {
        if (_terrain is null) return;
        try
        {
            int capacity = _terrain.GetMeshDrawCapacity(renderer);
            if (capacity <= 0) return;
            if (_terrainDraws.Length < capacity) _terrainDraws = new MeshDrawCall[capacity];
            int count = 0;
            _terrain.SubmitPreviewMeshes(_viewport.Camera.Eye,
                _viewport.ViewMatrix * _viewport.ProjectionMatrix, _terrainDraws, ref count, renderer);
            for (int index = 0; index < count; index++) renderer.DrawMesh(_terrainDraws[index]);
        }
        catch (Exception exception) when (IsAssetFailure(exception))
        {
            _terrain.Dispose();
            _terrain = null;
        }
    }

    private void DrawVisual(IRenderController renderer, SceneVisual visual, Matrix4x4 placement, float time, string? clipOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(visual.Model))
        {
            string clip = string.IsNullOrWhiteSpace(clipOverride) ? visual.Animation : clipOverride;
            Matrix4x4 world = Matrix4x4.CreateScale(visual.Scale) * placement;
            try
            {
                if (_models.DrawModel(
                    _projectRoot,
                    visual.Model,
                    visual.Material,
                    world,
                    new Draw3DComponent { Visible = true, CastShadows = true, ReceiveShadows = true },
                    new ModelRendererComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1, CastShadows = true, ReceiveShadows = true },
                    new RuntimeModelAnimationState(clip, time, visual.AnimationFps, visual.Loop),
                    renderer)) return;
            }
            catch (Exception exception) when (IsAssetFailure(exception)) { }
        }
        renderer.DrawMesh(new MeshDrawCall
        {
            Mesh = _unitCube,
            World = Matrix4x4.CreateScale(.75f) * Matrix4x4.CreateTranslation(0, .375f, 0) * placement,
            Tint = visual.Tint,
            Alpha = 1f,
            Flags = MeshDrawFlags.NoCull,
        });
    }

    private Matrix4x4 AgentWorld(PathingPreviewAgent agent)
    {
        float yaw = agent.Velocity.LengthSquared() > 1e-5f ? MathF.Atan2(agent.Velocity.X, agent.Velocity.Z) : 0f;
        return Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(agent.Position);
    }

    private void DrawOverlay(IRenderController renderer)
    {
        if (_asset.Dimension == PathingDimension.TwoD) return;
        DrawNavMesh(renderer);
        DrawRoute(renderer);
        DrawAgents(renderer);
        float scale = OverlayScale;
        renderer.DrawRect(10 * scale, 8 * scale, MathF.Min(390 * scale, MathF.Max(1, _viewport.SurfaceWidth - 20 * scale)), 52 * scale,
            new RenderColor(.07f, .08f, .11f, .92f), true, -9002);
        renderer.DrawText("PATH PREVIEW · fixed 60 Hz simulation", 16 * scale, 14 * scale, 11 * scale, RenderColor.White);
        string room = _room is null ? "No room selected" : _room.Name;
        renderer.DrawText($"{room} · {_simulation.Agents.Count} simulated agent(s)", 16 * scale, 34 * scale, 10 * scale,
            new RenderColor(.62f, .7f, .76f));
        if (_simulation.CollisionWarning)
        {
            renderer.DrawRect(12, _viewport.SurfaceHeight - 44, 330, 30, new RenderColor(.72f, .12f, .16f, .92f), true, -9002);
            renderer.DrawText("Path intersects un-walkable geometry", 22, _viewport.SurfaceHeight - 37, 10, RenderColor.White);
        }
    }

    private void DrawNavMesh(IRenderController renderer)
    {
        if (_navMesh is null) return;
        int stride = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(_navMesh.Count / 3500f)));
        RenderColor walkable = new(.08f, .82f, .9f, .32f);
        RenderColor blocked = new(.96f, .24f, .3f, .22f);
        for (int z = 0; z < _navMesh.Depth; z += stride)
        for (int x = 0; x < _navMesh.Width; x += stride)
        {
            int cell = z * _navMesh.Width + x;
            Vector3 center = _navMesh.Center(cell) + Vector3.UnitY * .025f;
            float half = _navMesh.CellSize * stride * .5f;
            Vector3[] corners =
            [
                center + new Vector3(-half, 0, -half), center + new Vector3(half, 0, -half),
                center + new Vector3(half, 0, half), center + new Vector3(-half, 0, half),
            ];
            RenderColor color = _navMesh.Walkable[cell] ? walkable : blocked;
            for (int edge = 0; edge < 4; edge++) DrawWorldLine(renderer, corners[edge], corners[(edge + 1) % 4], color, 1f);
        }
    }

    private void DrawRoute(IRenderController renderer)
    {
        IReadOnlyList<PathingWaypoint> waypoints = _asset.Route.Waypoints;
        if (waypoints.Count > 1)
        {
            IReadOnlyList<Vector3> samples = RouteSamples(waypoints);
            for (int index = 1; index < samples.Count; index++)
            {
                DrawWorldLine(renderer, samples[index - 1], samples[index], new RenderColor(.08f, .8f, .92f, .3f), 6f);
                DrawWorldLine(renderer, samples[index - 1], samples[index], new RenderColor(.08f, .9f, 1f), 2.2f);
            }
        }
        for (int index = 0; index < waypoints.Count; index++)
        {
            Vector3 screen = _viewport.WorldToSurface(waypoints[index].Position + Vector3.UnitY * .08f);
            if (!IsScreenVisible(screen)) continue;
            RenderColor color = index == _selectedWaypoint ? new RenderColor(1f, .72f, .16f) : new RenderColor(.08f, .9f, 1f);
            float scale = OverlayScale;
            EditorTransformGizmo.DrawCircle(renderer, new Vector2(screen.X, screen.Y), 10 * scale, new RenderColor(.05f, .15f, .2f), color);
            renderer.DrawText((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), screen.X - 3.5f * scale, screen.Y - 6 * scale, 9 * scale, RenderColor.White);
        }
    }

    private void DrawAgents(IRenderController renderer)
    {
        foreach (PathingPreviewAgent agent in _simulation.Agents)
        {
            DrawWorldRing(renderer, agent.Position + Vector3.UnitY * .035f, .45f, new RenderColor(.2f, 1f, .4f, .75f));
            DrawWorldVector(renderer, agent.Position + Vector3.UnitY * .12f, agent.Velocity * .35f,
                new RenderColor(.08f, .9f, 1f), 2.3f);
            DrawWorldVector(renderer, agent.Position + Vector3.UnitY * .16f, agent.Steering,
                new RenderColor(1f, .2f, .22f), 1.6f);
            Vector3 screen = _viewport.WorldToSurface(agent.Position + Vector3.UnitY * 1.65f);
            if (IsScreenVisible(screen))
            {
                float scale = OverlayScale;
                _labelTypeface ??= SkiaSharp.SKTypeface.FromFamilyName("Segoe UI");
                _labelFont ??= new SkiaSharp.SKFont(_labelTypeface);
                _labelFont.Size = 9 * scale;
                float available = MathF.Max(1, _viewport.SurfaceWidth - 12 * scale);
                string label = agent.Name;
                int[] characters = System.Globalization.StringInfo.ParseCombiningCharacters(label);
                int count = characters.Length;
                while (count > 1 && _labelFont.MeasureText(label) > available)
                    label = agent.Name[..characters[--count]] + "…";
                float width = _labelFont.MeasureText(label);
                float x = Math.Clamp(screen.X + 7 * scale, 6 * scale,
                    MathF.Max(6 * scale, _viewport.SurfaceWidth - width - 6 * scale));
                float y = Math.Clamp(screen.Y, 6 * scale, MathF.Max(6 * scale, _viewport.SurfaceHeight - 14 * scale));
                renderer.DrawText(label, x + scale, y + scale, 9 * scale, RenderColor.Black);
                renderer.DrawText(label, x, y, 9 * scale, RenderColor.White);
            }
        }
    }

    private void DrawWorldVector(IRenderController renderer, Vector3 origin, Vector3 vector, RenderColor color, float thickness)
    {
        if (vector.LengthSquared() < 1e-5f) return;
        DrawWorldLine(renderer, origin, origin + vector, color, thickness);
    }

    private void DrawWorldRing(IRenderController renderer, Vector3 center, float radius, RenderColor color)
    {
        const int segments = 24;
        for (int index = 0; index < segments; index++)
        {
            float a = index * MathF.Tau / segments;
            float b = (index + 1) * MathF.Tau / segments;
            DrawWorldLine(renderer,
                center + new Vector3(MathF.Cos(a) * radius, 0, MathF.Sin(a) * radius),
                center + new Vector3(MathF.Cos(b) * radius, 0, MathF.Sin(b) * radius), color, 1.5f);
        }
    }

    private void DrawWorldLine(IRenderController renderer, Vector3 a, Vector3 b, RenderColor color, float thickness)
    {
        Vector3 sa = _viewport.WorldToSurface(a);
        Vector3 sb = _viewport.WorldToSurface(b);
        if (IsScreenVisible(sa) && IsScreenVisible(sb)) renderer.DrawLine(sa.X, sa.Y, sb.X, sb.Y, color, thickness, -8999);
    }

    private static bool IsScreenVisible(Vector3 screen) => screen.Z is > 0f and < 1f;

    private static float OverlayScale => MathF.Max(1, EditorChrome.BaseFont.SizeInPoints / 9.5f);

    private IReadOnlyList<Vector3> RouteSamples(IReadOnlyList<PathingWaypoint> points)
    {
        if (points.Count == 0) return [];
        List<Vector3> samples = [points[0].Position];
        for (int index = 1; index < points.Count; index++)
            samples.AddRange(_asset.Route.SegmentPoints(index - 1, index));
        if (_asset.Route.LoopMode == PathingLoopMode.Loop && points.Count > 1)
            samples.AddRange(_asset.Route.SegmentPoints(points.Count - 1, 0));
        return _asset.Dimension == PathingDimension.TwoD ? samples
            : samples.Select(point => point + Vector3.UnitY * .055f).ToArray();
    }
    private void PointerDown(object? sender, MouseEventArgs args)
    {
        _viewport.Host.Focus();
        if (args.Button != MouseButtons.Left) return;
        _dragWaypoint = HitWaypoint(args.Location);
        if (_dragWaypoint < 0) return;
        _selectedWaypoint = _dragWaypoint;
        WaypointDragStarted?.Invoke();
        _viewport.NavigationEnabled = false;
        _viewport.Host.Capture = true;
        WaypointSelected?.Invoke(_dragWaypoint);
        _viewport.Host.Invalidate();
    }

    private void PointerMove(object? sender, MouseEventArgs args)
    {
        if (_dragWaypoint < 0 || args.Button != MouseButtons.Left) return;
        if (_asset.Dimension == PathingDimension.TwoD)
        {
            Vector2 xy = _viewport.ControlToWorld2D(args.Location);
            WaypointMoved?.Invoke(_dragWaypoint, new Vector3(xy, _asset.Route.Waypoints[_dragWaypoint].Z));
            return;
        }
        float height = _asset.Route.Waypoints[_dragWaypoint].Y;
        if (!_viewport.RayToGround(args.Location, height, out Vector3 position)) return;
        int cell = _navMesh?.Cell(position) ?? -1;
        if (_navMesh is not null && cell >= 0) position.Y = _navMesh.Heights[cell];
        WaypointMoved?.Invoke(_dragWaypoint, position);
    }

    private void PointerUp(object? sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Left) return;
        if (_dragWaypoint >= 0) WaypointDragCompleted?.Invoke();
        _dragWaypoint = -1;
        _viewport.NavigationEnabled = true;
        _viewport.Host.Capture = false;
    }

    private void KeyPressed(object? sender, KeyEventArgs args)
    {
        if (args.KeyCode != Keys.Delete || _selectedWaypoint < 0) return;
        WaypointDeleteRequested?.Invoke(_selectedWaypoint);
        _selectedWaypoint = -1;
        args.Handled = true;
    }

    private int HitWaypoint(Point client)
    {
        PointF surface = _viewport.ControlToSurface(client);
        for (int index = _asset.Route.Waypoints.Count - 1; index >= 0; index--)
        {
            if (_asset.Dimension == PathingDimension.TwoD)
            {
                Vector3 world = _asset.Route.Waypoints[index].Position;
                Vector2 xy = _viewport.World2DToSurface(new Vector2(world.X, world.Y));
                if (Vector2.DistanceSquared(xy, new Vector2(surface.X, surface.Y)) <= 225) return index;
                continue;
            }
            Vector3 point = _viewport.WorldToSurface(_asset.Route.Waypoints[index].Position + Vector3.UnitY * .08f);
            float dx = surface.X - point.X, dy = surface.Y - point.Y;
            float radius = 15 * OverlayScale;
            if (IsScreenVisible(point) && dx * dx + dy * dy <= radius * radius) return index;
        }
        return -1;
    }

    private void DrawScene2D(IRenderController renderer)
    {
        UseRenderer(renderer);
        if (_frameWidth != renderer.PixelWidth || _frameHeight != renderer.PixelHeight)
        {
            FrameContent2D(renderer.PixelWidth, renderer.PixelHeight);
            renderer.SetCamera2D(_viewport.Camera2DX, _viewport.Camera2DY, _viewport.Zoom2D, 0);
        }
        RefreshSpriteReference();
        if (!string.IsNullOrWhiteSpace(_agentSprite))
        {
            try
            {
                SpriteRuntimeAsset sprite = SpriteAssetLoader.Load(_projectRoot, _agentSprite);
                if (!ReferenceEquals(sprite, _spriteAsset) || _time < _spriteTime || _spriteState != _asset.Route.AnimationState)
                {
                    _spriteAsset = sprite; _spriteTime = 0; _spriteState = _asset.Route.AnimationState;
                    _spritePlayback = new SpriteComponent { ImageSpeed = 1, AnimationTagIndex = sprite.Tags.FindIndex(tag =>
                        string.Equals(tag.Name, _spriteState, StringComparison.OrdinalIgnoreCase)), AnimationLoopOverride = -1 };
                }
                SpritePlayback.Advance(ref _spritePlayback, sprite, Math.Max(0, _time - _spriteTime));
                _spriteTime = _time;
            }
            catch (Exception exception) when (IsAssetFailure(exception)) { _spriteAsset = null; }
        }
        PgslRenderDrawSurface drawing = new(renderer, null!, renderer.PixelWidth, renderer.PixelHeight, projectPath: _projectRoot);
        foreach (PathingPreviewAgent agent in _simulation.Agents)
        {
            Vector3 world = _asset.WorldPosition(agent.Position);
            if (_spriteAsset is not null)
                drawing.DrawSprite(_agentSprite, world.X, world.Y, _spritePlayback.ImageIndex, 1, 1, 0, Color.White, 1);
            else
                renderer.DrawRect(world.X - .2f, world.Y - .2f, .4f, .4f, new RenderColor(.3f, 1, .5f));
        }
        IReadOnlyList<Vector3> samples = RouteSamples(_asset.Route.Waypoints);
        for (int index = 1; index < samples.Count; index++)
            renderer.DrawLine(samples[index - 1].X, samples[index - 1].Y, samples[index].X, samples[index].Y,
                new RenderColor(.08f, .9f, 1f), 2 / _viewport.Zoom2D);
        for (int index = 0; index < _asset.Route.Waypoints.Count; index++)
        {
            Vector3 world = _asset.Route.Waypoints[index].Position;
            float radius = 9 / _viewport.Zoom2D;
            renderer.DrawRect(world.X - radius, world.Y - radius, radius * 2, radius * 2,
                index == _selectedWaypoint ? new RenderColor(1, .72f, .16f) : new RenderColor(.08f, .7f, .8f));
            Vector2 screen = _viewport.World2DToSurface(new Vector2(world.X, world.Y));
            renderer.DrawText((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), screen.X - 4, screen.Y - 7, 10, RenderColor.White);
        }
        renderer.DrawText("2D XY · drag waypoints · fixed 60 Hz simulation", 16, 14, EditorChrome.SmallFont.SizeInPoints, RenderColor.White);
        if (!string.IsNullOrWhiteSpace(_agentSprite) && _spriteAsset is null)
            renderer.DrawText("Preview image unavailable: " + _agentSprite, 16, 40, EditorChrome.SmallFont.SizeInPoints, new RenderColor(1, .7f, .2f));
    }

    private void RefreshSpriteReference()
    {
        string file = ResolveReference(_boundObject, ResourceKind.GameObject);
        if (!File.Exists(file)) return;
        long stamp = File.GetLastWriteTimeUtc(file).Ticks;
        if (_objectStamp == stamp) return;
        _objectStamp = stamp;
        try
        {
            JObject prefab = ObjectDefinitionResolver.PreviewPrefab(ObjectDefinitionResolver.Load(_projectRoot, file));
            _agentSprite = (string?)prefab["sprite"] ?? string.Empty;
            _spriteAsset = null;
        }
        catch (Exception exception) when (IsAssetFailure(exception)) { _agentSprite = string.Empty; }
    }

    private void UseRenderer(IRenderController renderer)
    {
        if (ReferenceEquals(_renderer, renderer)) return;
        if (_renderer is not null)
        {
            _models.InvalidateAssets(_renderer);
            if (_unitCube.IsValid) _renderer.ReleaseMesh(_unitCube);
        }
        _unitCube = MeshHandle.Invalid;
        _renderer = renderer;
    }

    private SceneVisual VisualForNode(RoomNode node)
    {
        SceneVisual? visual = VisualForObjectReference(node.GameObject?.Prefab ?? string.Empty, WorldForNode(node));
        if (visual is not null) return visual;
        int hash = (node.GameObject?.Prefab ?? node.Name).GetHashCode(StringComparison.OrdinalIgnoreCase);
        return new SceneVisual(string.Empty, string.Empty, string.Empty, 30, true, WorldForNode(node), Vector3.One,
            new RenderColor(.35f + (hash & 63) / 180f, .38f + ((hash >> 6) & 63) / 180f, .45f + ((hash >> 12) & 63) / 190f));
    }

    private SceneVisual? VisualForObjectReference(string reference, Matrix4x4 world)
    {
        string path = ResolveReference(reference, ResourceKind.GameObject);
        if (!File.Exists(path)) return null;
        try
        {
            JObject prefab = ObjectDefinitionResolver.PreviewPrefab(ObjectDefinitionResolver.Load(_projectRoot, path));
            JArray? components = prefab["components"] as JArray;
            JObject? modelComponent = components?.OfType<JObject>().FirstOrDefault(component =>
                ((bool?)component["enabled"] ?? true)
                && (((string?)component["type"] ?? string.Empty).Equals("ModelRendererComponent", StringComparison.OrdinalIgnoreCase)
                    || ((string?)component["type"] ?? string.Empty).Equals("ModelComponent", StringComparison.OrdinalIgnoreCase)));
            JObject? properties = modelComponent?["props"] as JObject;
            string model = (string?)properties?["ModelAsset"] ?? (string?)properties?["Model"] ?? (string?)prefab["model"] ?? string.Empty;
            JObject? materialComponent = components?.OfType<JObject>().FirstOrDefault(component =>
                ((bool?)component["enabled"] ?? true)
                && ((string?)component["type"] ?? string.Empty).Equals("MaterialComponent", StringComparison.OrdinalIgnoreCase));
            string material = (string?)(materialComponent?["props"] as JObject)?["Asset"] ?? string.Empty;
            JObject? animator = components?.OfType<JObject>().FirstOrDefault(component =>
                ((bool?)component["enabled"] ?? true)
                && ((string?)component["type"] ?? string.Empty).Equals("ModelAnimatorComponent", StringComparison.OrdinalIgnoreCase));
            JObject? animatorProperties = animator?["props"] as JObject;
            return new SceneVisual(
                model,
                material,
                (string?)animatorProperties?["ClipName"] ?? string.Empty,
                MathF.Max(1f, (float?)animatorProperties?["ClipFps"] ?? 30f),
                (bool?)animatorProperties?["Loop"] ?? true,
                world,
                new Vector3(
                    MathF.Max(.0001f, (float?)properties?["ScaleX"] ?? 1f),
                    MathF.Max(.0001f, (float?)properties?["ScaleY"] ?? 1f),
                    MathF.Max(.0001f, (float?)properties?["ScaleZ"] ?? 1f)),
                RenderColor.White);
        }
        catch (Exception exception) when (IsAssetFailure(exception)) { return null; }
    }

    private Matrix4x4 WorldForNode(RoomNode node)
    {
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        Matrix4x4 result = LocalTransform(node.Transform);
        string parentId = node.ParentId;
        while (_room is not null && !string.IsNullOrWhiteSpace(parentId) && visited.Add(parentId))
        {
            RoomNode? parent = _room.Nodes.FirstOrDefault(candidate => string.Equals(candidate.Id, parentId, StringComparison.OrdinalIgnoreCase));
            if (parent is null) break;
            result *= LocalTransform(parent.Transform);
            parentId = parent.ParentId;
        }
        return result;
    }

    private static Matrix4x4 LocalTransform(RoomTransform transform) =>
        Matrix4x4.CreateScale(transform.ScaleX, transform.ScaleY, transform.ScaleZ)
        * Matrix4x4.CreateFromYawPitchRoll(
            transform.RotationY * MathF.PI / 180f,
            transform.RotationX * MathF.PI / 180f,
            transform.RotationZ * MathF.PI / 180f)
        * Matrix4x4.CreateTranslation(transform.X, transform.Y, transform.Z);

    private string ResolveReference(string reference, ResourceKind kind)
    {
        return ProjectAssetIndex.ResolveReference(_projectRoot, reference, kind);
    }

    private void ReleaseRuntimeResources()
    {
        _labelFont?.Dispose(); _labelFont = null;
        _labelTypeface?.Dispose(); _labelTypeface = null;
        _terrain?.Dispose();
        _terrain = null;
        if (_renderer is not null)
        {
            _models.InvalidateAssets(_renderer);
            if (_unitCube.IsValid) _renderer.ReleaseMesh(_unitCube);
        }
        _unitCube = MeshHandle.Invalid;
        _renderer = null;
    }

    private static bool IsAssetFailure(Exception exception) => exception is IOException
        or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
        or ArgumentException or System.Text.Json.JsonException or Newtonsoft.Json.JsonException;
}
