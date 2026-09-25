namespace Genesis.Application.Editors.Image.Imaging;

public enum PbrMaterialPreset
{
    Organic,
    Rock,
    Wood,
    Metal,
    Fabric,
    Stylized,
}

public sealed class PbrMaterialSettings
{
    public PbrMaterialPreset Preset { get; set; } = PbrMaterialPreset.Organic;
    public float NormalStrength { get; set; } = 2.5f;
    public float HeightContrast { get; set; } = 1f;
    public float Roughness { get; set; } = 0.72f;
    public float Metallic { get; set; }
    public bool Seamless { get; set; } = true;

    public static PbrMaterialSettings FromPreset(PbrMaterialPreset preset) => preset switch
    {
        PbrMaterialPreset.Rock => new() { Preset = preset, NormalStrength = 4.2f, HeightContrast = 1.35f, Roughness = 0.82f, Metallic = 0.02f },
        PbrMaterialPreset.Wood => new() { Preset = preset, NormalStrength = 2.8f, HeightContrast = 1.1f, Roughness = 0.68f, Metallic = 0f },
        PbrMaterialPreset.Metal => new() { Preset = preset, NormalStrength = 1.8f, HeightContrast = 0.65f, Roughness = 0.32f, Metallic = 0.92f },
        PbrMaterialPreset.Fabric => new() { Preset = preset, NormalStrength = 3.4f, HeightContrast = 0.72f, Roughness = 0.9f, Metallic = 0f },
        PbrMaterialPreset.Stylized => new() { Preset = preset, NormalStrength = 2.2f, HeightContrast = 1.65f, Roughness = 0.58f, Metallic = 0f },
        _ => new() { Preset = preset, NormalStrength = 2.5f, HeightContrast = 1f, Roughness = 0.72f, Metallic = 0f },
    };
}

public readonly record struct PbrGeneratedMap(string Name, ImageMaterialChannel Channel, byte[] Pixels);

/// <summary>Deterministic colour-to-PBR derivation used by both UI and headless editor tests.</summary>
public static class PbrMaterialGenerator
{
    public static IReadOnlyList<PbrGeneratedMap> Generate(byte[] color, int width, int height, PbrMaterialSettings settings)
    {
        ArgumentNullException.ThrowIfNull(color);
        ArgumentNullException.ThrowIfNull(settings);
        if (width <= 0 || height <= 0 || color.Length < checked(width * height * 4))
            throw new ArgumentException("Colour buffer dimensions are invalid.", nameof(color));
        float normalStrength = Math.Clamp(settings.NormalStrength, 0.05f, 16f);
        float contrast = Math.Clamp(settings.HeightContrast, 0.05f, 4f);
        byte[] source = settings.Seamless ? MakeSeamless(color, width, height) : color.AsSpan(0, width * height * 4).ToArray();
        float[] heightMap = new float[width * height];
        for (int i = 0; i < heightMap.Length; i++)
        {
            int p = i * 4;
            float luminance = (source[p] * 0.2126f + source[p + 1] * 0.7152f + source[p + 2] * 0.0722f) / 255f;
            float chroma = (Math.Max(source[p], Math.Max(source[p + 1], source[p + 2])) - Math.Min(source[p], Math.Min(source[p + 1], source[p + 2]))) / 255f;
            float shaped = settings.Preset switch
            {
                PbrMaterialPreset.Rock => luminance * 0.78f + chroma * 0.22f,
                PbrMaterialPreset.Wood => luminance * 0.9f + MathF.Sin(i % width * 0.16f) * 0.035f,
                PbrMaterialPreset.Fabric => luminance * 0.88f + ((i % width + i / width) & 1) * 0.045f,
                _ => luminance,
            };
            heightMap[i] = Math.Clamp((shaped - 0.5f) * contrast + 0.5f, 0f, 1f);
        }

        byte[] heightPixels = ScalarMap(heightMap, source);
        byte[] normalPixels = new byte[source.Length];
        byte[] roughnessPixels = new byte[source.Length];
        byte[] metallicPixels = new byte[source.Length];
        byte[] occlusionPixels = new byte[source.Length];
        float baseRoughness = Math.Clamp(settings.Roughness, 0f, 1f);
        float baseMetallic = Math.Clamp(settings.Metallic, 0f, 1f);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = y * width + x;
            int p = i * 4;
            float left = Sample(heightMap, width, height, x - 1, y, settings.Seamless);
            float right = Sample(heightMap, width, height, x + 1, y, settings.Seamless);
            float up = Sample(heightMap, width, height, x, y - 1, settings.Seamless);
            float down = Sample(heightMap, width, height, x, y + 1, settings.Seamless);
            System.Numerics.Vector3 normal = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(
                (left - right) * normalStrength, (up - down) * normalStrength, 1f));
            normalPixels[p] = ToByte(normal.X * 0.5f + 0.5f);
            normalPixels[p + 1] = ToByte(normal.Y * 0.5f + 0.5f);
            normalPixels[p + 2] = ToByte(normal.Z * 0.5f + 0.5f);
            normalPixels[p + 3] = source[p + 3];

