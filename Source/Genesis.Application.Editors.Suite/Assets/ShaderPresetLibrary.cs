using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Genesis.Application.Core.Projects;
using Genesis.Shared.Assets;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>A reusable built-in or project-scoped shader preset.</summary>
public sealed record ShaderPresetDefinition(
    string Name,
    string Description,
    ShaderTargetType TargetType,
    string Source,
    bool IsBuiltIn,
    string? FilePath = null,
    string Entry = "MainPS",
    string Profile = "ps_5_0",
    IReadOnlyList<ShaderParameterValue>? Parameters = null);

/// <summary>Loads Genesis defaults together with reusable presets stored in the current project.</summary>
public static class ShaderPresetLibrary
{
    private const string SpriteRainbowSource = """
        Texture2D SpriteTex : register(t0);
        SamplerState SpriteSamp : register(s0);
        cbuffer GenesisFrame : register(b4) { float Time; float Frame; float2 Resolution; };
        cbuffer GenesisParameters : register(b5) { float Speed; float Saturation; };
        struct VSOut { float4 SvPos : SV_Position; float2 UV : TEXCOORD0; float4 Color : COLOR; float FogDepth : TEXCOORD1; };
        float3 HsvToRgb(float3 c)
        {
            float3 p = abs(frac(c.xxx + float3(0.0, 0.666667, 0.333333)) * 6.0 - 3.0);
            return c.z * lerp(1.0.xxx, saturate(p - 1.0), c.y);
        }
        float4 MainPS(VSOut IN) : SV_Target
        {
            float4 tex = SpriteTex.Sample(SpriteSamp, IN.UV) * IN.Color;
            float hue = frac(IN.UV.x + Time * max(Speed, 0.01));
            return float4(tex.rgb * HsvToRgb(float3(hue, saturate(Saturation), 1.0)), tex.a);
        }
        """;

    private const string MeshRainbowSource = """
        Texture2D AlbedoTex : register(t1);
        SamplerState AlbedoSamp : register(s0);
        cbuffer GenesisFrame : register(b4) { float Time; float Frame; float2 Resolution; };
        cbuffer GenesisParameters : register(b5) { float Speed; float Saturation; };
        struct VSOut
        {
            float4 SvPos : SV_Position; float3 WorldPos : TEXCOORD1; float3 Normal : TEXCOORD2;
            float4 Color : TEXCOORD3; float2 UV : TEXCOORD4; float4 ShadowPos : TEXCOORD5;
            float4 ShadowPosNr : TEXCOORD6; float AtlasLayer : TEXCOORD7;
        };
        float3 HsvToRgb(float3 c)
        {
            float3 p = abs(frac(c.xxx + float3(0.0, 0.666667, 0.333333)) * 6.0 - 3.0);
            return c.z * lerp(1.0.xxx, saturate(p - 1.0), c.y);
        }
        float4 MainPS(VSOut IN) : SV_Target
        {
            float4 tex = AlbedoTex.Sample(AlbedoSamp, IN.UV) * IN.Color;
            float hue = frac(IN.UV.x + Time * max(Speed, 0.01));
            return float4(tex.rgb * HsvToRgb(float3(hue, saturate(Saturation), 1.0)), tex.a);
        }
        """;

    private const string ParticlePulseSource = """
        Texture2D SpriteTex : register(t0);
        SamplerState SpriteSamp : register(s0);
        cbuffer GenesisFrame : register(b4) { float Time; float Frame; float2 Resolution; };
        cbuffer GenesisParameters : register(b5) { float Speed; float Intensity; };
        struct VSOut { float4 SvPos : SV_Position; float2 UV : TEXCOORD0; float4 Color : COLOR; float FogDepth : TEXCOORD1; };
        float4 MainPS(VSOut IN) : SV_Target
        {
            float4 tex = SpriteTex.Sample(SpriteSamp, IN.UV) * IN.Color;
            float pulse = 0.55 + 0.45 * sin(Time * max(Speed, 0.01) + IN.UV.y * 6.28318);
            return float4(saturate(tex.rgb * pulse * max(Intensity, 0.0)), tex.a);
        }
        """;

