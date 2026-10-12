using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace NewQ.App.Infrastructure;

/// <summary>
/// The colour of each cue type: the design token, or the operator's choice (Impostazioni → Grafica). Badges bind
/// to shared brushes that change colour in place; hand-drawn views (timeline, web remote) listen to <see cref="Changed"/>.
/// </summary>
public static class CueColors
{
    /// <summary>The cue types in the order the settings page lists them: type name, design token.</summary>
    public static readonly IReadOnlyList<(string Type, string Token)> Types = new[]
    {
        ("Audio", "CueAudioColor"),
        ("Video", "CueVideoColor"),
        ("Immagine", "CueImageColor"),
        ("Cattura", "CueLiveColor"),
        ("MIDI", "CueMidiColor"),
        ("Rete", "CueNetworkColor"),
        ("Attesa", "CueWaitColor"),
        ("Fade", "CueFadeColor"),
        ("Stop", "CueStopColor"),
        ("Show", "CueWaitColor"),
    };

    private static readonly Color Fallback = Color.FromRgb(0x9A, 0x9F, 0xA8);
    private static readonly Dictionary<string, Color> Overrides = new();
    private static readonly Dictionary<(string Type, byte Alpha), SolidColorBrush> Brushes = new();

    /// <summary>Raised after the colours change (hand-drawn views redraw).</summary>
    public static event Action? Changed;

    /// <summary>Changes at every <see cref="Apply"/> (caches keyed on it rebuild).</summary>
    public static int Version { get; private set; }

    /// <summary>The design token colour of a type.</summary>
    public static Color DefaultOf(string type)
    {
        var token = "CueWaitColor";
        foreach (var (t, k) in Types)
            if (t == type) { token = k; break; }
        return Application.Current?.TryFindResource(token) is Color c ? c : Fallback;
    }

    /// <summary>The colour in use for a type.</summary>
    public static Color Of(string type) => Overrides.TryGetValue(type, out var c) ? c : DefaultOf(type);

    /// <summary>
    /// A shared brush of the type's colour with the given alpha (255 = text, lower = background tint). Not frozen:
    /// it takes the new colour when the settings change, and every binding using it follows.
    /// </summary>
    public static SolidColorBrush BrushOf(string type, byte alpha)
    {
        if (Brushes.TryGetValue((type, alpha), out var brush)) return brush;
        var c = Of(type);
        return Brushes[(type, alpha)] = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
    }

    /// <summary>Uses the colours of the settings (type → "#RRGGBB"); types missing or invalid keep the token colour.</summary>
    public static void Apply(IReadOnlyDictionary<string, string>? colors)
    {
        Overrides.Clear();
        if (colors is not null)
            foreach (var (type, text) in colors)
                if (TryParse(text, out var c)) Overrides[type] = c;
        foreach (var ((type, alpha), brush) in Brushes)
        {
            var c = Of(type);
            brush.Color = Color.FromArgb(alpha, c.R, c.G, c.B);
        }
        Version++;
        Changed?.Invoke();
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>"#RRGGBB" or "RRGGBB".</summary>
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        var s = text?.Trim().TrimStart('#') ?? "";
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        color = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }
}
