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

    sealed record Pending(int ProcessId, string ProcessPath, Guid WorkspaceId, DateTimeOffset LaunchedAt, string? CommandLine);

    readonly ImmutableList<Pending> entries;

    PendingPlacements(ImmutableList<Pending> entries) => this.entries = entries;

    public static PendingPlacements Empty { get; } = new([]);

    public PendingPlacements Add(int processId, string processPath, Guid workspaceId, DateTimeOffset now, string? commandLine = null) =>
        new(entries.Add(new Pending(processId, processPath, workspaceId, now, commandLine)));

    // Match priority: exact pid -> content identity (path+args — separates two launches
    // of the same exe with different solutions) -> bare path (browsers hand the window
    // to an existing process AND rewrite their args, so identity may not survive).
    public (PendingPlacements Remaining, Maybe<Guid> WorkspaceId) Match(WindowInfo window, DateTimeOffset now)
    {
        var alive = entries.RemoveAll(p => now - p.LaunchedAt > Ttl);
        var hit = alive.FirstOrDefault(p => p.ProcessId == window.ProcessId)
                  ?? alive.FirstOrDefault(p => window.ProcessPath is not null
                        && RosterIdentity.Of(p.ProcessPath, p.CommandLine) == RosterIdentity.Of(window.ProcessPath, window.CommandLine))
                  ?? alive.FirstOrDefault(p => p.ProcessPath.Equals(window.ProcessPath, StringComparison.OrdinalIgnoreCase));
        return hit is null
            ? (new PendingPlacements(alive), Maybe<Guid>.None)
            : (new PendingPlacements(alive.Remove(hit)), hit.WorkspaceId);
    }
}
