using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Tesseract;
using ImageFormat = System.Drawing.Imaging.ImageFormat;

namespace Genesis.Application.Studio.Assistant;

/// <summary>One line of text found in a picture, with where it is (pixels of the picture) and how sure the engine is.</summary>
public sealed record OcrLine(string Text, double Confidence, int X, int Y, int Width, int Height);

/// <summary>What OCR read from a picture.</summary>
public sealed record OcrResult(bool Available, string Text, IReadOnlyList<OcrLine> Lines, double Confidence, string Error)
{
    /// <summary>The result in words for a model: the text, then each line with its position.</summary>
    public string Describe(string? query = null)
    {
        if (!Available) return "Text could not be read: " + Error;
        if (Lines.Count == 0) return "No text was found.";
        StringBuilder text = new($"Text read by Genesis OCR (mean confidence {Confidence:P0}):\n");
        foreach (OcrLine line in Lines.Take(40))
            text.AppendLine($"- \"{line.Text}\" at x {line.X}, y {line.Y} ({line.Width} x {line.Height}), confidence {line.Confidence:P0}");
        if (!string.IsNullOrWhiteSpace(query))
        {
            OcrLine? match = Lines.FirstOrDefault(line => line.Text.Contains(query, StringComparison.OrdinalIgnoreCase));
            text.AppendLine(match is null ? $"\"{query}\" was not found." : $"\"{query}\" is at x {match.X}, y {match.Y}.");
        }
        return text.ToString();
    }
}

/// <summary>
/// Genesis Nova OCR: Tesseract's English LSTM model (the engine and data Nova uses) reading a picture
/// for models that cannot see. One read at a time; the engine is made for each read and freed after.
/// </summary>
public static class GenesisOcr
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string DataFolder => Path.Combine(AppContext.BaseDirectory, "tessdata");

    public static async Task<OcrResult> ReadAsync(Bitmap picture, Rectangle? region = null, bool block = false, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Read(picture, region, block), ct).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Reads <paramref name="picture"/> (or <paramref name="region"/> of it). Small pictures are enlarged first: Tesseract reads text about 20 pixels tall best.</summary>
    public static OcrResult Read(Bitmap picture, Rectangle? region = null, bool block = false)
    {
        Rectangle area = region ?? new Rectangle(0, 0, picture.Width, picture.Height);
        area.Intersect(new Rectangle(0, 0, picture.Width, picture.Height));
        if (area.Width <= 0 || area.Height <= 0) return new OcrResult(false, "", [], 0, "The region is outside the picture.");
        string data = DataFolder;
        if (!File.Exists(Path.Combine(data, "eng.traineddata")))
            return new OcrResult(false, "", [], 0, "The English OCR data (tessdata\\eng.traineddata) is missing beside Studio.");
        // Sprites and small text are scaled up; very large screenshots are scaled down to about 4 megapixels.
        double scale = Math.Clamp(Math.Sqrt(4_000_000d / ((double)area.Width * area.Height)), 1, area.Height < 200 ? 4 : 1);
        if (area.Width * area.Height > 4_000_000) scale = Math.Sqrt(4_000_000d / ((double)area.Width * area.Height));
        using Bitmap crop = new(Math.Max(1, (int)(area.Width * scale)), Math.Max(1, (int)(area.Height * scale)), PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(crop))
        {
            // Transparent sprite pixels become white, so dark text on them reads.
            g.Clear(Color.White);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(picture, new Rectangle(0, 0, crop.Width, crop.Height), area, GraphicsUnit.Pixel);
        }
        try
        {
            using MemoryStream png = new();
            crop.Save(png, ImageFormat.Png);
            using Pix pix = Pix.LoadFromMemory(png.ToArray());
            using TesseractEngine engine = new(data, "eng", EngineMode.LstmOnly);
            using Page page = engine.Process(pix, block ? PageSegMode.SingleBlock : PageSegMode.SparseText);
            string text = page.GetText() ?? string.Empty;
            List<OcrLine> lines = [];
            using ResultIterator iterator = page.GetIterator();
            iterator.Begin();
            do
            {
                string? value = iterator.GetText(PageIteratorLevel.TextLine)?.Trim();
                if (string.IsNullOrWhiteSpace(value) || !iterator.TryGetBoundingBox(PageIteratorLevel.TextLine, out Rect box)) continue;
                lines.Add(new OcrLine(value, iterator.GetConfidence(PageIteratorLevel.TextLine) / 100.0,
                    area.X + (int)(box.X1 / scale), area.Y + (int)(box.Y1 / scale), (int)(box.Width / scale), (int)(box.Height / scale)));
            } while (iterator.Next(PageIteratorLevel.TextLine) && lines.Count < 200);
            return new OcrResult(true, text.Trim(), lines, page.GetMeanConfidence(), string.Empty);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new OcrResult(false, "", [], 0, error.Message
                + " (If native parts are missing, install Microsoft's Visual C++ 2015-2022 x64 runtime.)");
        }
    }
}

