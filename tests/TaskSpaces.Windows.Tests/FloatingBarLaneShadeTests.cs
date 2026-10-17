using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using TaskSpaces.App;
using TaskSpaces.Core;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using Xunit.Abstractions;

namespace TaskSpaces.Windows.Tests;

// Petre, on separating the two screens' halves of a row: rings were "too much", he asked for "softer
// separation", and of five mockups he picked the shade step. Every other screen's lane is a shade darker
// than the row's tint, with a 5 DIP gradient across the gap where the hairline used to be, and nothing
// else is drawn.
//
// These tests began as the hairline's ("separator is not always uniform in height", "separator on GEPHA is
// in a wrong place on the second line") and keep its invariants, because the shade answers the same
// questions: an empty row marks its boundary where a busy one does, every line of a wrapped row agrees, and
// the boundary does not wander.
//
// The shade is painted per LINE in the lane's own column, so a wrapped row's shade is a stack of slices
// that must meet edge to edge. Measured through TransformToAncestor, so the bar's scale and each row's own
// scale are part of the number rather than something to argue about afterwards.
public class FloatingBarLaneShadeTests(ITestOutputHelper output)
{
    // An empty workspace's shade is the same height and at the same x as a busy one's.
    //
    // About the line FLOOR as much as anything: a line holding nothing is still one icon line tall, so an
    // empty screen's half (a drop target, #102) is shaded exactly as fully as one with windows in it.
    [Fact]
    public void A_row_with_no_windows_is_shaded_like_a_row_with_icons() => StaThread.Run(() =>
    {
        using var bar = TwoScreens(busyIcons: 2);

        var lanes = bar.Shades().Where(s => s.Row is not "Pinned" && !s.IsFade).ToList();
        lanes.ForEach(s => output.WriteLine(s.ToString()));

        // Two rows, one line each, and only the second screen's half shaded in each.
        Assert.Equal(2, lanes.Count);
        Assert.Single(lanes.Select(s => Math.Round(s.Height, 1)).Distinct());
        Assert.Single(lanes.Select(s => Math.Round(s.X, 1)).Distinct());
    });

    // It is the SECOND screen that is shaded, and its shade starts where the gap's soft edge ends, so the
    // tone changes across the gap and nowhere else.
    [Fact]
    public void The_second_screen_is_shaded_and_the_change_is_softened_across_the_gap() => StaThread.Run(() =>
    {
        using var bar = TwoScreens(busyIcons: 2);

        var busy = bar.Shades().Where(s => s.Row == "Busy").ToList();
        busy.ForEach(s => output.WriteLine(s.ToString()));

        var fade = Assert.Single(busy, s => s.IsFade);
        var lane = Assert.Single(busy, s => !s.IsFade);
        Assert.Equal(fade.X + fade.Width, lane.X, 1);

        // To the right of the row's middle: the right-hand half, not the left.
        Assert.True(lane.X > lane.RowWidth / 2 - 5, lane.ToString());
    });

    // A wrapped row is shaded down every line, the slices meeting with no gap or overlap, and only the
    // last line's slice rounds the row's bottom-right corner.
    [Fact]
    public void A_wrapped_row_is_shaded_down_every_line() => StaThread.Run(() =>
    {
        // Seven windows, six of them on the first screen, so that screen's lane runs onto a second line.
        using var bar = TwoScreens(busyIcons: 7);

        var slices = bar.Shades().Where(s => s.Row == "Busy" && !s.IsFade).OrderBy(s => s.InsetInRow).ToList();
        slices.ForEach(s => output.WriteLine(s.ToString()));

        // The second screen has nothing on the second line and is still shaded down it, so the half is
        // the row's full height.
        Assert.Equal(2, slices.Count);
        Assert.Equal(slices[0].X, slices[1].X, 1);
        Assert.Equal(slices[0].InsetInRow + slices[0].Height, slices[1].InsetInRow, 1);
        Assert.False(slices[0].RoundedBottomRight);
        Assert.True(slices[1].RoundedBottomRight);
    });

