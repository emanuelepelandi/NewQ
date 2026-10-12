using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NewQ.App.Infrastructure;
using NewQ.Core;

namespace NewQ.App.Views.Setup;

/// <summary>Colours of the cue types. Works on a copy: the settings window applies it on OK.</summary>
public partial class GraphicsPanel : UserControl
{
    public sealed class Row : ObservableObject
    {
        private string _hex;
        private Color _color;

        public Row(string type, Color color)
        {
            Type = type;
            Label = type == "Show" ? "Show (START / END)" : type;
            _color = color;
            _hex = CueColors.ToHex(color);
        }

        public string Type { get; }
        public string Label { get; }
        public Color Color => _color;
        public bool IsValid => CueColors.TryParse(_hex, out _);
        public Brush Solid => Frozen(_color, 255);
        public Brush Tint => Frozen(_color, 40); // as the badges of the cue list

        /// <summary>The typed colour; the preview follows as soon as it is valid.</summary>
        public string Hex
        {
            get => _hex;
            set
            {
                if (!SetField(ref _hex, value)) return;
                if (!CueColors.TryParse(value, out var c) || c == _color) return;
                _color = c;
                OnPropertyChanged(nameof(Solid));
                OnPropertyChanged(nameof(Tint));
            }
        }

        public void Set(Color c) => Hex = CueColors.ToHex(c);

        private static Brush Frozen(Color c, byte alpha)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        }
    }

    public sealed record Swatch(Brush Brush, string Hex, Color Color);

    private List<Row> _rows = new();
    private Row? _paletteRow;

    public GraphicsPanel()
    {
        InitializeComponent();
        Swatches.ItemsSource = BuildPalette();
    }

    public void Initialize(bool timelineCursors)
    {
        ProgressBox.SelectedIndex = timelineCursors ? 1 : 0;
        _rows = CueColors.Types.Select(t => new Row(t.Type, CueColors.Of(t.Type))).ToList();
        Rows.ItemsSource = _rows;
    }

    /// <summary>Timeline progress as cursors instead of filled bars.</summary>
    public bool TimelineCursors => ProgressBox.SelectedIndex == 1;

    /// <summary>The colours that differ from the defaults (type → "#RRGGBB"); throws on an invalid value.</summary>
    public Dictionary<string, string> Result()
    {
        var result = new Dictionary<string, string>();
        foreach (var row in _rows)
        {
            if (!row.IsValid) throw new FormatException($"Colore {row.Label}: valore non valido.");
            if (row.Color != CueColors.DefaultOf(row.Type)) result[row.Type] = CueColors.ToHex(row.Color);
        }
        return result;
    }

    private void OnResetOne(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Row row) row.Set(CueColors.DefaultOf(row.Type));
    }

    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.Set(CueColors.DefaultOf(row.Type));
    }

    private void OnPalette(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Row row } button) return;
        _paletteRow = row;
        Palette.PlacementTarget = button;
        Palette.IsOpen = true;
    }

    private void OnSwatch(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Swatch swatch) _paletteRow?.Set(swatch.Color);
        Palette.IsOpen = false;
    }

    /// <summary>Soft tones like the default ones: 12 hues in three shades, then greys.</summary>
    private static List<Swatch> BuildPalette()
    {
        var colors = new List<Color>();
        foreach (var (s, l) in new[] { (0.60, 0.74), (0.55, 0.64), (0.45, 0.52) })
            for (var hue = 0; hue < 360; hue += 30) colors.Add(FromHsl(hue, s, l));
        foreach (var l in new[] { 0.86, 0.76, 0.66, 0.56, 0.46, 0.36 }) colors.Add(FromHsl(215, 0.08, l));
        return colors.Select(c =>
        {
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return new Swatch(brush, CueColors.ToHex(c), c);
        }).ToList();
    }

    private static Color FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        static byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return Color.FromRgb(B(r + m), B(g + m), B(b + m));
    }
}
