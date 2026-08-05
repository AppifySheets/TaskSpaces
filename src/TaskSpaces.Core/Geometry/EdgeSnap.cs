namespace TaskSpaces.Core.Geometry;

// Petre: "can you snap to edges?"
//
// Pure, and beside WorkAreaClamp for the same reason that one is: everything here is already
// in DIPs by the time it is called, so the maths needs no window, no monitor and no DPI query
// to test.
//
// Snapping happens when a drag ENDS, not while it is in progress. DragMove runs a native move
// loop that owns the mouse, so live snapping would mean fighting it; and a bar that jumps
// under the cursor mid-drag is harder to place deliberately than one that settles when let go.
public static class EdgeSnap
{
    // How close an edge has to come before it is taken as "meant for the edge". About one
    // icon's width: close enough that it never fires on a deliberate placement a little way
    // in from the edge, far enough that nobody has to be precise to the pixel.
    public const double Distance = 20;

    // Snaps each axis independently, so a corner falls out of the two rules rather than
    // needing a case of its own.
    //
    // Each axis prefers the NEAR edge: a bar wider than twice the snap distance can only be in
    // range of one horizontal edge at a time, and if the work area is so narrow that both are
    // in range, the left/top wins by being tested first, which matches WorkAreaClamp pinning
    // to left/top when a box does not fit.
    public static (double Left, double Top) Snap(
        double left, double top, double width, double height,
        double workAreaLeft, double workAreaTop, double workAreaRight, double workAreaBottom) =>
        (
            SnapAxis(left, width, workAreaLeft, workAreaRight),
            SnapAxis(top, height, workAreaTop, workAreaBottom)
        );

    static double SnapAxis(double position, double size, double areaStart, double areaEnd) =>
        Math.Abs(position - areaStart) <= Distance ? areaStart
        : Math.Abs(position + size - areaEnd) <= Distance ? areaEnd - size
        : position;

    // Which edge the bar should GROW from, once it has landed.
    //
    // This is derived rather than remembered, which is the point: a bar sitting against the
    // left edge must grow rightwards, or it walks off the screen -- the mirror image of the
    // problem that made the right edge the anchor in the first place. Deriving it from where
    // the bar actually is means the two can never disagree, and nothing extra has to be
    // persisted or kept in step.
    //
    // True = pin the right edge (the usual case, including a bar floating in open space).
    // False = pin the left edge, which is WPF's own behaviour, so the caller does nothing.
    public static bool GrowsLeftwards(double left, double workAreaLeft) =>
        left > workAreaLeft + Distance;
}
