using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NewQ.App.Video.Gpu;

/// <summary>HLSL for the compositor. Compiled once per device at start-up (Shader Model 4.0, any DX10+ GPU).</summary>
internal static class Shaders
{
    /// <summary>5×7 bitmap font of the test patterns (upper case, digits, a few signs; 'x' = multiplication sign).</summary>
    /// <remarks>Seven rows of five dots, top to bottom.</remarks>
    private static readonly Dictionary<char, string> Font = new()
    {
        ['0'] = ".###. #...# #...# #...# #...# #...# .###.",
        ['1'] = "..#.. .##.. ..#.. ..#.. ..#.. ..#.. .###.",
        ['2'] = ".###. #...# ....# ...#. ..#.. .#... #####",
        ['3'] = "##### ...#. ..#.. ...#. ....# #...# .###.",
        ['4'] = "...#. ..##. .#.#. #..#. ##### ...#. ...#.",
        ['5'] = "##### #.... ####. ....# ....# #...# .###.",
        ['6'] = "..##. .#... #.... ####. #...# #...# .###.",
        ['7'] = "##### ....# ...#. ..#.. .#... .#... .#...",
        ['8'] = ".###. #...# #...# .###. #...# #...# .###.",
        ['9'] = ".###. #...# #...# .#### ....# ...#. .##..",
        ['A'] = ".###. #...# #...# ##### #...# #...# #...#",
        ['B'] = "####. #...# #...# ####. #...# #...# ####.",
        ['C'] = ".###. #...# #.... #.... #.... #...# .###.",
        ['D'] = "###.. #..#. #...# #...# #...# #..#. ###..",
        ['E'] = "##### #.... #.... ####. #.... #.... #####",
        ['F'] = "##### #.... #.... ####. #.... #.... #....",
        ['G'] = ".###. #...# #.... #.### #...# #...# .####",
        ['H'] = "#...# #...# #...# ##### #...# #...# #...#",
        ['I'] = ".###. ..#.. ..#.. ..#.. ..#.. ..#.. .###.",
        ['J'] = "..### ...#. ...#. ...#. ...#. #..#. .##..",
        ['K'] = "#...# #..#. #.#.. ##... #.#.. #..#. #...#",
        ['L'] = "#.... #.... #.... #.... #.... #.... #####",
        ['M'] = "#...# ##.## #.#.# #.#.# #...# #...# #...#",
        ['N'] = "#...# #...# ##..# #.#.# #..## #...# #...#",
        ['O'] = ".###. #...# #...# #...# #...# #...# .###.",
        ['P'] = "####. #...# #...# ####. #.... #.... #....",
        ['Q'] = ".###. #...# #...# #...# #.#.# #..#. .##.#",
        ['R'] = "####. #...# #...# ####. #.#.. #..#. #...#",
        ['S'] = ".#### #.... #.... .###. ....# ....# ####.",
        ['T'] = "##### ..#.. ..#.. ..#.. ..#.. ..#.. ..#..",
        ['U'] = "#...# #...# #...# #...# #...# #...# .###.",
        ['V'] = "#...# #...# #...# #...# #...# .#.#. ..#..",
        ['W'] = "#...# #...# #...# #.#.# #.#.# #.#.# .#.#.",
        ['X'] = "#...# #...# .#.#. ..#.. .#.#. #...# #...#",
        ['Y'] = "#...# #...# .#.#. ..#.. ..#.. ..#.. ..#..",
        ['Z'] = "##### ....# ...#. ..#.. .#... #.... #####",
        [':'] = "..... .##.. .##.. ..... .##.. .##.. .....",
        ['.'] = "..... ..... ..... ..... ..... .##.. .##..",
        ['/'] = "....# ....# ...#. ..#.. .#... #.... #....",
        ['%'] = "##..# ##..# ...#. ..#.. .#... #..## #..##",
        ['-'] = "..... ..... ..... .###. ..... ..... .....",
        ['+'] = "..... ..#.. ..#.. ##### ..#.. ..#.. .....",
        ['x'] = "..... ..... #...# .#.#. ..#.. .#.#. #...#",
    };

