using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TaskSpaces.Core;
using TaskSpaces.Windows.Monitoring;

namespace TaskSpaces.App;

// The switcher: every window across every workspace in one place (spec) — the answer
// to "I need to see all windows, similar to taskbar, without changing desktop first".
// One instance lives for the app's lifetime; each summon rebuilds content fresh.
//
// Task 10: group/row construction (AddGroup/WorkspaceHeader/RunningRow/RosterRow/
// RunningMenu, the Report helpers) moved out into the shared WindowGroupsView (Groups,
// declared in the XAML) — ManageWindow's Windows tab hosts the exact same control now.
// This class keeps only the popup-shell machinery that is specific to being a
// tray-summoned, borderless, topmost window: hover-to-peek, summon-point proximity,
// multi-monitor positioning, and the child-dialog coordination that Groups needs
// injected (RunChildDialog<T> below is passed to Groups.Bind as its runChildDialog hook).
public partial class SwitcherPanel : Window
{
    // Task 7 fix round 1 (reviewer, Important): PromptDialog/OpenFileDialog/MessageBox all
    // steal focus from the panel, which fires OnDeactivated -> Hide() the instant they open —
    // defeating the "panel stays open so Petre can act on several rows in a row" design (spec).
    // Every child-dialog invocation runs through RunChildDialog, which sets this flag first;
    // OnDeactivated checks it and skips Hide() while it's true.
    bool childDialogOpen;

    // Task 9 (spec §Tray interaction & hotkeys): hover-to-peek. While true, the panel is
    // visible but was never Activate()d — OnDeactivated must not hide it (it's not
    // focused anyway, so a stray deactivation means nothing), and the proximity timer
    // (not OnDeactivated) governs when it disappears.
    bool peekMode;
    readonly System.Windows.Threading.DispatcherTimer proximityTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // Fix round 2 (Petre: panel blinked while hovering the tray icon): the cursor
    // location at the moment Peek() was called — physical screen pixels, same raw
    // units GetCursorPos returns, converted to DIPs alongside the live cursor reading
    // in ProximityTick (see there) rather than up front, since DPI can only be read
    // reliably once the window is shown. This is the tray icon's position: when peeking
    // from the tray, the cursor sits ON THE ICON, never inside the panel itself (which
    // is positioned near, not under, the cursor) — without also treating that point as
    // "still hovering", ProximityTick correctly finds "outside the panel" on every
    // tick, hides ~250ms after every Peek, and the hover timer immediately re-Peeks —
    // a hide/show strobe.
    double summonScreenX, summonScreenY;

    // DIP margin around the panel's own bounds inside which the cursor keeps it open —
    // without slack the panel would vanish the instant the mouse crosses its own edge
    // while moving in to click something near the border.
    const double ProximityMarginDip = 24;

    // Fix round 2: generous radius (DIPs) around the summon point (see summonScreenX/Y
    // above) that also counts as "still hovering" — covers the tray icon itself plus
    // slack for imprecise mousing, without being so large it never lets go.
    const double SummonRadiusDip = 48;

    public SwitcherPanel(WorkspaceManager manager)
    {
        InitializeComponent();
        // RunChildDialog<object?> closes over `this` and matches the non-generic
        // Func<Func<object?>, object?> shape Groups.Bind expects exactly — no wrapper
        // lambda needed. Hide is the afterAction: a successful jump/switch/start closes
        // the panel, same as before extraction.
        Groups.Bind(manager, RunChildDialog<object?>, Hide);
        proximityTimer.Tick += (_, _) => ProximityTick();
    }

    // Fix round 1 (reviewer, Important): Summon(screenX, screenY) — the click-opened,
    // Activate()-ing summon path — used to live here. Task 9 deleted its only caller
    // (App.xaml.cs's TrayLeftMouseUp handler; left-click now opens the tray menu
    // instead) and nothing else ever called it, making it dead code. Removed outright
    // rather than kept "just in case" — Peek() below is now the only way this panel
    // gets shown, and PositionNear (shared by both) is unchanged.

    // Task 9: hover summon — the only way the panel is shown now that Summon() is gone.
    // Deliberately does NOT Activate() — spec: "opens the switcher panel WITHOUT
    // stealing focus". A no-op if already visible (an already-open — peeking or
    // graduated-to-focused, see OnPreviewMouseDown — panel isn't repositioned out from
    // under the cursor mid-interaction).
    public void Peek(double screenX, double screenY)
    {
        if (IsVisible) return;
        Groups.Refresh();
        ShowActivated = false;
        peekMode = true;
        summonScreenX = screenX;
        summonScreenY = screenY;
        Show();
        PositionNear(screenX, screenY);
        proximityTimer.Start();
    }

