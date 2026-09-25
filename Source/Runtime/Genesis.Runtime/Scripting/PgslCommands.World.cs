using System;
using System.Collections.Generic;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Scripting;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Scripting;

// Instance queries, sprite/animation control, and room queries.
//
// These are the families that need a live game, so every one guards ActiveGameContext and its World.
// With nothing running they return 0 / "" / false rather than throwing — the auto-tester calls all of
// them cold, and more importantly a designer's script must not crash because it ran a frame early.
//
// Instance identity: PGSL sees an instance as its ECS Entity.Id (an int). Entity 0 is never a valid
// live instance, so 0 doubles as "nothing found", matching the GameMaker noone convention closely
// enough to be unsurprising.
public static partial class PgslCommands
{
    #region Instances

    private static EcsWorld World => ActiveGameContext?.World;

    /// <summary>Every living entity that has a transform — the set a spatial query can consider.</summary>
    private static List<(int Id, float X, float Y)> LivingTransforms()
    {
        List<(int, float, float)> found = [];
        EcsWorld world = World;
        if (world is null) return found;

        foreach (var entry in world.Query<TransformComponent>())
        {
            ref TransformComponent transform = ref world.GetRef<TransformComponent>(entry.Entity);
            found.Add((entry.Entity.Id, transform.X, transform.Y));
        }

        return found;
    }

    /// <summary>Every living entity that has a transform in 3D (X, Y, Z).</summary>
    private static List<(int Id, float X, float Y, float Z)> LivingTransforms3D()
    {
        List<(int, float, float, float)> found = [];
        EcsWorld world = World;
        if (world is null) return found;

        foreach (var entry in world.Query<TransformComponent>())
        {
            ref TransformComponent transform = ref world.GetRef<TransformComponent>(entry.Entity);
            found.Add((entry.Entity.Id, transform.X, transform.Y, transform.Z));
        }

        return found;
    }

    private static bool TryTransform(double instanceId, out Entity entity, out EcsWorld world)
    {
        entity = default;
        world = World;
        if (world is null || !double.IsFinite(instanceId) || instanceId < 0 || instanceId > int.MaxValue || instanceId != Math.Truncate(instanceId)) return false;

        entity = world.GetEntity((int)instanceId);
        return world.IsAlive(entity) && world.Has<TransformComponent>(entity);
    }

    [PgslCommand("InstanceCount", "InstanceCount() -> number", "How many instances are alive", "Instances")]
    public static double InstanceCount() => World?.LivingEntityCount ?? 0;

    [PgslCommand("InstanceGetX", "InstanceGetX(id) -> number", "X of an instance; 0 when it does not exist", "Instances")]
    public static double InstanceGetX(double instanceId) =>
        TryTransform(instanceId, out Entity entity, out EcsWorld world) ? world.GetRef<TransformComponent>(entity).X : 0;

    [PgslCommand("InstanceGetY", "InstanceGetY(id) -> number", "Y of an instance; 0 when it does not exist", "Instances")]
    public static double InstanceGetY(double instanceId) =>
        TryTransform(instanceId, out Entity entity, out EcsWorld world) ? world.GetRef<TransformComponent>(entity).Y : 0;

    [PgslCommand("InstanceGetZ", "InstanceGetZ(id) -> number", "Z of an instance; 0 when it does not exist", "Instances")]
    public static double InstanceGetZ(double instanceId) =>
        TryTransform(instanceId, out Entity entity, out EcsWorld world) ? world.GetRef<TransformComponent>(entity).Z : 0;

    [PgslCommand("InstanceSetX", "InstanceSetX(id, value)", "Move an instance on X", "Instances")]
    public static void InstanceSetX(double instanceId, double value)
    {
        if (TryTransform(instanceId, out Entity entity, out EcsWorld world))
        {
            world.GetRef<TransformComponent>(entity).X = (float)value;
        }
    }

