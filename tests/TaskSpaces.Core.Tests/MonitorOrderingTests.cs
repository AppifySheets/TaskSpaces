using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Overview;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Tests;

// Petre: "sort icons in workspaces by monitors, first icons from monitor1, then monitor2, etc.
// and i want to have the monitor number on the icon", then "can you also identify which window
// is minimized, vs not? or which one is on top?" and "maybe we can do 1 in bold, if it's on top".
//
// Against OverviewBuilder directly rather than through WorkspaceManager: the builder is pure by
// design (every OS fact arrives as data), so the ordering rules are testable without a single
// COM call or a fake desktop shell.
public class MonitorOrderingTests
{
    static readonly Guid Desktop = Guid.NewGuid();

    static WindowInfo Window(nint handle, string process) =>
        new(new WindowHandle(handle), (int)handle, process, $@"C:\{process}.exe", $"{process} window", $@"""C:\{process}.exe""");

    static readonly WindowInfo A = Window(0xA, "Alpha");
    static readonly WindowInfo B = Window(0xB, "Bravo");
    static readonly WindowInfo C = Window(0xC, "Charlie");

    // All three on one unbound desktop, which surfaces as a single OtherDesktops group.
    static IReadOnlyList<WindowRow> Rows(IReadOnlyList<WindowInfo> windows, ScreenFacts screen) =>
        OverviewBuilder.Build(
                AppState.Empty,
                windows,
                _ => Maybe<string>.None,
                new HashSet<WindowHandle>(),
                windows.ToDictionary(w => w.Handle, _ => Desktop),
                [new DesktopInfo(Desktop, "Main")],
                Guid.NewGuid(), // current is something else, so nothing is suppressed as "here"
                screen: screen)
            .OtherDesktops.Single().Windows;

    static ScreenFacts Facts(
        (WindowInfo Window, int Monitor)[] monitors,
        WindowInfo[]? minimized = null,
        WindowInfo[]? frontToBack = null) =>
        new(monitors.ToDictionary(x => x.Window.Handle, x => x.Monitor),
            (minimized ?? []).Select(w => w.Handle).ToHashSet(),
            (frontToBack ?? []).Select((w, i) => (w.Handle, i)).ToDictionary(x => x.Handle, x => x.i));

    [Fact]
    public void Icons_are_ordered_by_monitor_number()
    {
        // Deliberately supplied out of order: monitor 2, monitor 1, monitor 2.
        var rows = Rows([A, B, C], Facts([(A, 2), (B, 1), (C, 2)]));

        Assert.Equal([B.Handle, A.Handle, C.Handle], rows.Select(r => r.Window.Handle));
    }

    // The sort must REGROUP without reshuffling: two windows on the same monitor keep the order
    // they arrived in, so icons stay where Petre's hand expects them. OrderBy is stable, and
    // this pins that we are relying on it.
    [Fact]
    public void Windows_on_the_same_monitor_keep_their_existing_order()
    {
        var rows = Rows([C, A, B], Facts([(C, 2), (A, 2), (B, 1)]));

        Assert.Equal([B.Handle, C.Handle, A.Handle], rows.Select(r => r.Window.Handle));
    }

    // Petre: "let's also sort those icons by z-index." Monitor first, then front-most first
    // within each monitor -- so the two sorts compose rather than one overriding the other.
    [Fact]
    public void Within_a_monitor_icons_are_ordered_front_most_first()
    {
        // B is in front of A, and both are on monitor 1; C sits alone on monitor 2.
        var rows = Rows([A, B, C], Facts([(A, 1), (B, 1), (C, 2)], frontToBack: [B, A, C]));

        Assert.Equal([B.Handle, A.Handle, C.Handle], rows.Select(r => r.Window.Handle));
    }

    // Monitor still wins: a window in front on monitor 2 does not jump ahead of monitor 1.
    [Fact]
    public void Z_order_never_reorders_across_monitors()
    {
        var rows = Rows([A, B], Facts([(A, 2), (B, 1)], frontToBack: [A, B]));

        Assert.Equal([B.Handle, A.Handle], rows.Select(r => r.Window.Handle));
    }

