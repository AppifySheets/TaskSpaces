namespace TaskSpaces.Windows.Monitoring;

using static NativeMethods;

// Decides which HWNDs the product cares about: roughly "would this show on the taskbar?".
public static class TopLevelWindows
{
    public static bool IsTaskbarCandidate(nint hwnd) =>
        GetAncestor(hwnd, GA_ROOT) == hwnd          // top-level, not a child control
        && IsWindowVisible(hwnd)
        && !IsCloaked(hwnd)                          // UWP ghosts & windows on other desktops still count as visible; cloak check kills true ghosts
        && GetWindowTextLength(hwnd) > 0             // taskbar buttons always have text
        && !IsShellOwned(hwnd)                       // the desktop, the Start button, the IME host: real hwnds with titles that Windows itself never lists
        && (!HasExStyle(hwnd, WS_EX_TOOLWINDOW) || HasExStyle(hwnd, WS_EX_APPWINDOW)); // tool windows skip the taskbar unless they opt back in

    public static IReadOnlyList<nint> Enumerate()
    {
        var found = new List<nint>();
        EnumWindows((hwnd, _) => { if (IsTaskbarCandidate(hwnd)) found.Add(hwnd); return true; }, 0);
        return found;
    }

    static bool HasExStyle(nint hwnd, long style) => (GetWindowLongPtr(hwnd, GWL_EXSTYLE) & style) != 0;

    // Petre, seeing "Windows Input Experience" alone in the bar's Unplaced row: "should i be
    // seeing unplaced?" No: the row was doing its job, it was catching junk. The fix belongs
    // here, at the source, rather than by hiding the row -- a real window the virtual-desktop
    // API cannot resolve must still show up somewhere.
    //
    // Matched on WINDOW CLASS, and the alternative was measured and rejected first. The
    // obvious approach is DWM's cloaked attribute, but on Petre's machine TextInputHost
    // reports cloak reason 2 (DWM_CLOAKED_SHELL) -- the SAME value as Beeper, RDM and every
    // other real window sitting on another virtual desktop. Filtering on that would have
    // hidden every off-desktop window and gutted the app.
    //
    // Class names, verified against a dump of every visible titled window on his machine:
    //   Windows.UI.Core.CoreWindow  UWP SHELL components (TextInputHost/IME, SearchHost,
    //                               ShellExperienceHost). Real Store apps are framed by
    //                               ApplicationFrameWindow instead, so they are unaffected --
    //                               and nothing else in the dump used this class.
    //   Progman / WorkerW           the desktop itself, titled "Program Manager".
    //   Shell_TrayWnd / ...         the taskbar and its parts.
    //   Button                      explorer's Start button, which really is a top-level
    //                               window with a title. Safe to exclude: "Button" is a
    //                               control class, so a top-level app window will not use it.
    static readonly IReadOnlySet<string> ShellClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Windows.UI.Core.CoreWindow",
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Button",
    };

    static bool IsShellOwned(nint hwnd)
    {
        var buffer = new char[256];
        var length = GetClassNameW(hwnd, buffer, buffer.Length);
        return length > 0 && ShellClasses.Contains(new string(buffer, 0, length));
    }

    // Caveat: windows on OTHER virtual desktops report DWM cloaked. Only exclude cloaked
    // windows at initial-snapshot time when they're also invisible; for our purposes a
    // cloaked-but-visible window (other desktop) is still a window we manage.
    static bool IsCloaked(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0
        && cloaked != 0
        && cloaked != 2; // DWM_CLOAKED_SHELL: cloaked by the shell = other virtual desktop = keep it
}
