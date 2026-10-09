using System.Text.Json;
using Genesis.Shared.Assets;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Shader resources for the project-shader checks (warm-up, cache keys, export cooking): a mesh
/// shader with an include, its own vertex and skinned vertex entries, a second pass, a disabled
/// pass and a variant; a sprite shader; and a Fullscreen post effect. Every program compiles for
/// DX11, DX12, Vulkan and OpenGL.
/// </summary>
internal static class ProjectShaderFixtures
{
    public const string MeshName = "Fixture Mesh";
    public const string SpriteName = "Fixture Sprite";
    public const string EffectName = "Fixture Effect";
    public const string IncludeName = "FixtureCommon.hlsli";

    /// <summary>Programs a draw compiles with each resource's active variant.</summary>
    public const int ActivePrograms = 6;

    /// <summary>Programs an export cooks: every variant (the mesh shader's "Fancy" adds four).</summary>
    public const int EveryVariantPrograms = 10;

    public const string Include = "float3 FixtureTint(float3 c) { return c * float3(0.9, 1.0, 0.8); }\n";

    private const string MeshSource = """
        #include "FixtureCommon.hlsli"
        cbuffer GenesisFrame : register(b4) { float Time; float Frame; float2 Resolution; };
        struct VSIn { float3 Pos : POSITION; float3 Normal : NORMAL; float4 Color : COLOR; float2 UV : TEXCOORD; };
        struct VSOut { float4 SvPos : SV_Position; float3 WorldPos : TEXCOORD1; float3 Normal : TEXCOORD2; float4 Color : TEXCOORD3; float2 UV : TEXCOORD4; };
        VSOut MainVS(VSIn i) { VSOut o = (VSOut)0; o.SvPos = float4(i.Pos, 1); o.Color = i.Color; o.UV = i.UV; return o; }
        VSOut SkinnedVS(VSIn i) { VSOut o = MainVS(i); o.SvPos.x += 0.001; return o; }
        float4 MainPS(VSOut input) : SV_Target
        {
        #ifdef FANCY
            return float4(FixtureTint(input.Color.rgb) * 1.5, 1);
        #else
            return float4(FixtureTint(input.Color.rgb) + Time * 0.0, 1);
        #endif
        }
        float4 OutlinePS(VSOut input) : SV_Target { return float4(0, 0, 0, 1); }
        """;

    private const string SpriteSource = """
        Texture2D Image : register(t0);
        SamplerState Linear : register(s0);
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return Image.Sample(Linear, uv).bgra; }
        """;

    private const string EffectSource = """
        cbuffer GenesisParameters : register(b5) { float Amount; };
        Texture2D SceneColor : register(t0);
        struct PreviewVSOut { float4 SvPos : SV_Position; float2 UV : TEXCOORD0; };
        float4 MainPS(PreviewVSOut IN) : SV_Target
        {
            float4 c = SceneColor.Load(int3(int2(IN.SvPos.xy), 0));
            return float4(lerp(c.rgb, 1.0 - c.rgb, Amount), c.a);
        }
        """;

    /// <summary>Writes the three resources (and the include) into the project's Assets\Shaders.</summary>
    public static string Write(string projectRoot)
    {
        string folder = Path.Combine(projectRoot, "Assets", "Shaders");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, IncludeName), Include);
        Save(Path.Combine(folder, MeshName + ".shader.json"), new ShaderAssetDocument
        {
            Pipeline = ShaderAssetPipeline.Mesh,
            AuthoringMode = ShaderAuthoringMode.Code,
            TargetType = ShaderTargetType.Model,
            Source = MeshSource,
            Variants = [new ShaderVariant { Name = "Fancy", Keywords = ["FANCY"] }],
            Passes =
            [
                new ShaderPassDefinition { Name = "Surface", Entry = "MainPS", VertexEntry = "MainVS", SkinnedVertexEntry = "SkinnedVS", Source = MeshSource },
                new ShaderPassDefinition { Name = "Outline", Entry = "OutlinePS", MeshPassMode = ShaderMeshPassMode.StencilOutline, Source = MeshSource },
                new ShaderPassDefinition { Name = "Off", Enabled = false, Entry = "NoSuchEntry", Source = MeshSource },
            ],
        });
        Save(Path.Combine(folder, SpriteName + ".shader.json"), new ShaderAssetDocument
        {
            Pipeline = ShaderAssetPipeline.Sprite,
            AuthoringMode = ShaderAuthoringMode.Code,
            Source = SpriteSource,
        });
        Save(Path.Combine(folder, EffectName + ".shader.json"), new ShaderAssetDocument
        {
            Pipeline = ShaderAssetPipeline.Fullscreen,
            AuthoringMode = ShaderAuthoringMode.Code,
            TargetType = ShaderTargetType.Fullscreen,
            Source = EffectSource,
            Parameters = [new ShaderParameterValue { Name = "Amount", Type = "float", Value = [1f] }],
        });
        ResourceCatalog.Invalidate(projectRoot);
        return folder;
    }

    private static void Save(string path, ShaderAssetDocument document) =>
        File.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
}