            float detail = Math.Clamp((MathF.Abs(left - right) + MathF.Abs(up - down)) * 2.4f, 0f, 1f);
            float roughness = Math.Clamp(baseRoughness + detail * 0.18f - heightMap[i] * 0.08f, 0f, 1f);
            float metallic = settings.Preset == PbrMaterialPreset.Metal
                ? Math.Clamp(baseMetallic - detail * 0.12f, 0f, 1f)
                : Math.Clamp(baseMetallic * (0.65f + heightMap[i] * 0.35f), 0f, 1f);
            float occlusion = Math.Clamp(0.62f + heightMap[i] * 0.46f - detail * 0.18f, 0f, 1f);
            WriteScalar(roughnessPixels, p, roughness, source[p + 3]);
            WriteScalar(metallicPixels, p, metallic, source[p + 3]);
            WriteScalar(occlusionPixels, p, occlusion, source[p + 3]);
        }
        return new[]
        {
            new PbrGeneratedMap("Normal Map", ImageMaterialChannel.Normal, normalPixels),
            new PbrGeneratedMap("Roughness", ImageMaterialChannel.Roughness, roughnessPixels),
            new PbrGeneratedMap("Metallic", ImageMaterialChannel.Metallic, metallicPixels),
            new PbrGeneratedMap("Height", ImageMaterialChannel.Height, heightPixels),
            new PbrGeneratedMap("Occlusion", ImageMaterialChannel.Occlusion, occlusionPixels),
        };
    }

    private static byte[] MakeSeamless(byte[] source, int width, int height)
    {
        byte[] output = source.AsSpan(0, width * height * 4).ToArray();
        int borderX = Math.Max(1, width / 10);
        int borderY = Math.Max(1, height / 10);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            float edgeX = Math.Clamp(Math.Min(x, width - 1 - x) / (float)borderX, 0f, 1f);
            float edgeY = Math.Clamp(Math.Min(y, height - 1 - y) / (float)borderY, 0f, 1f);
            float blendX = 1f - edgeX;
            float blendY = 1f - edgeY;
            int oppositeX = width - 1 - x;
            int oppositeY = height - 1 - y;
            int p = (y * width + x) * 4;
            int px = (y * width + oppositeX) * 4;
            int py = (oppositeY * width + x) * 4;
            for (int c = 0; c < 4; c++)
            {
                float value = source[p + c];
                value = float.Lerp(value, (value + source[px + c]) * 0.5f, blendX * 0.72f);
                value = float.Lerp(value, (value + source[py + c]) * 0.5f, blendY * 0.72f);
                output[p + c] = (byte)Math.Clamp((int)MathF.Round(value), 0, 255);
            }
        }
        return output;
    }

    private static byte[] ScalarMap(float[] values, byte[] alphaSource)
    {
        byte[] output = new byte[alphaSource.Length];
        for (int i = 0; i < values.Length; i++) WriteScalar(output, i * 4, values[i], alphaSource[i * 4 + 3]);
        return output;
    }

    private static float Sample(float[] values, int width, int height, int x, int y, bool wrap)
    {
        if (wrap)
        {
            x = (x % width + width) % width;
            y = (y % height + height) % height;
        }
        else
        {
            x = Math.Clamp(x, 0, width - 1);
            y = Math.Clamp(y, 0, height - 1);
        }
        return values[y * width + x];
    }

    private static void WriteScalar(byte[] output, int offset, float value, byte alpha)
    {
        byte encoded = ToByte(value);
        output[offset] = encoded; output[offset + 1] = encoded; output[offset + 2] = encoded; output[offset + 3] = alpha;
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f), 0, 255);
}
