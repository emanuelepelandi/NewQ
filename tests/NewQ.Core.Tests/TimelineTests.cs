using NewQ.Core.Model;
using NewQ.Core.Timeline;
using Xunit;

namespace NewQ.Core.Tests;

public class TimelineTests
{
    private static AudioCue Audio(string n, double seconds, ContinueMode mode = ContinueMode.DoNotContinue, double pre = 0, double post = 0)
        => new() { Number = n, MediaDuration = seconds, ContinueMode = mode, PreWait = pre, PostWait = post, FilePath = n + ".wav" };

    private static TimelineItem ItemOf(ShowTimeline t, string number) => t.Items.Single(i => i.Cue.Number == number);

    [Fact]
    public void Each_go_opens_a_block_and_continue_chains_stay_in_it()
    {
        var t = TimelineBuilder.Build(new Cue[]
        {
            Audio("1", 10, ContinueMode.WaitFollow, post: 2),     // ends at 10, fires 2 two seconds later
            Audio("2", 5, ContinueMode.AutoFollow, pre: 1),        // starts at 12 + 1 = 13, ends at 18, then fires 3
            Audio("3", 4),                                          // starts at 18, ends at 22; chain stops
            Audio("4", 6),                                          // next GO
        });

        Assert.Equal(2, t.Blocks.Count);
        Assert.Equal(new TimelinePoint(0, 0), ItemOf(t, "1").Start);
        Assert.Equal(new TimelinePoint(0, 13), ItemOf(t, "2").Start);
        Assert.Equal(new TimelinePoint(0, 18), ItemOf(t, "3").Start);
        Assert.Equal(22, t.Blocks[0].Length);
        Assert.Equal(new TimelinePoint(1, 0), ItemOf(t, "4").Start);
        Assert.True(ItemOf(t, "1").StartsBlock);
        Assert.False(ItemOf(t, "2").StartsBlock);
    }

    [Fact]
    public void Disarmed_cues_take_no_time_but_keep_the_chain()
    {
        var skipped = Audio("2", 30, ContinueMode.AutoFollow);
        skipped.Armed = false;
        var t = TimelineBuilder.Build(new Cue[] { Audio("1", 10, ContinueMode.AutoFollow), skipped, Audio("3", 5) });

        Assert.Single(t.Blocks);
        Assert.True(ItemOf(t, "2").Disarmed);
        Assert.Equal(new TimelinePoint(0, 10), ItemOf(t, "3").Start);
        Assert.Equal(15, t.Blocks[0].Length);
    }

    [Fact]
    public void Fades_in_and_out_shape_the_envelope()
    {
        var cue = Audio("1", 20);
        cue.FadeIn = 2;
        cue.FadeOut = 4;
        var item = ItemOf(TimelineBuilder.Build(new Cue[] { cue }), "1");

        Assert.Equal(0, TimelineBuilder.LevelAt(item, new(0, 0)), 3);
        Assert.Equal(0.5, TimelineBuilder.LevelAt(item, new(0, 1)), 3);
        Assert.Equal(1, TimelineBuilder.LevelAt(item, new(0, 10)), 3);
        Assert.Equal(0.5, TimelineBuilder.LevelAt(item, new(0, 18)), 3);
        Assert.Equal(new TimelinePoint(0, 20), item.End);
    }

    [Fact]
    public void Looping_cue_runs_through_later_blocks_until_a_stop_cue_ends_it()
    {
        var loop = new AudioCue { Number = "1", Loop = true, MediaDuration = 3, FilePath = "loop.wav" };
        var t = TimelineBuilder.Build(new Cue[]
        {
            loop,
            Audio("2", 5),
            new StopCue { Number = "3", TargetNumber = "1", FadeTime = 2 },
        });

        var item = ItemOf(t, "1");
        Assert.Equal(3, t.Blocks.Count);
        Assert.Equal(new TimelinePoint(2, 2), item.End); // stopped in the third block with a 2 s fade
        Assert.True(item.EndedByCue);
        Assert.Equal(1, TimelineBuilder.LevelAt(item, new(2, 0)), 3);
        Assert.Equal(0.5, TimelineBuilder.LevelAt(item, new(2, 1)), 3);
        Assert.Equal(2, t.Blocks[2].Length);
    }

