using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using TaskSpaces.App;
using TaskSpaces.Core;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using Xunit.Abstractions;

namespace TaskSpaces.Windows.Tests;

// Petre, with a screenshot of four rows: "separator is not always uniform in height".
//
// MEASURED FIRST, because the code says it cannot happen: MonitorMarker builds the stroke with a
// literal Height of 14, so nothing in it varies. What varies is where that stroke LANDS, and these
// are the numbers off a real rendered bar, at the bar's own 0.9 scale:
//
//   row with icons   mark 12.6   row 27   mark 7.2 down from the row's top   <- centred
//   row with none    mark 12.6   row 27   mark 1.8 down from the row's top   <- hugs the top
//
// Same ink, different place. A line is as tall as its tallest child, so a line holding icons is about
// 28 DIP and the mark centres inside it, while a line holding nothing but marks is exactly as tall as
// a mark and sits at the top of the row with all the slack below it. Two neighbouring rows then show
// their separators at different heights, which is what the screenshot shows.
//
// Measured through TransformToAncestor, so the bar's scale and each row's own scale are part of the
// number rather than something to argue about afterwards.
public class FloatingBarMonitorMarkTests(ITestOutputHelper output)
{
    // The invariant: an empty workspace's separator sits exactly where a busy one's does.
    //
    // About the OFFSET as much as the height. Height alone was already equal before the fix, the
    // stroke being a constant, so a height-only assertion passes on the bug.
    [Fact]
    public void A_row_with_no_windows_puts_its_mark_where_a_row_with_icons_does() => StaThread.Run(() =>
    {
        using var bar = TwoScreens(busyIcons: 2);

        var marks = bar.Marks();
        marks.ForEach(m => output.WriteLine(m.ToString()));

        // Full-size rows only, named rather than measured: the pinned row is scaled to 0.8 as a whole
        // (#109) and a minimized one to 0.43 (#52), so their marks scale with everything else in
        // those rows, which is the point of scaling a row rather than its parts. A height threshold
        // was the first attempt and is too close to call -- the pinned row measures 22.3 against a
        // full row's 27.
        var full = marks.Where(m => m.Row is not "📌").ToList();
        Assert.Equal(2, full.Count); // the busy row and the empty one, one mark each

        Assert.Single(full.Select(m => Math.Round(m.Height, 1)).Distinct());
        Assert.Single(full.Select(m => Math.Round(m.InsetInRow, 1)).Distinct());
    });

    // The same rule one level down, because a row is not always one line: a wrapped row draws a mark
    // per line, and each has to be centred on ITS line or the pair reads as a stagger rather than as
    // one divider carried down the row.
    //
    // This passed before the fix and is kept anyway: it is the case that breaks if the height floor is
    // ever moved back off the line and onto the row, which is where it used to be.
    [Fact]
    public void Every_line_of_a_wrapped_row_centres_its_own_mark() => StaThread.Run(() =>
    {
        // Seven windows, six of them on the first screen, so that screen's lane runs onto a second line.
        using var bar = TwoScreens(busyIcons: 7);

        var wrapped = bar.Marks().Where(m => m.RowHeight > 40).ToList();
        wrapped.ForEach(m => output.WriteLine(m.ToString()));

        Assert.Equal(2, wrapped.Count); // the first line's mark, then the continuation line's
        Assert.Single(wrapped.Select(m => Math.Round(m.Height, 1)).Distinct());
        Assert.Single(wrapped.Select(m => Math.Round(m.InsetInLine, 1)).Distinct());
    });