    /// <summary>Fixed texts of the patterns: each becomes "#define T_NAME start, length" into TEXT[].</summary>
    private static readonly (string Name, string Text)[] Texts =
    {
        ("MOTION", "MOTION"), ("PACING", "FRAME PACING"), ("HZ", " HZ"), ("NOHZ", "--.-- HZ"), ("X", "x"),
        ("COLON", ":"), ("DOT", "."), ("FRAME", "FRAME "), ("DROPPED", "DROPPED "), ("PXF", " PX/F"),
        ("GAP", "    "), ("LEGEND1", "LANES: FIXED STEP EVERY FRAME"), ("LEGEND2", "STRIP: ONE CELL PER REFRESH"),
    };

    public static readonly string Source = BuildFont() + Body;

    private static string BuildFont()
    {
        var sb = new StringBuilder();
        sb.Append("static const uint2 FONT[128] = {");
        for (var c = 0; c < 128; c++)
        {
            uint lo = 0, hi = 0;
            if (Font.TryGetValue((char)c, out var rows))
            {
                rows = rows.Replace(" ", "");
                if (rows.Length != 35) throw new System.InvalidOperationException($"Font: glyph '{(char)c}'");
                for (var bit = 0; bit < 35; bit++)
                    if (rows[bit] == '#')
                    {
                        if (bit < 20) lo |= 1u << bit;
                        else hi |= 1u << (bit - 20);
                    }
            }
            sb.Append(c == 0 ? "" : ",").Append($"uint2({lo}u,{hi}u)");
        }
        sb.AppendLine("};");

        var codes = new List<int>();
        foreach (var (name, text) in Texts)
        {
            sb.AppendLine($"#define T_{name} {codes.Count}, {text.Length}");
            codes.AddRange(text.Select(ch => (int)ch));
        }
        sb.AppendLine($"static const uint TEXT[{codes.Count}] = {{{string.Join(",", codes)}}};");
        return sb.ToString();
    }

