using System;
using System.Drawing;
using System.IO;
using Genesis.Runtime.Assets;
using Genesis.Runtime.ECS;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS;

namespace Genesis.Runtime.Spatial;

/// <summary>
/// Frame-aware authored mask bounds: an axis-aligned box, not a pixel mask. Turned with the instance,
/// each shape is turned itself (a circle stays its own size, a polygon's points are turned), so the box
/// is the turned mask's true bounds rather than the bounds of its turned bounding box.
/// </summary>
public static class SpriteCollisionBounds
{
    public static RectangleF Resolve(SpriteRuntimeAsset asset, int frameIndex, float x, float y,
        float scaleX = 1, float scaleY = 1, float rotation = 0)
    {
        if (asset == null) return new RectangleF(x - 16, y - 16, 32, 32);
        int frame = SpriteAssetLoader.NormalizeFrameIndex(frameIndex, asset.Frames.Count);
        string frameId = asset.Frames.Count > 0 ? asset.Frames[frame].Id : "";
        SpriteRuntimeOrigin origin = asset.Frames.Count > 0
            ? asset.Frames[frame].OriginOverride ?? asset.Origin : asset.Origin;
        bool normalized = !string.Equals(origin?.Space, "pixels", StringComparison.OrdinalIgnoreCase);
        float ox = (float)(origin?.X ?? .5) * (normalized ? asset.Canvas.Width : 1);
        float oy = (float)(origin?.Y ?? .5) * (normalized ? asset.Canvas.Height : 1);
        float radians = rotation * MathF.PI / 180, cosine = MathF.Cos(radians), sine = MathF.Sin(radians);
        if (sine != 0 && float.IsFinite(sine) && float.IsFinite(cosine))
            return Turned(asset, frameId, x, y, ox, oy, scaleX, scaleY, cosine, sine);
        RectangleF local = default;
        bool found = false;
        foreach (SpriteRuntimeCollisionShape shape in asset.CollisionShapes)
        {
            if (!Applies(shape, frameId)) continue;
            float px = (float)(shape.Position?.X ?? 0), py = (float)(shape.Position?.Y ?? 0);
            RectangleF box;
            if (shape.Points?.Count > 0)
            {
                float left = float.PositiveInfinity, top = left, right = float.NegativeInfinity, bottom = right;
                foreach (SpriteRuntimePoint point in shape.Points)
                {
                    left = MathF.Min(left, (float)point.X + px); top = MathF.Min(top, (float)point.Y + py);
                    right = MathF.Max(right, (float)point.X + px); bottom = MathF.Max(bottom, (float)point.Y + py);
                }
                box = RectangleF.FromLTRB(left, top, right, bottom);
            }
            else if (shape.Radius > 0)
                box = new RectangleF(px - (float)shape.Radius, py - (float)shape.Radius,
                    (float)shape.Radius * 2, (float)shape.Radius * 2);
            else box = new RectangleF(px, py, (float)(shape.Size?.X ?? 0), (float)(shape.Size?.Y ?? 0));
            if (box.Width <= 0 || box.Height <= 0) continue;
            local = found ? RectangleF.Union(local, box) : box;
            found = true;
        }
        if (!found) local = new RectangleF(0, 0, asset.Canvas.Width, asset.Canvas.Height);
        float minX = float.PositiveInfinity, minY = minX, maxX = float.NegativeInfinity, maxY = maxX;
        for (int corner = 0; corner < 4; corner++)
        {
            float cx = (((corner & 1) == 0 ? local.Left : local.Right) - ox) * scaleX;
            float cy = (((corner & 2) == 0 ? local.Top : local.Bottom) - oy) * scaleY;
            float wx = x + cx * cosine - cy * sine, wy = y + cx * sine + cy * cosine;
            minX = MathF.Min(minX, wx); maxX = MathF.Max(maxX, wx);
            minY = MathF.Min(minY, wy); maxY = MathF.Max(maxY, wy);
        }
        return RectangleF.FromLTRB(minX, minY, maxX, maxY);
    }

    private static bool Applies(SpriteRuntimeCollisionShape shape, string frameId) => shape != null && !shape.IsTrigger
        && (string.IsNullOrEmpty(shape.FrameId) || shape.FrameId == frameId);

