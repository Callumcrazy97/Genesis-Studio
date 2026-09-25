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
                float cost = costs[current] + Vector3.Distance(Data.Center(current), Data.Center(neighbor));
                if (costs.TryGetValue(neighbor, out float old) && cost >= old) continue;
                costs[neighbor] = cost; previous[neighbor] = current;
                frontier.Enqueue(neighbor, cost + Vector3.Distance(Data.Center(neighbor), Data.Center(last)));
            }
        }
        return [];
    }
    private IReadOnlyList<Vector3> PullString(List<Vector3> points)
    {
        // Conservative grid string pulling uses a supercover walk, including both cells at a
        // diagonal corner. It cannot smooth a route through an obstacle or across a disconnected edge.
        var result = new List<Vector3> { points[0] };
        int anchor = 0;
        while (anchor < points.Count - 1)
        {
            int next = anchor + 1;
            for (int candidate = points.Count - 1; candidate > next; candidate--)
                if (CanTravel(points[anchor], points[candidate])) { next = candidate; break; }
            result.Add(points[next]); anchor = next;
        }
        return result;
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