    // Petre, with a screenshot of a wrapped GEPHA row: "separator on GEPHA is in a wrong place on the
    // second line." His own geometry dump said it in two lines:
    //
    //   line 0  cols=[*:74, auto:5, *:74]        zones=[screen1@start, screen2@73]
    //   line 1  cols=[*:50, auto:5, *:50, *:50]  zones=[screen1@5,     screen2@51]
    //
    // A THIRD lane on the second line, so the boundary moved from 73 to 51. It comes from the fallback
    // for a window whose monitor cannot be resolved -- spacedesk's hidden notification dialog, in his
    // case -- which used to get a lane of its own. That changes the column count for one line, and the
    // whole point of laying every row out over every known screen is that the boundary CANNOT wander
    // (#39, #99, #103).
    //
    // The fallback itself is not the mistake and must stay: drawing only the mapped screens once meant
    // a bar where no window had a monitor rendered every row with no icons at all. What is wrong is
    // giving those windows a lane instead of a home in one.
    [Fact]
    public void A_window_with_no_monitor_does_not_move_the_boundary() => StaThread.Run(() =>
    {
        using var bar = TwoScreens(busyIcons: 7, oneWithNoMonitor: true);

        var marks = bar.Marks().Where(m => m.Row is not "📌").ToList();
        marks.ForEach(m => output.WriteLine(m.ToString()));

        // Every line of every full-size row divides its screens at the SAME x.
        Assert.Single(marks.Select(m => Math.Round(m.X, 1)).Distinct());
    });

    // Two displays, which is what turns the marks on at all; one workspace holding busyIcons windows
    // and one holding nothing. The last window goes on the second screen so the busy row has a
    // boundary to draw, and the rest on the first.
    static Bar TwoScreens(int busyIcons, bool oneWithNoMonitor = false)
    {
        var busyDesktop = Guid.NewGuid();
        var emptyDesktop = Guid.NewGuid();
        var busy = new Workspace(Guid.NewGuid(), "Busy", busyDesktop);
        var empty = new Workspace(Guid.NewGuid(), "Empty", emptyDesktop);

        var desktops = new PulsingDesktops { CurrentId = busyDesktop };
        desktops.Desktops.Add(new DesktopInfo(busyDesktop, "Busy"));
        desktops.Desktops.Add(new DesktopInfo(emptyDesktop, "Empty"));

        var monitor = new StubMonitor();
        var placement = new List<(WindowHandle Window, int Monitor)>();
        Enumerable.Range(1, busyIcons).ToList().ForEach(i =>
        {
            var window = new WindowInfo(new WindowHandle(100 + i), 10 + i, "app" + i, @"C:\app" + i + ".exe", "App " + i, null);
            desktops.Placements[window.Handle] = busyDesktop;
            monitor.Initial.Add(window);
            // The last window goes on the second screen; the one before it can be given no monitor at
            // all, which is the case that used to add a lane.
            if (oneWithNoMonitor && i == busyIcons - 1) return;
            placement.Add((window.Handle, i == busyIcons ? 2 : 1));
        });

        var screens = new StubScreens { Facts = TwoDisplays(placement) };
        // A STORED WIDTH, because that is the bar Petre runs and it selects a different layout: with
        // one, each screen's lane wraps against its own share of the row, and without one the rows keep
        // the old fixed five-icons rule and a lane can overflow its share -- which pushes a star column
        // wider and moves the boundary for reasons that have nothing to do with what is being tested
        // here. That case is known and written down in GroupRow ("still unprotected on a bar that has
        // never been resized"); it resolves itself the moment a width is stored, which is the moment
        // this fixture reproduces.
        var store = new StubStore
        {
            Stored = AppState.Empty with
            {
                Workspaces = [busy, empty],
                FloatingBar = new FloatingBarState(0, 0, true) { Width = 202 },
            },
        };
        var manager = new WorkspaceManager(desktops, monitor, new StubTitles(), store, screenLayout: screens);
        Assert.True(manager.Start().IsSuccess);

        // Parked off the virtual screen, as every bar test does, so the suite never flashes a
        // translucent topmost bar at whoever is running it. WPF still reports it visible.
        var bar = new FloatingBar(manager) { Left = -32000, Top = -32000 };
        bar.Show();
        // SizeToContent: nothing in the tree has a height until a layout pass has run.
        bar.UpdateLayout();
        return new Bar(bar);
    }

