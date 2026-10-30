using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Text.Json.Nodes;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;

namespace Genesis.Application.Core.Automation;

/// <summary>
/// The Image editor's drawing as commands: an Image resource of a size, with frames, drawn on with
/// shapes, lines, text, fills and pixels, crisp for pixel art or smoothed. Frames are stored as the
/// editor and the templates store them (one PNG a frame beside the resource), so the Image editor
/// opens and the game draws what a command made. <c>image.info</c> gives each frame's picture, so
/// whoever drew it can look at the result.
/// </summary>
public static class ImageCommands
{
    public static IEnumerable<IAutomationCommand> All() => [new ImageCreate(), new ImageDraw(), new ImageInfo()];

    private const int MaximumSize = 4096;

    private static string ImageFile(AutomationContext context, string path)
    {
        string full = context.Resolve(path);
        if (!File.Exists(full) || !full.EndsWith(".image.json", StringComparison.OrdinalIgnoreCase))
            throw new AutomationException($"'{path}' is not an Image resource (a .image.json file).");
        return full;
    }

    private static string DataFolder(string imagePath)
    {
        string stem = imagePath[..^".image.json".Length];
        return stem + ".spritedata";
    }

    /// <summary>"#RGB", "#RRGGBB", "#RRGGBBAA" or a colour name (Red, CornflowerBlue, Transparent).</summary>
    public static Color ParseColor(string? text, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        string value = text.Trim();
        if (value.StartsWith('#'))
        {
            string hex = value[1..];
            if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
            if ((hex.Length == 6 || hex.Length == 8) && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint packed))
            {
                if (hex.Length == 6) packed = (packed << 8) | 0xFF;
                return Color.FromArgb((int)(packed & 0xFF), (int)(packed >> 24), (int)((packed >> 16) & 0xFF), (int)((packed >> 8) & 0xFF));
            }
            throw new AutomationException($"'{text}' is not a colour; write #RGB, #RRGGBB or #RRGGBBAA.");
        }
        Color named = Color.FromName(value);
        if (named.IsKnownColor || named.IsNamedColor && named.A > 0 || value.Equals("Transparent", StringComparison.OrdinalIgnoreCase)) return named;
        throw new AutomationException($"'{text}' is not a colour; write #RGB, #RRGGBB, #RRGGBBAA or a colour name.");
    }

    private static string FramePath(string imagePath, ImageDocument document, int index)
    {
        if (index < 0 || index >= document.Frames.Count)
            throw new AutomationException($"The image has {document.Frames.Count} frame(s); frame {index} does not exist.");
        string? source = document.Frames[index].Source;
        if (string.IsNullOrWhiteSpace(source)) throw new AutomationException($"Frame {index} has no picture of its own (it is layered); draw on it in the Image editor.");
        string full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(imagePath)!, source));
        if (!File.Exists(full)) throw new AutomationException($"Frame {index}'s picture '{source}' is missing.");
        return full;
    }

    // Through a FileStream, not GDI+'s own file open: GDI+ fails with "A generic error occurred" on a
    // path past 260 characters (a project deep inside a test output folder), while .NET's file
    // handling takes it. Image.FromFile would fail the same way, which is why loading reads bytes.
    private static void SavePng(Bitmap bitmap, string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        bitmap.Save(stream, ImageFormat.Png);
    }

    private static Bitmap LoadBitmap(string path)
    {
        // A copy, so the file is not held open while it is drawn on and saved over.
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        using var loaded = new Bitmap(stream);
        var bitmap = new Bitmap(loaded.Width, loaded.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.DrawImage(loaded, 0, 0, loaded.Width, loaded.Height);
        return bitmap;
    }

    public sealed class CreateInput
    {
        [Description("The image's name."), RequiredInput] public string Name { get; set; } = string.Empty;
        [Description("Folder to make it in, project-relative (default Assets/Sprites).")] public string? Folder { get; set; }
        [Description("Width in pixels (default 64; up to 4096).")] public int? Width { get; set; }
        [Description("Height in pixels (default 64; up to 4096).")] public int? Height { get; set; }
        [Description("Frames, for an animation (default 1).")] public int? Frames { get; set; }
        [Description("Milliseconds each frame shows (default 100).")] public int? FrameMilliseconds { get; set; }
        [Description("The colour every frame starts as (#RRGGBBAA or a name; default Transparent).")] public string? Background { get; set; }
        [Description("What it is for: Sprite (default), Background, Tileset, Texture.")] public ImageUsage? Usage { get; set; }
        [Description("A tile set's tile width, pixels (default 16).")] public int? TileWidth { get; set; }
        [Description("A tile set's tile height, pixels (default 16).")] public int? TileHeight { get; set; }
        [Description("A tile set's solid tiles, by number: left to right, top to bottom from 0. room.paintTiles with \"solid\": true makes them walls and floors for TileMoveX/TileMoveY.")]
        public List<int>? SolidTiles { get; set; }
        [Description("A tile set that picks each painted tile from its neighbours: the 16 tiles for every mix of neighbours (above 1, right 2, below 4, left 8) in a 4 by 4 block from autotileColumn, autotileRow.")]
        public bool? Autotile { get; set; }
        [Description("The autotile block's first column (default 0).")] public int? AutotileColumn { get; set; }
        [Description("The autotile block's first row (default 0).")] public int? AutotileRow { get; set; }
        [Description(AutomationContext.ReplaceDescription)] public bool? Replace { get; set; }
    }

    public sealed class ImageCreate : AutomationCommand<CreateInput>
    {
        public override string Name => "image.create";
        public override string Description => "Makes an Image resource of a size and number of frames, every frame filled with a colour, ready to draw on (image.draw) or open in the Image editor. " +
            "A name that is taken is refused, naming what has it, rather than made as \"Name (2)\"; replace: true moves the old Image to the trash and makes this one in its place.";

        protected override AutomationResult Execute(AutomationContext context, CreateInput input)
        {
            int width = input.Width ?? 64, height = input.Height ?? 64, frames = input.Frames ?? 1;
            if (width is < 1 or > MaximumSize || height is < 1 or > MaximumSize) throw new AutomationException($"An image is 1 to {MaximumSize} pixels each way.");
            if (frames is < 1 or > 256) throw new AutomationException("An image has 1 to 256 frames.");
            Color background = ParseColor(input.Background, Color.Transparent);

            // The settings are checked before the resource is made, so a refused call leaves nothing
            // behind and a corrected one can use the same name.
            ImageDocument document = ImageDocument.CreateDefault(width, height);
            document.Usage.Allowed = input.Usage ?? ImageUsage.Sprite;
            bool tileSettings = input.TileWidth is not null || input.TileHeight is not null || input.SolidTiles is not null || input.Autotile is not null;
            if (tileSettings && !document.Usage.Supports(ImageUsage.Tileset)) throw new AutomationException("Tile sizes, solid tiles and autotiling are for a Tileset image (\"usage\": \"Tileset\").");
            if (tileSettings)
            {
                ImageTilesetSettings tiles = document.Usage.Tileset;
                tiles.TileWidth = input.TileWidth ?? tiles.TileWidth;
                tiles.TileHeight = input.TileHeight ?? tiles.TileHeight;
                if (tiles.TileWidth is < 1 or > MaximumSize || tiles.TileHeight is < 1 or > MaximumSize) throw new AutomationException("A tile is at least a pixel each way.");
                tiles.Collision = [.. (input.SolidTiles ?? []).Where(index => index >= 0).Distinct().Order()];
                tiles.Autotile = input.Autotile ?? false;
                tiles.AutotileColumn = Math.Max(0, input.AutotileColumn ?? 0);
                tiles.AutotileRow = Math.Max(0, input.AutotileRow ?? 0);
            }
            string full = context.CreateResource(context.Resolve(input.Folder ?? "Assets/Sprites"), ResourceKind.Image, input.Name, input.Replace == true);
            string data = DataFolder(full);
            Directory.CreateDirectory(Path.Combine(data, "frames"));
            var layer = new ImageLayer { Name = "Layer 1" };
            document.Layers.Add(layer);
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.Clear(background);
                for (int i = 0; i < frames; i++)
                {
                    string id = Guid.NewGuid().ToString("N");
                    SavePng(bitmap, Path.Combine(data, "frames", id + ".png"));
                    document.Frames.Add(new ImageFrame
                    {
                        Id = id,
                        Name = frames > 1 ? $"Frame {i + 1}" : string.Empty,
                        DurationMilliseconds = Math.Clamp(input.FrameMilliseconds ?? 100, 10, 60000),
                        Source = Path.Combine(Path.GetFileName(data), "frames", id + ".png").Replace('\\', '/'),
                    });
                    layer.Cels.Add(new ImageCel { FrameId = id });
                }
            }
            ImageDocumentSerializer.SaveAtomic(full, document);
            return AutomationResult.Success($"Made {Path.GetFileName(full)}: {width} x {height}, {frames} frame(s).",
                new JsonObject { ["path"] = context.Relative(full), ["name"] = ResourceNames.Name(context.Root, full), ["width"] = width, ["height"] = height, ["frames"] = frames },
                context.Relative(full));
        }
    }

    public sealed class DrawOperation
    {
        [Description("rect, ellipse, line, polygon, text, fill (the whole frame), pixels or clear (a rectangle back to transparent)."), RequiredInput]
        public string Shape { get; set; } = string.Empty;
        [Description("Left, pixels.")] public float X { get; set; }
        [Description("Top, pixels.")] public float Y { get; set; }
        [Description("Width, pixels (rect, ellipse, clear).")] public float Width { get; set; }
        [Description("Height, pixels (rect, ellipse, clear).")] public float Height { get; set; }
        [Description("Points [[x, y], ...]: a line's ends (or a path), a polygon's corners, or the pixels to set.")] public List<float[]>? Points { get; set; }
        [Description("Colour (#RRGGBBAA or a name; default White).")] public string? Color { get; set; }
        [Description("Draw only the outline this many pixels wide (rect, ellipse, polygon); 0 fills (default). Lines are this wide (default 1).")] public float Thickness { get; set; }
        [Description("The words (text).")] public string? Text { get; set; }
        [Description("Font family (text; default Segoe UI).")] public string? Font { get; set; }
        [Description("Font size in pixels (text; default 12).")] public float? Size { get; set; }
        [Description("Bold text.")] public bool Bold { get; set; }
        [Description("Smooth edges (default false: crisp pixels for pixel art).")] public bool Antialias { get; set; }
    }

    public sealed class DrawInput
    {
        [Description("The Image resource, project-relative (Assets/Sprites/Name.image.json)."), RequiredInput] public string Image { get; set; } = string.Empty;
        [Description("The frame to draw on, from 0 (default 0); -1 draws on every frame.")] public int? Frame { get; set; }
        [Description("What to draw, in order."), RequiredInput] public List<DrawOperation> Operations { get; set; } = [];
    }

    public sealed class ImageDraw : AutomationCommand<DrawInput>
    {
        public override string Name => "image.draw";
        public override string Description => "Draws on an Image's frame: rectangles, ellipses, lines, polygons, text, fills and single pixels, crisp (pixel art) or smoothed. The image is backed up first.";

        protected override AutomationResult Execute(AutomationContext context, DrawInput input)
        {
            string full = ImageFile(context, input.Image);
            if (input.Operations.Count == 0) throw new AutomationException("Give at least one operation.");
            if (input.Operations.Count > 2000) throw new AutomationException("At most 2000 operations in one call.");
            ImageDocument document = ImageDocumentSerializer.LoadAtomic(full).Document;
            int frame = input.Frame ?? 0;
            int[] frames = frame == -1 ? [.. Enumerable.Range(0, document.Frames.Count)] : [frame];
            var changed = new List<string>();
            foreach (int index in frames)
            {
                string png = FramePath(full, document, index);
                using Bitmap bitmap = LoadBitmap(png);
                using (Graphics graphics = Graphics.FromImage(bitmap))
                    foreach (DrawOperation operation in input.Operations) Apply(graphics, bitmap, operation);
                ResourceBackupService.BackupBeforeOverwrite(png);
                SavePng(bitmap, png);
                changed.Add(context.Relative(png));
            }
            // The resource itself changes too, so Studio and the game notice the new pixels.
            File.SetLastWriteTimeUtc(full, DateTime.UtcNow);
            return AutomationResult.Success($"Drew {input.Operations.Count} operation(s) on {frames.Length} frame(s).",
                new JsonObject { ["frames"] = new JsonArray([.. changed.Select(c => (JsonNode?)c)]) }, [context.Relative(full), .. changed]);
        }

        private static void Apply(Graphics graphics, Bitmap bitmap, DrawOperation operation)
        {
            graphics.SmoothingMode = operation.Antialias ? SmoothingMode.AntiAlias : SmoothingMode.None;
            graphics.PixelOffsetMode = operation.Antialias ? PixelOffsetMode.HighQuality : PixelOffsetMode.Half;
            graphics.TextRenderingHint = operation.Antialias ? TextRenderingHint.AntiAliasGridFit : TextRenderingHint.SingleBitPerPixelGridFit;
            graphics.CompositingMode = CompositingMode.SourceOver;
            Color color = ParseColor(operation.Color, Color.White);
            using var brush = new SolidBrush(color);
            using var pen = new Pen(color, Math.Max(1, operation.Thickness));
            var rectangle = new RectangleF(operation.X, operation.Y, operation.Width, operation.Height);
            PointF[] points = [.. (operation.Points ?? []).Select(p => p is { Length: 2 } ? new PointF(p[0], p[1]) : throw new AutomationException("Each point is [x, y]."))];
            switch (operation.Shape.Trim().ToLowerInvariant())
            {
                case "rect":
                    if (operation.Thickness > 0) graphics.DrawRectangle(pen, rectangle.X, rectangle.Y, Math.Max(0, rectangle.Width - 1), Math.Max(0, rectangle.Height - 1));
                    else graphics.FillRectangle(brush, rectangle);
                    break;
                case "ellipse":
                    if (operation.Thickness > 0) graphics.DrawEllipse(pen, rectangle);
                    else graphics.FillEllipse(brush, rectangle);
                    break;
                case "line":
                    if (points.Length < 2) throw new AutomationException("A line needs at least two points.");
                    graphics.DrawLines(pen, points);
                    break;
                case "polygon":
                    if (points.Length < 3) throw new AutomationException("A polygon needs at least three points.");
                    if (operation.Thickness > 0) graphics.DrawPolygon(pen, points); else graphics.FillPolygon(brush, points);
                    break;
                case "text":
                    if (string.IsNullOrEmpty(operation.Text)) throw new AutomationException("Text needs its words.");
                    using (var font = new Font(operation.Font ?? "Segoe UI", Math.Max(1, operation.Size ?? 12), operation.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
                        graphics.DrawString(operation.Text, font, brush, operation.X, operation.Y);
                    break;
                case "fill":
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.FillRectangle(brush, 0, 0, bitmap.Width, bitmap.Height);
                    break;
                case "clear":
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    using (var clear = new SolidBrush(Color.Transparent)) graphics.FillRectangle(clear, rectangle);
                    break;
                case "pixels":
                    if (points.Length == 0) throw new AutomationException("Pixels needs the points to set.");
                    foreach (PointF point in points)
                    {
                        int x = (int)point.X, y = (int)point.Y;
                        if (x >= 0 && y >= 0 && x < bitmap.Width && y < bitmap.Height) bitmap.SetPixel(x, y, color);
                    }
                    break;
                default:
                    throw new AutomationException($"'{operation.Shape}' is not a shape: rect, ellipse, line, polygon, text, fill, pixels or clear.");
            }
        }
    }

    public sealed class InfoInput
    {
        [Description("The Image resource, project-relative."), RequiredInput] public string Image { get; set; } = string.Empty;
    }

    public sealed class ImageInfo : AutomationCommand<InfoInput>
    {
        public override string Name => "image.info";
        public override string Description => "Describes an Image: size, use, layers, and each frame with its duration and the full path of its picture (to look at what was drawn).";
        public override bool ChangesProject => false;

        protected override AutomationResult Execute(AutomationContext context, InfoInput input)
        {
            string full = ImageFile(context, input.Image);
            ImageDocument document = ImageDocumentSerializer.LoadAtomic(full).Document;
            var frames = new JsonArray();
            for (int i = 0; i < document.Frames.Count; i++)
            {
                ImageFrame frame = document.Frames[i];
                string? picture = string.IsNullOrWhiteSpace(frame.Source) ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(full)!, frame.Source));
                frames.Add(new JsonObject { ["index"] = i, ["name"] = frame.Name, ["milliseconds"] = frame.DurationMilliseconds, ["picture"] = picture });
            }
            return AutomationResult.Success($"{document.Canvas.Width} x {document.Canvas.Height}, {document.Frames.Count} frame(s), {document.Layers.Count} layer(s).",
                new JsonObject
                {
                    ["path"] = context.Relative(full),
                    ["width"] = document.Canvas.Width,
                    ["height"] = document.Canvas.Height,
                    ["usage"] = document.Usage.Allowed.ToString(),
                    ["layers"] = new JsonArray([.. document.Layers.Select(l => (JsonNode?)l.Name)]),
                    ["frames"] = frames,
                });
        }
    }
}
