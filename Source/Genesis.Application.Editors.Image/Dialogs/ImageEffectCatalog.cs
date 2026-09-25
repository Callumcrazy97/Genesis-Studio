using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Image.Dialogs;

/// <summary>Reshade effect definitions for the preview dialog (parameters + apply funcs).</summary>
public static class ImageEffectCatalog
{
    public static ImageEffectDefinition Brightness { get; } = new("Brightness",
        [new("amount", "Brightness", EffectParamType.Slider, 0, -100, 100)],
        (source, _, _, p) => MapChannels(source, value => value + Int(p, "amount", 0) * 2.55f));

    public static ImageEffectDefinition Contrast { get; } = new("Contrast",
        [new("amount", "Contrast", EffectParamType.Slider, 0, -100, 100)],
        (source, _, _, p) => MapChannels(source, value => (value - 127.5f) * MathF.Pow(2, Int(p, "amount", 0) / 50f) + 127.5f));

    public static ImageEffectDefinition HueSaturation { get; } = new("Hue / Saturation",
        [new("hue", "Hue", EffectParamType.Slider, 0, -180, 180),
         new("saturation", "Saturation %", EffectParamType.Slider, 100, 0, 200),
         new("value", "Value %", EffectParamType.Slider, 100, 0, 200)],
        (source, _, _, p) => ImageAdvancedOperations.AdjustHsv(source, Int(p,"hue",0), Int(p,"saturation",100) / 100f, Int(p,"value",100) / 100f));

    public static ImageEffectDefinition Greyscale { get; } = new("Greyscale", [],
        (source, _, _, _) => ImageAdvancedOperations.AdjustHsv(source, 0, 0, 1));

    public static ImageEffectDefinition Invert { get; } = new("Invert", [],
        (source, _, _, _) => MapChannels(source, value => 255 - value));

    public static ImageEffectDefinition Opacity { get; } = new("Opacity",
        [new("amount", "Opacity %", EffectParamType.Slider, 100, 0, 100)],
        (source, _, _, p) => MapChannels(source, value => value * Int(p,"amount",100) / 100f, alphaOnly: true));

    public static ImageEffectDefinition Posterise { get; } = new("Posterise",
        [new("levels", "Levels per channel", EffectParamType.Slider, 6, 2, 32)],
        (source, _, _, p) => MapChannels(source, value => {
            float steps = Math.Clamp(Int(p,"levels",6),2,32) - 1;
            return MathF.Round(value / 255f * steps) / steps * 255f;
        }));

    public static ImageEffectDefinition ColourSwap { get; } = new("Colour Swap",
        [new("from", "From", EffectParamType.Colour, Color.Black), new("to", "To", EffectParamType.Colour, Color.White),
         new("tolerance", "Tolerance", EffectParamType.Slider, 0, 0, 255)],
        (source, _, _, p) => ImageAdvancedOperations.ReplaceColor(source, Colour(p,"from",Color.Black), Colour(p,"to",Color.White), Int(p,"tolerance",0), false));

    private static byte[] MapChannels(byte[] source, Func<float,float> transform, bool alphaOnly = false)
    {
        byte[] result = (byte[])source.Clone();
        for (int pixel = 0; pixel < result.Length; pixel += 4)
            for (int channel = alphaOnly ? 3 : 0; channel < (alphaOnly ? 4 : 3); channel++)
                result[pixel + channel] = (byte)Math.Clamp((int)MathF.Round(transform(source[pixel + channel])),0,255);
        return result;
    }

    public static readonly Color[] GameBoyPalette =
    [
        Color.FromArgb(15, 56, 15),
        Color.FromArgb(48, 98, 48),
        Color.FromArgb(139, 172, 15),
        Color.FromArgb(155, 188, 15),
    ];

    public static readonly Color[] WarmRampPalette =
    [
        Color.FromArgb(34, 24, 31),
        Color.FromArgb(117, 46, 61),
        Color.FromArgb(218, 94, 65),
        Color.FromArgb(255, 205, 117),
    ];

    public static ImageEffectDefinition WindWaker { get; } = new(
        "Wind Waker",
        [
            new EffectParameter("strength", "Strength", EffectParamType.Slider, 70, 0, 100),
        ],
        (source, _, _, p) => ImageAdvancedOperations.WindWaker(source, Int(p, "strength", 70)));

    public static ImageEffectDefinition N64Filter { get; } = new(
        "N64 Filter",
        Array.Empty<EffectParameter>(),
        (source, w, h, _) => ImageAdvancedOperations.N64Filter(source, w, h));

    public static ImageEffectDefinition CrtScanlines { get; } = new(
        "CRT Scanlines",
        [
            new EffectParameter("strength", "Strength", EffectParamType.Slider, 50, 0, 100),
        ],
        (source, w, h, p) => ImageAdvancedOperations.CrtScanlines(source, w, h, Int(p, "strength", 50)));

    public static ImageEffectDefinition Bloom { get; } = new(
        "Bloom",
        [
            new EffectParameter("threshold", "Threshold", EffectParamType.Slider, 80, 0, 100),
        ],
        (source, w, h, p) => ImageAdvancedOperations.Bloom(source, w, h, Int(p, "threshold", 80)));

    public static ImageEffectDefinition Vibrance { get; } = new(
        "Vibrance",
        [
            new EffectParameter("amount", "Amount", EffectParamType.Slider, 120, 0, 200),
        ],
        (source, _, _, p) => ImageAdvancedOperations.Vibrance(source, Int(p, "amount", 120)));

    public static ImageEffectDefinition Emboss { get; } = new(
        "Emboss",
        Array.Empty<EffectParameter>(),
        (source, w, h, _) => ImageAdvancedOperations.Emboss(source, w, h));

    public static ImageEffectDefinition Outline { get; } = new(
        "Outline",
        [
            new EffectParameter("thickness", "Thickness", EffectParamType.Slider, 1, 1, 10),
            new EffectParameter("color", "Color", EffectParamType.Colour, Color.Black),
            new EffectParameter("tolerance", "Tolerance", EffectParamType.Slider, 10, 0, 255),
        ],
        (source, w, h, p) => ImageAdvancedOperations.ReshadeOutline(
            source,
            w,
            h,
            Colour(p, "color", Color.Black),
            Int(p, "thickness", 1),
            Int(p, "tolerance", 10)));

    public static ImageEffectDefinition GameBoy { get; } = new(
        "Game Boy",
        Array.Empty<EffectParameter>(),
        (source, _, _, _) => ImageAdvancedOperations.ReshadePalette(source, GameBoyPalette, preserveLuminance: false));

    public static ImageEffectDefinition WarmRamp { get; } = new(
        "Warm Ramp",
        Array.Empty<EffectParameter>(),
        (source, _, _, _) => ImageAdvancedOperations.ReshadePalette(source, WarmRampPalette, preserveLuminance: false));

    public static int Int(IReadOnlyDictionary<string, object> parameters, string name, int fallback)
    {
        if (!parameters.TryGetValue(name, out object? value) || value is null)
            return fallback;
        return value switch
        {
            int i => i,
            float f => (int)MathF.Round(f),
            double d => (int)Math.Round(d),
            decimal m => (int)Math.Round(m),
            _ => Convert.ToInt32(value),
        };
    }

    public static Color Colour(IReadOnlyDictionary<string, object> parameters, string name, Color fallback)
    {
        if (!parameters.TryGetValue(name, out object? value) || value is null)
            return fallback;
        return value is Color color ? color : fallback;
    }
}