    // Petre, with a screenshot of a wrapped GEPHA row: "separator on GEPHA is in a wrong place on the
    // second line." His own geometry dump said it in two lines:
    //
    //   line 0  cols=[*:74, auto:5, *:74]        zones=[screen1@start, screen2@73]
    //   line 1  cols=[*:50, auto:5, *:50, *:50]  zones=[screen1@5,     screen2@51]
    //
    // A THIRD lane on the second line, so the boundary moved from 73 to 51. It comes from the fallback
    // for a window whose monitor cannot be resolved, which used to get a lane of its own. With shading the
    // same mistake would move or narrow the shade on that line, so the check is one shade per line,
    // starting at the same x everywhere.
    [Fact]
    public void A_window_with_no_monitor_does_not_move_the_boundary() => StaThread.Run(() =>
    {
        using var bar = TwoScreens(busyIcons: 7, oneWithNoMonitor: true);

        var lanes = bar.Shades().Where(s => s.Row is not "Pinned" && !s.IsFade).ToList();
        lanes.ForEach(s => output.WriteLine(s.ToString()));

        Assert.All(lanes.GroupBy(s => s.Line), line => Assert.Single(line));
        Assert.Single(lanes.Select(s => Math.Round(s.X, 1)).Distinct());
    });

    // The shade sits UNDER everything else in its line, #89's drop highlight included, so dragging a window
    // over the shaded half lights it up as brightly as the other half instead of through a darkening.
    [Fact]
    public void The_shade_is_painted_beneath_the_drop_highlight() => StaThread.Run(() =>
    {
        using var bar = TwoScreens(busyIcons: 2);

        Assert.All(bar.Shades(), s =>
        {
            var children = s.LineGrid.Children.OfType<UIElement>().ToList();
            var shadeAt = children.IndexOf(s.Element);
            Assert.All(children.Where(c => !Bar.IsShade(c)), other => Assert.True(children.IndexOf(other) > shadeAt));
        });
    });

    // Petre: "arrange icons on the second screen to the right, push them to the right and leave a bit of
    // space in the middle", "so the middle line is the clicking area". The first screen's icons start at
    // the row's left edge, the second screen's end at its right edge, and the slack is in between.
    [Fact]
    public void The_second_screens_icons_are_pushed_to_the_right_edge() => StaThread.Run(() =>
    {
        using var bar = TwoScreens(busyIcons: 2);

        var (left, right) = bar.LaneIcons("Busy");
        output.WriteLine($"left icon {left.Start:0.##}..{left.End:0.##} in lane {left.LaneStart:0.##}..{left.LaneEnd:0.##}");
        output.WriteLine($"right icon {right.Start:0.##}..{right.End:0.##} in lane {right.LaneStart:0.##}..{right.LaneEnd:0.##}");

        Assert.Equal(left.LaneStart, left.Start, 1);
        Assert.Equal(right.LaneEnd, right.End, 1);
        // ...and between them, room to click that is not an icon.
        Assert.True(right.Start - left.End > 24, "the middle should be wider than an icon on a row this empty");
    });

    // From the mockups, he picked the middle that lights up on hover: hovering the row (not an icon) washes
    // the free space, never what is behind an icon, and moving onto an icon turns it off, because clicking
    // an icon goes to that window rather than to the workspace.
    [Fact]
    public void Hovering_the_row_lights_its_free_space_and_an_icon_turns_it_off() => StaThread.Run(() =>
    {
        using var bar = TwoScreens(busyIcons: 2);
        var row = bar.RowContainer("Busy");

        Assert.Empty(bar.LitWashes(row));

        row.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
        var lit = bar.LitWashes(row);
        // Each lane's free part plus the gap between them, and nothing holding an icon.
        Assert.Equal(3, lit.Count);
        Assert.All(lit, wash => Assert.Null(wash.Child));

        // "make the highlight with rounded edges a bit": the three pieces read as ONE rounded pill, so the
        // two lane pieces are rounded on their outer ends and square where they meet the gap, and the gap
        // itself is square on both sides.
        var ordered = lit.OrderBy(w => w.TransformToAncestor(row).Transform(new Point(0, 0)).X).ToList();
        Assert.True(ordered[0].CornerRadius is { TopLeft: > 0, BottomLeft: > 0, TopRight: 0, BottomRight: 0 }, ordered[0].CornerRadius.ToString());
        Assert.Equal(new CornerRadius(0), ordered[1].CornerRadius);
        Assert.True(ordered[2].CornerRadius is { TopLeft: 0, BottomLeft: 0, TopRight: > 0, BottomRight: > 0 }, ordered[2].CornerRadius.ToString());

        var icon = bar.Icons(row).First();
        icon.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
        Assert.Empty(bar.LitWashes(row));
    });

