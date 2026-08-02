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
}
