using System.Numerics;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Editors.Suite.Assets;

internal sealed class ModelRigWizardSpace
{
    public Vector3 Right { get; }
    public Vector3 Up { get; }
    public Vector3 Forward { get; }
    public Vector3 Min { get; }
    public Vector3 Size { get; }
    public float Scale { get; }
    public float MirrorX { get; }
    public float Ground { get; }
    public ModelRigWizardSpace(GModelAsset asset, GModelRigWizardSetup setup)
    {
        Up = Vector3.Normalize(setup.Up); Forward = Vector3.Normalize(setup.Forward);
        if (!float.IsFinite(Up.LengthSquared()) || !float.IsFinite(Forward.LengthSquared()) || Math.Abs(Vector3.Dot(Up, Forward)) > .01f)
            throw new InvalidOperationException("Up and forward must be different perpendicular axes.");
        Right = Vector3.Normalize(Vector3.Cross(Up, Forward));
        var points = asset.Meshes.Where((_, i) => !setup.ExcludedMeshes.Contains(i)).SelectMany(m => Positions(m).Select(Project)).ToArray();
        if (points.Length == 0) throw new InvalidOperationException("Include at least one mesh in shape detection.");
        Min = points.Aggregate(Vector3.Min); var max = points.Aggregate(Vector3.Max);
        Scale = Math.Max(.0001f, Math.Max((max - Min).X, Math.Max((max - Min).Y, (max - Min).Z)));
        Size = Vector3.Max((max - Min) / Scale, new Vector3(.001f));
        MirrorX = (setup.SymmetryOffset - Min.X) / Scale; Ground = (setup.GroundHeight - Min.Y) / Scale;
    }
    private Vector3 Project(Vector3 p) => new(Vector3.Dot(p, Right), Vector3.Dot(p, Up), Vector3.Dot(p, Forward));
    public Vector3 ToFit(Vector3 p) => (Project(p) - Min) / Scale;
    public Vector3 ToModel(Vector3 p) { var q = p * Scale + Min; return Right * q.X + Up * q.Y + Forward * q.Z; }
    public Vector3 Fraction(float x, float y, float z) => Size * new Vector3(x, y, z);
    public Vector3 Mirror(Vector3 p) => p - Right * (2 * (Vector3.Dot(p, Right) - (MirrorX * Scale + Min.X)));
    public static IEnumerable<Vector3> Positions(GModelMesh mesh) => mesh.Vertices.Length > 0
        ? mesh.Vertices.Select(v => v.Position) : mesh.SkinnedVertices.Select(v => v.Position);
}

