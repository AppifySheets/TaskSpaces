using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Windows.Monitoring;

using static NativeMethods;

// Petre: "sort icons in workspaces by monitors, first icons from monitor1, then monitor2, etc.
// and i want to have the monitor number on the icon", plus "can you also identify which window
// is minimized, vs not? or which one is on top?"
//
// All three facts come from one sweep. Cheap next to what an overview build already costs: this
// is user32 calls only, while WindowsByWorkspace makes a DesktopOf COM call per window.
public sealed class ScreenLayout : IScreenLayout
{
    public ScreenFacts Snapshot()
    {
        var (numbers, primary) = MonitorNumbers();

        // EnumWindows hands back windows FRONT TO BACK, so the index in this list is the
        // z-order outright -- no extra call needed. (Enumerate() filters to taskbar candidates
        // but preserves the order it walked them in.)
        var zOrder = TopLevelWindows.Enumerate()
            .Select((hwnd, index) => (hwnd, index))
            .ToDictionary(x => new WindowHandle(x.hwnd), x => x.index);

        var monitorOf = new Dictionary<WindowHandle, int>();
        var minimized = new HashSet<WindowHandle>();

        zOrder.Keys.ToList().ForEach(window =>
        {
            // MONITOR_DEFAULTTONEAREST rather than DEFAULTTONULL: a window dragged mostly off
            // screen still belongs SOMEWHERE, and "nearest" is the same answer Windows itself
            // uses when it has to pick.
            var handle = MonitorFromWindow(window.Value, MONITOR_DEFAULTTONEAREST);
            if (numbers.TryGetValue(handle, out var number)) monitorOf[window] = number;
            if (IsIconic(window.Value)) minimized.Add(window);
        });

        return new ScreenFacts(monitorOf, minimized, zOrder, primary);
    }

    // HMONITOR -> the number Windows shows under Display Settings > Identify.
    //
    // Read out of szDevice ("\\.\DISPLAY1", "\\.\DISPLAY2", ...) rather than taken from the
    // enumeration ORDER, which carries no guarantee whatsoever. Measured on Petre's two-monitor
    // setup while designing this: DISPLAY1 sits at x=-3840 (left) and DISPLAY2 at x=0 and is
    // PRIMARY -- so enumeration order and device number happen to agree there, and so does
    // plain left-to-right position. They would NOT agree on a layout whose primary is not
    // leftmost, and this is the reading that still matches what his screen says when he presses
    // Identify.
    static (Dictionary<nint, int> Numbers, Maybe<int> Primary) MonitorNumbers()
    {
        var numbers = new Dictionary<nint, int>();
        var primary = Maybe<int>.None;
        // The delegate is only used for the duration of this call -- unlike the WinEvent hook
        // in WindowMonitor, EnumDisplayMonitors is synchronous, so there is nothing for the GC
        // to collect out from under us.
        EnumDisplayMonitors(0, 0, (nint monitor, nint _, ref RECT _, nint _) =>
        {
            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfoEx(monitor, ref info) || Number(info.szDevice) is not { } number) return true;
            numbers[monitor] = number;
            // Asked of Windows rather than assumed to be display 1. Measured on the setup this
            // was built against: the PRIMARY there is DISPLAY2, not DISPLAY1 -- so "main
            // monitor" and "monitor 1" are genuinely different questions.
            if ((info.dwFlags & MONITORINFOF_PRIMARY) != 0) primary = number;
            return true;
        }, 0);
        return (numbers, primary);
    }

    // "\\.\DISPLAY12" -> 12. Null for anything that does not end in digits, which is not a
    // shape Windows produces but is cheaper to tolerate than to assume away.
    static int? Number(string device)
    {
        var digits = new string(device.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : null;
    }
}
