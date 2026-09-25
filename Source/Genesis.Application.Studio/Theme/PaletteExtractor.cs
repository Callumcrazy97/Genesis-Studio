using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Genesis.Application.Studio.Theme;

/// <summary>
/// Builds a complete <see cref="ThemePalette"/> out of an arbitrary image.
/// </summary>
/// <remarks>
/// This is what makes user-supplied backdrops workable. A hand-authored palette per image would
/// look better, but it only scales to images we ship — and the whole point is that someone can drop
/// their own artwork into the Themes folder. So the palette is derived, the way Windows picks an
/// accent colour from your wallpaper.
///
/// The derivation is deliberately opinionated rather than faithful. Chrome is not artwork: it has to
/// stay out of the way of the panels sitting on top of it, so the image's colours are used for
/// <i>hue</i> and the lightness is retargeted to values that work as an IDE surface. A photograph of
/// a beach yields a pale, sand-tinted chrome — not a beach.
///
/// Every colour it returns is then forced past a contrast floor (<see cref="ContrastRatio"/>).
/// Derivation that produces an unreadable interface is worse than no derivation at all, and an
/// arbitrary image absolutely will produce one if nothing checks.
/// </remarks>
public static class PaletteExtractor
{
    /// <summary>Side length the image is reduced to before sampling.</summary>
    /// <remarks>
    /// Sampling is a fixed 4096 pixels whatever the source, so a 6000px wallpaper costs the same as
    /// a thumbnail. The downscale itself averages neighbouring pixels, which is exactly the
    /// smoothing a histogram wants — single stray pixels stop being able to win a bucket.
    /// </remarks>
    private const int SampleSize = 64;

    /// <summary>Minimum contrast between body text and the surface behind it.</summary>
    /// <remarks>WCAG AA for normal text. Below this, code in a docked panel starts to swim.</remarks>
    private const double TextContrastFloor = 4.5;

    /// <summary>Minimum contrast for muted text and for the accent against its surface.</summary>
    /// <remarks>
    /// WCAG AA for large text and UI components. Muted text is by definition meant to recede, so
    /// holding it to the full 4.5 would make it indistinguishable from body text.
    /// </remarks>
    private const double SecondaryContrastFloor = 3.0;

    /// <summary>Derives a palette from an image.</summary>
    public static ThemePalette FromImage(Image image)
    {
        ArgumentNullException.ThrowIfNull(image);

        Color[] pixels = Sample(image);
        double meanLightness = MeanLightness(pixels);
        bool dark = meanLightness < 0.5;

        Color dominant = Dominant(pixels);
        Color vivid = MostVivid(pixels, dominant, meanLightness);

        // The chrome takes the image's hue and a fraction of its saturation. Full saturation here
        // reads as a coloured filter over the whole application rather than as a tinted grey.
        (double hue, double saturation, _) = ToHsl(dominant);
        double tint = Math.Min(saturation * 0.45, dark ? 0.30 : 0.16);

        Color canvas = FromHsl(hue, tint, dark ? 0.075 : 0.955);
        Color surface = FromHsl(hue, tint, dark ? 0.115 : 0.925);
        Color surfaceRaised = FromHsl(hue, tint, dark ? 0.150 : 0.975);
        Color surfaceHover = FromHsl(hue, tint, dark ? 0.195 : 0.890);
        Color border = FromHsl(hue, tint * 0.9, dark ? 0.255 : 0.820);
        Color borderStrong = FromHsl(hue, tint * 0.8, dark ? 0.380 : 0.700);

        Color accent = Readable(Accentuate(vivid, dark), surface, SecondaryContrastFloor, dark);
        Color accentHover = Shift(accent, dark ? 0.10 : -0.10);

        Color text = Readable(
            FromHsl(hue, tint * 0.25, dark ? 0.92 : 0.13), canvas, TextContrastFloor, dark);
        Color textMuted = Readable(
            FromHsl(hue, tint * 0.30, dark ? 0.68 : 0.38), canvas, SecondaryContrastFloor, dark);

        // Semantic colours keep their conventional hues — a red that is not red has stopped being an
        // error colour — but borrow the palette's vividness so they sit in the same world.
        double semanticSaturation = Math.Clamp(ToHsl(accent).Saturation, 0.45, 0.85);
        Color success = Readable(FromHsl(142, semanticSaturation, dark ? 0.55 : 0.34), surface, SecondaryContrastFloor, dark);
        Color warning = Readable(FromHsl(38, semanticSaturation, dark ? 0.55 : 0.36), surface, SecondaryContrastFloor, dark);
        Color error = Readable(FromHsl(2, semanticSaturation, dark ? 0.60 : 0.40), surface, SecondaryContrastFloor, dark);

        return new ThemePalette(
            canvas, surface, surfaceRaised, surfaceHover,
            border, borderStrong,
            text, textMuted,
            accent, accentHover,
            success, warning, error);
    }

