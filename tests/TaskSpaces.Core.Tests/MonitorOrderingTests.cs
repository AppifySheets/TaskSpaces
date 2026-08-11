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

    // Petre: "when there are multiple similar icons, multiple edges, i want them numbered,
    // arbitrarily, if i'm selecting the second browser, i can see that the other, first got
    // demoted in the bar", and "no numbers for one-instance apps".
    [Fact]
    public void Windows_of_the_same_app_are_numbered_and_a_lone_app_is_not()
    {
        var edgeOne = Window(0x20, "Edge");
        var edgeTwo = Window(0x10, "Edge"); // lower handle, so this is the one that gets 1
        var editor = Window(0x30, "Editor");

        var rows = Rows([edgeOne, edgeTwo, editor], Facts([(edgeOne, 1), (edgeTwo, 1), (editor, 1)]));

        Assert.Equal(1, rows.Single(r => r.Window.Handle == edgeTwo.Handle).Ordinal.Value);
        Assert.Equal(2, rows.Single(r => r.Window.Handle == edgeOne.Handle).Ordinal.Value);
        Assert.False(rows.Single(r => r.Window.Handle == editor.Handle).Ordinal.HasValue);
    }

    // #105, and the bug this file's grouping had all along: "the YouTube Music icon in the Personal
    // row shows the underline colour band, but there is only one YouTube Music window."
    //
    // A Chromium PWA runs as the BROWSER's own exe, so grouping by process name counted the PWA and
    // the browser window beside it as two windows of one app. The band's only job is telling apart
    // windows whose ARTWORK is identical, and a PWA is drawn with its own icon (IconCache asks the
    // window itself), so it never needed distinguishing from the browser at all.
    [Fact]
    public void A_browser_app_window_is_not_another_window_of_the_browser()
    {
        var browser = new WindowInfo(new WindowHandle(0x10), 10, "chrome", @"C:\chrome.exe", "Inbox",
            @"""C:\chrome.exe"" --profile-directory=Default");
        var music = new WindowInfo(new WindowHandle(0x20), 10, "chrome", @"C:\chrome.exe", "YouTube Music",
            @"""C:\chrome.exe"" --profile-directory=Default --app-id=cinhimbnkkaeohfgghhklpknlkffjgod");

        var rows = Rows([browser, music], Facts([(browser, 1), (music, 1)]));

        Assert.False(rows.Single(r => r.Window.Handle == browser.Handle).Ordinal.HasValue);
        Assert.False(rows.Single(r => r.Window.Handle == music.Handle).Ordinal.HasValue);
    }

    // The other direction, and the reason RosterIdentity is the WRONG key here however tempting the
    // reuse looks: it includes the profile, so two browser windows on different profiles would each
    // be a family of one and lose the band. They are drawn with the same icon, which makes them
    // exactly the pair the band was asked for.
    [Fact]
    public void Two_browser_windows_on_different_profiles_are_still_numbered()
    {
        var work = new WindowInfo(new WindowHandle(0x10), 10, "chrome", @"C:\chrome.exe", "Work",
            @"""C:\chrome.exe"" --profile-directory=Default");
        var home = new WindowInfo(new WindowHandle(0x20), 11, "chrome", @"C:\chrome.exe", "Home",
            @"""C:\chrome.exe"" --profile-directory=""Profile 2""");

        var rows = Rows([work, home], Facts([(work, 1), (home, 1)]));

        Assert.Equal(1, rows.Single(r => r.Window.Handle == work.Handle).Ordinal.Value);
        Assert.Equal(2, rows.Single(r => r.Window.Handle == home.Handle).Ordinal.Value);
    }

    // Two PWAs of the same browser are two apps, not two windows of one, and each is alone.
    [Fact]
    public void Two_different_browser_apps_are_each_alone()
    {
        var music = new WindowInfo(new WindowHandle(0x10), 10, "chrome", @"C:\chrome.exe", "YouTube Music",
            @"""C:\chrome.exe"" --app-id=music");
        var mail = new WindowInfo(new WindowHandle(0x20), 10, "chrome", @"C:\chrome.exe", "Outlook",
            @"""C:\chrome.exe"" --app-id=mail");

        var rows = Rows([music, mail], Facts([(music, 1), (mail, 1)]));

        Assert.False(rows.Single(r => r.Window.Handle == music.Handle).Ordinal.HasValue);
        Assert.False(rows.Single(r => r.Window.Handle == mail.Handle).Ordinal.HasValue);
    }

    // ...and two windows of the SAME PWA are two windows of one app, band and all.
    [Fact]
    public void Two_windows_of_one_browser_app_are_numbered()
    {
        var first = new WindowInfo(new WindowHandle(0x10), 10, "chrome", @"C:\chrome.exe", "YouTube Music",
            @"""C:\chrome.exe"" --app-id=music");
        var second = new WindowInfo(new WindowHandle(0x20), 10, "chrome", @"C:\chrome.exe", "YouTube Music",
            @"""C:\chrome.exe"" --app-id=music");

        var rows = Rows([first, second], Facts([(first, 1), (second, 1)]));

        Assert.Equal(1, rows.Single(r => r.Window.Handle == first.Handle).Ordinal.Value);
        Assert.Equal(2, rows.Single(r => r.Window.Handle == second.Handle).Ordinal.Value);
    }

    // Two Rider windows on different solutions look identical and must stay numbered, which is the
    // second reason the roster's identity cannot be reused: it separates them by their arguments.
    [Fact]
    public void Two_windows_of_one_app_on_different_documents_are_still_numbered()
    {
        var x = new WindowInfo(new WindowHandle(0x10), 10, "rider64", @"C:\rider64.exe", "X", @"""C:\rider64.exe"" X.sln");
        var y = new WindowInfo(new WindowHandle(0x20), 11, "rider64", @"C:\rider64.exe", "Y", @"""C:\rider64.exe"" Y.sln");

        var rows = Rows([x, y], Facts([(x, 1), (y, 1)]));

        Assert.Equal(1, rows.Single(r => r.Window.Handle == x.Handle).Ordinal.Value);
        Assert.Equal(2, rows.Single(r => r.Window.Handle == y.Handle).Ordinal.Value);
    }

    // An elevated window has no readable path, and two of them still have to group. The process
    // name is the only key left, which is what the old grouping used for everything.
    [Fact]
    public void Windows_with_no_readable_path_group_by_name()
    {
        var one = new WindowInfo(new WindowHandle(0x10), 10, "admin", null, "One", null);
        var two = new WindowInfo(new WindowHandle(0x20), 11, "admin", null, "Two", null);

        var rows = Rows([one, two], Facts([(one, 1), (two, 1)]));

        Assert.Equal(1, rows.Single(r => r.Window.Handle == one.Handle).Ordinal.Value);
        Assert.Equal(2, rows.Single(r => r.Window.Handle == two.Handle).Ordinal.Value);
    }

    // The point of the number is to stay put while the icons move. If it were derived from
    // position, watching "2 change places with 1" would be impossible -- the labels would just
    // follow the sort and nothing would appear to have happened.
    [Fact]
    public void The_number_does_not_follow_z_order()
    {
        var edgeOne = Window(0x10, "Edge");
        var edgeTwo = Window(0x20, "Edge");

        var before = Rows([edgeOne, edgeTwo], Facts([(edgeOne, 1), (edgeTwo, 1)], frontToBack: [edgeOne, edgeTwo]));
        // Now the second one is brought to the front, which reverses the ROW order.
        var after = Rows([edgeOne, edgeTwo], Facts([(edgeOne, 1), (edgeTwo, 1)], frontToBack: [edgeTwo, edgeOne]));

        Assert.Equal([edgeOne.Handle, edgeTwo.Handle], before.Select(r => r.Window.Handle));
        Assert.Equal([edgeTwo.Handle, edgeOne.Handle], after.Select(r => r.Window.Handle));
        // ...and each window kept the number it had, which is what makes the demotion visible.
        Assert.Equal(1, after.Single(r => r.Window.Handle == edgeOne.Handle).Ordinal.Value);
        Assert.Equal(2, after.Single(r => r.Window.Handle == edgeTwo.Handle).Ordinal.Value);
    }

    // Petre: "i'd like to arrange windows by my monitors -- my left monitor first, my right
    // monitor next", after "i only see hairlines at the beginning of every workspace".
    //
    // REVERSES The_primary_display_is_the_silent_one, which this replaces, and the reasoning is
    // worth keeping rather than just the outcome. Making the PRIMARY silent fixed a real
    // complaint (marks landing on the main screen), but it left two orderings in play: icons
    // grouped by display number, silence assigned by primary. On Petre's machine the primary is
    // DISPLAY2 -- the RIGHT screen, which sorts second -- so the unmarked group sat in the middle
    // of the row where "no mark" cannot read as a boundary. His two groups ran together and the
    // only hairline was the stray one opening the row.
    //
    // Position now decides both, so the unmarked group is unmarked BECAUSE it comes first.
    //
    // Petre's real geometry: DISPLAY1 at x=-2560 (left, not primary), DISPLAY2 at x=0 (right,
    // primary), offset vertically by 153px.
    // The numbering is deliberately set AGAINST the geometry -- DISPLAY1 on the right and also
    // the primary, DISPLAY2 on the left -- so that both of the rules this replaces would give the
    // opposite answer on every assertion here. Order by display number would be A then B;
    // primary-is-silent would rank A at 0. Anything less than this passes under the old rule too.
    [Fact]
    public void The_leftmost_display_leads_and_is_the_silent_one_whichever_is_primary()
    {
        var rows = Rows([A, B], Placed(primary: 1, (A, 1, Right), (B, 2, Left)));

        // B is on the LEFT screen, so it leads the row and draws no stroke -- despite the higher
        // display number, and despite the other screen being the primary.
        Assert.Equal([B.Handle, A.Handle], rows.Select(r => r.Window.Handle));
        Assert.Equal(0, rows.Single(r => r.Window.Handle == B.Handle).MonitorRank.Value);
        Assert.Equal(1, rows.Single(r => r.Window.Handle == A.Handle).MonitorRank.Value);
    }

    // The banding, and Petre's own setup is why it exists: his screens sit side by side but are
    // offset vertically by 153px. Ordering strictly by top edge would put the RIGHT screen first,
    // because its top edge is higher -- the exact opposite of what he asked for.
    [Fact]
    public void Side_by_side_screens_read_left_to_right_however_badly_they_are_aligned()
    {
        // Right screen's top edge is HIGHER (y=0 against y=153), so a naive topmost-first order
        // would lead with it. Numbered against the geometry again, so display-number order would
        // also lead with the right-hand screen.
        var rows = Rows([A, B], Placed(primary: 1,
            (A, 1, new MonitorBounds(0, 0, 2560, 1440)),
            (B, 2, new MonitorBounds(-2560, 153, 0, 1593))));

        Assert.Equal([B.Handle, A.Handle], rows.Select(r => r.Window.Handle));
    }

    // ...and a genuine grid still reads top-left, top-right, then down. Screens that only TOUCH
    // vertically start a new row rather than joining the one above.
    [Fact]
    public void A_grid_of_screens_reads_across_then_down()
    {
        var order = MonitorArrangement.ReadingOrder(new Dictionary<int, MonitorBounds>
        {
            [3] = new(0, 1080, 1920, 2160),     // bottom-left
            [1] = new(1920, 0, 3840, 1080),     // top-right
            [4] = new(1920, 1080, 3840, 2160),  // bottom-right
            [2] = new(0, 0, 1920, 1080),        // top-left
        });

        Assert.Equal([2, 1, 3, 4], order);
    }

    static readonly MonitorBounds Left = new(-2560, 0, 0, 1440);
    static readonly MonitorBounds Right = new(0, 0, 2560, 1440);

    // Facts with real geometry: each window's display number plus where that display sits.
    static ScreenFacts Placed(int primary, params (WindowInfo Window, int Monitor, MonitorBounds Bounds)[] on) =>
        new(on.ToDictionary(x => x.Window.Handle, x => x.Monitor),
            new HashSet<WindowHandle>(),
            new Dictionary<WindowHandle, int>(),
            primary,
            on.GroupBy(x => x.Monitor).ToDictionary(g => g.Key, g => g.First().Bounds));

    // With no primary reported -- only tests -- it degrades to plain ascending order, so display
    // 1 goes silent exactly as it did before.
    [Fact]
    public void Without_a_primary_the_lowest_display_is_silent()
    {
        var rows = Rows([A, B], Facts([(A, 1), (B, 2)]));

        Assert.Equal(0, rows.Single(r => r.Window.Handle == A.Handle).MonitorRank.Value);
        Assert.Equal(1, rows.Single(r => r.Window.Handle == B.Handle).MonitorRank.Value);
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
