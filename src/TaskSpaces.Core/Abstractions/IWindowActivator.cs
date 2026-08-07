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
}
