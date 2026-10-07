using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using NewQ.App.Infrastructure;
using NewQ.App.Video.Gpu;
using NewQ.Core.Model;
using Forms = System.Windows.Forms;

namespace NewQ.App.Video;

public sealed record ScreenOption(int Index, string Label)
{
    public static IReadOnlyList<ScreenOption> All()
    {
        var list = new List<ScreenOption> { new(VideoOutput.PreviewWindow, "Finestra di anteprima") };
        var screens = Forms.Screen.AllScreens;
        for (var i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            list.Add(new ScreenOption(i, $"Schermo {i + 1} · {s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? " (principale)" : "")}"));
        }
        return list;
    }
}

/// <summary>A native child window the GPU renderer presents into.</summary>
internal sealed class RenderSurface : HwndHost
{
    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var hwnd = NativeMethods.CreateWindowEx(0, NativeMethods.BlackSurfaceClass, "",
            NativeMethods.WsChild | NativeMethods.WsVisible | NativeMethods.WsClipChildren | NativeMethods.WsClipSiblings,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Impossibile creare la superficie video.");
        return new HandleRef(this, hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd) => NativeMethods.DestroyWindow(hwnd.Handle);
}

/// <summary>
/// One physical output: a monitor (borderless, topmost, covering the whole screen) or the resizable preview
/// window. Everything shown on it is composed by its <see cref="GpuRenderer"/>.
/// </summary>
public sealed class OutputWindow : Window
{
    private readonly System.Drawing.Rectangle? _bounds;
    private readonly CompositionHub _hub;
    private readonly RenderSurface _surface = new();
    private GpuRenderer? _renderer;

    public OutputWindow(int screenIndex, System.Drawing.Rectangle? bounds, CompositionHub hub)
    {
        ScreenIndex = screenIndex;
        _bounds = bounds;
        _hub = hub;
        Content = _surface;
        Background = Brushes.Black;
        ShowActivated = false;

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

        _surface.Loaded += (_, _) =>
        {
            _renderer ??= new GpuRenderer(_surface.Handle, screenIndex, hub);
            _renderer.Failed += message => RendererFailed?.Invoke(message);
        };
    }

    public int ScreenIndex { get; }

    /// <summary>When false (default), outputs are only hidden or closed by the application.</summary>
    public bool AllowClose { get; set; }

    public event Action<string>? RendererFailed;

    public long FramesPresented => _renderer?.FramesPresented ?? 0;

    private void PlaceOnScreen()
    {
        if (_bounds is not System.Drawing.Rectangle b) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        // Physical pixels: independent of the per-monitor DPI scaling.
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HwndTopmost, b.X, b.Y, b.Width, b.Height,
            NativeMethods.SwpShowWindow | NativeMethods.SwpNoActivate);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            // Alt+F4 on a projector must not reveal the desktop; the preview window just hides.
            e.Cancel = true;
            if (_bounds is null) Hide();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _renderer?.Dispose();
        _renderer = null;
        base.OnClosed(e);
    }
}

/// <summary>Keeps one output window open for every screen used by a video route.</summary>
public sealed class OutputWindowManager : IDisposable
{
    private readonly Dictionary<int, OutputWindow> _windows = new();

    public OutputWindowManager(CompositionHub hub)
    {
        Hub = hub;
    }

    public CompositionHub Hub { get; }

    public event Action<string>? Warning;

    public IReadOnlyCollection<OutputWindow> Windows => _windows.Values;

    /// <summary>Opens/closes output windows to match the screens the routes use. UI thread.</summary>
    public void Sync()
    {
        var screens = Forms.Screen.AllScreens;
        var wanted = new HashSet<int>();
        foreach (var screen in Hub.UsedScreens())
        {
            if (screen >= screens.Length)
            {
                Warning?.Invoke($"Lo schermo {screen + 1} non è collegato: le sue uscite non sono visibili.");
                continue;
            }
            wanted.Add(screen);
        }

        foreach (var index in _windows.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            _windows[index].AllowClose = true;
            _windows[index].Close();
            _windows.Remove(index);
        }

        foreach (var index in wanted.Where(i => !_windows.ContainsKey(i)))
        {
            var window = new OutputWindow(index, index >= 0 ? screens[index].Bounds : null, Hub);
            window.RendererFailed += message => window.Dispatcher.BeginInvoke(() => Warning?.Invoke(message));
            _windows[index] = window;
            window.Show();
        }
    }

    /// <summary>Shows the preview window again if the user hid it and something is now played there.</summary>
    public void EnsureVisible(Guid routeId)
    {
        if (_windows.Count == 0) Sync(); // reopened after "Chiudi uscite video"
        foreach (var (route, output) in Hub.UsedScreens().SelectMany(s => Hub.SnapshotFor(s)))
            if (route.RouteId == routeId && _windows.TryGetValue(output.ScreenIndex, out var window) && !window.IsVisible)
                window.Show();
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