    // Polls the cursor rather than relying on WPF mouse-leave events: the panel was never
    // activated, and MouseLeave on an unfocused, unowned popup-style window is unreliable
    // across monitors/DPI boundaries — GetCursorPos + a bounds check is simple and correct.
    void ProximityTick()
    {
        NativeMethods.GetCursorPos(out var cursor);
        // GetCursorPos answers in physical screen pixels; Left/Top/ActualWidth/ActualHeight
        // are DIPs (same mismatch PositionNear guards against) — divide by this window's
        // DPI scale before comparing, or a scaled display hides/strobes at the wrong
        // distance from the panel.
        var dpi = VisualTreeHelper.GetDpi(this);
        var cursorX = cursor.X / dpi.DpiScaleX;
        var cursorY = cursor.Y / dpi.DpiScaleY;
        var insidePanel = cursorX >= Left - ProximityMarginDip && cursorX <= Left + ActualWidth + ProximityMarginDip
            && cursorY >= Top - ProximityMarginDip && cursorY <= Top + ActualHeight + ProximityMarginDip;

        // Fix round 2 (Petre: panel blinked while hovering the tray icon) — see
        // summonScreenX/Y's comment above. The summon point is stored in the same raw
        // physical-pixel units as GetCursorPos, so it's converted here alongside the
        // live cursor reading rather than once at Peek() time.
        var summonDipX = summonScreenX / dpi.DpiScaleX;
        var summonDipY = summonScreenY / dpi.DpiScaleY;
        var dx = cursorX - summonDipX;
        var dy = cursorY - summonDipY;
        var nearSummonPoint = dx * dx + dy * dy <= SummonRadiusDip * SummonRadiusDip;

        if (insidePanel || nearSummonPoint) return;

        proximityTimer.Stop();
        peekMode = false;
        ShowActivated = true; // reset so the next Peek() starts from the same unfocused state
        Hide();
    }

    // Anchors the panel's bottom-right corner at the cursor (tray icons live bottom-right of
    // their own monitor) and clamps it fully inside THAT monitor's work area — never the
    // primary monitor's — so a monitor placed left/above primary (negative virtual-screen
    // coordinates) still gets the panel on the correct screen, never spilling over the taskbar.
    void PositionNear(double screenX, double screenY)
    {
        var cursor = new NativeMethods.POINT { X = (int)screenX, Y = (int)screenY };
        var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            // Best-effort fallback if the API ever fails — better than crashing the summon.
            Left = Math.Max(0, screenX - 320);
            Top = Math.Max(0, screenY - 24 - 660);
            return;
        }

        // GetMonitorInfo answers in physical pixels; WPF's Left/Top are device-independent
        // units (DIPs). Divide by this window's DPI scale or a scaled display places the
        // panel off by the scale factor (e.g. wrong by 25% at 125% scaling).
        var dpi = VisualTreeHelper.GetDpi(this);
        var workLeft = info.rcWork.Left / dpi.DpiScaleX;
        var workTop = info.rcWork.Top / dpi.DpiScaleY;
        var workRight = info.rcWork.Right / dpi.DpiScaleX;
        var workBottom = info.rcWork.Bottom / dpi.DpiScaleY;

        Left = Math.Clamp(screenX - ActualWidth, workLeft, Math.Max(workLeft, workRight - ActualWidth));
        Top = Math.Clamp(screenY - ActualHeight, workTop, Math.Max(workTop, workBottom - ActualHeight));
    }

    // Task 9: peekMode added alongside the existing childDialogOpen guard — a peeked
    // panel was never Activate()d, so it can't legitimately Deactivate() either; any
    // event here while peeking is spurious and must not hide it (the proximity timer
    // owns hide-on-leave for peek mode instead).
    void OnDeactivated(object? s, EventArgs e) { if (!childDialogOpen && !peekMode) Hide(); }
    void OnKeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Escape) Hide(); }

    // Task 9: clicking inside a peeked panel "graduates" it to a normal, focused panel —
    // spec: "Clicking inside the peeked panel activates it, after which normal
    // focus/dismiss behavior applies." From here on OnDeactivated's Hide() governs again.
    void OnPreviewMouseDown(object s, MouseButtonEventArgs e)
    {
        if (!peekMode) return;
        peekMode = false;
        proximityTimer.Stop();
        Activate();
    }

    // Runs a child dialog (PromptDialog.Ask, OpenFileDialog.ShowDialog, the Report
    // MessageBoxes — all triggered from within Groups) without the panel disappearing out
    // from under it: sets childDialogOpen so OnDeactivated ignores the deactivation the
    // dialog causes, then re-Activate()s the panel afterwards so focus/foreground comes
    // back to it once the dialog closes. Passed to Groups.Bind as its runChildDialog hook.
    T RunChildDialog<T>(Func<T> show)
    {
        childDialogOpen = true;
        try { return show(); }
        finally
        {
            childDialogOpen = false;
            Activate();
        }
    }
}
