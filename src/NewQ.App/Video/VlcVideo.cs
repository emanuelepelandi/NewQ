using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using NewQ.App.Video.Gpu;
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
}

/// <summary>Where the sound of a video goes: a WASAPI endpoint (empty = Windows default) and a linear gain.</summary>
public readonly record struct VideoAudioTarget(string DeviceId, double Gain);

/// <summary>
/// A video opened by libVLC, decoding into a <see cref="VideoFrameSource"/>. Preloading plays it muted until the
/// first frame is decoded, then pauses on frame 0, so GO starts it instantly.
/// </summary>
internal sealed class PreparedVideo : IDisposable
{
    /// <summary>libVLC players currently alive (playing or preloaded); diagnostics.</summary>
    public static int LiveCount;

    private readonly string _path;
    private readonly string _audioDevice;
    private readonly Dispatcher _dispatcher;
    private readonly Media _media;
    private bool _disposed;
    private bool _deviceSet;

    public PreparedVideo(string path, bool loop, string audioDevice, Dispatcher dispatcher)
    {
        Interlocked.Increment(ref LiveCount);
        _path = path;
        _audioDevice = audioDevice;
        _dispatcher = dispatcher;
        Loop = loop;

        Player = new MediaPlayer(VlcRuntime.LibVlc) { EnableKeyInput = false, EnableMouseInput = false, Volume = 0 };
        Frames = new VideoFrameSource(Player);
        Frames.FirstFrame += () => Post(OnFirstFrame);
        Player.Playing += (_, _) => Post(SetAudioDevice);
        Player.EncounteredError += (_, _) => Post(() => Fail("libVLC non riesce a riprodurre il file"));
        Player.EndReached += (_, _) => Post(() => Ended?.Invoke());

        _media = new Media(VlcRuntime.LibVlc, path, FromType.FromPath);
        if (loop) _media.AddOption(":input-repeat=65535"); // seamless loop handled by libVLC
        Player.Play(_media);
    }

    public MediaPlayer Player { get; }
    public VideoFrameSource Frames { get; }
    public bool Loop { get; }
    public bool IsClaimed { get; private set; }
    public bool IsReady { get; private set; }
    public string? Error { get; private set; }

    public event Action? Ended;
    public event Action<string>? Failed;

    public bool IsUsableFor(string path, bool loop, string audioDevice)
        => !_disposed && Error is null && Loop == loop && _audioDevice == audioDevice
           && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase);

    /// <summary>Starts playback (now if ready, otherwise as soon as the first frame is decoded).</summary>
    public void Claim()
    {
        IsClaimed = true;
        if (IsReady) Player.SetPause(false);
    }

    private void OnFirstFrame()
    {
        if (_disposed || IsReady) return;
        IsReady = true;
        if (IsClaimed) return; // GO came while preloading: keep playing
        Player.SetPause(true);
        Player.Time = 0; // libVLC decodes and displays frame 0 again
    }

    private void SetAudioDevice()
    {
        if (_disposed || _deviceSet || string.IsNullOrEmpty(_audioDevice)) return;
        _deviceSet = true;
        Player.SetOutputDevice(_audioDevice, null);
    }

    private void Fail(string message)
    {
        if (_disposed || Error is not null) return;
        Error = message;
        Failed?.Invoke(message);
    }

    /// <summary>libVLC events arrive on its own threads: never call back into libVLC from there.</summary>
    private void Post(Action action) => _dispatcher.BeginInvoke(action);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var player = Player;
        var media = _media;
        var frames = Frames;
        // Stop() blocks until libVLC has stopped calling back: do it off the UI thread.
        Task.Run(() =>
        {
            try { player.Stop(); } catch { /* already stopped */ }
            frames.Dispose();
            player.Dispose();
            media.Dispose();
            Interlocked.Decrement(ref LiveCount);
        });
    }
}

/// <summary>Linear volume ramp on a libVLC player, driven by a UI timer. Gain 1 = 100 %.</summary>
internal sealed class VolumeRamp
{
    private readonly MediaPlayer _player;
    private readonly DispatcherTimer _timer;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private double _from, _to, _current;
    private TimeSpan _duration;

    public VolumeRamp(MediaPlayer player, Dispatcher dispatcher)
    {
        _player = player;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Send, (_, _) => Step(), dispatcher);
        _timer.Stop();
    }

    public void RampTo(double gain, double seconds)
    {
        gain = Math.Clamp(gain, 0, 2); // libVLC goes up to 200 %
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
