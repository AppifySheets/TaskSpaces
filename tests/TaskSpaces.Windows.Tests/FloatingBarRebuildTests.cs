using System.Reactive.Subjects;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using TaskSpaces.App;
using TaskSpaces.Core;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Windows.Tests;

// Regression test for Petre's screenshot showing every bar row rendered TWICE
// (GEPHA / Sparrow / Main / Unplaced, then all four again, one info line at the bottom).
//
// ROOT CAUSE this pins: FloatingBar.Rebuild() clears Rows.Children at the top and adds
// the new rows at the bottom, with manager.WindowsByWorkspace() in between. Four facts
// combine to make that gap re-entrant:
//
//   1. WindowMonitor hooks with WINEVENT_OUTOFCONTEXT, so its callbacks are delivered on
//      the UI thread *while messages are being pumped*.
//   2. WorkspaceManager.stateChanged is a plain Rx Subject -- OnNext calls subscribers
//      synchronously, on the calling thread.
//   3. The bar's subscription uses Dispatcher.Invoke, which runs the delegate INLINE when
//      the caller is already on the dispatcher thread (it does not queue).
//   4. WindowsByWorkspace() makes virtual-desktop COM calls, and COM calls on an STA
//      thread pump the message queue.
//
// So a window appearing mid-query re-enters Rebuild: the nested call clears an
// already-empty panel, adds ITS rows, returns -- and then the outer call appends its own
// rows on top. Every row appears twice until the next clean pulse repairs it, which is
// exactly why the bug was transient and self-healing.
//
// The test reproduces that call stack with no COM and no message pump at all: the
// desktop-service stub fires one window event from inside DesktopOf, which
// WindowsByWorkspace calls -- precisely the point where the real pump used to let a
// WinEvent callback in.
public class FloatingBarRebuildTests
{
    [Fact]
    public void Rows_are_not_duplicated_when_a_window_event_lands_mid_rebuild() => StaThread.Run(() =>
    {
        var gephaDesktop = Guid.NewGuid();
        var sparrowDesktop = Guid.NewGuid();
        var gepha = new Workspace(Guid.NewGuid(), "GEPHA", gephaDesktop);
        var sparrow = new Workspace(Guid.NewGuid(), "Sparrow", sparrowDesktop);

        var desktops = new PulsingDesktops { CurrentId = gephaDesktop };
        desktops.Desktops.Add(new DesktopInfo(gephaDesktop, "GEPHA"));
        desktops.Desktops.Add(new DesktopInfo(sparrowDesktop, "Sparrow"));

        // Both windows are PLACED, so neither lands in the "Unplaced" catch-all and the
        // expected group count stays exactly the two workspaces.
        var rdm = new WindowInfo(new WindowHandle(101), 11, "rdm", @"C:\rdm.exe", "Remote Desktop Manager", null);
        var beeper = new WindowInfo(new WindowHandle(202), 22, "beeper", @"C:\beeper.exe", "Beeper", null);
        desktops.Placements[rdm.Handle] = gephaDesktop;
        desktops.Placements[beeper.Handle] = sparrowDesktop;

        var monitor = new StubMonitor();
        monitor.Initial.AddRange([rdm, beeper]);

        var store = new StubStore { Stored = AppState.Empty with { Workspaces = [gepha, sparrow] } };
        var manager = new WorkspaceManager(desktops, monitor, new StubTitles(), store);
        Assert.True(manager.Start().IsSuccess);

        var bar = new FloatingBar(manager);
        // Parked far off the virtual screen so running the suite never flashes a real
        // translucent topmost bar at whoever is running it. It still counts as visible to
        // WPF, which is what the subscription's IsVisible guard requires.
        bar.Left = -32000;
        bar.Top = -32000;
        bar.Show();

        var rows = (StackPanel)bar.FindName("Rows")!;

        // GroupRow builds a Border wrapping a Grid per group; Separator builds a childless
        // Border. Counting the Borders that CONTAIN a Grid counts the groups, independent of
        // separator placement.
        //
        // (The row container became a Grid when the labels moved right of the icons, which
        // needed two columns. The Border around it came later, when the current-workspace
        // marker moved from a pill on the label to a box around the whole row.)
        int GroupCount() => rows.Children.OfType<Border>().Count(b => b.Child is Grid);

        // 3 = the always-rendered 📌 row (Task 12) + GEPHA + Sparrow.
        Assert.Equal(3, GroupCount()); // baseline from the constructor's own Rebuild

        // Captured so the assertions below can prove a rebuild ACTUALLY happened. Without
        // this the test could pass vacuously: if the bar never rebuilt (e.g. IsVisible false,
        // so the StateChanged subscription skips), the panel would still hold its original
        // three groups and the "not doubled" assertion would succeed while testing nothing.
        var firstGroupBefore = rows.Children[0];

        // ARM: the next DesktopOf call -- made from inside the OUTER rebuild's query --
        // delivers a window event, standing in for the WinEvent that a real COM call's
        // message pump used to let through. One-shot, so the nested rebuild it provokes
        // cannot recurse forever.
        desktops.PulseOnNextDesktopOf = () => monitor.Pump.OnNext(new WindowEvent(WindowEventKind.Appeared, rdm));

        // ACT: trigger the outer rebuild exactly as production does -- a window event
        // pulses StateChanged, whose subscription rebuilds the bar.
        monitor.Pump.OnNext(new WindowEvent(WindowEventKind.Appeared, beeper));

        // The rebuild really ran (see the capture above), and it produced ONE set of groups.
        // Before the fix this doubled: the nested rebuild's groups, then the outer rebuild's
        // appended after them.
        Assert.NotSame(firstGroupBefore, rows.Children[0]);
        Assert.Equal(3, GroupCount());

        bar.Close();
    });

