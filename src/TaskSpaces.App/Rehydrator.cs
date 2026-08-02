using System.Diagnostics;
using System.IO;
using TaskSpaces.Core;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rehydration;

namespace TaskSpaces.App;

// Relaunches a workspace's remembered apps and tells the manager to expect their
// windows. Failures are per-entry and non-fatal: a moved/uninstalled exe just doesn't
// come back (matching the browser-session-restore mental model from the spec).
public static class Rehydrator
{
    public static int Launch(WorkspaceManager manager, Guid workspaceId, IReadOnlyList<InventoryEntry> entries) =>
        entries.Count(entry => TryLaunch(manager, workspaceId, entry));

    static bool TryLaunch(WorkspaceManager manager, Guid workspaceId, InventoryEntry entry)
    {
        try
        {
            // CommandLine is the ORIGINAL full command line ("exe" args...) — strip the
            // exe part; what remains are the arguments to relaunch with.
            var process = Process.Start(new ProcessStartInfo(entry.ProcessPath)
            {
                Arguments = CommandLines.ArgumentsOf(entry.CommandLine, entry.ProcessPath),
                UseShellExecute = true,
            });
            if (process is null) return false;
            manager.RegisterPendingLaunch(process.Id, entry.ProcessPath, workspaceId, entry.CommandLine);
            return true;
        }
        // Fix round 1 (reviewer, Important): Process.Start can throw more than the three
        // exception types originally listed here (e.g. UnauthorizedAccessException for a
        // permissions-denied exe, ArgumentException for a malformed path) — those were
        // propagating out of the entries.Count(...) LINQ in Launch(), aborting the rest of
        // that workspace's batch and surfacing unhandled on the UI thread from
        // RehydratePrompt.OnRestore. Relaunching remembered apps is best-effort: one bad
        // entry (moved/uninstalled/permission-denied exe) must never abort the batch or
        // crash the app, so catch broadly here — this is the one place in the app where a
        // blanket catch is correct, because the failure mode really is "this one relaunch
        // didn't happen," never "something is structurally wrong."
        catch (Exception)
        {
            return false;
        }
    }
}
