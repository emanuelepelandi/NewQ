using NewQ.Core.Model;

namespace NewQ.Core.Engine;

/// <summary>One firing of a cue, from GO (including pre-wait) until it ends.</summary>
public sealed class RunningCue
{
    internal RunningCue(Cue cue, TimeSpan firedAt, int group, IReadOnlySet<Cue> linkPath)
    {
        Cue = cue;
        FiredAt = firedAt;
        Group = group;
        LinkPath = linkPath;
    }

    public Cue Cue { get; }
    public TimeSpan FiredAt { get; }

    /// <summary>
    /// The trigger this firing belongs to: every GO (or direct start) opens a group, and the cues it fires through
    /// follows and links share it. The timeline draws one cursor per group.
    /// </summary>
    public int Group { get; }

    /// <summary>Cues started by links on the way to this one: a link never starts one of them again (no loops).</summary>
    internal IReadOnlySet<Cue> LinkPath { get; }
    public IActiveCue? Handle { get; internal set; }
    public bool IsInPreWait => PreWaitTimer is not null;
    public bool IsFinished { get; internal set; }

    internal IDisposable? PreWaitTimer { get; set; }
    internal IDisposable? SafetyTimer { get; set; }
    internal bool StoppedManually { get; set; }

    /// <summary>Its follow has already fired (at the end of a video that holds its last frame).</summary>
    internal bool FollowFired { get; set; }

    /// <summary>Fading out to a stop (Stop/Panic with fade, Fade cue that stops, Fade out link).</summary>
    public bool IsFadingOut { get; internal set; }
    public bool IsPaused { get; internal set; }
}

/// <summary>
/// Sequencing core: playhead, GO, pre/post-wait, follows, links, fades, stops, panic.
/// Not thread-safe: call it only from the scheduler thread.
/// </summary>
public sealed class CueEngine
{
    private static readonly TimeSpan PanicDoublePressWindow = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan StopSafetyMargin = TimeSpan.FromSeconds(0.5);

    private readonly IScheduler _scheduler;
    private readonly List<ICuePlayer> _players;
    private readonly List<RunningCue> _running = new();
    private readonly List<(IDisposable Timer, int Group)> _pendingContinues = new();
    private static readonly TimeSpan PreloadDebounce = TimeSpan.FromMilliseconds(250);

    private TimeSpan? _lastGo;
    private TimeSpan? _lastPanic;
    private IDisposable? _preloadTimer;
    private int _lastGroup;

    // Safety net against loops that fire without ever waiting (e.g. follows and links between instant cues):
    // past this many firings of one GO within a second, the GO is stopped instead of freezing the app.
    private const int MaxFiresPerGroupPerSecond = 300;
    private readonly Dictionary<int, (TimeSpan Since, int Count)> _groupBurst = new();
    private static readonly IReadOnlySet<Cue> NoLinks = new HashSet<Cue>();

    public CueEngine(IScheduler scheduler, IEnumerable<ICuePlayer> players, Workspace? workspace = null)
    {
        _scheduler = scheduler;
        _players = players.ToList();
        Workspace = workspace ?? new Workspace();
        SetPlayhead(Workspace.Cues.FirstOrDefault());
    }

    public Workspace Workspace { get; private set; }

    /// <summary>The engine's clock (the time base of <see cref="RunningCue.FiredAt"/>).</summary>
    public TimeSpan Now => _scheduler.Now;
    public Cue? Playhead { get; private set; }
    public IReadOnlyList<RunningCue> Running => _running;
    public bool IsPaused { get; private set; }

    /// <summary>Something of this GO is still going on: a cue running (or in pre-wait), or a wait follow pending.</summary>
    public bool IsGroupActive(int group)
        => _running.Any(r => r.Group == group && !r.IsFinished) || _pendingContinues.Any(p => p.Group == group);

    public event EventHandler? PlayheadChanged;
    public event EventHandler? RunningChanged;
    public event EventHandler<EngineLogEventArgs>? Log;

    // ------------------------------------------------------------------ workspace / playhead

    public void LoadWorkspace(Workspace workspace)
    {
        HardStopAll();
        foreach (var cue in Workspace.Cues) cue.IsPlayhead = false;
        Workspace = workspace;
        Playhead = null;
        SetPlayhead(workspace.Cues.FirstOrDefault());
        SchedulePreload(); // also releases preloads of the old workspace when the new one is empty
    }