    [PgslCommand("InstanceSetY", "InstanceSetY(id, value)", "Move an instance on Y", "Instances")]
    public static void InstanceSetY(double instanceId, double value)
    {
        if (TryTransform(instanceId, out Entity entity, out EcsWorld world))
        {
            world.GetRef<TransformComponent>(entity).Y = (float)value;
        }
    }

    [PgslCommand("InstanceSetZ", "InstanceSetZ(id, value)", "Move an instance on Z", "Instances")]
    public static void InstanceSetZ(double instanceId, double value)
    {
        if (TryTransform(instanceId, out Entity entity, out EcsWorld world))
        {
            world.GetRef<TransformComponent>(entity).Z = (float)value;
        }
    }

    [PgslCommand("InstanceAlive", "InstanceAlive(id) -> bool", "True when an instance still exists", "Instances")]
    public static bool InstanceAlive(double instanceId)
    {
        EcsWorld world = World;
        if (world is null || instanceId < 1) return false;
        return world.IsAlive(world.GetEntity((int)instanceId));
    }

    [PgslCommand("InstanceNearest", "InstanceNearest(x, y) -> id", "Closest instance to a point; 0 when none", "Instances")]
    public static double InstanceNearest(double x, double y) => NearestOrFurthest(x, y, nearest: true, excludeSelf: false);

    [PgslCommand("InstanceFurthest", "InstanceFurthest(x, y) -> id", "Furthest instance from a point; 0 when none", "Instances")]
    public static double InstanceFurthest(double x, double y) => NearestOrFurthest(x, y, nearest: false, excludeSelf: false);

    [PgslCommand("InstanceNearestOther", "InstanceNearestOther(x, y) -> id", "Closest instance excluding the caller", "Instances")]
    public static double InstanceNearestOther(double x, double y) => NearestOrFurthest(x, y, nearest: true, excludeSelf: true);

    private static double NearestOrFurthest(double x, double y, bool nearest, bool excludeSelf)
    {
        int selfId = excludeSelf ? GetContext()?.InstanceId ?? 0 : 0;
        double best = nearest ? double.MaxValue : double.MinValue;
        int bestId = 0;

        foreach ((int id, float ix, float iy) in LivingTransforms())
        {
            if (excludeSelf && id == selfId) continue;

            double dx = ix - x;
            double dy = iy - y;
            double distanceSquared = (dx * dx) + (dy * dy);
            if (nearest ? distanceSquared < best : distanceSquared > best)
            {
                best = distanceSquared;
                bestId = id;
            }
        }

        return bestId;
    }

    [PgslCommand("InstanceDistanceTo", "InstanceDistanceTo(id, x, y) -> number", "Distance from an instance to a point; -1 when it does not exist", "Instances")]
    public static double InstanceDistanceTo(double instanceId, double x, double y)
    {
        if (!TryTransform(instanceId, out Entity entity, out EcsWorld world)) return -1;
        ref TransformComponent transform = ref world.GetRef<TransformComponent>(entity);
        double dx = transform.X - x;
        double dy = transform.Y - y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    [PgslCommand("InstanceInRadius", "InstanceInRadius(x, y, radius) -> number", "How many instances lie within a radius", "Instances")]
    public static double InstanceInRadius(double x, double y, double radius)
    {
        double limit = radius * radius;
        int count = 0;
        foreach ((int _, float ix, float iy) in LivingTransforms())
        {
            double dx = ix - x;
            double dy = iy - y;
            if ((dx * dx) + (dy * dy) <= limit) count++;
        }

        return count;
    }

    [PgslCommand("InstanceFirstInRadius", "InstanceFirstInRadius(x, y, radius) -> id", "An instance within a radius, excluding the caller; 0 when none", "Instances")]
    public static double InstanceFirstInRadius(double x, double y, double radius)
    {
        int selfId = GetContext()?.InstanceId ?? 0;
        double limit = radius * radius;
        foreach ((int id, float ix, float iy) in LivingTransforms())
        {
            if (id == selfId) continue;
            double dx = ix - x;
            double dy = iy - y;
            if ((dx * dx) + (dy * dy) <= limit) return id;
        }

        return 0;
    }

    #endregion

    #region Collisions

    // Geometry helpers that need no world at all, so they work in an editor preview as readily as in
    // a running game. The instance-aware variants build on the queries above.

    [PgslCommand("CirclesOverlap", "CirclesOverlap(x1, y1, r1, x2, y2, r2) -> bool", "Circle-vs-circle overlap test", "Collision")]
    public static bool CirclesOverlap(double x1, double y1, double r1, double x2, double y2, double r2)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        double reach = r1 + r2;
        return (dx * dx) + (dy * dy) <= reach * reach;
    }

