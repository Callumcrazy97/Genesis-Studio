using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using Genesis.Shared.ECS;

namespace Genesis.Runtime.Spatial
{
    public struct SpatialEntityEntry
    {
        public Entity Entity;
        public int InstanceId;
        public string ObjectName;
        public RectangleF Bounds;
        public bool IsSolid;
    }

    /// <summary>
    /// High-performance 2D spatial hash grid broadphase for Genesis Engine.
    /// Reduces PlaceMeeting, InstancePlace, and collision queries from O(N) linear time to O(1) constant time.
    /// </summary>
    public sealed class SpatialHashGrid2D
    {
        private const float DefaultCellSize = 64f;
        private readonly float _cellSize;
        private readonly float _invCellSize;

        // Grid cells mapped by packed 32-bit (cellX, cellY) key
        private readonly Dictionary<long, List<SpatialEntityEntry>> _grid = new();
        private readonly List<SpatialEntityEntry> _allEntries = new();

        public SpatialHashGrid2D(float cellSize = DefaultCellSize)
        {
            _cellSize = Math.Max(16f, cellSize);
            _invCellSize = 1f / _cellSize;
        }

        public void Clear()
        {
            _grid.Clear();
            _allEntries.Clear();
        }

        public void Insert(Entity entity, int instanceId, string objectName, RectangleF bounds, bool isSolid = false)
        {
            var entry = new SpatialEntityEntry
            {
                Entity = entity,
                InstanceId = instanceId,
                ObjectName = objectName ?? string.Empty,
                Bounds = bounds,
                IsSolid = isSolid,
            };

            _allEntries.Add(entry);

            int minX = (int)MathF.Floor(bounds.Left * _invCellSize);
            int maxX = (int)MathF.Floor(bounds.Right * _invCellSize);
            int minY = (int)MathF.Floor(bounds.Top * _invCellSize);
            int maxY = (int)MathF.Floor(bounds.Bottom * _invCellSize);

            for (int cy = minY; cy <= maxY; cy++)
            {
                for (int cx = minX; cx <= maxX; cx++)
                {
                    long key = PackKey(cx, cy);
                    if (!_grid.TryGetValue(key, out var cellList))
                    {
                        cellList = new List<SpatialEntityEntry>(4);
                        _grid[key] = cellList;
                    }
                    cellList.Add(entry);
                }
            }
        }

        public bool QueryFirst(RectangleF queryBounds, string targetObjectName, int ignoreInstanceId, out SpatialEntityEntry hit)
        {
            hit = default;
            int minX = (int)MathF.Floor(queryBounds.Left * _invCellSize);
            int maxX = (int)MathF.Floor(queryBounds.Right * _invCellSize);
            int minY = (int)MathF.Floor(queryBounds.Top * _invCellSize);
            int maxY = (int)MathF.Floor(queryBounds.Bottom * _invCellSize);

            bool checkName = !string.IsNullOrWhiteSpace(targetObjectName) && !string.Equals(targetObjectName, "all", StringComparison.OrdinalIgnoreCase);

            for (int cy = minY; cy <= maxY; cy++)
            {
                for (int cx = minX; cx <= maxX; cx++)
                {
                    long key = PackKey(cx, cy);
                    if (!_grid.TryGetValue(key, out var cellList)) continue;

                    for (int i = 0; i < cellList.Count; i++)
                    {
                        var candidate = cellList[i];
                        if (candidate.InstanceId == ignoreInstanceId) continue;

                        if (checkName)
                        {
                            bool nameMatch = candidate.ObjectName.Contains(targetObjectName, StringComparison.OrdinalIgnoreCase)
                                || (string.Equals(targetObjectName, "obj_solid", StringComparison.OrdinalIgnoreCase) && candidate.IsSolid);
                            if (!nameMatch) continue;
                        }

                        if (queryBounds.IntersectsWith(candidate.Bounds))
                        {
                            hit = candidate;
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static long PackKey(int x, int y) => ((long)x << 32) | (uint)y;
    }
}
