namespace TaskSpaces.Core.Domain;

// A single instant's answer to "where is each window on the physical screen, and what state is
// it in". Gathered in one pass (see IScreenLayout) because a z-order index only means anything
// relative to the enumeration that produced it.
//
// Empty is the honest default everywhere this is unavailable -- compatibility mode, and every
// test that predates it. It reads as "no monitor known, nothing minimised, no z-order", which
// leaves the bar sorting and rendering exactly as it did before any of this existed.
public sealed record ScreenFacts(
    // Windows' own display number -- the 1, 2, ... you see under Display Settings > Identify.
    // Absent for a window whose monitor could not be resolved; those sort last rather than
    // disappearing, on the same principle as the overview's "Unplaced" group.
    IReadOnlyDictionary<WindowHandle, int> MonitorOf,
    IReadOnlySet<WindowHandle> Minimized,
    // Position in the OS's front-to-back order: 0 is the front-most window. Only ever populated
    // for the CURRENT virtual desktop, because EnumWindows skips cloaked windows and every
    // window on another desktop is cloaked. That is a limit worth keeping rather than working
    // around: "which one is on top" is a question about the screen you are looking at.
    IReadOnlyDictionary<WindowHandle, int> ZOrder)
{
    public static readonly ScreenFacts Empty = new(
        new Dictionary<WindowHandle, int>(),
        new HashSet<WindowHandle>(),
        new Dictionary<WindowHandle, int>());
}
