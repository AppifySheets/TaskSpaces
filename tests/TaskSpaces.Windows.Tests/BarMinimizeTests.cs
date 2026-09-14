using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Interop;
using TaskSpaces.App;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Windows.Monitoring;
using TaskSpaces.Core;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using Xunit.Abstractions;

namespace TaskSpaces.Windows.Tests;

// Petre: "i want ability to minimize the floating bar, which gets minimized in every workspace as an
// item in the taskbar" / "should open in current"... and, asked where the control belongs, all three
// places: the bar's own menus, a button on the bar, and the tray.
//
// The half worth testing hardest is not that the bar hides. It is that the bar's HWND SURVIVES.
// Minimizing looks like a job for ShowInTaskbar, and WPF recreates a window's handle when that
// property changes -- which would silently invalidate the handle WindowMonitor.Ignore was given at
// startup and the one ReclaimTopmost pokes every second, so the bar would begin listing itself in
// its own rows. A separate stand-in window exists precisely to avoid that, and
// The_bars_handle_survives_a_full_cycle is what stops anyone "simplifying" it back.
public class BarMinimizeTests(ITestOutputHelper output)
{
    // Parked off the virtual screen like every other bar test here, so the suite never flashes a
    // translucent topmost bar at whoever is running it.
    static FloatingBar Bar(WorkspaceManager manager) =>
        new(manager) { Left = -32000, Top = -32000 };

