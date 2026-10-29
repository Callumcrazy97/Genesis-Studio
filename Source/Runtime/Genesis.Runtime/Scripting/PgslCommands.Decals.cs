using System;
using System.Numerics;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Decals: pictures projected onto the world (bullet holes, scorch marks, blood) from any event. The
// engine keeps them, draws them every frame on whatever opaque surface is there (walls, models,
// terrain), fades them out at the end of their life and makes room for new ones by removing the
// oldest. Colour is red, green and blue from 0 to 255 and alpha from 0 to 1, as DrawSetColorRgb and
// DrawSetAlpha take a colour.
public static partial class PgslCommands
{
    [PgslCommand("DecalAdd", "DecalAdd(image, x, y, z, nx, ny, nz, size, life) -> decal",
        "Put a picture on the world at x, y, z facing along the surface normal nx, ny, nz (a bullet hole where a ray hit, a scorch mark under a blast): size across, projected onto whatever opaque surface is there, walls, models and terrain alike, and not onto surfaces turned away. It lasts life seconds, fading out over the end (0: until removed). image is an Image resource or a texture from TextureCreate. Multiplied over the surface by default, so it keeps the surface's light. 0 when it could not be placed",
        "Decals", Namespace = "Engine.Rendering.Decals")]
    public static double DecalAdd(string image, double x, double y, double z, double nx, double ny, double nz, double size, double life)
    {
        if (!Finite3(x, y, z) || !Finite3(nx, ny, nz) || !double.IsFinite(size) || !double.IsFinite(life)) return 0;
        return WorldDecals.Add(image, new Vector3((float)x, (float)y, (float)z),
            new Vector3((float)nx, (float)ny, (float)nz), (float)size, (float)life);
    }

    [PgslCommand("DecalFollow", "DecalFollow(decal, instance) -> following",
        "Keep a decal on an object that moves (a door, a cart, a crate): it moves and turns with it from now on and goes when the object does. False when there is no such decal or object",
        "Decals", Namespace = "Engine.Rendering.Decals")]
    public static bool DecalFollow(double decal, double instance)
    {
        if (!double.IsFinite(decal) || !double.IsFinite(instance)) return false;
        Matrix4x4? world = WorldDecals.ObjectWorld(ActiveGameContext?.World as Genesis.Runtime.ECS.World, (int)instance);
        if (world is not { } placed) return false;
        // An instance making a decal follow itself mid-event: its script's x, y, z are not on its
        // transform until the event ends (a shadow made in Create after moving there).
        if (GetContext() is { } ctx && ctx.InstanceId == (int)instance
            && TryTransform(instance, out Genesis.Shared.ECS.Entity entity, out Genesis.Runtime.ECS.World ecs))
        {
            ref Genesis.Runtime.ECS.Components.TransformComponent t = ref ecs.GetRef<Genesis.Runtime.ECS.Components.TransformComponent>(entity);
            placed = WorldDecals.AtScriptPosition(placed, new Vector3(t.X, t.Y, t.Z), new Vector3((float)ctx.X, (float)ctx.Y, (float)ctx.Z));
        }
        return WorldDecals.Follow((int)decal, (int)instance, placed);
    }

    [PgslCommand("DecalUnfollow", "DecalUnfollow(decal) -> changed", "Stop a decal following its object; it stays where it is",
        "Decals", Namespace = "Engine.Rendering.Decals")]
    public static bool DecalUnfollow(double decal) => double.IsFinite(decal) && WorldDecals.Unfollow((int)decal);
    [PgslCommand("DecalSetColor", "DecalSetColor(decal, r, g, b, alpha) -> changed",
        "Tint a decal (0-255 each; white leaves the picture as it is) and set its opacity (0-1)",
        "Decals", Namespace = "Engine.Rendering.Decals")]
    public static bool DecalSetColor(double decal, double r, double g, double b, double alpha) =>
        IsDecalId(decal) && Finite3(r, g, b) && double.IsFinite(alpha)
        && WorldDecals.SetColor((int)decal,
            new Vector3((float)Math.Clamp(r, 0, 255) / 255f, (float)Math.Clamp(g, 0, 255) / 255f, (float)Math.Clamp(b, 0, 255) / 255f),
            (float)alpha);

