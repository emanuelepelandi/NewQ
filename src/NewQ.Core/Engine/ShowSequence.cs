using NewQ.Core.Model;

namespace NewQ.Core.Engine;

/// <summary>
/// The rules of the sequence, shared by the engine, the preloads and the timeline: a GO fires the playhead cue;
/// a follow (wait or auto) fires the next cue in the list when the cue ends; a Start link fires its target when the
/// cue starts. After a GO the playhead goes past every cue right below that the GO already covers.
/// </summary>
public static class ShowSequence
{
    /// <summary>A cue with a Follow other than none starts the next one when it ends.</summary>
    public static bool Follows(Cue cue) => cue.ContinueMode is ContinueMode.AutoFollow or ContinueMode.WaitFollow;

    /// <summary>Seconds between the end of a cue and the start of the next one it follows.</summary>
    public static double FollowDelay(Cue cue) => cue.ContinueMode == ContinueMode.WaitFollow ? cue.PostWait : 0;

    /// <summary>The cue a link acts on, or null (no link, unknown number, or the cue itself).</summary>
    public static Cue? LinkTargetOf(IReadOnlyList<Cue> cues, Cue cue)
    {
        if (!cue.HasLink) return null;
        var target = cues.FirstOrDefault(c => string.Equals(c.Number.Trim(), cue.LinkTarget, StringComparison.OrdinalIgnoreCase));
        return ReferenceEquals(target, cue) ? null : target;
    }

    /// <summary>
    /// Every cue a GO on <paramref name="start"/> fires, in the order they are reached: its follow chain and the
    /// targets of Start links (with their own chains and links). Disarmed cues keep their follow but not their link.
    /// </summary>
    public static IReadOnlyList<Cue> FiredBy(IReadOnlyList<Cue> cues, Cue start)
    {
        var result = new List<Cue>();
        var seen = new HashSet<Cue>();
        var queue = new Queue<Cue>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var cue = queue.Dequeue();
            if (!seen.Add(cue)) continue;
            result.Add(cue);
            if (cue.Armed && cue.LinkAction == LinkAction.Start && LinkTargetOf(cues, cue) is Cue target) queue.Enqueue(target);
            if (Follows(cue) && NextInList(cues, cue) is Cue next) queue.Enqueue(next);
        }
        return result;
    }

    /// <summary>Where the playhead goes after a GO on <paramref name="start"/>: the first cue below it the GO doesn't fire.</summary>
    public static Cue? NextAfter(IReadOnlyList<Cue> cues, Cue start)
    {
        var index = IndexOf(cues, start);
        if (index < 0) return null;
        var fired = new HashSet<Cue>(FiredBy(cues, start));
        var j = index + 1;
        while (j < cues.Count && fired.Contains(cues[j])) j++;
        return j < cues.Count ? cues[j] : null;
    }

    /// <summary>
    /// A loop of Start links through <paramref name="from"/> (from → … → from), or null. Such a loop would start the
    /// same cues again in the same instant forever, so it is never allowed. Follows go down the list and wait for
    /// the end of the cue: a loop through them is just a show that repeats, and is fine.
    /// </summary>
    public static IReadOnlyList<Cue>? StartLinkLoop(IReadOnlyList<Cue> cues, Cue from)
    {
        var path = new List<Cue> { from };
        var cue = from;
        while (cue.LinkAction == LinkAction.Start && LinkTargetOf(cues, cue) is Cue target)
        {
            if (ReferenceEquals(target, from)) { path.Add(from); return path; }
            if (path.Contains(target)) return null; // a loop further on, not through this cue
            path.Add(target);
            cue = target;
        }
        return null;
    }

    /// <summary>The first loop of Start links in the list, or null.</summary>
    public static IReadOnlyList<Cue>? FindStartLinkLoop(IReadOnlyList<Cue> cues)
    {
        foreach (var cue in cues)
            if (StartLinkLoop(cues, cue) is { } loop) return loop;
        return null;
    }

    /// <summary>"1 → 2 → 1", for messages.</summary>
    public static string Describe(IReadOnlyList<Cue> loop) => string.Join(" → ", loop.Select(c => c.Number));

    public static Cue? NextInList(IReadOnlyList<Cue> cues, Cue cue)
    {
        var index = IndexOf(cues, cue);
        return index >= 0 && index + 1 < cues.Count ? cues[index + 1] : null;
    }

    private static int IndexOf(IReadOnlyList<Cue> cues, Cue cue)
    {
        for (var i = 0; i < cues.Count; i++)
            if (ReferenceEquals(cues[i], cue)) return i;
        return -1;
    }
}
