using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Rehydration;

namespace TaskSpaces.Core.Overview;

// Pure: all OS facts (pin states, desktop-of, desktop list, current) arrive as data,
// so every grouping rule is unit-testable without a single COM call.
public static class OverviewBuilder
{
    public static Core.Overview.Overview Build(
        AppState state,
        IReadOnlyList<WindowInfo> windows,
        Func<WindowHandle, Maybe<string>> originalTitleOf,
        ISet<WindowHandle> pinned,
        IReadOnlyDictionary<WindowHandle, Guid> desktopOf,
        IReadOnlyList<DesktopInfo> desktops,
        Guid currentDesktopId,
        // Optional and last so every pre-existing caller and test compiles unchanged; None
        // simply means "nothing is highlighted".
        Maybe<WindowHandle> activeWindow = default)
    {
        WindowRow Row(WindowInfo w) =>
            new(w, originalTitleOf(w.Handle), activeWindow.Map(active => active == w.Handle).GetValueOrDefault(false));

        var pinnedRows = windows.Where(w => pinned.Contains(w.Handle)).Select(Row).ToList();

        List<WindowRow> OnDesktop(Guid desktopId) => windows
            .Where(w => !pinned.Contains(w.Handle) && desktopOf.TryGetValue(w.Handle, out var d) && d == desktopId)
            .Select(Row).ToList();

        var workspaceGroups = state.Workspaces
            .Select(ws => new WorkspaceGroup(
                ws,
                ws.DesktopId == currentDesktopId,
                ws.DesktopId is { } id ? OnDesktop(id) : [],
                state.Inventory.GetValueOrDefault(ws.Id, []).Where(e => !RosterIdentity.IsRunning(e, windows)).ToList()))
            .ToList();

        // BUG FIX (Task 10, Petre: "i don't think i see windows in the non-workspace
        // section, default workspace"): two real gaps found under investigation, evidence
        // in task-10-report.md —
        //
        // (a) A desktop the user never manually renamed (Windows' Task View labels it
        //     "Desktop 2" etc. in the shell UI, but that label is NOT the COM object's
        //     Name property, which stays "") rendered as a blank/unrecognizable group
        //     header. Not reproduced on Petre's own machine today (all 3 of his desktops
        //     happen to be named), but the gap is real and matches the spec's own
        //     component description ("labeled with that desktop's actual name... 'Desktop
        //     1', etc.") — the ORIGINAL implementation never actually applied this
        //     fallback. `index` = position in `desktops` (GetDesktops order), computed
        //     over the FULL list before filtering to unclaimed desktops, so numbering
        //     stays stable regardless of which desktops are workspaces.
        var desktopNames = desktops
            .Select((d, index) => (d.Id, Name: string.IsNullOrEmpty(d.Name) ? $"Desktop {index + 1}" : d.Name))
            .ToDictionary(x => x.Id, x => x.Name);

        var claimed = state.Workspaces.Where(w => w.DesktopId is not null).Select(w => w.DesktopId!.Value).ToHashSet();
        var otherDesktops = desktops
            .Where(d => !claimed.Contains(d.Id))
            .Select(d => new DesktopGroup(d.Id, desktopNames[d.Id], d.Id == currentDesktopId, OnDesktop(d.Id)))
            .Where(g => g.Windows.Count > 0) // an empty unbound desktop is noise, not information
            .ToList();

        // (b) CONFIRMED on Petre's machine (real window "Windows Input Experience",
        //     hwnd present in TopLevelWindows.Enumerate, not pinned): DesktopOf can fail
        //     for a real, visible, taskbar-candidate window (VirtualDesktop.FromHwnd
        //     returned null for it — a shell-owned window the virtual-desktop API simply
        //     doesn't track). WorkspaceManager.WindowsByWorkspace() silently drops these
        //     from `desktopOf` (`.Where(x => x.Desktop.IsSuccess)`), so such a window
        //     never appeared ANYWHERE in the panel — not Pinned, not a workspace, not
        //     OtherDesktops. A window absent from both `pinned` and `desktopOf` here IS
        //     exactly that case (every non-pinned known window gets queried; only
        //     failures are missing from the dictionary) — group it under a catch-all
        //     instead of disappearing.
        var unplacedRows = windows
            .Where(w => !pinned.Contains(w.Handle) && !desktopOf.ContainsKey(w.Handle))
            .Select(Row)
            .ToList();
        if (unplacedRows.Count > 0)
            otherDesktops = [.. otherDesktops, new DesktopGroup(Guid.Empty, "Unplaced", false, unplacedRows)];

        return new(pinnedRows, workspaceGroups, otherDesktops);
    }
}
