using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Abstractions;

// Bring a window to the foreground (un-minimizing if needed). Windows-layer concern
// (SetForegroundWindow); abstracted so JumpTo is testable with a fake.
public interface IWindowActivator
{
    Result Activate(WindowHandle window);

    // ...and put it away again. The other half of the bar's click toggle (Petre: "i want to be
    // able to minimize windows from the floating bar"), and the exact inverse of what Activate
    // does to a minimized window, so the two belong on the same interface rather than in a
    // second one that would inevitably be handed around alongside it.
    Result Minimize(WindowHandle window);

    // Is it down right now? A plain bool with no Result, because IsIconic cannot fail and cannot
    // be refused: it reads a window's own style bits, unlike the two calls above, which ask the
    // OS to DO something and can be denied.
    //
    // Here rather than on IScreenLayout (which also reports minimised state) because those facts
    // are a SNAPSHOT taken during a rebuild, and the whole reason this exists is that a snapshot
    // is exactly what cannot be trusted at the moment a click is decided (see
    // WorkspaceManager.ToggleWindow).
    bool IsMinimized(WindowHandle window);
}
