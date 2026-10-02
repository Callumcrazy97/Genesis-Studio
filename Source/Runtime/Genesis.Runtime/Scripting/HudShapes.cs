using System;
using System.Numerics;

namespace Genesis.Runtime.Scripting
{
    /// <summary>
    /// Circles, arcs and polygons for a HUD, built from the rectangles and lines every canvas can
    /// draw. A filled shape is one rectangle for each row of pixels it covers.
    /// </summary>
    public static class HudShapes
    {
        private const float LargestRadius = 4096f;

        /// <summary>Line segments used to draw a curve of this radius through this many degrees.</summary>
        public static int Segments(float radius, float sweepDegrees)
        {
            // About one segment for every six pixels of curve, and never fewer than one for every
            // thirty degrees, so a small ring is still round.
            float length = MathF.Abs(sweepDegrees) * (MathF.PI / 180f) * MathF.Max(0f, radius);
            int least = Math.Max(2, (int)MathF.Ceiling(MathF.Abs(sweepDegrees) / 30f));
            return Math.Clamp((int)MathF.Ceiling(length / 6f), least, 180);
        }

        public static void Circle(IHudCanvas hud, float centerX, float centerY, float radius, Vector4 color, bool filled, float thickness)
        {
            if (hud == null || !(radius > 0f) || !float.IsFinite(centerX) || !float.IsFinite(centerY)) return;
            radius = MathF.Min(radius, LargestRadius);
            if (!filled)
            {
                Arc(hud, centerX, centerY, radius, 0f, 360f, color, thickness);
                return;
            }

            // Each row is sampled through its middle, so the disc is symmetrical top to bottom.
            int first = (int)MathF.Floor(centerY - radius), last = (int)MathF.Ceiling(centerY + radius);
            for (int row = first; row < last; row++)
            {
                float dy = row + 0.5f - centerY;
                float inside = radius * radius - dy * dy;
                if (inside <= 0f) continue;
                float half = MathF.Sqrt(inside);
                hud.Rect(centerX - half, row, half * 2f, 1f, color, filled: true);
            }
        }

        /// <summary>Part of a ring. Angles are in degrees, clockwise on screen from the right (three o'clock).</summary>
        public static void Arc(IHudCanvas hud, float centerX, float centerY, float radius, float startDegrees, float sweepDegrees,
            Vector4 color, float thickness)
        {
            if (hud == null || !(radius > 0f) || !float.IsFinite(startDegrees) || !float.IsFinite(sweepDegrees) || sweepDegrees == 0f) return;
            radius = MathF.Min(radius, LargestRadius);
            float sweep = Math.Clamp(sweepDegrees, -360f, 360f);
            int segments = Segments(radius, sweep);
            float start = startDegrees * (MathF.PI / 180f), step = sweep * (MathF.PI / 180f) / segments;
            float x = centerX + MathF.Cos(start) * radius, y = centerY + MathF.Sin(start) * radius;
            for (int i = 1; i <= segments; i++)
            {
                float angle = start + step * i;
                float nextX = centerX + MathF.Cos(angle) * radius, nextY = centerY + MathF.Sin(angle) * radius;
                hud.Line(x, y, nextX, nextY, color, thickness);
                x = nextX;
                y = nextY;
            }
        }

        public static void Polygon(IHudCanvas hud, ReadOnlySpan<Vector2> points, Vector4 color, bool filled, float thickness)
        {
            if (hud == null || points.Length < 2) return;
            if (!filled || points.Length < 3)
            {
                // Two points are one line; more are joined back to the first.
                int edges = points.Length == 2 ? 1 : points.Length;
                for (int i = 0; i < edges; i++)
                {
                    Vector2 from = points[i], to = points[(i + 1) % points.Length];
                    hud.Line(from.X, from.Y, to.X, to.Y, color, thickness);
                }

                return;
            }

            float top = float.MaxValue, bottom = float.MinValue;
            foreach (Vector2 point in points)
            {
                if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) return;
                top = MathF.Min(top, point.Y);
                bottom = MathF.Max(bottom, point.Y);
            }

            int first = (int)MathF.Floor(top), last = Math.Min((int)MathF.Ceiling(bottom), first + (int)LargestRadius * 2);
            Span<float> crossings = points.Length <= 64 ? stackalloc float[points.Length] : new float[points.Length];
            for (int row = first; row < last; row++)
            {
                // Where the row's middle crosses each edge; inside is between the 1st and 2nd, the 3rd and 4th.
                float y = row + 0.5f;
                int count = 0;
                for (int i = 0; i < points.Length; i++)
                {
                    Vector2 a = points[i], b = points[(i + 1) % points.Length];
                    if ((a.Y <= y) == (b.Y <= y)) continue;
                    crossings[count++] = a.X + (y - a.Y) / (b.Y - a.Y) * (b.X - a.X);
                }

                crossings[..count].Sort();
                for (int i = 0; i + 1 < count; i += 2)
                    if (crossings[i + 1] > crossings[i])
                        hud.Rect(crossings[i], row, crossings[i + 1] - crossings[i], 1f, color, filled: true);
            }
        }
    }
}
