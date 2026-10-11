using NewQ.Core.Engine;
using NewQ.Core.Model;
using NewQ.Core.Serialization;
using NewQ.Core.Timeline;
using Xunit;

namespace NewQ.Core.Tests;

public class ShowMarkerTests
{
    private readonly ManualScheduler _scheduler = new();
    private readonly FakePlayer _player = new();

    private static AudioCue Audio(string number, ContinueMode mode = ContinueMode.DoNotContinue)
        => new() { Number = number, ContinueMode = mode, MediaDuration = 10 };

    private CueEngine CreateShow(params Cue[] cues)
    {
        var workspace = new Workspace();
        workspace.Settings.GoLockoutMs = 0;
        foreach (var cue in cues) workspace.Cues.Add(cue);
        workspace.EnsureShowMarkers();
        return new CueEngine(_scheduler, new[] { _player }, workspace);
    }

    [Fact]
    public void Ensure_puts_start_first_and_end_last_once()
    {
        var workspace = new Workspace();
        workspace.Cues.Add(Audio("1"));
        workspace.EnsureShowMarkers();
        workspace.EnsureShowMarkers();

        Assert.Equal(3, workspace.Cues.Count);
        Assert.Equal(ShowMarkerCue.StartName, workspace.ShowStart?.Name);
        Assert.Equal(ShowMarkerCue.EndName, workspace.ShowEnd?.Name);
        Assert.Same(workspace.Cues[0], workspace.ShowStart);
        Assert.Same(workspace.Cues[^1], workspace.ShowEnd);
    }

    [Fact]
    public void Ensure_repairs_misplaced_duplicated_and_renamed_markers()
    {
        var workspace = new Workspace();
        var end = ShowMarkerCue.Create(ShowMarkerKind.End);
        var start = ShowMarkerCue.Create(ShowMarkerKind.Start);
        end.Name = "rinominata";
        start.Number = "7";
        workspace.Cues.Add(end);
        workspace.Cues.Add(Audio("1"));
        workspace.Cues.Add(start);
        workspace.Cues.Add(ShowMarkerCue.Create(ShowMarkerKind.Start));
        workspace.Cues.Add(Audio("2"));

        workspace.EnsureShowMarkers();

        Assert.Equal(new[] { "", "1", "2", "" }, workspace.Cues.Select(c => c.Number));
        Assert.Same(start, workspace.ShowStart);
        Assert.Same(end, workspace.ShowEnd);
        Assert.Equal(ShowMarkerCue.EndName, end.Name);
    }

    [Fact]
    public void New_cues_go_between_the_markers()
    {
        var workspace = new Workspace();
        workspace.EnsureShowMarkers();
        Assert.Equal(1, workspace.ClampToProgram(0));
        Assert.Equal(1, workspace.ClampToProgram(2)); // after END → before it
        workspace.Cues.Insert(1, Audio("1"));
        Assert.Equal(2, workspace.ClampToProgram(3));
    }

    [Fact]
    public void Show_opens_on_start_and_GO_on_start_only_moves_on()
    {
        var engine = CreateShow(Audio("1"), Audio("2"));
        engine.Workspace.ShowStart!.HasOwnGo = true;
        Assert.Same(engine.Workspace.ShowStart, engine.Playhead);

        Assert.True(engine.Go());
        _scheduler.Advance(0.01);

        Assert.Empty(_player.Started);
        Assert.Equal("1", engine.Playhead?.Number);
        Assert.Empty(engine.Running);
    }

    [Fact]
    public void Start_with_auto_follow_fires_the_first_cue_with_the_same_GO()
    {
        var engine = CreateShow(Audio("1"), Audio("2"));
        engine.Workspace.ShowStart!.ContinueMode = ContinueMode.AutoFollow;

        engine.Go();
        _scheduler.Advance(0.01);

        Assert.Equal(new[] { "1" }, _player.StartedNumbers);
        Assert.Equal("2", engine.Playhead?.Number);
    }

