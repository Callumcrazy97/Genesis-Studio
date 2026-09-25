using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Genesis.Rendering.Textures;
using Genesis.Runtime.Assets;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using StbImageSharp;

namespace Genesis.Runtime.Textures;

/// <summary>
/// Stitches each project Texture Group into one or more GPU atlas sheets at Player start so
/// sprites that share a group batch into the same material run.
/// </summary>
public static class RuntimeTextureAtlas
{
    private sealed class SourceMapping
    {
        public TextureHandle AtlasHandle;
        public Vector4 AtlasUv;
        public string GroupName = TextureGroupDefaults.DefaultName;
        public string SheetName = string.Empty;
    }

    private sealed class GroupDef
    {
        public string Name = TextureGroupDefaults.DefaultName;
        public int AtlasSize = TextureGroupDefaults.DefaultAtlasSize;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, SourceMapping> BySourcePath =
        new(StringComparer.OrdinalIgnoreCase);
    private static TextureAtlasPacker Packer = new();
    private static bool Built;
    private static string ProjectRoot = string.Empty;
    private static int SheetCount;
    private static int SpriteCount;
    private static float OccupancyPercent;

    public static bool IsActive => Built && BySourcePath.Count > 0;
    public static int AtlasSheetCount => SheetCount;
    public static int MappedSpriteCount => SpriteCount;
    public static float AverageOccupancyPercent => OccupancyPercent;
    public static TextureAtlasPacker ActivePacker => Packer;

    public static void Clear()
    {
        lock (Gate)
        {
            BySourcePath.Clear();
            Packer = new TextureAtlasPacker();
            Built = false;
            ProjectRoot = string.Empty;
            SheetCount = 0;
            SpriteCount = 0;
            OccupancyPercent = 0f;
        }
    }

    public static void Build(string projectPath, IRenderController renderer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(renderer);

        lock (Gate)
        {
            ClearUnlocked();
            ProjectRoot = Path.GetFullPath(projectPath);
            string assets = Path.Combine(ProjectRoot, "Assets");
            if (!Directory.Exists(assets))
            {
                Built = true;
                return;
            }

            Dictionary<string, GroupDef> groups = LoadGroups(ProjectRoot);
            // group -> list of (spriteKey, sourcePath, pixels)
            var pending = new Dictionary<string, List<PendingSprite>>(StringComparer.OrdinalIgnoreCase);

            foreach (string descriptor in Directory.EnumerateFiles(
                         assets, "*.image.json", SearchOption.AllDirectories))
            {
                try
                {
                    SpriteRuntimeAsset asset = SpriteAssetLoader.Load(descriptor);
                    string group = TextureGroupDefaults.NormalizeOrDefault(asset.TextureGroup);
                    if (!groups.ContainsKey(group))
                        group = TextureGroupDefaults.DefaultName;

                    if (asset.Frames.Count == 0)
                    {
                        string fallback = SpriteAssetLoader.ResolveFrameTexturePath(descriptor, asset, 0);
                        TryQueue(pending, group, fallback, fallback);
                        continue;
                    }

                    for (int i = 0; i < asset.Frames.Count; i++)
                    {
                        string framePath = SpriteAssetLoader.ResolveFrameTexturePath(descriptor, asset, i);
                        string key = $"{descriptor}|{i}|{framePath}";
                        TryQueue(pending, group, key, framePath);
                    }
                }
                catch
                {
                    // Skip corrupt descriptors; gameplay still loads them individually.
                }
            }

            float occupancySum = 0f;
            int occupancyCount = 0;

            foreach (KeyValuePair<string, List<PendingSprite>> pair in pending)
            {
                if (!groups.TryGetValue(pair.Key, out GroupDef def))
                    def = groups[TextureGroupDefaults.DefaultName];

                // Largest first improves shelf packing.
                pair.Value.Sort((a, b) => (b.Height * b.Width).CompareTo(a.Height * a.Width));

                var sheetPixels = new Dictionary<TextureGroupAtlas, byte[]>(ReferenceEqualityComparer.Instance);

                foreach (PendingSprite sprite in pair.Value)
                {
                    TextureGroupAtlas sheet = Packer.Allocate(
                        pair.Key,
                        sprite.Key,
                        sprite.Width,
                        sprite.Height,
                        def.AtlasSize,
                        out AtlasSpriteEntry entry);

                    if (!sheetPixels.TryGetValue(sheet, out byte[] pixels))
                    {
                        pixels = new byte[sheet.Width * sheet.Height * 4];
                        sheetPixels[sheet] = pixels;
                    }

                    Blit(sprite.Rgba, sprite.Width, sprite.Height, pixels, sheet.Width, entry);
                }

                foreach (KeyValuePair<TextureGroupAtlas, byte[]> sheet in sheetPixels)
                {
                    TextureHandle handle = renderer.CreateTexture(
                        sheet.Key.Width, sheet.Key.Height, sheet.Value);
                    if (!handle.IsValid)
                        continue;

                    sheet.Key.GpuTexture = default; // CPU-side packer handle unused; TextureHandle is above.
                    SheetCount++;
                    occupancySum += sheet.Key.OccupancyPercent;
                    occupancyCount++;

                    foreach (AtlasSpriteEntry entry in sheet.Key.Entries.Values)
                    {
                        // Recover source path from key suffix after last '|' when present.
                        string sourcePath = entry.SpriteKey;
                        int last = entry.SpriteKey.LastIndexOf('|');
                        if (last >= 0 && last + 1 < entry.SpriteKey.Length)
                            sourcePath = entry.SpriteKey[(last + 1)..];

                        string full = Path.GetFullPath(sourcePath);
                        BySourcePath[full] = new SourceMapping
                        {
                            AtlasHandle = handle,
                            AtlasUv = entry.UvRect,
                            GroupName = pair.Key,
                            SheetName = sheet.Key.GroupName,
                        };
                        SpriteCount++;
                    }
                }
            }

            OccupancyPercent = occupancyCount > 0 ? occupancySum / occupancyCount : 0f;
            Built = true;
        }
    }

