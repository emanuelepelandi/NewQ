using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NewQ.App.Video.Gpu;
using NewQ.Core;
using NewQ.Core.Engine;
using NewQ.Core.Model;

namespace NewQ.App.Video;

/// <summary>
/// Plays video and image cues on their video route through the GPU compositor. UI thread only.
/// Videos are preloaded paused on their first frame when they become the next GO; images are decoded in background.
/// </summary>
public sealed class VisualCuePlayer : IPreloadingCuePlayer
{
    private readonly CompositionHub _hub;
    private readonly OutputWindowManager _outputs;
    private readonly Func<Guid?, VideoAudioTarget> _audioTarget;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<Guid, PreparedVideo> _videos = new();
    private readonly Dictionary<string, Task<ImageFrameSource>> _images = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="audioTarget">Resolves a cue's audio route to a sound card and gain (route gain × master).</param>
    public VisualCuePlayer(CompositionHub hub, OutputWindowManager outputs, Func<Guid?, VideoAudioTarget> audioTarget, Dispatcher dispatcher)
    {
        _hub = hub;
        _outputs = outputs;
        _audioTarget = audioTarget;
        _dispatcher = dispatcher;
    }

    public bool CanPlay(Cue cue) => cue is VideoCue or ImageCue;

    // ------------------------------------------------------------------ preload

    public void Preload(Cue cue, Workspace workspace)
    {
        var visual = (VisualCue)cue;
        var path = ResolveExisting(visual, workspace.ResolvePath);

        switch (visual)
        {
            case VideoCue video:
                var device = _audioTarget(video.AudioRouteId).DeviceId;
                if (_videos.TryGetValue(video.Id, out var prepared))
                {
                    if (prepared.IsUsableFor(path, video.Loop, device)) return;
                    prepared.Dispose();
                }
                _videos[video.Id] = new PreparedVideo(path, video.Loop, device, _dispatcher);
                break;

            case ImageCue:
                if (!_images.ContainsKey(path)) _images[path] = Task.Run(() => LoadImage(path));
                break;
        }
    }

    public void ReleasePreloads(IReadOnlyCollection<Cue> keep)
    {
        var keepIds = keep.Select(c => c.Id).ToHashSet();
        foreach (var id in _videos.Keys.Where(id => !keepIds.Contains(id)).ToList())
        {
            _videos[id].Dispose();
            _videos.Remove(id);
        }
        if (_images.Count > 16) _images.Clear(); // paths can't be resolved here: keep the cache small
    }

    // ------------------------------------------------------------------ start

    public IActiveCue Start(Cue cue, CueContext context)
    {
        var visual = (VisualCue)cue;
        var path = ResolveExisting(visual, context.ResolvePath);
        var routeId = context.Workspace.ResolveVideoRoute(visual.VideoRouteId).Id;
        _outputs.EnsureVisible(routeId);

        switch (visual)
        {
            case VideoCue video:
                var audio = _audioTarget(video.AudioRouteId);
                if (_videos.Remove(video.Id, out var prepared) && !prepared.IsUsableFor(path, video.Loop, audio.DeviceId))
                {
                    prepared.Dispose();
                    prepared = null;
                }
                prepared ??= new PreparedVideo(path, video.Loop, audio.DeviceId, _dispatcher);
                return new VideoLayer(video, prepared, _hub, routeId, audio.Gain, context);

            case ImageCue image:
                var source = _images.TryGetValue(path, out var task) && task.IsCompletedSuccessfully ? task.Result : LoadImage(path);
                return new ImageLayer(image, source, _hub, routeId, context);

            default:
                throw new NotSupportedException();
        }
    }

    private static string ResolveExisting(VisualCue cue, Func<string, string> resolvePath)
    {
        if (string.IsNullOrWhiteSpace(cue.FilePath)) throw new InvalidOperationException("Nessun file selezionato.");
        var path = resolvePath(cue.FilePath);
        if (!File.Exists(path)) throw new FileNotFoundException($"File non trovato: {path}");
        return path;
    }

    /// <summary>Decodes an image to BGRA; safe on any thread.</summary>
    private static ImageFrameSource LoadImage(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        return new ImageFrameSource(bitmap);
    }
}

/// <summary>Common lifecycle of a visual cue: a layer on its route, fades, timed finish.</summary>
internal abstract class VisualLayer : IActiveCue
{
    private readonly DispatcherTimer _tick;
    private DispatcherTimer? _finishTimer;
    private DispatcherTimer? _completionTimer;
    private bool _finished;

    protected VisualLayer(VisualCue cue, CompositionHub hub, Guid routeId, IFrameSource source, CueContext context)
    {
        VisualCue = cue;
        Hub = hub;
        RouteId = routeId;
        Context = context;
        Layer = hub.AddLayer(routeId, source, null, cue.Layer, cue.FitMode);
        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _tick.Tick += (_, _) => { if (!_finished) OnTick(); };
        _tick.Start();
    }

