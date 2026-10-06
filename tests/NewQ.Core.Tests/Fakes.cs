using NewQ.Core.Engine;
using NewQ.Core.Model;

namespace NewQ.Core.Tests;

/// <summary>Deterministic scheduler: time only moves when the test calls <see cref="Advance"/>.</summary>
internal sealed class ManualScheduler : IScheduler
{
    private sealed class Entry : IDisposable
    {
        public required TimeSpan Due { get; init; }
        public required long Sequence { get; init; }
        public required Action Action { get; init; }
        public bool Cancelled { get; private set; }
        public void Dispose() => Cancelled = true;
    }

    private readonly List<Entry> _queue = new();
    private long _sequence;

    public TimeSpan Now { get; private set; }

    public void Post(Action action) => Schedule(TimeSpan.Zero, action);

    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        var entry = new Entry { Due = Now + (delay < TimeSpan.Zero ? TimeSpan.Zero : delay), Sequence = _sequence++, Action = action };
        _queue.Add(entry);
        return entry;
    }

    public void Advance(TimeSpan by)
    {
        var target = Now + by;
        while (true)
        {
            var next = _queue.Where(e => !e.Cancelled && e.Due <= target)
                             .OrderBy(e => e.Due).ThenBy(e => e.Sequence)
                             .FirstOrDefault();
            if (next is null) break;
            _queue.Remove(next);
            if (next.Due > Now) Now = next.Due;
            next.Action();
        }
        Now = target;
        _queue.RemoveAll(e => e.Cancelled);
    }

    public void Advance(double seconds) => Advance(TimeSpan.FromSeconds(seconds));

    /// <summary>Runs everything due now (posted callbacks).</summary>
    public void Flush() => Advance(TimeSpan.Zero);
}

/// <summary>Player that records what happens and lets tests finish cues.</summary>
internal sealed class FakePlayer : ICuePlayer
{
    public sealed class Handle : IActiveCue
    {
        public required Cue Cue { get; init; }
        public required CueContext Context { get; init; }
        public TimeSpan Elapsed => TimeSpan.Zero;
        public TimeSpan? Duration { get; set; } = TimeSpan.FromSeconds(10);
        public bool Paused { get; private set; }
        public bool CanSeek => true;
        public TimeSpan? SeekedTo { get; private set; }
        public void Seek(TimeSpan position) => SeekedTo = position;
        public TimeSpan? StoppedWithFade { get; private set; }
        public List<FadeRequest> Fades { get; } = new();
        public void Pause() => Paused = true;
        public void Resume() => Paused = false;
        public void Stop(TimeSpan fade) { StoppedWithFade = fade; Context.Completed(); }
        public void Fade(FadeRequest request) => Fades.Add(request);
        public void Finish() => Context.Completed();
    }

    public List<Handle> Started { get; } = new();
    public IEnumerable<string> StartedNumbers => Started.Select(h => h.Cue.Number);
    public bool ThrowOnStart { get; set; }

    public bool CanPlay(Cue cue) => cue is AudioCue or VideoCue or ImageCue or MidiCue or NetworkCue;

    public IActiveCue Start(Cue cue, CueContext context)
    {
        if (ThrowOnStart) throw new FileNotFoundException("file mancante");
        var handle = new Handle { Cue = cue, Context = context };
        Started.Add(handle);
        return handle;
    }
}
