// Adapted from Genesis Modeler's Triangulator.cs, supplied as a reference project.
// MIT License
// Copyright (c) 2026 Genesis Modeler contributors
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System.Numerics;

namespace Genesis.Application.Editors.Suite.Assets;

internal static class ModelProfileTriangulator
{
    private const double Epsilon = 1e-9;

    public static ushort[] Build(IReadOnlyList<Vector3> points, Vector3 u, Vector3 v, Vector3 normal)
    {
        if (points.Count < 3) throw new InvalidOperationException("Connect at least three points to make a face.");
        if (points.Count > 2048) throw new InvalidOperationException("Use fewer than 2,049 points in one outline.");
        Vector2[] projected = points.Select(p => new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v))).ToArray();
        if (points.Any(p => !float.IsFinite(p.LengthSquared())))
            throw new InvalidOperationException("The outline contains an invalid point.");
        Vector2 min = projected.Aggregate(Vector2.Min), max = projected.Aggregate(Vector2.Max);
        float extent = Math.Max(max.X - min.X, max.Y - min.Y);
        if (extent < 1e-7f) throw new InvalidOperationException("Draw a closed outline with some width and height.");
        if (points.Any(p => Math.Abs(Vector3.Dot(p - points[0], normal)) > Math.Max(1e-5f, extent * 1e-5f)))
            throw new InvalidOperationException("Keep the connected outline on one drawing plane.");
        // Normalizing makes geometric tolerances independent of the drawing's world size.
        Vector2[] p = projected.Select(point => (point - min) / extent).ToArray();
        Validate(p);
        double area = Enumerable.Range(0, p.Length).Sum(i => Cross(p[i], p[(i + 1) % p.Length]));
        if (Math.Abs(area) < Epsilon) throw new InvalidOperationException("The outline needs a non-zero area.");
        double sign = Math.Sign(area);
        bool reverse = sign * Vector3.Dot(Vector3.Cross(u, v), normal) < 0;
        List<int> remaining = Enumerable.Range(0, p.Length).ToList();
        List<ushort> indices = [];
        while (remaining.Count > 3)
        {
            bool cut = false;
            for (int j = 0; j < remaining.Count; j++)
            {
                int a = remaining[(j + remaining.Count - 1) % remaining.Count], b = remaining[j], c = remaining[(j + 1) % remaining.Count];
                if (Cross(p[b] - p[a], p[c] - p[b]) * sign <= Epsilon) continue;
                if (remaining.Any(k => k != a && k != b && k != c && Inside(p[k], p[a], p[b], p[c], sign))) continue;
                Add(a, b, c); remaining.RemoveAt(j); cut = true; break;
            }
            if (cut) continue;
            int collinear = remaining.FindIndex(b =>
            {
                int at = remaining.IndexOf(b), a = remaining[(at + remaining.Count - 1) % remaining.Count], c = remaining[(at + 1) % remaining.Count];
                return Math.Abs(Cross(p[b] - p[a], p[c] - p[b])) <= Epsilon;
            });
            if (collinear < 0) throw new InvalidOperationException("The outline cannot form a face. Check for crossing edges.");
            remaining.RemoveAt(collinear);
        }
        if (remaining.Count == 3 && Math.Abs(Cross(p[remaining[1]] - p[remaining[0]], p[remaining[2]] - p[remaining[0]])) > Epsilon)
            Add(remaining[0], remaining[1], remaining[2]);
        return indices.ToArray();

        void Add(int a, int b, int c)
        {
            indices.Add((ushort)a); indices.Add((ushort)(reverse ? c : b)); indices.Add((ushort)(reverse ? b : c));
        }
    }

    private static double Cross(Vector2 a, Vector2 b) => (double)a.X * b.Y - (double)a.Y * b.X;
    private static bool Inside(Vector2 p, Vector2 a, Vector2 b, Vector2 c, double sign)
        => Cross(b - a, p - a) * sign >= -Epsilon && Cross(c - b, p - b) * sign >= -Epsilon && Cross(a - c, p - c) * sign >= -Epsilon;

    private static void Validate(Vector2[] points)
    {
        for (int i = 0; i < points.Length; i++)
        {
            Vector2 a = points[i], b = points[(i + 1) % points.Length];
            if (Vector2.DistanceSquared(a, b) < 1e-14f) throw new InvalidOperationException("The outline contains a repeated point.");
            for (int j = i + 1; j < points.Length; j++)
            {
                if (j == i + 1 || (i == 0 && j == points.Length - 1)) continue;
                Vector2 c = points[j], d = points[(j + 1) % points.Length];
                double denominator = Cross(b - a, d - c);
                if (Math.Abs(denominator) <= Epsilon)
                {
                    if (Math.Abs(Cross(c - a, b - a)) <= Epsilon &&
                        (OnSegment(c, a, b) || OnSegment(d, a, b) || OnSegment(a, c, d)))
                        throw new InvalidOperationException("Edges overlap. Undo the last edge and draw the outline without overlaps.");
                    continue;
                }
                double t = Cross(c - a, d - c) / denominator, s = Cross(c - a, b - a) / denominator;
                if (t >= -Epsilon && t <= 1 + Epsilon && s >= -Epsilon && s <= 1 + Epsilon)
                    throw new InvalidOperationException("Edges cross. Undo the last edge and draw the outline without crossings.");
            }
        }
    }

    private static bool OnSegment(Vector2 p, Vector2 a, Vector2 b)
        => p.X >= Math.Min(a.X, b.X) - Epsilon && p.X <= Math.Max(a.X, b.X) + Epsilon
        && p.Y >= Math.Min(a.Y, b.Y) - Epsilon && p.Y <= Math.Max(a.Y, b.Y) + Epsilon;
}
