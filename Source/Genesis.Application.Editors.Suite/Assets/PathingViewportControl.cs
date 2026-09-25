using System.Numerics;
using Genesis.Application.Core.Resources;
using Genesis.Rendering.Meshes;
using Genesis.Runtime.ECS.Components;
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
    private readonly RuntimeModelRenderSystem _models = new();
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
        _viewport.DrawOverlay += DrawOverlay;
        _viewport.Host.MouseDown += PointerDown;
        _viewport.Host.MouseMove += PointerMove;
        _viewport.Host.MouseUp += PointerUp;
        _viewport.Host.KeyDown += KeyPressed;
        Controls.Add(_viewport);
        Disposed += (_, _) => ReleaseRuntimeResources();
    }

    public event Action<int, Vector3>? WaypointMoved;
    public event Action<int>? WaypointSelected;
    public event Action<int>? WaypointDeleteRequested;

    public void Bind(PathingAsset asset, RoomAsset? room, NavMeshData? navMesh, PathingPreviewSimulation simulation)
    {
        bool contextChanged = !ReferenceEquals(_room, room) || !ReferenceEquals(_navMesh, navMesh)
            || !string.Equals(_asset.TargetObject, asset.TargetObject, StringComparison.OrdinalIgnoreCase);
        _asset = asset;
        _room = room;
        _navMesh = navMesh;
        _simulation = simulation;
        if (contextChanged)
        {
            RebuildContext();
            FrameContent();
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

    public void FrameContent()
    {
        List<Vector3> points = _asset.Route.Waypoints.Select(point => point.Position).ToList();
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
        _viewport.Camera.Target = new Vector3(center.X, MathF.Max(.75f, center.Y), center.Z);
        _viewport.Camera.Distance = Math.Clamp(span * .82f, 8f, 4500f);
        _hasFramed = true;
        _viewport.Host.Invalidate();
    }

    private void RebuildContext()
    {
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
        _renderer = renderer;
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
        DrawNavMesh(renderer);
        DrawRoute(renderer);
        DrawAgents(renderer);
        renderer.DrawText("NAVMESH VIEW · 60 FPS", 16, 14, 13, RenderColor.White);
        string room = _room is null ? "No room selected" : _room.Name;
        renderer.DrawText($"{room} · {_simulation.Agents.Count} simulated agent(s)", 16, 34, 10,
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
            EditorTransformGizmo.DrawCircle(renderer, new Vector2(screen.X, screen.Y), 10f, new RenderColor(.05f, .15f, .2f), color);
            renderer.DrawText((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), screen.X - 3.5f, screen.Y - 6f, 9, RenderColor.White);
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
            if (IsScreenVisible(screen)) renderer.DrawText(agent.Name, screen.X + 7f, screen.Y, 9, RenderColor.White);
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

    private IReadOnlyList<Vector3> RouteSamples(IReadOnlyList<PathingWaypoint> points)
    {
        List<Vector3> samples = points.Select(point => point.Position + Vector3.UnitY * .055f).ToList();
        if (_asset.Route.LoopMode == PathingLoopMode.Loop && samples.Count > 1) samples.Add(samples[0]);
        if (!points.Any(point => point.Curve) || samples.Count < 3) return samples;
        List<Vector3> curve = [];
        for (int index = 0; index < samples.Count - 1; index++)
        {
            Vector3 p0 = samples[Math.Max(0, index - 1)];
            Vector3 p1 = samples[index];
            Vector3 p2 = samples[index + 1];
            Vector3 p3 = samples[Math.Min(samples.Count - 1, index + 2)];
            for (int step = 0; step < 10; step++)
            {
                float t = step / 10f;
                float t2 = t * t, t3 = t2 * t;
                curve.Add(.5f * ((2f * p1) + (-p0 + p2) * t
                    + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                    + (-p0 + 3f * p1 - 3f * p2 + p3) * t3));
            }
        }
        curve.Add(samples[^1]);
        return curve;
    }

    private void PointerDown(object? sender, MouseEventArgs args)
    {
        _viewport.Host.Focus();
        if (args.Button != MouseButtons.Left) return;
        _dragWaypoint = HitWaypoint(args.Location);
        if (_dragWaypoint < 0) return;
        _selectedWaypoint = _dragWaypoint;
        _viewport.NavigationEnabled = false;
        _viewport.Host.Capture = true;
        WaypointSelected?.Invoke(_dragWaypoint);
        _viewport.Host.Invalidate();
    }

    private void PointerMove(object? sender, MouseEventArgs args)
    {
        if (_dragWaypoint < 0 || args.Button != MouseButtons.Left) return;
        float height = _asset.Route.Waypoints[_dragWaypoint].Y;
        if (!_viewport.RayToGround(args.Location, height, out Vector3 position)) return;
        int cell = _navMesh?.Cell(position) ?? -1;
        if (_navMesh is not null && cell >= 0) position.Y = _navMesh.Heights[cell];
        WaypointMoved?.Invoke(_dragWaypoint, position);
    }

    private void PointerUp(object? sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Left) return;
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
            Vector3 point = _viewport.WorldToSurface(_asset.Route.Waypoints[index].Position + Vector3.UnitY * .08f);
            float dx = surface.X - point.X, dy = surface.Y - point.Y;
            if (IsScreenVisible(point) && dx * dx + dy * dy <= 15f * 15f) return index;
        }
        return -1;
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
