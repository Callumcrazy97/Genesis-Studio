using System;
using System.Collections.Generic;
using System.Numerics;

namespace Genesis.World
{
    /// <summary>Maps block ids to per-vertex tint colors.</summary>
    public sealed class VoxelPalette
    {
        public static VoxelPalette Default { get; } = new VoxelPalette();
        
        public Func<VoxelBlock, int, (Vector2 Min, Vector2 Max)> UvLookup { get; set; }

        /// <summary>Optional per-face vertex tint when texturing (block, face, worldY) → RGBA.
        /// Null = white (texture shown as authored).</summary>
        public Func<VoxelBlock, int, int, Vector4> TintLookup { get; set; }

        /// <summary>Liquid surface vertex tint from the solid block directly below the water cell.</summary>
        public Func<VoxelBlock, Vector4> WaterFloorTintLookup { get; set; }

        /// <summary>When true, the chunk mesher skips cross-sprite geometry (game draws extruded 3D sprites).</summary>
        public Func<VoxelBlock, bool> SkipCrossMeshLookup { get; set; }

        private readonly Dictionary<VoxelBlock, Vector4> _colors = new Dictionary<VoxelBlock, Vector4>
        {
            [VoxelBlock.Stone] = new Vector4(0.45f, 0.45f, 0.48f, 1f),
            [VoxelBlock.Dirt] = new Vector4(0.52f, 0.40f, 0.28f, 1f),
            [VoxelBlock.Grass] = new Vector4(0.38f, 0.65f, 0.32f, 1f),
            [VoxelBlock.Snow] = new Vector4(0.88f, 0.90f, 0.94f, 1f),
            [VoxelBlock.Sand] = new Vector4(0.86f, 0.78f, 0.52f, 1f),
            [VoxelBlock.Wood] = new Vector4(0.55f, 0.38f, 0.22f, 1f),
            [VoxelBlock.Water] = new Vector4(0.18f, 0.42f, 0.82f, 0.55f),
            [VoxelBlock.Gravel] = new Vector4(0.58f, 0.56f, 0.54f, 1f),
            [VoxelBlock.Leaves] = new Vector4(0.28f, 0.58f, 0.24f, 0.92f),
            [VoxelBlock.Ice] = new Vector4(0.72f, 0.88f, 0.96f, 0.85f),
            [VoxelBlock.Bedrock] = new Vector4(0.22f, 0.22f, 0.24f, 1f),
            [VoxelBlock.Clay] = new Vector4(0.62f, 0.52f, 0.48f, 1f),
            [VoxelBlock.Cobblestone] = new Vector4(0.48f, 0.48f, 0.50f, 1f),
            [VoxelBlock.CoalOre] = new Vector4(0.2f, 0.2f, 0.2f, 1f),
            [VoxelBlock.IronOre] = new Vector4(0.6f, 0.5f, 0.4f, 1f),
            [VoxelBlock.GoldOre] = new Vector4(0.8f, 0.7f, 0.2f, 1f),
            [VoxelBlock.DiamondOre] = new Vector4(0.2f, 0.8f, 0.8f, 1f),
            [VoxelBlock.Lava] = new Vector4(0.9f, 0.3f, 0.05f, 0.9f),
        };

        public Vector4 this[VoxelBlock block] => _colors.TryGetValue(block, out Vector4 c) ? c : Vector4.One;

        public Vector4 Top(VoxelBlock block, int worldY)
        {
            if (block == VoxelBlock.Grass) return TintByHeight(_colors[VoxelBlock.Grass], worldY, 0.010f);
            if (block == VoxelBlock.Sand) return TintByHeight(_colors[VoxelBlock.Sand], worldY, 0.006f);
            return this[block];
        }

        public Vector4 Side(VoxelBlock block) => this[block];

        private static Vector4 TintByHeight(Vector4 color, int worldY, float scale)
        {
            float delta = Math.Clamp(((worldY % 17) - 8) * scale, -0.10f, 0.10f);
            return new Vector4(
                Math.Clamp(color.X + delta, 0f, 1f),
                Math.Clamp(color.Y + delta, 0f, 1f),
                Math.Clamp(color.Z + delta * 0.7f, 0f, 1f),
                color.W);
        }
    }
}
