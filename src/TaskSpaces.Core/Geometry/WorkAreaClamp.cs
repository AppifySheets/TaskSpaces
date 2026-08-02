namespace TaskSpaces.Core.Geometry;

// Task 11 fix round 2 (reviewer): pure clamp math extracted out of
// FloatingBar.xaml.cs's PositionFromState so it is unit-testable without a running
// WPF window, a real monitor, or any DPI query at all -- everything here is already
// in the SAME unit (DIPs), by the time it's called. The bug this guards against
// (Petre's bar landing at Left=2408 on a monitor whose DIP-space work area only goes
// to ~2048) was upstream of this math -- a stale/wrong DPI scale fed physical pixel
// values in here as if they were DIPs -- but this clamp is the last line of defense:
// whatever units the caller accidentally hands it, the result is guaranteed to sit
// fully inside the given work area (or as close as the area allows, if the area is
// narrower than the window itself).
public static class WorkAreaClamp
{
    // Clamps a proposed top-left corner (left, top) so a WIDTH x HEIGHT box starting
    // there stays entirely inside [workAreaLeft, workAreaTop, workAreaRight,
    // workAreaBottom]. If the work area is narrower/shorter than the box, pins to the
    // area's own left/top edge rather than producing a negative-width overlap (same
    // Math.Max guard SwitcherPanel.PositionNear already uses).
    public static (double Left, double Top) Clamp(
        double left, double top, double width, double height,
        double workAreaLeft, double workAreaTop, double workAreaRight, double workAreaBottom) =>
        (
            Math.Clamp(left, workAreaLeft, Math.Max(workAreaLeft, workAreaRight - width)),
            Math.Clamp(top, workAreaTop, Math.Max(workAreaTop, workAreaBottom - height))
        );
}
