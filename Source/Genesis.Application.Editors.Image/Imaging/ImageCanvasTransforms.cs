using Genesis.Application.Core.Images;

namespace Genesis.Application.Editors.Image.Imaging;

public static class ImageCanvasTransforms
{
    public static Rectangle OpaqueBounds(ImageWorkspace workspace,IReadOnlyList<int>? frames=null)
    {
        int left = workspace.Width, top = workspace.Height, right = -1, bottom = -1;
        foreach (var layer in (frames ?? Enumerable.Range(0,workspace.Frames.Count).ToArray()).SelectMany(i=>workspace.Frames[i].Layers))
            for (int y = 0; y < workspace.Height; y++) for (int x = 0; x < workspace.Width; x++)
                if (layer.Pixels[(y * workspace.Width + x) * 4 + 3] != 0)
                { left = Math.Min(left,x); top = Math.Min(top,y); right = Math.Max(right,x); bottom = Math.Max(bottom,y); }
        return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left,top,right+1,bottom+1);
    }

    public static Size RotatedSize(int width, int height, double degrees, bool expand)
    {
        if (!double.IsFinite(degrees)) throw new ArgumentOutOfRangeException(nameof(degrees));
        if (!expand) return new Size(width,height);
        double angle = degrees*Math.PI/180, c = Math.Abs(Math.Cos(angle)), s = Math.Abs(Math.Sin(angle));
        return new Size((int)Math.Ceiling(width*c+height*s-1e-8),(int)Math.Ceiling(width*s+height*c-1e-8));
    }

    public static PointF RotatePoint(PointF point, int width, int height, Size target, double degrees)
    {
        double angle = degrees*Math.PI/180, c = Math.Cos(angle), s = Math.Sin(angle);
        double x = point.X-width/2d, y = point.Y-height/2d;
        return new PointF((float)(x*c-y*s+target.Width/2d),(float)(x*s+y*c+target.Height/2d));
    }

    public static byte[] Rotate(byte[] source, int width, int height, double degrees, bool expand)
    {
        Size target = RotatedSize(width,height,degrees,expand);
        return Remap(source,width,height,target,p => RotatePoint(p,target.Width,target.Height,new Size(width,height),-degrees));
    }

    public static (byte[] Pixels, Size Size) RotatePreview(byte[] source, int width, int height, double degrees, bool expand)
    {
        Size target = RotatedSize(width,height,degrees,expand), preview = PreviewSize(target);
        return (Remap(source,width,height,preview,p => RotatePoint(new PointF(p.X*target.Width/preview.Width,p.Y*target.Height/preview.Height),target.Width,target.Height,new Size(width,height),-degrees)),preview);
    }

    private static Size PreviewSize(Size target)
    {
        float scale = Math.Min(1,1024f/Math.Max(target.Width,target.Height));
        return new Size(Math.Max(1,(int)Math.Round(target.Width*scale)),Math.Max(1,(int)Math.Round(target.Height*scale)));
    }

    public static byte[] Remap(byte[] source, int width, int height, Size target, Func<PointF,PointF> inverse)
    {
        ValidateSize(target.Width,target.Height);
        if (source.Length != checked(width*height*4)) throw new ArgumentException("Pixel buffer dimensions do not match.",nameof(source));
        byte[] result = new byte[checked(target.Width*target.Height*4)];
        for (int y = 0; y < target.Height; y++) for (int x = 0; x < target.Width; x++)
        {
            PointF original = inverse(new PointF(x+.5f,y+.5f));
            int ox = (int)Math.Floor(original.X), oy = (int)Math.Floor(original.Y);
            if (ox >= 0 && oy >= 0 && ox < width && oy < height)
                Buffer.BlockCopy(source,(oy*width+ox)*4,result,(y*target.Width+x)*4,4);
        }
        return result;
    }

    public static void ValidateSize(int width, int height)
    {
        if (width < 1 || height < 1 || width > 8192 || height > 8192)
            throw new ArgumentOutOfRangeException(nameof(width),"Canvas dimensions must be between 1 and 8192 pixels.");
    }

    public static void ValidateSlices(ImageNineSlice slices, int width, int height)
    {
        if (slices.Left < 0 || slices.Top < 0 || slices.Right < 0 || slices.Bottom < 0 ||
            (long)slices.Left+slices.Right >= width || (long)slices.Top+slices.Bottom >= height ||
            !Enum.IsDefined(slices.HorizontalEdges) || !Enum.IsDefined(slices.VerticalEdges) || !Enum.IsDefined(slices.Centre))
            throw new ArgumentException("Guides must leave at least one pixel in the centre.",nameof(slices));
    }

    public static byte[] NineSlice(byte[] source, int width, int height, Size target, ImageNineSlice slices) => RenderNineSlice(source,width,height,target,target,slices);

    public static (byte[] Pixels, Size Size) NineSlicePreview(byte[] source, int width, int height, Size target, ImageNineSlice slices)
    {
        Size preview = PreviewSize(target); return (RenderNineSlice(source,width,height,target,preview,slices),preview);
    }

    private static byte[] RenderNineSlice(byte[] source, int width, int height, Size target, Size output, ImageNineSlice slices)
    {
        ValidateSize(target.Width,target.Height); ValidateSlices(slices,width,height);
        if (source.Length != checked(width*height*4)) throw new ArgumentException("Pixel buffer dimensions do not match.",nameof(source));
        if (target.Width <= slices.Left+slices.Right || target.Height <= slices.Top+slices.Bottom)
            throw new ArgumentException("Output must leave at least one pixel between the fixed borders.",nameof(target));
        byte[] result = new byte[checked(output.Width*output.Height*4)];
        for (int py = 0; py < output.Height; py++) for (int px = 0; px < output.Width; px++)
        {
            int x = (int)((px+.5)*target.Width/output.Width), y = (int)((py+.5)*target.Height/output.Height);
            bool mx = x >= slices.Left && x < target.Width-slices.Right;
            bool my = y >= slices.Top && y < target.Height-slices.Bottom;
            ImageSliceMode mode = mx && my ? slices.Centre : mx ? slices.HorizontalEdges : my ? slices.VerticalEdges : ImageSliceMode.Stretch;
            if (mode == ImageSliceMode.Transparent && (mx || my)) continue;
            int ox = SampleAxis(x,width,target.Width,slices.Left,slices.Right,mode);
            int oy = SampleAxis(y,height,target.Height,slices.Top,slices.Bottom,mode);
            Buffer.BlockCopy(source,(oy*width+ox)*4,result,(py*output.Width+px)*4,4);
        }
        return result;
    }

    private static int SampleAxis(int value, int source, int target, int first, int last, ImageSliceMode mode)
    {
        if (value < first) return value;
        if (value >= target-last) return source-(target-value);
        int length = source-first-last, offset = value-first;
        return first + (mode switch {
            ImageSliceMode.Repeat => offset%length,
            ImageSliceMode.Mirror => offset/length%2 == 0 ? offset%length : length-1-offset%length,
            _ => (int)((long)offset*length/(target-first-last)) });
    }

    public static float MapSliceCoordinate(float value, int source, int target, int first, int last) => value < first
        ? value : value > source-last ? target-(source-value) : first+(value-first)*(target-first-last)/(source-first-last);
}
