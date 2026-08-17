using CSharpFunctionalExtensions;

namespace TaskSpaces.Core.Domain;

// Petre: "maybe an alt-tab like shortcut for me to switch through workspaces".
//
// What makes Alt+Tab worth using is not that it cycles -- Ctrl+Alt+arrows used to cycle, in
// list order, and was removed as redundant -- it is that it cycles in MOST-RECENTLY-USED order,
// so the single most common switch (back to where you just were) is one tap regardless of how
// the list is arranged. This is that order, and nothing else: no UI, no Win32, no timing. Pure
// and in Core so the ordering rules are unit-testable on their own.
//
// Live-only, deliberately not persisted: "where I was a moment ago" is a fact about this
// session. A restored MRU from last Tuesday would send the first tap somewhere arbitrary.
public sealed record WorkspaceMru(IReadOnlyList<Guid> Recent)
{
    public static readonly WorkspaceMru Empty = new([]);

    // Most recent first. Re-visiting a workspace MOVES it to the front rather than adding a
    // duplicate, which is what keeps the list bounded by the number of workspaces.
    public WorkspaceMru Touch(Guid workspaceId) =>
        new([workspaceId, .. Recent.Where(other => other != workspaceId)]);

    // Workspaces in switching order: visited ones by recency, then everything never visited
    // this session in the user's own defined order.
    //
    // Ids in Recent that no longer name a workspace (deleted since) simply fall out -- there
    // is no separate cleanup step, because filtering against the live list here IS the
    // cleanup, and it cannot go stale.
    public IReadOnlyList<Workspace> Order(IReadOnlyList<Workspace> workspaces)
    {
        var visited = Recent
            .Select(id => workspaces.TryFirst(w => w.Id == id))
            .Where(found => found.HasValue)
            .Select(found => found.Value)
            .ToList();
        var seen = visited.Select(w => w.Id).ToHashSet();
        return [.. visited, .. workspaces.Where(w => !seen.Contains(w.Id))];
    }
}

// What an Alt+Tab-style picker needs in one call: the list to walk, and where the highlight
// starts. CurrentIndex is -1 when the current desktop is not a workspace at all (one of
// Petre's unbound desktops, e.g. "Main") -- the picker reads that as "start before the
// beginning", so the first forward tap lands on the most recent workspace rather than
// skipping past it.
public sealed record RecentWorkspaces(IReadOnlyList<Workspace> Ordered, int CurrentIndex)
{
    // The index one tap in `direction` lands on, wrapping in both directions.
    //
    // Written the long way because C#'s % keeps the sign of the LEFT operand: -1 % 3 is -1,
    // not 2. Lifted out of WorkspaceSwitchGesture so the chord and the bar's back button
    // resolve "one tap away" through the same arithmetic -- two copies of this would drift,
    // and the drift would show up as a button that disagrees with the keyboard.
    public int IndexAfter(int from, int direction) =>
        ((from + direction) % Ordered.Count + Ordered.Count) % Ordered.Count;

    // Petre: "on the floating window i want a go back to previous button... basically the same
    // as ctrl+win+tab tap once, without the kb."
    //
    // Where a single forward tap from the current workspace lands, which is the whole
    // definition of the back button. No separate history is kept: the MRU is already updated
    // by the time you have arrived somewhere (Switch touches it directly, and RememberVisit
    // catches the switches JumpTo makes through the desktop service), so "where I came from"
    // is simply the next entry.
    //
    // None in exactly the cases where there is nowhere to go -- no workspaces at all, or the
    // step lands on where we already are (one workspace, and we are on it). Note that being on
    // an UNBOUND desktop is not one of those cases: CurrentIndex is -1 there, so one step
    // forward lands on the most recent workspace, which is a real move.
    public Maybe<Workspace> Back =>
        Ordered.Count == 0 || IndexAfter(CurrentIndex, 1) == CurrentIndex
            ? Maybe<Workspace>.None
            : Ordered[IndexAfter(CurrentIndex, 1)];

    // Where you have just been, nearest first (#155). Petre: "highlight the PREVIOUS workspace I was
    // in, so i know what i'm coming from and where i'd go with the shortcut", then "maybe also ring
    // the one before that, and the one before that, so i know what is the history of workspaces".
    //
    // Walked with IndexAfter rather than by slicing this list, and that is the whole reason it lives
    // here rather than in the bar: the FIRST entry is what Back returns, by construction, so the trail
    // the bar draws and the destination the back button names cannot drift apart. Two pieces of code
    // that agree today are not the same thing as one that cannot disagree.
    //
    // It stops when the walk returns to where it started, which keeps the current workspace out of its
    // own history. That is an ordinary case rather than a corner: with two workspaces, two steps
    // forward lands back on the one you are standing on.
    //
    // Also empty when there is nowhere to have come from, matching Back's None exactly -- one
    // workspace, or none -- so the bar and the back button agree about that too.
    public IReadOnlyList<Workspace> Trail(int depth)
    {
        var trail = new List<Workspace>();
        if (Ordered.Count == 0) return trail;

        var at = CurrentIndex;
        while (trail.Count < depth)
        {
            var next = IndexAfter(at, 1);
            if (next == CurrentIndex) break;
            trail.Add(Ordered[next]);
            at = next;
        }

        return trail;
    }
}
