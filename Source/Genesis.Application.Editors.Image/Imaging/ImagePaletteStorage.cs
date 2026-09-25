using System.Globalization;

namespace Genesis.Application.Editors.Image.Imaging;

public static class ImagePaletteStorage
{
    public static IReadOnlyList<Color> Extract(IEnumerable<byte[]> frames, int maximumColours = 256, CancellationToken cancellationToken = default)
    {
        if (maximumColours is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maximumColours));
        var counts = new Dictionary<uint,long>();
        foreach (var pixels in frames)
        {
            if (pixels.Length % 4 != 0) throw new ArgumentException("Palette extraction needs complete RGBA pixels.");
            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                if ((offset & 16383) == 0) cancellationToken.ThrowIfCancellationRequested();
                uint rgba = pixels[offset+3] == 0 ? 0 :
                    ((uint)pixels[offset] << 24) | ((uint)pixels[offset+1] << 16) | ((uint)pixels[offset+2] << 8) | pixels[offset+3];
                counts.TryGetValue(rgba,out long count);
                counts[rgba] = count + 1;
                if (counts.Count > 1_048_576)
                    throw new InvalidOperationException("This image has over a million distinct colours. Extract from a smaller image or reduce its colour depth first.");
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (counts.Count == 0) return [Color.FromArgb(0,0,0,0)];
        return counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).Take(maximumColours)
            .Select(pair => Color.FromArgb((int)(pair.Key & 255),(int)(pair.Key >> 24),(int)((pair.Key >> 16) & 255),(int)((pair.Key >> 8) & 255))).ToArray();
    }

    public static string Hex(Color colour) => $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}{colour.A:X2}";
    public static bool TryColour(string text,out Color colour)
    {
        colour=Color.Transparent;string hex=text.Trim().TrimStart('#');
        if(hex.Length is not (6 or 8)||!uint.TryParse(hex,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out uint value))return false;
        colour=hex.Length==6?Color.FromArgb(255,(int)(value>>16),(int)(value>>8&255),(int)(value&255)):
            Color.FromArgb((int)(value&255),(int)(value>>24),(int)(value>>16&255),(int)(value>>8&255));return true;
    }
    public static IReadOnlyList<Color> Read(string text)
    {
        string[] lines=text.Replace("\r","").Split('\n');bool jasc=lines.FirstOrDefault()?.Trim()=="JASC-PAL";
        bool gpl=lines.FirstOrDefault()?.Trim()=="GIMP Palette";List<Color> colours=[];
        foreach(string raw in lines.Skip(jasc?3:gpl?1:0))
        {
            string line=raw.Trim();if(line.Length==0)continue;
            if(TryColour(line,out var colour)){colours.Add(colour);continue;}
            if(line.StartsWith('#')||line.StartsWith("Name:",StringComparison.Ordinal)||line.StartsWith("Columns:",StringComparison.Ordinal))continue;
            string[] parts=line.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
            if(parts.Length>=3&&byte.TryParse(parts[0],out byte r)&&byte.TryParse(parts[1],out byte g)&&byte.TryParse(parts[2],out byte b))colours.Add(Color.FromArgb(r,g,b));
            else throw new InvalidDataException("Unsupported palette row: "+line);
        }
        if(colours.Count is <1 or >256)throw new InvalidDataException("A palette must contain 1–256 colours.");
        if(jasc&&(lines.Length<3||!int.TryParse(lines[2],out int expected)||expected!=colours.Count))throw new InvalidDataException("The JASC palette count does not match its colours.");
        return colours;
    }
    public static string Write(IReadOnlyList<Color> colours,bool gpl=false)
    {
        if(gpl&&colours.Any(c=>c.A!=255))throw new InvalidDataException("GIMP palettes do not store alpha. Export as RGBA hex to preserve transparency.");
        return gpl ? "GIMP Palette\nName: Genesis palette\nColumns: 8\n#\n"+string.Join("\n",colours.Select(c=>$"{c.R} {c.G} {c.B}")) : string.Join("\n",colours.Select(Hex));
    }
}
