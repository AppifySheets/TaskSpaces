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
    // "Does this hwnd still name a window at all?" -- the unambiguous test Resync uses to
    // tell a DESTROYED window from one that merely left the taskbar (minimised to tray).
    [DllImport("user32.dll")] public static extern bool IsWindow(nint hwnd);
    // Used to reject shell-owned windows, which Windows itself never lists on the taskbar.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(nint hwnd, [Out] char[] buffer, int size);
    // Seeds the active-window highlight at startup; EVENT_SYSTEM_FOREGROUND alone only
    // reports CHANGES, so without this nothing is highlighted until the user switches window.
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] public static extern nint GetWindowLongPtr(nint hwnd, int index);
    // Petre: "i can see the taskspaces app in alt+tab, can you hide it from there?"
    //
    // The write half, used in exactly one place: FloatingBar stamps WS_EX_TOOLWINDOW on itself
    // (see FloatingBar.OnSourceInitialized for why ShowInTaskbar="False" was not enough).
    // EntryPoint is spelled out for the same reason GetClassLongPtr's is: the "Ptr" name is a
    // real x64 export, and pinning it to the W variant keeps the resolution unambiguous.
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint SendMessageTimeout(nint hwnd, uint msg, nint wparam, string lparam, uint flags, uint timeoutMs, out nint result);

    // Same export, integer lparam -- WM_GETICON takes no string. A separate declaration
    // rather than a marshalling trick: EntryPoint pins it to the same "W" function the
    // string overload above resolves to, and the two signatures stay independently readable.
    //
    // TIMEOUT, not SendMessage: WM_GETICON is answered by the OWNING process's UI thread,
    // so a hung app would otherwise block ours indefinitely -- and this is called from the
    // floating bar's rebuild, which runs on the dispatcher thread on every window event.
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    public static extern nint SendMessageTimeout(nint hwnd, uint msg, nint wparam, nint lparam, uint flags, uint timeoutMs, out nint result);

    // The class-level icon, the fallback for windows that answer WM_GETICON with nothing.
    // "Ptr" suffix is real on x64 user32 (on 32-bit it is only a macro for GetClassLongW),
    // which is fine -- this project is x64-only, as the file header already notes.
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW", SetLastError = true)]
    public static extern nint GetClassLongPtr(nint hwnd, int index);

    public const uint EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003, EVENT_OBJECT_NAMECHANGE = 0x800C;
    // Petre: "active window should be highlighted in the floating window". Note this one is
    // in the SYSTEM range (0x0003), far below the OBJECT events above, so it needs its own
    // hook rather than widening an existing range -- a single hook spanning 0x0003..0x800C
    // would subscribe us to every event in between.
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    // Fires ONCE, when the user lets go of a window they were dragging or resizing -- which is
    // exactly the moment a window may have landed on a different monitor. The obvious
    // alternative, EVENT_OBJECT_LOCATIONCHANGE, fires continuously throughout the drag and for
    // every animating window on the desktop; this fires once per gesture. Adjacent to
    // FOREGROUND in the SYSTEM range, so the two share one hook.
    public const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;

    // The shell hook: how the taskbar itself is told what windows are doing. Used here for one
    // notification only, HSHELL_FLASH, because flashing is invisible to WinEvents -- measured
    // with a probe that hooked every event in both ranges and saw nothing at all.
    [DllImport("user32.dll")] public static extern bool RegisterShellHookWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool DeregisterShellHookWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessageW(string name);
    // Sent repeatedly WHILE a window flashes, with lParam = that window's hwnd. There is no
    // matching "stopped flashing" notification -- also measured -- so when attention ENDS is a
    // question the app has to answer for itself.
    public const int HSHELL_FLASH = 0x8006;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    public const int OBJID_WINDOW = 0, CHILDID_SELF = 0;
    public const uint GA_ROOT = 2;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080, WS_EX_APPWINDOW = 0x00040000;
    public const uint DWMWA_CLOAKED = 14;
    public const uint WM_SETTEXT = 0x000C;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    // Petre: "i also don't see an icon for whatsapp app". WhatsApp is a Store app whose
    // WhatsApp.Root.exe is a launcher stub with NO embedded icon, so
    // Icon.ExtractAssociatedIcon does not fail -- it quietly hands back the generic Windows
    // default, which is why the bar showed a blank-looking placeholder rather than nothing.
    // The fix is to ask the WINDOW what it is displaying instead of asking the file.
    // ICON_BIG first (usually 32px, so it scales down cleanly to the bar's 20px) then the
    // small variants, then the window class's own icon.
    public const uint WM_GETICON = 0x007F;
    public const nint ICON_SMALL = 0, ICON_BIG = 1, ICON_SMALL2 = 2;
    public const int GCLP_HICON = -14, GCLP_HICONSM = -34;

    // Petre: "i want it to be on top of the taskbar, if i activate the taskbar, it hides the
    // floating window."
    //
    // Topmost is a BAND, not a rank: every topmost window shares one, and whichever was
    // activated most recently sits at its top. The taskbar is topmost too (so is
    // StartAllBack's menu), so activating it climbs over our bar and WPF's Topmost="True"
    // does nothing to prevent it. Re-asserting HWND_TOPMOST moves the bar back to the top of
    // the band. SWP_NOACTIVATE is essential: without it this would steal focus from whatever
    // the user just clicked, including the taskbar they were reaching for.
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    public static readonly nint HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);

    // Windows only grants SetForegroundWindow to a process that already has a claim on the
    // foreground -- it received the last input event, owns the current foreground window, and so
    // on. Attaching our input queue to the CURRENT foreground thread borrows that claim for the
    // length of one call, which is the documented way to hand focus on deliberately rather than
    // have the request silently degrade to a flashing taskbar button.
    //
    // See WindowActivator, which is the only caller and explains why the workspace switcher
    // needs this at all.
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(nint hwnd, int cmdShow);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }

    // Task 7 fix round 1 (reviewer, Important): the switcher panel used to clamp its
    // position to (0,0) -- the PRIMARY monitor's origin -- which is wrong on any layout with
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

    // Petre: "sort icons in workspaces by monitors, first icons from monitor1, then monitor2,
    // etc. and i want to have the monitor number on the icon."
    //
    // MonitorFromWindow rather than MonitorFromPoint: we are asking about a WINDOW, and it
    // answers for a minimised one too (the monitor it would restore onto), which is what keeps
    // a tray-minimised window from jumping position in the bar.
    [DllImport("user32.dll")] public static extern nint MonitorFromWindow(nint hwnd, uint flags);

    // MONITORINFOEX is MONITORINFO plus the device name -- "\\.\DISPLAY1", "\\.\DISPLAY2" --
    // and that trailing number is the one Windows itself shows under Display Settings >
    // Identify. Deriving the badge from it rather than from enumeration ORDER is deliberate:
    // enumeration order carries no guarantee at all, while this is the number Petre can check
    // against his own screen.
    //
    // CharSet.Unicode and the ByValTStr SizeConst=32 are both load-bearing -- CCHDEVICENAME is
    // 32 wide chars, and the struct is memcpy'd by the OS, so a wrong size or width silently
    // yields garbage rather than an error. (This codebase has been bitten by a missing
    // CharSet.Unicode before; see the probe notes in CLAUDE.md.)
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    // Separate declaration from GetMonitorInfo above rather than a shared one: the two take
    // different structs, and the OS decides which it is filling in purely from cbSize.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    public static extern bool GetMonitorInfoEx(nint hMonitor, ref MONITORINFOEX info);

    // dwFlags bit meaning "this is the main monitor". Not the same question as "is this display
    // 1": on the setup this was built against, the primary is DISPLAY2.
    public const uint MONITORINFOF_PRIMARY = 1;

    public delegate bool MonitorEnumProc(nint monitor, nint hdc, ref RECT clip, nint data);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    // Task 11 fix round 2 (reviewer, confirmed root cause of Petre's off-screen
    // floating bar): GetDpiForMonitor (Shcore.dll) asks Windows for a SPECIFIC
    // monitor's DPI directly, independent of any Window's own per-monitor-DPI-
    // awareness negotiation. VisualTreeHelper.GetDpi(window) is scoped to a WINDOW
    // instead, and can read a stale/provisional DPI (scale 1.0) immediately after that
    // window's first Show() -- before its WM_DPICHANGED round-trip has landed -- which
    // is exactly how a monitor's raw physical rcWork ended up written into FloatingBar's
    // DIP-valued Left/Top unconverted. Querying the MONITOR (the same HMONITOR already
    // in hand from MonitorFromPoint) instead of the window removes that race entirely.
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(nint hMonitor, int dpiType, out uint dpiX, out uint dpiY);
    public const int MDT_EFFECTIVE_DPI = 0;

    public const int SW_RESTORE = 9; // NEVER SW_HIDE anywhere in this codebase (spec)
    // Puts a window down without hiding it: still on the taskbar, still on its desktop, still
    // recoverable if we crash. That last point is why SW_HIDE stays banned and this does not.
    public const int SW_MINIMIZE = 6;

    // The app's one global chord: the Alt+Tab-style workspace switcher (Win+Ctrl+Tab by default).
    // RegisterHotKey delivers WM_HOTKEY to the given window's message queue regardless
    // of focus -- exactly what a background tray app needs (no window ever has to be
    // foreground for the chord to fire).
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool UnregisterHotKey(nint hwnd, int id);

    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4;
    public const uint WM_HOTKEY = 0x0312;
    public const uint VK_LEFT = 0x25, VK_RIGHT = 0x27;

    // Alt+Tab-style workspace switching (Petre: "maybe an alt-tab like shortcut for me to
    // switch through workspaces"). The chord is configurable and defaults to Win+Ctrl+Tab; this
    // constant remains because VK_OEM_3 (the backtick/tilde key) was the original default and
    // Chord still has to be able to spell it.
    public const uint VK_OEM_3 = 0xC0;

    // The missing half of that gesture. RegisterHotKey reports a chord being PRESSED and
    // has no concept of release at all, but "hold the modifiers, tap the key, release to
    // commit" is the entire point of Alt+Tab. GetAsyncKeyState is how the release is seen:
    // it reports global key state regardless of which app has focus, so a background tray
    // app can poll it (briefly, only while the picker is on screen).
    //
    // The alternative is a WH_KEYBOARD_LL hook, which would see releases directly but puts
    // this process in the input path of EVERY keystroke on the machine -- a far bigger
    // liability than a 30ms poll that only runs during the gesture itself.
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    public const int VK_CONTROL = 0x11, VK_MENU = 0x12, VK_SHIFT = 0x10; // VK_MENU is Alt
    public const int VK_ESCAPE = 0x1B;
    public const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;    // Win has two keys; either counts as held
    public const int KeyDownBit = 0x8000;               // GetAsyncKeyState's "down right now" bit
}