    // Task 12 (spec: "always rendered, even when empty"). The 📌 row is the ONLY way to
    // pin a window from the bar -- dropping onto it calls PinWindow -- so hiding it while
    // nothing is pinned made the drop target unreachable and the feature impossible to
    // start using. Same trap fix round 6 removed for empty workspaces.
    [Fact]
    public void Pinned_row_is_rendered_even_when_nothing_is_pinned() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        Assert.Empty(harness.Desktops.PinnedWindows); // precondition: nothing pinned

        using var bar = harness.ShowBar();

        Assert.Contains("📌", Labels(bar.Rows));
    });

    // A window whose desktop the COM API cannot resolve MUST appear somewhere, or it becomes
    // unfindable: the Task 10 defect Petre reported as "i don't think i see windows in the
    // non-workspace section".
    //
    // This assertion is the REVERSE of what it was a few commits ago. Task 12 hid the
    // "Unplaced" row from the bar because the row is not actionable and the switcher panel
    // would still show it ("bar = actionable, panel = complete"). The panel and Manage's
    // Windows tab have both since been deleted, so the bar is the only surface left and hiding
    // the row would resurrect the original bug. The test flipped along with the reasoning
    // rather than being deleted, so the invariant stays enforced either way.
    [Fact]
    public void Unplaced_windows_still_appear_somewhere_which_is_now_the_bar() => StaThread.Run(() =>
    {
        var harness = Harness.Build(withUnresolvableWindow: true);

        using var bar = harness.ShowBar();

        Assert.Contains(Labels(bar.Rows), label => label.StartsWith("Unplaced"));
    });

    // Task 12: the menu is naming ONLY. Petre explicitly rejected "Send to ▸" and
    // "Pin / Unpin" here as redundant with drag, so this test fails if either creeps back in --
    // the exact list is the assertion, not incidental detail.
    //
    // Now four entries. "Rename all <app> windows…" was added after Petre's "i've renamed remote
    // desktop manager to RDP yesterday, today it's still the original name, why?": renaming THIS
    // window records the exact title it had, which RDM rewrites with its current session, so the
    // record can never match again. The app-wide entry writes a rule keyed on the process name
    // instead. "Rename by title pattern…" came later (#136): "i want two separate boxes - one for
    // the title wildcard, another for the new name." All of them are naming, so the rule this menu
    // follows -- drag expresses movement and pinning, right-click expresses naming -- still holds.
    //
    // "Name <app> windows by folder" is deliberately absent HERE and its own test covers why: the
    // harness window's process is "rdm", which is not a name TitleToken knows, and an app whose
    // title shape cannot be read has no folder to be named after.
    [Fact]
    public void Icon_right_click_menu_offers_naming_only() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();

        var menu = IconButtons(bar.Rows).First().ContextMenu;

        Assert.NotNull(menu);
        Assert.Equal(
            ["Rename this window…", "Rename by title pattern…", "Rename all rdm windows…", "Restore title"],
            menu.Items.OfType<MenuItem>().Select(item => (string)item.Header));
        // Never renamed, so Restore title is visible-but-unavailable rather than absent
        // (mirrors WindowGroupsView.RunningMenu; a menu whose shape shifts per icon is
        // harder to learn than one with a greyed entry).
        Assert.False(menu.Items.OfType<MenuItem>().Last().IsEnabled);
    });

    // Petre: "active window should be highlighted in the floating window". Asserts the fact
    // travels the whole way -- foreground event -> WorkspaceManager -> Overview.IsActive ->
    // the icon's own brushes -- and that only ONE icon is marked.
    [Fact]
    public void The_focused_windows_icon_is_the_only_one_highlighted() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();

        harness.Monitor.Pump.OnNext(new WindowEvent(WindowEventKind.Activated, harness.First));

        // Localises a failure: if this fails the fact never reached the model; if it passes
        // and the brush assertions below fail, the bar is not reflecting it.
        var overview = harness.Manager.WindowsByWorkspace().Value;
        Assert.Contains(overview.Workspaces.SelectMany(g => g.Running), row => row.IsActive);

        var icons = IconButtons(bar.Rows);
        var active = icons.Single(b => ((string)b.ToolTip).Contains(harness.First.Title));
        var inactive = icons.Single(b => ((string)b.ToolTip).Contains(harness.Second.Title));

        Assert.NotEqual(Brushes.Transparent, active.BorderBrush);
        Assert.Equal(Brushes.Transparent, inactive.BorderBrush);
        // Layout must not shift when the highlight moves, or the SizeToContent bar would
        // creep across the screen as focus changes.
        Assert.Equal(active.BorderThickness, inactive.BorderThickness);
    });

    // Petre: "i need to be able to move workspaces up or down in the manage window".
    //
    // Constructing the window is most of the value here: XAML is parsed at construction, so a
    // typo in a Click handler name or an x:Name only fails THEN -- which would otherwise mean
    // crashing the moment Petre opens Manage, with nothing having caught it. Asserting the
    // reorder buttons exist also stops them being quietly dropped in a later XAML edit.
    [Fact]
    public void The_manage_window_offers_workspace_reordering() => StaThread.Run(() =>
    {
        var harness = Harness.Build();

        var manage = new ManageWindow(harness.Manager, compatibilityMode: false);

        // Button.Content here is a raw string rather than a TextBlock, so these are collected
        // separately from the text labels.
        var buttons = ButtonLabels(manage);
        Assert.Contains("↑ Up", buttons);
        Assert.Contains("↓ Down", buttons);

        // There must be NO "Show floating bar" checkbox. Petre: "show floating bar doesn't make
        // sense anymore, it's crucial for the app's design" -- the bar is the only surface that
        // lists windows, so offering to switch it off both made no sense and left no way back.
        Assert.Null(manage.FindName("ShowFloatingBar"));

        manage.Close();
    });

    // Petre: "i'd prefer to be able to click on the empty row as well and it takes me to the
    // right place." The label used to be the only switch target -- a ~10px word at the right
    // end of the row, carrying the bar's second most common action.
    //
    // Sparrow rather than GEPHA throughout this group: GEPHA is the CURRENT workspace, whose
    // label is already drawn at full strength, so a hover assertion there could not tell
    // "highlight applied" from "nothing happened".
    [Fact]
    public void Clicking_a_rows_empty_area_switches_to_that_workspace() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        var sparrowDesktop = harness.Desktops.Desktops.Single(d => d.Name == "Sparrow").Id;
        Assert.Empty(harness.Desktops.Switched); // precondition: nothing switched yet

        var row = RowFor(bar.Rows, "Sparrow");
        Press(row);
        Release(row);

        Assert.Equal([sparrowDesktop], harness.Desktops.Switched);
    });

    // Petre, for the third time: "i still sometimes click on a workspace and it doesn't switch."
    //
    // ROOT CAUSE this pins, measured with a synthetic-input probe rather than reasoned about:
    // ButtonBase.OnMouseLeftButtonUp sets e.Handled = true UNCONDITIONALLY -- including when it
    // decides NOT to raise Click, which is every release whose press started somewhere else.
    // And MouseLeftButtonUp is a DIRECT routed event that WPF re-raises per element as the
    // bubbling MouseUp travels, so a handled release at an icon stops the row's own
    // MouseLeftButtonUp being raised AT ALL. The click vanished: no icon Click, no row switch.
    //
    // A row is mostly icons with a ~10px label at the right, so a couple of pixels of ordinary
    // drift between press and release crossed one of those edges and ate the click. That is the
    // whole of the "sometimes", and it was never the drag gesture -- moving the bar onto
    // Ctrl+drag left this untouched, which is exactly what Petre observed.
    //
    // WHAT THIS COVERS, stated exactly, because half the gestures cannot be reached from here:
    //
    // The probe found four dead gestures. Two of them -- press blank row, release over a child --
    // are pure routing and reproduce faithfully below. The other two -- press a child, drift off
    // it -- fail in production because the Button took real MOUSE CAPTURE on press, so the
    // release is routed to the BUTTON wherever the cursor actually is. Nothing raised through
    // RaiseEvent can reproduce that: capture is consulted by the input manager when it builds
    // the route, not by RaiseEvent, so a synthesised release simply goes where it is aimed.
    // Writing those two as tests would have produced two that passed no matter what the bar did.
    //
    // They are not a separate defect and need no separate cover: all four are one Handled flag
    // set by one ButtonBase, and the fix is the row listening on the bubbling MouseUp, which the
    // capture route also travels. The two below fail without that fix and pass with it.
    [Theory]
    [InlineData(Where.Icon)]  // pressed blank row, drifted onto an icon
    [InlineData(Where.Label)] // pressed blank row, drifted onto the label
    public void A_click_released_over_a_rows_icon_or_label_still_switches(Where releasedOver) => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        var sparrowDesktop = harness.Desktops.Desktops.Single(d => d.Name == "Sparrow").Id;
        var row = RowFor(bar.Rows, "Sparrow");

        Press(row);
        Release(Target(row, releasedOver));

        Assert.Equal([sparrowDesktop], harness.Desktops.Switched);
    });

    // ...and the counterpart that must NOT regress: a clean press-and-release on an icon is a
    // jump to that WINDOW, and must not ALSO be read as a click on the row behind it. This is
    // why the row handler cannot simply fire on every release that reaches it -- it has to know
    // a child already consumed the press.
    //
    // JumpTo switches to the window's desktop on its way to the window, so the expected count is
    // one, not zero. Two would mean the row fired as well.
    [Fact]
    public void A_clean_icon_click_jumps_to_the_window_without_also_switching_the_row() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        var icon = IconButtons(RowFor(bar.Rows, "Sparrow")).Single();

        Press(icon);
        Release(icon);

        Assert.Single(harness.Desktops.Switched);
    });

    // Where in a row a release lands. Used by the theory above.
    public enum Where { Bare, Icon, Label }

    static UIElement Target(Grid row, Where where) => where switch
    {
        Where.Icon => IconButtons(row).Single(),
        // The label Button: the only Button in the row that is not tagged as an icon.
        Where.Label => Buttons(row).Single(b => b is not { Tag: "icon" }),
        _ => row,
    };

    // The 📌 Pinned row has no destination -- pinned windows are on every workspace by
    // definition -- and neither does the "Unplaced" catch-all. Those rows must stay inert, or
    // the highlight stops meaning "click and you go there".
    [Fact]
    public void A_row_with_no_destination_ignores_a_click() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();

        var pinned = RowFor(bar.Rows, "📌");
        Press(pinned);
        Release(pinned);

        Assert.Empty(harness.Desktops.Switched);
    });

    // Hover feedback is the LABEL brightening and nothing else: the row background already
    // means "a dragged window will land here" (DropHighlight), so it cannot also mean hover.
    //
    // What this test does and does not cover, stated plainly: MouseEnter/MouseLeave are DIRECT
    // routed events, so raising them proves the wiring and the opacity arithmetic without a
    // rendered window. It cannot prove the hit-test REGIONS are the intended ones -- that
    // blank row area really raises the container's MouseEnter rather than some child's. That
    // half is verified by looking at the running bar.
    [Fact]
    public void Hovering_a_rows_empty_area_brightens_its_label_and_releases_it_on_leave() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        var row = RowFor(bar.Rows, "Sparrow");
        var label = TextBlocks(row).Single(t => t.Text == "Sparrow");
        var resting = Strength(label);

        row.RaiseEvent(MouseEnter());
        var hovered = Strength(label);

        row.RaiseEvent(MouseLeave());

        Assert.True(hovered > resting, $"hover should brighten the label; was {resting}, hovered {hovered}");
        // Rises to exactly the strength the CURRENT row is drawn at, and no further: the two
        // values are shared through one brush precisely so hover cannot drift past "current".
        Assert.Equal(Strength(TextBlocks(RowFor(bar.Rows, "GEPHA")).Single(t => t.Text == "GEPHA")), hovered);
        Assert.Equal(resting, Strength(label)); // restored, not left stuck bright
        // Weight never moves -- and now it never moves for ANY reason, not just hover. Petre:
        // "let's make all text bold and only change its color if it's active", because a bold
        // current row measured wider and the SizeToContent bar resized on every switch.
        Assert.Equal(FontWeights.Bold, label.FontWeight);
    });

    // Clicking an icon jumps to a WINDOW, not to a workspace, so the icons punch holes in the
    // row's hover area -- lighting the label there would advertise an action the click does
    // not perform. Note the inversion this pins: entering an icon CLEARS a highlight the
    // container's own MouseEnter has already set, because entering a child counts as entering
    // the parent.
    [Fact]
    public void Hovering_an_icon_does_not_brighten_its_rows_label() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        var row = RowFor(bar.Rows, "Sparrow");
        var label = TextBlocks(row).Single(t => t.Text == "Sparrow");
        var resting = Strength(label);

        row.RaiseEvent(MouseEnter());                              // into the row: bright
        IconButtons(row).Single().RaiseEvent(MouseEnter());         // onto its icon: back to rest

        Assert.Equal(resting, Strength(label));
    });

    // Petre: "when switching workspaces, because the caption of the workspace gets bolded, it
    // increases the width of the floating window a little if that workspace is full, which is a
    // little bad."
    //
    // The bar is SizeToContent, so anything that differs BETWEEN the current row and the others
    // and participates in measure moves the whole window on every switch. This pins the fix at
    // the level the bug actually lived at -- not "is it bold" but "does current-ness cost width
    // anywhere" -- so a future marker that reintroduces the problem fails here rather than in
    // Petre's peripheral vision.
    //
    // Colour is exempt by construction: a brush cannot change a measure. That is the whole
    // reason the state moved into brushes.
    [Fact]
    public void Being_the_current_workspace_costs_no_width() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        var current = TextBlocks(RowFor(bar.Rows, "GEPHA")).Single(t => t.Text == "GEPHA");
        var other = TextBlocks(RowFor(bar.Rows, "Sparrow")).Single(t => t.Text == "Sparrow");

        Assert.Equal(other.FontWeight, current.FontWeight);
        Assert.Equal(other.FontSize, current.FontSize);
        Assert.Equal(other.Margin, current.Margin);

        // ...and the same for the box drawn around each ROW, which is where the current-workspace
        // marker moved to (Petre: "when looking at the left edge, i can't really see what's
        // active" -- the pill this replaced sat in the right-hand gutter). Present on both rows
        // at the same thickness and corner radius, differing only in the brush.
        var currentRow = RowBorderFor(bar.Rows, "GEPHA");
        var otherRow = RowBorderFor(bar.Rows, "Sparrow");
        Assert.Equal(otherRow.BorderThickness, currentRow.BorderThickness);
        Assert.Equal(otherRow.CornerRadius, currentRow.CornerRadius);
        Assert.Equal(otherRow.Margin, currentRow.Margin);
        Assert.NotEqual(Strength(otherRow.BorderBrush), Strength(currentRow.BorderBrush));
    });

    // The current-row state lives entirely in brush ALPHA now, so "brighter" is a number these
    // tests can compare rather than something only an eye can judge.
    static byte Strength(TextBlock label) => Strength(label.Foreground);

    static byte Strength(Brush brush) => ((SolidColorBrush)brush).Color.A;

    // Petre: "i want a go back to previous button... basically the same as ctrl+win+tab tap
    // once, without the kb."
    //
    // The harness starts on GEPHA with Sparrow never visited this session, so Ordered is
    // [GEPHA, Sparrow] with CurrentIndex 0 and one step forward is Sparrow.
    [Fact]
    public void The_back_button_switches_to_the_workspace_one_tap_away() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        var back = (Button)bar.Bar.FindName("BackButton")!;
        var sparrowDesktop = harness.Desktops.Desktops.Single(d => d.Name == "Sparrow").Id;

        // Names the destination, because the glyph cannot and the info line's text is
        // overwritten on icon hover.
        Assert.True(back.IsEnabled);
        Assert.Equal("Back to Sparrow", back.ToolTip);

        back.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        Assert.Equal([sparrowDesktop], harness.Desktops.Switched);
    });

    // Dimmed-and-disabled rather than absent, following the ruling the icon context menu
    // already follows for a greyed "Restore title": a surface whose shape shifts is harder to
    // learn than one with a visibly unavailable control.
    [Fact]
    public void The_back_button_is_disabled_when_there_is_nowhere_to_go() => StaThread.Run(() =>
    {
        var harness = Harness.Build(singleWorkspace: true);
        using var bar = harness.ShowBar();
        var back = (Button)bar.Bar.FindName("BackButton")!;

        Assert.False(back.IsEnabled);
        Assert.Equal("Nowhere to go back to", back.ToolTip);
        Assert.True(back.Opacity < 1.0);
    });

    // Synthesised input, and the choice of EVENT here is the whole point rather than a detail.
    //
    // MouseLeftButtonDown/Up are DIRECT routed events. They do not travel: WPF raises the
    // bubbling Mouse.MouseUp (or tunnelling Mouse.PreviewMouseDown) along the real route and
    // each element on it "cracks" that into a Direct MouseLeftButtonUp raised on ITSELF -- and
    // skips the crack once the event is handled. Raising MouseLeftButtonUpEvent straight at the
    // row (which these tests used to do) therefore bypasses the very mechanism the straddle bug
    // lives in, and would pass no matter how broken the bar was.
    //
    // So press and release are injected as the raw bubbling/tunnelling events, exactly as the
    // input manager delivers them, and every crack, every ButtonBase class handler and every
    // Handled flag along the way is the real one.
    // Both halves of a press, in the order the input manager delivers them: the tunnelling
    // preview (which is where FloatingBar arms the gesture) and then the bubbling MouseDown
    // (which is where ButtonBase takes capture and sets IsPressed, so a Button pressed here
    // really does raise Click on release).
    static void Press(UIElement target)
    {
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent,
        });
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.MouseDownEvent,
        });
    }

    static void Release(UIElement target) =>
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.MouseUpEvent,
        });

    static MouseEventArgs MouseEnter() => new(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent };
    static MouseEventArgs MouseLeave() => new(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseLeaveEvent };

    // The row container for a given label. GroupRow builds one Grid per group, added directly
    // to the Rows panel, so the group's Grid is the one holding a TextBlock with that text.
    // Every row is a Border wrapping its Grid now: the current workspace is marked by a box
    // around the whole row, and that border has to exist on every row (transparent when not
    // current) or the SizeToContent bar would resize on each switch. Separators are Bordered
    // too, but childless, so asking for the Grid inside filters them out.
    // #120. Petre: "when resizing the floating window in width, icons should immediately flow to the
    // second row, instead of waiting for the resize to finish and then do it, so i can determine the
    // correct width."
    //
    // Driven by setting Width rather than by a mouse: that is what a resize drag does on every mouse
    // move, and SizeChanged does not care who moved it. So the behaviour is testable without SendInput.
    [Fact]
    public void Narrowing_the_bar_re_wraps_its_icons_at_once() => StaThread.Run(() =>
    {
        var harness = Harness.Build(busyWorkspace: true);
        using var bar = harness.ShowBar();

        // Six icons, no explicit width: the fixed five-per-line rule, so one break.
        Assert.Equal(2, IconLines(bar.Rows, "Sparrow"));

        // Now a width that leaves room for about four. 170 = 58 caption + 4 ring + 8 padding + ~100 of
        // icons, and the three-icon floor means the break lands after the fourth.
        bar.Bar.ApplyWidth(170); // the same call a resize drag makes on every mouse move
        // SizeChanged is raised by a LAYOUT pass, not by the assignment: WPF defers layout to the
        // dispatcher, and nothing is pumping one here. The app gets that pass for free on every mouse
        // move; a test has to ask.
        bar.Bar.UpdateLayout();

        Assert.Equal(2, IconLines(bar.Rows, "Sparrow"));
        Assert.Equal([4, 2], LineWidths(bar.Rows, "Sparrow"));
    });

    // ...and it does it WITHOUT asking the OS anything, which is the whole reason it is a separate path
    // from Rebuild. Measured on Petre's machine, one rebuild is 160-240ms and the query is nearly all of
    // it: at 60 to 125 mouse moves a second, re-querying would make the drag unusable.
    [Fact]
    public void A_re_wrap_asks_the_desktops_nothing() => StaThread.Run(() =>
    {
        var harness = Harness.Build(busyWorkspace: true);
        using var bar = harness.ShowBar();

        var queriesBefore = harness.Desktops.DesktopOfCalls;
        Assert.True(queriesBefore > 0); // precondition: the first draw DID query

        bar.Bar.ApplyWidth(170); // the same call a resize drag makes on every mouse move
        // SizeChanged is raised by a LAYOUT pass, not by the assignment: WPF defers layout to the
        // dispatcher, and nothing is pumping one here. The app gets that pass for free on every mouse
        // move; a test has to ask.
        bar.Bar.UpdateLayout();

        Assert.Equal([4, 2], LineWidths(bar.Rows, "Sparrow"));
        Assert.Equal(queriesBefore, harness.Desktops.DesktopOfCalls);
    });

    // A row press must still stand its ground: a re-layout throws every row away, and the pressed
    // Button with it, which is #48's whole mechanism. A resize press lands on the grip and sets no
    // pressed row, so the two cases are told apart rather than assumed to be the same.
    [Fact]
    public void A_width_change_stands_down_while_a_row_is_pressed() => StaThread.Run(() =>
    {
        var harness = Harness.Build(busyWorkspace: true);
        using var bar = harness.ShowBar();
        var row = RowFor(bar.Rows, "Sparrow");

        Press(row); // as if the press had begun on the row itself
        bar.Bar.ApplyWidth(170); // the same call a resize drag makes on every mouse move
        // SizeChanged is raised by a LAYOUT pass, not by the assignment: WPF defers layout to the
        // dispatcher, and nothing is pumping one here. The app gets that pass for free on every mouse
        // move; a test has to ask.
        bar.Bar.UpdateLayout();

        // Unchanged: still the five-per-line split, because no re-layout ran.
        Assert.Equal([5, 1], LineWidths(bar.Rows, "Sparrow"));
    });

    // The vertical stack of icon lines in one row: one child per line.
    static Panel IconStack(Panel rows, string label) =>
        RowFor(rows, label).Children.OfType<StackPanel>()
            .Single(panel => panel.Orientation == Orientation.Vertical);

    static int IconLines(Panel rows, string label) => IconStack(rows, label).Children.Count;

    static IReadOnlyList<int> LineWidths(Panel rows, string label) =>
        IconStack(rows, label).Children.OfType<DependencyObject>()
            .Select(line => IconButtons(line).Count)
            .ToList();

    static Grid RowFor(Panel rows, string label) =>
        rows.Children.OfType<Border>()
            .Select(b => b.Child)
            .OfType<Grid>()
            .Single(row => TextBlocks(row).Any(t => t.Text == label));

    static Border RowBorderFor(Panel rows, string label) =>
        rows.Children.OfType<Border>()
            .Single(b => b.Child is Grid g && TextBlocks(g).Any(t => t.Text == label));

    // The bar tags every icon Button so its press-drag moves the WINDOW rather than the
    // bar; the literal matches FloatingBar's private IconTag const.
    static IReadOnlyList<Button> IconButtons(DependencyObject root)
    {
        var found = new List<Button>();
        Collect(root);
        return found;

        void Collect(DependencyObject node) =>
            LogicalTreeHelper.GetChildren(node)
                .OfType<DependencyObject>()
                .ToList()
                .ForEach(child =>
                {
                    if (child is Button { Tag: "icon" } button) found.Add(button);
                    Collect(child);
                });
    }

    // Every Button in the logical subtree, icons and labels alike.
    static IReadOnlyList<Button> Buttons(DependencyObject root)
    {
        var found = new List<Button>();
        Collect(root);
        return found;

        void Collect(DependencyObject node) =>
            LogicalTreeHelper.GetChildren(node)
                .OfType<DependencyObject>()
                .ToList()
                .ForEach(child =>
                {
                    if (child is Button button) found.Add(button);
                    Collect(child);
                });
    }

    // Every Button in the logical subtree whose Content is a plain string.
    static IReadOnlyList<string> ButtonLabels(DependencyObject root)
    {
        var found = new List<string>();
        Collect(root);
        return found;

        void Collect(DependencyObject node) =>
            LogicalTreeHelper.GetChildren(node)
                .OfType<DependencyObject>()
                .ToList()
                .ForEach(child =>
                {
                    if (child is Button { Content: string label }) found.Add(label);
                    Collect(child);
                });
    }

    static IReadOnlyList<string> Labels(DependencyObject root) =>
        TextBlocks(root).Select(text => text.Text).ToList();

    // Every TextBlock in the logical subtree, flattened. The logical tree (not the visual
    // one) because these elements are constructed but never rendered, so Button templates
    // are not expanded -- a Button's Content is a logical child regardless.
    static IReadOnlyList<TextBlock> TextBlocks(DependencyObject root)
    {
        var found = new List<TextBlock>();
        Collect(root);
        return found;

        void Collect(DependencyObject node) =>
            LogicalTreeHelper.GetChildren(node)
                .OfType<DependencyObject>()
                .ToList()
                .ForEach(child =>
                {
                    if (child is TextBlock text) found.Add(text);
                    Collect(child);
                });
    }
}

