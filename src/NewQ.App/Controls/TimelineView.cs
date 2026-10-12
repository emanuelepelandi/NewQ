using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using NewQ.App.Infrastructure;
using NewQ.Core.Model;
using NewQ.Core.Timeline;

namespace NewQ.App.Controls;

/// <summary>
/// Draws the predicted show (<see cref="ShowTimeline"/>): one lane per route or per cue type, GO blocks side by side
/// (separated by a band: the wait for the operator's GO), each cue as a bar whose filled shape is its level over
/// time (fade in, Fade cues, fade out), with the first frame of videos and images. Redrawn only when something
/// changes; the progress of running cues is a separate lightweight layer (<see cref="TimelineProgressLayer"/>).
/// </summary>
public sealed class TimelineView : FrameworkElement
{
    public const double GutterWidth = 132;
    public const double RulerHeight = 24;
    public const double GapWidth = 56;
    private const double TrackHeight = 30;
    private const double VisualTrackHeight = 42;
    private const double OpenTail = 120;
    private const double MinBlockSeconds = 2;
    /// <summary>A lane of the shared layout (<see cref="TimelineLayout"/>) with its place on screen.</summary>
    private sealed class Lane
    {
        public Lane(TimelineLane info) => Info = info;
        public TimelineLane Info { get; }
        public string Label => Info.Label;
        public double Top { get; set; }
        public double Height => Info.Tracks * TrackH;
        public double TrackH => Info.Visual ? VisualTrackHeight : TrackHeight;
    }

    private sealed class Placed
    {
        public Placed(TimelineItem item, Lane lane, int track)
        {
            Item = item;
            Lane = lane;
            Track = track;
        }

        public TimelineItem Item { get; }
        public Lane Lane { get; }
        public int Track { get; }
        public Rect Bounds { get; set; }
    }

    private ShowTimeline _timeline = ShowTimeline.Empty;
    private Workspace? _workspace;
    private List<Lane> _lanes = new();
    private List<Placed> _placed = new();
    private Dictionary<TimelineItem, Placed> _byItem = new();
    private double[] _blockX = Array.Empty<double>();
    private double _contentWidth, _contentHeight;
    private double _pixelsPerSecond = 24;
    private double _horizontalOffset, _verticalOffset;

    public TimelineView()
    {
        ClipToBounds = true;
        Focusable = false;
        System.Windows.Controls.ToolTipService.SetInitialShowDelay(this, 400);
        CueColors.Changed += () =>
        {
            TypeBrushes.Clear();
            InvalidateVisual();
        };
    }

    /// <summary>Raised when the user clicks a cue.</summary>
    public event Action<Cue>? CueClicked;

    /// <summary>Raised when the extent or the offsets change (the panel updates its scroll bars).</summary>
    public event Action? ScrollChanged;

    /// <summary>Raised after a zoom with the mouse wheel (the panel saves it).</summary>
    public event Action? Zoomed;

    public bool LanesByRoute { get; set; } = true;
    public Func<Cue, ImageSource?> ThumbnailOf { get; set; } = _ => null;
    public Cue? PlayheadCue { get; set; }
    public Cue? SelectedCue { get; set; }

    public double PixelsPerSecond
    {
        get => _pixelsPerSecond;
        set { _pixelsPerSecond = Math.Clamp(value, 2, 400); Relayout(); }
    }

    public double ContentWidth => _contentWidth;
    public double ContentHeight => _contentHeight;
    public double ViewportWidth => Math.Max(0, ActualWidth - GutterWidth);
    public double ViewportHeight => Math.Max(0, ActualHeight - RulerHeight);

    public double HorizontalOffset
    {
        get => _horizontalOffset;
        set
        {
            var v = Math.Clamp(value, 0, Math.Max(0, _contentWidth - ViewportWidth));
            if (Math.Abs(v - _horizontalOffset) < 0.1) return;
            _horizontalOffset = v;
            InvalidateVisual();
            ScrollChanged?.Invoke();
        }
    }

