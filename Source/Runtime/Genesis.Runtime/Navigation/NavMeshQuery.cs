#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Genesis.Runtime.Navigation;

public sealed class NavMeshQuery
{
    public NavMeshData Data { get; }
    public NavMeshQuery(NavMeshData data) { data.Validate(); Data = data; }

    // Area costs: how much more (or less) a cell costs to cross than its length, 1 everywhere
    // until a game sets them (a road 0.5, mud 3). Not saved with the grid: set after a bake or load.
    private float[]? _costs;
    private float _cheapest = 1f;
    public const float MinimumAreaCost = 0.05f, MaximumAreaCost = 1000f;

    /// <summary>Sets the cost of every cell whose centre lies in the rectangle; returns how many.</summary>
    public int SetAreaCost(float minX, float minZ, float maxX, float maxZ, float cost)
    {
        if (!float.IsFinite(cost)) return 0;
        cost = Math.Clamp(cost, MinimumAreaCost, MaximumAreaCost);
        if (minX > maxX) (minX, maxX) = (maxX, minX);
        if (minZ > maxZ) (minZ, maxZ) = (maxZ, minZ);
        _costs ??= Enumerable.Repeat(1f, Data.Count).ToArray();
        int changed = 0;
        for (int cell = 0; cell < Data.Count; cell++)
        {
            Vector3 centre = Data.Center(cell);
            if (centre.X < minX || centre.X > maxX || centre.Z < minZ || centre.Z > maxZ) continue;
            _costs[cell] = cost;
            changed++;
        }
        _cheapest = 1f;
        foreach (float each in _costs) _cheapest = Math.Min(_cheapest, each);
        return changed;
    }

    /// <summary>The area cost of the cell at a point; 1 where none was set or off the grid.</summary>
    public float AreaCostAt(Vector3 position)
    {
        int cell = Data.Cell(position);
        return _costs != null && cell >= 0 ? _costs[cell] : 1f;
    }

    private float Cost(int cell) => _costs?[cell] ?? 1f;

    // Off-mesh links: ways across that walking cannot take (a jump down, a gap, a ladder, a door),
    // by id. Not saved with the grid either.
    private readonly Dictionary<int, NavMeshLink> _links = [];
    private readonly Dictionary<int, List<(int Id, bool Forward)>> _linksFrom = [];
    private int _nextLink = 1;
    // The cheapest a link's cost is for its length: the search's distance estimate is scaled by it,
    // so a link that costs less than walking its length (a teleporter) is still found.
    private float _cheapestLink = 1f;

    /// <summary>The links routes may take, by id.</summary>
    public IReadOnlyDictionary<int, NavMeshLink> Links => _links;

    /// <summary>
    /// Lets routes go straight from <paramref name="start"/> to <paramref name="end"/> (and back,
    /// when <paramref name="bothWays"/>), for <paramref name="cost"/> (its length when 0 or less).
    /// Each end is put on the ground of its cell. Returns the link's id, or 0 when an end is off
    /// the grid or on a cell nothing can stand on.
    /// </summary>
    public int AddLink(Vector3 start, Vector3 end, bool bothWays = true, float cost = 0f)
    {
        int from = Data.Cell(start), to = Data.Cell(end);
        if (from < 0 || to < 0 || from == to || !Data.Walkable[from] || !Data.Walkable[to]) return 0;
        start.Y = Data.Heights[from]; end.Y = Data.Heights[to];
        float length = Vector3.Distance(start, end);
        cost = float.IsFinite(cost) && cost > 0f ? Math.Min(cost, MaximumAreaCost * Math.Max(1f, length)) : length;
        var link = new NavMeshLink(_nextLink++, start, end, bothWays, cost);
        _links[link.Id] = link;
        From(from).Add((link.Id, true));
        if (bothWays) From(to).Add((link.Id, false));
        UpdateCheapestLink();
        return link.Id;
    }

    /// <summary>Takes a link away; routes already found keep it.</summary>
    public bool RemoveLink(int id)
    {
        if (!_links.Remove(id, out NavMeshLink link)) return false;
        foreach (int cell in new[] { Data.Cell(link.Start), Data.Cell(link.End) })
            if (_linksFrom.TryGetValue(cell, out var exits) && exits.RemoveAll(exit => exit.Id == id) > 0 && exits.Count == 0)
                _linksFrom.Remove(cell);
        UpdateCheapestLink();
        return true;
    }

    private List<(int Id, bool Forward)> From(int cell)
    {
        if (!_linksFrom.TryGetValue(cell, out var exits)) _linksFrom[cell] = exits = [];
        return exits;
    }

    private void UpdateCheapestLink()
    {
        _cheapestLink = 1f;
        foreach (NavMeshLink link in _links.Values)
        {
            float length = Vector3.Distance(link.Start, link.End);
            if (length > 1e-4f) _cheapestLink = Math.Min(_cheapestLink, link.Cost / length);
        }
        _cheapestLink = Math.Max(0f, _cheapestLink);
    }

    public IReadOnlyList<Vector3> FindPath(Vector3 start, Vector3 end) => FindRoute(start, end);

