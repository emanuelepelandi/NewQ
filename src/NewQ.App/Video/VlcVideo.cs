using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using NAudio.CoreAudioApi;
using NewQ.App.Video.Gpu;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace NewQ.App.Video;

/// <summary>The shared libVLC instance. Created in background at startup (loading plugins takes ~1 s).</summary>
public static class VlcRuntime
{
    private static readonly Lazy<LibVLC> Instance = new(() =>
    {
        LibVLCSharp.Shared.Core.Initialize();
        // DirectSound, not the default WASAPI output (mmdevice): mmdevice puts every player of the process in one
        // Windows audio session and sets the volume on that session, so muting a preloading video muted the one
        // playing too. DirectSound keeps one volume per player.
        return new LibVLC("--no-osd", "--no-video-title-show", "--no-snapshot-preview", "--no-stats", "--quiet", "--aout=directsound");
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

/// <summary>libVLC's DirectSound output names a sound card by the endpoint GUID, not by the WASAPI id the routes store.</summary>
internal static class DirectSoundDevices
{
    private static readonly PropertyKey EndpointGuid = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 4);
    private static readonly ConcurrentDictionary<string, string?> Cache = new();

    /// <summary>The DirectSound id of a WASAPI endpoint; null = the Windows default (empty id, or a missing card).</summary>
    public static string? Of(string wasapiId)
    {
        if (string.IsNullOrEmpty(wasapiId)) return null;
        return Cache.GetOrAdd(wasapiId, id =>
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDevice(id);
                return device.Properties[EndpointGuid]?.Value?.ToString()?.ToUpperInvariant();
            }
            catch { return null; } // unplugged: the default device, like libVLC did before
        });
    }
}

/// <summary>What libVLC opens: a file, or a live capture device (DirectShow).</summary>
/// <param name="Key">Identity of the source with all its options: two cues with the same key can share it.</param>
internal sealed record VideoSource(string Location, FromType Type, bool Loop, bool IsLive, string Key, IReadOnlyList<string> Options)
{
    public static VideoSource File(string path, bool loop)
        => new(path, FromType.FromPath, loop, false, $"file|{path.ToLowerInvariant()}|{loop}",
            loop ? new[] { ":input-repeat=65535" } : Array.Empty<string>()); // seamless loop handled by libVLC

    /// <summary>A DirectShow capture device (camera, HDMI capture card) with optional size, rate and audio input.</summary>
    public static VideoSource Capture(string device, string audioDevice, string resolution, double frameRate)
    {
        var options = new List<string>
        {
            ":dshow-vdev=" + device,
            ":dshow-adev=" + (string.IsNullOrWhiteSpace(audioDevice) ? "none" : audioDevice),
            ":live-caching=40", // ms of buffering: low latency for a live picture
        };
        if (resolution.Length > 0) options.Add(":dshow-size=" + resolution);
        if (frameRate > 0) options.Add(":dshow-fps=" + frameRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new("dshow://", FromType.FromLocation, false, true,
            $"live|{device}|{audioDevice}|{resolution}|{frameRate}".ToLowerInvariant(), options);
    }
}

/// <summary>
/// A video opened by libVLC, decoding into a <see cref="VideoFrameSource"/>. Preloading plays it muted until the
/// first frame is decoded, then pauses on frame 0, so GO starts it instantly. A live source can't pause: preloaded,
/// it keeps running muted and unseen, so GO shows the current picture at once.
/// </summary>
internal sealed class PreparedVideo : IDisposable
{
    /// <summary>libVLC players currently alive (playing or preloaded); diagnostics.</summary>
    public static int LiveCount;

    private readonly VideoSource _source;
    private readonly string _audioDevice;
    private readonly Dispatcher _dispatcher;
    private readonly Media _media;
    private bool _disposed;

    public PreparedVideo(string path, bool loop, string audioDevice, Dispatcher dispatcher)
        : this(VideoSource.File(path, loop), audioDevice, dispatcher) { }

    public PreparedVideo(VideoSource source, string audioDevice, Dispatcher dispatcher)
    {
        Interlocked.Increment(ref LiveCount);
        _source = source;
        _audioDevice = audioDevice;
        _dispatcher = dispatcher;
        Loop = source.Loop;

        Player = new MediaPlayer(VlcRuntime.LibVlc) { EnableKeyInput = false, EnableMouseInput = false, Volume = 0 };
        // Before Play, so the sound never starts on the default device.
        if (DirectSoundDevices.Of(audioDevice) is string device) Player.SetOutputDevice(device, null);
        Frames = new VideoFrameSource(Player);
        Frames.FirstFrame += () => Post(OnFirstFrame);
        Player.EncounteredError += (_, _) => Post(() => Fail(source.IsLive
            ? "sorgente non disponibile (scollegata o in uso?)"
            : "file non riproducibile"));
        Player.EndReached += (_, _) => Post(() => Ended?.Invoke());

        _media = new Media(VlcRuntime.LibVlc, source.Location, source.Type);
        foreach (var option in source.Options) _media.AddOption(option);
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

    public bool IsLive => _source.IsLive;

    public bool IsUsableFor(string path, bool loop, string audioDevice) => IsUsableFor(VideoSource.File(path, loop), audioDevice);

    public bool IsUsableFor(VideoSource source, string audioDevice)
        => !_disposed && Error is null && _audioDevice == audioDevice && _source.Key == source.Key;

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
        if (IsClaimed || IsLive) return; // GO came while preloading, or a live source: keep playing
        Player.SetPause(true);
        Player.Time = 0; // libVLC decodes and displays frame 0 again
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
