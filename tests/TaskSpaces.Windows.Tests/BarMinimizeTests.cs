using System.Windows;
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
        var asks = 0;
        standIn.RestoreRequested += () => asks++;

        standIn.WindowState = WindowState.Normal;
        standIn.Close();

        Assert.Equal(1, asks);
    });
}