    private const string Body = @"
Texture2D tex : register(t0);
SamplerState samp : register(s0);

cbuffer Params : register(b0)
{
    float4 p0;          // x = opacity, y = test pattern kind (-1 = texture), z = time (s), w = frame number
    float4 p1;          // xy = canvas size in pixels, z = output index, w = identify outputs (1/0)
    float4 blend;       // soft edge widths: left, right, top, bottom (fraction of the output)
    float4 blendShape;  // x = gamma, y = curve exponent
    float4 p2;          // x = measured refresh (Hz, 0 = not yet), y = dropped frames since the pattern appeared
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
static const float3 OK      = float3(0.40, 0.78, 0.52);
static const float3 ALERT   = float3(0.90, 0.36, 0.33);

// ---------------------------------------------------------------- helpers

// Design size (on a 1080-line canvas) to whole canvas pixels: crisp edges at any resolution.
float Px(float v) { return max(1.0, round(v * p1.y / 1080.0)); }

float Glyph(uint code, int2 cell)
{
    if (cell.x < 0 || cell.x > 4 || cell.y < 0 || cell.y > 6) return 0;
    uint2 g = FONT[code & 127];
    int bit = cell.y * 5 + cell.x;
    return bit < 20 ? (float)((g.x >> bit) & 1) : (float)((g.y >> (bit - 20)) & 1);
}

// Text from TEXT[start..start+len) with its top-left at (x, y), one font dot = dot pixels; x advances.
float Run(inout float x, float2 px, float y, float dot, int start, int len)
{
    float2 p = (px - float2(x, y)) / dot;
    x += len * 6 * dot;
    if (p.y < 0 || p.y >= 7 || p.x < 0 || p.x >= len * 6) return 0;
    int i = (int)(p.x / 6);
    return Glyph(TEXT[start + i], int2((int)p.x - i * 6, (int)p.y));
}

static const int POW10[7] = { 1, 10, 100, 1000, 10000, 100000, 1000000 };

int DigitCount(int v) { return v >= 1000000 ? 7 : v >= 100000 ? 6 : v >= 10000 ? 5 : v >= 1000 ? 4 : v >= 100 ? 3 : v >= 10 ? 2 : 1; }

// A number with `digits` digits (zero padded), like Run.
float RunNum(inout float x, float2 px, float y, float dot, int value, int digits)
{
    float2 p = (px - float2(x, y)) / dot;
    x += digits * 6 * dot;
    if (p.y < 0 || p.y >= 7 || p.x < 0 || p.x >= digits * 6) return 0;
    int i = (int)(p.x / 6);
    int power = POW10[clamp(digits - 1 - i, 0, 6)];
    return Glyph(48 + (uint)((value / power) % 10), int2((int)p.x - i * 6, (int)p.y));
}

// Like RunNum, each font dot drawn as a rounded square (large sizes): anti-aliased coverage 0..1.
float RunNumRound(inout float x, float2 px, float y, float dot, int value, int digits)
{
    float2 p = (px - float2(x, y)) / dot;
    x += digits * 6 * dot;
    if (p.y < 0 || p.y >= 7 || p.x < 0 || p.x >= digits * 6) return 0;
    int i = (int)(p.x / 6);
    int power = POW10[clamp(digits - 1 - i, 0, 6)];
    if (Glyph(48 + (uint)((value / power) % 10), int2((int)p.x - i * 6, (int)p.y)) == 0) return 0;
    float2 q = abs(frac(p) - 0.5) - 0.22;
    return saturate(0.5 - (length(max(q, 0)) - 0.2) * dot);
}

// Rounded-dot colon (two dots in the middle column).
float ColonRound(inout float x, float2 px, float y, float dot)
{
    float2 p = (px - float2(x, y)) / dot;
    x += 6 * dot;
    if (p.x < 2 || p.x >= 3 || !((p.y >= 2 && p.y < 3) || (p.y >= 4 && p.y < 5))) return 0;
    float2 q = abs(frac(p) - 0.5) - 0.22;
    return saturate(0.5 - (length(max(q, 0)) - 0.2) * dot);
}

// Width in pixels of n characters.
float TextWidth(int n, float dot) { return (n * 6 - 1) * dot; }

// Anti-aliased coverage of a line of width w at distance d.
float Stroke(float d, float w) { return saturate(w * 0.5 + 0.5 - abs(d)); }

// ---------------------------------------------------------------- motion / frame pacing

float3 Motion(float2 px)
{
    float W = p1.x, H = p1.y;
    int frame = (int)p0.w;
    float t = p0.z;
    float hz = p2.x;
    int drops = (int)p2.y;
    int fps = hz > 1 ? (int)round(hz) : 60;
    float m = Px(56);

    // The raster's last pixel on every side: the whole picture must be visible.
    if (px.x < 1 || px.y < 1 || px.x > W - 1 || px.y > H - 1) return RULE * 2;

    // Header: name on the left, canvas size and measured refresh on the right.
    float d1 = Px(4);
    float y = m;
    float x = m;
    if (px.y >= y && px.y < y + 7 * d1)
    {
    if (Run(x, px, y, d1, T_MOTION) > 0) return TEXT_HI;
    x += 2 * 6 * d1;
    if (Run(x, px, y, d1, T_PACING) > 0) return TEXT_LO;

    int cw = (int)W, ch = (int)H;
    int hz100 = (int)round(hz * 100);
    int n = DigitCount(cw) + 1 + DigitCount(ch) + 4 + (hz > 1 ? DigitCount(hz100 / 100) + 6 : 8);
    x = W - m - TextWidth(n, d1);
    if (RunNum(x, px, y, d1, cw, DigitCount(cw)) > 0) return TEXT_LO;
    if (Run(x, px, y, d1, T_X) > 0) return TEXT_LO;
    if (RunNum(x, px, y, d1, ch, DigitCount(ch)) > 0) return TEXT_LO;
    if (Run(x, px, y, d1, T_GAP) > 0) return TEXT_LO;
    if (hz > 1)
    {
        if (RunNum(x, px, y, d1, hz100 / 100, DigitCount(hz100 / 100)) > 0) return TEXT_HI;
        if (Run(x, px, y, d1, T_DOT) > 0) return TEXT_HI;
        if (RunNum(x, px, y, d1, hz100 % 100, 2) > 0) return TEXT_HI;
        if (Run(x, px, y, d1, T_HZ) > 0) return TEXT_LO;
    }
    else if (Run(x, px, y, d1, T_NOHZ) > 0) return TEXT_LO;
    return BG;
    }

    float ruleY = m + 7 * d1 + Px(24);
    if (px.y >= ruleY && px.y < ruleY + 1 && px.x >= m && px.x < W - m) return RULE;

    // Timecode of the shared clock: identical on every output (sync between screens and projectors).
    int ffDigits = fps > 100 ? 3 : 2;
    int tcChars = 9 + ffDigits;
    float dTc = max(1.0, floor(min(Px(17), (W - 2 * m) / (tcChars * 6))));
    float tcY = Px(196);
    if (px.y >= tcY && px.y < tcY + 7 * dTc)
    {
        int total = (int)floor(t);
        int ff = min((int)floor(frac(t) * fps), fps - 1);
        x = floor((W - TextWidth(tcChars, dTc)) / 2);
        float cv = RunNumRound(x, px, tcY, dTc, (total / 3600) % 100, 2);
        if (cv > 0) return lerp(BG, TEXT_HI, cv);
        cv = ColonRound(x, px, tcY, dTc);                                    if (cv > 0) return lerp(BG, TEXT_LO, cv);
        cv = RunNumRound(x, px, tcY, dTc, (total / 60) % 60, 2);             if (cv > 0) return lerp(BG, TEXT_HI, cv);
        cv = ColonRound(x, px, tcY, dTc);                                    if (cv > 0) return lerp(BG, TEXT_LO, cv);
        cv = RunNumRound(x, px, tcY, dTc, total % 60, 2);                    if (cv > 0) return lerp(BG, TEXT_HI, cv);
        cv = ColonRound(x, px, tcY, dTc);                                    if (cv > 0) return lerp(BG, TEXT_LO, cv);
        cv = RunNumRound(x, px, tcY, dTc, ff, ffDigits);                     if (cv > 0) return lerp(BG, ACCENT, cv);
        return BG;
    }

    // Frame counter and dropped frames (measured on this screen since the pattern appeared).
    float d2 = Px(3);
    float sy = tcY + 7 * dTc + Px(44);
    int dropDigits = DigitCount(drops);
    if (px.y >= sy && px.y < sy + 7 * d2)
    {
    x = floor((W - TextWidth(6 + 7 + 4 + 8 + dropDigits, d2)) / 2);
    if (Run(x, px, sy, d2, T_FRAME) > 0) return TEXT_LO;
    if (RunNum(x, px, sy, d2, frame % 10000000, 7) > 0) return TEXT_HI;
    if (Run(x, px, sy, d2, T_GAP) > 0) return TEXT_LO;
    if (Run(x, px, sy, d2, T_DROPPED) > 0) return TEXT_LO;
    if (RunNum(x, px, sy, d2, drops, dropDigits) > 0) return drops == 0 ? OK : ALERT;
    return BG;
    }

    // Three lanes, blocks advancing a fixed step every frame (4, 8, 16 px on 1080 lines):
    // even motion = every refresh got a new frame; a hitch = a frame dropped or shown twice.
    float laneH = Px(80), laneGap = Px(24), laneTop = Px(500);
    float trackX0 = m + Px(176), trackX1 = W - m;
    float blockW = Px(88);
    int lane = (int)floor((px.y - laneTop) / (laneH + laneGap));
    float y0 = laneTop + lane * (laneH + laneGap);
    if (lane >= 0 && lane < 3 && px.y < y0 + laneH)
    {
        int speed = (int)Px(4) << lane;
        x = m;
        float ly = y0 + floor((laneH - 7 * d2) / 2);
        if (RunNum(x, px, ly, d2, speed, DigitCount(speed)) > 0) return TEXT_HI;
        if (Run(x, px, ly, d2, T_PXF) > 0) return TEXT_LO;
        if (px.x < trackX0 || px.x >= trackX1) return BG;

        int period = (int)(trackX1 - trackX0 + blockW);
        float bx = trackX0 - blockW + (float)(((frame % period) * speed) % period);
        float local = px.x - bx;
        if (local >= 0 && local < blockW)
        {
            float mid = floor(blockW / 2);
            return (local >= mid - Px(1) && local < mid + Px(1)) ? BG : TEXT_HI;
        }
        // Ruler ticks every 32 px at the lane edges: a still photo shows the step between frames.
        float tick = Px(32);
        float ty = px.y - y0;
        if (fmod(px.x - trackX0, tick) < 1 && (ty < Px(10) || ty >= laneH - Px(10))) return RULE * 1.6;
        return LANE;
    }

    // Frame strip: one cell per refresh of the last second, a lit cell walking with a short trail.
    // A camera at 1/15 s or longer sees a run of cells: a gap or a doubled cell is a skipped or repeated frame.
    float stripY = Px(856), stripH = Px(56);
    int cells = clamp(fps, 24, 240);
    float cellW = (W - 2 * m) / cells;
    if (px.y >= stripY && px.y < stripY + stripH && px.x >= m && px.x < W - m)
    {
        int idx = min((int)((px.x - m) / cellW), cells - 1);
        float inCell = px.x - m - idx * cellW;
        float gap = cellW >= 8 ? 2 : cellW >= 4 ? 1 : 0;
        if (inCell >= cellW - gap) return BG;
        int age = (frame % cells - idx + cells) % cells;
        if (age == 0) return TEXT_HI;
        if (age <= 3) return lerp(CELL, ACCENT, 1.0 - age * 0.25);
        return CELL;
    }
    // Cell numbers every 10 cells.
    float ny = stripY + stripH + Px(14);
    float d3 = Px(2);
    if (px.y >= ny && px.y < ny + 7 * d3 && px.x >= m && px.x < W - m)
    {
        int k = (int)((px.x - m) / (10 * cellW));
        x = m + k * 10 * cellW;
        if (RunNum(x, px, ny, d3, k * 10, DigitCount(k * 10)) > 0) return TEXT_LO;
    }

    // Footer: how to read the lanes and the strip.
    float footY = H - m - 7 * d3;
    if (px.y >= footY - Px(24) && px.y < footY - Px(24) + 1 && px.x >= m && px.x < W - m) return RULE;
    if (px.y < footY || px.y >= footY + 7 * d3) return BG;
    x = m;
    if (Run(x, px, footY, d3, T_LEGEND1) > 0) return TEXT_LO;
    x = W - m - TextWidth(27, d3);
    if (Run(x, px, footY, d3, T_LEGEND2) > 0) return TEXT_LO;
    return BG;
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
    float dl = Px(3);
    int cw = (int)W, ch = (int)H;
    int n = DigitCount(cw) + 1 + DigitCount(ch);
    float lw = TextWidth(n, dl), pad = Px(12);
    float ly = c.y + cell + half - floor(7 * dl / 2);
    float lx = c.x - floor(lw / 2);
    if (px.x >= lx - pad && px.x < lx + lw + pad && px.y >= ly - pad && px.y < ly + 7 * dl + pad)
    {
        float x = lx;
        if (RunNum(x, px, ly, dl, cw, DigitCount(cw)) > 0) return TEXT_HI;
        if (Run(x, px, ly, dl, T_X) > 0) return TEXT_LO;
        if (RunNum(x, px, ly, dl, ch, DigitCount(ch)) > 0) return TEXT_HI;
        return float3(0, 0, 0);
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
    float d = Px(3);
    int label = labelFrom + i * labelStep;
    int digits = DigitCount(label);
    float cellW = p1.x / count;
    float x = floor(i * cellW + (cellW - TextWidth(digits, d)) / 2);
    float y = floor(y1 * H - 7 * d - Px(14));
    float3 ink = (float3)(v < 0.45 ? v + 0.28 : v - 0.28);
    if (RunNum(x, px, y, d, label, digits) > 0) return ink;
    return (float3)v;
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

// Output number for 'identify outputs', in the output's own 0..1 space (any size, any geometry): rounded dots,
// anti-aliased from the real pixel footprint.
float IdNumber(int value, float2 local)
{
    int digits = DigitCount(value);
    float dot = 0.4 / 7;
    float2 scale = float2(dot * 0.6, dot);
    float2 origin = float2(0.5 - (digits * 6 - 1) * scale.x * 0.5, 0.3);
    float2 p = (local - origin) / scale;
    float aa = max(fwidth(p.y), 1e-4);
    if (p.y < 0 || p.y >= 7 || p.x < 0 || p.x >= digits * 6 - 1) return 0;
    int i = (int)(p.x / 6);
    int power = POW10[clamp(digits - 1 - i, 0, 6)];
    if (Glyph(48 + (uint)((value / power) % 10), int2((int)p.x - i * 6, (int)p.y)) == 0) return 0;
    float2 q = abs(frac(p) - 0.5) - 0.22;
    return saturate(0.5 - (length(max(q, 0)) - 0.2) / aa);
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
        c = lerp(c, id, IdNumber(index + 1, i.local));
    }
    return float4(c, 1);
}
";
}
