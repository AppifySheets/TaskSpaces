using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Abstractions;

// Source of truth for "what windows exist". Start() MUST be called on a thread that
// pumps messages (the WPF dispatcher thread) — WinEvent callbacks arrive there.
public interface IWindowMonitor
{
    Result Start();
    IObservable<WindowEvent> Events { get; }
    IReadOnlyList<WindowInfo> Snapshot();

    // Whichever window has focus RIGHT NOW. Needed because EVENT_SYSTEM_FOREGROUND only
    // fires on a CHANGE: without seeding, the active-window highlight would stay blank from
    // launch until the user next switched windows, which reads as the feature being broken.
    // None when the foreground window is not one we track (or there is none).
    Maybe<WindowHandle> Foreground();
}
