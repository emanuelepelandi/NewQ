using NewQ.Core.Engine;
using NewQ.Core.Model;
using Xunit;

namespace NewQ.Core.Tests;

public class CueEngineTests
{
    private readonly ManualScheduler _scheduler = new();
    private readonly FakePlayer _player = new();

    private CueEngine CreateEngine(params Cue[] cues)
    {
        var workspace = new Workspace();
        workspace.Settings.GoLockoutMs = 0;
        foreach (var cue in cues) workspace.Cues.Add(cue);
        return new CueEngine(_scheduler, new[] { _player }, workspace);
    }

    private static AudioCue Audio(string number, ContinueMode mode = ContinueMode.DoNotContinue, double preWait = 0, double postWait = 0)
        => new() { Number = number, ContinueMode = mode, PreWait = preWait, PostWait = postWait };

    private static T Linked<T>(T cue, LinkAction action, string target) where T : Cue
    {
        cue.LinkAction = action;
        cue.LinkTarget = target;
        return cue;
    }

    [Fact]
    public void Go_fires_playhead_and_advances()
    {
        var engine = CreateEngine(Audio("1"), Audio("2"));

        engine.Go();

        Assert.Equal(new[] { "1" }, _player.StartedNumbers);
        Assert.Equal("2", engine.Playhead?.Number);
        Assert.Equal(CueRunState.Running, engine.Workspace.Cues[0].RunState);
    }

    [Fact]
    public void Go_at_end_of_list_leaves_no_playhead()
    {
        var engine = CreateEngine(Audio("1"));
        engine.Go();
        Assert.Null(engine.Playhead);
        Assert.False(engine.Go());
    }

    [Fact]
    public void Go_lockout_ignores_double_presses()
    {
        var engine = CreateEngine(Audio("1"), Audio("2"));
        engine.Workspace.Settings.GoLockoutMs = 200;

        engine.Go();
        _scheduler.Advance(0.05);
        Assert.False(engine.Go());
        _scheduler.Advance(0.3);
        Assert.True(engine.Go());

        Assert.Equal(new[] { "1", "2" }, _player.StartedNumbers);
    }

    [Fact]
    public void PreWait_delays_start()
    {
        var engine = CreateEngine(Audio("1", preWait: 2));

        engine.Go();
        Assert.Empty(_player.Started);
        Assert.Equal(CueRunState.PreWait, engine.Workspace.Cues[0].RunState);

        _scheduler.Advance(1.9);
        Assert.Empty(_player.Started);

        _scheduler.Advance(0.2);
        Assert.Single(_player.Started);
    }

    [Fact]
    public void WaitFollow_fires_next_post_wait_after_the_end_and_playhead_skips_it()
    {
        var engine = CreateEngine(Audio("1", ContinueMode.WaitFollow, postWait: 1.5), Audio("2"), Audio("3"));

        engine.Go();
        Assert.Equal("3", engine.Playhead?.Number);
        _scheduler.Advance(5);
        Assert.Equal(new[] { "1" }, _player.StartedNumbers); // still playing: nothing yet

        _player.Started[0].Finish();
        _scheduler.Advance(1.0);
        Assert.Equal(new[] { "1" }, _player.StartedNumbers);

        _scheduler.Advance(0.6);
        Assert.Equal(new[] { "1", "2" }, _player.StartedNumbers);
    }

    [Fact]
    public void WaitFollow_does_not_fire_when_cue_is_stopped_manually()
    {
        var engine = CreateEngine(Audio("1", ContinueMode.WaitFollow, postWait: 1), Audio("2"));
        engine.Go();
        engine.Stop(engine.Workspace.Cues[0], TimeSpan.Zero);
        _scheduler.Advance(5);
        Assert.Equal(new[] { "1" }, _player.StartedNumbers);
    }

    [Fact]
    public void Legacy_auto_continue_does_nothing_in_the_engine()
    {
        var engine = CreateEngine(Audio("1", ContinueMode.AutoContinue), Audio("2"));
        engine.Go();
        _scheduler.Advance(5);
        Assert.Equal(new[] { "1" }, _player.StartedNumbers);
        Assert.Equal("2", engine.Playhead?.Number);
    }

