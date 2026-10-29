using System;

namespace Genesis.Shared.Rendering;

/// <summary>
/// Render scale (<c>Engine.Display.Resolution.*</c>): the 3D scene, and every pass that reads it,
/// drawn with a share of the view's pixels and stretched onto it at the end, with a light
/// sharpening; the GUI and text, drawn after, stay sharp. 0.75 on a 1080-line screen draws the
/// scene at 1440 x 810, about 44% fewer pixels to shade; above 1 draws more pixels than the screen
/// has and shrinks them (a sharper picture for a fast card). Dynamic resolution lowers the scale
/// while the scene's frames take too long for <see cref="TargetFps"/> and raises it again when
/// there is room, never above <see cref="Scale"/> or below <see cref="MinimumScale"/>.
/// </summary>
public static class RenderScale
{
    public const float Smallest = 0.25f;
    public const float Largest = 2f;

    // Steps the dynamic scale moves in, so the images the scene is drawn into are remade rarely.
    private const float Step = 0.05f;

    private static float _scale = 1f;
    private static float _targetFps = 60f;
    private static float _minimum = 0.5f;
    private static float _sharpness = 0.5f;
    private static float _dynamicScale = 1f;
    private static double _slowFor, _fastFor, _settleFor;

    /// <summary>The share of the view's size the scene is drawn at, 0.25 to 2 (1 unless set).</summary>
    public static float Scale
    {
        get => _scale;
        set => _scale = float.IsFinite(value) ? Math.Clamp(value, Smallest, Largest) : 1f;
    }

    /// <summary>
    /// Whether the scene is drawn at the scale at all (Engine.Display.Resolution.Enabled; true unless set); off, it is drawn at
    /// the view's own size, and the scale, the sharpening and the dynamic scale are kept for when it is switched on.
    /// </summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Lower the scale to hold <see cref="TargetFps"/>, and raise it again when there is room (false unless set).</summary>
    public static bool Dynamic { get; set; }

    /// <summary>The frame rate dynamic resolution holds, 10 to 500 (60 unless set).</summary>
    public static float TargetFps
    {
        get => _targetFps;
        set => _targetFps = float.IsFinite(value) ? Math.Clamp(value, 10f, 500f) : 60f;
    }

    /// <summary>The lowest scale dynamic resolution goes to, 0.25 to 1 (0.5 unless set).</summary>
    public static float MinimumScale
    {
        get => _minimum;
        set => _minimum = float.IsFinite(value) ? Math.Clamp(value, Smallest, 1f) : 0.5f;
    }

    /// <summary>How much the stretched picture is sharpened, 0 to 1 (0.5 unless set).</summary>
    public static float Sharpness
    {
        get => _sharpness;
        set => _sharpness = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0.5f;
    }

    /// <summary>The scale the scene was last drawn at: <see cref="Scale"/>, or lower while dynamic resolution holds the frame rate.</summary>
    public static float Current { get; private set; } = 1f;

    /// <summary>
    /// The scale for the next frame, from how long the scene's last frame took on the graphics card
    /// (0 when unknown) and the seconds since the frame before. Dynamic resolution steps down after
    /// a quarter of a second over the frame's budget and up after two seconds well under it.
    /// </summary>
    public static float Advance(double gpuMilliseconds, double seconds)
    {
        if (!Enabled) return Current = 1f;
        if (!Dynamic)
        {
            _dynamicScale = _scale;
            _slowFor = _fastFor = _settleFor = 0;
            return Current = _scale;
        }

        float ceiling = _scale, floor = Math.Min(_minimum, _scale);
        _dynamicScale = Math.Clamp(_dynamicScale, floor, ceiling);
        if (gpuMilliseconds > 0 && double.IsFinite(gpuMilliseconds) && seconds > 0 && double.IsFinite(seconds))
        {
            seconds = Math.Min(seconds, 0.25);
            // The scene's share of a frame; the rest is left for the GUI and handing the frame over.
            double budget = 1000.0 / _targetFps * 0.85;
            _settleFor = Math.Max(0, _settleFor - seconds);
            _slowFor = gpuMilliseconds > budget ? _slowFor + seconds : 0;
            _fastFor = gpuMilliseconds < budget * 0.7 ? _fastFor + seconds : 0;
            if (_settleFor <= 0 && _slowFor >= 0.25 && _dynamicScale > floor)
            {
                _dynamicScale = Math.Max(floor, _dynamicScale - 2 * Step);
                _slowFor = _fastFor = 0;
                _settleFor = 0.5;
            }
            else if (_settleFor <= 0 && _fastFor >= 2.0 && _dynamicScale < ceiling)
            {
                _dynamicScale = Math.Min(ceiling, _dynamicScale + Step);
                _slowFor = _fastFor = 0;
                _settleFor = 1.0;
            }
        }
        float stepped = MathF.Round(_dynamicScale / Step) * Step;
        return Current = Math.Clamp(stepped, floor, ceiling);
    }

    /// <summary>Every value back to its default (a new game).</summary>
    public static void Reset()
    {
        _scale = 1f;
        _targetFps = 60f;
        _minimum = 0.5f;
        _sharpness = 0.5f;
        _dynamicScale = 1f;
        _slowFor = _fastFor = _settleFor = 0;
        Enabled = true;
        Dynamic = false;
        Current = 1f;
    }
}