    public static bool TryRemap(
        string sourceTexturePath,
        in Vector4 sourceUv,
        out TextureHandle atlasHandle,
        out Vector4 atlasUv)
    {
        atlasHandle = TextureHandle.Invalid;
        atlasUv = sourceUv;
        if (string.IsNullOrWhiteSpace(sourceTexturePath))
            return false;

        lock (Gate)
        {
            if (!Built)
                return false;
            string full = Path.GetFullPath(sourceTexturePath);
            if (!BySourcePath.TryGetValue(full, out SourceMapping mapping))
                return false;

            atlasHandle = mapping.AtlasHandle;
            atlasUv = RemapUv(mapping.AtlasUv, sourceUv);
            return atlasHandle.IsValid;
        }
    }

    private static Vector4 RemapUv(in Vector4 atlasRect, in Vector4 localUv)
    {
        // The sprite contract uses a zero/empty rectangle for the whole source image.
        // Mapping zero literally collapses to one atlas texel; the shader then expands
        // that empty rectangle to the entire atlas, making every object show the atlas.
        Vector4 source = localUv.Z <= localUv.X || localUv.W <= localUv.Y
            ? new Vector4(0, 0, 1, 1) : localUv;
        // localUv is corners (u0,v0,u1,v1) in the source texture; atlasRect is the packed region.
        float u0 = Lerp(atlasRect.X, atlasRect.Z, source.X);
        float v0 = Lerp(atlasRect.Y, atlasRect.W, source.Y);
        float u1 = Lerp(atlasRect.X, atlasRect.Z, source.Z);
        float v1 = Lerp(atlasRect.Y, atlasRect.W, source.W);
        return new Vector4(u0, v0, u1, v1);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static void ClearUnlocked()
    {
        BySourcePath.Clear();
        Packer = new TextureAtlasPacker();
        Built = false;
        ProjectRoot = string.Empty;
        SheetCount = 0;
        SpriteCount = 0;
        OccupancyPercent = 0f;
    }

    private static Dictionary<string, GroupDef> LoadGroups(string projectPath)
    {
        var map = new Dictionary<string, GroupDef>(StringComparer.OrdinalIgnoreCase)
        {
            [TextureGroupDefaults.DefaultName] = new GroupDef(),
        };

        try
        {
            string[] projects = Directory.GetFiles(projectPath, "*.genesisproj", SearchOption.TopDirectoryOnly);
            if (projects.Length == 0)
                return map;

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(projects[0]));
            if (!doc.RootElement.TryGetProperty("runtime", out JsonElement runtime)
                && !doc.RootElement.TryGetProperty("Runtime", out runtime))
            {
                return map;
            }

            if (!runtime.TryGetProperty("textureGroups", out JsonElement groups)
                && !runtime.TryGetProperty("TextureGroups", out groups))
            {
                return map;
            }

            if (groups.ValueKind != JsonValueKind.Array)
                return map;

            foreach (JsonElement group in groups.EnumerateArray())
            {
                string name = TextureGroupDefaults.DefaultName;
                if (group.TryGetProperty("name", out JsonElement nameEl)
                    || group.TryGetProperty("Name", out nameEl))
                {
                    name = TextureGroupDefaults.NormalizeOrDefault(nameEl.GetString());
                }

                int size = TextureGroupDefaults.DefaultAtlasSize;
                if ((group.TryGetProperty("atlasSize", out JsonElement sizeEl)
                     || group.TryGetProperty("AtlasSize", out sizeEl))
                    && sizeEl.TryGetInt32(out int parsed))
                {
                    size = TextureGroupDefaults.NormalizeAtlasSize(parsed);
                }

                map[name] = new GroupDef { Name = name, AtlasSize = size };
            }

            if (!map.ContainsKey(TextureGroupDefaults.DefaultName))
                map[TextureGroupDefaults.DefaultName] = new GroupDef();
        }
        catch
        {
            // Keep Default only.
        }

        return map;
    }