    [Fact]
    public void Start_links_fire_their_targets_together_and_playhead_skips_them()
    {
        var engine = CreateEngine(Linked(Audio("1"), LinkAction.Start, "2"), Linked(Audio("2"), LinkAction.Start, "3"), Audio("3"), Audio("4"));

        engine.Go();

        Assert.Equal(new[] { "1", "2", "3" }, _player.StartedNumbers);
        Assert.Equal("4", engine.Playhead?.Number);
        Assert.Single(engine.Running.Select(r => r.Group).Distinct()); // one GO, one group
    }

    [Fact]
    public void Linked_target_waits_its_own_pre_wait()
    {
        var engine = CreateEngine(Linked(Audio("1"), LinkAction.Start, "2"), Audio("2", preWait: 2));
        engine.Go();
        Assert.Equal(new[] { "1" }, _player.StartedNumbers);
        _scheduler.Advance(2.1);
        Assert.Equal(new[] { "1", "2" }, _player.StartedNumbers);
    }

    [Fact]
    public void Link_acts_when_the_cue_starts_after_its_pre_wait()
    {
        var engine = CreateEngine(Linked(Audio("1", preWait: 1), LinkAction.Start, "3"), Audio("2"), Audio("3"));
        engine.Go();
        Assert.Empty(_player.Started);
        Assert.Equal("2", engine.Playhead?.Number); // 3 is linked but not right below 1: its own GO stays
        _scheduler.Advance(1.1);
        Assert.Equal(new[] { "1", "3" }, _player.StartedNumbers);
    }

    [Fact]
    public void Link_stop_and_fade_out_act_on_their_targets()
    {
        var engine = CreateEngine(Audio("1"), Audio("2"), Linked(Audio("3"), LinkAction.Stop, "1"), Linked(Audio("4"), LinkAction.FadeOut, "2"));
        engine.Workspace.Settings.CueFadeSeconds = 2.5;
        engine.Go();
        engine.Go();
        engine.Go(); // 3 stops 1
        Assert.Equal(TimeSpan.Zero, _player.Started[0].StoppedWithFade);
        engine.Go(); // 4 fades 2 out
        Assert.Equal(TimeSpan.FromSeconds(2.5), _player.Started[1].StoppedWithFade);
    }

    [Fact]
    public void Link_loops_start_each_cue_once()
    {
        string? warned = null;
        var engine = CreateEngine(Linked(Audio("1"), LinkAction.Start, "2"), Linked(Audio("2"), LinkAction.Start, "1"));
        engine.Log += (_, e) => warned = e.Message;
        engine.Go();
        Assert.Equal(new[] { "1", "2" }, _player.StartedNumbers);
        Assert.NotNull(warned);
    }

    [Fact]
    public void Link_on_a_disarmed_cue_does_nothing()
    {
        var engine = CreateEngine(Linked(Audio("1"), LinkAction.Start, "3"), Audio("2"), Audio("3"));
        engine.Workspace.Cues[0].Armed = false;
        engine.Go();
        _scheduler.Flush();
        Assert.Empty(_player.Started);
    }

    [Fact]
    public void Each_GO_is_a_group_shared_by_its_follows_and_links()
    {
        var engine = CreateEngine(Audio("1", ContinueMode.AutoFollow), Linked(Audio("2"), LinkAction.Start, "3"), Audio("3"), Audio("4"));
        engine.Go();                 // 1, then 2 (+3) when 1 ends
        _player.Started[0].Finish();
        _scheduler.Flush();
        engine.Go();                 // 4
        var groups = engine.Running.ToDictionary(r => r.Cue.Number, r => r.Group);
        Assert.Equal(groups["2"], groups["3"]);
        Assert.NotEqual(groups["2"], groups["4"]);
        Assert.True(engine.IsGroupActive(groups["2"]));
    }

