#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Genesis.Shared.Assets;

namespace Genesis.Runtime.Imaging;

/// <summary>
/// Static colour layers from the bound source frame surround the deforming layer in authored
/// order. Ordinary frame animation on other layers is not driven by rig keyframes.
/// </summary>
public sealed class PixelRigLayerStack
{
    private sealed class Layer
    {
        public Layer() { }
        public string Id { get; set; } = "";
        public string? ParentId { get; set; }
        public LayerKind Kind { get; set; }
        public bool Visible { get; set; } = true;
        public float Opacity { get; set; } = 1;
        public PixelBlendMode BlendMode { get; set; }
        public List<Cel> Cels { get; set; } = [];
    }
    private enum LayerKind { Raster, Group, Reference }
    private sealed class Cel
    {
        public Cel() { }
        public string FrameId { get; set; } = "";
        public string? Source { get; set; }
        public float Opacity { get; set; } = 1;
        public PixelRigPoint Offset { get; set; } = new();
    }
    private sealed record Entry(byte[]? Pixels, float Opacity, PixelBlendMode Blend);
    private readonly List<Entry> _layers = [];
    private readonly byte[] _canvas;
    private long _revision = -1;
    private readonly PixelRigPlayer _player;
    private static readonly JsonSerializerOptions Options = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new() { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
    private PixelRigLayerStack(PixelRigPlayer player) { _player = player; _canvas = new byte[player.Width * player.Height * 4]; }

    public static PixelRigLayerStack? Load(JsonElement imageDocument, string descriptor, PixelRigPlayer player)
    {
        if (!Property(imageDocument,"layers",out JsonElement raw) || raw.ValueKind != JsonValueKind.Array || raw.GetArrayLength()==0) return null;
        if (raw.GetArrayLength()>256) throw new InvalidDataException("A live rig image supports up to 256 colour layers.");
        List<Layer> layers = raw.Deserialize<List<Layer>>(Options) ?? [];
        if (layers.Any(layer => layer is null || string.IsNullOrEmpty(layer.Id)) || layers.Select(l=>l.Id).Distinct().Count()!=layers.Count)
            throw new InvalidDataException("Image layers need unique IDs.");
        if (!layers.Any(layer=>layer.Id==player.LayerId)) throw new InvalidDataException("The rig's bound layer was deleted. Rebind it in the Image Editor.");
        HashSet<string> nonColour = [];
        if (Property(imageDocument,"materialChannels",out JsonElement channels) && channels.ValueKind==JsonValueKind.Array)
            foreach (JsonElement channel in channels.EnumerateArray())
                if (Property(channel,"layerId",out JsonElement id) && id.ValueKind==JsonValueKind.String
                    && Property(channel,"kind",out JsonElement kind) && kind.ToString() is not ("Albedo" or "0")) nonColour.Add(id.GetString()!);
        PixelRigLayerStack stack = new(player);
        long budget = stack._canvas.Length;
        foreach (Layer layer in layers)
        {
            if (layer.Cels is null || layer.Cels.Any(cel => cel is null))
                throw new InvalidDataException("Image layer cels cannot be null.");
            bool visible = layer.Visible;
            string? parent = layer.ParentId;
            HashSet<string> seen = new(StringComparer.Ordinal) { layer.Id };
            while (!string.IsNullOrEmpty(parent))
            {
                if (!seen.Add(parent)) throw new InvalidDataException("Image layer hierarchy contains a cycle.");
                Layer? ancestor = layers.FirstOrDefault(l=>l.Id==parent);
                if (ancestor is null) break;
                visible &= ancestor.Visible; parent = ancestor.ParentId;
            }
            if (!visible || layer.Kind != LayerKind.Raster || nonColour.Contains(layer.Id)) continue;
            Cel? cel = layer.Cels?.FirstOrDefault(c=>c.FrameId==player.SourceFrameId);
            float opacity = layer.Opacity * (cel?.Opacity ?? 1);
            if (!float.IsFinite(opacity) || !Enum.IsDefined(layer.BlendMode)) throw new InvalidDataException("Invalid image-layer blend or opacity.");
            if (opacity<=0) continue;
            if (layer.Id==player.LayerId)
            { stack._layers.Add(new Entry(null,opacity,layer.BlendMode)); continue; }
            if (string.IsNullOrWhiteSpace(cel?.Source)) continue; // An empty cel is transparent, not a flattened duplicate.
            string source = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(descriptor)!, cel.Source.Replace('/',Path.DirectorySeparatorChar)));
            if (!File.Exists(source)) throw new FileNotFoundException("A rig image layer source is missing.",source);
            byte[] decoded = ImageAssetDecoder.DecodeToRgba(source,out int width,out int height);
            if (width<1 || height<1 || (long)width*height>4_194_304) throw new InvalidDataException("A rig layer exceeds the supported canvas limit.");
            budget += stack._canvas.Length;
            if (budget>128L*1024*1024) throw new InvalidDataException("Static rig layers exceed the 128 MiB per-instance budget.");
            byte[] canvas = new byte[stack._canvas.Length];
            double offsetX=cel.Offset?.X ?? 0, offsetY=cel.Offset?.Y ?? 0;
            if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY) || Math.Abs(offsetX)>1_000_000 || Math.Abs(offsetY)>1_000_000)
                throw new InvalidDataException("Image layer offsets must be finite.");
            int ox=(int)Math.Round(offsetX), oy=(int)Math.Round(offsetY);
            for (int y=Math.Max(0,-oy);y<Math.Min(height,player.Height-oy);y++)
            {
                int sx=Math.Max(0,-ox), count=Math.Min(width,player.Width-ox)-sx;
                if(count>0) Buffer.BlockCopy(decoded,(y*width+sx)*4,canvas,((y+oy)*player.Width+sx+ox)*4,count*4);
            }
            stack._layers.Add(new Entry(canvas,opacity,layer.BlendMode));
        }
        return stack;
    }

    public ReadOnlyMemory<byte> GetPixels()
    {
        if (_revision==_player.Revision) return _canvas;
        Array.Clear(_canvas);
        foreach (Entry layer in _layers)
        {
            ReadOnlySpan<byte> source = layer.Pixels is null ? _player.GetPixels().Span : layer.Pixels.AsSpan();
            PixelLayerCompositor.Composite(_canvas,source,layer.Opacity,layer.Blend);
        }
        _revision=_player.Revision;
        return _canvas;
    }

    private static bool Property(JsonElement value,string name,out JsonElement result)
    {
        if(value.ValueKind==JsonValueKind.Object)
            foreach(JsonProperty property in value.EnumerateObject())
                if(property.Name.Equals(name,StringComparison.OrdinalIgnoreCase)) {result=property.Value;return true;}
        result=default;return false;
    }
}
