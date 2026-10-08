using NewQ.Core.Model;

namespace NewQ.Core.Timeline;

/// <summary>A moment of the show: seconds from the start of a GO block.</summary>
public readonly record struct TimelinePoint(int Block, double Time);

/// <summary>Where the show is: a moment of a block, or the end of a block while the operator's GO is awaited.</summary>
public readonly record struct TimelineNow(TimelinePoint At, bool WaitingForGo);

/// <summary>Level of a cue (fraction of its own volume/opacity, 1 = as programmed) at a moment.</summary>
public readonly record struct EnvelopePoint(TimelinePoint At, double Level);

public enum TimelineItemKind { Media, Wait, Fade, Stop, Instant }

/// <summary>One cue placed on the timeline.</summary>
public sealed class TimelineItem
{
    public TimelineItem(Cue cue, TimelineItemKind kind, TimelinePoint start)
    {
        Cue = cue;
        Kind = kind;
        Start = start;
    }

    public Cue Cue { get; }
    public TimelineItemKind Kind { get; }

    /// <summary>When the cue starts (after its pre-wait).</summary>
    public TimelinePoint Start { get; }

    /// <summary>When the cue ends; null = it keeps going to the end of the show (loop, live, held image).</summary>
    public TimelinePoint? End { get; set; }

    /// <summary>The cue doesn't end by itself and nothing stops it (drawn as open to the right).</summary>
    public bool IsOpenEnded => End is null;

    /// <summary>True when the end comes from a Stop or Fade cue rather than from the media length.</summary>
    public bool EndedByCue { get; set; }

    /// <summary>The cue is disarmed: it is skipped (zero length) but the sequence continues.</summary>
    public bool Disarmed { get; init; }

    /// <summary>Level changes: fade in, Fade cues, end fades. Empty = flat at 1.</summary>
    public List<EnvelopePoint> Envelope { get; } = new();

    /// <summary>The cue fired by the GO that opens its block (as opposed to one chained after it).</summary>
    public bool StartsBlock { get; init; }
}

/// <summary>What one GO fires: the cue at the playhead and every cue chained to it.</summary>
/// <param name="Length">Seconds until the last cue of the block ends (open-ended cues excluded).</param>
public sealed record TimelineBlock(int Index, Cue FirstCue, double Length);

/// <summary>The predicted course of the show, GO by GO (see <see cref="TimelineBuilder"/>).</summary>
public sealed class ShowTimeline
{
    public static readonly ShowTimeline Empty = new(Array.Empty<TimelineBlock>(), Array.Empty<TimelineItem>());

    public ShowTimeline(IReadOnlyList<TimelineBlock> blocks, IReadOnlyList<TimelineItem> items)
    {
        Blocks = blocks;
        Items = items;
    }

    public IReadOnlyList<TimelineBlock> Blocks { get; }
    public IReadOnlyList<TimelineItem> Items { get; }

    /// <summary>The block a cue belongs to, or -1.</summary>
    public int BlockOf(Cue cue)
    {
        foreach (var item in Items)
            if (ReferenceEquals(item.Cue, cue)) return item.Start.Block;
        return -1;
    }
}
