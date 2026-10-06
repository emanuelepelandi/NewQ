using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using NewQ.App.Infrastructure;
using NewQ.Core;
using NewQ.Core.Engine;
using NewQ.Core.Model;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace NewQ.App.Video;

/// <summary>The shared libVLC instance. Created in background at startup (loading plugins takes ~1 s).</summary>
public static class VlcRuntime
{
    private static readonly Lazy<LibVLC> Instance = new(() =>
    {
        LibVLCSharp.Shared.Core.Initialize();
        return new LibVLC("--no-osd", "--no-video-title-show", "--no-snapshot-preview", "--no-stats", "--quiet");
    });

    public static LibVLC LibVlc => Instance.Value;

    private static readonly System.Diagnostics.Stopwatch DebugClock = System.Diagnostics.Stopwatch.StartNew();
    private static readonly string? DebugFile = Environment.GetEnvironmentVariable("NEWQ_DEBUG") is null
        ? null : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "newq-debug.log");

    /// <summary>Timestamped diagnostics, only when the NEWQ_DEBUG environment variable is set.</summary>
    public static void Debug(string message)
    {
        if (DebugFile is null) return;
        lock (DebugClock) System.IO.File.AppendAllText(DebugFile, $"{DebugClock.Elapsed.TotalMilliseconds,10:F1} [{Environment.CurrentManagedThreadId,2}] {message}{Environment.NewLine}");
    }

    /// <summary>Starts loading libVLC without blocking.</summary>
    public static void WarmUp() => Task.Run(() => _ = Instance.Value);

    public static void Shutdown()
    {
        if (Instance.IsValueCreated) Instance.Value.Dispose();
    }
}

