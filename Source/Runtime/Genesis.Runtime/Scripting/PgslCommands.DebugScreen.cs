using System;
using Genesis.Runtime.Debugger;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Engine.Debug: the F6 debug screen and what it draws over the frame (Documentation/Design/EngineApiAreas.md).
// Everything is off unless set; turning a part on opens what it belongs to, up to the screen.
public static partial class PgslCommands
{
    [PgslCommand("Enabled", "Engine.Debug.Enabled = true", "The F6 debug screen is open (false unless set, or opened with F6); closing it hides everything it draws, which comes back as it was when it opens again", "Debug", Namespace = "Engine.Debug", Flat = false)]
    public static bool DebugEnabledPart { get => DebugScreenSettings.Screen; set => DebugScreenSettings.Screen = value; }

    [PgslCommand("Enabled", "Engine.Debug.Overlay.Enabled = true", "What the debug screen draws over the frame (colliders, GUI layer outlines), as a group (false unless set; true once any of them is turned on)", "Debug", Namespace = "Engine.Debug.Overlay", Flat = false)]
    public static bool DebugOverlayEnabled { get => DebugScreenSettings.Overlay; set => DebugScreenSettings.Overlay = value; }

    [PgslCommand("Enabled", "Engine.Debug.Overlay.Colliders.Enabled = true", "Colliders and hitboxes drawn over the frame (false unless set): physics bodies, characters, triggers, a 2D room's solid tiles and sprite hitboxes, each in its own colour, as wireframes in 3D and outlines in 2D", "Debug", Namespace = "Engine.Debug.Overlay.Colliders", Flat = false)]
    public static bool DebugOverlayCollidersEnabled { get => DebugScreenSettings.Colliders; set => DebugScreenSettings.Colliders = value; }

    [PgslCommand("Bodies", "Engine.Debug.Overlay.Colliders.Bodies = false", "Whether the collider overlay draws physics bodies: static blue, moving green, kinematic yellow (true unless set)", "Debug", Namespace = "Engine.Debug.Overlay.Colliders", Flat = false)]
    public static bool DebugOverlayCollidersBodies { get => DebugScreenSettings.ColliderBodies; set => DebugScreenSettings.ColliderBodies = value; }

    [PgslCommand("Characters", "Engine.Debug.Overlay.Colliders.Characters = false", "Whether the collider overlay draws characters' bodies, in magenta (true unless set)", "Debug", Namespace = "Engine.Debug.Overlay.Colliders", Flat = false)]
    public static bool DebugOverlayCollidersCharacters { get => DebugScreenSettings.ColliderCharacters; set => DebugScreenSettings.ColliderCharacters = value; }

    [PgslCommand("Triggers", "Engine.Debug.Overlay.Colliders.Triggers = false", "Whether the collider overlay draws triggers (sensor bodies and sprite trigger shapes), in orange (true unless set)", "Debug", Namespace = "Engine.Debug.Overlay.Colliders", Flat = false)]
    public static bool DebugOverlayCollidersTriggers { get => DebugScreenSettings.ColliderTriggers; set => DebugScreenSettings.ColliderTriggers = value; }

    [PgslCommand("Tiles", "Engine.Debug.Overlay.Colliders.Tiles = false", "Whether the collider overlay draws a 2D room's solid tiles, in red (true unless set)", "Debug", Namespace = "Engine.Debug.Overlay.Colliders", Flat = false)]
    public static bool DebugOverlayCollidersTiles { get => DebugScreenSettings.ColliderTiles; set => DebugScreenSettings.ColliderTiles = value; }

    [PgslCommand("Hitboxes", "Engine.Debug.Overlay.Colliders.Hitboxes = false", "Whether the collider overlay draws 2D instances' sprite hitboxes (the mask box collisions test), in cyan (true unless set)", "Debug", Namespace = "Engine.Debug.Overlay.Colliders", Flat = false)]
    public static bool DebugOverlayCollidersHitboxes { get => DebugScreenSettings.ColliderHitboxes; set => DebugScreenSettings.ColliderHitboxes = value; }

    [PgslCommand("Enabled", "Engine.Debug.Overlay.Gui.Enabled = true", "Each GUI layer's area outlined over the frame, with its owner's name (false unless set): which instance's Draw GUI event or HUD drew where", "Debug", Namespace = "Engine.Debug.Overlay.Gui", Flat = false)]
    public static bool DebugOverlayGuiEnabled { get => DebugScreenSettings.GuiBounds; set => DebugScreenSettings.GuiBounds = value; }