    private static void TryQueue(
        Dictionary<string, List<PendingSprite>> pending,
        string group,
        string key,
        string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return;

        string full = Path.GetFullPath(sourcePath);
        // One mapping per source file — frames that share pixels share the atlas slot.
        foreach (List<PendingSprite> list in pending.Values)
        {
            foreach (PendingSprite existing in list)
            {
                if (string.Equals(existing.SourcePath, full, StringComparison.OrdinalIgnoreCase))
                {
                    // Alias this key onto the same source; Build remaps by source path.
                    return;
                }
            }
        }

        try
        {
            using FileStream stream = File.OpenRead(full);
            ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            if (image is null || image.Width <= 0 || image.Height <= 0 || image.Data is null)
                return;

            if (!pending.TryGetValue(group, out List<PendingSprite> bucket))
            {
                bucket = [];
                pending[group] = bucket;
            }

            bucket.Add(new PendingSprite
            {
                Key = string.IsNullOrWhiteSpace(key) ? full : key,
                SourcePath = full,
                Width = image.Width,
                Height = image.Height,
                Rgba = image.Data,
            });
        }
        catch
        {
            // Skip undecodable sources.
        }
    }

    private static void Blit(
        byte[] source,
        int srcW,
        int srcH,
        byte[] dest,
        int destW,
        AtlasSpriteEntry entry)
    {
        for (int y = 0; y < srcH; y++)
        {
            int srcRow = y * srcW * 4;
            int dstRow = ((entry.PixelY + y) * destW + entry.PixelX) * 4;
            Buffer.BlockCopy(source, srcRow, dest, dstRow, srcW * 4);
        }
    }

    private sealed class PendingSprite
    {
        public string Key = string.Empty;
        public string SourcePath = string.Empty;
        public int Width;
        public int Height;
        public byte[] Rgba = [];
    }
}
