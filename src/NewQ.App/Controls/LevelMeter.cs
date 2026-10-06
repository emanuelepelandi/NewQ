using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using NewQ.Core;

namespace NewQ.App.Controls;

/// <summary>
/// Horizontal peak meter in dBFS: green below −12 dB, yellow up to −3 dB, red up to 0 dBFS, with a peak-hold
/// marker. The scale gives more room to the top of the range, like broadcast meters.
/// </summary>
public sealed class LevelMeter : FrameworkElement
{
    public const double YellowDb = -12;
    public const double RedDb = -3;

    private static readonly Brush Track = Freeze(new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1A)));
    private static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0x7A)));
    private static readonly Brush Yellow = Freeze(new SolidColorBrush(Color.FromRgb(0xE2, 0xC1, 0x4E)));
    private static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x55)));
    private static readonly Brush GreenDim = Freeze(new SolidColorBrush(Color.FromArgb(0x2A, 0x4C, 0xC2, 0x7A)));
    private static readonly Brush YellowDim = Freeze(new SolidColorBrush(Color.FromArgb(0x2A, 0xE2, 0xC1, 0x4E)));
    private static readonly Brush RedDim = Freeze(new SolidColorBrush(Color.FromArgb(0x2A, 0xE0, 0x5A, 0x55)));
    private static readonly Brush SegmentGap = Freeze(new SolidColorBrush(Color.FromArgb(0x90, 0x14, 0x16, 0x1A)));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(Decibels.Floor, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PeakHoldProperty = DependencyProperty.Register(
        nameof(PeakHold), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(Decibels.Floor, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SegmentWidthProperty = DependencyProperty.Register(
        nameof(SegmentWidth), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Current level in dBFS.</summary>
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    /// <summary>Peak-hold marker in dBFS (at the floor = hidden).</summary>
    public double PeakHold { get => (double)GetValue(PeakHoldProperty); set => SetValue(PeakHoldProperty, value); }

    /// <summary>Width of one "LED" segment; 0 for a continuous bar.</summary>
    public double SegmentWidth { get => (double)GetValue(SegmentWidthProperty); set => SetValue(SegmentWidthProperty, value); }

    /// <summary>Position (0–1) of a level on the meter scale.</summary>
    public static double Map(double db)
    {
        db = Math.Clamp(db, Decibels.Floor, 0);
        // Piecewise scale: −60…−40 → 0–15 %, −40…−20 → 15–40 %, −20…−10 → 40–62 %, −10…0 → 62–100 %.
        if (db < -40) return 0.15 * (db + 60) / 20;
        if (db < -20) return 0.15 + 0.25 * (db + 40) / 20;
        if (db < -10) return 0.40 + 0.22 * (db + 20) / 10;
        return 0.62 + 0.38 * (db + 10) / 10;
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 6 : Math.Min(availableSize.Height, 6));

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), 1.5, 1.5);

        var levelX = Level <= Decibels.Floor ? 0 : Map(Level) * w;
        var yellowX = Map(YellowDb) * w;
        var redX = Map(RedDb) * w;

        DrawZone(dc, 0, yellowX, levelX, h, Green, GreenDim);
        DrawZone(dc, yellowX, redX, levelX, h, Yellow, YellowDim);
        DrawZone(dc, redX, w, levelX, h, Red, RedDim);

        if (SegmentWidth > 1)
            for (var x = SegmentWidth; x < w; x += SegmentWidth)
                dc.DrawRectangle(SegmentGap, null, new Rect(x - 0.5, 0, 1, h));

        if (PeakHold > Decibels.Floor)
        {
            var px = Math.Min(w - 2, Map(PeakHold) * w);
            var brush = PeakHold >= RedDb ? Red : PeakHold >= YellowDb ? Yellow : Green;
            dc.DrawRectangle(brush, null, new Rect(px, 0, 2, h));
        }
    }

    private static void DrawZone(DrawingContext dc, double from, double to, double levelX, double h, Brush lit, Brush dim)
    {
        if (to <= from) return;
        var litTo = Math.Clamp(levelX, from, to);
        if (litTo > from) dc.DrawRectangle(lit, null, new Rect(from, 0, litTo - from, h));
        if (to > litTo) dc.DrawRectangle(dim, null, new Rect(litTo, 0, to - litTo, h));
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}

/// <summary>dB labels aligned with <see cref="LevelMeter"/>.</summary>
public sealed class MeterScale : FrameworkElement
{
    private static readonly double[] Marks = { -60, -40, -30, -20, -12, -6, -3, 0 };
    private static readonly Brush Text = new SolidColorBrush(Color.FromRgb(0x8C, 0x91, 0x9B));

    static MeterScale() => Text.Freeze();

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width, 12);

    protected override void OnRender(DrawingContext dc)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI");
        foreach (var mark in Marks)
        {
            var label = mark == 0 ? "0" : mark.ToString(CultureInfo.InvariantCulture);
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 9, Text, dpi);
            var x = LevelMeter.Map(mark) * ActualWidth - text.Width / 2;
            dc.DrawText(text, new Point(Math.Clamp(x, 0, Math.Max(0, ActualWidth - text.Width)), 0));
        }
    }
}