// Shared arrange for the bar tests: two workspaces on two desktops, one placed window
// each, optionally a third window whose desktop the (stub) COM layer cannot resolve.
sealed class Harness
{
    public required PulsingDesktops Desktops { get; init; }
    public required StubMonitor Monitor { get; init; }
    public required WorkspaceManager Manager { get; init; }
    public required WindowInfo First { get; init; }
    public required WindowInfo Second { get; init; }

    // singleWorkspace: the only state in which the bar's back button has nowhere to go -- one
    // workspace, and you are standing on it. Sparrow and its window are simply left out rather
    // than a second fixture being written, so the two paths share every other detail.
    // busyWorkspace: five extra windows in Sparrow, so its row has something to WRAP. One window per
    // row is enough for every other test here and cannot exercise a line break at all.
    public static Harness Build(bool withUnresolvableWindow = false, bool singleWorkspace = false,
        bool busyWorkspace = false)
    {

        // NO Application is created here, deliberately. An Application belongs to the thread
        // that constructs it, StaThread gives every test a FRESH STA thread, and only one
        // Application may exist per process -- so the first test would own it and every later
        // test would build its window on a foreign dispatcher, where Show() does not make
        // IsVisible true. That silently disabled the bar's StateChanged subscription (which
        // guards on IsVisible) and made these tests order-dependent: the highlight test
        // passed alone and failed in a full run. Application.LoadComponent resolves a
        // Window's XAML from the assembly's resources without any Application instance.
        var gephaDesktop = Guid.NewGuid();
        var sparrowDesktop = Guid.NewGuid();
        var gepha = new Workspace(Guid.NewGuid(), "GEPHA", gephaDesktop);
        var sparrow = new Workspace(Guid.NewGuid(), "Sparrow", sparrowDesktop);

        var desktops = new PulsingDesktops { CurrentId = gephaDesktop };
        desktops.Desktops.Add(new DesktopInfo(gephaDesktop, "GEPHA"));
        if (!singleWorkspace) desktops.Desktops.Add(new DesktopInfo(sparrowDesktop, "Sparrow"));

        var rdm = new WindowInfo(new WindowHandle(101), 11, "rdm", @"C:\rdm.exe", "Remote Desktop Manager", null);
        var beeper = new WindowInfo(new WindowHandle(202), 22, "beeper", @"C:\beeper.exe", "Beeper", null);
        desktops.Placements[rdm.Handle] = gephaDesktop;
        desktops.Placements[beeper.Handle] = sparrowDesktop;

        var monitor = new StubMonitor();
        monitor.Initial.Add(rdm);
        if (!singleWorkspace) monitor.Initial.Add(beeper);

        if (busyWorkspace)
            Enumerable.Range(1, 5).ToList().ForEach(i =>
            {
                var extra = new WindowInfo(new WindowHandle(400 + i), 40 + i, $"app{i}", $@"C:pp{i}.exe", $"App {i}", null);
                desktops.Placements[extra.Handle] = sparrowDesktop;
                monitor.Initial.Add(extra);
            });

        if (withUnresolvableWindow)
            // Deliberately absent from Placements, so DesktopOf fails for it exactly as
            // VirtualDesktop.FromHwnd does for "Windows Input Experience" on Petre's box.
            monitor.Initial.Add(new WindowInfo(new WindowHandle(303), 33, "textinputhost", @"C:\tih.exe", "Windows Input Experience", null));

        var store = new StubStore
        {
            Stored = AppState.Empty with { Workspaces = singleWorkspace ? [gepha] : [gepha, sparrow] },
        };
        var manager = new WorkspaceManager(desktops, monitor, new StubTitles(), store);
        Assert.True(manager.Start().IsSuccess);

        return new Harness { Desktops = desktops, Monitor = monitor, Manager = manager, First = rdm, Second = beeper };
    }

