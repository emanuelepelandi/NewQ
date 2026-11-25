using System.Collections.ObjectModel;
using NewQ.Core.Engine;
using NewQ.Core.Model;
using NewQ.Core.Timeline;
using Xunit;

namespace NewQ.Core.Tests;

public class CueListTests
{
    private static AudioCue A(string n) => new() { Number = n };

    /// <summary>START, the given cues, END; a group's cues follow it ("G" then children named "G.x").</summary>
    private static ObservableCollection<Cue> List(params Cue[] cues)
    {
        var list = new ObservableCollection<Cue> { ShowMarkerCue.Create(ShowMarkerKind.Start) };
        foreach (var c in cues) list.Add(c);
        list.Add(ShowMarkerCue.Create(ShowMarkerKind.End));
        return list;
    }

    private static string Layout(IEnumerable<Cue> cues)
        => string.Join(" ", cues.Where(c => !c.IsShowMarker).Select(c => (c.IsInGroup ? ">" : "") + c.Number));

    private static (GroupCue Group, Cue[] Children) Group(string n, params string[] children)
    {
        var g = new GroupCue { Number = n };
        return (g, children.Select(c => (Cue)new AudioCue { Number = c, GroupId = g.Id }).ToArray());
    }

    [Fact]
    public void Arrows_move_a_cue_into_and_out_of_a_group()
    {
        var (g, kids) = Group("G", "1", "2");
        var x = A("X");
        var list = List(x, g, kids[0], kids[1], A("Y"));

        CueList.MoveStep(list, x, +1);                 // onto the group: its first cue
        Assert.Equal("G >X >1 >2 Y", Layout(list));
        CueList.MoveStep(list, x, +1);
        CueList.MoveStep(list, x, +1);
        Assert.Equal("G >1 >2 >X Y", Layout(list));
        CueList.MoveStep(list, x, +1);                 // past the last cue: out, in the same place
        Assert.Equal("G >1 >2 X Y", Layout(list));
        CueList.MoveStep(list, x, -1);                 // back up: in again, as the last cue
        Assert.Equal("G >1 >2 >X Y", Layout(list));
        for (var i = 0; i < 2; i++) CueList.MoveStep(list, x, -1);
        Assert.Equal("G >X >1 >2 Y", Layout(list));
        CueList.MoveStep(list, x, -1);                 // first cue: out, above the group
        Assert.Equal("X G >1 >2 Y", Layout(list));
        Assert.False(CueList.MoveStep(list, x, -1));  // START stays first
    }

    [Fact]
    public void A_group_moves_with_its_cues_and_steps_over_whole_groups()
    {
        var (g, kids) = Group("G", "1", "2");
        var (h, hk) = Group("H", "3");
        var list = List(A("X"), g, kids[0], kids[1], h, hk[0]);

        CueList.MoveStep(list, g, +1);
        Assert.Equal("X H >3 G >1 >2", Layout(list));
        CueList.MoveStep(list, g, -1);
        CueList.MoveStep(list, g, -1);
        Assert.Equal("G >1 >2 X H >3", Layout(list));
        Assert.False(CueList.MoveStep(list, g, -1));
    }

    [Fact]
    public void An_empty_group_is_entered_from_below_and_from_above()
    {
        var g = new GroupCue { Number = "G" };
        var x = A("X");
        var list = List(g, x);
        CueList.MoveStep(list, x, -1);
        Assert.Equal("G >X", Layout(list));
        Assert.Equal(g.Id, x.GroupId);
    }

    [Fact]
    public void Drop_targets_follow_the_half_of_the_row()
    {
        var (g, kids) = Group("G", "1", "2");
        var x = A("X");
        var list = List(x, g, kids[0], kids[1], A("Y"));   // rows: 0 START, 1 X, 2 G, 3 1, 4 2, 5 Y, 6 END

        Assert.Equal(new DropTarget(2, null), CueList.TargetAt(list, kids[1], 2, upperHalf: true));   // above the group
        Assert.Equal(new DropTarget(3, g), CueList.TargetAt(list, x, 2, upperHalf: false));          // below its row: first cue
        Assert.Equal(new DropTarget(5, g), CueList.TargetAt(list, x, 4, upperHalf: false));          // below the last cue: still inside
        Assert.Equal(new DropTarget(5, null), CueList.TargetAt(list, x, 5, upperHalf: true));        // above Y: outside
        Assert.Equal(new DropTarget(1, null), CueList.TargetAt(list, x, 0, upperHalf: false));       // START: below it
        Assert.Equal(new DropTarget(6, null), CueList.TargetAt(list, x, 6, upperHalf: true));        // END: above it
        Assert.False(CueList.MoveTo(list, g, CueList.TargetAt(list, g, 3, upperHalf: true)!.Value)); // a group onto itself: no move
    }