    [PgslCommand("RectsOverlap", "RectsOverlap(ax, ay, aw, ah, bx, by, bw, bh) -> bool", "Axis-aligned rectangle overlap test", "Collision")]
    public static bool RectsOverlap(
        double ax, double ay, double aw, double ah, double bx, double by, double bw, double bh) =>
        ax < bx + bw && ax + aw > bx && ay < by + bh && ay + ah > by;

    [PgslCommand("PointInRect", "PointInRect(px, py, x, y, w, h) -> bool", "Point-in-rectangle test", "Collision")]
    public static bool PointInRect(double px, double py, double x, double y, double w, double h) =>
        px >= x && px <= x + w && py >= y && py <= y + h;

    [PgslCommand("PointInCircle", "PointInCircle(px, py, x, y, radius) -> bool", "Point-in-circle test", "Collision")]
    public static bool PointInCircle(double px, double py, double x, double y, double radius)
    {
        double dx = px - x;
        double dy = py - y;
        return (dx * dx) + (dy * dy) <= radius * radius;
    }

    [PgslCommand("RectContainsRect", "RectContainsRect(ax, ay, aw, ah, bx, by, bw, bh) -> bool", "True when B is fully inside A", "Collision")]
    public static bool RectContainsRect(
        double ax, double ay, double aw, double ah, double bx, double by, double bw, double bh) =>
        bx >= ax && by >= ay && bx + bw <= ax + aw && by + bh <= ay + ah;

    [PgslCommand("CollisionCircle", "CollisionCircle(x, y, radius, objectName?) -> id", "First matching instance whose position falls in a circle, excluding the caller", "Collision")]
    public static double CollisionCircle(double x, double y, double radius, string objectName = null)
    {
        int selfId = GetContext()?.InstanceId ?? 0;
        double limit = radius * radius;
        foreach ((int id, float ix, float iy) in LivingTransforms())
        {
            if (id == selfId || !MatchesObject(id, objectName)) continue;
            double dx = ix - x;
            double dy = iy - y;
            if ((dx * dx) + (dy * dy) <= limit) return id;
        }

        return 0;
    }

    private static bool MatchesObject(int entityId, string requested)
    {
        if (string.IsNullOrWhiteSpace(requested) || string.Equals(requested, "all", StringComparison.OrdinalIgnoreCase)) return true;

        EcsWorld world = World;
        if (world is null) return false;
        Entity entity = world.GetEntity(entityId);
        if (entity.IsNull
            || !ObjectDrawAssetRegistry.TryGet(entity, out ObjectDrawAssetEntry assets)
            || string.IsNullOrWhiteSpace(assets.Prefab))
        {
            return false;
        }

        // One matcher, shared with viewport follow targets, so the two cannot drift apart.
        return Genesis.Runtime.Scene.ObjectNameMatcher.Matches(assets.Prefab, requested);
    }

    [PgslCommand("CollisionPoint", "CollisionPoint(x, y, tolerance) -> id", "First instance at a point within a tolerance", "Collision")]
    public static double CollisionCircleAtPoint(double x, double y, double tolerance) =>
        InstanceFirstInRadius(x, y, Math.Max(0.001, tolerance));

    [PgslCommand("PlaceFreeRadius", "PlaceFreeRadius(x, y, radius) -> bool", "True when no other instance occupies a radius", "Collision")]
    public static bool PlaceFree(double x, double y, double radius) => InstanceFirstInRadius(x, y, radius) == 0;

