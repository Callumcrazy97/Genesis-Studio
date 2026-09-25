using System;
using System.Numerics;

namespace Genesis.World
{
    public readonly struct VoxelRaycastHit
    {
        public bool Hit { get; init; }
        public int BlockX { get; init; }
        public int BlockY { get; init; }
        public int BlockZ { get; init; }
        public int PlaceX { get; init; }
        public int PlaceY { get; init; }
        public int PlaceZ { get; init; }
        public VoxelBlock Block { get; init; }
        public Vector3 Normal { get; init; }
    }

    /// <summary>DDA voxel ray traversal (Minecraft-style block picking).</summary>
    public static class VoxelRaycast
    {
        public static VoxelRaycastHit Cast(VoxelWorld world, Vector3 origin, Vector3 direction, float maxDistance = 8f)
        {
            direction = Vector3.Normalize(direction);
            if (direction.LengthSquared() < 0.0001f)
                return default;

            int x = (int)MathF.Floor(origin.X);
            int y = (int)MathF.Floor(origin.Y);
            int z = (int)MathF.Floor(origin.Z);

            int stepX = direction.X > 0 ? 1 : (direction.X < 0 ? -1 : 0);
            int stepY = direction.Y > 0 ? 1 : (direction.Y < 0 ? -1 : 0);
            int stepZ = direction.Z > 0 ? 1 : (direction.Z < 0 ? -1 : 0);

            float tDeltaX = stepX != 0 ? MathF.Abs(1f / direction.X) : float.MaxValue;
            float tDeltaY = stepY != 0 ? MathF.Abs(1f / direction.Y) : float.MaxValue;
            float tDeltaZ = stepZ != 0 ? MathF.Abs(1f / direction.Z) : float.MaxValue;

            float fracX = stepX > 0 ? (x + 1 - origin.X) : (stepX < 0 ? origin.X - x : float.MaxValue);
            float fracY = stepY > 0 ? (y + 1 - origin.Y) : (stepY < 0 ? origin.Y - y : float.MaxValue);
            float fracZ = stepZ > 0 ? (z + 1 - origin.Z) : (stepZ < 0 ? origin.Z - z : float.MaxValue);

            float tMaxX = stepX != 0 ? fracX * tDeltaX : float.MaxValue;
            float tMaxY = stepY != 0 ? fracY * tDeltaY : float.MaxValue;
            float tMaxZ = stepZ != 0 ? fracZ * tDeltaZ : float.MaxValue;

            int prevX = x, prevY = y, prevZ = z;
            float traveled = 0f;

            while (traveled <= maxDistance)
            {
                VoxelBlock block = world.GetBlock(x, y, z);
                if (block != VoxelBlock.Air && VoxelBlocks.IsSolid(block))
                {
                    Vector3 normal = new Vector3(prevX - x, prevY - y, prevZ - z);
                    if (normal == Vector3.Zero)
                        normal = -direction;

                    return new VoxelRaycastHit
                    {
                        Hit = true,
                        BlockX = x,
                        BlockY = y,
                        BlockZ = z,
                        PlaceX = prevX,
                        PlaceY = prevY,
                        PlaceZ = prevZ,
                        Block = block,
                        Normal = Vector3.Normalize(normal),
                    };
                }

                prevX = x;
                prevY = y;
                prevZ = z;

                if (tMaxX < tMaxY)
                {
                    if (tMaxX < tMaxZ)
                    {
                        x += stepX;
                        traveled = tMaxX;
                        tMaxX += tDeltaX;
                    }
                    else
                    {
                        z += stepZ;
                        traveled = tMaxZ;
                        tMaxZ += tDeltaZ;
                    }
                }
                else
                {
                    if (tMaxY < tMaxZ)
                    {
                        y += stepY;
                        traveled = tMaxY;
                        tMaxY += tDeltaY;
                    }
                    else
                    {
                        z += stepZ;
                        traveled = tMaxZ;
                        tMaxZ += tDeltaZ;
                    }
                }
            }

            return default;
        }
    }
}
