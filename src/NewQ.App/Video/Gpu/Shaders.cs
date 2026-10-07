namespace NewQ.App.Video.Gpu;

/// <summary>HLSL for the compositor. Compiled once per device at start-up (Shader Model 4.0, any DX10+ GPU).</summary>
internal static class Shaders
{
    public const string Source = @"
Texture2D tex : register(t0);
SamplerState samp : register(s0);

cbuffer Params : register(b0)
{
    float4 p0;          // x = opacity, y = test pattern kind (-1 = texture), z = time (s), w = frame number
    float4 p1;          // xy = canvas size in pixels, z = output index, w = identify outputs (1/0)
    float4 blend;       // soft edge widths: left, right, top, bottom (fraction of the output)
    float4 blendShape;  // x = gamma, y = curve exponent
};

struct VSIn  { float2 pos : POSITION; float2 uv : TEXCOORD0; float2 local : TEXCOORD1; };
struct PSIn  { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float2 local : TEXCOORD1; };

PSIn VSMain(VSIn i)
{
    PSIn o;
    o.pos = float4(i.pos, 0, 1);
    o.uv = i.uv;
    o.local = i.local;
    return o;
}

// ---------------------------------------------------------------- helpers

// 7-segment digit: d in 0..9, p in the digit cell (0..1). Returns 1 inside a lit segment.
float Digit(int d, float2 p)
{
    if (p.x < 0 || p.x > 1 || p.y < 0 || p.y > 1) return 0;
    // segments: a b c d e f g  (bit 0..6)
    int masks[10] = { 0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F };
    int m = masks[clamp(d, 0, 9)];
    float t = 0.14;  // thickness
    float lit = 0;
    if ((m & 1)  && p.y < t && p.x > t && p.x < 1 - t) lit = 1;                              // a
    if ((m & 2)  && p.x > 1 - t && p.y > t && p.y < 0.5) lit = 1;                            // b
    if ((m & 4)  && p.x > 1 - t && p.y > 0.5 && p.y < 1 - t) lit = 1;                        // c
    if ((m & 8)  && p.y > 1 - t && p.x > t && p.x < 1 - t) lit = 1;                          // d
    if ((m & 16) && p.x < t && p.y > 0.5 && p.y < 1 - t) lit = 1;                            // e
    if ((m & 32) && p.x < t && p.y > t && p.y < 0.5) lit = 1;                                // f
    if ((m & 64) && abs(p.y - 0.5) < t / 2 && p.x > t && p.x < 1 - t) lit = 1;               // g
    return lit;
}

// Draws a number (up to 4 digits) inside the rectangle origin..origin+size (in uv units).
float Number(int value, float2 uv, float2 origin, float2 size)
{
    float2 p = (uv - origin) / size;
    if (p.x < 0 || p.x > 1 || p.y < 0 || p.y > 1) return 0;
    int digits = value >= 1000 ? 4 : value >= 100 ? 3 : value >= 10 ? 2 : 1;
    float cell = 1.0 / digits;
    int index = (int)(p.x / cell);
    int power = digits - 1 - index;
    int d = (value / (int)pow(10, power)) % 10;
    float2 q = float2((p.x - index * cell) / cell, p.y);
    q.x = (q.x - 0.1) / 0.8;
    return Digit(d, q);
}

float Line(float coord, float spacing, float widthPx, float pixel)
{
    float m = abs(frac(coord / spacing + 0.5) - 0.5) * spacing;
    return m < widthPx * pixel * 0.5 ? 1 : 0;
}

// ---------------------------------------------------------------- test patterns

float3 Pattern(float2 uv)
{
    int kind = (int)p0.y;
    float2 px = uv * p1.xy;
    float t = p0.z;
    int frame = (int)p0.w;
    float aspect = p1.x / p1.y;

    if (kind == 0) // colour bars (75 %) with grey ramp below
    {
        if (uv.y < 0.67)
        {
            float3 bars[7] = { float3(.75,.75,.75), float3(.75,.75,0), float3(0,.75,.75), float3(0,.75,0),
                               float3(.75,0,.75), float3(.75,0,0), float3(0,0,.75) };
            return bars[min((int)(uv.x * 7), 6)];
        }
        if (uv.y < 0.75) return float3(0, 0, 0);
        return (float3)floor(uv.x * 11) / 10;
    }
    if (kind == 1) // alignment grid: lines, centre cross, circles, red border
    {
        float pixel = 1.0;
        float cell = p1.y / 12;
        float g = max(Line(px.x, cell, 2, pixel), Line(px.y, cell, 2, pixel));
        float2 c = px - p1.xy / 2;
        float cross = (abs(c.x) < 2 || abs(c.y) < 2) ? 1 : 0;
        float r = length(c);
        float circle = abs(r - p1.y * 0.45) < 2 ? 1 : 0;
        float small = abs(r - p1.y * 0.2) < 2 ? 1 : 0;
        float diag = (abs(abs(c.x / aspect) - abs(c.y)) < 2 && abs(c.y) < p1.y * 0.45) ? 1 : 0;
        float border = (px.x < 6 || px.y < 6 || px.x > p1.x - 6 || px.y > p1.y - 6) ? 1 : 0;
        if (border) return float3(1, 0.15, 0.1);
        float3 col = (float3)max(max(g, cross), max(circle, max(small, diag)));
        return lerp(float3(0.02, 0.02, 0.02), float3(1, 1, 1), col);
    }
    if (kind == 2) // checkerboard
    {
        float cell = p1.y / 12;
        int2 k = (int2)floor(px / cell);
        return ((k.x + k.y) & 1) ? float3(1, 1, 1) : float3(0, 0, 0);
    }
    if (kind == 3) // grey ramp (top) and 11 steps (bottom): gamma / linearity / black level
    {
        if (uv.y < 0.5) return (float3)uv.x;
        return (float3)floor(uv.x * 11) / 10;
    }
    if (kind == 4) return float3(1, 1, 1);
    if (kind == 5) return float3(0, 0, 0);
    if (kind == 6) return float3(1, 0, 0);
    if (kind == 7) return float3(0, 1, 0);
    if (kind == 8) return float3(0, 0, 1);
    if (kind == 9) return float3(0.5, 0.5, 0.5);
    if (kind == 10) // motion / smoothness test (Resolume-style)
    {
        float3 col = float3(0.08, 0.09, 0.12);
        // Bars moving at constant speed: judder or dropped frames make them stutter.
        float slow = frac(t * 0.25), fast = frac(t * 0.6);
        if (abs(uv.x - slow) < 0.012 && uv.y < 0.6) col = float3(1, 1, 1);
        if (abs(uv.x - fast) < 0.006 && uv.y < 0.6) col = float3(1, 0.6, 0.1);
        // Square bouncing horizontally.
        float bx = abs(frac(t * 0.35) * 2 - 1) * 0.85 + 0.05;
        float2 sq = float2((uv.x - bx) * aspect, uv.y - 0.72);
        if (abs(sq.x) < 0.05 && abs(sq.y) < 0.05) col = float3(0.3, 0.7, 1);
        // Frame strip: one cell per frame, 60 cells. A skipped cell = a dropped frame.
        if (uv.y > 0.86 && uv.y < 0.94)
        {
            int cellIndex = (int)(uv.x * 60);
            if (cellIndex == frame % 60) col = float3(0.2, 1, 0.3);
            else if (frac(uv.x * 60) < 0.06) col = float3(0.25, 0.25, 0.25);
        }
        // Frame counter.
        if (Number(frame % 10000, uv, float2(0.04, 0.05), float2(0.18, 0.14))) col = float3(1, 1, 1);
        return col;
    }
    return float3(0, 0, 0);
}

// ---------------------------------------------------------------- canvas pass

float4 PSLayer(PSIn i) : SV_TARGET
{
    float a = p0.x;
    float3 c = tex.Sample(samp, i.uv).rgb;
    return float4(c * a, a); // premultiplied alpha
}

float4 PSPattern(PSIn i) : SV_TARGET
{
    float a = p0.x;
    return float4(Pattern(i.uv) * a, a);
}

// ---------------------------------------------------------------- output pass (geometry + edge blend)

// Complementary S-curve: f(t) + f(1-t) = 1, so two overlapping edges always sum to full light.
float Ramp(float t, float curve)
{
    t = saturate(t);
    return t < 0.5 ? 0.5 * pow(2 * t, curve) : 1 - 0.5 * pow(2 * (1 - t), curve);
}

float Edge(float x, float width)
{
    if (width <= 0) return 1;
    float light = Ramp(x / width, blendShape.y);
    return pow(light, 1.0 / blendShape.x); // projector gamma: we want linear light to cross-fade
}

float4 PSOutput(PSIn i) : SV_TARGET
{
    float3 c = tex.Sample(samp, i.uv).rgb;
    float b = Edge(i.local.x, blend.x) * Edge(1 - i.local.x, blend.y)
            * Edge(i.local.y, blend.z) * Edge(1 - i.local.y, blend.w);
    c *= b;
    if (p1.w > 0.5) // identify outputs: big number + coloured frame
    {
        int index = (int)p1.z;
        float3 colors[6] = { float3(1,.3,.3), float3(.3,1,.4), float3(.3,.6,1), float3(1,.9,.2), float3(1,.4,1), float3(.3,1,1) };
        float3 id = colors[index % 6];
        if (i.local.x < 0.015 || i.local.y < 0.025 || i.local.x > 0.985 || i.local.y > 0.975) c = id;
        if (Number(index + 1, i.local, float2(0.38, 0.3), float2(0.24, 0.4))) c = id;
    }
    return float4(c, 1);
}
";
}