    private const string TerrainTintSource = """
        cbuffer GenesisFrame : register(b4) { float Time; float Frame; float2 Resolution; };
        cbuffer GenesisParameters : register(b5) { float Speed; float TintStrength; };
        struct VSOut
        {
            float4 SvPos : SV_Position; float3 WorldPos : TEXCOORD1; float3 Normal : TEXCOORD2;
            float4 Color : TEXCOORD3; float2 UV : TEXCOORD4; float4 ShadowPos : TEXCOORD5;
            float4 ShadowPosNr : TEXCOORD6; float AtlasLayer : TEXCOORD7;
        };
        float4 MainPS(VSOut IN) : SV_Target
        {
            float wave = 0.5 + 0.5 * sin(Time * max(Speed, 0.01) + (IN.WorldPos.x + IN.WorldPos.z) * 0.08);
            float3 tint = lerp(IN.Color.rgb, float3(0.24, 0.72, 0.42), saturate(TintStrength) * wave);
            return float4(saturate(tint), IN.Color.a);
        }
        """;

    private const string PondWaterSource = """
        cbuffer GenesisFrame : register(b4) { float Time; float Frame; float2 Resolution; };
        cbuffer GenesisParameters : register(b5) { float Speed; float DepthTint; };
        struct VSOut
        {
            float4 SvPos : SV_Position; float3 WorldPos : TEXCOORD1; float3 Normal : TEXCOORD2;
            float4 Color : TEXCOORD3; float2 UV : TEXCOORD4; float4 ShadowPos : TEXCOORD5;
            float4 ShadowPosNr : TEXCOORD6; float AtlasLayer : TEXCOORD7;
        };
        float4 MainPS(VSOut IN) : SV_Target
        {
            float wave = 0.5 + 0.5 * sin(Time * max(Speed, 0.01) * 1.35 + IN.WorldPos.x * 0.32 + IN.WorldPos.z * 0.21);
            float3 shallow = float3(0.20, 0.58, 0.74);
            float3 deep = float3(0.04, 0.18, 0.38);
            float3 color = lerp(shallow, deep, saturate(DepthTint));
            color += float3(0.10, 0.16, 0.18) * wave;
            float facing = saturate(IN.Normal.y * 0.55 + 0.45);
            float alpha = lerp(0.92, 0.72, facing);
            return float4(saturate(color), alpha);
        }
        """;

    private const string FullscreenVignetteSource = """
        cbuffer GenesisFrame : register(b4) { float Time; float Frame; float2 Resolution; };
        cbuffer GenesisParameters : register(b5) { float Speed; float Strength; };
        struct PreviewVSOut { float4 SvPos : SV_Position; float2 UV : TEXCOORD0; };
        float4 MainPS(PreviewVSOut IN) : SV_Target
        {
            float2 p = IN.UV - 0.5;
            float vignette = saturate(1.0 - dot(p, p) * max(Strength, 0.0) * 2.0);
            float hue = 0.5 + 0.5 * sin(Time * max(Speed, 0.01) + IN.UV.x * 6.28318);
            return float4(vignette * float3(0.18 + hue * 0.4, 0.34, 0.72 - hue * 0.3), 1.0);
        }
        """;

