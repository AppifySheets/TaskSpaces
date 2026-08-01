using System.Diagnostics;
using System.IO;
using TaskSpaces.Core;
using TaskSpaces.Core.Persistence;

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
                Arguments = StripExecutable(entry.CommandLine, entry.ProcessPath),
                UseShellExecute = true,
            });
            if (process is null) return false;
            manager.RegisterPendingLaunch(process.Id, entry.ProcessPath, workspaceId);
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return false;
        }
    }

    static string StripExecutable(string? commandLine, string processPath)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return "";
        var trimmed = commandLine.TrimStart();
        // Quoted form: "C:\path\app.exe" args   Unquoted form: C:\path\app.exe args
        if (trimmed.StartsWith('"'))
        {
            var close = trimmed.IndexOf('"', 1);
            return close < 0 ? "" : trimmed[(close + 1)..].TrimStart();
        }
        return trimmed.StartsWith(processPath, StringComparison.OrdinalIgnoreCase)
            ? trimmed[processPath.Length..].TrimStart()
            : ""; // command line doesn't start with the known exe — safer to relaunch bare
    }
}
