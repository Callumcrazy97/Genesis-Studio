using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Genesis.Shared.Assets;

namespace Genesis.Runtime.Assets;

public sealed class UnsupportedSpriteFormatException : IOException
{
    public UnsupportedSpriteFormatException(string message) : base(message) { }
}

/// <summary>
/// Loads greenfield <c>.image.json</c> descriptors and resolves frame texture paths for runtime draw.
/// </summary>
public static class SpriteAssetLoader
{
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    private sealed class CacheEntry
    {
        public SpriteRuntimeAsset Asset = null!;
        public long WriteTicks;
        public string SpritePath = string.Empty;
    }

    private static readonly Dictionary<string, CacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, int> FrameCountByImageKey =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, SpriteRuntimeAsset> AssetByImageKey =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool IsSpriteDescriptorPath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.EndsWith(".image.json", StringComparison.OrdinalIgnoreCase);

    public static SpriteRuntimeAsset Load(string spritePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spritePath);
        string fullPath = Path.GetFullPath(spritePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Sprite descriptor was not found.", fullPath);

        long writeTicks = File.GetLastWriteTimeUtc(fullPath).Ticks;
        if (Cache.TryGetValue(fullPath, out CacheEntry cached) && cached.WriteTicks == writeTicks)
            return cached.Asset;

        string json = File.ReadAllText(fullPath);
        SpriteRuntimeAsset asset = Parse(json);
        using (JsonDocument metadata = JsonDocument.Parse(json))
        {
            if (TryGetProperty(metadata.RootElement, "usage", out JsonElement usage))
                asset.Usage = usage.Deserialize<SpriteRuntimeUsage>(JsonOptions) ?? new();
            if (TryGetProperty(metadata.RootElement, "collisionShapes", out JsonElement shapes))
                asset.CollisionShapes = shapes.Deserialize<List<SpriteRuntimeCollisionShape>>(JsonOptions) ?? new();
        }
        Normalize(asset, fullPath);
        Cache[fullPath] = new CacheEntry
        {
            Asset = asset,
            WriteTicks = writeTicks,
            SpritePath = fullPath,
        };
        return asset;
    }

    public static SpriteRuntimeAsset Load(string projectPath, string imageKey)
    {
        string resolved = ResolveDescriptorPath(projectPath, imageKey);
        if (string.IsNullOrWhiteSpace(resolved))
            throw new FileNotFoundException($"Sprite descriptor '{imageKey}' could not be resolved.");
        SpriteRuntimeAsset asset = Load(resolved);
        RegisterAsset(imageKey, asset);
        RegisterAsset(resolved, asset);
        return asset;
    }

    public static int GetFrameCount(string imageKey)
    {
        if (string.IsNullOrWhiteSpace(imageKey))
            return 0;
        return FrameCountByImageKey.TryGetValue(imageKey.Trim(), out int count) ? count : 0;
    }

    public static void RegisterFrameCount(string imageKey, int frameCount)
    {
        if (string.IsNullOrWhiteSpace(imageKey) || frameCount <= 0)
            return;
        FrameCountByImageKey[imageKey.Trim()] = frameCount;
    }

    public static void RegisterAsset(string imageKey, SpriteRuntimeAsset asset)
    {
        if (string.IsNullOrWhiteSpace(imageKey) || asset == null)
            return;
        string key = imageKey.Trim();
        AssetByImageKey[key] = asset;
        RegisterFrameCount(key, asset.Frames.Count);
    }

    public static bool TryGetCachedAsset(string imageKey, out SpriteRuntimeAsset asset)
    {
        asset = null!;
        if (string.IsNullOrWhiteSpace(imageKey))
            return false;
        return AssetByImageKey.TryGetValue(imageKey.Trim(), out asset);
    }

    /// <summary>Forget descriptor and alias state after a project dependency generation changes.</summary>
    public static void ClearCache()
    {
        Cache.Clear();
        FrameCountByImageKey.Clear();
        AssetByImageKey.Clear();
    }

    public static int NormalizeFrameIndex(int frameIndex, int frameCount)
    {
        if (frameCount <= 0)
            return 0;
        int wrapped = frameIndex % frameCount;
        return wrapped < 0 ? wrapped + frameCount : wrapped;
    }

    public static string ResolveFrameTexturePath(string spritePath, SpriteRuntimeAsset asset, int frameIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spritePath);
        ArgumentNullException.ThrowIfNull(asset);
        string fullPath = Path.GetFullPath(spritePath);
        string directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("The sprite descriptor has no parent directory.");