    // Petre: "can we try to recreate the actual taskbar in the workspace?", "with window titles, vertically
    // arranged", "active workspace only", then "same as it is now but at the bottom". The workspace you
    // are on is listed at the bottom of the bar, one window per line in its screen's column, each with its
    // title.
    [Fact]
    public void The_current_workspace_lists_one_titled_window_per_line_in_its_screens_column() => StaThread.Run(() =>
    {
        // Three windows: two on screen 1, one on screen 2.
        using var bar = TwoScreens(busyIcons: 3, standOnBusy: true);

        var titles = Bar.TitlesIn(bar.TitledList());
        output.WriteLine(string.Join(" | ", titles.Select(t => $"{t.Text}@{t.X:0},{t.Y:0}")));

        Assert.Equal(["App 1", "App 2", "App 3"], titles.Select(t => t.Text));
        // Screen 1's two windows one above the other, in the same column...
        Assert.Equal(titles[0].X, titles[1].X, 1);
        Assert.True(titles[1].Y > titles[0].Y);
        // ...and screen 2's in the other column, on the first line.
        Assert.True(titles[2].X > titles[0].X + 40);
        Assert.Equal(titles[0].Y, titles[2].Y, 1);
    });

    // Petre: "i want to show the pinned windows in the active taskspace as well, so the beeper." A pinned
    // window joins the titled list at the end of its screen's column, marked with a pin, and stays in the
    // pinned row too.
    [Fact]
    public void The_titled_list_includes_pinned_windows_marked_with_a_pin() => StaThread.Run(() =>
    {
        using var bar = TwoScreens(busyIcons: 3, standOnBusy: true, withPinned: true);

        var titles = Bar.TitlesIn(bar.TitledList());
        output.WriteLine(string.Join(" | ", titles.Select(t => $"{t.Text}@{t.X:0},{t.Y:0}")));

        // Screen 1's column: the workspace's two windows, then the pinned one; screen 2's: App 3.
        Assert.Equal(["App 1", "App 2", "📌 Beeper", "App 3"], titles.Select(t => t.Text));
        Assert.Equal(titles[0].X, titles[2].X, 1);
        // ...and it is still in the pinned row.
        Assert.Contains(bar.Icons(bar.RowContainer("Pinned")), icon =>
            System.Windows.Automation.AutomationProperties.GetName(icon) == "Beeper");
    });

    // Petre: "make the expanded workspace stand out from the rest", and from the mockups he picked a bar in
    // the workspace's colour down the list's left edge: the spine a nested row wears, drawn by the same code.
    [Fact]
    public void The_list_wears_a_spine_down_its_left_edge() => StaThread.Run(() =>
    {
        using var bar = TwoScreens(busyIcons: 3, standOnBusy: true);
        var list = bar.TitledList();

        // Visible: the drag marker is also a thin left-aligned Border in the same grid, hidden until a drag.
        var spine = list.Children.OfType<Border>().SingleOrDefault(b =>
            b is { Child: null, HorizontalAlignment: HorizontalAlignment.Left, Width: > 0 and < 5, Background: not null, Visibility: Visibility.Visible });
        Assert.NotNull(spine);
        Assert.Equal(list.RowDefinitions.Count, Grid.GetRowSpan(spine));
    });

    // Petre: "let's attempt expanding the current workspace, instead of adding a new bar below it". With
    // that setting the current workspace's OWN row is the titled list, and the bottom panel is empty.
    [Fact]
    public void With_the_setting_the_current_workspaces_own_row_expands() => StaThread.Run(() =>
    {
        using var bar = TwoScreens(busyIcons: 3, standOnBusy: true, expandRow: true);

        Assert.Equal(["App 1", "App 2", "App 3"], Bar.TitlesIn(bar.RowContainer("Busy")).Select(t => t.Text));
        Assert.Empty(bar.CurrentListPanel().Children);
    });

