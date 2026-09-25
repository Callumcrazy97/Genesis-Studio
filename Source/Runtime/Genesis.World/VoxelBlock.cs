namespace Genesis.World
{
    /// <summary>Block types stored in <see cref="VoxelChunk"/> arrays.</summary>
    public enum VoxelBlock : byte
    {
        Air = 0,
        Stone,
        Dirt,
        Grass,
        Snow,
        Sand,
        Wood,
        Water,
        Gravel,
        Leaves,
        Ice,
        Bedrock,
        Clay,
        Cobblestone,
        CoalOre,
        IronOre,
        GoldOre,
        DiamondOre,
        Lava,

        // ── Voxel-survival game content (appended; storage stays a byte enum) ──
        BirchWood,
        BirchLeaves,
        OakPlanks,
        BirchPlanks,
        CoalBlock,
        Furnace,
        FurnaceLit,
        CraftingTable,
        Poppy,
        Bluebell,
        IronBlock,
        GoldBlock,
        DiamondBlock,
        Glowstone,
        Torch,
        WhiteWool,
        WhiteWoolCarpet,
        WhiteBedFoot,
        WhiteBedHead,
        OakSapling,
        BirchSapling,

        Glass,
        CrackedGlass,
        OakDoor,
        BirchDoor,

        // ── Zcode VoxelWorld game content (appended; storage stays a byte enum) ──
        Brick,
        Daisy,
        OakChest,
    }
}
