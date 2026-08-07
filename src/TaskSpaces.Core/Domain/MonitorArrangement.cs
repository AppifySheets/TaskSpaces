namespace TaskSpaces.Core.Domain;

// Where a display sits on the virtual screen, in the OS's own pixel coordinates. Left and Top
// are routinely NEGATIVE: Windows puts the primary display's top-left at 0,0, so anything placed
// to its left or above it gets negative coordinates. On Petre's machine DISPLAY1 sits at
// x = -2560 and DISPLAY2 (the primary) at x = 0.
public sealed record MonitorBounds(int Left, int Top, int Right, int Bottom);

// Petre: "i'd like to arrange windows by my monitors -- my left monitor first, my right monitor
// next, if there are other monitors, follow in that order, from top left to the right and down."
//
// This replaces two DIFFERENT orderings that used to disagree with each other, and that
// disagreement was the bug rather than a tidiness problem. Icons were grouped by display NUMBER
// while the unmarked ("silent") group was chosen by which display was PRIMARY. Silence only
// reads as a mark when it falls on the group that comes FIRST -- and on Petre's machine the
// primary is DISPLAY2, which sorts second, so the row opened with a hairline and the boundary
// between his two groups had nothing at all to show it. "i only see hairlines at the beginning
// of every workspace."
//
// One order now decides both, so that class of bug cannot come back: the leading group is
// unmarked BECAUSE it leads.
//
// Why display numbers cannot be the order: Windows numbers displays by the order they were
// enumerated, which has nothing to do with where they physically are. Position does, and
// position is what Petre navigates by -- he reaches for the left screen's windows on the left.
public static class MonitorArrangement
{
    // Displays in reading order: left to right across a row of screens, then down to the next.
    //
    // The banding is the whole subtlety, and Petre's own setup is exactly why it is needed. His
    // screens are side by side but vertically offset by 153px (DISPLAY2 at y=0, DISPLAY1 at
    // y=153). A plain "topmost first, then leftmost" would seize on that 153px and put the RIGHT
    // screen first -- the opposite of what he asked for -- because its top edge is higher.
    //
    // So displays whose vertical ranges OVERLAP are treated as one row and sorted purely left to
    // right; a display only starts a new row when it begins at or below the bottom of the row
    // being built. Two side-by-side screens are one row however badly aligned they are, while a
    // genuine 2x2 grid still reads top-left, top-right, bottom-left, bottom-right.
    public static IReadOnlyList<int> ReadingOrder(IReadOnlyDictionary<int, MonitorBounds> monitors) =>
        monitors
            // Seeded in top-then-left order so each band is begun by its own topmost display and
            // the walk below only ever has to look at the band it is currently building.
            .OrderBy(m => m.Value.Top)
            .ThenBy(m => m.Value.Left)
            .Aggregate(new List<List<KeyValuePair<int, MonitorBounds>>>(), (bands, monitor) =>
            {
                // Strictly less than, so screens that merely TOUCH edge to edge (a display
                // stacked exactly below another) start a new row rather than joining this one.
                if (bands.LastOrDefault() is { } band && monitor.Value.Top < band.Max(m => m.Value.Bottom))
                    band.Add(monitor);
                else
                    bands.Add([monitor]);
                return bands;
            })
            .SelectMany(band => band.OrderBy(m => m.Value.Left).Select(m => m.Key))
            .ToList();
}