/// <summary>Pictures of what the assistant is asked to look at: Studio's window, the open editor, a region, or an image resource.</summary>
public static class StudioCapture
{
    /// <summary>A screenshot of a part of the screen (it includes 3D viewports, which a control cannot draw to a bitmap).</summary>
    public static Bitmap Screen(Rectangle bounds)
    {
        bounds.Intersect(SystemInformation.VirtualScreen);
        Bitmap shot = new(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height), PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(shot);
        g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        return shot;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr hdc, uint flags);

    private const uint RenderFullContent = 2;

    /// <summary>
    /// A window's own pixels, even when another window covers it (PrintWindow with full content, which
    /// includes DirectX viewports); a screen grab when that fails.
    /// </summary>
    public static Bitmap Window(Form form)
    {
        Rectangle bounds = form.Bounds;
        if (form.IsHandleCreated && form.WindowState != FormWindowState.Minimized && bounds.Width > 0 && bounds.Height > 0)
        {
            // 24-bit, so GDI drawing (which leaves alpha at 0) does not read as transparent.
            Bitmap shot = new(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            bool drawn;
            using (Graphics g = Graphics.FromImage(shot))
            {
                IntPtr hdc = g.GetHdc();
                try { drawn = PrintWindow(form.Handle, hdc, RenderFullContent); }
                finally { g.ReleaseHdc(hdc); }
            }
            if (drawn) return shot;
            shot.Dispose();
        }
        return Screen(bounds);
    }

    /// <summary><paramref name="area"/> of <paramref name="control"/> (in its client pixels), cut from its window's picture.</summary>
    public static Bitmap Region(Control control, Rectangle area)
    {
        Rectangle onScreen = control.RectangleToScreen(area);
        Form? form = control as Form ?? control.FindForm();
        if (form is null) return Screen(onScreen);
        using Bitmap window = Window(form);
        Rectangle cut = onScreen;
        cut.Offset(-form.Bounds.X, -form.Bounds.Y);
        cut.Intersect(new Rectangle(Point.Empty, window.Size));
        return cut.Width > 0 && cut.Height > 0 ? window.Clone(cut, window.PixelFormat) : Screen(onScreen);
    }

    public static Bitmap Control(Control control) => Region(control, control.ClientRectangle);

    /// <summary>The first frame of an image resource (a .image.json with frames beside it), or a picture file.</summary>
    public static Bitmap? Image(string path)
    {
        if (path.EndsWith(".image.json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("frames", out JsonElement frames) && frames.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement frame in frames.EnumerateArray())
                        if (frame.TryGetProperty("source", out JsonElement source) && source.GetString() is { Length: > 0 } relative)
                        {
                            string file = Path.Combine(Path.GetDirectoryName(path)!, relative.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(file)) return Load(file);
                        }
            }
            catch (Exception error) when (error is IOException or JsonException) { }
            string data = path[..^".image.json".Length] + ".spritedata";
            string? first = Directory.Exists(data) ? Directory.EnumerateFiles(data, "*.png", SearchOption.AllDirectories).FirstOrDefault() : null;
            return first is null ? null : Load(first);
        }
        return File.Exists(path) ? Load(path) : null;
    }

    private static Bitmap Load(string file)
    {
        using FileStream stream = File.OpenRead(file);
        using System.Drawing.Image image = System.Drawing.Image.FromStream(stream);
        return new Bitmap(image);
    }
}
