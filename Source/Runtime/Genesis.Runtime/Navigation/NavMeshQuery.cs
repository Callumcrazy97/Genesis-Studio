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
    public IReadOnlyList<Vector3> FindPath(Vector3 start, Vector3 end)
    {
        int first = Data.Cell(start), last = Data.Cell(end);
        if (first < 0 || last < 0 || !Data.Walkable[first] || !Data.Walkable[last]) return [];
        start.Y = Data.Heights[first]; end.Y = Data.Heights[last];
        var frontier = new PriorityQueue<int, float>();
        var costs = new Dictionary<int, float> { [first] = 0 };
        var previous = new Dictionary<int, int>();
        var closed = new HashSet<int>();
        frontier.Enqueue(first, 0);
        while (frontier.TryDequeue(out int current, out _))
        {
            if (!closed.Add(current)) continue;
            if (current == last)
            {
                var cells = new List<int> { last };
                while (current != first) { current = previous[current]; cells.Add(current); }
                cells.Reverse();
                var points = new List<Vector3> { start };
                points.AddRange(cells.Skip(1).SkipLast(1).Select(Data.Center)); points.Add(end);
                return PullString(points);
            }
            foreach (int neighbor in Data.Neighbors(current))
            {
                // Half of each cell's area cost: the step crosses half of one and half of the other.
                float step = Vector3.Distance(Data.Center(current), Data.Center(neighbor));
                float cost = costs[current] + (_costs == null ? step : step * 0.5f * (Cost(current) + Cost(neighbor)));
                if (costs.TryGetValue(neighbor, out float old) && cost >= old) continue;
                costs[neighbor] = cost; previous[neighbor] = current;
                frontier.Enqueue(neighbor, cost + Vector3.Distance(Data.Center(neighbor), Data.Center(last)) * _cheapest);
            }
        }
        return [];
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
