using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskSpaces.Core;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using Xunit.Abstractions;

namespace TaskSpaces.Windows.Tests;

// Petre: "i can't see dotted lines", then "can't see dashed anything".
//
// The ring tests next door assert the STROKE and the PATTERN, and every one of them passed while the
// rings were invisible on his screen. That is the hole this file exists to close: a WPF Border with a
// non-zero CornerRadius clips its child, the ring was a child of the row's container overhanging into
// the border band, and the clip removed all of it. Properties are not pixels.
//
// So these render the bar to a bitmap and COUNT INK in the band each row's ring is drawn in. Which is
// also the only honest way to answer "can you see it": a pattern nobody can see is a pattern that is
// not there, whatever the dash array says.
public class RowRingPixelTests(ITestOutputHelper output)
{
    // Ink along the top edge of a row, as a fraction of the row's width. Solid should be close to
    // whole, a dashed ring a good part of it, a dotted ring less, and a row wearing nothing zero.
    [Fact]
    public void Solid_dashed_and_dotted_rings_all_paint_and_in_that_order() => StaThread.Run(() =>
    {
        using var bar = ThreeWorkspaces();

        var current = bar.TopEdgeInk("GEPHA");
        var previous = bar.TopEdgeInk("Sparrow");
        var earlier = bar.TopEdgeInk("Archive");
        var none = bar.TopEdgeInk("📌");

        output.WriteLine($"current={current:0.###} previous={previous:0.###} earlier={earlier:0.###} none={none:0.###}");

        // Visible at all, which is the whole report. A tenth of the row's width in ink is the floor for
        // "you can see this from where Petre sits": the dotted ring at 2px dots and 4px gaps measures
        // about a third, so the floor is not tight, it just refuses nothing.
        Assert.True(current > 0.5, $"the current row's solid ring measured {current:0.###} of its width");
        Assert.True(previous > 0.1, $"the previous row's dashed ring measured {previous:0.###}");
        Assert.True(earlier > 0.1, $"the earlier row's dotted ring measured {earlier:0.###}");

        // ...and still told apart, in the order Petre asked for: solid, then dashed, then dotted.
        Assert.True(current > previous, $"solid {current:0.###} should out-ink dashed {previous:0.###}");
        Assert.True(previous > earlier, $"dashed {previous:0.###} should out-ink dotted {earlier:0.###}");

        // A row outside the trail wears nothing, so the marks keep meaning something.
        Assert.True(none < 0.02, $"the pinned row measured {none:0.###} of ink and should have none");
    });

    static Bar ThreeWorkspaces()
    {
        var harness = Harness.Build(extraWorkspace: true);
        var bar = harness.ShowBar();
        bar.Bar.UpdateLayout();
        return new Bar(bar);
    }

    sealed class Bar(BarUnderTest bar) : IDisposable
    {
        public void Dispose() => bar.Dispose();

        // The fraction of the row's width that carries ring ink, sampled along the row's top edge.
        //
        // Sampled at the row's own top rather than at the bar's: rows are stacked with a 1px overlap
        // (the negative margin that pays for the caption line), and the band is only a couple of pixels
        // tall, so the sample has to follow the row.
        public double TopEdgeInk(string label)
        {
            var row = Row(label);
            var bitmap = Render();
            var origin = row.TransformToAncestor(bar.Bar).Transform(new Point(0, 0));
            var width = (int)Math.Floor(row.ActualWidth * Scale);

            // The ring is 2 DIP thick and sits within a few pixels of the row's edge either way, so a
            // 6px band catches it wherever inside that it lands and never reaches the row's content.
            var top = (int)Math.Round(origin.Y * Scale);
            var lit = Enumerable.Range(0, width)
                .Count(x => Enumerable.Range(Math.Max(0, top - 1), 6).Any(y => IsInk(bitmap, x + (int)Math.Round(origin.X * Scale), y)));

            return width == 0 ? 0 : (double)lit / width;
        }

        // The ring is near-white at 75% over a dark translucent bar, so "ink" is any pixel that is
        // brighter than the bar could be on its own. The bar's own background is 0x20 grey and its
        // brightest content on these rows is the label text in the right-hand gutter, which the band
        // above never reaches.
        static bool IsInk(BitmapSource bitmap, int x, int y)
        {
            if (x < 0 || y < 0 || x >= bitmap.PixelWidth || y >= bitmap.PixelHeight) return false;
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return (pixel[0] + pixel[1] + pixel[2]) / 3 > 0x60;
        }

        // Rendered at 1:1 with the bar's own DIPs. The window is translucent, so the bitmap starts
        // transparent and everything in it was painted by the bar.
        const double Scale = 1.0;

        BitmapSource Render()
        {
            var root = (FrameworkElement)bar.Bar.Content;
            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(root.ActualWidth * Scale), (int)Math.Ceiling(root.ActualHeight * Scale),
                96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
            bitmap.Render(root);
            return bitmap;
        }

        FrameworkElement Row(string label) =>
            bar.Rows.Children.OfType<Border>()
                .Where(box => box.Child is Grid)
                .Single(box => Texts(box).Any(text => text.Contains(label)));

        static IEnumerable<string> Texts(DependencyObject root) =>
            LogicalTreeHelper.GetChildren(root)
                .OfType<DependencyObject>()
                .SelectMany(child => child is TextBlock text ? [text.Text] : Texts(child));
    }
}