    // Parked far off the virtual screen so the suite never flashes a translucent topmost
    // bar at whoever is running it; WPF still reports it visible, which the bar's
    // live-refresh subscription requires.
    public BarUnderTest ShowBar()
    {
        var bar = new FloatingBar(Manager) { Left = -32000, Top = -32000 };
        bar.Show();
        return new BarUnderTest(bar);
    }
}

// Keeps the Rows panel handy and closes the window when the test ends.
sealed class BarUnderTest(FloatingBar bar) : IDisposable
{
    public FloatingBar Bar { get; } = bar;
    public Panel Rows { get; } = (Panel)bar.FindName("Rows")!;
    public void Dispose() => Bar.Close();
}

// Desktop-service stub whose DesktopOf can fire a caller-supplied action, letting a test
// inject an event into the middle of WorkspaceManager.WindowsByWorkspace().
sealed class PulsingDesktops : IVirtualDesktopService
{
    public List<DesktopInfo> Desktops { get; } = [];
    public Dictionary<WindowHandle, Guid> Placements { get; } = [];
    public HashSet<WindowHandle> PinnedWindows { get; } = [];
    public Guid CurrentId { get; set; }

    // One-shot: cleared the moment it fires.
    public Action? PulseOnNextDesktopOf { get; set; }

