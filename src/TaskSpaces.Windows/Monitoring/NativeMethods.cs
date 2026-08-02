using System.Runtime.InteropServices;
using System.Text;

namespace TaskSpaces.Windows.Monitoring;

// All P/Invoke in one place. x64-only (GetWindowLongPtr does not exist on 32-bit user32).
// Public (not internal): Task 7's switcher panel lives in TaskSpaces.App and needs
// GetCursorPos to position itself near the cursor.
public static class NativeMethods
{
    public delegate void WinEventProc(nint hook, uint @event, nint hwnd, int idObject, int idChild, uint thread, uint time);
    public delegate bool EnumWindowsProc(nint hwnd, nint lparam);

    [DllImport("user32.dll")] public static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc, WinEventProc proc, uint idProcess, uint idThread, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, nint lparam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] public static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] public static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint SendMessageTimeout(nint hwnd, uint msg, nint wparam, string lparam, uint flags, uint timeoutMs, out nint result);

    public const uint EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003, EVENT_OBJECT_NAMECHANGE = 0x800C;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    public const int OBJID_WINDOW = 0, CHILDID_SELF = 0;
    public const uint GA_ROOT = 2;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080, WS_EX_APPWINDOW = 0x00040000;
    public const uint DWMWA_CLOAKED = 14;
    public const uint WM_SETTEXT = 0x000C;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(nint hwnd, int cmdShow);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }

    // Task 7 fix round 1 (reviewer, Important): the switcher panel used to clamp its
    // position to (0,0) — the PRIMARY monitor's origin — which is wrong on any layout with
    // a monitor placed LEFT of primary (negative virtual-screen coordinates put the panel on
    // the wrong screen entirely). These let the panel ask Windows which monitor the cursor is
    // actually on, and clamp inside THAT monitor's work area (which excludes the taskbar).
    [DllImport("user32.dll")] public static extern nint MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO info);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor; // full monitor bounds
        public RECT rcWork;    // bounds minus taskbar/docked toolbars
        public uint dwFlags;
    }
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    public const int SW_RESTORE = 9; // NEVER SW_HIDE anywhere in this codebase (spec)
}
