using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using TaskSpaces.Core;
using TaskSpaces.Core.Geometry;
using TaskSpaces.Core.Overview;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Windows.Activation;
using TaskSpaces.Windows.Monitoring;

namespace TaskSpaces.App;

// Task 11 (spec §Floating icon bar): a small always-on-top, borderless, translucent
// bar showing ONLY app icons, one compact row per workspace (📌 Pinned first when
// non-empty; unbound/non-workspace desktops excluded -- it is a workspace bar, not the
// full switcher overview). Click an icon -> JumpTo (switch workspace if needed, then
// focus). No text, no roster (not-running) entries, no drag-and-drop of WINDOWS onto
// it -- only the bar's own background is draggable, to reposition itself. A glanceable
// jump surface, not a manager (spec, explicitly).
//
// One instance lives for the app's lifetime, created lazily on first show and then
// toggled via ShowBar()/HideBar() from the tray menu (App.xaml.cs) -- unlike
// SwitcherPanel it is not summon/dismiss-on-focus-loss; once shown it sits on screen
// until explicitly hidden.
public partial class FloatingBar : Window
{
    readonly WorkspaceManager manager;
    readonly WindowActivator activator = new();
    IDisposable? subscription;

    // Task 11 fix round 3 (reviewer, Petre: "can't drag it"): the screen-coordinate
    // press point, set on PreviewMouseLeftButtonDown and cleared on release or once a
    // drag actually starts. Null means "no press in progress" -- same sentinel pattern
    // as WindowGroupsView.SetupDragSource's dragStart.
    Point? dragStart;

    public FloatingBar(WorkspaceManager manager)
    {
        this.manager = manager;
        InitializeComponent();
        Rebuild();
        // Live-refresh while visible, same pattern as WindowGroupsView.Bind: windows
        // opening/closing (manual script item 36) must update the bar without Petre
        // having to toggle it off and on.
        subscription = manager.StateChanged.Subscribe(_ => Dispatcher.Invoke(() => { if (IsVisible) Rebuild(); }));
        Closed += (_, _) => subscription?.Dispose();
    }

    // Called both at startup (App.OnStartup, when persisted state says Visible: true)
    // and from the tray toggle. Rebuilds unconditionally first: StateChanged may have
    // fired while hidden (the subscription above skips rebuilds whenever !IsVisible),
    // so without this the bar could flash stale content the instant it reappears.
    // Persists right after positioning so Visible=true and the (possibly clamped)
    // position always land together.
    public void ShowBar()
    {
        Rebuild();
        Show();
        PositionFromState();
        manager.SaveFloatingBar(new FloatingBarState(Left, Top, true));
    }

    public void HideBar()
    {
        Hide();
        manager.SaveFloatingBar(new FloatingBarState(Left, Top, false));
    }

    void OnHideClick(object sender, RoutedEventArgs e) => HideBar();

    // Task 11 fix round 3 (reviewer, Petre: "can't drag it"): the ORIGINAL design put
    // the drag handler on the Border alone, betting on it having bare background to
    // grab -- but the bar is nearly all icon Buttons with only 6px of Padding around
    // them, so there was almost no pixel left to press-and-drag from. Fixed by wiring
    // drag at the WINDOW level instead: these Preview* (tunnelling) handlers see every
    // press/move/release anywhere in the window BEFORE any icon Button's own (bubbling)
    // Click processing, including presses that start ON an icon -- "drag from
    // anywhere, clicks still work" (spec ask). This replaces the old
    // Border.MouseLeftButtonDown handler outright (removed) rather than keeping both:
    // the window-level mechanism already covers the Border's own bare-background case
    // too, so a second, narrower mechanism would be pure redundancy.
    //
    // Records the press point but does NOT set e.Handled -- an icon Button must still
    // arm normally on press (matches WindowGroupsView.SetupDragSource's
    // PreviewMouseLeftButtonDown, which does the same for row-drag).
    void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => dragStart = PointToScreen(e.GetPosition(this));

    // Clears a finished press so a later, unrelated move never measures distance from
    // a stale point -- same hardening WindowGroupsView.SetupDragSource applies to its
    // own dragStart (pitfall #2 in that file's comments).
    void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => dragStart = null;