    public double VerticalOffset
    {
        get => _verticalOffset;
        set
        {
            var v = Math.Clamp(value, 0, Math.Max(0, _contentHeight - ViewportHeight));
            if (Math.Abs(v - _verticalOffset) < 0.1) return;
            _verticalOffset = v;
            InvalidateVisual();
            ScrollChanged?.Invoke();
        }
    }

    /// <summary>New data: recomputes lanes and positions, then redraws.</summary>
    public void Update(ShowTimeline timeline, Workspace workspace)
    {
        _timeline = timeline;
        _workspace = workspace;
        Relayout();
    }

    /// <summary>Selection / playhead / previews changed: redraw only.</summary>
    public void Redraw() => InvalidateVisual();

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        HorizontalOffset = _horizontalOffset; // re-clamp
        VerticalOffset = _verticalOffset;
        ScrollChanged?.Invoke();
    }

    // ------------------------------------------------------------------ layout

    private void Relayout()
    {
        BuildLanes();
        _blockX = new double[_timeline.Blocks.Count];
        var x = StartGap;
        for (var b = 0; b < _timeline.Blocks.Count; b++)
        {
            x += GapWidth; // the GO that opens the block
            _blockX[b] = x;
            x += BlockWidth(b);
        }
        _contentWidth = x + OpenTail;

        var y = 0.0;
        foreach (var lane in _lanes)
        {
            lane.Top = y;
            y += lane.Height;
        }
        _contentHeight = y;

        foreach (var p in _placed)
        {
            var x0 = XOf(p.Item.Start);
            var x1 = p.Item.End is TimelinePoint end ? XOf(end) : _contentWidth;
            if (x1 - x0 < 10) x1 = x0 + 10;
            var top = p.Lane.Top + p.Track * p.Lane.TrackH + 3;
            p.Bounds = new Rect(x0, top, x1 - x0, p.Lane.TrackH - 6);
        }

        HorizontalOffset = _horizontalOffset;
        VerticalOffset = _verticalOffset;
        InvalidateVisual();
        ScrollChanged?.Invoke();
    }

    private double BlockWidth(int block) => Math.Max(MinBlockSeconds, _timeline.Blocks[block].Length) * _pixelsPerSecond;

    /// <summary>X of a moment, in content coordinates (0 = start of the first block).</summary>
    public double XOf(TimelinePoint p)
        => p.Block < 0 || p.Block >= _blockX.Length ? 0 : _blockX[p.Block] + Math.Max(0, p.Time) * _pixelsPerSecond;

    /// <summary>Screen X of the middle of the GO gap before a block (a block past the last one = after the show).</summary>
    public double ScreenXOfGo(int block)
    {
        double contentX;
        if (block >= 0 && block < _blockX.Length) contentX = _blockX[block] - GapWidth / 2;
        else if (_blockX.Length > 0) contentX = _blockX[^1] + BlockWidth(_blockX.Length - 1) + GapWidth / 2;
        else contentX = GapWidth / 2;
        return GutterWidth + contentX - _horizontalOffset;
    }

    /// <summary>X of a moment on screen (control coordinates).</summary>
    public double ScreenXOf(TimelinePoint p) => GutterWidth + XOf(p) - _horizontalOffset;

    /// <summary>The bar of an item on screen (control coordinates); false if it is not drawn.</summary>
    public bool TryGetScreenBounds(TimelineItem item, out Rect bounds)
    {
        bounds = Rect.Empty;
        if (!_byItem.TryGetValue(item, out var p)) return false;
        bounds = p.Bounds;
        bounds.Offset(GutterWidth - _horizontalOffset, RulerHeight - _verticalOffset);
        return true;
    }

    /// <summary>The moment under a content X (the nearest block for X in a gap).</summary>
    private TimelinePoint PointAt(double contentX)
    {
        for (var b = _blockX.Length - 1; b >= 0; b--)
            if (contentX >= _blockX[b]) return new TimelinePoint(b, (contentX - _blockX[b]) / _pixelsPerSecond);
        return new TimelinePoint(0, 0);
    }

    /// <summary>Scrolls so that a block starts a quarter into the view.</summary>
    public void ScrollToBlock(int block)
    {
        if (block < 0 || block >= _blockX.Length) return;
        HorizontalOffset = _blockX[block] - GapWidth - ViewportWidth / 4;
    }

    /// <summary>Zoom keeping the moment under <paramref name="screenX"/> where it is.</summary>
    public void ZoomAt(double factor, double screenX)
    {
        var contentX = screenX - GutterWidth + _horizontalOffset;
        var anchor = PointAt(contentX);
        PixelsPerSecond = _pixelsPerSecond * factor;
        HorizontalOffset = XOf(anchor) - (screenX - GutterWidth);
    }

    /// <summary>Zoom so the whole show fits the view.</summary>
    public void ZoomToFit()
    {
        var seconds = _timeline.Blocks.Sum(b => Math.Max(MinBlockSeconds, b.Length));
        var room = ViewportWidth - _timeline.Blocks.Count * GapWidth - StartGap - OpenTail;
        if (seconds <= 0 || room <= 50) return;
        PixelsPerSecond = room / seconds;
        HorizontalOffset = 0;
    }

    private void BuildLanes()
    {
        var layout = TimelineLayout.Build(_timeline, _workspace, LanesByRoute);
        var lanes = layout.Lanes.ToDictionary(l => l, l => new Lane(l));
        _lanes = layout.Lanes.Select(l => lanes[l]).ToList();
        _placed = layout.Placements.Select(p => new Placed(p.Item, lanes[p.Lane], p.Track)).ToList();
        _byItem = new Dictionary<TimelineItem, Placed>();
        foreach (var p in _placed) _byItem.TryAdd(p.Item, p);
    }

    // ------------------------------------------------------------------ input

    private Placed? HitTest(Point p)
    {
        if (p.X < GutterWidth || p.Y < RulerHeight) return null;
        var content = new Point(p.X - GutterWidth + _horizontalOffset, p.Y - RulerHeight + _verticalOffset);
        for (var i = _placed.Count - 1; i >= 0; i--)
        {
            var b = _placed[i].Bounds;
            if (new Rect(b.X - 4, b.Y, b.Width + 8, b.Height).Contains(content)) return _placed[i];
        }
        return null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (HitTest(e.GetPosition(this)) is Placed hit) CueClicked?.Invoke(hit.Item.Cue);
        e.Handled = true;
    }

    private Placed? _hover;

    private int _hoverGap = -1;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var point = e.GetPosition(this);
        var hit = HitTest(point);
        var gap = hit is null ? GapAt(point) : -1;
        if (ReferenceEquals(hit, _hover) && gap == _hoverGap) return;
        _hover = hit;
        _hoverGap = gap;
        ToolTip = hit is not null ? Describe(hit.Item)
            : gap >= 0 ? $"GO {gap + 1} → {_timeline.Blocks[gap].FirstCue.Number} {_timeline.Blocks[gap].FirstCue.Name}"
            : null;
        Cursor = hit is null ? null : Cursors.Hand;
    }

    /// <summary>The block whose GO gap is under the point, or -1.</summary>
    private int GapAt(Point p)
    {
        if (p.X < GutterWidth) return -1;
        var x = p.X - GutterWidth + _horizontalOffset;
        for (var b = 0; b < _blockX.Length; b++)
            if (x >= _blockX[b] - GapWidth && x < _blockX[b]) return b;
        return -1;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control))
        {
            ZoomAt(e.Delta > 0 ? 1.25 : 0.8, e.GetPosition(this).X);
            Zoomed?.Invoke();
        }
        else if (mods.HasFlag(ModifierKeys.Shift)) VerticalOffset -= e.Delta / 3.0;
        else HorizontalOffset -= e.Delta;
        e.Handled = true;
    }

    private string Describe(TimelineItem item)
    {
        var c = item.Cue;
        string When(TimelinePoint p) => $"GO {p.Block + 1} + {p.Time.ToString("0.##", CultureInfo.CurrentCulture)} s";
        var lines = new List<string> { $"{c.Number}  {c.Name}  ·  {c.TypeName}{(item.Disarmed ? " (disarmata)" : "")}" };
        lines.Add($"Inizio: {When(item.Start)}");
        lines.Add(item.End is TimelinePoint end
            ? $"Fine: {When(end)}{(item.EndedByCue ? " (fermata da una cue)" : "")}"
            : "Fine: finché fermata");
        if (c is MediaCue m && (m.FadeIn > 0 || m.FadeOut > 0))
            lines.Add($"Fade in {m.FadeIn.ToString("0.##", CultureInfo.CurrentCulture)} s · fade out {m.FadeOut.ToString("0.##", CultureInfo.CurrentCulture)} s");
        return string.Join("\n", lines);
    }

    // ------------------------------------------------------------------ drawing

    private Brush B(string key) => (Brush)FindResource(key);

    private static readonly Dictionary<(string, byte), SolidColorBrush> TypeBrushes = new();

    /// <summary>Colour key of a bar: its type, or "fading" (amber) while the cue fades out to a stop.</summary>
    private const string FadingKey = "\u0001fading";

    private static string ColorKeyOf(Cue cue) => cue.IsFadingOut ? FadingKey : cue.TypeName;

    private static Brush TypeBrush(string type, byte alpha)
    {
        if (TypeBrushes.TryGetValue((type, alpha), out var brush)) return brush;
        var c = type == FadingKey
            ? ((SolidColorBrush)Application.Current.FindResource("WarningBrush")).Color
            : CueTypeBrushConverter.ColorOf(type);
        brush = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        brush.Freeze();
        return TypeBrushes[(type, alpha)] = brush;
    }

    private FormattedText Text(string text, double size, Brush brush, FontWeight? weight = null)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private int _renders;

    protected override void OnRender(DrawingContext dc)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try { RenderTimeline(dc); }
        finally
        {
            if (++_renders % 20 == 0 || clock.Elapsed.TotalMilliseconds > 16)
                NewQ.App.Video.VlcRuntime.Debug($"timeline render: {clock.Elapsed.TotalMilliseconds:F1} ms ({_placed.Count} cues)");
        }
    }

    private void RenderTimeline(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(B("PanelBrush"), null, new Rect(0, 0, w, h));
        if (_timeline.Blocks.Count == 0)
        {
            var empty = Text("Nessuna cue", 13, B("TextMutedBrush"));
            dc.DrawText(empty, new Point(GutterWidth + 16, RulerHeight + 16));
            return;
        }

        var line = new Pen(B("BorderSubtleBrush"), 1);
        line.Freeze();
        var content = new Rect(GutterWidth, RulerHeight, Math.Max(0, w - GutterWidth), Math.Max(0, h - RulerHeight));
        // Which GOs are past (grey), next (solid green) and future (tinted green), from the playhead.
        var onStart = PlayheadCue is ShowMarkerCue { IsStart: true };
        var playheadBlock = PlayheadCue switch
        {
            null => -1,
            ShowMarkerCue { IsEnd: true } => _timeline.Blocks.Count, // all GOs are behind
            _ when onStart && _timeline.StartHasGo => -1,             // the next GO is START's
            _ => _timeline.BlockOf(PlayheadCue),
        };
        GoState StateOf(int block) => playheadBlock < 0 && !onStart ? GoState.Future
            : block < playheadBlock ? GoState.Past : block == playheadBlock ? GoState.Next : GoState.Future;
        var startState = onStart ? GoState.Next : GoState.Past;

        // ---- content: lanes, blocks, cues
        dc.PushClip(new RectangleGeometry(content));
        dc.PushTransform(new TranslateTransform(GutterWidth - _horizontalOffset, RulerHeight - _verticalOffset));

        foreach (var lane in _lanes)
            dc.DrawLine(line, new Point(_horizontalOffset, lane.Top + lane.Height - 0.5), new Point(_horizontalOffset + w, lane.Top + lane.Height - 0.5));

        foreach (var p in _placed) DrawItem(dc, p);

        // GO gaps: the wait for the operator's GO, hatched, with the GO line in the middle
        // (drawn over the cues that keep running through them, which look dimmed there).
        if (_timeline.StartHasGo) DrawGap(dc, 0, startState, h);
        for (var b = 0; b < _blockX.Length; b++)
            DrawGap(dc, _blockX[b] - GapWidth, StateOf(b), h);
        dc.Pop();
        dc.Pop();

        // ---- ruler (time from each block's GO)
        dc.DrawRectangle(B("RaisedBrush"), null, new Rect(0, 0, w, RulerHeight));
        dc.DrawLine(line, new Point(0, RulerHeight - 0.5), new Point(w, RulerHeight - 0.5));
        dc.PushClip(new RectangleGeometry(new Rect(GutterWidth, 0, Math.Max(0, w - GutterWidth), RulerHeight)));
        var step = TickStep();
        for (var b = 0; b < _blockX.Length; b++)
        {
            var x0 = GutterWidth + _blockX[b] - _horizontalOffset;
            var width = BlockWidth(b);
            DrawBadge(dc, _blockX[b] - GapWidth / 2, $"GO {b + 1}", StateOf(b));
            if (x0 > w || x0 + width < GutterWidth) continue;
            for (var t = step; t * _pixelsPerSecond < width - 10; t += step)
            {
                var x = x0 + t * _pixelsPerSecond;
                if (x < GutterWidth || x > w) continue;
                dc.DrawLine(line, new Point(x + 0.5, RulerHeight - 6), new Point(x + 0.5, RulerHeight));
                dc.DrawText(Text(FormatTime(t), 10, B("TextMutedBrush")), new Point(x + 3, 6));
            }
        }
        if (_timeline.StartHasGo) DrawBadge(dc, GapWidth / 2, "START", startState);
        dc.Pop();

        // ---- gutter: lane names
        dc.DrawRectangle(B("RaisedBrush"), null, new Rect(0, RulerHeight, GutterWidth, Math.Max(0, h - RulerHeight)));
        dc.DrawLine(line, new Point(GutterWidth - 0.5, 0), new Point(GutterWidth - 0.5, h));
        dc.DrawText(Text(LanesByRoute ? "ROUTE" : "TIPO", 10, B("TextMutedBrush"), FontWeights.SemiBold), new Point(12, 6));
        dc.PushClip(new RectangleGeometry(new Rect(0, RulerHeight, GutterWidth, Math.Max(0, h - RulerHeight))));
        foreach (var lane in _lanes)
        {
            var y = RulerHeight + lane.Top - _verticalOffset;
            var label = Text(lane.Label, 12, B("TextSecondaryBrush"));
            label.MaxTextWidth = GutterWidth - 20;
            label.MaxLineCount = 1;
            label.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(label, new Point(12, y + (lane.TrackH - label.Height) / 2));
            dc.DrawLine(line, new Point(0, y + lane.Height - 0.5), new Point(GutterWidth, y + lane.Height - 0.5));
        }
        dc.Pop();
    }

    private static readonly Brush GapFill = Freeze(new SolidColorBrush(Color.FromArgb(0xC8, 0x0D, 0x0F, 0x12)));

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private enum GoState { Past, Next, Future }

    /// <summary>Room before GO 1 for START's own GO.</summary>
    private double StartGap => _timeline.StartHasGo ? GapWidth : 0;

    /// <summary>A GO gap starting at content X <paramref name="left"/>: hatched, with the GO line in the middle.</summary>
    private void DrawGap(DrawingContext dc, double left, GoState state, double height)
    {
        var right = left + GapWidth;
        var next = state == GoState.Next;
        if (right < _horizontalOffset || left > _horizontalOffset + ActualWidth) return;
        var top = _verticalOffset;
        var area = new Rect(left, top, GapWidth, height);
        dc.DrawRectangle(GapFill, null, area);

        // Diagonal hatching: "waiting".
        dc.PushClip(new RectangleGeometry(area));
        var hatch = new Pen(B("BorderSubtleBrush"), 1);
        for (var d = -height; d < GapWidth; d += 10)
            dc.DrawLine(hatch, new Point(left + d, top + height), new Point(left + d + height, top));
        dc.Pop();

        var center = new Pen(state == GoState.Past ? B("TextMutedBrush") : B("GoBrush"), next ? 2 : 1.5)
        {
            DashStyle = next ? DashStyles.Solid : DashStyles.Dash,
        };
        dc.DrawLine(center, new Point(left + GapWidth / 2, top), new Point(left + GapWidth / 2, top + height));
        dc.DrawLine(new Pen(B("BorderStrongBrush"), 1), new Point(right + 0.5, top), new Point(right + 0.5, top + height));
    }

    /// <summary>GO pill in the ruler over a gap (content X of its middle): next solid green, future tinted, past grey.</summary>
    private void DrawBadge(DrawingContext dc, double contentCenter, string label, GoState state)
    {
        var center = GutterWidth + contentCenter - _horizontalOffset;
        if (center < GutterWidth - GapWidth || center > ActualWidth + GapWidth) return;
        var textBrush = state switch
        {
            GoState.Next => B("TextOnAccentBrush"),
            GoState.Past => B("TextMutedBrush"),
            _ => B("TextPrimaryBrush"),
        };
        var text = Text(label, 11, textBrush, FontWeights.SemiBold);
        var width = Math.Max(text.Width + 14, 40);
        var pill = new Rect(center - width / 2, 3, width, RulerHeight - 7);
        var (fill, edge) = state switch
        {
            GoState.Next => (B("GoBrush"), B("GoBrush")),
            GoState.Past => (B("RaisedBrush"), B("BorderStrongBrush")),
            _ => (TintOf("GoBrush", 0x4D), B("GoBrush")),
        };
        dc.DrawRoundedRectangle(fill, new Pen(edge, 1), pill, pill.Height / 2, pill.Height / 2);
        dc.DrawText(text, new Point(center - text.Width / 2, pill.Y + (pill.Height - text.Height) / 2));
    }

    private Brush TintOf(string key, byte alpha)
    {
        var c = ((SolidColorBrush)B(key)).Color;
        return Freeze(new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)));
    }

    private void DrawItem(DrawingContext dc, Placed p)
    {
        var item = p.Item;
        var r = p.Bounds;
        if (r.Right < _horizontalOffset - 20 || r.Left > _horizontalOffset + ActualWidth) return; // off screen
        var type = ColorKeyOf(item.Cue);
        var selected = ReferenceEquals(item.Cue, SelectedCue);
        if (item.Disarmed) dc.PushOpacity(0.35);

        var instant = item.End is TimelinePoint e && Equals(e, item.Start);
        if (instant || item.Kind == TimelineItemKind.Stop && item.End is not null && XOf(item.End.Value) - XOf(item.Start) < 4)
        {
            // Instant cue (MIDI, network, stop): a diamond and its label.
            var cx = r.X;
            var cy = r.Y + r.Height / 2;
            var d = new StreamGeometry();
            using (var g = d.Open())
            {
                g.BeginFigure(new Point(cx, cy - 6), true, true);
                g.LineTo(new Point(cx + 6, cy), true, false);
                g.LineTo(new Point(cx, cy + 6), true, false);
                g.LineTo(new Point(cx - 6, cy), true, false);
            }
            d.Freeze();
            dc.DrawGeometry(TypeBrush(type, 0xFF), selected ? new Pen(B("AccentBrush"), 2) : null, d);
            var t = Text($"{item.Cue.Number} {item.Cue.Name}", 11, B("TextSecondaryBrush"));
            t.MaxTextWidth = 160;
            t.MaxLineCount = 1;
            t.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(t, new Point(cx + 10, cy - t.Height / 2));
            if (item.Disarmed) dc.Pop();
            return;
        }

        if (item.Kind != TimelineItemKind.Media)
        {
            // Control cues (Fade, Wait, Stop with a fade): no level of their own, just a plain pink bar
            // (amber while the cue fades out, like media cues).
            var control = item.Cue.IsFadingOut ? FadingKey : "Fade";
            dc.DrawRoundedRectangle(TypeBrush(control, 0x70), new Pen(selected ? B("AccentBrush") : TypeBrush(control, 0xB0), selected ? 2 : 1), r, 3, 3);
            dc.PushClip(new RectangleGeometry(r));
            var name = Text($"{item.Cue.Number}  {item.Cue.Name}", 11, B("TextPrimaryBrush"), FontWeights.SemiBold);
            name.MaxLineCount = 1;
            dc.DrawText(name, new Point(r.X + 6, r.Y + 3));
            dc.Pop();
            if (item.Disarmed) dc.Pop();
            return;
        }

        // Bar background, first frame, level shape (envelope, over the frame so fades stay visible), outline.
        dc.DrawRoundedRectangle(TypeBrush(type, 0x1E), null, r, 3, 3);
        dc.PushClip(new RectangleGeometry(r));
        var textX = r.X + 6;
        if (ThumbnailOf(item.Cue) is ImageSource thumb && r.Width > 40)
        {
            var th = r.Height - 4;
            var tw = thumb.Height > 0 ? th * thumb.Width / thumb.Height : th * 16 / 9;
            dc.DrawImage(thumb, new Rect(r.X + 2, r.Y + 2, tw, th));
            textX = r.X + tw + 8;
        }
        dc.Pop();
        var shape = EnvelopeGeometry(item, r);
        dc.DrawGeometry(TypeBrush(type, 0x55), null, shape.Fill);
        dc.DrawGeometry(null, new Pen(TypeBrush(type, 0xFF), 1.5), shape.Line);
        var outline = new Pen(selected ? B("AccentBrush") : TypeBrush(type, 0x90), selected ? 2 : 1);
        if (item.IsOpenEnded) outline.DashStyle = DashStyles.Dash;
        dc.DrawRoundedRectangle(null, outline, r, 3, 3);

        // Number and name.
        dc.PushClip(new RectangleGeometry(r));
        var label = Text($"{item.Cue.Number}  {item.Cue.Name}", 11, B("TextPrimaryBrush"), FontWeights.SemiBold);
        label.MaxLineCount = 1;
        dc.DrawText(label, new Point(textX, r.Y + 3));
        if (item.IsOpenEnded)
            dc.DrawText(Text("continua →", 10, B("TextMutedBrush")), new Point(Math.Max(textX, r.Right - 70), r.Bottom - 15));
        dc.Pop();

        if (item.Disarmed) dc.Pop();
    }

    /// <summary>The level over time as a filled shape (from the bottom of the bar) and its top line.</summary>
    private (Geometry Fill, Geometry Line) EnvelopeGeometry(TimelineItem item, Rect r)
    {
        double Y(double level) => r.Bottom - 2 - Math.Clamp(level, 0, 1.25) / 1.25 * (r.Height - 6);

        var points = new List<Point> { new(r.X, Y(TimelineBuilder.LevelAt(item, item.Start))) };
        foreach (var e in item.Envelope)
        {
            var x = XOf(e.At);
            if (x < r.X || x > r.Right) continue;
            points.Add(new Point(x, Y(e.Level)));
        }
        var endLevel = item.End is TimelinePoint end ? TimelineBuilder.LevelAt(item, end) : points[^1].Y;
        points.Add(new Point(r.Right, item.End is null ? points[^1].Y : Y(endLevel)));
        points.Sort((a, b) => a.X.CompareTo(b.X));

        var lineGeometry = new StreamGeometry();
        using (var g = lineGeometry.Open())
        {
            g.BeginFigure(points[0], false, false);
            foreach (var pt in points.Skip(1)) g.LineTo(pt, true, false);
        }
        lineGeometry.Freeze();

        var fill = new StreamGeometry();
        using (var g = fill.Open())
        {
            g.BeginFigure(new Point(r.X, r.Bottom), true, true);
            foreach (var pt in points) g.LineTo(pt, true, false);
            g.LineTo(new Point(r.Right, r.Bottom), true, false);
        }
        fill.Freeze();
        return (fill, lineGeometry);
    }

    private double TickStep()
    {
        foreach (var s in new[] { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 })
            if (s * _pixelsPerSecond >= 60) return s;
        return 1200;
    }

    private static string FormatTime(double seconds)
        => seconds < 60 ? seconds.ToString("0.#", CultureInfo.CurrentCulture) + " s" : TimeSpan.FromSeconds(seconds).ToString(@"m\:ss");
}

