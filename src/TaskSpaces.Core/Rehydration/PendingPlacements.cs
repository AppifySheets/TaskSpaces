using System.Collections.Immutable;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Rehydration;

// "We just launched pid X (path Y) for workspace Z — when its window appears, place it
// there without consulting rules." Entries expire: a browser may reuse an existing
// process, so a pending entry that never matches must not linger forever.
public sealed class PendingPlacements
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    sealed record Pending(int ProcessId, string ProcessPath, Guid WorkspaceId, DateTimeOffset LaunchedAt);

    readonly ImmutableList<Pending> entries;

    PendingPlacements(ImmutableList<Pending> entries) => this.entries = entries;

    public static PendingPlacements Empty { get; } = new([]);

    public PendingPlacements Add(int processId, string processPath, Guid workspaceId, DateTimeOffset now) =>
        new(entries.Add(new Pending(processId, processPath, workspaceId, now)));

    // Match by pid first (exact), then by process path (browsers hand off to an existing
    // process, so the window's pid won't be the launched pid). Matched entry is consumed.
    public (PendingPlacements Remaining, Maybe<Guid> WorkspaceId) Match(WindowInfo window, DateTimeOffset now)
    {
        var alive = entries.RemoveAll(p => now - p.LaunchedAt > Ttl);
        var hit = alive.FirstOrDefault(p => p.ProcessId == window.ProcessId)
                  ?? alive.FirstOrDefault(p => p.ProcessPath.Equals(window.ProcessPath, StringComparison.OrdinalIgnoreCase));
        return hit is null
            ? (new PendingPlacements(alive), Maybe<Guid>.None)
            : (new PendingPlacements(alive.Remove(hit)), hit.WorkspaceId);
    }
}
