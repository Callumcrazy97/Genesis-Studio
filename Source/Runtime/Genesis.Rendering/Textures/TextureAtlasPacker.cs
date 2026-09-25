using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using Genesis.Rendering.Abstractions;

namespace Genesis.Rendering.Textures
{
    public sealed class AtlasSpriteEntry
    {
        public string SpriteKey { get; set; }
        public int PixelX { get; set; }
        public int PixelY { get; set; }
        public int PixelWidth { get; set; }
        public int PixelHeight { get; set; }
        public Vector4 UvRect { get; set; } // u0, v0, u1, v1
    }

    public sealed class TextureGroupAtlas
    {
        public string GroupName { get; set; } = "Default Texture Group";
        public int Width { get; set; } = 2048;
        public int Height { get; set; } = 2048;
        public GpuTextureHandle GpuTexture { get; set; } = GpuTextureHandle.Invalid;
        public int SpriteCount => _entries.Count;
        public long VramBytes => (long)Width * Height * 4;
        public float OccupancyPercent { get; private set; }

        private readonly Dictionary<string, AtlasSpriteEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, AtlasSpriteEntry> Entries => _entries;

        // Shelf packing state
        private int _currentX = 0;
        private int _currentY = 0;
        private int _currentRowHeight = 0;
        private long _usedPixels = 0;

        public TextureGroupAtlas(string groupName, int size = 2048)
        {
            GroupName = groupName ?? "Default Texture Group";
            Width = size >= 4096 ? 4096 : 2048;
            Height = Width;
        }

        public bool TryAllocate(string spriteKey, int pixelWidth, int pixelHeight, out AtlasSpriteEntry entry)
        {
            if (_entries.TryGetValue(spriteKey, out entry))
                return true;

            int padding = 2;
            int allocW = pixelWidth + padding * 2;
            int allocH = pixelHeight + padding * 2;

            if (_currentX + allocW > Width)
            {
                // Move to next shelf
                _currentX = 0;
                _currentY += _currentRowHeight;
                _currentRowHeight = 0;
            }

            if (_currentY + allocH > Height)
            {
                entry = null;
                return false; // Atlas full
            }

            int posX = _currentX + padding;
            int posY = _currentY + padding;

            _currentX += allocW;
            _currentRowHeight = Math.Max(_currentRowHeight, allocH);
            _usedPixels += (long)pixelWidth * pixelHeight;
            OccupancyPercent = Math.Clamp(((float)_usedPixels / (Width * Height)) * 100f, 0f, 100f);

            float invW = 1f / Width;
            float invH = 1f / Height;

            entry = new AtlasSpriteEntry
            {
                SpriteKey = spriteKey,
                PixelX = posX,
                PixelY = posY,
                PixelWidth = pixelWidth,
                PixelHeight = pixelHeight,
                UvRect = new Vector4(
                    posX * invW,
                    posY * invH,
                    (posX + pixelWidth) * invW,
                    (posY + pixelHeight) * invH),
            };

            _entries[spriteKey] = entry;
            return true;
        }

        public bool TryGet(string spriteKey, out AtlasSpriteEntry entry) =>
            _entries.TryGetValue(spriteKey, out entry);
    }

    /// <summary>
    /// Automatic Texture Group and runtime atlas stitcher for Genesis Engine.
    /// Manages per-group texture atlases (2048x2048 or 4096x4096) and remaps sprite UVs
    /// so all sprites in the same group draw in 1 single hardware batch.
    /// </summary>
    public sealed class TextureAtlasPacker
    {
        private readonly Dictionary<string, List<TextureGroupAtlas>> _atlases = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, List<TextureGroupAtlas>> AtlasSheets => _atlases;

        /// <summary>Flattened view of the first sheet per group (F6 / legacy callers).</summary>
        public IReadOnlyDictionary<string, TextureGroupAtlas> Atlases
        {
            get
            {
                Dictionary<string, TextureGroupAtlas> map = new(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, List<TextureGroupAtlas>> pair in _atlases)
                {
                    if (pair.Value.Count > 0)
                        map[pair.Key] = pair.Value[0];
                }

                return map;
            }
        }

        public void Clear() => _atlases.Clear();

        public TextureGroupAtlas GetOrCreateAtlas(string groupName, int defaultSize = 2048)
        {
            if (string.IsNullOrWhiteSpace(groupName))
                groupName = "Default Texture Group";

            if (!_atlases.TryGetValue(groupName, out List<TextureGroupAtlas> sheets) || sheets.Count == 0)
            {
                sheets = [];
                TextureGroupAtlas atlas = new(groupName, defaultSize);
                sheets.Add(atlas);
                _atlases[groupName] = sheets;
                return atlas;
            }

            return sheets[0];
        }

        /// <summary>
        /// Allocates into the group's sheets, creating an overflow sheet when the current one is full.
        /// Never silently leaves a sprite unmapped.
        /// </summary>
        public TextureGroupAtlas Allocate(
            string groupName,
            string spriteKey,
            int pixelWidth,
            int pixelHeight,
            int atlasSize,
            out AtlasSpriteEntry entry)
        {
            if (string.IsNullOrWhiteSpace(groupName))
                groupName = "Default Texture Group";

            if (!_atlases.TryGetValue(groupName, out List<TextureGroupAtlas> sheets))
            {
                sheets = [];
                _atlases[groupName] = sheets;
            }

            foreach (TextureGroupAtlas sheet in sheets)
            {
                if (sheet.TryAllocate(spriteKey, pixelWidth, pixelHeight, out entry) && entry is not null)
                    return sheet;
            }

            var overflow = new TextureGroupAtlas($"{groupName}#{sheets.Count + 1}", atlasSize);
            sheets.Add(overflow);
            if (!overflow.TryAllocate(spriteKey, pixelWidth, pixelHeight, out entry) || entry is null)
            {
                throw new InvalidOperationException(
                    $"Sprite '{spriteKey}' ({pixelWidth}x{pixelHeight}) does not fit a {atlasSize} atlas sheet.");
            }

            return overflow;
        }

        public bool TryGetSpriteUv(string groupName, string spriteKey, out Vector4 uvRect, out GpuTextureHandle texture)
        {
            uvRect = new Vector4(0, 0, 1, 1);
            texture = GpuTextureHandle.Invalid;

            if (string.IsNullOrWhiteSpace(groupName))
                groupName = "Default Texture Group";

            if (!_atlases.TryGetValue(groupName, out List<TextureGroupAtlas> sheets))
                return false;

            foreach (TextureGroupAtlas atlas in sheets)
            {
                if (!atlas.TryGet(spriteKey, out AtlasSpriteEntry entry))
                    continue;
                uvRect = entry.UvRect;
                texture = atlas.GpuTexture;
                return true;
            }

            return false;
        }
    }
}