/// <summary>
/// The show's progress over <see cref="TimelineView"/>, a thin layer redrawn alone while cues run: the played part of
/// each running cue's bar in green (or, by choice, one cursor per GO), and the bars of live sources pulsing softly.
/// </summary>
public sealed class TimelineProgressLayer : FrameworkElement
{
    private readonly List<(Rect Bar, double X)> _bars = new();
    private readonly List<Rect> _live = new();
    private readonly List<(double X, bool Waiting)> _cursors = new();
    private byte _pulse;
    private Color _green;
    private Brush? _fill;
    private Pen? _edge;

    public TimelineProgressLayer() => IsHitTestVisible = false;

    /// <summary>
    /// On screen: running cues' bars and progress X, live bars, cursors (X, waiting for the GO); <paramref name="pulse"/>
    /// = opacity of the live bars now. Redraws only when something changed.
    /// </summary>
    public void Set(IReadOnlyList<(Rect Bar, double X)> bars, IReadOnlyList<Rect> live, IReadOnlyList<(double X, bool Waiting)> cursors, byte pulse)
    {
        if (Same(bars, _bars) && Same(live, _live) && Same(cursors, _cursors) && (live.Count == 0 || pulse == _pulse)) return;
        Copy(bars, _bars);
        Copy(live, _live);
        Copy(cursors, _cursors);
        _pulse = pulse;
        InvalidateVisual();
    }