    /// <summary>A turned instance's mask bounds: each shape turned on its own, then joined.</summary>
    private static RectangleF Turned(SpriteRuntimeAsset asset, string frameId, float x, float y,
        float ox, float oy, float scaleX, float scaleY, float cosine, float sine)
    {
        float minX = float.PositiveInfinity, minY = minX, maxX = float.NegativeInfinity, maxY = maxX;
        void Add(float lx, float ly)
        {
            float cx = (lx - ox) * scaleX, cy = (ly - oy) * scaleY;
            float wx = x + cx * cosine - cy * sine, wy = y + cx * sine + cy * cosine;
            minX = MathF.Min(minX, wx); maxX = MathF.Max(maxX, wx);
            minY = MathF.Min(minY, wy); maxY = MathF.Max(maxY, wy);
        }
        bool found = false;
        foreach (SpriteRuntimeCollisionShape shape in asset.CollisionShapes)
        {
            if (!Applies(shape, frameId)) continue;
            float px = (float)(shape.Position?.X ?? 0), py = (float)(shape.Position?.Y ?? 0);
            if (shape.Points?.Count > 0)
            {
                float left = float.PositiveInfinity, top = left, right = float.NegativeInfinity, bottom = right;
                foreach (SpriteRuntimePoint point in shape.Points)
                {
                    left = MathF.Min(left, (float)point.X + px); top = MathF.Min(top, (float)point.Y + py);
                    right = MathF.Max(right, (float)point.X + px); bottom = MathF.Max(bottom, (float)point.Y + py);
                }
                if (right <= left || bottom <= top) continue;
                foreach (SpriteRuntimePoint point in shape.Points) Add((float)point.X + px, (float)point.Y + py);
            }
            else if (shape.Radius > 0)
            {
                // A circle (an ellipse once scaled unevenly) keeps its own extent at any angle.
                float cx = (px - ox) * scaleX, cy = (py - oy) * scaleY;
                float wx = x + cx * cosine - cy * sine, wy = y + cx * sine + cy * cosine;
                float a = (float)shape.Radius * MathF.Abs(scaleX), b = (float)shape.Radius * MathF.Abs(scaleY);
                float halfX = MathF.Sqrt(a * a * cosine * cosine + b * b * sine * sine);
                float halfY = MathF.Sqrt(a * a * sine * sine + b * b * cosine * cosine);
                minX = MathF.Min(minX, wx - halfX); maxX = MathF.Max(maxX, wx + halfX);
                minY = MathF.Min(minY, wy - halfY); maxY = MathF.Max(maxY, wy + halfY);
            }
            else
            {
                float width = (float)(shape.Size?.X ?? 0), height = (float)(shape.Size?.Y ?? 0);
                if (width <= 0 || height <= 0) continue;
                Add(px, py); Add(px + width, py); Add(px, py + height); Add(px + width, py + height);
            }
            found = true;
        }
        if (!found)
        {
            Add(0, 0); Add(asset.Canvas.Width, 0); Add(0, asset.Canvas.Height); Add(asset.Canvas.Width, asset.Canvas.Height);
        }
        return RectangleF.FromLTRB(minX, minY, maxX, maxY);
    }

    public static SpriteRuntimeAsset Load(string project, string image)
    {
        if (string.IsNullOrWhiteSpace(image)) return null;
        string key = SpriteAssetLoader.ResolveDescriptorPath(project, image);
        if (!SpriteAssetLoader.IsSpriteDescriptorPath(key)) return null;
        if (SpriteAssetLoader.TryGetCachedAsset(key, out SpriteRuntimeAsset cached)) return cached;
        return SpriteAssetLoader.Load(project, image);
    }

    public static RectangleF ForEntity(global::Genesis.Runtime.ECS.World world, Entity entity, string project)
    {
        ref TransformComponent transform = ref world.GetRef<TransformComponent>(entity);
        if (!ObjectDrawAssetRegistry.TryGet(entity, out ObjectDrawAssetEntry draw))
            return new RectangleF(transform.X - 16, transform.Y - 16, 32, 32);
        int frame = world.Has<SpriteComponent>(entity) ? (int)world.GetRef<SpriteComponent>(entity).ImageIndex : 0;
        return Resolve(Load(project, draw.Image), frame, transform.X, transform.Y,
            transform.ScaleX, transform.ScaleY, transform.Rotation);
    }
}