    [PgslCommand("GetTerrainHeight", "GetTerrainHeight(x, z) -> number", "Sample terrain elevation at world XZ", "Terrain")]
    public static double GetTerrainHeight(double x, double z) =>
        ActiveGameContext?.GetTerrainHeight((float)x, (float)z) ?? 0;

    [PgslCommand("TerrainGetHeight", "TerrainGetHeight(x, z) -> number", "Sample terrain elevation at world XZ (alias)", "Terrain")]
    public static double TerrainGetHeight(double x, double z) => GetTerrainHeight(x, z);

    [PgslCommand("WaterDepthAt", "WaterDepthAt(x, y, z) -> number", "Water depth above a world point, or 0 outside water", "Terrain")]
    public static double WaterDepthAt(double x, double y, double z)
    {
        Genesis.Physics.PhysicsWaterVolume water = ActiveGameContext?.Scene?.FindWater(new System.Numerics.Vector3((float)x, (float)y, (float)z));
        return water == null ? 0 : Math.Max(0, water.SurfaceY - y);
    }

    [PgslCommand("WaterSurfaceAt", "WaterSurfaceAt(x, z) -> number", "Authored water surface height, or terrain height when no water exists", "Terrain")]
    public static double WaterSurfaceAt(double x, double z)
    {
        Genesis.Physics.PhysicsWaterVolume water = ActiveGameContext?.Scene?.FindWater((float)x, (float)z);
        return water?.SurfaceY ?? GetTerrainHeight(x, z);
    }

    [PgslCommand("WaterIsSwimmable", "WaterIsSwimmable(x, y, z) -> bool", "True when a point is inside a swimmable water volume", "Terrain")]
    public static bool WaterIsSwimmable(double x, double y, double z) =>
        ActiveGameContext?.Scene?.FindWater(new System.Numerics.Vector3((float)x, (float)y, (float)z))?.Swimmable == true;

    [PgslCommand("WaterFlowX", "WaterFlowX(x, y, z) -> number", "Water current X velocity at a point", "Terrain")]
    public static double WaterFlowX(double x, double y, double z) =>
        ActiveGameContext?.Scene?.FindWater(new System.Numerics.Vector3((float)x, (float)y, (float)z))?.FlowVelocity.X ?? 0;

    [PgslCommand("WaterFlowZ", "WaterFlowZ(x, y, z) -> number", "Water current Z velocity at a point", "Terrain")]
    public static double WaterFlowZ(double x, double y, double z) =>
        ActiveGameContext?.Scene?.FindWater(new System.Numerics.Vector3((float)x, (float)y, (float)z))?.FlowVelocity.Z ?? 0;

    [PgslCommand("CharacterIsSwimming", "CharacterIsSwimming() -> bool", "True when this character motor is swimming", "Physics")]
    public static bool CharacterIsSwimming()
    {
        EcsWorld world = World;
        int id = GetContext()?.InstanceId ?? -1;
        Entity entity = world?.GetEntity(id) ?? Entity.Null;
        return !entity.IsNull && world.Has<CharacterMotorComponent>(entity) &&
            world.GetRef<CharacterMotorComponent>(entity).State == CharacterMotorState.Swimming;
    }

    [PgslCommand("CharacterSubmerged", "CharacterSubmerged() -> number", "Submerged fraction of this character from 0 to 1", "Physics")]
    public static double CharacterSubmerged()
    {
        EcsWorld world = World;
        int id = GetContext()?.InstanceId ?? -1;
        Entity entity = world?.GetEntity(id) ?? Entity.Null;
        return !entity.IsNull && world.Has<CharacterMotorComponent>(entity)
            ? world.GetRef<CharacterMotorComponent>(entity).SubmergedFraction
            : 0;
    }

    [PgslCommand("CharacterEnteredWater", "CharacterEnteredWater() -> bool", "True for the step this character enters swimmable water", "Physics")]
    public static bool CharacterEnteredWater() => CurrentCharacterMotor()?.EnteredWater == true;

