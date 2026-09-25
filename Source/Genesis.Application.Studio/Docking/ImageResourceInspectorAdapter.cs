using System.Globalization;
using System.Text.RegularExpressions;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Suite;

namespace Genesis.Application.Studio.Docking;

/// <summary>
/// Presents the shared Image session/workspace to Studio's global Inspector. Every edit is an
/// Image history command, so the viewer, detached editor, Undo and Save continue to agree.
/// </summary>
internal sealed class ImageResourceInspectorAdapter(
    ImageDocumentSession session,
    ImageWorkspace workspace,
    Action refresh,
    Func<IReadOnlyList<string>?>? listTextureGroups = null)
{
    private readonly ImageDocumentSession _session = session;
    private readonly ImageWorkspace _workspace = workspace;
    private readonly Action _refresh = refresh;
    private readonly Func<IReadOnlyList<string>?>? _listTextureGroups = listTextureGroups;

    public IReadOnlyList<ResourceInspectorLiveValue> GetValues()
    {
        ImageDocument document = _session.Document;
        List<ResourceInspectorLiveValue> values = [];

        AddUsage(values, document);
        values.Add(new("Source & import", "import.source", "Source", document.Import.Source ?? string.Empty,
            ReadOnly: true, Description: "Use Import in the Image Viewer to replace source pixels safely."));
        values.Add(new("Source & import", "import.mode", "Import mode", document.Import.Mode.ToString(),
            Choices: Enum.GetNames<ImageImportMode>()));
        values.Add(new("Source & import", "import.preserveSource", "Preserve source", document.Import.PreserveSource));
        values.Add(new("Source & import", "import.premultiplyAlpha", "Premultiply alpha", document.Import.PremultiplyAlpha));
        values.Add(Number("Source & import", "import.cellWidth", "Cell width", document.Import.CellWidth, 0, 16_384, 1, 0));
        values.Add(Number("Source & import", "import.cellHeight", "Cell height", document.Import.CellHeight, 0, 16_384, 1, 0));
        values.Add(Number("Source & import", "import.margin", "Margin", document.Import.Margin, 0, 16_384, 1, 0));
        values.Add(Number("Source & import", "import.spacing", "Spacing", document.Import.Spacing, 0, 16_384, 1, 0));
        string textureGroup = string.IsNullOrWhiteSpace(document.TextureGroup)
            ? TextureGroupCatalog.DefaultName
            : document.TextureGroup.Trim();
        values.Add(new(
            "Texture group",
            "textureGroup",
            "Texture group",
            textureGroup,
            Choices: TextureGroupChoices()));

        ImageRenderingSettings sprite = document.Usage.Sprite;
        values.Add(new("Sprite rendering", "usage.sprite.filter", "Filter", sprite.Filter.ToString(),
            Choices: Enum.GetNames<ImageFilterMode>()));
        values.Add(new("Sprite rendering", "usage.sprite.wrap", "Wrap", sprite.Wrap.ToString(),
            Choices: Enum.GetNames<ImageWrapMode>()));
        values.Add(Number("Sprite rendering", "usage.sprite.pixelsPerUnit", "Pixels per unit",
            sprite.PixelsPerUnit, 0.01m, 100_000, 1, 2));
        values.Add(new("Sprite rendering", "usage.sprite.generateMipmaps", "Generate mipmaps", sprite.GenerateMipmaps));

        if (document.Usage.Supports(ImageUsage.Tileset)) AddTileset(values, document.Usage.Tileset);
        if (document.Usage.Supports(ImageUsage.Background)) AddBackground(values, document.Usage.Background);
        if (document.Usage.Supports(ImageUsage.Texture)) AddMaterial(values, document.Usage.Material);

        values.Add(Number("Origin & pivot", "origin.x", "X", document.Origin.X, -16_384, 16_384, 0.01m, 3));
        values.Add(Number("Origin & pivot", "origin.y", "Y", document.Origin.Y, -16_384, 16_384, 0.01m, 3));
        values.Add(new("Origin & pivot", "origin.space", "Coordinate space", document.Origin.Space.ToString(),
            Choices: Enum.GetNames<ImageCoordinateSpace>()));

        AddActiveFrame(values, document);
        AddActiveLayer(values);
        values.Add(new("Canvas", "canvas.width", "Width", _workspace.Width, ReadOnly: true));
        values.Add(new("Canvas", "canvas.height", "Height", _workspace.Height, ReadOnly: true));
        values.Add(new("Canvas", "canvas.pixelFormat", "Pixel format", document.Canvas.PixelFormat.ToString(), ReadOnly: true));
        values.Add(new("Canvas", "canvas.colorSpace", "Colour space", document.Canvas.ColorSpace.ToString(), ReadOnly: true));
        values.Add(new("Image summary", "Summary.Frames", "Frames", _workspace.Frames.Count, ReadOnly: true));
        values.Add(new("Image summary", "Summary.Layers", "Layers", _workspace.CurrentFrame?.Layers.Count ?? 0, ReadOnly: true));
        values.Add(new("Image summary", "Summary.Tags", "Animation tags", document.Tags.Count, ReadOnly: true));
        values.Add(new("Image summary", "Summary.CollisionShapes", "Collision shapes", document.CollisionShapes.Count, ReadOnly: true));
        return values;
    }

    public bool TryApply(string path, object? value)
    {
        ImageDocument document = _session.Document;
        bool handled = path.ToLowerInvariant() switch
        {
            "usage.sprite" => ApplyUsage(ImageUsage.Sprite, value),
            "usage.tileset" => ApplyUsage(ImageUsage.Tileset, value),
            "usage.background" => ApplyUsage(ImageUsage.Background, value),
            "usage.texture" => ApplyUsage(ImageUsage.Texture, value),
            "usage.nineslice" => ApplyUsage(ImageUsage.NineSlice, value),
            "import.mode" => ApplyEnum("Change import mode", () => document.Import.Mode,
                next => document.Import.Mode = next, value),
            "import.preservesource" => Apply("Preserve image source", () => document.Import.PreserveSource,
                next => document.Import.PreserveSource = next, Convert.ToBoolean(value, CultureInfo.InvariantCulture)),
            "import.premultiplyalpha" => Apply("Premultiply image alpha", () => document.Import.PremultiplyAlpha,
                next => document.Import.PremultiplyAlpha = next, Convert.ToBoolean(value, CultureInfo.InvariantCulture)),
            "import.cellwidth" => Apply("Import cell width", () => document.Import.CellWidth,
                next => document.Import.CellWidth = next, Math.Clamp(Convert.ToInt32(value, CultureInfo.InvariantCulture), 0, 16_384)),
            "import.cellheight" => Apply("Import cell height", () => document.Import.CellHeight,
                next => document.Import.CellHeight = next, Math.Clamp(Convert.ToInt32(value, CultureInfo.InvariantCulture), 0, 16_384)),
            "import.margin" => Apply("Import margin", () => document.Import.Margin,
                next => document.Import.Margin = next, Math.Clamp(Convert.ToInt32(value, CultureInfo.InvariantCulture), 0, 16_384)),
            "import.spacing" => Apply("Import spacing", () => document.Import.Spacing,
                next => document.Import.Spacing = next, Math.Clamp(Convert.ToInt32(value, CultureInfo.InvariantCulture), 0, 16_384)),
            "texturegroup" => ApplyTextureGroup(value),
            "usage.sprite.filter" => ApplyEnum("Sprite filter", () => document.Usage.Sprite.Filter,
                next => document.Usage.Sprite.Filter = next, value),
            "usage.sprite.wrap" => ApplyEnum("Sprite wrap", () => document.Usage.Sprite.Wrap,
                next => document.Usage.Sprite.Wrap = next, value),
            "usage.sprite.pixelsperunit" => Apply("Pixels per unit", () => document.Usage.Sprite.PixelsPerUnit,
                next => document.Usage.Sprite.PixelsPerUnit = next, Math.Clamp(Convert.ToDouble(value, CultureInfo.InvariantCulture), 0.01, 100_000)),
            "usage.sprite.generatemipmaps" => Apply("Generate mipmaps", () => document.Usage.Sprite.GenerateMipmaps,
                next => document.Usage.Sprite.GenerateMipmaps = next, Convert.ToBoolean(value, CultureInfo.InvariantCulture)),
            "usage.tileset.tilewidth" => Apply("Tile width", () => document.Usage.Tileset.TileWidth,
                next => document.Usage.Tileset.TileWidth = next, Math.Clamp(Convert.ToInt32(value, CultureInfo.InvariantCulture), 1, 16_384)),
            "usage.tileset.tileheight" => Apply("Tile height", () => document.Usage.Tileset.TileHeight,
                next => document.Usage.Tileset.TileHeight = next, Math.Clamp(Convert.ToInt32(value, CultureInfo.InvariantCulture), 1, 16_384)),
            "usage.tileset.margin" => Apply("Tile margin", () => document.Usage.Tileset.Margin,
                next => document.Usage.Tileset.Margin = next, Math.Clamp(Convert.ToInt32(value, CultureInfo.InvariantCulture), 0, 16_384)),
            "usage.tileset.spacing" => Apply("Tile spacing", () => document.Usage.Tileset.Spacing,
                next => document.Usage.Tileset.Spacing = next, Math.Clamp(Convert.ToInt32(value, CultureInfo.InvariantCulture), 0, 16_384)),
            "usage.background.mode" => ApplyEnum("Background mode", () => document.Usage.Background.Mode,
                next => document.Usage.Background.Mode = next, value),
            "usage.background.repeatx" => Apply("Repeat background X", () => document.Usage.Background.RepeatX,
                next => document.Usage.Background.RepeatX = next, Convert.ToBoolean(value, CultureInfo.InvariantCulture)),
            "usage.background.repeaty" => Apply("Repeat background Y", () => document.Usage.Background.RepeatY,
                next => document.Usage.Background.RepeatY = next, Convert.ToBoolean(value, CultureInfo.InvariantCulture)),
            "usage.background.parallaxx" => Apply("Background parallax X", () => document.Usage.Background.ParallaxX,
                next => document.Usage.Background.ParallaxX = next, Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            "usage.background.parallaxy" => Apply("Background parallax Y", () => document.Usage.Background.ParallaxY,
                next => document.Usage.Background.ParallaxY = next, Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            "usage.material.roughness" => Apply("Material roughness", () => document.Usage.Material.Roughness,
                next => document.Usage.Material.Roughness = next, Math.Clamp(Convert.ToDouble(value, CultureInfo.InvariantCulture), 0, 1)),
            "usage.material.metallic" => Apply("Material metallic", () => document.Usage.Material.Metallic,
                next => document.Usage.Material.Metallic = next, Math.Clamp(Convert.ToDouble(value, CultureInfo.InvariantCulture), 0, 1)),
            "usage.material.normalmap" => Apply("Material normal map", () => document.Usage.Material.NormalMap ?? string.Empty,
                next => document.Usage.Material.NormalMap = string.IsNullOrWhiteSpace(next) ? null : next, Text(value)),
            "usage.material.tilingx" => Apply("Material tiling X", () => document.Usage.Material.TilingX,
                next => document.Usage.Material.TilingX = next, Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            "usage.material.tilingy" => Apply("Material tiling Y", () => document.Usage.Material.TilingY,
                next => document.Usage.Material.TilingY = next, Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            "usage.material.flowx" => Apply("Material flow X", () => document.Usage.Material.FlowX,
                next => document.Usage.Material.FlowX = next, Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            "usage.material.flowy" => Apply("Material flow Y", () => document.Usage.Material.FlowY,
                next => document.Usage.Material.FlowY = next, Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            "origin.x" => Apply("Image origin X", () => document.Origin.X,
                next => document.Origin.X = next, Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            "origin.y" => Apply("Image origin Y", () => document.Origin.Y,
                next => document.Origin.Y = next, Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            "origin.space" => ApplyEnum("Image origin space", () => document.Origin.Space,
                next => document.Origin.Space = next, value),
            _ => TryApplyFrameOrLayer(path, value),
        };
        if (handled) _refresh();
        return handled;
    }

    private void AddUsage(List<ResourceInspectorLiveValue> values, ImageDocument document)
    {
        ImageUsage allowed = document.Usage.Allowed;
        values.Add(new("Image usage", "Usage.Sprite", "Sprite", allowed.HasFlag(ImageUsage.Sprite)));
        values.Add(new("Image usage", "Usage.Tileset", "Tile set", allowed.HasFlag(ImageUsage.Tileset)));
        values.Add(new("Image usage", "Usage.Background", "Background", allowed.HasFlag(ImageUsage.Background)));
        values.Add(new("Image usage", "Usage.Texture", "Model texture", allowed.HasFlag(ImageUsage.Texture)));
        values.Add(new("Image usage", "Usage.NineSlice", "Nine-slice UI", allowed.HasFlag(ImageUsage.NineSlice)));
    }

    private IReadOnlyList<string> TextureGroupChoices()
    {
        IReadOnlyList<string>? names = _listTextureGroups?.Invoke();
        if (names is { Count: > 0 })
            return names.ToArray();
        return [TextureGroupCatalog.DefaultName];
    }

    private bool ApplyTextureGroup(object? value)
    {
        string next = TextureGroupCatalog.NormalizeOrDefault(Text(value));
        IReadOnlyList<string> choices = TextureGroupChoices();
        if (!choices.Any(c => string.Equals(c, next, StringComparison.OrdinalIgnoreCase)))
            next = TextureGroupCatalog.DefaultName;
        return Apply(
            "Texture group",
            () => _session.Document.TextureGroup,
            assigned => _session.Document.TextureGroup = assigned,
            next);
    }

    private static void AddTileset(List<ResourceInspectorLiveValue> values, ImageTilesetSettings settings)
    {
        values.Add(Number("Tile set", "usage.tileset.tileWidth", "Tile width", settings.TileWidth, 1, 16_384, 1, 0));
        values.Add(Number("Tile set", "usage.tileset.tileHeight", "Tile height", settings.TileHeight, 1, 16_384, 1, 0));
        values.Add(Number("Tile set", "usage.tileset.margin", "Margin", settings.Margin, 0, 16_384, 1, 0));
        values.Add(Number("Tile set", "usage.tileset.spacing", "Spacing", settings.Spacing, 0, 16_384, 1, 0));
        values.Add(new("Tile set", "Summary.SolidTiles", "Solid tiles", settings.Collision.Count, ReadOnly: true));
    }

    private static void AddBackground(List<ResourceInspectorLiveValue> values, ImageBackgroundSettings settings)
    {
        values.Add(new("Background", "usage.background.mode", "Mode", settings.Mode.ToString(), Choices: Enum.GetNames<ImageBackgroundMode>()));
        values.Add(new("Background", "usage.background.repeatX", "Repeat X", settings.RepeatX));
        values.Add(new("Background", "usage.background.repeatY", "Repeat Y", settings.RepeatY));
        values.Add(Number("Background", "usage.background.parallaxX", "Parallax X", settings.ParallaxX, -100, 100, 0.01m, 3));
        values.Add(Number("Background", "usage.background.parallaxY", "Parallax Y", settings.ParallaxY, -100, 100, 0.01m, 3));
    }

    private static void AddMaterial(List<ResourceInspectorLiveValue> values, ImageMaterialSettings settings)
    {
        values.Add(Number("Material surface", "usage.material.roughness", "Roughness", settings.Roughness, 0, 1, 0.01m, 2));
        values.Add(Number("Material surface", "usage.material.metallic", "Metallic", settings.Metallic, 0, 1, 0.01m, 2));
        values.Add(new("Material surface", "usage.material.normalMap", "Normal map", settings.NormalMap ?? string.Empty, AssetKind: ResourceKind.Image));
        values.Add(Number("Material surface", "usage.material.tilingX", "Tiling X", settings.TilingX, -1000, 1000, 0.05m, 3));
        values.Add(Number("Material surface", "usage.material.tilingY", "Tiling Y", settings.TilingY, -1000, 1000, 0.05m, 3));
        values.Add(Number("Material surface", "usage.material.flowX", "Flow X", settings.FlowX, -1000, 1000, 0.01m, 3));
        values.Add(Number("Material surface", "usage.material.flowY", "Flow Y", settings.FlowY, -1000, 1000, 0.01m, 3));
    }

    private void AddActiveFrame(List<ResourceInspectorLiveValue> values, ImageDocument document)
    {
        int index = Math.Clamp(_workspace.SelectedFrameIndex, 0, Math.Max(0, _workspace.Frames.Count - 1));
        if (_workspace.Frames.Count == 0) return;
        ImageFrameBuffer frame = _workspace.Frames[index];
        string root = $"frames[{index}]";
        values.Add(new($"{frame.Name} · Frame", root + ".name", "Name", frame.Name));
        values.Add(Number($"{frame.Name} · Frame", root + ".durationMilliseconds", "Duration (ms)",
            frame.DurationMilliseconds, 1, 60_000, 1, 0));
        values.Add(new($"{frame.Name} · Frame", root + ".source", "Source",
            index < document.Frames.Count ? document.Frames[index].Source ?? string.Empty : string.Empty,
            ReadOnly: true));
    }

    private void AddActiveLayer(List<ResourceInspectorLiveValue> values)
    {
        ImageLayerBuffer? layer = _workspace.CurrentLayer;
        if (layer is null) return;
        int index = _workspace.SelectedLayerIndex;
        string root = $"layers[{index}]";
        string group = $"{layer.Name} · Layer";
        values.Add(new(group, root + ".name", "Name", layer.Name));
        values.Add(new(group, root + ".visible", "Visible", layer.Visible));
        values.Add(new(group, root + ".locked", "Locked", layer.Locked));
        values.Add(Number(group, root + ".opacity", "Opacity", layer.Opacity, 0, 1, 0.01m, 2));
        values.Add(new(group, root + ".blendMode", "Blend mode", layer.BlendMode.ToString(), Choices: Enum.GetNames<ImageBlendMode>()));
        values.Add(new(group, root + ".channel", "Material channel", layer.Channel.ToString(), Choices: Enum.GetNames<ImageMaterialChannel>()));
    }

    private bool TryApplyFrameOrLayer(string path, object? value)
    {
        Match frame = Regex.Match(path, @"^frames\[(?<index>\d+)\]\.(?<name>\w+)$", RegexOptions.IgnoreCase);
        if (frame.Success && int.TryParse(frame.Groups["index"].Value, out int frameIndex)
            && frameIndex >= 0 && frameIndex < _workspace.Frames.Count)
        {
            ImageFrameBuffer buffer = _workspace.Frames[frameIndex];
            ImageFrame? schema = frameIndex < _session.Document.Frames.Count ? _session.Document.Frames[frameIndex] : null;
            return frame.Groups["name"].Value.ToLowerInvariant() switch
            {
                "name" => ApplyFrame("Rename image frame", buffer, schema, name: Text(value), duration: null),
                "durationmilliseconds" => ApplyFrame("Change frame duration", buffer, schema, name: null,
                    duration: Math.Clamp(Convert.ToInt32(value, CultureInfo.InvariantCulture), 1, 60_000)),
                _ => false,
            };
        }

        Match layer = Regex.Match(path, @"^layers\[(?<index>\d+)\]\.(?<name>\w+)$", RegexOptions.IgnoreCase);
        if (!layer.Success || !int.TryParse(layer.Groups["index"].Value, out int layerIndex)) return false;
        return ApplyLayer(layerIndex, layer.Groups["name"].Value, value);
    }

    private bool ApplyUsage(ImageUsage flag, object? value)
    {
        bool enabled = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        ImageUsage current = _session.Document.Usage.Allowed;
        ImageUsage next = enabled ? current | flag : current & ~flag;
        return Apply("Change image usage", () => _session.Document.Usage.Allowed,
            changed => _session.Document.Usage.Allowed = changed, next);
    }

    private bool ApplyFrame(string description, ImageFrameBuffer buffer, ImageFrame? schema,
        string? name, int? duration)
    {
        string beforeName = buffer.Name;
        int beforeDuration = buffer.DurationMilliseconds;
        string afterName = name ?? beforeName;
        int afterDuration = duration ?? beforeDuration;
        if (beforeName == afterName && beforeDuration == afterDuration) return true;
        void Write(string nextName, int nextDuration)
        {
            buffer.Name = nextName;
            buffer.DurationMilliseconds = nextDuration;
            if (schema is not null)
            {
                schema.Name = nextName;
                schema.DurationMilliseconds = nextDuration;
            }
            _workspace.Touch();
        }
        _session.Execute(new StructuralImageCommand(description,
            _ => Write(afterName, afterDuration), _ => Write(beforeName, beforeDuration)));
        return true;
    }

    private bool ApplyLayer(int index, string property, object? value)
    {
        string normalizedProperty = property.ToLowerInvariant();
        if (normalizedProperty is not ("name" or "visible" or "locked" or "opacity" or "blendmode" or "channel"))
        {
            return false;
        }

        List<ImageLayerBuffer> layers = _workspace.Frames
            .Where(frame => index >= 0 && index < frame.Layers.Count)
            .Select(frame => frame.Layers[index])
            .ToList();
        if (layers.Count == 0) return false;
        ImageLayerBuffer primary = layers[0];
        ImageLayer? schema = index < _session.Document.Layers.Count ? _session.Document.Layers[index] : null;
        object before = normalizedProperty switch
        {
            "name" => primary.Name,
            "visible" => primary.Visible,
            "locked" => primary.Locked,
            "opacity" => primary.Opacity,
            "blendmode" => primary.BlendMode,
            "channel" => primary.Channel,
            _ => string.Empty,
        };
        object after = normalizedProperty switch
        {
            "name" => Text(value),
            "visible" => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            "locked" => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            "opacity" => Math.Clamp(Convert.ToSingle(value, CultureInfo.InvariantCulture), 0f, 1f),
            "blendmode" when Enum.TryParse(Text(value), true, out ImageBlendMode blend) => blend,
            "channel" when Enum.TryParse(Text(value), true, out ImageMaterialChannel channel) => channel,
            _ => before,
        };
        if (Equals(before, after)) return true;
        void Write(object changed)
        {
            foreach (ImageLayerBuffer target in layers)
            {
                switch (normalizedProperty)
                {
                    case "name": target.Name = (string)changed; break;
                    case "visible": target.Visible = (bool)changed; break;
                    case "locked": target.Locked = (bool)changed; break;
                    case "opacity": target.Opacity = (float)changed; break;
                    case "blendmode": target.BlendMode = (ImageBlendMode)changed; break;
                    case "channel": target.Channel = (ImageMaterialChannel)changed; break;
                }
            }
            if (schema is not null)
            {
                schema.Name = primary.Name;
                schema.Visible = primary.Visible;
                schema.Locked = primary.Locked;
                schema.Opacity = primary.Opacity;
            }
            _workspace.Touch();
        }
        _session.Execute(new StructuralImageCommand("Edit image layer", _ => Write(after), _ => Write(before)));
        return true;
    }

    private bool ApplyEnum<T>(string description, Func<T> read, Action<T> write, object? value)
        where T : struct, Enum =>
        Enum.TryParse(Text(value), true, out T parsed) && Apply(description, read, write, parsed);

    private bool Apply<T>(string description, Func<T> read, Action<T> write, T value)
    {
        T previous = read();
        if (EqualityComparer<T>.Default.Equals(previous, value)) return true;
        void Set(T changed)
        {
            write(changed);
            _workspace.Touch();
        }
        _session.Execute(new StructuralImageCommand(description, _ => Set(value), _ => Set(previous)));
        return true;
    }

    private static ResourceInspectorLiveValue Number(
        string group, string path, string label, object value,
        decimal minimum, decimal maximum, decimal increment, int places) =>
        new(group, path, label, value, Minimum: minimum, Maximum: maximum,
            Increment: increment, DecimalPlaces: places);

    private static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
}
