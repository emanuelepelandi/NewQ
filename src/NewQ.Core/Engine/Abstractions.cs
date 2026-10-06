using NewQ.Core.Model;

namespace NewQ.Core.Engine;

/// <summary>
/// Single-threaded clock and work queue. The engine is not thread-safe: every engine call and every
/// scheduled callback must run on the scheduler's thread (the UI thread in the app).
/// </summary>
public interface IScheduler
{
    TimeSpan Now { get; }

    /// <summary>Queues an action to run on the scheduler thread (never inline).</summary>
    void Post(Action action);

    /// <summary>Runs an action after a delay on the scheduler thread. Dispose to cancel.</summary>
    IDisposable Schedule(TimeSpan delay, Action action);
}

/// <summary>Request to change the level of a running cue.</summary>
/// <param name="VolumeDb">Target volume, or null to leave the volume unchanged.</param>
/// <param name="Opacity">Target opacity (visual cues), or null to leave it unchanged.</param>
public sealed record FadeRequest(TimeSpan Duration, double? VolumeDb, double? Opacity, bool StopWhenDone);

/// <summary>Handle to one running instance of a cue.</summary>
public interface IActiveCue
{
    TimeSpan Elapsed { get; }

    /// <summary>Total length, or null if indefinite (loops, held images...).</summary>
    TimeSpan? Duration { get; }

    void Pause();
    void Resume();

    /// <summary>Stops the cue (optionally fading out). Must eventually call <see cref="CueContext.Completed"/>.</summary>
    void Stop(TimeSpan fade);

    void Fade(FadeRequest request);

    /// <summary>True if the playback position can be moved (audio, video, wait).</summary>
    bool CanSeek => false;

    /// <summary>Moves the playback position (clamped to the cue's length).</summary>
    void Seek(TimeSpan position) { }
}

/// <summary>Plays one family of cues (audio, video, MIDI...).</summary>
public interface ICuePlayer
{
    bool CanPlay(Cue cue);

    /// <summary>
    /// Starts the cue. Throw to report a failure that prevents starting.
    /// Call <see cref="CueContext.Completed"/> (from any thread) when the cue ends.
    /// </summary>
    IActiveCue Start(Cue cue, CueContext context);
}

/// <summary>
/// A player that can prepare cues before GO (open files, warm up decoders) so they start instantly.
/// The engine asks it to preload the cues that the next GO will fire.
/// </summary>
public interface IPreloadingCuePlayer : ICuePlayer
{
    /// <summary>Prepares the cue. Must be cheap to call again for a cue that is already prepared.</summary>
    void Preload(Cue cue, Func<string, string> resolvePath);

    /// <summary>Releases every prepared cue that is not in <paramref name="keep"/>.</summary>
    void ReleasePreloads(IReadOnlyCollection<Cue> keep);
}

/// <summary>Callbacks given to a player for one cue instance. Thread-safe.</summary>
public sealed class CueContext
{
    private readonly Action _completed;
    private readonly Action<string> _error;
    private readonly Func<string, string> _resolvePath;
    private int _done;

    public CueContext(Action completed, Action<string> error, Func<string, string> resolvePath)
    {
        _completed = completed;
        _error = error;
        _resolvePath = resolvePath;
    }

    /// <summary>Signals the end of the cue. Extra calls are ignored.</summary>
    public void Completed()
    {
        if (Interlocked.Exchange(ref _done, 1) == 0) _completed();
    }

    public void Error(string message) => _error(message);

    public string ResolvePath(string path) => _resolvePath(path);
}

/// <summary>Handle for fire-and-forget cues (MIDI, network, stop...).</summary>
public sealed class InstantActiveCue : IActiveCue
{
    public static readonly InstantActiveCue Instance = new();
    public TimeSpan Elapsed => TimeSpan.Zero;
    public TimeSpan? Duration => TimeSpan.Zero;
    public void Pause() { }
    public void Resume() { }
    public void Stop(TimeSpan fade) { }
    public void Fade(FadeRequest request) { }
}

public enum EngineLogLevel { Info, Warning, Error }

public sealed class EngineLogEventArgs : EventArgs
{
    public EngineLogEventArgs(EngineLogLevel level, string message)
    {
        Level = level;
        Message = message;
        Timestamp = DateTime.Now;
    }

    public EngineLogLevel Level { get; }
    public string Message { get; }
    public DateTime Timestamp { get; }
}
