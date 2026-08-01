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
        && (!HasExStyle(hwnd, WS_EX_TOOLWINDOW) || HasExStyle(hwnd, WS_EX_APPWINDOW)); // tool windows skip the taskbar unless they opt back in

    public static IReadOnlyList<nint> Enumerate()
    {
        var found = new List<nint>();
        EnumWindows((hwnd, _) => { if (IsTaskbarCandidate(hwnd)) found.Add(hwnd); return true; }, 0);
        return found;
    }

    static bool HasExStyle(nint hwnd, long style) => (GetWindowLongPtr(hwnd, GWL_EXSTYLE) & style) != 0;

    // Caveat: windows on OTHER virtual desktops report DWM cloaked. Only exclude cloaked
    // windows at initial-snapshot time when they're also invisible; for our purposes a
    // cloaked-but-visible window (other desktop) is still a window we manage.
    static bool IsCloaked(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0
        && cloaked != 0
        && cloaked != 2; // DWM_CLOAKED_SHELL: cloaked by the shell = other virtual desktop = keep it
}
