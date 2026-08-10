namespace TaskSpaces.Core.Domain;

// A window's rectangle in virtual-screen coordinates, which is what GetWindowRect reports and what
// SetWindowPos takes. Right and Bottom are EXCLUSIVE, matching Win32's RECT, so a 1920-wide window
// at x=0 has Right = 1920.
public sealed record WindowRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

// Where a window should land when it is sent to another monitor (#89). Petre: "dropping the icon
// onto another monitor within the current workspace -- drag the icon across its own row's hairline
// to send the window to the other screen."
//
// The geometry is here rather than in the Win32 call for the usual reason in this codebase: it is the
// part that can be wrong in ways nobody notices for a week, and it is the part a test can pin down.
// The call site does two things only, both unarguable: read the current rectangle, write the new one.
public static class MonitorMove
{
    // The rectangle a window should occupy on `target`, given where it sits on `source` now.
    //
    // PROPORTIONAL, not absolute: the position and size are expressed as fractions of the source
    // monitor and re-applied to the target. That is what Windows' own Win+Shift+Arrow does, and the
    // reason it is right here has nothing to do with imitation. Monitors differ in resolution, so
    // copying the offset would put a window centred on a 3840-wide screen off the right-hand edge of
    // a 1920-wide one, and copying the size would make a maximized-ish window on the small screen
    // overflow the large one. Fractions survive both.
    //
    // The window is CLAMPED into the target afterwards, which is not the same as scaling and matters
    // for the degenerate cases: a window hanging off the edge of its own monitor (its fractions are
    // outside 0..1) must still arrive somewhere reachable, because the point of the gesture is to see
    // the thing you moved.
    //
    // A window WIDER OR TALLER than the target keeps as much as fits rather than being shrunk to it.
    // Shrinking would silently resize a window the user never asked to resize, and a window that
    // arrives too big can be resized by hand; one that arrives resized has lost information.
    public static WindowRect Fit(WindowRect window, MonitorBounds source, MonitorBounds target)
    {
        var sourceWidth = Math.Max(1, source.Right - source.Left);
        var sourceHeight = Math.Max(1, source.Bottom - source.Top);
        var targetWidth = target.Right - target.Left;
        var targetHeight = target.Bottom - target.Top;

        // Rounded rather than truncated so a window at the exact centre of one screen arrives at the
        // centre of the other rather than a pixel to its left.
        var width = (int)Math.Round((double)window.Width * targetWidth / sourceWidth);
        var height = (int)Math.Round((double)window.Height * targetHeight / sourceHeight);
        var left = target.Left + (int)Math.Round((double)(window.Left - source.Left) * targetWidth / sourceWidth);
        var top = target.Top + (int)Math.Round((double)(window.Top - source.Top) * targetHeight / sourceHeight);

        // At least one pixel each way. A degenerate rectangle is a window nobody can find or grab,
        // and Win32 will happily create one.
        width = Math.Clamp(width, 1, Math.Max(1, targetWidth));
        height = Math.Clamp(height, 1, Math.Max(1, targetHeight));

        // Clamped so the whole window is inside the target, unless it does not fit, in which case its
        // top left corner is: the title bar and the left edge are what you grab, so those are the
        // parts worth guaranteeing are on screen.
        left = Math.Clamp(left, target.Left, Math.Max(target.Left, target.Right - width));
        top = Math.Clamp(top, target.Top, Math.Max(target.Top, target.Bottom - height));

        return new WindowRect(left, top, left + width, top + height);
    }

    // Which monitor a rectangle is on, by the same rule Windows uses when it has to pick: the display
    // it overlaps most, and failing any overlap the nearest one.
    //
    // Needed because the move is expressed as "from where it is now to there", and the window's own
    // monitor is the source of the fractions above. ScreenFacts already answers this for a live
    // window, but a window can be dragged between the overview and the drop, and reading the
    // rectangle and deciding from it keeps the two halves of one calculation consistent.
    public static int? MonitorOf(WindowRect window, IReadOnlyDictionary<int, MonitorBounds> monitors) =>
        monitors.Count == 0
            ? null
            : monitors
                .OrderByDescending(m => Overlap(window, m.Value))
                .ThenBy(m => Distance(window, m.Value))
                .Select(m => (int?)m.Key)
                .First();

    static long Overlap(WindowRect window, MonitorBounds monitor) =>
        (long)Math.Max(0, Math.Min(window.Right, monitor.Right) - Math.Max(window.Left, monitor.Left))
        * Math.Max(0, Math.Min(window.Bottom, monitor.Bottom) - Math.Max(window.Top, monitor.Top));

    // Squared distance between centres, which only ever breaks a tie between monitors the window does
    // not touch at all. Squared because ordering is all that is wanted and a square root would only
    // add a rounding step.
    static long Distance(WindowRect window, MonitorBounds monitor)
    {
        var dx = (long)((window.Left + window.Right) / 2 - (monitor.Left + monitor.Right) / 2);
        var dy = (long)((window.Top + window.Bottom) / 2 - (monitor.Top + monitor.Bottom) / 2);
        return dx * dx + dy * dy;
    }
}
