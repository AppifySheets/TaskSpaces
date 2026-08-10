using CSharpFunctionalExtensions;
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

    // Where one window is, right now, and where to put it (#89: drop an icon on another monitor).
    //
    // Per-window rather than part of the snapshot above, because these serve a deliberate gesture
    // rather than a rebuild: one window, once, at the moment it is dropped. Putting them in
    // ScreenFacts would mean reading every window's rectangle on every overview build to answer a
    // question asked once a day.
    //
    // Maybe rather than a Result on the read: a handle that has died between the drag and the drop is
    // an ordinary outcome on this bar, not a failure worth reporting to anyone.
    Maybe<WindowRect> RectOf(WindowHandle window);

    // Restores a maximized window before moving it and maximizes it again afterwards, because a
    // maximized window's rectangle belongs to the monitor it is maximized on: writing a new one has no
    // effect at all until something un-maximizes it, and then it snaps back to where it was.
    //
    // `mayChangeShowState` is false for a window that is NOT on the desktop you are standing on, and it
    // is the guard on that un-maximizing. Bringing a window down and back up is a visible act, and doing
    // it to a window on another desktop makes Windows follow the window to ITS desktop -- the app
    // yanking you somewhere you did not ask to go. So for one of those, the geometry is written and
    // nothing else is touched: if it takes, good, and if it does not, the caller queues it for when the
    // window is somewhere it can be handled properly.
    Result MoveTo(WindowHandle window, WindowRect rect, bool mayChangeShowState);
}
