using System;
using System.Drawing;
using System.Numerics;
using Genesis.Rendering.D3dMath;
using Genesis.Runtime.Assets;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Spatial;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Debugger;

/// <summary>What a drawn collider is, each in its own colour.</summary>
public enum DebugColliderKind
{
    StaticBody,
    DynamicBody,
    KinematicBody,
    Character,
    Trigger,
    TileSolid,
    Hitbox,
}

/// <summary>
/// Draws the colliders the game's collisions use over the frame, as wireframes in 3D and outlines in
/// 2D: physics bodies (static, moving, kinematic), characters, triggers, a 2D room's solid tiles and
/// its instances' sprite hitboxes (the mask box collisions test, and trigger shapes). Only runs while
/// the debug screen shows colliders.
/// </summary>
internal sealed class DebugColliderOverlay
{
    /// <summary>More than this many shapes in a frame are counted, not drawn.</summary>
    public const int MaxShapes = 4000;

    /// <summary>Mesh colliders with more triangles than this are drawn as their bounding box.</summary>
    public const int MaxMeshTriangles = 2000;

    private const float Thickness = 1.5f;
    private const int CircleSegments = 20;

    private readonly int[] _counts = new int[7];
    private int _drawn, _skipped;

    /// <summary>The shapes of each kind drawn in the last frame.</summary>
    public int Count(DebugColliderKind kind) => _counts[(int)kind];

    /// <summary>All shapes drawn in the last frame, and those over <see cref="MaxShapes"/>.</summary>
    public int Drawn => _drawn;
    public int Skipped => _skipped;

    public static Vector4 Colour(DebugColliderKind kind) => kind switch
    {
        DebugColliderKind.StaticBody => new Vector4(0.55f, 0.68f, 0.95f, 0.95f),
        DebugColliderKind.DynamicBody => new Vector4(0.30f, 0.95f, 0.40f, 0.95f),
        DebugColliderKind.KinematicBody => new Vector4(0.95f, 0.88f, 0.25f, 0.95f),
        DebugColliderKind.Character => new Vector4(0.98f, 0.35f, 0.95f, 1f),
        DebugColliderKind.Trigger => new Vector4(1f, 0.55f, 0.12f, 1f),
        DebugColliderKind.TileSolid => new Vector4(0.98f, 0.25f, 0.25f, 0.9f),
        _ => new Vector4(0.20f, 0.88f, 1f, 1f),
    };

    public static string Label(DebugColliderKind kind) => kind switch
    {
        DebugColliderKind.StaticBody => "Static body",
        DebugColliderKind.DynamicBody => "Moving body",
        DebugColliderKind.KinematicBody => "Kinematic body",
        DebugColliderKind.Character => "Character",
        DebugColliderKind.Trigger => "Trigger",
        DebugColliderKind.TileSolid => "Solid tile",
        _ => "Sprite hitbox",
    };

    private static bool Wanted(DebugColliderKind kind) => kind switch
    {
        DebugColliderKind.Character => DebugScreenSettings.ColliderCharacters,
        DebugColliderKind.Trigger => DebugScreenSettings.ColliderTriggers,
        DebugColliderKind.TileSolid => DebugScreenSettings.ColliderTiles,
        DebugColliderKind.Hitbox => DebugScreenSettings.ColliderHitboxes,
        _ => DebugScreenSettings.ColliderBodies,
    };

    public void Draw(IHudCanvas hud, RuntimeScene scene, int width, int height)
    {
        Array.Clear(_counts);
        _drawn = _skipped = 0;
        if (hud == null || scene?.World == null) return;
        DebugProjection projection = DebugProjection.For(scene, width, height);
        if (projection.Is2D) Draw2D(hud, scene, projection);
        else if (projection.Has3D) Draw3D(hud, scene, projection);
    }

    private bool Take(DebugColliderKind kind)
    {
        if (!Wanted(kind)) return false;
        if (_drawn >= MaxShapes) { _skipped++; return false; }
        _drawn++;
        _counts[(int)kind]++;
        return true;
    }

