using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Windows.Monitoring;

namespace TaskSpaces.Windows.Renaming;

using static NativeMethods;

/// <summary>
/// Win32 implementation of IWindowTitles using WM_SETTEXT with a timeout.
/// Timeouts protect against hung applications that would otherwise block
/// TaskSpaces indefinitely (SetWindowText would block on an unresponsive window).
/// </summary>
public sealed class Win32WindowTitles : IWindowTitles
{
    /// <summary>
    /// Set a window's title with a 2-second timeout. Returns success only if
    /// SendMessageTimeout succeeds; if the window is closed or hung, returns failure.
    /// </summary>
    public Result Set(WindowHandle window, string title) =>
        Result.SuccessIf(
            SendMessageTimeout(window.Value, WM_SETTEXT, 0, title, SMTO_ABORTIFHUNG, 2000, out _) != 0,
            $"Window {window.Value} did not accept the title (closed or hung).");

    /// <summary>
    /// Get a window's current title via the WindowInfoFactory helper.
    /// </summary>
    public Result<string> Get(WindowHandle window) =>
        Result.Success(WindowInfoFactory.TitleOf(window.Value));
}
