using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NewQ.App.Video;
using NewQ.Core.Model;

namespace NewQ.App.Controls;

/// <summary>
/// The picture of a video or image cue with its colour effects applied (same formula as the output), redrawn as the
/// sliders move. Uses the cue's preview frame; when there is none or it is almost black (many videos start black), a
/// reference picture of colour bars and a grey ramp.
/// </summary>
public sealed class EffectsPreview : Image
{
    public const int PreviewWidth = 192, PreviewHeight = 108;

    /// <summary>The same sizes for XAML (Width and Height are doubles).</summary>
    public static double FrameWidth => PreviewWidth;
    public static double FrameHeight => PreviewHeight;

    /// <summary>The preview frame of a cue (set by the app: the timeline thumbnails).</summary>
    public static Func<VisualCue, BitmapSource?> SourceOf { get; set; } = _ => null;

    /// <summary>Raised by the app when new preview frames are available.</summary>
    public static event Action? SourcesChanged;
    public static void RaiseSourcesChanged() => SourcesChanged?.Invoke();

    private static readonly ConditionalWeakTable<BitmapSource, Pixels> Cache = new();
    private static Pixels? _bars;

    public static readonly DependencyProperty CueProperty = DependencyProperty.Register(
        nameof(Cue), typeof(VisualCue), typeof(EffectsPreview), new PropertyMetadata(null, OnCueChanged));

    private sealed record Pixels(byte[] Data, int Width, int Height);

    private WriteableBitmap? _target;
    private byte[]? _buffer;
    private bool _usesBars;

    public EffectsPreview()
    {
        Stretch = Stretch.Uniform;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Loaded += (_, _) =>
        {
            SourcesChanged -= Redraw;
            SourcesChanged += Redraw;
            Watch(null, Cue);
            Redraw();
        };
        Unloaded += (_, _) =>
        {
            SourcesChanged -= Redraw;
            Watch(Cue, null);
        };
    }

    public VisualCue? Cue { get => (VisualCue?)GetValue(CueProperty); set => SetValue(CueProperty, value); }

    /// <summary>True when the picture is the reference bars (no usable frame of the cue).</summary>
    public bool UsesBars => _usesBars;

    private static void OnCueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var preview = (EffectsPreview)d;
        if (!preview.IsLoaded) return;
        preview.Watch(e.OldValue as VisualCue, e.NewValue as VisualCue);
        preview.Redraw();
    }

    private void Watch(VisualCue? old, VisualCue? now)
    {
        if (old is not null) old.PropertyChanged -= OnCuePropertyChanged;
        if (now is not null) { now.PropertyChanged -= OnCuePropertyChanged; now.PropertyChanged += OnCuePropertyChanged; }
    }

    private void OnCuePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaCue.FilePath) || (e.PropertyName is string p && VisualCue.LookProperties.Contains(p)))
            Redraw();
    }

    private void Redraw()
    {
        if (Cue is not VisualCue cue) return;
        var source = Usable(SourceOf(cue));
        _usesBars = source is null;
        source ??= _bars ??= Bars();

        if (_target is null || _target.PixelWidth != source.Width || _target.PixelHeight != source.Height)
        {
            _target = new WriteableBitmap(source.Width, source.Height, 96, 96, PixelFormats.Bgra32, null);
            _buffer = new byte[source.Data.Length];
            Source = _target;
        }

        var fx = new ColorEffects(cue.Hue, cue.Saturation, cue.Contrast, cue.Brightness, cue.InvertColors);
        var input = source.Data;
        var output = _buffer!;
        const float scale = 1f / 255;
        for (var i = 0; i < input.Length; i += 4)
        {
            var c = fx.Apply(input[i + 2] * scale, input[i + 1] * scale, input[i] * scale);
            output[i] = (byte)(c.B * 255 + 0.5f);
            output[i + 1] = (byte)(c.G * 255 + 0.5f);
            output[i + 2] = (byte)(c.R * 255 + 0.5f);
            output[i + 3] = 255;
        }
        _target.WritePixels(new Int32Rect(0, 0, source.Width, source.Height), output, source.Width * 4, 0);
    }

    /// <summary>The frame's pixels (cached), or null when there is none or it is almost black.</summary>
    private static Pixels? Usable(BitmapSource? frame)
    {
        if (frame is null) return null;
        var pixels = Cache.GetValue(frame, Read);
        long sum = 0;
        for (var i = 0; i < pixels.Data.Length; i += 4) sum += pixels.Data[i] + pixels.Data[i + 1] + pixels.Data[i + 2];
        return sum / (pixels.Data.Length / 4 * 3.0) < 10 ? null : pixels;
    }

    private static Pixels Read(BitmapSource frame)
    {
        BitmapSource bgra = frame.Format == PixelFormats.Bgra32 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var data = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(data, bgra.PixelWidth * 4, 0);
        return new Pixels(data, bgra.PixelWidth, bgra.PixelHeight);
    }

    /// <summary>Reference picture: six colour bars over a grey ramp from black to white.</summary>
    private static Pixels Bars()
    {
        const int w = PreviewWidth, h = PreviewHeight;
        var data = new byte[w * h * 4];
        (byte R, byte G, byte B)[] bars =
        {
            (230, 60, 50), (235, 190, 40), (70, 180, 80), (50, 170, 200), (60, 90, 210), (190, 70, 180),
        };
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                (byte R, byte G, byte B) c;
                if (y < h * 2 / 3) c = bars[x * bars.Length / w];
                else
                {
                    var v = (byte)(x * 255 / (w - 1));
                    c = (v, v, v);
                }
                data[i] = c.B;
                data[i + 1] = c.G;
                data[i + 2] = c.R;
                data[i + 3] = 255;
            }
        return new Pixels(data, w, h);
    }
}