    private static DebugColliderKind KindOf(EcsWorld world, Entity entity, in RigidBodyComponent body)
    {
        if (body.IsSensor) return DebugColliderKind.Trigger;
        if (world.Has<CharacterMotorComponent>(entity) || world.Has<PhysicsPlayerTag>(entity)) return DebugColliderKind.Character;
        return body.Motion switch
        {
            PhysicsMotionType.Dynamic => DebugColliderKind.DynamicBody,
            PhysicsMotionType.Kinematic => DebugColliderKind.KinematicBody,
            _ => DebugColliderKind.StaticBody,
        };
    }

    // Where a body is: its physics pose (Transform3D) when it has one, else its Object's transform.
    private static bool TryPose(EcsWorld world, Entity entity, out Vector3 position, out Quaternion rotation)
    {
        if (world.Has<Transform3DComponent>(entity))
        {
            ref Transform3DComponent transform = ref world.GetRef<Transform3DComponent>(entity);
            position = transform.Position;
            rotation = transform.Rotation.LengthSquared() > 1e-8f ? Quaternion.Normalize(transform.Rotation) : Quaternion.Identity;
            return true;
        }
        if (world.Has<TransformComponent>(entity))
        {
            ref TransformComponent transform = ref world.GetRef<TransformComponent>(entity);
            position = new Vector3(transform.X, transform.Y, transform.Z);
            const float toRadians = MathF.PI / 180f;
            rotation = Quaternion.CreateFromYawPitchRoll(transform.RotationY * toRadians, transform.RotationX * toRadians, transform.RotationZ * toRadians);
            return true;
        }
        position = default;
        rotation = Quaternion.Identity;
        return false;
    }

    // ── 3D ─────────────────────────────────────────────────────────────────

    private void Draw3D(IHudCanvas hud, RuntimeScene scene, in DebugProjection projection)
    {
        EcsWorld world = scene.World;
        Frustum frustum = new(projection.ViewProjection);
        foreach (var item in world.Query<RigidBodyComponent>())
        {
            ref RigidBodyComponent body = ref item.C1;
            if (!body.Collision || body.Dormant) continue;
            Entity entity = item.Entity;
            DebugColliderKind kind = KindOf(world, entity, body);
            if (!Wanted(kind) || !TryPose(world, entity, out Vector3 position, out Quaternion rotation)) continue;
            Vector3 centre = position + Vector3.Transform(body.LocalOffset, rotation);
            float reach = body.Shape is CollisionShape.Mesh or CollisionShape.ConvexHull
                ? MeshReach(world, entity) : body.HalfExtents.Length();
            if (!frustum.ContainsSphere(centre, Math.Max(0.05f, reach)) || !Take(kind)) continue;
            Vector4 colour = Colour(kind);
            switch (body.Shape)
            {
                case CollisionShape.Sphere:
                    Sphere(hud, projection, centre, rotation, Math.Max(0.01f, body.Size.X), colour);
                    break;
                case CollisionShape.Capsule:
                    Capsule(hud, projection, centre, rotation, Math.Max(0.01f, body.Size.X), Math.Max(0.01f, body.Size.Y), colour);
                    break;
                case CollisionShape.Cylinder:
                    Cylinder(hud, projection, centre, rotation, Math.Max(0.01f, body.Size.X), Math.Max(0.01f, body.Size.Y), colour);
                    break;
                case CollisionShape.Mesh or CollisionShape.ConvexHull:
                    Mesh(hud, projection, world, entity, centre, rotation, colour);
                    break;
                default:
                    Box(hud, projection, centre, rotation, body.Size, colour);
                    break;
            }
        }
    }

    private static float MeshReach(EcsWorld world, Entity entity)
    {
        if (!world.Has<MeshColliderComponent>(entity)) return 1f;
        ref MeshColliderComponent mesh = ref world.GetRef<MeshColliderComponent>(entity);
        if (mesh.Vertices is not { Length: > 0 }) return 1f;
        Vector3 scale = mesh.Scale == Vector3.Zero ? Vector3.One : mesh.Scale;
        float reach = 0f;
        foreach (Vector3 vertex in mesh.Vertices) reach = MathF.Max(reach, (vertex * scale).LengthSquared());
        return MathF.Sqrt(reach);
    }

