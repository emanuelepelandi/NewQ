using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NewQ.App.Video;

namespace NewQ.App.Controls;

/// <summary>The colour effect a slider sets: its track shows what each value does.</summary>
public enum EffectKind { Hue, Saturation, Contrast, Brightness }

/// <summary>
/// Attached to a Slider with the EffectSlider style: paints its track with the effect applied along it (computed with
/// the output's formula, so the track is a true picture of each value) and resets the value on double click.
/// </summary>
public static class EffectTrack
{
    private const int TrackWidth = 256, TrackHeight = 8;
    private static readonly Dictionary<EffectKind, ImageBrush> Brushes = new();

    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached(
        "Kind", typeof(EffectKind?), typeof(EffectTrack), new PropertyMetadata(null, OnKindChanged));

    public static EffectKind? GetKind(DependencyObject d) => (EffectKind?)d.GetValue(KindProperty);
    public static void SetKind(DependencyObject d, EffectKind? value) => d.SetValue(KindProperty, value);

    /// <summary>The neutral value (no effect) a double click goes back to.</summary>
    public static double Neutral(EffectKind kind) => kind is EffectKind.Saturation or EffectKind.Contrast ? 100 : 0;

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider || e.NewValue is not EffectKind kind) return;
        slider.Background = BrushOf(kind);
        slider.MouseDoubleClick -= OnDoubleClick;
        slider.MouseDoubleClick += OnDoubleClick;
    }

    private static void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider { IsEnabled: true } slider && GetKind(slider) is EffectKind kind)
        {
            slider.Value = Neutral(kind);
            e.Handled = true;
        }
    }

    private static ImageBrush BrushOf(EffectKind kind)
    {
        if (Brushes.TryGetValue(kind, out var brush)) return brush;
        var pixels = new byte[TrackWidth * TrackHeight * 4];
        var (min, max) = kind switch
        {
            EffectKind.Hue => (-180.0, 180.0),
            EffectKind.Brightness => (-100.0, 100.0),
            _ => (0.0, 200.0),
        };
        for (var x = 0; x < TrackWidth; x++)
        {
            var v = min + (max - min) * x / (TrackWidth - 1);
            var fx = kind switch
            {
                EffectKind.Hue => new ColorEffects(v, 100, 100, 0, false),
                EffectKind.Saturation => new ColorEffects(0, v, 100, 0, false),
                EffectKind.Contrast => new ColorEffects(0, 100, v, 0, false),
                _ => new ColorEffects(0, 100, 100, v, false),
            };
            for (var y = 0; y < TrackHeight; y++)
            {
                // The sample each track transforms: a saturated red (hue, saturation), two greys (contrast), mid grey.
                var (r, g, b) = kind switch
                {
                    EffectKind.Hue => (0.9f, 0.15f, 0.15f),
                    EffectKind.Saturation => (0.85f, 0.35f, 0.2f),
                    EffectKind.Contrast => y < TrackHeight / 2 ? (0.3f, 0.3f, 0.3f) : (0.7f, 0.7f, 0.7f),
                    _ => (0.5f, 0.5f, 0.5f),
                };
                var c = fx.Apply(r, g, b);
                var i = (y * TrackWidth + x) * 4;
                pixels[i] = (byte)(c.B * 255);
                pixels[i + 1] = (byte)(c.G * 255);
                pixels[i + 2] = (byte)(c.R * 255);
                pixels[i + 3] = 255;
            }
        }
        var bitmap = BitmapSource.Create(TrackWidth, TrackHeight, 96, 96, PixelFormats.Bgra32, null, pixels, TrackWidth * 4);
        bitmap.Freeze();
        brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.Linear);
        brush.Freeze();
        Brushes[kind] = brush;
        return brush;
    }
}
