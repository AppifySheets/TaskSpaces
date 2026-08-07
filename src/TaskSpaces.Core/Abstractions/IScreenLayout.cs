using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Abstractions;

// Where windows are on the PHYSICAL screen, as opposed to which virtual desktop they are on
// (IVirtualDesktopService). Separate interface because the two answer different questions and
// have nothing in common: a window has both a desktop and a monitor, and neither implies the
// other.
//
// One call returns everything rather than three per-window questions, because all three facts
// are wanted at the same instant -- once per overview build -- and a z-order index is only
// meaningful relative to the enumeration it came from.
public interface IScreenLayout
{
    ScreenFacts Snapshot();
}
