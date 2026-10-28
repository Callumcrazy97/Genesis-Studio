using System.ComponentModel;
using System.Text.Json.Nodes;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Runtime.Scene;

namespace Genesis.Application.Core.Automation;

/// <summary>
/// The Room editor's tile painting as a command: a 2D room's floors, walls and paths painted from a
/// Tileset image on a tile layer, as the editor's tile brush paints them (one layer per tile set,
/// cells in grid units, autotiled sets choosing each tile from its neighbours). Without it a 2D level
/// could only be made of Objects, or written into the room file by hand.
/// </summary>
public static class RoomTileCommands
{
    public static IEnumerable<IAutomationCommand> All() => [new RoomPaintTiles()];

    public sealed class TileRect
    {
        [Description("Left cell."), RequiredInput] public int X { get; set; }
        [Description("Top cell."), RequiredInput] public int Y { get; set; }
        [Description("Cells across (default 1).")] public int? Width { get; set; }
        [Description("Cells down (default 1).")] public int? Height { get; set; }
        [Description("The tile to fill it with, by number (default the call's tile).")] public int? Tile { get; set; }
    }

    public sealed class PaintTilesInput
    {
        [Description("The 2D Room resource, project-relative."), RequiredInput] public string Room { get; set; } = string.Empty;
        [Description("The Tileset Image resource, project-relative (image.create with \"usage\": \"Tileset\")."), RequiredInput] public string Tileset { get; set; } = string.Empty;
        [Description("The tile layer's name (default the tile set's name and \" Tiles\"); made if the room has none of that name.")] public string? Layer { get; set; }
        [Description("The tile painted where a cell or rectangle names none, by number: left to right, top to bottom from 0 (default 0). Ignored by an autotiled set.")]
        public int? Tile { get; set; }
        [Description("Cells to paint: [x, y] or [x, y, tile], in grid units (a cell is one tile; x right, y down).")]
        public List<int[]>? Cells { get; set; }
        [Description("Rectangles of cells to fill: floors, walls, platforms.")] public List<TileRect>? Rects { get; set; }
        [Description("Take the cells away instead of painting them.")] public bool? Erase { get; set; }
        [Description("Make the layer solid: its tiles that the tile set marks solid stop TileMoveX and TileMoveY.")] public bool? Solid { get; set; }
        [Description("The layer's depth: lower draws in front (default 0).")] public int? Depth { get; set; }
    }

    public sealed class RoomPaintTiles : AutomationCommand<PaintTilesInput>
    {
        public override string Name => "room.paintTiles";
        public override string Description => "Paints tiles in a 2D room from a Tileset image, as the Room editor's tile brush does: single cells and filled rectangles " +
            "in grid units, or takes them away; one tile layer per name, solid if asked (walls and floors for TileMoveX/TileMoveY). An autotiled set picks each tile from its neighbours.";