    private static void Segment(IHudCanvas hud, in DebugProjection projection, Vector3 a, Vector3 b, Vector4 colour)
    {
        if (projection.TrySegment(a, b, out Vector2 sa, out Vector2 sb)) hud.Line(sa.X, sa.Y, sb.X, sb.Y, colour, Thickness);
    }

    private static void Box(IHudCanvas hud, in DebugProjection projection, Vector3 centre, Quaternion rotation, Vector3 half, Vector4 colour)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        for (int index = 0; index < 8; index++)
        {
            Vector3 local = new((index & 1) == 0 ? -half.X : half.X, (index & 2) == 0 ? -half.Y : half.Y, (index & 4) == 0 ? -half.Z : half.Z);
            corners[index] = centre + Vector3.Transform(local, rotation);
        }
        for (int index = 0; index < 8; index++)
        {
            if ((index & 1) == 0) Segment(hud, projection, corners[index], corners[index | 1], colour);
            if ((index & 2) == 0) Segment(hud, projection, corners[index], corners[index | 2], colour);
            if ((index & 4) == 0) Segment(hud, projection, corners[index], corners[index | 4], colour);
        }
    }

    // A circle of the given radius round centre, in the plane of the two axes.
    private static void Circle(IHudCanvas hud, in DebugProjection projection, Vector3 centre, Vector3 axisA, Vector3 axisB,
        float radius, Vector4 colour, float startTurn = 0f, float turns = 1f)
    {
        int segments = Math.Max(4, (int)MathF.Ceiling(CircleSegments * turns));
        Vector3 previous = default;
        for (int step = 0; step <= segments; step++)
        {
            float angle = (startTurn + turns * step / segments) * MathF.Tau;
            Vector3 point = centre + (axisA * MathF.Cos(angle) + axisB * MathF.Sin(angle)) * radius;
            if (step > 0) Segment(hud, projection, previous, point, colour);
            previous = point;
        }
    }

    private static void Sphere(IHudCanvas hud, in DebugProjection projection, Vector3 centre, Quaternion rotation, float radius, Vector4 colour)
    {
        Vector3 x = Vector3.Transform(Vector3.UnitX, rotation), y = Vector3.Transform(Vector3.UnitY, rotation), z = Vector3.Transform(Vector3.UnitZ, rotation);
        Circle(hud, projection, centre, x, z, radius, colour);
        Circle(hud, projection, centre, x, y, radius, colour);
        Circle(hud, projection, centre, y, z, radius, colour);
    }

    // A capsule stands on its local Y: radius, and half its height caps included (as the physics makes it).
    private static void Capsule(IHudCanvas hud, in DebugProjection projection, Vector3 centre, Quaternion rotation, float radius, float halfHeight, Vector4 colour)
    {
        Vector3 x = Vector3.Transform(Vector3.UnitX, rotation), y = Vector3.Transform(Vector3.UnitY, rotation), z = Vector3.Transform(Vector3.UnitZ, rotation);
        float half = MathF.Max(0.005f, halfHeight - radius);
        Vector3 top = centre + y * half, bottom = centre - y * half;
        Circle(hud, projection, top, x, z, radius, colour);
        Circle(hud, projection, bottom, x, z, radius, colour);
        foreach (Vector3 side in new[] { x, -x, z, -z })
            Segment(hud, projection, top + side * radius, bottom + side * radius, colour);
        Circle(hud, projection, top, x, y, radius, colour, 0f, 0.5f);
        Circle(hud, projection, top, z, y, radius, colour, 0f, 0.5f);
        Circle(hud, projection, bottom, x, y, radius, colour, 0.5f, 0.5f);
        Circle(hud, projection, bottom, z, y, radius, colour, 0.5f, 0.5f);
    }

    private static void Cylinder(IHudCanvas hud, in DebugProjection projection, Vector3 centre, Quaternion rotation, float radius, float halfHeight, Vector4 colour)
    {
        Vector3 x = Vector3.Transform(Vector3.UnitX, rotation), y = Vector3.Transform(Vector3.UnitY, rotation), z = Vector3.Transform(Vector3.UnitZ, rotation);
        Vector3 top = centre + y * halfHeight, bottom = centre - y * halfHeight;
        Circle(hud, projection, top, x, z, radius, colour);
        Circle(hud, projection, bottom, x, z, radius, colour);
        foreach (Vector3 side in new[] { x, -x, z, -z })
            Segment(hud, projection, top + side * radius, bottom + side * radius, colour);
    }

    // A mesh or hull collider's triangles (its saved model geometry); a large one as its box.
    private static void Mesh(IHudCanvas hud, in DebugProjection projection, EcsWorld world, Entity entity, Vector3 centre, Quaternion rotation, Vector4 colour)
    {
        if (!world.Has<MeshColliderComponent>(entity)) { Box(hud, projection, centre, rotation, new Vector3(0.5f), colour); return; }
        ref MeshColliderComponent mesh = ref world.GetRef<MeshColliderComponent>(entity);
        if (mesh.Vertices is not { Length: >= 3 } vertices || mesh.Indices is not { Length: >= 3 } indices) return;
        Vector3 scale = mesh.Scale == Vector3.Zero ? Vector3.One : mesh.Scale;
        if (indices.Length / 3 > MaxMeshTriangles)
        {
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (Vector3 vertex in vertices) { min = Vector3.Min(min, vertex * scale); max = Vector3.Max(max, vertex * scale); }
            Box(hud, projection, centre + Vector3.Transform((min + max) * 0.5f, rotation), rotation, (max - min) * 0.5f, colour);
            return;
        }
        for (int index = 0; index + 2 < indices.Length; index += 3)
        {
            int ia = indices[index], ib = indices[index + 1], ic = indices[index + 2];
            if ((uint)ia >= (uint)vertices.Length || (uint)ib >= (uint)vertices.Length || (uint)ic >= (uint)vertices.Length) continue;
            Vector3 a = centre + Vector3.Transform(vertices[ia] * scale, rotation);
            Vector3 b = centre + Vector3.Transform(vertices[ib] * scale, rotation);
            Vector3 c = centre + Vector3.Transform(vertices[ic] * scale, rotation);
            Segment(hud, projection, a, b, colour);
            Segment(hud, projection, b, c, colour);
            Segment(hud, projection, c, a, colour);
        }
    }

    // ── 2D ─────────────────────────────────────────────────────────────────

    private void Draw2D(IHudCanvas hud, RuntimeScene scene, in DebugProjection projection)
    {
        EcsWorld world = scene.World;
        string project = PgslCommands.ProjectPath;
        bool split = projection.Views2D.Count > 1;
        for (int viewIndex = 0; viewIndex < projection.Views2D.Count; viewIndex++)
        {
            RoomRenderSubsystem.View2D view = projection.Views2D[viewIndex];
            if (split) hud.SetClip(view.Port.X, view.Port.Y, view.Port.Width, view.Port.Height);
            RectangleF seen = SeenRoomArea(view);

            // A 2D room's solid tiles, as its collisions read them.
            if (DebugScreenSettings.ColliderTiles && PgslCommands.ActiveGameContext?.Room is { } room
                && RoomTileCollisionMap.Get(room, project) is { SolidCount: > 0 } tiles)
            {
                foreach (RectangleF solid in tiles.Solids)
                    if (solid.IntersectsWith(seen) && Take(DebugColliderKind.TileSolid))
                        Rect2D(hud, view, solid, Colour(DebugColliderKind.TileSolid));
            }

            // Each drawn instance's sprite hitbox (the mask box collisions use) and trigger shapes.
            foreach (var item in world.Query<TransformComponent, Draw2DComponent>())
            {
                if (!item.C2.Visible) continue;
                Entity entity = item.Entity;
                if (!ObjectDrawAssetRegistry.TryGet(entity, out ObjectDrawAssetEntry assets) || assets.Is3D == true
                    || string.IsNullOrWhiteSpace(assets.Image)) continue;
                ref TransformComponent transform = ref item.C1;
                SpriteRuntimeAsset sprite = SpriteCollisionBounds.Load(project, assets.Image);
                int frame = world.Has<SpriteComponent>(entity) ? (int)world.GetRef<SpriteComponent>(entity).ImageIndex : 0;
                RectangleF box = SpriteCollisionBounds.Resolve(sprite, frame, transform.X, transform.Y,
                    transform.ScaleX, transform.ScaleY, transform.Rotation);
                if (!box.IntersectsWith(seen)) continue;
                if (Take(DebugColliderKind.Hitbox)) Rect2D(hud, view, box, Colour(DebugColliderKind.Hitbox));
                if (sprite != null && DebugScreenSettings.ColliderTriggers)
                    TriggerShapes2D(hud, view, sprite, frame, transform);
            }

            // Physics bodies of a 2D room: sprite physics keeps them in metres with up as +Y.
            bool metres = scene.IsSpritePhysicsScene;
            float pixelsPerUnit = metres ? SpritePhysicsBinding.PixelsPerMetre : 1f;
            foreach (var item in world.Query<RigidBodyComponent>())
            {
                ref RigidBodyComponent body = ref item.C1;
                if (!body.Collision || body.Dormant) continue;
                DebugColliderKind kind = KindOf(world, item.Entity, body);
                if (!Wanted(kind) || !TryPose(world, item.Entity, out Vector3 position, out Quaternion rotation)) continue;
                Vector3 centre = position + Vector3.Transform(body.LocalOffset, rotation);
                Vector3 pixels = metres ? SpritePhysicsBinding.ToPixels(centre) : centre;
                float reach = body.HalfExtents.Length() * pixelsPerUnit;
                if (!RectangleF.Inflate(seen, reach, reach).Contains(pixels.X, pixels.Y) || !Take(kind)) continue;
                Body2D(hud, view, body, centre, rotation, metres, Colour(kind));
            }
        }
        if (split) hud.SetClip(0, 0, 0, 0);
    }

    // The part of the room a view shows, a little larger.
    private static RectangleF SeenRoomArea(in RoomRenderSubsystem.View2D view)
    {
        float zoom = Math.Abs(view.Zoom) < 1e-5f ? 1f : view.Zoom;
        float left = -view.OffsetX / zoom, top = -view.OffsetY / zoom;
        RectangleF area = new(left, top, view.Port.Width / zoom, view.Port.Height / zoom);
        area.Inflate(64f, 64f);
        return area;
    }

    private static void Rect2D(IHudCanvas hud, in RoomRenderSubsystem.View2D view, RectangleF box, Vector4 colour)
    {
        Vector2 a = DebugProjection.Map(view, box.Left, box.Top), b = DebugProjection.Map(view, box.Right, box.Bottom);
        hud.Rect(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Max(1f, MathF.Abs(b.X - a.X)), MathF.Max(1f, MathF.Abs(b.Y - a.Y)), colour, filled: false);
    }

    private static void TriggerShapes2D(IHudCanvas hud, in RoomRenderSubsystem.View2D view, SpriteRuntimeAsset sprite, int frameIndex, in TransformComponent transform)
    {
        if (sprite.CollisionShapes is not { Count: > 0 }) return;
        int frame = SpriteAssetLoader.NormalizeFrameIndex(frameIndex, sprite.Frames.Count);
        string frameId = sprite.Frames.Count > 0 ? sprite.Frames[frame].Id : "";
        SpriteRuntimeOrigin origin = sprite.Frames.Count > 0 ? sprite.Frames[frame].OriginOverride ?? sprite.Origin : sprite.Origin;
        bool normalized = !string.Equals(origin?.Space, "pixels", StringComparison.OrdinalIgnoreCase);
        float ox = (float)(origin?.X ?? .5) * (normalized ? sprite.Canvas.Width : 1);
        float oy = (float)(origin?.Y ?? .5) * (normalized ? sprite.Canvas.Height : 1);
        float radians = transform.Rotation * MathF.PI / 180f, cosine = MathF.Cos(radians), sine = MathF.Sin(radians);
        RoomRenderSubsystem.View2D target = view;
        float x = transform.X, y = transform.Y, scaleX = transform.ScaleX, scaleY = transform.ScaleY;
        Vector2 World(float lx, float ly)
        {
            float cx = (lx - ox) * scaleX, cy = (ly - oy) * scaleY;
            return DebugProjection.Map(target, x + cx * cosine - cy * sine, y + cx * sine + cy * cosine);
        }
        Vector4 colour = Colour(DebugColliderKind.Trigger);
        foreach (SpriteRuntimeCollisionShape shape in sprite.CollisionShapes)
        {
            if (shape is not { IsTrigger: true } || (!string.IsNullOrEmpty(shape.FrameId) && shape.FrameId != frameId)) continue;
            float px = (float)(shape.Position?.X ?? 0), py = (float)(shape.Position?.Y ?? 0);
            if (shape.Points?.Count > 1)
            {
                Vector2 first = World((float)shape.Points[0].X + px, (float)shape.Points[0].Y + py), previous = first;
                for (int index = 1; index < shape.Points.Count; index++)
                {
                    Vector2 point = World((float)shape.Points[index].X + px, (float)shape.Points[index].Y + py);
                    hud.Line(previous.X, previous.Y, point.X, point.Y, colour, Thickness);
                    previous = point;
                }
                hud.Line(previous.X, previous.Y, first.X, first.Y, colour, Thickness);
            }
            else if (shape.Radius > 0)
            {
                Vector2 centre = World(px, py);
                float radius = (float)shape.Radius * MathF.Max(MathF.Abs(transform.ScaleX), MathF.Abs(transform.ScaleY)) * Math.Abs(view.Zoom);
                hud.Circle(centre.X, centre.Y, radius, colour, filled: false, Thickness);
            }
            else
            {
                float w = (float)(shape.Size?.X ?? 0), h = (float)(shape.Size?.Y ?? 0);
                if (w <= 0 || h <= 0) continue;
                Vector2 a = World(px, py), b = World(px + w, py), c = World(px + w, py + h), d = World(px, py + h);
                hud.Line(a.X, a.Y, b.X, b.Y, colour, Thickness);
                hud.Line(b.X, b.Y, c.X, c.Y, colour, Thickness);
                hud.Line(c.X, c.Y, d.X, d.Y, colour, Thickness);
                hud.Line(d.X, d.Y, a.X, a.Y, colour, Thickness);
            }
        }
    }

    // A body of a 2D room: its outline turned about the view's axis, in pixels.
    private static void Body2D(IHudCanvas hud, in RoomRenderSubsystem.View2D view, in RigidBodyComponent body, Vector3 centre, Quaternion rotation,
        bool metres, Vector4 colour)
    {
        RoomRenderSubsystem.View2D target = view;
        Vector2 Point(Vector3 local)
        {
            Vector3 world = centre + Vector3.Transform(local, rotation);
            Vector3 pixels = metres ? SpritePhysicsBinding.ToPixels(world) : world;
            return DebugProjection.Map(target, pixels.X, pixels.Y);
        }
        if (body.Shape == CollisionShape.Sphere)
        {
            Vector2 middle = Point(Vector3.Zero);
            float scale = metres ? SpritePhysicsBinding.PixelsPerMetre : 1f;
            hud.Circle(middle.X, middle.Y, body.Size.X * scale * Math.Abs(view.Zoom), colour, filled: false, Thickness);
            return;
        }
        Vector3 half = body.HalfExtents;
        Vector2 a = Point(new Vector3(-half.X, -half.Y, 0)), b = Point(new Vector3(half.X, -half.Y, 0));
        Vector2 c = Point(new Vector3(half.X, half.Y, 0)), d = Point(new Vector3(-half.X, half.Y, 0));
        hud.Line(a.X, a.Y, b.X, b.Y, colour, Thickness);
        hud.Line(b.X, b.Y, c.X, c.Y, colour, Thickness);
        hud.Line(c.X, c.Y, d.X, d.Y, colour, Thickness);
        hud.Line(d.X, d.Y, a.X, a.Y, colour, Thickness);
    }
}
