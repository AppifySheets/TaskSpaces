using TaskSpaces.Core.Geometry;

namespace TaskSpaces.Core.Tests;

// Task 11 fix round 2: Petre's floating bar landed at (2408, 1396) with
// Visible=true and was invisible -- a 2560x1440 monitor at 125% scaling has a
// ~2048x1152 DIP-space work area, so 2408 sits ~360 DIPs past its right edge.
// These tests cover the pure clamp math in isolation (no DPI query, no monitor,
// no WPF window needed) -- the regression case reproduces Petre's exact numbers.
public class WorkAreaClampTests
{
    [Fact]
    public void Regression_stale_offscreen_left_from_a_wider_monitor_is_pulled_back_onto_a_narrower_work_area()
    {
        // Petre's exact scenario: a 2560px-wide monitor at 125% scaling has a
        // 2048-DIP-wide work area; a stale/bad Left of 2408 (past the right edge)
        // must clamp back to fully inside it.
        var (left, _) = WorkAreaClamp.Clamp(
            left: 2408, top: 1396, width: 160, height: 44,
            workAreaLeft: 0, workAreaTop: 0, workAreaRight: 2048, workAreaBottom: 1152);

        Assert.Equal(2048 - 160, left);
    }

    [Fact]
    public void Regression_stale_offscreen_top_is_pulled_back_onto_a_shorter_work_area()
    {
        var (_, top) = WorkAreaClamp.Clamp(
            left: 2408, top: 1396, width: 160, height: 44,
            workAreaLeft: 0, workAreaTop: 0, workAreaRight: 2048, workAreaBottom: 1152);

        Assert.Equal(1152 - 44, top);
    }

    [Fact]
    public void Position_already_inside_the_work_area_is_left_untouched()
    {
        var (left, top) = WorkAreaClamp.Clamp(
            left: 500, top: 300, width: 160, height: 44,
            workAreaLeft: 0, workAreaTop: 0, workAreaRight: 2048, workAreaBottom: 1152);

        Assert.Equal(500, left);
        Assert.Equal(300, top);
    }

    [Fact]
    public void Negative_left_above_the_work_area_origin_clamps_to_the_left_edge()
    {
        var (left, top) = WorkAreaClamp.Clamp(
            left: -400, top: -200, width: 160, height: 44,
            workAreaLeft: 0, workAreaTop: 0, workAreaRight: 2048, workAreaBottom: 1152);

        Assert.Equal(0, left);
        Assert.Equal(0, top);
    }

    [Fact]
    public void Work_area_narrower_than_the_box_pins_to_the_areas_own_left_top_edge_instead_of_going_negative()
    {
        // Same Math.Max guard as SwitcherPanel.PositionNear: a work area smaller than
        // the box itself must not produce workAreaRight - width < workAreaLeft.
        var (left, top) = WorkAreaClamp.Clamp(
            left: 100, top: 100, width: 3000, height: 2000,
            workAreaLeft: 0, workAreaTop: 0, workAreaRight: 2048, workAreaBottom: 1152);

        Assert.Equal(0, left);
        Assert.Equal(0, top);
    }

    [Fact]
    public void Work_area_not_anchored_at_the_origin_is_respected()
    {
        // A secondary monitor positioned to the right of primary has a work area that
        // does not start at (0,0) -- the clamp must use the GIVEN bounds, not assume 0.
        var (left, top) = WorkAreaClamp.Clamp(
            left: 5000, top: 50, width: 160, height: 44,
            workAreaLeft: 2048, workAreaTop: 0, workAreaRight: 3968, workAreaBottom: 1080);

        Assert.Equal(3968 - 160, left);
        Assert.Equal(50, top); // already inside vertically
    }
}
