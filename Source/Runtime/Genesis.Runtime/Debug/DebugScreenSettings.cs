using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Debugger;

/// <summary>What the debug screen's diagnostic view draws the frame as (Engine.Debug.View.Mode).</summary>
public enum DebugViewMode
{
    /// <summary>The frame as the game draws it.</summary>
    Off,
    /// <summary>Every model in the colour of the level of detail it is drawn at.</summary>
    Lod,
    /// <summary>Models drawn see-through and added up: the brighter, the more layers cover a pixel.</summary>
    Overdraw,
    /// <summary>Distance from the camera, near bright.</summary>
    Depth,
    /// <summary>Surface directions as colours.</summary>
    Normals,
    /// <summary>Lit (white) and shadowed (black) by the sun's farthest shadow map.</summary>
    ShadowCoverage,
    /// <summary>Which of the sun's shadow cascades covers each pixel (needs the forward shader's branch).</summary>
    ShadowCascades,
    /// <summary>Where each surface's reflection comes from and how far it reaches (needs the forward shader's branch).</summary>
    Reflections,
    /// <summary>How many lights reach each pixel's cluster, and the lights culled (needs the forward shader's branch).</summary>
    LightCount,
}

/// <summary>
/// The debug screen's drawing toggles, shared by the F6 screen and Engine.Debug.* (all off unless
/// set). The Enabled rule of the API holds: turning on a part turns on what it belongs to (a
/// collider overlay opens the overlays, the overlays open the screen); closing the screen hides
/// them all, and they come back as they were when it opens again. Read every frame only through
/// the cached <see cref="CollidersShown"/>, <see cref="GuiBoundsShown"/> and <see cref="ActiveView"/>,
/// so a closed screen costs a field read.
/// </summary>
public static class DebugScreenSettings
{
    private static bool _overlay, _colliders, _gui, _inspect, _view;
    private static DebugViewMode _viewMode = DebugViewMode.Lod;
    private static bool _screenPending, _wireframePending;

    /// <summary>The F6 screen is open (Engine.Debug.Enabled).</summary>
    public static bool Screen
    {
        get => DebugOverlay.Current?.IsVisible ?? _screenPending;
        set
        {
            // Asked for before the game's screen exists (a room's settings): it opens with it.
            if (DebugOverlay.Current is { } overlay) overlay.IsVisible = value;
            else { _screenPending = value; Refresh(); }
        }
    }

    /// <summary>The drawn overlays as a group (Engine.Debug.Overlay.Enabled).</summary>
    public static bool Overlay { get => _overlay; set { _overlay = value; if (value) Screen = true; Refresh(); } }

    /// <summary>Colliders and hitboxes drawn over the frame (Engine.Debug.Overlay.Colliders.Enabled).</summary>
    public static bool Colliders { get => _colliders; set { _colliders = value; if (value) Overlay = true; Refresh(); } }

    /// <summary>Which colliders are drawn while <see cref="Colliders"/> is on (all unless set).</summary>
    public static bool ColliderBodies { get; set; } = true;
    public static bool ColliderCharacters { get; set; } = true;
    public static bool ColliderTriggers { get; set; } = true;
    public static bool ColliderTiles { get; set; } = true;
    public static bool ColliderHitboxes { get; set; } = true;

    /// <summary>Each GUI layer's bounds outlined over the frame (Engine.Debug.Overlay.Gui.Enabled).</summary>
    public static bool GuiBounds { get => _gui; set { _gui = value; if (value) Overlay = true; Refresh(); } }

    /// <summary>
    /// The 3D scene drawn as wireframe (Engine.Debug.Overlay.Wireframe.Enabled): the screen's own toggle once it exists,
    /// and what was asked for before it did until then.
    /// </summary>
    public static bool Wireframe
    {
        get => DebugOverlay.Current?.ShowWireframe ?? _wireframePending;
        set
        {
            if (DebugOverlay.Current is { } overlay) overlay.ShowWireframe = value;
            else _wireframePending = value;
            if (value) Overlay = true;
        }
    }

    /// <summary>The object report (Engine.Debug.Inspect.Enabled): pick under the mouse or step through the list.</summary>
    public static bool Inspect { get => _inspect; set { _inspect = value; if (value) Screen = true; Refresh(); } }

    /// <summary>The instance the object report is about (Engine.Debug.Inspect.Instance); 0 for none.</summary>
    public static int InspectInstance { get; set; }

    /// <summary>The diagnostic view (Engine.Debug.View.Enabled).</summary>
    public static bool View { get => _view; set { _view = value; if (value) Screen = true; Refresh(); } }