/// <summary>A native child window libVLC renders into (black until the first frame).</summary>
internal sealed class VideoSurface : HwndHost
{
    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var hwnd = NativeMethods.CreateWindowEx(0, NativeMethods.BlackSurfaceClass, "",
            NativeMethods.WsChild | NativeMethods.WsVisible | NativeMethods.WsClipChildren
            | NativeMethods.WsClipSiblings,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Impossibile creare la superficie video.");
        return new HandleRef(this, hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd) => NativeMethods.DestroyWindow(hwnd.Handle);

    public void SendToBottom()
    {
        if (Handle == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(Handle, NativeMethods.HwndBottom, 0, 0, 0, 0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
    }

    public void BringToTop()
    {
        if (Handle == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTop, 0, 0, 0, 0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
    }
}

/// <summary>What the output window needs to stack videos.</summary>
internal interface IVideoPlane
{
    Rectangle Dimmer { get; }
    void RaiseSurface();
}

/// <summary>
/// A video opened by libVLC on its output (under the black cover), muted and paused on its first frame.
/// When a <see cref="VideoLayer"/> claims it, playback starts within a frame or two.
/// </summary>
internal sealed class PreparedVideo : IDisposable
{
    private readonly string _path;
    private readonly int _screenIndex;
    private readonly Media _media;
    private bool _disposed;

    public PreparedVideo(string path, int screenIndex, OutputWindow window, bool loop)
    {
        _path = path;
        _screenIndex = screenIndex;
        Loop = loop;
        Window = window;

        // Visible but at the bottom of the stack and under the output's black cover: libVLC presents the first
        // frame while paused, so revealing it on GO is instant.
        Surface = new VideoSurface();
        window.VideoHost.Children.Add(Surface);
        window.Closed += OnWindowClosed;

        Player = new MediaPlayer(VlcRuntime.LibVlc)
        {
            EnableKeyInput = false,
            EnableMouseInput = false,
            Volume = 0,
        };
        Player.Vout += (_, e) => { VlcRuntime.Debug($"event Vout count={e.Count}"); if (e.Count > 0) Post(OnFirstFrame); };
        Player.Playing += (_, _) => VlcRuntime.Debug("event Playing");
        Player.Paused += (_, _) => VlcRuntime.Debug("event Paused");
        Player.EncounteredError += (_, _) => Post(() => Fail("libVLC non riesce a riprodurre il file"));
        Player.EndReached += (_, _) => Post(() => Ended?.Invoke());

        _media = new Media(VlcRuntime.LibVlc, path, FromType.FromPath);
        if (loop) _media.AddOption(":input-repeat=65535"); // seamless loop handled by libVLC

        // The native window only exists once the surface is connected to the output window.
        if (Surface.Handle != IntPtr.Zero) StartPlayer();
        else Surface.Loaded += OnSurfaceLoaded;
    }

    private void OnSurfaceLoaded(object sender, RoutedEventArgs e)
    {
        Surface.Loaded -= OnSurfaceLoaded;
        if (!_disposed) StartPlayer();
    }

    private void StartPlayer()
    {
        VlcRuntime.Debug($"StartPlayer {_path}");
        Surface.SendToBottom();
        Player.Hwnd = Surface.Handle;
        Player.Play(_media);
    }

    public OutputWindow Window { get; }
    public VideoSurface Surface { get; }
    public MediaPlayer Player { get; }
    public bool Loop { get; }
    public bool IsClaimed { get; private set; }
    public bool IsReady { get; private set; }
    public string? Error { get; private set; }

    public event Action? Ended;
    public event Action<string>? Failed;

    public bool IsUsableFor(string path, int screenIndex, bool loop)
        => !_disposed && Error is null && Window.IsVisible && _screenIndex == screenIndex && Loop == loop
           && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase);

    /// <summary>Starts playback (now if ready, otherwise as soon as the first frame is decoded).</summary>
    public void Claim()
    {
        VlcRuntime.Debug($"Claim ready={IsReady}");
        IsClaimed = true;
        if (IsReady) Player.SetPause(false);
    }

    /// <summary>
    /// The video output exists and shows the first frame: freeze it and rewind, so decoder and output are
    /// already running when GO arrives. (":start-paused" would pause before the decoder even starts.)
    /// </summary>
    private void OnFirstFrame()
    {
        if (_disposed || IsReady) return;
        IsReady = true;
        if (IsClaimed) return; // GO came while preloading: just keep playing
        Player.SetPause(true);
        Player.Time = 0;
        VlcRuntime.Debug("preload ready (paused at 0)");
    }

    private void Fail(string message)
    {
        if (_disposed || Error is not null) return;
        Error = message;
        Failed?.Invoke(message);
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Fail("finestra di uscita chiusa");

    /// <summary>libVLC events arrive on its own threads: never call back into libVLC from there.</summary>
    private void Post(Action action) => Window.Dispatcher.BeginInvoke(action);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Window.Closed -= OnWindowClosed;
        Surface.Visibility = Visibility.Hidden;

        // Stop() blocks until libVLC has released the window: do it off the UI thread, then destroy the surface.
        var player = Player;
        var media = _media;
        var surface = Surface;
        var host = Window.VideoHost;
        Task.Run(() =>
        {
            try { player.Stop(); } catch { /* already stopped */ }
            Window.Dispatcher.BeginInvoke(() =>
            {
                host.Children.Remove(surface);
                surface.Dispose();
                player.Dispose();
                media.Dispose();
            });
        });
    }
}

/// <summary>
/// A playing video cue. Opacity is rendered as a black "dimmer" over the video (fade from/to black),
/// volume is ramped on the libVLC player.
/// </summary>
internal sealed class VideoLayer : VisualLayer, IVideoPlane
{
    private readonly VideoCue _cue;
    private readonly PreparedVideo _prepared;
    private readonly VolumeRamp _volume;
    private bool _paused;
    private bool _autoFading; // the automatic fade-out before the end (as opposed to a stop/fade cue)

    public VideoLayer(VideoCue cue, PreparedVideo prepared, CueContext context) : base(cue, prepared.Window, context)
    {
        _cue = cue;
        _prepared = prepared;
        _volume = new VolumeRamp(prepared.Player, prepared.Window.Dispatcher);

        Dimmer = new Rectangle { Fill = Brushes.Black, Opacity = 1, IsHitTestVisible = false };
        Panel.SetZIndex(Dimmer, 0); // below every image
        Window.Stage.Children.Add(Dimmer);

        ApplyFit(prepared.Player, cue.FitMode, Window);
        prepared.Ended += OnEnded;
        prepared.Failed += OnFailed;

        Window.BringVideoToTop(this);
        prepared.Claim();

        StartTick();
        AnimateOpacity(cue.Opacity, cue.FadeIn);
        _volume.RampTo(Decibels.ToGain(cue.VolumeDb), cue.FadeIn);
    }

    public Rectangle Dimmer { get; }

    public void RaiseSurface() => _prepared.Surface.BringToTop();

    public override TimeSpan Elapsed => TimeSpan.FromMilliseconds(Math.Max(0, _prepared.Player.Time));

    public override TimeSpan? Duration
    {
        get
        {
            if (_cue.Loop) return null;
            var length = _prepared.Player.Length;
            return length > 0 ? TimeSpan.FromMilliseconds(length) : null;
        }
    }

    public override void Pause()
    {
        _paused = true;
        _prepared.Player.SetPause(true);
    }

    public override void Resume()
    {
        _paused = false;
        _prepared.Player.SetPause(false);
    }

    public override bool CanSeek => !_cue.Loop && _prepared.Player.Length > 0;

    public override void Seek(TimeSpan position)
    {
        var length = _prepared.Player.Length;
        if (length <= 0) return;
        // Stay a little before the end so scrubbing to the far right doesn't end the cue.
        var ms = (long)Math.Clamp(position.TotalMilliseconds, 0, Math.Max(0, length - 200));
        _prepared.Player.Time = ms;

        if (_autoFading && (length - ms) / 1000.0 > _cue.FadeOut)
        {
            // Moved back before the automatic end fade: bring picture and sound back.
            _autoFading = false;
            FadingOut = false;
            AnimateOpacity(_cue.Opacity, 0);
            _volume.RampTo(Decibels.ToGain(_cue.VolumeDb), 0);
        }
    }

    protected override void AnimateOpacity(double to, double seconds, Action? completed = null)
    {
        var dimmerTarget = 1 - Math.Clamp(to, 0, 1);
        if (seconds <= 0)
        {
            Dimmer.BeginAnimation(UIElement.OpacityProperty, null);
            Dimmer.Opacity = dimmerTarget;
            completed?.Invoke();
            return;
        }
        var animation = new DoubleAnimation(dimmerTarget, TimeSpan.FromSeconds(seconds)) { FillBehavior = FillBehavior.HoldEnd };
        if (completed is not null) animation.Completed += (_, _) => completed();
        Dimmer.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    protected override void AnimateVolume(double gain, double seconds) => _volume.RampTo(gain, seconds);

    protected override void OnTick()
    {
        if (_paused || _cue.Loop || _cue.FadeOut <= 0 || FadingOut) return;
        var length = _prepared.Player.Length;
        var time = _prepared.Player.Time;
        if (length <= 0 || time < 0) return;
        var remaining = (length - time) / 1000.0;
        if (remaining > _cue.FadeOut) return;
        FadingOut = true;
        _autoFading = true;
        AnimateOpacity(0, Math.Max(0, remaining));
        _volume.RampTo(0, Math.Max(0, remaining));
    }

    private void OnEnded() => Finish();

    private void OnFailed(string message)
    {
        Context.Error($"riproduzione video fallita: {message}");
        Finish();
    }

    protected override void OnFinish()
    {
        _prepared.Ended -= OnEnded;
        _prepared.Failed -= OnFailed;
        _volume.Stop();
        Window.RemoveVideo(this);
        Dimmer.BeginAnimation(UIElement.OpacityProperty, null);
        Window.Stage.Children.Remove(Dimmer);
        _prepared.Dispose();
    }

    private static void ApplyFit(MediaPlayer player, FitMode mode, OutputWindow window)
    {
        var aspect = $"{Math.Max(1, (int)window.VideoHost.ActualWidth)}:{Math.Max(1, (int)window.VideoHost.ActualHeight)}";
        switch (mode)
        {
            case FitMode.Fill: player.CropGeometry = aspect; break;   // crop to the screen shape, then fit = fill
            case FitMode.Stretch: player.AspectRatio = aspect; break; // force the screen aspect ratio
            case FitMode.Original: player.Scale = 1; break;           // 1 video pixel = 1 screen pixel
            default: player.Scale = 0; break;                          // fit (letterbox)
        }
    }
}

/// <summary>Linear volume ramp on a libVLC player (0–100), driven by a UI timer.</summary>
internal sealed class VolumeRamp
{
    private readonly MediaPlayer _player;
    private readonly DispatcherTimer _timer;
    private double _from, _to, _current;
    private TimeSpan _duration;
    private readonly System.Diagnostics.Stopwatch _clock = new();

    public VolumeRamp(MediaPlayer player, Dispatcher dispatcher)
    {
        _player = player;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Send, (_, _) => Step(), dispatcher);
        _timer.Stop();
    }

    public void RampTo(double gain, double seconds)
    {
        gain = Math.Clamp(gain, 0, 1);
        _from = _current;
        _to = gain;
        _duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        _clock.Restart();
        if (seconds <= 0) Set(gain);
        else _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Step()
    {
        var t = _duration.TotalMilliseconds <= 0 ? 1 : Math.Min(1, _clock.Elapsed.TotalMilliseconds / _duration.TotalMilliseconds);
        Set(_from + (_to - _from) * t);
        if (t >= 1) _timer.Stop();
    }

    private void Set(double gain)
    {
        _current = gain;
        try { _player.Volume = (int)Math.Round(gain * 100); } catch { /* player disposed */ }
    }
}
