using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using NewQ.App.Audio;
using NewQ.App.Infrastructure;
using NewQ.Core;
using NewQ.Core.Model;

namespace NewQ.App.Controls;

/// <summary>
/// Start, end and fades of an audio or video cue over the waveform of its file. Drag the start and end bars (grips at
/// the bottom), and the fade handles at the top (Ctrl = precision drag); arrows nudge the selected handle by 0.01 s (Shift = 0.1 s), Canc
/// resets it. Ctrl+wheel zooms around the mouse, the wheel scrolls a zoomed view, double click shows the whole file.
/// While the cue runs, a green line shows where it is. Writes the cue directly, like the other inspector fields.
/// </summary>
public sealed class TrimEditor : FrameworkElement
{
    private const double RulerHeight = 18;
    private const double GripSize = 7;
    private const double HitRange = 7;
    private const double MinRegion = 0.1;      // seconds between start and end
    private const double MinView = 0.25;       // seconds shown at most zoom

    private enum Handle { Start, End, FadeIn, FadeOut }

    // Frozen brushes mirroring the design tokens (InsetBrush, BorderSubtle, TextMuted, Accent, Success, Window).
    private static readonly Brush Background = Freeze(new SolidColorBrush(Color.FromRgb(0x0D, 0x0F, 0x12)));
    private static readonly Brush RulerFill = Freeze(new SolidColorBrush(Color.FromRgb(0x16, 0x19, 0x1E)));
    private static readonly Pen TickPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x37, 0x3C, 0x46)), 1));
    private static readonly Pen CenterPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x26, 0x2A, 0x31)), 1));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x7A, 0x81, 0x8D)));
    private static readonly Brush ValueBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE4, 0xE7, 0xEC)));
    private static readonly Brush OutsideFill = Freeze(new SolidColorBrush(Color.FromArgb(0xB4, 0x11, 0x13, 0x17)));
    private static readonly Brush Accent = Freeze(new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xD6)));
    private static readonly Pen AccentPen = Freeze(new Pen(Accent, 2));
    private static readonly Brush Selected = Freeze(new SolidColorBrush(Colors.White));
    private static readonly Pen EnvelopePen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xD0, 0xE4, 0xE7, 0xEC)), 1.5));
    private static readonly Brush FadeShade = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0x11, 0x13, 0x17)));
    private static readonly Pen PlayPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x4C, 0xB2, 0x7E)), 2));
    private static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public static readonly DependencyProperty CueProperty = DependencyProperty.Register(
        nameof(Cue), typeof(MediaCue), typeof(TrimEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnCueChanged));

    private Waveform? _waveform;
    private string? _waveformPath;
    private double _viewStart, _viewLength; // seconds shown; _viewLength 0 = the whole file
    private Handle? _selected, _dragging;
    private double _dragOffset;             // seconds between the mouse and the handle when the drag began
    private Drawing? _waveDrawing;          // waveform of the current view and size (rebuilt only when they change)
    private (double Start, double Length, Size Size, Waveform? Wave, Color Color) _waveKey;

    public TrimEditor()
    {
        Focusable = true;
        ClipToBounds = true;
        Loaded += (_, _) =>
        {
            CueColors.Changed -= OnColorsChanged;
            CueColors.Changed += OnColorsChanged;
            Watch(null, Cue);
        };
        Unloaded += (_, _) =>
        {
            CueColors.Changed -= OnColorsChanged; // the inspector creates editors as cues change: never keep old ones
            Watch(Cue, null);
        };
    }

    /// <summary>The audio or video cue edited (its start, end and fades).</summary>
    public MediaCue? Cue { get => (MediaCue?)GetValue(CueProperty); set => SetValue(CueProperty, value); }

    private ITrimmedMedia? Trim => Cue as ITrimmedMedia;

    private static void OnCueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (TrimEditor)d;
        editor.Watch(e.OldValue as MediaCue, e.NewValue as MediaCue);
        editor._viewLength = 0;
        editor._selected = null;
        editor.LoadWaveform();
    }

    private void Watch(MediaCue? old, MediaCue? now)
    {
        if (old is not null) old.PropertyChanged -= OnCuePropertyChanged;
        if (now is not null && IsLoaded) { now.PropertyChanged -= OnCuePropertyChanged; now.PropertyChanged += OnCuePropertyChanged; }
    }

    private void OnColorsChanged() => Dispatcher.BeginInvoke(() => { _waveDrawing = null; InvalidateVisual(); });

    private void OnCuePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MediaCue.FilePath):
                _viewLength = 0;
                LoadWaveform();
                break;
            case nameof(ITrimmedMedia.StartTime) or nameof(ITrimmedMedia.EndTime) or nameof(MediaCue.FadeIn) or nameof(MediaCue.FadeOut)
                or nameof(ITrimmedMedia.MediaDuration) or nameof(Core.Model.Cue.RunState) or nameof(VideoCue.HoldAtEnd):
                InvalidateVisual();
                break;
            case nameof(Core.Model.Cue.ElapsedSeconds):
                if (Cue?.RunState is CueRunState.Running or CueRunState.Paused) InvalidateVisual(); // the play line
                break;
        }
    }

    private void LoadWaveform()
    {
        _waveform = null;
        _waveDrawing = null;
        var path = Cue?.FilePath;
        _waveformPath = path;
        InvalidateVisual();
        if (string.IsNullOrWhiteSpace(path)) return;
        Waveforms.Shared.Get(path).ContinueWith(t =>
        {
            if (_waveformPath != path) return; // another cue meanwhile
            _waveform = t.Result;
            _waveDrawing = null;
            InvalidateVisual();
        }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    // ------------------------------------------------------------------ geometry

    /// <summary>Length of the file: the waveform's, the probed one, or enough to show the points set.</summary>
    private double Length
    {
        get
        {
            if (_waveform is not null) return _waveform.Length;
            if (Trim?.MediaDuration is double probed && probed > 0) return probed;
            var t = Trim;
            return Math.Max(1, Math.Max(t?.EndTime ?? 0, (t?.StartTime ?? 0) + (Cue?.FadeIn ?? 0) + (Cue?.FadeOut ?? 0)));
        }
    }

    private double ViewLength => _viewLength > 0 ? _viewLength : Length;
    private double ViewStart => _viewLength > 0 ? _viewStart : 0;

    private Rect WaveArea => new(0, RulerHeight, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight - RulerHeight));

    private double XOf(double seconds) => (seconds - ViewStart) / ViewLength * ActualWidth;
    private double TimeOf(double x) => ViewStart + x / Math.Max(1, ActualWidth) * ViewLength;

    private double Start => Trim?.StartTime ?? 0;

    /// <summary>The end point (the end of the file when not set).</summary>
    private double End => Trim is { EndTime: > 0 } t ? Math.Min(t.EndTime, Length) : Length;

    private bool CanFadeOut => Cue is not VideoCue { HoldAtEnd: true, Loop: false };

    private double HandleX(Handle h) => h switch
    {
        Handle.Start => XOf(Start),
        Handle.End => XOf(End),
        Handle.FadeIn => XOf(Start + (Cue?.FadeIn ?? 0)),
        _ => XOf(End - (Cue?.FadeOut ?? 0)),
    };

    /// <summary>The handle under the mouse: in the top strip the fade handles come first (with no fade they sit on the bars), elsewhere the bars.</summary>
    private Handle? HitTest(Point p)
    {
        if (Trim is null) return null;
        var area = WaveArea;
        var top = p.Y >= area.Top && p.Y <= area.Top + 14;
        Handle? best = null;
        var bestDistance = HitRange;
        foreach (var h in top ? new[] { Handle.FadeIn, Handle.FadeOut, Handle.Start, Handle.End } : new[] { Handle.Start, Handle.End })
        {
            if (h == Handle.FadeOut && !CanFadeOut) continue;
            var distance = Math.Abs(HandleX(h) - p.X);
            if (distance < bestDistance) { best = h; bestDistance = distance; }
        }
        return best;
    }

    // ------------------------------------------------------------------ editing

    private static double Round(double seconds) => Math.Round(seconds, 2);

    /// <summary>Moves a handle to a time, within what the other points allow.</summary>
    private void MoveHandle(Handle h, double time)
    {
        if (Cue is not MediaCue cue || Trim is not ITrimmedMedia trim) return;
        var length = Length;
        switch (h)
        {
            case Handle.Start:
                trim.StartTime = Round(Math.Clamp(time, 0, Math.Max(0, End - MinRegion)));
                break;
            case Handle.End:
                var end = Round(Math.Clamp(time, Start + MinRegion, length));
                trim.EndTime = end >= Round(length) ? 0 : end; // at the end of the file: "until the end"
                break;
            case Handle.FadeIn:
                cue.FadeIn = Round(Math.Clamp(time - Start, 0, Math.Max(0, End - Start - (CanFadeOut ? cue.FadeOut : 0))));
                break;
            case Handle.FadeOut:
                cue.FadeOut = Round(Math.Clamp(End - time, 0, Math.Max(0, End - Start - cue.FadeIn)));
                break;
        }
    }

    private double HandleTime(Handle h) => h switch
    {
        Handle.Start => Start,
        Handle.End => End,
        Handle.FadeIn => Start + (Cue?.FadeIn ?? 0),
        _ => End - (Cue?.FadeOut ?? 0),
    };

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        if (e.ClickCount == 2)
        {
            if (HitTest(p) is Handle reset) Reset(reset); // double click on a handle: back to its default
            else _viewLength = 0;                          // elsewhere: the whole file
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        _selected = HitTest(p);
        _lastMouse = p;
        if (_selected is Handle h)
        {
            _dragging = h;
            _dragOffset = HandleTime(h) - TimeOf(p.X);
            CaptureMouse();
        }
        InvalidateVisual();
        e.Handled = true;
    }

    private Point _lastMouse;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_dragging is not Handle h)
        {
            Cursor = HitTest(p) is null ? null : Cursors.SizeWE;
            _lastMouse = p;
            return;
        }
        // Ctrl = precision drag: the handle moves a tenth of the mouse.
        var x = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? _lastMouse.X + (p.X - _lastMouse.X) * 0.1 : p.X;
        _lastMouse = new Point(x, p.Y);
        MoveHandle(h, TimeOf(x) + _dragOffset);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = null;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _dragging = null;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var length = Length;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // Zoom around the mouse.
            var anchor = TimeOf(e.GetPosition(this).X);
            var view = Math.Clamp(ViewLength * (e.Delta > 0 ? 0.8 : 1.25), Math.Min(MinView, length), length);
            if (view >= length) _viewLength = 0;
            else
            {
                var ratio = (anchor - ViewStart) / ViewLength;
                _viewLength = view;
                _viewStart = Math.Clamp(anchor - ratio * view, 0, length - view);
            }
        }
        else if (_viewLength > 0)
            _viewStart = Math.Clamp(_viewStart - Math.Sign(e.Delta) * _viewLength * 0.15, 0, length - _viewLength);
        else return; // nothing to scroll: the panel scrolls
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_selected is not Handle h || Trim is null) return;
        if (e.Key is Key.Delete or Key.Back)
        {
            Reset(h);
            e.Handled = true;
            return;
        }
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 0.01;
        var delta = e.Key switch { Key.Left => -step, Key.Right => step, _ => 0.0 };
        if (delta == 0) return;
        MoveHandle(h, HandleTime(h) + delta);
        e.Handled = true;
    }

    private void Reset(Handle h)
    {
        if (Cue is not MediaCue cue || Trim is not ITrimmedMedia trim) return;
        switch (h)
        {
            case Handle.Start: trim.StartTime = 0; break;
            case Handle.End: trim.EndTime = 0; break;
            case Handle.FadeIn: cue.FadeIn = 0; break;
            case Handle.FadeOut: cue.FadeOut = 0; break;
        }
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _selected = null;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ drawing

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var area = WaveArea;
        dc.DrawRectangle(Background, null, new Rect(0, 0, w, ActualHeight));
        if (Trim is null || w < 2) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // waveform (cached per view and size)
        var color = CueColors.Of(Cue!.TypeName);
        var key = (ViewStart, ViewLength, RenderSize, _waveform, color);
        if (_waveDrawing is null || key != _waveKey)
        {
            _waveKey = key;
            _waveDrawing = BuildWave(area, color);
        }
        dc.DrawLine(CenterPen, new Point(0, area.Top + area.Height / 2), new Point(w, area.Top + area.Height / 2));
        if (_waveDrawing is not null) dc.DrawDrawing(_waveDrawing);
        else if (_waveform is null && Cue is VideoCue)
            DrawText(dc, "Forma d'onda non disponibile", 8, area.Top + area.Height / 2 - 8, LabelBrush, dpi);

        // outside the played region: dimmed
        var xs = XOf(Start);
        var xe = XOf(End);
        if (xs > 0) dc.DrawRectangle(OutsideFill, null, new Rect(0, area.Top, Math.Min(w, xs), area.Height));
        if (xe < w) dc.DrawRectangle(OutsideFill, null, new Rect(Math.Max(0, xe), area.Top, w - Math.Max(0, xe), area.Height));

        // fades: shaded under the envelope, then the envelope line
        var fadeIn = Cue.FadeIn;
        var fadeOut = CanFadeOut ? Cue.FadeOut : 0;
        var xfi = XOf(Start + fadeIn);
        var xfo = XOf(End - fadeOut);
        var envelope = new StreamGeometry();
        using (var g = envelope.Open())
        {
            g.BeginFigure(new Point(xs, area.Bottom), false, false);
            g.LineTo(new Point(xfi, area.Top + 1), true, false);
            g.LineTo(new Point(xfo, area.Top + 1), true, false);
            g.LineTo(new Point(xe, area.Bottom), true, false);
        }
        envelope.Freeze();
        if (fadeIn > 0) dc.DrawGeometry(FadeShade, null, Triangle(new Point(xs, area.Top), new Point(xfi, area.Top), new Point(xs, area.Bottom)));
        if (fadeOut > 0) dc.DrawGeometry(FadeShade, null, Triangle(new Point(xfo, area.Top), new Point(xe, area.Top), new Point(xe, area.Bottom)));
        dc.DrawGeometry(null, EnvelopePen, envelope);

        // start and end bars, with their grips at the top
        DrawBar(dc, xs, area, pointingRight: true, _selected == Handle.Start);
        DrawBar(dc, xe, area, pointingRight: false, _selected == Handle.End);
        DrawFadeHandle(dc, xfi, area, _selected == Handle.FadeIn);
        if (CanFadeOut) DrawFadeHandle(dc, xfo, area, _selected == Handle.FadeOut);

        // where the running cue is
        if (Cue.RunState is CueRunState.Running or CueRunState.Paused && End > Start)
        {
            var region = End - Start;
            var played = Cue.ElapsedSeconds;
            if (Trim.Loop) played %= region;
            var xp = XOf(Start + Math.Min(played, region));
            dc.DrawLine(PlayPen, new Point(xp, area.Top), new Point(xp, area.Bottom));
        }

        DrawRuler(dc, w, dpi);

        // the value of the handle being dragged or selected
        if ((_dragging ?? _selected) is Handle shown)
        {
            var text = shown switch
            {
                Handle.FadeIn => "Fade in " + TimeFormat.Format(fadeIn),
                Handle.FadeOut => "Fade out " + TimeFormat.Format(Cue.FadeOut),
                Handle.Start => "Inizio " + TimeFormat.Format(Start),
                _ => "Fine " + TimeFormat.Format(End),
            };
            var x = HandleX(shown);
            var formatted = Text(text, ValueBrush, dpi);
            var left = Math.Clamp(x + 6, 2, Math.Max(2, w - formatted.Width - 6));
            dc.DrawRectangle(RulerFill, null, new Rect(left - 3, area.Bottom - formatted.Height - 5, formatted.Width + 6, formatted.Height + 2));
            dc.DrawText(formatted, new Point(left, area.Bottom - formatted.Height - 4));
        }
    }

    private static Geometry Triangle(Point a, Point b, Point c)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(a, true, true);
            g.LineTo(b, false, false);
            g.LineTo(c, false, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static void DrawBar(DrawingContext dc, double x, Rect area, bool pointingRight, bool selected)
    {
        // Grip at the bottom (the top belongs to the fade handles).
        dc.DrawLine(AccentPen, new Point(x, area.Top), new Point(x, area.Bottom));
        var tip = pointingRight ? x + GripSize * 1.4 : x - GripSize * 1.4;
        dc.DrawGeometry(selected ? Selected : Accent, null,
            Triangle(new Point(x, area.Bottom), new Point(tip, area.Bottom), new Point(x, area.Bottom - GripSize * 1.6)));
    }

    private static void DrawFadeHandle(DrawingContext dc, double x, Rect area, bool selected)
    {
        var r = new Rect(x - GripSize / 2, area.Top + 2, GripSize, GripSize);
        dc.DrawRectangle(selected ? Selected : Accent, null, r);
    }

    /// <summary>The waveform of the view: per pixel column, the lowest and highest sample.</summary>
    private Drawing? BuildWave(Rect area, Color color)
    {
        if (_waveform is not Waveform wave || area.Width < 2) return null;
        var columns = (int)Math.Ceiling(area.Width);
        var mid = area.Top + area.Height / 2;
        // Scaled to the file's peak (at most ×20: near-silence stays flat), so quiet files can still be read.
        var half = (area.Height / 2 - 2) / Math.Max(0.05, Math.Min(1, wave.Peak));
        var tops = new double[columns];
        var bottoms = new double[columns];
        var last = wave.Max.Length - 1;
        for (var x = 0; x < columns; x++)
        {
            var b0 = (int)Math.Floor(TimeOf(x) / wave.SecondsPerBucket);
            var b1 = (int)Math.Floor(TimeOf(x + 1) / wave.SecondsPerBucket);
            b0 = Math.Clamp(b0, 0, last);
            b1 = Math.Clamp(Math.Max(b1, b0 + 1), 0, last + 1);
            float lo = 0, hi = 0;
            for (var b = b0; b < b1; b++)
            {
                if (wave.Min[b] < lo) lo = wave.Min[b];
                if (wave.Max[b] > hi) hi = wave.Max[b];
            }
            tops[x] = Math.Max(area.Top + 1, mid - hi * half - 0.5);
            bottoms[x] = Math.Min(area.Bottom - 1, mid - lo * half + 0.5);
        }
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(0, tops[0]), true, true);
            for (var x = 1; x < columns; x++) g.LineTo(new Point(x, tops[x]), false, false);
            for (var x = columns - 1; x >= 0; x--) g.LineTo(new Point(x, bottoms[x]), false, false);
        }
        geometry.Freeze();
        var fill = new SolidColorBrush(Color.FromArgb(0xC8, color.R, color.G, color.B));
        fill.Freeze();
        var drawing = new GeometryDrawing(fill, null, geometry);
        drawing.Freeze();
        return drawing;
    }

    private static readonly double[] Steps = { 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1200 };

    private void DrawRuler(DrawingContext dc, double w, double dpi)
    {
        dc.DrawRectangle(RulerFill, null, new Rect(0, 0, w, RulerHeight));
        var perSecond = w / ViewLength;
        var step = Steps[^1];
        foreach (var s in Steps)
            if (s * perSecond >= 80) { step = s; break; }
        var first = Math.Ceiling(ViewStart / step) * step;
        for (var t = first; t <= ViewStart + ViewLength + 1e-9; t += step)
        {
            var x = Math.Round(XOf(t)) + 0.5;
            dc.DrawLine(TickPen, new Point(x, RulerHeight - 5), new Point(x, RulerHeight));
            if (x + 4 < w - 30) DrawText(dc, Label(t, step), x + 3, 2, LabelBrush, dpi);
        }
        dc.DrawLine(TickPen, new Point(0, RulerHeight - 0.5), new Point(w, RulerHeight - 0.5));
    }

    private static string Label(double t, double step)
    {
        var minutes = (int)(t / 60);
        var seconds = t - minutes * 60;
        var s = step < 1 ? seconds.ToString(step < 0.1 ? "00.00" : "00.0", CultureInfo.InvariantCulture) : ((int)Math.Round(seconds)).ToString("00", CultureInfo.InvariantCulture);
        return $"{minutes}:{s}";
    }

    private static FormattedText Text(string text, Brush brush, double dpi)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11, brush, dpi);

    private static void DrawText(DrawingContext dc, string text, double x, double y, Brush brush, double dpi)
        => dc.DrawText(Text(text, brush, dpi), new Point(x, y));

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