    // "clicking the workspace expands it below its current position but keeps the position of the top,
    // until i leave the floating window". With the pointer on the bar, switching away leaves the row you
    // were on expanded, so nothing above the new one shrinks; leaving the bar collapses it.
    [Fact]
    public void An_expanded_row_stays_expanded_until_the_pointer_leaves_the_bar() => StaThread.Run(() =>
    {
        using var bar = TwoScreens(busyIcons: 3, standOnBusy: true, expandRow: true);
        bar.Enter();

        Assert.True(bar.Manager.Switch(bar.EmptyWorkspace).IsSuccess);
        bar.Pulse();
        Assert.NotEmpty(Bar.TitlesIn(bar.RowContainer("Busy")));

        // Just after leaving, within the grace (the trip to grab a window's title bar): still held.
        bar.LeaveWithoutWaiting();
        Assert.NotEmpty(Bar.TitlesIn(bar.RowContainer("Busy")));

        bar.Leave();
        Assert.Empty(Bar.TitlesIn(bar.RowContainer("Busy")));
    });

    // "dragging and dropping a window from expanded workspace to another shouldn't collapse anything", "until
    // i leave the floating window, nothing should collapse". A drag's modal loop reads to WPF as the pointer
    // leaving; that must not let go. When the drag ends somewhere off the bar, it does.
    [Fact]
    public void A_drag_does_not_release_the_hold_until_it_ends_off_the_bar() => StaThread.Run(() =>
    {
        using var bar = TwoScreens(busyIcons: 3, standOnBusy: true, expandRow: true);
        bar.Enter();
        Assert.True(bar.Manager.Switch(bar.EmptyWorkspace).IsSuccess);
        bar.Pulse();

        // A drag in flight: the flag the drag source's own callbacks set (the real drag runs inside
        // Windows' modal OLE loop, which a test cannot drive).
        bar.SetField("draggingWindow", true);
        bar.Leave();
        Assert.NotEmpty(Bar.TitlesIn(bar.RowContainer("Busy")));

        // The drag ends with the pointer off the bar (it is parked off screen), so the hold lets go.
        bar.SetField("draggingWindow", false);
        bar.Leave();
        Assert.Empty(Bar.TitlesIn(bar.RowContainer("Busy")));
    });

    // "re collapses": a trip from the bar to a window's title bar can outlast any grace (the log had one of
    // eight seconds), so a title-bar drag starting soon after a release puts the released rows back.
    [Fact]
    public void A_title_bar_drag_soon_after_leaving_puts_the_released_rows_back() => StaThread.Run(() =>
    {
        using var bar = TwoScreens(busyIcons: 3, standOnBusy: true, expandRow: true);
        bar.Enter();
        Assert.True(bar.Manager.Switch(bar.EmptyWorkspace).IsSuccess);
        bar.Pulse();

        bar.Leave(); // the grace ran out on the way to the title bar
        Assert.Empty(Bar.TitlesIn(bar.RowContainer("Busy")));

        bar.Window.OnWindowDragStarted(new WindowHandle(101));
        bar.Window.UpdateLayout();
        Assert.NotEmpty(Bar.TitlesIn(bar.RowContainer("Busy")));
    });

    // "re collapses": a window leaving the expanded row (here by a title-bar drop) used to take its line with
    // it, so the row shrank under the pointer. While the hold lasts its line stays, faded; leaving the bar
    // lets the row close up.
    [Fact]
    public void A_window_leaving_an_expanded_row_keeps_its_line_until_the_pointer_leaves() => StaThread.Run(() =>
    {
        using var bar = TwoScreens(busyIcons: 3, standOnBusy: true, expandRow: true);
        bar.Enter();

        bar.HeaderDrop("Empty", new WindowHandle(101));
        bar.Pulse();
        Assert.Equal(["App 1", "App 2", "App 3"], Bar.TitlesIn(bar.RowContainer("Busy")).Select(t => t.Text));

        bar.Leave();
        bar.Pulse();
        Assert.Equal(["App 2", "App 3"], Bar.TitlesIn(bar.RowContainer("Busy")).Select(t => t.Text));
    });