    [Fact]
    public void Group_stays_active_while_a_wait_follow_is_pending()
    {
        var engine = CreateEngine(Audio("1", ContinueMode.WaitFollow, postWait: 3), Audio("2"));
        engine.Go();
        var group = engine.Running[0].Group;
        _player.Started[0].Finish();
        _scheduler.Advance(1);
        Assert.Empty(engine.Running);
        Assert.True(engine.IsGroupActive(group));
        engine.StopAll(TimeSpan.Zero);
        Assert.False(engine.IsGroupActive(group));
    }

    [Fact]
    public void AutoFollow_fires_next_when_cue_ends()
    {
        var engine = CreateEngine(Audio("1", ContinueMode.AutoFollow), Audio("2"));

        engine.Go();
        _scheduler.Advance(10);
        Assert.Equal(new[] { "1" }, _player.StartedNumbers);

        _player.Started[0].Finish();
        _scheduler.Flush();
        Assert.Equal(new[] { "1", "2" }, _player.StartedNumbers);
    }

    [Fact]
    public void AutoFollow_does_not_fire_when_cue_is_stopped_manually()
    {
        var engine = CreateEngine(Audio("1", ContinueMode.AutoFollow), Audio("2"));

        engine.Go();
        engine.StopAll(TimeSpan.Zero);
        _scheduler.Flush();

        Assert.Equal(new[] { "1" }, _player.StartedNumbers);
        Assert.Empty(engine.Running);
    }

    [Fact]
    public void StopAll_cancels_pending_continues_and_prewaits()
    {
        var engine = CreateEngine(Audio("1", ContinueMode.WaitFollow, postWait: 2), Audio("2"), Audio("3", preWait: 5));

        engine.Go();            // fires 1
        _player.Started[0].Finish();
        _scheduler.Advance(0.5); // 2 is pending
        engine.Fire(engine.Workspace.Cues[2]); // 3 in pre-wait
        engine.StopAll(TimeSpan.Zero);
        _scheduler.Advance(10);

        Assert.Equal(new[] { "1" }, _player.StartedNumbers);
        Assert.Empty(engine.Running);
        Assert.All(engine.Workspace.Cues, c => Assert.Equal(CueRunState.Idle, c.RunState));
    }

    [Fact]
    public void Stop_with_fade_is_forced_complete_by_safety_timer_if_player_never_confirms()
    {
        var workspace = new Workspace();
        workspace.Cues.Add(Audio("1"));
        var engine = new CueEngine(_scheduler, new[] { new NeverEndingPlayer() }, workspace);

        engine.Go();
        engine.StopAll(TimeSpan.FromSeconds(2));
        _scheduler.Advance(2.0);
        Assert.Single(engine.Running); // still fading

        _scheduler.Advance(1.0);
        Assert.Empty(engine.Running);  // forced after fade + margin
    }

