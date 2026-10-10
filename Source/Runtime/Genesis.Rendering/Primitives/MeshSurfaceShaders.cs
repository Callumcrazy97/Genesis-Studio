using System.Text;
using System.Text.RegularExpressions;

namespace Genesis.Rendering.Primitives
{
    /// <summary>
    /// Surface shaders: a mesh Shader resource that includes <c>GenesisSurface.hlsl</c> writes one
    /// function, <c>void Surface(GenesisSurfaceInput i, inout GenesisSurface s)</c>, which receives
    /// the material's own albedo, normal, roughness, metalness, occlusion, emission and alpha and may
    /// change any of them. The engine then lights the result exactly as it lights its own materials
    /// (sun and its shadows, ambient, point and spot lights and their shadows, environment
    /// reflection, weather, fog), so a camo pattern, a lit window or a glass edge needs no copy of
    /// the engine's lighting or of its constant buffers.
    /// </summary>
    /// <remarks>
    /// The include is not a file: <see cref="ShaderCompiler"/> sees the line and compiles the
    /// engine's forward pixel shader (<see cref="ForwardShaders"/>) with <c>GENESIS_SURFACE</c>
    /// defined and the project's code placed between the engine's declarations and its pixel
    /// shader, whose entry point (<see cref="PixelEntry"/>) is used whatever entry the resource
    /// names. The inputs below are versioned (<see cref="Version"/>); fields are only ever added.
    /// Documentation/MeshShaders.md describes the contract.
    /// </remarks>
    public static class MeshSurfaceShaders
    {
        /// <summary>The name a project shader includes to be a surface shader.</summary>
        public const string IncludeName = "GenesisSurface.hlsl";

        /// <summary>The contract's version, also <c>GENESIS_SURFACE_VERSION</c> in HLSL.</summary>
        public const int Version = 1;

        /// <summary>The pixel entry point a surface shader is compiled with.</summary>
        public const string PixelEntry = "PS";