    private static bool Same<T>(IReadOnlyList<T> a, List<T> b) where T : struct
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!a[i].Equals(b[i])) return false;
        return true;
    }

    private static void Copy<T>(IReadOnlyList<T> from, List<T> to)
    {
        to.Clear();
        for (var i = 0; i < from.Count; i++) to.Add(from[i]);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_bars.Count == 0 && _live.Count == 0 && _cursors.Count == 0) return;
        if (_fill is null || _edge is null)
        {
            _green = ((SolidColorBrush)FindResource("SuccessBrush")).Color;
            _fill = new SolidColorBrush(Color.FromArgb(0x8C, _green.R, _green.G, _green.B));
            _fill.Freeze();
            _edge = new Pen(new SolidColorBrush(_green), 2);
            _edge.Freeze();
        }
        dc.PushClip(new RectangleGeometry(new Rect(TimelineView.GutterWidth, TimelineView.RulerHeight,
            Math.Max(0, ActualWidth - TimelineView.GutterWidth), Math.Max(0, ActualHeight - TimelineView.RulerHeight))));
        foreach (var (bar, x) in _bars)
        {
            var right = Math.Clamp(x, bar.Left, bar.Right);
            if (right <= bar.Left || bar.Right < TimelineView.GutterWidth || bar.Left > ActualWidth) continue;
            dc.PushClip(new RectangleGeometry(new Rect(bar.Left, bar.Top, right - bar.Left, bar.Height)));
            dc.DrawRoundedRectangle(_fill, null, bar, 3, 3);
            dc.Pop();
            if (right < bar.Right - 1)
                dc.DrawLine(_edge, new Point(right, bar.Top), new Point(right, bar.Bottom));
        }
        if (_live.Count > 0)
        {
            var pulse = new SolidColorBrush(Color.FromArgb(_pulse, _green.R, _green.G, _green.B));
            pulse.Freeze();
            foreach (var bar in _live)
                if (bar.Right >= TimelineView.GutterWidth && bar.Left <= ActualWidth) dc.DrawRoundedRectangle(pulse, null, bar, 3, 3);
        }
        dc.Pop();

        foreach (var (x, waiting) in _cursors)
        {
            if (x < TimelineView.GutterWidth - TimelineView.GapWidth / 2 || x > ActualWidth) continue;
            var brush = (Brush)FindResource(waiting ? "WarningBrush" : "SuccessBrush");
            dc.DrawLine(new Pen(brush, 2), new Point(x, TimelineView.RulerHeight), new Point(x, ActualHeight));
            var head = new StreamGeometry();
            using (var g = head.Open())
            {
                g.BeginFigure(new Point(x - 5, TimelineView.RulerHeight), true, true);
                g.LineTo(new Point(x + 5, TimelineView.RulerHeight), true, false);
                g.LineTo(new Point(x, TimelineView.RulerHeight + 7), true, false);
            }
            head.Freeze();
            dc.DrawGeometry(brush, null, head);
        }
    }
}