    protected VisualCue VisualCue { get; }
    protected CompositionHub Hub { get; }
    protected Guid RouteId { get; }
    protected CueContext Context { get; }
    protected CompositionLayer Layer { get; }
    protected bool FadingOut { get; set; }

    public abstract TimeSpan Elapsed { get; }
    public abstract TimeSpan? Duration { get; }
    public abstract void Pause();
    public abstract void Resume();

    public virtual bool CanSeek => false;
    public virtual void Seek(TimeSpan position) { }

    protected virtual void AnimateVolume(double gain, double seconds) { }
    protected virtual void OnTick() { }
    protected virtual void OnFinish() { }

    /// <summary>Animates the opacity on the GPU (evaluated at each screen refresh); optional callback at the end.</summary>
    protected void AnimateOpacity(double to, double seconds, Action? completed = null)
    {
        Layer.AnimateOpacity(to, seconds, Hub.Now);
        _completionTimer?.Stop();
        if (completed is null) return;
        if (seconds <= 0) { completed(); return; }
        _completionTimer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(seconds) };
        _completionTimer.Tick += (_, _) => { _completionTimer?.Stop(); completed(); };
        _completionTimer.Start();
    }

    public void Stop(TimeSpan fade)
    {
        if (_finished) return;
        if (fade <= TimeSpan.Zero)
        {
            Finish();
            return;
        }
        FadingOut = true;
        AnimateVolume(0, fade.TotalSeconds);
        AnimateOpacity(0, fade.TotalSeconds, Finish);
    }

    public void Fade(FadeRequest request)
    {
        if (_finished) return;
        var seconds = request.Duration.TotalSeconds;
        if (request.Opacity is double opacity) AnimateOpacity(opacity, seconds);
        if (request.VolumeDb is double db) AnimateVolume(Decibels.ToGain(db), seconds);
        if (request.StopWhenDone)
        {
            FadingOut = true;
            FinishAfter(request.Duration);
        }
    }

    protected void FinishAfter(TimeSpan delay)
    {
        _finishTimer?.Stop();
        _finishTimer = new DispatcherTimer(DispatcherPriority.Send) { Interval = delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1) };
        _finishTimer.Tick += (_, _) => Finish();
        _finishTimer.Start();
    }

    protected void Finish()
    {
        if (_finished) return;
        _finished = true;
        _tick.Stop();
        _finishTimer?.Stop();
        _completionTimer?.Stop();
        Hub.RemoveLayer(RouteId, Layer);
        try { OnFinish(); } catch { /* already released */ }
        Context.Completed();
    }
}

internal sealed class ImageLayer : VisualLayer
{
    private readonly ImageCue _cue;
    private readonly Stopwatch _clock = new();

    public ImageLayer(ImageCue cue, ImageFrameSource source, CompositionHub hub, Guid routeId, CueContext context)
        : base(cue, hub, routeId, source, context)
    {
        _cue = cue;
        AnimateOpacity(cue.Opacity, cue.FadeIn);
        _clock.Start();
    }

    public override TimeSpan Elapsed => _clock.Elapsed;
    public override TimeSpan? Duration => _cue.HoldDuration > 0 ? TimeSpan.FromSeconds(_cue.HoldDuration) : null;
    public override void Pause() => _clock.Stop();
    public override void Resume() => _clock.Start();

    protected override void OnTick()
    {
        if (_cue.HoldDuration <= 0 || FadingOut || !_clock.IsRunning) return;
        var remaining = _cue.HoldDuration - _clock.Elapsed.TotalSeconds;
        if (remaining > _cue.FadeOut) return;
        FadingOut = true;
        AnimateOpacity(0, Math.Max(0, remaining), Finish);
    }
}

internal sealed class VideoLayer : VisualLayer
{
    private readonly VideoCue _cue;
    private readonly PreparedVideo _prepared;
    private readonly VolumeRamp _volume;
    private readonly double _routeGain;
    private bool _paused;
    private bool _autoFading; // the automatic fade-out before the end (as opposed to a stop/fade cue)

    public VideoLayer(VideoCue cue, PreparedVideo prepared, CompositionHub hub, Guid routeId, double routeGain, CueContext context)
        : base(cue, hub, routeId, prepared.Frames, context)
    {
        _cue = cue;
        _prepared = prepared;
        _routeGain = routeGain;
        _volume = new VolumeRamp(prepared.Player, Dispatcher.CurrentDispatcher);

        prepared.Ended += OnEnded;
        prepared.Failed += OnFailed;
        prepared.Claim();

        AnimateOpacity(cue.Opacity, cue.FadeIn);
        _volume.RampTo(Decibels.ToGain(cue.VolumeDb) * routeGain, cue.FadeIn);
    }

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
            _volume.RampTo(Decibels.ToGain(_cue.VolumeDb) * _routeGain, 0);
        }
    }

    protected override void AnimateVolume(double gain, double seconds) => _volume.RampTo(gain * _routeGain, seconds);

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
        _prepared.Dispose();
    }
}
