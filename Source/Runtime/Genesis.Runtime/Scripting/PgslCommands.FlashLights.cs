using System;
using System.Numerics;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Flash lights: a muzzle flash, an explosion, a spark. Fired once from any event; the engine fades
// each one out over its life and removes it. Colour is red, green and blue from 0 to 255, as
// DrawPointLight3D takes it. Never shadowed, so they never take a lamp's shadow slot.
public static partial class PgslCommands
{
    [PgslCommand("LightFlash", "LightFlash(x, y, z, r, g, b, intensity, radius, life) -> flash",
        "A short point light at x, y, z: full brightness at once, fading to nothing over life seconds, then gone (a muzzle flash, an explosion, a spark). Colour 0-255; intensity and radius as DrawPointLight3D. It lights surfaces and fog but is never shadowed and never takes a lamp's shadow. Up to 256 at once (a new one then replaces the one nearest its end). 0 when nothing would be lit",
        "Lighting", Namespace = "Engine.Rendering.Lights")]
    public static double LightFlash(double x, double y, double z, double r, double g, double b,
        double intensity, double radius, double life)
    {
        if (!Finite3(x, y, z) || !Finite3(r, g, b) || !Finite3(intensity, radius, life)) return 0;
        return FlashLights.Add(
            new Vector3((float)x, (float)y, (float)z),
            new Vector3((float)Math.Clamp(r, 0, 255) / 255f, (float)Math.Clamp(g, 0, 255) / 255f, (float)Math.Clamp(b, 0, 255) / 255f),
            (float)intensity, (float)radius, (float)life);
    }

    [PgslCommand("LightFlashMove", "LightFlashMove(flash, x, y, z) -> moved",
        "Move a flash (one that follows a moving muzzle or a rolling fireball); false when it has burnt out",
        "Lighting", Namespace = "Engine.Rendering.Lights")]
    public static bool LightFlashMove(double flash, double x, double y, double z) =>
        double.IsFinite(flash) && Finite3(x, y, z) && FlashLights.Move((int)flash, new Vector3((float)x, (float)y, (float)z));

    [PgslCommand("LightFlashRemove", "LightFlashRemove(flash) -> removed",
        "Put a flash out at once; false when it has burnt out already",
        "Lighting", Namespace = "Engine.Rendering.Lights")]
    public static bool LightFlashRemove(double flash) => double.IsFinite(flash) && FlashLights.Remove((int)flash);

    [PgslCommand("LightFlashExists", "LightFlashExists(flash) -> alive",
        "Whether a flash is still lit", "Lighting", Namespace = "Engine.Rendering.Lights")]
    public static bool LightFlashExists(double flash) => double.IsFinite(flash) && FlashLights.Exists((int)flash);

    [PgslCommand("LightFlashClear", "LightFlashClear()",
        "Put every flash out (a room change does this too)", "Lighting", Namespace = "Engine.Rendering.Lights")]
    public static void LightFlashClear() => FlashLights.Clear();

    [PgslCommand("LightFlashCount", "LightFlashCount() -> count",
        "How many flashes are lit now", "Lighting", Namespace = "Engine.Rendering.Lights")]
    public static double LightFlashCount() => FlashLights.Count;
}
