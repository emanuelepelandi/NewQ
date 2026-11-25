namespace NewQ.Core.Model;

/// <summary>Where a dragged cue lands: before row <see cref="Index"/>, inside <see cref="Group"/> (null = not in a group).</summary>
public readonly record struct DropTarget(int Index, GroupCue? Group);

/// <summary>
/// The structure of the cue list with groups: a group's cues are the rows right below it carrying its id (its
/// "block"). Moving, dragging and checking keep that true. Groups don't nest; --SHOW START-- and --SHOW END-- stay
/// first and last.
/// </summary>
public static class CueList
{
    /// <summary>The group a cue belongs to, or null.</summary>
    public static GroupCue? ParentOf(IReadOnlyList<Cue> cues, Cue cue)
    {
        if (cue.GroupId is not Guid id) return null;
        foreach (var c in cues)
            if (c is GroupCue g && g.Id == id) return g;
        return null;
    }

    /// <summary>The cues of a group, in list order.</summary>
    public static IReadOnlyList<Cue> ChildrenOf(IReadOnlyList<Cue> cues, GroupCue group)
    {
        var index = IndexOf(cues, group);
        var children = new List<Cue>();
        for (var i = index + 1; index >= 0 && i < cues.Count && cues[i].GroupId == group.Id; i++) children.Add(cues[i]);
        return children;
    }

    /// <summary>The rows a cue takes: itself, and its cues if it is a group.</summary>
    public static int BlockLength(IReadOnlyList<Cue> cues, Cue cue) => cue is GroupCue g ? 1 + ChildrenOf(cues, g).Count : 1;

    /// <summary>
    /// Fixes the structure after loading or a change: a cue whose group is gone, or that is not inside its group's
    /// block, leaves the group; a group never belongs to another group. Returns true if something changed.
    /// </summary>
    public static bool Normalize(IList<Cue> cues)
    {
        var changed = false;
        Guid? open = null; // the group whose block the scan is in
        foreach (var cue in cues)
        {
            if (cue is GroupCue group)
            {
                if (group.GroupId is not null) { group.GroupId = null; changed = true; }
                open = group.Id;
                continue;
            }
            if (cue.GroupId is null) { open = null; continue; }
            if (cue.IsShowMarker || cue.GroupId != open) { cue.GroupId = null; open = null; changed = true; }
        }
        return changed;
    }

    /// <summary>
    /// One step up (-1) or down (+1), the arrows' move: a cue swaps with its neighbour, enters a group it reaches
    /// (moving down onto a group it becomes its first cue, moving up below a group its last) and leaves it past the
    /// edge; a group moves with its cues and steps over whole groups. False if it can't move.
    /// </summary>
    public static bool MoveStep(IList<Cue> cues, Cue cue, int direction)
    {
        var index = IndexOf(cues, cue);
        if (index < 0 || cue.IsShowMarker) return false;
        var list = (IReadOnlyList<Cue>)AsReadOnly(cues);

        if (cue is GroupCue group)
        {
            var length = BlockLength(list, group);
            if (direction < 0)
            {
                if (index - 1 < 0 || cues[index - 1].IsShowMarker) return false;
                // Over the cue above, or over the whole group above.
                var target = ParentOf(list, cues[index - 1]) is GroupCue g ? IndexOf(cues, g) : index - 1;
                MoveRange(cues, index, length, target);
            }
            else
            {
                var after = index + length;
                if (after >= cues.Count || cues[after].IsShowMarker) return false;
                MoveRange(cues, index, length, after + BlockLength(list, cues[after]));
            }
            return true;
        }

        var parent = ParentOf(list, cue);
        if (direction < 0)
        {
            if (index - 1 < 0) return false;
            var above = cues[index - 1];
            if (parent is not null)
            {
                if (ReferenceEquals(above, parent)) // first cue of its group: it leaves, above the group
                {
                    cue.GroupId = null;
                    MoveRange(cues, index, 1, index - 1);
                }
                else Swap(cues, index, index - 1);
                return true;
            }
            if (above.IsShowMarker) return false;
            if (above is GroupCue emptyGroup) { cue.GroupId = emptyGroup.Id; return true; } // just below an empty group
            if (ParentOf(list, above) is GroupCue g) { cue.GroupId = g.Id; return true; }    // below a group: its last cue
            Swap(cues, index, index - 1);
            return true;
        }
        else
        {
            if (index + 1 >= cues.Count) return false;
            var below = cues[index + 1];
            if (parent is not null)
            {
                if (below.GroupId == parent.Id) Swap(cues, index, index + 1);
                else cue.GroupId = null; // last cue of its group: it leaves, staying where it is
                return true;
            }
            if (below.IsShowMarker) return false;
            if (below is GroupCue g) // onto a group: its first cue
            {
                cue.GroupId = g.Id;
                MoveRange(cues, index, 1, index + 2);
                return true;
            }
            Swap(cues, index, index + 1);
            return true;
        }
    }

