using NewQ.Core.Model;

namespace NewQ.Core.Timeline;

/// <summary>
/// A row of the timeline: a video/audio route (or MIDI, network, control), or a cue type. A class, not a record:
/// it is a dictionary key while <see cref="Tracks"/> grows, so it must keep its identity.
/// </summary>
public sealed class TimelineLane
{
    public TimelineLane(string key, string label, int order, bool visual)
    {
        Key = key;
        Label = label;
        Order = order;
        Visual = visual;
    }

    public string Key { get; }
    public string Label { get; }
    public int Order { get; }
    public bool Visual { get; }

    /// <summary>Sub-rows needed so that cues overlapping in time don't cover each other.</summary>
    public int Tracks { get; internal set; } = 1;
}

/// <summary>Where a cue of the timeline is drawn: its lane and the sub-row in it.</summary>
public sealed record TimelinePlacement(TimelineItem Item, TimelineLane Lane, int Track);

/// <summary>
/// Lanes and rows of a timeline, independent of how it is drawn (the app's view and the web remote share it).
/// A cue goes on the first row of its lane that is free when it starts.
/// </summary>
public sealed class TimelineLayout
{
    /// <summary>Seconds an instant cue (MIDI, network...) keeps its row busy, so labels don't overlap.</summary>
    public const double InstantSeconds = 0.6;

    private static readonly string[] TypeOrder = { "Video", "Cattura", "Immagine", "Audio", "MIDI", "Rete", "Attesa", "Fade", "Stop" };

    private TimelineLayout(IReadOnlyList<TimelineLane> lanes, IReadOnlyList<TimelinePlacement> placements)
    {
        Lanes = lanes;
        Placements = placements;
    }

    public IReadOnlyList<TimelineLane> Lanes { get; }
    public IReadOnlyList<TimelinePlacement> Placements { get; }

    public static TimelineLayout Build(ShowTimeline timeline, Workspace? workspace, bool lanesByRoute)
    {
        var lanes = new Dictionary<string, TimelineLane>();
        var placements = new List<TimelinePlacement>();
        if (workspace is null) return new TimelineLayout(Array.Empty<TimelineLane>(), placements);

        var videoRoutes = workspace.VideoRoutes.ToList();
        var audioRoutes = workspace.AudioRoutes.ToList();

        TimelineLane LaneFor(Cue cue)
        {
            string key, label;
            int order;
            var visual = cue is VisualCue;
            if (lanesByRoute)
            {
                switch (cue)
                {
                    case VisualCue v:
                        var vr = videoRoutes.FirstOrDefault(r => r.Id == v.VideoRouteId) ?? videoRoutes.FirstOrDefault();
                        key = "v:" + vr?.Id; label = vr?.Name ?? "Video"; order = vr is null ? 0 : videoRoutes.IndexOf(vr);
                        break;
                    case AudioCue a:
                        var ar = audioRoutes.FirstOrDefault(r => r.Id == a.AudioRouteId) ?? audioRoutes.FirstOrDefault();
                        key = "a:" + ar?.Id; label = ar?.Name ?? "Audio"; order = 100 + (ar is null ? 0 : audioRoutes.IndexOf(ar));
                        break;
                    case MidiCue: key = "midi"; label = "MIDI"; order = 200; break;
                    case NetworkCue: key = "net"; label = "Rete"; order = 201; break;
                    default: key = "ctrl"; label = "Controllo"; order = 202; break;
                }
            }
            else
            {
                key = cue.TypeName; label = cue.TypeName;
                order = Array.IndexOf(TypeOrder, cue.TypeName) is var i and >= 0 ? i : 99;
            }
            if (!lanes.TryGetValue(key, out var lane)) lanes[key] = lane = new TimelineLane(key, label, order, visual);
            return lane;
        }

        var trackEnds = new Dictionary<TimelineLane, List<(int Block, double Time)>>();
        foreach (var item in timeline.Items.OrderBy(i => i.Start.Block).ThenBy(i => i.Start.Time))
        {
            var lane = LaneFor(item.Cue);
            if (!trackEnds.TryGetValue(lane, out var ends)) trackEnds[lane] = ends = new();
            var end = item.End is TimelinePoint e
                ? (e.Block, Math.Max(e.Time, item.Start.Time + InstantSeconds))
                : (int.MaxValue, double.MaxValue);
            var track = ends.FindIndex(t => t.Block < item.Start.Block || t.Block == item.Start.Block && t.Time <= item.Start.Time);
            if (track < 0) { ends.Add(end); track = ends.Count - 1; }
            else ends[track] = end;
            lane.Tracks = Math.Max(lane.Tracks, track + 1);
            placements.Add(new TimelinePlacement(item, lane, track));
        }
        return new TimelineLayout(lanes.Values.OrderBy(l => l.Order).ThenBy(l => l.Label).ToList(), placements);
    }
}