    /// <summary>The diagnostic view's mode (Engine.Debug.View.Mode); setting one turns the view on.</summary>
    public static DebugViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (value == DebugViewMode.Off) { View = false; return; }
            _viewMode = value;
            View = true;
        }
    }

    /// <summary>True while colliders are being drawn: the screen and its overlays open, colliders on.</summary>
    public static bool CollidersShown { get; private set; }

    /// <summary>True while GUI layer bounds are being outlined.</summary>
    public static bool GuiBoundsShown { get; private set; }

    /// <summary>The diagnostic view the frame is drawn with now: Off unless the screen is open with a view on.</summary>
    public static DebugViewMode ActiveView { get; private set; }

    /// <summary>The model tint the view asks for (LOD or overdraw), read once per model draw; Off otherwise.</summary>
    public static DebugViewMode ModelView { get; private set; }

    /// <summary>Back to nothing drawn, for a new play session.</summary>
    public static void Reset()
    {
        _overlay = _colliders = _gui = _inspect = _view = false;
        _viewMode = DebugViewMode.Lod;
        _screenPending = _wireframePending = false;
        ColliderBodies = ColliderCharacters = ColliderTriggers = ColliderTiles = ColliderHitboxes = true;
        InspectInstance = 0;
        Refresh();
    }

    /// <summary>A screen opened by a setting made before it existed takes that setting now.</summary>
    internal static bool TakePendingScreen()
    {
        bool pending = _screenPending;
        _screenPending = false;
        return pending;
    }

    /// <summary>A screen made after wireframe was asked for takes it now.</summary>
    internal static bool TakePendingWireframe()
    {
        bool pending = _wireframePending;
        _wireframePending = false;
        return pending;
    }

    /// <summary>Works out the cached values again; the screen calls it when it opens or closes.</summary>
    internal static void Refresh()
    {
        bool open = DebugOverlay.Current?.IsVisible ?? false;
        CollidersShown = open && _overlay && _colliders;
        GuiBoundsShown = open && _overlay && _gui;
        ActiveView = open && _view ? _viewMode : DebugViewMode.Off;
        ModelView = ActiveView is DebugViewMode.Lod or DebugViewMode.Overdraw ? ActiveView : DebugViewMode.Off;
    }

    // ── Modes by name, for Engine.Debug.View.Mode ───────────────────────────────

    private static readonly (DebugViewMode Mode, string Name, string Label)[] Modes =
    {
        (DebugViewMode.Lod, "lod", "LOD level"),
        (DebugViewMode.Overdraw, "overdraw", "Overdraw"),
        (DebugViewMode.Depth, "depth", "Depth"),
        (DebugViewMode.Normals, "normals", "Normals"),
        (DebugViewMode.ShadowCoverage, "shadows", "Shadow coverage"),
        (DebugViewMode.ShadowCascades, "cascades", "Shadow cascades"),
        (DebugViewMode.Reflections, "reflections", "Reflections"),
        (DebugViewMode.LightCount, "lights", "Light count"),
    };

    /// <summary>The view modes in the order the screen offers them.</summary>
    public static IEnumerable<DebugViewMode> AllModes { get { foreach (var mode in Modes) yield return mode.Mode; } }

    /// <summary>The names Engine.Debug.View.Mode takes.</summary>
    public static IEnumerable<string> ModeNames { get { foreach (var mode in Modes) yield return mode.Name; } }

    public static string NameOf(DebugViewMode mode)
    {
        foreach (var entry in Modes) if (entry.Mode == mode) return entry.Name;
        return "off";
    }

    public static string LabelOf(DebugViewMode mode)
    {
        foreach (var entry in Modes) if (entry.Mode == mode) return entry.Label;
        return "Off";
    }

    public static bool TryParseMode(string text, out DebugViewMode mode)
    {
        mode = DebugViewMode.Off;
        string name = text?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Equals("off", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var entry in Modes)
        {
            if (!entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !entry.Label.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            mode = entry.Mode;
            return true;
        }
        return false;
    }

    /// <summary>
    /// What the renderer draws for a mode: the debug views its forward shader has (normals, depth,
    /// the sun's shadow map), or null for a mode it draws as shaded (LOD and overdraw are tints the
    /// model draws carry; cascades, reflections and light count have no shader branch yet).
    /// </summary>
    public static RenderDebugView? RendererView(DebugViewMode mode) => mode switch
    {
        DebugViewMode.Depth => RenderDebugView.SceneDepth,
        DebugViewMode.Normals => RenderDebugView.Normals,
        DebugViewMode.ShadowCoverage => RenderDebugView.ShadowMap,
        _ => null,
    };

    /// <summary>True when the frame shows the mode; false for the modes still waiting for their shader branch.</summary>
    public static bool IsDrawn(DebugViewMode mode) => mode is DebugViewMode.Lod or DebugViewMode.Overdraw
        || RendererView(mode) != null;

    // ── Legend ─────────────────────────────────────────────────────────────────

    /// <summary>The colour of each level of detail in the LOD view: full detail green to coarsest red.</summary>
    public static readonly Vector4[] LodColours =
    {
        new(0.20f, 0.85f, 0.30f, 1f),
        new(0.95f, 0.90f, 0.20f, 1f),
        new(1.00f, 0.55f, 0.10f, 1f),
        new(0.95f, 0.20f, 0.20f, 1f),
    };

    /// <summary>What one layer adds in the overdraw view.</summary>
    public static readonly Vector4 OverdrawStep = new(0.10f, 0.05f, 0.02f, 1f);

    /// <summary>
    /// A model's draw as the LOD or overdraw view shows it: unlit in its level's colour, or added up
    /// in a dim colour without depth. Called per model draw only while <see cref="ModelView"/> is on;
    /// the draw is this frame's own, so nothing the game keeps is changed.
    /// </summary>
    internal static void TintModelDraw(ref MeshDrawCall draw, int level)
    {
        draw.Texture = draw.NormalMap = draw.OrmMap = draw.EmissionMap = draw.HeightMap = draw.ExtrasMap = TextureHandle.Invalid;
        draw.Shader = default;
        draw.AuthoredTextures = default;
        draw.SurfaceParams = default;
        draw.Emissive = 0f;
        draw.EmissionColor = default;
        draw.Alpha = 1f;
        if (ModelView == DebugViewMode.Overdraw)
        {
            draw.Tint = new RenderColor(OverdrawStep.X, OverdrawStep.Y, OverdrawStep.Z, 1f);
            draw.Flags = (draw.Flags & ~(MeshDrawFlags.Transparent | MeshDrawFlags.Emissive))
                | MeshDrawFlags.Additive | MeshDrawFlags.Unlit | MeshDrawFlags.NoShadow | MeshDrawFlags.NoDepthWrite
                | MeshDrawFlags.NoReceiveShadow | MeshDrawFlags.NoFog | MeshDrawFlags.NoCull;
            return;
        }
        Vector4 colour = LodColours[Math.Clamp(level, 0, LodColours.Length - 1)];
        draw.Tint = new RenderColor(colour.X, colour.Y, colour.Z, 1f);
        draw.Flags = (draw.Flags & ~(MeshDrawFlags.Transparent | MeshDrawFlags.Emissive | MeshDrawFlags.NoDepthWrite))
            | MeshDrawFlags.Unlit | MeshDrawFlags.NoReceiveShadow | MeshDrawFlags.NoFog;
    }

    /// <summary>The legend lines for a mode: a colour (or none) and what it means.</summary>
    public static IReadOnlyList<(Vector4? Colour, string Text)> Legend(DebugViewMode mode) => mode switch
    {
        DebugViewMode.Lod => new (Vector4?, string)[]
        {
            (LodColours[0], "Level 0: full detail"), (LodColours[1], "Level 1"), (LodColours[2], "Level 2"),
            (LodColours[3], "Level 3: coarsest"), (null, "Models only; terrain, grass and script meshes as shaded"),
        },
        DebugViewMode.Overdraw => new (Vector4?, string)[]
        {
            (new Vector4(0.20f, 0.10f, 0.04f, 1f), "Two layers"), (new Vector4(0.60f, 0.30f, 0.12f, 1f), "Six layers"),
            (new Vector4(1f, 0.6f, 0.25f, 1f), "Ten or more"), (null, "Models only, added up without depth"),
        },
        DebugViewMode.Depth => new (Vector4?, string)[] { (new Vector4(1f), "Near"), (new Vector4(0.25f, 0.25f, 0.25f, 1f), "Far") },
        DebugViewMode.Normals => new (Vector4?, string)[]
        {
            (new Vector4(1f, 0.5f, 0.5f, 1f), "Facing +X"), (new Vector4(0.5f, 1f, 0.5f, 1f), "Facing up (+Y)"), (new Vector4(0.5f, 0.5f, 1f, 1f), "Facing +Z"),
        },
        DebugViewMode.ShadowCoverage => new (Vector4?, string)[]
        {
            (new Vector4(1f), "Lit by the sun"), (new Vector4(0f, 0f, 0f, 1f), "In the sun's shadow"),
            (new Vector4(0.02f, 0.02f, 0.06f, 1f), "Outside the shadow map"),
        },
        _ => new (Vector4?, string)[]
        {
            (null, "Not drawn yet: needs its branch in the forward shader (the renderer's own work)."),
            (null, "The frame is drawn as shaded meanwhile."),
        },
    };
}
