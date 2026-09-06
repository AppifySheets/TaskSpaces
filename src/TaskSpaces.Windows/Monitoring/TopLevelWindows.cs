namespace TaskSpaces.Windows.Monitoring;

using static NativeMethods;

// Decides which HWNDs the product cares about: roughly "would this show on the taskbar?".
public static class TopLevelWindows
{
    public static bool IsTaskbarCandidate(nint hwnd) =>
        GetAncestor(hwnd, GA_ROOT) == hwnd          // top-level, not a child control
        && IsWindowVisible(hwnd)
        && !IsCloaked(hwnd)                          // UWP ghosts & windows on other desktops still count as visible; cloak check kills true ghosts
        && (GetWindowTextLength(hwnd) > 0            // text, or an explicit taskbar opt-in (see below)
            || HasExStyle(hwnd, WS_EX_APPWINDOW))
        && !IsShellOwned(hwnd)                       // the desktop, the Start button, the IME host: real hwnds with titles that Windows itself never lists
        && (!HasExStyle(hwnd, WS_EX_TOOLWINDOW) || HasExStyle(hwnd, WS_EX_APPWINDOW)); // tool windows skip the taskbar unless they opt back in

    // WHY TEXT IS NOT REQUIRED ANY MORE, and why the alternative is an opt-in rather than nothing.
    //
    // Petre: "i can't see Buzz in HRIS workspace." Buzz was running with its window visible on that
    // desktop, and the bar had never heard of it:
    //
    //   0x1F12EA buzz-desktop vis=1 cloaked=0 owner=0x0 ex=0x00040110 class='Tauri Window' title=''
    //
    // The line above used to read "taskbar buttons always have text", which is what this file believed
    // until a Tauri app that never sets a title turned up. Windows gives that window a button anyway:
    // text is not part of the shell's rule, only of the heuristic here that kept junk out.
    //
    // So the heuristic is now "text OR WS_EX_APPWINDOW" -- an explicit request to be on the taskbar --
    // and the width of that relaxation was measured before it was written. Every window on his machine
    // that the old rule rejected ONLY for having no text:
    //
    //   with WS_EX_APPWINDOW:  1  (Buzz)
    //   without it:            0
    //
    // Dropping the text test outright would have been the same change on that machine and a bigger one
    // on someone else's: a titleless window that never asked for a button is the shape of the helper
    // windows apps leave lying around, and a row spent on one of those is a row wasted.
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
