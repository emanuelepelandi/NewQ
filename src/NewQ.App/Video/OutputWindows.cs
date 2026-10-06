using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using NewQ.App.Infrastructure;
using NewQ.Core.Model;
using Forms = System.Windows.Forms;

namespace NewQ.App.Video;

public sealed record ScreenOption(int Index, string Label)
{
    public static IReadOnlyList<ScreenOption> All()
    {
        var list = new List<ScreenOption> { new(VisualCue.PreviewWindow, "Finestra di anteprima") };
        var screens = Forms.Screen.AllScreens;
        for (var i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            list.Add(new ScreenOption(i, $"Schermo {i + 1} · {s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? " (principale)" : "")}"));
        }
        return list;
    }
}

/// <summary>
/// One output (a monitor, borderless and topmost, or a resizable preview window).
/// <para>
/// Two planes: videos are rendered by libVLC into native child windows inside <see cref="VideoHost"/>
/// (smooth, vsync'd to the output monitor, independent of the WPF render thread). WPF content can't be
/// drawn over native windows, so images and the per-video fade "dimmers" live in a transparent,
/// click-through <see cref="OverlayWindow"/> that tracks the output. Images are therefore always above videos.
/// </para>
/// </summary>
public sealed class OutputWindow : Window
{
    private readonly System.Drawing.Rectangle? _bounds;
    private readonly List<IVideoPlane> _videos = new(); // in reveal order, last = on top

    public OutputWindow(int screenIndex, System.Drawing.Rectangle? bounds)
    {
        ScreenIndex = screenIndex;
        _bounds = bounds;
        VideoHost = new Grid { Background = Brushes.Black };
        Content = VideoHost;
        Background = Brushes.Black;
        ShowActivated = false;
        Overlay = new OverlayWindow();
        // Black cover while no video is revealed: preloaded videos sit visible (first frame already
        // presented, so GO shows it instantly) underneath it.
        BaseDimmer = new System.Windows.Shapes.Rectangle { Fill = Brushes.Black, IsHitTestVisible = false };
        Panel.SetZIndex(BaseDimmer, -1);
        Overlay.Stage.Children.Add(BaseDimmer);

        if (bounds is null)
        {
            Title = "NewQ – Anteprima uscita video";
            Width = 960;
            Height = 540;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        else
        {
            Title = $"NewQ – Uscita schermo {screenIndex + 1}";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Cursor = Cursors.None;
            SourceInitialized += (_, _) => PlaceOnScreen();
            DpiChanged += (_, _) => Dispatcher.BeginInvoke(PlaceOnScreen);
        }

        Loaded += (_, _) =>
        {
            Overlay.Owner = this; // owned windows always stay above their owner
            Overlay.Topmost = Topmost;
            Overlay.Show();
            PlaceOverlay();
        };
        LocationChanged += (_, _) => PlaceOverlay();
        SizeChanged += (_, _) => PlaceOverlay();
        StateChanged += (_, _) => PlaceOverlay();
        Closed += (_, _) => Overlay.Close();
    }

    public int ScreenIndex { get; }

    /// <summary>Native video surfaces go here.</summary>
    public Grid VideoHost { get; }

    public OverlayWindow Overlay { get; }

    /// <summary>WPF content shown above the videos (images, fade dimmers).</summary>
    public Grid Stage => Overlay.Stage;

    private System.Windows.Shapes.Rectangle BaseDimmer { get; }

    /// <summary>When false (default), fullscreen outputs can only be closed by the application.</summary>
    public bool AllowClose { get; set; }

    // ------------------------------------------------------------------ video stacking

    /// <summary>A video becomes visible: it goes on top of the other videos and its dimmer takes over.</summary>
    internal void BringVideoToTop(IVideoPlane video)
    {
        _videos.Remove(video);
        _videos.Add(video);
        video.RaiseSurface();
        UpdateDimmers();
    }

    internal void RemoveVideo(IVideoPlane video)
    {
        if (_videos.Remove(video)) UpdateDimmers();
    }

    /// <summary>Only the top video is visible, so only its dimmer (fade to/from black) applies.</summary>
    private void UpdateDimmers()
    {
        for (var i = 0; i < _videos.Count; i++)
            _videos[i].Dimmer.Visibility = i == _videos.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
        BaseDimmer.Visibility = _videos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ placement

    private void PlaceOnScreen()
    {
        if (_bounds is not System.Drawing.Rectangle b) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        // Physical pixels: independent of the per-monitor DPI scaling.
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HwndTopmost, b.X, b.Y, b.Width, b.Height,
            NativeMethods.SwpShowWindow | NativeMethods.SwpNoActivate);
        PlaceOverlay();
    }

    private void PlaceOverlay()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Overlay.IsLoaded) return;
        NativeMethods.GetClientRect(hwnd, out var client);
        var origin = new NativeMethods.Point();
        NativeMethods.ClientToScreen(hwnd, ref origin);
        Overlay.PlaceAt(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 on a projector must not reveal the desktop during a show.
        if (_bounds is not null && !AllowClose) e.Cancel = true;
        base.OnClosing(e);
    }
}

/// <summary>Transparent, click-through window drawn above an output's videos.</summary>
public sealed class OverlayWindow : Window
{
    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        Stage = new Grid { ClipToBounds = true };
        Content = Stage;
        Width = Height = 1;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
            ex |= NativeMethods.WsExTransparent | NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, new IntPtr(ex));
        };
    }

    public Grid Stage { get; }

    public void PlaceAt(int x, int y, int width, int height)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, Math.Max(1, width), Math.Max(1, height),
            NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder);
    }
}

public sealed class OutputWindowManager : IDisposable
{
    private readonly Dictionary<int, OutputWindow> _windows = new();

    public event Action<string>? Warning;

    public OutputWindow GetOrCreate(int screenIndex)
    {
        var screens = Forms.Screen.AllScreens;
        if (screenIndex >= screens.Length)
        {
            Warning?.Invoke($"Lo schermo {screenIndex + 1} non è collegato: uso la finestra di anteprima.");
            screenIndex = VisualCue.PreviewWindow;
        }
        if (screenIndex < 0) screenIndex = VisualCue.PreviewWindow;

        if (_windows.TryGetValue(screenIndex, out var existing) && existing.IsVisible)
            return existing;

        var window = new OutputWindow(screenIndex, screenIndex >= 0 ? screens[screenIndex].Bounds : null);
        window.Closed += (_, _) => _windows.Remove(screenIndex);
        _windows[screenIndex] = window;
        window.Show();
        return window;
    }

    public void CloseAll()
    {
        foreach (var window in _windows.Values.ToList())
        {
            window.AllowClose = true;
            window.Close();
        }
        _windows.Clear();
    }

    public void Dispose() => CloseAll();
}
