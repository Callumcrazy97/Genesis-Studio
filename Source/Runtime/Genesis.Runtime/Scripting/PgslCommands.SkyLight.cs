using System;
using System.Numerics;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// Request 64a: the hemisphere sky light. The room's <c>skyLight</c> settings start it; these change
/// it for the room being played (a new room starts from its own settings again). With no game
/// running they do nothing.
/// </summary>
public static partial class PgslCommands
{
    private static SceneEnvironment ActiveEnvironment => ActiveGameContext?.Scene?.Environment;

    [PgslCommand("SkySetAmbientMode", "SkySetAmbientMode(mode) -> bool",
        "How the sky lights shade: \"hemisphere\" (the light of the whole sky dome and the ground, brighter and less blue) or \"zenith\" (the sky's zenith colour, as before). False for another name",
        "Lighting")]
    public static bool SkySetAmbientMode(string mode)
    {
        if (!SkyLightModes.TryParse(mode, out int parsed)) return false;
        SceneEnvironment environment = ActiveEnvironment;
        if (environment != null) environment.SkyLightMode = parsed;
        return true;
    }

    [PgslCommand("SkyGetAmbientMode", "SkyGetAmbientMode() -> string", "\"hemisphere\" or \"zenith\": how the sky lights shade in this room", "Lighting")]
    public static string SkyGetAmbientMode() => SkyLightModes.Name(ActiveEnvironment?.SkyLightMode ?? SkyLightModes.Zenith);

    [PgslCommand("SkySetAmbientStrength", "SkySetAmbientStrength(strength)",
        "Brightness of the hemisphere sky light, 0 to 16 (1 = the sky as drawn; 1.5 for brighter, game-like shade)", "Lighting")]
    public static void SkySetAmbientStrength(double strength)
    {
        SceneEnvironment environment = ActiveEnvironment;
        if (environment != null && double.IsFinite(strength))
            environment.SkyLightStrength = SkyLightDefaults.ClampStrength((float)strength);
    }

    [PgslCommand("SkyGetAmbientStrength", "SkyGetAmbientStrength() -> number", "Brightness of the hemisphere sky light", "Lighting")]
    public static double SkyGetAmbientStrength() => ActiveEnvironment?.SkyLightStrength ?? SkyLightDefaults.Strength;

    [PgslCommand("SkySetAmbientTint", "SkySetAmbientTint(r, g, b)",
        "Colour multiplier of the hemisphere sky light, each 0 to 4 (1, 1, 1 = none)", "Lighting")]
    public static void SkySetAmbientTint(double r, double g, double b)
    {
        SceneEnvironment environment = ActiveEnvironment;
        if (environment == null || !double.IsFinite(r) || !double.IsFinite(g) || !double.IsFinite(b)) return;
        environment.SkyLightTint = new Vector3(
            SkyLightDefaults.ClampTint((float)r), SkyLightDefaults.ClampTint((float)g), SkyLightDefaults.ClampTint((float)b));
    }

    [PgslCommand("SkySetAmbientSaturation", "SkySetAmbientSaturation(saturation)",
        "Colourfulness of the hemisphere sky light, 0 to 2: 1 the sky's own colour, 0 grey; 0.4 by default (a clear day's sunlit ground comes out near neutral)",
        "Lighting")]
    public static void SkySetAmbientSaturation(double saturation)
    {
        SceneEnvironment environment = ActiveEnvironment;
        if (environment != null && double.IsFinite(saturation))
            environment.SkyLightSaturation = SkyLightDefaults.ClampSaturation((float)saturation);
    }

    [PgslCommand("SkyGetAmbientSaturation", "SkyGetAmbientSaturation() -> number", "Colourfulness of the hemisphere sky light", "Lighting")]
    public static double SkyGetAmbientSaturation() => ActiveEnvironment?.SkyLightSaturation ?? SkyLightDefaults.Saturation;

    [PgslCommand("AmbientMode", "Engine.Sky.AmbientMode",
        "How the sky lights shade: \"hemisphere\" or \"zenith\" (as SkySetAmbientMode)", "Engine · Sky",
        Namespace = "Engine.Sky")]
    public static string SkyAmbientMode
    {
        get => SkyGetAmbientMode();
        set => SkySetAmbientMode(value);
    }

    [PgslCommand("AmbientStrength", "Engine.Sky.AmbientStrength",
        "Brightness of the hemisphere sky light, 0 to 16 (1 = the sky as drawn)", "Engine · Sky",
        Namespace = "Engine.Sky")]
    public static float SkyAmbientStrength
    {
        get => (float)SkyGetAmbientStrength();
        set => SkySetAmbientStrength(value);
    }

    [PgslCommand("AmbientSaturation", "Engine.Sky.AmbientSaturation",
        "Colourfulness of the hemisphere sky light, 0 to 2 (1 = the sky's own colour)", "Engine · Sky",
        Namespace = "Engine.Sky")]
    public static float SkyAmbientSaturation
    {
        get => (float)SkyGetAmbientSaturation();
        set => SkySetAmbientSaturation(value);
    }

    [PgslCommand("SetAmbientTint", "Engine.Sky.SetAmbientTint",
        "Colour multiplier of the hemisphere sky light (as SkySetAmbientTint)", "Engine · Sky",
        Namespace = "Engine.Sky")]
    public static void SkySetAmbientTintNamespaced(float r, float g, float b) => SkySetAmbientTint(r, g, b);
}
