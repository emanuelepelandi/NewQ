using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using NewQ.Core.Model;
using NewQ.Core.Video;

namespace NewQ.App.Controls;

/// <summary>
/// Interactive editor for the geometry of one video output: the screen is drawn as a rectangle, the image as its
/// (keystoned, warped) grid. Drag the 4 corner handles for keystone / corner pin and the inner handles for the
/// warp. Arrow keys nudge the selected handle (Shift = 10×).
/// </summary>
public sealed class GeometryEditor : FrameworkElement
{
    private const double HandleRadius = 6;
    private const double Inset = 24;
    private const double Nudge = 0.001;

    // Frozen brushes mirroring the design tokens (InsetBrush, AccentBrush, WarningBrush, TextMutedBrush).
    private static readonly Brush Background = Freeze(new SolidColorBrush(Color.FromRgb(0x0D, 0x0F, 0x12)));
    private static readonly Pen ScreenPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x4A, 0x50, 0x5B)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Brush ScreenFill = Freeze(new SolidColorBrush(Color.FromRgb(0x08, 0x09, 0x0B)));
    private static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0x5B, 0x8D, 0xD6)), 1));
    private static readonly Pen OutlinePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xD6)), 2));
    private static readonly Pen BlendPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xE2, 0xA9, 0x45)), 1) { DashStyle = DashStyles.Dot });
    private static readonly Brush CornerFill = Freeze(new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xD6)));
    private static readonly Brush WarpFill = Freeze(new SolidColorBrush(Color.FromRgb(0xE2, 0xA9, 0x45)));
    private static readonly Brush SelectedFill = Freeze(new SolidColorBrush(Colors.White));
    private static readonly Pen HandlePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x0D, 0x0F, 0x12)), 1.5));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x7A, 0x81, 0x8D)));

    public static readonly DependencyProperty OutputProperty = DependencyProperty.Register(
        nameof(Output), typeof(VideoOutput), typeof(GeometryEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnOutputChanged));

    public static readonly DependencyProperty ScreenAspectProperty = DependencyProperty.Register(
        nameof(ScreenAspect), typeof(double), typeof(GeometryEditor),
        new FrameworkPropertyMetadata(16.0 / 9.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>A handle: corner 0–3 (TL, TR, BR, BL) or warp point (column, row).</summary>
    private readonly record struct Handle(int Corner, int Column, int Row)
    {
        public bool IsCorner => Corner >= 0;
        public static Handle ForCorner(int i) => new(i, -1, -1);
        public static Handle ForWarp(int c, int r) => new(-1, c, r);
    }

    private Handle? _selected;
    private bool _dragging;
    private Point _dragStart;
    private NormalizedPoint _dragOrigin;

    public GeometryEditor()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
    }

    public VideoOutput? Output { get => (VideoOutput?)GetValue(OutputProperty); set => SetValue(OutputProperty, value); }

    /// <summary>Width / height of the physical screen the output is on.</summary>
    public double ScreenAspect { get => (double)GetValue(ScreenAspectProperty); set => SetValue(ScreenAspectProperty, value); }

    private static void OnOutputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (GeometryEditor)d;
        if (e.OldValue is VideoOutput oldOutput) oldOutput.PropertyChanged -= editor.OnOutputPropertyChanged;
        if (e.NewValue is VideoOutput newOutput) newOutput.PropertyChanged += editor.OnOutputPropertyChanged;
        editor._selected = null;
        editor._dragging = false;
    }

    private void OnOutputPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VideoOutput.WarpColumns) or nameof(VideoOutput.WarpRows) && _selected is { IsCorner: false })
            _selected = null;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ coordinates

    /// <summary>Screen rectangle inside the control (letterboxed to the screen aspect).</summary>
    private Rect ScreenRect()
    {
        var w = Math.Max(1, ActualWidth - 2 * Inset);
        var h = Math.Max(1, ActualHeight - 2 * Inset);
        var aspect = ScreenAspect > 0 ? ScreenAspect : 16.0 / 9.0;
        if (w / h > aspect) w = h * aspect; else h = w / aspect;
        return new Rect((ActualWidth - w) / 2, (ActualHeight - h) / 2, w, h);
    }

    private Point ToView((double X, double Y) p, Rect s) => new(s.X + p.X * s.Width, s.Y + p.Y * s.Height);

    private static Homography BaseHomography(VideoOutput o)
    {
        var c = OutputGeometry.Corners(o);
        return Homography.FromUnitSquare(c[0], c[1], c[2], c[3]);
    }

    private static (double X, double Y) MapImage(VideoOutput o, Homography h, double u, double v)
    {
        var (x, y) = h.Map(u, v);
        if (!o.HasWarp) return (x, y);
        var (dx, dy) = OutputGeometry.WarpOffset(o, u, v);
        return (x + dx, y + dy);
    }

    private (double X, double Y) HandlePosition(VideoOutput o, Handle handle)
    {
        if (handle.IsCorner) return OutputGeometry.Corners(o)[handle.Corner];
        var h = BaseHomography(o);
        var (x, y) = h.Map((double)handle.Column / o.WarpColumns, (double)handle.Row / o.WarpRows);
        var p = o.GetWarpPoint(handle.Column, handle.Row);
        return (x + p.X, y + p.Y);
    }

    private NormalizedPoint GetOffset(VideoOutput o, Handle handle) => handle.IsCorner
        ? handle.Corner switch { 0 => o.TopLeft, 1 => o.TopRight, 2 => o.BottomRight, _ => o.BottomLeft }
        : o.GetWarpPoint(handle.Column, handle.Row);

    private static void SetOffset(VideoOutput o, Handle handle, NormalizedPoint value)
    {
        value = new NormalizedPoint(Math.Clamp(value.X, -1, 1), Math.Clamp(value.Y, -1, 1));
        if (!handle.IsCorner) { o.SetWarpPoint(handle.Column, handle.Row, value); return; }
        switch (handle.Corner)
        {
            case 0: o.TopLeft = value; break;
            case 1: o.TopRight = value; break;
            case 2: o.BottomRight = value; break;
            default: o.BottomLeft = value; break;
        }
    }

    private Handle? HitTest(Point point)
    {
        if (Output is not VideoOutput o) return null;
        var s = ScreenRect();
        // Corners win over warp points that sit on them.
        for (var i = 0; i < 4; i++)
            if ((ToView(HandlePosition(o, Handle.ForCorner(i)), s) - point).Length <= HandleRadius + 3) return Handle.ForCorner(i);
        if (o.HasWarp)
            for (var r = 0; r <= o.WarpRows; r++)
            for (var c = 0; c <= o.WarpColumns; c++)
                if ((ToView(HandlePosition(o, Handle.ForWarp(c, r)), s) - point).Length <= HandleRadius + 3) return Handle.ForWarp(c, r);
        return null;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    // ------------------------------------------------------------------ input

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        _selected = HitTest(e.GetPosition(this));
        if (_selected is Handle handle && Output is VideoOutput o)
        {
            _dragging = true;
            _dragStart = e.GetPosition(this);
            _dragOrigin = GetOffset(o, handle);
            CaptureMouse();
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging)
        {
            Cursor = HitTest(e.GetPosition(this)) is null ? null : Cursors.SizeAll;
            return;
        }
        if (_selected is not Handle handle || Output is not VideoOutput o) return;
        var s = ScreenRect();
        var delta = e.GetPosition(this) - _dragStart;
        var fine = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 0.1 : 1.0; // Ctrl = precision drag
        SetOffset(o, handle, new NormalizedPoint(_dragOrigin.X + delta.X / s.Width * fine, _dragOrigin.Y + delta.Y / s.Height * fine));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) => _dragging = false;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_selected is not Handle handle || Output is not VideoOutput o) return;
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? Nudge * 10 : Nudge;
        var (dx, dy) = e.Key switch
        {
            Key.Left => (-step, 0.0), Key.Right => (step, 0.0),
            Key.Up => (0.0, -step), Key.Down => (0.0, step),
            _ => (0.0, 0.0),
        };
        if (e.Key is Key.Delete or Key.Back) { SetOffset(o, handle, NormalizedPoint.Zero); e.Handled = true; return; }
        if (dx == 0 && dy == 0) return;
        var p = GetOffset(o, handle);
        SetOffset(o, handle, new NormalizedPoint(p.X + dx, p.Y + dy));
        e.Handled = true;
    }

    // ------------------------------------------------------------------ drawing

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var s = ScreenRect();
        dc.DrawRectangle(ScreenFill, ScreenPen, s);
        if (Output is not VideoOutput o)
        {
            DrawLabel(dc, "Seleziona un'uscita", new Point(s.X + 8, s.Y + 6));
            return;
        }

        var h = BaseHomography(o);
        const int lines = 8, steps = 32;

        // Image grid (with warp), outline thicker.
        for (var k = 0; k <= lines; k++)
        {
            var t = (double)k / lines;
            var pen = k == 0 || k == lines ? OutlinePen : GridPen;
            DrawCurve(dc, pen, steps, i => MapImage(o, h, t, (double)i / steps), s);
            DrawCurve(dc, pen, steps, i => MapImage(o, h, (double)i / steps, t), s);
        }

        // Blend zones (in image coordinates).
        if (o.BlendLeft > 0) DrawCurve(dc, BlendPen, steps, i => MapImage(o, h, o.BlendLeft, (double)i / steps), s);
        if (o.BlendRight > 0) DrawCurve(dc, BlendPen, steps, i => MapImage(o, h, 1 - o.BlendRight, (double)i / steps), s);
        if (o.BlendTop > 0) DrawCurve(dc, BlendPen, steps, i => MapImage(o, h, (double)i / steps, o.BlendTop), s);
        if (o.BlendBottom > 0) DrawCurve(dc, BlendPen, steps, i => MapImage(o, h, (double)i / steps, 1 - o.BlendBottom), s);

        if (o.HasWarp)
            for (var r = 0; r <= o.WarpRows; r++)
            for (var c = 0; c <= o.WarpColumns; c++)
            {
                var handle = Handle.ForWarp(c, r);
                DrawHandle(dc, ToView(HandlePosition(o, handle), s), _selected == handle ? SelectedFill : WarpFill, HandleRadius - 1.5);
            }
        for (var i = 0; i < 4; i++)
        {
            var handle = Handle.ForCorner(i);
            DrawHandle(dc, ToView(HandlePosition(o, handle), s), _selected == handle ? SelectedFill : CornerFill, HandleRadius);
        }

        var hint = _selected is Handle sel
            ? $"{(sel.IsCorner ? "Angolo" : $"Warp {sel.Column},{sel.Row}")}: {GetOffset(o, sel).X * 100:0.0} %, {GetOffset(o, sel).Y * 100:0.0} %  ·  frecce = sposta, Maiusc = ×10, Canc = azzera"
            : "Trascina gli angoli (keystone) o i punti gialli (warp). Ctrl = trascinamento fine.";
        DrawLabel(dc, hint, new Point(8, ActualHeight - 18));
    }

    private void DrawCurve(DrawingContext dc, Pen pen, int steps, Func<int, (double X, double Y)> point, Rect s)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(ToView(point(0), s), false, false);
            for (var i = 1; i <= steps; i++) ctx.LineTo(ToView(point(i), s), true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static void DrawHandle(DrawingContext dc, Point p, Brush fill, double radius)
        => dc.DrawEllipse(fill, HandlePen, p, radius, radius);

    private void DrawLabel(DrawingContext dc, string text, Point at)
    {
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, at);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
