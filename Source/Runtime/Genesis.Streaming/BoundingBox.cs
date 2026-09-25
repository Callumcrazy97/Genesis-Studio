using System;
using System.Numerics;

namespace Genesis.Streaming
{
    /// <summary>Axis-aligned bounding box for streaming visibility tests.</summary>
    public readonly struct BoundingBox
    {
        public Vector3 Min { get; }
        public Vector3 Max { get; }

        public BoundingBox(Vector3 min, Vector3 max)
        {
            Min = min;
            Max = max;
        }

        public Vector3 Center => (Min + Max) * 0.5f;

        public BoundingBox Expand(float padding)
        {
            if (padding <= 0f)
                return this;

            Vector3 pad = new Vector3(padding);
            return new BoundingBox(Min - pad, Max + pad);
        }

        public float ExtentRadius
        {
            get
            {
                Vector3 e = (Max - Min) * 0.5f;
                return e.Length();
            }
        }

        public static BoundingBox FromCenterExtents(Vector3 center, Vector3 halfExtents) =>
            new BoundingBox(center - halfExtents, center + halfExtents);

        public static float DistanceSquaredToPoint(Vector3 point, in BoundingBox box)
        {
            float dx = MathF.Max(box.Min.X - point.X, MathF.Max(0f, point.X - box.Max.X));
            float dy = MathF.Max(box.Min.Y - point.Y, MathF.Max(0f, point.Y - box.Max.Y));
            float dz = MathF.Max(box.Min.Z - point.Z, MathF.Max(0f, point.Z - box.Max.Z));
            return dx * dx + dy * dy + dz * dz;
        }
    }
}