    public void SetPlayhead(Cue? cue)
    {
        if (cue is not null && !Workspace.Cues.Contains(cue)) cue = null;
        if (ReferenceEquals(cue, Playhead)) return;
        if (Playhead is not null) Playhead.IsPlayhead = false;
        Playhead = cue;
        if (cue is not null) cue.IsPlayhead = true;
        PlayheadChanged?.Invoke(this, EventArgs.Empty);
        SchedulePreload();
    }

    /// <summary>
    /// Re-evaluates which cues to preload (the playhead and its continue chain). Debounced, so scrolling
    /// through the list or firing a cue doesn't open and close files on every step.
    /// Call it also after editing a cue (file, output...).
    /// </summary>
    public void SchedulePreload()
    {
        _preloadTimer?.Dispose();
        _preloadTimer = _scheduler.Schedule(PreloadDebounce, () =>
        {
            _preloadTimer = null;
            RefreshPreloads();
        });
    }

    /// <summary>The cues the next GO will fire: the playhead, its follows and its Start links.</summary>
    public IReadOnlyList<Cue> CuesFiredByNextGo()
        => Playhead is null ? Array.Empty<Cue>() : ShowSequence.FiredBy(Workspace.Cues, Playhead);

    private void RefreshPreloads()
    {
        var keep = CuesFiredByNextGo().Where(c => c.Armed).ToList();
        foreach (var player in _players.OfType<IPreloadingCuePlayer>())
        {
            player.ReleasePreloads(keep);
            foreach (var cue in keep.Where(player.CanPlay))
            {
                try { player.Preload(cue, Workspace); }
                catch (Exception ex) { Warn($"Preload cue {cue.Number}: {ex.Message}"); }
            }
        }
    }

    public void MovePlayhead(int delta)
    {
        var cues = Workspace.Cues;
        if (cues.Count == 0) return;
        var index = Playhead is null ? (delta > 0 ? -1 : cues.Count) : cues.IndexOf(Playhead);
        SetPlayhead(cues[Math.Clamp(index + delta, 0, cues.Count - 1)]);
    }

    // ------------------------------------------------------------------ transport

    /// <summary>Fires the cue at the playhead and moves the playhead past the cues this GO covers.</summary>
    public bool Go()
    {
        var now = _scheduler.Now;
        if (_lastGo is TimeSpan last && (now - last).TotalMilliseconds < Workspace.Settings.GoLockoutMs)
            return false;
        _lastGo = now;

        var cue = Playhead;
        if (cue is null) return false;
        var group = ++_lastGroup;

        // Past the last cue the playhead rests on --SHOW END-- (when there is one), never on nothing.
        var next = ShowSequence.NextAfter(Workspace.Cues, cue) ?? Workspace.ShowEnd;
        Fire(cue, group, NoLinks);
        SetPlayhead(next);
        return true;
    }

    /// <summary>Fires a specific cue without moving the playhead (a new group, like a GO).</summary>
    public void Fire(Cue cue) => Fire(cue, ++_lastGroup, NoLinks);

    private void Fire(Cue cue, int group, IReadOnlySet<Cue> linkPath)
    {
        if (IsRunaway(group))
        {
            ReportError(cue, "loop di follow/link infinito: GO fermato");
            CancelPendingContinues(group);
            return;
        }
        if (IsPaused) ResumeAll();

        var rc = new RunningCue(cue, _scheduler.Now, group, linkPath);
        _running.Add(rc);
        cue.ErrorMessage = null;
        cue.Clipped = false;

        if (cue.PreWait > 0)
        {
            rc.PreWaitTimer = _scheduler.Schedule(TimeSpan.FromSeconds(cue.PreWait), () =>
            {
                rc.PreWaitTimer = null;
                Begin(rc);
            });
            RefreshState(cue);
            RunningChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            RunningChanged?.Invoke(this, EventArgs.Empty);
            Begin(rc);
        }

        // The fired cue may have consumed its preload (e.g. Fire without moving the playhead).
        SchedulePreload();
    }

    public void Stop(Cue cue, TimeSpan fade)
    {
        foreach (var rc in _running.Where(r => ReferenceEquals(r.Cue, cue)).ToList())
            StopInstance(rc, fade);
    }

    public void StopAll(TimeSpan fade)
    {
        CancelPendingContinues();
        foreach (var rc in _running.ToList())
            StopInstance(rc, fade);
        IsPaused = false;
    }

    /// <summary>Fades everything out; a second press within 1.5 s stops immediately.</summary>
    public void Panic()
    {
        var now = _scheduler.Now;
        var fade = _lastPanic is TimeSpan last && now - last < PanicDoublePressWindow
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(Workspace.Settings.PanicFadeSeconds);
        _lastPanic = now;
        Info(fade == TimeSpan.Zero ? "Stop: immediato" : $"Stop: fade out {fade.TotalSeconds:0.##} s");
        StopAll(fade);
    }

