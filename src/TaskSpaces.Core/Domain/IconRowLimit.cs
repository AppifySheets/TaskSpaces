namespace TaskSpaces.Core.Domain;

// Petre: "if any one workspace grows too wide, then it's inefficient, because the icons in
// other workspaces are kind of not taking up much space, so they're empty."
//
// True: the bar is as wide as its widest row, so one busy workspace stretches every other
// lane into empty space. Beyond this many icons a row wraps onto another line, trading width
// for height.
//
// His first proposal was adaptive -- cap at (second-widest row + 1), so the bar grows only
// when two workspaces genuinely need it. Rejected together, for STABILITY: that limit is
// recomputed on every rebuild, so closing one window in a quiet workspace would re-wrap a busy
// one, and this window is SizeToContent anchored on its RIGHT edge, so every width change
// re-lays-out and repositions it. A bar that shuffles as windows come and go is worse than a
// bar that is occasionally taller than it needs to be.
//
// A fixed limit cannot oscillate. Five is not arbitrary: the info line below the rows is a
// fixed 168 DIP wide (the ↩ button at 18 plus Info at 150), which already sets the bar's
// MINIMUM width, and about five icon cells plus a label gutter is what fits inside it.
// Wrapping tighter would make the bar taller without making it any narrower, because the info
// line would still be holding that width open.
public static class IconRowLimit
{
    // Change this and rebuild. Deliberately NOT a state.json setting, unlike BarScale: that is
    // a taste dial with no correct value, while this one is determined by the info line's
    // width, so it gets found once and never touched again.
    public const int IconsPerLine = 5;

    // The icons of one group, split into the lines they should be drawn on. Pure and here
    // rather than in the bar so the boundaries are testable without constructing a window.
    //
    // An empty group yields NO lines rather than one empty line: an empty workspace's row is
    // just its label, and an empty horizontal panel would still claim an icon's height.
    public static IReadOnlyList<IReadOnlyList<T>> Lines<T>(IEnumerable<T> icons) =>
        icons.Chunk(IconsPerLine).Select(line => (IReadOnlyList<T>)line).ToList();
}
