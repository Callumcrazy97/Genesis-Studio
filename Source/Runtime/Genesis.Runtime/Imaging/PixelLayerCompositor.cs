#nullable enable
using System;
namespace Genesis.Runtime.Imaging;

public enum PixelBlendMode { Normal, Multiply, Screen, Overlay, Add, Subtract, Darken, Lighten }

/// <summary>The Image Editor's RGBA layer blend implementation, also used by live pixel rigs.</summary>
public static class PixelLayerCompositor
{
    public static void Composite(Span<byte> destination, ReadOnlySpan<byte> source, float opacity, PixelBlendMode mode)
    {
        if (source.Length != destination.Length || source.Length % 4 != 0)
            throw new ArgumentException("Composite canvases must have matching RGBA dimensions.");
        if (!float.IsFinite(opacity)) throw new ArgumentException("Layer opacity must be finite.");
        float layerOpacity = Math.Clamp(opacity, 0f, 1f);
        for (int i = 0; i < destination.Length; i += 4)
        {
            float sa = source[i + 3] / 255f * layerOpacity;
            if (sa <= 0f) continue;
            float da = destination[i + 3] / 255f;
            float outA = sa + da * (1f - sa);
            for (int c = 0; c < 3; c++)
            {
                float s = source[i + c] / 255f;
                float d = destination[i + c] / 255f;
                float blended = Blend(s, d, mode);
                float value = outA <= 0f
                    ? 0f
                    : (s * sa * (1f - da) + blended * sa * da + d * da * (1f - sa)) / outA;
                destination[i + c] = (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
            }
            destination[i + 3] = (byte)Math.Clamp((int)MathF.Round(outA * 255f), 0, 255);
        }
    }

    private static float Blend(float source, float destination, PixelBlendMode mode) =>
        mode switch
        {
            PixelBlendMode.Multiply => source * destination,
            PixelBlendMode.Screen => 1f - (1f - source) * (1f - destination),
            PixelBlendMode.Overlay => destination < 0.5f
                ? 2f * source * destination
                : 1f - 2f * (1f - source) * (1f - destination),
            PixelBlendMode.Add => MathF.Min(1f, source + destination),
            PixelBlendMode.Subtract => MathF.Max(0f, destination - source),
            PixelBlendMode.Darken => MathF.Min(source, destination),
            PixelBlendMode.Lighten => MathF.Max(source, destination),
            _ => source,
        };

}
