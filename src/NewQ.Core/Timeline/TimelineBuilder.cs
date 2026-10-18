using NewQ.Core.Engine;
using NewQ.Core.Model;

namespace NewQ.Core.Timeline;

/// <summary>
/// Predicts the course of the show from the cue list, with the engine's rules (<see cref="ShowSequence"/>): a GO
/// fires the playhead cue; pre-wait delays its start; a follow fires the next cue when the cue ends (wait follow:
/// post-wait seconds later); a Start link fires its target when the cue starts, Stop and Fade out links end it.
/// Everything a GO fires forms a block; the next GO starts a new one. Disarmed cues take no time and do no link but
/// keep their follow. Fade and Stop cues act on the running cues with the target number.
/// The wait between GOs is up to the operator: each block is placed after the previous one has ended. Cues that never
/// end by themselves (loops, live sources, held images) go on through the following blocks until something stops them.
/// </summary>
public static class TimelineBuilder
{
    /// <param name="lengthOf">
    /// Seconds a cue plays (null = until stopped). The default uses <see cref="Cue.Duration"/>; the app adds the
    /// lengths of video files it has probed.
    /// </param>
    /// <param name="linkFadeSeconds">Duration of a Fade out link (the workspace's "Fade cue" time).</param>
    public static ShowTimeline Build(IReadOnlyList<Cue> cues, Func<Cue, double?>? lengthOf = null, double linkFadeSeconds = 3)
    {
        lengthOf ??= c => c.Duration;
        var startHasGo = cues.Count > 0 && cues[0] is ShowMarkerCue { IsStart: true, HasOwnGo: true };
        cues = cues.Where(c => !c.IsShowMarker).ToList(); // --SHOW START-- / --SHOW END-- take no time and fire nothing
        var items = new List<TimelineItem>();
        var blocks = new List<TimelineBlock>();
        var carried = new List<TimelineItem>(); // open-ended cues still running from earlier blocks

        var i = 0;
        while (i < cues.Count)
        {
            var block = blocks.Count;
            var blockItems = new List<TimelineItem>();
            // Cues fired by this GO, waiting to be placed: (moment fired, order, cue). The earliest start goes first,
            // so a Stop or Fade sees what is running at its moment.
            var pending = new List<(double FireAt, int Order, Cue Cue)> { (0, 0, cues[i]) };
            var placed = new HashSet<Cue>();
            var order = 1;
            while (pending.Count > 0)
            {
                var k = 0;
                for (var n = 1; n < pending.Count; n++)
                {
                    var a = pending[n].FireAt + Math.Max(0, pending[n].Cue.PreWait);
                    var b = pending[k].FireAt + Math.Max(0, pending[k].Cue.PreWait);
                    if (a < b || a == b && pending[n].Order < pending[k].Order) k = n;
                }
                var (fireAt, _, cue) = pending[k];
                pending.RemoveAt(k);
                if (!placed.Add(cue)) continue; // reached twice (links): the engine doesn't restart it either

                var start = new TimelinePoint(block, fireAt + Math.Max(0, cue.PreWait));
                var item = CreateItem(cue, start, ReferenceEquals(cue, cues[i]), lengthOf);
                ApplyEffects(item, carried.Concat(blockItems));
                blockItems.Add(item);

                if (cue.Armed && ShowSequence.LinkTargetOf(cues, cue) is Cue target)
                {
                    if (cue.LinkAction == LinkAction.Start)
                        pending.Add((start.Time, order++, target));
                    else
                        StopRunning(carried.Concat(blockItems), target.Number, start,
                            cue.LinkAction == LinkAction.FadeOut ? linkFadeSeconds : 0);
                }
                // A follow on a cue that never ends never fires (Show Check warns about it).
                if (ShowSequence.Follows(cue) && ShowSequence.NextInList(cues, cue) is Cue next
                    && item.ActionEnd is TimelinePoint end && end.Block == block)
                    pending.Add((end.Time + ShowSequence.FollowDelay(cue), order++, next));
            }

            var length = blockItems.Select(it => it.Start.Time)
                .Concat(blockItems.Concat(carried).Where(it => it.End?.Block == block).Select(it => it.End!.Value.Time))
                .Concat(blockItems.Where(it => it.HoldFrom?.Block == block).Select(it => it.HoldFrom!.Value.Time))
                .Concat(blockItems.Concat(carried).SelectMany(it => it.Envelope).Where(p => p.At.Block == block).Select(p => p.At.Time))
                .DefaultIfEmpty(0).Max();
            blocks.Add(new TimelineBlock(block, cues[i], length));
            items.AddRange(blockItems);

            carried.RemoveAll(it => !it.IsOpenEnded);
            carried.AddRange(blockItems.Where(it => it.IsOpenEnded));
            // The next GO: the first cue below that this block didn't place (like the engine's playhead, except after
            // a follow that never fires: that cue is shown as its own GO rather than lost).
            i++;
            while (i < cues.Count && placed.Contains(cues[i])) i++;
        }

        return new ShowTimeline(blocks, items) { StartHasGo = startHasGo };
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
        // Hold on finish: the last frame stays until something stops it; the follow still fires at the end of the file.
        if (cue is VideoCue { HoldAtEnd: true, Loop: false } && cue.Armed && item.End is TimelinePoint fileEnd)
        {
            item.HoldFrom = fileEnd;
            item.End = null;
        }

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

    /// <summary>Stop / Fade out link: the running cues with that number fade to nothing and end.</summary>
    private static void StopRunning(IEnumerable<TimelineItem> others, string number, TimelinePoint at, double fadeSeconds)
    {
        foreach (var target in others.Where(o => o.Kind == TimelineItemKind.Media && !o.Disarmed && IsRunningAt(o, at) && SameNumber(o.Cue, number)).ToList())
        {
            var endAt = at with { Time = at.Time + fadeSeconds };
            Ramp(target, at, endAt, 0);
            EndAt(target, endAt);
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
