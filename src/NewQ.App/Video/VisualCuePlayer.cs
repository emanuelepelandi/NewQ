using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NewQ.Core;
using NewQ.Core.Engine;
using NewQ.Core.Model;

namespace NewQ.App.Video;

/// <summary>
/// Plays video (libVLC) and image (WPF) cues on the output windows. UI thread only.
/// Videos are preloaded paused on their first frame when they become the next GO; images are decoded
/// in background.
/// </summary>
public sealed class VisualCuePlayer : IPreloadingCuePlayer
{
    private readonly OutputWindowManager _outputs;
    private readonly Dictionary<Guid, PreparedVideo> _videos = new();
    private readonly Dictionary<string, Task<BitmapSource>> _images = new(StringComparer.OrdinalIgnoreCase);

    public VisualCuePlayer(OutputWindowManager outputs) => _outputs = outputs;

    public bool CanPlay(Cue cue) => cue is VideoCue or ImageCue;

    // ------------------------------------------------------------------ preload

    public void Preload(Cue cue, Func<string, string> resolvePath)
    {
        var visual = (VisualCue)cue;
        var path = ResolveExisting(visual, resolvePath);

        switch (visual)
        {
            case VideoCue video:
                if (_videos.TryGetValue(video.Id, out var prepared))
                {
                    if (prepared.IsUsableFor(path, video.ScreenIndex, video.Loop)) return;
                    prepared.Dispose();
                }
                _videos[video.Id] = new PreparedVideo(path, video.ScreenIndex, _outputs.GetOrCreate(video.ScreenIndex), video.Loop);
                break;

            case ImageCue:
                if (!_images.ContainsKey(path)) _images[path] = Task.Run(() => LoadBitmap(path));
                _outputs.GetOrCreate(visual.ScreenIndex); // have the output window ready before GO
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

        // Image paths can't be resolved here; keep the cache small instead.
        if (_images.Count > 16) _images.Clear();
    }

    // ------------------------------------------------------------------ start

    public IActiveCue Start(Cue cue, CueContext context)
    {
        var visual = (VisualCue)cue;
        var path = ResolveExisting(visual, context.ResolvePath);

        switch (visual)
        {
            case VideoCue video:
                if (_videos.Remove(video.Id, out var prepared) && !prepared.IsUsableFor(path, video.ScreenIndex, video.Loop))
                {
                    prepared.Dispose();
                    prepared = null;
                }
                prepared ??= new PreparedVideo(path, video.ScreenIndex, _outputs.GetOrCreate(video.ScreenIndex), video.Loop);
                return new VideoLayer(video, prepared, context);

            case ImageCue image:
                var bitmap = _images.TryGetValue(path, out var task) && task.IsCompletedSuccessfully
                    ? task.Result
                    : LoadBitmap(path);
                return new ImageLayer(image, bitmap, _outputs.GetOrCreate(image.ScreenIndex), context);

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

    /// <summary>Decodes an image; safe on any thread (the result is frozen).</summary>
    private static BitmapSource LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    internal static Stretch ToStretch(FitMode mode) => mode switch
    {
        FitMode.Fill => Stretch.UniformToFill,
        FitMode.Stretch => Stretch.Fill,
        FitMode.Original => Stretch.None,
        _ => Stretch.Uniform,
    };
}

/// <summary>Common lifecycle of a visual cue on an output: fades, timed finish, window closing.</summary>
internal abstract class VisualLayer : IActiveCue
{
    private readonly DispatcherTimer _tick;
    private DispatcherTimer? _finishTimer;
    private bool _finished;

    protected VisualLayer(VisualCue cue, OutputWindow window, CueContext context)
    {
        VisualCue = cue;
        Window = window;
        Context = context;
        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _tick.Tick += (_, _) => { if (!_finished) OnTick(); };
        window.Closed += OnWindowClosed;
    }

    protected VisualCue VisualCue { get; }
    protected OutputWindow Window { get; }
    protected CueContext Context { get; }
    protected bool FadingOut { get; set; }

    public abstract TimeSpan Elapsed { get; }
    public abstract TimeSpan? Duration { get; }
    public abstract void Pause();
    public abstract void Resume();

    public virtual bool CanSeek => false;
    public virtual void Seek(TimeSpan position) { }

    /// <summary>Animates the visible opacity (0–1) of the cue.</summary>
    protected abstract void AnimateOpacity(double to, double seconds, Action? completed = null);

    protected virtual void AnimateVolume(double gain, double seconds) { }
    protected virtual void OnTick() { }
    protected virtual void OnFinish() { }

    protected void StartTick() => _tick.Start();

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
        Window.Closed -= OnWindowClosed;
        try { OnFinish(); } catch { /* output already gone */ }
        Context.Completed();
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Finish();
}

internal sealed class ImageLayer : VisualLayer
{
    /// <summary>Images are drawn above videos (see <see cref="OutputWindow"/>).</summary>
    private const int ImageZBase = 1000;

    private readonly ImageCue _cue;
    private readonly Image _image;
    private readonly Stopwatch _clock = new();

    public ImageLayer(ImageCue cue, BitmapSource bitmap, OutputWindow window, CueContext context) : base(cue, window, context)
    {
        _cue = cue;
        _image = new Image { Source = bitmap, Stretch = VisualCuePlayer.ToStretch(cue.FitMode), Opacity = 0, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Panel.SetZIndex(_image, ImageZBase + cue.Layer);
        window.Stage.Children.Add(_image);

        AnimateOpacity(cue.Opacity, cue.FadeIn);
        StartTick();
        _clock.Start();
    }

    public override TimeSpan Elapsed => _clock.Elapsed;
    public override TimeSpan? Duration => _cue.HoldDuration > 0 ? TimeSpan.FromSeconds(_cue.HoldDuration) : null;
    public override void Pause() => _clock.Stop();
    public override void Resume() => _clock.Start();

    protected override void AnimateOpacity(double to, double seconds, Action? completed = null)
    {
        if (seconds <= 0)
        {
            _image.BeginAnimation(UIElement.OpacityProperty, null);
            _image.Opacity = to;
            completed?.Invoke();
            return;
        }
        var animation = new DoubleAnimation(to, TimeSpan.FromSeconds(seconds)) { FillBehavior = FillBehavior.HoldEnd };
        if (completed is not null) animation.Completed += (_, _) => completed();
        _image.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    protected override void OnTick()
    {
        if (_cue.HoldDuration <= 0 || FadingOut || !_clock.IsRunning) return;
        var remaining = _cue.HoldDuration - _clock.Elapsed.TotalSeconds;
        if (remaining > _cue.FadeOut) return;
        FadingOut = true;
        AnimateOpacity(0, Math.Max(0, remaining), Finish);
    }

    protected override void OnFinish()
    {
        _image.BeginAnimation(UIElement.OpacityProperty, null);
        Window.Stage.Children.Remove(_image);
    }
}
