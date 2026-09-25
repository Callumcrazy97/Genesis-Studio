using System;

namespace Genesis.Shared.Assets;

public static class SpriteOriginUtility
{
    /// <summary>
    /// True when an origin declares itself normalized but carries values no normalized origin could
    /// have, which in practice means pixel values wearing the wrong label (NEXT-092).
    /// </summary>
    /// <remarks>
    /// A normalized origin is a fraction of the sprite: 0..1, or modestly outside it for a pivot
    /// deliberately placed off the sprite. A 32x32 sprite claiming a normalized origin of (16,32)
    /// resolves to (512,1024) and renders a quarter of a screen away — invisible, silently, with
    /// every other part of the object still working. Deliberately a <b>report</b> rather than a
    /// reinterpretation: a genuinely off-sprite pivot is legal, so guessing would break a valid
    /// document to rescue an invalid one. Being told beats being quietly corrected.
    /// </remarks>
    public static bool IsImplausibleNormalizedOrigin(SpriteRuntimeOrigin origin)
    {
        if (origin == null) return false;
        if (string.Equals(origin.Space, "pixel", StringComparison.OrdinalIgnoreCase)
            || string.Equals(origin.Space, "pixels", StringComparison.OrdinalIgnoreCase))
            return false;

        const double plausibleLimit = 4.0;
        return Math.Abs(origin.X) > plausibleLimit || Math.Abs(origin.Y) > plausibleLimit;
    }

    /// <summary>Actionable description of an implausible origin, naming the fix.</summary>
    public static string DescribeImplausibleOrigin(SpriteRuntimeOrigin origin, string spriteName)
    {
        if (origin == null) return string.Empty;
        return $"Sprite '{spriteName}' declares origin ({origin.X}, {origin.Y}) in '{origin.Space}' "
            + "space, but a normalized origin is a fraction of the sprite. These look like pixel "
            + "values, which resolve far outside the sprite and draw it off screen. Set the origin's "
            + "space to 'pixels', or express the values as fractions.";
    }

    public static (float X, float Y) ResolvePixels(SpriteRuntimeOrigin origin, int width, int height)
    {
        if (origin == null || width <= 0 || height <= 0)
            return (width * 0.5f, height * 0.5f);

        // The editor's enum serializes as "pixels"; accept the legacy singular spelling too.
        if (string.Equals(origin.Space, "pixel", StringComparison.OrdinalIgnoreCase)
            || string.Equals(origin.Space, "pixels", StringComparison.OrdinalIgnoreCase))
            return ((float)origin.X, (float)origin.Y);

        return ((float)origin.X * width, (float)origin.Y * height);
    }

    public static (float X, float Y) ResolveDisplayPivot(
        SpriteRuntimeOrigin origin,
        int sourceWidth,
        int sourceHeight,
        float displayWidth,
        float displayHeight)
    {
        (float px, float py) = ResolvePixels(origin, sourceWidth, sourceHeight);
        float sx = sourceWidth <= 0 ? 1f : displayWidth / sourceWidth;
        float sy = sourceHeight <= 0 ? 1f : displayHeight / sourceHeight;
        return (px * sx, py * sy);
    }
}