    [Fact]
    public void Safety_timer_hard_stops_a_player_that_never_confirms()
    {
        var player = new NeverEndingPlayer();
        var workspace = new Workspace();
        workspace.Cues.Add(Audio("1"));
        var engine = new CueEngine(_scheduler, new[] { player }, workspace);

        engine.Go();
        engine.StopAll(TimeSpan.FromSeconds(1));
        _scheduler.Advance(2);

        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.Zero }, player.Handle!.Stops);
    }

    private sealed class NeverEndingPlayer : ICuePlayer
    {
        public NeverEndingHandle? Handle { get; private set; }
        public bool CanPlay(Cue cue) => true;
        public IActiveCue Start(Cue cue, CueContext context) => Handle = new NeverEndingHandle();
    }

    private sealed class NeverEndingHandle : IActiveCue
    {
        public List<TimeSpan> Stops { get; } = new();
        public TimeSpan Elapsed => TimeSpan.Zero;
        public TimeSpan? Duration => null;
        public void Pause() { }
        public void Resume() { }
        public void Stop(TimeSpan fade) => Stops.Add(fade);
        public void Fade(FadeRequest request) { }
    }

    [Fact]
    public void Panic_fades_then_second_press_stops_immediately()
    {
        var engine = CreateEngine(Audio("1"));
        engine.Workspace.Settings.PanicFadeSeconds = 2;
        engine.Go();

        engine.Panic();
        Assert.Equal(TimeSpan.FromSeconds(2), _player.Started[0].StoppedWithFade);

        engine.Go(); // nothing at playhead
        engine.Fire(engine.Workspace.Cues[0]);
        engine.Panic();
        Assert.Equal(TimeSpan.Zero, _player.Started[1].StoppedWithFade);
    }

    [Fact]
    public void Wait_cue_lasts_its_duration_and_auto_follows()
    {
        var engine = CreateEngine(new WaitCue { Number = "1", WaitDuration = 3, ContinueMode = ContinueMode.AutoFollow }, Audio("2"));

        engine.Go();
        _scheduler.Advance(2.9);
        Assert.Empty(_player.Started);
        _scheduler.Advance(0.2);
        Assert.Equal(new[] { "2" }, _player.StartedNumbers);
    }

    [Fact]
    public void Fade_cue_sends_fade_request_to_running_target()
    {
        var engine = CreateEngine(
            Audio("1"),
            new FadeCue { Number = "2", TargetNumber = "1", FadeDuration = 4, TargetVolumeDb = -20, StopWhenDone = false });

        engine.Go();
        engine.Go();

        var fade = Assert.Single(_player.Started[0].Fades);
        Assert.Equal(TimeSpan.FromSeconds(4), fade.Duration);
        Assert.Equal(-20, fade.VolumeDb);
        Assert.Null(fade.Opacity);
        Assert.False(fade.StopWhenDone);
    }

    [Fact]
    public void Stop_cue_stops_only_its_target()
    {
        var engine = CreateEngine(Audio("1"), Audio("2"), new StopCue { Number = "3", TargetNumber = "1", FadeTime = 0 });

        engine.Go();
        engine.Go();
        engine.Go();
        _scheduler.Flush();

        Assert.Equal(TimeSpan.Zero, _player.Started[0].StoppedWithFade);
        Assert.Null(_player.Started[1].StoppedWithFade);
        Assert.Equal(new[] { "2" }, engine.Running.Select(r => r.Cue.Number));
    }

    [Fact]
    public void Disarmed_cue_does_nothing_but_continues()
    {
        var engine = CreateEngine(Audio("1", ContinueMode.AutoFollow), Audio("2"));
        engine.Workspace.Cues[0].Armed = false;

        engine.Go();
        _scheduler.Flush();

        Assert.Equal(new[] { "2" }, _player.StartedNumbers);
    }

    [Fact]
    public void Failed_start_reports_error_and_marks_cue()
    {
        var engine = CreateEngine(Audio("1"));
        _player.ThrowOnStart = true;
        string? logged = null;
        engine.Log += (_, e) => logged = e.Message;

        engine.Go();

        Assert.Equal(CueRunState.Error, engine.Workspace.Cues[0].RunState);
        Assert.Contains("file mancante", logged);
        Assert.Empty(engine.Running);
    }

    private sealed class PreloadRecorder : IPreloadingCuePlayer
    {
        public List<string> Preloaded { get; } = new();
        public List<string> Kept { get; private set; } = new();
        public bool CanPlay(Cue cue) => cue is VideoCue;
        public IActiveCue Start(Cue cue, CueContext context) { context.Completed(); return InstantActiveCue.Instance; }
        public void Preload(Cue cue, Workspace workspace) => Preloaded.Add(cue.Number);
        public void ReleasePreloads(IReadOnlyCollection<Cue> keep) => Kept = keep.Select(c => c.Number).ToList();
    }

    [Fact]
    public void Preloads_the_cues_fired_by_next_go_after_debounce()
    {
        var recorder = new PreloadRecorder();
        var ws = new Workspace();
        ws.Settings.GoLockoutMs = 0;
        ws.Cues.Add(new VideoCue { Number = "1" });
        ws.Cues.Add(new AudioCue { Number = "2", LinkAction = LinkAction.Start, LinkTarget = "3" });
        ws.Cues.Add(new VideoCue { Number = "3" });
        ws.Cues.Add(new VideoCue { Number = "4" });
        var engine = new CueEngine(_scheduler, new ICuePlayer[] { recorder, _player }, ws);

        _scheduler.Advance(0.1);
        Assert.Empty(recorder.Preloaded); // debounced

        _scheduler.Advance(0.2);
        Assert.Equal(new[] { "1" }, recorder.Preloaded);

        engine.Go(); // playhead → 2, whose chain is 2 → 3
        recorder.Preloaded.Clear();
        _scheduler.Advance(0.3);
        Assert.Equal(new[] { "3" }, recorder.Preloaded);           // only video cues of the chain
        Assert.Equal(new[] { "2", "3" }, recorder.Kept);           // everything else released
        Assert.Equal(new[] { "2", "3" }, engine.CuesFiredByNextGo().Select(c => c.Number));
    }

    [Fact]
    public void Pause_and_resume_propagate_to_handles()
    {
        var engine = CreateEngine(Audio("1"));
        engine.Go();

        engine.PauseAll();
        Assert.True(_player.Started[0].Paused);
        Assert.Equal(CueRunState.Paused, engine.Workspace.Cues[0].RunState);

        engine.ResumeAll();
        Assert.False(_player.Started[0].Paused);
        Assert.Equal(CueRunState.Running, engine.Workspace.Cues[0].RunState);
    }
}

