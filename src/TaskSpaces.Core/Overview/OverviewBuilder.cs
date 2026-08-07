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
        ScreenFacts? screen = null,
        // Windows whose taskbar button has flashed and that the user has not looked at since.
        IReadOnlySet<WindowHandle>? wantsAttention = null)
    {
        var facts = screen ?? ScreenFacts.Empty;
        var attention = wantsAttention ?? new HashSet<WindowHandle>();

        // `desktopId` is null for rows that belong to no single desktop -- pinned windows, and
        // the "Unplaced" catch-all below -- which is exactly the set that can never carry a
        // landing marker.
        WindowRow Row(WindowInfo w, Guid? desktopId = null, Maybe<bool> frontmost = default, Maybe<int> ordinal = default) =>
            new(w,
                originalTitleOf(w.Handle),
                activeWindow.Map(active => active == w.Handle).GetValueOrDefault(false),
                WillActivate(w, desktopId),
                MonitorOf(w),
                facts.Minimized.Contains(w.Handle),
                frontmost,
                ordinal,
                attention.Contains(w.Handle),
                RankOf(w));

        // Petre: "when there are multiple similar icons, multiple edges, i want them numbered,
        // arbitrarily, if i'm selecting the second browser, i can see that the other, first got
        // demoted in the bar" -- and "no numbers for one-instance apps".
        //
        // So: within one group, number the windows of any process that has MORE THAN ONE of
        // them, and leave everything else unnumbered. Three Edge windows become 1, 2, 3; the
        // lone editor beside them stays bare.
        //
        // Ordered by HANDLE, which is the load-bearing choice. The number exists to survive
        // exactly the thing that prompted it: icons re-sort by z-order as Petre works, and a
        // number that re-sorted with them could not show him that the window he left has been
        // demoted -- it would just relabel whatever is now in front. A handle is fixed for a
        // window's lifetime, so the number is too. Which window gets 1 is arbitrary, as asked.
        //
        // Numbers are per GROUP rather than global, so they stay small and start at 1 in every
        // row. The cost is that closing a window renumbers the ones after it -- acceptable
        // because it happens on close, never on focus, which is the moment that had to stay
        // stable.
        static Dictionary<WindowHandle, int> Ordinals(IReadOnlyList<WindowInfo> group) =>
            group
                .GroupBy(w => w.ProcessName)
                .Where(sameApp => sameApp.Count() > 1)
                .SelectMany(sameApp => sameApp
                    .OrderBy(w => w.Handle.Value)
                    .Select((w, index) => (w.Handle, Ordinal: index + 1)))
                .ToDictionary(x => x.Handle, x => x.Ordinal);

        static Maybe<int> OrdinalOf(IReadOnlyDictionary<WindowHandle, int> ordinals, WindowInfo w) =>
            ordinals.TryGetValue(w.Handle, out var ordinal) ? ordinal : Maybe<int>.None;

        Maybe<int> MonitorOf(WindowInfo w) =>
            facts.MonitorOf.TryGetValue(w.Handle, out var number) ? number : Maybe<int>.None;

        // Display numbers ordered so the PRIMARY comes first, then the rest ascending. A
        // monitor's position in this list is how many strokes its marker draws, so the primary
        // draws none.
        //
        // Which display is silent used to be display 1, and that was arbitrary rather than
        // considered. Windows numbers displays by how they were enumerated, not by how much you
        // use them, so on any machine whose primary is not DISPLAY1 the marks landed on the main
        // screen and the silence on the side one. The primary is the closest thing the OS offers
        // to "the screen you mostly work on", and it is what the fallback restore already uses.
        //
        // With no primary reported -- which only happens in tests -- this degrades to plain
        // ascending order, so display 1 goes silent exactly as before.
        var ranked = facts.MonitorOf.Values
            .Distinct()
            .OrderBy(m => facts.PrimaryMonitor.Map(p => p == m ? 0 : 1).GetValueOrDefault(1))
            .ThenBy(m => m)
            .Select((m, rank) => (Monitor: m, Rank: rank))
            .ToDictionary(x => x.Monitor, x => x.Rank);

        Maybe<int> RankOf(WindowInfo w) =>
            MonitorOf(w).Bind(m => ranked.TryGetValue(m, out var rank) ? rank : Maybe<int>.None);

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

        var pinnedWindows = windows.Where(w => pinned.Contains(w.Handle)).ToList();
        var pinnedOrdinals = Ordinals(pinnedWindows);
        var pinnedRows = pinnedWindows.Select(w => Row(w, ordinal: OrdinalOf(pinnedOrdinals, w))).ToList();

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
        // ...then by z-order within each monitor, front-most first. Petre: "let's also sort those
        // icons by z-index."
        //
        // Argued against first and then asked for anyway, so the objection is recorded rather
        // than lost: z-order changes on every click, so the icons on the row you are looking at
        // reshuffle as you work and a POSITION stops being a stable click target. What makes
        // that survivable is the same limitation that shaped everything else here -- only the
        // CURRENT desktop has z-order at all, so every other row keeps the order it had. The
        // rows you navigate from memory are exactly the ones that hold still.
        //
        // Windows with no z-order sort last within their monitor and keep their relative order,
        // so a desktop with no z-order at all comes out exactly as it did before.
        List<WindowRow> OnDesktop(Guid desktopId)
        {
            var here = windows
                .Where(w => !pinned.Contains(w.Handle) && desktopOf.TryGetValue(w.Handle, out var d) && d == desktopId)
                .OrderBy(w => MonitorOf(w).GetValueOrDefault(int.MaxValue))
                .ThenBy(w => facts.ZOrder.TryGetValue(w.Handle, out var z) ? z : int.MaxValue)
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
            var ordinals = Ordinals(here);

            return here
                .Select(w => Row(
                    w,
                    desktopId,
                    known ? frontmost.Contains(w.Handle) : Maybe<bool>.None,
                    OrdinalOf(ordinals, w)))
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
        // in task-10-report.md --
        //
        // (a) A desktop the user never manually renamed (Windows' Task View labels it
        //     "Desktop 2" etc. in the shell UI, but that label is NOT the COM object's
        //     Name property, which stays "") rendered as a blank/unrecognizable group
        //     header. Not reproduced on Petre's own machine today (all 3 of his desktops
        //     happen to be named), but the gap is real and matches the spec's own
        //     component description ("labeled with that desktop's actual name... 'Desktop
        //     1', etc.") -- the ORIGINAL implementation never actually applied this
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
        //     returned null for it -- a shell-owned window the virtual-desktop API simply
        //     doesn't track). WorkspaceManager.WindowsByWorkspace() silently drops these
        //     from `desktopOf` (`.Where(x => x.Desktop.IsSuccess)`), so such a window
        //     never appeared ANYWHERE in the panel -- not Pinned, not a workspace, not
        //     OtherDesktops. A window absent from both `pinned` and `desktopOf` here IS
        //     exactly that case (every non-pinned known window gets queried; only
        //     failures are missing from the dictionary) -- group it under a catch-all
        //     instead of disappearing.
        var unplaced = windows
            .Where(w => !pinned.Contains(w.Handle) && !desktopOf.ContainsKey(w.Handle))
            .ToList();
        var unplacedOrdinals = Ordinals(unplaced);
        var unplacedRows = unplaced.Select(w => Row(w, ordinal: OrdinalOf(unplacedOrdinals, w))).ToList();
        if (unplacedRows.Count > 0)
            otherDesktops = [.. otherDesktops, new DesktopGroup(Guid.Empty, "Unplaced", false, unplacedRows)];

        return new(pinnedRows, workspaceGroups, otherDesktops);
    }
}
