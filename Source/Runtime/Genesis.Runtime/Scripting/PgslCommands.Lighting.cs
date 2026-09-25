using System;
using System.Numerics;
using Genesis.Runtime.ECS.Components;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// Persistent Light Emitter controls. Calling one of these from an Object event creates the
/// component when necessary; transient draw-pass lights remain available through DrawPointLight3D.
/// These commands are discovered automatically by the Universal/Visual Builder catalog.
/// </summary>
public static partial class PgslCommands
{
    [PgslCommand("LightEmitterEnable", "LightEmitterEnable(enabled)",
        "Enable or disable this object's persistent Light Emitter", "Lighting",
        Namespace = "Engine.Rendering.Lights")]
    public static void LightEmitterEnable(bool enabled) => ChangeLight(ctx =>
        ctx.LightEmitterEnabled = enabled);

    [PgslCommand("LightEmitterSetRadius", "LightEmitterSetRadius(radius)",
        "Set this object's light radius", "Lighting", Namespace = "Engine.Rendering.Lights")]
    public static void LightEmitterSetRadius(double radius) => ChangeLight(ctx =>
        ctx.LightEmitterRadius = Math.Max(0.01, radius));

    [PgslCommand("LightEmitterSetIntensity", "LightEmitterSetIntensity(intensity)",
        "Set this object's light intensity", "Lighting", Namespace = "Engine.Rendering.Lights")]
    public static void LightEmitterSetIntensity(double intensity) => ChangeLight(ctx =>
        ctx.LightEmitterIntensity = Math.Max(0, intensity));

    [PgslCommand("LightEmitterSetFalloff", "LightEmitterSetFalloff(exponent)",
        "Set light falloff (1 linear, 2 quadratic, larger values concentrate light near the emitter)",
        "Lighting", Namespace = "Engine.Rendering.Lights")]
    public static void LightEmitterSetFalloff(double exponent) => ChangeLight(ctx =>
        ctx.LightEmitterFalloff = Math.Clamp(exponent, 0.05, 16.0));

    [PgslCommand("LightEmitterSetColor", "LightEmitterSetColor(r, g, b)",
        "Set the primary light colour", "Lighting", Namespace = "Engine.Rendering.Lights")]
    public static void LightEmitterSetColor(double r, double g, double b) => ChangeLight(ctx =>
        ctx.LightEmitterColor = ColorVector(r, g, b));

    [PgslCommand("LightEmitterSetSecondaryColor", "LightEmitterSetSecondaryColor(r, g, b)",
        "Set the second light colour used by colour cycling", "Lighting",
        Namespace = "Engine.Rendering.Lights")]
    public static void LightEmitterSetSecondaryColor(double r, double g, double b) => ChangeLight(ctx =>
        ctx.LightEmitterSecondaryColor = ColorVector(r, g, b));

    [PgslCommand("LightEmitterSetTertiaryColor", "LightEmitterSetTertiaryColor(r, g, b)",
        "Set the third light colour used by colour cycling", "Lighting",
        Namespace = "Engine.Rendering.Lights")]
    public static void LightEmitterSetTertiaryColor(double r, double g, double b) => ChangeLight(ctx =>
        ctx.LightEmitterTertiaryColor = ColorVector(r, g, b));

    [PgslCommand("LightEmitterSetColorCount", "LightEmitterSetColorCount(count)",
        "Use one, two, or three authored light colours", "Lighting",
        Namespace = "Engine.Rendering.Lights")]
    public static void LightEmitterSetColorCount(double count) => ChangeLight(ctx =>
        ctx.LightEmitterColorCount = Math.Clamp((int)Math.Round(count), 1, 3));

    [PgslCommand("LightEmitterSetOffset", "LightEmitterSetOffset(x, y, z)",
        "Set the emitter offset relative to this object", "Lighting",
        Namespace = "Engine.Rendering.Lights")]
    public static void LightEmitterSetOffset(double x, double y, double z) => ChangeLight(ctx =>
        ctx.LightEmitterOffset = new Vector3((float)x, (float)y, (float)z));

    [PgslCommand("LightEmitterSetAction", "LightEmitterSetAction(action, speed, amount)",
        "Set Steady, Flicker, Pulse, Glow, or ColourCycle behaviour", "Lighting",
        Namespace = "Engine.Rendering.Lights")]
    public static void LightEmitterSetAction(string action, double speed, double amount) => ChangeLight(ctx =>
    {
        ctx.LightEmitterAction = Enum.TryParse(
            action,
            ignoreCase: true,
            out LightEmitterAction parsed)
                ? parsed.ToString()
                : LightEmitterAction.Steady.ToString();
        ctx.LightEmitterActionSpeed = Math.Max(0, speed);
        ctx.LightEmitterActionAmount = Math.Clamp(amount, 0, 1);
    });

    private static void ChangeLight(Action<PgslContext> change)
    {
        PgslContext ctx = GetContext();
        if (ctx is null) return;
        change(ctx);
        ctx.LightEmitterTouched = true;
    }

    private static Vector3 ColorVector(double r, double g, double b) => new(
        (float)Math.Clamp(r, 0, 1),
        (float)Math.Clamp(g, 0, 1),
        (float)Math.Clamp(b, 0, 1));
}
