using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>How the sun's shafts in the volumetric fog look (FogShaftSettings). The defaults are the fog as before.</summary>
public static partial class PgslCommands
{
    [PgslCommand("FogShaftAnisotropy", "Engine.Rendering.FogShaftAnisotropy",
        "How strongly the haze scatters sunlight forward, 0 to 0.95 (0.25 as before). Above 0.25 shafts glow towards the sun and fade side-on, as in dusty air; about 0.7 for sunbeams indoors",
        "Engine · Rendering", Namespace = "Engine.Rendering")]
    public static double FogShaftAnisotropy
    {
        get => FogShaftSettings.Anisotropy;
        set => FogShaftSettings.Anisotropy = (float)value;
    }

    [PgslCommand("FogShaftFloor", "Engine.Rendering.FogShaftFloor",
        "Brightness of fog in shadow against fog in sunlight, 0 to 1 (0.35 as before); higher gives subtler shafts",
        "Engine · Rendering", Namespace = "Engine.Rendering")]
    public static double FogShaftFloor
    {
        get => FogShaftSettings.ShadowedFloor;
        set => FogShaftSettings.ShadowedFloor = (float)value;
    }

    [PgslCommand("FogShaftSoftness", "Engine.Rendering.FogShaftSoftness",
        "Width of a sun shaft's soft edge in metres, 0 to 4 (0 as before: one shadow sample, a cut edge)",
        "Engine · Rendering", Namespace = "Engine.Rendering")]
    public static double FogShaftSoftness
    {
        get => FogShaftSettings.Softness;
        set => FogShaftSettings.Softness = (float)value;
    }
}