    [PgslCommand("CharacterExitedWater", "CharacterExitedWater() -> bool", "True for the step this character exits swimmable water", "Physics")]
    public static bool CharacterExitedWater() => CurrentCharacterMotor()?.ExitedWater == true;

    private static CharacterMotorComponent? CurrentCharacterMotor()
    {
        EcsWorld world = World;
        int id = GetContext()?.InstanceId ?? -1;
        Entity entity = world?.GetEntity(id) ?? Entity.Null;
        return !entity.IsNull && world.Has<CharacterMotorComponent>(entity)
            ? world.GetRef<CharacterMotorComponent>(entity)
            : null;
    }

    [PgslCommand("CollisionCircle3D", "CollisionCircle3D(x, z, radius, objectName?) -> id", "First matching instance whose position falls in an XZ circle, excluding the caller", "Collision")]
    public static double CollisionCircle3D(double x, double z, double radius, string objectName = null)
    {
        int selfId = GetContext()?.InstanceId ?? 0;
        double limit = radius * radius;
        foreach ((int id, float ix, float iy, float iz) in LivingTransforms3D())
        {
            if (id == selfId || !MatchesObject(id, objectName)) continue;
            double dx = ix - x;
            double dz = iz - z;
            if ((dx * dx) + (dz * dz) <= limit) return id;
        }

        return 0;
    }

    [PgslCommand("PlaceFree3D", "PlaceFree3D(x, z, radius, objectName?) -> bool", "True when no other instance occupies an XZ radius", "Collision")]
    public static bool PlaceFree3D(double x, double z, double radius, string objectName = null) =>
        CollisionCircle3D(x, z, radius, objectName) == 0;

    [PgslCommand("CollisionDistance3D", "CollisionDistance3D(x, z, objectName?) -> number", "Distance on XZ plane to nearest matching instance, or -1 if none found", "Collision")]
    public static double CollisionDistance3D(double x, double z, string objectName = null)
    {
        int selfId = GetContext()?.InstanceId ?? 0;
        double minDistanceSq = double.MaxValue;
        bool found = false;
        foreach ((int id, float ix, float iy, float iz) in LivingTransforms3D())
        {
            if (id == selfId || !MatchesObject(id, objectName)) continue;
            double dx = ix - x;
            double dz = iz - z;
            double distSq = (dx * dx) + (dz * dz);
            if (distSq < minDistanceSq)
            {
                minDistanceSq = distSq;
                found = true;
            }
        }

        return found ? Math.Sqrt(minDistanceSq) : -1;
    }

    #endregion

    #region Sprites

    // Sprite and animation state all lives on PgslContext already; these are the command surface.

    [PgslCommand("SpriteSet", "SpriteSet(name)", "Bind the instance's sprite by resource name", "Sprites")]
    public static void SpriteSet(string name)
    {
        PgslContext ctx = GetContext();
        if (ctx is not null) ctx.SpriteIndex = name ?? string.Empty;
    }

    [PgslCommand("SpriteGet", "SpriteGet() -> string", "The instance's current sprite name", "Sprites")]
    public static string SpriteGet() => GetContext()?.SpriteIndex ?? string.Empty;

    [PgslCommand("SpriteSetFrame", "SpriteSetFrame(index)", "Jump to an animation frame", "Sprites")]
    public static void SpriteSetFrame(double index)
    {
        PgslContext ctx = GetContext();
        if (ctx is not null) ctx.ImageIndex = Math.Max(0, index);
    }

    [PgslCommand("SpriteGetFrame", "SpriteGetFrame() -> number", "Current animation frame", "Sprites")]
    public static double SpriteGetFrame() => GetContext()?.ImageIndex ?? 0;

    [PgslCommand("SpriteSetSpeed", "SpriteSetSpeed(speed)", "Frames advanced per game frame", "Sprites")]
    public static void SpriteSetSpeed(double speed)
    {
        PgslContext ctx = GetContext();
        if (ctx is not null)
        {
            ctx.ImageSpeed = speed;
            ctx.SpriteAnimationSpeed = speed;
            ctx.SpriteAnimationActive = speed != 0;
        }
    }

