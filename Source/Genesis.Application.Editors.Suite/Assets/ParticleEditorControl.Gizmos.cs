using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Runtime.Particles;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ParticleEditorControl
{
    private enum ParticleGizmoHandle { None, Shape, Beam, Bounds }

    private ParticleGizmoHandle _selectedParticleGizmo;
    private ParticleGizmoHandle _draggingParticleGizmo;

    private void InitialiseParticleAuthoringGizmos()
    {
        _viewport.Host.MouseDown += ParticleGizmoMouseDown;
        _viewport.Host.MouseMove += ParticleGizmoMouseMove;
        _viewport.Host.MouseUp += ParticleGizmoMouseUp;
        _viewport.WheelInputHandler = ParticleGizmoWheel;
    }

    private void DrawParticleAuthoringGizmos(IRenderController renderer)
    {
        if (_effect.Preview2D) return;

        RenderColor normal = new(0.18f, 0.82f, 1f, 0.92f);
        RenderColor selected = new(1f, 0.72f, 0.18f, 1f);
        RenderColor bounds = new(0.72f, 0.38f, 1f, 0.9f);
        Vector3 origin = _previewOrigin;

        if (_config.Shape is ParticleEmitShape.Disc or ParticleEmitShape.Ring)
        {
            DrawWorldCircle(renderer, origin, MathF.Max(0.01f, (float)_config.EmitRadius),
                _selectedParticleGizmo == ParticleGizmoHandle.Shape ? selected : normal);
            DrawHandle(renderer, ShapeGizmoPoint(), ParticleGizmoHandle.Shape, normal, selected);
        }
        else if (_config.Shape == ParticleEmitShape.Box)
        {
            Vector3 half = new(
                MathF.Max(0.01f, (float)_config.BoxSizeX) * 0.5f,
                0f,
                MathF.Max(0.01f, (float)_config.BoxSizeZ) * 0.5f);
            DrawWorldRectangle(renderer, origin, half.X, half.Z,
                _selectedParticleGizmo == ParticleGizmoHandle.Shape ? selected : normal);
            DrawHandle(renderer, origin + new Vector3(half.X, 0, half.Z),
                ParticleGizmoHandle.Shape, normal, selected);
        }

        if (_config.RendererKind == ParticleRendererKind.Beam)
        {
            Vector3 end = BeamGizmoPoint();
            DrawWorldLine(renderer, origin, end,
                _selectedParticleGizmo == ParticleGizmoHandle.Beam ? selected : normal, 1.7f);
            DrawHandle(renderer, end, ParticleGizmoHandle.Beam, normal, selected);
        }

        if (_config.BoundsMode == ParticleBoundsMode.Custom)
        {
            Vector3 center = BoundsCenterWorld();
            float hx = MathF.Max(0.01f, (float)_config.BoundsSizeX) * 0.5f;
            float hz = MathF.Max(0.01f, (float)_config.BoundsSizeZ) * 0.5f;
            DrawWorldRectangle(renderer, center, hx, hz,
                _selectedParticleGizmo == ParticleGizmoHandle.Bounds ? selected : bounds);
            Vector3 top = center + Vector3.UnitY * MathF.Max(0.01f, (float)_config.BoundsSizeY) * 0.5f;
            DrawWorldLine(renderer, center, top, bounds, 1f);
            DrawHandle(renderer, center + new Vector3(hx, 0, hz),
                ParticleGizmoHandle.Bounds, bounds, selected);
        }
    }

    private void ParticleGizmoMouseDown(object? sender, MouseEventArgs e)
    {
        if (_effect.Preview2D || e.Button != MouseButtons.Left) return;
        ParticleGizmoHandle hit = HitParticleGizmo(e.Location);
        if (hit == ParticleGizmoHandle.None) return;

        _selectedParticleGizmo = hit;
        _draggingParticleGizmo = hit;
        BeginParticleGesture();
        _viewport.NavigationEnabled = false;
        _viewport.Host.Capture = true;
        _viewport.Host.Invalidate();
    }

    private void ParticleGizmoMouseMove(object? sender, MouseEventArgs e)
    {
        if (_draggingParticleGizmo == ParticleGizmoHandle.None) return;

        bool changed = _draggingParticleGizmo switch
        {
            ParticleGizmoHandle.Shape => DragShapeGizmo(e.Location),
            ParticleGizmoHandle.Beam => DragBeamGizmo(e.Location),
            ParticleGizmoHandle.Bounds => DragBoundsGizmo(e.Location),
            _ => false,
        };

        if (!changed) return;
        _activePreset = "Custom";
        ConfigChanged(reset: false);
    }

    private void ParticleGizmoMouseUp(object? sender, MouseEventArgs e)
    {
        if (_draggingParticleGizmo == ParticleGizmoHandle.None || e.Button != MouseButtons.Left) return;
        _draggingParticleGizmo = ParticleGizmoHandle.None;
        _viewport.NavigationEnabled = true;
        _viewport.Host.Capture = false;
        FinishParticleGesture();
        SyncControls();
        _viewport.Invalidate(true);
    }

    private bool ParticleGizmoWheel(Point location, int delta, Keys modifiers)
    {
        if (_effect.Preview2D || (modifiers & Keys.Control) == 0
            || _selectedParticleGizmo == ParticleGizmoHandle.None)
            return false;

        double amount = Math.Sign(delta) * 0.25;
        switch (_selectedParticleGizmo)
        {
            case ParticleGizmoHandle.Shape when _config.Shape == ParticleEmitShape.Box:
                _config.BoxSizeY = Math.Max(0.01, _config.BoxSizeY + amount * 2);
                break;
            case ParticleGizmoHandle.Beam:
                _config.BeamEndY += amount;
                break;
            case ParticleGizmoHandle.Bounds when _config.BoundsMode == ParticleBoundsMode.Custom:
                _config.BoundsSizeY = Math.Max(0.01, _config.BoundsSizeY + amount * 2);
                break;
            default:
                return false;
        }

        _activePreset = "Custom";
        ConfigChanged(reset: false);
        return true;
    }

    private bool DragShapeGizmo(Point point)
    {
        if (!_viewport.RayToGround(point, _previewOrigin.Y, out Vector3 hit)) return false;
        Vector3 delta = hit - _previewOrigin;
        if (_config.Shape is ParticleEmitShape.Disc or ParticleEmitShape.Ring)
        {
            _config.EmitRadius = Math.Max(0.01, Math.Sqrt(delta.X * delta.X + delta.Z * delta.Z));
            return true;
        }
        if (_config.Shape == ParticleEmitShape.Box)
        {
            _config.BoxSizeX = Math.Max(0.01, Math.Abs(delta.X) * 2);
            _config.BoxSizeZ = Math.Max(0.01, Math.Abs(delta.Z) * 2);
            return true;
        }
        return false;
    }

    private bool DragBeamGizmo(Point point)
    {
        float y = _previewOrigin.Y + (float)_config.BeamEndY;
        if (!_viewport.RayToGround(point, y, out Vector3 hit)) return false;
        Vector3 delta = hit - _previewOrigin;
        _config.BeamEndX = delta.X;
        _config.BeamEndZ = delta.Z;
        return true;
    }

    private bool DragBoundsGizmo(Point point)
    {
        Vector3 center = BoundsCenterWorld();
        if (!_viewport.RayToGround(point, center.Y, out Vector3 hit)) return false;
        Vector3 delta = hit - center;
        _config.BoundsSizeX = Math.Max(0.01, Math.Abs(delta.X) * 2);
        _config.BoundsSizeZ = Math.Max(0.01, Math.Abs(delta.Z) * 2);
        return true;
    }

    private ParticleGizmoHandle HitParticleGizmo(Point clientPoint)
    {
        PointF surface = _viewport.ControlToSurface(clientPoint);
        Vector2 mouse = new(surface.X, surface.Y);
        const float radius = 14f;

        if (_config.RendererKind == ParticleRendererKind.Beam
            && DistanceToSurface(mouse, BeamGizmoPoint()) <= radius)
            return ParticleGizmoHandle.Beam;

        if (_config.BoundsMode == ParticleBoundsMode.Custom)
        {
            Vector3 center = BoundsCenterWorld();
            Vector3 point = center + new Vector3(
                (float)_config.BoundsSizeX * 0.5f, 0, (float)_config.BoundsSizeZ * 0.5f);
            if (DistanceToSurface(mouse, point) <= radius)
                return ParticleGizmoHandle.Bounds;
        }

        if (_config.Shape is ParticleEmitShape.Disc or ParticleEmitShape.Ring or ParticleEmitShape.Box
            && DistanceToSurface(mouse, ShapeGizmoPoint()) <= radius)
            return ParticleGizmoHandle.Shape;

        return ParticleGizmoHandle.None;
    }

    private float DistanceToSurface(Vector2 mouse, Vector3 world)
    {
        Vector3 point = _viewport.WorldToSurface(world);
        if (point.Z < 0f || point.Z > 1.2f) return float.MaxValue;
        return Vector2.Distance(mouse, new Vector2(point.X, point.Y));
    }

    private Vector3 ShapeGizmoPoint() => _config.Shape == ParticleEmitShape.Box
        ? _previewOrigin + new Vector3((float)_config.BoxSizeX * 0.5f, 0, (float)_config.BoxSizeZ * 0.5f)
        : _previewOrigin + Vector3.UnitX * MathF.Max(0.01f, (float)_config.EmitRadius);

    private Vector3 BeamGizmoPoint() => _previewOrigin + new Vector3(
        (float)_config.BeamEndX, (float)_config.BeamEndY, (float)_config.BeamEndZ);

    private Vector3 BoundsCenterWorld() => _previewOrigin + new Vector3(
        (float)_config.BoundsCenterX, (float)_config.BoundsCenterY, (float)_config.BoundsCenterZ);

    private void DrawHandle(IRenderController renderer, Vector3 world, ParticleGizmoHandle kind,
        RenderColor normal, RenderColor selected)
    {
        Vector3 p = _viewport.WorldToSurface(world);
        if (p.Z < 0f || p.Z > 1.2f) return;
        RenderColor colour = _selectedParticleGizmo == kind ? selected : normal;
        renderer.DrawRect(p.X - 5, p.Y - 5, 10, 10, colour, true);
        renderer.DrawRect(p.X - 7, p.Y - 7, 14, 14, colour, false);
    }

    private void DrawWorldLine(IRenderController renderer, Vector3 a, Vector3 b, RenderColor colour, float thickness)
    {
        Vector3 pa = _viewport.WorldToSurface(a);
        Vector3 pb = _viewport.WorldToSurface(b);
        if (pa.Z is < -0.2f or > 1.2f || pb.Z is < -0.2f or > 1.2f) return;
        renderer.DrawLine(pa.X, pa.Y, pb.X, pb.Y, colour, thickness);
    }

    private void DrawWorldRectangle(IRenderController renderer, Vector3 center, float halfX, float halfZ, RenderColor colour)
    {
        Vector3[] points =
        [
            center + new Vector3(-halfX, 0, -halfZ),
            center + new Vector3( halfX, 0, -halfZ),
            center + new Vector3( halfX, 0,  halfZ),
            center + new Vector3(-halfX, 0,  halfZ),
        ];
        for (int i = 0; i < 4; i++)
            DrawWorldLine(renderer, points[i], points[(i + 1) & 3], colour, 1.35f);
    }

    private void DrawWorldCircle(IRenderController renderer, Vector3 center, float radius, RenderColor colour)
    {
        const int segments = 32;
        Vector3 previous = center + Vector3.UnitX * radius;
        for (int i = 1; i <= segments; i++)
        {
            float angle = i / (float)segments * MathF.Tau;
            Vector3 next = center + new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius);
            DrawWorldLine(renderer, previous, next, colour, 1.25f);
            previous = next;
        }
    }
}
