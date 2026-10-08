using System;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NewQ.App.Infrastructure;

namespace NewQ.App.Controls;

/// <summary>
/// Field that records a key combination: click (or tab) into it, press the keys. Every key is captured,
/// Esc and Enter included (they can be shortcuts); Tab still moves on. Looks like a NewQ text field.
/// </summary>
public sealed class KeyCaptureBox : Control
{
    public static readonly DependencyProperty GestureProperty = DependencyProperty.Register(
        nameof(Gesture), typeof(string), typeof(KeyCaptureBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Typeface Face = new("Segoe UI");

    public KeyCaptureBox()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Height = 32;
        MinWidth = 160;
        Cursor = Cursors.Hand;
        SetResourceReference(BackgroundProperty, "ControlBrush");
        SetResourceReference(BorderBrushProperty, "BorderStrongBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
    }

    /// <summary>Canonical gesture ("Ctrl+Shift+S"); empty = none.</summary>
    public string Gesture { get => (string)GetValue(GestureProperty); set => SetValue(GestureProperty, value); }

    /// <summary>Raised after a new combination was recorded.</summary>
    public event EventHandler? GestureCaptured;

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        e.Handled = true;
    }

    private Window? _window;

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is not null) _window.PreviewMouseDown += OnWindowMouseDown;
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        if (_window is not null) _window.PreviewMouseDown -= OnWindowMouseDown;
        _window = null;
        InvalidateVisual();
    }

    /// <summary>A click elsewhere ends the capture, so the next key press isn't recorded by surprise.</summary>
    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsMouseOver) StopCapture();
    }

    private void StopCapture()
    {
        if (!IsKeyboardFocused || _window is null) return;
        FocusManager.SetFocusedElement(_window, null);
        Keyboard.ClearFocus();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift) return; // keyboard navigation
        e.Handled = true; // nothing else reaches the window (no Esc = Annulla, no Enter = OK)
        if (ShortcutKeys.FromEvent(e) is not string gesture) return; // modifier alone: wait for the key
        Gesture = gesture;
        GestureCaptured?.Invoke(this, EventArgs.Empty);
        StopCapture(); // show what was recorded
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var focused = IsKeyboardFocused;
        var border = focused ? (Brush)FindResource("AccentBrush") : BorderBrush;
        dc.DrawRoundedRectangle(Background, new Pen(border, focused ? 2 : 1), new Rect(0.5, 0.5, ActualWidth - 1, ActualHeight - 1), 4, 4);

        string text;
        Brush brush;
        if (focused) { text = "Premi la combinazione…"; brush = (Brush)FindResource("AccentBrush"); }
        else if (string.IsNullOrEmpty(Gesture)) { text = "nessun tasto"; brush = (Brush)FindResource("TextMutedBrush"); }
        else { text = ShortcutKeys.Display(Gesture); brush = Foreground; }

        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Face, 13, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point(10, (ActualHeight - formatted.Height) / 2));
    }
}
