using System;
using System.Collections.Generic;

namespace Genesis.Rendering.Primitives;

/// <summary>
/// Parts of the frame switched off for a whole run with GENESIS_RENDER_OFF (a comma list: ao, post,
/// decals, lampshadows, cull, uvslide, transparent, probes, lighting), whatever the game asks, to find which one makes something look wrong:
/// a game.run picture with and without each, compared. Unset leaves everything as the game set it.
/// </summary>
public static class RenderDiagnosticsOff
{
    public const string EnvironmentVariable = "GENESIS_RENDER_OFF";

    private static readonly HashSet<string> Parts = Read(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>Ambient occlusion (GTAO).</summary>
    public static bool AmbientOcclusion => Parts.Contains("ao");

    /// <summary>The project's own post effects (Fullscreen shaders added by the game).</summary>
    public static bool PostEffects => Parts.Contains("post");

    /// <summary>Projected decals and decal cards.</summary>
    public static bool Decals => Parts.Contains("decals");

    /// <summary>Point and spot light shadows.</summary>
    public static bool LampShadows => Parts.Contains("lampshadows");

    /// <summary>Back-face culling: every surface is drawn from both sides.</summary>
    public static bool Culling => Parts.Contains("cull");

    /// <summary>Sliding material pictures (a model material's UV scroll): every picture stands still.</summary>
    public static bool UvSlide => Parts.Contains("uvslide");

    /// <summary>See-through (alpha-blended) world surfaces.</summary>
    public static bool Transparent => Parts.Contains("transparent");

    /// <summary>The room's light probe (bounced light and reflections): the flat ambient alone.</summary>
    public static bool LightProbes => Parts.Contains("probes");

    /// <summary>Lighting: every surface shows its own colour, as an unlit material does.</summary>
    public static bool Lighting => Parts.Contains("lighting");

    internal static HashSet<string> Read(string value)
    {
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            parts.Add(part);
        return parts;
    }
}