    [Fact]
    public void Dropping_moves_the_cue_and_sets_its_group()
    {
        var (g, kids) = Group("G", "1", "2");
        var x = A("X");
        var y = A("Y");
        var list = List(x, g, kids[0], kids[1], y);

        Assert.True(CueList.MoveTo(list, x, CueList.TargetAt(list, x, 3, upperHalf: false)!.Value));
        Assert.Equal("G >1 >X >2 Y", Layout(list));
        Assert.True(CueList.MoveTo(list, kids[1], CueList.TargetAt(list, kids[1], 5, upperHalf: false)!.Value));
        Assert.Equal("G >1 >X Y 2", Layout(list));
        Assert.True(CueList.MoveTo(list, g, CueList.TargetAt(list, g, 5, upperHalf: false)!.Value)); // the whole block
        Assert.Equal("Y 2 G >1 >X", Layout(list));
    }

    [Fact]
    public void A_group_dropped_inside_another_lands_before_or_after_it()
    {
        var (g, kids) = Group("G", "1", "2", "3");
        var (h, hk) = Group("H", "4");
        var list = List(g, kids[0], kids[1], kids[2], h, hk[0]);   // rows: 1 G, 2 1, 3 2, 4 3, 5 H, 6 4
        Assert.Equal(new DropTarget(1, null), CueList.TargetAt(list, h, 2, upperHalf: true));
        Assert.Equal(new DropTarget(5, null), CueList.TargetAt(list, h, 4, upperHalf: false));
    }

    [Fact]
    public void Normalize_drops_cues_outside_their_group()
    {
        var (g, kids) = Group("G", "1");
        var stray = new AudioCue { Number = "2", GroupId = g.Id };
        var orphan = new AudioCue { Number = "3", GroupId = Guid.NewGuid() };
        var list = List(g, kids[0], A("X"), stray, orphan);
        Assert.True(CueList.Normalize(list));
        Assert.Equal("G >1 X 2 3", Layout(list));
        Assert.False(CueList.Normalize(list));
    }

    [Fact]
    public void Group_length_is_the_longest_or_the_sum()
    {
        var (g, kids) = Group("G", "1", "2");
        kids[1].PreWait = 1;
        var list = List(g, kids[0], kids[1]);
        double? Len(Cue c) => c.Number == "1" ? 4 : 2;
        g.Mode = GroupMode.Simultaneous;
        Assert.Equal(4, CueList.GroupLength(list, g, Len));
        g.Mode = GroupMode.Sequence;
        Assert.Equal(7, CueList.GroupLength(list, g, Len));
        g.Mode = GroupMode.Enter;
        Assert.Null(CueList.GroupLength(list, g, Len));
    }
}

public class GroupEngineTests
{
    private readonly ManualScheduler _scheduler = new();
    private readonly FakePlayer _player = new();

    private (CueEngine Engine, GroupCue Group) Create(GroupMode mode, ContinueMode groupFollow = ContinueMode.DoNotContinue)
    {
        var ws = new Workspace();
        ws.Settings.GoLockoutMs = 0;
        var g = new GroupCue { Number = "G", Mode = mode, ContinueMode = groupFollow };
        ws.Cues.Add(g);
        // The cues' own follows are set on purpose: a group that plays as one doesn't use them.
        ws.Cues.Add(new AudioCue { Number = "1", GroupId = g.Id, ContinueMode = ContinueMode.AutoFollow });
        ws.Cues.Add(new AudioCue { Number = "2", GroupId = g.Id });
        ws.Cues.Add(new AudioCue { Number = "3", GroupId = g.Id });
        ws.Cues.Add(new AudioCue { Number = "4" });
        return (new CueEngine(_scheduler, new[] { _player }, ws), g);
    }

    private FakePlayer.Handle Handle(string number) => _player.Started.Last(h => h.Cue.Number == number);

    [Fact]
    public void Enter_group_starts_its_first_cue_and_the_playhead_goes_inside()
    {
        var (engine, _) = Create(GroupMode.Enter);
        engine.Go();
        Assert.Equal(new[] { "1" }, _player.StartedNumbers);
        Assert.Equal("3", engine.Playhead?.Number);   // 1 auto-follows into 2: both covered by this GO
        Handle("1").Finish();
        _scheduler.Advance(0.1);
        Assert.Equal(new[] { "1", "2" }, _player.StartedNumbers);
        engine.Go();
        Assert.Equal("4", engine.Playhead?.Number);   // past the last cue of the group
    }