    public Result Initialize() => Result.Success();
    public Result<IReadOnlyList<DesktopInfo>> GetDesktops() => Result.Success<IReadOnlyList<DesktopInfo>>(Desktops.ToList());

    public Result<DesktopInfo> Create(string name)
    {
        var created = new DesktopInfo(Guid.NewGuid(), name);
        Desktops.Add(created);
        return created;
    }

    public Result Rename(Guid desktopId, string name) => Result.Success();
    public Result Remove(Guid desktopId) => Result.Success();

    // Recorded rather than merely succeeding: the row-click tests assert on WHICH desktop a
    // click reached, and that an inert row reached none.
    public List<Guid> Switched { get; } = [];

    public Result Switch(Guid desktopId)
    {
        Switched.Add(desktopId);
        CurrentId = desktopId;
        return Result.Success();
    }

    public Result MoveWindow(WindowHandle window, Guid desktopId)
    {
        Placements[window] = desktopId;
        return Result.Success();
    }

    // Counted because #120 turns on it: a re-layout during a resize must draw from the overview it
    // already has, and the only way to assert "no query happened" is to count the calls the query makes.
    public int DesktopOfCalls { get; private set; }

    public Result<Guid> DesktopOf(WindowHandle window)
    {
        DesktopOfCalls++;
        var pulse = PulseOnNextDesktopOf;
        PulseOnNextDesktopOf = null;
        pulse?.Invoke();
        return Placements.TryGetValue(window, out var id) ? id : Result.Failure<Guid>("not placed");
    }

