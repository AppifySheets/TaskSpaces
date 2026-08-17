using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TaskSpaces.Windows.Tests;

// #155, the drawing half. The walk itself is settled in Core (HistoryTrailTests); what can only be
// checked against a real bar is that the mark lands on the right row, in the right strength, and gives
// way to every ring that outranks it.
//
// Asserted on the ALPHA of each row's border brush rather than on a named colour, because the claim
// worth pinning is the ORDER: current brighter than previous, previous brighter than the one before,
// and everything else invisible. A future retune may move all three numbers; it must not reorder them.
public class HistoryRingTests
{
    static IReadOnlyList<Border> RowBoxes(Panel rows) =>
        rows.Children.OfType<Border>().Where(b => b.Child is Grid).ToList();

    static Border RowLabelled(Panel rows, string label) =>
        RowBoxes(rows).Single(box => TextIn(box).Any(text => text.Contains(label)));

    static byte AlphaOf(Border box) => (box.BorderBrush as SolidColorBrush)?.Color.A ?? 0;

    static IReadOnlyList<string> TextIn(DependencyObject root)
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
                    if (child is TextBlock { Text: { } text }) found.Add(text);
                    Collect(child);
                });
    }

    // The harness stands on GEPHA with Sparrow as the only other workspace, so Sparrow is where the
    // back button points and therefore where the trail's first mark belongs.
    [Fact]
    public void The_previous_workspace_wears_a_ring() => StaThread.Run(() =>
    {
        using var bar = Harness.Build().ShowBar();

        Assert.True(AlphaOf(RowLabelled(bar.Rows, "Sparrow")) > 0);
    });

    // "Maybe still white, less pronounced than the current one." Both halves of that sentence: same
    // colour, weaker. Two currents would be worse than no mark at all.
    [Fact]
    public void It_is_the_same_white_as_the_current_ring_but_fainter() => StaThread.Run(() =>
    {
        using var bar = Harness.Build().ShowBar();

        var current = (SolidColorBrush)RowLabelled(bar.Rows, "GEPHA").BorderBrush;
        var previous = (SolidColorBrush)RowLabelled(bar.Rows, "Sparrow").BorderBrush;

        Assert.Equal(Colors.White.R, previous.Color.R);
        Assert.Equal(Colors.White.G, previous.Color.G);
        Assert.Equal(Colors.White.B, previous.Color.B);
        Assert.True(previous.Color.A < current.Color.A);
    });

    // Rows that are neither current nor in the trail stay invisible, or the mark stops meaning
    // anything: a bar where every row wears a ring says nothing.
    [Fact]
    public void Rows_outside_the_trail_wear_nothing() => StaThread.Run(() =>
    {
        using var bar = Harness.Build(withUnnamedDesktop: true).ShowBar();

        Assert.Equal(0, AlphaOf(RowLabelled(bar.Rows, "📌")));
    });

    // The current row keeps the brightest claim on the bar. This is the regression that would matter
    // most: a trail that overwrote "you are here" would be a straight downgrade.
    [Fact]
    public void The_current_row_still_wears_the_brightest_ring() => StaThread.Run(() =>
    {
        using var bar = Harness.Build(withUnnamedDesktop: true).ShowBar();

        var current = AlphaOf(RowLabelled(bar.Rows, "GEPHA"));

        Assert.True(RowBoxes(bar.Rows)
            .Where(box => box != RowLabelled(bar.Rows, "GEPHA"))
            .All(box => AlphaOf(box) < current));
    });

    // With one workspace there is nowhere to have come from, and the back button is disabled for the
    // same reason. The two surfaces must always agree.
    [Fact]
    public void With_one_workspace_no_row_wears_a_trail() => StaThread.Run(() =>
    {
        using var bar = Harness.Build(singleWorkspace: true).ShowBar();

        var current = RowLabelled(bar.Rows, "GEPHA");
        Assert.True(RowBoxes(bar.Rows).Where(box => box != current).All(box => AlphaOf(box) == 0));
    });
}