    // A window dragged by its own title bar and dropped on a row (Petre: "move to another workspace by
    // dragging a window") moves there WITHOUT taking you along: "don't follow the workspace, move it out of
    // the current workspace, stay on the current workspace". The drag itself runs in Windows' move loop and
    // needs the real cursor, so this calls the row's drop directly, which is what the end of a drag does.
    [Fact]
    public void A_title_bar_drop_moves_the_window_and_does_not_follow_it() => StaThread.Run(() =>
    {
        using var bar = TwoScreens(busyIcons: 3, standOnBusy: true);
        var window = new WindowHandle(101); // App 1, on Busy, the workspace you are standing on

        // Made the window you are IN, which is exactly when an icon drop would take you along with it.
        bar.Pulse();
        bar.HeaderDrop("Empty", window);

        Assert.Equal(bar.EmptyDesktop, bar.Desktops.Placements[window]);
        Assert.Empty(bar.Desktops.Switched);
    });

    // "so workspaces don't change sizes every time i click them": every workspace row stays icons, the one
    // you are on included, so standing on a workspace leaves its row exactly as tall as it was.
    [Fact]
    public void A_workspace_row_stays_icons_and_keeps_its_height_when_you_stand_on_it() => StaThread.Run(() =>
    {
        double HeightOfBusy(bool standOnBusy)
        {
            using var bar = TwoScreens(busyIcons: 3, standOnBusy: standOnBusy);
            var row = bar.RowContainer("Busy");
            Assert.Empty(Bar.TitlesIn(row));
            return row.ActualHeight;
        }

        Assert.Equal(HeightOfBusy(standOnBusy: false), HeightOfBusy(standOnBusy: true), 1);
    });

    // One display: one lane, nothing to tell apart, so nothing is shaded.
    [Fact]
    public void One_screen_shades_nothing() => StaThread.Run(() =>
    {
        using var bar = TwoScreens(busyIcons: 3, everythingOnOneScreen: true);

        Assert.Empty(bar.Shades());
    });

    // Two displays, which is what turns the lanes on at all; one workspace holding busyIcons windows and
    // one holding nothing. The last window goes on the second screen so the busy row uses both lanes,
    // and the rest on the first.
    // `standOnBusy` makes Busy the CURRENT workspace, which draws it as the titled list rather than as an
    // icon row. Off by default: every test here but the titled ones is about icon rows, so they stand on
    // the empty workspace and leave Busy as an ordinary row.
    // `withPinned` adds one window pinned to every desktop, on screen 1, named "Beeper".
    // `expandRow` turns on AppState.ExpandCurrentWorkspace: the current workspace's own row expands into
    // the titled list instead of the list sitting at the bottom of the bar.
    static Bar TwoScreens(int busyIcons, bool oneWithNoMonitor = false, bool everythingOnOneScreen = false, bool standOnBusy = false, bool withPinned = false, bool expandRow = false)
    {
        var busyDesktop = Guid.NewGuid();
        var emptyDesktop = Guid.NewGuid();
        var busy = new Workspace(Guid.NewGuid(), "Busy", busyDesktop);
        var empty = new Workspace(Guid.NewGuid(), "Empty", emptyDesktop);

        var desktops = new PulsingDesktops { CurrentId = standOnBusy ? busyDesktop : emptyDesktop };
        desktops.Desktops.Add(new DesktopInfo(busyDesktop, "Busy"));
        desktops.Desktops.Add(new DesktopInfo(emptyDesktop, "Empty"));

        var monitor = new StubMonitor();
        var placement = new List<(WindowHandle Window, int Monitor)>();
        Enumerable.Range(1, busyIcons).ToList().ForEach(i =>
        {
            var window = new WindowInfo(new WindowHandle(100 + i), 10 + i, "app" + i, @"C:\app" + i + ".exe", "App " + i, null);
            desktops.Placements[window.Handle] = busyDesktop;
            monitor.Initial.Add(window);
            // The last window goes on the second screen; the one before it can be given no monitor at
            // all, which is the case that used to add a lane.
            if (oneWithNoMonitor && i == busyIcons - 1) return;
            placement.Add((window.Handle, i == busyIcons && !everythingOnOneScreen ? 2 : 1));
        });

        if (withPinned)
        {
            var beeper = new WindowInfo(new WindowHandle(0x500), 500, "Beeper", @"C:\Beeper.exe", "Beeper", null);
            monitor.Initial.Add(beeper);
            desktops.PinnedWindows.Add(beeper.Handle);
            placement.Add((beeper.Handle, 1));
        }

        var screens = new StubScreens { Facts = TwoDisplays(placement) };
        // A STORED WIDTH, because that is the bar Petre runs and it selects a different layout: with
        // one, each screen's lane wraps against its own share of the row, and without one the rows keep
        // the old fixed five-icons rule and a lane can overflow its share -- which pushes a star column
        // wider and moves the boundary for reasons that have nothing to do with what is being tested
        // here. That case is known and written down in GroupRow ("still unprotected on a bar that has
        // never been resized"); it resolves itself the moment a width is stored, which is the moment
        // this fixture reproduces.
        //
        // 160 rather than the 202 the hairline tests used: the caption gutter's 58 DIP went back to the
        // icons when the caption moved above them. At the default 0.9 scale, 160 / 0.9 - 12 (padding and
        // ring) leaves 165.8 DIP, so each of the two lanes gets 80 and holds three 24 DIP icons -- the
        // same three that 202 gave with the gutter -- and seven windows still wrap the way these tests
        // need. It is also just above the bar's minimum width (176 * 0.9 = 158.4), which a smaller stored
        // width would be silently raised to.
        var store = new StubStore
        {
            Stored = AppState.Empty with
            {
                Workspaces = [busy, empty],
                FloatingBar = new FloatingBarState(0, 0, true) { Width = 160 },
                ExpandCurrentWorkspace = expandRow,
            },
        };
        var manager = new WorkspaceManager(desktops, monitor, new StubTitles(), store, screenLayout: screens);
        Assert.True(manager.Start().IsSuccess);

        // Parked off the virtual screen, as every bar test does, so the suite never flashes a
        // translucent topmost bar at whoever is running it. WPF still reports it visible.
        var bar = new FloatingBar(manager) { Left = -32000, Top = -32000 };
        bar.Show();
        // SizeToContent: nothing in the tree has a height until a layout pass has run.
        bar.UpdateLayout();
        // A rebuild the way the running app gets one after a switch: a window event. The fake desktops
        // change the current desktop on Switch but send no notification of their own.
        return new Bar(bar, manager, empty.Id, () => monitor.Pump.OnNext(new WindowEvent(WindowEventKind.Activated, monitor.Initial[0])),
            desktops, emptyDesktop);
    }

