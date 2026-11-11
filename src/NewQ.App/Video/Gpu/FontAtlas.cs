using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace NewQ.App.Video.Gpu;

/// <summary>
/// The test patterns' glyphs (digits, ×, "FPS") in the interface font, drawn once with anti-aliasing into a
/// texture the shader samples (with mipmaps, so text stays clean at any canvas size). Built on first use.
/// </summary>
internal static class FontAtlas
{
    private const float FontPixels = 120;
    private const int Pad = 24; // between cells, so mip levels don't bleed

    /// <summary>Pieces after the ten digits.</summary>
    private static readonly string[] Extra = { "×", "FPS" };

    public static readonly int Width, Height;
    public static readonly byte[] Rgba;

    /// <summary>HLSL: atlas size, digit band, cell of each glyph (x, width in atlas pixels).</summary>
    public static readonly string Hlsl;

    static FontAtlas()
    {
        using var font = new Font("Segoe UI", FontPixels, FontStyle.Regular, GraphicsUnit.Pixel);
        var format = StringFormat.GenericTypographic;
        using var probe = new Bitmap(1, 1);
        using var measure = Graphics.FromImage(probe);
        measure.TextRenderingHint = TextRenderingHint.AntiAlias;

        float digitW = 0;
        for (var d = 0; d < 10; d++) digitW = Math.Max(digitW, measure.MeasureString(d.ToString(), font, 10000, format).Width);
        var cellW = (int)Math.Ceiling(digitW);
        var widths = new int[10 + Extra.Length];
        for (var i = 0; i < 10; i++) widths[i] = cellW;
        for (var i = 0; i < Extra.Length; i++) widths[10 + i] = (int)Math.Ceiling(measure.MeasureString(Extra[i], font, 10000, format).Width);

        var lineH = (int)Math.Ceiling(font.GetHeight(measure));
        Height = lineH + 2 * Pad;
        var xs = new int[widths.Length];
        var x = Pad;
        for (var i = 0; i < widths.Length; i++) { xs[i] = x; x += widths[i] + Pad; }
        Width = x;

        using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Black);
            g.TextRenderingHint = TextRenderingHint.AntiAlias; // grey coverage, no ClearType colour fringes
            for (var i = 0; i < widths.Length; i++)
            {
                var text = i < 10 ? i.ToString() : Extra[i - 10];
                var w = measure.MeasureString(text, font, 10000, format).Width;
                g.DrawString(text, font, Brushes.White, xs[i] + (widths[i] - w) / 2, Pad, format);
            }
        }

        var data = bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bgra = new byte[Width * Height * 4];
        for (var row = 0; row < Height; row++)
            Marshal.Copy(data.Scan0 + row * data.Stride, bgra, row * Width * 4, Width * 4);
        bitmap.UnlockBits(data);

        // Coverage (the red channel of white on black) in all four channels; the digit band from the "0" cell.
        Rgba = new byte[bgra.Length];
        int top = Height, bottom = 0;
        for (var row = 0; row < Height; row++)
            for (var col = 0; col < Width; col++)
            {
                var v = bgra[(row * Width + col) * 4 + 2];
                Rgba[(row * Width + col) * 4] = Rgba[(row * Width + col) * 4 + 1] = Rgba[(row * Width + col) * 4 + 2] = Rgba[(row * Width + col) * 4 + 3] = v;
                if (v > 127 && col >= xs[0] && col < xs[0] + widths[0]) { top = Math.Min(top, row); bottom = Math.Max(bottom, row + 1); }
            }

        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"#define ATLAS_SIZE float2({Width}, {Height})");
        sb.AppendLine($"#define DIGIT_TOP {top}.0");
        sb.AppendLine($"#define DIGIT_H {bottom - top}.0");
        sb.AppendLine("#define G_TIMES 10");
        sb.AppendLine("#define G_FPS 11");
        sb.Append($"static const float2 GLYPH[{widths.Length}] = {{");
        for (var i = 0; i < widths.Length; i++) sb.Append(i == 0 ? "" : ", ").Append(string.Format(inv, "float2({0}, {1})", xs[i], widths[i]));
        sb.AppendLine("};");
        Hlsl = sb.ToString();
    }
}
