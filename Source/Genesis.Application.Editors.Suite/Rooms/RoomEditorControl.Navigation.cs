using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Runtime.Scene;

namespace Genesis.Application.Editors.Suite.Rooms;

public sealed partial class RoomEditorControl
{
    private bool _constrainedBodyDrag;
    private bool _constrainedAxis;
    private Vector3? _constrainedDirection;
    private Vector2 _constrainedScreenDirection;
    private float _constrainedWorldPerPixel;
    private RoomNode? _constrainedClickNode;

    public EditorCameraControlMethod CameraControlMethod
    {
        get => _viewport.ControlMethod;
        set
        {
            if (!Enum.IsDefined(value)) return;
            _viewport.ControlMethod = value;
            UpdateStatus(value == EditorCameraControlMethod.Free
                ? "Free camera · RMB look · MMB pan · wheel travel"
                : "Orbit camera · RMB orbit · MMB pan · wheel zoom");
        }
    }

    private void AddCameraControlChoices(ToolStripDropDownButton camera)
    {
        var controls = new ToolStripMenuItem("Control method") { Name = "RoomCameraControlMethod" };
        foreach (var (mode, label) in new[] { (EditorCameraControlMethod.Free, "Free camera"), (EditorCameraControlMethod.Orbit, "Orbit") })
        {
            var item = new ToolStripMenuItem(label) { Checked = CameraControlMethod == mode, Name = "RoomCamera" + mode,
                ToolTipText = mode == EditorCameraControlMethod.Free ? "RMB look from here · MMB pan · wheel travel" : "RMB orbit target · MMB pan · wheel zoom" };
            item.Click += (_, _) => CameraControlMethod = mode;
            controls.DropDownItems.Add(item);
        }
        camera.DropDownItems.Add(controls);
        camera.DropDownItems.Add(new ToolStripSeparator());
    }

    private bool HandleSelectionWheel(Point client, int delta, Keys modifiers)
    {
        if (!ViewMode3D || _selected is null || (modifiers & (Keys.Control | Keys.Shift)) == 0) return false;
        if (IsGameCameraPreview || !CanEditNodeInActiveContext(_selected) || _drag != DragKind.None) return true;
        RoomTransform selected = GetNodeWorldTransform(_selected);
        float step = _room.Settings.SnapEnabled ? MetricGridSize
            : MathF.Max(.1f, Vector3.Distance(_viewport.Camera.Eye, new(selected.X, selected.Y, selected.Z)) * .025f);
        Vector3 direction = (modifiers & Keys.Control) != 0 ? -_viewport.Camera.Forward : Vector3.UnitY;
        MoveSelectionBy(direction * (delta / 120f) * step);
        return true;
    }

    private bool TryBeginConstrainedBodyDrag(RoomNode hit, Point client, Keys modifiers)
    {
        if (!ViewMode3D || IsGameCameraPreview || !_selection.Contains(hit) || !CanEditNodeInActiveContext(hit)) return false;
        SetSelection(_selection.Where(node => !ReferenceEquals(node, hit)).Append(hit));
        BeginTransformDrag(DragKind.Move, client);
        _constrainedBodyDrag = true;
        _constrainedAxis = (modifiers & Keys.Control) != 0;
        _constrainedDirection = null;
        _constrainedClickNode = hit;
        _viewport.NavigationEnabled = false;
        return true;
    }

    private void UpdateConstrainedBodyDrag(Point client, RoomTransform start, RoomTransform transform)
    {
        Vector2 pixels = new(client.X - _dragStartClient.X, client.Y - _dragStartClient.Y);
        if (_constrainedDirection is null)
        {
            if (pixels.Length() < 6) return;
            Vector3 origin = new(start.X, start.Y, start.Z);
            Vector3 direction;
            if (_constrainedAxis)
            {
                direction = Vector3.UnitX;
                float best = -1;
                foreach (Vector3 axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
                {
                    Vector2 projected = ProjectDirection(origin, axis);
                    if (projected.LengthSquared() < .0001f) continue;
                    float score = MathF.Abs(Vector2.Dot(Vector2.Normalize(projected), Vector2.Normalize(pixels)));
                    if (score > best) { best = score; direction = axis; }
                }
            }
            else
            {
                Vector3 normal = _viewport.Camera.Forward;
                Vector3 OnPlane(Point point)
                {
                    var ray = _viewport.PickRay(point);
                    float divisor = Vector3.Dot(ray.Direction, normal);
                    return MathF.Abs(divisor) < .00001f ? origin
                        : ray.Origin + ray.Direction * (Vector3.Dot(origin - ray.Origin, normal) / divisor);
                }
                Vector3 delta = OnPlane(client) - OnPlane(_dragStartClient);
                if (delta.LengthSquared() < .000001f) return;
                direction = Vector3.Normalize(delta);
            }
            Vector2 screen = ProjectDirection(origin, direction);
            if (screen.LengthSquared() < .0001f) return;
            _constrainedDirection = direction;
            _constrainedScreenDirection = Vector2.Normalize(screen);
            _constrainedWorldPerPixel = 1f / screen.Length();
        }
        float distance = Vector2.Dot(pixels, _constrainedScreenDirection) * _constrainedWorldPerPixel;
        if (_room.Settings.SnapEnabled) distance = MathF.Round(distance / MetricGridSize) * MetricGridSize;
        Vector3 movement = _constrainedDirection.Value * distance;
        transform.X = start.X + movement.X; transform.Y = start.Y + movement.Y; transform.Z = start.Z + movement.Z;
        UpdateStatus(_constrainedAxis
            ? $"Axis locked: {(_constrainedDirection.Value.X != 0 ? "X" : _constrainedDirection.Value.Y != 0 ? "Y" : "Z")} · Esc cancels"
            : "Initial drag direction locked · Esc cancels");
    }

    private Vector2 ProjectDirection(Vector3 origin, Vector3 direction)
    {
        Vector3 a = _viewport.WorldToSurface(origin), b = _viewport.WorldToSurface(origin + direction);
        return new Vector2((b.X - a.X) * _viewport.Host.ClientSize.Width / _viewport.SurfaceWidth,
            (b.Y - a.Y) * _viewport.Host.ClientSize.Height / _viewport.SurfaceHeight);
    }

    private void FinishConstrainedBodyDrag()
    {
        RoomNode? clicked = _constrainedClickNode;
        bool toggle = _constrainedBodyDrag && _constrainedAxis && _constrainedDirection is null;
        _constrainedBodyDrag = false; _constrainedDirection = null; _constrainedClickNode = null;
        if (toggle && clicked is not null) Select(clicked, additive: true, toggle: true);
    }

    private bool CancelTransformDrag()
    {
        if (_drag == DragKind.None || _dragStartTransform is null) return false;
        _drag = DragKind.None; _dragStartTransform = null;
        _constrainedBodyDrag = false; _constrainedDirection = null; _constrainedClickNode = null;
        ApplyTransformSnapshot(_dragStartTransforms);
        _viewport.NavigationEnabled = !IsGameCameraPreview;
        UpdateStatus("Move cancelled");
        return true;
    }
}