    [Fact]
    public void Fade_cue_changes_the_level_of_its_target_and_can_stop_it()
    {
        var music = Audio("1", 60);
        music.VolumeDb = 0;
        music.LinkAction = LinkAction.Start;
        music.LinkTarget = "2";
        var fade = new FadeCue { Number = "2", TargetNumber = "1", FadeDuration = 4, FadeVolume = true, TargetVolumeDb = -6.0206, StopWhenDone = false,
                             PreWait = 10, ContinueMode = ContinueMode.WaitFollow, PostWait = 2 };   // 10 → 14, then 3 at 16
        var t = TimelineBuilder.Build(new Cue[] { music, fade, new FadeCue { Number = "3", TargetNumber = "1", FadeDuration = 2, FadeVolume = true, TargetVolumeDb = -60, StopWhenDone = true } });

        var item = ItemOf(t, "1");
        Assert.Equal(0.5, TimelineBuilder.LevelAt(item, new(0, 14)), 2);   // fade 2: 10 → 14 s down to half
        Assert.Equal(1, TimelineBuilder.LevelAt(item, new(0, 9)), 2);
        Assert.Equal(new TimelinePoint(0, 18), item.End);                   // fade 3 starts at 16 and stops it after 2 s
        Assert.True(item.EndedByCue);
        Assert.Equal(0.25, TimelineBuilder.LevelAt(item, new(0, 17)), 2);
        Assert.Single(t.Blocks);
    }

    [Fact]
    public void Video_without_a_known_length_and_live_sources_are_open_ended()
    {
        var video = new VideoCue { Number = "1", FilePath = "v.mp4" };
        var live = new LiveCue { Number = "2", DeviceName = "Camera" };
        var t = TimelineBuilder.Build(new Cue[] { video, live });
        Assert.True(ItemOf(t, "1").IsOpenEnded);
        Assert.True(ItemOf(t, "2").IsOpenEnded);

        var probed = TimelineBuilder.Build(new Cue[] { video }, c => c is VideoCue ? 12.5 : c.Duration);
        Assert.Equal(new TimelinePoint(0, 12.5), ItemOf(probed, "1").End);
    }

    [Fact]
    public void Hold_on_finish_keeps_the_bar_open_and_follows_at_the_end_of_the_file()
    {
        var video = new VideoCue { Number = "1", FilePath = "v.mp4", HoldAtEnd = true, FadeOut = 2, ContinueMode = ContinueMode.AutoFollow };
        var t = TimelineBuilder.Build(new Cue[] { video, Audio("2", 5) }, c => c is VideoCue ? 10 : c.Duration);
        var held = ItemOf(t, "1");
        Assert.True(held.IsOpenEnded);
        Assert.Equal(new TimelinePoint(0, 10), held.HoldFrom);
        Assert.Empty(held.Envelope);                                   // no final fade-out while holding
        Assert.Equal(new TimelinePoint(0, 10), ItemOf(t, "2").Start);  // the follow fires at the end of the file
        Assert.Single(t.Blocks);
        Assert.Equal(15, t.Blocks[0].Length);

        video.Loop = true;                                             // with Loop, Hold has no effect
        Assert.Null(ItemOf(TimelineBuilder.Build(new Cue[] { video }, c => null), "1").HoldFrom);
    }

    [Fact]
    public void Trimmed_video_plays_from_its_start_point_to_its_end_point()
    {
        var video = new VideoCue { Number = "1", FilePath = "v.mp4", StartTime = 10, EndTime = 25, MediaDuration = 60, ContinueMode = ContinueMode.AutoFollow };
        Assert.Equal(15, video.Duration);
        var t = TimelineBuilder.Build(new Cue[] { video, Audio("2", 5) });
        Assert.Equal(new TimelinePoint(0, 15), ItemOf(t, "1").End);
        Assert.Equal(new TimelinePoint(0, 15), ItemOf(t, "2").Start);

        video.EndTime = 0;                       // until the end of the file
        Assert.Equal(50, video.Duration);
        video.EndTime = 90;                      // past the end of the file: the file ends first
        Assert.Equal(50, video.Duration);
        video.HoldAtEnd = true;                  // held on the end point's frame
        Assert.Equal(new TimelinePoint(0, 50), ItemOf(TimelineBuilder.Build(new Cue[] { video }), "1").HoldFrom);
        video.Loop = true;
        Assert.Null(video.Duration);
    }

    [Fact]
    public void Play_length_of_a_trimmed_file()
    {
        Assert.Equal(30, MediaTrim.PlayLength(0, 0, 30));
        Assert.Equal(20, MediaTrim.PlayLength(10, 0, 30));
        Assert.Equal(5, MediaTrim.PlayLength(10, 15, 30));
        Assert.Equal(0, MediaTrim.PlayLength(40, 0, 30));  // start past the end: nothing plays
    }

    [Fact]
    public void Auto_follow_on_a_cue_that_never_ends_stops_the_chain()
    {
        var loop = new AudioCue { Number = "1", Loop = true, ContinueMode = ContinueMode.AutoFollow, FilePath = "l.wav" };
        var t = TimelineBuilder.Build(new Cue[] { loop, Audio("2", 5) });
        Assert.Equal(2, t.Blocks.Count);
    }
}
