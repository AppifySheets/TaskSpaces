using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TaskSpaces.Windows.Tests;

// #155, the drawing half. The walk itself is settled in Core (HistoryTrailTests); what can only be
// checked against a real bar is that the mark lands on the right row, in the right STYLE, and gives
// way to every ring that outranks it.
//
// The trail used to be told apart by brightness -- one white at three alphas -- and Petre replaced
// that: "let the active workspace be circled the way it is, previous workspaces be dashed, instead of
// dimmed white", then "current - solid - previous - dashed; one before that - dotted".
//
// So these assert on the STROKE PATTERN rather than on alpha, and on all three rings being the same
// white. That is the point of the change: the pattern carries the order, and a dashed outline is
// already quieter than a solid one without being dimmed into guesswork.
public class HistoryRingTests
{
    static IReadOnlyList<Border> RowBoxes(Panel rows) =>
        rows.Children.OfType<Border>().Where(b => b.Child is Grid).ToList();

    static Border RowLabelled(Panel rows, string label) =>
        RowBoxes(rows).Single(box => TextIn(box).Any(text => text.Contains(label)));

    // The ring is the row's only Rectangle: a stroked outline drawn over the row's border band, which
    // is what a dashed ring needs -- a Border's BorderBrush cannot be dashed at all.
    static Rectangle Ring(Panel rows, string label) =>
        Descendants(RowLabelled(rows, label)).OfType<Rectangle>().Single();

    static byte AlphaOf(Rectangle ring) => (ring.Stroke as SolidColorBrush)?.Color.A ?? 0;

    // The dash pattern, flattened for comparison: empty means solid.
    static IReadOnlyList<double> DashesOf(Rectangle ring) => ring.StrokeDashArray.ToList();

    static IEnumerable<DependencyObject> Descendants(DependencyObject node) =>
        LogicalTreeHelper.GetChildren(node)
            .OfType<DependencyObject>()
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    static IReadOnlyList<string> TextIn(DependencyObject root) =>
        Descendants(root).OfType<TextBlock>().Select(t => t.Text).ToList();

    // The harness stands on GEPHA with Sparrow as the only other workspace, so Sparrow is where the
    // back button points and therefore where the trail's first mark belongs.
    [Fact]
    public void The_previous_workspace_wears_a_ring() => StaThread.Run(() =>
    {
        using var bar = Harness.Build().ShowBar();

        Assert.True(AlphaOf(Ring(bar.Rows, "Sparrow")) > 0);
    });

    // "Current - solid - previous - dashed." The current row keeps the unbroken circle it has always
    // had, and the row you came from is the same ring with gaps in it.
    [Fact]
    public void The_current_row_is_solid_and_the_previous_one_dashed() => StaThread.Run(() =>
    {
        using var bar = Harness.Build().ShowBar();

        Assert.Empty(DashesOf(Ring(bar.Rows, "GEPHA")));
        Assert.NotEmpty(DashesOf(Ring(bar.Rows, "Sparrow")));
    });

    // "One before that - dotted", which needs a third workspace to have a second step at all.
    //
    // Dots are told from dashes by the length of the ink, not by the gap: a dot is a stroke as short
    // as the outline is thick. Asserting the ORDER of those lengths rather than the numbers leaves
    // both patterns free to be retuned by eye without rewriting the test.
    [Fact]
    public void The_step_before_the_previous_one_is_dotted() => StaThread.Run(() =>
    {
        using var bar = Harness.Build(extraWorkspace: true).ShowBar();

        var dashed = DashesOf(Ring(bar.Rows, "Sparrow"));
        var dotted = DashesOf(Ring(bar.Rows, "Archive"));

        Assert.NotEmpty(dotted);
        Assert.True(dotted[0] < dashed[0], $"dotted ink {dotted[0]} should be shorter than dashed ink {dashed[0]}");
    });

    // The other half of Petre's sentence: "instead of dimmed white". All three rings are one colour at
    // one strength now, so the trail's order is carried by the pattern alone. Dimming as well would say
    // the same thing twice and cost the faintest step its legibility over a lane tint.
    [Fact]
    public void All_three_rings_are_the_same_white() => StaThread.Run(() =>
    {
        using var bar = Harness.Build(extraWorkspace: true).ShowBar();

        var strokes = new[] { "GEPHA", "Sparrow", "Archive" }
            .Select(label => (SolidColorBrush)Ring(bar.Rows, label).Stroke)
            .ToList();

        Assert.Single(strokes.Select(s => s.Color).Distinct());
        Assert.Equal(Colors.White.R, strokes[0].Color.R);
        Assert.Equal(Colors.White.G, strokes[0].Color.G);
        Assert.Equal(Colors.White.B, strokes[0].Color.B);
    });

    // Rows that are neither current nor in the trail stay invisible, or the mark stops meaning
    // anything: a bar where every row wears a ring says nothing.
    [Fact]
    public void Rows_outside_the_trail_wear_nothing() => StaThread.Run(() =>
    {
        using var bar = Harness.Build(withUnnamedDesktop: true).ShowBar();

        Assert.Equal(0, AlphaOf(Ring(bar.Rows, "📌")));
    });

    // The regression that would matter most: exactly one row says "you are here", and it is the only
    // unbroken ring on the bar.
    [Fact]
    public void Only_the_current_row_wears_an_unbroken_ring() => StaThread.Run(() =>
    {
        using var bar = Harness.Build(withUnnamedDesktop: true, extraWorkspace: true).ShowBar();

        var solid = RowBoxes(bar.Rows)
            .Select(box => Descendants(box).OfType<Rectangle>().Single())
            .Where(ring => AlphaOf(ring) > 0 && DashesOf(ring).Count == 0)
            .ToList();

        Assert.Single(solid);
        Assert.Same(Ring(bar.Rows, "GEPHA"), solid[0]);
    });

    // With one workspace there is nowhere to have come from, and the back button is disabled for the
    // same reason. The two surfaces must always agree.
    [Fact]
    public void With_one_workspace_no_row_wears_a_trail() => StaThread.Run(() =>
    {
        using var bar = Harness.Build(singleWorkspace: true).ShowBar();

        var current = RowLabelled(bar.Rows, "GEPHA");
        Assert.True(RowBoxes(bar.Rows)
            .Where(box => box != current)
            .All(box => AlphaOf(Descendants(box).OfType<Rectangle>().Single()) == 0));
    });
}
