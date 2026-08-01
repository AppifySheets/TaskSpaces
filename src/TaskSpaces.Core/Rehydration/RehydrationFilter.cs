using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Rehydration;

// Finding 4 (reviewer, Important): a plain app restart — apps still running, nothing
// actually crashed or rebooted — must not re-offer to relaunch apps that never left.
// Pure, pinned-by-test filter: given a workspace's remembered inventory and the windows
// the manager currently knows about, keep only the entries whose app ISN'T already live.
public static class RehydrationFilter
{
    // Matched by ProcessPath (case-insensitive: Windows paths are not case-sensitive).
    // Entries with no live match "survive" and are worth offering to relaunch; live ones
    // are dropped silently — the whole point is the user never sees them as an offer.
    public static IReadOnlyList<InventoryEntry> Surviving(IReadOnlyList<InventoryEntry> inventory, IReadOnlyList<WindowInfo> knownWindows)
    {
        var livePaths = knownWindows
            .Where(w => w.ProcessPath is not null)
            .Select(w => w.ProcessPath!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return inventory.Where(e => !livePaths.Contains(e.ProcessPath)).ToList();
    }
}