    /// <summary>
    /// The cheapest route, smoothed, with the links it takes: an empty route when there is none.
    /// </summary>
    public NavMeshRoute FindRoute(Vector3 start, Vector3 end)
    {
        int first = Data.Cell(start), last = Data.Cell(end);
        if (first < 0 || last < 0 || !Data.Walkable[first] || !Data.Walkable[last]) return NavMeshRoute.Empty;
        start.Y = Data.Heights[first]; end.Y = Data.Heights[last];
        var frontier = new PriorityQueue<int, float>();
        var costs = new Dictionary<int, float> { [first] = 0 };
        var previous = new Dictionary<int, int>();
        // The link a cell was reached by (positive forward, negative backward), when it was.
        var reachedBy = new Dictionary<int, int>();
        var closed = new HashSet<int>();
        float estimate = Math.Min(_cheapest, _links.Count > 0 ? _cheapestLink : 1f);
        frontier.Enqueue(first, 0);
        while (frontier.TryDequeue(out int current, out _))
        {
            if (!closed.Add(current)) continue;
            if (current == last) return Route(first, last, start, end, previous, reachedBy);
            foreach (int neighbor in Data.Neighbors(current))
            {
                // Half of each cell's area cost: the step crosses half of one and half of the other.
                float step = Vector3.Distance(Data.Center(current), Data.Center(neighbor));
                float cost = costs[current] + (_costs == null ? step : step * 0.5f * (Cost(current) + Cost(neighbor)));
                if (costs.TryGetValue(neighbor, out float old) && cost >= old) continue;
                costs[neighbor] = cost; previous[neighbor] = current; reachedBy.Remove(neighbor);
                frontier.Enqueue(neighbor, cost + Vector3.Distance(Data.Center(neighbor), Data.Center(last)) * estimate);
            }
            if (!_linksFrom.TryGetValue(current, out var exits)) continue;
            foreach ((int id, bool forward) in exits)
            {
                NavMeshLink link = _links[id];
                int neighbor = Data.Cell(forward ? link.End : link.Start);
                float cost = costs[current] + link.Cost;
                if (costs.TryGetValue(neighbor, out float old) && cost >= old) continue;
                costs[neighbor] = cost; previous[neighbor] = current; reachedBy[neighbor] = forward ? id : -id;
                frontier.Enqueue(neighbor, cost + Vector3.Distance(Data.Center(neighbor), Data.Center(last)) * estimate);
            }
        }
        return NavMeshRoute.Empty;
    }

    private NavMeshRoute Route(int first, int last, Vector3 start, Vector3 end,
        Dictionary<int, int> previous, Dictionary<int, int> reachedBy)
    {
        var cells = new List<int> { last };
        for (int current = last; current != first;) { current = previous[current]; cells.Add(current); }
        cells.Reverse();
        // Walked stretches are smoothed one at a time; a link's two ends stay where they are.
        var points = new List<Vector3>();
        var links = new List<int>();
        var walk = new List<Vector3> { start };
        for (int index = 1; index < cells.Count; index++)
        {
            if (reachedBy.TryGetValue(cells[index], out int via))
            {
                NavMeshLink link = _links[Math.Abs(via)];
                Vector3 exit = via > 0 ? link.End : link.Start;
                walk.Add(via > 0 ? link.Start : link.End);
                AddWalk(points, links, walk);
                points.Add(exit); links.Add(link.Id);
                walk = [exit];
            }
            else if (index < cells.Count - 1)
            {
                walk.Add(Data.Center(cells[index]));
            }
        }
        walk.Add(end);
        AddWalk(points, links, walk);
        return new NavMeshRoute(points, links);
    }

    // Appends a smoothed walk; the segments it adds are walked (link 0). After the first walk, a
    // walk starts at the end of the link just added, the last point already there.
    private void AddWalk(List<Vector3> points, List<int> links, List<Vector3> walk)
    {
        IReadOnlyList<Vector3> smoothed = PullString(walk);
        for (int i = points.Count == 0 ? 0 : 1; i < smoothed.Count; i++)
        {
            // A link that starts where the route does (or ends where it ends) leaves no walk.
            if (points.Count > 0 && Vector3.DistanceSquared(points[^1], smoothed[i]) < 1e-8f) continue;
            if (points.Count > 0) links.Add(0);
            points.Add(smoothed[i]);
        }
    }
    private IReadOnlyList<Vector3> PullString(List<Vector3> points)
    {
        // Conservative grid string pulling uses a supercover walk, including both cells at a
        // diagonal corner. It cannot smooth a route through an obstacle or across a disconnected edge.
        var result = new List<Vector3> { points[0] };
        // With area costs, what the route costs up to each point, so a shortcut can be compared
        // with the stretch it would replace.
        float[]? routeCost = null;
        if (_costs != null)
        {
            routeCost = new float[points.Count];
            for (int i = 1; i < points.Count; i++) routeCost[i] = routeCost[i - 1] + LineCost(points[i - 1], points[i]);
        }
        int anchor = 0;
        while (anchor < points.Count - 1)
        {
            int next = anchor + 1;
            for (int candidate = points.Count - 1; candidate > next; candidate--)
                if (CanTravel(points[anchor], points[candidate])
                    && (routeCost == null || LineCost(points[anchor], points[candidate]) <= (routeCost[candidate] - routeCost[anchor]) * 1.001f + 1e-4f))
                { next = candidate; break; }
            result.Add(points[next]); anchor = next;
        }
        return result;
    }
    // A straight stretch's cost: its length weighed by the cells it crosses, sampled a quarter of a
    // cell apart. Smoothing takes a shortcut only when it costs no more than the route it replaces,
    // so it neither cuts back across mud the search went round nor leaves a road it chose.
    private float LineCost(Vector3 start, Vector3 end)
    {
        float length = Vector3.Distance(new Vector3(start.X, 0, start.Z), new Vector3(end.X, 0, end.Z));
        if (length <= 0f) return 0f;
        int samples = Math.Max(1, (int)MathF.Ceiling(length / (Data.CellSize * 0.25f)));
        float total = 0f;
        for (int i = 0; i < samples; i++)
        {
            int cell = Data.Cell(Vector3.Lerp(start, end, (i + 0.5f) / samples));
            total += cell >= 0 ? Cost(cell) : 1f;
        }
        return total * length / samples;
    }