    private const string StylizedMeshTemplate = """
        #define GENESIS_EFFECT __EFFECT__
        Texture2D AlbedoTex : register(t1);
        SamplerState AlbedoSamp : register(s0);
        cbuffer GenesisFrame : register(b4) { float Time; float Frame; float2 Resolution; };
        cbuffer GenesisParameters : register(b5) { float Speed; float Strength; float EdgeWidth; float DepthFade; };
        struct VSOut
        {
            float4 SvPos : SV_Position; float3 WorldPos : TEXCOORD1; float3 Normal : TEXCOORD2;
            float4 Color : TEXCOORD3; float2 UV : TEXCOORD4; float4 ShadowPos : TEXCOORD5;
            float4 ShadowPosNr : TEXCOORD6; float AtlasLayer : TEXCOORD7;
        };
        float4 MainPS(VSOut IN) : SV_Target
        {
            float2 uv = IN.UV;
            #if GENESIS_EFFECT == 8
                uv.x += (frac(sin(floor((uv.y + Time * Speed) * 28.0) * 91.7) * 193.2) - 0.5) * Strength * 0.08;
            #endif
            float4 tex = AlbedoTex.Sample(AlbedoSamp, uv) * IN.Color;
            float pulse = 0.5 + 0.5 * sin(Time * max(Speed, 0.01));
            float rim = pow(saturate(1.0 - abs(normalize(IN.Normal).z)), max(0.25, EdgeWidth));
            float3 color = tex.rgb;
            float alpha = tex.a;
            #if GENESIS_EFFECT == 2
                float noise = frac(sin(dot(floor(IN.WorldPos.xz * 17.0), float2(12.9898, 78.233))) * 43758.5453);
                alpha *= step(noise, saturate(0.55 + sin(Time * Speed) * Strength * 0.35));
                color += float3(1.0, 0.32, 0.05) * rim;
            #elif GENESIS_EFFECT == 3
                float scan = 0.55 + 0.45 * sin(IN.WorldPos.y * 36.0 - Time * Speed * 8.0);
                color = lerp(color, float3(0.05, 0.75, 1.0), saturate(Strength)) * scan + rim * 0.8;
                alpha *= 0.72;
            #elif GENESIS_EFFECT == 4
                color = lerp(color, float3(0.12, 0.45, 1.0), saturate(Strength)) + rim * float3(0.3, 0.9, 1.0) * (1.0 + pulse);
                alpha = saturate(alpha * 0.55 + rim * 0.5);
            #elif GENESIS_EFFECT == 5
                color += rim * float3(0.25, 0.65, 1.0) * max(Strength, 0.0);
            #elif GENESIS_EFFECT == 6
                float lava = 0.5 + 0.5 * sin(IN.WorldPos.x * 7.0 + IN.WorldPos.z * 5.0 + Time * Speed * 2.0);
                color = lerp(float3(0.08, 0.01, 0.0), float3(1.0, 0.22, 0.01), pow(lava, 2.0)) * (1.0 + Strength);
            #elif GENESIS_EFFECT == 7
                float light = saturate(dot(normalize(IN.Normal), normalize(float3(-0.4, 0.8, -0.5))) * 0.5 + 0.5);
                color *= floor(light * 4.0 + 0.5) / 4.0 + 0.15;
            #elif GENESIS_EFFECT == 8
                color = lerp(color, color.brg, step(0.82, frac(Time * Speed * 2.0 + IN.UV.y * 5.0)) * saturate(Strength));
            #endif
            return float4(saturate(color), saturate(alpha));
        }
        """;