    static ScreenFacts TwoDisplays(IReadOnlyList<(WindowHandle Window, int Monitor)> frontToBack) =>
        new(frontToBack.ToDictionary(x => x.Window, x => x.Monitor),
            new HashSet<WindowHandle>(),
            frontToBack.Select((x, i) => (x.Window, i)).ToDictionary(x => x.Window, x => x.i),
            PrimaryMonitor: 1);

    // One measured mark: how long its ink is, and how far down its line and its row it begins. Every
    // number is in the bar's own coordinates, transforms included.
    sealed record Mark(string Row, double X, double Height, double RowHeight, double InsetInRow, double LineHeight, double InsetInLine)
    {
        public override string ToString() =>
            "row=" + Row + " x=" + X.ToString("0.##") + " mark=" + Height.ToString("0.##") + " rowHeight=" + RowHeight.ToString("0.##") +
            " insetInRow=" + InsetInRow.ToString("0.##") + " lineHeight=" + LineHeight.ToString("0.##") +
            " insetInLine=" + InsetInLine.ToString("0.##");
    }

    sealed class Bar(FloatingBar bar) : IDisposable
    {
        public void Dispose() => bar.Close();

        public List<Mark> Marks() =>
            Strokes((Panel)bar.FindName("Rows")!)
                .Select(stroke =>
                {
                    var line = Ancestor<Grid>(stroke);
                    var row = Row(stroke);
                    return new Mark(Label(row), Left(stroke), Extent(stroke), Extent(row), Top(stroke) - Top(row),
                        Extent(line), Top(stroke) - Top(line));
                })
                .ToList();

        // The stroke itself: a 1 DIP wide childless Border, which on this bar is the tally mark and
        // nothing else. Found by shape, because none of the bar's elements are named.
        static List<Border> Strokes(DependencyObject root) =>
            Descendants(root).OfType<Border>().Where(b => b is { Width: 1, Child: null }).ToList();

        static IEnumerable<DependencyObject> Descendants(DependencyObject node) =>
            Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(node))
                .Select(i => VisualTreeHelper.GetChild(node, i))
                .SelectMany(child => new[] { child }.Concat(Descendants(child)));

        // The row: the outermost Border wrapping a Grid on the way up, which is what GroupRow builds
        // around every row's container.
        static FrameworkElement Row(DependencyObject node) =>
            Ancestors(node).OfType<Border>().Last(b => b.Child is Grid);

        static T Ancestor<T>(DependencyObject node) where T : FrameworkElement =>
            Ancestors(node).OfType<T>().First();

        static IEnumerable<DependencyObject> Ancestors(DependencyObject node)
        {
            for (var at = VisualTreeHelper.GetParent(node); at is not null; at = VisualTreeHelper.GetParent(at))
                yield return at;
        }

        double Extent(FrameworkElement element) =>
            element.TransformToAncestor(bar).TransformBounds(new Rect(0, 0, 1, element.ActualHeight)).Height;

        double Top(FrameworkElement element) => element.TransformToAncestor(bar).Transform(new Point(0, 0)).Y;

        double Left(FrameworkElement element) => element.TransformToAncestor(bar).Transform(new Point(0, 0)).X;

        static string Label(DependencyObject row) =>
            Descendants(row).OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => t.Length > 0) ?? "unnamed";
    }
}

// Monitor numbers for the bar and nothing else: the window moves this interface also carries are no
// part of this question.
sealed class StubScreens : IScreenLayout
{
    public ScreenFacts Facts { get; set; } = ScreenFacts.Empty;
    public ScreenFacts Snapshot() => Facts;
    public Maybe<WindowRect> RectOf(WindowHandle window) => Maybe<WindowRect>.None;
    public Result MoveTo(WindowHandle window, WindowRect rect, bool mayChangeShowState) => Result.Success();
}
