// Ink Outline: a post effect (Fullscreen Shader resource) for illustrated and cel-shaded games.
// Lines come from scene depth alone: inverse depth is affine across a plane in screen space, so
// its second difference is zero on any flat surface and non-zero only where the surface steps (a
// silhouette) or bends (a crease). Lines thin and fade with distance like any level of detail, and
// foliage (marked by the renderer in SceneFlags) loses its lines sooner, so a distant meadow is not
// inked into a dark speckle.

cbuffer GenesisFrame : register(b4) { float Time; float Frame; float2 Resolution; };

cbuffer GenesisParameters : register(b5)
{
    float Opacity;            // 0 draws nothing, 1 full strength
    float WidthPixels;        // line width at 1080p
    float DepthStep;          // relative depth jump that counts as a silhouette (0.012 = 1.2%)
    float CreaseDegrees;      // creases sharper than this are inked
    float3 InkColor;          // display-space colour
    float FullWidthDistance;  // lines keep full width to here (metres)
    float FarDistance;        // one pixel wide and FarOpacity by here
    float FarOpacity;
    float FadeOutDistance;    // gone altogether by here
    float FoliageDistance;    // foliage lines gone by here
};

cbuffer GenesisCamera : register(b6)
{
    float4 CameraClip;        // near, far, depth reversed (1), tan(vertical fov / 2)
    float4 CameraView;        // aspect, perspective (1), radians per pixel, unused
    float4x4 InvViewProjection;
    float4 CameraPosition;
};

Texture2D SceneColor : register(t0);
Texture2D<float> SceneDepth : register(t1);
Texture2D SceneFlags : register(t2);
SamplerState LinearClamp : register(s0);

struct PreviewVSOut { float4 SvPos : SV_Position; float2 UV : TEXCOORD0; };

// Proportional to 1 / view depth for either depth convention.
float InverseDepth(int2 pixel)
{
    float d = SceneDepth.Load(int3(pixel, 0));
    float near = CameraClip.x, far = CameraClip.y;
    float viewZ = CameraClip.z > 0.5
        ? near * far / max(near + d * (far - near), 1e-6)
        : near * far / max(far - d * (far - near), 1e-6);
    return 1.0 / max(viewZ, 1e-4);
}

static const int2 TapDirs[4] = { int2(1, 0), int2(0, 1), int2(1, 1), int2(1, -1) };
static const float TapLengths[4] = { 1.0, 1.0, 1.41421356, 1.41421356 };

float Ink(int2 pixel, int2 maxPixel)
{
    float q0 = InverseDepth(pixel);
    float viewDepth = 1.0 / q0;
    float distant = saturate((viewDepth - FullWidthDistance) / max(FarDistance - FullWidthDistance, 1e-3));
    float width = WidthPixels * (Resolution.y / 1080.0);
    int tapStep = max((int)ceil(width / 3.0), 1);
    width = lerp(width, min(width, 1.0), distant) / (float)tapStep;
    float creaseRadians = CreaseDegrees * (3.14159265 / 180.0);

    float ink = 0.0;
    [unroll]
    for (int ring = 1; ring <= 3; ring++)
    {
        float ringWeight = saturate(width - (float)(ring - 1));
        float strongest = 0.0;
        [unroll]
        for (int i = 0; i < 4; i++)
        {
            int2 offset = TapDirs[i] * (ring * tapStep);
            float qa = InverseDepth(clamp(pixel + offset, int2(0, 0), maxPixel));
            float qb = InverseDepth(clamp(pixel - offset, int2(0, 0), maxPixel));
            float bend = (qa + qb - 2.0 * q0) / q0;
            // Silhouette: only the nearer surface is inked, so a line is one width rather than two.
            float step = bend < 0.0 ? smoothstep(DepthStep, DepthStep * 2.0, -bend) : 0.0;
            // Crease: the angle the surface turns through, its own slope divided out. The far side of
            // a silhouette step is not a crease.
            float span = TapLengths[i] * (float)(ring * tapStep) * CameraView.z;
            float slope = (qa - qb) / (2.0 * q0 * span);
            float turn = abs(bend) / (span * (1.0 + slope * slope));
            float crease = bend > DepthStep ? 0.0 : smoothstep(creaseRadians, creaseRadians * 1.6, turn);
            // A sliver a pixel or two across (farther surface on both sides) loses its line with
            // distance: a level of detail by size on screen.
            bool sliver = ring <= 2 && (q0 - qa) > DepthStep * q0 && (q0 - qb) > DepthStep * q0;
            strongest = max(strongest, max(step, crease) * (sliver ? 1.0 - distant : 1.0));
        }
        ink = max(ink, strongest * ringWeight);
    }

    float gone = FadeOutDistance > FarDistance
        ? saturate((viewDepth - FarDistance) / max(FadeOutDistance - FarDistance, 1e-3))
        : 0.0;
    // The renderer marks foliage pixels 0.875 or 0.375 in SceneFlags.r.
    float flag = SceneFlags.Load(int3(pixel, 0)).r;
    if ((abs(flag - 0.375) < 0.06 || abs(flag - 0.875) < 0.06) && FoliageDistance > FullWidthDistance)
        gone = max(gone, saturate((viewDepth - FullWidthDistance) / max(FoliageDistance - FullWidthDistance, 1e-3)));
    return ink * lerp(1.0, FarOpacity, distant) * (1.0 - gone);
}

float4 MainPS(PreviewVSOut IN) : SV_Target
{
    int2 pixel = int2(IN.SvPos.xy);
    float4 color = SceneColor.Load(int3(pixel, 0));
    if (CameraView.y < 0.5 || Opacity <= 0.001) return color;
    int2 maxPixel = int2(Resolution) - int2(1, 1);
    color.rgb = lerp(color.rgb, InkColor, saturate(Ink(pixel, maxPixel) * Opacity));
    return color;
}
