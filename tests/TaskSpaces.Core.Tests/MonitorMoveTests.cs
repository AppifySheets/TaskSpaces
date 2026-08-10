using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Tests;

// #89: dropping a window's icon onto a monitor, either in another workspace's row or across its own
// row's hairline. This is the geometry half, which is the half that can be quietly wrong: the Win32
// call on top only reads a rectangle and writes one.
//
// The layout used throughout is Petre's own, measured while #39 was built: DISPLAY1 is 3840 wide and
// sits LEFT of the origin, DISPLAY2 is 1920 wide at the origin and is primary. Two screens of
// different sizes is exactly the case where copying coordinates instead of scaling them goes wrong.
public class MonitorMoveTests
{
    static readonly MonitorBounds Left = new(-3840, 0, 0, 2160);
    static readonly MonitorBounds Right = new(0, 0, 1920, 1080);

    static readonly IReadOnlyDictionary<int, MonitorBounds> Both =
        new Dictionary<int, MonitorBounds> { [1] = Left, [2] = Right };

    // --- which monitor a window is on -----------------------------------------------------------

    [Fact]
    public void A_window_belongs_to_the_monitor_it_is_on() =>
        Assert.Equal(2, MonitorMove.MonitorOf(new WindowRect(100, 100, 900, 700), Both));

    [Fact]
    public void A_window_on_the_negative_side_belongs_to_the_left_monitor() =>
        Assert.Equal(1, MonitorMove.MonitorOf(new WindowRect(-2000, 100, -1200, 700), Both));

    // Straddling two screens is what a dragged window does constantly. Windows itself resolves it by
    // area, and so does this: whichever screen holds more of the window.
    [Fact]
    public void A_window_straddling_two_monitors_belongs_to_the_one_holding_more_of_it()
    {
        Assert.Equal(1, MonitorMove.MonitorOf(new WindowRect(-800, 0, 200, 600), Both));
        Assert.Equal(2, MonitorMove.MonitorOf(new WindowRect(-200, 0, 800, 600), Both));
    }

    // A window dragged mostly off the desktop still has to belong somewhere, or the move would refuse
    // to do anything for a window that is merely awkwardly placed.
    [Fact]
    public void A_window_off_every_monitor_belongs_to_the_nearest() =>
        Assert.Equal(2, MonitorMove.MonitorOf(new WindowRect(3000, 0, 3800, 600), Both));

    [Fact]
    public void With_no_monitors_there_is_no_answer() =>
        Assert.Null(MonitorMove.MonitorOf(new WindowRect(0, 0, 100, 100), new Dictionary<int, MonitorBounds>()));

    // --- where it lands -------------------------------------------------------------------------

    // The proportional rule, stated at its simplest: the middle of one screen is the middle of the
    // other, whatever the two are sized.
    [Fact]
    public void A_window_keeps_its_place_as_a_fraction_of_the_screen()
    {
        // Half the left screen's width and height, at a quarter in from its top left.
        var window = new WindowRect(-2880, 540, -960, 1620);

        var moved = MonitorMove.Fit(window, Left, Right);

        Assert.Equal(new WindowRect(480, 270, 1440, 810), moved);
    }

    // The reason it is proportional and not absolute. Copying the offset would put this window at
    // x=2000 on a screen 1920 wide, which is off the edge and unreachable.
    [Fact]
    public void A_window_near_the_right_of_a_wide_screen_stays_on_the_narrow_one()
    {
        var window = new WindowRect(-800, 0, -100, 500);

        var moved = MonitorMove.Fit(window, Left, Right);

        Assert.True(moved.Right <= Right.Right);
        Assert.True(moved.Left >= Right.Left);
    }

    // Scaling DOWN a window that was proportionally large is fine; what must never happen is a
    // rectangle that leaves the target.
    [Fact]
    public void A_window_the_size_of_its_screen_arrives_the_size_of_the_new_one()
    {
        var moved = MonitorMove.Fit(new WindowRect(-3840, 0, 0, 2160), Left, Right);

        Assert.Equal(new WindowRect(0, 0, 1920, 1080), moved);
    }

    // A window hanging off the left edge of its own screen has fractions outside 0..1, and the point
    // of the gesture is to SEE the thing you moved, so it arrives inside.
    [Fact]
    public void A_window_hanging_off_its_own_screen_arrives_inside_the_new_one()
    {
        var moved = MonitorMove.Fit(new WindowRect(-4200, -300, -3400, 300), Left, Right);

        Assert.True(moved.Left >= Right.Left && moved.Top >= Right.Top);
        Assert.True(moved.Right <= Right.Right && moved.Bottom <= Right.Bottom);
    }

    // Going the other way, to the BIGGER screen, the window grows in proportion. Worth pinning down
    // because it is the direction that would look like a bug if it did not.
    [Fact]
    public void Moving_to_a_larger_screen_scales_up()
    {
        var moved = MonitorMove.Fit(new WindowRect(480, 270, 1440, 810), Right, Left);

        Assert.Equal(new WindowRect(-2880, 540, -960, 1620), moved);
    }

    // Never a degenerate rectangle: Win32 accepts one, and a window a pixel wide cannot be found or
    // grabbed by the person who just moved it.
    [Fact]
    public void A_window_is_never_moved_to_nothing()
    {
        var moved = MonitorMove.Fit(new WindowRect(0, 0, 1, 1), Left, Right);

        Assert.True(moved.Width >= 1);
        Assert.True(moved.Height >= 1);
    }

    // A screen reporting an empty rectangle is not something Windows does, but it arrives here from a
    // dictionary this code does not own, and dividing by its width is one line away.
    [Fact]
    public void A_degenerate_source_monitor_does_not_divide_by_zero()
    {
        var moved = MonitorMove.Fit(new WindowRect(0, 0, 100, 100), new MonitorBounds(0, 0, 0, 0), Right);

        Assert.True(moved.Width >= 1 && moved.Height >= 1);
    }
}
