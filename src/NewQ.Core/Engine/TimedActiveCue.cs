namespace NewQ.Core.Engine;

/// <summary>A cue that just lasts for a given time (Wait, Fade). Pausable.</summary>
internal sealed class TimedActiveCue : IActiveCue
{
    private readonly IScheduler _scheduler;
    private readonly CueContext _context;
    private readonly TimeSpan _duration;
    private TimeSpan _startedAt;
    private TimeSpan _accumulated;
    private IDisposable? _timer;
    private bool _paused;
    private bool _done;

    public TimedActiveCue(IScheduler scheduler, TimeSpan duration, CueContext context)
    {
        _scheduler = scheduler;
        _context = context;
        _duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        _startedAt = scheduler.Now;
        _timer = scheduler.Schedule(_duration, Finish);
    }

    public TimeSpan Elapsed
    {
        get
        {
            var elapsed = _paused || _done ? _accumulated : _accumulated + (_scheduler.Now - _startedAt);
            return elapsed > _duration ? _duration : elapsed;
        }
    }

    public TimeSpan? Duration => _duration;

    public void Pause()
    {
        if (_paused || _done) return;
        _accumulated += _scheduler.Now - _startedAt;
        _timer?.Dispose();
        _timer = null;
        _paused = true;
    }

    public void Resume()
    {
        if (!_paused || _done) return;
        _paused = false;
        _startedAt = _scheduler.Now;
        var remaining = _duration - _accumulated;
        _timer = _scheduler.Schedule(remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining, Finish);
    }

    public void Stop(TimeSpan fade) => Finish();

    public void Fade(FadeRequest request) { }

    public bool CanSeek => !_done;

    public void Seek(TimeSpan position)
    {
        if (_done) return;
        position = position < TimeSpan.Zero ? TimeSpan.Zero : position > _duration ? _duration : position;
        _accumulated = position;
        if (_paused) return;
        _startedAt = _scheduler.Now;
        _timer?.Dispose();
        _timer = _scheduler.Schedule(_duration - position, Finish);
    }

    private void Finish()
    {
        if (_done) return;
        if (!_paused) _accumulated += _scheduler.Now - _startedAt;
        _done = true;
        _timer?.Dispose();
        _timer = null;
        _context.Completed();
    }
}
