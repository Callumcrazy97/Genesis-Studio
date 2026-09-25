using System.Drawing;
using System.Numerics;
using Genesis.Shared.Interfaces;
using Genesis.Runtime.Particles;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ParticleEditorControl
{
    private EditorGizmoMode _particleGizmoMode = EditorGizmoMode.Move;
    private EditorGizmoSpace _particleGizmoSpace = EditorGizmoSpace.Local;
    private bool _particleGizmoDragging;
    private int _particleGizmoAxis = -1;
    private Vector3 _particleDragAxisWorld;
    private Vector2 _particleDragAxisScreen;
    private float _particleDragWorldPerPixel;
    private Vector3 _particleDragStartOffset;
    private Vector3 _particleDragStartEuler;
    private Vector3 _particleDragStartBox;
    private double _particleDragStartRadius;

    private Vector3 EmitterWorldOrigin => _previewOrigin + new Vector3(
        (float)_config.EmitterOffsetX,
        (float)_config.EmitterOffsetY,
        (float)_config.EmitterOffsetZ);

    private Vector3 EmitterEuler => new(
        (float)_config.EmitterPitch,
        (float)_config.EmitterYaw,
        (float)_config.EmitterRoll);

    private void AttachParticleGizmoInput()
    {
        _viewport.Host.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) TryBeginParticleGizmoDrag(e.Location);
        };
        _viewport.Host.MouseMove += (_, e) =>
        {
            if (_particleGizmoDragging && e.Button == MouseButtons.Left)
                ApplyParticleGizmoDrag(e.Location);
        };
        _viewport.Host.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) EndParticleGizmoDrag();
        };
        _viewport.Host.MouseCaptureChanged += (_, _) =>
        {
            if (_particleGizmoDragging && !_viewport.Host.Capture) EndParticleGizmoDrag();
        };
    }

    private void DrawParticleEmitterGizmo(IRenderController renderer)
    {
        EditorTransformGizmo.Draw3D(
            _viewport,
            renderer,
            EmitterWorldOrigin,
            ParticleGizmoLength(),
            _particleGizmoMode,
            _particleGizmoSpace,
            EmitterEuler,
            includeZ: true,
            activeAxis: _particleGizmoAxis >= 0 ? _particleGizmoAxis : null);
    }

    private void DrawParticleBounds(IRenderController renderer)
    {
        Vector3 center;
        Vector3 size;
        if (_config.BoundsMode == ParticleBoundsMode.Manual)
        {
            center = EmitterWorldOrigin + new Vector3(
                (float)_config.BoundsCenterX,
                (float)_config.BoundsCenterY,
                (float)_config.BoundsCenterZ);
            size = new Vector3(
                (float)Math.Max(.001, _config.BoundsSizeX),
                (float)Math.Max(.001, _config.BoundsSizeY),
                (float)Math.Max(.001, _config.BoundsSizeZ));
        }
        else
        {
            float lifeTravel = (float)(Math.Max(.01, _config.Lifetime) * Math.Max(0, _config.Speed));
            Vector3 emit = _config.Shape == ParticleEmitShape.Box
                ? new Vector3((float)_config.BoxSizeX, (float)_config.BoxSizeY, (float)_config.BoxSizeZ) * .5f
                : new Vector3((float)Math.Max(.1, _config.EmitRadius));
            float padding = (float)Math.Max(_config.StartSize, _config.EndSize) + lifeTravel;
            size = (emit + new Vector3(padding)) * 2f;
            center = EmitterWorldOrigin + new Vector3(0, MathF.Max(0, lifeTravel * .35f), 0);
        }
        Vector3 half = Vector3.Max(size * .5f, new Vector3(.01f));
        EditorBoundsOverlay.DrawAabb(_viewport, renderer, center - half, center + half,
            new RenderColor(.50f, .72f, 1f, .72f));
    }

    private bool TryBeginParticleGizmoDrag(Point client)
    {
        if (_effect.Preview2D) return false;
        Vector3 origin = EmitterWorldOrigin;
        float length = ParticleGizmoLength();
        EditorGizmoHit? hit = EditorTransformGizmo.HitTest3D(
            _viewport,
            _viewport.ControlToSurface(client),
            origin,
            length,
            _particleGizmoMode,
            _particleGizmoSpace,
            EmitterEuler,
            includeZ: true);
        if (hit is null) return false;

        Vector3[] axes = EditorTransformGizmo.Axes(_particleGizmoSpace, EmitterEuler);
        Vector3 axis = axes[hit.Value.AxisIndex];
        if (!EditorTransformGizmo.TryProjectAxisToSurface(
                _viewport, origin, axis, length, out Vector2 screenDirection, out float screenLength))
            return false;

        _particleGizmoDragging = true;
        _particleGizmoAxis = hit.Value.AxisIndex;
        _particleDragAxisWorld = Vector3.Normalize(axis);
        _particleDragAxisScreen = screenDirection;
        _particleDragWorldPerPixel = length / screenLength;
        _particleDragStartOffset = new Vector3(
            (float)_config.EmitterOffsetX,
            (float)_config.EmitterOffsetY,
            (float)_config.EmitterOffsetZ);
        _particleDragStartEuler = EmitterEuler;
        _particleDragStartBox = new Vector3(
            (float)_config.BoxSizeX,
            (float)_config.BoxSizeY,
            (float)_config.BoxSizeZ);
        _particleDragStartRadius = _config.EmitRadius;
        BeginParticleGesture();
        _viewport.NavigationEnabled = false;
        _viewport.Host.Capture = true;
        return true;
    }

    private void ApplyParticleGizmoDrag(Point client)
    {
        if (!_particleGizmoDragging) return;
        Vector3 startWorld = _previewOrigin + _particleDragStartOffset;
        Vector3 projected = _viewport.WorldToSurface(startWorld);
        PointF surface = _viewport.ControlToSurface(client);
        Vector2 pointerDelta = new(surface.X - projected.X, surface.Y - projected.Y);
        float worldDelta = Vector2.Dot(pointerDelta, _particleDragAxisScreen) * _particleDragWorldPerPixel;

        if (_particleGizmoMode == EditorGizmoMode.Move)
        {
            Vector3 localDelta = _particleDragAxisWorld * worldDelta;
            Vector3 next = _particleDragStartOffset + localDelta;
            _config.EmitterOffsetX = next.X;
            _config.EmitterOffsetY = next.Y;
            _config.EmitterOffsetZ = next.Z;
        }
        else if (_particleGizmoMode == EditorGizmoMode.Rotate)
        {
            float degrees = worldDelta * 18f;
            Vector3 next = _particleDragStartEuler;
            if (_particleGizmoAxis == 0) next.X += degrees;
            else if (_particleGizmoAxis == 1) next.Y += degrees;
            else next.Z += degrees;
            _config.EmitterPitch = next.X;
            _config.EmitterYaw = next.Y;
            _config.EmitterRoll = next.Z;
        }
        else
        {
            float factor = Math.Clamp(1f + worldDelta * .1f, .02f, 50f);
            if (_config.Shape == ParticleEmitShape.Box)
            {
                Vector3 next = _particleDragStartBox;
                if (_particleGizmoAxis == 0) next.X = Math.Max(.001f, next.X * factor);
                else if (_particleGizmoAxis == 1) next.Y = Math.Max(.001f, next.Y * factor);
                else next.Z = Math.Max(.001f, next.Z * factor);
                _config.BoxSizeX = next.X;
                _config.BoxSizeY = next.Y;
                _config.BoxSizeZ = next.Z;
            }
            else
            {
                _config.EmitRadius = Math.Max(.001, _particleDragStartRadius * factor);
            }
        }

        _activePreset = "Custom";
        ConfigChanged(reset: false);
    }

    private void EndParticleGizmoDrag()
    {
        if (!_particleGizmoDragging) return;
        _particleGizmoDragging = false;
        _particleGizmoAxis = -1;
        _viewport.NavigationEnabled = true;
        _viewport.Host.Capture = false;
        FinishParticleGesture();
        SyncControls();
        _viewport.Invalidate(true);
    }

    private float ParticleGizmoLength() => MathF.Max(1.25f, _viewport.Camera.Distance * .12f);
}