    public bool CanTravel(Vector3 start, Vector3 end)
    {
        int cell = Data.Cell(start), target = Data.Cell(end);
        if (cell < 0 || target < 0 || !Data.Walkable[cell] || !Data.Walkable[target]) return false;
        float x = (start.X - Data.OriginX) / Data.CellSize, z = (start.Z - Data.OriginZ) / Data.CellSize;
        float dx = (end.X - start.X) / Data.CellSize, dz = (end.Z - start.Z) / Data.CellSize;
        int stepX = Math.Sign(dx), stepZ = Math.Sign(dz);
        float deltaX = dx == 0 ? float.PositiveInfinity : Math.Abs(1 / dx);
        float deltaZ = dz == 0 ? float.PositiveInfinity : Math.Abs(1 / dz);
        float maxX = dx == 0 ? float.PositiveInfinity : (stepX > 0 ? MathF.Floor(x) + 1 - x : x - MathF.Floor(x)) * deltaX;
        float maxZ = dz == 0 ? float.PositiveInfinity : (stepZ > 0 ? MathF.Floor(z) + 1 - z : z - MathF.Floor(z)) * deltaZ;
        for (int steps = 0; cell != target && steps <= Data.Width + Data.Depth; steps++)
        {
            int next;
            if (Math.Abs(maxX - maxZ) < 1e-6f)
            {
                int sideX = cell + stepX, sideZ = cell + stepZ * Data.Width;
                next = sideZ + stepX;
                if (!Data.Connected(cell, sideX) || !Data.Connected(cell, sideZ)
                    || !Data.Connected(sideX, next) || !Data.Connected(sideZ, next)) return false;
                maxX += deltaX; maxZ += deltaZ;
            }
            else if (maxX < maxZ) { next = cell + stepX; maxX += deltaX; }
            else { next = cell + stepZ * Data.Width; maxZ += deltaZ; }
            if (!Data.Connected(cell, next)) return false;
            cell = next;
        }
        return cell == target;
    }
}

/// <summary>A way across that walking cannot take, from <see cref="Start"/> to <see cref="End"/>.</summary>
public readonly record struct NavMeshLink(int Id, Vector3 Start, Vector3 End, bool BothWays, float Cost);

/// <summary>
/// A route's points, and for each stretch between two of them the link it crosses
/// (<see cref="LinkAt"/>; 0 where it is walked).
/// </summary>
public sealed class NavMeshRoute : IReadOnlyList<Vector3>
{
    public static readonly NavMeshRoute Empty = new([], []);
    private readonly Vector3[] _points;
    private readonly int[] _links;

    public NavMeshRoute(IReadOnlyList<Vector3> points, IReadOnlyList<int> links)
    {
        _points = [.. points];
        _links = new int[Math.Max(0, _points.Length - 1)];
        for (int i = 0; i < _links.Length && i < links.Count; i++) _links[i] = links[i];
    }

    /// <summary>The link crossed from point <paramref name="index"/> to the next; 0 when walked or out of range.</summary>
    public int LinkAt(int index) => index >= 0 && index < _links.Length ? _links[index] : 0;

    public bool HasLinks => Array.Exists(_links, link => link != 0);

    /// <summary>The same route with each point moved (2D rooms keep their routes on the x, z grid).</summary>
    public NavMeshRoute Moved(Func<Vector3, Vector3> move)
    {
        var moved = new Vector3[_points.Length];
        for (int i = 0; i < moved.Length; i++) moved[i] = move(_points[i]);
        return new NavMeshRoute(moved, _links);
    }

    public Vector3 this[int index] => _points[index];
    public int Count => _points.Length;
    public IEnumerator<Vector3> GetEnumerator() => ((IEnumerable<Vector3>)_points).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _points.GetEnumerator();
}
