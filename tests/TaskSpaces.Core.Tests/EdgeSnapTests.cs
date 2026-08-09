using TaskSpaces.Core.Geometry;

namespace TaskSpaces.Core.Tests;

// Petre: "can you snap to edges?"
//
// A 1000x800 work area starting at the origin throughout, and a 200x100 bar, so the numbers
// stay readable: the right edge for that bar is left=800, the bottom is top=700.
public class EdgeSnapTests
{
    const double W = 1000, H = 800, BarW = 200, BarH = 100;

    static (double Left, double Top) Snap(double left, double top) =>
        EdgeSnap.Snap(left, top, BarW, BarH, 0, 0, W, H);

    [Fact]
    public void Near_the_left_edge_it_snaps_flush() =>
        Assert.Equal(0, Snap(EdgeSnap.Distance - 1, 400).Left);

    [Fact]
    public void Near_the_right_edge_it_snaps_flush() =>
        Assert.Equal(W - BarW, Snap(W - BarW - (EdgeSnap.Distance - 1), 400).Left);

    [Fact]
    public void Near_the_top_edge_it_snaps_flush() =>
        Assert.Equal(0, Snap(400, EdgeSnap.Distance - 1).Top);

    [Fact]
    public void Near_the_bottom_edge_it_snaps_flush() =>
        Assert.Equal(H - BarH, Snap(400, H - BarH - (EdgeSnap.Distance - 1)).Top);

    // The axes are independent, so a corner needs no case of its own -- which is the reason
    // the implementation has none.
    [Fact]
    public void A_corner_snaps_on_both_axes_at_once() =>
        Assert.Equal((W - BarW, 0.0), Snap(W - BarW - 5, 5));

    // The threshold has to be a real boundary, or "snap" becomes "the bar moves whenever I put
    // it down anywhere near the middle".
    [Fact]
    public void Beyond_the_threshold_nothing_moves()
    {
        var placed = (Left: EdgeSnap.Distance + 1, Top: 400.0);
        Assert.Equal(placed, Snap(placed.Left, placed.Top));
    }

    [Fact]
    public void Exactly_at_the_threshold_still_snaps() =>
        Assert.Equal(0, Snap(EdgeSnap.Distance, 400).Left);

    // Dragged slightly PAST the edge, which is easy to do with a fast flick.
    [Fact]
    public void Slightly_off_screen_snaps_back_flush() =>
        Assert.Equal(0, Snap(-(EdgeSnap.Distance - 1), 400).Left);

    // A work area narrower than the bar has both edges in range at once; left wins, matching
    // WorkAreaClamp pinning to left/top when a box does not fit.
    [Fact]
    public void When_the_bar_is_wider_than_the_area_it_pins_left() =>
        Assert.Equal(0, EdgeSnap.Snap(5, 5, 2000, BarH, 0, 0, W, H).Left);

    // --- which edge it then grows from ---------------------------------------------------

    // The mirror of the bug that made the right edge the anchor: a bar against the LEFT edge
    // must grow rightwards, or it walks off the screen.
    [Fact]
    public void Snapped_to_the_left_edge_it_grows_rightwards() =>
        Assert.False(EdgeSnap.GrowsLeftwards(left: 0, workAreaLeft: 0));

    [Fact]
    public void Snapped_to_the_right_edge_it_grows_leftwards() =>
        Assert.True(EdgeSnap.GrowsLeftwards(left: W - BarW, workAreaLeft: 0));

    // The ordinary case: floating in open space, it grows leftwards, because that is where
    // Petre keeps it and off the right of the screen is where it used to go.
    [Fact]
    public void Floating_in_open_space_it_grows_leftwards() =>
        Assert.True(EdgeSnap.GrowsLeftwards(left: 400, workAreaLeft: 0));

    // A monitor left of the primary one has negative coordinates -- the layout Petre actually
    // runs, and the one that broke an earlier version of the positioning code.
    [Fact]
    public void A_monitor_at_negative_coordinates_works_the_same()
    {
        Assert.Equal(-1920, EdgeSnap.Snap(-1920 + 5, 5, BarW, BarH, -1920, 0, -920, H).Left);
        Assert.False(EdgeSnap.GrowsLeftwards(left: -1920, workAreaLeft: -1920));
    }

    // --- the vertical twin (#50) ------------------------------------------------------------
    //
    // Only width growth was anchored while height rarely moved. Inserting a workspace from the
    // bar's right-click menu and wrapping a row by dragging an edge both change the HEIGHT, and
    // the bar's home is the bottom-right corner -- so unanchored height growth walks it off the
    // bottom of the screen.

    [Fact]
    public void Snapped_to_the_top_edge_it_grows_downwards() =>
        Assert.False(EdgeSnap.GrowsUpwards(top: 0, workAreaTop: 0));

    [Fact]
    public void Snapped_to_the_bottom_edge_it_grows_upwards() =>
        Assert.True(EdgeSnap.GrowsUpwards(top: H - BarH, workAreaTop: 0));

    // The ordinary case, and the same answer its horizontal twin gives: floating in open space it
    // grows upwards, because the bottom-right corner is where the bar lives.
    [Fact]
    public void Floating_in_open_space_it_grows_upwards() =>
        Assert.True(EdgeSnap.GrowsUpwards(top: 300, workAreaTop: 0));

    // A taskbar along the TOP of the screen pushes the work area down, so "at the top" is a
    // work-area fact and never a screen one -- the same reason the horizontal twin takes
    // workAreaLeft rather than assuming zero, which is what makes it survive Petre's
    // negative-coordinate monitor above.
    [Fact]
    public void The_top_of_the_work_area_is_what_counts_not_the_top_of_the_screen() =>
        Assert.False(EdgeSnap.GrowsUpwards(top: 48, workAreaTop: 48));

    // Just inside the snap distance still counts as "at the top": the threshold is shared with
    // Snap, so a bar that snapped to an edge cannot then disagree about which way it grows.
    [Fact]
    public void Within_the_snap_distance_of_the_top_it_still_grows_downwards() =>
        Assert.False(EdgeSnap.GrowsUpwards(top: EdgeSnap.Distance, workAreaTop: 0));
}
