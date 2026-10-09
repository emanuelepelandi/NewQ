using NewQ.Core.Engine;
using NewQ.Core.Model;
using NewQ.Core.Serialization;
using NewQ.Core.Timeline;
using Xunit;

namespace NewQ.Core.Tests;

/// <summary>Follow (wait / auto), links, and the conversion of the old auto-continue.</summary>
public class FollowLinkTests
{
    private static AudioCue Audio(string n, double seconds, ContinueMode mode = ContinueMode.DoNotContinue)
        => new() { Number = n, MediaDuration = seconds, ContinueMode = mode, FilePath = n + ".wav" };

    private static T Link<T>(T cue, LinkAction action, string target) where T : Cue
    {
        cue.LinkAction = action;
        cue.LinkTarget = target;
        return cue;
    }

    [Fact]
    public void Files_before_format_3_turn_auto_continue_into_start_links()
    {
        const string json = """
        {
          "FormatVersion": 2,
          "Cues": [
            { "$type": "audio", "Number": "1", "ContinueMode": "AutoContinue", "PostWait": 2, "PreWait": 1 },
            { "$type": "audio", "Number": "", "ContinueMode": "AutoContinue", "PreWait": 0.5 },
            { "$type": "audio", "Number": "3", "ContinueMode": "AutoFollow" },
            { "$type": "audio", "Number": "4" }
          ]
        }
        """;
        var ws = WorkspaceSerializer.Deserialize(json);
        ws.FilePath = "show.newq";
        ws.EnsureRoutes(1);

        Assert.Equal(Workspace.CurrentFormatVersion, ws.FormatVersion);
        var c = ws.Cues;
        Assert.All(c.Take(2), cue => Assert.Equal(ContinueMode.DoNotContinue, cue.ContinueMode));
        Assert.Equal((LinkAction.Start, c[1].Number), (c[0].LinkAction, c[0].LinkTarget));
        Assert.False(string.IsNullOrEmpty(c[1].Number));                 // the target got a number
        Assert.Equal(2.5, c[1].PreWait);                                  // its post-wait moved here
        Assert.Equal(0, c[0].PostWait);
        Assert.Equal((LinkAction.Start, "3"), (c[1].LinkAction, c[1].LinkTarget));
        Assert.Equal(ContinueMode.AutoFollow, c[2].ContinueMode);         // untouched

        // The same show: one GO fires 1, 2 and 3; the playhead lands on 4.
        Assert.Equal(new[] { c[0], c[1], c[2], c[3] }, ShowSequence.FiredBy(c, c[0]));
        Assert.Null(ShowSequence.NextAfter(c, c[0]));
    }

    [Fact]
    public void New_workspaces_are_not_converted()
    {
        var ws = new Workspace();
        ws.Cues.Add(Audio("1", 5, ContinueMode.AutoContinue));
        ws.EnsureRoutes(1);
        Assert.Equal(ContinueMode.AutoContinue, ws.Cues[0].ContinueMode);
        Assert.Equal(LinkAction.None, ws.Cues[0].LinkAction);
    }

    [Fact]
    public void Timeline_places_linked_cues_with_their_source()
    {
        var cues = new Cue[]
        {
            Link(Audio("1", 10), LinkAction.Start, "3"),
            Audio("2", 4),
            new AudioCue { Number = "3", MediaDuration = 6, PreWait = 2, FilePath = "3.wav" },
        };
        var t = TimelineBuilder.Build(cues);

        // 1 and 3 (linked, after its pre-wait) in the first block; 2 is the next GO; 3 keeps its own GO too.
        Assert.Equal(3, t.Blocks.Count);
        Assert.Equal(new TimelinePoint(0, 2), t.ItemOf(cues[2], 0)!.Start);
        Assert.Equal(2, t.BlockOf(cues[2]));
        Assert.Equal("2", t.Blocks[1].FirstCue.Number);
        Assert.Equal("3", t.Blocks[2].FirstCue.Number);
        Assert.Equal(10, t.Blocks[0].Length); // 3 ends at 2 + 6 = 8
    }

    [Fact]
    public void Timeline_link_stop_and_fade_out_end_their_targets()
    {
        var music = Audio("1", 60);
        music.Loop = true; // open-ended: still playing at the next GOs
        var cut = Link(Audio("2", 5), LinkAction.Stop, "1");
        var bed = Audio("3", 60);
        bed.Loop = true;
        var fade = Link(Audio("4", 5), LinkAction.FadeOut, "3");
        var t = TimelineBuilder.Build(new Cue[] { music, cut, bed, fade }, linkFadeSeconds: 4);

        var first = t.Items.Single(i => i.Cue == music);
        Assert.Equal(new TimelinePoint(1, 0), first.End);                // stopped by 2, at the start of its GO
        Assert.True(first.EndedByCue);
        var bedItem = t.Items.Single(i => i.Cue == bed);
        Assert.Equal(bedItem.Start.Block + 1, bedItem.End!.Value.Block);
        Assert.Equal(4, bedItem.End!.Value.Time);                         // faded out by 4 over 4 s
    }