    private static string StylizedMesh(int effect) =>
        StylizedMeshTemplate.Replace("__EFFECT__", effect.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static IReadOnlyList<ShaderPresetDefinition> BuiltIns { get; } =
    [
        new("Water", "Animated reflective water tint with depth controls.", ShaderTargetType.Terrain, PondWaterSource, true),
        new("Dissolve", "Noise-threshold surface dissolve with a bright edge.", ShaderTargetType.Model, StylizedMesh(2), true),
        new("Hologram", "Animated cyan scan lines and translucent rim lighting.", ShaderTargetType.Model, StylizedMesh(3), true),
        new("Forcefield", "Pulsing translucent energy shell with edge glow.", ShaderTargetType.Model, StylizedMesh(4), true),
        new("Rim Light", "View-facing edge illumination for silhouettes.", ShaderTargetType.Model, StylizedMesh(5), true),
        new("Lava", "Flowing emissive lava bands over a mesh surface.", ShaderTargetType.Model, StylizedMesh(6), true),
        new("Toon", "Quantized directional lighting for stylized models.", ShaderTargetType.Model, StylizedMesh(7), true),
        new("Glitch", "Animated scan-line displacement and channel shifts.", ShaderTargetType.Model, StylizedMesh(8), true),
        new("Image Rainbow", "Animated rainbow sweep for an Image resource.", ShaderTargetType.Image, SpriteRainbowSource, true),
        new("Model Rainbow", "Animated surface colour for a Model resource.", ShaderTargetType.Model, MeshRainbowSource, true),
        new("Particle Pulse", "Pulsing intensity for a Particle resource.", ShaderTargetType.Particle, ParticlePulseSource, true),
        new("Terrain Tint", "Animated tint for a terrain surface or authored component.", ShaderTargetType.Terrain, TerrainTintSource, true),
        new("Pond Water", "Animated teal fill for an authored terrain pond or lake.", ShaderTargetType.Terrain, PondWaterSource, true),
        new("Fullscreen Vignette", "Animated full-frame vignette effect.", ShaderTargetType.Fullscreen, FullscreenVignetteSource, true),
    ];

    public static string ProjectFile(string projectRoot) =>
        Path.Combine(Path.GetFullPath(projectRoot), ".genesis", "Editor", "ShaderPresets.json");

    public static IReadOnlyList<ShaderPresetDefinition> Load(string projectRoot)
    {
        List<ShaderPresetDefinition> presets = [.. BuiltIns];
        string path = ProjectFile(projectRoot);
        ShaderPresetStore store = ReadStore(path);
        foreach (ShaderPresetEntry entry in store.Presets
                     .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(entry.Name)
                || string.IsNullOrWhiteSpace(entry.Source)
                || BuiltIns.Any(preset =>
                    string.Equals(preset.Name, entry.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            presets.Add(new ShaderPresetDefinition(
                entry.Name.Trim(),
                entry.Description?.Trim() ?? string.Empty,
                entry.TargetType,
                entry.Source,
                false,
                path,
                string.IsNullOrWhiteSpace(entry.Entry) ? "MainPS" : entry.Entry.Trim(),
                string.IsNullOrWhiteSpace(entry.Profile) ? "ps_5_0" : entry.Profile.Trim(),
                CloneParameters(entry.Parameters)));
        }

        return presets;
    }

    public static ShaderPresetDefinition Save(
        string projectRoot,
        string name,
        string description,
        ShaderTargetType targetType,
        string source,
        string? existingPath = null,
        string entryPoint = "MainPS",
        string profile = "ps_5_0",
        IReadOnlyList<ShaderParameterValue>? parameters = null)
    {
        name = RequireName(name);
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("A shader preset must contain source code.", nameof(source));

        _ = existingPath; // Retained for source compatibility with the original per-file catalog.
        if (BuiltIns.Any(preset => string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Built-in shader presets cannot be overwritten.");

        string path = ProjectFile(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ShaderPresetStore store = ReadStore(path);
        store.Presets.RemoveAll(entry =>
            string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
        ShaderPresetEntry entry = new()
        {
            Name = name,
            Description = description?.Trim() ?? string.Empty,
            TargetType = targetType,
            Source = source,
            Entry = string.IsNullOrWhiteSpace(entryPoint) ? "MainPS" : entryPoint.Trim(),
            Profile = string.IsNullOrWhiteSpace(profile) ? "ps_5_0" : profile.Trim(),
            Parameters = CloneParameters(parameters),
        };
        store.Presets.Add(entry);
        if (File.Exists(path)) ResourceBackupService.BackupBeforeOverwrite(path);
        WriteStore(path, store);
        return new ShaderPresetDefinition(
            entry.Name,
            entry.Description,
            entry.TargetType,
            entry.Source,
            false,
            path,
            entry.Entry,
            entry.Profile,
            CloneParameters(entry.Parameters));
    }

    public static ShaderPresetDefinition Restore(string projectRoot, ShaderPresetDefinition preset) =>
        Save(
            projectRoot,
            preset.Name,
            preset.Description,
            preset.TargetType,
            preset.Source,
            preset.FilePath,
            preset.Entry,
            preset.Profile,
            preset.Parameters);

    public static bool Delete(string projectRoot, ShaderPresetDefinition preset)
    {
        if (preset.IsBuiltIn || string.IsNullOrWhiteSpace(preset.FilePath)) return false;
        string expected = Path.GetFullPath(ProjectFile(projectRoot));
        string path = Path.GetFullPath(preset.FilePath);
        if (!path.Equals(expected, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
        ShaderPresetStore store = ReadStore(path);
        int removed = store.Presets.RemoveAll(entry =>
            string.Equals(entry.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return false;
        ResourceBackupService.BackupBeforeOverwrite(path);
        WriteStore(path, store);
        return true;
    }

    public static ShaderPresetDefinition Rename(
        string projectRoot,
        ShaderPresetDefinition preset,
        string newName)
    {
        if (preset.IsBuiltIn || string.IsNullOrWhiteSpace(preset.FilePath))
            throw new InvalidOperationException("Built-in shader presets cannot be renamed.");
        newName = RequireName(newName);
        if (BuiltIns.Any(candidate =>
                string.Equals(candidate.Name, newName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("A built-in shader preset already uses that name.");
        }

        string expected = Path.GetFullPath(ProjectFile(projectRoot));
        string path = Path.GetFullPath(preset.FilePath);
        if (!path.Equals(expected, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new FileNotFoundException("The project shader preset catalog is missing.", path);
        ShaderPresetStore store = ReadStore(path);
        if (store.Presets.Any(entry =>
                !string.Equals(entry.Name, preset.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Name, newName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("A project shader preset already uses that name.");
        }

        ShaderPresetEntry entry = store.Presets.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, preset.Name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected project shader preset no longer exists.");
        entry.Name = newName;
        ResourceBackupService.BackupBeforeOverwrite(path);
        WriteStore(path, store);
        return new ShaderPresetDefinition(
            entry.Name,
            entry.Description,
            entry.TargetType,
            entry.Source,
            false,
            path,
            entry.Entry,
            entry.Profile,
            CloneParameters(entry.Parameters));
    }

    private static ShaderPresetStore ReadStore(string path)
    {
        if (!File.Exists(path)) return new ShaderPresetStore();
        try
        {
            ShaderPresetStore? store = JsonSerializer.Deserialize<ShaderPresetStore>(
                File.ReadAllText(path),
                JsonOptions);
            if (store is not { SchemaVersion: 1 }) return new ShaderPresetStore();
            store.Presets ??= [];
            return store;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A broken optional preset catalog cannot make the Shader Editor unavailable.
            return new ShaderPresetStore();
        }
    }

    private static void WriteStore(string path, ShaderPresetStore store)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(store, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static List<ShaderParameterValue> CloneParameters(
        IReadOnlyList<ShaderParameterValue>? parameters) =>
        parameters?.Select(parameter => new ShaderParameterValue
        {
            Name = parameter.Name,
            Type = parameter.Type,
            Value = parameter.Value?.ToArray() ?? [],
        }).ToList() ?? [];

    private static string RequireName(string name)
    {
        name = name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            throw new ArgumentException("Enter a name for the shader preset.", nameof(name));
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains('/', StringComparison.Ordinal)
            || name.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("Preset names cannot contain path separators or invalid filename characters.", nameof(name));
        }

        return name;
    }

    private sealed class ShaderPresetStore
    {
        public int SchemaVersion { get; set; } = 1;
        public List<ShaderPresetEntry> Presets { get; set; } = [];
    }

    private sealed class ShaderPresetEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public ShaderTargetType TargetType { get; set; }
        public string Source { get; set; } = string.Empty;
        public string Entry { get; set; } = "MainPS";
        public string Profile { get; set; } = "ps_5_0";
        public List<ShaderParameterValue> Parameters { get; set; } = [];
    }
}
