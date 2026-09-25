using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite;

/// <summary>
/// Shared viewport session for 3D editors: grid, floor, preview lighting/sky, gizmo, and Play clock.
/// Editors keep their own documents; this is live preview state for the open surface.
/// </summary>
public sealed class Editor3DSession
{
    public static readonly Vector3 DefaultSunTravel = Vector3.Normalize(new Vector3(0.55f, -0.72f, 0.42f));
    public static readonly Vector3 DefaultSunColor = new(1.0f, 0.956f, 0.87f);
    public const float DefaultSunIntensity = 1.25f;

    private Vector3 _sunTravel = DefaultSunTravel;
    private Vector3 _sunTravelRest = DefaultSunTravel;
    private Vector3 _sunColor = DefaultSunColor;
    private Vector3 _backgroundColor = EditorSceneLighting.Sky.HorizonColor;
    private float _sunIntensity = DefaultSunIntensity;
    private bool _showGrid;
    private bool _snapToGrid;
    private bool _lit = true;
    private bool _showSunVisual;
    private float _gridCellSize = 1f;
    private float _sunOrbitSpeedDegreesPerSecond;
    private Color _gridColor = Color.FromArgb(18, 255, 255, 255);
    private EditorFloorStyle _floorStyle = EditorFloorStyle.None;
    private EditorGizmoMode _gizmoMode = EditorGizmoMode.Move;
    private EditorGizmoSpace _gizmoSpace = EditorGizmoSpace.World;
    private long _lastAdvanceTimestamp;

    public EditorPreviewClock Clock { get; } = new();

    public event EventHandler? Changed;

    public bool ShowGrid
    {
        get => _showGrid;
        set => Set(ref _showGrid, value);
    }

    public float GridCellSize
    {
        get => _gridCellSize;
        set => Set(ref _gridCellSize, Math.Clamp(value, 0.25f, 64f));
    }

    public Color GridColor
    {
        get => _gridColor;
        set => Set(ref _gridColor, value);
    }

    public bool SnapToGrid
    {
        get => _snapToGrid;
        set => Set(ref _snapToGrid, value);
    }

    public EditorFloorStyle FloorStyle
    {
        get => _floorStyle;
        set => Set(ref _floorStyle, value);
    }

    public bool Lit
    {
        get => _lit;
        set => Set(ref _lit, value);
    }

    public bool ShowSunVisual
    {
        get => _showSunVisual;
        set => Set(ref _showSunVisual, value);
    }

    public Vector3 SunTravel
    {
        get => _sunTravel;
        set
        {
            Vector3 next = value.LengthSquared() < 1e-8f ? DefaultSunTravel : Vector3.Normalize(value);
            if (_sunTravel == next)
            {
                return;
            }

            _sunTravel = next;
            RaiseChanged();
        }
    }

    public Vector3 SunColor
    {
        get => _sunColor;
        set => Set(ref _sunColor, Vector3.Clamp(value, Vector3.Zero, new Vector3(4f)));
    }

    public float SunIntensity
    {
        get => _sunIntensity;
        set => Set(ref _sunIntensity, Math.Clamp(value, 0f, 8f));
    }

    public float SunOrbitSpeedDegreesPerSecond
    {
        get => _sunOrbitSpeedDegreesPerSecond;
        set => Set(ref _sunOrbitSpeedDegreesPerSecond, Math.Clamp(value, 0f, 180f));
    }

    public Vector3 BackgroundColor
    {
        get => _backgroundColor;
        set => Set(ref _backgroundColor, Vector3.Clamp(value, Vector3.Zero, Vector3.One));
    }

    public EditorGizmoMode GizmoMode
    {
        get => _gizmoMode;
        set => Set(ref _gizmoMode, value);
    }

    public EditorGizmoSpace GizmoSpace
    {
        get => _gizmoSpace;
        set => Set(ref _gizmoSpace, value);
    }

    public void CaptureSunRest() => _sunTravelRest = _sunTravel;

    public void ResetSun()
    {
        _sunTravel = _sunTravelRest;
        RaiseChanged();
    }

    public void ResetLighting()
    {
        _sunTravel = DefaultSunTravel;
        _sunTravelRest = DefaultSunTravel;
        _sunColor = DefaultSunColor;
        _sunIntensity = DefaultSunIntensity;
        _sunOrbitSpeedDegreesPerSecond = 0f;
        _lit = true;
        RaiseChanged();
    }

    public void ResetSky()
    {
        _backgroundColor = EditorSceneLighting.Sky.HorizonColor;
        RaiseChanged();
    }

    public float Snap(float world)
    {
        if (!_snapToGrid || _gridCellSize < 0.01f)
        {
            return world;
        }

        return MathF.Round(world / _gridCellSize) * _gridCellSize;
    }

    public Vector3 Snap(Vector3 world) => new(Snap(world.X), world.Y, Snap(world.Z));

    /// <summary>Applies preview lighting, sky, and Lit/Unlit to a scene state the editor already built.</summary>
    public Mesh3DState Apply(Mesh3DState state)
    {
        state.LightingEnabled = _lit;
        state.LightingWeight = _lit ? 1f : 0f;
        state.LightDirection = _sunTravel;
        state.SunColor = _sunColor;
        state.SunIntensity = _sunIntensity;
        state.ShowSunVisual = _showSunVisual && _lit;
        state.BackgroundColor = _backgroundColor;
        state.ShowFloor = _floorStyle.DrawsPlate();
        return state;
    }

    public void DrawGrid(
        EditorViewport3D viewport,
        IRenderController renderer,
        float horizontalExtent,
        float verticalExtent)
    {
        if (!_showGrid)
        {
            return;
        }

        float cell = MathF.Max(0.25f, _gridCellSize);
        float alpha = _gridColor.A / 255f;
        EditorViewportGridHelper.DrawGrid3D(
            viewport,
            renderer,
            cell,
            [_gridColor.R / 255f, _gridColor.G / 255f, _gridColor.B / 255f, alpha <= 0f ? 0.05f : alpha],
            horizontalExtent,
            verticalExtent);
    }

    /// <summary>Call once per presented frame while Play is on: clock tick + optional sun orbit.</summary>
    public bool AdvancePlayingFrame()
    {
        if (!Clock.Playing)
        {
            _lastAdvanceTimestamp = 0;
            return false;
        }

        long now = Stopwatch.GetTimestamp();
        if (_lastAdvanceTimestamp != 0)
        {
            double elapsed = (now - _lastAdvanceTimestamp) / (double)Stopwatch.Frequency;
            if (elapsed < EditorPreviewClock.FrameDelta * 0.75)
            {
                return false;
            }
        }

        _lastAdvanceTimestamp = now;
        if (!Clock.TryTickPlaying())
        {
            return false;
        }

        if (_sunOrbitSpeedDegreesPerSecond > 0f)
        {
            float radians = _sunOrbitSpeedDegreesPerSecond * (MathF.PI / 180f) * EditorPreviewClock.FrameDelta;
            _sunTravel = Vector3.Normalize(Vector3.TransformNormal(_sunTravel, Matrix4x4.CreateRotationY(radians)));
        }

        return true;
    }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