    static WorkspaceManager Started(StubStore store)
    {
        var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
        desktops.Desktops.Add(new DesktopInfo(desktops.CurrentId, "Work"));
        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), store);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    // --- the bar's side of it --------------------------------------------------------

    [Fact]
    public void Minimizing_hides_the_bar_and_showing_brings_it_back() => StaThread.Run(() =>
    {
        var bar = Bar(Started(new StubStore()));
        bar.ShowBar();
        Assert.True(bar.IsVisible);

        bar.MinimizeToTaskbar();
        Assert.False(bar.IsVisible);
        Assert.True(bar.Minimized);

        bar.ShowBar();
        Assert.True(bar.IsVisible);
        Assert.False(bar.Minimized);

        bar.Close();
    });

    // THE trap this design exists to dodge. Same handle before, during and after: anything that
    // reached for ShowInTaskbar on the bar itself would fail here.
    [Fact]
    public void The_bars_handle_survives_a_full_cycle() => StaThread.Run(() =>
    {
        var bar = Bar(Started(new StubStore()));
        var handle = new WindowInteropHelper(bar).EnsureHandle();
        bar.ShowBar();

        bar.MinimizeToTaskbar();
        var whileMinimized = new WindowInteropHelper(bar).Handle;
        bar.ShowBar();
        var afterRestore = new WindowInteropHelper(bar).Handle;

        output.WriteLine($"start={handle:X} minimized={whileMinimized:X} restored={afterRestore:X}");
        Assert.Equal(handle, whileMinimized);
        Assert.Equal(handle, afterRestore);

        bar.Close();
    });

    // Persisted the moment it happens, not at exit: a kill or a crash has to leave the state he
    // actually chose. Visible is the field that was written-and-ignored for years, and this is what
    // brings it back to life.
    [Fact]
    public void The_minimized_state_is_persisted_as_not_visible() => StaThread.Run(() =>
    {
        var store = new StubStore();
        var bar = Bar(Started(store));
        bar.ShowBar();
        Assert.True(store.Stored.FloatingBar!.Visible);

        bar.MinimizeToTaskbar();
        Assert.False(store.Stored.FloatingBar!.Visible);

        bar.ShowBar();
        Assert.True(store.Stored.FloatingBar!.Visible);

        bar.Close();
    });

    // Starting up already minimized must not WRITE anything. The bar is SizeToContent, so before the
    // first layout pass its width is 0 and the right/bottom anchors computed from it are nonsense;
    // saving here would replace the position Petre chose with one derived from a window that was
    // never laid out.
    [Fact]
    public void Adopting_the_minimized_state_at_startup_does_not_touch_the_remembered_position()
        => StaThread.Run(() =>
    {
        var store = new StubStore
        {
            Stored = AppState.Empty with { FloatingBar = new FloatingBarState(1234, 567, false) },
        };
        var bar = Bar(Started(store));

        bar.AdoptMinimizedState();

        Assert.True(bar.Minimized);
        Assert.Equal(1234, store.Stored.FloatingBar!.Left);
        Assert.Equal(567, store.Stored.FloatingBar!.Top);
        Assert.False(store.Stored.FloatingBar!.Visible);

        bar.Close();
    });

    // All three gestures raise ONE event, so App has one restore path to keep correct. The button is
    // the one that has to be found by name, since it lives in the bar's chrome rather than in a menu.
    [Fact]
    public void The_button_on_the_bar_asks_to_be_minimized() => StaThread.Run(() =>
    {
        var bar = Bar(Started(new StubStore()));
        bar.ShowBar();
        var asks = 0;
        bar.MinimizeRequested += () => asks++;

        var button = (Button)bar.FindName("MinimizeButton")!;
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

        Assert.Equal(1, asks);
        bar.Close();
    });

    // --- the stand-in ----------------------------------------------------------------

    // What makes a taskbar button possible at all: on the taskbar, and NOT a tool window, which is
    // the flag the bar itself carries to stay off it.
    [Fact]
    public void The_stand_in_is_a_taskbar_window_rather_than_a_tool_window() => StaThread.Run(() =>
    {
        var standIn = new BarStandIn();
        var hwnd = standIn.EnsureHandle();
        standIn.Show();
        standIn.MinimizeToButton(); // the caller pins between these two, which a test cannot do

        var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        output.WriteLine($"stand-in ex-style 0x{(long)ex:X8}");

        Assert.True(standIn.ShowInTaskbar);
        Assert.Equal(0, (long)(ex & (nint)NativeMethods.WS_EX_TOOLWINDOW));
        Assert.Equal(WindowState.Minimized, standIn.WindowState);
        Assert.False(standIn.Topmost); // a topmost window nobody can see is a window that steals clicks

        standIn.Close();
    });

    // Clicking the taskbar button asks Windows to restore the stand-in. It must refuse, and ask for
    // the bar instead: a stand-in that actually restored would flash its own panel on the way out.
    [Fact]
    public void Restoring_the_stand_in_asks_for_the_bar_and_stays_minimized() => StaThread.Run(() =>
    {
        var standIn = new BarStandIn();
        standIn.EnsureHandle();
        standIn.Show();
        standIn.MinimizeToButton();
        var asks = 0;
        standIn.RestoreRequested += () => asks++;

        standIn.WindowState = WindowState.Normal;

        Assert.Equal(1, asks);
        Assert.Equal(WindowState.Minimized, standIn.WindowState);

        standIn.Close();
    });

    // The X on the taskbar thumbnail restores rather than exits, which is what Petre picked: Exit
    // lives in the tray, and a taskbar X that killed the app would put quitting one stray click away
    // from a button whose whole purpose is to be clicked.
    [Fact]
    public void Closing_the_stand_in_asks_for_the_bar_rather_than_exiting() => StaThread.Run(() =>
    {
        var standIn = new BarStandIn();
        standIn.EnsureHandle();
        standIn.Show();
        standIn.MinimizeToButton();
        var asks = 0;
        standIn.RestoreRequested += () => asks++;

        standIn.Close();

        Assert.Equal(1, asks);
        Assert.True(standIn.IsClosing); // so App does not call Close() on a window already closing
    });

    // One restore per stand-in, however many gestures arrive: a click can produce both a state
    // change and a close, and App answers this event by showing the bar and disposing the window.
    [Fact]
    public void Repeated_gestures_ask_only_once() => StaThread.Run(() =>
    {
        var standIn = new BarStandIn();
        standIn.EnsureHandle();
        standIn.Show();
        standIn.MinimizeToButton();
        var asks = 0;
        standIn.RestoreRequested += () => asks++;

        standIn.WindowState = WindowState.Normal;
        standIn.Close();

        Assert.Equal(1, asks);
    });

    // --- where the two buttons live (#173, third round) ------------------------------
    //
    // Petre: "move those buttons down next to the pin icon; remove the pin icon and have those
    // buttons at the right."
    //
    // So they sit in the PINNED ROW's caption cell, which is the rightmost column of every row and
    // is where that row used to draw a pin glyph. Asserted through the tree rather than by
    // coordinates: a position test would pass on a bar whose strip had drifted back out into one of
    // its own.
    [Fact]
    public void Both_buttons_sit_in_the_pinned_rows_caption_cell() => StaThread.Run(() =>
    {
        var bar = Bar(Started(new StubStore()));
        bar.ShowBar();

        var back = (Button)bar.FindName("BackButton")!;
        var minimize = (Button)bar.FindName("MinimizeButton")!;
        var strip = (Panel)back.Parent;

        Assert.Same(strip, minimize.Parent); // one strip, so the two cannot drift apart

        // Up from the strip to the row that owns it, and that row must be the pinned one.
        var row = Ancestors(strip).OfType<Grid>().First(g =>
            System.Windows.Automation.AutomationProperties.GetName(g).Length > 0);
        Assert.Equal("Pinned", System.Windows.Automation.AutomationProperties.GetName(row));

        // In the caption COLUMN, which is the fixed gutter on the right that every row shares.
        Assert.Equal(1, Grid.GetColumn(strip));

        // ...and inside the rows panel, which is what tells the reparenting actually happened: the
        // strip is declared in XAML as a sibling of Rows and is moved in on every rebuild.
        Assert.Contains((Panel)bar.FindName("Rows")!, Ancestors(strip).OfType<Panel>());

        bar.Close();
    });

    static IEnumerable<DependencyObject> Ancestors(DependencyObject from)
    {
        for (var at = LogicalTreeHelper.GetParent(from); at is not null; at = LogicalTreeHelper.GetParent(at))
            yield return at;
    }

    // The pin glyph is gone with it: the row is still the pinned one to everything that matters, and
    // says so through the accessible name every row now carries.
    [Fact]
    public void The_pinned_row_keeps_its_name_without_a_glyph() => StaThread.Run(() =>
    {
        var bar = Bar(Started(new StubStore()));
        bar.ShowBar();

        var rows = (Panel)bar.FindName("Rows")!;
        var pinned = rows.Children.OfType<Border>().Select(b => b.Child).OfType<Grid>()
            .Single(g => System.Windows.Automation.AutomationProperties.GetName(g) == "Pinned");

        Assert.DoesNotContain("📌", Texts(pinned));

        bar.Close();
    });

    static IReadOnlyList<string> Texts(DependencyObject root)
    {
        var found = new List<string>();
        Collect(root);
        return found;

        void Collect(DependencyObject node) =>
            LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().ToList().ForEach(child =>
            {
                if (child is TextBlock { Text: { } text }) found.Add(text);
                Collect(child);
            });
    }

    // Petre, with the two buttons blown up: "these two aren't really aligned to one another, do
    // align them."
    //
    // Measured as INK rather than as boxes, which is the whole point: both buttons are the same size,
    // the same font and the same VerticalAlignment, and they looked wrong anyway, because '▁' paints
    // on the baseline floor while '↩' paints mid line. A box comparison passes on the bug.
    [Fact]
    public void The_two_glyphs_share_a_centre_line() => StaThread.Run(() =>
    {
        var bar = Bar(Started(new StubStore()));
        bar.ShowBar();

        var back = InkCentre((Button)bar.FindName("BackButton")!);
        var minimize = InkCentre((Button)bar.FindName("MinimizeButton")!);
        output.WriteLine($"back={back:0.##} minimize={minimize:0.##} difference={Math.Abs(back - minimize):0.##}");

        // Half a DIP: tighter than anything an eye can see at this size, and loose enough that a font
        // fallback rounding differently does not fail the build.
        Assert.True(Math.Abs(back - minimize) < 0.5,
            $"the glyphs' ink centres are {Math.Abs(back - minimize):0.##} DIP apart");

        bar.Close();
    });

    // Where the glyph's ink actually sits inside the button, transform included -- the alignment is
    // paid for by a TranslateTransform, so a reading that ignored it would measure the wrong thing.
    static double InkCentre(Button button)
    {
        var text = new FormattedText((string)button.Content, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch),
            button.FontSize, Brushes.White, 96);
        var ink = text.BuildGeometry(new Point(0, 0)).Bounds;
        var shift = (button.RenderTransform as TranslateTransform)?.Y ?? 0;
        return (ink.Top + ink.Bottom) / 2 + shift;
    }

    // Petre: "that taskbar icon when i minimize the taskspaces app is kinda small, why?" Because a
    // plain BitmapImage takes the FIRST frame of a multi-frame .ico, and in this one that is 16x16 --
    // so the shell was magnifying a thumbnail onto a 32px or larger button.
    //
    // Asserted on the SIZE rather than on the DecodePixelWidth that produces it: the property is the
    // mechanism, the pixels are the requirement, and an .ico re-exported one day without its large
    // frames would pass a property check and still look small.
    [Fact]
    public void The_app_icon_is_decoded_big_enough_for_a_taskbar_button() => StaThread.Run(() =>
    {
        var icon = (System.Windows.Media.Imaging.BitmapSource)global::TaskSpaces.App.App.AppIcon;
        output.WriteLine($"app icon decodes to {icon.PixelWidth}x{icon.PixelHeight}");

        // 48 is the floor rather than the target: a 200% display asks for 64 on the taskbar, and the
        // file carries frames up to 256, so anything under this means the wrong frame was taken.
        Assert.True(icon.PixelWidth >= 48, $"the app icon decoded to {icon.PixelWidth}px");
        Assert.True(icon.IsFrozen); // shared across every window, so it must not take thread affinity
    });

    // The line that showed hovered titles, the idle hint and the row hint is gone outright. The
    // hover card already showed the window details beside the icon, which was the "two places
    // showing the same string" this file warned about long before it was deleted.
    [Fact]
    public void The_bar_has_no_title_line_any_more() => StaThread.Run(() =>
    {
        var bar = Bar(Started(new StubStore()));
        bar.ShowBar();

        Assert.Null(bar.FindName("Info"));

        bar.Close();
    });
}