        private static readonly Regex IncludeLine = new(
            @"^[ \t]*#[ \t]*include[ \t]*[""<][ \t]*GenesisSurface\.hlsl[ \t]*["">][^\r\n]*",
            RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex SurfaceFunction = new(
            @"\bvoid\s+Surface\s*\(",
            RegexOptions.Compiled);

        /// <summary>True when <paramref name="source"/> includes <see cref="IncludeName"/>.</summary>
        public static bool IsSurfaceSource(string source) =>
            !string.IsNullOrEmpty(source)
            && source.Contains(IncludeName, StringComparison.OrdinalIgnoreCase)
            && IncludeLine.IsMatch(source);

        /// <summary>
        /// The complete HLSL compiled for a surface shader: the engine's declarations, the surface
        /// contract, the project's code (its include line blanked so line numbers stay its own) and
        /// the engine's pixel shader.
        /// </summary>
        public static string Compose(string source, string sourceName = null)
        {
            if (!IsSurfaceSource(source))
                throw new ArgumentException("The shader does not include " + IncludeName + ".", nameof(source));
            string project = IncludeLine.Replace(source, string.Empty);
            if (!SurfaceFunction.IsMatch(ShaderSourceText.WithoutComments(project)))
            {
                throw new InvalidOperationException(
                    "A surface shader (#include \"" + IncludeName + "\") defines "
                    + "void Surface(GenesisSurfaceInput i, inout GenesisSurface s). It was not found.");
            }

            string engine = ForwardShaders.Source;
            int split = engine.IndexOf(PixelSectionMarker, StringComparison.Ordinal);
            if (split < 0)
                throw new InvalidOperationException("The forward shader no longer has the section a surface shader is placed before.");

            // Only the file's name: a #line naming a folder would put the folder into the cache key,
            // and a game's shaders cooked in the project's folder would not be found where it is installed.
            string name = string.IsNullOrWhiteSpace(sourceName) ? "surface" : Path.GetFileName(sourceName);
            name = name.Replace('"', '\'');

            var composed = new StringBuilder(engine.Length + Api.Length + Epilogue.Length + project.Length + 256);
            composed.Append("#define GENESIS_SURFACE 1\n");
            composed.Append("#define GENESIS_SURFACE_VERSION ").Append(Version).Append('\n');
            composed.Append(engine, 0, split);
            composed.Append(Api);
            composed.Append("\n#line 1 \"").Append(name).Append("\"\n");
            composed.Append(project);
            composed.Append("\n#line 1 \"").Append(IncludeName).Append("\"\n");
            composed.Append(Epilogue);
            composed.Append(engine, split, engine.Length - split);
            return composed.ToString();
        }

        /// <summary>The forward shader's text the project's code is placed before.</summary>
        internal const string PixelSectionMarker = "struct PSOut";

        /// <summary>
        /// The contract, after the engine's own declarations (so a surface can call SrgbToLinear3,
        /// Hash21, InterleavedGradientNoise and read the material's textures) and before the project's code.
        /// </summary>
        internal const string Api = @"
// ── GenesisSurface.hlsl (contract version 1) ─────────────────────────────────
// Documentation/MeshShaders.md. Colours (Albedo, Emission, BaseColorTexture, SunColor) are in
// the space the engine lights in: linear when the project's colour pipeline is linear, as picked
// otherwise. GenesisColor() turns a colour picked in a colour box, or read from an ordinary image,
// into that space.

cbuffer GenesisSurfaceFrame : register(b3)
{
    float  GenesisSurfaceTime;
    float  GenesisSurfaceFrameNumber;
    float2 GenesisSurfaceResolution;
};

// Smooth, repeating sampling for a surface's own textures (AlbedoSamp, s0, is the material's own:
// sharp for pixel art, smooth only when the material has an ORM map).
SamplerState GenesisLinearWrap : register(s3);

struct GenesisSurfaceInput
{
    float3 WorldPosition;    // world space
    float3 Normal;           // the mesh's own normal, world space, unit length, towards the side drawn
    float3 ViewDirection;    // unit vector from the surface to the camera
    float3 CameraPosition;   // world space
    float2 UV;               // the mesh's texture coordinates
    float2 MaterialUV;       // the coordinates the material's maps are read with (UV scale, flow, parallax)
    float4 Color;            // the draw's colour: vertex colour x material colour x instance tint, as picked; a = its alpha
    float4 BaseColorTexture; // the material's base colour image at MaterialUV (rgb in lighting space, a as stored)
    float2 ScreenUV;         // 0..1 across the image being drawn
    float2 ScreenPosition;   // pixels
    float  Time;             // seconds since the game started (GenesisFrame.Time)
    float  Frame;            // frame number
    float2 Resolution;       // the image's size in pixels
    float3 SunDirection;     // unit vector from the surface towards the sun
    float3 SunColor;         // the sun's colour times its strength
    float3 AmbientSky;       // ambient light from above
    float3 AmbientGround;    // ambient light from below
    bool   FrontFace;        // false on the back of a two-sided surface
};

struct GenesisSurface
{
    float3 Albedo;     // base colour, lighting space (starts as the material's)
    float  Alpha;      // coverage: below the material's cut-off the pixel is not drawn; a see-through draw blends by it
    float3 Normal;     // world space (starts as the material's normal, with its normal map)
    float  Roughness;  // 0 mirror .. 1 matte
    float  Metalness;  // 0 .. 1
    float  Occlusion;  // ambient occlusion, 1 none
    float3 Emission;   // light the surface gives off, lighting space, added after lighting (may exceed 1)
};

// A colour picked in a colour box (or read from an ordinary image) in the space the engine lights in.
float3 GenesisColor(float3 picked)
{
    return LinearColorPipeline() ? SrgbToLinear3(picked) : picked;
}

float GenesisLuminance(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

// Schlick's Fresnel term: 0 facing the camera, 1 at a grazing angle. power 5 is physical.
float GenesisFresnel(GenesisSurfaceInput i, float3 normal, float power)
{
    return pow(1.0 - saturate(dot(normalize(normal), i.ViewDirection)), max(power, 0.0001));
}

// Bends a world-space normal by a tangent-space normal (xyz in -1..1, z out of the surface), with
// the tangent frame taken from the screen-space change of position and texture coordinates.
float3 GenesisPerturbNormal(GenesisSurfaceInput i, float3 normal, float3 tangentNormal)
{
    float3 dp1 = ddx(i.WorldPosition), dp2 = ddy(i.WorldPosition);
    float2 duv1 = ddx(i.UV), duv2 = ddy(i.UV);
    float3 dpdu = dp1 * duv2.y - dp2 * duv1.y;
    if (dot(dpdu, dpdu) < 1e-12)
        return normal;
    float3 n = normalize(normal);
    float3 t = normalize(dpdu - dot(dpdu, n) * n);
    float3 b = cross(n, t);
    return normalize(tangentNormal.x * t + tangentNormal.y * b + tangentNormal.z * n);
}
";

        /// <summary>
        /// After the project's code: fills the input, calls its Surface and hands the result back to
        /// the engine's pixel shader (the <c>GENESIS_SURFACE</c> blocks in <see cref="ForwardShaders"/>).
        /// </summary>
        internal const string Epilogue = @"
GenesisSurfaceInput GenesisSurfaceInputFor(VSOut IN, bool isFront, float2 materialUv, float4 tex)
{
    GenesisSurfaceInput i;
    float3 g = normalize(IN.Normal);
    i.WorldPosition = IN.WorldPos;
    i.Normal = isFront ? g : -g;
    i.CameraPosition = CameraPosTime.xyz;
    i.ViewDirection = normalize(CameraPosTime.xyz - IN.WorldPos);
    i.UV = IN.UV;
    i.MaterialUV = materialUv;
    i.Color = MaterialColor * IN.Color;
    i.BaseColorTexture = tex;
    i.ScreenUV = IN.SvPos.xy * ViewportParams.zw;
    i.ScreenPosition = IN.SvPos.xy;
    i.Time = GenesisSurfaceTime;
    i.Frame = GenesisSurfaceFrameNumber;
    i.Resolution = GenesisSurfaceResolution;
    i.SunDirection = normalize(-LightDirEnabled.xyz);
    i.SunColor = SunColorIntensity.rgb * SunColorIntensity.w;
    i.AmbientSky = AmbientColor.rgb;
    i.AmbientGround = AmbientGroundColor.rgb;
    i.FrontFace = isFront;
    return i;
}

void GenesisApplySurface(VSOut IN, bool isFront, float2 materialUv, float alphaCutoff,
    inout float4 tex, inout float3 base, inout float3 n, out float3 orm, out float3 emission)
{
    // The material's own values, exactly as the engine's pixel shader works them out.
    orm = MaterialFeatures.x > 0.5 ? OrmMap.Sample(AlbedoSamp, materialUv).rgb : float3(1.0, 0.72, 0.0);
    if (MaterialRoughness > 0.0)
    {
        float2 factors = float2(MaterialRoughness - 1.0, MaterialMetallic);
        orm.gb = MaterialFeatures.x > 0.5 ? orm.gb * factors : factors;
    }
    emission = MaterialFeatures.z > 0.5 ? EmissionMap.Sample(AlbedoSamp, materialUv).rgb * MaterialSurface.z : 0.0;
    if (LinearColorPipeline())
        emission = SrgbToLinear3(emission / max(MaterialSurface.z, 1e-4)) * MaterialSurface.z;

    GenesisSurface s;
    s.Albedo = base;
    s.Alpha = tex.a;
    s.Normal = n;
    s.Roughness = orm.g;
    s.Metalness = orm.b;
    s.Occlusion = orm.r;
    s.Emission = emission;
    Surface(GenesisSurfaceInputFor(IN, isFront, materialUv, tex), s);

    tex.a = s.Alpha;
    clip(tex.a - alphaCutoff);
    base = max(s.Albedo, 0.0);
    if (dot(s.Normal, s.Normal) > 1e-12)
        n = normalize(s.Normal);
    orm = float3(saturate(s.Occlusion), saturate(s.Roughness), saturate(s.Metalness));
    emission = max(s.Emission, 0.0);
}

";
    }

    /// <summary>Small text helpers for reading shader source.</summary>
    internal static class ShaderSourceText
    {
        private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex LineComment = new(@"//[^\r\n]*", RegexOptions.Compiled);

        public static string WithoutComments(string source) =>
            string.IsNullOrEmpty(source) ? string.Empty : LineComment.Replace(BlockComment.Replace(source, string.Empty), string.Empty);
    }
}
