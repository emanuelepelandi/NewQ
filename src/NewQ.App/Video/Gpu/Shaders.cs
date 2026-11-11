namespace NewQ.App.Video.Gpu;

/// <summary>HLSL for the compositor. Compiled once per device at start-up (Shader Model 4.0, any DX10+ GPU).</summary>
internal static class Shaders
{
    public static readonly string Source = FontAtlas.Hlsl + Body;

    private const string Body = @"
Texture2D tex : register(t0);
Texture2D font : register(t1);   // FontAtlas: glyph coverage
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

// ---------------------------------------------------------------- palette (NewQ: neutral greys, one accent)

static const float3 BG      = float3(0.043, 0.047, 0.055);
static const float3 RULE    = float3(0.16, 0.17, 0.19);
static const float3 CELL    = float3(0.10, 0.11, 0.125);
static const float3 LANE    = float3(0.075, 0.08, 0.09);
static const float3 TEXT_LO = float3(0.50, 0.52, 0.56);
static const float3 TEXT_HI = float3(0.93, 0.94, 0.95);
static const float3 ACCENT  = float3(0.36, 0.55, 0.84);
static const float3 ALERT   = float3(0.90, 0.36, 0.33);

// ---------------------------------------------------------------- helpers

// Design size (on a 1080-line canvas) to whole canvas pixels: crisp edges at any resolution.
float Px(float v) { return max(1.0, round(v * p1.y / 1080.0)); }

// ---- text (FontAtlas): y = top of the digits, h = digit height, both in canvas pixels; x advances.

float Piece(inout float x, float2 px, float y, float h, int g)
{
    float sc = h / DIGIT_H;                       // canvas px per atlas px
    float2 r = GLYPH[g];
    float x0 = x;
    x += r.y * sc;
    if (px.x < x0 || px.x >= x) return 0;
    float2 a = float2(r.x, DIGIT_TOP) + (px - float2(x0, y)) / sc;
    if (a.y < 0 || a.y >= ATLAS_SIZE.y) return 0;
    return font.SampleLevel(samp, a / ATLAS_SIZE, max(0, log2(1 / sc))).r;
}

static const int POW10[7] = { 1, 10, 100, 1000, 10000, 100000, 1000000 };

int DigitCount(int v) { return v >= 1000000 ? 7 : v >= 100000 ? 6 : v >= 10000 ? 5 : v >= 1000 ? 4 : v >= 100 ? 3 : v >= 10 ? 2 : 1; }

float DigitAdvance(float h) { return GLYPH[0].y * h / DIGIT_H; }

float PieceWidth(int g, float h) { return GLYPH[g].y * h / DIGIT_H; }

// A number with `digits` digits (zero padded), tabular figures.
float Num(inout float x, float2 px, float y, float h, int value, int digits)
{
    float adv = DigitAdvance(h);
    float x0 = x;
    x += digits * adv;
    if (px.x < x0 || px.x >= x) return 0;
    int i = min((int)((px.x - x0) / adv), digits - 1);
    float gx = x0 + i * adv;
    return Piece(gx, px, y, h, (value / POW10[clamp(digits - 1 - i, 0, 6)]) % 10);
}

// Anti-aliased coverage of a line of width w at distance d.
float Stroke(float d, float w) { return saturate(w * 0.5 + 0.5 - abs(d)); }

// ---------------------------------------------------------------- motion

// Bars crossing at the same speed, each redrawn at its own frame rate (label on the left), and a frame counter.
// A bar that stutters more than its rate implies, or a jump in the counter, is a dropped frame.
float3 Motion(float2 px)
{
    float W = p1.x, H = p1.y;
    int frame = (int)p0.w;
    float t = p0.z;
    float m = Px(96);

    // Frame counter.
    float hc = Px(64);
    float cy = Px(116);
    if (px.y >= cy - Px(8) && px.y < cy + hc + Px(8))
    {
        float x = floor((W - 7 * DigitAdvance(hc)) / 2);
        return lerp(BG, TEXT_HI, Num(x, px, cy, hc, frame % 10000000, 7));
    }

    // Lanes: 60, 50, 30, 25, 24 fps.
    static const float RATES[5] = { 60, 50, 30, 25, 24 };
    float laneH = Px(110), gap = Px(28), top = Px(300);
    int lane = (int)floor((px.y - top) / (laneH + gap));
    float y0 = top + lane * (laneH + gap);
    if (lane < 0 || lane > 4 || px.y >= y0 + laneH) return BG;

    float rate = RATES[lane];
    float hl = Px(26);
    float ly = y0 + floor((laneH - hl) / 2);
    if (px.x < m + Px(220))
    {
        float x = m;
        float c = Num(x, px, ly, hl, (int)rate, 2);
        if (c > 0) return lerp(BG, TEXT_HI, c);
        x += hl * 0.3;
        return lerp(BG, TEXT_LO, Piece(x, px, ly, hl, G_FPS));
    }

    float x0 = m + Px(240), x1 = W - m;
    if (px.x < x0 || px.x >= x1) return BG;
    float barW = Px(24);
    float shown = floor(t * rate) / rate;                       // time of the frame this lane is showing
    float bx = x0 - barW + frac(shown / 3) * (x1 - x0 + barW);  // one crossing every 3 s
    return (px.x >= bx && px.x < bx + barW) ? TEXT_HI : LANE;
}

// ---------------------------------------------------------------- alignment grid

float3 Grid(float2 px)
{
    float W = p1.x, H = p1.y;
    float2 c = floor(float2(W, H) / 2);
    float2 d = px - c;          // from the centre (pixel centres at ±0.5)
    float2 a = abs(d);
    float cell = max(8.0, round(H / 12));
    float half = cell / 2;
    float w = Px(2);

    // Outermost pixel of the raster, and arrows at the middle of each edge.
    if (px.x < 1 || px.y < 1 || px.x > W - 1 || px.y > H - 1) return float3(1, 1, 1);
    float arrow = Px(30);
    if ((px.y < arrow && a.x < arrow - px.y) || (H - px.y < arrow && a.x < arrow - (H - px.y)) ||
        (px.x < arrow && a.y < arrow - px.x) || (W - px.x < arrow && a.y < arrow - (W - px.x)))
        return ALERT;

    // Resolution label in the cell row under the centre.
    float hl = Px(22);
    int cw = (int)W, ch = (int)H;
    float gapX = hl * 0.25;
    float lw = (DigitCount(cw) + DigitCount(ch)) * DigitAdvance(hl) + PieceWidth(G_TIMES, hl) + 2 * gapX, pad = Px(14);
    float ly = c.y + cell + half - floor(hl / 2);
    float lx = c.x - floor(lw / 2);
    if (px.x >= lx - pad && px.x < lx + lw + pad && px.y >= ly - pad && px.y < ly + hl + pad)
    {
        float x = lx;
        float v1 = Num(x, px, ly, hl, cw, DigitCount(cw));
        x += gapX;
        float v2 = Piece(x, px, ly, hl, G_TIMES);
        x += gapX;
        float v3 = Num(x, px, ly, hl, ch, DigitCount(ch));
        return lerp(lerp(float3(0, 0, 0), TEXT_HI, max(v1, v3)), TEXT_LO, v2);
    }

    // Centre cross.
    float cross = Px(3);
    if ((a.x < cross && a.y < cell) || (a.y < cross && a.x < cell)) return float3(1, 1, 1);

    // 1-pixel line patches either side of the centre: scaling, focus, pixel mapping.
    float2 pl = float2(c.x - 3 * cell - half, c.y - half), pr = float2(c.x + 2 * cell + half, c.y - half);
    if (px.y >= pl.y && px.y < pl.y + cell)
    {
        if (px.x >= pl.x && px.x < pl.x + cell) return (float3)(((int)floor(px.x) & 1) ? 1 : 0);
        if (px.x >= pr.x && px.x < pr.x + cell) return (float3)(((int)floor(px.y) & 1) ? 1 : 0);
    }

    float v = 0;
    // Circle touching top and bottom (aspect, geometry), corner circles (corner focus and keystone).
    v = max(v, Stroke(length(d) - (H / 2 - w), w));
    float rc = H / 6;
    float2 corner = float2(min(px.x, W - px.x), min(px.y, H - px.y));
    v = max(v, Stroke(length(corner - rc) - (rc - Px(12)), w * 0.75));
    // Diagonals corner to corner.
    float len = sqrt(W * W + H * H);
    v = max(v, 0.55 * max(Stroke((d.x * H - d.y * W) / len, 1.25), Stroke((d.x * H + d.y * W) / len, 1.25)));
    // Major lines every cell from the centre, minor lines between.
    float mx = fmod(a.x, cell), my = fmod(a.y, cell);
    if (min(mx, cell - mx) < w * 0.5 + 0.01 || min(my, cell - my) < w * 0.5 + 0.01) v = max(v, 0.5);
    float hx = fmod(a.x + half, cell), hy = fmod(a.y + half, cell);
    if (hx < 1 || hy < 1) v = max(v, 0.16);
    // Safe area at 5 % (dashed, accent).
    int2 ip = (int2)floor(px);
    int2 inset = (int2)round(float2(W, H) * 0.05);
    float dash = Px(12);
    bool inX = ip.x >= inset.x && ip.x <= (int)W - 1 - inset.x, inY = ip.y >= inset.y && ip.y <= (int)H - 1 - inset.y;
    bool onX = (ip.x == inset.x || ip.x == (int)W - 1 - inset.x) && inY;
    bool onY = (ip.y == inset.y || ip.y == (int)H - 1 - inset.y) && inX;
    if ((onX && fmod(px.y, 2 * dash) < dash) || (onY && fmod(px.x, 2 * dash) < dash)) return ACCENT;
    return (float3)v;
}

// ---------------------------------------------------------------- colour bars (SMPTE layout, full-range RGB)

float3 Bars(float2 uv)
{
    float b = uv.x * 7;
    int bar = min((int)b, 6);
    if (uv.y < 0.67)
    {
        float3 bars[7] = { float3(.75,.75,.75), float3(.75,.75,0), float3(0,.75,.75), float3(0,.75,0),
                           float3(.75,0,.75), float3(.75,0,0), float3(0,0,.75) };
        return bars[bar];
    }
    if (uv.y < 0.75) // reverse castellations: chroma and phase checks against the bars above
    {
        float3 rev[7] = { float3(0,0,.75), float3(0,0,0), float3(.75,0,.75), float3(0,0,0),
                          float3(0,.75,.75), float3(0,0,0), float3(.75,.75,.75) };
        return rev[bar];
    }
    // -I, 100 % white, +Q, black, PLUGE (0 / 2 / 4 %: only the 4 % strip may show), black.
    if (b < 1.25) return float3(0, 0.129, 0.298);
    if (b < 2.5)  return float3(1, 1, 1);
    if (b < 3.75) return float3(0.196, 0, 0.416);
    if (b < 5)    return float3(0, 0, 0);
    if (b < 5 + 1.0 / 3) return float3(0, 0, 0);
    if (b < 5 + 2.0 / 3) return (float3)0.02;
    if (b < 6)    return (float3)0.04;
    return float3(0, 0, 0);
}

// ---------------------------------------------------------------- grey ramp and steps

// Labelled steps: values from..to in `count` steps across the width, band between y0 and y1 (uv).
float3 Steps(float2 px, float2 uv, float y0, float y1, float from, float to, int count, int labelFrom, int labelStep)
{
    int i = min((int)(uv.x * count), count - 1);
    float v = from + (to - from) * i / (count - 1);
    float H = p1.y;
    float h = Px(20);
    int label = labelFrom + i * labelStep;
    int digits = DigitCount(label);
    float cellW = p1.x / count;
    float x = floor(i * cellW + (cellW - digits * DigitAdvance(h)) / 2);
    float y = floor(y1 * H - h - Px(16));
    float3 ink = (float3)(v < 0.45 ? v + 0.28 : v - 0.28);
    if (px.y < y - Px(4) || px.y >= y + h + Px(4)) return (float3)v;
    return lerp((float3)v, ink, Num(x, px, y, h, label, digits));
}

float3 Ramp(float2 px, float2 uv)
{
    if (uv.y < 0.25) return (float3)uv.x;                                         // continuous: banding, gamma
    if (uv.y < 0.5)  return Steps(px, uv, 0.25, 0.5, 0, 1, 11, 0, 10);            // 0..100 %
    if (uv.y < 0.75) return Steps(px, uv, 0.5, 0.75, 0, 0.10, 11, 0, 1);          // 0..10 %: black level, crush
    return Steps(px, uv, 0.75, 1, 0.90, 1, 11, 90, 1);                            // 90..100 %: clipping
}

// ---------------------------------------------------------------- test patterns

float3 Pattern(float2 uv)
{
    int kind = (int)p0.y;
    float2 px = uv * p1.xy;

    if (kind == 0) return Bars(uv);
    if (kind == 1) return Grid(px);
    if (kind == 2) // checkerboard, square cells from the centre, pixel exact
    {
        float cell = max(2.0, round(p1.y / 12));
        int2 k = (int2)floor((px - floor(p1.xy / 2)) / cell);
        return ((k.x + k.y) & 1) ? float3(0, 0, 0) : float3(1, 1, 1);
    }
    if (kind == 3) return Ramp(px, uv);
    if (kind == 4) return float3(1, 1, 1);
    if (kind == 5) return float3(0, 0, 0);
    if (kind == 6) return float3(1, 0, 0);
    if (kind == 7) return float3(0, 1, 0);
    if (kind == 8) return float3(0, 0, 1);
    if (kind == 9) return float3(0.5, 0.5, 0.5);
    if (kind == 10) return Motion(px);
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
float BlendRamp(float t, float curve)
{
    t = saturate(t);
    return t < 0.5 ? 0.5 * pow(2 * t, curve) : 1 - 0.5 * pow(2 * (1 - t), curve);
}

float Edge(float x, float width)
{
    if (width <= 0) return 1;
    float light = BlendRamp(x / width, blendShape.y);
    return pow(light, 1.0 / blendShape.x); // projector gamma: we want linear light to cross-fade
}

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