    [PgslCommand("DecalSetRotation", "DecalSetRotation(decal, degrees) -> changed",
        "Turn a decal's picture about its normal, anticlockwise as seen from the front (a random turn makes bullet holes look less alike)",
        "Decals", Namespace = "Engine.Rendering.Decals")]
    public static bool DecalSetRotation(double decal, double degrees) =>
        IsDecalId(decal) && double.IsFinite(degrees) && WorldDecals.SetRotation((int)decal, (float)degrees);

    [PgslCommand("DecalSetSize", "DecalSetSize(decal, width, height, depth) -> changed",
        "A decal's width and height, and how deep through the surface it reaches (half its size at first: deeper reaches bumpier ground, but also anything standing on it)",
        "Decals", Namespace = "Engine.Rendering.Decals")]
    public static bool DecalSetSize(double decal, double width, double height, double depth) =>
        IsDecalId(decal) && Finite3(width, height, depth) && WorldDecals.SetSize((int)decal, (float)width, (float)height, (float)depth);

    [PgslCommand("DecalSetBlend", "DecalSetBlend(decal, blend) -> changed",
        "How a decal goes over the surface: \"multiply\" (the default: darkens and tints and keeps the surface's light, for holes, scorch, blood, dirt), \"alpha\" (paints its own colours, unlit, for signs and paint) or \"add\" (adds light, for embers and glowing marks)",
        "Decals", Namespace = "Engine.Rendering.Decals")]
    public static bool DecalSetBlend(double decal, string blend)
    {
        if (!IsDecalId(decal)) return false;
        DecalBlend mode;
        switch ((blend ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "multiply": case "mul": case "": mode = DecalBlend.Multiply; break;
            case "alpha": case "paint": case "normal": mode = DecalBlend.Alpha; break;
            case "add": case "additive": case "glow": mode = DecalBlend.Additive; break;
            default: return false;
        }
        return WorldDecals.SetBlend((int)decal, mode);
    }

    [PgslCommand("DecalSetSeeThrough", "DecalSetSeeThrough(decal, onSeeThrough) -> changed",
        "Put a decal on a see-through surface (a bullet hole in a window pane): drawn as a flat picture in its own plane after glass and other see-through draws, seen from both sides and hidden only by opaque things in front, instead of projected onto the opaque world. Off by default",
        "Decals", Namespace = "Engine.Rendering.Decals")]
    public static bool DecalSetSeeThrough(double decal, bool onSeeThrough) =>
        IsDecalId(decal) && WorldDecals.SetSeeThrough((int)decal, onSeeThrough);

    [PgslCommand("DecalSetLayer", "DecalSetLayer(decal, layer) -> changed",
        "Put a decal on a layer, 0 to 5 (0 at first, or Engine.Decal.Layer): it shows only on surfaces that take that layer (Engine.Model.DecalLayers; every surface takes every layer unless set), so bullet holes can leave characters alone. False for a layer out of range",
        "Decals", Namespace = "Engine.Rendering.Decals")]
    public static bool DecalSetLayer(double decal, double layer) =>
        IsDecalId(decal) && double.IsFinite(layer) && WorldDecals.SetLayer((int)decal, (int)Math.Round(layer));

    [PgslCommand("DecalSetNormalMap", "DecalSetNormalMap(decal, image) -> changed",
        "Give a decal a normal map (an Image resource or a texture from TextureCreate, its bumps in tangent space, up the picture is +Y): the surface under it is re-shaded by the bumps in the room's sun and ambient light, a dent or raised paint. \"\" removes it",
        "Decals", Namespace = "Engine.Rendering.Decals")]
    public static bool DecalSetNormalMap(double decal, string image) =>
        IsDecalId(decal) && WorldDecals.SetNormalMap((int)decal, image);

    [PgslCommand("DecalSetRoughnessMap", "DecalSetRoughnessMap(decal, image) -> changed",
        "Give a decal a roughness map (red: 0 mirror-smooth to 1 matte): the sun's highlight on the decal is added at that roughness, for wet blood, fresh paint or oil. It adds a sheen; it cannot take away the surface's own. \"\" removes it",
        "Decals", Namespace = "Engine.Rendering.Decals")]
    public static bool DecalSetRoughnessMap(double decal, string image) =>
        IsDecalId(decal) && WorldDecals.SetRoughnessMap((int)decal, image);

    [PgslCommand("Enabled", "Engine.Decal.Enabled = true",
        "Whether decals are drawn; turned off they are kept and come back when turned on. On at each room's start",
        "Decals", Namespace = "Engine.Decal", Flat = false)]
    public static bool DecalApiEnabled
    {
        get => WorldDecals.Enabled;
        set => WorldDecals.Enabled = value;
    }

    [PgslCommand("Layer", "Engine.Decal.Layer = layer",
        "The layer a new decal goes on, 0 to 5 (0 at each room's start); DecalSetLayer moves one decal",
        "Decals", Namespace = "Engine.Decal", Flat = false)]
    public static double DecalApiLayer
    {
        get => WorldDecals.DefaultLayer;
        set { if (double.IsFinite(value)) WorldDecals.DefaultLayer = (int)Math.Round(value); }
    }

    [PgslCommand("Limit", "Engine.Decal.Limit = count",
        "How many decals may be in the world at once (256 at first, at most 4096): adding past it removes the oldest",
        "Decals", Namespace = "Engine.Decal", Flat = false)]
    public static double DecalApiLimit
    {
        get => WorldDecals.Limit;
        set => DecalSetLimit(value);
    }

    [PgslCommand("DecalSetFade", "DecalSetFade(decal, seconds) -> changed",
        "How many seconds at the end of its life a decal takes to fade out (by default the last quarter of its life, at most 2 seconds; less than 0 goes back to that)",
        "Decals", Namespace = "Engine.Rendering.Decals")]
    public static bool DecalSetFade(double decal, double seconds) =>
        IsDecalId(decal) && double.IsFinite(seconds) && WorldDecals.SetFade((int)decal, (float)seconds);

    [PgslCommand("DecalSetLife", "DecalSetLife(decal, seconds) -> changed",
        "Give a decal a new life from now (0: until removed)", "Decals", Namespace = "Engine.Rendering.Decals")]
    public static bool DecalSetLife(double decal, double seconds) =>
        IsDecalId(decal) && double.IsFinite(seconds) && WorldDecals.SetLife((int)decal, (float)seconds);

    [PgslCommand("DecalMove", "DecalMove(decal, x, y, z, nx, ny, nz) -> moved",
        "Move a decal and turn it to a new normal. Decals stay where they were put in the world: one on a moving object follows it only when moved like this each frame",
        "Decals", Namespace = "Engine.Rendering.Decals")]
    public static bool DecalMove(double decal, double x, double y, double z, double nx, double ny, double nz) =>
        IsDecalId(decal) && Finite3(x, y, z) && Finite3(nx, ny, nz)
        && WorldDecals.Move((int)decal, new Vector3((float)x, (float)y, (float)z), new Vector3((float)nx, (float)ny, (float)nz));

    [PgslCommand("DecalRemove", "DecalRemove(decal) -> removed",
        "Remove a decal at once; false when it is gone already", "Decals", Namespace = "Engine.Rendering.Decals")]
    public static bool DecalRemove(double decal) => IsDecalId(decal) && WorldDecals.Remove((int)decal);

    [PgslCommand("DecalExists", "DecalExists(decal) -> exists",
        "Whether a decal is still in the world", "Decals", Namespace = "Engine.Rendering.Decals")]
    public static bool DecalExists(double decal) => IsDecalId(decal) && WorldDecals.Exists((int)decal);

    [PgslCommand("DecalClear", "DecalClear()",
        "Remove every decal (a room change does this too)", "Decals", Namespace = "Engine.Rendering.Decals")]
    public static void DecalClear() => WorldDecals.Clear();

    [PgslCommand("DecalSetLimit", "DecalSetLimit(count)",
        "How many decals may be in the world at once (256 at first, at most 4096): adding past it removes the oldest, as lowering it does",
        "Decals", Namespace = "Engine.Rendering.Decals")]
    public static void DecalSetLimit(double count)
    {
        if (double.IsFinite(count)) WorldDecals.SetLimit((int)Math.Clamp(Math.Round(count), 0, WorldDecals.MaxLimit));
    }

    [PgslCommand("DecalCount", "DecalCount() -> count",
        "How many decals are in the world now", "Decals", Namespace = "Engine.Rendering.Decals")]
    public static double DecalCount() => WorldDecals.Count;

    private static bool IsDecalId(double id) => double.IsFinite(id) && id >= 1 && id <= int.MaxValue;
}