    [PgslCommand("SpriteGetSpeed", "SpriteGetSpeed() -> number", "Frames advanced per game frame", "Sprites")]
    public static double SpriteGetSpeed() => GetContext()?.ImageSpeed ?? 0;

    [PgslCommand("SpriteSetScale", "SpriteSetScale(xscale, yscale)", "Scale the sprite", "Sprites")]
    public static void SpriteSetScale(double xscale, double yscale)
    {
        PgslContext ctx = GetContext();
        if (ctx is null) return;
        ctx.ImageXScale = xscale;
        ctx.ImageYScale = yscale;
    }

    [PgslCommand("SpriteSetAngle", "SpriteSetAngle(degrees)", "Rotate the sprite", "Sprites")]
    public static void SpriteSetAngle(double degrees)
    {
        PgslContext ctx = GetContext();
        if (ctx is not null) ctx.ImageAngle = degrees;
    }

    [PgslCommand("SpriteGetAngle", "SpriteGetAngle() -> number", "Sprite rotation in degrees", "Sprites")]
    public static double SpriteGetAngle() => GetContext()?.ImageAngle ?? 0;

    [PgslCommand("SpriteSetAlpha", "SpriteSetAlpha(alpha)", "Sprite opacity, 0-1", "Sprites")]
    public static void SpriteSetAlpha(double alpha)
    {
        PgslContext ctx = GetContext();
        if (ctx is not null) ctx.ImageAlpha = Math.Clamp(alpha, 0, 1);
    }

    [PgslCommand("SpriteSetBlend", "SpriteSetBlend(r, g, b)", "Tint the sprite from 0-255 channels", "Sprites")]
    public static void SpriteSetBlend(double r, double g, double b)
    {
        PgslContext ctx = GetContext();
        if (ctx is null) return;
        ctx.ImageBlend = System.Drawing.Color.FromArgb(
            (int)Math.Clamp(r, 0, 255),
            (int)Math.Clamp(g, 0, 255),
            (int)Math.Clamp(b, 0, 255));
    }

    [PgslCommand("SpriteSetVisible", "SpriteSetVisible(visible)", "Show or hide the instance", "Sprites")]
    public static void SpriteSetVisible(bool visible)
    {
        PgslContext ctx = GetContext();
        if (ctx is not null) ctx.Visible = visible;
    }

    [PgslCommand("SpriteGetVisible", "SpriteGetVisible() -> bool", "Whether the instance is drawn", "Sprites")]
    public static bool SpriteGetVisible() => GetContext()?.Visible ?? false;

    [PgslCommand("SpriteSetDepth", "SpriteSetDepth(depth)", "Draw order; lower draws in front", "Sprites")]
    public static void SpriteSetDepth(double depth)
    {
        PgslContext ctx = GetContext();
        if (ctx is not null) ctx.Depth = (int)depth;
    }

    [PgslCommand("SpriteGetDepth", "SpriteGetDepth() -> number", "Draw order", "Sprites")]
    public static double SpriteGetDepth() => GetContext()?.Depth ?? 0;

    #endregion

    #region Animation

    [PgslCommand("AnimationPlay", "AnimationPlay(tag, loop)", "Start a named animation clip", "Animation")]
    public static void AnimationPlay(string tag, bool loop)
    {
        PgslContext ctx = GetContext();
        if (ctx is null) return;
        ctx.SpriteAnimationTag = tag ?? string.Empty;
        ctx.SpriteAnimationLoop = loop;
        ctx.SpriteAnimationActive = !string.IsNullOrEmpty(tag);
        if (ctx.SpriteAnimationActive && ctx.SpriteAnimationSpeed == 0)
            ctx.SpriteAnimationSpeed = ctx.ImageSpeed == 0 ? 1 : ctx.ImageSpeed;
        if (ctx.SpriteAnimationActive)
            ctx.ImageSpeed = ctx.SpriteAnimationSpeed;
    }