    static ScreenFacts TwoDisplays(IReadOnlyList<(WindowHandle Window, int Monitor)> frontToBack) =>
        new(frontToBack.ToDictionary(x => x.Window, x => x.Monitor),
            new HashSet<WindowHandle>(),
            frontToBack.Select((x, i) => (x.Window, i)).ToDictionary(x => x.Window, x => x.i),
            PrimaryMonitor: 1);

    // One measured slice of shade: a lane's (solid) or the gap's soft edge (a gradient). Every number is in
    // the bar's own coordinates, transforms included.
    sealed record Shade(string Row, Grid LineGrid, Border Element, bool IsFade, double X, double Width, double Height,
        double InsetInRow, double RowWidth, bool RoundedBottomRight)
    {
        public object Line => LineGrid;

        public override string ToString() =>
            "row=" + Row + (IsFade ? " fade" : " lane") + " x=" + X.ToString("0.##") + " width=" + Width.ToString("0.##") +
            " height=" + Height.ToString("0.##") + " insetInRow=" + InsetInRow.ToString("0.##") +
            " rowWidth=" + RowWidth.ToString("0.##") + " rounded=" + RoundedBottomRight;
    }

    sealed class Bar(FloatingBar bar, WorkspaceManager? manager = null, Guid emptyWorkspace = default, Action? pulse = null,
        PulsingDesktops? desktops = null, Guid emptyDesktop = default) : IDisposable
    {
        public PulsingDesktops Desktops => desktops!;

        public FloatingBar Window => bar;

        public Guid EmptyDesktop => emptyDesktop;

        // What the end of a title-bar drag over the named row does, called directly: the row's registered
        // header drop, with no screen aimed.
        public void HeaderDrop(string label, WindowHandle window)
        {
            var targets = (System.Collections.IEnumerable)typeof(FloatingBar).GetField("headerDropTargets", Private)!.GetValue(bar)!;
            var target = targets.Cast<object>().Single(t => (string)t.GetType().GetProperty("Label")!.GetValue(t)! == label);
            var drop = (Action<WindowHandle, int?>)target.GetType().GetProperty("Drop")!.GetValue(target)!;
            drop(window, null);
        }

        public WorkspaceManager Manager => manager!;

        public Guid EmptyWorkspace => emptyWorkspace;

        // ...and a layout pass after it: rows built by a rebuild have no visual tree to search until one runs,
        // which the app gets for free and a test has to ask for.
        public void Pulse()
        {
            pulse?.Invoke();
            bar.UpdateLayout();
        }

        // For state only a modal loop or a private callback can set (a drag in flight, and its finish).
        const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        public void SetField(string name, object value) => typeof(FloatingBar).GetField(name, Private)!.SetValue(bar, value);

        public void Call(string method) => typeof(FloatingBar).GetMethod(method, Private)!.Invoke(bar, null);

        // The pointer arriving on, and leaving, the bar window, as WPF raises them. Leaving is deferred in the
        // bar (it re-checks where the pointer really is on the next dispatcher turn), so the queue is drained
        // here; the real pointer is nowhere near a bar parked off screen, so the re-check agrees it left.
        public void Enter() => bar.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });

        // The pointer leaving, and then the release's grace running out (called directly rather
        // than waited for).
        public void Leave()
        {
            LeaveWithoutWaiting();
            Call("ReleaseHeldRowsNow");
            bar.UpdateLayout();
        }

        // The pointer leaving, with the grace still running.
        public void LeaveWithoutWaiting()
        {
            bar.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseLeaveEvent });
            bar.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            bar.UpdateLayout();
        }

        public void Dispose() => bar.Close();

        public List<Shade> Shades() =>
            Descendants((Panel)bar.FindName("Rows")!).OfType<Border>().Where(IsShade)
                .Select(shade =>
                {
                    var row = Row(shade);
                    return new Shade(Label(row), Ancestor<Grid>(shade), shade, shade.Background is LinearGradientBrush,
                        Left(shade), Width(shade), Extent(shade), Top(shade) - Top(row), Width(row),
                        shade.CornerRadius.BottomRight > 0);
                })
                .ToList();

        // The Grid a row is built around, which is what carries the row's hover handlers. The FIRST one
        // with that name: the current workspace's titled list at the bottom of the bar carries its
        // workspace's name too (see TitledList).
        public Grid RowContainer(string label) =>
            Containers().First(g => System.Windows.Automation.AutomationProperties.GetName(g) == label);

        // Every row container in Rows, top to bottom (separators skipped).
        public List<Grid> RowsInOrder() => Containers().ToList();

        public Panel CurrentListPanel() => (Panel)bar.FindName("CurrentList")!;

        // The current workspace's titled list, in its own panel below every workspace row.
        public Grid TitledList() =>
            ((Panel)bar.FindName("CurrentList")!).Children.OfType<Border>().Select(b => b.Child).OfType<Grid>().Single();

        IEnumerable<Grid> Containers() =>
            ((Panel)bar.FindName("Rows")!).Children.OfType<Border>().Select(b => b.Child).OfType<Grid>();

        // The window titles drawn in a row: TextBlocks inside a lane's icon buttons (the caption's text is
        // outside every lane), with where each one starts, ordered by column and then down the column.
        public static List<(string Text, double X, double Y)> TitlesIn(Grid row) =>
            Descendants(row).OfType<DockPanel>()
                .SelectMany(lane => Descendants(lane).OfType<Button>())
                // The title is the text that trims; a window with no icon draws a one-letter placeholder,
                // which is a TextBlock too, and does not.
                .SelectMany(button => Descendants(button).OfType<TextBlock>().Where(t => t.TextTrimming == TextTrimming.CharacterEllipsis))
                .Select(t => (t.Text, Origin: t.TransformToAncestor(row).Transform(new Point(0, 0))))
                .Select(t => (t.Text, t.Origin.X, t.Origin.Y))
                .OrderBy(t => Math.Round(t.X / 20))
                .ThenBy(t => t.Y)
                .ToList();

        // The row's icon buttons: the ones inside a lane, as opposed to the caption's button above them.
        public List<Button> Icons(DependencyObject row) =>
            Descendants(row).OfType<DockPanel>().SelectMany(lane => Descendants(lane).OfType<Button>()).ToList();

        // Free-space washes currently lit: an EMPTY Border painted the hover wash's faint white. Empty,
        // because an active icon's own chrome is a Border with a light fill too, and it holds the icon.
        public List<Border> LitWashes(DependencyObject row) =>
            Descendants(row).OfType<Border>()
                .Where(b => b.Child is null && b.Background is SolidColorBrush { Color: { R: 255, G: 255, B: 255, A: > 0 and < 0x40 } })
                .ToList();

        public sealed record Span(double Start, double End, double LaneStart, double LaneEnd);

        // The only icon on each screen of a row, with the lane it sits in, as x ranges in bar coordinates.
        public (Span Left, Span Right) LaneIcons(string label)
        {
            var spans = Icons(RowContainer(label))
                .Select(icon =>
                {
                    var lane = Ancestor<DockPanel>(icon);
                    return new Span(Left(icon), Left(icon) + Width(icon), Left(lane), Left(lane) + Width(lane));
                })
                .OrderBy(s => s.Start)
                .ToList();
            return (spans[0], spans[^1]);
        }

        // A shade: a childless Border that does not take the mouse and paints a black, translucent fill
        // (solid for a lane, a gradient for the gap's soft edge). Found by shape, because none of the bar's
        // elements are named, and nothing else on the bar is a hit-test-invisible, childless, black fill.
        public static bool IsShade(object node) =>
            node is Border { Child: null, IsHitTestVisible: false } border
            && border.Background switch
            {
                SolidColorBrush solid => solid.Color is { R: 0, G: 0, B: 0, A: > 0 },
                LinearGradientBrush gradient => gradient.GradientStops.All(s => s.Color is { R: 0, G: 0, B: 0 }),
                _ => false,
            };

        static IEnumerable<DependencyObject> Descendants(DependencyObject node) =>
            Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(node))
                .Select(i => VisualTreeHelper.GetChild(node, i))
                .SelectMany(child => new[] { child }.Concat(Descendants(child)));

        // The row: the outermost Border wrapping a Grid on the way up, which is what GroupRow builds
        // around every row's container.
        static FrameworkElement Row(DependencyObject node) =>
            Ancestors(node).OfType<Border>().Last(b => b.Child is Grid);

        static T Ancestor<T>(DependencyObject node) where T : FrameworkElement =>
            Ancestors(node).OfType<T>().First();

        static IEnumerable<DependencyObject> Ancestors(DependencyObject node)
        {
            for (var at = VisualTreeHelper.GetParent(node); at is not null; at = VisualTreeHelper.GetParent(at))
                yield return at;
        }

        double Extent(FrameworkElement element) =>
            element.TransformToAncestor(bar).TransformBounds(new Rect(0, 0, 1, element.ActualHeight)).Height;

        double Width(FrameworkElement element) =>
            element.TransformToAncestor(bar).TransformBounds(new Rect(0, 0, element.ActualWidth, 1)).Width;

        double Top(FrameworkElement element) => element.TransformToAncestor(bar).Transform(new Point(0, 0)).Y;

        double Left(FrameworkElement element) => element.TransformToAncestor(bar).Transform(new Point(0, 0)).X;

        // The row's accessible name first, read off the Grid inside the row's Border, because since
        // #173 the pinned row has no text at all -- its caption cell holds the bar's two buttons -- and
        // a text-first reading would label it "↩". Text second, since a workspace row is still
        // identified by its caption.
        static string Label(DependencyObject row) =>
            (row is Border { Child: { } inner }
                ? System.Windows.Automation.AutomationProperties.GetName(inner)
                : System.Windows.Automation.AutomationProperties.GetName(row)) is { Length: > 0 } name
                ? name
                : Descendants(row).OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => t.Length > 0) ?? "unnamed";
    }
}

// Monitor numbers for the bar and nothing else: the window moves this interface also carries are no
// part of this question.
sealed class StubScreens : IScreenLayout
{
    public ScreenFacts Facts { get; set; } = ScreenFacts.Empty;
    public ScreenFacts Snapshot() => Facts;
    public Maybe<WindowRect> RectOf(WindowHandle window) => Maybe<WindowRect>.None;
    public Result MoveTo(WindowHandle window, WindowRect rect, bool mayChangeShowState) => Result.Success();
}
