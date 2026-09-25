namespace Genesis.Application.Core.Images;

/// <summary>Pixel margins and resize behaviour. Nine-slice is independent of the image's usage flags.</summary>
public sealed class ImageNineSlice
{
    public bool Enabled { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    public ImageSliceMode HorizontalEdges { get; set; }
    public ImageSliceMode VerticalEdges { get; set; }
    public ImageSliceMode Centre { get; set; }
    public ImageNineSlice Clone() => (ImageNineSlice)MemberwiseClone();
}

public enum ImageSliceMode { Stretch, Repeat, Mirror, Transparent }
