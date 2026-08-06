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
