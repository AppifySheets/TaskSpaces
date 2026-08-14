using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Geometry;

namespace TaskSpaces.Core.Tests;

// #150. Petre: "when changing screen resolution, when connecting from a laptop, floating window may
// not show... maybe have its own place for each layout, as windows already does i think."
//
// The key is what makes "each layout" mean anything, so what is tested here is the two ways it could
// be wrong in opposite directions: calling one layout two things (the bar forgets where it sat) and
// calling two layouts one thing (the bar restores a position from somebody else's desk).
public class MonitorLayoutKeyTests
{
    static MonitorBounds Screen(int left, int top, int right, int bottom) => new(left, top, right, bottom);

    static readonly MonitorBounds Desk = Screen(0, 0, 2560, 1440);
    static readonly MonitorBounds Second = Screen(2560, 0, 4480, 1080);

    // The one that matters most, because EnumDisplayMonitors makes no promise about order: the same
    // two screens handed back the other way round have to be the same desk.
    [Fact]
    public void Enumeration_order_does_not_change_the_key() =>
        Assert.Equal(
            MonitorLayoutKey.Of([Desk, Second]),
            MonitorLayoutKey.Of([Second, Desk]));

    [Fact]
    public void Two_screens_are_a_different_layout_from_one() =>
        Assert.NotEqual(
            MonitorLayoutKey.Of([Desk]),
            MonitorLayoutKey.Of([Desk, Second]));

    // A resolution change moves every edge the bar could be parked against, so the position chosen at
    // 4K is not the position wanted at 1080p. Same screen, different layout, on purpose.
    [Fact]
    public void A_resolution_change_is_a_different_layout() =>
        Assert.NotEqual(
            MonitorLayoutKey.Of([Screen(0, 0, 3840, 2160)]),
            MonitorLayoutKey.Of([Screen(0, 0, 1920, 1080)]));

    // The laptop-on-the-left arrangement Petre actually has: negative coordinates must survive into
    // the key rather than being lost to formatting.
    [Fact]
    public void A_screen_left_of_the_primary_one_keeps_its_negative_origin() =>
        Assert.Contains("-1920,0", MonitorLayoutKey.Of([Screen(-1920, 0, 0, 1080), Desk]));

    // Moving a screen in the arrangement is a different layout too, and this is the case sorting
    // could quietly swallow: the same two rectangles' SIZES with different origins.
    [Fact]
    public void Rearranging_the_same_two_screens_is_a_different_layout() =>
        Assert.NotEqual(
            MonitorLayoutKey.Of([Screen(0, 0, 1920, 1080), Screen(1920, 0, 3840, 1080)]),
            MonitorLayoutKey.Of([Screen(0, 0, 1920, 1080), Screen(-1920, 0, 0, 1080)]));

    [Fact]
    public void The_same_layout_twice_gives_the_same_key() =>
        Assert.Equal(MonitorLayoutKey.Of([Desk, Second]), MonitorLayoutKey.Of([Desk, Second]));

    // No monitors and no answer are both Unknown, which callers read as "do not remember, and do not
    // look up". Storing under an empty key would let two different unknown layouts share one entry
    // and hand each other's position back.
    [Fact]
    public void No_monitors_is_unknown() =>
        Assert.Equal(MonitorLayoutKey.Unknown, MonitorLayoutKey.Of([]));

    [Fact]
    public void Null_is_unknown() =>
        Assert.Equal(MonitorLayoutKey.Unknown, MonitorLayoutKey.Of(null));

    [Fact]
    public void Unknown_is_not_known() => Assert.False(MonitorLayoutKey.IsKnown(MonitorLayoutKey.Unknown));

    [Fact]
    public void A_real_layout_is_known() => Assert.True(MonitorLayoutKey.IsKnown(MonitorLayoutKey.Of([Desk])));
}
