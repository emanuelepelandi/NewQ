using NewQ.Core.Model;

namespace NewQ.Core.Timeline;

/// <summary>
/// Predicts the course of the show from the cue list, with the engine's rules: a GO fires the playhead cue;
/// pre-wait delays its start; auto-continue fires the next cue post-wait seconds after the start, auto-follow when
/// the cue ends; the chain stops at the first "do not continue" cue, and the next GO starts a new block. Disarmed
/// cues take no time but keep the chain going. Fade and Stop cues act on the running cues with the target number.
/// The wait between GOs is up to the operator: each block is placed after the previous one has ended. Cues that never
/// end by themselves (loops, live sources, held images) go on through the following blocks until something stops them.
/// </summary>
public static class TimelineBuilder
{
    /// <param name="lengthOf">
    /// Seconds a cue plays (null = until stopped). The default uses <see cref="Cue.Duration"/>; the app adds the
    /// lengths of video files it has probed.
    /// </param>
    public static ShowTimeline Build(IReadOnlyList<Cue> cues, Func<Cue, double?>? lengthOf = null)
    {
        lengthOf ??= c => c.Duration;
        var items = new List<TimelineItem>();
        var blocks = new List<TimelineBlock>();
        var carried = new List<TimelineItem>(); // open-ended cues still running from earlier blocks

        var i = 0;
        while (i < cues.Count)
        {
            var block = blocks.Count;
            var blockItems = new List<TimelineItem>();
            var fireAt = 0.0;
            var j = i;
            while (true)
            {
                var cue = cues[j];
                var start = new TimelinePoint(block, fireAt + Math.Max(0, cue.PreWait));
                var item = CreateItem(cue, start, j == i, lengthOf);
                ApplyEffects(item, carried.Concat(blockItems));
                blockItems.Add(item);

                if (cue.ContinueMode == ContinueMode.DoNotContinue || j + 1 >= cues.Count) break;
                if (cue.ContinueMode == ContinueMode.AutoContinue)
                    fireAt = start.Time + Math.Max(0, cue.PostWait);
                else if (item.End is TimelinePoint end && end.Block == block)
                    fireAt = end.Time; // auto-follow: when this cue ends
                else
                    break; // auto-follow on a cue that never ends: the chain stops here (Show Check warns about it)
                j++;
            }

            var length = blockItems.Select(it => it.Start.Time)
                .Concat(blockItems.Concat(carried).Where(it => it.End?.Block == block).Select(it => it.End!.Value.Time))
                .Concat(blockItems.Concat(carried).SelectMany(it => it.Envelope).Where(p => p.At.Block == block).Select(p => p.At.Time))
                .DefaultIfEmpty(0).Max();
            blocks.Add(new TimelineBlock(block, cues[i], length));
            items.AddRange(blockItems);

            carried.RemoveAll(it => !it.IsOpenEnded);
            carried.AddRange(blockItems.Where(it => it.IsOpenEnded));
            i = j + 1;
        }

        return new ShowTimeline(blocks, items);
    }

    private static TimelineItem CreateItem(Cue cue, TimelinePoint start, bool startsBlock, Func<Cue, double?> lengthOf)
    {
        var kind = cue switch
        {
            MediaCue => TimelineItemKind.Media,
            WaitCue => TimelineItemKind.Wait,
            FadeCue => TimelineItemKind.Fade,
            StopCue => TimelineItemKind.Stop,
            _ => TimelineItemKind.Instant,
        };
        var item = new TimelineItem(cue, kind, start) { Disarmed = !cue.Armed, StartsBlock = startsBlock };

        double? length = !cue.Armed ? 0 : kind switch
        {
            TimelineItemKind.Media => lengthOf(cue),
            TimelineItemKind.Wait or TimelineItemKind.Fade => cue.Duration ?? 0,
            _ => 0,
        };
        if (length is double l) item.End = start with { Time = start.Time + Math.Max(0, l) };

        if (kind == TimelineItemKind.Media && cue.Armed && cue is MediaCue media)
        {
            if (media.FadeIn > 0)
            {
                item.Envelope.Add(new(start, 0));
                item.Envelope.Add(new(start with { Time = start.Time + media.FadeIn }, 1));
            }
            // The final fade-out only exists when the media ends by itself.
            if (item.End is TimelinePoint end && media.FadeOut > 0)
            {
                var from = Math.Max(start.Time + media.FadeIn, end.Time - media.FadeOut);
                item.Envelope.Add(new(end with { Time = from }, 1));
                item.Envelope.Add(new(end, 0));
            }
        }
        return item;
    }