    /// <summary>
    /// WCAG relative-luminance contrast ratio between two colours, from 1.0 to 21.0.
    /// </summary>
    public static double ContrastRatio(Color first, Color second)
    {
        double a = RelativeLuminance(first);
        double b = RelativeLuminance(second);
        (double lighter, double darker) = a >= b ? (a, b) : (b, a);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>Reduces the image to a fixed grid of samples.</summary>
    private static Color[] Sample(Image image)
    {
        using Bitmap small = new(SampleSize, SampleSize, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(small))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(image, new Rectangle(0, 0, SampleSize, SampleSize));
        }

        Color[] pixels = new Color[SampleSize * SampleSize];
        BitmapData data = small.LockBits(
            new Rectangle(0, 0, SampleSize, SampleSize), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] buffer = new byte[Math.Abs(data.Stride) * SampleSize];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            for (int index = 0; index < pixels.Length; index++)
            {
                int offset = (index / SampleSize * data.Stride) + (index % SampleSize * 4);
                pixels[index] = Color.FromArgb(buffer[offset + 2], buffer[offset + 1], buffer[offset]);
            }
        }
        finally
        {
            small.UnlockBits(data);
        }

        return pixels;
    }

    private static double MeanLightness(Color[] pixels)
    {
        double total = 0;
        foreach (Color pixel in pixels)
        {
            total += ToHsl(pixel).Lightness;
        }

        return total / pixels.Length;
    }

    /// <summary>The most common colour, quantised so near-identical shades count as one.</summary>
    private static Color Dominant(Color[] pixels)
    {
        Dictionary<int, int> counts = [];
        foreach (Color pixel in pixels)
        {
            int key = (pixel.R >> 3 << 10) | (pixel.G >> 3 << 5) | (pixel.B >> 3);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        int best = counts.OrderByDescending(pair => pair.Value).First().Key;

        // Average the pixels in the winning bucket rather than using the bucket's centre: the bucket
        // spans 8 levels per channel and its centre can sit visibly off the actual colour.
        long r = 0, g = 0, b = 0, n = 0;
        foreach (Color pixel in pixels)
        {
            int key = (pixel.R >> 3 << 10) | (pixel.G >> 3 << 5) | (pixel.B >> 3);
            if (key != best) continue;
            r += pixel.R;
            g += pixel.G;
            b += pixel.B;
            n++;
        }

        return n == 0 ? Color.Black : Color.FromArgb((int)(r / n), (int)(g / n), (int)(b / n));
    }

    /// <summary>
    /// The colour that best carries the image's character, for use as the accent.
    /// </summary>
    /// <remarks>
    /// Scored so a small patch of intense neon beats a large wash of muted colour — which is what a
    /// person means when they point at an image and call it "the cyan one". Weighting by frequency
    /// alone returns the background every time; by saturation alone it latches onto a stray pixel.
    ///
    /// Two terms earn their place. Saturation is <b>cubed</b>, because squaring was not enough to
    /// beat sheer area: all three shipped themes sit on large dark-navy fields, and every one of
    /// them derived the same blue accent despite looking nothing alike. And colours brighter than
    /// the image's mean are favoured, because in art like this the accent colour is nearly always
    /// something that glows — the neon, the sun, the rim light — rather than the field behind it.
    ///
    /// Votes are pooled by <b>hue</b>, not by RGB bucket. An RGB histogram splits a gradient across
    /// dozens of neighbouring buckets that then individually lose to a flat field, which is exactly
    /// how the synthwave sunset — whose entire subject is a glowing orange sun — derived an indigo
    /// accent from the sky behind it. Every shade of that sun shares a hue, so pooling by hue counts
    /// it once and it wins. Within the winning hue the colour is the weight-biased mean, so the
    /// vivid core of the glow decides the final tone rather than its washed-out fringe.
    /// </remarks>
    private static Color MostVivid(Color[] pixels, Color dominant, double meanLightness)
    {
        const int HueBins = 30;   // 12° apiece: fine enough to separate cyan from blue.

        double[] scores = new double[HueBins];
        double[] sumR = new double[HueBins];
        double[] sumG = new double[HueBins];
        double[] sumB = new double[HueBins];

        foreach (Color pixel in pixels)
        {
            (double hue, double saturation, double lightness) = ToHsl(pixel);

            // Near-black and near-white have unstable hues; treating them as candidates produces an
            // accent that changes wildly between two visually identical images. The window is wide
            // enough to keep pale artwork in play — clipping at 0.92 excluded a cream image
            // entirely and dropped it into the greyscale fallback.
            if (lightness is < 0.12 or > 0.95) continue;
            if (saturation < 0.15) continue;

            double glow = 1d + (3d * Math.Max(0d, lightness - meanLightness));
            double weight = saturation * saturation * saturation * glow;

            int bin = (int)(((hue % 360d) + 360d) % 360d / (360d / HueBins)) % HueBins;
            scores[bin] += weight;
            sumR[bin] += pixel.R * weight;
            sumG[bin] += pixel.G * weight;
            sumB[bin] += pixel.B * weight;
        }

        int winner = 0;
        for (int bin = 1; bin < HueBins; bin++)
        {
            if (scores[bin] > scores[winner]) winner = bin;
        }

        if (scores[winner] <= 0d)
        {
            // A greyscale image has no accent to find. Lift the dominant colour instead, so the
            // result is still a deliberate part of the palette rather than an arbitrary blue.
            return Shift(dominant, 0.35);
        }

        double total = scores[winner];
        return Color.FromArgb(
            (int)Math.Clamp(sumR[winner] / total, 0, 255),
            (int)Math.Clamp(sumG[winner] / total, 0, 255),
            (int)Math.Clamp(sumB[winner] / total, 0, 255));
    }

    /// <summary>
    /// Turns a sampled colour into something that works as an accent.
    /// </summary>
    /// <remarks>
    /// Taking the sampled colour as-is gave dull results: averaging a histogram bucket pulls
    /// saturation down, so a vividly purple image produced a muted slate accent. The hue is the part
    /// worth keeping — saturation and lightness are set to values that read as an accent on chrome.
    ///
    /// A nearly-colourless image is left alone. Forcing saturation onto it would invent a hue out of
    /// sensor noise, and two greyscale images that look identical would get unrelated accents.
    /// </remarks>
    private static Color Accentuate(Color colour, bool darkChrome)
    {
        (double hue, double saturation, double lightness) = ToHsl(colour);
        if (saturation < 0.12)
        {
            return FromHsl(hue, saturation, darkChrome ? Math.Max(lightness, 0.55) : Math.Min(lightness, 0.45));
        }

        return FromHsl(hue, Math.Clamp(Math.Max(saturation, 0.58), 0d, 0.92), darkChrome ? 0.60 : 0.42);
    }

    /// <summary>
    /// Finds the lightness at which a colour best clears the contrast floor against its background.
    /// </summary>
    /// <remarks>
    /// Scans the whole lightness axis and keeps the best result rather than stepping until it stops
    /// improving. Greedy stepping looks correct and is not: a colour sitting just above a light
    /// background gets *worse* before it gets better, because the route to contrast runs through the
    /// background's own luminance. That bug turned a cream image's accent into pure white on a
    /// near-white surface — 1.17:1, invisible.
    ///
    /// It stops at the first lightness that clears the floor, so a colour is darkened or lightened
    /// only as far as legibility actually requires and keeps as much of its character as it can.
    /// </remarks>
    private static Color Readable(Color colour, Color background, double floor, bool darkChrome)
    {
        if (ContrastRatio(colour, background) >= floor)
        {
            return colour;
        }

        (double hue, double saturation, double lightness) = ToHsl(colour);
        Color best = colour;
        double bestRatio = ContrastRatio(colour, background);

        // Search outward from where the colour already is, preferring the direction the chrome
        // gives room in, so the accent stays near its sampled lightness when it can.
        double direction = darkChrome ? 1d : -1d;
        for (int step = 1; step <= 40; step++)
        {
            foreach (double candidateLightness in new[]
            {
                lightness + (direction * step * 0.025),
                lightness - (direction * step * 0.025),
            })
            {
                if (candidateLightness is < 0d or > 1d) continue;

                Color candidate = FromHsl(hue, saturation, candidateLightness);
                double ratio = ContrastRatio(candidate, background);
                if (ratio > bestRatio)
                {
                    best = candidate;
                    bestRatio = ratio;
                }

                if (bestRatio >= floor) return best;
            }
        }

        return best;
    }

    /// <summary>Moves a colour along the lightness axis, keeping its hue and saturation.</summary>
    private static Color Shift(Color colour, double amount)
    {
        (double hue, double saturation, double lightness) = ToHsl(colour);
        return FromHsl(hue, saturation, Math.Clamp(lightness + amount, 0d, 1d));
    }

    private static double RelativeLuminance(Color colour)
    {
        static double Channel(int value)
        {
            double v = value / 255d;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(colour.R)) + (0.7152 * Channel(colour.G)) + (0.0722 * Channel(colour.B));
    }

    private static (double Hue, double Saturation, double Lightness) ToHsl(Color colour)
    {
        double r = colour.R / 255d;
        double g = colour.G / 255d;
        double b = colour.B / 255d;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double lightness = (max + min) / 2d;

        if (Math.Abs(max - min) < 1e-9)
        {
            return (0d, 0d, lightness);
        }

        double delta = max - min;
        double saturation = lightness > 0.5 ? delta / (2d - max - min) : delta / (max + min);

        double hue;
        if (Math.Abs(max - r) < 1e-9)
        {
            hue = ((g - b) / delta) + (g < b ? 6d : 0d);
        }
        else if (Math.Abs(max - g) < 1e-9)
        {
            hue = ((b - r) / delta) + 2d;
        }
        else
        {
            hue = ((r - g) / delta) + 4d;
        }

        return (hue * 60d, saturation, lightness);
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        saturation = Math.Clamp(saturation, 0d, 1d);
        lightness = Math.Clamp(lightness, 0d, 1d);

        if (saturation < 1e-9)
        {
            int grey = (int)Math.Round(lightness * 255d);
            return Color.FromArgb(grey, grey, grey);
        }

        double q = lightness < 0.5
            ? lightness * (1d + saturation)
            : lightness + saturation - (lightness * saturation);
        double p = (2d * lightness) - q;
        double h = ((hue % 360d) + 360d) % 360d / 360d;

        return Color.FromArgb(
            Channel(p, q, h + (1d / 3d)),
            Channel(p, q, h),
            Channel(p, q, h - (1d / 3d)));

        static int Channel(double p, double q, double t)
        {
            if (t < 0d) t += 1d;
            if (t > 1d) t -= 1d;

            double value = t < 1d / 6d ? p + ((q - p) * 6d * t)
                : t < 1d / 2d ? q
                : t < 2d / 3d ? p + ((q - p) * ((2d / 3d) - t) * 6d)
                : p;

            return (int)Math.Round(Math.Clamp(value, 0d, 1d) * 255d);
        }
    }
}
