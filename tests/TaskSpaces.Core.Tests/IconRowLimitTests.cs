using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Tests;

// Petre: "if any one workspace grows too wide, then it's inefficient... there's an icon limit,
// and if that's exceeded, then it's the next row that needs to be added."
//
// The line boundaries, tested without constructing a bar. The rule is deliberately FIXED
// rather than derived from the other rows (see IconRowLimit's own comment): an adaptive limit
// would be recomputed on every rebuild and re-wrap a busy workspace whenever an unrelated
// window opened or closed, which on a SizeToContent window means it repositions itself.
public class IconRowLimitTests
{
    static IReadOnlyList<int> Sizes(int count) =>
        IconRowLimit.Lines(Enumerable.Range(1, count).ToList()).Select(line => line.Count).ToList();

    // The common case, and the one that must not change: everything up to the limit stays on a
    // single line, exactly as the bar looked before wrapping existed.
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(IconRowLimit.IconsPerLine)]
    public void A_row_within_the_limit_is_a_single_line(int count) =>
        Assert.Equal([count], Sizes(count));

    [Fact]
    public void One_icon_past_the_limit_starts_a_second_line() =>
        Assert.Equal([IconRowLimit.IconsPerLine, 1], Sizes(IconRowLimit.IconsPerLine + 1));

    // Petre's actual HRIS row at the time this was written: eight icons, against five other
    // workspaces holding one to four each.
    [Fact]
    public void A_busy_workspace_wraps_onto_as_many_lines_as_it_needs() =>
        Assert.Equal([5, 3], Sizes(8));

    [Fact]
    public void An_exact_multiple_of_the_limit_leaves_no_trailing_empty_line() =>
        Assert.Equal([5, 5], Sizes(10));

    // An empty workspace's row is just its label. Yielding one empty line instead would add an
    // icon's worth of height to every empty lane -- Personal and Extra both sit empty on
    // Petre's bar, so that is two wasted rows of the thing this feature exists to save.
    [Fact]
    public void An_empty_group_produces_no_lines_at_all() =>
        Assert.Empty(IconRowLimit.Lines(Array.Empty<int>()));

    // --- width-driven split (the bar has a width the user dragged) ------------------------
    //
    // Only in play once the bar has an EXPLICIT width. The fixed rule above stays the default,
    // and the instability that got adaptive wrapping rejected in the first place cannot happen
    // here: a width the user chose does not change when an unrelated window opens.

    // Every icon the same width, which is the ordinary case -- 24 DIP cells, no monitor markers.
    static IReadOnlyList<int> FitSizes(int count, double available) =>
        IconRowLimit.LinesThatFit(Enumerable.Range(1, count), _ => 24.0, available).Select(line => line.Count).ToList();

    [Fact]
    public void Icons_fill_the_width_they_are_given() =>
        Assert.Equal([5, 5, 2], FitSizes(12, available: 5 * 24));

    // A wider bar is the whole point of dragging one out: more icons per line, fewer lines.
    [Fact]
    public void A_wider_bar_puts_more_on_each_line() =>
        Assert.Equal([8, 4], FitSizes(12, available: 8 * 24));

    // Petre: "i want no less than 3 icons per row width." A floor, not a target -- the line takes
    // the overflow rather than degrading to one icon per line and a bar taller than the screen.
    [Theory]
    [InlineData(0)]
    [InlineData(24)]
    [InlineData(50)]
    public void A_line_is_never_broken_before_three_icons(double available) =>
        Assert.Equal([3, 3, 1], FitSizes(7, available));

    // ...and the floor is the CALLER's to state, because a line divided into one lane per monitor
    // gives each lane a share of it. Undivided, three per lane means six on a line that fits four,
    // and the overflow is clipped rather than wrapped: measured on a 177px bar, a 61 DIP lane held
    // two cells and drew three. Petre: "on the second monitor, edge is cut, not moved to the second
    // row."
    [Fact]
    public void A_lane_sharing_a_line_wraps_at_the_floor_it_is_given() =>
        Assert.Equal(
            [2, 2, 2],
            IconRowLimit.LinesThatFit(Enumerable.Range(1, 6), _ => 24.0, available: 61, minimumPerLine: 1)
                .Select(line => line.Count));

    // One is the smallest floor there is, and a zero or negative one must not produce an infinite
    // sequence of empty lines: an icon always lands somewhere.
    [Fact]
    public void A_floor_below_one_still_puts_one_icon_on_a_line() =>
        Assert.Equal(
            [1, 1, 1],
            IconRowLimit.LinesThatFit(Enumerable.Range(1, 3), _ => 24.0, available: 10, minimumPerLine: 0)
                .Select(line => line.Count));

    // Exactly at the boundary: a line is broken when the NEXT icon would overflow, not when the
    // current one merely fills the width. Off by one here means a permanently ragged bar.
    [Fact]
    public void An_exact_fit_is_not_broken_early() =>
        Assert.Equal([4], FitSizes(4, available: 4 * 24));

    // The monitor markers are drawn inline between groups, so they consume width that no
    // count-based rule can see. An icon carrying one is wider, and the line has to know.
    [Fact]
    public void A_marker_costs_the_line_the_width_it_takes()
    {
        // The 4th item carries a monitor marker, so it is 24 + 16 wide and no longer fits a
        // 4-cell line -- but the 3-icon floor is already satisfied, so the break is allowed.
        var lines = IconRowLimit.LinesThatFit([1, 2, 3, 4, 5], icon => icon == 4 ? 40.0 : 24.0, available: 4 * 24);

        Assert.Equal([1, 2, 3], lines[0]);
        Assert.Equal([4, 5], lines[1]);
    }

    [Fact]
    public void An_empty_group_produces_no_lines_at_all_by_width() =>
        Assert.Empty(IconRowLimit.LinesThatFit(Array.Empty<int>(), _ => 24.0, available: 200));

    // Order is preserved across the split: the bar's icons are ordered, and a wrap must read
    // left-to-right then down, not shuffle.
    [Fact]
    public void Icons_keep_their_order_across_the_split()
    {
        var lines = IconRowLimit.Lines(Enumerable.Range(1, 8).ToList());

        Assert.Equal([1, 2, 3, 4, 5], lines[0]);
        Assert.Equal([6, 7, 8], lines[1]);
    }
}