    void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragStart is not { } start) return;
        var current = PointToScreen(e.GetPosition(this));
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragStart = null;

        // If the press landed on an icon Button, ButtonBase.OnMouseLeftButtonDown
        // already called CaptureMouse() on it (same mechanism WindowGroupsView.
        // SetupDragSource documents at length). Releasing that capture before
        // DragMove() lets its own native move-loop take clean, uncontested control of
        // the mouse, and -- just as importantly -- means that Button never receives
        // the MouseLeftButtonUp DragMove()'s loop consumes, so it never raises Click
        // for this press. That IS the desired split: press-and-drag moves the bar;
        // press-and-release-in-place still reaches the icon's Click normally.
        Mouse.Capture(null);
        DragMove(); // blocks until the mouse button is released
        manager.SaveFloatingBar(new FloatingBarState(Left, Top, true)); // only draggable while shown
    }

    void Rebuild()
    {
        Rows.Children.Clear();
        manager.WindowsByWorkspace().Tap(overview =>
        {
            if (overview.Pinned.Count > 0)
                Rows.Children.Add(GroupRow("Pinned", overview.Pinned));

            // Spec: "unbound desktops excluded -- it is a workspace bar" (OtherDesktops
            // is never consulted here). Workspaces with nothing running are skipped
            // outright rather than shown as an empty placeholder row -- an icon bar
            // with nothing to click in a group is noise, not useful chrome.
            overview.Workspaces
                .Where(g => g.Running.Count > 0)
                .ToList()
                .ForEach(g => Rows.Children.Add(GroupRow(g.Workspace.Name, g.Running)));
        });
        // Overview query failure (e.g. a transient desktop-enumeration hiccup) just
        // leaves whatever the bar last showed -- there's no text area on this surface to
        // report an error into, and the next StateChanged pulse retries for free.
    }

    UIElement GroupRow(string workspaceName, IEnumerable<WindowRow> rows)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        rows.ToList().ForEach(r => panel.Children.Add(IconButton(workspaceName, r)));
        return panel;
    }

    UIElement IconButton(string workspaceName, WindowRow row)
    {
        var button = new Button
        {
            Padding = new Thickness(2),
            Margin = new Thickness(2),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = $"{workspaceName} · {row.Window.Title}",
        };
        var icon = IconCache.For(row.Window.ProcessPath);
        if (icon is not null)
            // IconCache freezes icons at a fixed 16px source (shared with the switcher
            // panel and Manage window rows). Scaling that 16px bitmap up to the bar's
            // 20px target here is the simple option called out in the brief -- adding a
            // size parameter to the cache for this one caller isn't worth it, and a
            // 16->20px upscale of a small glyph is not visually distinguishable.
            button.Content = new Image { Source = icon, Width = 20, Height = 20 };
        // Click -> jump, with no Hide() afterwards: unlike the switcher panel, this bar
        // is a persistent surface (spec) -- it stays open across every jump so Petre can
        // click several icons in a row.
        //
        // Reviewer (Task 11 fix round 1, Minor): why WindowActivator.Activate's
        // SetForegroundWindow succeeds from here -- clicking the icon Button first
        // activates THIS window (FloatingBar, a normal top-level window with no
        // WS_EX_NOACTIVATE style), which grants our process the foreground-change
        // rights Windows normally restricts; the activator then hands that foreground
        // privilege on to the target window. Same rationale as SwitcherPanel's
        // running-row click.
        button.Click += (_, _) => Report(manager.JumpTo(row.Window.Handle, activator));
        return button;
    }

    // Restores Left/Top from persisted state (or computes the bottom-right work-area
    // default when never configured) and clamps into the nearest monitor's work area.
    // Called after Show() (like SwitcherPanel.Peek does for PositionNear) so
    // ActualWidth/ActualHeight already reflect the SizeToContent layout pass -- but see
    // the GetDpiForMonitor comment below for why that ordering alone is NOT enough.
    //
    // Task 11 fix round 2 (reviewer): root-caused Petre's invisible bar. State.json had
    // FloatingBar = { Left: 2408, Top: 1396, Visible: true } -- his monitor is 2560x1440
    // at 125% scaling, whose real DIP-space work area is only ~2048x1152, so that
    // position sat ~360 DIPs past the right/bottom edge. This was the FIRST-EVER show
    // (brand new feature, no prior drag), so it came from the DEFAULT branch below:
    // workRight/workBottom computed with a DPI scale of 1.0 instead of 1.25 --
    // VisualTreeHelper.GetDpi(this), queried immediately after Show() returns, can
    // still report the window's stale/provisional per-monitor-DPI-context (scale 1.0)
    // before its WM_DPICHANGED round-trip has actually landed on the dispatcher, even
    // though Show() already ran. 2560/1.0 - ActualWidth and 1440/1.0 - ActualHeight
    // land almost exactly on the reported (2408, 1396) -- the raw physical rcWork was
    // written straight into DIP-valued Left/Top, unconverted. Reordering Show() before
    // positioning (already true here) does NOT fix this, because the race is in
    // GetDpi's window-scoped negotiation state, not in call order.
    //
    // Fix: query the MONITOR's own DPI directly (GetDpiForMonitor, Shcore.dll) using
    // the SAME HMONITOR already returned by MonitorFromPoint below, instead of asking
    // the window. A monitor-scoped query has no per-window negotiation state to race.
    void PositionFromState()
    {
        var stored = manager.State.FloatingBar;
        // Task 11 fix round 2 (reviewer, restore-path safety): the saved position is
        // run through MonitorFromPoint + the clamp on EVERY show, stored or not, so a
        // stale/bad save (like Petre's) self-heals here without editing state.json --
        // MONITOR_DEFAULTTONEAREST always returns a real, valid monitor no matter how
        // far outside every monitor's bounds the probe point falls.
        var probe = new NativeMethods.POINT { X = (int)(stored?.Left ?? 0), Y = (int)(stored?.Top ?? 0) };
        var monitor = NativeMethods.MonitorFromPoint(probe, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            // Best-effort fallback if the API ever fails -- better than crashing the show.
            Left = stored?.Left ?? 0;
            Top = stored?.Top ?? 0;
            return;
        }

        NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY);
        var scaleX = dpiX / 96.0;
        var scaleY = dpiY / 96.0;
        var workLeft = info.rcWork.Left / scaleX;
        var workTop = info.rcWork.Top / scaleY;
        var workRight = info.rcWork.Right / scaleX;
        var workBottom = info.rcWork.Bottom / scaleY;

        // No persisted state at all (first run, or an old state.json): default to the
        // bottom-right corner of the work area minus the bar's own size (brief). This
        // default path gets the exact same clamp treatment as the restore path below --
        // one shared call, both branches feed into it.
        var (rawLeft, rawTop) = stored is { } s
            ? (s.Left, s.Top)
            : (workRight - ActualWidth, workBottom - ActualHeight);

        (Left, Top) = WorkAreaClamp.Clamp(rawLeft, rawTop, ActualWidth, ActualHeight, workLeft, workTop, workRight, workBottom);
    }

    Result Report(Result result) => result.TapError(err => MessageBox.Show(err, "TaskSpaces"));
}
