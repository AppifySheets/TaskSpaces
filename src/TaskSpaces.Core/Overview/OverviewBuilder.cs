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
        Maybe<WindowHandle> activeWindow = default,
        // Which window each desktop will restore focus to when you land on it (see
        // WorkspaceManager.RestoreLastActive). Optional and last, same reason as activeWindow:
        // null means "mark nothing", and every pre-existing caller and test keeps compiling.
        IReadOnlyDictionary<Guid, WindowHandle>? lastActiveByDesktop = null,
        // Monitor, minimised state and z-order, gathered in one pass by IScreenLayout. Null
        // means "no screen facts available" -- compatibility mode, and every test written
        // before this existed -- and leaves ordering and rendering exactly as they were.
        ScreenFacts? screen = null)
    {
        var facts = screen ?? ScreenFacts.Empty;

        // `desktopId` is null for rows that belong to no single desktop -- pinned windows, and
        // the "Unplaced" catch-all below -- which is exactly the set that can never carry a
        // landing marker.
        WindowRow Row(WindowInfo w, Guid? desktopId = null, Maybe<bool> frontmost = default) =>
            new(w,
                originalTitleOf(w.Handle),
                activeWindow.Map(active => active == w.Handle).GetValueOrDefault(false),
                WillActivate(w, desktopId),
                MonitorOf(w),
                facts.Minimized.Contains(w.Handle),
                frontmost);

        Maybe<int> MonitorOf(WindowInfo w) =>
            facts.MonitorOf.TryGetValue(w.Handle, out var number) ? number : Maybe<int>.None;

        // Suppressed on the CURRENT desktop, and not as a detail: the map is stamped on the way
        // OUT, so while you are standing on a desktop its entry still names whatever you were
        // looking at when you last LEFT. Rendering that would put a "you will land here" marker
        // on one icon while a different icon on the same row wears the live IsActive highlight
        // -- two contradictory claims about one row. There is also nothing to predict about the
        // desktop you are already on.
        bool WillActivate(WindowInfo w, Guid? desktopId) =>
            desktopId is { } d
            && d != currentDesktopId
            && lastActiveByDesktop is not null
            && lastActiveByDesktop.TryGetValue(d, out var last)
            && last == w.Handle;

        var pinnedRows = windows.Where(w => pinned.Contains(w.Handle)).Select(w => Row(w)).ToList();

        // Petre: "sort icons in workspaces by monitors, first icons from monitor1, then
        // monitor2, etc."
        //
        // OrderBy is a STABLE sort, and that is doing real work here rather than being an
        // incidental property: windows on the same monitor keep the relative order they already
        // had, so the sort regroups the row without reshuffling within a group. Icons stay where
        // Petre's hand expects them.
        //
        // A window whose monitor could not be resolved sorts last rather than being dropped --
        // same principle as the "Unplaced" group below. int.MaxValue because there is no monitor
        // number that could collide with it.
        //
        // Deliberately NOT sorted by z-order, which was the other way to answer "which one is on
        // top": z-order changes on every click, so the icons would re-shuffle under the cursor
        // and a position would stop being a stable click target. The badge carries that instead.
        List<WindowRow> OnDesktop(Guid desktopId)
        {
            var here = windows
                .Where(w => !pinned.Contains(w.Handle) && desktopOf.TryGetValue(w.Handle, out var d) && d == desktopId)
                .OrderBy(w => MonitorOf(w).GetValueOrDefault(int.MaxValue))
                .ToList();

            // "On top" is per MONITOR, not per row: with two monitors there are two front-most
            // windows on screen at once, and marking only one of them would be a lie about the
            // other.
            var frontmost = here
                .Where(w => facts.ZOrder.ContainsKey(w.Handle) && MonitorOf(w).HasValue)
                .GroupBy(w => MonitorOf(w).Value)
                .Select(monitor => monitor.MinBy(w => facts.ZOrder[w.Handle])!.Handle)
                .ToHashSet();

            // Whether this desktop can answer the question AT ALL, asked once for the group
            // rather than per window. Every desktop but the current one is made of cloaked
            // windows, which EnumWindows does not return, so none of them has any z-order --
            // and "no window here is known to be in front" must not be reported as "every
            // window here is behind". Petre dims the ones that are behind; that distinction is
            // the difference between one dimmed icon per monitor and an entire workspace
            // rendering greyed out.
            var known = here.Any(w => facts.ZOrder.ContainsKey(w.Handle));

            return here
                .Select(w => Row(w, desktopId, known ? frontmost.Contains(w.Handle) : Maybe<bool>.None))
                .ToList();
        }

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
            .Select(w => Row(w))
            .ToList();
        if (unplacedRows.Count > 0)
            otherDesktops = [.. otherDesktops, new DesktopGroup(Guid.Empty, "Unplaced", false, unplacedRows)];

        return new(pinnedRows, workspaceGroups, otherDesktops);
    }
}