    public void PauseAll()
    {
        if (IsPaused) return;
        IsPaused = true;
        foreach (var rc in _running) PauseInstance(rc);
        RunningChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResumeAll()
    {
        if (!IsPaused && !_running.Any(r => r.IsPaused)) return;
        IsPaused = false;
        foreach (var rc in _running) ResumeInstance(rc);
        RunningChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Pauses or resumes every running instance of one cue, leaving the others alone.</summary>
    public void TogglePause(Cue cue)
    {
        var instances = _running.Where(r => ReferenceEquals(r.Cue, cue) && r.Handle is not null).ToList();
        if (instances.Count == 0) return;
        var pause = instances.Any(r => !r.IsPaused);
        foreach (var rc in instances)
        {
            if (pause) PauseInstance(rc);
            else ResumeInstance(rc);
        }
        if (!pause && !_running.Any(r => r.IsPaused)) IsPaused = false;
        RunningChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves the playback position of a cue's running instances (used to scrub paused cues).</summary>
    public void Seek(Cue cue, TimeSpan position)
    {
        foreach (var rc in _running.Where(r => ReferenceEquals(r.Cue, cue) && r.Handle is { CanSeek: true }))
            rc.Handle!.Seek(position);
        UpdateProgress();
    }

    private void PauseInstance(RunningCue rc)
    {
        if (rc.Handle is null || rc.IsPaused) return;
        rc.Handle.Pause();
        rc.IsPaused = true;
        RefreshState(rc.Cue);
    }

    private void ResumeInstance(RunningCue rc)
    {
        if (rc.Handle is null || !rc.IsPaused) return;
        rc.Handle.Resume();
        rc.IsPaused = false;
        RefreshState(rc.Cue);
    }

    /// <summary>Updates the progress fields of running cues. Call periodically from the UI.</summary>
    public void UpdateProgress()
    {
        var now = _scheduler.Now;
        foreach (var rc in _running)
        {
            if (rc.IsInPreWait)
            {
                var pre = rc.Cue.PreWait;
                rc.Cue.ElapsedSeconds = (now - rc.FiredAt).TotalSeconds;
                rc.Cue.RuntimeDuration = pre;
                rc.Cue.Progress = pre > 0 ? Math.Clamp(rc.Cue.ElapsedSeconds / pre, 0, 1) : 0;
            }
            else if (rc.Handle is not null)
            {
                var elapsed = rc.Handle.Elapsed.TotalSeconds;
                var duration = rc.Handle.Duration?.TotalSeconds;
                rc.Cue.ElapsedSeconds = elapsed;
                rc.Cue.RuntimeDuration = duration;
                rc.Cue.Progress = duration is > 0 ? Math.Clamp(elapsed / duration.Value, 0, 1) : 0;
            }
        }
    }

    // ------------------------------------------------------------------ internals

    private void Begin(RunningCue rc)
    {
        if (rc.IsFinished) return;
        var cue = rc.Cue;

        try
        {
            rc.Handle = StartHandle(rc);
            if (!rc.IsFinished)
            {
                if (IsPaused) PauseInstance(rc);
                RefreshState(cue);
            }
        }
        catch (Exception ex)
        {
            ReportError(cue, ex.Message);
            Complete(rc);
        }

        // Right after the start, in the same moment, even if the cue failed to start: the sequence goes on.
        if (cue.Armed && cue.HasLink) ExecuteLink(rc);
    }

    private IActiveCue StartHandle(RunningCue rc)
    {
        var cue = rc.Cue;
        var context = new CueContext(
            completed: () => _scheduler.Post(() => Complete(rc)),
            error: message => _scheduler.Post(() => ReportError(cue, message)),
            workspace: Workspace,
            actionDone: () => _scheduler.Post(() => ActionDone(rc)));

        if (!cue.Armed || cue.IsShowMarker) // --SHOW START-- / --SHOW END-- do nothing
        {
            context.Completed();
            return InstantActiveCue.Instance;
        }

        switch (cue)
        {
            case WaitCue wait:
                return new TimedActiveCue(_scheduler, TimeSpan.FromSeconds(wait.WaitDuration), context);

            case FadeCue fade:
                return StartFade(fade, context);

            case StopCue stop:
                ExecuteStop(stop, rc);
                context.Completed();
                return InstantActiveCue.Instance;

            default:
                var player = _players.FirstOrDefault(p => p.CanPlay(cue))
                             ?? throw new InvalidOperationException($"Nessun player disponibile per le cue {cue.TypeName}.");
                return player.Start(cue, context);
        }
    }

    private IActiveCue StartFade(FadeCue fade, CueContext context)
    {
        var duration = TimeSpan.FromSeconds(fade.FadeDuration);
        var targets = _running
            .Where(r => !r.IsFinished && r.Handle is not null
                        && string.Equals(r.Cue.Number, fade.TargetNumber, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (targets.Count == 0)
            Warn($"Fade {fade.Number}: cue {fade.TargetNumber} non in esecuzione.");

        var request = new FadeRequest(
            duration,
            fade.FadeVolume ? fade.TargetVolumeDb : null,
            fade.FadeOpacity ? fade.TargetOpacity : null,
            fade.StopWhenDone);

        foreach (var target in targets)
        {
            target.Handle!.Fade(request);
            if (fade.StopWhenDone)
            {
                target.StoppedManually = true;
                target.IsFadingOut = true;
                RefreshState(target.Cue);
                ArmSafetyTimer(target, duration);
            }
        }

        return new TimedActiveCue(_scheduler, duration, context);
    }

    private void ExecuteStop(StopCue stop, RunningCue self)
    {
        var fade = TimeSpan.FromSeconds(stop.FadeTime);
        IEnumerable<RunningCue> targets = string.IsNullOrEmpty(stop.TargetNumber)
            ? _running
            : _running.Where(r => string.Equals(r.Cue.Number, stop.TargetNumber, StringComparison.OrdinalIgnoreCase));

        foreach (var rc in targets.Where(r => !ReferenceEquals(r, self)).ToList())
            StopInstance(rc, fade);
    }

    private void StopInstance(RunningCue rc, TimeSpan fade)
    {
        if (rc.IsFinished) return;
        rc.StoppedManually = true;

        if (rc.PreWaitTimer is not null || rc.Handle is null)
        {
            Complete(rc);
            return;
        }

        rc.Handle.Stop(fade);
        if (fade <= TimeSpan.Zero)
            Complete(rc);                // don't wait for the player to confirm a hard stop
        else
        {
            rc.IsFadingOut = true;
            RefreshState(rc.Cue);
            ArmSafetyTimer(rc, fade);    // ...and never wait forever for a fade either
        }
    }

    private void ArmSafetyTimer(RunningCue rc, TimeSpan fade)
    {
        rc.SafetyTimer?.Dispose();
        rc.SafetyTimer = _scheduler.Schedule(fade + StopSafetyMargin, () =>
        {
            // The player didn't confirm the end of its fade: force it, so nothing is left playing or open.
            if (!rc.IsFinished) rc.Handle?.Stop(TimeSpan.Zero);
            Complete(rc);
        });
    }

    private void Complete(RunningCue rc)
    {
        if (rc.IsFinished) return;
        rc.IsFinished = true;
        rc.PreWaitTimer?.Dispose();
        rc.PreWaitTimer = null;
        rc.SafetyTimer?.Dispose();
        rc.SafetyTimer = null;
        _running.Remove(rc);
        RefreshState(rc.Cue);
        RunningChanged?.Invoke(this, EventArgs.Empty);

        if (!rc.FollowFired && ShowSequence.Follows(rc.Cue) && !rc.StoppedManually && NextCue(rc.Cue) is Cue next)
            ScheduleFollow(next, TimeSpan.FromSeconds(ShowSequence.FollowDelay(rc.Cue)), rc);
        else if (_running.Count == 0 && Playhead is null && Workspace.ShowEnd is ShowMarkerCue end)
            SetPlayhead(end); // everything is over: the playhead waits on --SHOW END--
    }

    /// <summary>
    /// A cue that stays running after its action (a video holding its last frame, as QLab's Hold at end): the follow
    /// fires now, as if it had ended; stopping it later fires nothing more.
    /// </summary>
    private void ActionDone(RunningCue rc)
    {
        if (rc.IsFinished || rc.FollowFired || rc.StoppedManually) return;
        rc.FollowFired = true;
        if (ShowSequence.Follows(rc.Cue) && NextCue(rc.Cue) is Cue next)
            ScheduleFollow(next, TimeSpan.FromSeconds(ShowSequence.FollowDelay(rc.Cue)), rc);
    }

    /// <summary>Follow: the next cue, in the same group, now (auto) or after the post-wait (wait follow).</summary>
    private void ScheduleFollow(Cue next, TimeSpan delay, RunningCue from)
    {
        if (delay <= TimeSpan.Zero)
        {
            Fire(next, from.Group, NoLinks);
            return;
        }
        IDisposable? timer = null;
        timer = _scheduler.Schedule(delay, () =>
        {
            _pendingContinues.RemoveAll(p => ReferenceEquals(p.Timer, timer));
            Fire(next, from.Group, NoLinks);
        });
        _pendingContinues.Add((timer, from.Group));
    }

    /// <summary>The link of a cue that starts: start, stop or fade out its target.</summary>
    private void ExecuteLink(RunningCue rc)
    {
        var cue = rc.Cue;
        if (ShowSequence.LinkTargetOf(Workspace.Cues, cue) is not Cue target)
        {
            Warn($"Link {cue.Number}: cue {cue.LinkTarget} inesistente.");
            return;
        }
        switch (cue.LinkAction)
        {
            case LinkAction.Start:
                if (rc.LinkPath.Contains(target))
                {
                    Warn($"Link {cue.Number}: cue {target.Number} già avviata.");
                    return;
                }
                var path = new HashSet<Cue>(rc.LinkPath) { cue };
                Fire(target, rc.Group, path);
                break;
            case LinkAction.Stop:
                Stop(target, TimeSpan.Zero);
                break;
            case LinkAction.FadeOut:
                Stop(target, TimeSpan.FromSeconds(Workspace.Settings.CueFadeSeconds));
                break;
        }
    }

    /// <summary>Counts the firings of a GO; true once it fires too many in one second (a loop with no wait).</summary>
    private bool IsRunaway(int group)
    {
        var now = _scheduler.Now;
        if (_groupBurst.Count > 64)
            foreach (var old in _groupBurst.Where(g => now - g.Value.Since > TimeSpan.FromSeconds(2)).Select(g => g.Key).ToList())
                _groupBurst.Remove(old);

        var (since, count) = _groupBurst.TryGetValue(group, out var burst) && now - burst.Since < TimeSpan.FromSeconds(1)
            ? burst : (now, 0);
        _groupBurst[group] = (since, count + 1);
        return count + 1 > MaxFiresPerGroupPerSecond;
    }

    private void CancelPendingContinues(int group)
    {
        foreach (var (timer, _) in _pendingContinues.Where(p => p.Group == group).ToList())
        {
            timer.Dispose();
            _pendingContinues.RemoveAll(p => ReferenceEquals(p.Timer, timer));
        }
    }

    private void CancelPendingContinues()
    {
        foreach (var (timer, _) in _pendingContinues) timer.Dispose();
        _pendingContinues.Clear();
    }

    private void HardStopAll()
    {
        CancelPendingContinues();
        foreach (var rc in _running.ToList())
        {
            rc.StoppedManually = true;
            rc.Handle?.Stop(TimeSpan.Zero);
            Complete(rc);
        }
        IsPaused = false;
    }

    private void RefreshState(Cue cue)
    {
        var instances = _running.Where(r => ReferenceEquals(r.Cue, cue)).ToList();
        if (instances.Count == 0)
        {
            cue.RunState = cue.ErrorMessage is null ? CueRunState.Idle : CueRunState.Error;
            cue.Progress = 0;
            cue.ElapsedSeconds = 0;
            cue.RuntimeDuration = null;
        }
        else if (instances.Any(r => !r.IsInPreWait && !r.IsPaused))
            cue.RunState = CueRunState.Running;
        else if (instances.Any(r => r.IsPaused))
            cue.RunState = CueRunState.Paused;
        else
            cue.RunState = CueRunState.PreWait;

        cue.CanSeek = instances.Any(r => r.IsPaused && r.Handle is { CanSeek: true } && r.Handle.Duration is not null);
        cue.IsFadingOut = instances.Any(r => r.IsFadingOut);
    }

    private Cue? NextCue(Cue cue)
    {
        var index = Workspace.Cues.IndexOf(cue);
        return index >= 0 && index + 1 < Workspace.Cues.Count ? Workspace.Cues[index + 1] : null;
    }

    private void ReportError(Cue cue, string message)
    {
        cue.ErrorMessage = message;
        if (!_running.Any(r => ReferenceEquals(r.Cue, cue))) cue.RunState = CueRunState.Error;
        Log?.Invoke(this, new EngineLogEventArgs(EngineLogLevel.Error, $"Cue {cue.Number} ({cue.TypeName}): {message}"));
    }

    private void Info(string message) => Log?.Invoke(this, new EngineLogEventArgs(EngineLogLevel.Info, message));
    private void Warn(string message) => Log?.Invoke(this, new EngineLogEventArgs(EngineLogLevel.Warning, message));
}