    [Fact]
    public void Wait_follow_waits_after_the_end_in_the_timeline()
    {
        var a = Audio("1", 8, ContinueMode.WaitFollow);
        a.PostWait = 3;
        var t = TimelineBuilder.Build(new Cue[] { a, Audio("2", 4) });
        Assert.Single(t.Blocks);
        Assert.Equal(new TimelinePoint(0, 11), t.Items.Single(i => i.Cue.Number == "2").Start);
        Assert.Equal(15, t.Blocks[0].Length);
    }

    [Fact]
    public void Playhead_skips_only_what_the_GO_fires_right_below()
    {
        var cues = new Cue[] { Link(Audio("1", 5), LinkAction.Start, "2"), Audio("2", 5), Audio("3", 5), Link(Audio("4", 5), LinkAction.Stop, "3") };
        Assert.Same(cues[2], ShowSequence.NextAfter(cues, cues[0]));
        Assert.Same(cues[3], ShowSequence.NextAfter(cues, cues[2]));
        Assert.Equal(new[] { cues[3] }, ShowSequence.FiredBy(cues, cues[3])); // a Stop link fires nothing
    }

    [Fact]
    public void Start_link_loops_are_found_however_long()
    {
        var cues = new Cue[] { Link(Audio("1", 5), LinkAction.Start, "2"), Link(Audio("2", 5), LinkAction.Start, "3"), Link(Audio("3", 5), LinkAction.Start, "1"), Audio("4", 5) };
        Assert.Equal("1 -> 2 -> 3 -> 1", ShowSequence.Describe(ShowSequence.StartLinkLoop(cues, cues[0])!));
        Assert.NotNull(ShowSequence.StartLinkLoop(cues, cues[2]));
        Assert.Null(ShowSequence.StartLinkLoop(cues, cues[3]));
        Assert.NotNull(ShowSequence.FindStartLinkLoop(cues));
    }

    [Fact]
    public void Stop_links_and_follows_never_make_a_forbidden_loop()
    {
        var cues = new Cue[]
        {
            Link(Audio("1", 5, ContinueMode.AutoFollow), LinkAction.Stop, "2"),
            Link(Audio("2", 5, ContinueMode.AutoFollow), LinkAction.FadeOut, "1"),
            Link(Audio("3", 5, ContinueMode.WaitFollow), LinkAction.Start, "1"),   // follow down + link up: a repeating show
        };
        Assert.Null(ShowSequence.FindStartLinkLoop(cues));
    }

    [Fact]
    public void Engine_stops_a_GO_that_loops_without_ever_waiting()
    {
        // Two instant cues: 1 follows into 2, 2 starts 1 again with a link. Allowed (it goes through a follow) but
        // nothing in it takes time: the engine must stop it instead of spinning forever.
        var scheduler = new ManualScheduler();
        var ws = new Workspace();
        ws.Settings.GoLockoutMs = 0;
        ws.Cues.Add(new StopCue { Number = "1", TargetNumber = "x", ContinueMode = ContinueMode.AutoFollow });
        ws.Cues.Add(Link(new StopCue { Number = "2", TargetNumber = "x" }, LinkAction.Start, "1"));
        var engine = new CueEngine(scheduler, new[] { new FakePlayer() }, ws);
        string? error = null;
        engine.Log += (_, e) => { if (e.Level == EngineLogLevel.Error) error ??= e.Message; };

        engine.Go();
        for (var i = 0; i < 5000 && engine.Running.Count > 0; i++) scheduler.Advance(0);
        scheduler.Advance(0.1);

        Assert.Empty(engine.Running);
        Assert.Contains("loop", error);
    }

    [Fact]
    public void A_slow_repeating_show_is_not_stopped()
    {
        var scheduler = new ManualScheduler();
        var player = new FakePlayer();
        var ws = new Workspace();
        ws.Cues.Add(Audio("1", 5, ContinueMode.AutoFollow));
        ws.Cues.Add(Link(Audio("2", 5), LinkAction.Start, "1"));
        var engine = new CueEngine(scheduler, new[] { player }, ws);

        engine.Go();
        for (var lap = 0; lap < 400; lap++)
        {
            player.Started[^1].Finish(); // each cue ends: the show goes round
            scheduler.Advance(0.5);
        }
        Assert.True(player.Started.Count > 400);
    }
}
