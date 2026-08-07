using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Abstractions;

// "This window is asking for you." Petre: "can you also identify if an app has something to say,
// a notification, and say it on the icon?... let's say somebody has messaged me, or vscode is
// asking for my attention somewhere."
//
// Both of those are one Windows mechanism: a taskbar button flashing, either because the app
// called FlashWindowEx or because it tried to take the foreground while you were in something
// else and Windows flashed it instead of letting it steal focus.
//
// Deliberately NOT folded into IWindowMonitor, despite both being "things windows do". They have
// no mechanism in common: window lifecycle comes from WinEvent hooks, and a flash is invisible to
// those -- measured, not assumed. A probe hooked every event in the SYSTEM and OBJECT ranges
// while flashing a window and saw exactly nothing. The shell hook is a different subscription
// with a different lifetime, and merging them would only hide that.
public interface IAttentionMonitor
{
    Result Start();

    // Fires repeatedly while a window is flashing -- it is a heartbeat, not a one-shot, and
    // there is no matching "stopped" notification at all (also measured). Whoever consumes this
    // therefore owns the question of when attention ENDS; the OS will not say.
    IObservable<WindowHandle> Flashed { get; }
}
