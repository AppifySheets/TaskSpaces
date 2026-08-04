using CSharpFunctionalExtensions;

namespace TaskSpaces.Core.Domain;

// Petre: "maybe an alt-tab like shortcut for me to switch through workspaces".
//
// What makes Alt+Tab worth using is not that it cycles -- Ctrl+Alt+arrows already cycles --
// it is that it cycles in MOST-RECENTLY-USED order, so the single most common switch (back
// to where you just were) is one tap regardless of how the list is arranged. This is that
// order, and nothing else: no UI, no Win32, no timing. Pure and in Core so the ordering
// rules are unit-testable on their own.
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
public sealed record RecentWorkspaces(IReadOnlyList<Workspace> Ordered, int CurrentIndex);
