using System.Text.Json;
using Genesis.Application.Core.Images;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>
/// Reads the tile-set half of an Image resource.
/// </summary>
/// <remarks>
/// T1 consolidation: this replaces <c>TileSetAssetLoader</c> and the standalone
/// <c>.tileset.json</c> resource. Tile geometry now lives in the Image document's
/// <see cref="ImageUsageProfile.Tileset"/> block, so a stone sheet can be a tile set *and* a model
/// texture without existing twice on disk. <see cref="Load"/> returns null for images that have not
/// been enabled for <see cref="ImageUsage.Tileset"/>, which is what keeps palettes honest.
/// </remarks>
public sealed record TileSetInfo(
    string ImagePath,
    int TileWidth,
    int TileHeight,
    int Margin,
    int Separation,
    IReadOnlyList<int> Collision)
{
    /// <summary>Tiles across a sheet of <paramref name="imageWidth"/> pixels.</summary>
    public int ColumnsFor(int imageWidth) =>
        TileWidth <= 0 ? 0 : Math.Max(0, (imageWidth - Margin + Separation) / (TileWidth + Separation));

    /// <summary>Tiles down a sheet of <paramref name="imageHeight"/> pixels.</summary>
    public int RowsFor(int imageHeight) =>
        TileHeight <= 0 ? 0 : Math.Max(0, (imageHeight - Margin + Separation) / (TileHeight + Separation));

    /// <summary>Source-image pixel rect of one tile index, given the sheet width.</summary>
    public Rectangle TileRect(int index, int imageWidth)
    {
        int columns = Math.Max(1, ColumnsFor(imageWidth));
        int column = index % columns;
        int row = index / columns;
        return new Rectangle(
            Margin + column * (TileWidth + Separation),
            Margin + row * (TileHeight + Separation),
            TileWidth,
            TileHeight);
    }

    public bool IsSolid(int tileIndex) => Collision.Contains(tileIndex);

    /// <summary>
    /// Load the tile-set view of an Image resource, or null when the file is unreadable or the
    /// image is not enabled for tile-set use.
    /// </summary>
    public static TileSetInfo? Load(string imageResourcePath)
    {
        if (string.IsNullOrWhiteSpace(imageResourcePath) || !File.Exists(imageResourcePath))
        {
            return null;
        }

        ImageDocument? document;
        try
        {
            document = ImageDocumentSerializer.Deserialize(File.ReadAllText(imageResourcePath)).Document;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }

        if (document is null || !document.Usage.Supports(ImageUsage.Tileset))
        {
            return null;
        }

        ImageTilesetSettings tiles = document.Usage.Tileset;
        if (tiles.TileWidth <= 0 || tiles.TileHeight <= 0)
        {
            return null;
        }

        return new TileSetInfo(
            imageResourcePath,
            tiles.TileWidth,
            tiles.TileHeight,
            Math.Max(0, tiles.Margin),
            Math.Max(0, tiles.Spacing),
            tiles.Collision ?? []);
    }
}