/// <summary>Welded interior volume and distance candidates, accelerated by a triangle BVH.</summary>
internal sealed class ModelRigWizardGeometry
{
    private const int Resolution = 72;
    private const float Cell = 1f / Resolution;
    private readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C, int Part);
    private sealed record Node(Vector3 Min, Vector3 Max, int Start, int Count, Node? Left, Node? Right);
    private readonly Triangle[] _triangles;
    private readonly Node _tree;
    private readonly bool[] _inside;
    private readonly int[] _depth;
    private readonly int _nx, _ny, _nz;
    private readonly List<int> _candidates = [];
    private readonly CancellationToken _token;
    public ModelRigWizardSpace Space { get; }
    public bool HasInterior => _candidates.Count > 0;
    public ModelRigWizardGeometry(GModelAsset asset, GModelRigWizardSetup setup, CancellationToken token, IProgress<string>? progress)
    {
        _token = token; Space = new(asset, setup);
        _nx = Math.Max(3, (int)Math.Ceiling(Space.Size.X * Resolution) + 2);
        _ny = Math.Max(3, (int)Math.Ceiling(Space.Size.Y * Resolution) + 2);
        _nz = Math.Max(3, (int)Math.Ceiling(Space.Size.Z * Resolution) + 2);
        var lookup = new Dictionary<(long, long, long), int>(); var parents = new List<int>(); var triangles = new List<Triangle>();
        for (int m = 0; m < asset.Meshes.Count; m++)
        {
            token.ThrowIfCancellationRequested(); if (setup.ExcludedMeshes.Contains(m)) continue;
            var mesh = asset.Meshes[m]; var vertices = ModelRigWizardSpace.Positions(mesh).ToArray(); var ids = new int[vertices.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                var p = Space.ToFit(vertices[i]); var key = ((long)Math.Round(p.X * 1e6), (long)Math.Round(p.Y * 1e6), (long)Math.Round(p.Z * 1e6));
                if (!lookup.TryGetValue(key, out int id)) { id = parents.Count; parents.Add(id); lookup[key] = id; }
                ids[i] = id;
            }
            for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
            {
                int a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2]; if (a >= ids.Length || b >= ids.Length || c >= ids.Length) continue;
                Join(ids[a], ids[b]); Join(ids[a], ids[c]);
                triangles.Add(new(Space.ToFit(vertices[a]), Space.ToFit(vertices[b]), Space.ToFit(vertices[c]), ids[a]));
            }
        }
        _triangles = triangles.Select(t => t with { Part = Root(t.Part) }).ToArray();
        if (_triangles.Length == 0) throw new InvalidOperationException("The included meshes contain no triangles.");
        progress?.Report("Finding the interior of the included meshes…");
        _tree = Build(0, _triangles.Length); _inside = new bool[_nx * _ny * _nz]; _depth = new int[_inside.Length];
        for (int y = 0; y < _ny; y++) for (int x = 0; x < _nx; x++)
        {
            token.ThrowIfCancellationRequested(); var origin = new Vector3((x - .5f) * Cell, (y - .5f) * Cell, -Cell * 2);
            var hits = new List<(float Z, int Part)>(); Intersections(_tree, origin, hits);
            foreach (var part in hits.GroupBy(h => h.Part))
            {
                var zValues = part.Select(h => h.Z).Order().ToArray(); var boundaries = new List<float>();
                foreach (float z in zValues) if (boundaries.Count == 0 || z - boundaries[^1] > 1e-5f) boundaries.Add(z);
                for (int j = 0; j + 1 < boundaries.Count; j += 2)
                    for (int z = 0; z < _nz; z++) { float at = (z - .5f) * Cell; if (at >= boundaries[j] && at <= boundaries[j + 1]) _inside[Index(x, y, z)] = true; }
            }
        }
        var queue = new Queue<int>();
        for (int i = 0; i < _inside.Length; i++) if (_inside[i] && Neighbours(i).Any(n => !_inside[n])) { _depth[i] = 1; queue.Enqueue(i); }
        int iterations = 0;
        while (queue.TryDequeue(out int at))
        {
            if ((iterations++ & 1023) == 0) token.ThrowIfCancellationRequested();
            foreach (int n in Neighbours(at)) if (_inside[n] && _depth[n] == 0) { _depth[n] = _depth[at] + 1; queue.Enqueue(n); }
        }
        for (int i = 0; i < _inside.Length; i++) if (_depth[i] > 0) _candidates.Add(i);
        int Root(int i) { while (parents[i] != i) { parents[i] = parents[parents[i]]; i = parents[i]; } return i; }
        void Join(int a, int b) { a = Root(a); b = Root(b); if (a != b) parents[b] = a; }
    }
    public (Vector3 Point, float Confidence) Fit(Vector3 desired)
    {
        if (!HasInterior)
        {
            var nearest = _triangles.Select(t => (t.A + t.B + t.C) / 3).MinBy(p => Vector3.DistanceSquared(p, desired));
            return (nearest, 0);
        }
        float best = float.MaxValue; int chosen = _candidates[0];
        foreach (int i in _candidates)
        {
            float distance = Vector3.DistanceSquared(Position(i), desired);
            float score = distance - Math.Min(_depth[i], 5) * Cell * Cell * .7f;
            if (score < best) { best = score; chosen = i; }
        }
        float error = Vector3.Distance(Position(chosen), desired);
        return (RefineDepth(Position(chosen)), Math.Clamp(1 - error / .22f, 0, 1));
    }
    public List<Vector3> Extremities(int side, bool arms)
    {
        var eligible = new HashSet<int>(_candidates.Where(i =>
        {
            var p = Position(i); float sideDistance = (p.X - Space.MirrorX) * side;
            return arms ? sideDistance > Space.Size.X * .3f && p.Y > Space.Ground + Space.Size.Y * .25f
                : sideDistance > Cell * .25f && p.Y < Space.Ground + Space.Size.Y * .12f;
        }));
        var groups = new List<List<Vector3>>(); var queue = new Queue<int>();
        while (eligible.Count > 0)
        {
            _token.ThrowIfCancellationRequested(); int seed = eligible.First(); eligible.Remove(seed); queue.Enqueue(seed); var group = new List<Vector3>();
            while (queue.TryDequeue(out int at))
            {
                group.Add(Position(at)); foreach (int neighbour in Neighbours(at)) if (eligible.Remove(neighbour)) queue.Enqueue(neighbour);
            }
            if (group.Count >= 2) groups.Add(group);
        }
        return groups.OrderByDescending(g => g.Count).Select(g =>
        {
            float extreme = arms ? g.Max(p => p.X * side) : g.Min(p => p.Y);
            var tip = g.Where(p => arms ? p.X * side >= extreme - Cell * 1.5f : p.Y <= extreme + Cell * .5f).ToArray();
            return tip.Aggregate(Vector3.Zero, (a, b) => a + b) / tip.Length;
        }).ToList();
    }
    private Vector3 RefineDepth(Vector3 point)
    {
        var hits = new List<(float Z, int Part)>(); Intersections(_tree, new(point.X, point.Y, -Cell * 2), hits);
        float best = float.PositiveInfinity, center = point.Z;
        foreach (var group in hits.GroupBy(h => h.Part))
        {
            var boundaries = new List<float>();
            foreach (float z in group.Select(h => h.Z).Order()) if (boundaries.Count == 0 || z - boundaries[^1] > 1e-5f) boundaries.Add(z);
            for (int i = 0; i + 1 < boundaries.Count; i += 2)
            {
                if (point.Z < boundaries[i] - Cell || point.Z > boundaries[i + 1] + Cell) continue;
                float mid = (boundaries[i] + boundaries[i + 1]) * .5f, error = Math.Abs(mid - point.Z);
                if (error < best) { best = error; center = mid; }
            }
        }
        point.Z += Math.Clamp(center - point.Z, -Cell * 2, Cell * 2); return point;
    }
    public List<Vector3> InteriorPath(Vector3 from, Vector3 to, int side = 0)
    {
        if (!HasInterior) return [];
        int start = _candidates.MinBy(i => Vector3.DistanceSquared(Position(i), from));
        int end = _candidates.MinBy(i => Vector3.DistanceSquared(Position(i), to));
        var distance = new float[_inside.Length]; Array.Fill(distance, float.PositiveInfinity);
        var previous = new int[_inside.Length]; Array.Fill(previous, -1); distance[start] = 0;
        var queue = new PriorityQueue<int, float>(); queue.Enqueue(start, 0);
        while (queue.TryDequeue(out int v, out _))
        {
            if ((v & 255) == 0) _token.ThrowIfCancellationRequested(); if (v == end) break;
            foreach (int n in Neighbours(v))
            {
                if (!_inside[n]) continue;
                float margin = Math.Min(Math.Abs(from.X - Space.MirrorX), Math.Abs(to.X - Space.MirrorX)) * .65f;
                if (side != 0 && (Position(n).X - Space.MirrorX) * side < margin) continue;
                float next = distance[v] + 1 + 2f / Math.Max(1, _depth[n]);
                if (next >= distance[n]) continue; distance[n] = next; previous[n] = v; queue.Enqueue(n, next + Vector3.Distance(Position(n), Position(end)) / Cell);
            }
        }
        if (start != end && previous[end] < 0) return [];
        var path = new List<Vector3>(); for (int i = end; i >= 0; i = previous[i]) { path.Add(Position(i)); if (i == start) break; }
        path.Reverse(); return path;
    }
    private int Index(int x, int y, int z) => (z * _ny + y) * _nx + x;
    private Vector3 Position(int i) => new((i % _nx - .5f) * Cell, (i / _nx % _ny - .5f) * Cell, (i / (_nx * _ny) - .5f) * Cell);
    private IEnumerable<int> Neighbours(int i)
    {
        int x = i % _nx, y = i / _nx % _ny, z = i / (_nx * _ny);
        if (x > 0) yield return i - 1; if (x + 1 < _nx) yield return i + 1; if (y > 0) yield return i - _nx; if (y + 1 < _ny) yield return i + _nx;
        if (z > 0) yield return i - _nx * _ny; if (z + 1 < _nz) yield return i + _nx * _ny;
    }
    private Node Build(int start, int count)
    {
        _token.ThrowIfCancellationRequested(); Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        for (int i = start; i < start + count; i++) { var t = _triangles[i]; min = Vector3.Min(min, Vector3.Min(t.A, Vector3.Min(t.B, t.C))); max = Vector3.Max(max, Vector3.Max(t.A, Vector3.Max(t.B, t.C))); }
        if (count <= 12) return new(min, max, start, count, null, null);
        var size = max - min; int axis = size.X > size.Y ? (size.X > size.Z ? 0 : 2) : (size.Y > size.Z ? 1 : 2);
        Array.Sort(_triangles, start, count, Comparer<Triangle>.Create((a, b) => ((a.A + a.B + a.C)[axis]).CompareTo((b.A + b.B + b.C)[axis])));
        int left = count / 2; return new(min, max, start, 0, Build(start, left), Build(start + left, count - left));
    }
    private void Intersections(Node node, Vector3 origin, List<(float Z, int Part)> hits)
    {
        if (origin.X < node.Min.X || origin.X > node.Max.X || origin.Y < node.Min.Y || origin.Y > node.Max.Y) return;
        if (node.Left is not null) { Intersections(node.Left, origin, hits); Intersections(node.Right!, origin, hits); return; }
        for (int i = node.Start; i < node.Start + node.Count; i++)
        {
            var t = _triangles[i]; var e1 = t.B - t.A; var e2 = t.C - t.A; var p = Vector3.Cross(Vector3.UnitZ, e2); float det = Vector3.Dot(e1, p);
            if (Math.Abs(det) < 1e-10f) continue; var d = origin - t.A; float u = Vector3.Dot(d, p) / det; if (u < 0 || u > 1) continue;
            var q = Vector3.Cross(d, e1); float v = q.Z / det; if (v < 0 || u + v > 1) continue;
            hits.Add((origin.Z + Vector3.Dot(e2, q) / det, t.Part));
        }
    }
}
