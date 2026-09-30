using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// Ink outline controls for illustrated and cel-shaded projects. Authors write
/// <c>Engine.Rendering.InkOutlineEnabled(true);</c> and peers; the values land on
/// <see cref="InkOutlineSettings"/>, which the final composite reads every frame.
/// </summary>
public static partial class PgslCommands
{
    [PgslCommand("InkOutlineEnabled", "Engine.Rendering.InkOutlineEnabled",
        "Draw ink outlines on silhouettes and creases (off by default)", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static bool InkOutlineEnabled
    {
        get => InkOutlineSettings.Enabled;
        set => ReconfigureInkOutline(enabled: value);
    }

    [PgslCommand("InkOutlineWidth", "Engine.Rendering.InkOutlineWidth",
        "Ink line width in pixels at 1080p (0.5–6)", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static float InkOutlineWidth
    {
        get => InkOutlineSettings.WidthPixels;
        set => ReconfigureInkOutline(widthPixels: value);
    }

    [PgslCommand("InkOutlineOpacity", "Engine.Rendering.InkOutlineOpacity",
        "Ink line opacity (0–1)", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static float InkOutlineOpacity
    {
        get => InkOutlineSettings.Opacity;
        set => ReconfigureInkOutline(opacity: value);
    }

    [PgslCommand("InkOutlineCreaseAngle", "Engine.Rendering.InkOutlineCreaseAngle",
        "Creases sharper than this many degrees are inked (2–89)", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static float InkOutlineCreaseAngle
    {
        get => InkOutlineSettings.CreaseAngleDegrees;
        set => ReconfigureInkOutline(creaseAngleDegrees: value);
    }

    [PgslCommand("InkOutlineDepthStep", "Engine.Rendering.InkOutlineDepthStep",
        "Relative depth jump that counts as a silhouette (0.012 = 1.2%)", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static float InkOutlineDepthStep
    {
        get => InkOutlineSettings.DepthStep;
        set => ReconfigureInkOutline(depthStep: value);
    }

    [PgslCommand("InkOutlineColor", "Engine.Rendering.InkOutlineColor(r, g, b)",
        "Ink colour, 0–1 per channel, in display space", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static void InkOutlineColor(float r, float g, float b) => InkOutlineSettings.SetColor(r, g, b);

    [PgslCommand("InkOutlineDistance", "Engine.Rendering.InkOutlineDistance(fullWidth, far, farOpacity)",
        "Lines keep full width to fullWidth metres, then thin and fade to farOpacity by far", "Engine · Rendering",
        Namespace = "Engine.Rendering")]
    public static void InkOutlineDistance(float fullWidth, float far, float farOpacity)
        => ReconfigureInkOutline(fullWidthDistance: fullWidth, farDistance: far, farOpacity: farOpacity);

    /// <summary>Re-apply <see cref="InkOutlineSettings"/> changing only the requested fields.</summary>
    private static void ReconfigureInkOutline(
        bool? enabled = null,
        float? widthPixels = null,
        float? opacity = null,
        float? creaseAngleDegrees = null,
        float? depthStep = null,
        float? fullWidthDistance = null,
        float? farDistance = null,
        float? farOpacity = null)
    {
        InkOutlineSettings.Configure(
            enabled ?? InkOutlineSettings.Enabled,
            widthPixels ?? InkOutlineSettings.WidthPixels,
            opacity ?? InkOutlineSettings.Opacity,
            creaseAngleDegrees ?? InkOutlineSettings.CreaseAngleDegrees,
            depthStep ?? InkOutlineSettings.DepthStep,
            fullWidthDistance ?? InkOutlineSettings.FullWidthDistance,
            farDistance ?? InkOutlineSettings.FarDistance,
            farOpacity ?? InkOutlineSettings.FarOpacity);
    }
}