        if (asset.Frames.Count == 0)
            return ResolveFallbackTexture(directory, fullPath, asset.Import.Source);

        int index = NormalizeFrameIndex(frameIndex, asset.Frames.Count);
        SpriteRuntimeFrame frame = asset.Frames[index];
        if (!string.IsNullOrWhiteSpace(frame.Source))
        {
            string relative = frame.Source.Replace('/', Path.DirectorySeparatorChar);
            string candidate = Path.GetFullPath(Path.Combine(directory, relative));
            if (File.Exists(candidate))
                return candidate;
        }

        return ResolveFallbackTexture(directory, fullPath, frame.Source, asset.Import.Source);
    }

    public static string ResolveFrameTexturePath(string projectPath, string imageKey, int frameIndex)
    {
        string spritePath = ResolveDescriptorPath(projectPath, imageKey);
        if (string.IsNullOrWhiteSpace(spritePath))
            return string.Empty;
        if (!IsSpriteDescriptorPath(spritePath)) return spritePath; // private raw-payload compatibility
        SpriteRuntimeAsset asset = Load(spritePath);
        RegisterAsset(imageKey, asset);
        return ResolveFrameTexturePath(spritePath, asset, frameIndex);
    }

    public static string ResolveDescriptorPath(string projectPath, string imageKey)
    {
        return ResourceNames.ResolveFile(projectPath, imageKey, ResourceType.Image);
    }

    private static SpriteRuntimeAsset Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        int schemaVersion = ReadSchemaVersion(root);

        if (schemaVersion == SpriteRuntimeAsset.CurrentSchemaVersion)
        {
            try
            {
                SpriteRuntimeAsset asset = JsonSerializer.Deserialize<SpriteRuntimeAsset>(json, JsonOptions)
                    ?? throw new InvalidDataException("Sprite descriptor is empty.");
                asset.SchemaVersion = schemaVersion;
                return asset;
            }
            catch (JsonException)
            {
                // Editor/template exports may use PascalCase property names and enum integers.
            }
        }

        if (schemaVersion is 1 or 2)
        {
            SpriteRuntimeAsset migrated = MigrateFromDocument(root);
            migrated.SchemaVersion = SpriteRuntimeAsset.CurrentSchemaVersion;
            return migrated;
        }

        throw new UnsupportedSpriteFormatException(
            $"Sprite schema version {schemaVersion} is not supported. Expected version " +
            $"{SpriteRuntimeAsset.CurrentSchemaVersion}.");
    }

    private static int ReadSchemaVersion(JsonElement root)
    {
        if (TryGetProperty(root, "schemaVersion", out JsonElement versionElement) &&
            versionElement.TryGetInt32(out int parsedVersion))
        {
            return parsedVersion;
        }

        return 1;
    }

    private static SpriteRuntimeAsset MigrateFromDocument(JsonElement root)
    {
        int width = 64;
        int height = 64;
        if (TryGetProperty(root, "canvas", out JsonElement canvas))
        {
            if (TryGetInt(canvas, "width", out int canvasWidth) && canvasWidth > 0)
                width = canvasWidth;
            if (TryGetInt(canvas, "height", out int canvasHeight) && canvasHeight > 0)
                height = canvasHeight;
        }

        SpriteRuntimeAsset asset = new()
        {
            Canvas = new SpriteRuntimeCanvas { Width = width, Height = height },
            TextureGroup = TextureGroupDefaults.NormalizeOrDefault(ReadString(root, "textureGroup")),
        };

        if (TryGetProperty(root, "import", out JsonElement import))
            asset.Import.Source = ReadString(import, "source") ?? string.Empty;

        if (TryGetProperty(root, "origin", out JsonElement origin))
        {
            asset.Origin.X = ReadDouble(origin, "x", 0.5);
            asset.Origin.Y = ReadDouble(origin, "y", 0.5);
            asset.Origin.Space = ReadOriginSpace(origin);
        }

        if (TryGetProperty(root, "frames", out JsonElement frames) && frames.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement frameElement in frames.EnumerateArray())
            {
                SpriteRuntimeFrame frame = new()
                {
                    Id = ReadString(frameElement, "id") ?? string.Empty,
                    Name = ReadString(frameElement, "name") ?? string.Empty,
                    DurationMilliseconds = Math.Max(1, ReadInt(frameElement, "durationMilliseconds", 100)),
                    Source = ReadString(frameElement, "source") ?? string.Empty,
                };
                if (TryGetProperty(frameElement, "sourceRectangle", out JsonElement sourceRectangle))
                {
                    frame.SourceRectangle = new SpriteRuntimeRectangle
                    {
                        X = ReadInt(sourceRectangle, "x", 0),
                        Y = ReadInt(sourceRectangle, "y", 0),
                        // An empty rectangle means the whole source image, not its first pixel.
                        Width = Math.Max(0, ReadInt(sourceRectangle, "width", width)),
                        Height = Math.Max(0, ReadInt(sourceRectangle, "height", height)),
                    };
                }
                else
                {
                    frame.SourceRectangle = new SpriteRuntimeRectangle { Width = width, Height = height };
                }

                asset.Frames.Add(frame);
            }
        }

        if (TryGetProperty(root, "tags", out JsonElement tags) && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement tagElement in tags.EnumerateArray())
            {
                asset.Tags.Add(new SpriteRuntimeAnimationTag
                {
                    Name = ReadString(tagElement, "name") ?? string.Empty,
                    StartFrameId = ReadString(tagElement, "startFrameId") ?? string.Empty,
                    EndFrameId = ReadString(tagElement, "endFrameId") ?? string.Empty,
                    Direction = ReadString(tagElement, "direction") ?? "forward",
                    Loop = ReadBool(tagElement, "loop", true),
                });
            }
        }

        return asset;
    }

    private static string ReadOriginSpace(JsonElement origin)
    {
        if (!TryGetProperty(origin, "space", out JsonElement space))
            return "normalized";
        if (space.ValueKind == JsonValueKind.String)
        {
            string value = space.GetString();
            return string.IsNullOrWhiteSpace(value) ? "normalized" : value;
        }

        if (space.TryGetInt32(out int numeric))
            return numeric == 0 ? "pixels" : "normalized";

        return "normalized";
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
            return true;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string ReadString(JsonElement element, string name) =>
        TryGetProperty(element, name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool ReadBool(JsonElement element, string name, bool fallback) =>
        TryGetProperty(element, name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static int ReadInt(JsonElement element, string name, int fallback) =>
        TryGetInt(element, name, out int value) ? value : fallback;

    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        if (!TryGetProperty(element, name, out JsonElement property))
        {
            value = 0;
            return false;
        }

        return property.TryGetInt32(out value);
    }

    private static double ReadDouble(JsonElement element, string name, double fallback) =>
        TryGetProperty(element, name, out JsonElement property) && property.TryGetDouble(out double value)
            ? value
            : fallback;

    private static void Normalize(SpriteRuntimeAsset asset, string spritePath)
    {
        asset.TextureGroup = TextureGroupDefaults.NormalizeOrDefault(asset.TextureGroup);
        if (asset.Frames.Count > 0)
            return;

        string directory = Path.GetDirectoryName(spritePath);
        string stem = Path.GetFileName(spritePath);
        if (stem.EndsWith(".image.json", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^".image.json".Length];

        string source = string.IsNullOrWhiteSpace(asset.Import.Source) ? string.Empty : asset.Import.Source;
        if (string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(directory))
        {
            string[] fallbacks =
            [
                Path.Combine(directory, stem + ".png"),
                Path.Combine(directory, stem + ".spritedata", "frame-0000.png"),
            ];
            foreach (string fallback in fallbacks)
            {
                if (File.Exists(fallback))
                {
                    source = Path.GetRelativePath(directory, fallback).Replace('\\', '/');
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(source))
            return;

        asset.Frames.Add(new SpriteRuntimeFrame
        {
            Id = "frame-0000",
            Name = "Frame 1",
            DurationMilliseconds = 100,
            Source = source,
            SourceRectangle = new SpriteRuntimeRectangle
            {
                Width = Math.Max(1, asset.Canvas.Width),
                Height = Math.Max(1, asset.Canvas.Height),
            },
        });
    }

    private static string ResolveFallbackTexture(
        string directory,
        string spritePath,
        params string[] relativeSources)
    {
        string stem = Path.GetFileName(spritePath);
        if (stem.EndsWith(".image.json", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^".image.json".Length];

        foreach (string relative in relativeSources)
        {
            if (string.IsNullOrWhiteSpace(relative))
                continue;
            string candidate = Path.GetFullPath(
                Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(candidate))
                return candidate;
        }

        string[] ordered =
        [
            Path.Combine(directory, stem + ".png"),
            Path.Combine(directory, stem + ".spritedata", "frame-0000.png"),
        ];
        foreach (string candidate in ordered)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return string.Empty;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
