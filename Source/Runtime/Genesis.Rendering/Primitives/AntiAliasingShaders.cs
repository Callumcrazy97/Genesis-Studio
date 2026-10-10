namespace Genesis.Rendering.Primitives;

/// <summary>
/// The engine's post-process anti-aliasing: FXAA (one pass) and SMAA 1x (edges, blend weights,
/// neighbourhood blend). Each runs over the tonemapped, display-encoded image after the composite
/// and the held items, and before a project's post effects and the GUI.
/// </summary>
/// <remarks>
/// Written for this engine after the published algorithms (Lottes' FXAA 3.11 quality path;
/// Jimenez et al.'s SMAA 1x with its orthogonal patterns). The SMAA area that the reference reads
/// from a precomputed texture is computed here from the same revectorised line, and the edge ends
/// are found by walking the edges one pixel at a time, so no lookup textures ship. Diagonal and
/// corner patterns are not detected. Pixels are read by integer position (worked out from the
/// UV, as the fog composite does) so every backend reads the same texels; mixing is done in linear
/// light, as a multisampled resolve would.
/// </remarks>
internal static class AntiAliasingShaders
{
    public const string Source = @"
cbuffer AntiAliasConstants : register(b0)
{
    // x = 1/width, y = 1/height, z = width, w = height of the image.
    float4 Metrics;
    // FXAA: x = sub-pixel blend, y = edge threshold (of the brightest neighbour), z = darkest threshold.
    // SMAA: x = edge threshold, y = local contrast factor, z = longest edge walked (pixels).
    float4 Params;
};

Texture2D ColorTex : register(t0);
Texture2D EdgesTex : register(t1);
Texture2D BlendTex : register(t2);
SamplerState LinearClamp : register(s0);

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VS(uint id : SV_VertexID)
{
    VSOut o;
    float2 p = float2((id << 1) & 2, id & 2);
    o.pos = float4(p * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    o.uv = p;
    return o;
}

int2 PixelOf(float2 uv) { return int2(floor(uv * Metrics.zw)); }
int2 Clamped(int2 p) { return clamp(p, int2(0, 0), int2(Metrics.zw) - int2(1, 1)); }
bool Inside(int2 p) { return p.x >= 0 && p.y >= 0 && p.x < (int)Metrics.z && p.y < (int)Metrics.w; }

// Perceived brightness of the display-encoded colour.
float Luma(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }
float4 ColorAt(int2 p) { return ColorTex.Load(int3(Clamped(p), 0)); }
float LumaAt(int2 p) { return Luma(ColorAt(p).rgb); }
float LumaAtUv(float2 uv) { return Luma(ColorTex.SampleLevel(LinearClamp, uv, 0).rgb); }

float3 ToLinear(float3 c)
{
    c = saturate(c);
    return lerp(pow(max((c + 0.055) / 1.055, 1e-7), 2.4), c / 12.92, step(c, 0.04045));
}

float3 ToDisplay(float3 c)
{
    c = saturate(c);
    return lerp(1.055 * pow(max(c, 1e-7), 1.0 / 2.4) - 0.055, c * 12.92, step(c, 0.0031308));
}

// ---------------------------------------------------------------------------------------------
// FXAA
// ---------------------------------------------------------------------------------------------

float4 PS_Fxaa(VSOut IN) : SV_Target
{
    int2 p = PixelOf(IN.uv);
    float4 centre = ColorAt(p);
    float lumaM = Luma(centre.rgb);
    float lumaN = LumaAt(p + int2(0, -1));
    float lumaS = LumaAt(p + int2(0, 1));
    float lumaW = LumaAt(p + int2(-1, 0));
    float lumaE = LumaAt(p + int2(1, 0));
    float lumaMax = max(max(max(lumaN, lumaS), max(lumaW, lumaE)), lumaM);
    float lumaMin = min(min(min(lumaN, lumaS), min(lumaW, lumaE)), lumaM);
    float range = lumaMax - lumaMin;
    // Too little contrast for a step anyone would see: left exactly as it is.
    if (range < max(Params.z, lumaMax * Params.y))
        return centre;

    float lumaNW = LumaAt(p + int2(-1, -1));
    float lumaNE = LumaAt(p + int2(1, -1));
    float lumaSW = LumaAt(p + int2(-1, 1));
    float lumaSE = LumaAt(p + int2(1, 1));

    // How far the centre stands out from its whole neighbourhood: a pixel-sized feature is
    // softened even where no long edge runs through it.
    float neighbourhood = ((lumaN + lumaS + lumaW + lumaE) * 2.0 + (lumaNW + lumaNE + lumaSW + lumaSE)) / 12.0;
    float subpixel = smoothstep(0.0, 1.0, saturate(abs(neighbourhood - lumaM) / range));
    subpixel = subpixel * subpixel * Params.x;

    // Horizontal edge: brightness changes from row to row more than from column to column.
    float acrossRows = abs(lumaN + lumaS - 2.0 * lumaM) * 2.0
        + abs(lumaNE + lumaSE - 2.0 * lumaE) + abs(lumaNW + lumaSW - 2.0 * lumaW);
    float acrossColumns = abs(lumaW + lumaE - 2.0 * lumaM) * 2.0
        + abs(lumaNW + lumaNE - 2.0 * lumaN) + abs(lumaSW + lumaSE - 2.0 * lumaS);
    bool horizontal = acrossRows >= acrossColumns;

    // The neighbour across the edge is on the side with the steeper change.
    float lumaBefore = horizontal ? lumaN : lumaW;
    float lumaAfter = horizontal ? lumaS : lumaE;
    float gradientBefore = abs(lumaBefore - lumaM);
    float gradientAfter = abs(lumaAfter - lumaM);
    bool before = gradientBefore >= gradientAfter;
    float edgeLuma = 0.5 * (lumaM + (before ? lumaBefore : lumaAfter));
    float gradient = 0.25 * max(gradientBefore, gradientAfter);
    float2 across = horizontal ? float2(0.0, Metrics.y) : float2(Metrics.x, 0.0);
    int2 acrossPixel = horizontal ? int2(0, 1) : int2(1, 0);
    if (before)
    {
        across = -across;
        acrossPixel = -acrossPixel;
    }

    // Walk both ways along the edge, half a pixel across it, until the brightness there (the two
    // rows averaged by the filtered read) leaves the edge's.
    float2 along = horizontal ? float2(Metrics.x, 0.0) : float2(0.0, Metrics.y);
    float2 onEdge = IN.uv + across * 0.5;
    float2 uv1 = onEdge - along;
    float2 uv2 = onEdge + along;
    float end1 = LumaAtUv(uv1) - edgeLuma;
    float end2 = LumaAtUv(uv2) - edgeLuma;
    bool done1 = abs(end1) >= gradient;
    bool done2 = abs(end2) >= gradient;
    [loop]
    for (int i = 0; i < 11; i++)
    {
        if (done1 && done2)
            break;
        float stride = i < 4 ? 1.0 : (i == 4 ? 1.5 : (i < 9 ? 2.0 : (i == 9 ? 4.0 : 8.0)));
        if (!done1)
        {
            uv1 -= along * stride;
            end1 = LumaAtUv(uv1) - edgeLuma;
            done1 = abs(end1) >= gradient;
        }
        if (!done2)
        {
            uv2 += along * stride;
            end2 = LumaAtUv(uv2) - edgeLuma;
            done2 = abs(end2) >= gradient;
        }
    }

    float distance1 = horizontal ? (IN.uv.x - uv1.x) : (IN.uv.y - uv1.y);
    float distance2 = horizontal ? (uv2.x - IN.uv.x) : (uv2.y - IN.uv.y);
    bool nearer1 = distance1 < distance2;
    float edgeOffset = 0.5 - min(distance1, distance2) / max(distance1 + distance2, 1e-6);
    // Blend only towards the end where the step turns away from the centre's side of the edge.
    bool centreDarker = lumaM < edgeLuma;
    bool turnsAway = ((nearer1 ? end1 : end2) < 0.0) != centreDarker;
    float amount = max(turnsAway ? edgeOffset : 0.0, subpixel);

    float3 neighbour = ColorAt(p + acrossPixel).rgb;
    float3 mixed = ToDisplay(lerp(ToLinear(centre.rgb), ToLinear(neighbour), amount));
    return float4(mixed, centre.a);
}

// ---------------------------------------------------------------------------------------------
// SMAA 1x: 1) edges
// ---------------------------------------------------------------------------------------------

// r: an edge along this pixel's left side; g: an edge along its top.
float4 PS_SmaaEdges(VSOut IN) : SV_Target
{
    int2 p = PixelOf(IN.uv);
    float lumaM = LumaAt(p);
    float lumaLeft = LumaAt(p + int2(-1, 0));
    float lumaTop = LumaAt(p + int2(0, -1));
    float2 delta = abs(lumaM - float2(lumaLeft, lumaTop));
    float2 edges = step(Params.x, delta);
    if (edges.x + edges.y == 0.0)
        return float4(0.0, 0.0, 0.0, 0.0);

    // Local contrast adaptation: a step much weaker than one right beside it is the soft side of
    // that stronger edge, not an edge of its own.
    float lumaRight = LumaAt(p + int2(1, 0));
    float lumaBottom = LumaAt(p + int2(0, 1));
    float2 strongest = max(delta, abs(lumaM - float2(lumaRight, lumaBottom)));
    float lumaLeftLeft = LumaAt(p + int2(-2, 0));
    float lumaTopTop = LumaAt(p + int2(0, -2));
    strongest = max(strongest, abs(float2(lumaLeft, lumaTop) - float2(lumaLeftLeft, lumaTopTop)));
    float finalDelta = max(strongest.x, strongest.y);
    edges *= step(finalDelta, Params.y * delta);
    return float4(edges, 0.0, 0.0);
}

// ---------------------------------------------------------------------------------------------
// SMAA 1x: 2) blend weights
// ---------------------------------------------------------------------------------------------

float2 EdgeAt(int2 p)
{
    if (!Inside(p))
        return float2(0.0, 0.0);
    return EdgesTex.Load(int3(p, 0)).rg;
}

// The area of the pixel column [x, x+1] between the line p1-p2 and the edge (y = 0): x is on this
// pixel's side of the edge (the line below it), y on the far side (the line above it).
float2 LineArea(float2 p1, float2 p2, float x)
{
    float xa = max(x, p1.x);
    float xb = min(x + 1.0, p2.x);
    if (xb <= xa)
        return float2(0.0, 0.0);
    float slope = (p2.y - p1.y) / (p2.x - p1.x);
    float ya = p1.y + slope * (xa - p1.x);
    float yb = p1.y + slope * (xb - p1.x);
    float width = xb - xa;
    if (ya * yb >= 0.0)
    {
        float a = 0.5 * (ya + yb) * width;
        return a < 0.0 ? float2(-a, 0.0) : float2(0.0, a);
    }
    // The line crosses the edge inside this column: a triangle on each side.
    float t = ya / (ya - yb);
    float first = 0.5 * abs(ya) * t * width;
    float second = 0.5 * abs(yb) * (1.0 - t) * width;
    return ya < 0.0 ? float2(first, second) : float2(second, first);
}

// A short U-shaped bump is blended more than its revectorised line alone would.
float2 SmoothArea(float d, float2 a1, float2 a2)
{
    float2 b1 = sqrt(a1 * 2.0) * 0.5;
    float2 b2 = sqrt(a2 * 2.0) * 0.5;
    float p = saturate(d / 32.0);
    return lerp(b1, a1, p) + lerp(b2, a2, p);
}

// The blend for the pixel 'left' pixels from the start of an edge 'left + right + 1' long. Each
// end's crossing edge: 0 none, 1 on this side, 2 on the far side, 3 both.
float2 OrthoArea(float left, float right, int kindLeft, int kindRight)
{
    if (kindLeft == 3)
    {
        if (kindRight == 1 || kindRight == 2) kindLeft = 3 - kindRight;
        else return float2(0.0, 0.0);
    }
    if (kindRight == 3)
    {
        if (kindLeft == 1 || kindLeft == 2) kindRight = 3 - kindLeft;
        else return float2(0.0, 0.0);
    }
    if (kindLeft == 0 && kindRight == 0)
        return float2(0.0, 0.0);

    float d = left + right + 1.0;
    float2 start = float2(0.0, kindLeft == 1 ? -0.5 : 0.5);
    float2 middle = float2(0.5 * d, 0.0);
    float2 end = float2(d, kindRight == 1 ? -0.5 : 0.5);
    if (kindLeft != 0 && kindRight != 0)
    {
        // A Z (the ends cross on opposite sides) is one line end to end; a U is two halves.
        if (kindLeft != kindRight)
            return LineArea(start, end, left);
        return SmoothArea(d, LineArea(start, middle, left), LineArea(middle, end, left));
    }
    // An L: from the crossing end to the middle, nothing on the other half.
    if (kindLeft != 0)
        return left <= right ? LineArea(start, middle, left) : float2(0.0, 0.0);
    return left >= right ? LineArea(middle, end, left) : float2(0.0, 0.0);
}

int CrossingKind(float nearSide, float farSide)
{
    return (nearSide > 0.5 ? 1 : 0) + (farSide > 0.5 ? 2 : 0);
}

// rg: across this pixel's top edge (r: how much this pixel takes from the one above, g: how much
// the one above takes from this). ba: the same across its left edge.
float4 PS_SmaaWeights(VSOut IN) : SV_Target
{
    int2 p = PixelOf(IN.uv);
    float2 e = EdgeAt(p);
    float4 weights = float4(0.0, 0.0, 0.0, 0.0);
    int longest = (int)Params.z;

    [branch]
    if (e.y > 0.5)
    {
        // Walk the top edge left and right until it stops or an edge crosses it.
        int left = 0;
        [loop]
        for (; left < longest; left++)
        {
            int2 q = p + int2(-left, 0);
            if (EdgeAt(q).x > 0.5 || EdgeAt(q + int2(0, -1)).x > 0.5) break;
            if (EdgeAt(q + int2(-1, 0)).y < 0.5) break;
        }
        int right = 0;
        [loop]
        for (; right < longest; right++)
        {
            int2 q = p + int2(right + 1, 0);
            if (EdgeAt(q).x > 0.5 || EdgeAt(q + int2(0, -1)).x > 0.5) break;
            if (EdgeAt(q).y < 0.5) break;
        }
        int2 l = p + int2(-left, 0);
        int2 r = p + int2(right + 1, 0);
        weights.rg = OrthoArea((float)left, (float)right,
            CrossingKind(EdgeAt(l).x, EdgeAt(l + int2(0, -1)).x),
            CrossingKind(EdgeAt(r).x, EdgeAt(r + int2(0, -1)).x));
    }

    [branch]
    if (e.x > 0.5)
    {
        // Walk the left edge up and down.
        int up = 0;
        [loop]
        for (; up < longest; up++)
        {
            int2 q = p + int2(0, -up);
            if (EdgeAt(q).y > 0.5 || EdgeAt(q + int2(-1, 0)).y > 0.5) break;
            if (EdgeAt(q + int2(0, -1)).x < 0.5) break;
        }
        int down = 0;
        [loop]
        for (; down < longest; down++)
        {
            int2 q = p + int2(0, down + 1);
            if (EdgeAt(q).y > 0.5 || EdgeAt(q + int2(-1, 0)).y > 0.5) break;
            if (EdgeAt(q).x < 0.5) break;
        }
        int2 t = p + int2(0, -up);
        int2 b = p + int2(0, down + 1);
        weights.ba = OrthoArea((float)up, (float)down,
            CrossingKind(EdgeAt(t).y, EdgeAt(t + int2(-1, 0)).y),
            CrossingKind(EdgeAt(b).y, EdgeAt(b + int2(-1, 0)).y));
    }
    return weights;
}

// ---------------------------------------------------------------------------------------------
// SMAA 1x: 3) neighbourhood blend
// ---------------------------------------------------------------------------------------------

float4 WeightsAt(int2 p)
{
    if (!Inside(p))
        return float4(0.0, 0.0, 0.0, 0.0);
    return BlendTex.Load(int3(p, 0));
}

float4 PS_SmaaBlend(VSOut IN) : SV_Target
{
    int2 p = PixelOf(IN.uv);
    float4 centre = ColorAt(p);
    float4 own = WeightsAt(p);
    float fromTop = own.r;
    float fromLeft = own.b;
    float fromRight = WeightsAt(p + int2(1, 0)).a;
    float fromBottom = WeightsAt(p + int2(0, 1)).g;
    if (fromTop + fromLeft + fromRight + fromBottom < 1e-5)
        return centre;

    float3 c = ToLinear(centre.rgb);
    float3 mixed;
    if (max(fromLeft, fromRight) > max(fromTop, fromBottom))
    {
        float3 l = lerp(c, ToLinear(ColorAt(p + int2(-1, 0)).rgb), fromLeft);
        float3 r = lerp(c, ToLinear(ColorAt(p + int2(1, 0)).rgb), fromRight);
        mixed = (l * fromLeft + r * fromRight) / (fromLeft + fromRight);
    }
    else
    {
        float3 t = lerp(c, ToLinear(ColorAt(p + int2(0, -1)).rgb), fromTop);
        float3 b = lerp(c, ToLinear(ColorAt(p + int2(0, 1)).rgb), fromBottom);
        mixed = (t * fromTop + b * fromBottom) / (fromTop + fromBottom);
    }
    return float4(ToDisplay(mixed), centre.a);
}
";
}
