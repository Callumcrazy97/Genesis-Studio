namespace Genesis.World
{
    /// <summary>Per-block gameplay and rendering flags.</summary>
    public readonly struct BlockDefinition
    {
        public bool IsSolid { get; init; }
        public bool IsOpaque { get; init; }
        public bool IsLiquid { get; init; }
        public bool CastsShadows { get; init; }
        /// <summary>Rendered as two diagonal alpha-tested quads (flowers/plants) instead of a cube.</summary>
        public bool IsCross { get; init; }
        /// <summary>Rendered as a thin slab hugging the floor (carpets) instead of a full cube.</summary>
        public bool IsCarpet { get; init; }
        /// <summary>Rendered as a low two-cell furniture model (beds) instead of a full cube.</summary>
        public bool IsBed { get; init; }
    }

    /// <summary>Static lookup table for <see cref="VoxelBlock"/> metadata.</summary>
    public static class VoxelBlocks
    {
        private static readonly BlockDefinition[] Table = BuildTable();

        public static ref readonly BlockDefinition Get(VoxelBlock block) => ref Table[(int)block];

        public static bool IsSolid(VoxelBlock block) => Get(block).IsSolid;
        public static bool IsOpaque(VoxelBlock block) => Get(block).IsOpaque;
        public static bool IsLiquid(VoxelBlock block) => Get(block).IsLiquid;
        public static bool IsCross(VoxelBlock block) => Get(block).IsCross;
        public static bool IsCarpet(VoxelBlock block) => Get(block).IsCarpet;
        public static bool IsBed(VoxelBlock block) => Get(block).IsBed;

        private static BlockDefinition[] BuildTable()
        {
            var t = new BlockDefinition[256];
            t[(int)VoxelBlock.Air] = new BlockDefinition { IsSolid = false, IsOpaque = false, IsLiquid = false, CastsShadows = false };

            foreach (VoxelBlock solid in new[]
            {
                VoxelBlock.Stone, VoxelBlock.Dirt, VoxelBlock.Grass, VoxelBlock.Snow, VoxelBlock.Sand,
                VoxelBlock.Wood, VoxelBlock.Gravel, VoxelBlock.Leaves, VoxelBlock.Ice, VoxelBlock.Bedrock,
                VoxelBlock.Clay, VoxelBlock.Cobblestone,
                VoxelBlock.CoalOre, VoxelBlock.IronOre, VoxelBlock.GoldOre, VoxelBlock.DiamondOre
            })
            {
                t[(int)solid] = new BlockDefinition
                {
                    IsSolid = true,
                    IsOpaque = solid != VoxelBlock.Leaves,
                    IsLiquid = false,
                    CastsShadows = solid != VoxelBlock.Leaves,
                };
            }

            t[(int)VoxelBlock.Water] = new BlockDefinition
            {
                IsSolid = false,
                IsOpaque = false,
                IsLiquid = true,
                CastsShadows = false,
            };

            t[(int)VoxelBlock.Lava] = new BlockDefinition
            {
                IsSolid = false,
                IsOpaque = false,
                IsLiquid = true,
                CastsShadows = true, // Lava casts light (emissive) usually, we can mark shadow casting to true or false. Actually, liquid usually doesn't cast directional shadows. Let's make it false.
            };
            t[(int)VoxelBlock.Lava] = new BlockDefinition { IsSolid = false, IsOpaque = false, IsLiquid = true, CastsShadows = false };

            // ── Voxel-survival game content ──
            foreach (VoxelBlock solid in new[]
            {
                VoxelBlock.BirchWood, VoxelBlock.OakPlanks, VoxelBlock.BirchPlanks,
                VoxelBlock.CoalBlock, VoxelBlock.Furnace, VoxelBlock.FurnaceLit,
                VoxelBlock.CraftingTable, VoxelBlock.IronBlock, VoxelBlock.GoldBlock,
                VoxelBlock.DiamondBlock, VoxelBlock.Glowstone, VoxelBlock.WhiteWool,
            })
            {
                t[(int)solid] = new BlockDefinition
                {
                    IsSolid = true, IsOpaque = true, IsLiquid = false, CastsShadows = true,
                };
            }

            // Birch leaves: solid but non-opaque (lets neighbour faces show through, like oak leaves).
            t[(int)VoxelBlock.BirchLeaves] = new BlockDefinition
            {
                IsSolid = true, IsOpaque = false, IsLiquid = false, CastsShadows = false,
            };

            // Flowers: targetable/breakable (IsSolid) and meshed, but non-opaque and non-shadowing.
            // The game's BlockRegistry marks them non-collidable so the player walks through them.
            foreach (VoxelBlock flower in new[]
            {
                VoxelBlock.Poppy, VoxelBlock.Bluebell, VoxelBlock.Torch,
                VoxelBlock.OakSapling, VoxelBlock.BirchSapling,
            })
            {
                t[(int)flower] = new BlockDefinition
                {
                    IsSolid = true, IsOpaque = false, IsLiquid = false, CastsShadows = false, IsCross = true,
                };
            }

            // Carpet: targetable thin slab, doesn't occlude neighbours, no directional shadow.
            t[(int)VoxelBlock.WhiteWoolCarpet] = new BlockDefinition
            {
                IsSolid = true, IsOpaque = false, IsLiquid = false, CastsShadows = false, IsCarpet = true,
            };

            // Bed: two-cell furniture model, targetable, doesn't occlude neighbours.
            foreach (VoxelBlock bed in new[] { VoxelBlock.WhiteBedFoot, VoxelBlock.WhiteBedHead })
            {
                t[(int)bed] = new BlockDefinition
                {
                    IsSolid = true, IsOpaque = false, IsLiquid = false, CastsShadows = false, IsBed = true,
                };
            }

            t[(int)VoxelBlock.Glass] = new BlockDefinition
            {
                IsSolid = true, IsOpaque = false, IsLiquid = false, CastsShadows = false,
            };

            t[(int)VoxelBlock.CrackedGlass] = new BlockDefinition
            {
                IsSolid = true, IsOpaque = false, IsLiquid = false, CastsShadows = false,
            };

            foreach (VoxelBlock door in new[] { VoxelBlock.OakDoor, VoxelBlock.BirchDoor })
            {
                t[(int)door] = new BlockDefinition
                {
                    IsSolid = true, IsOpaque = true, IsLiquid = false, CastsShadows = true,
                };
            }

            // ── Zcode VoxelWorld game content ──
            // Brick + OakChest: solid opaque cubes.
            foreach (VoxelBlock solid in new[] { VoxelBlock.Brick, VoxelBlock.OakChest })
            {
                t[(int)solid] = new BlockDefinition
                {
                    IsSolid = true, IsOpaque = true, IsLiquid = false, CastsShadows = true,
                };
            }
            // Daisy: cross flower (like Poppy/Bluebell).
            t[(int)VoxelBlock.Daisy] = new BlockDefinition
            {
                IsSolid = true, IsOpaque = false, IsLiquid = false, CastsShadows = false, IsCross = true,
            };

            return t;
        }
    }
}
