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
        var (numbers, primary, placement) = MonitorNumbers();

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

        return new ScreenFacts(monitorOf, minimized, zOrder, primary, placement);
    }

    // #89. Physical pixels, not DIPs: this is what SetWindowPos speaks, and MonitorMove does its
    // arithmetic in the same units as the monitor bounds that came out of GetMonitorInfoEx above, so
    // nothing here converts anything. The app is per-monitor-DPI-aware, which is what makes that
    // true across screens at different scales.
    public Maybe<WindowRect> RectOf(WindowHandle window) =>
        GetWindowRect(window.Value, out var rect)
            ? new WindowRect(rect.Left, rect.Top, rect.Right, rect.Bottom)
            : Maybe<WindowRect>.None;

    public Result MoveTo(WindowHandle window, WindowRect rect)
    {
        // A maximized window has to come down first. Its rectangle is the monitor's while it is
        // maximized, so SetWindowPos on it is accepted and then ignored, and the window snaps back to
        // the old monitor the moment anything restores it. Restore, move, maximize again: it ends up
        // maximized on the new screen, which is what the gesture asked for.
        var maximized = IsZoomed(window.Value);
        if (maximized) ShowWindowAsync(window.Value, SW_RESTORE);

        // SWP_NOACTIVATE for the same reason the bar's topmost re-assert uses it: this runs from a
        // drop, and the pointer is over the bar, so pulling the moved window to the foreground would
        // take focus from whatever the user was actually working in. SWP_NOZORDER leaves the stack
        // alone as well: moving a window between screens is not a request to raise it.
        //
        // The insertAfter handle is ignored under SWP_NOZORDER, hence 0.
        var moved = SetWindowPos(window.Value, 0, rect.Left, rect.Top, rect.Width, rect.Height,
            SWP_NOACTIVATE | SWP_NOZORDER);

        if (maximized) ShowWindowAsync(window.Value, SW_MAXIMIZE);

        // A dead handle, a window belonging to an elevated process, or one that simply refuses to be
        // moved. All the same outcome to the caller, and all ordinary: the bar reports the message and
        // nothing else changes.
        return moved || maximized
            ? Result.Success()
            : Result.Failure("Windows would not move that window.");
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
    // ...and, in the same sweep, WHERE each display sits. The geometry is what orders the icon
    // groups now (see MonitorArrangement): Petre asked for "my left monitor first, my right
    // monitor next... from top left to the right and down", and enumeration order cannot answer
    // that -- his own DISPLAY1 is the LEFT screen while DISPLAY2 is the primary on the right.
    //
    // rcMonitor, not rcWork: the taskbar's cutout has nothing to do with which screen is further
    // left, and rcWork would make two identically-placed screens differ for no reason.
    static (Dictionary<nint, int> Numbers, Maybe<int> Primary, Dictionary<int, MonitorBounds> Placement) MonitorNumbers()
    {
        var numbers = new Dictionary<nint, int>();
        var placement = new Dictionary<int, MonitorBounds>();
        var primary = Maybe<int>.None;
        // The delegate is only used for the duration of this call -- unlike the WinEvent hook
        // in WindowMonitor, EnumDisplayMonitors is synchronous, so there is nothing for the GC
        // to collect out from under us.
        EnumDisplayMonitors(0, 0, (nint monitor, nint _, ref RECT _, nint _) =>
        {
            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfoEx(monitor, ref info) || Number(info.szDevice) is not { } number) return true;
            numbers[monitor] = number;
            placement[number] = new MonitorBounds(
                info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom);
            // Asked of Windows rather than assumed to be display 1. Measured on the setup this
            // was built against: the PRIMARY there is DISPLAY2, not DISPLAY1 -- so "main
            // monitor" and "monitor 1" are genuinely different questions.
            if ((info.dwFlags & MONITORINFOF_PRIMARY) != 0) primary = number;
            return true;
        }, 0);
        return (numbers, primary, placement);
    }

    // "\\.\DISPLAY12" -> 12. Null for anything that does not end in digits, which is not a
    // shape Windows produces but is cheaper to tolerate than to assume away.
    static int? Number(string device)
    {
        var digits = new string(device.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : null;
    }
}
