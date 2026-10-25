using System;
using System.Numerics;

namespace Genesis.Runtime.Input;

/// <summary>
/// The player's own input settings, as an options screen sets them (Engine.Input.Mouse.Sensitivity,
/// Engine.Input.Mouse.InvertY, Engine.Input.Gamepad.VibrationStrength): applied where the mouse's
/// movement enters the game and where a controller's motors are run, so the engine's cameras and
/// every script see the same.
/// </summary>
public static class InputSettings
{
    private static float _sensitivity = 1f;
    private static float _vibration = 1f;

    /// <summary>How far the view turns for a given mouse movement, 0.05 to 10 (1 unless set).</summary>
    public static float LookSensitivity
    {
        get => _sensitivity;
        set => _sensitivity = float.IsFinite(value) ? Math.Clamp(value, 0.05f, 10f) : 1f;
    }

    /// <summary>Moving the mouse up looks down, as some players prefer (false unless set).</summary>
    public static bool InvertLookY { get; set; }

    /// <summary>How strongly controllers vibrate, 0 (never) to 1 (as the game asks) (1 unless set).</summary>
    public static float VibrationStrength
    {
        get => _vibration;
        set => _vibration = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 1f;
    }

    /// <summary>The mouse's movement this frame as the player's settings turn it into looking.</summary>
    public static Vector2 Look(Vector2 movement) =>
        new(movement.X * _sensitivity, movement.Y * _sensitivity * (InvertLookY ? -1f : 1f));

    /// <summary>Every setting back to its default (a new game).</summary>
    public static void Reset()
    {
        _sensitivity = 1f;
        _vibration = 1f;
        InvertLookY = false;
    }
}