    [Fact]
    public void After_the_last_cue_the_playhead_rests_on_end_and_stays_there()
    {
        var engine = CreateShow(Audio("1"));
        engine.Workspace.ShowStart!.HasOwnGo = true;
        engine.Go(); // START
        engine.Go(); // 1
        Assert.Same(engine.Workspace.ShowEnd, engine.Playhead);

        _player.Started[0].Finish();
        _scheduler.Advance(0.01);
        Assert.Same(engine.Workspace.ShowEnd, engine.Playhead);

        Assert.True(engine.Go()); // GO on END: nothing happens, still on END
        _scheduler.Advance(0.01);
        Assert.Single(_player.Started);
        Assert.Same(engine.Workspace.ShowEnd, engine.Playhead);

        engine.MovePlayhead(-1); // the playhead can still be moved back
        Assert.Equal("1", engine.Playhead?.Number);
    }

    [Fact]
    public void Last_cue_chained_into_end_still_leaves_the_playhead_on_end()
    {
        var engine = CreateShow(Audio("1"), Audio("2", ContinueMode.AutoFollow));
        engine.SetPlayhead(engine.Workspace.Cues[2]);
        engine.Go();
        Assert.Same(engine.Workspace.ShowEnd, engine.Playhead);

        _player.Started[0].Finish(); // auto-follow fires END: nothing to play
        _scheduler.Advance(0.01);
        Assert.Single(_player.Started);
        Assert.Empty(engine.Running);
        Assert.Same(engine.Workspace.ShowEnd, engine.Playhead);
    }

    [Fact]
    public void Playhead_lost_while_cues_run_returns_to_end_when_everything_is_over()
    {
        var engine = CreateShow(Audio("1"));
        engine.Fire(engine.Workspace.Cues[1]);
        engine.SetPlayhead(null);

        _player.Started[0].Finish();
        _scheduler.Advance(0.01);

        Assert.Same(engine.Workspace.ShowEnd, engine.Playhead);
    }

    [Fact]
    public void Markers_survive_save_and_load()
    {
        var engine = CreateShow(Audio("1"));
        engine.Workspace.ShowStart!.Notes = "chiamata attori";

        var loaded = WorkspaceSerializer.Deserialize(WorkspaceSerializer.Serialize(engine.Workspace));
        loaded.EnsureShowMarkers();

        Assert.Equal(3, loaded.Cues.Count);
        Assert.Equal("chiamata attori", loaded.ShowStart?.Notes);
        Assert.NotNull(loaded.ShowEnd);
    }

    [Fact]
    public void Timeline_ignores_markers_and_start_points_at_the_first_GO()
    {
        var engine = CreateShow(Audio("1"), Audio("2"));
        var timeline = TimelineBuilder.Build(engine.Workspace.Cues);

        Assert.Equal(2, timeline.Blocks.Count);
        Assert.DoesNotContain(timeline.Items, i => i.Cue.IsShowMarker);
        Assert.Equal(0, timeline.BlockOf(engine.Workspace.ShowStart!));
        Assert.Equal(-1, timeline.BlockOf(engine.Workspace.ShowEnd!));
    }

    [Fact]
    public void New_start_has_no_GO_of_its_own_so_the_first_GO_starts_the_show()
    {
        var engine = CreateShow(Audio("1"), Audio("2"));
        Assert.False(engine.Workspace.ShowStart!.HasOwnGo);

        engine.Go();
        _scheduler.Advance(0.01);

        Assert.Equal(new[] { "1" }, _player.StartedNumbers);
        Assert.Equal("2", engine.Playhead?.Number);
        Assert.False(TimelineBuilder.Build(engine.Workspace.Cues).StartHasGo);
    }

    [Fact]
    public void A_dedicated_start_GO_shows_in_the_timeline_but_is_not_counted()
    {
        var engine = CreateShow(Audio("1"), Audio("2"));
        engine.Workspace.ShowStart!.HasOwnGo = true;
        Assert.Equal(ContinueMode.DoNotContinue, engine.Workspace.ShowStart.ContinueMode);

        var t = TimelineBuilder.Build(engine.Workspace.Cues);
        Assert.True(t.StartHasGo);
        Assert.Equal(2, t.Blocks.Count);                 // GO 1 and GO 2 are still cue 1 and cue 2
        Assert.Equal("1", t.Blocks[0].FirstCue.Number);
    }
}