    [Fact]
    public void Simultaneous_group_starts_all_its_cues_and_ends_with_the_last()
    {
        var (engine, group) = Create(GroupMode.Simultaneous, ContinueMode.AutoFollow);
        engine.Go();
        Assert.Equal(new[] { "1", "2", "3" }, _player.StartedNumbers);
        Assert.Null(engine.Playhead);                        // the GO covers the group's follow too
        Assert.Equal(CueRunState.Running, group.RunState);

        Handle("1").Finish();
        Handle("2").Finish();
        _scheduler.Advance(0.1);
        Assert.Equal(CueRunState.Running, group.RunState);
        Assert.DoesNotContain("4", _player.StartedNumbers);  // 1's own follow is not used
        Handle("3").Finish();
        _scheduler.Advance(0.1);
        Assert.Equal(CueRunState.Idle, group.RunState);
        Assert.Equal("4", _player.Started[^1].Cue.Number);     // the group's follow
    }

    [Fact]
    public void Sequence_group_plays_its_cues_one_after_the_other()
    {
        var (engine, group) = Create(GroupMode.Sequence, ContinueMode.AutoFollow);
        engine.Go();
        Assert.Equal(new[] { "1" }, _player.StartedNumbers);
        Assert.Null(engine.Playhead);
        Handle("1").Finish();
        _scheduler.Advance(0.1);
        Assert.Equal(new[] { "1", "2" }, _player.StartedNumbers);
        Handle("2").ReachEndAndHold();                       // a held video: the next one starts, it stays on
        _scheduler.Advance(0.1);
        Assert.Equal(new[] { "1", "2", "3" }, _player.StartedNumbers);
        Handle("3").Finish();
        _scheduler.Advance(0.1);
        Assert.Equal(CueRunState.Running, group.RunState);   // 2 still holds
        Handle("2").Finish();
        _scheduler.Advance(0.1);
        Assert.Equal(new[] { "1", "2", "3", "4" }, _player.StartedNumbers);
    }

    [Fact]
    public void Stopping_a_group_stops_its_cues_and_starts_nothing_more()
    {
        var (engine, group) = Create(GroupMode.Sequence, ContinueMode.AutoFollow);
        engine.Go();
        engine.Stop(group, TimeSpan.Zero);
        _scheduler.Advance(0.1);
        Assert.Equal(TimeSpan.Zero, Handle("1").StoppedWithFade);
        Assert.Equal(new[] { "1" }, _player.StartedNumbers);
        Assert.Empty(engine.Running);
    }

    [Fact]
    public void Pausing_a_group_pauses_its_cues()
    {
        var (engine, group) = Create(GroupMode.Simultaneous);
        engine.Go();
        engine.TogglePause(group);
        Assert.True(Handle("1").Paused && Handle("2").Paused && Handle("3").Paused);
        engine.TogglePause(group);
        Assert.False(Handle("1").Paused);
    }

    [Fact]
    public void Fade_cue_on_a_group_fades_its_cues()
    {
        var (engine, group) = Create(GroupMode.Simultaneous);
        engine.Workspace.Cues.Add(new FadeCue { Number = "F", TargetNumber = "G", FadeDuration = 2, FadeVolume = true, TargetVolumeDb = -20 });
        engine.Go();
        engine.Fire(engine.Workspace.Cues[^1]);
        Assert.All(new[] { "1", "2", "3" }, n => Assert.Single(Handle(n).Fades));
    }

    [Fact]
    public void Preloads_cover_the_cues_a_group_plays()
    {
        var (engine, _) = Create(GroupMode.Simultaneous);
        Assert.Equal(new[] { "G", "1", "2", "3" }, engine.CuesFiredByNextGo().Select(c => c.Number));
    }

    [Fact]
    public void Timeline_places_the_cues_of_a_sequence_one_after_the_other()
    {
        var (engine, group) = Create(GroupMode.Sequence);
        double? Len(Cue c) => c is GroupCue g ? CueList.GroupLength(engine.Workspace.Cues, g, Len) : 5;
        var t = TimelineBuilder.Build(engine.Workspace.Cues, Len);
        TimelinePoint StartOf(string n) => t.Items.Single(i => i.Cue.Number == n).Start;
        Assert.Equal(0, StartOf("1").Time);
        Assert.Equal(5, StartOf("2").Time);
        Assert.Equal(10, StartOf("3").Time);
        Assert.Equal(15, t.Items.Single(i => i.Cue == group).End?.Time);
    }
}