    [PgslCommand("AnimationStop", "AnimationStop()", "Halt the current clip", "Animation")]
    public static void AnimationStop()
    {
        PgslContext ctx = GetContext();
        if (ctx is not null)
        {
            ctx.SpriteAnimationActive = false;
            ctx.ImageSpeed = 0;
        }
    }

    [PgslCommand("AnimationIsPlaying", "AnimationIsPlaying() -> bool", "True while a clip is running", "Animation")]
    public static bool AnimationIsPlaying() => GetContext()?.SpriteAnimationActive ?? false;

    [PgslCommand("AnimationGetTag", "AnimationGetTag() -> string", "Name of the current clip", "Animation")]
    public static string AnimationGetTag() => GetContext()?.SpriteAnimationTag ?? string.Empty;

    [PgslCommand("AnimationSetSpeed", "AnimationSetSpeed(speed)", "Clip playback rate multiplier", "Animation")]
    public static void AnimationSetSpeed(double speed)
    {
        PgslContext ctx = GetContext();
        if (ctx is not null)
        {
            ctx.SpriteAnimationSpeed = speed;
            ctx.ImageSpeed = speed;
            ctx.SpriteAnimationActive = speed != 0;
        }
    }

    [PgslCommand("AnimationGetSpeed", "AnimationGetSpeed() -> number", "Clip playback rate multiplier", "Animation")]
    public static double AnimationGetSpeed() => GetContext()?.SpriteAnimationSpeed ?? 0;

    [PgslCommand("AnimationSetLoop", "AnimationSetLoop(loop)", "Whether the clip repeats", "Animation")]
    public static void AnimationSetLoop(bool loop)
    {
        PgslContext ctx = GetContext();
        if (ctx is not null) ctx.SpriteAnimationLoop = loop;
    }

    #endregion

    #region Rooms

    private static RoomAsset Room => ActiveGameContext?.Room;

    [PgslCommand("RoomGoto", "RoomGoto(roomName)", "Change to another room", "Rooms")]
    public static void RoomGoto(string roomName)
    {
        if (!string.IsNullOrWhiteSpace(roomName)) ActiveGameContext?.ChangeRoom(roomName);
    }

    [PgslCommand("RoomRestart", "RoomRestart()", "Re-enter the current room", "Rooms")]
    public static void RoomRestart()
    {
        string name = Room?.Name;
        if (!string.IsNullOrWhiteSpace(name)) ActiveGameContext?.ChangeRoom(name);
    }

    [PgslCommand("RoomGetName", "RoomGetName() -> string", "Name of the current room", "Rooms")]
    public static string RoomGetName() => Room?.Name ?? string.Empty;

    [PgslCommand("RoomGetWidth", "RoomGetWidth() -> number", "Room width in world units", "Rooms")]
    public static double RoomGetWidth() => Room?.Settings?.Width ?? GetContext()?.RoomWidth ?? 0;

    [PgslCommand("RoomGetHeight", "RoomGetHeight() -> number", "Room height in world units", "Rooms")]
    public static double RoomGetHeight() => Room?.Settings?.Height ?? GetContext()?.RoomHeight ?? 0;

    [PgslCommand("RoomIsThreeD", "RoomIsThreeD() -> bool", "True when the room is a 3D room", "Rooms")]
    public static bool RoomIsThreeD() => Room?.Dimension == RoomDimension.ThreeD;

    [PgslCommand("RoomLayerCount", "RoomLayerCount() -> number", "How many layers the room has", "Rooms")]
    public static double RoomLayerCount() => Room?.Layers?.Count ?? 0;

    [PgslCommand("RoomNodeCount", "RoomNodeCount() -> number", "How many nodes the room defines", "Rooms")]
    public static double RoomNodeCount() => Room?.Nodes?.Count ?? 0;

    [PgslCommand("RoomGetGridSize", "RoomGetGridSize() -> number", "The room's editing grid size", "Rooms")]
    public static double RoomGetGridSize() => Room?.Settings?.GridSize ?? 0;

    #endregion
}