        protected override AutomationResult Execute(AutomationContext context, PaintTilesInput input)
        {
            string roomPath = context.Resolve(input.Room);
            if (!File.Exists(roomPath) || !roomPath.EndsWith(".room.json", StringComparison.OrdinalIgnoreCase))
                throw new AutomationException($"'{input.Room}' is not a Room resource (a .room.json file).");
            string tilesetPath = context.Resolve(input.Tileset);
            if (!File.Exists(tilesetPath) || !tilesetPath.EndsWith(".image.json", StringComparison.OrdinalIgnoreCase))
                throw new AutomationException($"'{input.Tileset}' is not an Image resource (a .image.json file).");
            ImageDocument image = ImageDocumentSerializer.LoadAtomic(tilesetPath).Document
                ?? throw new AutomationException($"'{input.Tileset}' could not be read.");
            if (!image.Usage.Supports(ImageUsage.Tileset))
                throw new AutomationException($"'{input.Tileset}' is not a tile set: make it with \"usage\": \"Tileset\" (or set its usage in the Image editor).");
            ImageTilesetSettings tiles = image.Usage.Tileset;
            if (tiles.TileWidth <= 0 || tiles.TileHeight <= 0) throw new AutomationException($"'{input.Tileset}' has no tile size.");

            RoomAsset room = RoomAssetLoader.Parse(roomPath);
            if (room.Dimension != RoomDimension.TwoD) throw new AutomationException("Tiles are painted in a 2D room.");
            string reference = ResourceNames.Name(context.Root, tilesetPath);
            string layerName = string.IsNullOrWhiteSpace(input.Layer) ? reference + " Tiles" : input.Layer.Trim();
            RoomNode? node = room.Nodes.FirstOrDefault(n => n.Kind == RoomNodeKind.TileLayer && n.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
            bool made = node is null;
            if (node is null)
            {
                node = new RoomNode
                {
                    Kind = RoomNodeKind.TileLayer,
                    Name = layerName,
                    LayerId = room.Layers.FirstOrDefault()?.Id ?? string.Empty,
                    EnabledIn3D = false,
                    TileLayer = new RoomTileLayerData(),
                };
                room.Nodes.Add(node);
            }
            RoomTileLayerData layer = node.TileLayer ??= new RoomTileLayerData();
            if (!string.IsNullOrEmpty(layer.Tileset) && !layer.Tileset.Equals(reference, StringComparison.OrdinalIgnoreCase) && layer.Cells.Count > 0)
                throw new AutomationException($"The layer '{layerName}' is painted from '{layer.Tileset}'; name another layer for '{reference}'.");
            layer.Tileset = reference;
            layer.CellWidth = tiles.TileWidth;
            layer.CellHeight = tiles.TileHeight;
            layer.Margin = Math.Max(0, tiles.Margin);
            layer.Separation = Math.Max(0, tiles.Spacing);
            if (input.Solid is bool solid) layer.CollisionEnabled = solid;
            if (input.Depth is int depth) layer.Depth = depth;

            // Columns as the editor's tile picker counts them, so tile numbers mean the same tiles.
            int columns = Math.Max(1, (image.Canvas.Width - layer.Margin + layer.Separation) / (tiles.TileWidth + layer.Separation));
            int defaultTile = Math.Max(0, input.Tile ?? 0);
            var wanted = new List<(int X, int Y, int Tile)>();
            foreach (int[] cell in input.Cells ?? [])
            {
                if (cell is null || cell.Length < 2) throw new AutomationException("A cell is [x, y] or [x, y, tile].");
                wanted.Add((cell[0], cell[1], cell.Length > 2 ? Math.Max(0, cell[2]) : defaultTile));
            }
            foreach (TileRect rect in input.Rects ?? [])
            {
                int width = rect.Width ?? 1, height = rect.Height ?? 1;
                if (width < 1 || height < 1 || (long)width * height > 1_000_000) throw new AutomationException("A rectangle is 1 to a million cells.");
                for (int y = rect.Y; y < rect.Y + height; y++)
                    for (int x = rect.X; x < rect.X + width; x++)
                        wanted.Add((x, y, Math.Max(0, rect.Tile ?? defaultTile)));
            }
            if (wanted.Count == 0) throw new AutomationException("Give cells or rects to paint.");

            var byCell = layer.Cells.GroupBy(c => (c.X, c.Y)).ToDictionary(g => g.Key, g => g.Last());
            int changed = 0;
            foreach ((int x, int y, int tile) in wanted)
            {
                if (input.Erase == true)
                {
                    if (byCell.Remove((x, y))) changed++;
                    continue;
                }
                byCell[(x, y)] = new RoomTileCell { X = x, Y = y, TileX = tile % columns, TileY = tile / columns };
                changed++;
            }
            layer.Cells = [.. byCell.Values.OrderBy(c => c.Y).ThenBy(c => c.X)];
            if (tiles.Autotile) TileAutotile.RefreshAll(layer, tiles.AutotileColumn, tiles.AutotileRow);

            ResourceBackupService.BackupBeforeOverwrite(roomPath);
            RoomAssetLoader.Save(room, roomPath);
            string verb = input.Erase == true ? "Took away" : "Painted";
            return AutomationResult.Success($"{verb} {changed} tiles on '{layerName}'{(made ? " (a new layer)" : string.Empty)}; it has {layer.Cells.Count}{(layer.CollisionEnabled ? ", solid" : string.Empty)}.",
                new JsonObject
                {
                    ["layer"] = layerName,
                    ["tileset"] = reference,
                    ["cellSize"] = new JsonArray(layer.CellWidth, layer.CellHeight),
                    ["changed"] = changed,
                    ["cells"] = layer.Cells.Count,
                    ["solid"] = layer.CollisionEnabled,
                    ["solidTiles"] = new JsonArray([.. tiles.Collision.Select(t => (JsonNode?)t)]),
                },
                context.Relative(roomPath));
        }
    }
}
