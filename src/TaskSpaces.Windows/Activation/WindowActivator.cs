using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Windows.Monitoring;

namespace TaskSpaces.Windows.Activation;

using static NativeMethods;

// "Bring it to me": restore if minimized, then foreground. Called from a click inside
// OUR focused panel, which is exactly the situation where Windows grants
// SetForegroundWindow permission — outside that, it degrades to a taskbar flash,
// which is acceptable best-effort behavior, not an error worth failing loudly on.
public sealed class WindowActivator : IWindowActivator
{
    public Result Activate(WindowHandle window) =>
        Result.Try(() =>
        {
            if (IsIconic(window.Value)) ShowWindowAsync(window.Value, SW_RESTORE);
            SetForegroundWindow(window.Value);
        }, e => $"Could not activate window {window.Value}: {e.Message}");

    // SW_MINIMIZE, emphatically NOT SW_HIDE — a hidden window can be orphaned by a crash with
    // no way back, which is the reason this codebase is built on virtual desktops in the first
    // place (see the spec). A minimized window is still on the taskbar and still on its desktop;
    // it has simply been put down.
    //
    // Async, like the restore above: ShowWindowAsync posts rather than waiting for the target's
    // message loop to answer, so a hung window cannot block the bar's click handler.
    //
    // No IsIconic guard. Minimizing an already-minimized window is a no-op in Windows, and the
    // one caller only reaches this for the FOCUSED window, which by definition is not minimized.
    public Result Minimize(WindowHandle window) =>
        Result.Try(() => ShowWindowAsync(window.Value, SW_MINIMIZE),
            e => $"Could not minimize window {window.Value}: {e.Message}");
}