    // A desktop with no z-order -- which is every desktop but the current one -- must come out
    // exactly as it did before z-order sorting existed.
    [Fact]
    public void Windows_with_no_z_order_sort_last_within_their_monitor_and_keep_their_order()
    {
        // C is in front; A and B have no z-order at all. All three on monitor 1.
        var rows = Rows([A, B, C], Facts([(A, 1), (B, 1), (C, 1)], frontToBack: [C]));

        Assert.Equal([C.Handle, A.Handle, B.Handle], rows.Select(r => r.Window.Handle));
    }

    // A window whose monitor could not be resolved must still be reachable -- same principle as
    // the "Unplaced" group, which exists because a window that renders nowhere is a window you
    // cannot click.
    [Fact]
    public void A_window_with_no_known_monitor_sorts_last_rather_than_vanishing()
    {
        var rows = Rows([A, B], Facts([(B, 1)])); // A's monitor unknown

        Assert.Equal([B.Handle, A.Handle], rows.Select(r => r.Window.Handle));
        Assert.False(rows.Single(r => r.Window.Handle == A.Handle).Monitor.HasValue);
    }

    [Fact]
    public void Each_row_carries_its_monitor_number_and_minimized_state()
    {
        var rows = Rows([A, B], Facts([(A, 1), (B, 2)], minimized: [B]));

        Assert.Equal(1, rows.Single(r => r.Window.Handle == A.Handle).Monitor.Value);
        Assert.False(rows.Single(r => r.Window.Handle == A.Handle).IsMinimized);
        Assert.True(rows.Single(r => r.Window.Handle == B.Handle).IsMinimized);
    }

    // "On top" is per MONITOR, not per row: with two monitors there are two front-most windows
    // on screen at once, and marking only one of them would misdescribe the other.
    [Fact]
    public void Each_monitor_gets_its_own_frontmost_window()
    {
        // Front to back: A (mon 1), B (mon 2), C (mon 1). So A leads monitor 1, B leads monitor 2.
        var rows = Rows([A, B, C], Facts([(A, 1), (B, 2), (C, 1)], frontToBack: [A, B, C]));

        Assert.True(rows.Single(r => r.Window.Handle == A.Handle).IsFrontmostOnMonitor.Value);
        Assert.True(rows.Single(r => r.Window.Handle == B.Handle).IsFrontmostOnMonitor.Value);
        Assert.False(rows.Single(r => r.Window.Handle == C.Handle).IsFrontmostOnMonitor.Value);
    }

    // EnumWindows skips cloaked windows, and every window on a non-current desktop is cloaked --
    // so z-order simply does not exist there. The flag must come back UNKNOWN rather than FALSE,
    // and the distinction is not academic: Petre dims the windows that are behind, so reporting
    // "not known to be in front" as "behind" would render every icon on every other workspace
    // dimmed at once.
    [Fact]
    public void Frontmost_is_unknown_rather_than_false_when_there_is_no_z_order()
    {
        var rows = Rows([A, B], Facts([(A, 1), (B, 1)])); // no z-order supplied

        Assert.All(rows, r => Assert.False(r.IsFrontmostOnMonitor.HasValue));
    }

    // ...but on a desktop that DOES have z-order, a window behind another must say so plainly,
    // or it would never dim.
    [Fact]
    public void A_covered_window_reports_false_rather_than_unknown()
    {
        var rows = Rows([A, B], Facts([(A, 1), (B, 1)], frontToBack: [A, B]));

        Assert.True(rows.Single(r => r.Window.Handle == A.Handle).IsFrontmostOnMonitor.Value);
        Assert.False(rows.Single(r => r.Window.Handle == B.Handle).IsFrontmostOnMonitor.Value);
    }

    // Everything above is additive: with no screen facts at all -- compatibility mode, or any
    // caller written before this existed -- the overview must look exactly as it used to.
    [Fact]
    public void Without_screen_facts_the_order_is_untouched_and_nothing_is_marked()
    {
        var rows = Rows([C, A, B], ScreenFacts.Empty);

        Assert.Equal([C.Handle, A.Handle, B.Handle], rows.Select(r => r.Window.Handle));
        Assert.DoesNotContain(rows, r => r.Monitor.HasValue || r.IsMinimized || r.IsFrontmostOnMonitor.HasValue);
    }
}
