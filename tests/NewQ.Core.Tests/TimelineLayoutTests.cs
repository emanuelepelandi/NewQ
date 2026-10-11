using NewQ.Core.Model;
using NewQ.Core.Timeline;
using Xunit;

namespace NewQ.Core.Tests;

public class TimelineLayoutTests
{
    private static AudioCue Audio(string n, double seconds, string? link = null) => new()
    {
        Number = n, MediaDuration = seconds, FilePath = n + ".wav",
        LinkAction = link is null ? LinkAction.None : LinkAction.Start, LinkTarget = link ?? "",
    };

    [Fact]
    public void Overlapping_cues_of_one_lane_get_one_row_each()
    {
        // Three cues starting together (1 starts 2, 2 starts 3) on the same audio route.
        var ws = new Workspace();
        ws.EnsureRoutes(1);
        ws.Cues.Add(Audio("1", 10, "2"));
        ws.Cues.Add(Audio("2", 10, "3"));
        ws.Cues.Add(Audio("3", 10));
        foreach (var cue in ws.Cues.OfType<AudioCue>()) cue.AudioRouteId = ws.AudioRoutes[0].Id;

        var layout = TimelineLayout.Build(TimelineBuilder.Build(ws.Cues), ws, lanesByRoute: true);

        var lane = Assert.Single(layout.Lanes);
        Assert.Equal(3, lane.Tracks);
        Assert.Equal(new[] { 0, 1, 2 }, layout.Placements.Select(p => p.Track).OrderBy(t => t));
    }

    [Fact]
    public void A_row_is_reused_once_the_cue_on_it_has_ended()
    {
        var ws = new Workspace();
        ws.EnsureRoutes(1);
        ws.Cues.Add(Audio("1", 5, "2"));
        ws.Cues.Add(new AudioCue { Number = "2", MediaDuration = 2, FilePath = "2.wav", PreWait = 0 });
        ws.Cues.Add(new AudioCue { Number = "3", MediaDuration = 2, FilePath = "3.wav" });
        var layout = TimelineLayout.Build(TimelineBuilder.Build(ws.Cues), ws, lanesByRoute: false);
        Assert.Equal(2, layout.Lanes.Single().Tracks);
        Assert.Equal(0, layout.Placements.Single(p => p.Item.Cue.Number == "3").Track); // next GO: row 0 is free again
    }
}
