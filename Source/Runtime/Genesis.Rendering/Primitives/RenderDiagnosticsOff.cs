using System;
using System.Collections.Generic;

namespace Genesis.Rendering.Primitives;

/// <summary>
/// Parts of the frame switched off for a whole run with GENESIS_RENDER_OFF (a comma list: ao, post,
/// decals, lampshadows, cull, uvslide, transparent, probes, lighting), whatever the game asks, to find
/// which one makes something look wrong: a game.run picture with and without each, compared. Unset
/// leaves everything as the game set it. Read once at start-up: several are asked on every draw.
/// </summary>
public static class RenderDiagnosticsOff
{
    public const string EnvironmentVariable = "GENESIS_RENDER_OFF";

    private static readonly HashSet<string> Parts = Read(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>Ambient occlusion (GTAO).</summary>
    public static bool AmbientOcclusion { get; } = Parts.Contains("ao");

    /// <summary>The project's own post effects (Fullscreen shaders added by the game).</summary>
    public static bool PostEffects { get; } = Parts.Contains("post");

    /// <summary>Projected decals and decal cards.</summary>
    public static bool Decals { get; } = Parts.Contains("decals");

    /// <summary>Point and spot light shadows.</summary>
    public static bool LampShadows { get; } = Parts.Contains("lampshadows");

    /// <summary>Back-face culling: every surface is drawn from both sides.</summary>
    public static bool Culling { get; } = Parts.Contains("cull");

    /// <summary>Sliding material pictures (a model material's UV scroll): every picture stands still.</summary>
    public static bool UvSlide { get; } = Parts.Contains("uvslide");

    /// <summary>See-through (alpha-blended) world surfaces.</summary>
    public static bool Transparent { get; } = Parts.Contains("transparent");

    /// <summary>The room's light probe (bounced light and reflections): the flat ambient alone.</summary>
    public static bool LightProbes { get; } = Parts.Contains("probes");

    /// <summary>Lighting: every surface shows its own colour, as an unlit material does.</summary>
    public static bool Lighting { get; } = Parts.Contains("lighting");

    /// <summary>GENESIS_RENDER_VIEW: a debug view for a whole run (albedo, ambient, direct, normals, depth, shadow).</summary>
    public const string ViewEnvironmentVariable = "GENESIS_RENDER_VIEW";

    /// <summary>The debug view a diagnosis run asks for (GENESIS_RENDER_VIEW); Shaded when unset.</summary>
    public static Genesis.Shared.Interfaces.RenderDebugView View { get; } =
        ParseView(Environment.GetEnvironmentVariable(ViewEnvironmentVariable));

    /// <summary>A debug view's name (as Engine.DebugView takes it) to the view; Shaded for anything else.</summary>
    public static Genesis.Shared.Interfaces.RenderDebugView ParseView(string name) =>
        (name ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "normals" => Genesis.Shared.Interfaces.RenderDebugView.Normals,
            "depth" => Genesis.Shared.Interfaces.RenderDebugView.SceneDepth,
            "shadow" => Genesis.Shared.Interfaces.RenderDebugView.ShadowMap,
            "albedo" => Genesis.Shared.Interfaces.RenderDebugView.Albedo,
            "ambient" => Genesis.Shared.Interfaces.RenderDebugView.AmbientLight,
            "direct" => Genesis.Shared.Interfaces.RenderDebugView.DirectLight,
            _ => Genesis.Shared.Interfaces.RenderDebugView.Shaded,
        };

    /// <summary>GENESIS_RENDER_HIDE: models whose name contains any of these (a comma list) are not drawn.</summary>
    public const string HideEnvironmentVariable = "GENESIS_RENDER_HIDE";

    private static readonly string[] HiddenModels =
        [.. Read(Environment.GetEnvironmentVariable(HideEnvironmentVariable))];

    /// <summary>True when a diagnosis run leaves this model out (GENESIS_RENDER_HIDE), to see what is behind it.</summary>
    public static bool HidesModel(string modelName)
    {
        if (HiddenModels.Length == 0 || string.IsNullOrEmpty(modelName)) return false;
        foreach (string part in HiddenModels)
            if (modelName.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static HashSet<string> Read(string value)
    {
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            parts.Add(part);
        return parts;
    }
}