    /// <summary>
    /// Where a cue dragged over row <paramref name="row"/> lands: on the upper half before it, on the lower half after
    /// it, in the group of that position (below a group's row = its first cue). A group can't go inside a group:
    /// it lands before or after the whole block. Null where it can't go (before START, after END, inside itself).
    /// </summary>
    public static DropTarget? TargetAt(IReadOnlyList<Cue> cues, Cue dragged, int row, bool upperHalf)
    {
        if (row < 0 || row >= cues.Count) return null;
        var over = cues[row];
        DropTarget target;
        if (over.IsShowMarker)
        {
            if (row == 0) target = new DropTarget(1, null);                 // START: right below it
            else target = new DropTarget(row, null);                          // END: right above it
        }
        else if (upperHalf)
            target = new DropTarget(row, over is GroupCue ? null : ParentOf(cues, over));
        else if (over is GroupCue g)
            target = new DropTarget(row + 1, g);
        else
            target = new DropTarget(row + 1, ParentOf(cues, over));

        if (dragged is GroupCue)
        {
            if (target.Group is GroupCue inside) // a group lands outside: before or after the block it points into
            {
                var start = IndexOf(cues, inside);
                var end = start + BlockLength(cues, inside);
                target = new DropTarget(target.Index - start <= end - target.Index ? start : end, null);
            }
        }
        var from = IndexOf(cues, dragged);
        var length = BlockLength(cues, dragged);
        if (target.Index > from && target.Index < from + length) return null; // inside itself
        if (target.Group is not null && ReferenceEquals(target.Group, dragged)) return null;
        return target;
    }

    /// <summary>Moves a cue (with its cues, if a group) to a drop target. False if nothing changed.</summary>
    public static bool MoveTo(IList<Cue> cues, Cue cue, DropTarget target)
    {
        var from = IndexOf(cues, cue);
        if (from < 0 || cue.IsShowMarker) return false;
        var length = BlockLength(AsReadOnly(cues), cue);
        var groupId = cue is GroupCue ? null : target.Group?.Id;
        var sameSpot = target.Index >= from && target.Index <= from + length;
        if (sameSpot && cue.GroupId == groupId) return false;
        if (cue is not GroupCue) cue.GroupId = groupId;
        if (!sameSpot) MoveRange(cues, from, length, target.Index);
        return true;
    }

    /// <summary>
    /// How long a group plays from its cues (pre-wait + length): all together the longest, in sequence the sum; null
    /// when it never ends by itself or a length is unknown, or for an "enter" group.
    /// </summary>
    public static double? GroupLength(IReadOnlyList<Cue> cues, GroupCue group, Func<Cue, double?> lengthOf)
    {
        if (!group.PlaysAsOne) return null;
        double total = 0;
        foreach (var child in ChildrenOf(cues, group))
        {
            if (!child.Armed) continue;
            if (lengthOf(child) is not double length) return null;
            var span = Math.Max(0, child.PreWait) + Math.Max(0, length);
            total = group.Mode == GroupMode.Simultaneous ? Math.Max(total, span) : total + span;
        }
        return total;
    }

    /// <summary>Where a new cue goes after <paramref name="selected"/>: after it (a group: after its block), in its group.</summary>
    public static (int Index, Guid? GroupId) InsertionAfter(IReadOnlyList<Cue> cues, Cue? selected, bool isGroup)
    {
        if (selected is null) return (cues.Count, null);
        var index = IndexOf(cues, selected);
        if (selected is GroupCue g) return isGroup ? (index + BlockLength(cues, g), null) : (index + 1, g.Id);
        if (isGroup && ParentOf(cues, selected) is GroupCue parent)
            return (IndexOf(cues, parent) + BlockLength(cues, parent), null); // groups don't nest: after the group
        return (index + 1, selected.GroupId);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Moves <paramref name="length"/> rows from <paramref name="from"/> so they start before row <paramref name="before"/> (as numbered before the move).</summary>
    private static void MoveRange(IList<Cue> cues, int from, int length, int before)
    {
        if (before >= from && before <= from + length) return;
        var block = new List<Cue>();
        for (var i = 0; i < length; i++) block.Add(cues[from + i]);
        if (cues is System.Collections.ObjectModel.ObservableCollection<Cue> observable)
        {
            // ObservableCollection.Move keeps the rows (selection, focus) instead of removing and adding them.
            if (before < from)
                for (var i = 0; i < length; i++) observable.Move(from + i, before + i);
            else
                for (var i = 0; i < length; i++) observable.Move(from, before - 1);
            return;
        }
        for (var i = 0; i < length; i++) cues.RemoveAt(from);
        var at = before > from ? before - length : before;
        for (var i = 0; i < length; i++) cues.Insert(at + i, block[i]);
    }

    private static void Swap(IList<Cue> cues, int a, int b)
    {
        if (cues is System.Collections.ObjectModel.ObservableCollection<Cue> observable) observable.Move(a, b);
        else (cues[a], cues[b]) = (cues[b], cues[a]);
    }

    private static IReadOnlyList<Cue> AsReadOnly(IList<Cue> cues) => cues as IReadOnlyList<Cue> ?? cues.ToList();

    private static int IndexOf(IReadOnlyList<Cue> cues, Cue cue)
    {
        for (var i = 0; i < cues.Count; i++)
            if (ReferenceEquals(cues[i], cue)) return i;
        return -1;
    }

    private static int IndexOf(IList<Cue> cues, Cue cue) => IndexOf(AsReadOnly(cues), cue);
}