    [PgslCommand("Report", "Engine.Debug.Overlay.Gui.Report", "The GUI layers drawn in the last frame, one a line: pass, owner, instance, draw calls, area and the User Interface resources drawn; empty unless the GUI page or outline is showing (read)", "Debug", Namespace = "Engine.Debug.Overlay.Gui", Flat = false)]
    public static string DebugOverlayGuiReport => DebugOverlay.GuiLayersText();

    [PgslCommand("Enabled", "Engine.Debug.Overlay.Wireframe.Enabled = true", "The 3D scene drawn as wireframe while the debug screen is open (false unless set)", "Debug", Namespace = "Engine.Debug.Overlay.Wireframe", Flat = false)]
    public static bool DebugOverlayWireframeEnabled
    {
        get => DebugOverlay.Current?.ShowWireframe ?? false;
        set { if (DebugOverlay.Current is { } overlay) { overlay.ShowWireframe = value; if (value) DebugScreenSettings.Overlay = true; } }
    }

    [PgslCommand("Enabled", "Engine.Debug.Inspect.Enabled = true", "The debug screen's object report (false unless set): click an instance to pick it, or step through them with Next and Previous", "Debug", Namespace = "Engine.Debug.Inspect", Flat = false)]
    public static bool DebugInspectEnabled { get => DebugScreenSettings.Inspect; set => DebugScreenSettings.Inspect = value; }

    [PgslCommand("Instance", "Engine.Debug.Inspect.Instance = id", "The instance the object report is about, 0 for none (0 unless set or picked)", "Debug", Namespace = "Engine.Debug.Inspect", Flat = false)]
    public static double DebugInspectInstance
    {
        get => DebugScreenSettings.InspectInstance;
        set { if (double.IsFinite(value)) DebugScreenSettings.InspectInstance = (int)Math.Clamp(Math.Round(value), 0, int.MaxValue); }
    }

    [PgslCommand("Report", "Engine.Debug.Inspect.Report", "The object report on Engine.Debug.Inspect.Instance, one \"Label: value\" a line: its Object, position, model, materials, shaders, body, level of detail, and whether it is drawn and if not why; empty for none (read)", "Debug", Namespace = "Engine.Debug.Inspect", Flat = false)]
    public static string DebugInspectReport => DebugOverlay.Current?.InspectReportText() ?? string.Empty;

    [PgslCommand("Next", "Engine.Debug.Inspect.Next()", "Report on the next instance in the room (by id), and turn the report on", "Debug", Namespace = "Engine.Debug.Inspect", Flat = false)]
    public static void DebugInspectNext() => DebugOverlay.Current?.StepInspect(1);

    [PgslCommand("Previous", "Engine.Debug.Inspect.Previous()", "Report on the previous instance in the room (by id), and turn the report on", "Debug", Namespace = "Engine.Debug.Inspect", Flat = false)]
    public static void DebugInspectPrevious() => DebugOverlay.Current?.StepInspect(-1);

    [PgslCommand("Enabled", "Engine.Debug.View.Enabled = true", "The frame drawn in a diagnostic way while the debug screen is open (false unless set), as Engine.Debug.View.Mode says", "Debug", Namespace = "Engine.Debug.View", Flat = false)]
    public static bool DebugViewEnabled { get => DebugScreenSettings.View; set => DebugScreenSettings.View = value; }

    [PgslCommand("Mode", "Engine.Debug.View.Mode = \"lod\"", "The diagnostic view (\"lod\" unless set): \"lod\" (models in their level of detail's colour), \"overdraw\", \"depth\", \"normals\", \"shadows\" (the sun's shadow coverage), \"cascades\", \"reflections\", \"lights\"; setting one turns the view on, \"off\" turns it off", "Debug", Namespace = "Engine.Debug.View", Flat = false)]
    public static string DebugViewModePart
    {
        get => DebugScreenSettings.NameOf(DebugScreenSettings.ViewMode);
        set { if (DebugScreenSettings.TryParseMode(value, out Genesis.Runtime.Debugger.DebugViewMode mode)) DebugScreenSettings.ViewMode = mode; }
    }
}