    public Result Pin(WindowHandle window)
    {
        PinnedWindows.Add(window);
        return Result.Success();
    }

    public Result Unpin(WindowHandle window)
    {
        PinnedWindows.Remove(window);
        return Result.Success();
    }

    public Result<bool> IsPinned(WindowHandle window) => PinnedWindows.Contains(window);
    public Result<Guid> CurrentDesktop() => CurrentId;
    public IObservable<Guid> CurrentChanged { get; } = new Subject<Guid>();
}

sealed class StubMonitor : IWindowMonitor
{
    public Subject<WindowEvent> Pump { get; } = new();
    public List<WindowInfo> Initial { get; } = [];
    public Maybe<WindowHandle> ForegroundWindow { get; set; } = Maybe<WindowHandle>.None;
    public Result Start() => Result.Success();
    public IObservable<WindowEvent> Events => Pump;
    public IReadOnlyList<WindowInfo> Snapshot() => Initial.ToList();
    public Maybe<WindowHandle> Foreground() => ForegroundWindow;
}

sealed class StubTitles : IWindowTitles
{
    readonly Dictionary<WindowHandle, string> titles = [];

    public Result Set(WindowHandle window, string title)
    {
        titles[window] = title;
        return Result.Success();
    }

    public Result<string> Get(WindowHandle window) => titles.TryGetValue(window, out var t) ? t : "";
}

sealed class StubStore : IPersistenceStore
{
    public AppState Stored { get; set; } = AppState.Empty;
    public Result<AppState> Load() => Stored;

    public Result Save(AppState state)
    {
        Stored = state;
        return Result.Success();
    }
}