    /// <summary>A Fade or Stop cue changes the cues running when it starts.</summary>
    private static void ApplyEffects(TimelineItem effect, IEnumerable<TimelineItem> others)
    {
        if (effect.Disarmed) return;
        var at = effect.Start;
        var running = others.Where(o => o.Kind == TimelineItemKind.Media && !o.Disarmed && IsRunningAt(o, at)).ToList();

        switch (effect.Cue)
        {
            case FadeCue fade when fade.TargetNumber.Length > 0:
                foreach (var target in running.Where(o => SameNumber(o.Cue, fade.TargetNumber)))
                {
                    var level = TargetLevel(fade, target.Cue);
                    if (level is null && !fade.StopWhenDone) continue; // fades nothing this cue has
                    var endAt = at with { Time = at.Time + fade.FadeDuration };
                    Ramp(target, at, endAt, level ?? LevelAt(target, at));
                    if (fade.StopWhenDone) EndAt(target, endAt);
                }
                break;

            case StopCue stop:
                foreach (var target in running.Where(o => stop.TargetNumber.Length == 0 || SameNumber(o.Cue, stop.TargetNumber)))
                {
                    var endAt = at with { Time = at.Time + stop.FadeTime };
                    Ramp(target, at, endAt, 0);
                    EndAt(target, endAt);
                }
                break;
        }
    }

    /// <summary>Target level of a Fade cue as a fraction of the cue's own level; null if it changes nothing the cue has.</summary>
    private static double? TargetLevel(FadeCue fade, Cue target)
    {
        if (fade.FadeOpacity && target is VisualCue visual)
            return visual.Opacity > 0 ? Math.Clamp(fade.TargetOpacity / visual.Opacity, 0, 2) : fade.TargetOpacity;
        if (fade.FadeVolume && VolumeOf(target) is double volumeDb)
            return Math.Clamp(Decibels.ToGain(fade.TargetVolumeDb) / Math.Max(1e-6, Decibels.ToGain(volumeDb)), 0, 2);
        return null;
    }

    private static double? VolumeOf(Cue cue) => cue switch
    {
        AudioCue a => a.VolumeDb,
        VideoCue v => v.VolumeDb,
        LiveCue l => l.VolumeDb,
        _ => null,
    };

    private static bool SameNumber(Cue cue, string number) => string.Equals(cue.Number.Trim(), number.Trim(), StringComparison.OrdinalIgnoreCase);

    private static int Compare(TimelinePoint a, TimelinePoint b) => a.Block != b.Block ? a.Block.CompareTo(b.Block) : a.Time.CompareTo(b.Time);

    private static bool IsRunningAt(TimelineItem item, TimelinePoint at)
        => Compare(item.Start, at) <= 0 && (item.End is not TimelinePoint end || Compare(end, at) > 0);

    /// <summary>Level of a cue at a moment (linear between envelope points; 1 before any change after the fade-in).</summary>
    public static double LevelAt(TimelineItem item, TimelinePoint at)
    {
        var points = item.Envelope;
        if (points.Count == 0) return 1;
        EnvelopePoint? before = null, after = null;
        foreach (var p in points)
        {
            if (Compare(p.At, at) <= 0) before = p;
            else { after = p; break; }
        }
        if (before is null) return after?.Level ?? 1;
        if (after is null || before.Value.At.Block != after.Value.At.Block) return before.Value.Level;
        var span = after.Value.At.Time - before.Value.At.Time;
        if (span <= 0) return after.Value.Level;
        var t = (at.Time - before.Value.At.Time) / span;
        return before.Value.Level + (after.Value.Level - before.Value.Level) * t;
    }

    /// <summary>From <paramref name="from"/> the level goes linearly to <paramref name="level"/>; later points are replaced.</summary>
    private static void Ramp(TimelineItem item, TimelinePoint from, TimelinePoint to, double level)
    {
        var current = LevelAt(item, from);
        item.Envelope.RemoveAll(p => Compare(p.At, from) >= 0);
        item.Envelope.Add(new(from, current));
        item.Envelope.Add(new(to, level));
    }

    private static void EndAt(TimelineItem item, TimelinePoint end)
    {
        if (item.End is TimelinePoint natural && Compare(natural, end) <= 0) return; // already over by then
        item.End = end;
        item.EndedByCue = true;
        item.Envelope.RemoveAll(p => Compare(p.At, end) > 0);
    }
}