public class PauseAndSeekTests
{
    private readonly ManualScheduler _scheduler = new();
    private readonly FakePlayer _player = new();

    private CueEngine Engine(params Cue[] cues)
    {
        var ws = new Workspace();
        ws.Settings.GoLockoutMs = 0;
        foreach (var c in cues) ws.Cues.Add(c);
        return new CueEngine(_scheduler, new[] { _player }, ws);
    }

    [Fact]
    public void TogglePause_affects_only_that_cue()
    {
        var engine = Engine(new AudioCue { Number = "1" }, new AudioCue { Number = "2" });
        engine.Go();
        engine.Go();

        engine.TogglePause(engine.Workspace.Cues[0]);

        Assert.True(_player.Started[0].Paused);
        Assert.False(_player.Started[1].Paused);
        Assert.Equal(CueRunState.Paused, engine.Workspace.Cues[0].RunState);
        Assert.Equal(CueRunState.Running, engine.Workspace.Cues[1].RunState);
        Assert.True(engine.Workspace.Cues[0].CanSeek);
        Assert.False(engine.Workspace.Cues[1].CanSeek);

        engine.TogglePause(engine.Workspace.Cues[0]);
        Assert.False(_player.Started[0].Paused);
        Assert.False(engine.Workspace.Cues[0].CanSeek);
    }

    [Fact]
    public void Seek_moves_running_instances()
    {
        var engine = Engine(new AudioCue { Number = "1" });
        engine.Go();
        engine.PauseAll();

        engine.Seek(engine.Workspace.Cues[0], TimeSpan.FromSeconds(4));

        Assert.Equal(TimeSpan.FromSeconds(4), _player.Started[0].SeekedTo);
    }

    [Fact]
    public void Indefinite_cues_cannot_be_scrubbed()
    {
        var engine = Engine(new AudioCue { Number = "1", Loop = true });
        engine.Go();
        _player.Started[0].Duration = null; // e.g. a loop
        engine.PauseAll();

        Assert.False(engine.Workspace.Cues[0].CanSeek);
    }

    [Fact]
    public void Wait_cue_can_be_scrubbed_while_paused()
    {
        var engine = Engine(new WaitCue { Number = "1", WaitDuration = 10, ContinueMode = ContinueMode.AutoFollow }, new AudioCue { Number = "2" });
        engine.Go();
        engine.PauseAll();
        engine.Seek(engine.Workspace.Cues[0], TimeSpan.FromSeconds(9));
        engine.ResumeAll();

        _scheduler.Advance(0.9);
        Assert.Empty(_player.Started);
        _scheduler.Advance(0.2);
        Assert.Equal(new[] { "2" }, _player.StartedNumbers);
    }
}
